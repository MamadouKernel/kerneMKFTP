using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>
/// Exécute une étape RPA via Robot Framework : lance un fichier .robot déjà écrit (hors kernelMK, avec un IDE
/// externe) via la commande "robot" en ligne de commande — même modèle que ScriptStepExecutor pour les scripts
/// PowerShell/Python. Utile pour automatiser une interface web sans API (ex. saisie répétitive sur un portail
/// GUCE/TOS). Nécessite Python + le paquet "robotframework" installés sur le serveur exécutant kernelMK.
/// </summary>
public class RobotFrameworkStepExecutor : IStepExecutor
{
    public IReadOnlyCollection<StepType> SupportedTypes { get; } = new[] { StepType.RpaRobotFramework };

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var config = JsonSerializer.Deserialize<RobotFrameworkStepConfig>(context.Step.ConfigJson)
                      ?? throw new InvalidOperationException("Configuration RPA/Robot Framework invalide.");

        if (string.IsNullOrWhiteSpace(config.RobotFilePath))
        {
            return StepExecutionResult.Fail("Aucun fichier .robot renseigné pour cette étape RPA.");
        }

        var outputDir = string.IsNullOrWhiteSpace(config.OutputDirectory)
            ? Path.Combine(Path.GetTempPath(), $"kmk-rpa-{context.Execution.Id:N}")
            : config.OutputDirectory;
        Directory.CreateDirectory(outputDir);

        var executable = string.IsNullOrWhiteSpace(config.RobotExecutablePath) ? "robot" : config.RobotExecutablePath;
        var arguments = BuildArguments(config, outputDir);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return StepExecutionResult.Fail(
                $"Impossible de lancer la commande \"{executable}\". Vérifiez que Python et le paquet " +
                $"\"robotframework\" sont installés sur ce serveur (pip install robotframework), ou renseignez " +
                $"le chemin complet de l'exécutable dans la configuration de l'étape. Détail : {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(context.CancellationToken);

        var reportPath = Path.Combine(outputDir, "report.html");
        var summary = $"Rapport Robot Framework : {reportPath}";

        // Code retour Robot Framework : 0 = tous les tests réussis. Tout autre code (nombre de tests en échec,
        // ou 250+/252/255 pour une erreur de framework/arguments) est traité comme un échec de l'étape.
        if (process.ExitCode == 0)
        {
            return StepExecutionResult.Ok($"{stdout}\n{summary}", process.ExitCode);
        }

        var errorDetail = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
        return StepExecutionResult.Fail($"{errorDetail}\n{summary}", process.ExitCode);
    }

    private static string BuildArguments(RobotFrameworkStepConfig config, string outputDir)
    {
        var parts = new List<string> { "--outputdir", Quote(outputDir) };

        if (!string.IsNullOrWhiteSpace(config.VariablesText))
        {
            foreach (var line in config.VariablesText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.Length == 0) continue;
                parts.Add("--variable");
                parts.Add(Quote(line));
            }
        }

        if (!string.IsNullOrWhiteSpace(config.IncludeTagsCsv))
        {
            foreach (var tag in config.IncludeTagsCsv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                parts.Add("--include");
                parts.Add(Quote(tag));
            }
        }

        if (!string.IsNullOrWhiteSpace(config.ExcludeTagsCsv))
        {
            foreach (var tag in config.ExcludeTagsCsv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                parts.Add("--exclude");
                parts.Add(Quote(tag));
            }
        }

        if (!string.IsNullOrWhiteSpace(config.ExtraArguments))
        {
            parts.Add(config.ExtraArguments);
        }

        parts.Add(Quote(config.RobotFilePath));
        return string.Join(' ', parts);
    }

    private static string Quote(string value) => $"\"{value}\"";
}

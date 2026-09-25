using System.Diagnostics;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Exécute des commandes système, scripts PowerShell, Python et batch (section 4.2 "Systeme"/"Scripts").</summary>
public class ScriptStepExecutor : IStepExecutor
{
    public IReadOnlyCollection<StepType> SupportedTypes { get; } = new[]
    {
        StepType.CommandeSysteme, StepType.ScriptPowerShell, StepType.ScriptPython, StepType.ScriptBatch
    };

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var config = JsonSerializer.Deserialize<ScriptStepConfig>(context.Step.ConfigJson)
                      ?? throw new InvalidOperationException("Configuration de script invalide.");

        string? tempScriptFile = null;
        try
        {
            var (fileName, arguments) = BuildProcessInvocation(context.Step.Type, config, ref tempScriptFile);

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory)
                    ? Environment.CurrentDirectory
                    : config.WorkingDirectory
            };

            if (config.EnvironmentVariables is not null)
            {
                foreach (var (key, value) in config.EnvironmentVariables)
                {
                    psi.Environment[key] = value;
                }
            }

            var result = await ProcessExecution.RunAsync(psi, context.CancellationToken);
            var success = config.SuccessExitCodes.Contains(result.ExitCode);
            return success
                ? StepExecutionResult.Ok(result.Output, result.ExitCode)
                : StepExecutionResult.Fail(result.Error.Length > 0 ? result.Error : result.Output, result.ExitCode);
        }
        finally
        {
            if (tempScriptFile is not null && File.Exists(tempScriptFile))
            {
                try { File.Delete(tempScriptFile); } catch { /* best effort cleanup */ }
            }
        }
    }

    private static (string FileName, string Arguments) BuildProcessInvocation(StepType type, ScriptStepConfig config, ref string? tempScriptFile)
    {
        if (!string.IsNullOrEmpty(config.InlineScript))
        {
            var extension = type switch
            {
                StepType.ScriptPowerShell => ".ps1",
                StepType.ScriptPython => ".py",
                StepType.ScriptBatch => ".cmd",
                _ => ".cmd"
            };
            tempScriptFile = Path.Combine(Path.GetTempPath(), $"job_{Guid.NewGuid():N}{extension}");
            File.WriteAllText(tempScriptFile, config.InlineScript);
        }

        var scriptPath = tempScriptFile ?? config.Path;

        return type switch
        {
            StepType.ScriptPowerShell => ("pwsh", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" {config.Arguments}"),
            StepType.ScriptPython => ("python", $"\"{scriptPath}\" {config.Arguments}"),
            StepType.ScriptBatch => (scriptPath, config.Arguments ?? string.Empty),
            StepType.CommandeSysteme => (config.Path, config.Arguments ?? string.Empty),
            _ => throw new NotSupportedException($"Type d'étape non supporté par ScriptStepExecutor : {type}")
        };
    }
}

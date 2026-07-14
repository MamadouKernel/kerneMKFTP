using System.IO.Compression;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Opérations fichiers : copier, déplacer, renommer, supprimer, (dé)compresser, vérifier (section 4.2 "Fichiers").</summary>
public class FileOpsStepExecutor : IStepExecutor
{
    public IReadOnlyCollection<StepType> SupportedTypes { get; } = new[]
    {
        StepType.FichierCopier, StepType.FichierDeplacer, StepType.FichierRenommer,
        StepType.FichierSupprimer, StepType.FichierCompresser, StepType.FichierDecompresser,
        StepType.FichierVerifier
    };

    public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var config = JsonSerializer.Deserialize<FileOpStepConfig>(context.Step.ConfigJson)
                      ?? throw new InvalidOperationException("Configuration fichier invalide.");

        var processed = new List<string>();

        try
        {
            switch (context.Step.Type)
            {
                case StepType.FichierCopier:
                    foreach (var file in ResolveSourceFiles(config))
                    {
                        var dest = Path.Combine(config.DestinationPath!, Path.GetFileName(file));
                        Directory.CreateDirectory(config.DestinationPath!);
                        File.Copy(file, dest, config.Overwrite);
                        processed.Add(dest);
                    }
                    break;

                case StepType.FichierDeplacer:
                    foreach (var file in ResolveSourceFiles(config))
                    {
                        var dest = Path.Combine(config.DestinationPath!, Path.GetFileName(file));
                        Directory.CreateDirectory(config.DestinationPath!);
                        if (config.Overwrite && File.Exists(dest)) File.Delete(dest);
                        File.Move(file, dest);
                        processed.Add(dest);
                    }
                    break;

                case StepType.FichierRenommer:
                    File.Move(config.SourcePath, config.DestinationPath!, config.Overwrite);
                    processed.Add(config.DestinationPath!);
                    break;

                case StepType.FichierSupprimer:
                    foreach (var file in ResolveSourceFiles(config))
                    {
                        File.Delete(file);
                        processed.Add(file);
                    }
                    break;

                case StepType.FichierCompresser:
                    if (File.Exists(config.DestinationPath) && config.Overwrite) File.Delete(config.DestinationPath!);
                    ZipFile.CreateFromDirectory(config.SourcePath, config.DestinationPath!);
                    processed.Add(config.DestinationPath!);
                    break;

                case StepType.FichierDecompresser:
                    Directory.CreateDirectory(config.DestinationPath!);
                    ZipFile.ExtractToDirectory(config.SourcePath, config.DestinationPath!, config.Overwrite);
                    processed.Add(config.DestinationPath!);
                    break;

                case StepType.FichierVerifier:
                    var files = ResolveSourceFiles(config).ToList();
                    if (files.Count == 0)
                    {
                        return Task.FromResult(StepExecutionResult.Fail($"Aucun fichier trouvé pour {config.SourcePath} (filtre : {config.Filter}).", 1));
                    }
                    processed.AddRange(files);
                    break;
            }

            return Task.FromResult(StepExecutionResult.Ok(
                $"{processed.Count} fichier(s) traité(s).",
                filesProcessedCsv: string.Join(";", processed)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(StepExecutionResult.Fail(ex.Message));
        }
    }

    private static IEnumerable<string> ResolveSourceFiles(FileOpStepConfig config)
    {
        if (Directory.Exists(config.SourcePath))
        {
            var searchOption = config.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.EnumerateFiles(config.SourcePath, "*", searchOption)
                .Where(f => FilePatternMatcher.IsMatch(Path.GetFileName(f), config.Filter));
        }

        return File.Exists(config.SourcePath) ? new[] { config.SourcePath } : Enumerable.Empty<string>();
    }
}

using System.IO.Compression;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.Entities;
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

        // Le credential associé à l'étape (ex: "comptesmb") n'a d'effet QUE sur les chemins UNC (\\serveur\partage\...) :
        // sans ouvrir une session SMB authentifiée avec ces identifiants, toute l'opération fichier tournerait
        // silencieusement sous l'identité du service Windows, qui peut ne pas avoir accès au partage.
        if (context.ResolvedCredential is { AuthType: CredentialAuthType.ClePriveeSsh })
        {
            return Task.FromResult(StepExecutionResult.Fail(
                "Le credential sélectionné pour cette étape est configuré en clé privée SSH — incompatible avec " +
                "l'authentification SMB Windows (qui exige un mot de passe). Choisis un credential de type « Mot de passe »."));
        }

        var smbUsername = context.ResolvedCredential?.Username;
        var smbSecret = context.ResolvedCredential?.Secret;
        using var sourceConn = SmbConnectionScope.Connect(config.SourcePath ?? string.Empty, smbUsername, smbSecret);
        using var destConn = SmbConnectionScope.Connect(config.DestinationPath ?? string.Empty, smbUsername, smbSecret);
        using var archiveConn = SmbConnectionScope.Connect(config.ArchiveDirectory ?? string.Empty, smbUsername, smbSecret);

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
                        ArchiveIfConfigured(config.ArchiveDirectory, file);
                        processed.Add(dest);
                    }
                    break;

                case StepType.FichierDeplacer:
                    foreach (var file in ResolveSourceFiles(config))
                    {
                        var dest = Path.Combine(config.DestinationPath!, Path.GetFileName(file));
                        Directory.CreateDirectory(config.DestinationPath!);
                        // Archiver avant le déplacement : une fois déplacé, le fichier n'existe plus à son
                        // emplacement d'origine (fonctionne aussi entre deux lecteurs différents, .NET bascule
                        // automatiquement sur une copie+suppression quand un déplacement direct n'est pas possible).
                        ArchiveIfConfigured(config.ArchiveDirectory, file);
                        // Ne pas supprimer la destination avant le déplacement : elle peut être
                        // la source elle-même, et un déplacement refusé doit préserver les fichiers.
                        File.Move(file, dest, config.Overwrite);
                        processed.Add(dest);
                    }
                    break;

                case StepType.FichierRenommer:
                    var sourcePath = RequirePath(config.SourcePath, "source");
                    ArchiveIfConfigured(config.ArchiveDirectory, sourcePath);
                    File.Move(sourcePath, config.DestinationPath!, config.Overwrite);
                    processed.Add(config.DestinationPath!);
                    break;

                case StepType.FichierSupprimer:
                    foreach (var file in ResolveSourceFiles(config))
                    {
                        // Archiver avant suppression : filet de sécurité pour ne jamais perdre définitivement
                        // un fichier supprimé par erreur (chemin d'archive distinct de la source/destination).
                        ArchiveIfConfigured(config.ArchiveDirectory, file);
                        File.Delete(file);
                        processed.Add(file);
                    }
                    break;

                case StepType.FichierCompresser:
                    if (File.Exists(config.DestinationPath) && config.Overwrite) File.Delete(config.DestinationPath!);
                    ZipFile.CreateFromDirectory(RequirePath(config.SourcePath, "source"), config.DestinationPath!);
                    ArchiveIfConfigured(config.ArchiveDirectory, config.DestinationPath!);
                    processed.Add(config.DestinationPath!);
                    break;

                case StepType.FichierDecompresser:
                    var archiveSourcePath = RequirePath(config.SourcePath, "source");
                    ArchiveIfConfigured(config.ArchiveDirectory, archiveSourcePath);
                    Directory.CreateDirectory(config.DestinationPath!);
                    ZipFile.ExtractToDirectory(archiveSourcePath, config.DestinationPath!, config.Overwrite);
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

    /// <summary>
    /// Dépose une copie de sécurité du fichier dans le dossier d'archive configuré, en plus de l'opération
    /// demandée — jamais à la place. Le dossier d'archive peut être sur un lecteur/volume différent : File.Copy
    /// gère nativement la copie entre volumes distincts sous Windows.
    /// </summary>
    private static string RequirePath(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException($"Chemin {name} requis.");

    private static void ArchiveIfConfigured(string? archiveDirectory, string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(archiveDirectory) || !File.Exists(sourceFile)) return;
        Directory.CreateDirectory(archiveDirectory);
        var dest = Path.Combine(archiveDirectory, Path.GetFileName(sourceFile));
        File.Copy(sourceFile, dest, overwrite: true);
    }

    private static IEnumerable<string> ResolveSourceFiles(FileOpStepConfig config)
    {
        if (Directory.Exists(config.SourcePath))
        {
            var searchOption = config.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            // Figer les entrées avant toute écriture : une destination/archive sous la source
            // ne doit pas être découverte pendant l'opération et traitée à son tour.
            return Directory.EnumerateFiles(config.SourcePath, "*", searchOption)
                .Where(f => FilePatternMatcher.IsMatch(Path.GetFileName(f), config.Filter))
                .ToArray();
        }

        return File.Exists(config.SourcePath) ? new[] { config.SourcePath } : Enumerable.Empty<string>();
    }
}

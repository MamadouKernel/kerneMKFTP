using System.Security.Cryptography;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Core.StepConfigs;
using KernelMK.Data;
using FluentFTP;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Transferts FTP, FTPS, SFTP et copie réseau SMB (section 4.2 "Transfert").</summary>
public class TransferStepExecutor : IStepExecutor
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<TransferStepExecutor> _logger;

    public TransferStepExecutor(IDbContextFactory<AppDbContext> dbFactory, ILogger<TransferStepExecutor> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public IReadOnlyCollection<StepType> SupportedTypes { get; } = new[]
    {
        StepType.TransfertFtp, StepType.TransfertFtps, StepType.TransfertSftp, StepType.TransfertSmb
    };

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var config = JsonSerializer.Deserialize<TransferStepConfig>(context.Step.ConfigJson)
                      ?? throw new InvalidOperationException("Configuration de transfert invalide.");

        var username = context.ResolvedCredential?.Username ?? "anonymous";
        var secret = context.ResolvedCredential?.Secret ?? string.Empty;

        try
        {
            switch (context.Step.Type)
            {
                case StepType.TransfertSftp:
                    return await ExecuteSftpAsync(config, username, secret, context.CancellationToken);

                case StepType.TransfertFtp:
                case StepType.TransfertFtps:
                    return await ExecuteFtpAsync(config, username, secret, context.Step.Type == StepType.TransfertFtps, context.CancellationToken);

                case StepType.TransfertSmb:
                    return ExecuteSmbCopy(config, context.ResolvedCredential);

                default:
                    throw new NotSupportedException($"Type de transfert non supporté : {context.Step.Type}");
            }
        }
        catch (Exception ex)
        {
            return StepExecutionResult.Fail(ex.Message);
        }
    }

    private async Task<StepExecutionResult> ExecuteSftpAsync(TransferStepConfig config, string username, string secret, CancellationToken ct)
    {
        var port = config.Port <= 0 ? 22 : config.Port;
        using var client = new SftpClient(config.Host, port, username, secret);

        // Vérification de la clé d'hôte (confiance à la première connexion, protection anti-usurpation).
        string? hostKeyError = null;
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(e.HostKey));
            var verdict = VerifyHostKeyAsync(config.Host, port, fingerprint, ct).GetAwaiter().GetResult();
            if (!verdict.Trusted)
            {
                hostKeyError = verdict.ErrorMessage;
            }
            e.CanTrust = verdict.Trusted;
        };

        try
        {
            await Task.Run(client.Connect, ct);
        }
        catch (SshConnectionException) when (hostKeyError is not null)
        {
            // SSH.NET lève sa propre exception générique quand CanTrust=false ; on préfère notre message explicite.
            return StepExecutionResult.Fail(hostKeyError);
        }

        if (hostKeyError is not null)
        {
            // Filet de sécurité si SSH.NET ne lève pas d'exception malgré CanTrust=false.
            return StepExecutionResult.Fail(hostKeyError);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(config.Filter))
            {
                var transferred = new List<string>();

                if (config.Upload)
                {
                    foreach (var file in Directory.EnumerateFiles(config.LocalPath)
                                 .Where(f => FilePatternMatcher.IsMatch(Path.GetFileName(f), config.Filter)))
                    {
                        var remoteFile = CombineRemotePath(config.RemotePath, Path.GetFileName(file));
                        await using var stream = File.OpenRead(file);
                        await Task.Run(() => client.UploadFile(stream, remoteFile, true), ct);
                        transferred.Add(file);
                        ArchiveIfRequested(config, file);
                    }
                }
                else
                {
                    Directory.CreateDirectory(config.LocalPath);
                    var remoteFiles = client.ListDirectory(config.RemotePath)
                        .Where(f => f.IsRegularFile && FilePatternMatcher.IsMatch(f.Name, config.Filter));
                    foreach (var rf in remoteFiles)
                    {
                        var localFile = Path.Combine(config.LocalPath, rf.Name);
                        await using var stream = File.Create(localFile);
                        await Task.Run(() => client.DownloadFile(rf.FullName, stream), ct);
                        transferred.Add(localFile);
                    }
                }

                if (transferred.Count == 0)
                {
                    return StepExecutionResult.Fail($"Aucun fichier ne correspond au filtre '{config.Filter}'.");
                }

                return StepExecutionResult.Ok(
                    $"{transferred.Count} fichier(s) transféré(s) via SFTP vers/depuis {config.Host}.",
                    filesProcessedCsv: string.Join(";", transferred));
            }

            if (config.Upload)
            {
                await using var stream = File.OpenRead(config.LocalPath);
                await Task.Run(() => client.UploadFile(stream, config.RemotePath, true), ct);

                if (!client.Exists(config.RemotePath))
                {
                    return StepExecutionResult.Fail("Le fichier distant n'a pas été trouvé après transfert (vérification échouée).");
                }
            }
            else
            {
                await using var stream = File.Create(config.LocalPath);
                await Task.Run(() => client.DownloadFile(config.RemotePath, stream), ct);
            }

            ArchiveIfRequested(config, config.LocalPath);
            return StepExecutionResult.Ok($"Transfert SFTP réussi vers/depuis {config.Host}.", filesProcessedCsv: config.LocalPath);
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static string CombineRemotePath(string remoteDir, string fileName) => remoteDir.TrimEnd('/') + "/" + fileName;

    /// <summary>
    /// Compare l'empreinte reçue à celle mémorisée pour cet hôte (TOFU : "Trust On First Use").
    /// Premher contact : la clé est enregistrée et acceptée. Contacts suivants : la clé DOIT correspondre,
    /// sinon la connexion est refusée (l'hôte a pu être usurpé/intercepté, ou sa clé a légitimement changé
    /// suite à une réinstallation — dans ce dernier cas un administrateur doit supprimer l'entrée mémorisée).
    /// </summary>
    private async Task<(bool Trusted, string? ErrorMessage)> VerifyHostKeyAsync(string host, int port, string fingerprint, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var known = await db.TrustedHostKeys.FirstOrDefaultAsync(k => k.Host == host && k.Port == port, ct);

        if (known is null)
        {
            db.TrustedHostKeys.Add(new TrustedHostKey { Host = host, Port = port, FingerprintSha256 = fingerprint });
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Nouvelle clé d'hôte SFTP mémorisée pour {Host}:{Port} (empreinte {Fingerprint}).", host, port, fingerprint);
            return (true, null);
        }

        if (known.FingerprintSha256 == fingerprint)
        {
            known.LastVerifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return (true, null);
        }

        _logger.LogWarning("ALERTE SECURITE : la clé d'hôte SFTP de {Host}:{Port} ne correspond pas à celle mémorisée. Connexion refusée.", host, port);
        return (false,
            $"Clé d'hôte SFTP inattendue pour {host}:{port}. Empreinte reçue : {fingerprint}. " +
            $"Empreinte connue : {known.FingerprintSha256}. Connexion refusée par sécurité (usurpation possible, " +
            "ou le serveur a été réinstallé — un administrateur doit alors supprimer l'entrée mémorisée pour ce serveur).");
    }

    private static async Task<StepExecutionResult> ExecuteFtpAsync(TransferStepConfig config, string username, string secret, bool useTls, CancellationToken ct)
    {
        using var client = new AsyncFtpClient(config.Host, username, secret, config.Port <= 0 ? 21 : config.Port);
        if (useTls || config.UseTls)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
        }

        await client.Connect(ct);

        try
        {
            if (!string.IsNullOrWhiteSpace(config.Filter))
            {
                var transferred = new List<string>();

                if (config.Upload)
                {
                    foreach (var file in Directory.EnumerateFiles(config.LocalPath)
                                 .Where(f => FilePatternMatcher.IsMatch(Path.GetFileName(f), config.Filter)))
                    {
                        var remoteFile = CombineRemotePath(config.RemotePath, Path.GetFileName(file));
                        var status = await client.UploadFile(file, remoteFile, FtpRemoteExists.Overwrite, true, token: ct);
                        if (status != FtpStatus.Success)
                        {
                            return StepExecutionResult.Fail($"Échec du transfert FTP pour '{Path.GetFileName(file)}' (statut != Success).");
                        }
                        transferred.Add(file);
                        ArchiveIfRequested(config, file);
                    }
                }
                else
                {
                    Directory.CreateDirectory(config.LocalPath);
                    var remoteFiles = (await client.GetListing(config.RemotePath, ct))
                        .Where(f => f.Type == FtpObjectType.File && FilePatternMatcher.IsMatch(f.Name, config.Filter));
                    foreach (var rf in remoteFiles)
                    {
                        var localFile = Path.Combine(config.LocalPath, rf.Name);
                        var status = await client.DownloadFile(localFile, rf.FullName, FtpLocalExists.Overwrite, token: ct);
                        if (status != FtpStatus.Success)
                        {
                            return StepExecutionResult.Fail($"Échec de la récupération FTP pour '{rf.Name}' (statut != Success).");
                        }
                        transferred.Add(localFile);
                    }
                }

                if (transferred.Count == 0)
                {
                    return StepExecutionResult.Fail($"Aucun fichier ne correspond au filtre '{config.Filter}'.");
                }

                return StepExecutionResult.Ok(
                    $"{transferred.Count} fichier(s) transféré(s) via FTP{(useTls ? "S" : "")} vers/depuis {config.Host}.",
                    filesProcessedCsv: string.Join(";", transferred));
            }

            if (config.Upload)
            {
                var status = await client.UploadFile(config.LocalPath, config.RemotePath, FtpRemoteExists.Overwrite, true, token: ct);
                if (status != FtpStatus.Success)
                {
                    return StepExecutionResult.Fail("Échec du transfert FTP (statut != Success).");
                }
            }
            else
            {
                var status = await client.DownloadFile(config.LocalPath, config.RemotePath, FtpLocalExists.Overwrite, token: ct);
                if (status != FtpStatus.Success)
                {
                    return StepExecutionResult.Fail("Échec de la récupération FTP (statut != Success).");
                }
            }

            ArchiveIfRequested(config, config.LocalPath);
            return StepExecutionResult.Ok($"Transfert FTP{(useTls ? "S" : "")} réussi vers/depuis {config.Host}.", filesProcessedCsv: config.LocalPath);
        }
        finally
        {
            await client.Disconnect(ct);
        }
    }

    /// <summary>
    /// Copie "réseau SMB". Si un credential est associé à l'étape et que le partage est un chemin UNC
    /// (\\serveur\partage), une session authentifiée avec CE compte est établie explicitement via
    /// WNetAddConnection2 (équivalent de "net use ... /user:compte motdepasse"), puis libérée après
    /// la copie — même si ce compte est différent de celui qui exécute le service KernelMK et n'est
    /// pas un compte de service. Sans credential (ou pour un lecteur déjà mappé), l'identité du
    /// processus est utilisée directement, comme un lecteur réseau déjà connecté.
    /// </summary>
    private static StepExecutionResult ExecuteSmbCopy(TransferStepConfig config, (string? Username, string? Secret)? credential)
    {
        var shareOrPath = config.SmbShare ?? config.RemotePath;

        using var connection = SmbConnectionScope.Connect(shareOrPath, credential?.Username, credential?.Secret);

        if (!string.IsNullOrWhiteSpace(config.Filter))
        {
            var transferred = new List<string>();

            if (config.Upload)
            {
                Directory.CreateDirectory(shareOrPath);
                foreach (var file in Directory.EnumerateFiles(config.LocalPath)
                             .Where(f => FilePatternMatcher.IsMatch(Path.GetFileName(f), config.Filter)))
                {
                    var dest = Path.Combine(shareOrPath, Path.GetFileName(file));
                    File.Copy(file, dest, true);
                    transferred.Add(dest);
                    ArchiveIfRequested(config, file);
                }
            }
            else
            {
                Directory.CreateDirectory(config.LocalPath);
                foreach (var file in Directory.EnumerateFiles(shareOrPath)
                             .Where(f => FilePatternMatcher.IsMatch(Path.GetFileName(f), config.Filter)))
                {
                    var dest = Path.Combine(config.LocalPath, Path.GetFileName(file));
                    File.Copy(file, dest, true);
                    transferred.Add(dest);
                }
            }

            if (transferred.Count == 0)
            {
                return StepExecutionResult.Fail($"Aucun fichier ne correspond au filtre '{config.Filter}'.");
            }

            return StepExecutionResult.Ok(
                $"{transferred.Count} fichier(s) copié(s) via SMB.",
                filesProcessedCsv: string.Join(";", transferred));
        }

        var destination = Path.Combine(shareOrPath, Path.GetFileName(config.LocalPath));

        if (config.Upload)
        {
            File.Copy(config.LocalPath, destination, true);
        }
        else
        {
            File.Copy(destination, config.LocalPath, true);
        }

        ArchiveIfRequested(config, config.LocalPath);
        return StepExecutionResult.Ok("Copie réseau SMB réussie.", filesProcessedCsv: destination);
    }

    private static void ArchiveIfRequested(TransferStepConfig config, string localFilePath)
    {
        if (!config.ArchiveAfterTransfer || string.IsNullOrWhiteSpace(config.ArchiveDirectory) || !config.Upload) return;
        if (!File.Exists(localFilePath)) return;

        Directory.CreateDirectory(config.ArchiveDirectory);
        var archivePath = Path.Combine(config.ArchiveDirectory, Path.GetFileName(localFilePath));
        File.Copy(localFilePath, archivePath, true);
    }
}

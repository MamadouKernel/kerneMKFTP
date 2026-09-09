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

        var host = !string.IsNullOrWhiteSpace(config.Host)
            ? config.Host
            : (context.ResolvedCredential?.Host ?? string.Empty);

        var defaultPort = context.Step.Type == StepType.TransfertSftp ? 22 : 21;
        var port = config.Port > 0
            ? config.Port
            : (context.ResolvedCredential?.Port is > 0 ? context.ResolvedCredential.Value.Port.Value : defaultPort);

        if (context.Step.Type != StepType.TransfertSmb && string.IsNullOrWhiteSpace(host))
        {
            return StepExecutionResult.Fail("Aucun hôte spécifié. Veuillez renseigner l'hôte dans la configuration de l'étape ou associer un Credential contenant l'adresse du serveur.");
        }

        try
        {
            switch (context.Step.Type)
            {
                case StepType.TransfertSftp:
                    return await ExecuteSftpAsync(
                        config, host, port, username, secret,
                        context.ResolvedCredential?.AuthType ?? CredentialAuthType.MotDePasse,
                        context.ResolvedCredential?.Passphrase,
                        context.CancellationToken);

                case StepType.TransfertFtp:
                case StepType.TransfertFtps:
                    return await ExecuteFtpAsync(config, host, port, username, secret, context.Step.Type == StepType.TransfertFtps, context.CancellationToken);

                case StepType.TransfertSmb:
                    return ExecuteSmbCopy(config, context.ResolvedCredential);

                default:
                    throw new NotSupportedException($"Type de transfert non supporté : {context.Step.Type}");
            }
        }
        catch (Exception ex)
        {
            return StepExecutionResult.Fail(FormatDetailedException(ex));
        }
    }

    private static string FormatDetailedException(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[ERREUR D'EXÉCUTION] : {ex.Message}");

        var current = ex.InnerException;
        int depth = 1;
        while (current != null && depth <= 5)
        {
            sb.AppendLine($"[CAUSE PROFONDE #{depth}] ({current.GetType().Name}) : {current.Message}");
            current = current.InnerException;
            depth++;
        }

        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.Flatten().InnerExceptions)
            {
                sb.AppendLine($"[SOUS-EXCEPTION] ({inner.GetType().Name}) : {inner.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[TRACE TECHNIQUE DÉTAILLÉE]");
        sb.AppendLine(ex.ToString());

        return sb.ToString();
    }

    private async Task<StepExecutionResult> ExecuteSftpAsync(TransferStepConfig config, string host, int port, string username, string secret, CredentialAuthType authType, string? passphrase, CancellationToken ct)
    {
        using var client = SftpClientFactory.Create(host, port, username, secret, authType, passphrase);

        // Vérification de la clé d'hôte (confiance à la première connexion, protection anti-usurpation).
        string? hostKeyError = null;
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(e.HostKey));
            var verdict = VerifyHostKeyAsync(host, port, fingerprint, ct).GetAwaiter().GetResult();
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
                    if (!Directory.Exists(config.LocalPath))
                    {
                        var isMappedDrive = config.LocalPath.Length >= 2 && config.LocalPath[1] == ':' && char.ToUpperInvariant(config.LocalPath[0]) != 'C';
                        var hint = isMappedDrive
                            ? " Remarque : Si l'application tourne en tant que Service Windows, les lecteurs réseau mappés (ex: K:\\) ne sont pas accessibles par le service. Utilisez le chemin réseau UNC direct (ex: \\\\Serveur\\Partage\\COPARN\\MSC\\)."
                            : " Vérifiez que le disque et le dossier existent bien sur cette machine.";
                        return StepExecutionResult.Fail($"Le dossier local source '{config.LocalPath}' est introuvable ou inaccessible.{hint}");
                    }

                    foreach (var file in Directory.EnumerateFiles(config.LocalPath)
                                 .Where(f => FilePatternMatcher.IsMatch(Path.GetFileName(f), config.Filter)))
                    {
                        var remoteFile = CombineRemotePath(config.RemotePath, Path.GetFileName(file));
                        await using (var stream = File.OpenRead(file))
                        {
                            await Task.Run(() => client.UploadFile(stream, remoteFile, true), ct);
                        }
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
                        await using (var stream = File.Create(localFile))
                        {
                            await Task.Run(() => client.DownloadFile(rf.FullName, stream), ct);
                        }
                        transferred.Add(localFile);

                        if (config.DeleteRemoteAfterDownload)
                        {
                            await Task.Run(() => client.DeleteFile(rf.FullName), ct);
                        }
                    }
                }

                if (transferred.Count == 0)
                {
                    return StepExecutionResult.Fail($"Aucun fichier ne correspond au filtre '{config.Filter}'.");
                }

                return StepExecutionResult.Ok(
                    $"{transferred.Count} fichier(s) transféré(s) via SFTP vers/depuis {host}.",
                    filesProcessedCsv: string.Join(";", transferred));
            }

            if (config.Upload)
            {
                if (!File.Exists(config.LocalPath))
                {
                    return StepExecutionResult.Fail($"Le fichier local source '{config.LocalPath}' est introuvable.");
                }

                await using (var stream = File.OpenRead(config.LocalPath))
                {
                    await Task.Run(() => client.UploadFile(stream, config.RemotePath, true), ct);
                }

                if (!client.Exists(config.RemotePath))
                {
                    return StepExecutionResult.Fail("Le fichier distant n'a pas été trouvé après transfert (vérification échouée).");
                }

                ArchiveIfRequested(config, config.LocalPath);
            }
            else
            {
                await using (var stream = File.Create(config.LocalPath))
                {
                    await Task.Run(() => client.DownloadFile(config.RemotePath, stream), ct);
                }

                if (config.DeleteRemoteAfterDownload)
                {
                    await Task.Run(() => client.DeleteFile(config.RemotePath), ct);
                }
            }

            return StepExecutionResult.Ok($"Transfert SFTP réussi vers/depuis {host}.", filesProcessedCsv: config.LocalPath);
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

    private static async Task<StepExecutionResult> ExecuteFtpAsync(TransferStepConfig config, string host, int port, string username, string secret, bool useTls, CancellationToken ct)
    {
        using var client = new AsyncFtpClient(host, username, secret, port);
        client.Config.ConnectTimeout = 15000;
        client.Config.DataConnectionConnectTimeout = 20000;
        client.Config.ReadTimeout = 30000;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;

        if (useTls || config.UseTls)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.ValidateAnyCertificate = true;
            client.Config.DataConnectionEncryption = true;
            client.Config.SslProtocols = System.Security.Authentication.SslProtocols.None;
        }

        try
        {
            await client.Connect(ct);
        }
        catch (Exception) when ((useTls || config.UseTls) && port == 990)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Implicit;
            await client.Connect(ct);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(config.Filter))
            {
                var transferred = new List<string>();

                if (config.Upload)
                {
                    if (!Directory.Exists(config.LocalPath))
                    {
                        var isMappedDrive = config.LocalPath.Length >= 2 && config.LocalPath[1] == ':' && char.ToUpperInvariant(config.LocalPath[0]) != 'C';
                        var hint = isMappedDrive
                            ? " Remarque : Si l'application tourne en tant que Service Windows, les lecteurs réseau mappés (ex: K:\\) ne sont pas accessibles par le service. Utilisez le chemin réseau UNC direct (ex: \\\\Serveur\\Partage\\COPARN\\MSC\\)."
                            : " Vérifiez que le disque et le dossier existent bien sur cette machine.";
                        return StepExecutionResult.Fail($"Le dossier local source '{config.LocalPath}' est introuvable ou inaccessible.{hint}");
                    }

                    foreach (var file in Directory.EnumerateFiles(config.LocalPath)
                                 .Where(f => FilePatternMatcher.IsMatch(Path.GetFileName(f), config.Filter)))
                    {
                        var remoteFile = CombineRemotePath(config.RemotePath, Path.GetFileName(file));
                        var status = await UploadWithFallbackAsync(client, file, remoteFile, ct);
                        if (status != FtpStatus.Success)
                        {
                            return StepExecutionResult.Fail($"Échec du transfert FTP pour '{Path.GetFileName(file)}' : {client.LastReply.Code} {client.LastReply.Message} (statut : {status}).");
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
                            return StepExecutionResult.Fail($"Échec de la récupération FTP pour '{rf.Name}' : {client.LastReply.Code} {client.LastReply.Message} (statut : {status}).");
                        }
                        transferred.Add(localFile);

                        if (config.DeleteRemoteAfterDownload)
                        {
                            await client.DeleteFile(rf.FullName, ct);
                        }
                    }
                }

                if (transferred.Count == 0)
                {
                    return StepExecutionResult.Fail($"Aucun fichier ne correspond au filtre '{config.Filter}'.");
                }

                return StepExecutionResult.Ok(
                    $"{transferred.Count} fichier(s) transféré(s) via FTP{(useTls ? "S" : "")} vers/depuis {host}.",
                    filesProcessedCsv: string.Join(";", transferred));
            }

            if (config.Upload)
            {
                if (!File.Exists(config.LocalPath))
                {
                    return StepExecutionResult.Fail($"Le fichier local source '{config.LocalPath}' est introuvable.");
                }

                var status = await UploadWithFallbackAsync(client, config.LocalPath, config.RemotePath, ct);
                if (status != FtpStatus.Success)
                {
                    return StepExecutionResult.Fail($"Échec du transfert FTP : {client.LastReply.Code} {client.LastReply.Message} (statut : {status}).");
                }
                ArchiveIfRequested(config, config.LocalPath);
            }
            else
            {
                var status = await client.DownloadFile(config.LocalPath, config.RemotePath, FtpLocalExists.Overwrite, token: ct);
                if (status != FtpStatus.Success)
                {
                    return StepExecutionResult.Fail("Échec de la récupération FTP (statut != Success).");
                }

                if (config.DeleteRemoteAfterDownload)
                {
                    await client.DeleteFile(config.RemotePath, ct);
                }
            }

            return StepExecutionResult.Ok($"Transfert FTP{(useTls ? "S" : "")} réussi vers/depuis {host}.", filesProcessedCsv: config.LocalPath);
        }
        finally
        {
            await client.Disconnect(ct);
        }
    }

    private static async Task<FtpStatus> UploadWithFallbackAsync(AsyncFtpClient client, string localPath, string remotePath, CancellationToken ct)
    {
        try
        {
            return await client.UploadFile(localPath, remotePath, FtpRemoteExists.Overwrite, true, token: ct);
        }
        catch (Exception) when (client.Config.DataConnectionEncryption)
        {
            // Repli automatique : si le serveur FTPS refuse le chiffrement du canal de données (PROT P),
            // on tente le canal de données standard clair (PROT C)
            client.Config.DataConnectionEncryption = false;
            try
            {
                return await client.UploadFile(localPath, remotePath, FtpRemoteExists.Overwrite, true, token: ct);
            }
            catch
            {
                client.Config.DataConnectionEncryption = true;
                throw;
            }
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
    private static StepExecutionResult ExecuteSmbCopy(TransferStepConfig config, (string? Username, string? Secret, string? Host, int? Port, CredentialAuthType AuthType, string? Passphrase)? credential)
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

                    if (config.DeleteRemoteAfterDownload)
                    {
                        try { File.Delete(file); } catch { }
                    }
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
            ArchiveIfRequested(config, config.LocalPath);
        }
        else
        {
            File.Copy(destination, config.LocalPath, true);
            if (config.DeleteRemoteAfterDownload)
            {
                try { File.Delete(destination); } catch { }
            }
        }

        return StepExecutionResult.Ok("Copie réseau SMB réussie.", filesProcessedCsv: destination);
    }

    /// <summary>
    /// Déplace (coupe) le fichier local vers le dossier d'archives après un transfert réussi
    /// afin d'éviter qu'il ne soit retransféré lors de la prochaine exécution.
    /// </summary>
    private static void ArchiveIfRequested(TransferStepConfig config, string localFilePath)
    {
        if (!config.ArchiveAfterTransfer || string.IsNullOrWhiteSpace(config.ArchiveDirectory) || !config.Upload) return;
        if (!File.Exists(localFilePath)) return;

        try
        {
            Directory.CreateDirectory(config.ArchiveDirectory);
            var archivePath = Path.Combine(config.ArchiveDirectory, Path.GetFileName(localFilePath));

            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            File.Move(localFilePath, archivePath);
        }
        catch
        {
            try
            {
                // Repli si partition différente sous Windows (C: vers D: etc.)
                var archivePath = Path.Combine(config.ArchiveDirectory, Path.GetFileName(localFilePath));
                File.Copy(localFilePath, archivePath, true);
                File.Delete(localFilePath);
            }
            catch
            {
                // Ne bloque pas l'exécution du job si l'archivage rencontre un verrou temporaire
            }
        }
    }
}

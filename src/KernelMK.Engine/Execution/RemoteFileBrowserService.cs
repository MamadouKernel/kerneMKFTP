using System.Security.Cryptography;
using System.Text;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using FluentFTP;
using FluentFTP.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace KernelMK.Engine.Execution;

public record RemoteItemInfo(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastModified,
    string? Extension)
{
    public string FormattedSize => IsDirectory
        ? "-"
        : SizeBytes switch
        {
            < 1024 => $"{SizeBytes} o",
            < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} Ko",
            < 1024 * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024.0):F1} Mo",
            _ => $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F2} Go"
        };
}

public class RemoteFileBrowserService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<RemoteFileBrowserService> _logger;

    public RemoteFileBrowserService(IDbContextFactory<AppDbContext> dbFactory, ILogger<RemoteFileBrowserService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<List<RemoteItemInfo>> ListItemsAsync(
        StepType protocol,
        string host,
        int port,
        string username,
        string secret,
        string remotePath,
        CredentialAuthType authType = CredentialAuthType.MotDePasse,
        string? passphrase = null,
        CancellationToken ct = default)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(remotePath) ? "/" : remotePath;

        return protocol switch
        {
            StepType.TransfertSftp => await ListSftpItemsAsync(host, port <= 0 ? 22 : port, username, secret, normalizedPath, authType, passphrase, ct),
            StepType.TransfertFtp => await ListFtpItemsAsync(host, port <= 0 ? 21 : port, username, secret, false, normalizedPath, ct),
            StepType.TransfertFtps => await ListFtpItemsAsync(host, port <= 0 ? 21 : port, username, secret, true, normalizedPath, ct),
            StepType.TransfertSmb => ListSmbItems(host, normalizedPath, username, secret),
            _ => throw new NotSupportedException($"Protocole {protocol} non supporté pour la navigation.")
        };
    }

    /// <summary>
    /// Confiance à la première connexion (TOFU) partagée entre clés d'hôte SSH et certificats TLS (FTPS) :
    /// la première empreinte vue pour un host:port est mémorisée, toute empreinte différente ensuite
    /// est refusée (usurpation possible, ou le serveur a été réinstallé/son certificat renouvelé).
    /// </summary>
    private async Task<(bool Trusted, string? ErrorMessage)> VerifyFingerprintAsync(string host, int port, string fingerprint, string protocolLabel, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var known = await db.TrustedHostKeys.FirstOrDefaultAsync(k => k.Host == host && k.Port == port, ct);

        if (known is null)
        {
            db.TrustedHostKeys.Add(new TrustedHostKey { Host = host, Port = port, FingerprintSha256 = fingerprint });
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Nouvelle empreinte {Protocol} mémorisée pour {Host}:{Port}.", protocolLabel, host, port);
            return (true, null);
        }

        if (known.FingerprintSha256 == fingerprint)
        {
            known.LastVerifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return (true, null);
        }

        _logger.LogWarning("ALERTE SECURITE : l'empreinte {Protocol} de {Host}:{Port} ne correspond pas à celle mémorisée. Connexion refusée.", protocolLabel, host, port);
        return (false,
            $"Empreinte {protocolLabel} inattendue pour {host}:{port}. Connexion refusée par sécurité (usurpation possible, " +
            "ou le serveur a été réinstallé/son certificat renouvelé — un administrateur doit alors supprimer l'entrée mémorisée pour ce serveur).");
    }

    private async Task<List<RemoteItemInfo>> ListSftpItemsAsync(
        string host, int port, string username, string secret, string remotePath, CredentialAuthType authType, string? passphrase, CancellationToken ct)
    {
        using var client = SftpClientFactory.Create(host, port, username, secret, authType, passphrase);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
        string? hostKeyError = null;
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(e.HostKey));
            var verdict = VerifyFingerprintAsync(host, port, fingerprint, "SSH", ct).GetAwaiter().GetResult();
            e.CanTrust = verdict.Trusted;
            if (!verdict.Trusted) hostKeyError = verdict.ErrorMessage;
        };

        try
        {
            await Task.Run(client.Connect, ct);
        }
        catch (Exception) when (hostKeyError is not null)
        {
            throw new InvalidOperationException(hostKeyError);
        }

        try
        {
            var targetPath = client.Exists(remotePath) ? remotePath : client.WorkingDirectory;
            var rawList = await Task.Run(() => client.ListDirectory(targetPath).ToList(), ct);

            return rawList
                .Where(f => f.Name != "." && f.Name != "..")
                .Select(f => new RemoteItemInfo(
                    Name: f.Name,
                    FullPath: f.FullName,
                    IsDirectory: f.IsDirectory,
                    SizeBytes: f.Length,
                    LastModified: f.LastWriteTimeUtc.ToLocalTime(),
                    Extension: f.IsDirectory ? null : Path.GetExtension(f.Name).ToLowerInvariant()
                ))
                .OrderByDescending(f => f.IsDirectory)
                .ThenBy(f => f.Name)
                .ToList();
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    private async Task ConnectFtpClientAsync(AsyncFtpClient client, bool useTls, int port, CancellationToken ct)
    {
        string? certError = null;
        if (useTls)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.DataConnectionEncryption = true;
            client.Config.SslProtocols = System.Security.Authentication.SslProtocols.None;

            client.ValidateCertificate += (_, e) =>
            {
                if (e.PolicyErrors == System.Net.Security.SslPolicyErrors.None)
                {
                    e.Accept = true;
                    return;
                }

                using var cert2 = new System.Security.Cryptography.X509Certificates.X509Certificate2(e.Certificate);
                var fingerprint = Convert.ToHexString(SHA256.HashData(cert2.RawData));
                var verdict = VerifyFingerprintAsync(client.Host, port, fingerprint, "TLS (FTPS)", ct).GetAwaiter().GetResult();
                e.Accept = verdict.Trusted;
                if (!verdict.Trusted) certError = verdict.ErrorMessage;
            };
        }

        try
        {
            await client.Connect(ct);
        }
        catch (Exception) when (useTls && port == 990 && certError is null)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Implicit;
            await client.Connect(ct);
        }
        catch (Exception) when (certError is not null)
        {
            throw new InvalidOperationException(certError);
        }
    }

    private async Task<List<RemoteItemInfo>> ListFtpItemsAsync(
        string host, int port, string username, string secret, bool useTls, string remotePath, CancellationToken ct)
    {
        using var client = new AsyncFtpClient(host, username, secret, port);
        client.Config.ConnectTimeout = 7000;
        client.Config.DataConnectionConnectTimeout = 8000;
        client.Config.ReadTimeout = 10000;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;

        await ConnectFtpClientAsync(client, useTls, port, ct);

        try
        {
            var items = await client.GetListing(remotePath, FtpListOption.Auto, ct);
            return items
                .Where(f => f.Name != "." && f.Name != "..")
                .Select(f => new RemoteItemInfo(
                    Name: f.Name,
                    FullPath: f.FullName,
                    IsDirectory: f.Type == FtpObjectType.Directory,
                    SizeBytes: f.Size,
                    LastModified: f.Modified.ToLocalTime(),
                    Extension: f.Type == FtpObjectType.Directory ? null : Path.GetExtension(f.Name).ToLowerInvariant()
                ))
                .OrderByDescending(f => f.IsDirectory)
                .ThenBy(f => f.Name)
                .ToList();
        }
        catch (Exception ex) when (useTls && client.Config.DataConnectionEncryption)
        {
            try
            {
                client.Config.DataConnectionEncryption = false;
                var items = await client.GetListing(remotePath, FtpListOption.Auto, ct);
                return items
                    .Where(f => f.Name != "." && f.Name != "..")
                    .Select(f => new RemoteItemInfo(
                        Name: f.Name,
                        FullPath: f.FullName,
                        IsDirectory: f.Type == FtpObjectType.Directory,
                        SizeBytes: f.Size,
                        LastModified: f.Modified.ToLocalTime(),
                        Extension: f.Type == FtpObjectType.Directory ? null : Path.GetExtension(f.Name).ToLowerInvariant()
                    ))
                    .OrderByDescending(f => f.IsDirectory)
                    .ThenBy(f => f.Name)
                    .ToList();
            }
            catch
            {
                throw ex;
            }
        }
        finally
        {
            if (client.IsConnected) await client.Disconnect(ct);
        }
    }

    private static List<RemoteItemInfo> ListSmbItems(string host, string remotePath, string username, string secret)
    {
        var target = remotePath.StartsWith(@"\\") ? remotePath : $@"\\{host}\{remotePath.TrimStart('/', '\\')}";
        using var scope = Executors.SmbConnectionScope.Connect(target, username, secret);

        var dir = new DirectoryInfo(target);
        if (!dir.Exists) throw new DirectoryNotFoundException($"Le partage SMB '{target}' est introuvable.");

        var dirs = dir.GetDirectories()
            .Select(d => new RemoteItemInfo(d.Name, d.FullName, true, 0, d.LastWriteTime, null));
        var files = dir.GetFiles()
            .Select(f => new RemoteItemInfo(f.Name, f.FullName, false, f.Length, f.LastWriteTime, f.Extension.ToLowerInvariant()));

        return dirs.Concat(files).OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name).ToList();
    }

    public async Task<byte[]> DownloadFileBytesAsync(
        StepType protocol, string host, int port, string username, string secret, string remoteFilePath,
        CredentialAuthType authType = CredentialAuthType.MotDePasse, string? passphrase = null, CancellationToken ct = default)
    {
        if (protocol == StepType.TransfertSftp)
        {
            var resolvedPort = port <= 0 ? 22 : port;
            using var client = SftpClientFactory.Create(host, resolvedPort, username, secret, authType, passphrase);
            string? hostKeyError = null;
            client.HostKeyReceived += (_, e) =>
            {
                var fingerprint = Convert.ToHexString(SHA256.HashData(e.HostKey));
                var verdict = VerifyFingerprintAsync(host, resolvedPort, fingerprint, "SSH", ct).GetAwaiter().GetResult();
                e.CanTrust = verdict.Trusted;
                if (!verdict.Trusted) hostKeyError = verdict.ErrorMessage;
            };
            try
            {
                await Task.Run(client.Connect, ct);
            }
            catch (Exception) when (hostKeyError is not null)
            {
                throw new InvalidOperationException(hostKeyError);
            }
            try
            {
                using var ms = new MemoryStream();
                await Task.Run(() => client.DownloadFile(remoteFilePath, ms), ct);
                return ms.ToArray();
            }
            finally
            {
                if (client.IsConnected) client.Disconnect();
            }
        }
        else if (protocol is StepType.TransfertFtp or StepType.TransfertFtps)
        {
            var portNum = port <= 0 ? 21 : port;
            using var client = new AsyncFtpClient(host, username, secret, portNum);
            await ConnectFtpClientAsync(client, protocol == StepType.TransfertFtps, portNum, ct);
            try
            {
                using var ms = new MemoryStream();
                var success = await client.DownloadStream(ms, remoteFilePath, token: ct);
                if (!success) throw new InvalidOperationException("Impossible de télécharger le fichier distant.");
                return ms.ToArray();
            }
            finally
            {
                if (client.IsConnected) await client.Disconnect(ct);
            }
        }
        else
        {
            return await File.ReadAllBytesAsync(remoteFilePath, ct);
        }
    }

    public async Task UploadFileStreamAsync(
        StepType protocol, string host, int port, string username, string secret, string remoteDirectory, string fileName, Stream contentStream,
        CredentialAuthType authType = CredentialAuthType.MotDePasse, string? passphrase = null, CancellationToken ct = default)
    {
        var remotePath = remoteDirectory.TrimEnd('/') + "/" + fileName;

        if (protocol == StepType.TransfertSftp)
        {
            var resolvedPort = port <= 0 ? 22 : port;
            using var client = SftpClientFactory.Create(host, resolvedPort, username, secret, authType, passphrase);
            string? hostKeyError = null;
            client.HostKeyReceived += (_, e) =>
            {
                var fingerprint = Convert.ToHexString(SHA256.HashData(e.HostKey));
                var verdict = VerifyFingerprintAsync(host, resolvedPort, fingerprint, "SSH", ct).GetAwaiter().GetResult();
                e.CanTrust = verdict.Trusted;
                if (!verdict.Trusted) hostKeyError = verdict.ErrorMessage;
            };
            try
            {
                await Task.Run(client.Connect, ct);
            }
            catch (Exception) when (hostKeyError is not null)
            {
                throw new InvalidOperationException(hostKeyError);
            }
            try
            {
                await Task.Run(() => client.UploadFile(contentStream, remotePath, true), ct);
            }
            finally
            {
                if (client.IsConnected) client.Disconnect();
            }
        }
        else if (protocol is StepType.TransfertFtp or StepType.TransfertFtps)
        {
            var portNum = port <= 0 ? 21 : port;
            using var client = new AsyncFtpClient(host, username, secret, portNum);
            await ConnectFtpClientAsync(client, protocol == StepType.TransfertFtps, portNum, ct);
            try
            {
                var status = await client.UploadStream(contentStream, remotePath, FtpRemoteExists.Overwrite, true, token: ct);
                if (status != FtpStatus.Success) throw new InvalidOperationException($"Échec de l'envoi FTP : {status}");
            }
            finally
            {
                if (client.IsConnected) await client.Disconnect(ct);
            }
        }
        else
        {
            // Défense en profondeur : ne garde que le segment nom-de-fichier pour ne jamais écrire hors du
            // dossier SMB actuellement parcouru, même si le nom transmis contient des séparateurs de chemin.
            var safeFileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                throw new InvalidOperationException("Nom de fichier invalide pour l'envoi.");
            }
            var localDest = Path.Combine(remoteDirectory, safeFileName);
            await using var fs = File.Create(localDest);
            await contentStream.CopyToAsync(fs, ct);
        }
    }

    public async Task DeleteItemAsync(
        StepType protocol, string host, int port, string username, string secret, string remotePath, bool isDirectory,
        CredentialAuthType authType = CredentialAuthType.MotDePasse, string? passphrase = null, CancellationToken ct = default)
    {
        if (protocol == StepType.TransfertSftp)
        {
            var resolvedPort = port <= 0 ? 22 : port;
            using var client = SftpClientFactory.Create(host, resolvedPort, username, secret, authType, passphrase);
            string? hostKeyError = null;
            client.HostKeyReceived += (_, e) =>
            {
                var fingerprint = Convert.ToHexString(SHA256.HashData(e.HostKey));
                var verdict = VerifyFingerprintAsync(host, resolvedPort, fingerprint, "SSH", ct).GetAwaiter().GetResult();
                e.CanTrust = verdict.Trusted;
                if (!verdict.Trusted) hostKeyError = verdict.ErrorMessage;
            };
            try
            {
                await Task.Run(client.Connect, ct);
            }
            catch (Exception) when (hostKeyError is not null)
            {
                throw new InvalidOperationException(hostKeyError);
            }
            try
            {
                if (isDirectory) await Task.Run(() => client.DeleteDirectory(remotePath), ct);
                else await Task.Run(() => client.DeleteFile(remotePath), ct);
            }
            finally
            {
                if (client.IsConnected) client.Disconnect();
            }
        }
        else if (protocol is StepType.TransfertFtp or StepType.TransfertFtps)
        {
            var portNum = port <= 0 ? 21 : port;
            using var client = new AsyncFtpClient(host, username, secret, portNum);
            await ConnectFtpClientAsync(client, protocol == StepType.TransfertFtps, portNum, ct);
            try
            {
                if (isDirectory) await client.DeleteDirectory(remotePath, ct);
                else await client.DeleteFile(remotePath, ct);
            }
            finally
            {
                if (client.IsConnected) await client.Disconnect(ct);
            }
        }
        else
        {
            if (isDirectory) Directory.Delete(remotePath, true);
            else File.Delete(remotePath);
        }
    }

    public async Task CreateDirectoryAsync(
        StepType protocol, string host, int port, string username, string secret, string remotePath,
        CredentialAuthType authType = CredentialAuthType.MotDePasse, string? passphrase = null, CancellationToken ct = default)
    {
        if (protocol == StepType.TransfertSftp)
        {
            var resolvedPort = port <= 0 ? 22 : port;
            using var client = SftpClientFactory.Create(host, resolvedPort, username, secret, authType, passphrase);
            string? hostKeyError = null;
            client.HostKeyReceived += (_, e) =>
            {
                var fingerprint = Convert.ToHexString(SHA256.HashData(e.HostKey));
                var verdict = VerifyFingerprintAsync(host, resolvedPort, fingerprint, "SSH", ct).GetAwaiter().GetResult();
                e.CanTrust = verdict.Trusted;
                if (!verdict.Trusted) hostKeyError = verdict.ErrorMessage;
            };
            try
            {
                await Task.Run(client.Connect, ct);
            }
            catch (Exception) when (hostKeyError is not null)
            {
                throw new InvalidOperationException(hostKeyError);
            }
            try
            {
                await Task.Run(() => client.CreateDirectory(remotePath), ct);
            }
            finally
            {
                if (client.IsConnected) client.Disconnect();
            }
        }
        else if (protocol is StepType.TransfertFtp or StepType.TransfertFtps)
        {
            var portNum = port <= 0 ? 21 : port;
            using var client = new AsyncFtpClient(host, username, secret, portNum);
            await ConnectFtpClientAsync(client, protocol == StepType.TransfertFtps, portNum, ct);
            try
            {
                await client.CreateDirectory(remotePath, ct);
            }
            finally
            {
                if (client.IsConnected) await client.Disconnect(ct);
            }
        }
        else
        {
            Directory.CreateDirectory(remotePath);
        }
    }

    public async Task<string> GetFilePreviewAsync(
        StepType protocol, string host, int port, string username, string secret, string remoteFilePath, int maxBytes = 32768,
        CredentialAuthType authType = CredentialAuthType.MotDePasse, string? passphrase = null, CancellationToken ct = default)
    {
        var bytes = await DownloadFileBytesAsync(protocol, host, port, username, secret, remoteFilePath, authType, passphrase, ct);
        if (bytes.Length == 0) return "(Fichier vide)";

        var slice = bytes.Length > maxBytes ? bytes.AsSpan(0, maxBytes).ToArray() : bytes;
        var text = Encoding.UTF8.GetString(slice);
        if (bytes.Length > maxBytes)
        {
            text += $"\n\n[... Aperçu tronqué aux premiers {maxBytes / 1024} Ko (Taille totale : {bytes.Length / 1024} Ko) ...]";
        }
        return text;
    }
}

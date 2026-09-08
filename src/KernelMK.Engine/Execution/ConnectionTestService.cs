using System.Net.Sockets;
using System.Security.Cryptography;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using FluentFTP;
using FluentFTP.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace KernelMK.Engine.Execution;

public record ConnectionTestResult(bool Success, string Message, string? WorkingDirectory = null)
{
    public static ConnectionTestResult Ok(string message, string? workingDir = null) => new(true, message, workingDir);
    public static ConnectionTestResult Fail(string message) => new(false, message);
}

/// <summary>
/// Service de test immédiat de connectivité (SFTP, FTP, FTPS, SMB), à la manière de VisualCron / WinSCP.
/// </summary>
public class ConnectionTestService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<ConnectionTestService> _logger;

    public ConnectionTestService(IDbContextFactory<AppDbContext> dbFactory, ILogger<ConnectionTestService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        StepType type,
        string host,
        int port,
        string? username,
        string? secret,
        string? remotePath = null,
        bool? useTls = null,
        CancellationToken ct = default)
    {
        if (type != StepType.TransfertSmb && string.IsNullOrWhiteSpace(host))
        {
            return ConnectionTestResult.Fail("L'adresse de l'hôte distant (nom ou IP) est obligatoire.");
        }

        var user = string.IsNullOrWhiteSpace(username) ? "anonymous" : username;
        var pwd = secret ?? string.Empty;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var isTls = useTls ?? (type == StepType.TransfertFtps);

        try
        {
            return type switch
            {
                StepType.TransfertSftp => await TestSftpAsync(host, port <= 0 ? 22 : port, user, pwd, remotePath, cts.Token),
                StepType.TransfertFtp or StepType.TransfertFtps => await TestFtpAsync(host, port <= 0 ? 21 : port, user, pwd, isTls, remotePath, cts.Token),
                StepType.TransfertSmb => TestSmb(host, remotePath, user, pwd),
                _ => ConnectionTestResult.Fail($"Le protocole {type} ne supporte pas le test de connexion.")
            };
        }
        catch (OperationCanceledException)
        {
            return ConnectionTestResult.Fail($"Délai d'attente dépassé (timeout 10s). Vérifiez que l'hôte '{host}' et le port sont joignables depuis cette machine.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec du test de connexion vers {Host}:{Port}", host, port);
            return ConnectionTestResult.Fail(ex.Message);
        }
    }

    private async Task<ConnectionTestResult> TestSftpAsync(string host, int port, string username, string secret, string? remotePath, CancellationToken ct)
    {
        using var client = new SftpClient(host, port, username, secret);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(8);

        string? hostKeyWarning = null;
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(e.HostKey));
            var (trusted, err) = VerifyHostKey(host, port, fingerprint);
            if (!trusted) hostKeyWarning = err;
            e.CanTrust = trusted;
        };

        try
        {
            await Task.Run(client.Connect, ct);
        }
        catch (SshAuthenticationException)
        {
            return ConnectionTestResult.Fail("Échec d'authentification SFTP : nom d'utilisateur ou mot de passe refusé par le serveur.");
        }
        catch (SshConnectionException ex)
        {
            return ConnectionTestResult.Fail(hostKeyWarning ?? $"Échec de connexion SSH/SFTP : {ex.Message}");
        }
        catch (SocketException ex)
        {
            return ConnectionTestResult.Fail($"Hôte inaccessible ({host}:{port}) : {ex.Message}");
        }

        try
        {
            var pwd = client.WorkingDirectory;
            var details = $"Répertoire de démarrage : {pwd}";

            if (!string.IsNullOrWhiteSpace(remotePath))
            {
                var exists = client.Exists(remotePath);
                details += exists ? $" · Dossier '{remotePath}' validé." : $" · Attention : le chemin '{remotePath}' n'a pas été trouvé.";
            }

            return ConnectionTestResult.Ok($"Connexion SFTP réussie vers {host}:{port}.", details);
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    private (bool Trusted, string? ErrorMessage) VerifyHostKey(string host, int port, string fingerprint)
    {
        using var db = _dbFactory.CreateDbContext();
        var known = db.TrustedHostKeys.FirstOrDefault(k => k.Host == host && k.Port == port);

        if (known is null)
        {
            db.TrustedHostKeys.Add(new TrustedHostKey { Host = host, Port = port, FingerprintSha256 = fingerprint });
            db.SaveChanges();
            return (true, null);
        }

        if (known.FingerprintSha256 == fingerprint)
        {
            known.LastVerifiedAt = DateTime.UtcNow;
            db.SaveChanges();
            return (true, null);
        }

        return (false, $"Clé d'hôte SSH inattendue pour {host}:{port}. L'empreinte ({fingerprint}) ne correspond pas à celle mémorisée.");
    }

    private static async Task<ConnectionTestResult> TestFtpAsync(string host, int port, string username, string secret, bool useTls, string? remotePath, CancellationToken ct)
    {
        var proto = useTls ? "FTPS" : "FTP";
        if (port == 22 && useTls)
        {
            return ConnectionTestResult.Fail($"Erreur de port ({host}:22) : Le port 22 est réservé au protocole SFTP (SSH), pas pour FTPS. Si vous cherchez à vous connecter en SFTP, sélectionnez 'TransfertSftp'. Si ce serveur utilise bien FTPS, renseignez le bon port (généralement 21 ou 990).");
        }

        try
        {
            return await TryConnectFtpAsync(host, port, username, secret, useTls, remotePath, ct);
        }
        catch (FtpException ex) when (!useTls && (ex.Message.Contains("encryption", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("530")))
        {
            // Tentative automatique avec chiffrement TLS explicite (FTPS)
            try
            {
                var tlsResult = await TryConnectFtpAsync(host, port, username, secret, true, remotePath, ct);
                if (tlsResult.Success)
                {
                    return ConnectionTestResult.Ok(
                        $"Connexion FTPS (TLS explicite) réussie vers {host}:{port}.",
                        (tlsResult.WorkingDirectory ?? "") + " · 💡 Le serveur distant exige le chiffrement TLS explicite (FTPS). La connexion a été validée avec succès.");
                }
            }
            catch
            {
                // Si l'essai FTPS échoue aussi, on retourne le message explicite
            }

            return ConnectionTestResult.Fail($"Erreur FTP (Code 530) vers {host}:{port} : Le serveur distant exige impérativement une session chiffrée FTPS (TLS). Veuillez cocher 'Chiffrement TLS explicite (FTPS)' ou choisir le type 'TransfertFtps'. ({ex.Message})");
        }
        catch (TimeoutException)
        {
            return ConnectionTestResult.Fail($"Délai d'attente dépassé (Timeout) vers {host}:{port} ({proto}). Vérifiez que l'adresse '{host}' et le port {port} sont bien joignables depuis cette machine et autorisés par votre réseau / pare-feu.");
        }
        catch (FtpException ex) when (ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionTestResult.Fail($"Délai d'attente dépassé (Timeout) vers {host}:{port} ({proto}). Vérifiez que l'adresse '{host}' et le port {port} sont bien joignables depuis cette machine et autorisés par votre réseau / pare-feu ({ex.Message}).");
        }
        catch (FtpException ex)
        {
            return ConnectionTestResult.Fail($"Erreur {proto} vers {host}:{port} : {ex.Message}");
        }
        catch (SocketException ex)
        {
            return ConnectionTestResult.Fail($"Serveur {proto} introuvable ({host}:{port}) : {ex.Message}");
        }
    }

    private static async Task<ConnectionTestResult> TryConnectFtpAsync(string host, int port, string username, string secret, bool useTls, string? remotePath, CancellationToken ct)
    {
        using var client = new AsyncFtpClient(host, username, secret, port);
        client.Config.ConnectTimeout = 10000;
        client.Config.DataConnectionConnectTimeout = 10000;
        client.Config.ReadTimeout = 10000;

        if (useTls)
        {
            client.Config.EncryptionMode = port == 990 ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit;
            client.Config.ValidateAnyCertificate = true;
            client.Config.DataConnectionEncryption = true;
            client.Config.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
        }

        await client.Connect(ct);
        try
        {
            var pwd = await client.GetWorkingDirectory(ct);
            var proto = useTls ? (port == 990 ? "FTPS (implicite)" : "FTPS (TLS explicite)") : "FTP";
            var details = $"Répertoire de démarrage : {pwd}";

            if (!string.IsNullOrWhiteSpace(remotePath))
            {
                var exists = await client.DirectoryExists(remotePath, ct) || await client.FileExists(remotePath, ct);
                details += exists ? $" · Chemin '{remotePath}' accessible." : $" · Attention : '{remotePath}' non trouvé.";
            }

            return ConnectionTestResult.Ok($"Connexion {proto} réussie vers {host}:{port}.", details);
        }
        finally
        {
            if (client.IsConnected) await client.Disconnect(ct);
        }
    }

    private static ConnectionTestResult TestSmb(string host, string? remotePath, string? username, string? secret)
    {
        var target = !string.IsNullOrWhiteSpace(remotePath) ? remotePath : host;
        if (!target.StartsWith(@"\\"))
        {
            return ConnectionTestResult.Fail($"Pour SMB, spécifiez un chemin de partage réseau UNC (ex: \\\\{host}\\partage).");
        }

        try
        {
            using var scope = Executors.SmbConnectionScope.Connect(target, username, secret);
            var exists = Directory.Exists(target);
            return exists
                ? ConnectionTestResult.Ok($"Accès au partage réseau SMB réussi ({target}).")
                : ConnectionTestResult.Fail($"Le partage SMB '{target}' n'est pas accessible ou n'existe pas.");
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Fail($"Échec de connexion SMB : {ex.Message}");
        }
    }
}

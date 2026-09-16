using System.Net.Sockets;
using System.Security.Cryptography;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Engine.Notifications;
using FluentFTP;
using FluentFTP.Exceptions;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Data.SqlClient;
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
    private readonly Microsoft365TokenProvider _tokenProvider;
    private readonly ILogger<ConnectionTestService> _logger;

    public ConnectionTestService(IDbContextFactory<AppDbContext> dbFactory, Microsoft365TokenProvider tokenProvider, ILogger<ConnectionTestService> logger)
    {
        _dbFactory = dbFactory;
        _tokenProvider = tokenProvider;
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
        CredentialAuthType authType = CredentialAuthType.MotDePasse,
        string? passphrase = null,
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
                StepType.TransfertSftp => await TestSftpAsync(host, port <= 0 ? 22 : port, user, pwd, remotePath, authType, passphrase, cts.Token),
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

    private async Task<ConnectionTestResult> TestSftpAsync(string host, int port, string username, string secret, string? remotePath, CredentialAuthType authType, string? passphrase, CancellationToken ct)
    {
        using var client = SftpClientFactory.Create(host, port, username, secret, authType, passphrase);
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
            return ConnectionTestResult.Fail(authType == CredentialAuthType.ClePriveeSsh
                ? "Échec d'authentification SFTP : clé privée refusée par le serveur (vérifiez la clé publique associée côté serveur, ou la passphrase)."
                : "Échec d'authentification SFTP : nom d'utilisateur ou mot de passe refusé par le serveur.");
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
        => VerifyFingerprint(host, port, fingerprint, "SSH");

    /// <summary>
    /// Confiance à la première connexion (TOFU) partagée entre clés d'hôte SSH et certificats FTPS :
    /// la première empreinte vue pour un host:port est mémorisée, toute empreinte différente ensuite
    /// est refusée (usurpation possible, ou le serveur a été réinstallé/son certificat renouvelé —
    /// dans ce dernier cas un administrateur doit supprimer l'entrée mémorisée).
    /// </summary>
    private (bool Trusted, string? ErrorMessage) VerifyFingerprint(string host, int port, string fingerprint, string protocolLabel)
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

        return (false, $"Empreinte {protocolLabel} inattendue pour {host}:{port}. L'empreinte reçue ({fingerprint}) ne correspond pas à celle mémorisée ({known.FingerprintSha256}). Connexion refusée par sécurité.");
    }

    private async Task<ConnectionTestResult> TestFtpAsync(string host, int port, string username, string secret, bool useTls, string? remotePath, CancellationToken ct)
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

    private async Task<ConnectionTestResult> TryConnectFtpAsync(string host, int port, string username, string secret, bool useTls, string? remotePath, CancellationToken ct)
    {
        using var client = new AsyncFtpClient(host, username, secret, port);
        client.Config.ConnectTimeout = 10000;
        client.Config.DataConnectionConnectTimeout = 10000;
        client.Config.ReadTimeout = 10000;

        string? certError = null;
        if (useTls)
        {
            // Le standard GUCE et la majorité des serveurs maritimes/douaniers utilisent le chiffrement TLS/SSL explicite (AUTH TLS).
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.DataConnectionEncryption = true;
            client.Config.SslProtocols = System.Security.Authentication.SslProtocols.None;

            // Confiance à la première connexion (TOFU) sur le certificat : de nombreux serveurs maritimes/douaniers
            // utilisent des certificats auto-signés, mais on refuse de valider n'importe quel certificat sans contrôle.
            client.ValidateCertificate += (_, e) =>
            {
                if (e.PolicyErrors == System.Net.Security.SslPolicyErrors.None)
                {
                    e.Accept = true;
                    return;
                }

                using var cert2 = new System.Security.Cryptography.X509Certificates.X509Certificate2(e.Certificate);
                var fingerprint = Convert.ToHexString(SHA256.HashData(cert2.RawData));
                var (trusted, err) = VerifyFingerprint(host, port, fingerprint, "TLS (FTPS)");
                e.Accept = trusted;
                if (!trusted) certError = err;
            };
        }

        try
        {
            await client.Connect(ct);
        }
        catch (Exception) when (useTls && port == 990 && certError is null)
        {
            // Repli sur le mode implicite uniquement si le mode explicite échoue sur le port historique 990
            client.Config.EncryptionMode = FtpEncryptionMode.Implicit;
            await client.Connect(ct);
        }
        catch (Exception) when (certError is not null)
        {
            throw new InvalidOperationException(certError);
        }

        try
        {
            var pwd = await client.GetWorkingDirectory(ct);
            var proto = useTls
                ? (client.Config.EncryptionMode == FtpEncryptionMode.Implicit ? "FTPS (implicite)" : "FTPS (TLS/SSL explicite)")
                : "FTP";
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

    /// <summary>
    /// Teste un compte destiné à l'accès réseau local (SMB) depuis la page Credentials — pour tous les
    /// credentials qui ne sont pas de type FTP/SFTP (ex: comptes de service utilisés pour authentifier
    /// l'accès à un dossier réseau \\serveur\partage, section "Accès disque réseau").
    /// Si un chemin de partage précis (<paramref name="sharePath"/>) est fourni, l'accès réel à ce dossier
    /// est vérifié (lecture du contenu). Sinon, seule l'authentification (login) est validée via le partage
    /// administratif IPC$, présent sur tout serveur Windows — l'accès à un dossier précis dépend ensuite
    /// des droits NTFS/partage accordés à ce compte.
    /// </summary>
    /// <summary>Teste un compte de messagerie IMAP (connexion + authentification) utilisé pour la relève d'EDI reçus par email.</summary>
    public async Task<ConnectionTestResult> TestImapAccountAsync(
        string? host, int port, string? username, string? secret,
        CredentialAuthType authType = CredentialAuthType.MotDePasse, string? oauth2ClientId = null, string? oauth2TenantId = null)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return ConnectionTestResult.Fail("Aucun serveur (hôte) IMAP renseigné sur ce credential.");
        }
        if (string.IsNullOrWhiteSpace(username))
        {
            return ConnectionTestResult.Fail("Aucun nom d'utilisateur renseigné sur ce credential.");
        }
        if (authType == CredentialAuthType.OAuth2Microsoft365 && (string.IsNullOrWhiteSpace(oauth2ClientId) || string.IsNullOrWhiteSpace(oauth2TenantId)))
        {
            return ConnectionTestResult.Fail("Credential OAuth2 Microsoft 365 incomplet : Id d'application (ClientId) ou Id de tenant manquant.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        using var client = new ImapClient();
        try
        {
            await client.ConnectAsync(host, port <= 0 ? 993 : port, SecureSocketOptions.SslOnConnect, cts.Token);

            if (authType == CredentialAuthType.OAuth2Microsoft365)
            {
                var accessToken = await _tokenProvider.GetTokenAsync(oauth2TenantId!, oauth2ClientId!, secret ?? string.Empty, cts.Token);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(username, accessToken), cts.Token);
            }
            else
            {
                await client.AuthenticateAsync(username, secret ?? string.Empty, cts.Token);
            }

            var inboxCount = client.Inbox.Count;
            await client.DisconnectAsync(true, cts.Token);

            return ConnectionTestResult.Ok($"Connexion IMAP réussie vers {host}:{port}.", $"Boîte de réception : {inboxCount} message(s).");
        }
        catch (OperationCanceledException)
        {
            return ConnectionTestResult.Fail($"Délai d'attente dépassé (timeout 10s) vers {host}:{port}.");
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Fail($"Échec de connexion IMAP vers {host}:{port} : {ex.Message}");
        }
    }

    /// <summary>Teste un compte de messagerie SMTP (connexion + authentification, sans envoyer d'email) utilisé pour l'envoi d'EDI par email.</summary>
    public async Task<ConnectionTestResult> TestSmtpAccountAsync(
        string? host, int port, string? username, string? secret, bool useTls,
        CredentialAuthType authType = CredentialAuthType.MotDePasse, string? oauth2ClientId = null, string? oauth2TenantId = null)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return ConnectionTestResult.Fail("Aucun serveur (hôte) SMTP renseigné sur ce credential.");
        }
        if (authType == CredentialAuthType.OAuth2Microsoft365 && (string.IsNullOrWhiteSpace(oauth2ClientId) || string.IsNullOrWhiteSpace(oauth2TenantId)))
        {
            return ConnectionTestResult.Fail("Credential OAuth2 Microsoft 365 incomplet : Id d'application (ClientId) ou Id de tenant manquant.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(host, port <= 0 ? 587 : port, useTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, cts.Token);

            if (!string.IsNullOrWhiteSpace(username))
            {
                if (authType == CredentialAuthType.OAuth2Microsoft365)
                {
                    var accessToken = await _tokenProvider.GetTokenAsync(oauth2TenantId!, oauth2ClientId!, secret ?? string.Empty, cts.Token);
                    await client.AuthenticateAsync(new SaslMechanismOAuth2(username, accessToken), cts.Token);
                }
                else
                {
                    await client.AuthenticateAsync(username, secret ?? string.Empty, cts.Token);
                }
            }

            await client.DisconnectAsync(true, cts.Token);
            return ConnectionTestResult.Ok($"Connexion SMTP réussie vers {host}:{port}.", string.IsNullOrWhiteSpace(username) ? "Aucune authentification testée (serveur relais ouvert)." : "Authentification validée.");
        }
        catch (OperationCanceledException)
        {
            return ConnectionTestResult.Fail($"Délai d'attente dépassé (timeout 10s) vers {host}:{port}.");
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Fail($"Échec de connexion SMTP vers {host}:{port} : {ex.Message}");
        }
    }

    /// <summary>
    /// Teste un credential de type "Base de données" (SQL Server) : connexion + authentification + un SELECT 1
    /// trivial pour confirmer que le serveur répond réellement, pas seulement que le port est ouvert.
    /// </summary>
    public async Task<ConnectionTestResult> TestDatabaseAccountAsync(string? host, int? port, string? username, string? secret, string? database)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return ConnectionTestResult.Fail("Aucun serveur (hôte) renseigné sur ce credential.");
        }
        if (string.IsNullOrWhiteSpace(database))
        {
            return ConnectionTestResult.Fail("Renseigne le nom de la base à tester dans le champ ci-contre (obligatoire pour se connecter à un serveur SQL Server).");
        }

        var server = port is > 0 ? $"{host},{port}" : host;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            UserID = username ?? string.Empty,
            Password = secret ?? string.Empty,
            TrustServerCertificate = true,
            ConnectTimeout = 10
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cts.CancelAfter(TimeSpan.FromSeconds(12));

        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cts.Token);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cts.Token);

            return ConnectionTestResult.Ok($"Connexion SQL Server réussie vers {server}.", $"Base '{database}' accessible, requête de test exécutée avec succès.");
        }
        catch (OperationCanceledException)
        {
            return ConnectionTestResult.Fail($"Délai d'attente dépassé (timeout) vers {server}.");
        }
        catch (SqlException ex)
        {
            return ConnectionTestResult.Fail($"Erreur SQL Server vers {server} : {ex.Message}");
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Fail($"Échec de connexion SQL Server vers {server} : {ex.Message}");
        }
    }

    public Task<ConnectionTestResult> TestSmbAccountAsync(string host, string? username, string? secret, string? sharePath = null)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Task.FromResult(ConnectionTestResult.Fail("Aucun serveur (hôte) renseigné sur ce credential — indique l'adresse du serveur de fichiers dans le champ 'Hôte'."));
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return Task.FromResult(ConnectionTestResult.Fail("Aucun nom d'utilisateur renseigné sur ce credential."));
        }

        if (!string.IsNullOrWhiteSpace(sharePath))
        {
            if (!sharePath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return Task.FromResult(ConnectionTestResult.Fail($"Le chemin de partage doit être au format UNC (ex : \\\\{host}\\partage)."));
            }

            try
            {
                using var scope = Executors.SmbConnectionScope.Connect(sharePath, username, secret);
                if (!Directory.Exists(sharePath))
                {
                    return Task.FromResult(ConnectionTestResult.Fail($"Authentification réussie, mais le dossier '{sharePath}' est introuvable ou inaccessible (vérifiez le chemin et les droits NTFS)."));
                }

                var entryCount = Directory.EnumerateFileSystemEntries(sharePath).Take(1).Any() ? "contient des éléments" : "est vide";
                return Task.FromResult(ConnectionTestResult.Ok(
                    $"Accès complet validé sur {sharePath}.",
                    $"Authentification et lecture du dossier réussies — le dossier {entryCount}."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ConnectionTestResult.Fail($"Échec d'accès à '{sharePath}' : {ex.Message}"));
            }
        }

        var target = host.StartsWith(@"\\", StringComparison.Ordinal) ? host : $@"\\{host}\IPC$";

        try
        {
            using var scope = Executors.SmbConnectionScope.Connect(target, username, secret);
            return Task.FromResult(ConnectionTestResult.Ok(
                $"Authentification réussie auprès de {host}.",
                "Le compte est valide pour ce serveur. Pour vérifier aussi l'accès à un dossier précis, renseignez un chemin de partage (\\\\serveur\\partage) avant de relancer le test."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ConnectionTestResult.Fail($"Échec de l'authentification SMB sur {host} : {ex.Message}"));
        }
    }
}

using System.Text;
using KernelMK.Core.Entities;
using Renci.SshNet;

namespace KernelMK.Engine.Execution;

/// <summary>
/// Construit un client SFTP authentifié soit par mot de passe, soit par clé privée SSH (PEM, avec
/// passphrase optionnelle) — deux méthodes d'authentification également chiffrées au repos dans le
/// coffre-fort de credentials (KernelMK.Core.Entities.Credential).
/// </summary>
public static class SftpClientFactory
{
    public static SftpClient Create(string host, int port, string username, string? secret, CredentialAuthType authType, string? passphrase)
    {
        if (authType == CredentialAuthType.ClePriveeSsh && !string.IsNullOrWhiteSpace(secret))
        {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(secret));
            var keyFile = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, passphrase);

            var connectionInfo = new ConnectionInfo(host, port, username,
                new PrivateKeyAuthenticationMethod(username, keyFile));
            return new SftpClient(connectionInfo);
        }

        return new SftpClient(host, port, username, secret ?? string.Empty);
    }
}

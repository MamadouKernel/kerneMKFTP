using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace KernelMK.Engine.Notifications;

/// <summary>
/// Déchiffre automatiquement le mot de passe SMTP configuré dans appsettings.json quand il est stocké sous
/// forme protégée (préfixe "protected:"), via le même mécanisme Data Protection/DPAPI que les credentials
/// SFTP/FTP/SMB en base. Un mot de passe sans ce préfixe reste utilisé tel quel (clair) pour ne pas casser
/// les déploiements existants — voir PROCEDURE_DEPLOIEMENT.md pour générer une valeur protégée.
/// </summary>
public class SmtpPasswordProtector : IPostConfigureOptions<SmtpOptions>
{
    public const string ProtectedPrefix = "protected:";
    public const string ProtectorPurpose = "KernelMK.SmtpPassword.v1";

    private readonly IDataProtectionProvider _dataProtectionProvider;

    public SmtpPasswordProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _dataProtectionProvider = dataProtectionProvider;
    }

    public void PostConfigure(string? name, SmtpOptions options)
    {
        if (string.IsNullOrEmpty(options.Password) || !options.Password.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var cipherText = options.Password[ProtectedPrefix.Length..];
        var protector = _dataProtectionProvider.CreateProtector(ProtectorPurpose);

        try
        {
            options.Password = protector.Unprotect(cipherText);
        }
        catch (CryptographicException)
        {
            // Déchiffrement impossible (trousseau de clés différent, valeur corrompue...) : on laisse le champ
            // vide plutôt que d'utiliser la valeur chiffrée telle quelle comme mot de passe SMTP en clair —
            // la connexion SMTP échouera alors explicitement au lieu d'un échec d'authentification silencieux.
            options.Password = string.Empty;
        }
    }
}

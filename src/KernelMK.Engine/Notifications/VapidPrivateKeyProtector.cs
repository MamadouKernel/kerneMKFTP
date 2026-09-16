using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace KernelMK.Engine.Notifications;

/// <summary>
/// Déchiffre automatiquement la clé privée VAPID configurée dans appsettings.json quand elle est stockée sous
/// forme protégée (préfixe "protected:"), via le même mécanisme Data Protection/DPAPI que le mot de passe SMTP
/// — voir <see cref="SmtpPasswordProtector"/>. Une clé sans ce préfixe reste utilisée telle quelle.
/// </summary>
public class VapidPrivateKeyProtector : IPostConfigureOptions<VapidOptions>
{
    public const string ProtectedPrefix = "protected:";
    public const string ProtectorPurpose = "KernelMK.VapidPrivateKey.v1";

    private readonly IDataProtectionProvider _dataProtectionProvider;

    public VapidPrivateKeyProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _dataProtectionProvider = dataProtectionProvider;
    }

    public void PostConfigure(string? name, VapidOptions options)
    {
        if (string.IsNullOrEmpty(options.PrivateKey) || !options.PrivateKey.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var cipherText = options.PrivateKey[ProtectedPrefix.Length..];
        var protector = _dataProtectionProvider.CreateProtector(ProtectorPurpose);

        try
        {
            options.PrivateKey = protector.Unprotect(cipherText);
        }
        catch (CryptographicException)
        {
            // Déchiffrement impossible (trousseau de clés différent, valeur corrompue...) : on laisse la clé
            // vide plutôt que d'utiliser la valeur chiffrée telle quelle — l'envoi push échouera alors
            // explicitement (clé VAPID manquante) au lieu d'un échec de signature silencieux.
            options.PrivateKey = null;
        }
    }
}

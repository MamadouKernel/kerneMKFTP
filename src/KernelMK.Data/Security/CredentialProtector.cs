using Microsoft.AspNetCore.DataProtection;

namespace KernelMK.Data.Security;

/// <summary>Chiffre/déchiffre les secrets (mots de passe, clés) stockés dans la table Credentials via ASP.NET Data Protection.</summary>
public class CredentialProtector
{
    private const string Purpose = "KernelMK.CredentialSecrets.v1";
    private readonly IDataProtector _protector;

    public CredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plainText) => _protector.Protect(plainText);

    public string Unprotect(string cipherText) => _protector.Unprotect(cipherText);
}

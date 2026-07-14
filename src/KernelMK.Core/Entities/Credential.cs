namespace KernelMK.Core.Entities;

public enum CredentialType
{
    CompteService,
    FtpSftp,
    Smtp,
    BaseDeDonnees,
    ApiKey
}

/// <summary>Coffre-fort de credentials : le secret est chiffré via IDataProtector avant stockage (jamais en clair).</summary>
public class Credential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CredentialType Type { get; set; }

    public string? Username { get; set; }
    public string EncryptedSecret { get; set; } = string.Empty;

    public string? Host { get; set; }
    public int? Port { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}

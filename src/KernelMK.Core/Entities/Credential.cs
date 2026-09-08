namespace KernelMK.Core.Entities;

public enum CredentialType
{
    CompteService,
    FtpSftp,
    Smtp,
    BaseDeDonnees,
    ApiKey
}

/// <summary>
/// Méthode d'authentification du credential. S'applique principalement au SFTP : les serveurs SSH acceptent
/// soit un mot de passe, soit une paire de clés (bien plus robuste, exigée par de nombreux partenaires).
/// </summary>
public enum CredentialAuthType
{
    MotDePasse,
    ClePriveeSsh
}

/// <summary>Coffre-fort de credentials : le secret est chiffré via IDataProtector avant stockage (jamais en clair).</summary>
public class Credential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CredentialType Type { get; set; }

    public string? Username { get; set; }

    /// <summary>Mot de passe chiffré (AuthType = MotDePasse) ou clé privée SSH au format PEM chiffrée (AuthType = ClePriveeSsh).</summary>
    public string EncryptedSecret { get; set; } = string.Empty;

    public CredentialAuthType AuthType { get; set; } = CredentialAuthType.MotDePasse;

    /// <summary>Passphrase chiffrée protégeant la clé privée SSH, si celle-ci en a une (optionnel).</summary>
    public string? EncryptedPassphrase { get; set; }

    public string? Host { get; set; }
    public int? Port { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}

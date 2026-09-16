namespace KernelMK.Core.Entities;

public enum CredentialType
{
    CompteService,
    FtpSftp,
    Smtp,
    BaseDeDonnees,
    ApiKey,
    /// <summary>Compte de messagerie IMAP utilisé pour relever automatiquement des EDI reçus par email (Host/Port = serveur IMAP).</summary>
    ImapEmail
}

/// <summary>
/// Méthode d'authentification du credential. S'applique principalement au SFTP : les serveurs SSH acceptent
/// soit un mot de passe, soit une paire de clés (bien plus robuste, exigée par de nombreux partenaires).
/// OAuth2Microsoft365 s'applique aux credentials Smtp/ImapEmail : Microsoft a désactivé l'authentification par
/// simple mot de passe (Basic Auth) sur la plupart des tenants Microsoft 365 depuis 2022-2023 — un compte SMTP/IMAP
/// hébergé sur M365 exige ce mode (flux OAuth2 "client credentials" via une application enregistrée dans Entra ID).
/// </summary>
public enum CredentialAuthType
{
    MotDePasse,
    ClePriveeSsh,
    OAuth2Microsoft365
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

    /// <summary>Id d'application (App registration) Entra ID — requis si AuthType = OAuth2Microsoft365. Pas un secret en soi (visible dans le portail Azure), non chiffré.</summary>
    public string? OAuth2ClientId { get; set; }

    /// <summary>Id du tenant Microsoft 365 — requis si AuthType = OAuth2Microsoft365. Non chiffré (identifiant, pas un secret).</summary>
    public string? OAuth2TenantId { get; set; }

    public string? Host { get; set; }
    public int? Port { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Rôles (AppRole) autorisés à voir/sélectionner ce credential dans l'Explorateur distant ou l'édition d'un
    /// job, séparés par des virgules (ex: "Administrateur,Developpeur"). Null ou vide = aucune restriction,
    /// visible par tous les rôles ayant accès à la fonctionnalité (comportement historique, rétro-compatible).
    /// N'affecte pas l'exécution automatique des jobs planifiés, qui tourne sous le compte de service.
    /// </summary>
    public string? AllowedRolesCsv { get; set; }
}

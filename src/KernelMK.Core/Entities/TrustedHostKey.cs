namespace KernelMK.Core.Entities;

/// <summary>
/// Empreinte de clé d'hôte SSH/SFTP mémorisée (confiance à la première connexion - TOFU).
/// Toute connexion ultérieure à ce couple hôte/port doit présenter la même empreinte,
/// sinon la connexion est refusée (protection contre l'usurpation / l'interception).
/// </summary>
public class TrustedHostKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string FingerprintSha256 { get; set; } = string.Empty;
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastVerifiedAt { get; set; } = DateTime.UtcNow;
}

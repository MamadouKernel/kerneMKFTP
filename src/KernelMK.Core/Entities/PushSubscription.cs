namespace KernelMK.Core.Entities;

/// <summary>
/// Abonnement Web Push d'un navigateur/appareil pour un utilisateur donné. Un même utilisateur peut avoir
/// plusieurs abonnements actifs (un par navigateur/appareil sur lequel il a activé les notifications push).
/// Les champs Endpoint/P256dh/Auth proviennent tels quels de PushSubscription.toJSON() côté navigateur
/// (Push API) et sont nécessaires au serveur pour chiffrer et adresser les notifications via WebPush.
/// </summary>
public class PushSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserId { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;

    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

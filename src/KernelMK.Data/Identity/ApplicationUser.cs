using Microsoft.AspNetCore.Identity;

namespace KernelMK.Data.Identity;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Horodatage de la toute première connexion réussie (mot de passe + 2FA/code de secours validés). Null si jamais connecté.</summary>
    public DateTime? FirstLoginAt { get; set; }

    /// <summary>Horodatage de la dernière connexion réussie. Sert de base au verrouillage/désactivation automatique par inactivité (AccountLifecycleService).</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Renseigné automatiquement quand AccountLifecycleService désactive définitivement le compte après 90 jours d'inactivité (traçabilité pour la revue des accès).</summary>
    public DateTime? DeactivatedForInactivityAt { get; set; }

    /// <summary>Horodatage du dernier "Tout marquer lu" sur le centre de notifications. Les exécutions
    /// démarrées après cette date sont considérées comme non lues (badge sur la cloche).</summary>
    public DateTime? NotificationsReadAt { get; set; }
}

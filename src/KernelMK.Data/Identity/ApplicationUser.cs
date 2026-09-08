using Microsoft.AspNetCore.Identity;

namespace KernelMK.Data.Identity;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Horodatage du dernier "Tout marquer lu" sur le centre de notifications. Les exécutions
    /// démarrées après cette date sont considérées comme non lues (badge sur la cloche).</summary>
    public DateTime? NotificationsReadAt { get; set; }
}

using Microsoft.AspNetCore.Identity;

namespace KernelMK.Data.Identity;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

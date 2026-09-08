using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace KernelMK.Data.Identity;

public class AppUserClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    public AppUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> optionsAccessor)
        : base(userManager, roleManager, optionsAccessor)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim("DisplayName", user.DisplayName));
            identity.AddClaim(new Claim(ClaimTypes.GivenName, user.DisplayName));
        }
        identity.AddClaim(new Claim("TwoFactorEnabled", user.TwoFactorEnabled ? "true" : "false"));
        return identity;
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KernelMK.Data.Identity;

/// <summary>
/// Étend les vérifications standard d'Identity (verrouillage, email confirmé...) pour bloquer aussi la connexion
/// des comptes désactivés définitivement (<see cref="ApplicationUser.Active"/> = false), qu'ils soient désactivés
/// manuellement par un administrateur ou automatiquement par <c>AccountLifecycleService</c> après 90 jours
/// d'inactivité. Sans ce correctif, le flag Active n'avait aucun effet réel sur la connexion.
/// </summary>
public class AppSignInManager : SignInManager<ApplicationUser>
{
    public AppSignInManager(
        UserManager<ApplicationUser> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<ApplicationUser>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<ApplicationUser> confirmation)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
    }

    public override async Task<ApplicationUser?> ValidateSecurityStampAsync(ClaimsPrincipal? principal)
    {
        var user = await base.ValidateSecurityStampAsync(principal);
        return user is not null && user.Active && !await UserManager.IsLockedOutAsync(user) ? user : null;
    }

    protected override async Task<SignInResult?> PreSignInCheck(ApplicationUser user)
    {
        if (!user.Active)
        {
            return SignInResult.NotAllowed;
        }

        return await base.PreSignInCheck(user);
    }
}

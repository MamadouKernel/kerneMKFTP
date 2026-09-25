using System.Security.Claims;
using KernelMK.Core.Entities;

namespace KernelMK.Web.Components.Shared;

public static class CredentialAccess
{
    public static bool CanUse(ClaimsPrincipal user, Credential credential) =>
        user.Identity?.IsAuthenticated == true &&
        (user.IsInRole("Administrateur") || string.IsNullOrWhiteSpace(credential.AllowedRolesCsv) ||
         credential.AllowedRolesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Any(user.IsInRole));
}

using KernelMK.Core;
using KernelMK.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KernelMK.Data;

public static class DbInitializer
{
    public static readonly string[] AllRoles =
    {
        nameof(AppRole.Administrateur),
        nameof(AppRole.Superviseur),
        nameof(AppRole.Exploitant),
        nameof(AppRole.Developpeur),
        nameof(AppRole.Auditeur)
    };

    /// <summary>
    /// Applique les migrations et crée les rôles applicatifs. Le compte Administrateur n'est PAS créé ici :
    /// il est demandé à l'utilisateur lors du premier lancement via la page /setup (voir SetupState).
    /// </summary>
    public static async Task SeedAsync(
        AppDbContext db,
        RoleManager<IdentityRole> roleManager,
        UserManager<ApplicationUser> userManager,
        ILogger logger)
    {
        await db.Database.MigrateAsync();

        foreach (var role in AllRoles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }
    }
}

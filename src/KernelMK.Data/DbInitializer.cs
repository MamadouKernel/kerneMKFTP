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

        const string adminEmail = "admin@local";
        var admin = await userManager.FindByEmailAsync(adminEmail);
        if (admin is null)
        {
            admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                DisplayName = "Administrateur",
                EmailConfirmed = true
            };

            const string defaultPassword = "Admin#12345";
            var result = await userManager.CreateAsync(admin, defaultPassword);
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(admin, nameof(AppRole.Administrateur));
                logger.LogWarning(
                    "Compte administrateur initial créé : {Email} / mot de passe par défaut {Password} — à changer immédiatement.",
                    adminEmail, defaultPassword);
            }
            else
            {
                logger.LogError("Échec de création du compte administrateur initial : {Errors}",
                    string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }
    }
}

using KernelMK.Data.Identity;
using KernelMK.Data.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KernelMK.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKernelMKData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ResolveConnectionString(configuration.GetConnectionString("Default") ?? "Data Source=automation-platform.db");

        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(GetKeyRingPath(configuration)))
            .SetApplicationName("KernelMK");

        services.AddScoped<CredentialProtector>();

        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.Password.RequiredLength = 10;
                options.Password.RequireNonAlphanumeric = false;
                options.SignIn.RequireConfirmedAccount = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders()
            .AddSignInManager();

        return services;
    }

    /// <summary>
    /// Convertit un chemin de base de données relatif en chemin absolu ancré sur le dossier de
    /// l'exécutable (AppContext.BaseDirectory). Indispensable en Service Windows : le dossier
    /// courant du processus (Environment.CurrentDirectory) y est C:\Windows\System32 et non le
    /// dossier de l'application, donc un "Data Source=App_Data/xxx.db" relatif pointerait au
    /// mauvais endroit et échouerait avec "unable to open database file".
    /// </summary>
    private static string ResolveConnectionString(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(builder.DataSource) && !Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.Combine(AppContext.BaseDirectory, builder.DataSource);
        }
        return builder.ConnectionString;
    }

    private static string GetKeyRingPath(IConfiguration configuration)
    {
        var dataDir = configuration["DataDirectory"] ?? AppContext.BaseDirectory;
        var path = Path.Combine(dataDir, "keys");
        Directory.CreateDirectory(path);
        return path;
    }
}

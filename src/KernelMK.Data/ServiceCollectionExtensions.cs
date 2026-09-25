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

        // Mode journal WAL (Write-Ahead Logging) : contrairement au mode par défaut ("DELETE"), qui verrouille
        // toute la base pendant une écriture et bloque les lecteurs concurrents, WAL permet aux lectures de
        // continuer pendant qu'un écrivain est actif. Réglage persistant (stocké dans le fichier .db), à activer
        // une seule fois — nécessaire dès que plusieurs utilisateurs consultent l'app pendant qu'un job s'exécute
        // ou qu'un autre utilisateur enregistre une modification (ex. création de job pendant que le dashboard
        // d'un collègue se rafraîchit automatiquement) : cause probable des lenteurs constatées en production.
        EnableWalMode(connectionString);

        services.AddDbContextFactory<AppDbContext>(options => options
            .UseSqlite(connectionString)
            .AddInterceptors(new SqlitePragmaInterceptor()));
        services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        // Le trousseau est persisté sur disque (obligatoire pour survivre aux redémarrages/mises à jour) mais
        // chiffré au repos via DPAPI machine : une copie du dossier keys\ (ex. sauvegarde volée) est inexploitable
        // sans être déchiffrée depuis cette machine Windows précise — la clé seule ne suffit plus à lire les
        // credentials SFTP/FTP/SMTP/BD chiffrés en base.
        var dataProtectionBuilder = services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(GetKeyRingPath(configuration)))
            .SetApplicationName("KernelMK");

        if (OperatingSystem.IsWindows())
        {
            dataProtectionBuilder.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        // Singleton : n'enveloppe que IDataProtectionProvider (lui-même singleton, sans état par requête) —
        // nécessaire pour être consommable par les exécuteurs d'étapes, eux aussi singletons (StepExecutorFactory).
        services.AddSingleton<CredentialProtector>();

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
            .AddSignInManager<AppSignInManager>()
            .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>()
            .AddErrorDescriber<FrenchIdentityErrorDescriber>();

        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));
        services.AddSingleton<UserAccessService>();

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

    private static void EnableWalMode(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
    }

    private static string GetKeyRingPath(IConfiguration configuration)
    {
        var dataDir = configuration["DataDirectory"] ?? AppContext.BaseDirectory;
        var path = Path.Combine(dataDir, "keys");
        Directory.CreateDirectory(path);
        return path;
    }
}

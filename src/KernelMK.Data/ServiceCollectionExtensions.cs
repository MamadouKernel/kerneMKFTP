using KernelMK.Data.Identity;
using KernelMK.Data.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KernelMK.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKernelMKData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default") ?? "Data Source=automation-platform.db";

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

    private static string GetKeyRingPath(IConfiguration configuration)
    {
        var dataDir = configuration["DataDirectory"] ?? AppContext.BaseDirectory;
        var path = Path.Combine(dataDir, "keys");
        Directory.CreateDirectory(path);
        return path;
    }
}

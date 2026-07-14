using KernelMK.Core;
using KernelMK.Data;
using KernelMK.Data.Identity;
using KernelMK.Engine;
using KernelMK.Engine.Execution;
using KernelMK.Web;
using KernelMK.Web.Components;
using KernelMK.Web.Components.Account;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

// Journalisation fichier (rotation quotidienne, 30 jours conservés) + console, pour pouvoir analyser
// le comportement de l'application une fois installée en Service Windows (pas de fenêtre console).
var logsPath = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logsPath);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logsPath, "kernelmk-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("Démarrage de kernelMK...");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(logsPath, "kernelmk-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            shared: true,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}"));

    // Permet d'exécuter l'application comme Service Windows (installation via sc.exe/New-Service) ;
    // sans effet quand elle est lancée normalement (console, IIS Express, dotnet run).
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "KernelMK";
    });

    // Garantit l'existence du dossier de données (fichier SQLite) au premier lancement, y compris depuis l'exécutable publié.
    Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
    Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "backups"));

    builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Administrateur", p => p.RequireRole("Administrateur"))
    .AddPolicy("Superviseur", p => p.RequireRole("Administrateur", "Superviseur"))
    .AddPolicy("Exploitant", p => p.RequireRole("Administrateur", "Superviseur", "Exploitant"))
    .AddPolicy("Developpeur", p => p.RequireRole("Administrateur", "Developpeur"))
    .AddPolicy("Auditeur", p => p.RequireRole("Administrateur", "Auditeur"));

builder.Services.AddKernelMKData(builder.Configuration);
builder.Services.AddKernelMKEngine(builder.Configuration);

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
builder.Services.AddSingleton<SetupState>();

var app = builder.Build();

app.UseSerilogRequestLogging();

var setupState = app.Services.GetRequiredService<SetupState>();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DbInitializer.SeedAsync(db, roleManager, userManager, logger);

    setupState.AdminExists = (await userManager.GetUsersInRoleAsync(nameof(AppRole.Administrateur))).Count > 0;
}

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

// Tant qu'aucun compte Administrateur n'existe, toute requête est redirigée vers /setup
// (premier lancement : l'utilisateur doit créer le compte admin avant d'accéder au reste de l'app).
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isSetupRoute = path.StartsWithSegments("/setup");
    var isFrameworkAsset = path.StartsWithSegments("/_blazor") || path.StartsWithSegments("/_framework")
        || path.StartsWithSegments("/css") || path.StartsWithSegments("/js") || path.StartsWithSegments("/favicon.png");

    if (!setupState.AdminExists && !isSetupRoute && !isFrameworkAsset)
    {
        context.Response.Redirect("/setup");
        return;
    }

    if (setupState.AdminExists && isSetupRoute)
    {
        context.Response.Redirect("/Account/Login");
        return;
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

// Déclenchement de job via appel HTTP sécurisé / webhook (section 4.3 "API").
app.MapPost("/api/triggers/{token}", async (string token, IDbContextFactory<AppDbContext> dbFactory, IJobRunner jobRunner) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var trigger = await db.JobTriggers.FirstOrDefaultAsync(t => t.Type == TriggerType.Api && t.WebhookToken == token && t.Enabled);
    if (trigger is null) return Results.NotFound();

    var execution = await jobRunner.RunAsync(trigger.JobId, "API/Webhook");
    return Results.Ok(new { execution.Id, Status = execution.Status.ToString() });
});

    app.Run();
}
catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
{
    var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5000 (par défaut)";
    Log.Fatal(ex,
        "Impossible de démarrer : le port est déjà utilisé par une autre application ({Urls}). " +
        "Vérifie ce qui occupe ce port avec 'netstat -ano | findstr :5000' (remplace 5000 par le bon port), " +
        "puis soit ferme cette application, soit change le port de kernelMK via la variable d'environnement " +
        "ASPNETCORE_URLS ou la section Kestrel:Endpoints de appsettings.json.", urls);
}
catch (Exception ex)
{
    Log.Fatal(ex, "kernelMK s'est arrêté de façon inattendue.");
}
finally
{
    Log.CloseAndFlush();
}

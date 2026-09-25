using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Data.Identity;
using KernelMK.Engine;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Queue;
using KernelMK.Engine.Notifications;
using KernelMK.Web;
using KernelMK.Web.Security;
using KernelMK.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using KernelMK.Web.Components;
using KernelMK.Web.Components.Account;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using QuestPDF.Infrastructure;
using Serilog;
using Serilog.Events;

// Utilitaire hors-ligne : "KernelMK.exe --protect-smtp-password <motDePasse>" affiche la valeur chiffrée à coller
// dans appsettings.json (Smtp:Password), puis quitte sans démarrer le serveur web. Utilise le même trousseau de
// clés Data Protection/DPAPI que l'application (dossier "keys", éventuellement sous DataDirectory) pour que la
// valeur produite soit déchiffrable par l'application elle-même au démarrage (cf. SmtpPasswordProtector).
if (args.Length == 2 && args[0] == "--protect-smtp-password")
{
    var cliConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build();
    var keysDir = Path.Combine(cliConfig["DataDirectory"] ?? AppContext.BaseDirectory, "keys");
    Directory.CreateDirectory(keysDir);

    var dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(keysDir), builder =>
    {
        builder.SetApplicationName("KernelMK");
        if (OperatingSystem.IsWindows())
        {
            builder.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }
    });

    var protector = dataProtectionProvider.CreateProtector(SmtpPasswordProtector.ProtectorPurpose);
    var protectedValue = SmtpPasswordProtector.ProtectedPrefix + protector.Protect(args[1]);

    Console.WriteLine();
    Console.WriteLine("Valeur protégée à coller dans appsettings.json (section \"Smtp\" -> \"Password\") :");
    Console.WriteLine();
    Console.WriteLine(protectedValue);
    Console.WriteLine();
    return;
}

// Utilitaire hors-ligne : "KernelMK.exe --generate-vapid-keys" génère une paire de clés VAPID (protocole Web
// Push) et affiche la clé publique en clair (à coller dans "Vapid":"PublicKey") ainsi que la clé privée déjà
// protégée par Data Protection/DPAPI (à coller dans "Vapid":"PrivateKey", prête à l'emploi — pas besoin d'un
// second passage par --protect-vapid-key). Ne démarre pas le serveur web.
if (args.Length == 1 && args[0] == "--generate-vapid-keys")
{
    var cliConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build();
    var keysDir = Path.Combine(cliConfig["DataDirectory"] ?? AppContext.BaseDirectory, "keys");
    Directory.CreateDirectory(keysDir);

    var dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(keysDir), builder =>
    {
        builder.SetApplicationName("KernelMK");
        if (OperatingSystem.IsWindows())
        {
            builder.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }
    });

    var vapidKeys = WebPush.VapidHelper.GenerateVapidKeys();
    var protector = dataProtectionProvider.CreateProtector(VapidPrivateKeyProtector.ProtectorPurpose);
    var protectedPrivateKey = VapidPrivateKeyProtector.ProtectedPrefix + protector.Protect(vapidKeys.PrivateKey);

    Console.WriteLine();
    Console.WriteLine("Valeurs à coller dans appsettings.json (section \"Vapid\") :");
    Console.WriteLine();
    Console.WriteLine("  \"PublicKey\": \"" + vapidKeys.PublicKey + "\",");
    Console.WriteLine("  \"PrivateKey\": \"" + protectedPrivateKey + "\"");
    Console.WriteLine();
    Console.WriteLine("Pense à renseigner aussi \"Subject\" (ex: \"mailto:support@cit.ci\").");
    Console.WriteLine();
    return;
}

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

    // Licence Community QuestPDF (gratuite pour les entités de moins de 1M$ de revenu annuel — cf. questpdf.com/license).
    QuestPDF.Settings.License = LicenseType.Community;

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
    // Ancré sur AppContext.BaseDirectory (et non ContentRootPath) pour toujours correspondre exactement
    // au dossier utilisé par la chaîne de connexion SQLite (cf. ServiceCollectionExtensions.ResolveConnectionString).
    Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "App_Data"));
    Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "backups"));

    // Before data services open SQLite, validate and atomically apply a staged restore with WAL safety.
    try
    {
        if (KernelMK.Engine.Backup.BackupService.ApplyPendingDatabaseRestore(builder.Configuration))
            Log.Information("Restauration de base de données appliquée avec succès.");
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Restauration refusée ; la base existante et la demande de restauration sont conservées.");
    }

    builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("webhook", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("ApplicationAccess", p => p.RequireAuthenticatedUser().RequireClaim("TwoFactorEnabled", "true"))
    .AddPolicy("Administrateur", p => p.RequireRole("Administrateur"))
    .AddPolicy("Superviseur", p => p.RequireRole("Administrateur", "Superviseur"))
    .AddPolicy("Exploitant", p => p.RequireRole("Administrateur", "Superviseur", "Exploitant"))
    .AddPolicy("Developpeur", p => p.RequireRole("Administrateur", "Developpeur"))
    .AddPolicy("Auditeur", p => p.RequireRole("Administrateur", "Auditeur"))
    // Filet de sécurité : toute page qui ne porte ni [Authorize] ni [AllowAnonymous] exige désormais une
    // authentification par défaut, au lieu d'être accessible par erreur si l'attribut est oublié un jour.
    // Les pages volontairement publiques (Login, Setup, /Error...) portent explicitement [AllowAnonymous].
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

builder.Services.AddKernelMKData(builder.Configuration);
builder.Services.AddKernelMKEngine(builder.Configuration);
builder.Services.AddDashboardSnapshots();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
builder.Services.AddSingleton<SetupState>();

var app = builder.Build();

app.UseMiddleware<SecurityHeadersMiddleware>();

// Legacy webhook URLs contain a bearer secret. Never record those paths in HTTP request logs.
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api/triggers"),
    pipeline => pipeline.UseSerilogRequestLogging());

var setupState = app.Services.GetRequiredService<SetupState>();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DbInitializer.SeedAsync(db, roleManager, userManager, logger);

    setupState.AdminExists = (await userManager.GetUsersInRoleAsync(nameof(AppRole.Administrateur))).Count > 0;

    // Recovery runs before hosted dispatchers: queued work survives, uncertain effects require review.
    var recovered = await scope.ServiceProvider.GetRequiredService<JobQueueService>().RecoverInterruptedAsync();
    if (recovered > 0) logger.LogWarning("{Count} élément(s) interrompu(s) signalé(s) au démarrage.", recovered);
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
        || path.StartsWithSegments("/css") || path.StartsWithSegments("/js") || path.StartsWithSegments("/images")
        || path.StartsWithSegments("/favicon.png") || path.StartsWithSegments("/favicon.ico")
        || path.StartsWithSegments("/sw.js") || path.StartsWithSegments("/manifest.json");

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

// Politique de Sécurité CIT : L'authentification 2FA est STRICTEMENT OBLIGATOIRE pour tous les utilisateurs.
// Tout utilisateur connecté dont le 2FA n'est pas encore configuré est automatiquement bloqué
// et redirigé vers /Account/Manage/EnableAuthenticator.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;

    // Laisser passer les ressources statiques et endpoints techniques
    var isAsset = path.StartsWithSegments("/_blazor")
               || path.StartsWithSegments("/_framework")
               || path.StartsWithSegments("/css")
               || path.StartsWithSegments("/js")
               || path.StartsWithSegments("/images")
               || path.StartsWithSegments("/favicon.png")
               || path.StartsWithSegments("/favicon.ico")
               || path.StartsWithSegments("/sw.js")
               || path.StartsWithSegments("/manifest.json")
               // Seul le webhook /api/triggers est volontairement anonyme (jeton dans l'URL, pas de session) ;
               // avant ce correctif, TOUT /api (y compris /api/push/*, qui exige une session authentifiée) était
               // exempté du contrôle 2FA, permettant à un compte connecté sans 2FA finalisé de s'abonner aux
               // notifications push — incohérent avec le blocage strict appliqué à tout le reste de l'app.
               || path.StartsWithSegments("/api/triggers");

    if (isAsset)
    {
        await next();
        return;
    }

    if (context.User.Identity?.IsAuthenticated == true)
    {
        var is2faClaim = context.User.FindFirst("TwoFactorEnabled")?.Value;
        if (is2faClaim != "true")
        {
            var isAllowed2faRoute = path.StartsWithSegments("/Account/Manage/EnableAuthenticator")
                                 || path.StartsWithSegments("/Account/Manage/ShowRecoveryCodes")
                                 || path.StartsWithSegments("/Account/Logout")
                                 || path.StartsWithSegments("/Account/AccessDenied");

            if (!isAllowed2faRoute)
            {
                context.Response.Redirect("/Account/Manage/EnableAuthenticator");
                return;
            }
        }
    }

    await next();
});

app.UseRateLimiter();
app.UseAntiforgery();

// Login/setup also need styles, scripts and images before a user has a session.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

// Déclenchement de job via appel HTTP sécurisé / webhook (section 4.3 "API").
// Comparaison à temps constant du jeton pour ne pas exposer d'oracle de timing sur un secret d'authentification.
// AllowAnonymous explicite : un système externe appelant ce webhook n'a pas de cookie de session ASP.NET Core —
// son authentification passe uniquement par le jeton dans l'URL, vérifié ci-dessous. Sans cet attribut, la
// FallbackPolicy globale (RequireAuthenticatedUser) bloquerait l'appel avant même que le jeton soit vérifié.
app.MapPost("/api/triggers/{token}", async (string token, HttpContext context, IDbContextFactory<AppDbContext> dbFactory, JobQueueService queue) =>
{
    if (string.IsNullOrEmpty(token) || token.Length > 512) return Results.NotFound();
    await using var db = await dbFactory.CreateDbContextAsync();
    var candidates = await db.JobTriggers.AsNoTracking()
        .Where(t => t.Type == TriggerType.Api && t.Enabled && t.Job!.Enabled)
        .Select(t => new { t.Id, t.JobId, t.WebhookToken }).ToListAsync();

    var tokenBytes = System.Text.Encoding.UTF8.GetBytes(token);
    var trigger = candidates.FirstOrDefault(t =>
        !string.IsNullOrEmpty(t.WebhookToken) &&
        t.WebhookToken.Length == token.Length &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(tokenBytes, System.Text.Encoding.UTF8.GetBytes(t.WebhookToken)));

    if (trigger is null) return Results.NotFound();

    var suppliedKey = context.Request.Headers["Idempotency-Key"];
    if (suppliedKey.Count > 1 || suppliedKey.ToString().Length > 200)
        return Results.BadRequest("Clé d'idempotence invalide (200 caractères maximum).");
    string? key = string.IsNullOrWhiteSpace(suppliedKey.ToString()) ? null :
        $"webhook:{trigger.Id:N}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(suppliedKey.ToString())))}";
    var request = await queue.EnqueueAsync(trigger.JobId, "API/Webhook", idempotencyKey: key, ct: context.RequestAborted);
    return Results.Accepted(value: new { RequestId = request.Id, Status = request.Status.ToString() });
})
.AllowAnonymous()
.RequireRateLimiting("webhook");

// Notifications push navigateur (Web Push) : clé publique VAPID nécessaire côté client pour s'abonner, puis
// abonnement/désabonnement par appareil. Ces trois endpoints restent soumis à la FallbackPolicy globale
// (RequireAuthenticatedUser) — pas de .AllowAnonymous() ici, contrairement au webhook ci-dessus, car un
// abonnement push est toujours rattaché à un utilisateur kernelMK authentifié.
app.MapGet("/api/push/vapid-public-key", (Microsoft.Extensions.Options.IOptionsMonitor<VapidOptions> vapid) =>
    Results.Text(vapid.CurrentValue.PublicKey ?? string.Empty, "text/plain"))
    .RequireAuthorization("ApplicationAccess");

app.MapGet("/api/security/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
}).RequireAuthorization("ApplicationAccess");

var pushGroup = app.MapGroup("/api/push").RequireAuthorization("ApplicationAccess");
pushGroup.AddEndpointFilter(async (context, next) =>
{
    try
    {
        await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>()
            .ValidateRequestAsync(context.HttpContext);
    }
    catch (AntiforgeryValidationException) { return Results.BadRequest("Jeton de sécurité absent ou invalide."); }
    return await next(context);
});
pushGroup.MapPost("/subscribe", PushSubscriptionSecurity.SubscribeAsync);
pushGroup.MapPost("/unsubscribe", PushSubscriptionSecurity.UnsubscribeAsync);

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

/// <summary>Forme JSON renvoyée par PushSubscription.toJSON() côté navigateur (Push API).</summary>
public record PushSubscribeRequest(string Endpoint, PushKeys? Keys);
public record PushKeys(string P256dh, string Auth);
public record PushUnsubscribeRequest(string Endpoint);

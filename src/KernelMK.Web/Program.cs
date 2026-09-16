using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Data.Identity;
using KernelMK.Engine;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Notifications;
using KernelMK.Web;
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

    // Applique une restauration de base de données mise en attente (page /backup → BackupService.
    // StagePendingDatabaseRestoreAsync) — fait ici, avant toute connexion SQLite, car remplacer le fichier
    // pendant que l'application tourne risquerait un verrou Windows ou une corruption. La base courante est
    // sauvegardée avant d'être écrasée, pour permettre un retour arrière si la restauration était une erreur.
    {
        // La chaîne de connexion réelle porte des paramètres après le chemin (ex. "...db;Cache=Shared") : ne
        // retirer que le préfixe "Data Source=" par Replace() laissait ce suffixe collé au chemin, ce qui faisait
        // échouer silencieusement toute restauration en attente (File.Exists/File.Copy sur un chemin invalide,
        // capturé par le catch ci-dessous et journalisé comme "Échec" sans jamais appliquer la restauration
        // demandée). SqliteConnectionStringBuilder analyse correctement la chaîne quel que soit son format.
        var dbConnString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=automation-platform.db";
        var dbPath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(dbConnString).DataSource;
        if (!Path.IsPathRooted(dbPath)) dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);
        var dbDir = Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory;
        var pendingRestorePath = Path.Combine(dbDir, KernelMK.Engine.Backup.BackupService.PendingRestoreFileName);

        if (File.Exists(pendingRestorePath))
        {
            Log.Information("Restauration de base de données en attente détectée ({Path}) — application avant démarrage.", pendingRestorePath);
            try
            {
                if (File.Exists(dbPath))
                {
                    var safetyBackupDir = Path.Combine(AppContext.BaseDirectory, "backups");
                    Directory.CreateDirectory(safetyBackupDir);
                    var safetyBackupPath = Path.Combine(safetyBackupDir, $"automation-platform_avant-restauration_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db");
                    File.Copy(dbPath, safetyBackupPath, overwrite: true);
                    Log.Information("Base actuelle sauvegardée avant restauration : {Path}", safetyBackupPath);
                }
                File.Copy(pendingRestorePath, dbPath, overwrite: true);
                File.Delete(pendingRestorePath);
                Log.Information("Restauration de base de données appliquée avec succès.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Échec de l'application de la restauration de base de données en attente — démarrage avec la base existante.");
            }
        }
    }

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
    .AddPolicy("Auditeur", p => p.RequireRole("Administrateur", "Auditeur"))
    // Filet de sécurité : toute page qui ne porte ni [Authorize] ni [AllowAnonymous] exige désormais une
    // authentification par défaut, au lieu d'être accessible par erreur si l'attribut est oublié un jour.
    // Les pages volontairement publiques (Login, Setup, /Error...) portent explicitement [AllowAnonymous].
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

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

    // Nettoyage des exécutions orphelines : si le service a été arrêté brutalement (crash, redémarrage,
    // application d'un patch) pendant qu'un job tournait, sa ligne JobExecution restait bloquée pour toujours
    // au statut "EnCours" en base — rien ne la faisait jamais basculer vers un état terminal. Le tableau de
    // bord comptait ces lignes fantômes comme des jobs réellement en cours (KPI "En cours" qui ne fait
    // qu'augmenter au fil des redémarrages), alors qu'aucun processus ne les exécute plus depuis le redémarrage.
    var orphanedExecutions = await db.JobExecutions
        .Where(e => e.Status == JobStatus.EnCours)
        .Include(e => e.StepLogs)
        .ToListAsync();
    if (orphanedExecutions.Count > 0)
    {
        var now = DateTime.UtcNow;
        foreach (var exec in orphanedExecutions)
        {
            exec.Status = JobStatus.Annule;
            exec.FinishedAt = now;
            exec.Message = "Exécution interrompue par un redémarrage du service (jamais reprise automatiquement).";
            foreach (var step in exec.StepLogs.Where(s => s.Status == StepExecutionStatus.EnCours))
            {
                step.Status = StepExecutionStatus.Annule;
                step.FinishedAt = now;
                step.ErrorOutput ??= "Étape interrompue par un redémarrage du service.";
            }
        }
        await db.SaveChangesAsync();
        logger.LogWarning("{Count} exécution(s) orpheline(s) (bloquée(s) en \"EnCours\" suite à un arrêt brutal précédent) marquée(s) comme annulée(s) au démarrage.", orphanedExecutions.Count);
    }
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
        || path.StartsWithSegments("/css") || path.StartsWithSegments("/js") || path.StartsWithSegments("/favicon.png")
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

// Court-circuite le pipeline avant UseAuthentication/UseAuthorization : la FallbackPolicy globale
// (RequireAuthenticatedUser) s'applique aussi aux endpoints de MapStaticAssets(), donc même avec ces chemins
// exemptés dans les middlewares personnalisés ci-dessus, manifest.json et sw.js restaient redirigés (302) vers
// /Account/Login pour tout visiteur non authentifié — y compris sur l'écran de connexion lui-même, où le
// <link rel="manifest"> les référence avant toute authentification possible : le navigateur recevait du HTML
// au lieu du JSON attendu, d'où l'erreur console "Manifest: ... Syntax error" permanente. Servir le fichier ici,
// avant que l'autorisation n'entre en jeu, élimine le problème à la racine plutôt que de rivaliser avec
// MapStaticAssets() sur la même route via un MapGet().AllowAnonymous() (essayé, inefficace : la requête restait
// bloquée, signe que ce chemin ne passait jamais par le point d'exécution des endpoints applicatifs).
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path == "/manifest.json" || path == "/sw.js")
    {
        var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var fileName = path == "/manifest.json" ? "manifest.json" : "sw.js";
        context.Response.ContentType = fileName.EndsWith(".json") ? "application/manifest+json" : "text/javascript";
        await context.Response.SendFileAsync(Path.Combine(env.WebRootPath, fileName));
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

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

// Déclenchement de job via appel HTTP sécurisé / webhook (section 4.3 "API").
// Comparaison à temps constant du jeton pour ne pas exposer d'oracle de timing sur un secret d'authentification.
// AllowAnonymous explicite : un système externe appelant ce webhook n'a pas de cookie de session ASP.NET Core —
// son authentification passe uniquement par le jeton dans l'URL, vérifié ci-dessous. Sans cet attribut, la
// FallbackPolicy globale (RequireAuthenticatedUser) bloquerait l'appel avant même que le jeton soit vérifié.
app.MapPost("/api/triggers/{token}", async (string token, IDbContextFactory<AppDbContext> dbFactory, IJobRunner jobRunner) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var candidates = await db.JobTriggers.Where(t => t.Type == TriggerType.Api && t.Enabled).ToListAsync();

    var tokenBytes = System.Text.Encoding.UTF8.GetBytes(token);
    var trigger = candidates.FirstOrDefault(t =>
        !string.IsNullOrEmpty(t.WebhookToken) &&
        t.WebhookToken.Length == token.Length &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(tokenBytes, System.Text.Encoding.UTF8.GetBytes(t.WebhookToken)));

    if (trigger is null) return Results.NotFound();

    var execution = await jobRunner.RunAsync(trigger.JobId, "API/Webhook");
    return Results.Ok(new { execution.Id, Status = execution.Status.ToString() });
})
.AllowAnonymous();

// Notifications push navigateur (Web Push) : clé publique VAPID nécessaire côté client pour s'abonner, puis
// abonnement/désabonnement par appareil. Ces trois endpoints restent soumis à la FallbackPolicy globale
// (RequireAuthenticatedUser) — pas de .AllowAnonymous() ici, contrairement au webhook ci-dessus, car un
// abonnement push est toujours rattaché à un utilisateur kernelMK authentifié.
app.MapGet("/api/push/vapid-public-key", (Microsoft.Extensions.Options.IOptionsMonitor<VapidOptions> vapid) =>
    Results.Text(vapid.CurrentValue.PublicKey ?? string.Empty, "text/plain"));

app.MapPost("/api/push/subscribe", async (PushSubscribeRequest req, HttpContext http, IDbContextFactory<AppDbContext> dbFactory, UserManager<ApplicationUser> userManager) =>
{
    var userId = userManager.GetUserId(http.User);
    if (userId is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.Endpoint) || req.Keys is null
        || string.IsNullOrWhiteSpace(req.Keys.P256dh) || string.IsNullOrWhiteSpace(req.Keys.Auth))
    {
        return Results.BadRequest();
    }

    await using var db = await dbFactory.CreateDbContextAsync();
    var existing = await db.PushSubscriptions.FirstOrDefaultAsync(p => p.Endpoint == req.Endpoint);
    if (existing is not null)
    {
        existing.UserId = userId;
        existing.P256dh = req.Keys.P256dh;
        existing.Auth = req.Keys.Auth;
        existing.UserAgent = http.Request.Headers.UserAgent.ToString();
    }
    else
    {
        db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = userId,
            Endpoint = req.Endpoint,
            P256dh = req.Keys.P256dh,
            Auth = req.Keys.Auth,
            UserAgent = http.Request.Headers.UserAgent.ToString()
        });
    }
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapPost("/api/push/unsubscribe", async (PushUnsubscribeRequest req, IDbContextFactory<AppDbContext> dbFactory) =>
{
    if (string.IsNullOrWhiteSpace(req.Endpoint)) return Results.BadRequest();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.PushSubscriptions.Where(p => p.Endpoint == req.Endpoint).ExecuteDeleteAsync();
    return Results.Ok();
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

/// <summary>Forme JSON renvoyée par PushSubscription.toJSON() côté navigateur (Push API).</summary>
public record PushSubscribeRequest(string Endpoint, PushKeys? Keys);
public record PushKeys(string P256dh, string Auth);
public record PushUnsubscribeRequest(string Endpoint);

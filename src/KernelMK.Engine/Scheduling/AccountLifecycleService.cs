using KernelMK.Core;
using KernelMK.Data.Identity;
using KernelMK.Engine.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Scheduling;

/// <summary>
/// Politique de sécurité CIT sur le cycle de vie des comptes (revue des accès) : verrouille automatiquement
/// tout compte sans connexion depuis 30 jours, et le désactive définitivement (réactivable par un administrateur,
/// jamais supprimé) sans connexion depuis 90 jours. Le dernier compte Administrateur actif restant est toujours
/// exempté, pour ne jamais bloquer l'accès total à l'application par accident.
/// </summary>
public class AccountLifecycleService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan LockAfter = TimeSpan.FromDays(30);
    private static readonly TimeSpan DeactivateAfter = TimeSpan.FromDays(90);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountLifecycleService> _logger;

    public AccountLifecycleService(IServiceScopeFactory scopeFactory, ILogger<AccountLifecycleService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du cycle de gestion du cycle de vie des comptes utilisateurs.");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var auditService = scope.ServiceProvider.GetRequiredService<AuditService>();

        var now = DateTime.UtcNow;
        var lockThreshold = now - LockAfter;
        var deactivateThreshold = now - DeactivateAfter;

        var admins = await userManager.GetUsersInRoleAsync(nameof(AppRole.Administrateur));
        var activeAdmins = admins.Where(a => a.Active).ToList();
        var protectedUserId = activeAdmins.Count == 1 ? activeAdmins[0].Id : null;

        var users = userManager.Users.ToList();

        foreach (var user in users)
        {
            ct.ThrowIfCancellationRequested();

            if (!user.Active || user.Id == protectedUserId) continue;

            var reference = user.LastLoginAt ?? user.CreatedAt;
            var displayName = user.Email ?? user.UserName ?? user.Id;

            if (reference <= deactivateThreshold)
            {
                user.Active = false;
                user.DeactivatedForInactivityAt = now;
                await userManager.UpdateAsync(user);

                await auditService.LogAsync(
                    AuditAction.Desactivation, nameof(ApplicationUser), user.Id, displayName,
                    null, "Système (revue des accès automatique)",
                    $"Désactivation définitive automatique : aucune connexion depuis plus de 90 jours (dernière connexion : {FormatLastLogin(user.LastLoginAt)}). Réactivable par un administrateur.");

                _logger.LogWarning("Compte {Email} désactivé automatiquement pour inactivité (90 jours).", displayName);
            }
            else if (reference <= lockThreshold && (user.LockoutEnd is null || user.LockoutEnd < now))
            {
                user.LockoutEnabled = true;
                user.LockoutEnd = DateTimeOffset.MaxValue;
                await userManager.UpdateAsync(user);

                await auditService.LogAsync(
                    AuditAction.Desactivation, nameof(ApplicationUser), user.Id, displayName,
                    null, "Système (revue des accès automatique)",
                    $"Verrouillage automatique : aucune connexion depuis plus de 30 jours (dernière connexion : {FormatLastLogin(user.LastLoginAt)}). Se reconnecter ou demander à un administrateur de déverrouiller le compte.");

                _logger.LogWarning("Compte {Email} verrouillé automatiquement pour inactivité (30 jours).", displayName);
            }
        }
    }

    private static string FormatLastLogin(DateTime? lastLoginAt) =>
        lastLoginAt.HasValue ? lastLoginAt.Value.ToString("g") : "jamais connecté";
}

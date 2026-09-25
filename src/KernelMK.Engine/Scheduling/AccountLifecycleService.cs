using KernelMK.Core;
using KernelMK.Data;
using KernelMK.Data.Identity;
using KernelMK.Engine.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Scheduling;

/// <summary>
/// Verrouille les comptes après 30 jours sans connexion et les désactive après 90 jours.
/// Le service d'accès protège le dernier administrateur disponible, y compris face aux changements concurrents.
/// </summary>
public class AccountLifecycleService(IServiceScopeFactory scopeFactory, ILogger<AccountLifecycleService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(24);

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
                logger.LogError(ex, "Erreur lors du cycle de gestion des comptes utilisateurs.");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var access = scope.ServiceProvider.GetRequiredService<UserAccessService>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var now = DateTime.UtcNow;
        var threshold = now.AddDays(-30);
        var candidates = await db.Users.AsNoTracking()
            .Where(user => user.Active && (user.LastLoginAt ?? user.CreatedAt) <= threshold)
            .Select(user => new { user.Id, user.Email, user.UserName })
            .ToListAsync(ct);

        foreach (var user in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Recheck each account under the same transaction/gate as manual changes.
                var action = await access.ApplyInactivityPolicyAsync(user.Id, now, ct);
                if (action == InactivityAction.None) continue;
                var displayName = user.Email ?? user.UserName ?? user.Id;
                var details = action == InactivityAction.Deactivated
                    ? "Désactivation automatique : aucune connexion depuis plus de 90 jours. Réactivable par un administrateur."
                    : "Verrouillage automatique : aucune connexion depuis plus de 30 jours. Demander à un administrateur de déverrouiller le compte.";
                await audit.LogAsync(AuditAction.Desactivation, nameof(ApplicationUser), user.Id, displayName,
                    null, "Système (revue des accès automatique)", details);
                logger.LogWarning("Compte {UserId} : {Action} pour inactivité.", user.Id, action);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // A rejected write must not prevent the remaining accounts from being reviewed.
                logger.LogError(ex, "Impossible d'appliquer la politique d'inactivité au compte {UserId}.", user.Id);
            }
        }
    }
}

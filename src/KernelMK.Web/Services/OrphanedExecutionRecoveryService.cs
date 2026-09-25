using KernelMK.Core;
using KernelMK.Data;
using KernelMK.Engine.Workflow;
using Microsoft.EntityFrameworkCore;

namespace KernelMK.Web.Services;

/// <summary>Annule uniquement les exécutions persistées qui ne sont plus suivies par le moteur en mémoire.</summary>
public sealed class OrphanedExecutionRecoveryService(
    IDbContextFactory<AppDbContext> dbFactory,
    IJobExecutionCoordinator coordinator,
    TimeProvider timeProvider,
    ILogger<OrphanedExecutionRecoveryService> logger)
{
    public async Task<int> CancelOrphanedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.JobExecutions
            .Where(execution => execution.Status == JobStatus.EnCours)
            .Include(execution => execution.StepLogs)
            .ToListAsync(cancellationToken);

        var orphaned = candidates.Where(execution => !coordinator.IsExecutionRunning(execution.Id)).ToList();
        if (orphaned.Count == 0) return 0;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var execution in orphaned)
        {
            execution.Status = JobStatus.Annule;
            execution.FinishedAt = now;
            execution.Message = "Exécution orpheline annulée par le nettoyage d'exploitation.";
            foreach (var step in execution.StepLogs.Where(step => step.Status == StepExecutionStatus.EnCours))
            {
                step.Status = StepExecutionStatus.Annule;
                step.FinishedAt = now;
                step.ErrorOutput ??= "Étape annulée : l'exécution n'était plus suivie par le moteur.";
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("{Count} exécution(s) orpheline(s) annulée(s) par un opérateur.", orphaned.Count);
        return orphaned.Count;
    }
}

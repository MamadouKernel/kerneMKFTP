using KernelMK.Core;
using KernelMK.Data;
using KernelMK.Engine.Queue;
using KernelMK.Engine.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Scheduling;

/// <summary>Commits trigger advancement and durable job admission in one SQLite transaction.</summary>
public class JobSchedulerService(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopes,
    ILogger<JobSchedulerService> logger,
    IConfiguration configuration) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MissedRunGrace = TimeSpan.FromMinutes(15);
    private readonly string _startupId = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupEnqueued = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!startupEnqueued)
                {
                    await FireStartupTriggersAsync(stoppingToken);
                    startupEnqueued = true;
                }
                await EnqueueDueAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Erreur de planification ; les demandes validées restent dans la file."); }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task FireStartupTriggersAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var jobs = await db.JobTriggers.AsNoTracking()
            .Where(t => t.Enabled && t.Type == TriggerType.Demarrage && t.Job!.Enabled)
            .Select(t => t.JobId).Distinct().ToListAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueueService>();
        foreach (var jobId in jobs)
            await queue.EnqueueAsync(jobId, "Démarrage serveur", idempotencyKey: $"startup:{_startupId}:{jobId:N}", ct: ct);
    }

    public async Task<int> EnqueueDueAsync(DateTime now, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var triggers = await db.JobTriggers.Include(t => t.Job)
            .Where(t => t.Enabled && t.Job!.Enabled &&
                (t.Type == TriggerType.Horaire || t.Type == TriggerType.Cron || t.Type == TriggerType.Calendrier) &&
                (t.NextRunAt == null || t.NextRunAt <= now)).ToListAsync(ct);
        var delayed = new List<(Guid JobId, DateTime ExpectedAt)>();
        var count = 0;
        foreach (var trigger in triggers)
        {
            try
            {
                trigger.NextRunAt ??= TriggerCalculator.ComputeNextRunAt(trigger, now);
                if (trigger.NextRunAt is not { } expectedAt || expectedAt > now) continue;
                trigger.NextRunAt = TriggerCalculator.ComputeNextRunAt(trigger, now);
                if (now - expectedAt > MissedRunGrace && !configuration.GetValue("Scheduler:RunMissedJobs", false))
                {
                    logger.LogWarning("Déclencheur {TriggerId} ignoré après un retard de {Delay}.", trigger.Id, now - expectedAt);
                    continue;
                }
                var key = $"schedule:{trigger.Id:N}:{expectedAt.Ticks}";
                if (!await db.JobExecutionRequests.AnyAsync(r => r.IdempotencyKey == key, ct))
                {
                    db.JobExecutionRequests.Add(JobQueueService.CreateRequest(trigger.JobId,
                        $"Planification ({trigger.Type})", (int)trigger.Job!.Criticite, key));
                    count++;
                }
                trigger.LastFiredAt = now;
                if (now - expectedAt > MissedRunGrace) delayed.Add((trigger.JobId, expectedAt));
            }
            catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException)
            {
                logger.LogError(error, "Déclencheur {TriggerId} invalide ; les autres déclencheurs continuent.", trigger.Id);
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        foreach (var item in delayed) await NotifyMissedRunAsync(item.JobId, item.ExpectedAt, now - item.ExpectedAt, ct);
        return count;
    }

    private async Task NotifyMissedRunAsync(Guid jobId, DateTime expectedAt, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var job = await db.Jobs.AsNoTracking().Include(j => j.NotificationRules).FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null) return;
            var details = $"Démarrage attendu à {expectedAt.ToLocalTime():dd/MM/yyyy HH:mm} — mis en file avec {(int)delay.TotalMinutes} min de retard.";
            await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchGroupedAsync(job,
                NotificationEvent.JobManquant, null, $"{job.Id}:{NotificationEvent.JobManquant}", TimeSpan.FromMinutes(30), details);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { logger.LogError(error, "Échec de notification de retard du job {JobId}.", jobId); }
    }
}

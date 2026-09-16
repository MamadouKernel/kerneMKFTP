using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Scheduling;

/// <summary>Service hébergé : évalue périodiquement les déclencheurs horaires/calendaires et lance les jobs dus (section 4.3).</summary>
public class JobSchedulerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>Au-delà de ce retard entre l'heure attendue et l'heure réelle de déclenchement, on considère que
    /// le service a probablement été interrompu (redémarrage serveur, arrêt du service Windows...) et on alerte —
    /// un tick de planificateur toutes les 15s ne peut normalement jamais produire un tel écart.</summary>
    private static readonly TimeSpan MissedRunGrace = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan NotificationGroupingWindow = TimeSpan.FromMinutes(30);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobSchedulerService> _logger;

    public JobSchedulerService(IDbContextFactory<AppDbContext> dbFactory, IServiceScopeFactory scopeFactory, ILogger<JobSchedulerService> logger)
    {
        _dbFactory = dbFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private Task RunJobAsync(Guid jobId, string triggeredBy) => Task.Run(async () =>
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRunner = scope.ServiceProvider.GetRequiredService<IJobRunner>();
        await jobRunner.RunAsync(jobId, triggeredBy, CancellationToken.None);
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FireStartupTriggersAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Arrêt normal du service hébergé (arrêt de l'application) : rien à signaler.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du tick du planificateur.");
            }
        }
    }

    private async Task FireStartupTriggersAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var startupTriggers = await db.JobTriggers
            .Include(t => t.Job)
            .Where(t => t.Enabled && t.Type == TriggerType.Demarrage && t.Job!.Enabled)
            .ToListAsync(ct);

        foreach (var trigger in startupTriggers)
        {
            _ = RunJobAsync(trigger.JobId, "Démarrage serveur");
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var candidateTriggers = await db.JobTriggers
            .Include(t => t.Job!).ThenInclude(j => j.NotificationRules)
            .Where(t => t.Enabled &&
                        (t.Type == TriggerType.Horaire || t.Type == TriggerType.Cron || t.Type == TriggerType.Calendrier) &&
                        t.Job!.Enabled)
            .ToListAsync(ct);

        foreach (var trigger in candidateTriggers)
        {
            trigger.NextRunAt ??= TriggerCalculator.ComputeNextRunAt(trigger, now);

            if (trigger.NextRunAt is not null && trigger.NextRunAt <= now)
            {
                var expectedAt = trigger.NextRunAt.Value;
                var delay = now - expectedAt;

                trigger.LastFiredAt = now;
                trigger.NextRunAt = TriggerCalculator.ComputeNextRunAt(trigger, now);

                _ = RunJobAsync(trigger.JobId, $"Planification ({trigger.Type})");

                if (delay > MissedRunGrace)
                {
                    _ = NotifyMissedRunAsync(trigger.Job!, expectedAt, delay);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Alerte "Job manquant" : un déclencheur planifié a fini par se lancer, mais bien plus tard que prévu —
    /// le signe le plus probable est que le service kernelMK était arrêté pendant la fenêtre attendue.
    /// </summary>
    private Task NotifyMissedRunAsync(Job job, DateTime expectedAt, TimeSpan delay) => Task.Run(async () =>
    {
        using var scope = _scopeFactory.CreateScope();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();

        var details = $"Démarrage attendu à {expectedAt.ToLocalTime():dd/MM/yyyy HH:mm} — lancé avec {FormatDelay(delay)} " +
            "de retard (le service a peut-être été interrompu entre-temps : redémarrage serveur, arrêt du service...).";

        await notifications.DispatchGroupedAsync(
            job, NotificationEvent.JobManquant, execution: null,
            $"{job.Id}:{NotificationEvent.JobManquant}", NotificationGroupingWindow, details);
    });

    private static string FormatDelay(TimeSpan delay) =>
        delay.TotalHours >= 1 ? $"{(int)delay.TotalHours}h{delay.Minutes:00}" : $"{(int)delay.TotalMinutes} min";
}

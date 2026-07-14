using KernelMK.Core;
using KernelMK.Data;
using KernelMK.Engine.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Scheduling;

/// <summary>Service hébergé : évalue périodiquement les déclencheurs horaires/calendaires et lance les jobs dus (section 4.3).</summary>
public class JobSchedulerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du tick du planificateur.");
            }

            await Task.Delay(PollInterval, stoppingToken);
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
            .Include(t => t.Job)
            .Where(t => t.Enabled &&
                        (t.Type == TriggerType.Horaire || t.Type == TriggerType.Cron || t.Type == TriggerType.Calendrier) &&
                        t.Job!.Enabled)
            .ToListAsync(ct);

        foreach (var trigger in candidateTriggers)
        {
            trigger.NextRunAt ??= TriggerCalculator.ComputeNextRunAt(trigger, now);

            if (trigger.NextRunAt is not null && trigger.NextRunAt <= now)
            {
                trigger.LastFiredAt = now;
                trigger.NextRunAt = TriggerCalculator.ComputeNextRunAt(trigger, now);

                _ = RunJobAsync(trigger.JobId, $"Planification ({trigger.Type})");
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

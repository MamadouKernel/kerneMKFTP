using KernelMK.Core;
using KernelMK.Data;
using KernelMK.Engine.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Queue;

/// <summary>Bounded dispatcher for one active server. SQLite holds admission; the in-memory gate holds live capacity.</summary>
public sealed class JobQueueDispatcher(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopes,
    IJobExecutionCoordinator coordinator,
    IConfiguration configuration,
    ILogger<JobQueueDispatcher> logger) : BackgroundService
{
    private sealed record Dispatch(Guid JobId, Task Task);
    private readonly Dictionary<Guid, Dispatch> _running = new();
    private readonly int _capacity = Math.Max(1, configuration.GetValue("Engine:MaxParallelJobs", 20));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var id in _running.Where(pair => pair.Value.Task.IsCompleted).Select(pair => pair.Key).ToArray())
                    _running.Remove(id);
                if (_running.Count < _capacity)
                {
                    await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                    // Include dispatched jobs before their runner registers with the coordinator.
                    var dispatchedJobs = coordinator.GetRunningJobIds()
                        .Concat(_running.Values.Select(dispatch => dispatch.JobId)).ToHashSet();
                    while (_running.Count < _capacity)
                    {
                        var activeJobs = dispatchedJobs.ToArray();
                        var activeRequests = _running.Keys.ToArray();
                        var pageSize = Math.Min(256, _capacity - _running.Count);
                        var candidates = await db.JobExecutionRequests.AsNoTracking()
                            .Where(request => request.Status == JobRequestStatus.Pending && !activeRequests.Contains(request.Id) &&
                                (request.Job!.AllowConcurrentExecution || !activeJobs.Contains(request.JobId)))
                            .OrderByDescending(request => request.Priority).ThenBy(request => request.RequestedAt).ThenBy(request => request.Id)
                            .Select(request => new { request.Id, request.JobId, request.Job!.AllowConcurrentExecution })
                            .Take(pageSize).ToListAsync(stoppingToken);
                        if (candidates.Count == 0) break;
                        foreach (var request in candidates)
                        {
                            if (_running.Count >= _capacity) break;
                            if (!request.AllowConcurrentExecution && !dispatchedJobs.Add(request.JobId)) continue;
                            _running.Add(request.Id, new Dispatch(request.JobId,
                                Task.Run(() => DispatchAsync(request.Id, stoppingToken), CancellationToken.None)));
                        }
                        // Re-query with newly selected jobs excluded. A page full of one job must
                        // not hide independent work farther down the queue or consume all slots.
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Impossible de lire la file persistante ; nouvel essai au prochain passage."); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task DispatchAsync(Guid requestId, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IQueuedJobRunner>().TryRunRequestAsync(requestId, stoppingToken);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Échec du moteur pour la demande {RequestId}.", requestId);
            try { await scope.ServiceProvider.GetRequiredService<JobQueueService>().MarkUncertainAsync(requestId); }
            catch (Exception persistenceError)
            {
                logger.LogError(persistenceError, "La demande {RequestId} sera vérifiée à la récupération au redémarrage.", requestId);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (ExecuteTask?.IsCompleted == true)
        {
            try { await Task.WhenAll(_running.Values.Select(dispatch => dispatch.Task)).WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
}

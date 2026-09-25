using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Workflow;

/// <summary>Suivi par exécution : plusieurs exécutions d'un job restent indépendamment annulables.</summary>
public class JobExecutionCoordinator : IJobExecutionCoordinator
{
    private sealed record ActiveExecution(Guid JobId, CancellationTokenSource Source);
    private readonly ConcurrentDictionary<Guid, ActiveExecution> _executions = new();
    private readonly ILogger<JobExecutionCoordinator> _logger;

    public JobExecutionCoordinator(ILogger<JobExecutionCoordinator> logger) => _logger = logger;

    public CancellationTokenSource RegisterExecution(Guid jobId, Guid executionId, CancellationToken parentToken = default)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        if (!_executions.TryAdd(executionId, new ActiveExecution(jobId, source)))
        {
            source.Dispose();
            throw new InvalidOperationException($"Exécution déjà enregistrée : {executionId}");
        }
        return source;
    }

    public void UnregisterExecution(Guid jobId, Guid executionId)
    {
        if (_executions.TryGetValue(executionId, out var active) && active.JobId == jobId
            && _executions.TryRemove(executionId, out active))
        {
            active.Source.Dispose();
        }
    }

    public bool IsJobRunning(Guid jobId) => _executions.Values.Any(x => x.JobId == jobId);
    public bool IsExecutionRunning(Guid executionId) => _executions.ContainsKey(executionId);

    public bool TryStopJob(Guid jobId)
    {
        var stopped = false;
        foreach (var active in _executions.Values.Where(x => x.JobId == jobId))
            stopped |= TryCancel(active.Source, jobId);
        return stopped;
    }

    public bool TryStopExecution(Guid executionId) =>
        _executions.TryGetValue(executionId, out var active) && TryCancel(active.Source, executionId);

    private bool TryCancel(CancellationTokenSource source, Guid id)
    {
        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // L'exécution s'est terminée entre la lecture et la demande d'arrêt.
            return false;
        }
        catch (AggregateException ex)
        {
            _logger.LogError(ex, "Un callback d'annulation a échoué pour {Id}. Le signal d'arrêt a été envoyé.", id);
            return true;
        }
    }

    public IReadOnlyCollection<Guid> GetRunningJobIds() =>
        _executions.Values.Select(x => x.JobId).Distinct().ToArray();
}

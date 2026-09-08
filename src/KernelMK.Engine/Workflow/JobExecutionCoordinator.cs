using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Workflow;

/// <summary>
/// Implémentation Singleton thread-safe du suivi des exécutions actives avec support d'annulation immédiate.
/// </summary>
public class JobExecutionCoordinator : IJobExecutionCoordinator
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningByJob = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningByExecution = new();
    private readonly ILogger<JobExecutionCoordinator> _logger;

    public JobExecutionCoordinator(ILogger<JobExecutionCoordinator> logger)
    {
        _logger = logger;
    }

    public CancellationTokenSource RegisterExecution(Guid jobId, Guid executionId, CancellationToken parentToken = default)
    {
        var cts = parentToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(parentToken)
            : new CancellationTokenSource();

        _runningByJob[jobId] = cts;
        _runningByExecution[executionId] = cts;

        _logger.LogInformation("Exécution {ExecutionId} du job {JobId} enregistrée auprès du coordinateur.", executionId, jobId);
        return cts;
    }

    public void UnregisterExecution(Guid jobId, Guid executionId)
    {
        _runningByJob.TryRemove(jobId, out _);
        if (_runningByExecution.TryRemove(executionId, out var cts))
        {
            try
            {
                cts.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Erreur lors de la libération du CancellationTokenSource pour l'exécution {ExecutionId}", executionId);
            }
        }
        _logger.LogInformation("Exécution {ExecutionId} du job {JobId} retirée du coordinateur.", executionId, jobId);
    }

    public bool IsJobRunning(Guid jobId) => _runningByJob.ContainsKey(jobId);

    public bool IsExecutionRunning(Guid executionId) => _runningByExecution.ContainsKey(executionId);

    public bool TryStopJob(Guid jobId)
    {
        if (_runningByJob.TryGetValue(jobId, out var cts))
        {
            try
            {
                _logger.LogWarning("Signal d'arrêt envoyé pour le job {JobId}", jobId);
                cts.Cancel();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Échec lors de l'arrêt du job {JobId}", jobId);
            }
        }
        return false;
    }

    public bool TryStopExecution(Guid executionId)
    {
        if (_runningByExecution.TryGetValue(executionId, out var cts))
        {
            try
            {
                _logger.LogWarning("Signal d'arrêt envoyé pour l'exécution {ExecutionId}", executionId);
                cts.Cancel();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Échec lors de l'arrêt de l'exécution {ExecutionId}", executionId);
            }
        }
        return false;
    }

    public IReadOnlyCollection<Guid> GetRunningJobIds() => _runningByJob.Keys.ToList();
}

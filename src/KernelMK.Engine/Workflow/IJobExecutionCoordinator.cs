namespace KernelMK.Engine.Workflow;

/// <summary>
/// Coordonne le suivi en mémoire des exécutions de jobs en cours et permet leur arrêt / annulation contrôlée.
/// </summary>
public interface IJobExecutionCoordinator
{
    /// <summary>
    /// Enregistre une exécution active et renvoie un CancellationTokenSource lié pouvant être annulé à la demande.
    /// </summary>
    CancellationTokenSource RegisterExecution(Guid jobId, Guid executionId, CancellationToken parentToken = default);

    /// <summary>
    /// Désenregistre une exécution une fois terminée.
    /// </summary>
    void UnregisterExecution(Guid jobId, Guid executionId);

    /// <summary>
    /// Indique si un job a une exécution actuellement en cours d'exécution.
    /// </summary>
    bool IsJobRunning(Guid jobId);

    /// <summary>
    /// Indique si une exécution spécifique est en cours.
    /// </summary>
    bool IsExecutionRunning(Guid executionId);

    /// <summary>
    /// Demande l'arrêt immédiat de l'exécution en cours d'un job.
    /// </summary>
    bool TryStopJob(Guid jobId);

    /// <summary>
    /// Demande l'arrêt d'une exécution spécifique par son identifiant d'exécution.
    /// </summary>
    bool TryStopExecution(Guid executionId);

    /// <summary>
    /// Liste des identifiants des jobs actuellement en cours d'exécution.
    /// </summary>
    IReadOnlyCollection<Guid> GetRunningJobIds();
}

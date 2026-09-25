using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Data.Security;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Notifications;
using KernelMK.Engine.Queue;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace KernelMK.Engine.Workflow;

/// <summary>Orchestre l'exécution d'un job : chaînage des étapes, reprises, timeouts, branches succès/échec (section 4.4/4.5).</summary>
public class JobRunner : IJobRunner, IQueuedJobRunner
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly StepExecutorFactory _executorFactory;
    private readonly ConcurrencyGate _concurrencyGate;
    private readonly IJobExecutionCoordinator _coordinator;
    private readonly NotificationDispatcher _notifications;
    private readonly CredentialProtector _credentialProtector;
    private readonly ILogger<JobRunner> _logger;
    private readonly IServiceScopeFactory _scopeFactory;


    /// <summary>Fenêtre de regroupement des notifications répétées (échec/timeout/anomalie) pour un même job :
    /// une seule alerte externe part par tranche de 30 minutes, même en cas d'échecs à répétition.</summary>
    private static readonly TimeSpan NotificationGroupingWindow = TimeSpan.FromMinutes(30);

    private const int MinHistoryForDurationAnomaly = 5;
    private const double DurationAnomalyFactor = 2.0;
    private static readonly TimeSpan DurationAnomalyFloor = TimeSpan.FromSeconds(30);

    public JobRunner(
        IDbContextFactory<AppDbContext> dbFactory,
        StepExecutorFactory executorFactory,
        ConcurrencyGate concurrencyGate,
        IJobExecutionCoordinator coordinator,
        NotificationDispatcher notifications,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<JobRunner> logger,
        IServiceScopeFactory scopeFactory)
    {
        _dbFactory = dbFactory;
        _executorFactory = executorFactory;
        _concurrencyGate = concurrencyGate;
        _coordinator = coordinator;
        _notifications = notifications;
        _credentialProtector = new CredentialProtector(dataProtectionProvider);
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task<JobExecution> RunAsync(Guid jobId, string triggeredBy, CancellationToken cancellationToken = default)
    {
        using var lineage = JobExecutionLineage.Enter(jobId);
        return (await RunCoreAsync(jobId, triggeredBy, null, cancellationToken))!;
    }

    public async Task<JobExecution?> TryRunRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.JobExecutionRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);
        if (request is null || request.Status != JobRequestStatus.Pending) return null;
        IDisposable lineage;
        try
        {
            lineage = JobExecutionLineage.Enter(request.JobId, JobExecutionLineage.Parse(request.AncestorJobIdsJson));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            _logger.LogError(error, "Ascendance invalide pour la demande {RequestId}.", requestId);
            await db.JobExecutionRequests.Where(r => r.Id == requestId && r.Status == JobRequestStatus.Pending)
                .ExecuteUpdateAsync(update => update.SetProperty(r => r.Status, JobRequestStatus.NeedsReview)
                    .SetProperty(r => r.FinishedAt, DateTime.UtcNow)
                    .SetProperty(r => r.Message, "Chaîne de jobs invalide ou cyclique. Corrigez la configuration avant toute relance."), cancellationToken);
            return null;
        }
        using (lineage)
            return await RunCoreAsync(request.JobId, request.TriggeredBy, requestId, cancellationToken);
    }

    private async Task<JobExecution?> RunCoreAsync(Guid jobId, string triggeredBy, Guid? requestId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs
            .Include(j => j.Steps)
            .Include(j => j.NotificationRules)
            .AsSplitQuery()
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job introuvable : {jobId}");

        var definitionHash = JobDefinitionFingerprint.Compute(job);
        var resumedStepIds = new HashSet<Guid>();
        JobExecutionRequest? request = null;
        if (requestId.HasValue)
        {
            request = await db.JobExecutionRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);
            if (request is null || request.Status != JobRequestStatus.Pending) return null;
            try
            {
                resumedStepIds = (JsonSerializer.Deserialize<Guid[]>(request.ResumeCompletedStepIdsJson) ?? []).ToHashSet();
                if (resumedStepIds.Count > 0 &&
                    !string.Equals(request.ExpectedJobDefinitionHash, definitionHash, StringComparison.Ordinal))
                {
                    await db.JobExecutionRequests.Where(r => r.Id == requestId && r.Status == JobRequestStatus.Pending)
                        .ExecuteUpdateAsync(update => update.SetProperty(r => r.Status, JobRequestStatus.NeedsReview)
                            .SetProperty(r => r.FinishedAt, DateTime.UtcNow)
                            .SetProperty(r => r.Message, "La définition du job a changé. Reprise à l'étape refusée."), cancellationToken);
                    return null;
                }
                if (resumedStepIds.Except(job.Steps.Select(step => step.Id)).Any())
                    throw new InvalidOperationException("Le point de reprise référence une étape inexistante.");
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                _logger.LogError(error, "Point de reprise invalide pour la demande {RequestId}.", requestId);
                await db.JobExecutionRequests.Where(r => r.Id == requestId && r.Status == JobRequestStatus.Pending)
                    .ExecuteUpdateAsync(update => update.SetProperty(r => r.Status, JobRequestStatus.NeedsReview)
                        .SetProperty(r => r.FinishedAt, DateTime.UtcNow)
                        .SetProperty(r => r.Message, "Point de reprise invalide. Vérifiez la demande avant toute relance."), cancellationToken);
                return null;
            }
            if (!job.Enabled)
            {
                await db.JobExecutionRequests.Where(r => r.Id == requestId && r.Status == JobRequestStatus.Pending)
                    .ExecuteUpdateAsync(update => update.SetProperty(r => r.Status, JobRequestStatus.Canceled)
                        .SetProperty(r => r.FinishedAt, DateTime.UtcNow)
                        .SetProperty(r => r.Message, "Job désactivé avant son démarrage."), cancellationToken);
                return null;
            }
        }

        if (!_concurrencyGate.TryEnter(jobId, job.AllowConcurrentExecution))
        {
            if (request is not null) return null; // Capacity exhaustion leaves durable work pending.
            var blocked = new JobExecution
            {
                JobId = jobId, TriggeredBy = triggeredBy, Status = JobStatus.Annule,
                FinishedAt = DateTime.UtcNow,
                Message = "Exécution refusée : job déjà actif ou limite globale d'exécutions atteinte."
            };
            db.JobExecutions.Add(blocked);
            await db.SaveChangesAsync(cancellationToken);
            return blocked;
        }

        var execution = new JobExecution
        {
            JobId = jobId, TriggeredBy = triggeredBy, Status = JobStatus.EnCours,
            ServerName = Environment.MachineName, ExecutionAccount = job.ExecutionAccount,
            JobDefinitionHash = definitionHash
        };
        CancellationTokenSource? runCts = null;
        var persisted = false;
        var finalized = false;
        try
        {
            // Le finally couvre aussi un échec de création en base ou d'enregistrement au coordinateur.
            await using (var admission = request is null ? null : await db.Database.BeginTransactionAsync(cancellationToken))
            {
                if (request is not null)
                {
                    var claimed = await db.JobExecutionRequests.Where(r => r.Id == request.Id && r.Status == JobRequestStatus.Pending)
                        .ExecuteUpdateAsync(update => update.SetProperty(r => r.Status, JobRequestStatus.Running), cancellationToken);
                    if (claimed != 1) return null;
                    request.Status = JobRequestStatus.Running;
                    request.StartedAt = execution.StartedAt;
                    request.ExecutionId = execution.Id;
                    request.ExpectedJobDefinitionHash = definitionHash;
                    request.Message = null;
                }
                db.JobExecutions.Add(execution);
                job.LastRunAt = execution.StartedAt;
                job.LastStatus = JobStatus.EnCours;
                await db.SaveChangesAsync(cancellationToken);
                if (admission is not null) await admission.CommitAsync(cancellationToken);
                persisted = true;
            }
            runCts = _coordinator.RegisterExecution(jobId, execution.Id, cancellationToken);

            using var jobTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(runCts.Token);
            jobTimeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, job.TimeoutSeconds)));
            var maxAttempts = Math.Max(1, job.MaxRetries + 1);
            var overallSuccess = false;
            var timedOut = false;
            var canceled = false;
            try
            {
                for (var attempt = 1; attempt <= maxAttempts && !overallSuccess; attempt++)
                {
                    jobTimeoutCts.Token.ThrowIfCancellationRequested();
                    execution.AttemptNumber = attempt;
                    overallSuccess = await RunStepsAsync(job, execution, db, resumedStepIds, jobTimeoutCts.Token);
                    if (!overallSuccess && attempt < maxAttempts)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, job.RetryDelaySeconds)), jobTimeoutCts.Token);
                }
                jobTimeoutCts.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (jobTimeoutCts.IsCancellationRequested)
            {
                canceled = runCts.IsCancellationRequested;
                timedOut = !canceled;
            }

            execution.FinishedAt = DateTime.UtcNow;
            execution.Status = canceled ? JobStatus.Annule : overallSuccess && !timedOut ? JobStatus.Succes : JobStatus.Echec;
            execution.ReturnCode = canceled ? -1 : execution.Status == JobStatus.Succes ? 0 : 1;
            execution.Message = canceled ? "Exécution annulée (opérateur ou arrêt du service)."
                : timedOut ? $"Timeout dépassé ({job.TimeoutSeconds}s)."
                : overallSuccess ? "Exécution terminée avec succès." : "Exécution terminée en échec après reprises.";
            job.LastStatus = execution.Status;
            CompleteRequest(request, execution, cancellationToken.IsCancellationRequested);
            if (!canceled) await StageDependentJobsAsync(jobId, execution, db, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
            finalized = true;

            if (!canceled)
            {
                // Une panne de notification ne doit pas réécrire le résultat métier du job.
                try
                {
                    if (timedOut)
                        await _notifications.DispatchGroupedAsync(job, NotificationEvent.Timeout, execution, $"{jobId}:{NotificationEvent.Timeout}", NotificationGroupingWindow);
                    else if (overallSuccess)
                    {
                        await _notifications.DispatchAsync(job, NotificationEvent.Succes, execution);
                        await CheckDurationAnomalyAsync(job, execution, db, cancellationToken);
                    }
                    else
                        await _notifications.DispatchGroupedAsync(job, NotificationEvent.Echec, execution, $"{jobId}:{NotificationEvent.Echec}", NotificationGroupingWindow);
                }
                catch (Exception ex) { _logger.LogError(ex, "Échec de notification du job {JobId}, résultat conservé.", jobId); }
            }
            return execution;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur moteur pour le job {JobId}.", jobId);
            if (!persisted || finalized) throw;
            // Une finalisation échouée ne doit pas publier les dépendances du résultat abandonné.
            foreach (var pendingChild in db.ChangeTracker.Entries<JobExecutionRequest>()
                         .Where(entry => entry.State == EntityState.Added).ToArray())
                pendingChild.State = EntityState.Detached;
            execution.FinishedAt = DateTime.UtcNow;
            execution.Status = ex is OperationCanceledException ? JobStatus.Annule : JobStatus.Echec;
            execution.Message = ex is OperationCanceledException ? "Exécution annulée." : $"Erreur moteur : {ex.Message}";
            execution.ReturnCode = ex is OperationCanceledException ? -1 : 1;
            job.LastStatus = execution.Status;
            CompleteRequest(request, execution, cancellationToken.IsCancellationRequested);
            if (request is not null && ex is not OperationCanceledException)
            {
                request.Status = JobRequestStatus.NeedsReview;
                request.Message = "Erreur du moteur ou de persistance. Vérifiez les effets externes avant une relance depuis le début.";
            }
            // Finalise aussi les étapes ouvertes si la panne s'est produite hors de l'exécuteur.
            foreach (var log in execution.StepLogs.Where(x => x.Status == StepExecutionStatus.EnCours))
            {
                log.FinishedAt = execution.FinishedAt;
                log.Status = ex is OperationCanceledException ? StepExecutionStatus.Annule : StepExecutionStatus.Echec;
                log.ErrorOutput = execution.Message;
            }
            await db.SaveChangesAsync(CancellationToken.None);
            return execution;
        }
        finally
        {
            try
            {
                if (runCts is not null) _coordinator.UnregisterExecution(jobId, execution.Id);
            }
            finally { _concurrencyGate.Exit(jobId); }
        }
    }

    private async Task<bool> RunStepsAsync(Job job, JobExecution execution, AppDbContext db, IReadOnlySet<Guid> resumedStepIds, CancellationToken jobToken)
    {
        jobToken.ThrowIfCancellationRequested();
        var steps = job.Steps.OrderBy(s => s.Order).ToList();
        if (steps.Count == 0) return true;

        var stepsByOrder = steps.ToDictionary(s => s.Order);
        var nextOrders = steps.Select((step, index) => (step.Order, Next: index + 1 < steps.Count ? (int?)steps[index + 1].Order : null))
            .ToDictionary(x => x.Order, x => x.Next);
        var currentOrder = steps.First().Order;
        var visited = new HashSet<int>();
        var encounteredFailure = false;

        while (true)
        {
            jobToken.ThrowIfCancellationRequested();
            if (!stepsByOrder.TryGetValue(currentOrder, out var step))
            {
                _logger.LogError("Branche vers une étape inexistante ({Order}) du job {JobId}.", currentOrder, job.Id);
                return false;
            }
            if (!visited.Add(currentOrder))
            {
                _logger.LogWarning("Boucle détectée dans le workflow du job {JobName} à l'étape {Order}, arrêt.", job.Name, currentOrder);
                return false;
            }

            bool stepSucceeded;
            if (resumedStepIds.Contains(step.Id))
            {
                var skipped = new StepExecutionLog
                {
                    JobExecutionId = execution.Id,
                    JobStepId = step.Id,
                    StepName = step.Name,
                    Order = step.Order,
                    Status = StepExecutionStatus.Ignore,
                    FinishedAt = DateTime.UtcNow,
                    Output = "Étape non rejouée : succès confirmé dans l'exécution reprise."
                };
                db.StepExecutionLogs.Add(skipped);
                await db.SaveChangesAsync(jobToken);
                stepSucceeded = true;
            }
            else
            {
                stepSucceeded = await RunStepWithRetriesAsync(job, step, execution, db, jobToken);
            }

            if (stepSucceeded)
            {
                if (step.OnSuccessGoToOrder is { } successJump)
                {
                    currentOrder = successJump;
                    continue;
                }
            }
            else
            {
                encounteredFailure = true;
                switch (step.OnErrorAction)
                {
                    case OnErrorAction.Arreter:
                        return false;
                    case OnErrorAction.BrancheAlternative when step.OnFailureGoToOrder is { } failureJump:
                        currentOrder = failureJump;
                        continue;
                    case OnErrorAction.Poursuivre:
                    case OnErrorAction.BrancheAlternative:
                        break;
                }
            }

            var nextOrder = nextOrders[currentOrder];
            if (nextOrder is null) return !encounteredFailure;
            currentOrder = nextOrder.Value;
        }
    }

    private async Task<bool> RunStepWithRetriesAsync(Job job, JobStep step, JobExecution execution, AppDbContext db, CancellationToken jobToken)
    {
        var executor = _executorFactory.Resolve(step.Type);
        var maxAttempts = Math.Max(1, step.MaxRetries + 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var log = new StepExecutionLog
            {
                JobExecutionId = execution.Id,
                JobStepId = step.Id,
                StepName = step.Name,
                Order = step.Order,
                Status = StepExecutionStatus.EnCours,
                AttemptNumber = attempt
            };
            db.StepExecutionLogs.Add(log);
            await db.SaveChangesAsync(jobToken);

            using var stepTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(jobToken);
            stepTimeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, step.TimeoutSeconds)));

            StepExecutionResult result;
            var timedOut = false;
            var stepCanceled = false;
            try
            {
                var context = new StepExecutionContext
                {
                    Job = job,
                    Step = step,
                    Execution = execution,
                    CancellationToken = stepTimeoutCts.Token,
                    ResolvedCredential = await ResolveCredentialAsync(step, db, jobToken)
                };
                result = await executor.ExecuteAsync(context);
                // Certains clients renvoient un résultat au lieu de propager l'annulation.
                // Le délai du moteur reste décisif pour le résultat et le journal de l'étape.
                stepTimeoutCts.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (stepTimeoutCts.IsCancellationRequested && !jobToken.IsCancellationRequested)
            {
                timedOut = true;
                result = StepExecutionResult.Fail($"Timeout de l'étape dépassé ({step.TimeoutSeconds}s).");
            }
            catch (OperationCanceledException) when (jobToken.IsCancellationRequested)
            {
                stepCanceled = true;
                result = StepExecutionResult.Fail("Étape annulée par l'arrêt du job.");
            }
            catch (Exception ex)
            {
                result = StepExecutionResult.Fail(ex.ToString());
            }

            log.FinishedAt = DateTime.UtcNow;
            log.Status = stepCanceled ? StepExecutionStatus.Annule : timedOut ? StepExecutionStatus.Timeout : (result.Success ? StepExecutionStatus.Succes : StepExecutionStatus.Echec);
            log.ReturnCode = result.ReturnCode;
            log.Output = result.Output;
            log.ErrorOutput = result.ErrorOutput;
            log.FilesProcessedCsv = result.FilesProcessedCsv;
            // Le token du job peut être annulé : conserver malgré tout le résultat terminal de l'étape.
            await db.SaveChangesAsync(CancellationToken.None);
            jobToken.ThrowIfCancellationRequested();

            if (result.Success) return true;

            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, step.RetryDelaySeconds)), jobToken);
            }
        }

        return false;
    }

    /// <summary>
    /// Compare la durée de l'exécution qui vient de réussir à la moyenne des N dernières exécutions réussies du
    /// même job. Si elle est anormalement plus longue (au moins <see cref="DurationAnomalyFactor"/> fois la
    /// moyenne, et au-delà d'un plancher pour ignorer le bruit sur les jobs très courts), déclenche une alerte
    /// "Anomalie de durée" — signe possible de ralentissement réseau ou de lenteur côté serveur distant.
    /// </summary>
    private async Task CheckDurationAnomalyAsync(Job job, JobExecution execution, AppDbContext db, CancellationToken ct)
    {
        if (execution.FinishedAt is null) return;

        var currentDuration = execution.FinishedAt.Value - execution.StartedAt;
        if (currentDuration < DurationAnomalyFloor) return;

        var history = await db.JobExecutions
            .Where(e => e.JobId == job.Id && e.Status == JobStatus.Succes && e.Id != execution.Id && e.FinishedAt != null)
            .OrderByDescending(e => e.StartedAt)
            .Take(20)
            .Select(e => new { e.StartedAt, e.FinishedAt })
            .ToListAsync(ct);

        if (history.Count < MinHistoryForDurationAnomaly) return;

        var avgSeconds = history.Average(e => (e.FinishedAt!.Value - e.StartedAt).TotalSeconds);
        if (avgSeconds <= 0 || currentDuration.TotalSeconds < avgSeconds * DurationAnomalyFactor) return;

        var details = $"Durée inhabituelle : {currentDuration:hh\\:mm\\:ss} contre une moyenne de " +
            $"{TimeSpan.FromSeconds(avgSeconds):hh\\:mm\\:ss} sur les {history.Count} dernières exécutions réussies — " +
            "possible ralentissement réseau ou lenteur côté serveur distant.";

        await _notifications.DispatchGroupedAsync(
            job, NotificationEvent.AnomalieDuree, execution,
            $"{job.Id}:{NotificationEvent.AnomalieDuree}", NotificationGroupingWindow, details);
    }

    private async Task StageDependentJobsAsync(Guid completedJobId, JobExecution execution, AppDbContext db, CancellationToken ct)
    {
        if (JobExecutionLineage.Current.Count >= 100)
        {
            _logger.LogError("Limite de 100 niveaux atteinte pour {JobId} : dépendances non admises.", completedJobId);
            return;
        }
        var finalStatus = execution.Status;
        var condition = finalStatus == JobStatus.Succes ? JobDependencyCondition.Succes : JobDependencyCondition.Echec;

        var dependents = await db.JobDependencies
            .Where(d => d.DependsOnJobId == completedJobId &&
                        (d.Condition == condition || d.Condition == JobDependencyCondition.Fin))
            .Include(d => d.Job)
            .Where(d => d.Job!.Enabled)
            .ToListAsync(ct);

        foreach (var dependency in dependents.DistinctBy(x => x.JobId))
        {
            if (JobExecutionLineage.Current.Contains(dependency.JobId))
            {
                _logger.LogError("Dépendance cyclique refusée entre {JobId} et {DependentId}.", completedJobId, dependency.JobId);
                continue;
            }
            var key = $"dependency:{execution.Id:N}:{dependency.JobId:N}";
            if (!await db.JobExecutionRequests.AnyAsync(r => r.IdempotencyKey == key, ct))
                db.JobExecutionRequests.Add(JobQueueService.CreateRequest(dependency.JobId,
                    $"Dépendance ({finalStatus})", (int)dependency.Job!.Criticite, key));
        }
    }

    private static void CompleteRequest(JobExecutionRequest? request, JobExecution execution, bool serviceStopping)
    {
        if (request is null) return;
        request.FinishedAt = execution.FinishedAt;
        request.Status = serviceStopping ? JobRequestStatus.NeedsReview : execution.Status switch
        {
            JobStatus.Succes => JobRequestStatus.Succeeded,
            JobStatus.Annule => JobRequestStatus.Canceled,
            _ => JobRequestStatus.Failed
        };
        request.Message = serviceStopping
            ? "Arrêt pendant le traitement. Vérifiez les effets externes avant une relance depuis le début."
            : execution.Message;
    }
    private async Task<(string? Username, string? Secret, string? Host, int? Port, CredentialAuthType AuthType, string? Passphrase, string? OAuth2ClientId, string? OAuth2TenantId)?> ResolveCredentialAsync(JobStep step, AppDbContext db, CancellationToken ct)
    {
        if (step.CredentialId is null) return null;

        var credential = await db.Credentials.FirstOrDefaultAsync(c => c.Id == step.CredentialId, ct);
        if (credential is null) return null;

        var secret = _credentialProtector.Unprotect(credential.EncryptedSecret);
        var passphrase = string.IsNullOrEmpty(credential.EncryptedPassphrase) ? null : _credentialProtector.Unprotect(credential.EncryptedPassphrase);
        return (credential.Username, secret, credential.Host, credential.Port, credential.AuthType, passphrase, credential.OAuth2ClientId, credential.OAuth2TenantId);
    }
}

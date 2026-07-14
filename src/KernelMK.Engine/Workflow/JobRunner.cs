using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Data.Security;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Workflow;

/// <summary>Orchestre l'exécution d'un job : chaînage des étapes, reprises, timeouts, branches succès/échec (section 4.4/4.5).</summary>
public class JobRunner : IJobRunner
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly StepExecutorFactory _executorFactory;
    private readonly ConcurrencyGate _concurrencyGate;
    private readonly NotificationDispatcher _notifications;
    private readonly CredentialProtector _credentialProtector;
    private readonly ILogger<JobRunner> _logger;

    public JobRunner(
        IDbContextFactory<AppDbContext> dbFactory,
        StepExecutorFactory executorFactory,
        ConcurrencyGate concurrencyGate,
        NotificationDispatcher notifications,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<JobRunner> logger)
    {
        _dbFactory = dbFactory;
        _executorFactory = executorFactory;
        _concurrencyGate = concurrencyGate;
        _notifications = notifications;
        _credentialProtector = new CredentialProtector(dataProtectionProvider);
        _logger = logger;
    }

    public async Task<JobExecution> RunAsync(Guid jobId, string triggeredBy, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs
            .Include(j => j.Steps)
            .Include(j => j.NotificationRules)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job introuvable : {jobId}");

        if (!_concurrencyGate.TryEnter(jobId, job.AllowConcurrentExecution))
        {
            var blocked = new JobExecution
            {
                JobId = jobId,
                TriggeredBy = triggeredBy,
                Status = JobStatus.Annule,
                FinishedAt = DateTime.UtcNow,
                Message = "Exécution bloquée : une exécution de ce job est déjà en cours (concurrence non autorisée)."
            };
            db.JobExecutions.Add(blocked);
            await db.SaveChangesAsync(cancellationToken);
            return blocked;
        }

        var execution = new JobExecution
        {
            JobId = jobId,
            TriggeredBy = triggeredBy,
            Status = JobStatus.EnCours,
            ServerName = Environment.MachineName,
            ExecutionAccount = job.ExecutionAccount
        };
        db.JobExecutions.Add(execution);
        job.LastRunAt = execution.StartedAt;
        job.LastStatus = JobStatus.EnCours;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            using var jobTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            jobTimeoutCts.CancelAfter(TimeSpan.FromSeconds(job.TimeoutSeconds));

            var maxAttempts = Math.Max(1, job.MaxRetries + 1);
            bool overallSuccess = false;
            bool timedOut = false;

            for (var attempt = 1; attempt <= maxAttempts && !overallSuccess; attempt++)
            {
                execution.AttemptNumber = attempt;
                try
                {
                    overallSuccess = await RunStepsAsync(job, execution, db, jobTimeoutCts.Token);
                }
                catch (OperationCanceledException) when (jobTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    timedOut = true;
                    break;
                }

                if (!overallSuccess && attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(job.RetryDelaySeconds), cancellationToken);
                }
            }

            execution.FinishedAt = DateTime.UtcNow;
            execution.Status = timedOut ? JobStatus.Echec : (overallSuccess ? JobStatus.Succes : JobStatus.Echec);
            execution.Message = timedOut
                ? $"Timeout dépassé ({job.TimeoutSeconds}s)."
                : (overallSuccess ? "Exécution terminée avec succès." : "Exécution terminée en échec après reprises.");
            execution.ReturnCode = overallSuccess ? 0 : 1;

            job.LastStatus = execution.Status;
            await db.SaveChangesAsync(cancellationToken);

            if (timedOut)
            {
                await _notifications.DispatchAsync(job, NotificationEvent.Timeout, execution);
            }
            else if (overallSuccess)
            {
                await _notifications.DispatchAsync(job, NotificationEvent.Succes, execution);
            }
            else
            {
                await _notifications.DispatchAsync(job, NotificationEvent.Echec, execution);
            }

            await TriggerDependentJobsAsync(jobId, execution.Status, cancellationToken);

            return execution;
        }
        finally
        {
            _concurrencyGate.Exit(jobId);
        }
    }

    private async Task<bool> RunStepsAsync(Job job, JobExecution execution, AppDbContext db, CancellationToken jobToken)
    {
        var steps = job.Steps.OrderBy(s => s.Order).ToList();
        if (steps.Count == 0) return true;

        var stepsByOrder = steps.ToDictionary(s => s.Order);
        var currentOrder = steps.First().Order;
        var visited = new HashSet<int>();

        while (true)
        {
            if (!stepsByOrder.TryGetValue(currentOrder, out var step)) break;
            if (!visited.Add(currentOrder))
            {
                _logger.LogWarning("Boucle détectée dans le workflow du job {JobName} à l'étape {Order}, arrêt.", job.Name, currentOrder);
                return false;
            }

            var stepSucceeded = await RunStepWithRetriesAsync(job, step, execution, db, jobToken);

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

            var nextOrder = steps.Where(s => s.Order > currentOrder).Select(s => s.Order).DefaultIfEmpty(-1).Min();
            if (nextOrder == -1) return true;
            currentOrder = nextOrder;
        }

        return true;
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
            stepTimeoutCts.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));

            StepExecutionResult result;
            var timedOut = false;
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
            }
            catch (OperationCanceledException) when (stepTimeoutCts.IsCancellationRequested && !jobToken.IsCancellationRequested)
            {
                timedOut = true;
                result = StepExecutionResult.Fail($"Timeout de l'étape dépassé ({step.TimeoutSeconds}s).");
            }
            catch (Exception ex)
            {
                result = StepExecutionResult.Fail(ex.ToString());
            }

            log.FinishedAt = DateTime.UtcNow;
            log.Status = timedOut ? StepExecutionStatus.Timeout : (result.Success ? StepExecutionStatus.Succes : StepExecutionStatus.Echec);
            log.ReturnCode = result.ReturnCode;
            log.Output = result.Output;
            log.ErrorOutput = result.ErrorOutput;
            log.FilesProcessedCsv = result.FilesProcessedCsv;
            execution.StepLogs.Add(log);
            await db.SaveChangesAsync(jobToken);

            if (result.Success) return true;

            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(step.RetryDelaySeconds), jobToken);
            }
        }

        return false;
    }

    private async Task TriggerDependentJobsAsync(Guid completedJobId, JobStatus finalStatus, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var condition = finalStatus == JobStatus.Succes ? JobDependencyCondition.Succes : JobDependencyCondition.Echec;

        var dependents = await db.JobDependencies
            .Where(d => d.DependsOnJobId == completedJobId &&
                        (d.Condition == condition || d.Condition == JobDependencyCondition.Fin))
            .Include(d => d.Job)
            .Where(d => d.Job!.Enabled)
            .ToListAsync(ct);

        foreach (var dependency in dependents)
        {
            _ = Task.Run(() => RunAsync(dependency.JobId, $"Dépendance ({finalStatus})", CancellationToken.None), CancellationToken.None);
        }
    }

    private async Task<(string? Username, string? Secret)?> ResolveCredentialAsync(JobStep step, AppDbContext db, CancellationToken ct)
    {
        if (step.CredentialId is null) return null;

        var credential = await db.Credentials.FirstOrDefaultAsync(c => c.Id == step.CredentialId, ct);
        if (credential is null) return null;

        var secret = _credentialProtector.Unprotect(credential.EncryptedSecret);
        return (credential.Username, secret);
    }
}

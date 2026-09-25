using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KernelMK.Engine.Queue;

/// <summary>Durable admission and operator decisions. No external job action runs inside these transactions.</summary>
public sealed class JobQueueService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<JobExecutionRequest> EnqueueAsync(Guid jobId, string triggeredBy, int priority = 0,
        string? idempotencyKey = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priority);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, 9);
        if (idempotencyKey is { Length: > 200 }) throw new ArgumentException("Clé d'idempotence trop longue.", nameof(idempotencyKey));
        if (JobExecutionLineage.Current.Contains(jobId)) throw new InvalidOperationException("Appel cyclique de job refusé.");
        if (JobExecutionLineage.Current.Count >= 100) throw new InvalidOperationException("La chaîne de jobs dépasse 100 niveaux.");
        idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (idempotencyKey is not null)
        {
            var existing = await db.JobExecutionRequests.AsNoTracking().SingleOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null) return SameJob(existing, jobId);
        }
        if (!await db.Jobs.AnyAsync(job => job.Id == jobId && job.Enabled, ct))
            throw new InvalidOperationException("Le job est introuvable ou désactivé. Activez-le avant de le mettre en file.");
        var request = CreateRequest(jobId, triggeredBy, priority, idempotencyKey);
        db.JobExecutionRequests.Add(request);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (idempotencyKey is not null && error.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // A concurrent producer may have committed the same key. Never treat other constraints as success.
            await using var verify = await dbFactory.CreateDbContextAsync(ct);
            var existing = await verify.JobExecutionRequests.AsNoTracking().SingleOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, ct);
            if (existing is null) throw;
            return SameJob(existing, jobId);
        }
        return request;
    }

    internal static JobExecutionRequest CreateRequest(Guid jobId, string triggeredBy, int priority = 0, string? key = null) => new()
    {
        JobId = jobId, TriggeredBy = triggeredBy, Priority = priority, IdempotencyKey = key,
        AncestorJobIdsJson = JobExecutionLineage.Serialize(), RequestedAt = DateTime.UtcNow
    };

    public async Task<bool> CancelAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.JobExecutionRequests.Where(r => r.Id == requestId &&
                (r.Status == JobRequestStatus.Pending || r.Status == JobRequestStatus.NeedsReview))
            .ExecuteUpdateAsync(update => update.SetProperty(r => r.Status, JobRequestStatus.Canceled)
                .SetProperty(r => r.FinishedAt, DateTime.UtcNow)
                .SetProperty(r => r.Message, "Demande annulée par un opérateur."), ct) == 1;
    }

    public async Task<bool> SetPriorityAsync(Guid requestId, int priority, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priority);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, 9);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.JobExecutionRequests.Where(r => r.Id == requestId && r.Status == JobRequestStatus.Pending)
            .ExecuteUpdateAsync(update => update.SetProperty(r => r.Priority, priority), ct) == 1;
    }

    /// <summary>An explicit restart from the beginning, using the current job definition; never an automatic replay.</summary>
    public async Task<JobExecutionRequest?> RetryAsync(Guid requestId, string triggeredBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var original = await db.JobExecutionRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, ct);
        if (original is null || original.Status is not (JobRequestStatus.NeedsReview or JobRequestStatus.Failed)) return null;
        if (!await db.Jobs.AnyAsync(job => job.Id == original.JobId && job.Enabled, ct))
            throw new InvalidOperationException("Le job est introuvable ou désactivé.");
        var changed = await db.JobExecutionRequests.Where(r => r.Id == requestId && r.Status == original.Status)
            .ExecuteUpdateAsync(update => update.SetProperty(r => r.Status, JobRequestStatus.Canceled)
                .SetProperty(r => r.FinishedAt, DateTime.UtcNow)
                .SetProperty(r => r.Message, "Remplacée par une relance explicite depuis le début ; historique conservé."), ct);
        if (changed != 1) return null;
        var retry = CreateRequest(original.JobId, triggeredBy, original.Priority);
        retry.RetryOfRequestId = original.Id;
        retry.AncestorJobIdsJson = original.AncestorJobIdsJson;
        db.JobExecutionRequests.Add(retry);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return retry;
    }

    /// <summary>Creates a new request that skips only steps proven successful under the exact same job definition.</summary>
    public async Task<JobExecutionRequest?> RetryFromCheckpointAsync(Guid requestId, string triggeredBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var original = await db.JobExecutionRequests.AsNoTracking()
            .Include(r => r.Execution!).ThenInclude(e => e.StepLogs)
            .SingleOrDefaultAsync(r => r.Id == requestId, ct);
        if (original is null || original.Status is not (JobRequestStatus.NeedsReview or JobRequestStatus.Failed))
            return null;
        if (original.Execution is null || string.IsNullOrWhiteSpace(original.Execution.JobDefinitionHash))
            throw new InvalidOperationException("Cette exécution ne possède pas de point de reprise fiable. Relancez-la depuis le début.");

        var job = await db.Jobs.AsNoTracking().Include(j => j.Steps)
            .SingleOrDefaultAsync(j => j.Id == original.JobId && j.Enabled, ct)
            ?? throw new InvalidOperationException("Le job est introuvable ou désactivé.");
        var currentHash = JobDefinitionFingerprint.Compute(job);
        if (!string.Equals(currentHash, original.Execution.JobDefinitionHash, StringComparison.Ordinal))
            throw new InvalidOperationException("La définition du job a changé depuis cette exécution. La reprise à l'étape est refusée ; relancez depuis le début après vérification.");

        var completed = original.Execution.StepLogs
            .Where(log => log.Status is StepExecutionStatus.Succes or StepExecutionStatus.Ignore)
            .Select(log => log.JobStepId).Distinct().ToArray();
        if (completed.Length == 0)
            throw new InvalidOperationException("Aucune étape terminée avec succès n'est disponible pour une reprise.");

        var changed = await db.JobExecutionRequests
            .Where(r => r.Id == requestId && r.Status == original.Status)
            .ExecuteUpdateAsync(update => update.SetProperty(r => r.Status, JobRequestStatus.Canceled)
                .SetProperty(r => r.FinishedAt, DateTime.UtcNow)
                .SetProperty(r => r.Message, "Remplacée par une reprise contrôlée ; historique conservé."), ct);
        if (changed != 1) return null;

        var retry = CreateRequest(original.JobId, triggeredBy, original.Priority);
        retry.RetryOfRequestId = original.Id;
        retry.ResumeOfExecutionId = original.Execution.Id;
        retry.ResumeCompletedStepIdsJson = JsonSerializer.Serialize(completed);
        retry.ExpectedJobDefinitionHash = currentHash;
        retry.AncestorJobIdsJson = original.AncestorJobIdsJson;
        db.JobExecutionRequests.Add(retry);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return retry;
    }
    /// <summary>Call only once at startup, before workers are started, on the single active application instance.</summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var requests = await db.JobExecutionRequests.Where(r => r.Status == JobRequestStatus.Running).ToListAsync(ct);
        foreach (var request in requests)
        {
            request.Status = JobRequestStatus.NeedsReview;
            request.FinishedAt = now;
            request.Message = "Traitement interrompu. Vérifiez les effets externes avant une relance depuis le début.";
        }
        var executions = await db.JobExecutions.Where(e => e.Status == JobStatus.EnCours).Include(e => e.StepLogs).ToListAsync(ct);
        var jobIds = executions.Select(e => e.JobId).Distinct().ToArray();
        await db.Jobs.Where(job => jobIds.Contains(job.Id) && job.LastStatus == JobStatus.EnCours)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.LastStatus, JobStatus.Annule), ct);
        foreach (var execution in executions)
        {
            execution.Status = JobStatus.Annule;
            execution.FinishedAt = now;
            execution.Message = "Exécution interrompue par un redémarrage. Vérification nécessaire avant toute relance.";
            foreach (var step in execution.StepLogs.Where(step => step.Status == StepExecutionStatus.EnCours))
            {
                step.Status = StepExecutionStatus.Annule;
                step.FinishedAt = now;
                step.ErrorOutput ??= "Étape interrompue par un redémarrage du service.";
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return requests.Count + executions.Count;
    }

    public async Task MarkUncertainAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.JobExecutionRequests.Where(r => r.Id == requestId && r.Status == JobRequestStatus.Running)
            .ExecuteUpdateAsync(update => update.SetProperty(r => r.Status, JobRequestStatus.NeedsReview)
                .SetProperty(r => r.FinishedAt, DateTime.UtcNow)
                .SetProperty(r => r.Message, "Résultat moteur incertain. Vérifiez l'historique et les effets externes avant de relancer."), ct);
    }

    private static JobExecutionRequest SameJob(JobExecutionRequest existing, Guid jobId) =>
        existing.JobId == jobId ? existing : throw new InvalidOperationException("Cette clé d'idempotence désigne un autre job.");
}

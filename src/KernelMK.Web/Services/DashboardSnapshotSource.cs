using System.Collections.Immutable;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;
using KernelMK.Data;
using Microsoft.EntityFrameworkCore;

namespace KernelMK.Web.Services;

public sealed class DashboardSnapshotSource(IDbContextFactory<AppDbContext> factory, TimeProvider timeProvider) : IDashboardSnapshotSource
{
    public async Task<DashboardSnapshot> LoadAsync(int periodHours, CancellationToken cancellationToken)
    {
        if (periodHours is not (24 or 168 or 720)) throw new ArgumentOutOfRangeException(nameof(periodHours));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var since = now.AddHours(-periodHours);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var jobs = await db.Jobs.AsNoTracking().GroupBy(j => j.Enabled)
            .Select(g => new { Enabled = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var running = await db.JobExecutions.CountAsync(e => e.Status == JobStatus.EnCours, cancellationToken);
        // SQLite translates DateTime.Ticks to SQL. No execution history is materialized for this aggregate.
        var totals = await db.JobExecutions.AsNoTracking()
            .Where(e => e.StartedAt >= since && e.FinishedAt != null).GroupBy(e => 1)
            .Select(g => new
            {
                Success = g.Count(e => e.Status == JobStatus.Succes),
                Failed = g.Count(e => e.Status == JobStatus.Echec),
                Average = g.Average(e => (e.FinishedAt!.Value.Ticks - e.StartedAt.Ticks) / (double)TimeSpan.TicksPerSecond)
            }).SingleOrDefaultAsync(cancellationToken);
        var upcoming = await db.JobTriggers.AsNoTracking()
            .Where(t => t.Enabled && t.NextRunAt != null && t.Job != null && t.Job.Enabled)
            .OrderBy(t => t.NextRunAt).ThenBy(t => t.Id).Take(50)
            .Select(t => new DashboardTrigger(t.Id, t.JobId, t.Job!.Name.Substring(0, 160), t.Type, t.NextRunAt))
            .ToListAsync(cancellationToken);
        var executions = await db.JobExecutions.AsNoTracking()
            .OrderByDescending(e => e.StartedAt).ThenBy(e => e.Id).Take(50)
            .Select(e => new DashboardExecution(e.Id, e.JobId, e.Job == null ? "Job supprimé" : e.Job.Name.Substring(0, 160),
                e.StartedAt, e.FinishedAt, e.Status, e.TriggeredBy.Substring(0, 80), 0))
            .ToListAsync(cancellationToken);
        var executionIds = executions.Select(e => e.Id).ToArray();
        var executionFiles = new Dictionary<Guid, long>();
        await foreach (var log in db.StepExecutionLogs.AsNoTracking()
            .Where(l => executionIds.Contains(l.JobExecutionId) && l.FilesProcessedCsv != null)
            .Select(l => new { l.JobExecutionId, l.FilesProcessedCsv }).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            executionFiles.TryGetValue(log.JobExecutionId, out var count);
            executionFiles[log.JobExecutionId] = count + AnalyzeFiles(log.FilesProcessedCsv).Count;
        }
        var neverRun = await db.Jobs.AsNoTracking().Where(j => j.LastRunAt == null)
            .OrderBy(j => j.Name).ThenBy(j => j.Id).Take(50)
            .Select(j => new DashboardJob(j.Id, j.Name.Substring(0, 160), j.Enabled)).ToListAsync(cancellationToken);
        var notifications = await db.Notifications.AsNoTracking().OrderByDescending(n => n.CreatedAt).Take(50)
            .Select(n => new DashboardNotification(n.JobName.Substring(0, 160), n.Event, n.CreatedAt)).ToListAsync(cancellationToken);

        // Only non-secret step metadata is retained; config JSON lives only during this refresh.
        var stepConfigs = new Dictionary<Guid, (string Armateur, bool Upload)>();
        await foreach (var step in db.JobSteps.AsNoTracking()
            .Where(s => s.Type == StepType.TransfertSftp || s.Type == StepType.TransfertFtp || s.Type == StepType.TransfertFtps || s.Type == StepType.TransfertSmb)
            .Select(s => new { s.Id, s.Name, JobName = s.Job == null ? "" : s.Job.Name, s.ConfigJson })
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            TransferStepConfig? config = null;
            try { config = JsonSerializer.Deserialize<TransferStepConfig>(step.ConfigJson); }
            catch (JsonException) { /* A malformed transfer config must not break global supervision. */ }
            var partner = string.IsNullOrWhiteSpace(config?.Armateur) ? DetectArmateur(step.JobName, step.Name) : config.Armateur.Trim();
            stepConfigs[step.Id] = (Clip(partner, 160), config?.Upload ?? true);
        }
        var incidentRows = await db.JobExecutions.AsNoTracking()
            .Where(e => e.StartedAt >= since && e.Status == JobStatus.Echec)
            .OrderByDescending(e => e.StartedAt).ThenBy(e => e.Id).Take(50)
            .Select(e => new
            {
                e.Id, JobName = e.Job == null ? "Job supprimé" : e.Job.Name.Substring(0, 160), e.StartedAt,
                Message = e.Message == null ? null : e.Message.Substring(0, 2048),
                Failure = e.StepLogs.Where(l => l.Status == StepExecutionStatus.Echec || l.Status == StepExecutionStatus.Timeout)
                    .OrderBy(l => l.Order).Select(l => new
                    {
                        l.JobStepId, StepName = l.StepName.Substring(0, 160),
                        ErrorOutput = l.ErrorOutput == null ? null : l.ErrorOutput.Substring(0, 2048)
                    }).FirstOrDefault()
            }).ToListAsync(cancellationToken);
        var incidents = incidentRows.Select(e => new DashboardIncident(e.Id, e.JobName, e.StartedAt,
            e.Failure?.StepName ?? "Étape inconnue", DashboardDiagnostics.ClassifySide(e.Failure?.ErrorOutput ?? e.Message),
            e.Failure is not null && stepConfigs.TryGetValue(e.Failure.JobStepId, out var info) ? info.Armateur : "—")).ToImmutableArray();

        var connectionGroups = await db.StepExecutionLogs.AsNoTracking()
            .Where(l => l.StartedAt >= since && (l.Status == StepExecutionStatus.Succes || l.Status == StepExecutionStatus.Echec || l.Status == StepExecutionStatus.Timeout))
            .GroupBy(l => new { l.JobStepId, l.Status }).Select(g => new { g.Key.JobStepId, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var connections = new Dictionary<string, (int Ok, int Fail)>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in connectionGroups)
        {
            if (!stepConfigs.TryGetValue(group.JobStepId, out var info)) continue;
            connections.TryGetValue(info.Armateur, out var value);
            connections[info.Armateur] = group.Status == StepExecutionStatus.Succes
                ? (value.Ok + group.Count, value.Fail) : (value.Ok, value.Fail + group.Count);
        }

        // Stream narrow rows: memory usage no longer grows with the number of execution logs.
        long filesProcessed = 0;
        var partners = new Dictionary<string, DashboardPartner>(StringComparer.OrdinalIgnoreCase);
        await foreach (var log in db.StepExecutionLogs.AsNoTracking()
            .Where(l => l.JobExecution != null && l.JobExecution.StartedAt >= since && l.FilesProcessedCsv != null && l.FilesProcessedCsv != "")
            .Select(l => new { l.JobStepId, l.StepName, l.FilesProcessedCsv, l.StartedAt, l.FinishedAt })
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var files = AnalyzeFiles(log.FilesProcessedCsv);
            filesProcessed += files.Count;
            if (files.Count == 0) continue;
            var info = stepConfigs.TryGetValue(log.JobStepId, out var configured) ? configured : (Armateur: DetectArmateur(log.StepName), Upload: true);
            if (!partners.TryGetValue(info.Armateur, out var partner))
                partner = new(info.Armateur, 0, 0, 0, null, null);
            var date = log.FinishedAt ?? log.StartedAt;
            var latest = partner.DernierEchange is null || date > partner.DernierEchange;
            partners[info.Armateur] = partner with
            {
                FichiersEnvoyes = partner.FichiersEnvoyes + (info.Upload ? files.Count : 0),
                FichiersRecuperes = partner.FichiersRecuperes + (info.Upload ? 0 : files.Count),
                TotalExecutions = partner.TotalExecutions + 1,
                DernierEchange = latest ? date : partner.DernierEchange,
                DernierFichier = latest ? Clip(files.LastFile ?? "", 240) : partner.DernierFichier
            };
        }
        foreach (var info in stepConfigs.Values)
            partners.TryAdd(info.Armateur, new(info.Armateur, 0, 0, 0, null, null));
        return new()
        {
            GeneratedAt = timeProvider.GetUtcNow().UtcDateTime, PeriodHours = periodHours,
            TotalJobs = jobs.Sum(j => j.Count), ActiveJobs = jobs.Where(j => j.Enabled).Sum(j => j.Count),
            RunningCount = running, SuccessCount = totals?.Success ?? 0, FailedCount = totals?.Failed ?? 0,
            AverageDurationSeconds = Math.Max(0, totals?.Average ?? 0), FilesProcessed = filesProcessed,
            Upcoming = upcoming.ToImmutableArray(),
            RecentExecutions = executions.Select(e => e with { FileCount = executionFiles.GetValueOrDefault(e.Id) }).ToImmutableArray(),
            RecentIncidents = incidents, NeverRunJobs = neverRun.ToImmutableArray(), Notifications = notifications.ToImmutableArray(),
            TotalFilesSent = partners.Values.Sum(p => p.FichiersEnvoyes),
            TotalFilesReceived = partners.Values.Sum(p => p.FichiersRecuperes), PartnerCount = partners.Count,
            Partners = partners.Values.OrderByDescending(p => p.TotalFichiers).ThenBy(p => p.Armateur).Take(100).ToImmutableArray(),
            LeastReliablePartners = connections.Where(p => p.Value.Ok + p.Value.Fail >= 3)
                .Select(p => new DashboardReliability(p.Key, p.Value.Ok, p.Value.Fail)).OrderBy(p => p.SuccessRate).Take(4).ToImmutableArray()
        };
    }

    public static (int Count, string? LastFile) AnalyzeFiles(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return (0, null);
        var span = csv.AsSpan();
        var count = 0;
        ReadOnlySpan<char> last = default;
        var start = 0;
        for (var index = 0; index <= span.Length; index++)
        {
            if (index != span.Length && span[index] is not (';' or ',')) continue;
            var file = span[start..index].Trim();
            if (!file.IsEmpty) { count++; last = file; }
            start = index + 1;
        }
        // Both separators are recognized independently of the host OS; no directory paths enter the cache.
        var separator = last.LastIndexOfAny('/', '\\');
        return (count, count == 0 ? null : last[(separator + 1)..].ToString());
    }

    private static readonly string[] KnownPartners = ["GUCE", "MSC", "CMA CGM", "CMA-CGM", "MAERSK", "HAPAG-LLOYD", "HAPAG", "COSCO", "ONE", "GRIMALDI", "PIL", "ARKAS", "BOLUDA"];
    private static string DetectArmateur(params string?[] candidates)
    {
        foreach (var text in candidates)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (var name in KnownPartners)
                if (text.Contains(name, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return Clip(candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? "Partenaire Général", 160);
    }
    private static string Clip(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}

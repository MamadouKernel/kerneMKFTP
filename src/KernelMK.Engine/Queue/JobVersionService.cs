using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using Microsoft.EntityFrameworkCore;

namespace KernelMK.Engine.Queue;

public sealed class JobVersionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<JobDefinitionVersion> CaptureAsync(
        AppDbContext db, Job job, string? createdBy, string summary, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(CreateSnapshot(job), JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var latest = await db.JobDefinitionVersions.Where(v => v.JobId == job.Id)
            .MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0;
        var version = new JobDefinitionVersion
        {
            JobId = job.Id, VersionNumber = latest + 1, CreatedBy = createdBy,
            ChangeSummary = summary, DefinitionHash = hash, DefinitionJson = json
        };
        db.Add(version);
        return version;
    }

    public async Task<JobDefinitionVersion> RestoreAsync(
        AppDbContext db, Guid jobId, Guid versionId, string? restoredBy, CancellationToken ct = default)
    {
        var source = await db.JobDefinitionVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == versionId && v.JobId == jobId, ct)
            ?? throw new InvalidOperationException("Version introuvable.");
        var snapshot = JsonSerializer.Deserialize<JobSnapshot>(source.DefinitionJson)
            ?? throw new InvalidOperationException("La définition de cette version est illisible.");
        var job = await db.Jobs.Include(j => j.Steps).Include(j => j.Triggers)
            .Include(j => j.Dependencies).Include(j => j.NotificationRules)
            .SingleOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new InvalidOperationException("Job introuvable.");

        var credentialIds = snapshot.Steps.Where(s => s.CredentialId.HasValue)
            .Select(s => s.CredentialId!.Value).Distinct().ToArray();
        if (await db.Credentials.CountAsync(c => credentialIds.Contains(c.Id), ct) != credentialIds.Length)
            throw new InvalidOperationException("La version référence un credential supprimé. Restauration refusée.");
        var dependencyIds = snapshot.Dependencies.Select(d => d.DependsOnJobId).Distinct().ToArray();
        if (await db.Jobs.CountAsync(j => dependencyIds.Contains(j.Id), ct) != dependencyIds.Length)
            throw new InvalidOperationException("La version référence un job dépendant supprimé. Restauration refusée.");

        var currentTriggerTokens = job.Triggers.ToDictionary(t => t.Id, t => t.WebhookToken);
        var currentNotificationUrls = job.NotificationRules.ToDictionary(n => n.Id, n => n.WebhookUrl);

        db.JobSteps.RemoveRange(job.Steps);
        db.JobTriggers.RemoveRange(job.Triggers);
        db.JobDependencies.RemoveRange(job.Dependencies);
        db.NotificationRules.RemoveRange(job.NotificationRules);
        await db.SaveChangesAsync(ct);

        job.Name = snapshot.Name;
        job.Description = snapshot.Description;
        job.Owner = snapshot.Owner;
        job.Criticite = snapshot.Criticite;
        job.Enabled = snapshot.Enabled;
        job.MaxRetries = snapshot.MaxRetries;
        job.RetryDelaySeconds = snapshot.RetryDelaySeconds;
        job.TimeoutSeconds = snapshot.TimeoutSeconds;
        job.AllowConcurrentExecution = snapshot.AllowConcurrentExecution;
        job.ExecutionAccount = snapshot.ExecutionAccount;
        job.UpdatedAt = DateTime.UtcNow;
        job.UpdatedBy = restoredBy;
        job.Steps = snapshot.Steps.Select(s => new JobStep
        {
            Id = s.Id, JobId = job.Id, Order = s.Order, Name = s.Name, Type = s.Type,
            ConfigJson = s.ConfigJson, CredentialId = s.CredentialId, TimeoutSeconds = s.TimeoutSeconds,
            MaxRetries = s.MaxRetries, RetryDelaySeconds = s.RetryDelaySeconds,
            OnErrorAction = s.OnErrorAction, OnSuccessGoToOrder = s.OnSuccessGoToOrder,
            OnFailureGoToOrder = s.OnFailureGoToOrder
        }).ToList();
        job.Triggers = snapshot.Triggers.Select(t => new JobTrigger
        {
            Id = t.Id, JobId = job.Id, Type = t.Type, Enabled = t.Enabled,
            CronExpression = t.CronExpression, IntervalSeconds = t.IntervalSeconds,
            DaysOfWeekCsv = t.DaysOfWeekCsv, ExcludeWeekends = t.ExcludeWeekends,
            ExcludeHolidays = t.ExcludeHolidays, HolidayDatesCsv = t.HolidayDatesCsv,
            WindowStart = t.WindowStart, WindowEnd = t.WindowEnd, FolderPath = t.FolderPath,
            FolderFilter = t.FolderFilter, FolderWatchEvent = t.FolderWatchEvent,
            DependsOnJobId = t.DependsOnJobId, DependencyCondition = t.DependencyCondition,
            WebhookToken = currentTriggerTokens.GetValueOrDefault(t.Id)
        }).ToList();
        job.Dependencies = snapshot.Dependencies.Select(d => new JobDependency
        {
            Id = d.Id, JobId = job.Id, DependsOnJobId = d.DependsOnJobId, Condition = d.Condition
        }).ToList();
        job.NotificationRules = snapshot.Notifications.Select(n => new NotificationRule
        {
            Id = n.Id, JobId = job.Id, Event = n.Event, Channel = n.Channel,
            RecipientsCsv = n.RecipientsCsv, WebhookUrl = currentNotificationUrls.GetValueOrDefault(n.Id), Enabled = n.Enabled
        }).ToList();

        await db.SaveChangesAsync(ct);
        var restored = await CaptureAsync(db, job, restoredBy,
            $"Restauration de la version {source.VersionNumber}", ct);
        await db.SaveChangesAsync(ct);
        return restored;
    }

    public static IReadOnlyList<string> DescribeDifferences(string currentJson, string targetJson)
    {
        using var current = JsonDocument.Parse(currentJson);
        using var target = JsonDocument.Parse(targetJson);
        var sections = new List<string>();
        var groups = new[] { "Steps", "Triggers", "Dependencies", "Notifications" };
        foreach (var group in groups)
            if (!JsonEquals(current.RootElement.GetProperty(group), target.RootElement.GetProperty(group)))
                sections.Add(group switch
                {
                    "Steps" => "Étapes",
                    "Triggers" => "Déclencheurs",
                    "Dependencies" => "Dépendances",
                    _ => "Notifications"
                });
        var ignored = groups.ToHashSet(StringComparer.Ordinal);
        if (current.RootElement.EnumerateObject().Where(p => !ignored.Contains(p.Name))
            .Any(p => !target.RootElement.TryGetProperty(p.Name, out var value) || !JsonEquals(p.Value, value)))
            sections.Insert(0, "Paramètres généraux");
        return sections;
    }

    private static bool JsonEquals(JsonElement left, JsonElement right) =>
        string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

    private static JobSnapshot CreateSnapshot(Job job) => new(
        job.Name, job.Description, job.Owner, job.Criticite, job.Enabled,
        job.MaxRetries, job.RetryDelaySeconds, job.TimeoutSeconds, job.AllowConcurrentExecution,
        job.ExecutionAccount,
        job.Steps.OrderBy(x => x.Order).Select(x => new StepSnapshot(
            x.Id, x.Order, x.Name, x.Type, x.ConfigJson, x.CredentialId, x.TimeoutSeconds,
            x.MaxRetries, x.RetryDelaySeconds, x.OnErrorAction, x.OnSuccessGoToOrder,
            x.OnFailureGoToOrder)).ToArray(),
        job.Triggers.OrderBy(x => x.Id).Select(x => new TriggerSnapshot(
            x.Id, x.Type, x.Enabled, x.CronExpression, x.IntervalSeconds, x.DaysOfWeekCsv,
            x.ExcludeWeekends, x.ExcludeHolidays, x.HolidayDatesCsv, x.WindowStart, x.WindowEnd,
            x.FolderPath, x.FolderFilter, x.FolderWatchEvent, x.DependsOnJobId,
            x.DependencyCondition)).ToArray(),
        job.Dependencies.OrderBy(x => x.Id).Select(x =>
            new DependencySnapshot(x.Id, x.DependsOnJobId, x.Condition)).ToArray(),
        job.NotificationRules.OrderBy(x => x.Id).Select(x =>
            new NotificationSnapshot(x.Id, x.Event, x.Channel, x.RecipientsCsv, x.Enabled)).ToArray());

    public sealed record JobSnapshot(
        string Name, string? Description, string? Owner, Criticite Criticite, bool Enabled,
        int MaxRetries, int RetryDelaySeconds, int TimeoutSeconds, bool AllowConcurrentExecution,
        string ExecutionAccount, StepSnapshot[] Steps, TriggerSnapshot[] Triggers,
        DependencySnapshot[] Dependencies, NotificationSnapshot[] Notifications);
    public sealed record StepSnapshot(
        Guid Id, int Order, string Name, StepType Type, string ConfigJson, Guid? CredentialId,
        int TimeoutSeconds, int MaxRetries, int RetryDelaySeconds, OnErrorAction OnErrorAction,
        int? OnSuccessGoToOrder, int? OnFailureGoToOrder);
    public sealed record TriggerSnapshot(
        Guid Id, TriggerType Type, bool Enabled, string? CronExpression, int? IntervalSeconds,
        string? DaysOfWeekCsv, bool ExcludeWeekends, bool ExcludeHolidays, string? HolidayDatesCsv,
        TimeSpan? WindowStart, TimeSpan? WindowEnd, string? FolderPath, string? FolderFilter,
        FolderWatchEventType? FolderWatchEvent, Guid? DependsOnJobId,
        JobDependencyCondition? DependencyCondition);
    public sealed record DependencySnapshot(Guid Id, Guid DependsOnJobId, JobDependencyCondition Condition);
    public sealed record NotificationSnapshot(
        Guid Id, NotificationEvent Event, NotificationChannel Channel,
        string? RecipientsCsv, bool Enabled);
}

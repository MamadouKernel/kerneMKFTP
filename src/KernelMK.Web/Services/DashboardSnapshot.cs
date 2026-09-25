using System.Collections.Immutable;
using KernelMK.Core;

namespace KernelMK.Web.Services;

// Deliberately contain neither EF entities, credentials, configuration JSON nor raw log output.
public sealed record DashboardSnapshot
{
    public DateTime GeneratedAt { get; init; }
    public int PeriodHours { get; init; }
    public int TotalJobs { get; init; }
    public int ActiveJobs { get; init; }
    public int RunningCount { get; init; }
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public long TotalFilesSent { get; init; }
    public long TotalFilesReceived { get; init; }
    public int PartnerCount { get; init; }
    public long FilesProcessed { get; init; }
    public double AverageDurationSeconds { get; init; }
    public int? SuccessRate => SuccessCount + FailedCount == 0 ? null : (int)(100L * SuccessCount / (SuccessCount + FailedCount));
    public ImmutableArray<DashboardExecution> RecentExecutions { get; init; } = [];
    public ImmutableArray<DashboardIncident> RecentIncidents { get; init; } = [];
    public ImmutableArray<DashboardTrigger> Upcoming { get; init; } = [];
    public ImmutableArray<DashboardJob> NeverRunJobs { get; init; } = [];
    public ImmutableArray<DashboardNotification> Notifications { get; init; } = [];
    public ImmutableArray<DashboardPartner> Partners { get; init; } = [];
    public ImmutableArray<DashboardReliability> LeastReliablePartners { get; init; } = [];
}

public sealed record DashboardExecution(Guid Id, Guid JobId, string JobName, DateTime StartedAt,
    DateTime? FinishedAt, JobStatus Status, string TriggeredBy, long FileCount)
{
    public TimeSpan? Duration => FinishedAt - StartedAt;
}
public sealed record DashboardTrigger(Guid Id, Guid JobId, string JobName, TriggerType Type, DateTime? NextRunAt);
public sealed record DashboardJob(Guid Id, string Name, bool Enabled);
public sealed record DashboardNotification(string JobName, NotificationEvent Event, DateTime CreatedAt);
public enum AnomalySide { Cit, Armateur, Indetermine, FauxPositif }
public sealed record SideClassification(AnomalySide Side, string Label, string CssClass, string Icon);
public sealed record DashboardIncident(Guid Id, string JobName, DateTime StartedAt, string StepName,
    SideClassification Diagnosis, string Armateur);
public sealed record DashboardPartner(string Armateur, long FichiersEnvoyes, long FichiersRecuperes,
    int TotalExecutions, DateTime? DernierEchange, string? DernierFichier)
{
    public long TotalFichiers => FichiersEnvoyes + FichiersRecuperes;
}
public sealed record DashboardReliability(string Armateur, int Ok, int Fail)
{
    public double SuccessRate => Ok + Fail > 0 ? 100.0 * Ok / (Ok + Fail) : 100;
}

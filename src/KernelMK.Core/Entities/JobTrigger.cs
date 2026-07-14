namespace KernelMK.Core.Entities;

public class JobTrigger
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Job? Job { get; set; }

    public TriggerType Type { get; set; }
    public bool Enabled { get; set; } = true;

    // Horaire / Cron
    public string? CronExpression { get; set; }
    public int? IntervalSeconds { get; set; }

    // Calendrier
    public string? DaysOfWeekCsv { get; set; } // ex: "Mon,Tue,Wed,Thu,Fri"
    public bool ExcludeWeekends { get; set; }
    public bool ExcludeHolidays { get; set; }
    public string? HolidayDatesCsv { get; set; } // yyyy-MM-dd,...
    public TimeSpan? WindowStart { get; set; }
    public TimeSpan? WindowEnd { get; set; }

    // Evénement dossier
    public string? FolderPath { get; set; }
    public string? FolderFilter { get; set; } // ex: *.csv
    public FolderWatchEventType? FolderWatchEvent { get; set; }

    // Dépendance job
    public Guid? DependsOnJobId { get; set; }
    public JobDependencyCondition? DependencyCondition { get; set; }

    // API / Webhook
    public string? WebhookToken { get; set; }

    public DateTime? NextRunAt { get; set; }
    public DateTime? LastFiredAt { get; set; }
}

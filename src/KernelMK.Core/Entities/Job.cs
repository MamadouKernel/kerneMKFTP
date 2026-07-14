namespace KernelMK.Core.Entities;

public class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Owner { get; set; }
    public Criticite Criticite { get; set; } = Criticite.Moyenne;

    public bool Enabled { get; set; } = true;
    public JobStatus LastStatus { get; set; } = JobStatus.EnAttente;

    public int MaxRetries { get; set; } = 0;
    public int RetryDelaySeconds { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 3600;
    public bool AllowConcurrentExecution { get; set; } = false;

    public string ExecutionAccount { get; set; } = "System";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }

    public List<JobStep> Steps { get; set; } = new();
    public List<JobTrigger> Triggers { get; set; } = new();
    public List<JobDependency> Dependencies { get; set; } = new();
    public List<NotificationRule> NotificationRules { get; set; } = new();
    public List<JobExecution> Executions { get; set; } = new();
}

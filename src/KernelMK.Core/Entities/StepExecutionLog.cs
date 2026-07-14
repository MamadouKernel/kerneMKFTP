namespace KernelMK.Core.Entities;

public class StepExecutionLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobExecutionId { get; set; }
    public JobExecution? JobExecution { get; set; }

    public Guid JobStepId { get; set; }
    public string StepName { get; set; } = string.Empty;
    public int Order { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public StepExecutionStatus Status { get; set; } = StepExecutionStatus.EnCours;

    public int? ReturnCode { get; set; }
    public string? Output { get; set; }
    public string? ErrorOutput { get; set; }
    public string? FilesProcessedCsv { get; set; }
    public int AttemptNumber { get; set; } = 1;

    public TimeSpan? Duration => FinishedAt.HasValue ? FinishedAt.Value - StartedAt : null;
}

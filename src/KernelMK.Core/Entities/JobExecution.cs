namespace KernelMK.Core.Entities;

public class JobExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Job? Job { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public JobStatus Status { get; set; } = JobStatus.EnCours;

    public string TriggeredBy { get; set; } = "Manuel";
    public string? ServerName { get; set; }
    public string? ExecutionAccount { get; set; }

    public int? ReturnCode { get; set; }
    public string? Message { get; set; }
    public int AttemptNumber { get; set; } = 1;

    public List<StepExecutionLog> StepLogs { get; set; } = new();

    public TimeSpan? Duration => FinishedAt.HasValue ? FinishedAt.Value - StartedAt : null;
}

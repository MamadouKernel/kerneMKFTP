namespace KernelMK.Core.Entities;

/// <summary>A durable request to run a job, retained independently of execution history.</summary>
public class JobExecutionRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Job? Job { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public JobRequestStatus Status { get; set; } = JobRequestStatus.Pending;
    public int Priority { get; set; }
    public string TriggeredBy { get; set; } = "Manuel";
    public string? IdempotencyKey { get; set; }
    public string AncestorJobIdsJson { get; set; } = "[]";

    public Guid? ExecutionId { get; set; }
    public JobExecution? Execution { get; set; }
    public Guid? RetryOfRequestId { get; set; }
    public Guid? ResumeOfExecutionId { get; set; }
    public string ResumeCompletedStepIdsJson { get; set; } = "[]";
    public string? ExpectedJobDefinitionHash { get; set; }
    public string? Message { get; set; }
}

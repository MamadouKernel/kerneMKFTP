using KernelMK.Core.Entities;

namespace KernelMK.Engine.Execution;

public interface IJobRunner
{
    Task<JobExecution> RunAsync(Guid jobId, string triggeredBy, CancellationToken cancellationToken = default);
}

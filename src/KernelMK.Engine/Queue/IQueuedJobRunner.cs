using KernelMK.Core.Entities;

namespace KernelMK.Engine.Queue;

public interface IQueuedJobRunner
{
    /// <summary>Returns null when the request is no longer pending or capacity is unavailable; it stays queued.</summary>
    Task<JobExecution?> TryRunRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
}

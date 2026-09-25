using KernelMK.Engine.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace KernelMK.Tests.Engine;

public class CoordinationTests
{
    [Fact]
    public void UnregisteringOneConcurrentRunKeepsOtherRunVisibleAndStoppable()
    {
        var coordinator = new JobExecutionCoordinator(NullLogger<JobExecutionCoordinator>.Instance);
        var jobId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        coordinator.RegisterExecution(jobId, firstId);
        var second = coordinator.RegisterExecution(jobId, secondId);
        coordinator.UnregisterExecution(jobId, firstId);

        Assert.True(coordinator.IsJobRunning(jobId));
        Assert.Equal(new[] { jobId }, coordinator.GetRunningJobIds());
        Assert.True(coordinator.TryStopJob(jobId));
        Assert.True(second.IsCancellationRequested);
        coordinator.UnregisterExecution(jobId, secondId);
        Assert.False(coordinator.IsJobRunning(jobId));
    }

    [Fact]
    public void StopJobCancelsEveryConcurrentExecution()
    {
        var coordinator = new JobExecutionCoordinator(NullLogger<JobExecutionCoordinator>.Instance);
        var jobId = Guid.NewGuid();
        using var first = coordinator.RegisterExecution(jobId, Guid.NewGuid());
        using var second = coordinator.RegisterExecution(jobId, Guid.NewGuid());
        Assert.True(coordinator.TryStopJob(jobId));
        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
    }

    [Fact]
    public void GateCountsConcurrentRunsWhenConcurrencyIsDisabledLater()
    {
        var gate = new ConcurrencyGate(3);
        var jobId = Guid.NewGuid();
        Assert.True(gate.TryEnter(jobId, true));
        Assert.True(gate.TryEnter(jobId, true));
        gate.Exit(jobId);
        Assert.False(gate.TryEnter(jobId, false));
        gate.Exit(jobId);
        Assert.True(gate.TryEnter(jobId, false));
        gate.Exit(jobId);
    }

    [Fact]
    public async Task GateNeverExceedsGlobalCapacityUnderContention()
    {
        var gate = new ConcurrencyGate(3);
        var active = 0;
        var peak = 0;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            var id = Guid.NewGuid();
            if (!gate.TryEnter(id, true)) return;
            var count = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref peak); }
            while (count > previous && Interlocked.CompareExchange(ref peak, count, previous) != previous);
            await Task.Delay(10);
            Interlocked.Decrement(ref active);
            gate.Exit(id);
        }));
        Assert.InRange(peak, 1, 3);
        Assert.Equal(0, active);
    }
}

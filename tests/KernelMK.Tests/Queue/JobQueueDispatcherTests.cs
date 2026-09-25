using System.Collections.Concurrent;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Engine.Queue;
using KernelMK.Engine.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KernelMK.Tests.Queue;

public sealed class JobQueueDispatcherTests
{
    private sealed class BlockedRunner(IReadOnlyDictionary<Guid, Guid> requestJobs) : IQueuedJobRunner
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<Guid, int> _calls = new();
        private int _finished;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Finished => Volatile.Read(ref _finished);
        public int CallsFor(Guid jobId) => _calls.GetValueOrDefault(jobId);
        public Task Started(Guid jobId) => Signal(jobId).Task;
        private TaskCompletionSource Signal(Guid jobId) => _started.GetOrAdd(jobId,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public async Task<JobExecution?> TryRunRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
        {
            var jobId = requestJobs[requestId];
            _calls.AddOrUpdate(jobId, 1, (_, calls) => calls + 1);
            Signal(jobId).TrySetResult();
            // Hold admission before coordinator registration: the dispatcher must already reserve
            // this non-concurrent job, and use its other slot for independent work.
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            Interlocked.Increment(ref _finished);
            return null;
        }
    }

    [Fact]
    public async Task LargeBacklogForOneJobDoesNotHideAnotherJobWhileFirstAdmissionIsBlocked()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"kernelmk-dispatch-{Guid.NewGuid():N}.db");
        try
        {
            var firstJob = new Job { Name = "Large non-concurrent backlog" };
            var secondJob = new Job { Name = "Independent job" };
            var requestedAt = DateTime.UtcNow.AddMinutes(-10);
            var requests = Enumerable.Range(0, 300).Select(index => new JobExecutionRequest
            {
                JobId = firstJob.Id, RequestedAt = requestedAt.AddMilliseconds(index)
            }).ToList();
            requests.Add(new JobExecutionRequest { JobId = secondJob.Id, RequestedAt = requestedAt.AddMinutes(1) });
            var runner = new BlockedRunner(requests.ToDictionary(request => request.Id, request => request.JobId));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Engine:MaxParallelJobs"] = "2"
            }).Build();
            var services = new ServiceCollection();
            services.AddDbContextFactory<AppDbContext>(options =>
                options.UseSqlite($"Data Source={databasePath};Pooling=False;Foreign Keys=True"));
            services.AddSingleton<IQueuedJobRunner>(runner);
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.Jobs.AddRange(firstJob, secondJob);
                db.JobExecutionRequests.AddRange(requests);
                await db.SaveChangesAsync();
            }
            var coordinator = new JobExecutionCoordinator(NullLogger<JobExecutionCoordinator>.Instance);
            using var dispatcher = new JobQueueDispatcher(factory,
                provider.GetRequiredService<IServiceScopeFactory>(), coordinator, configuration,
                NullLogger<JobQueueDispatcher>.Instance);
            try
            {
                await dispatcher.StartAsync(CancellationToken.None);
                await Task.WhenAll(runner.Started(firstJob.Id), runner.Started(secondJob.Id))
                    .WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(0, runner.Finished);
                Assert.False(runner.Release.Task.IsCompleted);
                Assert.Equal(1, runner.CallsFor(firstJob.Id));
                Assert.Equal(1, runner.CallsFor(secondJob.Id));
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await dispatcher.StopAsync(stop.Token);
                runner.Release.TrySetResult();
            }
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }
}

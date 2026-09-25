using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Data.Security;
using KernelMK.Engine;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Queue;
using KernelMK.Engine.Scheduling;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KernelMK.Tests.Queue;

public sealed class JobQueueServiceTests
{
    private sealed class CountingExecutor : IStepExecutor
    {
        public int Calls { get; private set; }
        public IReadOnlyCollection<StepType> SupportedTypes => [StepType.CommandeSysteme];
        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            Calls++;
            return Task.FromResult(StepExecutionResult.Ok());
        }
    }

    private sealed class FailOnceInterceptor : SaveChangesInterceptor
    {
        public bool FailNext { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Simulated persistence failure");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"kernelmk-queue-{Guid.NewGuid():N}.db");
        public FailOnceInterceptor Interceptor { get; } = new();
        public CountingExecutor Executor { get; } = new();
        public ServiceProvider Services { get; }
        public IDbContextFactory<AppDbContext> Factory => Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        public JobQueueService Queue => Services.GetRequiredService<JobQueueService>();

        public Fixture()
        {
            var configuration = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<CredentialProtector>();
            services.AddDbContextFactory<AppDbContext>(options => options
                .UseSqlite($"Data Source={_databasePath};Pooling=False;Foreign Keys=True")
                .AddInterceptors(Interceptor));
            services.AddKernelMKEngine(configuration);
            services.AddSingleton<IStepExecutor>(Executor);
            Services = services.BuildServiceProvider();
            using var db = Factory.CreateDbContext();
            db.Database.EnsureCreated();
        }

        public async Task SeedAsync(params object[] entities)
        {
            await using var db = await Factory.CreateDbContextAsync();
            db.AddRange(entities);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            File.Delete(_databasePath);
            File.Delete(_databasePath + "-wal");
            File.Delete(_databasePath + "-shm");
        }
    }

    [Fact]
    public async Task RepeatedIdempotencyKeyReturnsTheOriginalRequestWithoutChangingItsPriority()
    {
        await using var fixture = new Fixture();
        var job = new Job { Name = "Idempotent admission" };
        await fixture.SeedAsync(job);

        var original = await fixture.Queue.EnqueueAsync(job.Id, "first", priority: 3, idempotencyKey: "same-event");
        var repeated = await fixture.Queue.EnqueueAsync(job.Id, "second", priority: 9, idempotencyKey: "same-event");

        Assert.Equal(original.Id, repeated.Id);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var persisted = await db.JobExecutionRequests.SingleAsync();
        Assert.Equal(3, persisted.Priority);
        Assert.Equal("first", persisted.TriggeredBy);
        Assert.Equal(original.RequestedAt, persisted.RequestedAt);
        Assert.Equal(JobRequestStatus.Pending, persisted.Status);
    }

    [Fact]
    public async Task IdempotencyKeyForAnotherJobIsRejected()
    {
        await using var fixture = new Fixture();
        var first = new Job { Name = "First job" };
        var second = new Job { Name = "Second job" };
        await fixture.SeedAsync(first, second);
        var original = await fixture.Queue.EnqueueAsync(first.Id, "test", idempotencyKey: "shared-key");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Queue.EnqueueAsync(second.Id, "test", idempotencyKey: "shared-key"));

        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(original.Id, (await db.JobExecutionRequests.SingleAsync()).Id);
    }

    [Fact]
    public async Task CancelingAPendingRequestPreventsTheExecutorFromStarting()
    {
        await using var fixture = new Fixture();
        var job = new Job
        {
            Name = "Canceled before dispatch",
            Steps = [new JobStep { Name = "Must not run", Type = StepType.CommandeSysteme, Order = 1 }]
        };
        await fixture.SeedAsync(job);
        var request = await fixture.Queue.EnqueueAsync(job.Id, "test");

        Assert.True(await fixture.Queue.CancelAsync(request.Id));
        Assert.False(await fixture.Queue.CancelAsync(request.Id));
        await using var scope = fixture.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IQueuedJobRunner>().TryRunRequestAsync(request.Id);

        Assert.Null(result);
        Assert.Equal(0, fixture.Executor.Calls);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Empty(await db.JobExecutions.ToListAsync());
        var persisted = await db.JobExecutionRequests.SingleAsync();
        Assert.Equal(JobRequestStatus.Canceled, persisted.Status);
        Assert.Null(persisted.StartedAt);
        Assert.NotNull(persisted.FinishedAt);
        Assert.Null(persisted.ExecutionId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task PriorityBoundariesArePersistedAndOnlyPendingRequestsCanBeReprioritized(int priority)
    {
        await using var fixture = new Fixture();
        var job = new Job { Name = "Priority control" };
        await fixture.SeedAsync(job);
        var request = await fixture.Queue.EnqueueAsync(job.Id, "test", priority);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
            Assert.Equal(priority, (await db.JobExecutionRequests.SingleAsync()).Priority);

        var newPriority = 9 - priority;
        Assert.True(await fixture.Queue.SetPriorityAsync(request.Id, newPriority));
        Assert.True(await fixture.Queue.CancelAsync(request.Id));
        Assert.False(await fixture.Queue.SetPriorityAsync(request.Id, priority));

        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(newPriority, (await verify.JobExecutionRequests.SingleAsync()).Priority);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public async Task OutOfRangePriorityNeverChangesTheQueue(int priority)
    {
        await using var fixture = new Fixture();
        var job = new Job { Name = "Invalid priority" };
        await fixture.SeedAsync(job);
        var request = await fixture.Queue.EnqueueAsync(job.Id, "test", priority: 4);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Queue.EnqueueAsync(job.Id, "test", priority));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Queue.SetPriorityAsync(request.Id, priority));

        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(4, (await db.JobExecutionRequests.SingleAsync()).Priority);
    }

    [Theory]
    [InlineData(JobRequestStatus.Failed)]
    [InlineData(JobRequestStatus.NeedsReview)]
    public async Task ExplicitRetryCreatesOnlyOneNewRequestAndRetainsTheOriginalHistory(JobRequestStatus status)
    {
        await using var fixture = new Fixture();
        var job = new Job { Name = "Explicit restart" };
        var execution = new JobExecution { JobId = job.Id, Status = JobStatus.Echec };
        var ancestors = System.Text.Json.JsonSerializer.Serialize(new[] { Guid.NewGuid() });
        var original = new JobExecutionRequest
        {
            JobId = job.Id, ExecutionId = execution.Id, Status = status, Priority = 7,
            IdempotencyKey = "original-event", AncestorJobIdsJson = ancestors
        };
        await fixture.SeedAsync(job, execution, original);

        var retry = await fixture.Queue.RetryAsync(original.Id, "operator");
        var repeated = await fixture.Queue.RetryAsync(original.Id, "second operator");

        Assert.NotNull(retry);
        Assert.Null(repeated);
        Assert.NotEqual(original.Id, retry.Id);
        Assert.Equal(original.Id, retry.RetryOfRequestId);
        Assert.Equal(JobRequestStatus.Pending, retry.Status);
        Assert.Equal(7, retry.Priority);
        Assert.Equal(ancestors, retry.AncestorJobIdsJson);
        Assert.Null(retry.IdempotencyKey);
        Assert.Null(retry.ExecutionId);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.JobExecutionRequests.CountAsync());
        var retired = await db.JobExecutionRequests.SingleAsync(r => r.Id == original.Id);
        Assert.Equal(JobRequestStatus.Canceled, retired.Status);
        Assert.NotNull(retired.FinishedAt);
        Assert.Equal(execution.Id, retired.ExecutionId);
        Assert.Equal("original-event", retired.IdempotencyKey);
        Assert.Single(await db.JobExecutions.ToListAsync());
    }

    [Fact]
    public async Task FailedRetrySaveRestoresTheOriginalRequestInsteadOfLosingIt()
    {
        await using var fixture = new Fixture();
        var job = new Job { Name = "Retry rollback" };
        var original = new JobExecutionRequest { JobId = job.Id, Status = JobRequestStatus.NeedsReview };
        await fixture.SeedAsync(job, original);
        fixture.Interceptor.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Queue.RetryAsync(original.Id, "operator"));

        await using var db = await fixture.Factory.CreateDbContextAsync();
        var persisted = await db.JobExecutionRequests.SingleAsync();
        Assert.Equal(original.Id, persisted.Id);
        Assert.Equal(JobRequestStatus.NeedsReview, persisted.Status);
        Assert.Null(persisted.FinishedAt);
    }

    [Fact]
    public async Task RecoveryKeepsPendingRequestsAndFinalizesInterruptedExecutionsAndSteps()
    {
        await using var fixture = new Fixture();
        var now = DateTime.UtcNow;
        var job = new Job { Name = "Interrupted job", LastStatus = JobStatus.EnCours, LastRunAt = now.AddMinutes(-2) };
        var linked = new JobExecution { JobId = job.Id, StartedAt = now.AddMinutes(-2) };
        var orphan = new JobExecution { JobId = job.Id, StartedAt = now.AddMinutes(-5) };
        linked.StepLogs.Add(new StepExecutionLog { JobExecutionId = linked.Id, JobStepId = Guid.NewGuid(), StepName = "Interrupted" });
        orphan.StepLogs.Add(new StepExecutionLog { JobExecutionId = orphan.Id, JobStepId = Guid.NewGuid(), StepName = "Orphan", ErrorOutput = "Original diagnostic" });
        var completedStep = new StepExecutionLog
        {
            JobExecutionId = orphan.Id, JobStepId = Guid.NewGuid(), StepName = "Already completed",
            Status = StepExecutionStatus.Succes, FinishedAt = now.AddMinutes(-4)
        };
        orphan.StepLogs.Add(completedStep);
        var pending = new JobExecutionRequest { JobId = job.Id, Priority = 8 };
        var interrupted = new JobExecutionRequest { JobId = job.Id, ExecutionId = linked.Id, Status = JobRequestStatus.Running, StartedAt = linked.StartedAt };
        var completed = new JobExecutionRequest { JobId = job.Id, Status = JobRequestStatus.Succeeded, FinishedAt = now.AddDays(-1) };
        await fixture.SeedAsync(job, linked, orphan, pending, interrupted, completed);

        Assert.Equal(3, await fixture.Queue.RecoverInterruptedAsync());
        Assert.Equal(0, await fixture.Queue.RecoverInterruptedAsync());

        await using var db = await fixture.Factory.CreateDbContextAsync();
        var pendingAfter = await db.JobExecutionRequests.SingleAsync(r => r.Id == pending.Id);
        Assert.Equal(JobRequestStatus.Pending, pendingAfter.Status);
        Assert.Equal(8, pendingAfter.Priority);
        Assert.Null(pendingAfter.StartedAt);
        Assert.Null(pendingAfter.FinishedAt);
        var review = await db.JobExecutionRequests.SingleAsync(r => r.Id == interrupted.Id);
        Assert.Equal(JobRequestStatus.NeedsReview, review.Status);
        Assert.NotNull(review.FinishedAt);
        Assert.Equal(linked.Id, review.ExecutionId);
        Assert.Equal(JobRequestStatus.Succeeded, (await db.JobExecutionRequests.SingleAsync(r => r.Id == completed.Id)).Status);
        Assert.All(await db.JobExecutions.ToListAsync(), e =>
        {
            Assert.Equal(JobStatus.Annule, e.Status);
            Assert.NotNull(e.FinishedAt);
        });
        Assert.All(await db.StepExecutionLogs.Where(s => s.Id != completedStep.Id).ToListAsync(), s =>
        {
            Assert.Equal(StepExecutionStatus.Annule, s.Status);
            Assert.NotNull(s.FinishedAt);
            Assert.False(string.IsNullOrWhiteSpace(s.ErrorOutput));
        });
        Assert.Equal("Original diagnostic", (await db.StepExecutionLogs.SingleAsync(s => s.StepName == "Orphan")).ErrorOutput);
        var completedAfter = await db.StepExecutionLogs.SingleAsync(s => s.Id == completedStep.Id);
        Assert.Equal(StepExecutionStatus.Succes, completedAfter.Status);
        Assert.Equal(completedStep.FinishedAt, completedAfter.FinishedAt);
        Assert.Equal(JobStatus.Annule, (await db.Jobs.SingleAsync()).LastStatus);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Fact]
    public async Task SchedulerSaveFailureRollsBackTriggerAdvancementAndAdmissionTogether()
    {
        await using var fixture = new Fixture();
        var now = DateTime.UtcNow;
        var expectedAt = now.AddSeconds(-30);
        var trigger = new JobTrigger { Type = TriggerType.Horaire, IntervalSeconds = 60, NextRunAt = expectedAt };
        var job = new Job { Name = "Atomic schedule", Criticite = Criticite.Haute, Triggers = [trigger] };
        await fixture.SeedAsync(job);
        var scheduler = ActivatorUtilities.CreateInstance<JobSchedulerService>(fixture.Services);
        fixture.Interceptor.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.EnqueueDueAsync(now));

        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var persisted = await db.JobTriggers.SingleAsync();
            Assert.Equal(expectedAt, persisted.NextRunAt);
            Assert.Null(persisted.LastFiredAt);
            Assert.Empty(await db.JobExecutionRequests.ToListAsync());
        }
        Assert.Equal(1, await scheduler.EnqueueDueAsync(now));
        Assert.Equal(0, await scheduler.EnqueueDueAsync(now));
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var admitted = await verify.JobExecutionRequests.SingleAsync();
        Assert.Equal(job.Id, admitted.JobId);
        Assert.Equal(JobRequestStatus.Pending, admitted.Status);
        Assert.Equal((int)Criticite.Haute, admitted.Priority);
        Assert.Equal($"schedule:{trigger.Id:N}:{expectedAt.Ticks}", admitted.IdempotencyKey);
        var advanced = await verify.JobTriggers.SingleAsync();
        Assert.Equal(now.AddSeconds(60), advanced.NextRunAt);
        Assert.Equal(now, advanced.LastFiredAt);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    public async Task InvalidOrCyclicLineageRequiresReviewWithoutStartingTheJob(string ancestors)
    {
        await using var fixture = new Fixture();
        var job = new Job
        {
            Name = "Invalid lineage",
            Steps = [new JobStep { Name = "Must not run", Type = StepType.CommandeSysteme, Order = 1 }]
        };
        var request = new JobExecutionRequest { JobId = job.Id, AncestorJobIdsJson = ancestors };
        if (ancestors == "[]")
            request.AncestorJobIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { job.Id });
        await fixture.SeedAsync(job, request);

        await using var scope = fixture.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IQueuedJobRunner>().TryRunRequestAsync(request.Id);

        Assert.Null(result);
        Assert.Equal(0, fixture.Executor.Calls);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Empty(await db.JobExecutions.ToListAsync());
        var persisted = await db.JobExecutionRequests.SingleAsync();
        Assert.Equal(JobRequestStatus.NeedsReview, persisted.Status);
        Assert.NotNull(persisted.FinishedAt);
        Assert.Contains("invalide ou cyclique", persisted.Message);
    }

    [Fact]
    public async Task ExcessiveLineageRequiresReviewWithoutStartingTheJob()
    {
        await using var fixture = new Fixture();
        var job = new Job { Name = "Excessive lineage" };
        var request = new JobExecutionRequest
        {
            JobId = job.Id,
            AncestorJobIdsJson = System.Text.Json.JsonSerializer.Serialize(
                Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()))
        };
        await fixture.SeedAsync(job, request);

        await using var scope = fixture.Services.CreateAsyncScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IQueuedJobRunner>().TryRunRequestAsync(request.Id));

        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(JobRequestStatus.NeedsReview, (await db.JobExecutionRequests.SingleAsync()).Status);
        Assert.Empty(await db.JobExecutions.ToListAsync());
    }
    [Fact]
    public async Task CheckpointRetrySkipsProvenStepsAndRunsTheFirstRemainingStep()
    {
        await using var fixture = new Fixture();
        var first = new JobStep { Name = "Already sent", Type = StepType.CommandeSysteme, Order = 1 };
        var second = new JobStep { Name = "Continue here", Type = StepType.CommandeSysteme, Order = 2 };
        var job = new Job { Name = "Checkpoint workflow", Steps = [first, second] };
        var hash = JobDefinitionFingerprint.Compute(job);
        var execution = new JobExecution { JobId = job.Id, Status = JobStatus.Annule, JobDefinitionHash = hash };
        execution.StepLogs.Add(new StepExecutionLog
        {
            JobExecutionId = execution.Id, JobStepId = first.Id, StepName = first.Name, Order = first.Order,
            Status = StepExecutionStatus.Succes, FinishedAt = DateTime.UtcNow
        });
        var original = new JobExecutionRequest
        {
            JobId = job.Id, ExecutionId = execution.Id, Status = JobRequestStatus.NeedsReview,
            ExpectedJobDefinitionHash = hash
        };
        await fixture.SeedAsync(job, execution, original);

        var retry = await fixture.Queue.RetryFromCheckpointAsync(original.Id, "operator");
        Assert.NotNull(retry);
        Assert.Equal(execution.Id, retry.ResumeOfExecutionId);
        await using (var scope = fixture.Services.CreateAsyncScope())
            Assert.Equal(JobStatus.Succes,
                (await scope.ServiceProvider.GetRequiredService<IQueuedJobRunner>().TryRunRequestAsync(retry.Id))!.Status);

        Assert.Equal(1, fixture.Executor.Calls);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var resumedExecution = await db.JobExecutions.Include(e => e.StepLogs)
            .SingleAsync(e => e.Id != execution.Id);
        Assert.Contains(resumedExecution.StepLogs,
            log => log.JobStepId == first.Id && log.Status == StepExecutionStatus.Ignore);
        Assert.Contains(resumedExecution.StepLogs,
            log => log.JobStepId == second.Id && log.Status == StepExecutionStatus.Succes);
        Assert.Equal(JobRequestStatus.Canceled,
            (await db.JobExecutionRequests.SingleAsync(r => r.Id == original.Id)).Status);
        Assert.Equal(JobRequestStatus.Succeeded,
            (await db.JobExecutionRequests.SingleAsync(r => r.Id == retry.Id)).Status);
    }

    [Fact]
    public async Task CheckpointRetryIsRefusedWhenTheJobDefinitionChanged()
    {
        await using var fixture = new Fixture();
        var step = new JobStep { Name = "Original", Type = StepType.CommandeSysteme, Order = 1 };
        var job = new Job { Name = "Changed workflow", Steps = [step] };
        var originalHash = JobDefinitionFingerprint.Compute(job);
        var execution = new JobExecution { JobId = job.Id, Status = JobStatus.Annule, JobDefinitionHash = originalHash };
        execution.StepLogs.Add(new StepExecutionLog
        {
            JobExecutionId = execution.Id, JobStepId = step.Id, StepName = step.Name, Order = 1,
            Status = StepExecutionStatus.Succes, FinishedAt = DateTime.UtcNow
        });
        var original = new JobExecutionRequest
        {
            JobId = job.Id, ExecutionId = execution.Id, Status = JobRequestStatus.NeedsReview
        };
        await fixture.SeedAsync(job, execution, original);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var persistedStep = await db.JobSteps.SingleAsync();
            persistedStep.ConfigJson = """{"changed":true}""";
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Queue.RetryFromCheckpointAsync(original.Id, "operator"));

        Assert.Contains("définition du job a changé", error.Message);
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Single(await verify.JobExecutionRequests.ToListAsync());
        Assert.Equal(JobRequestStatus.NeedsReview,
            (await verify.JobExecutionRequests.SingleAsync()).Status);
    }}

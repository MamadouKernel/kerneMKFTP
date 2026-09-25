using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Engine;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Workflow;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KernelMK.Tests.Engine;

public class JobRunnerTests
{
    private sealed class Executor(Func<StepExecutionContext, Task<StepExecutionResult>> run) : IStepExecutor
    {
        public IReadOnlyCollection<StepType> SupportedTypes => new[] { StepType.CommandeSysteme };
        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) => run(context);
    }

    private sealed class FailOnceInterceptor : SaveChangesInterceptor
    {
        public bool FailNext { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("Échec DB simulé"); }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public ServiceProvider Services { get; }
        public FailOnceInterceptor Interceptor { get; } = new();

        public Fixture(IStepExecutor executor)
        {
            Connection.Open();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<KernelMK.Data.Security.CredentialProtector>();
            services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(Connection).AddInterceptors(Interceptor));
            services.AddKernelMKEngine(new ConfigurationBuilder().Build());
            services.AddSingleton(executor);
            services.AddSingleton<IStepExecutor>(executor);
            Services = services.BuildServiceProvider();
            using var db = Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
            db.Database.EnsureCreated();
        }

        public async Task Seed(Job job)
        {
            await using var db = await Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private static Job CreateJob() => new()
    {
        Name = "Test",
        Steps = new() { new JobStep { Name = "Étape", Type = StepType.CommandeSysteme, Order = 1 } }
    };

    [Fact]
    public async Task InitialSaveFailureReleasesConcurrencySlot()
    {
        await using var fixture = new Fixture(new Executor(_ => Task.FromResult(StepExecutionResult.Ok())));
        var job = CreateJob();
        await fixture.Seed(job);
        fixture.Interceptor.FailNext = true;
        await using var scope = fixture.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<IJobRunner>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(job.Id, "test"));
        var next = await runner.RunAsync(job.Id, "test");
        Assert.Equal(JobStatus.Succes, next.Status);
    }

    [Fact]
    public async Task CancellationPersistsTerminalStepAndExecution()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(new Executor(async context =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return StepExecutionResult.Ok();
        }));
        var job = CreateJob();
        await fixture.Seed(job);
        await using var scope = fixture.Services.CreateAsyncScope();
        var run = scope.ServiceProvider.GetRequiredService<IJobRunner>().RunAsync(job.Id, "test");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Services.GetRequiredService<IJobExecutionCoordinator>().TryStopJob(job.Id));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JobStatus.Annule, result.Status);

        await using var db = await fixture.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var log = await db.StepExecutionLogs.SingleAsync();
        Assert.Equal(StepExecutionStatus.Annule, log.Status);
        Assert.NotNull(log.FinishedAt);
        Assert.False(fixture.Services.GetRequiredService<IJobExecutionCoordinator>().IsJobRunning(job.Id));
    }

    [Fact]
    public async Task InvalidWorkflowBranchFailsInsteadOfReportingSuccess()
    {
        await using var fixture = new Fixture(new Executor(_ => Task.FromResult(StepExecutionResult.Ok())));
        var job = CreateJob();
        job.Steps[0].OnSuccessGoToOrder = 99;
        await fixture.Seed(job);
        await using var scope = fixture.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IJobRunner>().RunAsync(job.Id, "test");
        Assert.Equal(JobStatus.Echec, result.Status);
    }

    [Fact]
    public async Task JobTimeoutAlsoInterruptsRetryDelay()
    {
        await using var fixture = new Fixture(new Executor(_ => Task.FromResult(StepExecutionResult.Fail("retry"))));
        var job = CreateJob();
        job.TimeoutSeconds = 1;
        job.MaxRetries = 2;
        job.RetryDelaySeconds = 60;
        await fixture.Seed(job);
        await using var scope = fixture.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IJobRunner>().RunAsync(job.Id, "test").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JobStatus.Echec, result.Status);
        Assert.StartsWith("Timeout", result.Message);
    }
}

using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Engine;
using KernelMK.Engine.Execution;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KernelMK.Tests.Engine;

public class StepTimeoutRegressionTests
{
    private sealed class Executor(bool returnsSuccess) : IStepExecutor
    {
        public IReadOnlyCollection<StepType> SupportedTypes => new[] { StepType.CommandeSysteme };

        public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
            return returnsSuccess ? StepExecutionResult.Ok() : StepExecutionResult.Fail("Client canceled");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecutorCannotHideExpiredStepTimeout(bool returnsSuccess)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        collection.AddSingleton<KernelMK.Data.Security.CredentialProtector>();
        collection.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connection));
        collection.AddKernelMKEngine(new ConfigurationBuilder().Build());
        collection.AddSingleton<IStepExecutor>(new Executor(returnsSuccess));
        await using var services = collection.BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var job = new Job
        {
            Name = "Step timeout test", TimeoutSeconds = 30,
            Steps = new() { new JobStep { Name = "Slow client", Type = StepType.CommandeSysteme, Order = 1, TimeoutSeconds = 1 } }
        };
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        }
        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IJobRunner>()
            .RunAsync(job.Id, "test").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(JobStatus.Echec, result.Status);
        await using var verify = await factory.CreateDbContextAsync();
        var stepLog = await verify.StepExecutionLogs.SingleAsync();
        Assert.Equal(StepExecutionStatus.Timeout, stepLog.Status);
        Assert.Contains("Timeout de l'étape", stepLog.ErrorOutput);
    }
}

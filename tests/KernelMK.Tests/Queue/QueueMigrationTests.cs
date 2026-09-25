using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace KernelMK.Tests.Queue;

public sealed class QueueMigrationTests
{
    private const string PreviousMigration = "20260916034053_AddCredentialOAuth2Fields";

    [Fact]
    public async Task UpgradePreservesExistingJobsAndHistoryAndMatchesTheCurrentModel()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        var jobId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        // Seed through the historical schema. Using current EF entities before the upgrade would include
        // columns which correctly do not exist yet and would make this migration test invalid.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Jobs
                (Id, Name, Criticite, Enabled, LastStatus, MaxRetries, RetryDelaySeconds, TimeoutSeconds,
                 AllowConcurrentExecution, ExecutionAccount, CreatedAt)
            VALUES
                ({jobId}, {"Existing workflow"}, {0}, {true}, {(int)JobStatus.Succes}, {0}, {15}, {600},
                 {false}, {""}, {now})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO JobExecutions
                (Id, JobId, StartedAt, Status, TriggeredBy, AttemptNumber)
            VALUES
                ({executionId}, {jobId}, {now}, {(int)JobStatus.Succes}, {"migration-test"}, {1})
            """);

        await db.Database.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(jobId, (await db.Jobs.SingleAsync()).Id);
        Assert.Equal(executionId, (await db.JobExecutions.SingleAsync()).Id);
        var request = new JobExecutionRequest { JobId = jobId, RetryOfRequestId = Guid.NewGuid() };
        db.JobExecutionRequests.Add(request);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var persisted = await db.JobExecutionRequests.SingleAsync();
        Assert.Equal(request.Id, persisted.Id);
        Assert.Equal(JobRequestStatus.Pending, persisted.Status);
        Assert.Equal("[]", persisted.AncestorJobIdsJson);
        Assert.Equal(request.RetryOfRequestId, persisted.RetryOfRequestId);
        Assert.Equal("[]", persisted.ResumeCompletedStepIdsJson);
    }

    [Fact]
    public async Task IdempotencyKeyRejectsDuplicatesWhileAllowingMultipleUnkeyedRequests()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        var job = new Job { Name = "Idempotent workflow" };
        db.Jobs.Add(job);
        db.JobExecutionRequests.AddRange(
            new JobExecutionRequest { JobId = job.Id },
            new JobExecutionRequest { JobId = job.Id },
            new JobExecutionRequest { JobId = job.Id, IdempotencyKey = "trigger:one" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.JobExecutionRequests.Add(new JobExecutionRequest { JobId = job.Id, IdempotencyKey = "trigger:one" });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var sqliteError = Assert.IsType<SqliteException>(error.InnerException);
        Assert.Equal(19, sqliteError.SqliteErrorCode);
        Assert.Equal(2067, sqliteError.SqliteExtendedErrorCode);
        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.JobExecutionRequests.CountAsync());
    }

    [Fact]
    public async Task PurgingAnExecutionKeepsTheRequestAndItsIdempotencyRecord()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        var job = new Job { Name = "Archived workflow" };
        var execution = new JobExecution { JobId = job.Id, Status = JobStatus.Succes };
        var request = new JobExecutionRequest
        {
            JobId = job.Id,
            ExecutionId = execution.Id,
            Status = JobRequestStatus.Succeeded,
            IdempotencyKey = "archive:one"
        };
        db.Jobs.Add(job);
        db.JobExecutions.Add(execution);
        db.JobExecutionRequests.Add(request);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Archive retention deletes history directly; the database must preserve the request.
        await db.JobExecutions.Where(x => x.Id == execution.Id).ExecuteDeleteAsync();

        var persisted = await db.JobExecutionRequests.SingleAsync();
        Assert.Equal(request.Id, persisted.Id);
        Assert.Null(persisted.ExecutionId);
        Assert.Equal(JobRequestStatus.Succeeded, persisted.Status);
        Assert.Equal("archive:one", persisted.IdempotencyKey);
    }

    [Fact]
    public async Task DeletingAJobCascadesRequestsIncludingThoseLinkedToHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        var job = new Job { Name = "Deleted workflow" };
        var execution = new JobExecution { JobId = job.Id, Status = JobStatus.Succes };
        db.Jobs.Add(job);
        db.JobExecutions.Add(execution);
        db.JobExecutionRequests.AddRange(
            new JobExecutionRequest { JobId = job.Id },
            new JobExecutionRequest { JobId = job.Id, ExecutionId = execution.Id, Status = JobRequestStatus.Succeeded });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await db.Jobs.Where(x => x.Id == job.Id).ExecuteDeleteAsync();

        Assert.Empty(await db.JobExecutionRequests.ToListAsync());
        Assert.Empty(await db.JobExecutions.ToListAsync());
    }

    private static AppDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
}

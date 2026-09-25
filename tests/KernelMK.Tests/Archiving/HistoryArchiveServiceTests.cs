using System.IO.Compression;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Engine.Archiving;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KernelMK.Tests.Archiving;

public sealed class HistoryArchiveServiceTests
{
    [Fact]
    public async Task CompletedOldExecutionsAreCompressedVerifiedThenPurged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kernelmk-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "history.db");
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync();
        var factory = new Factory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var job = new Job { Name = "Archive test" };
            var old = new JobExecution { JobId = job.Id, Status = JobStatus.Succes, StartedAt = DateTime.UtcNow.AddDays(-100), FinishedAt = DateTime.UtcNow.AddDays(-100), Message = "old" };
            old.StepLogs.Add(new StepExecutionLog { JobExecutionId = old.Id, JobStepId = Guid.NewGuid(), StepName = "old step", Status = StepExecutionStatus.Succes, StartedAt = old.StartedAt, FinishedAt = old.FinishedAt, Output = "kept in archive" });
            var recent = new JobExecution { JobId = job.Id, Status = JobStatus.Succes, StartedAt = DateTime.UtcNow.AddDays(-1), FinishedAt = DateTime.UtcNow.AddDays(-1) };
            db.Jobs.Add(job);
            db.JobExecutions.AddRange(old, recent);
            await db.SaveChangesAsync();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HistoryArchive:Enabled"] = "true",
            ["HistoryArchive:Directory"] = Path.Combine(directory, "archive"),
            ["HistoryArchive:RetentionDays"] = "90",
            ["HistoryArchive:BatchSize"] = "100"
        }).Build();
        var service = new HistoryArchiveService(factory, config, NullLogger<HistoryArchiveService>.Instance);
        var result = await service.ArchiveOnceAsync();
        Assert.Equal(1, result.ExecutionsArchived);
        Assert.Equal(1, result.StepLogsArchived);
        Assert.NotNull(result.ArchivePath);
        Assert.True(File.Exists(result.ArchivePath!));
        Assert.True(File.Exists(result.ArchivePath! + ".sha256"));
        await using (var input = File.OpenRead(result.ArchivePath!))
        await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip))
            Assert.Contains("kept in archive", await reader.ReadToEndAsync());
        await using (var verify = await factory.CreateDbContextAsync())
        {
            Assert.Single(await verify.JobExecutions.ToListAsync());
            Assert.Empty(await verify.StepExecutionLogs.ToListAsync());
        }
        await connection.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppDbContext(options));
    }
}

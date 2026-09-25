using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Engine.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KernelMK.Tests.Queue;

public sealed class JobVersionServiceTests
{
    [Fact]
    public async Task CapturesImmutableCompleteDefinitionsWithSequentialNumbers()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var dependencyTarget = new Job { Name = "Parent" };
        var job = new Job
        {
            Name = "Versioned job",
            Steps = [new JobStep { Order = 1, Name = "Transfer", Type = StepType.TransfertSftp, ConfigJson = """{"remote":"/out"}""" }],
            Triggers = [new JobTrigger { Type = TriggerType.Api, WebhookToken = "local-token" }],
            Dependencies = [new JobDependency { DependsOnJobId = dependencyTarget.Id }],
            NotificationRules = [new NotificationRule { Event = NotificationEvent.Echec, RecipientsCsv = "ops@example.test" }]
        };
        db.AddRange(dependencyTarget, job);
        await db.SaveChangesAsync();
        var service = new JobVersionService();

        var first = await service.CaptureAsync(db, job, "alice", "Création");
        await db.SaveChangesAsync();
        var originalJson = first.DefinitionJson;
        job.Name = "Versioned job updated";
        job.Steps[0].ConfigJson = """{"remote":"/changed"}""";
        var second = await service.CaptureAsync(db, job, "bob", "Modification");
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var versions = await db.JobDefinitionVersions.OrderBy(v => v.VersionNumber).ToListAsync();
        Assert.Equal([1, 2], versions.Select(v => v.VersionNumber));
        Assert.Equal(originalJson, versions[0].DefinitionJson);
        Assert.Contains("/out", versions[0].DefinitionJson);
        Assert.DoesNotContain("/changed", versions[0].DefinitionJson);
        Assert.DoesNotContain("local-token", versions[0].DefinitionJson);
        Assert.Contains("/changed", versions[1].DefinitionJson);
        Assert.NotEqual(versions[0].DefinitionHash, versions[1].DefinitionHash);
        Assert.All(versions, version => Assert.Equal(64, version.DefinitionHash.Length));
    }

    [Fact]
    public async Task TransactionRollbackRemovesBothJobChangeAndVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kernelmk-version-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=True").Options;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Jobs.Add(new Job { Name = "Before" });
                await setup.SaveChangesAsync();
            }
            await using (var db = new AppDbContext(options))
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                var job = await db.Jobs.Include(j => j.Steps).Include(j => j.Triggers)
                    .Include(j => j.Dependencies).Include(j => j.NotificationRules).SingleAsync();
                job.Name = "After";
                await new JobVersionService().CaptureAsync(db, job, "test", "Uncommitted");
                await db.SaveChangesAsync();
                await transaction.RollbackAsync();
            }
            await using var verify = new AppDbContext(options);
            Assert.Equal("Before", (await verify.Jobs.SingleAsync()).Name);
            Assert.Empty(await verify.JobDefinitionVersions.ToListAsync());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public async Task RestoreReplacesTheDefinitionAndCreatesANewImmutableVersion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var originalStep = new JobStep { Name = "Original step", Order = 1, Type = StepType.CommandeSysteme };
        var trigger = new JobTrigger { Type = TriggerType.Api, WebhookToken = "old-revoked-token" };
        var job = new Job { Name = "Original name", TimeoutSeconds = 120, Steps = [originalStep], Triggers = [trigger] };
        db.Add(job);
        await db.SaveChangesAsync();
        var service = new JobVersionService();
        var first = await service.CaptureAsync(db, job, "alice", "Original");
        await db.SaveChangesAsync();

        job.Name = "Changed name";
        job.TimeoutSeconds = 999;
        trigger.WebhookToken = "current-valid-token";
        job.Steps.Add(new JobStep { Name = "Added later", Order = 2, Type = StepType.EmailSmtp });
        await db.SaveChangesAsync();
        await service.CaptureAsync(db, job, "bob", "Changed");
        await db.SaveChangesAsync();

        await using var transaction = await db.Database.BeginTransactionAsync();
        var restored = await service.RestoreAsync(db, job.Id, first.Id, "operator");
        await transaction.CommitAsync();

        db.ChangeTracker.Clear();
        var persisted = await db.Jobs.Include(j => j.Steps).Include(j => j.Triggers).SingleAsync();
        Assert.Equal("Original name", persisted.Name);
        Assert.Equal(120, persisted.TimeoutSeconds);
        Assert.Single(persisted.Steps);
        Assert.Equal(originalStep.Id, persisted.Steps[0].Id);
        Assert.Equal("Original step", persisted.Steps[0].Name);
        Assert.Equal("current-valid-token", Assert.Single(persisted.Triggers).WebhookToken);
        Assert.Equal(3, restored.VersionNumber);
        Assert.Equal("Restauration de la version 1", restored.ChangeSummary);
        Assert.Equal(3, await db.JobDefinitionVersions.CountAsync());
        Assert.Equal(first.DefinitionHash, restored.DefinitionHash);
    }}

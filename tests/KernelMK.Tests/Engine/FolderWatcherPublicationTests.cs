using System.Reflection;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Queue;
using KernelMK.Engine.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KernelMK.Tests.Engine;

public sealed class FolderWatcherPublicationTests
{
    [Theory]
    [InlineData(FolderWatchEventType.Arrivee, WatcherChangeTypes.Created, ".kernelmk-transfer-new.part", "*", false)]
    [InlineData(FolderWatchEventType.Arrivee, WatcherChangeTypes.Renamed, ".KERNELMK-TRANSFER-new.part", "*", false)]
    [InlineData(FolderWatchEventType.Modification, WatcherChangeTypes.Changed, ".kernelmk-transfer-new.part", "*", false)]
    [InlineData(FolderWatchEventType.Suppression, WatcherChangeTypes.Deleted, ".kernelmk-transfer-new.part", "*", false)]
    [InlineData(FolderWatchEventType.Arrivee, WatcherChangeTypes.Renamed, "message.edi", "*.edi", true)]
    [InlineData(FolderWatchEventType.Arrivee, WatcherChangeTypes.Renamed, "message.tmp", "*.edi", false)]
    [InlineData(FolderWatchEventType.Arrivee, WatcherChangeTypes.Created, "message.edi", "*.edi", true)]
    [InlineData(FolderWatchEventType.Modification, WatcherChangeTypes.Renamed, "message.edi", "*.edi", false)]
    [InlineData(FolderWatchEventType.Suppression, WatcherChangeTypes.Renamed, "message.edi", "*.edi", false)]
    [InlineData(FolderWatchEventType.Modification, WatcherChangeTypes.Changed, "message.edi", "*.edi", true)]
    [InlineData(FolderWatchEventType.Suppression, WatcherChangeTypes.Deleted, "message.edi", "*.edi", true)]
    public void PublishedFileEventUsesFinalNameAndConfiguredEvent(FolderWatchEventType expected,
        WatcherChangeTypes change, string name, string filter, bool accepted)
    {
        var matcher = typeof(FolderWatcherService).GetMethod("MatchesEvent", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(accepted, (bool)matcher.Invoke(null, new object[] { expected, change, name, filter })!);
    }

    [Fact]
    public async Task AtomicPublicationEnqueuesArrivalThroughRealFileSystemWatcher()
    {
        var directory = Directory.CreateTempSubdirectory("kmk-watcher-publication-").FullName;
        var incoming = Directory.CreateDirectory(Path.Combine(directory, "incoming")).FullName;
        var database = Path.Combine(directory, "state.sqlite");
        var collection = new ServiceCollection();
        collection.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()));
        collection.AddScoped<JobQueueService>();
        await using var services = collection.BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var watcher = new FolderWatcherService(factory, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FolderWatcherService>.Instance);
        var job = new Job
        {
            Name = "Published EDI", Enabled = true,
            Triggers = new()
            {
                new JobTrigger
                {
                    Type = TriggerType.EvenementDossier, FolderPath = incoming,
                    FolderFilter = "*.edi", FolderWatchEvent = FolderWatchEventType.Arrivee
                }
            }
        };
        try
        {
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.Jobs.Add(job);
                await db.SaveChangesAsync();
            }
            // Refresh synchronously in the test so the watcher is subscribed before publishing.
            var refresh = typeof(FolderWatcherService).GetMethod("RefreshWatchersAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)refresh.Invoke(watcher, new object[] { CancellationToken.None })!;

            var destination = Path.Combine(incoming, "message.edi");
            await TransferFilePublisher.PublishAsync(destination,
                (temporary, ct) => File.WriteAllTextAsync(temporary, "complete EDI message", ct), CancellationToken.None);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            JobExecutionRequest? request;
            do
            {
                await using var db = await factory.CreateDbContextAsync(deadline.Token);
                request = await db.JobExecutionRequests.AsNoTracking().SingleOrDefaultAsync(deadline.Token);
                if (request is null) await Task.Delay(25, deadline.Token);
            } while (request is null);

            Assert.Equal(job.Id, request.JobId);
            Assert.Equal(JobRequestStatus.Pending, request.Status);
            Assert.Contains("Renamed", request.TriggeredBy);
            Assert.Equal("complete EDI message", await File.ReadAllTextAsync(destination));
            Assert.Single(Directory.GetFiles(incoming));
        }
        finally
        {
            // The service's refresh was invoked without starting its background loop, so release
            // its native watchers explicitly before removing this test's temporary directory.
            var registered = (Dictionary<Guid, FileSystemWatcher>)typeof(FolderWatcherService)
                .GetField("_watchers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(watcher)!;
            foreach (var nativeWatcher in registered.Values) nativeWatcher.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}

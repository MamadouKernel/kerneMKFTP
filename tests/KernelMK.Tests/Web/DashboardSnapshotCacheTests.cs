using System.Threading.Channels;
using KernelMK.Web.Services;

namespace KernelMK.Tests.Web;

public sealed class DashboardSnapshotCacheTests
{
        [Fact]
    public void StatisticsExposeConsistentRequestAndReuseTotals()
    {
        var statistics = new DashboardCacheStatistics(11, 8, 8, 0, 0, 7.7, 1, 3, 15, null, null);

        Assert.Equal(19, statistics.Requests);
        Assert.Equal(11, statistics.AvoidedLoads);
        Assert.Equal(57.9, statistics.ReuseRate, 1);
    }
[Fact]
    public async Task ConcurrentReadersShareOneLoadAndCallerCancellationDoesNotCancelIt()
    {
        var source = new ControlledSource();
        var cache = new DashboardSnapshotCache(source, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var cancelled = cache.GetAsync(cancellationToken: cancellation.Token);
        var survivor = cache.GetAsync();
        var load = await source.NextAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(load.Token.IsCancellationRequested);
        var snapshot = new DashboardSnapshot { TotalJobs = 2 };
        load.Completion.SetResult(snapshot);
        Assert.Same(snapshot, await survivor.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(snapshot, await cache.GetAsync());
        Assert.Equal(1, source.LoadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidationDuringLoadRefreshesAllReadersBeforeReturning(bool obsoleteLoadFails)
    {
        var source = new ControlledSource();
        var cache = new DashboardSnapshotCache(source, TimeProvider.System);
        var originalReader = cache.GetAsync();
        var obsolete = await source.NextAsync();
        cache.Invalidate();
        var newReader = cache.GetAsync();
        Assert.Equal(1, source.LoadCount);

        if (obsoleteLoadFails)
            obsolete.Completion.SetException(new InvalidOperationException("Obsolete data source failure"));
        else
            obsolete.Completion.SetResult(new DashboardSnapshot { RunningCount = 10 });

        var replacement = await source.NextAsync();
        Assert.False(originalReader.IsCompleted);
        Assert.False(newReader.IsCompleted);
        var current = new DashboardSnapshot { RunningCount = 0 };
        replacement.Completion.SetResult(current);
        Assert.Same(current, await originalReader.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(current, await newReader.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(current, await cache.GetAsync());
        Assert.Equal(2, source.LoadCount);
        Assert.Equal(1, cache.GetStatistics().Entries);
    }

    [Fact]
    public async Task ExpiredSnapshotIsNotServedWhenRefreshFailsAndNextRequestRetries()
    {
        var clock = new TestTimeProvider();
        var source = new ControlledSource();
        var cache = new DashboardSnapshotCache(source, clock);
        var first = cache.GetAsync();
        (await source.NextAsync()).Completion.SetResult(new DashboardSnapshot { TotalJobs = 1 });
        await first;
        clock.Advance(DashboardSnapshotCache.TimeToLive);
        var failed = cache.GetAsync();
        (await source.NextAsync()).Completion.SetException(new InvalidOperationException("Database unavailable"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        Assert.Equal(0, cache.GetStatistics().Entries);

        var retry = cache.GetAsync();
        var current = new DashboardSnapshot { TotalJobs = 2 };
        (await source.NextAsync()).Completion.SetResult(current);
        Assert.Same(current, await retry);
        Assert.Equal(3, source.LoadCount);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed record PendingLoad(TaskCompletionSource<DashboardSnapshot> Completion, CancellationToken Token);

    private sealed class ControlledSource : IDashboardSnapshotSource
    {
        private readonly Channel<PendingLoad> _loads = Channel.CreateUnbounded<PendingLoad>();
        private int _loadCount;
        public int LoadCount => Volatile.Read(ref _loadCount);

        public Task<DashboardSnapshot> LoadAsync(int periodHours, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref _loadCount);
            _loads.Writer.TryWrite(new(completion, cancellationToken));
            return completion.Task;
        }

        public async Task<PendingLoad> NextAsync() =>
            await _loads.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}

using KernelMK.Data;
using KernelMK.Engine.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KernelMK.Tests.Engine;

public class DbWriteQueueTests
{
    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);
    }

    [Fact]
    public void SaturationIsCountedInsteadOfSilentlyDroppingWrites()
    {
        using var queue = new DbWriteQueueService(new Factory(), NullLogger<DbWriteQueueService>.Instance);
        for (var index = 0; index < 5001; index++) queue.Enqueue((_, _) => Task.CompletedTask);
        Assert.Equal(1, queue.FailedWriteCount);
    }

    [Fact]
    public async Task GracefulStopDrainsAcceptedWrites()
    {
        using var queue = new DbWriteQueueService(new Factory(), NullLogger<DbWriteQueueService>.Instance);
        var writes = 0;
        for (var index = 0; index < 25; index++)
            queue.Enqueue(async (_, ct) => { await Task.Delay(2, ct); Interlocked.Increment(ref writes); });
        await queue.StartAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.StopAsync(deadline.Token);
        Assert.Equal(25, writes);
        Assert.Equal(0, queue.FailedWriteCount);
        queue.Enqueue((_, _) => Task.CompletedTask);
        Assert.Equal(1, queue.FailedWriteCount);
    }
}

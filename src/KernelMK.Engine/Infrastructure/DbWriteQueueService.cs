using System.Threading.Channels;
using KernelMK.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Infrastructure;

/// <summary>Écritures auxiliaires en mémoire, bornées et sérialisées. Ce n'est pas une file durable.</summary>
public interface IDbWriteQueue
{
    void Enqueue(Func<AppDbContext, CancellationToken, Task> work);
    int FailedWriteCount { get; }
}

public class DbWriteQueueService : BackgroundService, IDbWriteQueue
{
    private const int Capacity = 5000;
    private readonly Channel<Func<AppDbContext, CancellationToken, Task>> _channel =
        Channel.CreateBounded<Func<AppDbContext, CancellationToken, Task>>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // Avec DropWrite, TryWrite retourne true même pour une écriture jetée.
            // Wait + TryWrite reste non bloquant et permet de comptabiliser les refus.
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<DbWriteQueueService> _logger;
    private int _failedWriteCount;

    public int FailedWriteCount => Volatile.Read(ref _failedWriteCount);

    public DbWriteQueueService(IDbContextFactory<AppDbContext> dbFactory, ILogger<DbWriteQueueService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public void Enqueue(Func<AppDbContext, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!_channel.Writer.TryWrite(work))
        {
            Interlocked.Increment(ref _failedWriteCount);
            _logger.LogWarning("File d'écritures DB saturée ({Capacity}) ou fermée : écriture refusée.", Capacity);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);
                    await work(db, stoppingToken);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failedWriteCount);
                    _logger.LogError(ex, "Échec d'une écriture auxiliaire en file d'attente.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _channel.Writer.TryComplete();
            var abandoned = 0;
            while (_channel.Reader.TryRead(out _)) abandoned++;
            if (abandoned > 0)
            {
                Interlocked.Add(ref _failedWriteCount, abandoned);
                _logger.LogWarning("{Count} écritures abandonnées après expiration du délai d'arrêt.", abandoned);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Ferme l'entrée et laisse le worker terminer avant de lui transmettre l'arrêt.
        _channel.Writer.TryComplete();
        try
        {
            if (ExecuteTask is { } worker)
                await worker.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            await base.StopAsync(cancellationToken);
        }
    }
}

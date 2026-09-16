using System.Threading.Channels;
using KernelMK.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Infrastructure;

/// <summary>
/// File d'attente d'écritures en base, vidée séquentiellement par un unique worker en arrière-plan
/// (<see cref="DbWriteQueueService"/>). But : sous SQLite, un seul écrivain à la fois peut modifier la base —
/// des écritures concurrentes (audit, notifications proactives...) provoquant chacune leur propre transaction
/// se disputent ce verrou et ralentissent tout le monde, y compris les actions interactives de l'utilisateur
/// (ex. enregistrer un job pendant qu'une entrée d'audit s'écrit ailleurs). En passant par cette file, l'appelant
/// n'attend qu'un empilement en mémoire (quasi instantané) et le worker sérialise les écritures une par une,
/// éliminant la contention.
///
/// Réservé aux écritures "best effort" sans besoin de confirmation immédiate (journal d'audit, alertes
/// proactives) : les écritures qui pilotent la logique métier elle-même (statut d'exécution d'un job, décisions
/// de reprise) restent en écriture directe et synchrone, car leur cohérence immédiate est nécessaire au moteur.
/// </summary>
public interface IDbWriteQueue
{
    void Enqueue(Func<AppDbContext, CancellationToken, Task> work);

    /// <summary>Nombre d'écritures perdues depuis le démarrage (échec d'écriture ou file saturée) — exposé pour
    /// qu'un futur widget de supervision puisse afficher une alerte si ce compteur bouge, au lieu que la perte
    /// ne soit visible que dans les logs applicatifs.</summary>
    int FailedWriteCount { get; }
}

public class DbWriteQueueService : BackgroundService, IDbWriteQueue
{
    // Bornée (et non illimitée) : si la base reste indisponible/lente durablement, la file cesse d'accepter de
    // nouvelles écritures au-delà de cette capacité plutôt que de croître indéfiniment en mémoire (risque de
    // saturation mémoire constaté lors de l'audit de sécurité de cette fonctionnalité).
    private const int Capacity = 5000;

    private readonly Channel<Func<AppDbContext, CancellationToken, Task>> _channel =
        Channel.CreateBounded<Func<AppDbContext, CancellationToken, Task>>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<DbWriteQueueService> _logger;
    private int _failedWriteCount;

    public int FailedWriteCount => _failedWriteCount;

    public DbWriteQueueService(IDbContextFactory<AppDbContext> dbFactory, ILogger<DbWriteQueueService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public void Enqueue(Func<AppDbContext, CancellationToken, Task> work)
    {
        if (!_channel.Writer.TryWrite(work))
        {
            Interlocked.Increment(ref _failedWriteCount);
            _logger.LogWarning("File d'attente d'écriture DB saturée ({Capacity} en attente) ou fermée — écriture ignorée. " +
                "Signe probable d'une base de données bloquée ou très lente depuis un moment.", Capacity);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
                _logger.LogError(ex, "Échec d'une écriture en file d'attente (audit/notification) — l'application continue, seule cette écriture est perdue.");
            }
        }
    }
}

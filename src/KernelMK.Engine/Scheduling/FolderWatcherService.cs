using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Scheduling;

/// <summary>Surveille les dossiers configurés en déclencheur et lance le job à l'arrivée/modification/suppression d'un fichier (section 4.3 "Evénement dossier").</summary>
public class FolderWatcherService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FolderWatcherService> _logger;
    private readonly Dictionary<Guid, FileSystemWatcher> _watchers = new();

    public FolderWatcherService(IDbContextFactory<AppDbContext> dbFactory, IServiceScopeFactory scopeFactory, ILogger<FolderWatcherService> logger)
    {
        _dbFactory = dbFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshWatchersAsync(stoppingToken);
                await Task.Delay(RefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Arrêt normal du service hébergé (arrêt de l'application) : rien à signaler.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du rafraîchissement des surveillances de dossiers.");
            }
        }

        foreach (var watcher in _watchers.Values) watcher.Dispose();
    }

    private async Task RefreshWatchersAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var triggers = await db.JobTriggers
            .Include(t => t.Job)
            .Where(t => t.Enabled && t.Type == TriggerType.EvenementDossier && t.Job!.Enabled && t.FolderPath != null)
            .ToListAsync(ct);

        var activeIds = triggers.Select(t => t.Id).ToHashSet();

        foreach (var staleId in _watchers.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            _watchers[staleId].Dispose();
            _watchers.Remove(staleId);
        }

        foreach (var trigger in triggers)
        {
            if (_watchers.ContainsKey(trigger.Id)) continue;
            if (!Directory.Exists(trigger.FolderPath)) continue;

            var watcher = new FileSystemWatcher(trigger.FolderPath!)
            {
                Filter = trigger.FolderFilter ?? "*.*",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
            };

            var jobId = trigger.JobId;
            var watchEvent = trigger.FolderWatchEvent ?? FolderWatchEventType.Arrivee;

            void Handler(object sender, FileSystemEventArgs e) => OnFileEvent(jobId, watchEvent, e.ChangeType, e.FullPath, trigger.FolderFilter);

            if (watchEvent == FolderWatchEventType.Arrivee)
            {
                watcher.Created += Handler;
                // Transfers are published by renaming a temporary file in this same directory.
                // That produces Renamed, not Created, on the watched filesystem.
                watcher.Renamed += Handler;
            }
            if (watchEvent == FolderWatchEventType.Modification) watcher.Changed += Handler;
            if (watchEvent == FolderWatchEventType.Suppression) watcher.Deleted += Handler;

            _watchers[trigger.Id] = watcher;
            watcher.EnableRaisingEvents = true;
            _logger.LogInformation("Surveillance activée sur {Path} (job {JobId}, événement {Event}).", trigger.FolderPath, jobId, watchEvent);
        }
    }

    private static bool MatchesEvent(FolderWatchEventType expected, WatcherChangeTypes changeType, string path, string? filter)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith(TransferFilePublisher.TemporaryFilePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        // FileSystemWatcher can report a rename when only the OLD name matches its filter.
        // An arrival must match the published (new) name as well.
        if (!FilePatternMatcher.IsMatch(fileName, filter)) return false;
        return expected switch
        {
            FolderWatchEventType.Arrivee => changeType is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed,
            FolderWatchEventType.Modification => changeType == WatcherChangeTypes.Changed,
            FolderWatchEventType.Suppression => changeType == WatcherChangeTypes.Deleted,
            _ => false
        };
    }

    private void OnFileEvent(Guid jobId, FolderWatchEventType expected, WatcherChangeTypes changeType, string path, string? filter)
    {
        if (!MatchesEvent(expected, changeType, path, filter)) return;

        _logger.LogInformation("Événement dossier détecté ({ChangeType}) sur {Path}, déclenchement du job {JobId}.", changeType, path, jobId);
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<JobQueueService>()
                    .EnqueueAsync(jobId, $"Événement dossier ({changeType})");
            }
            catch (Exception error) { _logger.LogError(error, "Impossible de mettre en file le job {JobId} après événement dossier.", jobId); }
        });
    }
}

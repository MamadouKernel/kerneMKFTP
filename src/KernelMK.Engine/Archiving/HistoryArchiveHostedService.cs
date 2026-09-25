using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Archiving;

public sealed class HistoryArchiveHostedService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<HistoryArchiveHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<HistoryArchiveService>().ArchiveOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Échec de l'archivage automatique de l'historique."); }

            var hours = Math.Clamp(configuration.GetValue<int?>("HistoryArchive:RunEveryHours") ?? 24, 1, 168);
            try { await Task.Delay(TimeSpan.FromHours(hours), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

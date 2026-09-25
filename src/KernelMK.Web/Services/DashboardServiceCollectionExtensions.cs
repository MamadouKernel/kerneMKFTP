using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KernelMK.Web.Services;

public static class DashboardServiceCollectionExtensions
{
    public static IServiceCollection AddDashboardSnapshots(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IDashboardSnapshotSource, DashboardSnapshotSource>();
        services.AddSingleton<DashboardSnapshotCache>();
        services.AddSingleton<OrphanedExecutionRecoveryService>();
        return services;
    }
}

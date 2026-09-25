using KernelMK.Engine.Assistant;
using KernelMK.Engine.Archiving;
using KernelMK.Engine.Audit;
using KernelMK.Engine.Backup;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Execution.Executors;
using KernelMK.Engine.Infrastructure;
using KernelMK.Engine.Notifications;
using KernelMK.Engine.Scheduling;
using KernelMK.Engine.Workflow;
using KernelMK.Engine.Queue;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KernelMK.Engine;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKernelMKEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HistoryArchiveOptions>(configuration.GetSection(HistoryArchiveOptions.SectionName));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.AddSingleton<IPostConfigureOptions<SmtpOptions>, SmtpPasswordProtector>();

        services.Configure<VapidOptions>(configuration.GetSection(VapidOptions.SectionName));
        services.AddSingleton<IPostConfigureOptions<VapidOptions>, VapidPrivateKeyProtector>();

        services.AddSingleton<IStepExecutor, ScriptStepExecutor>();
        services.AddSingleton<IStepExecutor, FileOpsStepExecutor>();
        services.AddSingleton<IStepExecutor, TransferStepExecutor>();
        services.AddSingleton<IStepExecutor, DatabaseStepExecutor>();
        services.AddSingleton<IStepExecutor, EmailStepExecutor>();
        services.AddSingleton<IStepExecutor, WebhookStepExecutor>();
        services.AddSingleton<IStepExecutor, ControlStepExecutor>();
        services.AddSingleton<IStepExecutor, EdifactStepExecutor>();
        services.AddSingleton<IStepExecutor, EmailReceptionStepExecutor>();
        services.AddSingleton<IStepExecutor, RobotFrameworkStepExecutor>();
        services.AddSingleton<StepExecutorFactory>();

        // Worker unique qui sérialise les écritures "best effort" (audit, alertes proactives) pour ne pas
        // se disputer le verrou d'écriture SQLite avec les actions interactives des utilisateurs — voir
        // DbWriteQueueService pour le détail. Singleton exposé à la fois comme IDbWriteQueue (les services
        // qui empilent du travail) et comme IHostedService (le worker que l'hôte démarre/arrête).
        services.AddSingleton<DbWriteQueueService>();
        services.AddSingleton<IDbWriteQueue>(sp => sp.GetRequiredService<DbWriteQueueService>());
        services.AddHostedService(sp => sp.GetRequiredService<DbWriteQueueService>());

        services.AddSingleton(new ConcurrencyGate(maxParallelJobs: configuration.GetValue("Engine:MaxParallelJobs", 20)));
        services.AddSingleton<IJobExecutionCoordinator, JobExecutionCoordinator>();
        services.AddSingleton<NotificationThrottleService>();
        services.AddSingleton<Microsoft365TokenProvider>();
        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<AuditService>();
        services.AddScoped<BackupService>();
        services.AddScoped<HistoryArchiveService>();
        services.AddScoped<AssistantService>();
        services.AddScoped<ConnectionTestService>();
        services.AddScoped<RemoteFileBrowserService>();
        services.AddScoped<JobRunner>();
        services.AddScoped<IJobRunner>(sp => sp.GetRequiredService<JobRunner>());
        services.AddScoped<IQueuedJobRunner>(sp => sp.GetRequiredService<JobRunner>());
        services.AddScoped<JobQueueService>();
        services.AddScoped<JobVersionService>();
        services.AddHostedService<JobQueueDispatcher>();

        services.AddHostedService<JobSchedulerService>();
        services.AddHostedService<FolderWatcherService>();
        services.AddHostedService<AccountLifecycleService>();
        services.AddHostedService<HistoryArchiveHostedService>();

        return services;
    }
}

using KernelMK.Engine.Assistant;
using KernelMK.Engine.Audit;
using KernelMK.Engine.Backup;
using KernelMK.Engine.Execution;
using KernelMK.Engine.Execution.Executors;
using KernelMK.Engine.Notifications;
using KernelMK.Engine.Scheduling;
using KernelMK.Engine.Workflow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KernelMK.Engine;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKernelMKEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));

        services.AddSingleton<IStepExecutor, ScriptStepExecutor>();
        services.AddSingleton<IStepExecutor, FileOpsStepExecutor>();
        services.AddSingleton<IStepExecutor, TransferStepExecutor>();
        services.AddSingleton<IStepExecutor, DatabaseStepExecutor>();
        services.AddSingleton<IStepExecutor, EmailStepExecutor>();
        services.AddSingleton<IStepExecutor, WebhookStepExecutor>();
        services.AddSingleton<IStepExecutor, ControlStepExecutor>();
        services.AddSingleton<IStepExecutor, EdifactStepExecutor>();
        services.AddSingleton<StepExecutorFactory>();

        services.AddSingleton(new ConcurrencyGate(maxParallelJobs: configuration.GetValue("Engine:MaxParallelJobs", 20)));
        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<AuditService>();
        services.AddScoped<BackupService>();
        services.AddScoped<AssistantService>();
        services.AddScoped<IJobRunner, JobRunner>();

        services.AddHostedService<JobSchedulerService>();
        services.AddHostedService<FolderWatcherService>();

        return services;
    }
}

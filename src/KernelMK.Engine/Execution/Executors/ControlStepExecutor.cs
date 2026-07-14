using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;
using Microsoft.Extensions.DependencyInjection;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Étapes de contrôle : attente, condition, appel d'un autre job (section 4.2 "Contrôle").</summary>
public class ControlStepExecutor : IStepExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ControlStepExecutor(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public IReadOnlyCollection<StepType> SupportedTypes { get; } = new[]
    {
        StepType.ControleAttente, StepType.ControleCondition, StepType.ControleAppelJob
    };

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var config = JsonSerializer.Deserialize<ControlStepConfig>(context.Step.ConfigJson)
                      ?? throw new InvalidOperationException("Configuration de contrôle invalide.");

        switch (config.Operation)
        {
            case ControlOperation.Attente:
                await Task.Delay(TimeSpan.FromSeconds(config.WaitSeconds), context.CancellationToken);
                return StepExecutionResult.Ok($"Attente de {config.WaitSeconds}s effectuée.");

            case ControlOperation.ConditionFichierPresent:
                if (string.IsNullOrWhiteSpace(config.FilePathToCheck) || !File.Exists(config.FilePathToCheck))
                {
                    return StepExecutionResult.Fail($"Fichier attendu absent : {config.FilePathToCheck}");
                }
                return StepExecutionResult.Ok("Fichier présent, condition remplie.");

            case ControlOperation.ConditionJobPrecedentReussi:
                var lastLog = context.Execution.StepLogs.LastOrDefault();
                var ok = lastLog is null || lastLog.Status == StepExecutionStatus.Succes;
                return ok
                    ? StepExecutionResult.Ok("Étape précédente réussie, condition remplie.")
                    : StepExecutionResult.Fail("Étape précédente en échec, condition non remplie.");

            case ControlOperation.AppelJob:
                if (config.JobIdToCall is null)
                {
                    return StepExecutionResult.Fail("Aucun job cible configuré pour l'appel.");
                }

                if (config.WaitForCompletion)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var jobRunner = scope.ServiceProvider.GetRequiredService<IJobRunner>();
                    var childExecution = await jobRunner.RunAsync(config.JobIdToCall.Value, $"Appel depuis job {context.Job.Name}", context.CancellationToken);
                    return childExecution.Status == JobStatus.Succes
                        ? StepExecutionResult.Ok($"Job appelé {config.JobIdToCall} terminé avec succès.")
                        : StepExecutionResult.Fail($"Job appelé {config.JobIdToCall} en échec.");
                }
                else
                {
                    var jobIdToCall = config.JobIdToCall.Value;
                    var jobName = context.Job.Name;
                    _ = Task.Run(async () =>
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var jobRunner = scope.ServiceProvider.GetRequiredService<IJobRunner>();
                        await jobRunner.RunAsync(jobIdToCall, $"Appel depuis job {jobName}", CancellationToken.None);
                    });
                    return StepExecutionResult.Ok($"Job {config.JobIdToCall} déclenché en asynchrone.");
                }

            default:
                return StepExecutionResult.Fail("Opération de contrôle inconnue.");
        }
    }
}

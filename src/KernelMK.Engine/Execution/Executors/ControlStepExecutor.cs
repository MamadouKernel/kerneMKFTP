using System.Collections.Immutable;
using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Étapes de contrôle : attente, condition, appel d'un autre job (section 4.2 "Contrôle").</summary>
public class ControlStepExecutor : IStepExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ControlStepExecutor> _logger;

    // Suit la chaîne des jobs déjà appelés (par AppelJob) dans la branche d'exécution courante, pour détecter un
    // appel circulaire (Job A appelle B qui rappelle A) avant qu'il ne provoque une récursion illimitée et un
    // crash du worker (StackOverflowException) au lieu d'un échec propre. AsyncLocal traverse naturellement les
    // await successifs ainsi que Task.Run (capturé au moment de l'appel), donc couvre aussi bien le mode
    // synchrone (WaitForCompletion) que le déclenchement asynchrone.
    private static readonly AsyncLocal<ImmutableHashSet<Guid>?> CallChain = new();

    public ControlStepExecutor(IServiceScopeFactory scopeFactory, ILogger<ControlStepExecutor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
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

                var targetJobId = config.JobIdToCall.Value;
                var currentChain = CallChain.Value ?? ImmutableHashSet<Guid>.Empty;
                if (targetJobId == context.Job.Id || currentChain.Contains(targetJobId))
                {
                    return StepExecutionResult.Fail(
                        $"Appel de job refusé : boucle détectée (le job {targetJobId} s'appellerait lui-même, directement ou via une chaîne d'appels précédente).");
                }
                var newChain = currentChain.Add(context.Job.Id);

                if (config.WaitForCompletion)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var jobRunner = scope.ServiceProvider.GetRequiredService<IJobRunner>();
                    CallChain.Value = newChain;
                    try
                    {
                        var childExecution = await jobRunner.RunAsync(targetJobId, $"Appel depuis job {context.Job.Name}", context.CancellationToken);
                        return childExecution.Status == JobStatus.Succes
                            ? StepExecutionResult.Ok($"Job appelé {targetJobId} terminé avec succès.")
                            : StepExecutionResult.Fail($"Job appelé {targetJobId} en échec.");
                    }
                    finally
                    {
                        CallChain.Value = currentChain;
                    }
                }
                else
                {
                    var jobName = context.Job.Name;
                    CallChain.Value = newChain;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var jobRunner = scope.ServiceProvider.GetRequiredService<IJobRunner>();
                            var childExecution = await jobRunner.RunAsync(targetJobId, $"Appel depuis job {jobName}", CancellationToken.None);
                            if (childExecution.Status != JobStatus.Succes)
                            {
                                // Avant ce correctif, un job appelé en asynchrone qui échouait ou plantait était
                                // invisible : l'étape appelante avait déjà rendu "succès" sans savoir ce qui
                                // s'était réellement passé côté job appelé.
                                _logger.LogWarning("Job {TargetJobId} déclenché en asynchrone depuis {JobName} terminé en {Status}.", targetJobId, jobName, childExecution.Status);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Échec du job {TargetJobId} déclenché en asynchrone depuis {JobName}.", targetJobId, jobName);
                        }
                    });
                    CallChain.Value = currentChain;
                    return StepExecutionResult.Ok($"Job {targetJobId} déclenché en asynchrone.");
                }

            default:
                return StepExecutionResult.Fail("Opération de contrôle inconnue.");
        }
    }
}

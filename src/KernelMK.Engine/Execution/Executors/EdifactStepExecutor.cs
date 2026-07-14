using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;
using KernelMK.Engine.Edifact;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Génère ou analyse un message EDIFACT du transport maritime/portuaire (COPARN/CODECO/COARRI/MANIFEST).</summary>
public class EdifactStepExecutor : IStepExecutor
{
    public IReadOnlyCollection<StepType> SupportedTypes { get; } = new[] { StepType.EdifactMessage };

    public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var config = JsonSerializer.Deserialize<EdifactStepConfig>(context.Step.ConfigJson)
                      ?? throw new InvalidOperationException("Configuration EDIFACT invalide.");

        try
        {
            return config.Operation == EdifactOperation.Generer
                ? Task.FromResult(Generate(config))
                : Task.FromResult(Parse(config));
        }
        catch (Exception ex)
        {
            return Task.FromResult(StepExecutionResult.Fail(ex.Message));
        }
    }

    private static StepExecutionResult Generate(EdifactStepConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.OutputPath))
        {
            return StepExecutionResult.Fail("Aucun chemin de sortie (OutputPath) renseigné pour la génération du message EDIFACT.");
        }

        var message = EdifactMessageBuilder.Build(config, DateTime.UtcNow);
        Directory.CreateDirectory(Path.GetDirectoryName(config.OutputPath) is { Length: > 0 } dir ? dir : ".");
        File.WriteAllText(config.OutputPath, message);

        return StepExecutionResult.Ok(
            $"Message {config.MessageType} généré ({config.Containers.Count} conteneur(s)) -> {config.OutputPath}",
            filesProcessedCsv: config.OutputPath);
    }

    private static StepExecutionResult Parse(EdifactStepConfig config)
    {
        string content;
        if (!string.IsNullOrWhiteSpace(config.InputContent))
        {
            content = config.InputContent;
        }
        else if (!string.IsNullOrWhiteSpace(config.InputPath) && File.Exists(config.InputPath))
        {
            content = File.ReadAllText(config.InputPath);
        }
        else
        {
            return StepExecutionResult.Fail("Aucun message EDIFACT à analyser (InputPath introuvable et InputContent vide).");
        }

        var parsed = EdifactMessageParser.Parse(content);
        var json = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });

        if (!string.IsNullOrWhiteSpace(config.ParsedOutputPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(config.ParsedOutputPath) is { Length: > 0 } dir ? dir : ".");
            File.WriteAllText(config.ParsedOutputPath, json);
        }

        var summary = $"Message {parsed.MessageType} analysé : document {parsed.DocumentNumber}, " +
                      $"{parsed.Containers.Count} conteneur(s), {parsed.SegmentCount} segment(s).";

        return StepExecutionResult.Ok(summary + Environment.NewLine + json);
    }
}

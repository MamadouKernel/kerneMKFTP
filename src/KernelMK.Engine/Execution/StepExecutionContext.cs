using KernelMK.Core.Entities;

namespace KernelMK.Engine.Execution;

public class StepExecutionContext
{
    public required Job Job { get; init; }
    public required JobStep Step { get; init; }
    public required JobExecution Execution { get; init; }
    public required CancellationToken CancellationToken { get; init; }
    /// <summary>Identifiants (username/secret déchiffré) résolus pour le credential associé à l'étape, si présent.</summary>
    public (string? Username, string? Secret)? ResolvedCredential { get; init; }
}

public class StepExecutionResult
{
    public bool Success { get; init; }
    public int? ReturnCode { get; init; }
    public string? Output { get; init; }
    public string? ErrorOutput { get; init; }
    public string? FilesProcessedCsv { get; init; }

    public static StepExecutionResult Ok(string? output = null, int returnCode = 0, string? filesProcessedCsv = null) =>
        new() { Success = true, ReturnCode = returnCode, Output = output, FilesProcessedCsv = filesProcessedCsv };

    public static StepExecutionResult Fail(string errorOutput, int? returnCode = null) =>
        new() { Success = false, ReturnCode = returnCode, ErrorOutput = errorOutput };
}

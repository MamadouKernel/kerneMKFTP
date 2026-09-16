using KernelMK.Core.Entities;

namespace KernelMK.Engine.Execution;

public class StepExecutionContext
{
    public required Job Job { get; init; }
    public required JobStep Step { get; init; }
    public required JobExecution Execution { get; init; }
    public required CancellationToken CancellationToken { get; init; }
    /// <summary>Identifiants (username/secret déchiffré, hôte et port éventuels) résolus pour le credential associé à l'étape, si présent.
    /// Pour le SFTP par clé privée SSH, Secret contient la clé PEM déchiffrée et Passphrase sa passphrase éventuelle.
    /// Pour AuthType = OAuth2Microsoft365, Secret contient le client secret déchiffré (pas un mot de passe de boîte
    /// mail) et OAuth2ClientId/OAuth2TenantId identifient l'application Entra ID à utiliser pour acquérir un jeton.</summary>
    public (string? Username, string? Secret, string? Host, int? Port, CredentialAuthType AuthType, string? Passphrase, string? OAuth2ClientId, string? OAuth2TenantId)? ResolvedCredential { get; init; }
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

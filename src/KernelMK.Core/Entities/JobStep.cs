namespace KernelMK.Core.Entities;

public class JobStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Job? Job { get; set; }

    public int Order { get; set; }
    public string Name { get; set; } = string.Empty;
    public StepType Type { get; set; }

    /// <summary>Configuration spécifique au type d'étape, sérialisée en JSON (chemins, requêtes, params FTP/SFTP, etc.).</summary>
    public string ConfigJson { get; set; } = "{}";

    public Guid? CredentialId { get; set; }
    public Credential? Credential { get; set; }

    public int TimeoutSeconds { get; set; } = 600;
    public int MaxRetries { get; set; } = 0;
    public int RetryDelaySeconds { get; set; } = 15;

    public OnErrorAction OnErrorAction { get; set; } = OnErrorAction.Arreter;

    /// <summary>Ordre de l'étape suivante en cas de succès (null = étape suivante naturelle).</summary>
    public int? OnSuccessGoToOrder { get; set; }
    /// <summary>Ordre de l'étape suivante en cas d'échec géré (branche alternative).</summary>
    public int? OnFailureGoToOrder { get; set; }
}

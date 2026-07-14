namespace KernelMK.Core.StepConfigs;

/// <summary>
/// DTOs sérialisés en JSON dans JobStep.ConfigJson. Un DTO par famille de StepType (voir section 4.2 du cahier des charges).
/// </summary>

public class ScriptStepConfig
{
    /// <summary>Chemin du script/exécutable, ou commande à lancer.</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>Arguments de ligne de commande.</summary>
    public string? Arguments { get; set; }
    /// <summary>Contenu inline du script (si renseigné, écrit dans un fichier temporaire avant exécution).</summary>
    public string? InlineScript { get; set; }
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    /// <summary>Codes retour considérés comme un succès (par défaut : 0).</summary>
    public int[] SuccessExitCodes { get; set; } = { 0 };
}

public class FileOpStepConfig
{
    public string SourcePath { get; set; } = string.Empty;
    public string? DestinationPath { get; set; }
    public bool Overwrite { get; set; } = true;
    public bool Recursive { get; set; } = false;
    /// <summary>Filtre de fichiers ; plusieurs motifs séparés par ';' acceptés, ex: *.csv;*.txt;*.xml</summary>
    public string? Filter { get; set; }
    /// <summary>Pour Compresser/Decompresser : format d'archive (zip).</summary>
    public string ArchiveFormat { get; set; } = "zip";
}

public class TransferStepConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string RemotePath { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    /// <summary>true = envoi local -> distant, false = récupération distant -> local.</summary>
    public bool Upload { get; set; } = true;
    public bool UseTls { get; set; } = true;
    public bool ArchiveAfterTransfer { get; set; } = true;
    public string? ArchiveDirectory { get; set; }
    public string? SmbShare { get; set; }

    /// <summary>
    /// Si renseigné, active le mode multi-fichiers : LocalPath et RemotePath sont alors traités comme des
    /// dossiers, et tous les fichiers correspondant au(x) motif(s) (plusieurs motifs séparés par ';', ex:
    /// *.csv;*.txt;*.xml) sont transférés en une seule étape. Si vide/null, comportement mono-fichier historique
    /// (LocalPath/RemotePath désignent directement un fichier).
    /// </summary>
    public string? Filter { get; set; }
}

public class SqlStepConfig
{
    public string ConnectionString { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
    public bool IsStoredProcedure { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 300;
    public string Provider { get; set; } = "Sqlite"; // Sqlite | SqlServer
}

public class EmailStepConfig
{
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool UseTls { get; set; } = true;
    public string From { get; set; } = string.Empty;
    public string ToCsv { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? AttachmentPath { get; set; }
}

public class WebhookStepConfig
{
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";
    public string? BodyJson { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

public enum ControlOperation
{
    Attente,
    ConditionFichierPresent,
    ConditionJobPrecedentReussi,
    AppelJob
}

public class ControlStepConfig
{
    public ControlOperation Operation { get; set; }
    public int WaitSeconds { get; set; }
    public string? FilePathToCheck { get; set; }
    public Guid? JobIdToCall { get; set; }
    public bool WaitForCompletion { get; set; } = true;
}

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
    /// <summary>
    /// Dossier optionnel où une copie de sauvegarde est déposée en plus de l'opération demandée (ex: copie de
    /// l'original avant une suppression ou un déplacement, pour ne jamais perdre de donnée). Si vide, aucune
    /// copie n'est faite. Peut être sur un lecteur différent de la source/destination.
    /// </summary>
    public string? ArchiveDirectory { get; set; }

    /// <summary>
    /// Armateur/partenaire concerné (optionnel) — permet à cette étape de remonter dans le Rapport des flux et
    /// la Cartographie EDI au même titre qu'une étape de transfert SFTP/FTP/SMB. Le sens (envoyé/reçu) est déduit
    /// automatiquement : si DestinationPath est un chemin UNC (\\serveur\partage\...), c'est un envoi vers le
    /// partenaire ; si c'est SourcePath qui est UNC, c'est une réception depuis le partenaire.
    /// </summary>
    public string? Armateur { get; set; }
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
    /// <summary>En mode téléchargement (Upload=false), supprime le fichier distant après récupération réussie pour éviter de le re-télécharger.</summary>
    public bool DeleteRemoteAfterDownload { get; set; } = false;
    public string? SmbShare { get; set; }
    /// <summary>Armateur ou partenaire associé (ex: MSC, CMA CGM, MAERSK, HAPAG-LLOYD, etc.).</summary>
    public string? Armateur { get; set; }

    /// <summary>
    /// Si renseigné, active le mode multi-fichiers : LocalPath et RemotePath sont alors traités comme des
    /// dossiers, et tous les fichiers correspondant au(x) motif(s) (plusieurs motifs séparés par ';', ex:
    /// *.csv;*.txt;*.xml) sont transférés en une seule étape. Si vide/null, comportement mono-fichier historique
    /// (LocalPath/RemotePath désignent directement un fichier).
    /// </summary>
    public string? Filter { get; set; }

    /// <summary>
    /// Identifiant (Credential) à utiliser pour authentifier l'accès à <see cref="LocalPath"/> quand celui-ci
    /// est un partage réseau UNC (\\serveur\partage\...) auquel le compte du service Windows n'a pas accès —
    /// distinct du Credential de l'étape, qui sert à l'authentification côté serveur distant (SFTP/FTP).
    /// Sans valeur, l'identité du processus est utilisée directement (comportement historique).
    /// </summary>
    public Guid? LocalCredentialId { get; set; }
}

public class SqlStepConfig
{
    /// <summary>
    /// Chaîne de connexion complète (mode avancé/legacy). Si un credential de type "Base de données" est
    /// sélectionné sur l'étape ET que Provider = "SqlServer", elle est ignorée : la connexion est construite
    /// automatiquement à partir de l'hôte/port/utilisateur/mot de passe du credential (chiffrés en base) plus
    /// le champ Database ci-dessous — le mot de passe SQL Server n'a alors plus besoin d'être tapé en clair ici.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Nom de la base à utiliser sur le serveur — uniquement utilisé quand un credential fournit host/port/auth (Provider = SqlServer).</summary>
    public string? Database { get; set; }

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

    /// <summary>Armateur ou partenaire destinataire (optionnel) — permet à cet envoi (canal SMTP) de remonter dans la Cartographie EDI comme émission vers ce partenaire.</summary>
    public string? Armateur { get; set; }
}

/// <summary>
/// Configuration d'une étape "Réception Email (IMAP)" : relève une boîte mail et télécharge les pièces jointes
/// correspondant aux filtres (ex. les EDI envoyés par un armateur/partenaire par email plutôt que par SFTP).
/// L'authentification (hôte/port/utilisateur/mot de passe IMAP) provient du Credential associé à l'étape
/// (CredentialType.ImapEmail) — voir StepExecutionContext.ResolvedCredential.
/// </summary>
public class EmailReceptionStepConfig
{
    /// <summary>Dossier de la boîte mail à relever (ex. "INBOX" ou "INBOX/EDI-Entrants").</summary>
    public string MailboxFolder { get; set; } = "INBOX";

    /// <summary>Filtre optionnel sur l'expéditeur (correspondance partielle, insensible à la casse).</summary>
    public string? SenderFilter { get; set; }

    /// <summary>Filtre optionnel sur l'objet du message (correspondance partielle, insensible à la casse).</summary>
    public string? SubjectFilter { get; set; }

    /// <summary>Extensions de pièces jointes à récupérer, séparées par ';' (ex. ".edi;.txt;.xml"). Vide = toutes.</summary>
    public string? AttachmentExtensionsCsv { get; set; } = ".edi;.txt;.xml";

    /// <summary>Dossier local où déposer les pièces jointes téléchargées.</summary>
    public string DownloadPath { get; set; } = string.Empty;

    /// <summary>Marque les emails traités comme lus après téléchargement, pour ne pas les retraiter au prochain passage.</summary>
    public bool MarkAsReadAfterDownload { get; set; } = true;

    /// <summary>Si vrai, supprime l'email de la boîte après téléchargement (sinon il reste, marqué lu).</summary>
    public bool DeleteAfterDownload { get; set; } = false;

    /// <summary>Nombre maximum de messages traités par exécution (protection contre une boîte surchargée).</summary>
    public int MaxMessagesPerRun { get; set; } = 50;

    /// <summary>Armateur ou partenaire expéditeur habituel de cette boîte/filtre (optionnel) — permet à cette relève (canal SMTP) de remonter dans la Cartographie EDI comme réception depuis ce partenaire.</summary>
    public string? Armateur { get; set; }
}

/// <summary>
/// Configuration d'une étape "RPA / Robot Framework" : exécute un fichier .robot (ou .resource) existant via la
/// commande "robot" en ligne de commande — automatisation d'interface (ex. saisie répétitive sur un portail web
/// GUCE/TOS sans API). Le fichier .robot lui-même est écrit hors de kernelMK (RIDE, VS Code + extension Robot
/// Framework...) ; kernelMK se contente de le lancer, de suivre son statut et de conserver le rapport généré.
/// Nécessite Python + le paquet "robotframework" installés sur le serveur exécutant kernelMK.
/// </summary>
public class RobotFrameworkStepConfig
{
    /// <summary>Chemin du fichier .robot (ou du dossier de suite de tests) à exécuter.</summary>
    public string RobotFilePath { get; set; } = string.Empty;

    /// <summary>Dossier de sortie pour report.html/log.html/output.xml (créé si absent). Vide = dossier temporaire.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Variables Robot Framework à injecter, une par ligne au format NOM:valeur (équivalent --variable).</summary>
    public string? VariablesText { get; set; }

    /// <summary>Tags à inclure, séparés par ';' (équivalent --include). Vide = tous.</summary>
    public string? IncludeTagsCsv { get; set; }

    /// <summary>Tags à exclure, séparés par ';' (équivalent --exclude).</summary>
    public string? ExcludeTagsCsv { get; set; }

    /// <summary>Arguments bruts supplémentaires passés tels quels à la commande "robot" (utilisateurs avancés).</summary>
    public string? ExtraArguments { get; set; }

    /// <summary>Chemin de l'exécutable "robot" si non présent dans le PATH système (ex: venv dédié).</summary>
    public string? RobotExecutablePath { get; set; }
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

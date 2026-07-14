namespace KernelMK.Core;

public enum JobStatus
{
    Inactif = 0,
    Actif = 1,
    EnCours = 2,
    Succes = 3,
    Echec = 4,
    Annule = 5,
    EnAttente = 6
}

public enum Criticite
{
    Faible = 0,
    Moyenne = 1,
    Haute = 2,
    Critique = 3
}

public enum TriggerType
{
    Horaire,
    Cron,
    Calendrier,
    EvenementDossier,
    DependanceJob,
    Demarrage,
    Api,
    Manuel
}

public enum FolderWatchEventType
{
    Arrivee,
    Modification,
    Suppression
}

public enum JobDependencyCondition
{
    Succes,
    Echec,
    Fin
}

public enum StepType
{
    CommandeSysteme,
    ScriptPowerShell,
    ScriptPython,
    ScriptBatch,
    ScriptSql,
    FichierCopier,
    FichierDeplacer,
    FichierRenommer,
    FichierSupprimer,
    FichierCompresser,
    FichierDecompresser,
    FichierVerifier,
    TransfertFtp,
    TransfertFtps,
    TransfertSftp,
    TransfertSmb,
    TransfertHttp,
    BaseDeDonneesRequete,
    EmailSmtp,
    Webhook,
    ControleAttente,
    ControleCondition,
    ControleAppelJob,
    EdifactMessage
}

/// <summary>Messages EDIFACT du transport maritime/portuaire pris en charge.</summary>
public enum EdifactMessageType
{
    /// <summary>Container Announcement : avis/réservation de conteneur (armateur → terminal).</summary>
    Coparn,
    /// <summary>Container Gate-in/Gate-out : ordre de mouvement de conteneur au dépôt/terminal.</summary>
    Codeco,
    /// <summary>Container Discharge/Loading Report : rapport des conteneurs déchargés/chargés d'un navire.</summary>
    Coarri,
    /// <summary>Manifeste de cargaison (liste des marchandises à bord, à destination de la douane).</summary>
    Manifest
}

public enum EdifactOperation
{
    Generer,
    Analyser
}

public enum EdifactFullEmptyIndicator
{
    Plein,
    Vide
}

/// <summary>Code de mouvement du conteneur (utilisé pour CODECO et COARRI).</summary>
public enum EdifactMovementCode
{
    EntreeDepot,
    SortieDepot,
    Chargement,
    Dechargement
}

public enum StepExecutionStatus
{
    EnAttente,
    EnCours,
    Succes,
    Echec,
    Ignore,
    Timeout,
    Annule
}

public enum OnErrorAction
{
    Arreter,
    Poursuivre,
    BrancheAlternative
}

public enum NotificationEvent
{
    Succes,
    Echec,
    Timeout,
    FichierAbsent,
    ConnexionImpossible,
    JobDesactive
}

public enum AppRole
{
    Administrateur,
    Superviseur,
    Exploitant,
    Developpeur,
    Auditeur
}

public enum AuditAction
{
    Creation,
    Modification,
    Suppression,
    Activation,
    Desactivation,
    ExecutionManuelle,
    Annulation,
    Connexion,
    ExportRapport,
    Sauvegarde,
    Restauration
}

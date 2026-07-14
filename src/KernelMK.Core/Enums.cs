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
    ControleAppelJob
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

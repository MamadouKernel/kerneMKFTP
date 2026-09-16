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
    /// <summary>Non implémenté : jamais évalué par le scheduler et non sélectionnable dans l'UI (JobEdit.razor).
    /// Le chaînage job-à-job est géré par l'entité JobDependency, indépendante du système de déclencheurs — ne
    /// pas renuméroter cette valeur (stockée en entier en base) sans migration des JobTrigger existants.</summary>
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
    EdifactMessage,
    /// <summary>Relève d'une boîte mail par IMAP : télécharge les pièces jointes (EDI...) correspondant aux filtres, pour un traitement ultérieur (ex. étape EdifactMessage en Analyse).</summary>
    ReceptionEmailImap,
    /// <summary>Exécute un fichier .robot (Robot Framework) via la commande "robot" — automatisation RPA (ex. saisie répétitive sur une interface web GUCE/TOS). Nécessite Python + le paquet robotframework installés sur le serveur.</summary>
    RpaRobotFramework
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
    JobDesactive,
    /// <summary>Déclencheur planifié (Horaire/Cron/Calendrier) lancé avec un retard anormal — signe possible
    /// que le service kernelMK a été interrompu (redémarrage serveur, arrêt du service Windows...) pendant la fenêtre attendue.</summary>
    JobManquant,
    /// <summary>Exécution réussie mais significativement plus longue que la moyenne historique du job —
    /// signe possible de ralentissement réseau ou de lenteur côté serveur distant.</summary>
    AnomalieDuree
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

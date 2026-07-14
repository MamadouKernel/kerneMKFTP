using System.Text;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using Microsoft.EntityFrameworkCore;

namespace KernelMK.Engine.Assistant;

/// <summary>
/// Assistant simple : répond à des questions en langage courant en interrogeant directement
/// les données réelles de la plateforme (jobs, exécutions, logs). Pas d'IA externe : reconnaissance
/// de mots-clés puis requêtes SQL, pour un fonctionnement 100% local et sans clé API.
/// </summary>
public class AssistantService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public AssistantService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<string> AskAsync(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return HelpMessage();
        }

        var q = Normalize(question);

        if (Contains(q, "coparn", "codeco", "coarri", "manifest", "edifact", "cuscar"))
        {
            return ExplainEdifactMessageTypes();
        }

        if (Contains(q, "type de transfert", "types de transfert", "ftp", "sftp", "ftps", "smb", "webhook", "protocole"))
        {
            return ExplainTransferTypes();
        }

        if (Contains(q, "type d'etape", "types d'etape", "quelles etapes", "quels types d'etape"))
        {
            return ExplainStepTypes();
        }

        // --- Questions sur les données réelles (vérifiées AVANT les questions d'usage génériques :
        // un nom de job peut contenir un mot-clé d'usage, ex. "Sauvegarde_Logs" ou "Guide_Verification",
        // et doit alors répondre sur ce job précis plutôt que sur l'aide générique de la fonctionnalité). ---
        if (Contains(q, "pourquoi") || Contains(q, "echec", "echoue", "erreur", "probleme"))
        {
            return await AnswerFailureAsync(q);
        }

        if (Contains(q, "a quoi sert", "objectif", "utilite", "ca sert a quoi", "role du job"))
        {
            return await AnswerJobPurposeAsync(q);
        }

        if (Contains(q, "fichier") && Contains(q, "envoy", "recu", "recept", "transfer", "traite", "combien"))
        {
            return await AnswerFileStatsAsync();
        }

        if (Contains(q, "combien") && Contains(q, "job"))
        {
            return await AnswerJobCountAsync();
        }

        if (Contains(q, "liste", "quels sont les jobs", "montre moi les jobs"))
        {
            return await ListJobsAsync();
        }

        // --- Questions sur l'utilisation de l'application (pas sur les données) ---
        if (Contains(q, "creer un job", "nouveau job", "ajouter un job", "comment faire un job"))
        {
            return UsageHowToCreateJob();
        }

        if (Contains(q, "mot de passe"))
        {
            return UsageChangePassword();
        }

        if (Contains(q, "voir les logs", "consulter les logs", "ou sont les logs", "ou voir l'historique", "consulter l'historique"))
        {
            return UsageWhereAreLogs();
        }

        if (Contains(q, "sauvegarder", "sauvegarde", "restaurer", "restauration", "backup"))
        {
            return UsageHowToBackup();
        }

        if (Contains(q, "ajouter un utilisateur", "creer un utilisateur", "gerer les utilisateurs", "nouveau compte", "nouvel utilisateur"))
        {
            return UsageHowToManageUsers();
        }

        if (Contains(q, "quels sont les roles", "quels roles", "les roles", "droits des roles", "c'est quoi les roles"))
        {
            return UsageExplainRoles();
        }

        if (Contains(q, "declencheur", "declencheurs", "planifier", "planification", "cron", "quand se lance"))
        {
            return UsageExplainTriggers();
        }

        if (Contains(q, "credential", "identifiants", "gerer les mots de passe", "coffre-fort"))
        {
            return UsageHowToCredentials();
        }

        if (Contains(q, "comment utiliser", "comment ca marche", "guide", "tutoriel", "documentation", "mode d'emploi"))
        {
            return UsageGeneralGuide();
        }

        return HelpMessage();
    }

    private async Task<string> AnswerJobCountAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var total = await db.Jobs.CountAsync();
        var active = await db.Jobs.CountAsync(j => j.Enabled);
        var inactive = total - active;
        var running = await db.JobExecutions.CountAsync(e => e.Status == JobStatus.EnCours);

        if (total == 0)
        {
            return "Il n'y a aucun job créé pour le moment. Rends-toi sur la page « Jobs » pour en créer un premier.";
        }

        return $"Il y a {total} job(s) au total : {active} actif(s), {inactive} inactif(s), dont {running} en cours d'exécution en ce moment.";
    }

    private async Task<string> AnswerJobPurposeAsync(string q)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var jobs = await db.Jobs.Include(j => j.Steps).ToListAsync();

        var matched = jobs.FirstOrDefault(j => q.Contains(Normalize(j.Name)));
        if (matched is null)
        {
            if (jobs.Count == 0)
            {
                return "Aucun job n'existe encore, donc rien à expliquer pour l'instant. Crée ton premier job depuis la page « Jobs ».";
            }
            var names = string.Join(", ", jobs.Select(j => j.Name));
            return $"Je n'ai pas trouvé de job correspondant dans ta question. Les jobs existants sont : {names}. " +
                   "Repose la question en citant le nom exact du job.";
        }

        var sb = new StringBuilder();
        sb.Append($"Le job « {matched.Name} » ");
        sb.Append(string.IsNullOrWhiteSpace(matched.Description)
            ? "n'a pas de description renseignée. "
            : $"sert à : {matched.Description}. ");
        sb.Append($"Il est {(matched.Enabled ? "actif" : "inactif")}, de criticité {matched.Criticite}, ");
        sb.Append($"et contient {matched.Steps.Count} étape(s) : {string.Join(" → ", matched.Steps.OrderBy(s => s.Order).Select(s => s.Type.ToString()))}.");
        return sb.ToString();
    }

    private async Task<string> AnswerFailureAsync(string q)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var jobs = await db.Jobs.ToListAsync();
        var matchedJob = jobs.FirstOrDefault(j => q.Contains(Normalize(j.Name)));

        var query = db.JobExecutions.Include(e => e.Job).Include(e => e.StepLogs).Where(e => e.Status == JobStatus.Echec);
        if (matchedJob is not null)
        {
            query = query.Where(e => e.JobId == matchedJob.Id);
        }

        var lastFailure = await query.OrderByDescending(e => e.StartedAt).FirstOrDefaultAsync();

        if (lastFailure is null)
        {
            return matchedJob is not null
                ? $"Bonne nouvelle : le job « {matchedJob.Name} » n'a aucun échec enregistré."
                : "Aucun échec enregistré pour le moment sur l'ensemble des jobs.";
        }

        var failingStep = lastFailure.StepLogs
            .Where(l => l.Status is StepExecutionStatus.Echec or StepExecutionStatus.Timeout)
            .OrderBy(l => l.Order)
            .FirstOrDefault();

        var sb = new StringBuilder();
        sb.Append($"Le job « {lastFailure.Job?.Name} » a échoué le {lastFailure.StartedAt.ToLocalTime():dd/MM/yyyy HH:mm} ");
        sb.Append($"(déclenché par {lastFailure.TriggeredBy}). ");
        if (failingStep is not null)
        {
            var reason = !string.IsNullOrWhiteSpace(failingStep.ErrorOutput)
                ? failingStep.ErrorOutput
                : "aucun détail d'erreur enregistré pour cette étape.";
            sb.Append($"L'étape en cause : « {failingStep.StepName} » ({failingStep.Status}). Raison : {Truncate(reason, 300)}");
        }
        else
        {
            sb.Append($"Message : {lastFailure.Message}");
        }

        return sb.ToString();
    }

    private async Task<string> AnswerFileStatsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var since = DateTime.UtcNow.AddHours(-24);

        var logs = await db.StepExecutionLogs
            .Include(l => l.JobExecution)
            .Where(l => l.JobExecution!.StartedAt >= since && l.FilesProcessedCsv != null && l.FilesProcessedCsv != "")
            .ToListAsync();

        var totalFiles = logs.Sum(l => l.FilesProcessedCsv!.Split(';', StringSplitOptions.RemoveEmptyEntries).Length);

        if (totalFiles == 0)
        {
            return "Aucun fichier n'a été traité (copié, déplacé ou transféré) au cours des dernières 24 heures.";
        }

        return $"{totalFiles} fichier(s) traité(s) au cours des dernières 24 heures, à travers {logs.Count} étape(s) " +
               "de type fichier ou transfert (FTP/SFTP/SMB/copie locale confondus — le détail envoi/réception " +
               "par protocole est visible dans l'historique d'exécution de chaque job).";
    }

    private async Task<string> ListJobsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var jobs = await db.Jobs.OrderBy(j => j.Name).ToListAsync();
        if (jobs.Count == 0) return "Aucun job n'existe encore.";

        var lines = jobs.Select(j => $"- {j.Name} ({(j.Enabled ? "actif" : "inactif")}, {j.LastStatus})");
        return "Jobs existants :\n" + string.Join("\n", lines);
    }

    private static string ExplainTransferTypes() =>
        "Méthodes de transfert de fichiers (section « Transfert » du cahier des charges) :\n" +
        "- FTP : protocole de transfert de fichiers classique, sans chiffrement. À réserver aux réseaux internes de confiance.\n" +
        "- FTPS : FTP avec chiffrement TLS explicite. Comme FTP mais sécurisé, compatible avec les serveurs FTP existants.\n" +
        "- SFTP : transfert de fichiers via SSH (protocole différent de FTP). Chiffré nativement, le plus répandu et recommandé aujourd'hui.\n" +
        "- SMB : copie sur partage réseau Windows (\\\\serveur\\partage). Utilise l'authentification Windows (session déjà " +
        "ouverte/lecteur mappé, ou identifiants dédiés à l'étape).\n" +
        "- Webhook/HTTP : appel d'une URL (API, service tiers) pour envoyer ou déclencher un traitement, plutôt qu'un vrai transfert de fichier.\n" +
        "En résumé : SFTP par défaut si le serveur le permet (sécurisé et standard), FTPS si seul un serveur FTP est disponible, " +
        "SMB pour les partages internes Windows, Webhook pour intégrer une API.\n" +
        "Multi-fichiers : en renseignant le champ Filter (ex. *.csv;*.txt;*.xml), une étape de transfert traite " +
        "RemotePath/LocalPath comme des dossiers et envoie/récupère tous les fichiers correspondants en une seule étape, " +
        "tous formats confondus.";

    private static string ExplainStepTypes() =>
        "Types d'étapes disponibles dans un job :\n" +
        "- Scripts (PowerShell, Python, Batch, Commande système) : exécuter un script ou un programme.\n" +
        "- Fichiers (copier, déplacer, renommer, supprimer, compresser, décompresser, vérifier) : manipuler des fichiers locaux.\n" +
        "- Transfert (FTP, FTPS, SFTP, SMB) : envoyer ou récupérer des fichiers vers/depuis un serveur distant.\n" +
        "- Base de données : exécuter une requête ou procédure SQL.\n" +
        "- Email : envoyer un email via SMTP.\n" +
        "- Webhook : appeler une URL HTTP (API).\n" +
        "- Message EDIFACT : générer ou analyser un message COPARN/CODECO/COARRI/MANIFEST (transport maritime/portuaire).\n" +
        "- Contrôle (attente, condition, appel de job) : piloter le déroulement du workflow (pause, vérification, chaînage vers un autre job).";

    private static string ExplainEdifactMessageTypes() =>
        "Messages EDIFACT du transport maritime/portuaire pris en charge (étape « Message EDIFACT ») :\n" +
        "- COPARN (Container Announcement) : avis/réservation de conteneur, envoyé par l'armateur au terminal ou dépôt.\n" +
        "- CODECO (Container Gate-in/Gate-out) : ordre de mouvement de conteneur à l'entrée/sortie d'un dépôt ou terminal.\n" +
        "- COARRI (Container Discharge/Loading Report) : rapport des conteneurs déchargés/chargés sur un navire lors d'une escale.\n" +
        "- MANIFEST (manifeste de cargaison, message EDIFACT CUSCAR) : liste des marchandises à bord, à destination de la douane.\n" +
        "Une étape de ce type peut soit GÉNÉRER un message (à partir d'une liste de conteneurs saisie dans la configuration), " +
        "soit ANALYSER un message reçu et en extraire les données (numéros de conteneurs, poids, scellés, navire, escale...) " +
        "vers un fichier JSON structuré exploitable par les étapes suivantes du job.\n" +
        "Note : la structure d'enveloppe (UNB/UNH/UNT/UNZ) est pleinement standard ; les codes de qualification détaillés " +
        "(lieux, fonctions de document) peuvent nécessiter un ajustement selon le guide d'implémentation exact de chaque " +
        "partenaire (armateur, terminal, douane).";

    private static string UsageHowToCreateJob() =>
        "Pour créer un job :\n" +
        "1. Va sur la page « Jobs » puis clique sur « + Nouveau job ».\n" +
        "2. Renseigne le nom, la description et la criticité.\n" +
        "3. Dans « Étapes », clique sur « + Ajouter une étape », choisis le type (script, fichier, transfert...) " +
        "puis clique sur « Modèle JSON » pour préremplir la configuration attendue.\n" +
        "4. Dans « Déclencheurs », ajoute une planification (horaire/cron, calendrier, dossier surveillé...) " +
        "si le job ne doit pas être lancé qu'à la main.\n" +
        "5. Clique sur « Enregistrer ». Tu peux ensuite le lancer manuellement avec le bouton « Lancer » " +
        "depuis la liste des jobs pour vérifier qu'il fonctionne.\n" +
        "(Réservé aux rôles Administrateur et Développeur.)";

    private static string UsageChangePassword() =>
        "Pour changer ton mot de passe : clique sur ton nom en bas de la barre latérale, puis « Gérer le compte » " +
        "→ onglet mot de passe. Un administrateur ne peut pas voir ni réinitialiser le mot de passe d'un autre " +
        "utilisateur directement (les mots de passe sont chiffrés) — en cas d'oubli, il faut recréer le compte " +
        "depuis la page « Utilisateurs & rôles ».";

    private static string UsageWhereAreLogs() =>
        "L'historique complet des exécutions (avec les logs détaillés de chaque étape) se trouve sur la page " +
        "« Historique d'exécution ». Clique sur « Détail » pour une exécution afin de voir, étape par étape : " +
        "le statut, la durée, la sortie standard et le message d'erreur exact. Tu peux aussi exporter tout " +
        "l'historique en CSV ou JSON depuis cette page.";

    private static string UsageHowToBackup() =>
        "La sauvegarde se gère depuis la page « Sauvegarde » (réservée à l'Administrateur) :\n" +
        "- « Exporter la configuration » : télécharge tous les jobs (étapes, déclencheurs, notifications) en JSON.\n" +
        "- « Importer » : recharge une configuration exportée précédemment (utile pour dupliquer vers un autre serveur).\n" +
        "- « Sauvegarder maintenant » : copie le fichier de base SQLite complet (jobs + historique + utilisateurs) " +
        "vers un dossier de sauvegarde horodaté sur le serveur.";

    private static string UsageHowToManageUsers() =>
        "La gestion des utilisateurs se fait depuis la page « Utilisateurs & rôles » (réservée à l'Administrateur) : " +
        "remplis email, nom, mot de passe et rôle initial dans le formulaire du haut, puis clique sur « Créer ». " +
        "Tu peux ensuite cocher/décocher les rôles de chaque utilisateur directement dans le tableau, et " +
        "activer/désactiver un compte sans le supprimer.";

    private static string UsageExplainRoles() =>
        "Les 5 rôles disponibles :\n" +
        "- Administrateur : accès complet (configuration, sécurité, sauvegardes, utilisateurs).\n" +
        "- Superviseur : consultation des tableaux de bord, statuts, historiques et alertes (lecture seule).\n" +
        "- Exploitant : lancement manuel des jobs, reprise, consultation des logs.\n" +
        "- Développeur : création et modification des jobs, gestion des credentials.\n" +
        "- Auditeur : lecture seule, export de rapports et consultation du journal d'audit.\n" +
        "Un utilisateur peut cumuler plusieurs rôles. La gestion se fait sur la page « Utilisateurs & rôles ».";

    private static string UsageExplainTriggers() =>
        "Un job peut être déclenché de plusieurs façons (section « Déclencheurs » de l'éditeur de job) :\n" +
        "- Horaire/Cron : à intervalle régulier ou selon une expression cron précise (ex. tous les jours à 22h).\n" +
        "- Calendrier : jours de la semaine choisis, avec exclusion des week-ends/jours fériés.\n" +
        "- Événement dossier : à l'arrivée, la modification ou la suppression d'un fichier dans un dossier surveillé.\n" +
        "- Dépendance : après le succès, l'échec ou la fin d'un autre job (chaînage).\n" +
        "- Démarrage serveur : au lancement de l'application.\n" +
        "- API/Webhook : en appelant l'URL /api/triggers/{jeton} depuis un système externe.\n" +
        "- Manuel uniquement : pas de déclencheur, lancement à la demande via le bouton « Lancer ».";

    private static string UsageHowToCredentials() =>
        "Les identifiants (comptes FTP/SFTP, SMTP, base de données...) se gèrent sur la page « Credentials » " +
        "(réservée à l'Administrateur et au Développeur). Le secret saisi est chiffré immédiatement et n'est " +
        "plus jamais affiché en clair. Une fois créé, sélectionne ce credential dans le champ « Credential » " +
        "de l'étape concernée (transfert, email...) pour qu'il soit utilisé automatiquement à l'exécution.";

    private static string UsageGeneralGuide() =>
        "kernelMK en bref :\n" +
        "- Tableau de bord : vue d'ensemble (jobs actifs, incidents récents, fichiers traités).\n" +
        "- Jobs : créer/modifier/lancer les traitements automatisés.\n" +
        "- Historique d'exécution : logs détaillés de chaque exécution, export CSV/JSON.\n" +
        "- Credentials : coffre-fort des identifiants utilisés par les jobs (Administrateur/Développeur).\n" +
        "- Utilisateurs & rôles : gestion des comptes et des permissions (Administrateur).\n" +
        "- Sauvegarde : export/import de configuration et copie de la base (Administrateur).\n" +
        "- Audit : journal des actions effectuées (Administrateur/Auditeur).\n" +
        "Pose-moi une question plus précise (« comment créer un job ? », « comment fonctionnent les rôles ? »...) " +
        "pour un pas-à-pas détaillé.";

    private static string HelpMessage() =>
        "Je peux répondre à des questions sur tes données :\n" +
        "- « Combien de jobs y a-t-il ? », « À quoi sert le job [nom] ? », « Pourquoi le job [nom] a-t-il échoué ? »\n" +
        "- « Combien de fichiers ont été traités ? », « Liste des jobs »\n" +
        "- « Quels types de transfert / d'étapes existent ? », « À quoi sert un COPARN/CODECO/COARRI/MANIFEST ? »\n" +
        "Et sur l'utilisation de l'application :\n" +
        "- « Comment créer un job ? », « Comment fonctionnent les rôles ? », « Où voir les logs ? »\n" +
        "- « Comment gérer les utilisateurs / les credentials / la sauvegarde ? », « Comment fonctionnent les déclencheurs ? »\n" +
        "- « Comment ça marche ? » pour un aperçu général de l'application.";

    private static bool Contains(string haystack, params string[] needles) => needles.Any(haystack.Contains);

    private static string Normalize(string s) => s.Trim().ToLowerInvariant()
        .Replace("é", "e").Replace("è", "e").Replace("ê", "e")
        .Replace("à", "a").Replace("ù", "u").Replace("ô", "o").Replace("î", "i").Replace("ç", "c");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

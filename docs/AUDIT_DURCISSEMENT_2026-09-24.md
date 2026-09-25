# Corrections de sécurité et de fiabilité — 24 septembre 2026

Les changements ci-dessous complètent les travaux déjà présents dans le dossier. Les modifications existantes ont été conservées. La validation porte sur le code local ; aucun déploiement ni transfert vers un serveur métier n'a été effectué.

## Corrections appliquées

| Domaine | Défaut corrigé | Comportement obtenu |
|---|---|---|
| Comptes | Le cycle d'inactivité pouvait verrouiller ou désactiver tous les administrateurs, car il utilisait un instantané unique. | Chaque changement relit le compte et les administrateurs disponibles dans une transaction, avec la même sérialisation que les actions manuelles. Un administrateur déjà verrouillé ne compte plus comme remplaçant disponible. |
| Révocation | Le verrouillage automatique conservait sessions et abonnements push. La réactivation pouvait conserver le verrouillage d'inactivité. | Rotation du security stamp et suppression des abonnements push dans la transaction ; réactivation d'un compte désactivé pour inactivité avec levée de son verrouillage. |
| FTPS | Un échec pouvait déclencher un nouvel essai avec le canal de données en clair. | Suppression du repli dans les envois et dans l'explorateur. Un serveur refusant le canal chiffré provoque un échec explicite. |
| Fichiers | Un déplacement ou archivage vers le dossier source pouvait supprimer le fichier. Les erreurs d'archivage pouvaient être masquées. | Déplacements sans suppression préalable ; refus de l'archivage sur lui-même ; échecs remontés. Les copies récursives figent la liste des entrées avant écriture. |
| Délais | Un exécuteur pouvait masquer une annulation en renvoyant un résultat. | Le moteur vérifie son jeton après le retour de l'exécuteur et conserve le statut timeout/annulation approprié. |
| Explorateur | Une modification de serveur ou une opération concurrente pouvait désynchroniser la cible et le dossier affiché. | Serveur fixé pendant la connexion, opérations sérialisées, relecture des droits et du credential avant chaque action, mise à jour atomique du chemin et de sa liste. |
| Entrées utilisateur | Noms de fichiers/dossiers autorisant des chemins ou flux Windows ; credentials restreints accessibles dans l'éditeur de jobs. | Validation d'un seul segment de nom avant le premier upload ; filtrage des credentials et revalidation avant test/sauvegarde, y compris LocalCredentialId dans le JSON. |
| Interface | Doubles enregistrements et erreurs peu visibles. | Bouton de sauvegarde verrouillé pendant l'enregistrement ; erreurs explicites dans l'éditeur et les modales de l'explorateur. |
| Web | Ressources statiques soumises à l'authentification, service du dashboard absent de l'injection, cache pouvant rendre un chargement invalidé. | Assets anonymes, service enregistré, chargement frais partagé après invalidation. Ajout des en-têtes nosniff, anti-cadrage, référent et CSP limitée compatible Blazor. |
| Recherche globale | Le démontage après rendu statique appelait JavaScript et produisait une exception à chaque page authentifiée. | Aucun appel JS sans inscription interactive ; fermeture sûre pendant une inscription en cours et libération de la référence .NET. |
| CI | La liste de codes NuGet passée en ligne de commande provoquait MSB1006. | Paramètres centralisés dans Directory.Build.props, audit des dépendances transitives bloquant sur NU1901 à NU1904 ; commande CI corrigée et README aligné sur .NET 10. |

## Validation

- Restauration NuGet avec audit bloquant : réussie.
- Compilation Release : réussie, sans avertissement ni erreur.
- Suite de tests : **78 réussites, 0 échec, 0 ignoré**, contre 29 tests au début de cette intervention.
- Audit NuGet des dépendances directes et transitives : aucune vulnérabilité connue signalée par les sources consultées.
- `git diff --check` : réussi.
- Instance locale avec base SQLite neuve et comptes fictifs : création du premier administrateur, activation TOTP réelle, puis réponses HTTP 200 sur `/`, `/operations`, `/jobs`, `/jobs/new`, `/explorer` et `/users`.
- Visiteur anonyme sur `/jobs` : redirection vers la connexion. Abonnement push sans jeton CSRF : rejet HTTP 400.
- CSS, JavaScript, logo, manifest et service worker accessibles anonymement en HTTP 200 avec l'en-tête nosniff.
- Journaux du parcours final : aucune exception de rendu JavaScript observée après correction.

## Limites de cette validation

- Les connexions à des serveurs FTP/FTPS/SFTP ou partages SMB distants n'ont pas été exercées. Les tests de fichiers utilisent des répertoires locaux temporaires. Les serveurs FTPS incompatibles avec le chiffrement des données doivent être corrigés ; leur ancien repli en clair a été supprimé.
- Le navigateur automatisé n'a pas démarré : le composant Windows échoue avec `os error 206`. Le rendu visuel et les interactions d'un circuit Blazor réel restent à vérifier ; les contrôles HTTP couvrent le rendu serveur et les protections décrites ci-dessus.
- Le parcours local a utilisé HTTP sur 127.0.0.1. Le certificat, la terminaison HTTPS et la configuration du proxy de production restent à valider dans l'environnement de déploiement. Le serveur local a signalé l'absence de port HTTPS configuré.
- Ces corrections ne constituent pas une certification de l'absence de toute faille. L'exécution de scripts conserve les privilèges du compte de service configuré.

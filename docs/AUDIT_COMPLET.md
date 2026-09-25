# Audit complet KernelMK — 23 septembre 2026

## Verdict

Le socle fonctionnel est ambitieux : orchestration multi-étapes, EDI, transferts, supervision, sauvegardes et interface web. Avant cet audit, il ne pouvait toutefois pas être considéré comme une plateforme d’exploitation fiable : plusieurs défauts pouvaient laisser un job fantôme, saturer SQLite, perdre des écritures auxiliaires, exposer des actions push ou produire une sauvegarde SQLite incomplète.

Les constats P0/P1 identifiés ont été corrigés et sont couverts par 28 tests. Le build est propre et le contrôle NuGet ne remonte plus de vulnérabilité connue.

## Correctifs livrés

| Domaine | Problème observé | Correction |
|---|---|---|
| Exécutions | Annulation de scripts/processus incomplète, créneaux de concurrence et exécutions parallèles fragiles | Suivi par identifiant d’exécution, arrêt du processus enfant, libération garantie du créneau et états terminaux persistés |
| Planification | Boucle d’erreur trop agressive et lancement avant persistance | Délais d’erreur, sélection des seuls déclencheurs échus et persistance avant lancement |
| SQLite | Écritures concurrentes, sauvegarde ignorant WAL | WAL, file d’écritures bornée avec métrique d’échec, sauvegarde/restauration cohérente avec WAL et validation atomique |
| Accès | Setup concurrent, compte désactivé restant connecté, routes push vulnérables aux requêtes croisées | Création admin atomique, révalidation d’utilisateur, 2FA exigée, anti-forgery, contrôle propriétaire et validation d’URL push |
| Webhook | Secret dans URL et risque de saturation | Absence de journalisation de cette route, comparaison constante du secret et limite de 30 appels/minute/IP |
| Dépendances | System.Security.Cryptography.Xml 9.0.0 vulnérable | Dépendances Microsoft alignées en 10.0.12 ; audit NuGet sans vulnérabilité |
| Dashboard | Environ 19 requêtes, entités EF sensibles et journaux complets à chaque rafraîchissement | Projections minimales, agrégats SQL, streaming des lignes, instantanés immuables sans secrets, cache partagé 15 s et regroupement des demandes concurrentes |
| Recherche | Requête par frappe et résultats de credentials trop larges | Délai de 300 ms, annulation des requêtes dépassées, projections sans secret et filtrage par rôle |

## Expérience premium

Le nouveau **Centre de pilotage** (`/operations`) donne une lecture opérationnelle immédiate : workflows actifs, jobs réellement en cours, succès, volumes, signaux de dégradation, mémoire serveur et métriques réelles du cache. L’interface est responsive, accessible au clavier, respecte `prefers-reduced-motion`, ne dépend d’aucun CDN et propose des raccourcis directs vers les opérations, la cartographie EDI et les rapports.

Le cache conserve au maximum trois instantanés (24 h, 7 j, 30 j), expire après 15 secondes et ne retient ni credentials, ni JSON de configuration, ni sorties de logs. Chaque chargement concurrent pour une même période est coalescé ; une annulation de navigateur n’annule pas le chargement pour les autres utilisateurs.

## Risques résiduels, sans maquillage

1. **.NET 10 LTS est maintenant la cible et il est supporte jusqu'en novembre 2028.** Les mises a jour de securite .NET doivent etre appliquees regulierement.
2. **SQLite garde un seul écrivain.** WAL et la file réduisent fortement les blocages, mais une très forte cadence d’écritures ou plusieurs nœuds nécessitent SQL Server/PostgreSQL et un cache distribué (Redis), pas davantage de cache mémoire.
3. **Le cache est par processus.** Il est excellent pour l’instance Windows actuelle, mais n’est pas partagé entre plusieurs services.
4. **Les tâches Script/PowerShell restent intrinsèquement puissantes.** L’application n’isole pas les processus avec un compte de service par job, AppLocker/WDAC ou un sandbox Windows. En production, le service doit tourner sous un compte dédié à privilèges minimaux et les répertoires de scripts doivent être verrouillés.
5. **Les webhooks gardent leur secret dans l’URL pour compatibilité.** Ils sont masqués des logs applicatifs et comparés en temps constant, mais un proxy externe peut encore journaliser l’URL. Une prochaine évolution doit porter un secret rotatif dans un en-tête HMAC.
6. **Il n’y a pas de haute disponibilité.** Les données et la clé DPAPI sont liées à la machine ; une reprise sur une autre machine exige une stratégie de sauvegarde/restauration et de clés explicitement conçue.
7. **La vérification visuelle native n’a pas pu être automatisée ici** : le helper Windows échoue avec `os error 206` (« nom de fichier ou extension trop long »). Les routes de l’instance isolée et les assets ont été vérifiés en HTTP, mais une revue manuelle de rendu sur navigateur reste recommandée avant diffusion.

## Validation effectuée

- `dotnet build KernelMK.slnx --no-restore` : 0 erreur, 0 avertissement.
- `dotnet test KernelMK.slnx --no-build` : 28 réussites.
- `dotnet list KernelMK.slnx package --vulnerable --include-transitive` : aucune vulnérabilité signalée.
- Instance locale isolée : `/setup`, `/manifest.json` et `/sw.js` répondent en HTTP 200.
- La CI Windows exécute restauration avec audit NuGet bloquant, build Release et tests à chaque push/pull request.


## Migration .NET 10 LTS

La migration a ete appliquee le 23 septembre 2026 : tous les projets ciblent
et10.0, les packages Microsoft utilisent 10.0.12, le SDK 10.0.401 est fixe par global.json et la CI utilise uniquement .NET 10. Les 28 tests passent apres recompilation.

## Archivage local automatise

Les executions et logs termines sont maintenant archives en JSON compresse avec empreinte SHA-256 avant purge SQLite. La retention par defaut est de 90 jours, avec un dossier local configurable sur le meme serveur. Voir docs/ARCHIVAGE_AUTOMATIQUE.md.

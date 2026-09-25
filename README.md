# kerneMKFTP

Plateforme centralisée d'automatisation, de planification et de supervision des traitements IT (jobs, scripts, transferts de fichiers, workflows, alertes, logs) — inspirée des besoins fonctionnels d'un outil type VisualCron.

## Stack

- **.NET 10** / Blazor Server (interactivité serveur)
- **SQLite** (Entity Framework Core) pour la configuration, l'historique d'exécution et l'audit
- **Tailwind CSS** (CLI standalone) pour l'interface
- Exécuteurs : scripts (PowerShell/Python/Batch), fichiers, transferts FTP/FTPS/SFTP/SMB, SQL, email, webhook
- Déclencheurs : cron/intervalle, calendrier, surveillance de dossier, dépendance de job, webhook API, démarrage serveur

## Prérequis

SDK .NET 10.0.401 (version fixée dans `global.json`). La restauration vérifie les vulnérabilités NuGet, y compris transitives, et bloque en cas d'alerte connue.

## Vérifier le projet

```powershell
dotnet restore KernelMK.slnx
dotnet build KernelMK.slnx -c Release --no-restore
dotnet test KernelMK.slnx -c Release --no-build
```

## Démarrer en développement

```
dotnet run --project src/KernelMK.Web
```

## File d'exécution durable

Les lancements manuels, planifiés, API et asynchrones sont admis dans une file SQLite persistante. La page `/queue` permet de suivre les demandes, changer leur priorité, les annuler et relancer explicitement celles qui nécessitent une vérification.

Un redémarrage conserve les demandes en attente. Une exécution interrompue passe à l'état **À vérifier** : la relance repart du début avec la configuration actuelle et peut donc répéter des effets externes déjà produits.

Le webhook `POST /api/triggers/{token}` répond désormais `202 Accepted`. L'en-tête facultatif `Idempotency-Key` (200 caractères maximum) permet à un émetteur de répéter le même appel sans créer une seconde demande.

Les téléchargements FTP, FTPS, SFTP et les copies SMB sont reçus dans un fichier temporaire du dossier cible, vidés sur disque puis publiés par renommage. Un transfert interrompu ne remplace donc pas le fichier final.

Depuis la file, un opérateur peut reprendre après les étapes déjà réussies. La reprise est refusée si la définition du job a changé ; les étapes protégées apparaissent comme ignorées dans la nouvelle trace. Une relance complète reste disponible lorsque la reprise n'est pas possible.

Chaque création ou modification de job crée aussi une version immuable dans SQLite. L'historique /jobs/{id}/versions conserve la définition, l'auteur, la date, le motif et une empreinte SHA-256. Une ancienne version peut être restaurée après aperçu des sections modifiées et confirmation ; la restauration devient elle-même une nouvelle version. Les secrets de credentials, jetons API et URL webhook ne sont pas copiés dans l'historique, et un ancien jeton révoqué ne peut pas être réactivé par restauration.

Le déploiement reste autonome : exécutable .NET publié, SQLite embarqué et aucun serveur de base, broker ou agent tiers à installer. Cette version vise une instance applicative active et ne coordonne pas plusieurs nœuds.
## Publier un exécutable Windows autonome

```
dotnet publish src/KernelMK.Web/KernelMK.Web.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

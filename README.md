# kerneMKFTP

Plateforme centralisée d'automatisation, de planification et de supervision des traitements IT (jobs, scripts, transferts de fichiers, workflows, alertes, logs) — inspirée des besoins fonctionnels d'un outil type VisualCron.

## Stack

- **.NET 9** / Blazor Server (interactivité serveur)
- **SQLite** (Entity Framework Core) pour la configuration, l'historique d'exécution et l'audit
- **Tailwind CSS** (CLI standalone) pour l'interface
- Exécuteurs : scripts (PowerShell/Python/Batch), fichiers, transferts FTP/FTPS/SFTP/SMB, SQL, email, webhook
- Déclencheurs : cron/intervalle, calendrier, surveillance de dossier, dépendance de job, webhook API, démarrage serveur

## Démarrer en développement

```
dotnet run --project src/KernelMK.Web
```

## Publier un exécutable Windows autonome

```
dotnet publish src/KernelMK.Web/KernelMK.Web.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

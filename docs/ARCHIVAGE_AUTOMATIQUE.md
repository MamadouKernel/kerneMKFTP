# Archivage automatique de l'historique

KernelMK archive automatiquement les exécutions terminées et leurs logs d'étapes avant de les purger de SQLite. Les jobs, utilisateurs, credentials et paramètres ne sont jamais déplacés.

## Fonctionnement

- Une exécution est archivée après 90 jours par défaut.
- Les archives sont des fichiers `json.gz` et un fichier `sha256` associé.
- Le fichier compressé est écrit, fermé et contrôlé par SHA-256 avant toute suppression dans SQLite.
- Le traitement s'exécute au démarrage puis toutes les 24 heures, par lots de 1 000 exécutions au maximum.
- Une erreur d'écriture ou de contrôle laisse les lignes SQLite intactes.

## Emplacement local

Par défaut, les archives vont dans `archives/history` sous le dossier de l'application. Ce chemin relatif est ancré sur l'exécutable, y compris lorsque KernelMK tourne comme Service Windows.

Pour utiliser un autre disque local du même serveur, modifiez `appsettings.json` :

```json
"HistoryArchive": {
  "Enabled": true,
  "Directory": "E:\\KernelMK-archives\\history",
  "RetentionDays": 90,
  "BatchSize": 1000,
  "RunEveryHours": 24
}
```

Le compte du service Windows KernelMK doit avoir accès en lecture et écriture au dossier choisi. Ce dossier contient les sorties de jobs et doit avoir des droits NTFS restreints au compte de service et aux administrateurs habilités.

Pour suspendre la purge tout en conservant les données actives, passez `Enabled` à `false`, puis redémarrez le service.

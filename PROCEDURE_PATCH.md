# Procédure d'Application de Patch — kernelMK (Côte d'Ivoire Terminal)

Ce guide explique comment appliquer un **patch léger** (correctif ou mise à jour) sur un serveur de production **sans republier ni redéployer l'intégralité de l'application (60 Mo)** et **sans aucun risque d'écraser la base de données, les identifiants SFTP ou les clés de chiffrement**.

---

## 1. Pourquoi utiliser un Patch au lieu d'une République Complète ?

| Critère | Déploiement Complet classique | Système de Patch kernelMK |
| :--- | :--- | :--- |
| **Taille du transfert** | ~60 Mo à 70 Mo | **~20 Mo** (archive ultra-compressée) |
| **Temps d'application** | 5 à 15 minutes manuelles | **Moins de 5 secondes** (script automatisé) |
| **Risque pour la base SQLite** | ⚠️ Risque d'écraser `App_Data` par inadvertance | 🛡️ **Zéro risque** : `App_Data/` et `keys/` ne sont jamais touchés |
| **Continuité de service** | Arrêt manuel, manipulation de fichiers | **Arrêt, sauvegarde de repli, copie et redémarrage automatique** |
| **Rollback (Retour arrière)** | Complexe si pas de backup préalable | **1-clic** via `rollback-patch.ps1` |

---

## 2. Contenu d'une Archive de Patch (`KernelMK-Patch.zip`)

L'archive de patch contient uniquement :
1. `KernelMK.exe` : L'exécutable compilé contenant les derniers correctifs.
2. `wwwroot\` : Les ressources statiques mises à jour (fichiers CSS, scripts JS, guides PDF, logos).
3. `apply-patch.ps1` : Le script PowerShell d'application automatique 1-clic.
4. `rollback-patch.ps1` : Le script de restauration immédiate vers la version précédente.
5. `PROCEDURE_PATCH.md` : Ce présent guide.

> 🔒 **Garantie de sécurité** : L'archive **ne contient jamais** de dossier `App_Data\` (base SQLite), ni de dossier `keys\` (clés de chiffrement). Vos utilisateurs, vos jobs SFTP, vos credentials et vos historiques restent **100% préservés**.

---

## 3. Étape par Étape : Comment Appliquer le Patch

### Étape 1 : Transférer l'archive sur le serveur
Copiez le fichier `KernelMK-Patch.zip` sur le serveur CIT (via SFTP, RDP, partage réseau ou clé USB sécurisée).

### Étape 2 : Extraire l'archive
Faites un clic droit sur `KernelMK-Patch.zip` > **Extraire tout...** dans un dossier temporaire (par exemple sur le Bureau ou dans `C:\CIT\kernelMK\patch\`).

### Étape 3 : Exécuter le script de mise à jour
1. Ouvrez une invite **PowerShell en tant qu'Administrateur** (clic droit sur PowerShell > *Exécuter en tant qu'administrateur*).
2. Naviguez vers le dossier extrait du patch :
   ```powershell
   cd C:\chemin\vers\KernelMK-Patch
   ```
3. Lancez le script :
   ```powershell
   .\apply-patch.ps1
   ```

### Que fait le script automatiquement ?
1. **Détection** : Il repère automatiquement l'emplacement d'installation de `KernelMK` (via le Service Windows ou le dossier standard `C:\CIT\kernelMK`).
2. **Arrêt sécurisé** : Il stoppe proprement le service Windows `KernelMK` (ou le processus en cours).
3. **Sauvegarde de précaution** : Il archive votre ancien `KernelMK.exe` dans le dossier `backups\patches\patch_YYYYMMDD_HHMMSS\`.
4. **Remplacement propre** : Il copie le nouveau `KernelMK.exe` et rafraîchit le dossier `wwwroot\`.
5. **Redémarrage** : Il redémarre le service Windows `KernelMK` et vérifie qu'il est bien à l'état `Running`.

*L'opération totale prend moins de 5 secondes.*

---

## 4. En cas de problème : Retour Arrière (Rollback 1-clic)

Si pour une raison quelconque la nouvelle version présente un comportement inattendu, vous pouvez revenir instantanément à la version précédente sans réinstaller :

1. Dans la même console PowerShell Administrateur :
   ```powershell
   .\rollback-patch.ps1
   ```
2. Le script arrête le service, reprend automatiquement la dernière version fonctionnelle archivée dans `backups\patches\`, la remet en place et relance le service Windows.

---

## 5. Comment Générer un Patch (Côté Développeur / Machine de build)

Pour fabriquer un nouveau patch après avoir corrigé des bugs dans le code source :

1. Ouvrir PowerShell à la racine du projet git.
2. Exécuter :
   ```powershell
   .\scripts\create-patch.ps1
   ```
   *(Ou avec `-SkipPublish` si la compilation a déjà été effectuée avec `dotnet publish`).*
3. Le fichier `release\KernelMK-Patch.zip` est généré, prêt à être livré aux administrateurs du serveur CIT.

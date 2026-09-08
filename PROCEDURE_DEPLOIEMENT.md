# Procédure de Déploiement — kernelMK (Côte d'Ivoire Terminal)

Ce document détaille la procédure complète pour déployer l'application **kernelMK - Côte d'Ivoire Terminal (CIT)** sur un serveur Windows ou un poste dédié.

---

## 1. Caractéristiques du Package

- **Nom du livrable** : `KernelMK-CIT.zip`
- **Type d'application** : Exécutable Windows autonome 64 bits (`self-contained`, `single-file`).
- **Prérequis runtime** : **Aucun** (.NET 9 est déjà embarqué dans l'exécutable, pas besoin d'installer le SDK ou le Runtime .NET sur la machine hôte).
- **Base de données** : SQLite locale intégrée, initialisée automatiquement au premier démarrage dans le sous-dossier `App_Data\`.
- **Politique de sécurité** : Authentification 2FA (TOTP) **obligatoire** pour tous les comptes.

---

## 2. Prérequis Système

| Élément | Spécification recommandée |
| :--- | :--- |
| **Système d'exploitation** | Windows Server 2016 / 2019 / 2022 ou Windows 10 / 11 (64 bits) |
| **Mémoire RAM** | 2 Go minimum (4 Go recommandés) |
| **Espace disque** | ~200 Mo pour l'application + espace pour les logs et sauvegardes |
| **Droits** | Compte Administrateur de la machine (pour le pare-feu et le service Windows) |
| **Port réseau** | Port `5000` TCP (modifiable dans `appsettings.json`) |

---

## 3. Installation des Fichiers

1. Transférer le fichier `KernelMK-CIT.zip` sur le serveur de destination.
2. Extraire le contenu de l'archive dans un répertoire dédié permanent, par exemple :
   ```text
   C:\CIT\kernelMK\
   ```
   > ⚠️ **Important** : Éviter d'installer l'application dans des dossiers temporaires (`C:\Users\...\Downloads` ou `C:\Temp`). Choisir un emplacement stable comme `C:\CIT\kernelMK\` ou `D:\Applications\kernelMK\`.

3. Vérifier que le dossier contient les éléments suivants :
   - `KernelMK.exe` (exécutable principal)
   - `appsettings.json` (fichier de configuration)
   - `web.config` (pour hébergement sous IIS optionnel)
   - `wwwroot\` (fichiers statiques, icônes, logos CIT)

---

## 4. Ouverture du Pare-feu Windows (Firewall)

Pour permettre aux autres postes du réseau local / intranet CIT d'accéder à l'application, ouvrir le port 5000 dans le Pare-feu Windows.

Exécuter la commande suivante dans une console **PowerShell (exécutée en tant qu'Administrateur)** :

```powershell
New-NetFirewallRule -DisplayName "CIT kernelMK (Port 5000)" -Direction Inbound -LocalPort 5000 -Protocol TCP -Action Allow
```

---

## 5. Mode de Déploiement au Choix

### Option A : Déploiement en tant que Service Windows (Recommandé en Production)

L'application intègre nativement la gestion des services Windows. En mode service, l'application démarre automatiquement au boot du serveur, même si aucune session utilisateur n'est ouverte.

#### 1. Création du service
Ouvrir **PowerShell en Administrateur** et exécuter :

```powershell
New-Service -Name "KernelMK" `
            -BinaryPathName "C:\CIT\kernelMK\KernelMK.exe" `
            -DisplayName "CIT kernelMK Automation Platform" `
            -Description "Plateforme d'automatisation et de supervision SFTP / Jobs - Côte d'Ivoire Terminal" `
            -StartupType Automatic
```

*(Remplacer `C:\CIT\kernelMK\KernelMK.exe` par le chemin réel où vous avez extrait l'application).*

#### 2. Démarrage du service
```powershell
Start-Service KernelMK
```

#### 3. Vérification de l'état
```powershell
Get-Service KernelMK
```

#### Commandes d'administration courantes du service :
- Arrêter : `Stop-Service KernelMK`
- Redémarrer : `Restart-Service KernelMK`
- Supprimer le service (en cas de désinstallation) : `sc.exe delete KernelMK`

---

### Option B : Lancement Interactif Manuel (Tests / Démonstration)

Pour tester rapidement ou lancer l'application en mode console :
1. Double-cliquer sur `KernelMK.exe`.
2. Une fenêtre de console s'ouvre indiquant :
   ```text
   info: Microsoft.Hosting.Lifetime[14]
         Now listening on: http://*:5000
   ```
3. Laisser la fenêtre ouverte pour maintenir l'application active.

---

## 6. Premier Accès et Configuration Initiale

1. Depuis le serveur ou un poste du réseau CIT, ouvrir un navigateur web :
   - Accès local sur le serveur : `http://localhost:5000`
   - Accès depuis le réseau CIT : `http://<IP_DU_SERVEUR>:5000` (ex: `http://192.168.1.50:5000`)
2. L'application détecte qu'il s'agit d'une nouvelle installation et affiche l'**Assistant d'installation initiale CIT** (`/setup`).
3. Saisir :
   - Nom complet
   - Adresse email de l'administrateur
   - Mot de passe fort
4. Cliquer sur **« Créer le compte administrateur »**.

---

## 7. Configuration Obligatoire de la 2FA (Deux Facteurs)

Dès la validation du compte administrateur (et pour tout nouvel utilisateur ultérieur) :
1. L'écran d'activation 2FA s'affiche automatiquement avec le QR code CIT.
2. Ouvrir une application d'authentification sur smartphone (**Microsoft Authenticator**, **Google Authenticator**, etc.).
3. Scanner le QR code affiché à l'écran (ou copier la clé secrète manuellement).
4. Saisir le code à 6 chiffres généré par l'application mobile et cliquer sur **« Activer la 2FA »**.
5. Les **10 codes de secours d'urgence** s'affichent :
   - Cliquer sur **« Copier tous les codes »** et les conserver dans un gestionnaire de mots de passe sécurisé.
6. Cliquer sur **« Accéder à la plateforme CIT → »**. L'accès complet est immédiatement opérationnel.

---

## 8. Configuration Avancée (`appsettings.json`)

Le fichier `appsettings.json` permet de personnaliser les paramètres de l'application :

### Changer le Port d'écoute
Pour écouter sur le port standard HTTP `80` ou un autre port (ex: `8080`) :
```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://*:8080"
      }
    }
  }
}
```

### Paramétrer les Alertes Email (SMTP)
```json
{
  "Smtp": {
    "Host": "smtp.cit.ci",
    "Port": 587,
    "UseTls": true,
    "Username": "service-alerts@cit.ci",
    "Password": "VOTRE_MOT_DE_PASSE",
    "From": "kernelmk@cit.ci"
  }
}
```

---

## 9. Sauvegarde et Maintenance

Tous les éléments d'état de l'application sont stockés dans le sous-dossier `App_Data\` :
- `App_Data\automation-platform.db` : Base de données SQLite (utilisateurs, rôles, configurations des jobs SFTP, historiques d'exécution, logs d'audit).
- `App_Data\logs\` : Journaux applicatifs horodatés (conservation automatique sur 30 jours glissants).
- `keys\` : Clés de protection des données et des cookies d'authentification.

> 💡 **Procédure de sauvegarde recommandée** : Sauvegarder régulièrement le dossier `App_Data\` et le dossier `keys\`.

---

## 10. Mises à Jour et Application de Patchs Légers (Correctifs)

Après la première installation complète, **il n'est plus nécessaire de republier ni de retransférer le bundle complet de 60 Mo pour chaque correctif**.

Un système de **patchs légers (~20 Mo)** est disponible :
- **Fichier du patch** : `KernelMK-Patch.zip`
- **Garantie** : Ne touche **jamais** à `App_Data\` (base de données) ni à `keys\` (chiffrement). Vos jobs, credentials et historiques sont 100% préservés.
- **Application** : Extraire l'archive sur le serveur et exécuter en PowerShell Administrateur :
  ```powershell
  .\apply-patch.ps1
  ```
  Le script arrête le service Windows, effectue une sauvegarde de repli dans `backups\patches\`, remplace l'exécutable et redémarre le service en moins de 5 secondes.
- **Rollback** : En cas de besoin, exécuter `.\rollback-patch.ps1` pour revenir instantanément à la version précédente.
- Pour plus de détails, consulter [PROCEDURE_PATCH.md](file:///c:/Users/KERNELMK/Documents/cit/sftp/kerneMKFTP/PROCEDURE_PATCH.md).

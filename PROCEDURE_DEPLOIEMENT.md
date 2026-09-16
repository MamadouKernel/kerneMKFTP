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

### Activer HTTPS (fortement recommandé)

⚠️ **Par défaut, kernelMK écoute en HTTP non chiffré.** Sur le réseau CIT, cela signifie que les
identifiants de connexion, le cookie de session et les codes 2FA transitent en clair et peuvent être
interceptés par toute personne ayant accès au même segment réseau. Activer HTTPS avant toute mise en
production réelle.

Le serveur CIT n'ayant pas de nom de domaine public, un certificat **Let's Encrypt/Certbot n'est pas
utilisable** (Certbot doit pouvoir vérifier la propriété d'un domaine, ce qu'une IP privée interne ne
permet pas). On utilise donc un certificat **auto-signé**, valable en interne :

1. Générer le certificat (une seule fois, en indiquant l'IP ou le nom du serveur) :
   ```powershell
   .\scripts\create-tls-cert.ps1 -DnsNames "192.168.1.50"
   ```
   Produit `certs\KernelMK-Server.pfx` (privé) et `certs\KernelMK-Server.cer` (public).

2. Définir le mot de passe du certificat comme variable d'environnement (jamais en clair dans
   `appsettings.json`) — par exemple pour le Service Windows :
   ```powershell
   [Environment]::SetEnvironmentVariable("Kestrel__Endpoints__Https__Certificate__Password", "VOTRE_MOT_DE_PASSE", "Machine")
   ```

3. Ajouter l'endpoint HTTPS dans `appsettings.json` :
   ```json
   {
     "Kestrel": {
       "Endpoints": {
         "Http": { "Url": "http://*:5000" },
         "Https": {
           "Url": "https://*:5001",
           "Certificate": {
             "Path": "certs/KernelMK-Server.pfx"
           }
         }
       }
     }
   }
   ```

4. Ouvrir le port HTTPS dans le pare-feu Windows :
   ```powershell
   New-NetFirewallRule -DisplayName "CIT kernelMK (Port 5001 HTTPS)" -Direction Inbound -LocalPort 5001 -Protocol TCP -Action Allow
   ```

5. Redémarrer le service (`Restart-Service KernelMK`). Les connexions HTTP sur le port 5000 sont
   automatiquement redirigées vers HTTPS.

6. Sur **chaque poste client**, installer une seule fois le certificat public pour éviter
   l'avertissement « connexion non sécurisée » du navigateur :
   ```powershell
   Import-Certificate -FilePath "certs\KernelMK-Server.cer" -CertStoreLocation Cert:\LocalMachine\Root
   ```
   (PowerShell administrateur sur le poste cible, ou double-clic sur le `.cer` → Installer le certificat
   → Ordinateur local → Autorités de certification racines de confiance.)

> 💡 Si CIT dispose un jour d'un vrai nom de domaine avec accès DNS pour ce serveur, Certbot en mode
> DNS-01 devient possible et remplace avantageusement le certificat auto-signé (renouvellement
> automatique, aucune installation manuelle sur les postes clients).

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

> 🔒 **Éviter le mot de passe en clair dans appsettings.json** : depuis le serveur, exécuter
> `KernelMK.exe --protect-smtp-password "VOTRE_MOT_DE_PASSE"` (dans le même dossier que l'exécutable,
> pour utiliser le même trousseau de clés `keys\`). La commande affiche une valeur du type
> `protected:CfDJ8...` à coller telle quelle dans `"Password"` — kernelMK la déchiffre automatiquement
> au démarrage. Un mot de passe sans ce préfixe continue de fonctionner en clair (rétro-compatibilité),
> mais la forme protégée est recommandée pour tout déploiement en production.

### Configurer l'authentification Microsoft 365 (OAuth2) pour l'email

Depuis 2022-2023, Microsoft a désactivé l'authentification SMTP/IMAP par simple mot de passe (Basic Auth) sur
la plupart des tenants Microsoft 365 — un compte email hébergé sur M365 (Exchange Online) nécessite désormais
l'authentification moderne (OAuth2). kernelMK supporte ce mode aussi bien pour les credentials email (étapes
"Email SMTP"/"Réception Email IMAP") que pour ses propres alertes internes (échec de job, etc.).

**1. Enregistrer une application dans Entra ID (Azure AD)** — dans le portail Azure, section *Entra ID →
Inscriptions d'applications → Nouvelle inscription* :
- Type de compte : "Comptes dans cet annuaire d'organisation uniquement".
- Une fois créée, note l'**Id d'application (client)** et l'**Id de l'annuaire (tenant)**.
- Onglet *Certificats et secrets* → génère un **nouveau secret client** (note sa valeur immédiatement, elle
  n'est plus visible ensuite) — c'est ce secret qui remplace le mot de passe.
- Onglet *Autorisations d'API* → *Ajouter une autorisation* → *API utilisées par mon organisation* → rechercher
  **"Office 365 Exchange Online"** → *Autorisations d'application* → cocher `IMAP.AccessAsApp` et
  `SMTP.SendAsApp` → **Accorder le consentement administrateur** (obligatoire, un admin M365 doit valider).

**2. Autoriser OAuth pour la boîte mail concernée** (PowerShell, module ExchangeOnlineManagement, en tant
qu'admin M365) :
```powershell
Connect-ExchangeOnline
Set-CASMailbox -Identity "flux-edi@cit-ci.onmicrosoft.com" -SmtpClientAuthenticationDisabled $false
```

**3. Configurer dans kernelMK** — deux emplacements possibles selon le besoin :
- **Pour une étape de job** (envoi/réception email dans un job) : créer un credential de type "Compte Email
  (IMAP)" ou "SMTP", authentification **"OAuth2 Microsoft 365"**, renseigner ClientId/TenantId + le secret
  client dans le champ "Client secret".
- **Pour les alertes internes** de kernelMK (notifications de job en échec) : dans `appsettings.json`, section
  `"Smtp"`, mettre `"UseOAuth2Microsoft365": true` et renseigner `OAuth2ClientId`/`OAuth2TenantId` ; le champ
  `"Password"` contient alors le secret client (protégeable via `--protect-smtp-password`, comme un mot de
  passe classique).

### Activer les Notifications Push (alertes navigateur)

kernelMK peut envoyer des notifications système (Windows/Chrome/Edge) directement sur les appareils des
utilisateurs qui l'activent, même onglet fermé — utile pour être alerté d'un job manquant, d'une anomalie
de durée ou d'un échec sans avoir à garder l'application ouverte. Chaque utilisateur active/désactive lui-même
cette option via le bouton 🔔 dans l'en-tête de l'application ; aucune configuration par utilisateur n'est
nécessaire côté serveur, seule la clé serveur (VAPID) doit être générée une fois :

```
KernelMK.exe --generate-vapid-keys
```

La commande affiche une clé publique en clair et une clé privée déjà protégée (`protected:CfDJ8...`, même
trousseau `keys\` que `--protect-smtp-password`) à coller telles quelles dans `appsettings.json` :

```json
{
  "Vapid": {
    "Subject": "mailto:support@cit.ci",
    "PublicKey": "BFI...",
    "PrivateKey": "protected:CfDJ8..."
  }
}
```

Tant que `PublicKey`/`PrivateKey` sont vides, les notifications push restent silencieusement désactivées
(aucune erreur) — les autres canaux (email, Teams, webhook) continuent de fonctionner normalement. Le site
doit être servi en HTTPS pour que le navigateur autorise l'abonnement (déjà le cas en production, voir
section précédente sur le certificat).

---

## 9. Sauvegarde et Maintenance

Tous les éléments d'état de l'application sont stockés dans le sous-dossier `App_Data\` :
- `App_Data\automation-platform.db` : Base de données SQLite (utilisateurs, rôles, configurations des jobs SFTP, historiques d'exécution, logs d'audit).
- `App_Data\logs\` : Journaux applicatifs horodatés (conservation automatique sur 30 jours glissants).
- `keys\` : Clés de protection des données et des cookies d'authentification.
- `certs\` : Certificat TLS (si HTTPS activé, voir section 8) — contient la clé privée du serveur, à protéger comme `keys\`.

> 💡 **Procédure de sauvegarde recommandée** : Sauvegarder régulièrement les dossiers `App_Data\`, `keys\` et `certs\`.

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

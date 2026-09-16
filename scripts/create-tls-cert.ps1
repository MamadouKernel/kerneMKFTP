<#
.SYNOPSIS
    Genere un certificat TLS auto-signe pour activer HTTPS sur le serveur kernelMK CIT.

.DESCRIPTION
    A utiliser quand le serveur n'a pas de nom de domaine public (donc pas d'automatisation
    Let's Encrypt/Certbot possible - Certbot doit pouvoir verifier la propriete d'un domaine,
    ce qu'un serveur purement interne en IP privee ne peut pas prouver).

    Produit deux fichiers dans -OutputDir (par defaut certs\) :
      - KernelMK-Server.pfx : certificat + cle privee (protege par mot de passe), utilise
        par Kestrel pour servir HTTPS. A garder confidentiel, ne jamais committer dans git.
      - KernelMK-Server.cer : certificat public, a installer une seule fois sur chaque poste
        client (voir instructions affichees a la fin) pour eviter l'avertissement du navigateur.

    Valide pour "localhost" et toutes les adresses IP/noms fournis via -DnsNames.

.PARAMETER DnsNames
    Noms d'hote et adresses IP couverts par le certificat (ex: l'IP du serveur CIT sur le
    reseau interne). "localhost" est toujours inclus automatiquement.

.PARAMETER OutputDir
    Dossier de sortie pour le .pfx et le .cer. Par defaut : certs\ a la racine du depot.

.PARAMETER Password
    Mot de passe du fichier .pfx (SecureString). Si omis, demande de facon interactive.

.PARAMETER ValidYears
    Duree de validite du certificat en annees (defaut : 5).

.EXAMPLE
    .\scripts\create-tls-cert.ps1 -DnsNames "192.168.1.50","kernelmk-cit"
#>

param(
    [string[]]$DnsNames = @(),
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\certs"),
    [System.Security.SecureString]$Password,
    [int]$ValidYears = 5
)

$ErrorActionPreference = "Stop"

$allNames = @("localhost") + $DnsNames | Select-Object -Unique
if ($allNames.Count -eq 1) {
    Write-Host "Astuce : passe -DnsNames avec l'IP ou le nom d'hote du serveur CIT (ex: -DnsNames '192.168.1.50')" -ForegroundColor Yellow
    Write-Host "         pour que le certificat soit valide quand on y accede depuis un autre poste du reseau." -ForegroundColor Yellow
    Write-Host ""
}

if (-not $Password) {
    $Password = Read-Host -AsSecureString -Prompt "Choisis un mot de passe pour proteger le certificat (.pfx)"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$pfxPath = Join-Path $OutputDir "KernelMK-Server.pfx"
$cerPath = Join-Path $OutputDir "KernelMK-Server.cer"

Write-Host "Generation du certificat TLS (auto-signe, usage interne) pour : $($allNames -join ', ')" -ForegroundColor Cyan

$cert = New-SelfSignedCertificate `
    -Type SSLServerAuthentication `
    -DnsName $allNames `
    -Subject "CN=$($allNames[0]), O=Cote d'Ivoire Terminal, C=CI" `
    -KeyUsage DigitalSignature, KeyEncipherment `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1") `
    -FriendlyName "KernelMK CIT Server TLS" `
    -NotAfter (Get-Date).AddYears($ValidYears) `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyExportPolicy Exportable `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256

Write-Host "Certificat cree (empreinte $($cert.Thumbprint)), export en cours..." -ForegroundColor Cyan

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $Password | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "Fichiers crees :" -ForegroundColor Green
Write-Host "  $pfxPath  (prive, a garder confidentiel — sert de certificat serveur Kestrel)"
Write-Host "  $cerPath  (public, a distribuer)"
Write-Host ""
Write-Host "ETAPE 1 — Activer HTTPS dans appsettings.json du serveur (voir PROCEDURE_DEPLOIEMENT.md" -ForegroundColor Cyan
Write-Host "section 'Activer HTTPS') : ajouter un endpoint Kestrel Https pointant vers ce .pfx," -ForegroundColor Cyan
Write-Host "avec le mot de passe fourni via la variable d'environnement Kestrel__Endpoints__Https__Certificate__Password" -ForegroundColor Cyan
Write-Host "(jamais en clair dans appsettings.json)." -ForegroundColor Cyan
Write-Host ""
Write-Host "ETAPE 2 — Sur CHAQUE poste client qui se connectera au serveur, installer le certificat" -ForegroundColor Yellow
Write-Host "public une seule fois (evite l'avertissement 'connexion non securisee' du navigateur) :" -ForegroundColor Yellow
Write-Host "  Import-Certificate -FilePath `"$cerPath`" -CertStoreLocation Cert:\LocalMachine\Root"
Write-Host "(necessite PowerShell administrateur sur le poste cible ; ou double-clic sur le .cer > Installer" -ForegroundColor Yellow
Write-Host " le certificat > Ordinateur local > Autorites de certification racines de confiance)." -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Green

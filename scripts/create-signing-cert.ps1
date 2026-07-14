<#
.SYNOPSIS
    Génère un certificat de signature de code auto-signé pour KernelMK.exe.

.DESCRIPTION
    Usage interne uniquement : ce certificat n'est reconnu que sur les machines où son
    certificat public (.cer) a été installé au préalable dans les magasins "Autorités de
    certification racines de confiance" ET "Éditeurs de confiance". Sans cette installation,
    Windows SmartScreen affichera toujours "Éditeur non identifié" pour KernelMK.exe.

    Produit deux fichiers dans le dossier -OutputDir :
      - KernelMK-CodeSigning.pfx : certificat + clé privée (protégé par mot de passe),
        utilisé par package-release.ps1 pour signer l'exécutable. À garder confidentiel.
      - KernelMK-CodeSigning.cer : certificat public, à distribuer et installer sur
        chaque poste cible (voir instructions affichées à la fin).

.PARAMETER OutputDir
    Dossier de sortie pour le .pfx et le .cer. Par défaut : certs\ à la racine du dépôt.

.PARAMETER Password
    Mot de passe du fichier .pfx (SecureString). Si omis, demandé de façon interactive.

.PARAMETER ValidYears
    Durée de validité du certificat en années (défaut : 5).

.EXAMPLE
    .\scripts\create-signing-cert.ps1
#>

param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\certs"),
    [System.Security.SecureString]$Password,
    [int]$ValidYears = 5
)

$ErrorActionPreference = "Stop"

if (-not $Password) {
    $Password = Read-Host -AsSecureString -Prompt "Choisis un mot de passe pour protéger le certificat (.pfx)"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$pfxPath = Join-Path $OutputDir "KernelMK-CodeSigning.pfx"
$cerPath = Join-Path $OutputDir "KernelMK-CodeSigning.cer"

Write-Host "Génération du certificat de signature de code (auto-signé, usage interne)..." -ForegroundColor Cyan

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=KernelMK Automation Platform, O=kernelMK, C=CI" `
    -KeyUsage DigitalSignature `
    -FriendlyName "KernelMK Code Signing" `
    -NotAfter (Get-Date).AddYears($ValidYears) `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyExportPolicy Exportable `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256

Write-Host "Certificat créé (empreinte $($cert.Thumbprint)), export en cours..." -ForegroundColor Cyan

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $Password | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

Write-Host ""
Write-Host "Fichiers créés :" -ForegroundColor Green
Write-Host "  $pfxPath  (privé, à garder confidentiel — sert à signer les prochaines publications)"
Write-Host "  $cerPath  (public, à distribuer)"
Write-Host ""
Write-Host "IMPORTANT — sur chaque poste cible, installer le certificat public une seule fois :" -ForegroundColor Yellow
Write-Host "  Import-Certificate -FilePath `"$cerPath`" -CertStoreLocation Cert:\LocalMachine\Root"
Write-Host "  Import-Certificate -FilePath `"$cerPath`" -CertStoreLocation Cert:\LocalMachine\TrustedPublisher"
Write-Host "(nécessite PowerShell administrateur sur le poste cible)."
Write-Host ""
Write-Host "Ensuite, publie avec :" -ForegroundColor Cyan
Write-Host "  .\scripts\package-release.ps1 -SignPfxPath `"$pfxPath`" -SignPfxPassword <mot-de-passe> -Zip"

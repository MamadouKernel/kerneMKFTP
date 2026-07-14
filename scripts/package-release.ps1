<#
.SYNOPSIS
    Publie kernelMK et prépare un dossier "à partager" propre : sans base de données,
    sans clés de chiffrement, sans sauvegardes ni symboles de débogage.

.DESCRIPTION
    1. Republie l'application (dotnet publish, self-contained, single-file, win-x64).
    2. Copie le résultat dans un dossier de sortie en excluant App_Data, keys, backups et *.pdb.
    L'application régénère elle-même ces dossiers (base vide, nouvelles clés) au premier
    lancement chez la personne qui reçoit le dossier, avec l'assistant de configuration initiale.

.PARAMETER OutputDir
    Dossier de sortie du package prêt à partager. Par défaut : release\ à la racine du dépôt.

.PARAMETER SkipPublish
    Ne relance pas dotnet publish ; réutilise le contenu déjà présent dans publish\.

.PARAMETER Zip
    Compresse aussi le résultat en un fichier .zip à côté du dossier de sortie.

.PARAMETER SignPfxPath
    Chemin vers un certificat de signature de code (.pfx). Si fourni, KernelMK.exe est signé
    après packaging (voir scripts\create-signing-cert.ps1 pour générer un certificat interne).

.PARAMETER SignPfxPassword
    Mot de passe du .pfx (SecureString). Ignoré si -SignPfxPath n'est pas fourni.

.EXAMPLE
    .\scripts\package-release.ps1
    .\scripts\package-release.ps1 -Zip
    .\scripts\package-release.ps1 -OutputDir C:\Partage\kernelMK -Zip
    .\scripts\package-release.ps1 -SignPfxPath certs\KernelMK-CodeSigning.pfx -Zip
#>

param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\release"),
    [switch]$SkipPublish,
    [switch]$Zip,
    [string]$SignPfxPath,
    [System.Security.SecureString]$SignPfxPassword
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $repoRoot "publish"
$webProject = Join-Path $repoRoot "src\KernelMK.Web\KernelMK.Web.csproj"

if (-not $SkipPublish) {
    Write-Host "Publication de kernelMK (Release, win-x64, self-contained, single-file)..." -ForegroundColor Cyan
    dotnet publish $webProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "Échec de dotnet publish (code $LASTEXITCODE)."
    }
}

if (-not (Test-Path $publishDir)) {
    throw "Dossier de publication introuvable : $publishDir. Lance sans -SkipPublish, ou publie d'abord manuellement."
}

# Dossiers/fichiers exclus : données runtime propres à une installation (base, clés, sauvegardes)
# et symboles de débogage (facultatifs, alourdissent le partage sans utilité pour l'utilisateur final).
$excludedDirs = @("App_Data", "keys", "backups")
$excludedFilePattern = "*.pdb"

if (Test-Path $OutputDir) {
    Write-Host "Nettoyage de l'ancien dossier de sortie : $OutputDir" -ForegroundColor Yellow
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Write-Host "Préparation du package propre dans : $OutputDir" -ForegroundColor Cyan

Get-ChildItem -Path $publishDir -Force | ForEach-Object {
    if ($_.PSIsContainer) {
        if ($excludedDirs -contains $_.Name) {
            Write-Host "  Ignoré (donnée runtime) : $($_.Name)\" -ForegroundColor DarkGray
            return
        }
        Copy-Item $_.FullName -Destination (Join-Path $OutputDir $_.Name) -Recurse -Force
    }
    else {
        if ($_.Name -like $excludedFilePattern) {
            Write-Host "  Ignoré (symboles debug) : $($_.Name)" -ForegroundColor DarkGray
            return
        }
        Copy-Item $_.FullName -Destination $OutputDir -Force
    }
}

if ($SignPfxPath) {
    if (-not (Test-Path $SignPfxPath)) {
        throw "Certificat introuvable : $SignPfxPath"
    }
    if (-not $SignPfxPassword) {
        $SignPfxPassword = Read-Host -AsSecureString -Prompt "Mot de passe du certificat ($SignPfxPath)"
    }

    $exePath = Join-Path $OutputDir "KernelMK.exe"
    Write-Host "Signature de $exePath..." -ForegroundColor Cyan

    $cert = Get-PfxCertificate -FilePath $SignPfxPath -Password $SignPfxPassword
    $signature = Set-AuthenticodeSignature -FilePath $exePath -Certificate $cert `
        -TimestampServer "http://timestamp.digicert.com" -HashAlgorithm SHA256

    # "UnknownError" avec un certificat auto-signé signifie presque toujours que la signature a bien
    # été apposée, mais que le certificat racine n'est pas (encore) installé dans les magasins de
    # confiance de CETTE machine — normal tant que create-signing-cert.ps1 n'a pas été suivi de
    # l'installation du .cer. On ne fait échouer le script que sur un vrai échec de signature.
    if ($signature.Status -eq "Valid") {
        Write-Host "Signature apposée et vérifiée comme fiable sur cette machine (certificat : $($cert.Subject))." -ForegroundColor Green
    }
    elseif ($signature.Status -eq "UnknownError" -and $signature.SignerCertificate) {
        Write-Host "Signature apposée (certificat : $($cert.Subject))." -ForegroundColor Green
        Write-Host "  Non reconnue comme fiable sur cette machine : $($signature.StatusMessage)" -ForegroundColor Yellow
        Write-Host "  Normal pour un certificat auto-signé tant que son .cer n'est pas installé (voir create-signing-cert.ps1)." -ForegroundColor Yellow
    }
    else {
        throw "Échec de la signature (statut : $($signature.Status) — $($signature.StatusMessage))."
    }
}

$size = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("Package prêt : {0} ({1:N1} Mo)" -f $OutputDir, $size) -ForegroundColor Green

if ($Zip) {
    $zipPath = "$OutputDir.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "Compression en : $zipPath" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Archive prête : $zipPath" -ForegroundColor Green
}

Write-Host ""
Write-Host "Ce dossier ne contient aucune donnée (base, clés, sauvegardes) : la personne qui le reçoit" -ForegroundColor Cyan
Write-Host "verra l'assistant de configuration initiale au premier lancement de KernelMK.exe." -ForegroundColor Cyan

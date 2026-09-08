<#
.SYNOPSIS
    Genere un package de patch leger pour kernelMK (Cote d'Ivoire Terminal).
    Ideal apres correction de bugs pour mettre a jour le serveur en quelques secondes
    sans retransferer les 60 Mo d'un bundle complet et sans risque d'ecraser la base.

.DESCRIPTION
    1. Compile et publie KernelMK.Web (Release, win-x64, self-contained, single-file).
    2. Extrait uniquement les elements necessaires au patch :
       - KernelMK.exe
       - wwwroot\ (fichiers web, guides, css)
       - apply-patch.ps1 (script d'application automatique 1-clic)
       - rollback-patch.ps1 (script de retour arriere immediat)
       - PROCEDURE_PATCH.md (instructions detaillees)
    3. Exclut rigoureusement : App_Data\, keys\, backups\, *.pdb.
    4. Compresse le tout dans 'release\KernelMK-Patch.zip' (~20-25 Mo).
#>

[CmdletBinding()]
param(
    [string]$OutputDir,
    [switch]$SkipPublish,
    [bool]$Zip = $true,
    [string]$SignPfxPath,
    [System.Security.SecureString]$SignPfxPassword
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot "release\KernelMK-Patch"
}
$publishDir = Join-Path $repoRoot "publish"
$webProject = Join-Path $repoRoot "src\KernelMK.Web\KernelMK.Web.csproj"

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "    CIT kernelMK -- Generation de Patch Leger               " -ForegroundColor White
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Compilation & Publication
if (-not $SkipPublish) {
    Write-Host "Compilation et publication (Release, win-x64, single-file)..." -ForegroundColor Cyan
    dotnet publish $webProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "Echec de dotnet publish (code $LASTEXITCODE)."
    }
    Write-Host "[OK] Compilation terminee avec succes." -ForegroundColor Green
}

if (-not (Test-Path $publishDir)) {
    throw "Dossier de publication introuvable : $publishDir"
}

$sourceExe = Join-Path $publishDir "KernelMK.exe"
if (-not (Test-Path $sourceExe)) {
    throw "KernelMK.exe introuvable dans $publishDir"
}

# 2. Preparation du dossier de patch
if (Test-Path $OutputDir) {
    Write-Host "Nettoyage de l'ancien dossier de patch : $OutputDir" -ForegroundColor Yellow
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Write-Host "Assemblage des composants du patch dans : $OutputDir" -ForegroundColor Cyan

# Copie de KernelMK.exe
Copy-Item $sourceExe -Destination $OutputDir -Force
Write-Host "  + KernelMK.exe" -ForegroundColor White

# Copie de wwwroot
$sourceWwwroot = Join-Path $publishDir "wwwroot"
if (Test-Path $sourceWwwroot) {
    Copy-Item $sourceWwwroot -Destination (Join-Path $OutputDir "wwwroot") -Recurse -Force
    Write-Host "  + Dossier wwwroot (ressources web, css, guides)" -ForegroundColor White
}

# Copie des scripts d'application et de rollback
$applyScript = Join-Path $PSScriptRoot "apply-patch.ps1"
if (Test-Path $applyScript) {
    Copy-Item $applyScript -Destination $OutputDir -Force
    Write-Host "  + Script d'application : apply-patch.ps1" -ForegroundColor White
}

$rollbackScript = Join-Path $PSScriptRoot "rollback-patch.ps1"
if (Test-Path $rollbackScript) {
    Copy-Item $rollbackScript -Destination $OutputDir -Force
    Write-Host "  + Script de secours : rollback-patch.ps1" -ForegroundColor White
}

# Copie du guide de patch
$patchDoc = Join-Path $repoRoot "PROCEDURE_PATCH.md"
if (Test-Path $patchDoc) {
    Copy-Item $patchDoc -Destination $OutputDir -Force
    Write-Host "  + Documentation : PROCEDURE_PATCH.md" -ForegroundColor White
}

# Signature de code optionnelle
if ($SignPfxPath) {
    if (-not (Test-Path $SignPfxPath)) {
        throw "Certificat introuvable : $SignPfxPath"
    }
    if (-not $SignPfxPassword) {
        $SignPfxPassword = Read-Host -AsSecureString -Prompt "Mot de passe du certificat ($SignPfxPath)"
    }

    $patchExe = Join-Path $OutputDir "KernelMK.exe"
    Write-Host "Signature numerique de $patchExe..." -ForegroundColor Cyan

    $cert = Get-PfxCertificate -FilePath $SignPfxPath -Password $SignPfxPassword
    $signature = Set-AuthenticodeSignature -FilePath $patchExe -Certificate $cert `
        -TimestampServer "http://timestamp.digicert.com" -HashAlgorithm SHA256

    if ($signature.Status -eq "Valid") {
        Write-Host "Signature apposee et verifiee avec succes." -ForegroundColor Green
    }
    else {
        Write-Host "Signature apposee (statut : $($signature.Status))." -ForegroundColor Yellow
    }
}

# 3. Compression ZIP
if ($Zip) {
    $zipPath = "$OutputDir.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "Compression de l'archive de patch : $zipPath" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
    
    $zipSize = (Get-Item $zipPath).Length / 1MB
    Write-Host ("Archive de patch prete : {0} ({1:N1} Mo)" -f $zipPath, $zipSize) -ForegroundColor Green
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "               PATCH CREE AVEC SUCCES !                     " -ForegroundColor White
Write-Host "============================================================" -ForegroundColor Green
Write-Host "Pour appliquer le patch sur le serveur :" -ForegroundColor White
Write-Host "  1. Copier 'KernelMK-Patch.zip' sur le serveur CIT." -ForegroundColor Yellow
Write-Host "  2. Extraire l'archive dans un dossier temporaire." -ForegroundColor Yellow
Write-Host "  3. Lancer PowerShell en Administrateur et executer :" -ForegroundColor Yellow
Write-Host "     .\apply-patch.ps1" -ForegroundColor Cyan
Write-Host ""
Write-Host "La base de donnees (App_Data), les cles et les configurations restent 100% intactes !" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""

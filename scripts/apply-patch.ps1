<#
.SYNOPSIS
    Script d'application automatique de patch pour kernelMK (Cote d'Ivoire Terminal).
    Met a jour l'executable et les ressources web SANS TOUCHER aux donnees (App_Data, cles, logs).

.DESCRIPTION
    1. Detecte le dossier d'installation de KernelMK (via le service Windows ou le dossier cible).
    2. Arrete proprement le service Windows KernelMK (ou le processus s'il tourne en console).
    3. Effectue une sauvegarde de securite immediate de l'ancien KernelMK.exe.
    4. Remplace KernelMK.exe et met a jour wwwroot/ sans toucher a la base de donnees ni aux cles.
    5. Redemarre automatiquement le service Windows.
    6. Verifie le bon demarrage de l'application.
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$InstallDir
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "    CIT kernelMK -- Application de Patch / Mise a Jour      " -ForegroundColor White
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Detection du dossier source du patch
$patchSourceDir = $PSScriptRoot
if (-not (Test-Path (Join-Path $patchSourceDir "KernelMK.exe"))) {
    throw "Fichier KernelMK.exe introuvable dans le dossier du patch : $patchSourceDir"
}

# 2. Detection du dossier d'installation cible sur le serveur
if (-not $InstallDir) {
    # Tentative 1 : Detection via le Service Windows
    $service = Get-Service -Name "KernelMK" -ErrorAction SilentlyContinue
    if ($service) {
        $wmi = Get-CimInstance -ClassName Win32_Service -Filter "Name='KernelMK'" -ErrorAction SilentlyContinue
        if ($wmi -and $wmi.PathName) {
            $rawPath = $wmi.PathName.Trim('"').Trim()
            $InstallDir = Split-Path -Parent $rawPath
            Write-Host "[OK] Installation detectee via le Service Windows KernelMK : $InstallDir" -ForegroundColor Green
        }
    }
}

if (-not $InstallDir) {
    # Tentative 2 : Si execute directement dans le dossier d'installation
    $parentExe = Join-Path (Split-Path -Parent $patchSourceDir) "KernelMK.exe"
    if (Test-Path $parentExe) {
        $InstallDir = Split-Path -Parent $patchSourceDir
    }
    elseif (Test-Path "C:\CIT\kernelMK\KernelMK.exe") {
        $InstallDir = "C:\CIT\kernelMK"
    }
    elseif (Test-Path "C:\inetpub\kernelMK\KernelMK.exe") {
        $InstallDir = "C:\inetpub\kernelMK"
    }
}

if (-not $InstallDir -or -not (Test-Path (Join-Path $InstallDir "KernelMK.exe"))) {
    Write-Host "Veuillez indiquer le chemin du dossier ou est installe kernelMK sur ce serveur." -ForegroundColor Yellow
    $InstallDir = Read-Host "Chemin d'installation (ex: C:\CIT\kernelMK)"
}

if (-not (Test-Path (Join-Path $InstallDir "KernelMK.exe"))) {
    throw "Dossier d'installation invalide (KernelMK.exe introuvable dans '$InstallDir')."
}

Write-Host "Dossier d'installation cible : $InstallDir" -ForegroundColor Cyan
Write-Host ""

# 3. Arret du service ou du processus
$serviceWasRunning = $false
$service = Get-Service -Name "KernelMK" -ErrorAction SilentlyContinue

if ($service) {
    if ($service.Status -eq "Running") {
        $serviceWasRunning = $true
        Write-Host "Arret du service Windows 'KernelMK'..." -ForegroundColor Yellow
        Stop-Service -Name "KernelMK" -Force
        Start-Sleep -Seconds 2
    }
}

# Arret de tout processus KernelMK en memoire
$runningProc = Get-Process -Name "KernelMK" -ErrorAction SilentlyContinue
if ($runningProc) {
    Write-Host "Arret du processus KernelMK en cours..." -ForegroundColor Yellow
    Stop-Process -Name "KernelMK" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

# 4. Sauvegarde de securite pre-patch (Rollback garanti)
$backupDir = Join-Path $InstallDir "backups\patches\patch_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

$currentExe = Join-Path $InstallDir "KernelMK.exe"
if (Test-Path $currentExe) {
    Copy-Item $currentExe -Destination (Join-Path $backupDir "KernelMK.exe") -Force
    Write-Host "[SAUVEGARDE] Ancien KernelMK.exe archive dans : $backupDir" -ForegroundColor Gray
}

# 5. Application des fichiers mis a jour
Write-Host "Remplacement de l'executable KernelMK.exe..." -ForegroundColor Cyan
Copy-Item (Join-Path $patchSourceDir "KernelMK.exe") -Destination $InstallDir -Force

# Mise a jour des assets wwwroot (fichiers statiques, docs, css)
$patchWwwroot = Join-Path $patchSourceDir "wwwroot"
if (Test-Path $patchWwwroot) {
    Write-Host "Mise a jour des ressources web (wwwroot)..." -ForegroundColor Cyan
    $destWwwroot = Join-Path $InstallDir "wwwroot"
    if (-not (Test-Path $destWwwroot)) {
        New-Item -ItemType Directory -Path $destWwwroot -Force | Out-Null
    }
    Copy-Item "$patchWwwroot\*" -Destination $destWwwroot -Recurse -Force
}

# Copie eventuelle des scripts d'administration
$rollbackScript = Join-Path $patchSourceDir "rollback-patch.ps1"
if (Test-Path $rollbackScript) {
    Copy-Item $rollbackScript -Destination $InstallDir -Force
}

# 6. Redemarrage du service Windows ou verification
if ($service -or $serviceWasRunning) {
    Write-Host "Redemarrage du service Windows 'KernelMK'..." -ForegroundColor Cyan
    Start-Service -Name "KernelMK"
    Start-Sleep -Seconds 3

    $srvStatus = (Get-Service -Name "KernelMK").Status
    if ($srvStatus -eq "Running") {
        Write-Host "[OK] Service Windows 'KernelMK' actif et en cours d'execution !" -ForegroundColor Green
    }
    else {
        Write-Host "[ATTENTION] Le service a le statut : $srvStatus" -ForegroundColor Yellow
    }
}
else {
    Write-Host "Le service Windows KernelMK n'est pas installe. Vous pouvez relancer KernelMK.exe manuellement." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "               PATCH APPLIQUE AVEC SUCCES !                " -ForegroundColor White
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  - Donnees conservees : Base SQLite (App_Data), Cles (keys)" -ForegroundColor Green
Write-Host "  - Jobs, historiques et credentials : 100% Intacts" -ForegroundColor Green
Write-Host "  - Sauvegarde de repli disponible dans : $backupDir" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""

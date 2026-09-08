<#
.SYNOPSIS
    Script de retour arriere (Rollback) de patch pour kernelMK (Cote d'Ivoire Terminal).
    Restaure la version precedente de KernelMK.exe sauvegardee avant l'application du patch.

.DESCRIPTION
    1. Detecte le dossier d'installation de KernelMK.
    2. Identifie la derniere sauvegarde dans 'backups\patches\'.
    3. Arrete proprement le service Windows KernelMK (ou le processus).
    4. Restaure KernelMK.exe depuis la sauvegarde de securite.
    5. Redemarre le service Windows.
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$InstallDir,

    [Parameter()]
    [string]$BackupFolder
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "============================================================" -ForegroundColor Magenta
Write-Host "    CIT kernelMK -- Restauration / Rollback de Patch        " -ForegroundColor White
Write-Host "============================================================" -ForegroundColor Magenta
Write-Host ""

# 1. Detection du dossier d'installation
if (-not $InstallDir) {
    # Detection via le Service Windows
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
    if (Test-Path ".\KernelMK.exe") {
        $InstallDir = (Get-Item ".").FullName
    }
    elseif (Test-Path "..\KernelMK.exe") {
        $InstallDir = (Get-Item "..").FullName
    }
    elseif (Test-Path "C:\CIT\kernelMK\KernelMK.exe") {
        $InstallDir = "C:\CIT\kernelMK"
    }
    elseif (Test-Path "C:\inetpub\kernelMK\KernelMK.exe") {
        $InstallDir = "C:\inetpub\kernelMK"
    }
}

if (-not $InstallDir -or -not (Test-Path (Join-Path $InstallDir "KernelMK.exe"))) {
    Write-Host "Veuillez indiquer le dossier d'installation de kernelMK :" -ForegroundColor Yellow
    $InstallDir = Read-Host "Chemin d'installation (ex: C:\CIT\kernelMK)"
}

if (-not (Test-Path (Join-Path $InstallDir "KernelMK.exe"))) {
    throw "Dossier d'installation invalide (KernelMK.exe introuvable dans '$InstallDir')."
}

# 2. Recherche des sauvegardes de patch
$patchesRoot = Join-Path $InstallDir "backups\patches"
if (-not (Test-Path $patchesRoot)) {
    throw "Aucun dossier de sauvegarde de patch trouve dans '$patchesRoot'."
}

$availableBackups = Get-ChildItem -Path $patchesRoot -Directory | Sort-Object CreationTime -Descending

if ($availableBackups.Count -eq 0) {
    throw "Aucune sauvegarde de patch disponible dans '$patchesRoot'."
}

$selectedBackup = $null
if ($BackupFolder) {
    $targetPath = Join-Path $patchesRoot $BackupFolder
    if (Test-Path $targetPath) {
        $selectedBackup = Get-Item $targetPath
    } else {
        throw "La sauvegarde specifiee n'existe pas : $targetPath"
    }
} else {
    # Prendre la sauvegarde la plus recente par defaut
    $selectedBackup = $availableBackups[0]
}

$backupExe = Join-Path $selectedBackup.FullName "KernelMK.exe"
if (-not (Test-Path $backupExe)) {
    throw "KernelMK.exe introuvable dans la sauvegarde : $($selectedBackup.FullName)"
}

Write-Host "Sauvegarde selectionnee pour le rollback :" -ForegroundColor Cyan
Write-Host "  Dossier : $($selectedBackup.Name) (cree le $($selectedBackup.CreationTime.ToString('dd/MM/yyyy HH:mm:ss')))" -ForegroundColor White
Write-Host "  Source  : $backupExe" -ForegroundColor Gray
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

$runningProc = Get-Process -Name "KernelMK" -ErrorAction SilentlyContinue
if ($runningProc) {
    Write-Host "Arret du processus KernelMK..." -ForegroundColor Yellow
    Stop-Process -Name "KernelMK" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

# 4. Restauration de l'ancien KernelMK.exe
Write-Host "Restauration de KernelMK.exe..." -ForegroundColor Cyan
Copy-Item $backupExe -Destination (Join-Path $InstallDir "KernelMK.exe") -Force

# 5. Redemarrage du service Windows
if ($service -or $serviceWasRunning) {
    Write-Host "Redemarrage du service Windows 'KernelMK'..." -ForegroundColor Cyan
    Start-Service -Name "KernelMK"
    Start-Sleep -Seconds 3

    $srvStatus = (Get-Service -Name "KernelMK").Status
    if ($srvStatus -eq "Running") {
        Write-Host "[OK] Service Windows 'KernelMK' restaure et actif !" -ForegroundColor Green
    }
    else {
        Write-Host "[ATTENTION] Le service a le statut : $srvStatus" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "               ROLLBACK EFFECTUE AVEC SUCCES                " -ForegroundColor White
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  - Version precedente restauree depuis : $($selectedBackup.Name)" -ForegroundColor Green
Write-Host "  - Base de donnees et cles : Intactes" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""

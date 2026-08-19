# ============================================================
# Install-Service.ps1
# Installe UN SEUL service Windows qui fait tourner KernelMKGateway-Engine.ps1
# (le moteur gere lui-meme tous les armateurs / types EDI / directions).
# A executer en PowerShell EN ADMINISTRATEUR.
# ============================================================
param(
    [string]$NssmPath = "C:\CIT\NAVIS\serviceEDI\nssm\nssm.exe",
    [string]$ScriptsDir = "C:\CIT\NAVIS\serviceEDI\scripts",
    [string]$LogRoot = "C:\CIT\NAVIS\serviceEDI\logs",

    [Parameter(Mandatory)][string]$ServiceAccount,
    [Parameter(Mandatory)][securestring]$ServiceAccountPassword,

    [string]$ServiceName = "CIT_KernelMKGateway"
)

if (-not (Test-Path $NssmPath)) {
    Write-Host "nssm.exe introuvable a : $NssmPath" -ForegroundColor Red
    Write-Host "Telechargez-le depuis https://nssm.cc/download" -ForegroundColor Yellow
    exit 1
}

$plainPwd = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($ServiceAccountPassword)
)

Write-Host "Installation du service $ServiceName ..." -ForegroundColor Cyan

& $NssmPath stop $ServiceName 2>$null
& $NssmPath remove $ServiceName confirm 2>$null

$psExe = (Get-Command powershell.exe).Source
$scriptPath = Join-Path $ScriptsDir "KernelMKGateway-Engine.ps1"
$serviceLogDir = Join-Path $LogRoot "_engine"
New-Item -ItemType Directory -Path $serviceLogDir -Force | Out-Null

& $NssmPath install $ServiceName $psExe "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
& $NssmPath set $ServiceName DisplayName "CIT KernelMKGateway - Moteur de transfert EDI multi-armateurs"
& $NssmPath set $ServiceName Description "Envoi et reception EDI (COARRI, CODECO, COPARN, etc.) pour tous les armateurs configures dans armateurs.json"
& $NssmPath set $ServiceName Start SERVICE_AUTO_START
& $NssmPath set $ServiceName AppStdout (Join-Path $serviceLogDir "service_stdout.log")
& $NssmPath set $ServiceName AppStderr (Join-Path $serviceLogDir "service_stderr.log")
& $NssmPath set $ServiceName AppRotateFiles 1
& $NssmPath set $ServiceName AppRotateOnline 1
& $NssmPath set $ServiceName AppRotateBytes 10485760
& $NssmPath set $ServiceName AppExit Default Restart
& $NssmPath set $ServiceName AppRestartDelay 5000
& $NssmPath set $ServiceName AppThrottle 10000
& $NssmPath set $ServiceName ObjectName $ServiceAccount $plainPwd

& $NssmPath start $ServiceName

Write-Host "Service $ServiceName installe et demarre." -ForegroundColor Green
$plainPwd = $null

# ============================================================
# Setup-Credentials.ps1
#
# A executer MANUELLEMENT, connecte avec le MEME compte Windows
# que celui qui fera tourner le service NSSM (DPAPI = lie au compte + machine).
#
# Usage :
#   .\Setup-Credentials.ps1 -ArmateurCode GUCE
#   .\Setup-Credentials.ps1 -ArmateurCode CMA-CGM
# ============================================================
param(
    [Parameter(Mandatory)][string]$ArmateurCode,
    [string]$GlobalConfigPath = "C:\CIT\NAVIS\serviceEDI\config\global-config.json",
    [string]$ArmateursConfigPath = "C:\CIT\NAVIS\serviceEDI\config\armateurs.json"
)

. "$PSScriptRoot\KernelMKGateway-Common.ps1"

$global = Get-GlobalConfig -Path $GlobalConfigPath
$armateursCfg = Get-ArmateursConfig -Path $ArmateursConfigPath
$armateur = $armateursCfg.armateurs | Where-Object { $_.code -eq $ArmateurCode } | Select-Object -First 1

if (-not $armateur) {
    Write-Host "Armateur '$ArmateurCode' introuvable dans armateurs.json" -ForegroundColor Red
    Write-Host "Codes disponibles : $(($armateursCfg.armateurs | ForEach-Object { $_.code }) -join ', ')" -ForegroundColor Yellow
    exit 1
}

$outFile = Join-Path $global.secretsDir $armateur.connection.passwordSecretFile
$prompt = "Mot de passe pour $($armateur.code) - $($armateur.name) (utilisateur: $($armateur.connection.username))"

$securePwd = Read-Host -Prompt $prompt -AsSecureString
$bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePwd)
$plain = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
[System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)

Protect-Secret -PlainText $plain -OutFile $outFile
$plain = $null

Write-Host ""
Write-Host "Mot de passe chiffre dans : $outFile" -ForegroundColor Green
Write-Host "Compte Windows utilise : $([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)" -ForegroundColor Yellow
Write-Host "Le service NSSM devra tourner sous ce MEME compte." -ForegroundColor Yellow

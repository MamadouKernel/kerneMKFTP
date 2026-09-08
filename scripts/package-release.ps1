<#
.SYNOPSIS
    Publie kernelMK et prepare un dossier a partager propre : sans base de donnees,
    sans cles de chiffrement, sans sauvegardes ni symboles de debogage.

.DESCRIPTION
    1. Republie l'application (dotnet publish, self-contained, single-file, win-x64).
    2. Copie le resultat dans un dossier de sortie en excluant App_Data, keys, backups et *.pdb.
    L'application regenerera elle-meme ces dossiers (base vide, nouvelles cles) au premier
    lancement chez la personne qui recoit le dossier, avec l'assistant de configuration initiale.
#>

param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\release\KernelMK-CIT"),
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
        throw "Echec de dotnet publish (code $LASTEXITCODE)."
    }
}

if (-not (Test-Path $publishDir)) {
    throw "Dossier de publication introuvable : $publishDir"
}

# Dossiers/fichiers exclus : donnees runtime propres a une installation (base, cles, sauvegardes)
# et symboles de debogage (facultatifs).
$excludedDirs = @("App_Data", "keys", "backups")
$excludedFilePattern = "*.pdb"

if (Test-Path $OutputDir) {
    Write-Host "Nettoyage de l'ancien dossier de sortie : $OutputDir" -ForegroundColor Yellow
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Write-Host "Preparation du package propre dans : $OutputDir" -ForegroundColor Cyan

Get-ChildItem -Path $publishDir -Force | ForEach-Object {
    if ($_.PSIsContainer) {
        if ($excludedDirs -contains $_.Name) {
            Write-Host "  Ignore (donnee runtime) : $($_.Name)\" -ForegroundColor DarkGray
            return
        }
        Copy-Item $_.FullName -Destination (Join-Path $OutputDir $_.Name) -Recurse -Force
    }
    else {
        if ($_.Name -like $excludedFilePattern) {
            Write-Host "  Ignore (symboles debug) : $($_.Name)" -ForegroundColor DarkGray
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

    if ($signature.Status -eq "Valid") {
        Write-Host "Signature apposee et verifiee comme fiable sur cette machine." -ForegroundColor Green
    }
    else {
        Write-Host "Signature apposee (statut : $($signature.Status))." -ForegroundColor Yellow
    }
}

$size = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("Package pret : {0} ({1:N1} Mo)" -f $OutputDir, $size) -ForegroundColor Green

if ($Zip) {
    $zipPath = "$OutputDir.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "Compression en : $zipPath" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Archive prete : $zipPath" -ForegroundColor Green
}

Write-Host ""
Write-Host "Ce dossier ne contient aucune donnee (base, cles, sauvegardes) : la personne qui le recoit" -ForegroundColor Cyan
Write-Host "verra l'assistant de configuration initiale au premier lancement de KernelMK.exe." -ForegroundColor Cyan

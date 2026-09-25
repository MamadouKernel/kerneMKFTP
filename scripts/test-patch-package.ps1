[CmdletBinding()]
param([Parameter(Mandatory)][string]$PatchPath)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$resolved = Resolve-Path -LiteralPath $PatchPath
$archive = [IO.Compression.ZipFile]::OpenRead($resolved)
try {
    $names = @($archive.Entries | ForEach-Object FullName)
    $required = @(
        "KernelMK.exe",
        "KernelMK.staticwebassets.endpoints.json",
        "wwwroot/css/app.css",
        "wwwroot/css/premium.css",
        "wwwroot/css/responsive.css",
        "apply-patch.ps1",
        "rollback-patch.ps1"
    )
    $missing = @($required | Where-Object { $_ -notin $names })
    $forbidden = @($names | Where-Object {
        $_ -match '(^|/)(App_Data|keys|backups|logs|certs)(/|$)' -or
        $_ -match '(^|/)appsettings(\..+)?\.json$' -or
        $_ -match '\.(db|sqlite|pdb)$'
    })
    if ($missing.Count) { throw "Fichiers obligatoires absents : $($missing -join ', ')" }
    if ($forbidden.Count) { throw "Fichiers persistants/interdits presents : $($forbidden -join ', ')" }

    $manifestEntry = $archive.GetEntry("KernelMK.staticwebassets.endpoints.json")
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    foreach ($asset in @("responsive", "premium")) {
        if (-not $manifest.Contains($asset, [StringComparison]::OrdinalIgnoreCase)) {
            throw "L'asset '$asset' est absent du manifeste statique."
        }
    }

    foreach ($scriptName in @("apply-patch.ps1", "rollback-patch.ps1")) {
        $entry = $archive.GetEntry($scriptName)
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $script = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $tokens = $null; $errors = $null
        [Management.Automation.Language.Parser]::ParseInput($script, [ref]$tokens, [ref]$errors) | Out-Null
        if ($errors.Count) { throw "Syntaxe PowerShell invalide dans $scriptName : $($errors[0].Message)" }
    }

    [pscustomobject]@{
        Patch = $resolved.Path
        Entries = $names.Count
        SizeMB = [math]::Round((Get-Item -LiteralPath $resolved).Length / 1MB, 1)
        SHA256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
        Status = "VALIDE"
    }
}
finally { $archive.Dispose() }
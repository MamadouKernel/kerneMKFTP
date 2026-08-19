# ============================================================
# KernelMKGateway-Common.ps1
# Fonctions partagees par le moteur generique multi-armateurs
# ============================================================

function Get-GlobalConfig {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path $Path)) { throw "global-config.json introuvable : $Path" }
    return Get-Content -Path $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Get-ArmateursConfig {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path $Path)) { throw "armateurs.json introuvable : $Path" }
    return Get-Content -Path $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Get-ArmateurPassword {
    # Deux modes possibles, au choix dans armateurs.json :
    #  - connection.password renseigne en clair -> utilise directement (simple, moins sur)
    #  - sinon connection.passwordSecretFile -> dechiffre via DPAPI (recommande)
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][string]$SecretsDir
    )
    if ($Connection.password -and $Connection.password.ToString().Trim() -ne "") {
        return $Connection.password
    }
    if ($Connection.passwordSecretFile -and $Connection.passwordSecretFile.Trim() -ne "") {
        $secretPath = Join-Path $SecretsDir $Connection.passwordSecretFile
        return Unprotect-Secret -EncryptedFile $secretPath
    }
    throw "Aucun mot de passe configure pour cet armateur (ni 'password', ni 'passwordSecretFile')."
}

function Get-EffectiveSetting {
    # Resout un reglage a 3 niveaux, du plus specifique au plus general :
    #   1) Edi.<Key>        (specifique a ce type EDI precis)
    #   2) Armateur.<Key>   (s'applique a tous les EDI de cet armateur)
    #   3) Global.<Key>     (valeur par defaut de toute l'application)
    # Permet de tout piloter depuis le global-config.json tout en autorisant
    # des exceptions ciblees dans armateurs.json, sans dupliquer la config.
    param($Edi, $Armateur, $Global, [Parameter(Mandatory)][string]$Key)

    if ($Edi -and ($Edi.PSObject.Properties.Name -contains $Key) -and $null -ne $Edi.$Key -and "$($Edi.$Key)".Trim() -ne "") {
        return $Edi.$Key
    }
    if ($Armateur -and ($Armateur.PSObject.Properties.Name -contains $Key) -and $null -ne $Armateur.$Key -and "$($Armateur.$Key)".Trim() -ne "") {
        return $Armateur.$Key
    }
    return $Global.$Key
}

function Get-EdiFilePatterns {
    # Retourne les patterns de fichiers a traiter pour un EDI donne.
    # Par defaut *.edi/*.EDI (retro-compatibilite), sauf si filePatterns est
    # explicitement renseigne dans armateurs.json (ex: ["*.xml"] pour les manifestes).
    param($Edi)
    if ($Edi.filePatterns -and $Edi.filePatterns.Count -gt 0) {
        return $Edi.filePatterns
    }
    return @("*.edi", "*.EDI", "*.xml", "*.XML", "*.txt", "*.TXT")
}

function Build-PatternCommandLines {
    # Construit N lignes de commande WinSCP, une par pattern de fichier.
    # Exemple : Build-PatternCommandLines @("*.xml") 'get {PATTERN} -resume'
    #           -> "get *.xml -resume"
    param(
        [Parameter(Mandatory)][string[]]$Patterns,
        [Parameter(Mandatory)][string]$CommandTemplate
    )
    $lines = foreach ($p in $Patterns) { $CommandTemplate.Replace("{PATTERN}", $p) }
    return ($lines -join "`n")
}

function Protect-Secret {
    param(
        [Parameter(Mandatory)][string]$PlainText,
        [Parameter(Mandatory)][string]$OutFile
    )
    $secure = ConvertTo-SecureString -String $PlainText -AsPlainText -Force
    $encrypted = ConvertFrom-SecureString -SecureString $secure
    $dir = Split-Path $OutFile -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -Path $OutFile -Value $encrypted -Encoding ASCII -Force

    $acl = Get-Acl $OutFile
    $acl.SetAccessRuleProtection($true, $false)
    $rules = @(
        (New-Object System.Security.AccessControl.FileSystemAccessRule("SYSTEM","FullControl","Allow")),
        (New-Object System.Security.AccessControl.FileSystemAccessRule("BUILTIN\Administrators","FullControl","Allow")),
        (New-Object System.Security.AccessControl.FileSystemAccessRule([System.Security.Principal.WindowsIdentity]::GetCurrent().Name,"FullControl","Allow"))
    )
    foreach ($r in $rules) { $acl.AddAccessRule($r) }
    Set-Acl -Path $OutFile -AclObject $acl
}

function Unprotect-Secret {
    param([Parameter(Mandatory)][string]$EncryptedFile)
    if (-not (Test-Path $EncryptedFile)) {
        throw "Secret chiffre introuvable : $EncryptedFile (executez Setup-Credentials.ps1)"
    }
    $encrypted = Get-Content -Path $EncryptedFile -Encoding ASCII
    $secure = ConvertTo-SecureString -String $encrypted
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Get-CombinLogDir {
    # Un log distinct par Armateur + Type EDI + Direction => diagnostic precis
    param($GlobalConfig, [string]$Armateur, [string]$EdiType, [string]$Direction)
    return Join-Path $GlobalConfig.logRoot "$Armateur\$EdiType\$Direction"
}

function Write-EngineLog {
    param(
        [Parameter(Mandatory)][string]$LogDir,
        [Parameter(Mandatory)][string]$Message,
        [string]$Level = "INFO"
    )
    if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
    $today = Get-Date -Format "yyyyMMdd"
    $logFile = Join-Path $LogDir "process_log_$today.log"
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] [$Level] $Message"
    Add-Content -Path $logFile -Value $line -Encoding UTF8
    Write-Host $line
}

function Write-LedgerEntry {
    param(
        [Parameter(Mandatory)][string]$LedgerFile,
        [Parameter(Mandatory)][string]$Armateur,
        [Parameter(Mandatory)][string]$EdiType,
        [Parameter(Mandatory)][string]$Direction,
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][long]$SizeBytes,
        [Parameter(Mandatory)][string]$Status,
        [string]$Detail = ""
    )
    $dir = Split-Path $LedgerFile -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    if (-not (Test-Path $LedgerFile)) {
        "Timestamp,Armateur,EdiType,Direction,FileName,SizeBytes,Status,Detail" | Set-Content -Path $LedgerFile -Encoding UTF8
    }
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $safeDetail = ($Detail -replace ',', ';')
    "$ts,$Armateur,$EdiType,$Direction,$FileName,$SizeBytes,$Status,$safeDetail" | Add-Content -Path $LedgerFile -Encoding UTF8
}

# Anti-repetition : evite d'ecrire la meme alerte des dizaines de fois si un
# dossier reste inaccessible pendant des heures.
$Global:__EngineAlertCache = @{}

function Write-EngineAlert {
    # Ecrit dans DEUX endroits :
    #  1) le log specifique de la combo (Armateur/EdiType/Direction) si $LogDir fourni
    #  2) un log d'alertes CENTRALISE (_alertes\alerts_AAAAMMJJ.log) a surveiller en un
    #     seul endroit, avec le contexte Armateur/EdiType en prefixe de chaque ligne.
    param(
        [Parameter(Mandatory)]$GlobalConfig,
        [Parameter(Mandatory)][string]$Armateur,
        [Parameter(Mandatory)][string]$EdiType,
        [Parameter(Mandatory)][string]$Direction,
        [Parameter(Mandatory)][string]$Message,
        [string]$AlertKey = $null,
        [string]$LogDir = $null,
        [Nullable[int]]$MinMinutesBetweenSameAlert = $null
    )

    if ($AlertKey) {
        $minGap = if ($null -ne $MinMinutesBetweenSameAlert) { $MinMinutesBetweenSameAlert } else { [int]$GlobalConfig.minMinutesBetweenSameAlert }
        $now = Get-Date
        if ($Global:__EngineAlertCache.ContainsKey($AlertKey)) {
            $last = $Global:__EngineAlertCache[$AlertKey]
            if (($now - $last).TotalMinutes -lt $minGap) { return }
        }
        $Global:__EngineAlertCache[$AlertKey] = $now
    }

    $prefixedMessage = "[$Armateur/$EdiType/$Direction] $Message"

    if ($LogDir) {
        Write-EngineLog -LogDir $LogDir -Message $Message -Level "ALERTE"
    }

    $alertsDir = $GlobalConfig.alertsLogDir
    if (-not (Test-Path $alertsDir)) { New-Item -ItemType Directory -Path $alertsDir -Force | Out-Null }
    $today = Get-Date -Format "yyyyMMdd"
    $alertsFile = Join-Path $alertsDir "alerts_$today.log"
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Add-Content -Path $alertsFile -Value "[$ts] $prefixedMessage" -Encoding UTF8
}

function Build-WinScpOpenLine {
    param($Connection, [string]$Password)
    $proto = $Connection.protocol.ToLower()
    $userEsc = [uri]::EscapeDataString($Connection.username)
    $passEsc = [uri]::EscapeDataString($Password)
    $url = "$($proto)://$($userEsc):$($passEsc)@$($Connection.host):$($Connection.port)/"

    if ($proto -eq "sftp" -and $Connection.hostKey) {
        return "open $url -hostkey=`"$($Connection.hostKey)`""
    }
    elseif (($proto -eq "ftpes" -or $proto -eq "ftps") -and $Connection.certificate) {
        return "open $url -certificate=`"$($Connection.certificate)`""
    }
    else {
        return "open $url"
    }
}

function New-WinScpScriptFile {
    param(
        [Parameter(Mandatory)][string]$TemplatePath,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][hashtable]$Replacements
    )
    $content = Get-Content -Path $TemplatePath -Raw -Encoding UTF8
    foreach ($key in $Replacements.Keys) {
        $content = $content.Replace("{{$key}}", $Replacements[$key])
    }
    Set-Content -Path $OutputPath -Value $content -Encoding ASCII -Force
}

function Invoke-EngineWinScp {
    param(
        [Parameter(Mandatory)][string]$WinScpExe,
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$WinScpLogPath,
        [Parameter(Mandatory)][string]$WinScpXmlLogPath
    )
    $argList = @(
        "/script=`"$ScriptPath`"",
        "/log=`"$WinScpLogPath`"",
        "/xmllog=`"$WinScpXmlLogPath`""
    )
    $proc = Start-Process -FilePath $WinScpExe -ArgumentList $argList -NoNewWindow -Wait -PassThru
    return $proc.ExitCode
}

function Get-WinScpTransferResults {
    param([Parameter(Mandatory)][string]$XmlLogPath)
    $results = @()
    if (-not (Test-Path $XmlLogPath)) { return $results }

    [xml]$xml = Get-Content -Path $XmlLogPath -Encoding UTF8
    $transferNodes = $xml.SelectNodes("//*[local-name()='upload' or local-name()='download']")
    foreach ($node in $transferNodes) {
        $filename = $node.filename
        $success = $node.SelectSingleNode("*[local-name()='success']") -ne $null
        $sizeNode = $node.SelectSingleNode(".//*[local-name()='size']")
        $size = if ($sizeNode) { [long]$sizeNode.InnerText } else { 0 }
        $results += [PSCustomObject]@{
            FileName  = [System.IO.Path]::GetFileName($filename)
            Success   = $success
            SizeBytes = $size
        }
    }
    return $results
}

function Compress-OldArchiveFiles {
    # Compresse les fichiers plus vieux que RetentionDays en zip mensuel
    # (archive_AAAAMM.zip), puis supprime les originaux une fois compresses.
    # Ne touche jamais aux fichiers deja .zip, ni aux fichiers recents.
    param(
        [Parameter(Mandatory)][string]$ArchiveDir,
        [Parameter(Mandatory)][int]$RetentionDays,
        [string]$LogDir = $null
    )

    if (-not (Test-Path $ArchiveDir)) { return }

    $cutoff = (Get-Date).AddDays(-$RetentionDays)
    $eligible = Get-ChildItem -Path $ArchiveDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt $cutoff -and $_.Extension -ne ".zip" }

    if (-not $eligible -or $eligible.Count -eq 0) { return }

    $groups = $eligible | Group-Object { $_.LastWriteTime.ToString("yyyyMM") }

    foreach ($grp in $groups) {
        $zipPath = Join-Path $ArchiveDir "archive_$($grp.Name).zip"
        try {
            Compress-Archive -Path ($grp.Group.FullName) -DestinationPath $zipPath -Update -ErrorAction Stop

            # Verifie que le zip contient bien tous les fichiers avant de supprimer les originaux
            Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
            $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
            $namesInZip = $zip.Entries | ForEach-Object { $_.Name }
            $zip.Dispose()

            $allPresent = $true
            foreach ($f in $grp.Group) {
                if ($namesInZip -notcontains $f.Name) { $allPresent = $false; break }
            }

            if ($allPresent) {
                $grp.Group | Remove-Item -Force -ErrorAction SilentlyContinue
                if ($LogDir) {
                    Write-EngineLog -LogDir $LogDir -Message "Archives compressees : $($grp.Group.Count) fichier(s) -> $zipPath (originaux supprimes)"
                }
            }
            else {
                if ($LogDir) {
                    Write-EngineLog -LogDir $LogDir -Message "Compression $zipPath incomplete, originaux CONSERVES par securite" -Level "ERROR"
                }
            }
        }
        catch {
            if ($LogDir) {
                Write-EngineLog -LogDir $LogDir -Message "ECHEC compression de $ArchiveDir : $($_.Exception.Message)" -Level "ERROR"
            }
        }
    }
}

function Get-ArchiveDirsForArmateur {
    # Retourne tous les dossiers d'archive locaux d'un armateur (out watchFolders +
    # out localArchiveDir + in localArchiveDir), chacun avec sa retention EFFECTIVE
    # (archiveCompressAfterDays surchargeable par Edi puis par Armateur, sinon Global),
    # pour la passe de compression quotidienne.
    param($Armateur, $Global)
    $entries = @()
    foreach ($edi in $Armateur.ediEnvoyes) {
        $days = [int](Get-EffectiveSetting -Edi $edi -Armateur $Armateur -Global $Global -Key "archiveCompressAfterDays")
        foreach ($wf in $edi.watchFolders) { $entries += [PSCustomObject]@{ Dir = $wf.archiveDir; RetentionDays = $days } }
        $entries += [PSCustomObject]@{ Dir = $edi.localArchiveDir; RetentionDays = $days }
    }
    foreach ($edi in $Armateur.ediRecus) {
        $days = [int](Get-EffectiveSetting -Edi $edi -Armateur $Armateur -Global $Global -Key "archiveCompressAfterDays")
        $entries += [PSCustomObject]@{ Dir = $edi.localArchiveDir; RetentionDays = $days }
    }
    $seen = @{}
    return $entries | Where-Object {
        if (-not $_.Dir) { return $false }
        if ($seen.ContainsKey($_.Dir)) { return $false }
        $seen[$_.Dir] = $true
        return $true
    }
}

function Test-ShouldRunDailyArchiveCompression {
    # Empeche de relancer la compression a chaque cycle (toutes les 10 sec) -
    # une seule fois par jour suffit. Utilise un fichier marqueur.
    param([Parameter(Mandatory)][string]$LogRoot)
    $marker = Join-Path $LogRoot "_engine\last_archive_compress.txt"
    $today = Get-Date -Format "yyyyMMdd"
    if ((Test-Path $marker) -and ((Get-Content $marker -ErrorAction SilentlyContinue) -eq $today)) {
        return $false
    }
    return $true
}

function Set-DailyArchiveCompressionDone {
    param([Parameter(Mandatory)][string]$LogRoot)
    $dir = Join-Path $LogRoot "_engine"
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Get-Date -Format "yyyyMMdd" | Set-Content -Path (Join-Path $dir "last_archive_compress.txt") -Encoding ASCII
}
function Connect-NetworkShare {
    # Authentifie explicitement le processus courant aupres du serveur de fichiers
    # (necessaire quand le compte qui execute le script/service n'a pas d'acces
    # direct au partage, et qu'un compte reseau specifique doit etre utilise).
    # Fonctionne aussi bien en session interactive qu'en service Windows (Session 0),
    # contrairement a un lecteur reseau mappe (K:) qui, lui, ne fonctionne QUE dans
    # la session interactive ou il a ete cree.
    param($GlobalConfig, [string]$LogDir = $null)

    if (-not $GlobalConfig.networkShare -or -not $GlobalConfig.networkShare.path) {
        return  # pas de partage reseau authentifie configure, rien a faire
    }

    $share = $GlobalConfig.networkShare

    # IMPORTANT : net.exe ecrit sur stderr des qu'il y a le moindre message (meme
    # "pas de connexion existante a supprimer"). Avec $ErrorActionPreference="Stop"
    # herite du script appelant, ce flux stderr redirige (2>&1) devient une erreur
    # TERMINANTE et interrompt tout le moteur. On neutralise donc temporairement
    # ce comportement le temps des appels a net.exe.
    $previousEAP = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $success = $false
    try {
        # Supprime une eventuelle connexion existante (Windows refuse 2 connexions
        # avec des identifiants differents vers le meme serveur - erreur 1219).
        # L'erreur "connexion introuvable" ici est normale et sans consequence.
        & net.exe use $share.path "/delete" "/y" 2>&1 | Out-Null

        $result = & net.exe use $share.path "/user:$($share.username)" $share.password "/persistent:no" 2>&1
        $success = ($LASTEXITCODE -eq 0)
    }
    catch {
        $result = $_.Exception.Message
        $success = $false
    }
    finally {
        $ErrorActionPreference = $previousEAP
    }

    if ($LogDir) {
        if ($success) {
            Write-EngineLog -LogDir $LogDir -Message "Connexion authentifiee etablie vers $($share.path)"
        }
        else {
            Write-EngineLog -LogDir $LogDir -Message "ECHEC connexion authentifiee vers $($share.path) : $result" -Level "ERROR"
        }
    }
    return $success
}

function Remove-OldLogsInDir {
    # Purge un seul dossier de logs avec une retention donnee (utilise pour
    # appliquer une retention EFFECTIVE differente par armateur/type EDI).
    param([Parameter(Mandatory)][string]$Dir, [Parameter(Mandatory)][int]$RetentionDays)
    if (-not (Test-Path $Dir)) { return }
    $cutoff = (Get-Date).AddDays(-$RetentionDays)
    Get-ChildItem -Path $Dir -Filter "*.log" -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt $cutoff } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Remove-OldLogs {
    # Purge TOUT le logRoot avec une seule retention (repli utilise pour les
    # dossiers hors perimetre armateur, ex: _engine, _alertes). Pour les logs
    # par armateur/type EDI, preferer Remove-OldLogsInDir avec la retention
    # effective de chaque combo (voir boucle principale du moteur).
    param([Parameter(Mandatory)][string]$LogRoot, [Parameter(Mandatory)][int]$RetentionDays)
    Remove-OldLogsInDir -Dir $LogRoot -RetentionDays $RetentionDays
}

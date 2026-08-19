# ============================================================
# KernelMKGateway-Engine.ps1
# Moteur generique multi-armateurs / multi-types EDI / bidirectionnel.
# Chaque armateur declare sa liste d'EDI a ENVOYER (ediEnvoyes) et sa
# liste d'EDI a RECEVOIR (ediRecus), independamment l'une de l'autre.
# ============================================================
param(
    [string]$GlobalConfigPath = "C:\CIT\NAVIS\serviceEDI\config\global-config.json",
    [string]$ArmateursConfigPath = "C:\CIT\NAVIS\serviceEDI\config\armateurs.json"
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\KernelMKGateway-Common.ps1"

function Invoke-OutCycle {
    # Traite UN element de la liste "ediEnvoyes" d'un armateur
    param($Global, $Armateur, $Edi)

    $logDir = Get-CombinLogDir -GlobalConfig $Global -Armateur $Armateur.code -EdiType $Edi.type -Direction "out"
    $alertKey = "$($Armateur.code)|$($Edi.type)|OUT"
    $alertMinGap = [int](Get-EffectiveSetting -Edi $Edi -Armateur $Armateur -Global $Global -Key "minMinutesBetweenSameAlert")

    foreach ($d in @($Edi.stagingDir, $Edi.localArchiveDir, $logDir)) {
        if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    }

    $patterns = Get-EdiFilePatterns -Edi $Edi
    $sentFiles = @()
    foreach ($wf in $Edi.watchFolders) {
        if (-not (Test-Path $wf.sourceDir)) {
            Write-EngineLog -LogDir $logDir -Message "Dossier source inaccessible : $($wf.sourceDir)" -Level "ERROR"
            Write-EngineAlert -GlobalConfig $Global -Armateur $Armateur.code -EdiType $Edi.type -Direction "OUT" `
                -Message "Dossier source inaccessible : $($wf.sourceDir) (reseau ? partage supprime ?)" `
                -AlertKey "$alertKey|srcdown|$($wf.sourceDir)" -LogDir $logDir -MinMinutesBetweenSameAlert $alertMinGap
            continue
        }

        $files = Get-ChildItem -Path $wf.sourceDir -Include $patterns -File -ErrorAction SilentlyContinue
        if (-not $files -or $files.Count -eq 0) { continue }

        Write-EngineLog -LogDir $logDir -Message "$($files.Count) fichier(s) trouve(s) dans $($wf.sourceDir)"
        foreach ($f in $files) {
            try {
                if (-not (Test-Path $wf.archiveDir)) { New-Item -ItemType Directory -Path $wf.archiveDir -Force | Out-Null }
                Copy-Item -Path $f.FullName -Destination $wf.archiveDir -Force
                $destPath = Join-Path $Edi.stagingDir $f.Name
                Move-Item -Path $f.FullName -Destination $destPath -Force
                $sentFiles += $f.Name
                Write-EngineLog -LogDir $logDir -Message "  -> $($f.Name) pret pour envoi"
            }
            catch {
                Write-EngineLog -LogDir $logDir -Message "ERREUR preparation $($f.Name) : $($_.Exception.Message)" -Level "ERROR"
            }
        }
    }

    if ($sentFiles.Count -eq 0) { return $false }

    $ts = Get-Date -Format "yyyy-MM-dd_HHmmss"
    $password = Get-ArmateurPassword -Connection $Armateur.connection -SecretsDir $Global.secretsDir
    $openLine = Build-WinScpOpenLine -Connection $Armateur.connection -Password $password
    $password = $null

    $genScript = Join-Path $logDir "winscp_$ts.txt"
    $winscpLog = Join-Path $logDir "winscp_$ts.log"
    $winscpXml = Join-Path $logDir "winscp_$ts.xml"

    # Le deplacement des fichiers envoyes vers une archive DISTANTE est optionnel :
    # si remoteArchivePath n'est pas renseigne, les fichiers restent tels quels
    # dans le dossier de depot distant apres envoi (comportement demande pour GUCE).
    $archiveBlock = ""
    if ($Edi.remoteArchivePath -and $Edi.remoteArchivePath.Trim() -ne "") {
        $archiveBlock = Build-PatternCommandLines -Patterns $patterns -CommandTemplate "mv {PATTERN} `"$($Edi.remoteArchivePath)/`""
    }

    $replacements = @{
        OPEN_LINE     = $openLine
        LOCAL_DIR     = $Edi.stagingDir
        REMOTE_PATH   = $Edi.remotePath
        ARCHIVE_BLOCK = $archiveBlock
    }
    $templatePath = Join-Path $Global.templatesDir "out.template.txt"
    New-WinScpScriptFile -TemplatePath $templatePath -OutputPath $genScript -Replacements $replacements

    Write-EngineLog -LogDir $logDir -Message "Envoi de $($sentFiles.Count) fichier(s) vers $($Armateur.code) ($($Edi.type))"
    $exitCode = Invoke-EngineWinScp -WinScpExe $Global.winScpExe -ScriptPath $genScript -WinScpLogPath $winscpLog -WinScpXmlLogPath $winscpXml
    Remove-Item -Path $genScript -Force -ErrorAction SilentlyContinue

    $results = Get-WinScpTransferResults -XmlLogPath $winscpXml
    $confirmedOk = @()
    $failed = @()

    foreach ($fname in $sentFiles) {
        $r = $results | Where-Object { $_.FileName -eq $fname } | Select-Object -First 1
        if ($r -and $r.Success) {
            $confirmedOk += $fname
            Write-LedgerEntry -LedgerFile $Global.ledgerFile -Armateur $Armateur.code -EdiType $Edi.type -Direction "OUT" -FileName $fname -SizeBytes $r.SizeBytes -Status "SUCCESS"
        }
        else {
            $failed += $fname
            Write-LedgerEntry -LedgerFile $Global.ledgerFile -Armateur $Armateur.code -EdiType $Edi.type -Direction "OUT" -FileName $fname -SizeBytes 0 -Status "FAILED" -Detail "exitcode=$exitCode"
        }
    }

    foreach ($fname in $confirmedOk) {
        $src = Join-Path $Edi.stagingDir $fname
        if (Test-Path $src) { Move-Item -Path $src -Destination $Edi.localArchiveDir -Force }
    }

    if ($exitCode -ne 0 -or $failed.Count -gt 0) {
        Write-EngineLog -LogDir $logDir -Message "Echec partiel/total. exitcode=$exitCode. OK=$($confirmedOk.Count)/$($sentFiles.Count). Echecs: $($failed -join ', ')" -Level "ERROR"
        Write-EngineAlert -GlobalConfig $Global -Armateur $Armateur.code -EdiType $Edi.type -Direction "OUT" `
            -Message "Echec transfert. exitcode=$exitCode. Confirmes: $($confirmedOk -join ', '). Echecs: $($failed -join ', '). Log: $winscpLog" `
            -AlertKey $alertKey -LogDir $logDir -MinMinutesBetweenSameAlert $alertMinGap
    }
    else {
        Write-EngineLog -LogDir $logDir -Message "Transfert confirme pour les $($confirmedOk.Count) fichier(s)."
    }

    return $true
}

function Invoke-InCycle {
    # Traite UN element de la liste "ediRecus" d'un armateur.
    # Supporte plusieurs sources distantes (Edi.remoteSources), symetrique aux
    # "watchFolders" de l'envoi : chaque source a son propre remotePath +
    # localDestinationDir + regle de nettoyage distant. Si remoteSources est
    # absent, on retombe sur les champs singuliers historiques (retro-compat).
    param($Global, $Armateur, $Edi)

    $logDir = Get-CombinLogDir -GlobalConfig $Global -Armateur $Armateur.code -EdiType $Edi.type -Direction "in"
    $alertKey = "$($Armateur.code)|$($Edi.type)|IN"
    $alertMinGap = [int](Get-EffectiveSetting -Edi $Edi -Armateur $Armateur -Global $Global -Key "minMinutesBetweenSameAlert")

    if (-not (Test-Path $Edi.localArchiveDir)) { New-Item -ItemType Directory -Path $Edi.localArchiveDir -Force | Out-Null }
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

    $sources = if ($Edi.remoteSources -and $Edi.remoteSources.Count -gt 0) {
        $Edi.remoteSources
    }
    else {
        @([PSCustomObject]@{
            remotePath                = $Edi.remotePath
            localDestinationDir       = $Edi.localDestinationDir
            remoteProcessedPath       = $Edi.remoteProcessedPath
            removeRemoteAfterDownload = $Edi.removeRemoteAfterDownload
        })
    }

    $patterns = Get-EdiFilePatterns -Edi $Edi
    $getLines = Build-PatternCommandLines -Patterns $patterns -CommandTemplate "get {PATTERN} -nopreservetime -resume"

    $anyReceived = $false
    foreach ($src in $sources) {
        if (-not (Test-Path $src.localDestinationDir)) { New-Item -ItemType Directory -Path $src.localDestinationDir -Force | Out-Null }

        $ts = Get-Date -Format "yyyy-MM-dd_HHmmss_fffffff"
        $password = Get-ArmateurPassword -Connection $Armateur.connection -SecretsDir $Global.secretsDir
        $openLine = Build-WinScpOpenLine -Connection $Armateur.connection -Password $password
        $password = $null

        $genScript = Join-Path $logDir "winscp_$ts.txt"
        $winscpLog = Join-Path $logDir "winscp_$ts.log"
        $winscpXml = Join-Path $logDir "winscp_$ts.xml"

        # Trois comportements possibles apres reception, par ordre de priorite :
        #   1) remoteProcessedPath renseigne -> deplacement (mv) vers ce dossier
        #   2) removeRemoteAfterDownload=true -> suppression (rm) sur le serveur distant
        #   3) aucun des deux -> les fichiers restent en place (ATTENTION : risque de
        #      re-telechargement au cycle suivant, un avertissement est loggue)
        $cleanupBlock = ""
        if ($src.remoteProcessedPath -and $src.remoteProcessedPath.Trim() -ne "") {
            $cleanupBlock = Build-PatternCommandLines -Patterns $patterns -CommandTemplate "mv {PATTERN} `"$($src.remoteProcessedPath)/`""
        }
        elseif ($src.removeRemoteAfterDownload -eq $true) {
            $cleanupBlock = Build-PatternCommandLines -Patterns $patterns -CommandTemplate "rm {PATTERN}"
        }
        else {
            Write-EngineLog -LogDir $logDir -Message "ATTENTION : ni remoteProcessedPath ni removeRemoteAfterDownload configures pour $($src.remotePath) - les fichiers resteront sur le serveur distant et seront re-telecharges au prochain cycle" -Level "WARN"
        }

        $replacements = @{
            OPEN_LINE      = $openLine
            LOCAL_DIR      = $src.localDestinationDir
            REMOTE_PATH    = $src.remotePath
            GET_LINES      = $getLines
            CLEANUP_BLOCK  = $cleanupBlock
        }
        $templatePath = Join-Path $Global.templatesDir "in.template.txt"
        New-WinScpScriptFile -TemplatePath $templatePath -OutputPath $genScript -Replacements $replacements

        $exitCode = Invoke-EngineWinScp -WinScpExe $Global.winScpExe -ScriptPath $genScript -WinScpLogPath $winscpLog -WinScpXmlLogPath $winscpXml
        Remove-Item -Path $genScript -Force -ErrorAction SilentlyContinue

        $results = Get-WinScpTransferResults -XmlLogPath $winscpXml
        if ($results.Count -eq 0) {
            if ($exitCode -ne 0) {
                Write-EngineLog -LogDir $logDir -Message "Echec connexion/reception sur $($src.remotePath) (exitcode=$exitCode), aucun fichier recupere." -Level "ERROR"
                Write-EngineAlert -GlobalConfig $Global -Armateur $Armateur.code -EdiType $Edi.type -Direction "IN" `
                    -Message "Impossible de recuperer les fichiers depuis $($src.remotePath). exitcode=$exitCode. Log: $winscpLog" `
                    -AlertKey "$alertKey|$($src.remotePath)" -LogDir $logDir -MinMinutesBetweenSameAlert $alertMinGap
            }
            continue
        }

        $anyReceived = $true
        Write-EngineLog -LogDir $logDir -Message "$($results.Count) fichier(s) recu(s) de $($Armateur.code) ($($Edi.type)) depuis $($src.remotePath)"

        $failed = @()
        foreach ($r in $results) {
            if ($r.Success) {
                Write-LedgerEntry -LedgerFile $Global.ledgerFile -Armateur $Armateur.code -EdiType $Edi.type -Direction "IN" -FileName $r.FileName -SizeBytes $r.SizeBytes -Status "SUCCESS"
                $localFile = Join-Path $src.localDestinationDir $r.FileName
                if (Test-Path $localFile) {
                    Copy-Item -Path $localFile -Destination $Edi.localArchiveDir -Force -ErrorAction SilentlyContinue
                }
                Write-EngineLog -LogDir $logDir -Message "  <- $($r.FileName) recu et confirme ($($r.SizeBytes) octets)"
            }
            else {
                $failed += $r.FileName
                Write-LedgerEntry -LedgerFile $Global.ledgerFile -Armateur $Armateur.code -EdiType $Edi.type -Direction "IN" -FileName $r.FileName -SizeBytes 0 -Status "FAILED"
            }
        }

        if ($failed.Count -gt 0) {
            Write-EngineLog -LogDir $logDir -Message "Fichiers en echec de reception depuis $($src.remotePath) : $($failed -join ', ')" -Level "ERROR"
            Write-EngineAlert -GlobalConfig $Global -Armateur $Armateur.code -EdiType $Edi.type -Direction "IN" `
                -Message "Reception partielle depuis $($src.remotePath). Echecs: $($failed -join ', '). Log: $winscpLog" `
                -AlertKey "$alertKey|$($src.remotePath)" -LogDir $logDir -MinMinutesBetweenSameAlert $alertMinGap
        }
    }

    return $anyReceived
}

# ============================================================
# BOUCLE PRINCIPALE
# ============================================================
Write-Host "Demarrage KernelMKGateway-Engine ($(Get-Date))"

while ($true) {
    $anyWorkDone = $false
    try {
        $global = Get-GlobalConfig -Path $GlobalConfigPath
        $armateursCfg = Get-ArmateursConfig -Path $ArmateursConfigPath

        $engineLogDirForConnect = Join-Path $global.logRoot "_engine"
        Connect-NetworkShare -GlobalConfig $global -LogDir $engineLogDirForConnect | Out-Null

        foreach ($armateur in $armateursCfg.armateurs) {
            if (-not $armateur.enabled) { continue }

            foreach ($edi in $armateur.ediEnvoyes) {
                try {
                    if (Invoke-OutCycle -Global $global -Armateur $armateur -Edi $edi) { $anyWorkDone = $true }
                }
                catch {
                    $errMsg = $_.Exception.Message
                    $logDir = Get-CombinLogDir -GlobalConfig $global -Armateur $armateur.code -EdiType $edi.type -Direction "out"
                    Write-EngineLog -LogDir $logDir -Message "ERREUR CYCLE OUT : $errMsg" -Level "ERROR"
                    Write-EngineAlert -GlobalConfig $global -Armateur $armateur.code -EdiType $edi.type -Direction "OUT" `
                        -Message "Erreur inattendue : $errMsg" -AlertKey "$($armateur.code)|$($edi.type)|OUT|crash" -LogDir $logDir `
                        -MinMinutesBetweenSameAlert ([int](Get-EffectiveSetting -Edi $edi -Armateur $armateur -Global $global -Key "minMinutesBetweenSameAlert"))
                }
            }

            foreach ($edi in $armateur.ediRecus) {
                try {
                    if (Invoke-InCycle -Global $global -Armateur $armateur -Edi $edi) { $anyWorkDone = $true }
                }
                catch {
                    $errMsg = $_.Exception.Message
                    $logDir = Get-CombinLogDir -GlobalConfig $global -Armateur $armateur.code -EdiType $edi.type -Direction "in"
                    Write-EngineLog -LogDir $logDir -Message "ERREUR CYCLE IN : $errMsg" -Level "ERROR"
                    Write-EngineAlert -GlobalConfig $global -Armateur $armateur.code -EdiType $edi.type -Direction "IN" `
                        -Message "Erreur inattendue : $errMsg" -AlertKey "$($armateur.code)|$($edi.type)|IN|crash" -LogDir $logDir `
                        -MinMinutesBetweenSameAlert ([int](Get-EffectiveSetting -Edi $edi -Armateur $armateur -Global $global -Key "minMinutesBetweenSameAlert"))
                }
            }
        }

        # Retention des logs : effective par armateur/type EDI (surcharge possible),
        # puis repli global pour les dossiers hors perimetre (_engine, _alertes, ledger...).
        foreach ($armateur in $armateursCfg.armateurs) {
            if (-not $armateur.enabled) { continue }
            foreach ($edi in $armateur.ediEnvoyes) {
                $days = [int](Get-EffectiveSetting -Edi $edi -Armateur $armateur -Global $global -Key "logRetentionDays")
                Remove-OldLogsInDir -Dir (Get-CombinLogDir -GlobalConfig $global -Armateur $armateur.code -EdiType $edi.type -Direction "out") -RetentionDays $days
            }
            foreach ($edi in $armateur.ediRecus) {
                $days = [int](Get-EffectiveSetting -Edi $edi -Armateur $armateur -Global $global -Key "logRetentionDays")
                Remove-OldLogsInDir -Dir (Get-CombinLogDir -GlobalConfig $global -Armateur $armateur.code -EdiType $edi.type -Direction "in") -RetentionDays $days
            }
        }
        Remove-OldLogs -LogRoot (Join-Path $global.logRoot "_engine") -RetentionDays ([int]$global.logRetentionDays)
        Remove-OldLogs -LogRoot (Join-Path $global.logRoot "_alertes") -RetentionDays ([int]$global.logRetentionDays)

        if (Test-ShouldRunDailyArchiveCompression -LogRoot $global.logRoot) {
            $engineLogDir = Join-Path $global.logRoot "_engine"
            Write-EngineLog -LogDir $engineLogDir -Message "Debut compression quotidienne des archives"
            foreach ($armateur in $armateursCfg.armateurs) {
                if (-not $armateur.enabled) { continue }
                $entries = Get-ArchiveDirsForArmateur -Armateur $armateur -Global $global
                foreach ($entry in $entries) {
                    Compress-OldArchiveFiles -ArchiveDir $entry.Dir -RetentionDays $entry.RetentionDays -LogDir $engineLogDir
                }
            }
            Set-DailyArchiveCompressionDone -LogRoot $global.logRoot
            Write-EngineLog -LogDir $engineLogDir -Message "Fin compression quotidienne des archives"
        }

        if ($anyWorkDone) {
            Start-Sleep -Seconds ([int]$global.waitSecondsBetweenFullPasses)
        }
        else {
            Start-Sleep -Seconds ([int]$global.waitSecondsIfNothingToDo)
        }
    }
    catch {
        Write-Host "[ERREUR FATALE] $($_.Exception.Message)"
        try {
            $global = Get-GlobalConfig -Path $GlobalConfigPath
            $fallbackLog = Join-Path $global.logRoot "_engine"
            Write-EngineLog -LogDir $fallbackLog -Message "ERREUR FATALE BOUCLE PRINCIPALE : $($_.Exception.Message)" -Level "ERROR"
            Write-EngineAlert -GlobalConfig $global -Armateur "SYSTEME" -EdiType "MOTEUR" -Direction "GLOBAL" `
                -Message "Erreur critique dans la boucle principale : $($_.Exception.Message)" -AlertKey "engine|crash" -LogDir $fallbackLog
        } catch {}
        Start-Sleep -Seconds 60
    }
}

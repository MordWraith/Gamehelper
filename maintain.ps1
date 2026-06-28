# Gamehelper (Stable) - Wartungs-Hub
# Usage:
#   maintain.cmd              GUI (Standard)
#   maintain.cmd -Console     Textmenue
#   maintain.cmd -Action Build
param(
    [ValidateSet(
        "Menu", "Status", "Build", "Run",
        "SyncGordinCore", "SyncGordinPlugins", "SyncGordinAll",
        "SyncPlugins", "SyncPluginsMordWraith", "SyncPluginsUpstream",
        "Publish", "PushSource", "VerifyPublish", "BuildDownloader"
    )]
    [string]$Action = "Menu",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Version,
    [string]$CommitMessage = "",
    [string]$PluginOnly = "",
    [string]$PluginMulti = "",
    [switch]$Gui,
    [switch]$Console
)

$ErrorActionPreference = "Stop"
$Root    = $PSScriptRoot
$Scripts = Join-Path $Root "scripts"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Get-ProjectVersion {
    $csproj = Join-Path $Root "GameHelper\GameHelper.csproj"
    if (-not (Test-Path $csproj)) { return "?" }
    $xml = Get-Content $csproj -Raw
    if ($xml -match '<Version>([^<]+)</Version>') { return $Matches[1].Trim() }
    return "?"
}

function Get-GithubRepository {
    $cfg = Join-Path $Root "github.config.json"
    if (Test-Path $cfg) {
        try {
            $j = Get-Content $cfg -Raw | ConvertFrom-Json
            if ($j.repository) { return $j.repository }
        } catch {}
    }
    return "MordWraith/Gamehelper (not configured)"
}

function Invoke-Script {
    param([string]$Path, [hashtable]$Arguments = @{})
    if (-not (Test-Path $Path)) { throw "Script not found: $Path" }
    Write-Host "  > $Path" -ForegroundColor DarkGray
    & $Path @Arguments
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Script failed (exit $LASTEXITCODE): $Path" }
}

# ---------------------------------------------------------------------------
# Actions
# ---------------------------------------------------------------------------
function Invoke-MaintainAction {
    param([string]$Name)

    switch ($Name) {
        "Status" {
            $ver  = Get-ProjectVersion
            $repo = Get-GithubRepository
            Write-Host "Version : $ver"
            Write-Host "Repo    : $repo"
            $exe = Join-Path $Root "publish\GameHelper.exe"
            Write-Host "publish : $(if (Test-Path $exe) { 'vorhanden' } else { 'fehlt' })"
            Push-Location $Root
            try { git status --short } finally { Pop-Location }
            return
        }
        "Build" {
            Invoke-Script (Join-Path $Scripts "build.ps1") @{ Configuration = $Configuration }
            Write-Host "Build OK -> $Root\publish\GameHelper.exe" -ForegroundColor Green
            return
        }
        "Run" {
            $exe = Join-Path $Root "publish\GameHelper.exe"
            if (-not (Test-Path $exe)) { throw "Noch nicht gebaut. Zuerst Build ausfuehren." }
            Start-Process $exe -WorkingDirectory (Join-Path $Root "publish")
            return
        }
        "SyncGordinCore" {
            Invoke-Script (Join-Path $Scripts "sync-gordin.ps1") @{ Mode = "CoreOnly" }
            return
        }
        "SyncGordinPlugins" {
            Invoke-Script (Join-Path $Scripts "sync-gordin.ps1") @{ Mode = "Plugins" }
            return
        }
        "SyncGordinAll" {
            Invoke-Script (Join-Path $Scripts "sync-gordin.ps1") @{ Mode = "AllGordinPlugins" }
            return
        }
        "SyncPlugins" {
            $args = @{ Set = "All" }
            if (-not [string]::IsNullOrWhiteSpace($PluginOnly)) { $args.Only = $PluginOnly }
            Invoke-Script (Join-Path $Scripts "sync-plugin-repos.ps1") $args
            return
        }
        "SyncPluginsMulti" {
            $names = $PluginMulti -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
            foreach ($name in $names) {
                Invoke-Script (Join-Path $Scripts "sync-plugin-repos.ps1") @{ Set = "All"; Only = $name }
            }
            return
        }
        "SyncPluginsMordWraith" {
            Invoke-Script (Join-Path $Scripts "sync-plugin-repos.ps1") @{ Set = "MordWraith" }
            return
        }
        "SyncPluginsUpstream" {
            Invoke-Script (Join-Path $Scripts "sync-plugin-repos.ps1") @{ Set = "Upstream" }
            return
        }
        "Publish" {
            if ([string]::IsNullOrWhiteSpace($Version) -or $Version -eq "?") {
                $script:Version = Get-ProjectVersion
            }
            if ([string]::IsNullOrWhiteSpace($script:Version)) { throw "Version erforderlich." }

            $overridePath = Join-Path $Root ".version-override"
            $savedVer = if (Test-Path $overridePath) { (Get-Content $overridePath -Raw).Trim() } else { "" }
            if ($script:Version -ne $savedVer) {
                $script:Version | Set-Content $overridePath -Encoding ASCII
                $csprojPath = Join-Path $Root "GameHelper\GameHelper.csproj"
                if (Test-Path $csprojPath) {
                    try {
                        $xml = [xml](Get-Content $csprojPath -Raw)
                        $pgs = $xml.Project.PropertyGroup
                        if ($pgs -isnot [array]) { $pgs = @($pgs) }
                        foreach ($pg in $pgs) {
                            if ($pg.Version)         { $pg.Version         = $script:Version }
                            if ($pg.AssemblyVersion) { $pg.AssemblyVersion = "$($script:Version).0" }
                            if ($pg.FileVersion)     { $pg.FileVersion     = "$($script:Version).0" }
                        }
                        $xml.Save($csprojPath)
                        Write-Host "  Version in csproj aktualisiert: $($script:Version)" -ForegroundColor DarkGray
                    } catch { Write-Warning "csproj-Version konnte nicht aktualisiert werden: $_" }
                }
            }

            Write-Host "Veroeffentliche Version $($script:Version) ..." -ForegroundColor Cyan
            $pubArgs = @{ Version = $script:Version; Configuration = $Configuration }
            if (-not [string]::IsNullOrWhiteSpace($CommitMessage)) {
                $pubArgs.Changelog = @($CommitMessage -split "`r?`n" | Where-Object { $_.Trim() })
            }
            Invoke-Script (Join-Path $Scripts "publish.ps1") $pubArgs
            return
        }
        "PushSource" {
            $args = @{}
            if (-not [string]::IsNullOrWhiteSpace($script:Version)) { $args.Version = $script:Version }
            if (-not [string]::IsNullOrWhiteSpace($CommitMessage))  { $args.CommitMessage = $CommitMessage }
            Invoke-Script (Join-Path $Scripts "push-github-source.ps1") $args
            return
        }
        "VerifyPublish" {
            if ([string]::IsNullOrWhiteSpace($script:Version)) { $script:Version = Get-ProjectVersion }
            Invoke-Script (Join-Path $Scripts "verify-github-publish.ps1") @{ ExpectedVersion = $script:Version }
            return
        }
        "BuildDownloader" {
            Invoke-Script (Join-Path $Scripts "build-downloader.ps1") @{}
            return
        }
        default { throw "Unbekannte Aktion: $Name" }
    }
}

# ---------------------------------------------------------------------------
# GUI Status-Badges aktualisieren
# ---------------------------------------------------------------------------
$script:MaintainGui      = $null
function Update-MaintainGuiStatus {
    if (-not $script:MaintainGui) { return }
    $b = $script:MaintainGui.Badges
    if (-not $b) { return }

    $ver        = Get-ProjectVersion
    $repo       = Get-GithubRepository
    $publishExe = Join-Path $Root "publish\GameHelper.exe"
    $signOk     = (Test-Path (Join-Path $Root "update-signing.key")) -and (Test-Path (Join-Path $Root "update-signing.pub"))
    $gh         = [bool](Get-Command gh -ErrorAction SilentlyContinue)
    $ghAuth     = $false
    if ($gh) { gh auth status 2>$null | Out-Null; $ghAuth = $LASTEXITCODE -eq 0 }

    $gitState = "kein Git-Repo"
    $gitClean = $false
    $gitDir   = Join-Path $Root ".git"
    if (Test-Path $gitDir) {
        $hasBranch = -not [string]::IsNullOrWhiteSpace(
            (Get-ChildItem (Join-Path $gitDir "refs\heads") -ErrorAction SilentlyContinue | Select-Object -First 1))
        if ($hasBranch) {
            Push-Location $Root
            try {
                $branch   = (git rev-parse --abbrev-ref HEAD 2>$null)
                $dirty    = (git status --porcelain 2>$null)
                $gitClean = -not $dirty
                $gitState = if ($dirty) { "$branch  (Aenderungen vorhanden)" } else { "$branch  (sauber)" }
            } finally { Pop-Location }
        } else { $gitState = "init (keine Commits)" }
    }

    $clrOk    = [System.Drawing.Color]::FromArgb(100, 200, 120)
    $clrWarn  = [System.Drawing.Color]::FromArgb(255, 200, 80)
    $clrError = [System.Drawing.Color]::FromArgb(220, 80, 80)
    $clrText  = [System.Drawing.Color]::FromArgb(235, 237, 245)

    $b.Version.Text      = "Version: $ver"
    $b.Version.ForeColor = $clrText
    $b.Repo.Text         = "Repo: $($repo -replace ' \(not configured\)','')"
    $b.Repo.ForeColor    = if ($repo -notlike "*(not configured*") { $clrOk } else { $clrWarn }
    $b.Publish.Text      = "publish: $(if (Test-Path $publishExe) { 'vorhanden' } else { 'fehlt' })"
    $b.Publish.ForeColor = if (Test-Path $publishExe) { $clrOk } else { $clrWarn }
    $b.Signatur.Text     = "Signatur: $(if ($signOk) { 'OK' } else { 'fehlt' })"
    $b.Signatur.ForeColor = if ($signOk) { $clrOk } else { $clrWarn }
    $b.GitHub.Text       = "GitHub: $(if ($ghAuth) { 'angemeldet' } elseif ($gh) { 'nicht angemeldet' } else { 'CLI fehlt' })"
    $b.GitHub.ForeColor  = if ($ghAuth) { $clrOk } elseif ($gh) { $clrWarn } else { $clrError }
    $b.Git.Text          = "Git: $gitState"
    $b.Git.ForeColor     = if ($gitClean) { $clrOk } else { $clrWarn }
}

# ---------------------------------------------------------------------------
# GUI Aktion im Hintergrund ausfuehren (Start-Job + Poll-Timer)
# ---------------------------------------------------------------------------
function Invoke-MaintainGuiAction {
    param(
        [string]$ActionName,
        [string]$CommitMessage = "",
        [string]$ExtraArgs = ""
    )

    $g = $script:MaintainGui
    if (-not $g) { return }
    if ($g['ActionRunning']) { return }

    $g['ActionRunning']       = $true
    $g.Form.UseWaitCursor     = $true
    if ($g.RunningLabel) {
        $g.RunningLabel.Text    = "  Aktion laeuft: $ActionName - bitte warten ..."
        $g.RunningLabel.Visible = $true
    }
    $g.Log.Clear()
    $g.Log.AppendText("Starte $ActionName ...`r`n")

    $maintainPath = Join-Path $g.Root "maintain.ps1"
    $argList = [System.Collections.Generic.List[string]]@(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", $maintainPath,
        "-Console", "-Action", $ActionName
    )
    if (-not [string]::IsNullOrWhiteSpace($CommitMessage)) {
        $argList.Add("-CommitMessage"); $argList.Add($CommitMessage)
    }
    if (-not [string]::IsNullOrWhiteSpace($ExtraArgs)) {
        if ($ExtraArgs -match '(?i)-Version\s+"?(\S+)"?')         { $argList.Add("-Version");     $argList.Add($Matches[1]) }
        if ($ExtraArgs -match '(?i)-PluginOnly\s+"?(\S+)"?')      { $argList.Add("-PluginOnly");  $argList.Add($Matches[1]) }
        if ($ExtraArgs -match '(?i)-PluginMulti\s+"?([^"]+)"?')   { $argList.Add("-PluginMulti"); $argList.Add($Matches[1]) }
    }
    $frozenArgs = $argList.ToArray()

    $job = Start-Job -ScriptBlock {
        param([string[]]$a)
        powershell.exe @a 2>&1
    } -ArgumentList (,$frozenArgs)

    $pollTimer = New-Object System.Windows.Forms.Timer
    $pollTimer.Interval = 150
    $pollTimer.Add_Tick({
        try {
            $lines = @(Receive-Job -Job $job -ErrorAction SilentlyContinue)
            foreach ($ln in $lines) {
                $t = "$ln" -replace '\r', ''
                if ($t.Trim() -and $t -notmatch '^\s*(remote:|Receiving|Resolving|Counting|Compressing) .*\d+%') {
                    $g.Log.AppendText($t + "`r`n")
                }
            }
            $done = $job.State -in @('Completed','Failed','Stopped')
            if ($done) {
                $pollTimer.Stop()
                $lines = @(Receive-Job -Job $job -ErrorAction SilentlyContinue)
                foreach ($ln in $lines) {
                    $t = "$ln" -replace '\r', ''
                    if ($t.Trim() -and $t -notmatch '^\s*(remote:|Receiving|Resolving|Counting|Compressing) .*\d+%') {
                        $g.Log.AppendText($t + "`r`n")
                    }
                }
                $ok = $job.State -eq 'Completed'
                Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
                if ($ok) {
                    $g.Log.AppendText("`r`nFertig.`r`n")
                    # Changelog-Eintraege aus Plugin-Sync einlesen und in Textfeld einfuegen
                    $pendingPath = Join-Path $g.Root "scripts\.pending-changelog.txt"
                    if ((Test-Path $pendingPath) -and $g['CommitBox']) {
                        try {
                            $newEntries = (Get-Content $pendingPath -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`r`n"
                            Remove-Item $pendingPath -Force -ErrorAction SilentlyContinue
                            if ($newEntries.Trim()) {
                                $existing = $g['CommitBox'].Text.Trim()
                                $g['CommitBox'].Text = if ($existing) { "$existing`r`n$newEntries" } else { $newEntries }
                                $g.Log.AppendText("Changelog automatisch ergaenzt.`r`n")
                            }
                        } catch {}
                    }
                }
                else     { $g.Log.AppendText("`r`nFehler - siehe Ausgabe oben.`r`n") }
                $g.Log.SelectionStart = $g.Log.Text.Length
                $g.Log.ScrollToCaret()
                $g['ActionRunning']    = $false
                $g.Form.UseWaitCursor  = $false
                if ($g.RunningLabel) { $g.RunningLabel.Visible = $false }
                try { Update-MaintainGuiStatus } catch {}
                $pollTimer.Stop()
                $pollTimer.Dispose()
            }
        } catch {
            try { Add-Content "$env:TEMP\maintain-stable-error.log" "$(Get-Date -Format 'HH:mm:ss') PollTick: $($_.Exception.Message)" } catch {}
            $g['ActionRunning'] = $false
            try { $g.Form.UseWaitCursor = $false } catch {}
            try { if ($g.RunningLabel) { $g.RunningLabel.Visible = $false } } catch {}
            try { $pollTimer.Stop(); $pollTimer.Dispose() } catch {}
        }
    }.GetNewClosure())
    $pollTimer.Start()
}

# ---------------------------------------------------------------------------
# SLN-Hilfsfunktionen: Plugin hinzufuegen / entfernen
# ---------------------------------------------------------------------------
$script:PluginProjectType = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}"
$script:PluginsVirtualFolder = "{9FA3D6BD-1EC1-3BA5-80CB-CE02773A58D5}"

function Add-PluginToSln {
    param([string]$SlnPath, [string]$Folder, [string]$CsprojName = "")
    if (-not $CsprojName) { $CsprojName = $Folder }
    $guid        = "{$([System.Guid]::NewGuid().ToString().ToUpper())}"
    $projType    = $script:PluginProjectType
    $pluginsDir  = $script:PluginsVirtualFolder
    $content     = [System.IO.File]::ReadAllText($SlnPath)

    $projectBlock = "Project(`"$projType`") = `"$CsprojName`", `"Plugins\$Folder\$CsprojName.csproj`", `"$guid`"`r`nEndProject`r`n"
    $content = $content -replace '(Project\("\{2150E333-8FDC-42A3-9474-1A3956D46DE8\}"\) = "Plugins")', "$projectBlock`$1"

    $configBlock = "`t`t$guid.Debug|Any CPU.ActiveCfg = Debug|Any CPU`r`n" +
                   "`t`t$guid.Debug|Any CPU.Build.0 = Debug|Any CPU`r`n" +
                   "`t`t$guid.Release|Any CPU.ActiveCfg = Release|Any CPU`r`n" +
                   "`t`t$guid.Release|Any CPU.Build.0 = Release|Any CPU`r`n"
    $content = $content -replace '(\tEndGlobalSection\r\n\tGlobalSection\(SolutionProperties\))', "$configBlock`$1"

    $nestedLine = "`t`t$guid = $pluginsDir`r`n"
    $content = $content -replace '(\tEndGlobalSection\r\n\tGlobalSection\(ExtensibilityGlobals\))', "$nestedLine`$1"

    [System.IO.File]::WriteAllText($SlnPath, $content, [System.Text.Encoding]::UTF8)
}

function Remove-PluginFromSln {
    param([string]$SlnPath, [string]$Folder)
    $content = [System.IO.File]::ReadAllText($SlnPath)
    $escapedFolder = [regex]::Escape($Folder)

    if ($content -notmatch "Project\(`"[^`"]*`"\`) = `"$escapedFolder`", `"[^`"]*`", `"(\{[A-F0-9\-]+\})`"") { return $false }
    $guid = $Matches[1]
    $escapedGuid = [regex]::Escape($guid)

    $content = $content -replace "Project\(`"[^`"]*`"\`) = `"$escapedFolder`", `"[^`"]*`", `"[^`"]*`"\r\nEndProject\r\n", ""
    $content = $content -replace "(\t\t$escapedGuid\.[^\r\n]+\r\n){4}", ""
    $content = $content -replace "\t\t$escapedGuid = \{[A-F0-9\-]+\}\r\n", ""

    [System.IO.File]::WriteAllText($SlnPath, $content, [System.Text.Encoding]::UTF8)
    return $true
}

# ---------------------------------------------------------------------------
# GUI: Dialog "Plugin hinzufuegen"
# ---------------------------------------------------------------------------
function Show-AddPluginDialog {
    $g = $script:MaintainGui
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text            = "Plugin hinzufuegen"
    $dlg.Size            = New-Object System.Drawing.Size(460, 220)
    $dlg.FormBorderStyle = "FixedDialog"
    $dlg.MaximizeBox     = $false
    $dlg.MinimizeBox     = $false
    $dlg.StartPosition   = "CenterParent"
    $dlg.BackColor       = [System.Drawing.Color]::FromArgb(28, 30, 38)
    $dlg.ForeColor       = [System.Drawing.Color]::FromArgb(235, 237, 245)
    $dlg.Font            = New-Object System.Drawing.Font("Segoe UI", 9.5)

    $cBg  = [System.Drawing.Color]::FromArgb(22, 25, 32)
    $cTxt = [System.Drawing.Color]::FromArgb(235, 237, 245)

    function Add-Label([string]$Text, [int]$Y) {
        $l = New-Object System.Windows.Forms.Label
        $l.Text = $Text; $l.Location = New-Object System.Drawing.Point(16, $Y)
        $l.AutoSize = $true; $dlg.Controls.Add($l)
    }
    function Add-TextBox([int]$Y, [string]$Placeholder = "") {
        $t = New-Object System.Windows.Forms.TextBox
        $t.Location    = New-Object System.Drawing.Point(16, $Y)
        $t.Size        = New-Object System.Drawing.Size(416, 22)
        $t.BackColor   = $cBg; $t.ForeColor = $cTxt
        $t.BorderStyle = "FixedSingle"
        if ($Placeholder) { $t.Text = $Placeholder }
        $dlg.Controls.Add($t)
        return $t
    }

    Add-Label "GitHub Repo (owner/repo oder https://github.com/...): " 16
    $tbRepo = Add-TextBox 34

    Add-Label "Ordnername in Plugins\ (wird automatisch ausgefuellt):" 70
    $tbFolder = Add-TextBox 88

    Add-Label "Kategorie:" 124
    $combo = New-Object System.Windows.Forms.ComboBox
    $combo.Location    = New-Object System.Drawing.Point(16, 142)
    $combo.Size        = New-Object System.Drawing.Size(160, 22)
    $combo.BackColor   = $cBg; $combo.ForeColor = $cTxt
    $combo.DropDownStyle = "DropDownList"
    @("mordWraith","upstream") | ForEach-Object { [void]$combo.Items.Add($_) }
    $combo.SelectedIndex = 0
    $dlg.Controls.Add($combo)

    # Auto-Fill: Ordnername vom Repo-Namen ableiten (letztes Segment nach /)
    $script:_autoFolder = ""
    $tbRepo.Add_TextChanged({
        $raw      = $tbRepo.Text.Trim() -replace '^https://github\.com/',''
        $derived  = ($raw -split '/')[-1] -replace '\.git$',''
        if ($tbFolder.Text -eq "" -or $tbFolder.Text -eq $script:_autoFolder) {
            $tbFolder.Text       = $derived
            $script:_autoFolder  = $derived
        }
    })

    $btnOk = New-Object System.Windows.Forms.Button
    $btnOk.Text          = "Hinzufuegen + Sync"
    $btnOk.Location      = New-Object System.Drawing.Point(240, 140)
    $btnOk.Size          = New-Object System.Drawing.Size(192, 32)
    $btnOk.FlatStyle     = "Flat"
    $btnOk.FlatAppearance.BorderSize = 0
    $btnOk.BackColor     = [System.Drawing.Color]::FromArgb(92, 140, 240)
    $btnOk.ForeColor     = $cTxt
    $btnOk.DialogResult  = [System.Windows.Forms.DialogResult]::OK
    $dlg.Controls.Add($btnOk)
    $dlg.AcceptButton = $btnOk

    if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return }

    $folder   = $tbFolder.Text.Trim()
    $repo     = $tbRepo.Text.Trim() -replace '^https://github\.com/',''
    $category = $combo.SelectedItem

    if (-not $folder -or -not $repo) {
        [System.Windows.Forms.MessageBox]::Show("Ordnername und Repo sind erforderlich.", "Fehler",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
        return
    }

    # In plugins-sources.json eintragen (Duplikat pruefen)
    $sourcesPath = Join-Path $g.Root "scripts\plugins-sources.json"
    try {
        $json = Get-Content $sourcesPath -Raw | ConvertFrom-Json
        $allKeys = @($json.mordWraith.PSObject.Properties.Name) + @($json.upstream.PSObject.Properties.Name)
        if ($allKeys -contains $folder) {
            [System.Windows.Forms.MessageBox]::Show("'$folder' ist bereits in plugins-sources.json eingetragen.", "Duplikat",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
            return
        }
        $json.$category | Add-Member -NotePropertyName $folder -NotePropertyValue $repo -Force
        $json | ConvertTo-Json -Depth 5 | Set-Content $sourcesPath -Encoding UTF8
        $slnPath = Join-Path $g.Root "GameOverlay.sln"
        Add-PluginToSln -SlnPath $slnPath -Folder $folder
        $script:MaintainGui.Log.AppendText("Plugin eingetragen: $folder <- $repo ($category)`r`n")
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Fehler beim Schreiben der plugins-sources.json:`n$_", "Fehler",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        return
    }

    # Sofort syncen
    Invoke-MaintainGuiAction -ActionName "SyncPlugins" -ExtraArgs "-PluginOnly `"$folder`""
}

# ---------------------------------------------------------------------------
# GUI: Dialog "Plugin entfernen"
# ---------------------------------------------------------------------------
function Show-RemovePluginDialog {
    $g           = $script:MaintainGui
    $sourcesPath = Join-Path $g.Root "scripts\plugins-sources.json"
    $slnPath     = Join-Path $g.Root "GameOverlay.sln"

    $json = Get-Content $sourcesPath -Raw | ConvertFrom-Json
    $allPlugins = [System.Collections.Generic.List[string]]@()
    foreach ($p in $json.mordWraith.PSObject.Properties) { $allPlugins.Add($p.Name) }
    foreach ($p in $json.upstream.PSObject.Properties)   { $allPlugins.Add($p.Name) }
    $allPlugins.Sort()

    if ($allPlugins.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Keine Plugins in plugins-sources.json vorhanden.", "Hinweis",
            [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
        return
    }

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text            = "Plugin entfernen"
    $dlg.Size            = New-Object System.Drawing.Size(380, 340)
    $dlg.FormBorderStyle = "FixedDialog"
    $dlg.MaximizeBox     = $false; $dlg.MinimizeBox = $false
    $dlg.StartPosition   = "CenterParent"
    $dlg.BackColor       = [System.Drawing.Color]::FromArgb(28, 30, 38)
    $dlg.ForeColor       = [System.Drawing.Color]::FromArgb(235, 237, 245)
    $dlg.Font            = New-Object System.Drawing.Font("Segoe UI", 9.5)

    $cBg = [System.Drawing.Color]::FromArgb(22, 25, 32); $cTxt = [System.Drawing.Color]::FromArgb(235, 237, 245)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = "Plugin auswaehlen (wird aus JSON, SLN und Plugins-Ordner entfernt):"; $lbl.AutoSize = $false
    $lbl.Location = New-Object System.Drawing.Point(14, 14); $lbl.Size = New-Object System.Drawing.Size(340, 34)
    $dlg.Controls.Add($lbl)

    $list = New-Object System.Windows.Forms.ListBox
    $list.Location    = New-Object System.Drawing.Point(14, 54)
    $list.Size        = New-Object System.Drawing.Size(340, 190)
    $list.BackColor   = $cBg; $list.ForeColor = $cTxt
    $list.BorderStyle = "FixedSingle"
    foreach ($p in $allPlugins) { [void]$list.Items.Add($p) }
    $dlg.Controls.Add($list)

    $btnOk = New-Object System.Windows.Forms.Button
    $btnOk.Text = "Entfernen"; $btnOk.Location = New-Object System.Drawing.Point(178, 258)
    $btnOk.Size = New-Object System.Drawing.Size(176, 32); $btnOk.FlatStyle = "Flat"
    $btnOk.FlatAppearance.BorderSize = 0
    $btnOk.BackColor = [System.Drawing.Color]::FromArgb(180, 60, 60)
    $btnOk.ForeColor = $cTxt; $btnOk.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $dlg.Controls.Add($btnOk); $dlg.AcceptButton = $btnOk

    if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return }
    if ($list.SelectedIndex -lt 0) { return }
    $folder = $list.SelectedItem

    $confirm = [System.Windows.Forms.MessageBox]::Show(
        "'$folder' komplett entfernen?`n(plugins-sources.json, GameOverlay.sln, Plugins\$folder\)",
        "Bestaetigung", [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Warning)
    if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    # Aus JSON entfernen
    $jsonFresh = Get-Content $sourcesPath -Raw | ConvertFrom-Json
    foreach ($cat in @("mordWraith","upstream")) {
        if ($jsonFresh.$cat.PSObject.Properties.Name -contains $folder) {
            $jsonFresh.$cat.PSObject.Properties.Remove($folder)
        }
    }
    $jsonFresh | ConvertTo-Json -Depth 5 | Set-Content $sourcesPath -Encoding UTF8

    # Aus SLN entfernen
    Remove-PluginFromSln -SlnPath $slnPath -Folder $folder | Out-Null

    # Ordner loeschen
    $pluginDir = Join-Path $g.Root "Plugins\$folder"
    if (Test-Path $pluginDir) { Remove-Item $pluginDir -Recurse -Force }

    $g.Log.AppendText("Plugin entfernt: $folder`r`n")
}

# ---------------------------------------------------------------------------
# GUI: Dialog "Plugin zur Solution hinzufuegen" (vorhandene Ordner, nicht in SLN)
# ---------------------------------------------------------------------------
function Show-AddExistingToSlnDialog {
    $g       = $script:MaintainGui
    $slnPath = Join-Path $g.Root "GameOverlay.sln"

    $slnContent  = [System.IO.File]::ReadAllText($slnPath)
    $pluginsBase = Join-Path $g.Root "Plugins"
    $candidates  = [System.Collections.Generic.List[hashtable]]@()

    foreach ($dir in (Get-ChildItem $pluginsBase -Directory -ErrorAction SilentlyContinue)) {
        $csprojFiles = @(Get-ChildItem $dir.FullName -Filter "*.csproj" -ErrorAction SilentlyContinue)
        if ($csprojFiles.Count -eq 0) { continue }
        $csprojName = [System.IO.Path]::GetFileNameWithoutExtension($csprojFiles[0].Name)
        $relPath    = "Plugins\$($dir.Name)\$csprojName.csproj"
        if ($slnContent -match [regex]::Escape($relPath)) { continue }
        $candidates.Add(@{ Folder = $dir.Name; CsprojName = $csprojName; RelPath = $relPath })
    }

    if ($candidates.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show(
            "Alle Plugin-Ordner sind bereits in der Solution.", "Nichts zu tun",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
        return
    }

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text            = "Plugin zur Solution hinzufuegen"
    $dlg.Size            = New-Object System.Drawing.Size(420, 360)
    $dlg.FormBorderStyle = "FixedDialog"
    $dlg.MaximizeBox     = $false; $dlg.MinimizeBox = $false
    $dlg.StartPosition   = "CenterParent"
    $dlg.BackColor       = [System.Drawing.Color]::FromArgb(28, 30, 38)
    $dlg.ForeColor       = [System.Drawing.Color]::FromArgb(235, 237, 245)
    $dlg.Font            = New-Object System.Drawing.Font("Segoe UI", 9.5)

    $cBg = [System.Drawing.Color]::FromArgb(22, 25, 32)
    $cTxt = [System.Drawing.Color]::FromArgb(235, 237, 245)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text     = "Plugin-Ordner auswaehlen (noch nicht in GameOverlay.sln):"
    $lbl.AutoSize = $false
    $lbl.Location = New-Object System.Drawing.Point(14, 14)
    $lbl.Size     = New-Object System.Drawing.Size(380, 34)
    $dlg.Controls.Add($lbl)

    $list = New-Object System.Windows.Forms.ListBox
    $list.Location        = New-Object System.Drawing.Point(14, 54)
    $list.Size            = New-Object System.Drawing.Size(380, 220)
    $list.BackColor       = $cBg; $list.ForeColor = $cTxt
    $list.BorderStyle     = "FixedSingle"
    $list.SelectionMode   = "MultiExtended"
    foreach ($c in $candidates) { [void]$list.Items.Add("$($c.Folder)  [$($c.CsprojName).csproj]") }
    $dlg.Controls.Add($list)

    $btnOk = New-Object System.Windows.Forms.Button
    $btnOk.Text         = "Zur Solution hinzufuegen"
    $btnOk.Location     = New-Object System.Drawing.Point(150, 288)
    $btnOk.Size         = New-Object System.Drawing.Size(244, 32)
    $btnOk.FlatStyle    = "Flat"
    $btnOk.FlatAppearance.BorderSize = 0
    $btnOk.BackColor    = [System.Drawing.Color]::FromArgb(92, 140, 240)
    $btnOk.ForeColor    = $cTxt
    $btnOk.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $dlg.Controls.Add($btnOk); $dlg.AcceptButton = $btnOk

    if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return }
    if ($list.SelectedIndices.Count -eq 0) { return }

    foreach ($idx in $list.SelectedIndices) {
        $c = $candidates[$idx]
        Add-PluginToSln -SlnPath $slnPath -Folder $c.Folder -CsprojName $c.CsprojName
        $g.Log.AppendText("SLN: $($c.Folder) hinzugefuegt ($($c.RelPath))`r`n")
    }
}

# ---------------------------------------------------------------------------
# GUI: Dialog "Einzelnes Plugin syncen"
# ---------------------------------------------------------------------------
function Show-SyncSinglePluginDialog {
    $g           = $script:MaintainGui
    $sourcesPath = Join-Path $g.Root "scripts\plugins-sources.json"
    if (-not (Test-Path $sourcesPath)) {
        [System.Windows.Forms.MessageBox]::Show("plugins-sources.json nicht gefunden.", "Fehler",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        return
    }

    $sources = Get-Content $sourcesPath -Raw | ConvertFrom-Json

    # Alle Plugins mit Repo-Info sammeln
    $allPlugins = [System.Collections.Generic.List[hashtable]]@()
    foreach ($p in $sources.mordWraith.PSObject.Properties) { $allPlugins.Add(@{ Name=$p.Name; Repo=$p.Value; Group="MordWraith" }) }
    foreach ($p in $sources.upstream.PSObject.Properties)   { $allPlugins.Add(@{ Name=$p.Name; Repo=$p.Value; Group="Upstream" }) }
    $allPlugins = @($allPlugins | Sort-Object { $_['Name'] })

    # Letzte Sync-SHAs lesen
    $statePath = Join-Path $g.Root "scripts\.plugin-sync-state.json"
    $syncState = if (Test-Path $statePath) { Get-Content $statePath -Raw | ConvertFrom-Json } else { $null }

    # Update-Check: parallele GitHub-API-Abfragen
    $updateMap = @{}
    if ($syncState) {
        [System.Windows.Forms.Cursor]::Current = [System.Windows.Forms.Cursors]::WaitCursor
        $jobs = @{}
        foreach ($plugin in $allPlugins) {
            $name = $plugin['Name']
            $repo = $plugin['Repo']
            $localSha = $syncState.$name
            if ($localSha) {
                $jobs[$name] = Start-Job -ScriptBlock {
                    param($r)
                    try { & gh api "repos/$r/commits?per_page=1" --jq '.[0].sha' 2>$null } catch { "" }
                } -ArgumentList $repo
            }
        }
        if ($jobs.Count -gt 0) {
            $null = Wait-Job -Job @($jobs.Values) -Timeout 8
            foreach ($name in @($jobs.Keys)) {
                $job = $jobs[$name]
                if ($job.State -eq 'Completed') {
                    $remoteSha = (Receive-Job $job 2>$null | Select-Object -First 1)
                    if ($remoteSha) { $remoteSha = $remoteSha.Trim() }
                    $localSha = $syncState.$name
                    if ($remoteSha -and $localSha -and ($remoteSha -ne $localSha)) {
                        $updateMap[$name] = $true
                    }
                }
                Remove-Job $job -Force -ErrorAction SilentlyContinue
            }
        }
        [System.Windows.Forms.Cursor]::Current = [System.Windows.Forms.Cursors]::Default
    }

    # Eintraege fuer Listbox aufbauen
    $displayItems = [System.Collections.Generic.List[string]]@()
    foreach ($plugin in $allPlugins) {
        $name    = $plugin['Name']
        $group   = $plugin['Group']
        $marker  = if ($updateMap[$name]) { "  *** Update verfuegbar" } else { "" }
        $displayItems.Add("$name  [$group]$marker")
    }

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text            = "Plugins syncen"
    $dlg.Size            = New-Object System.Drawing.Size(420, 380)
    $dlg.FormBorderStyle = "FixedDialog"
    $dlg.MaximizeBox     = $false; $dlg.MinimizeBox = $false
    $dlg.StartPosition   = "CenterParent"
    $dlg.BackColor       = [System.Drawing.Color]::FromArgb(28, 30, 38)
    $dlg.ForeColor       = [System.Drawing.Color]::FromArgb(235, 237, 245)
    $dlg.Font            = New-Object System.Drawing.Font("Segoe UI", 9.5)

    $cBg  = [System.Drawing.Color]::FromArgb(22, 25, 32)
    $cTxt = [System.Drawing.Color]::FromArgb(235, 237, 245)

    $hasUpdates = $updateMap.Count -gt 0
    $stateNote  = if ($syncState) { if ($hasUpdates) { "$($updateMap.Count) Update(s) verfuegbar  ***" } else { "Alles aktuell" } } else { "Kein Sync-Verlauf (erst nach 1. Sync sichtbar)" }

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text     = "Plugin(e) auswaehlen und von GitHub syncen:"
    $lbl.AutoSize = $false
    $lbl.Location = New-Object System.Drawing.Point(14, 12)
    $lbl.Size     = New-Object System.Drawing.Size(380, 22)
    $dlg.Controls.Add($lbl)

    $lblState = New-Object System.Windows.Forms.Label
    $lblState.Text      = $stateNote
    $lblState.AutoSize  = $false
    $lblState.Location  = New-Object System.Drawing.Point(14, 34)
    $lblState.Size      = New-Object System.Drawing.Size(380, 18)
    $lblState.Font      = New-Object System.Drawing.Font("Segoe UI", 8.5)
    $lblState.ForeColor = if ($hasUpdates) { [System.Drawing.Color]::FromArgb(255, 200, 60) } else { [System.Drawing.Color]::FromArgb(140, 160, 140) }
    $dlg.Controls.Add($lblState)

    $list = New-Object System.Windows.Forms.ListBox
    $list.Location      = New-Object System.Drawing.Point(14, 58)
    $list.Size          = New-Object System.Drawing.Size(380, 240)
    $list.BackColor     = $cBg; $list.ForeColor = $cTxt
    $list.BorderStyle   = "FixedSingle"
    $list.SelectionMode = "MultiExtended"
    foreach ($item in $displayItems) { [void]$list.Items.Add($item) }
    $dlg.Controls.Add($list)

    $lblHint = New-Object System.Windows.Forms.Label
    $lblHint.Text      = "Strg+Klick = Mehrfachauswahl"
    $lblHint.AutoSize  = $false
    $lblHint.Location  = New-Object System.Drawing.Point(14, 304)
    $lblHint.Size      = New-Object System.Drawing.Size(200, 18)
    $lblHint.Font      = New-Object System.Drawing.Font("Segoe UI", 8)
    $lblHint.ForeColor = [System.Drawing.Color]::FromArgb(110, 120, 140)
    $dlg.Controls.Add($lblHint)

    $btnOk = New-Object System.Windows.Forms.Button
    $btnOk.Text         = "Syncen"
    $btnOk.Location     = New-Object System.Drawing.Point(230, 298)
    $btnOk.Size         = New-Object System.Drawing.Size(164, 32)
    $btnOk.FlatStyle    = "Flat"
    $btnOk.FlatAppearance.BorderSize = 0
    $btnOk.BackColor    = [System.Drawing.Color]::FromArgb(92, 140, 240)
    $btnOk.ForeColor    = $cTxt
    $btnOk.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $dlg.Controls.Add($btnOk); $dlg.AcceptButton = $btnOk

    if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return }
    if ($list.SelectedIndices.Count -eq 0) { return }

    $selectedNames = @($list.SelectedIndices | ForEach-Object {
        $allPlugins[$_]['Name']
    })

    if ($selectedNames.Count -eq 1) {
        $g.Log.AppendText("Sync Plugin: $($selectedNames[0]) ...`r`n")
        Invoke-MaintainGuiAction -ActionName "SyncPlugins" -ExtraArgs "-PluginOnly `"$($selectedNames[0])`""
    } else {
        $joined = $selectedNames -join ','
        $g.Log.AppendText("Sync Plugins: $joined ...`r`n")
        Invoke-MaintainGuiAction -ActionName "SyncPluginsMulti" -ExtraArgs "-PluginMulti `"$joined`""
    }
}

# ---------------------------------------------------------------------------
# GUI
# ---------------------------------------------------------------------------
function Show-Gui {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    try {
        if (-not ("Win32ConsoleStable" -as [type])) {
            Add-Type @'
using System; using System.Runtime.InteropServices;
public class Win32ConsoleStable {
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]   public static extern bool ShowWindow(IntPtr h, int n);
}
'@
        }
        $h = [Win32ConsoleStable]::GetConsoleWindow()
        if ($h -ne [IntPtr]::Zero) { [void][Win32ConsoleStable]::ShowWindow($h, 0) }
    } catch {}

    $cBg     = [System.Drawing.Color]::FromArgb(24, 26, 32)
    $cPanel  = [System.Drawing.Color]::FromArgb(32, 35, 44)
    $cText   = [System.Drawing.Color]::FromArgb(235, 237, 245)
    $cMuted  = [System.Drawing.Color]::FromArgb(160, 165, 180)
    $cAccent = [System.Drawing.Color]::FromArgb(92, 140, 240)
    $cBtn    = [System.Drawing.Color]::FromArgb(50, 54, 68)
    $fUI     = New-Object System.Drawing.Font("Segoe UI", 9.5)
    $fBold   = New-Object System.Drawing.Font("Segoe UI", 9.5, [System.Drawing.FontStyle]::Bold)
    $fMono   = New-Object System.Drawing.Font("Consolas", 9)

    $form = New-Object System.Windows.Forms.Form
    $form.Text            = "Gamehelper Stabil - Wartung"
    $form.Size            = New-Object System.Drawing.Size(800, 760)
    $form.FormBorderStyle = "FixedSingle"
    $form.MaximizeBox     = $false
    $form.StartPosition   = "CenterScreen"
    $form.BackColor       = $cBg
    $form.ForeColor       = $cText
    $form.Font            = $fUI

    function New-Btn([string]$Text, [bool]$Accent = $false, [int]$W = 180, [int]$H = 36) {
        $b = New-Object System.Windows.Forms.Button
        $b.Text      = $Text
        $b.Size      = New-Object System.Drawing.Size($W, $H)
        $b.FlatStyle = "Flat"
        $b.FlatAppearance.BorderSize = 0
        $b.BackColor = if ($Accent) { $cAccent } else { $cBtn }
        $b.ForeColor = $cText
        $b.Font      = $fBold
        $b.Cursor    = [System.Windows.Forms.Cursors]::Hand
        return $b
    }

    function New-SectionLabel([string]$Text) {
        $l = New-Object System.Windows.Forms.Label
        $l.Text      = $Text
        $l.AutoSize  = $true
        $l.ForeColor = $cAccent
        $l.Font      = $fBold
        return $l
    }

    function New-SectionPanel([int]$H) {
        $p = New-Object System.Windows.Forms.Panel
        $p.Dock      = "Top"
        $p.Height    = $H
        $p.BackColor = $cPanel
        $p.Padding   = New-Object System.Windows.Forms.Padding(14, 10, 14, 10)
        return $p
    }

    # --- Statusleiste ---
    $statusPanel = New-Object System.Windows.Forms.Panel
    $statusPanel.Dock      = "Top"
    $statusPanel.Height    = 80
    $statusPanel.BackColor = [System.Drawing.Color]::FromArgb(16, 18, 24)
    $statusPanel.Padding   = New-Object System.Windows.Forms.Padding(12, 8, 12, 6)
    $statusFlow = New-Object System.Windows.Forms.FlowLayoutPanel
    $statusFlow.Dock          = "Fill"
    $statusFlow.BackColor     = [System.Drawing.Color]::FromArgb(16, 18, 24)
    $statusFlow.FlowDirection = "LeftToRight"
    $statusFlow.WrapContents  = $true
    $statusPanel.Controls.Add($statusFlow)
    $guiBadges = @{}
    foreach ($bKey in @("Version","Repo","Publish","Signatur","GitHub","Git")) {
        $bl = New-Object System.Windows.Forms.Label
        $bl.AutoSize  = $true
        $bl.Padding   = New-Object System.Windows.Forms.Padding(10,3,10,3)
        $bl.Margin    = New-Object System.Windows.Forms.Padding(0,0,8,4)
        $bl.BackColor = [System.Drawing.Color]::FromArgb(38,42,54)
        $bl.ForeColor = $cText
        $bl.Font      = New-Object System.Drawing.Font("Segoe UI", 8.5)
        $bl.Text      = $bKey
        $statusFlow.Controls.Add($bl)
        $guiBadges[$bKey] = $bl
    }

    # --- Laufend-Indikator ---
    $runningLabel = New-Object System.Windows.Forms.Label
    $runningLabel.Dock      = "Bottom"
    $runningLabel.Height    = 24
    $runningLabel.Text      = ""
    $runningLabel.BackColor = [System.Drawing.Color]::FromArgb(60, 80, 40)
    $runningLabel.ForeColor = [System.Drawing.Color]::FromArgb(170, 240, 130)
    $runningLabel.Font      = $fBold
    $runningLabel.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
    $runningLabel.Padding   = New-Object System.Windows.Forms.Padding(14,0,0,0)
    $runningLabel.Visible   = $false

    # --- Log ---
    $logLabel = New-Object System.Windows.Forms.Label
    $logLabel.Text      = "Ausgabe:"
    $logLabel.Dock      = "Bottom"
    $logLabel.Height    = 20
    $logLabel.ForeColor = $cMuted
    $logLabel.Font      = $fBold
    $logLabel.Padding   = New-Object System.Windows.Forms.Padding(14,2,0,0)
    $log = New-Object System.Windows.Forms.TextBox
    $log.Multiline   = $true
    $log.ReadOnly    = $true
    $log.ScrollBars  = "Vertical"
    $log.Dock        = "Bottom"
    $log.Height      = 130
    $log.BackColor   = [System.Drawing.Color]::FromArgb(18,22,30)
    $log.ForeColor   = [System.Drawing.Color]::FromArgb(170,220,170)
    $log.Font        = $fMono
    $log.BorderStyle = "FixedSingle"

    # --- Scroll-Container ---
    $scroll = New-Object System.Windows.Forms.Panel
    $scroll.Dock       = "Fill"
    $scroll.AutoScroll = $true
    $scroll.BackColor  = $cBg

    # --- Aktuelle Version aus csproj ---
    $currentVersion = "1.0.0"
    try {
        $csprojPath = Join-Path $Root "GameHelper\GameHelper.csproj"
        if (Test-Path $csprojPath) {
            $xml = [xml](Get-Content $csprojPath -Raw)
            $v = $xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
            if ($v) { $currentVersion = $v -replace '^v','' }
        }
    } catch {}

    # --- Changelog aus aktuellem GitHub Release vorausfuellen ---
    $existingChangelog = ""
    try {
        $repo = Get-GithubRepository
        if ($repo -notlike "*(not configured*") {
            $releaseJson = gh api "repos/$repo/releases/latest" 2>$null
            if ($releaseJson) {
                $release = $releaseJson | ConvertFrom-Json
                $body = $release.body
                if ($body) {
                    $lines = $body -split "`r?`n" | Where-Object { $_ -match '^\s*[-*]\s+' } |
                             ForEach-Object { $_ -replace '^\s*[-*]\s+','' }
                    $existingChangelog = ($lines -join "`r`n").Trim()
                }
            }
        }
    } catch {}

    # --- Abschnitt: Commit-Nachricht + Version ---
    $commitPanel = New-SectionPanel 160
    $cLabel = New-SectionLabel "Changelog (eine Zeile pro Eintrag, leer = release-notes.txt):"
    $cLabel.Location = New-Object System.Drawing.Point(14, 8)
    $commitPanel.Controls.Add($cLabel)
    $commitBox = New-Object System.Windows.Forms.TextBox
    $commitBox.Location    = New-Object System.Drawing.Point(14, 26)
    $commitBox.Size        = New-Object System.Drawing.Size(530, 88)
    $commitBox.BackColor   = [System.Drawing.Color]::FromArgb(22,25,32)
    $commitBox.ForeColor   = $cText
    $commitBox.Font        = $fUI
    $commitBox.BorderStyle = "FixedSingle"
    $commitBox.Multiline   = $true
    $commitBox.ScrollBars  = "Vertical"
    $commitBox.AcceptsReturn = $true
    $commitBox.Text        = $existingChangelog
    $commitPanel.Controls.Add($commitBox)

    $vLabel = New-SectionLabel "Version:"
    $vLabel.Location = New-Object System.Drawing.Point(14, 122)
    $commitPanel.Controls.Add($vLabel)
    $versionBox = New-Object System.Windows.Forms.TextBox
    $versionBox.Location    = New-Object System.Drawing.Point(80, 120)
    $versionBox.Size        = New-Object System.Drawing.Size(100, 22)
    $versionBox.BackColor   = [System.Drawing.Color]::FromArgb(22,25,32)
    $versionBox.ForeColor   = $cText
    $versionBox.Font        = $fUI
    $versionBox.BorderStyle = "FixedSingle"
    $versionBox.Text        = $currentVersion
    $commitPanel.Controls.Add($versionBox)

    $vHint = New-Object System.Windows.Forms.Label
    $vHint.Text      = "(leer = behalten, Timestamp triggert trotzdem Update)"
    $vHint.Location  = New-Object System.Drawing.Point(190, 123)
    $vHint.AutoSize  = $true
    $vHint.ForeColor = [System.Drawing.Color]::FromArgb(120,125,140)
    $vHint.Font      = New-Object System.Drawing.Font("Segoe UI", 8.5)
    $commitPanel.Controls.Add($vHint)

    # --- Abschnitt: Gordin sync ---
    $gordinPanel = New-SectionPanel 100
    $gl = New-SectionLabel "1a  Gordin syncen (Core + Gordin-Plugins aus Gordin/GameHelper2)"
    $gl.Location = New-Object System.Drawing.Point(14, 8)
    $gordinPanel.Controls.Add($gl)
    $btnGCore    = New-Btn "Core holen"      $false 150
    $btnGPlugins = New-Btn "Plugins holen"   $false 150
    $btnGAll     = New-Btn "Alles holen"     $false 150
    $btnGCheck   = New-Btn "Update pruefen?" $false 160
    $btnGCore.Location    = New-Object System.Drawing.Point(14, 32)
    $btnGPlugins.Location = New-Object System.Drawing.Point(174, 32)
    $btnGAll.Location     = New-Object System.Drawing.Point(334, 32)
    $btnGCheck.Location   = New-Object System.Drawing.Point(14, 70)
    $gordinPanel.Controls.AddRange(@($btnGCore, $btnGPlugins, $btnGAll, $btnGCheck))

    # --- Abschnitt: Plugin-Repos sync ---
    $pluginsPanel = New-SectionPanel 136
    $plbl = New-SectionLabel "1b  Plugin-Repos syncen (MordWraith + Upstream aus GitHub)"
    $plbl.Location = New-Object System.Drawing.Point(14, 8)
    $pluginsPanel.Controls.Add($plbl)
    $btnPAll      = New-Btn "Alle holen"            $false 150
    $btnPMord     = New-Btn "MordWraith"             $false 140
    $btnPUp       = New-Btn "Upstream"               $false 130
    $btnPSingle   = New-Btn "Einzeln..."             $false 120
    $btnPAdd      = New-Btn "+ Plugin hinzu..."      $false 160
    $btnPRem      = New-Btn "- Plugin entfernen"    $false 170
    $btnPSln      = New-Btn "SLN: Plugin eintragen" $false 200
    $btnPAll.Location    = New-Object System.Drawing.Point(14, 32)
    $btnPMord.Location   = New-Object System.Drawing.Point(174, 32)
    $btnPUp.Location     = New-Object System.Drawing.Point(324, 32)
    $btnPSingle.Location = New-Object System.Drawing.Point(464, 32)
    $btnPAdd.Location    = New-Object System.Drawing.Point(14, 70)
    $btnPRem.Location    = New-Object System.Drawing.Point(194, 70)
    $btnPSln.Location    = New-Object System.Drawing.Point(14, 106)
    $pluginsPanel.Controls.AddRange(@($btnPAll, $btnPMord, $btnPUp, $btnPSingle, $btnPAdd, $btnPRem, $btnPSln))

    # --- Abschnitt: Push + Publish ---
    $pushPanel = New-SectionPanel 72
    $pushLbl = New-SectionLabel "2  Pushen && Publish nach GitHub (MordWraith/Gamehelper)"
    $pushLbl.Location = New-Object System.Drawing.Point(14, 8)
    $pushPanel.Controls.Add($pushLbl)
    $btnPushCore = New-Btn "Source pushen"   $false 160
    $btnPublish  = New-Btn "Publish Release" $true  160
    $btnPushCore.Location = New-Object System.Drawing.Point(14, 30)
    $btnPublish.Location  = New-Object System.Drawing.Point(184, 30)
    $pushPanel.Controls.AddRange(@($btnPushCore, $btnPublish))

    # --- Abschnitt: Lokal ---
    $localPanel = New-SectionPanel 72
    $ll = New-SectionLabel "3  Lokal bauen && testen"
    $ll.Location = New-Object System.Drawing.Point(14, 8)
    $localPanel.Controls.Add($ll)
    $btnBuild  = New-Btn "Build (Release)" $true  180
    $btnRun    = New-Btn "Starten"         $false 150
    $btnStatus = New-Btn "Status"          $false 130
    $btnBuild.Location  = New-Object System.Drawing.Point(14, 30)
    $btnRun.Location    = New-Object System.Drawing.Point(204, 30)
    $btnStatus.Location = New-Object System.Drawing.Point(364, 30)
    $localPanel.Controls.AddRange(@($btnBuild, $btnRun, $btnStatus))

    # --- Scroll-Inhalt (letztes Control = ganz oben) ---
    $scroll.Controls.Clear()
    foreach ($p in @($localPanel, $pushPanel, $commitPanel, $pluginsPanel, $gordinPanel)) {
        $sep = New-Object System.Windows.Forms.Panel
        $sep.Dock = "Top"; $sep.Height = 8; $sep.BackColor = $cBg
        $scroll.Controls.Add($sep)
        $scroll.Controls.Add($p)
    }
    $titleLbl = New-Object System.Windows.Forms.Label
    $titleLbl.Text      = "Gamehelper Stabil - Gordin sync, Plugin-Repos, build, publish"
    $titleLbl.Dock      = "Top"
    $titleLbl.Height    = 28
    $titleLbl.ForeColor = $cMuted
    $titleLbl.Font      = $fUI
    $titleLbl.Padding   = New-Object System.Windows.Forms.Padding(14,6,0,0)
    $scroll.Controls.Add($titleLbl)

    $form.Controls.Add($scroll)
    $form.Controls.Add($runningLabel)
    $form.Controls.Add($log)
    $form.Controls.Add($logLabel)
    $form.Controls.Add($statusPanel)

    $script:MaintainGui = @{
        Root         = $Root
        Form         = $form
        Badges       = $guiBadges
        Log          = $log
        VersionBox   = $versionBox
        RunningLabel = $runningLabel
        CommitBox    = $commitBox
    }

    # --- Button-Handler ---
    function Invoke-Gui([string]$Act, [string]$Extra = "") {
        try { Invoke-MaintainGuiAction -ActionName $Act -ExtraArgs $Extra } catch {}
    }
    function Invoke-GuiWithCommit([string]$Act) {
        $msg = $commitBox.Text.Trim()
        if (-not $msg) {
            [System.Windows.Forms.MessageBox]::Show("Bitte eine Commit-Nachricht eingeben.",
                "Commit-Nachricht fehlt", [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
            return
        }
        try { Invoke-MaintainGuiAction -ActionName $Act -CommitMessage $msg } catch {}
    }

    # Gordin
    $btnGCore.Add_Click({    try { Invoke-Gui "SyncGordinCore" }    catch {} })
    $btnGPlugins.Add_Click({ try { Invoke-Gui "SyncGordinPlugins" } catch {} })
    $btnGAll.Add_Click({     try { Invoke-Gui "SyncGordinAll" }     catch {} })
    $btnGCheck.Add_Click({
        try {
            if ($script:MaintainGui -and $script:MaintainGui['ActionRunning']) { return }
            $log.Clear()
            $log.AppendText("Pruefe Gordin/GameHelper2 auf Updates...`r`n")
            $remoteHash = (git ls-remote https://github.com/Gordin/GameHelper2.git HEAD 2>$null) -replace '\s.*',''
            $syncFile   = Join-Path $Root ".gordin-last-sync"
            $localHash  = if (Test-Path $syncFile) { (Get-Content $syncFile -Raw).Trim() } else { "" }
            if (-not $remoteHash) {
                $log.AppendText("Kein Zugriff auf GitHub (offline?).")
            } elseif ($remoteHash -eq $localHash) {
                $log.AppendText("Gordin ist aktuell. Kein Sync noetig.`r`n($remoteHash)")
            } else {
                $log.AppendText("Gordin hat Aenderungen!`r`nRemote: $remoteHash`r`nLokal:  $(if ($localHash) { $localHash } else { '(noch nie gesynct)' })`r`n`r`nEmpfehlung: 'Alles holen' druecken.")
            }
        } catch { $log.AppendText("Fehler: $($_.Exception.Message)") }
    })

    # Plugin-Repos
    $btnPAll.Add_Click({    try { Invoke-Gui "SyncPlugins" }          catch {} })
    $btnPMord.Add_Click({   try { Invoke-Gui "SyncPluginsMordWraith" } catch {} })
    $btnPUp.Add_Click({     try { Invoke-Gui "SyncPluginsUpstream" }  catch {} })
    $btnPSingle.Add_Click({ try {
        if ($script:MaintainGui -and $script:MaintainGui['ActionRunning']) { return }
        Show-SyncSinglePluginDialog
    } catch { $log.AppendText("Fehler: $($_.Exception.Message)") } })
    $btnPAdd.Add_Click({
        try {
            if ($script:MaintainGui -and $script:MaintainGui['ActionRunning']) { return }
            Show-AddPluginDialog
        } catch { $log.AppendText("Fehler: $($_.Exception.Message)") }
    })
    $btnPRem.Add_Click({
        try {
            if ($script:MaintainGui -and $script:MaintainGui['ActionRunning']) { return }
            Show-RemovePluginDialog
        } catch { $log.AppendText("Fehler: $($_.Exception.Message)") }
    })
    $btnPSln.Add_Click({
        try {
            if ($script:MaintainGui -and $script:MaintainGui['ActionRunning']) { return }
            Show-AddExistingToSlnDialog
        } catch { $log.AppendText("Fehler: $($_.Exception.Message)") }
    })

    # Push + Publish
    $btnPushCore.Add_Click({ try { Invoke-GuiWithCommit "PushSource" } catch {} })
    $btnPublish.Add_Click({
        try {
            if ($script:MaintainGui -and $script:MaintainGui['ActionRunning']) { return }
            $msg = $commitBox.Text.Trim()
            $ver   = $versionBox.Text.Trim()
            $extra = if ($ver) { "-Version `"$ver`"" } else { "" }
            Invoke-MaintainGuiAction -ActionName "Publish" -ExtraArgs $extra -CommitMessage $msg
        } catch {}
    })

    # Lokal
    $btnBuild.Add_Click({  try { Invoke-Gui "Build" }  catch {} })
    $btnRun.Add_Click({    try { Invoke-Gui "Run" }    catch {} })
    $btnStatus.Add_Click({ try { Invoke-Gui "Status" } catch {} })

    try { Update-MaintainGuiStatus } catch {}
    try { [void]$form.ShowDialog() } catch {}
}

# ---------------------------------------------------------------------------
# Einstiegspunkt
# ---------------------------------------------------------------------------
try {
    if ($Gui -or (-not $Console -and $Action -eq "Menu")) {
        Show-Gui
    } else {
        Invoke-MaintainAction $Action
    }
} catch {
    $errMsg = $_.Exception.Message
    try { Add-Content "$env:TEMP\maintain-stable-error.log" "$(Get-Date -Format 'HH:mm:ss') FATAL: $errMsg`n$($_.ScriptStackTrace)" } catch {}
    if ($Console -or ($Action -ne "Menu" -and -not $Gui)) {
        Write-Host ""
        Write-Host "FEHLER: $errMsg" -ForegroundColor Red
        exit 1
    }
    try {
        [System.Windows.Forms.MessageBox]::Show(
            "Fehler:`n$errMsg`n`nLog: $env:TEMP\maintain-stable-error.log",
            "Gamehelper Stabil - Fehler",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    } catch {}
}

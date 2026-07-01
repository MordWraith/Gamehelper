# Quellcode nach GitHub pushen (manuell — nicht mehr Teil von rebuild-and-publish.ps1).
# Erzeugt Bulk-Commits wie "Release vX source"; bevorzugt normales git mit [Core]/[Radar]-Messages.
param(
    [string]$Repository = "Pililink/Gamehelper",
    [string]$Branch = "main",
    [string]$GitExe = "",
    [string]$Version = "",
    [string]$CommitMessage = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent

function Resolve-GitExecutable {
    param([string]$OverridePath)

    if (-not [string]::IsNullOrWhiteSpace($OverridePath)) {
        if (Test-Path $OverridePath) { return $OverridePath }
        throw "Git nicht gefunden: $OverridePath"
    }

    $cmd = Get-Command git -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        "C:\Program Files\Git\cmd\git.exe",
        "C:\Program Files\Git\bin\git.exe",
        "C:\Program Files (x86)\Git\cmd\git.exe"
    )
    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }

    throw @"
Git ist nicht installiert oder nicht im PATH.

Install:
  winget install --id Git.Git -e --source winget

Danach PowerShell NEU oeffnen und erneut ausfuehren:
  powershell -ExecutionPolicy Bypass -File scripts\push-github-source.ps1
"@
}

function Get-GitCommitIdentity {
    $user = gh api user 2>$null | ConvertFrom-Json
    if (-not $user) {
        return @{ Name = "MordWraith"; Email = "MordWraith@users.noreply.github.com" }
    }

    $email = $user.email
    if ([string]::IsNullOrWhiteSpace($email)) {
        $email = "{0}+{1}@users.noreply.github.com" -f $user.id, $user.login
    }

    return @{ Name = $user.login; Email = $email }
}

function Remove-TrackedPublishArtifacts {
    $blocked = @(
        "github.config.json",
        "update-signing.key",
        "update-signing.pub",
        "GameHelperDownloader.rar"
    )
    foreach ($path in $blocked) {
        $full = Join-Path $Root $path
        if (Test-Path $full) {
            & $script:GitExe rm --cached -f --ignore-unmatch -- $path 2>$null | Out-Null
        }
    }

    Get-ChildItem -Path $Root -Recurse -Filter "*.rar" -File -ErrorAction SilentlyContinue | ForEach-Object {
        $rel = $_.FullName.Substring($Root.Length + 1).Replace('\', '/')
        & $script:GitExe rm --cached -f --ignore-unmatch -- $rel 2>$null | Out-Null
    }
}

function Add-SourceTreeChanges {
    Invoke-Git @("add", "-u")
    $untracked = & $GitExe ls-files --others --exclude-standard
    foreach ($line in $untracked) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match '(?i)(^|/)github\.config\.json$') { continue }
        if ($line -match '(?i)\.rar$') { continue }
        if ($line -match '(?i)(^|/)update-signing\.(key|pub)$') { continue }
        Invoke-Git @("add", "--", $line)
    }
}

function Invoke-Git {
    param(
        [string[]]$GitArguments,
        [hashtable]$Identity = $null
    )

    if ($Identity) {
        & $script:GitExe -c "user.name=$($Identity.Name)" -c "user.email=$($Identity.Email)" @GitArguments
    }
    else {
        & $script:GitExe @GitArguments
    }

    if ($LASTEXITCODE -ne 0) {
        throw "git $($GitArguments -join ' ') fehlgeschlagen (exit $LASTEXITCODE)"
    }
}

$configPath = Join-Path $Root "github.config.json"
if (Test-Path $configPath) {
    $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
    if ($cfg.repository) { $Repository = $cfg.repository }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) fehlt. Install: winget install GitHub.cli"
}

$null = gh auth status 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "gh nicht eingeloggt: gh auth login"
}

$script:GitExe = Resolve-GitExecutable -OverridePath $GitExe
$identity = Get-GitCommitIdentity
Write-Host "Git: $script:GitExe" -ForegroundColor DarkGray
Write-Host "Author: $($identity.Name) <$($identity.Email)>" -ForegroundColor DarkGray

Push-Location $Root
try {
    if (-not (Test-Path (Join-Path $Root ".git"))) {
        Invoke-Git @("init", "-b", $Branch)
        Write-Host "Git-Repo initialisiert." -ForegroundColor Green
    }

    $remoteUrl = "https://github.com/$Repository.git"
    $remotes = & $GitExe remote 2>$null
    if ($remotes -notcontains "origin") {
        Invoke-Git @("remote", "add", "origin", $remoteUrl)
    }
    else {
        Invoke-Git @("remote", "set-url", "origin", $remoteUrl)
    }

    Remove-TrackedPublishArtifacts
    Add-SourceTreeChanges
    $status = & $GitExe status --porcelain
    if ($status) {
        if ([string]::IsNullOrWhiteSpace($CommitMessage)) {
            if (-not [string]::IsNullOrWhiteSpace($Version)) {
                $CommitMessage = "Release v$Version source ($((Get-Date).ToString('yyyy-MM-dd')))"
            }
            else {
                $CommitMessage = "Publish open-source tree ($((Get-Date).ToString('yyyy-MM-dd')))"
            }
        }

        Invoke-Git -Identity $identity @("commit", "-m", $CommitMessage)
    }
    else {
        Write-Host "Keine neuen Aenderungen zum Committen." -ForegroundColor DarkGray
    }

    Invoke-Git @("fetch", "origin", $Branch)
    $localHead = (& $GitExe rev-parse HEAD).Trim()
    $remoteHead = (& $GitExe rev-parse "origin/$Branch" 2>$null)
    if ($LASTEXITCODE -eq 0 -and $remoteHead -and $remoteHead.Trim() -ne $localHead) {
        Write-Host "Remote $Branch hat andere Commits - merge (lokale Aenderungen bevorzugt) ..." -ForegroundColor Yellow
        $mergeMsg = if (-not [string]::IsNullOrWhiteSpace($Version)) {
            "Merge remote $Branch (release v$Version)"
        }
        else {
            "Merge remote $Branch into local source tree"
        }

        & $script:GitExe -c "user.name=$($identity.Name)" -c "user.email=$($identity.Email)" `
            merge "origin/$Branch" -X ours -m $mergeMsg
        if ($LASTEXITCODE -ne 0) {
            $mergeHead = Join-Path $Root ".git\MERGE_HEAD"
            if (-not (Test-Path $mergeHead)) {
                throw "git merge origin/$Branch fehlgeschlagen (exit $LASTEXITCODE)"
            }

            Write-Host "Merge-Konflikte - README/CREDITS lokal uebernehmen ..." -ForegroundColor Yellow
            foreach ($doc in @("README.md", "CREDITS.md")) {
                $docPath = Join-Path $Root $doc
                if (Test-Path $docPath) {
                    Invoke-Git @("checkout", "--ours", "--", $doc)
                    Invoke-Git @("add", $doc)
                }
            }

            Invoke-Git -Identity $identity @("commit", "-m", $mergeMsg)
        }
    }

    Invoke-Git @("push", "-u", "origin", $Branch)
    Write-Host ""
    Write-Host "Quellcode gepusht: https://github.com/$Repository" -ForegroundColor Green
    Write-Host "Pruefen: https://github.com/$Repository/tree/$Branch/GameHelper" -ForegroundColor DarkGray
}
finally {
    Pop-Location
}

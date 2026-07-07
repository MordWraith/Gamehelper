# Prueft nach rebuild-and-publish, ob Release und Quellcode auf GitHub passen.
param(
    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,
    [string]$Repository = "MordWraith/Gamehelper",
    [string]$Branch = "main"
)

$ErrorActionPreference = "Stop"

$configPath = Join-Path (Split-Path $PSScriptRoot -Parent) "github.config.json"
if (Test-Path $configPath) {
    $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
    if ($cfg.repository) { $Repository = $cfg.repository }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) fehlt."
}

$tag = if ($ExpectedVersion -match '^v') { $ExpectedVersion } else { "v$ExpectedVersion" }
$failures = @()
$checks = 0

function Test-Check {
    param(
        [string]$Name,
        [bool]$Ok,
        [string]$Detail = ""
    )

    $script:checks++
    if ($Ok) {
        Write-Host ("  OK   {0}" -f $Name) -ForegroundColor Green
        if ($Detail) {
            Write-Host ("       {0}" -f $Detail) -ForegroundColor DarkGray
        }
    }
    else {
        Write-Host ("  FAIL {0}" -f $Name) -ForegroundColor Red
        if ($Detail) {
            Write-Host ("       {0}" -f $Detail) -ForegroundColor DarkYellow
        }
        $script:failures += $Name
    }
}

Write-Host ""
Write-Host "=== GitHub Publish Check (v$ExpectedVersion) ===" -ForegroundColor Cyan

# Release
$releaseJson = gh release view $tag --repo $Repository --json tagName,name,isDraft,isPrerelease 2>$null
if ($LASTEXITCODE -ne 0 -or -not $releaseJson) {
    Test-Check "GitHub Release $tag" $false "Release nicht gefunden"
}
else {
    $release = $releaseJson | ConvertFrom-Json
    Test-Check "GitHub Release $tag" ($release.tagName -eq $tag) $release.name
    Test-Check "Release ist oeffentlich" ((-not $release.isDraft) -and (-not $release.isPrerelease))
}

$assetNames = @(gh release view $tag --repo $Repository --json assets -q ".assets[].name" 2>$null)
if ($assetNames) {
    Test-Check "Asset: GameHelperDownloader.exe" ($assetNames -contains "GameHelperDownloader.exe")
    Test-Check "Asset: manifest.json" ($assetNames -contains "manifest.json")
    Test-Check "Asset: manifest.sig" ($assetNames -contains "manifest.sig")
    $fullZip = @($assetNames | Where-Object { $_ -like "GameHelper-*-full.zip" })
    Test-Check "Asset: full ZIP" ($fullZip.Count -gt 0) ($fullZip -join ", ")
}
else {
    Test-Check "Release-Assets" $false "Keine Assets lesbar"
}

# Source tree on main
$requiredPaths = @(
    "GameHelper/GameHelper.csproj",
    "Launcher",
    "scripts/build.ps1",
    "CREDITS.md",
    "SECURITY.md",
    "LICENSE"
)

foreach ($path in $requiredPaths) {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    gh api "repos/$Repository/contents/$($path -replace '\\','/')?ref=$Branch" 2>$null | Out-Null
    $pathOk = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEap
    Test-Check "main: $path" $pathOk
}

# Alle Plugins prüfen: aus plugins-sources.json + lokale Plugins-Ordner (Gordin-Plugins)
$scriptRoot    = Split-Path $PSScriptRoot -Parent
$sourcesPath   = Join-Path $PSScriptRoot "plugins-sources.json"
$pluginsDir    = Join-Path $scriptRoot "Plugins"
$knownPlugins  = [System.Collections.Generic.HashSet[string]]@()

if (Test-Path $sourcesPath) {
    $sources = Get-Content $sourcesPath -Raw | ConvertFrom-Json
    foreach ($cat in $sources.PSObject.Properties) {
        if ($cat.Name -eq "_comment") { continue }
        foreach ($p in $cat.Value.PSObject.Properties) {
            [void]$knownPlugins.Add($p.Name)
        }
    }
}

# Lokale Plugins-Ordner hinzufügen (Gordin-Plugins die nicht in plugins-sources sind)
if (Test-Path $pluginsDir) {
    Get-ChildItem $pluginsDir -Directory | ForEach-Object {
        [void]$knownPlugins.Add($_.Name)
    }
}

Write-Host ""
Write-Host "  Prüfe $($knownPlugins.Count) Plugins auf GitHub..." -ForegroundColor DarkGray
foreach ($plugin in ($knownPlugins | Sort-Object)) {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    gh api "repos/$Repository/contents/Plugins/$plugin?ref=$Branch" 2>$null | Out-Null
    $ok = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEap
    Test-Check "Plugin: $plugin" $ok
}

$prevEap = $ErrorActionPreference
$ErrorActionPreference = "SilentlyContinue"
$csprojB64 = gh api "repos/$Repository/contents/GameHelper/GameHelper.csproj?ref=$Branch" --jq ".content" 2>$null
$csprojOk = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevEap
if ($csprojOk -and $csprojB64) {
    $csprojXml = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($csprojB64 -replace "`n", "")))
    $remoteVersion = $null
    if ($csprojXml -match '<Version>([^<]+)</Version>') {
        $remoteVersion = $Matches[1]
    }

    $versionDetail = if ($remoteVersion) {
        "remote=$remoteVersion erwartet=$ExpectedVersion"
    }
    else {
        "Version-Tag nicht gefunden"
    }
    Test-Check "main: Version in GameHelper.csproj" ($remoteVersion -eq $ExpectedVersion) $versionDetail
}
else {
    Test-Check "main: Version in GameHelper.csproj" $false "csproj nicht lesbar"
}

$mainSha = (gh api "repos/$Repository/git/ref/heads/$Branch" --jq ".object.sha" 2>$null).Trim()
if ($mainSha) {
    $mainMsg = (gh api "repos/$Repository/commits/$mainSha" --jq ".commit.message" 2>$null).Trim()
    Test-Check "main: letzter Commit" $true $mainMsg
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "Alle $checks Checks bestanden." -ForegroundColor Green
    Write-Host "  Releases: https://github.com/$Repository/releases/tag/$tag" -ForegroundColor DarkGray
    Write-Host "  Source:   https://github.com/$Repository/tree/$Branch/GameHelper" -ForegroundColor DarkGray
}
else {
    Write-Host ("{0} von {1} Checks fehlgeschlagen." -f $failures.Count, $checks) -ForegroundColor Red
    throw "GitHub Publish Check fehlgeschlagen: $($failures -join ', ')"
}

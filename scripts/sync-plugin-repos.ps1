# Klont Plugin-Repos in Temp und kopiert Quelldateien nach Plugins\ (kein .git im Haupt-Repo).
# Gordin-Plugins (Radar, HealthBars, ...) werden NICHT hier beruehrt - die kommen via sync-gordin.ps1.
param(
    [string]$TargetRoot = (Split-Path $PSScriptRoot -Parent),
    [ValidateSet("MordWraith", "Upstream", "All")]
    [string]$Set = "All",
    # Nur ein einzelnes Plugin syncen (Name = Ordnername in Plugins\)
    [string]$Only = ""
)

$ErrorActionPreference = "Stop"
$SourcesPath = Join-Path $PSScriptRoot "plugins-sources.json"
if (-not (Test-Path $SourcesPath)) { throw "Fehlend: $SourcesPath" }

$sources     = Get-Content $SourcesPath -Raw | ConvertFrom-Json
$pluginsRoot = Join-Path $TargetRoot "Plugins"
New-Item -ItemType Directory -Force -Path $pluginsRoot | Out-Null

function Invoke-RobocopySource {
    param([string]$Source, [string]$Destination)
    & robocopy $Source $Destination /MIR /XD bin obj .git .vs /XF "*.dll" "*.pdb" "*.deps.json" /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    $rc = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($rc -ge 8) { throw "robocopy failed ($rc): $Source -> $Destination" }
}

function Sync-Plugin {
    param([string]$Folder, [string]$GithubRepo)

    $repoUrl = "https://github.com/$GithubRepo.git"
    $tempDir = Join-Path $env:TEMP "gh-plugin-sync-$Folder"
    $dst     = Join-Path $pluginsRoot $Folder

    Write-Host "Sync $Folder <- $GithubRepo ..." -ForegroundColor Cyan

    if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    & git clone --depth 1 $repoUrl $tempDir
    if ($LASTEXITCODE -ne 0) { throw "git clone failed: $GithubRepo" }

    # Wenn das Repo eine Plugins\<Name>\ Unterstruktur hat (Gordin-Stil), dort rein
    $repoName = ($GithubRepo -split '/')[-1]
    $subDir   = Join-Path $tempDir "Plugins\$Folder"
    $srcDir   = if (Test-Path $subDir) { $subDir } else { $tempDir }

    Invoke-RobocopySource -Source $srcDir -Destination $dst

    # csproj umbenennen wenn Repo-Name != Ordner-Name (z.B. RunecraftHelper -> RuneforgeHelper)
    if ($repoName -ne $Folder) {
        $oldCsproj = Join-Path $dst "$repoName.csproj"
        $newCsproj = Join-Path $dst "$Folder.csproj"
        if ((Test-Path $oldCsproj) -and -not (Test-Path $newCsproj)) {
            Rename-Item $oldCsproj $newCsproj
            Write-Host "  csproj umbenannt: $repoName.csproj -> $Folder.csproj" -ForegroundColor DarkGray
        }
    }

    # SHA vor dem Loeschen merken und im State-File speichern (fuer Update-Check in maintain)
    try {
        $syncedSha = (& git -C $tempDir rev-parse HEAD 2>$null).Trim()
        if ($syncedSha) {
            $statePath = Join-Path $PSScriptRoot ".plugin-sync-state.json"
            $st = if (Test-Path $statePath) { Get-Content $statePath -Raw | ConvertFrom-Json } else { [PSCustomObject]@{} }
            $st | Add-Member -NotePropertyName $Folder -NotePropertyValue $syncedSha -Force
            $st | ConvertTo-Json | Set-Content $statePath -Encoding UTF8
        }
    } catch {}

    if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }

    # Für Plugins mit lokalen csproj-Anpassungen: eigene Version nach dem Sync wiederherstellen.
    # plugins-csproj-overrides\ enthält die angepassten .csproj-Dateien die NICHT vom Upstream
    # überschrieben werden dürfen (z.B. StashValueByZx0.csproj mit lokalen ProjectReferences).
    $overrideDir  = Join-Path $PSScriptRoot "plugins-csproj-overrides"
    $overrideSrc  = Join-Path $overrideDir "$Folder.csproj"
    if (Test-Path $overrideSrc) {
        $csprojFiles = @(Get-ChildItem $dst -Filter "*.csproj" -ErrorAction SilentlyContinue)
        $csprojDst   = if ($csprojFiles.Count -gt 0) { $csprojFiles[0].FullName } else { Join-Path $dst "$Folder.csproj" }
        Copy-Item $overrideSrc $csprojDst -Force
        Write-Host "  csproj-override wiederhergestellt: $Folder" -ForegroundColor DarkCyan
    }

    # Release-Notes des neuesten Releases fuer Changelog-Vorschlag holen
    try {
        $releaseRaw = (& gh api "repos/$GithubRepo/releases/latest" 2>$null) -join ""
        if ($releaseRaw) {
            $rel  = $releaseRaw | ConvertFrom-Json
            $body = $rel.body
            if ($body) {
                # Erste Zeile jedes Absatzes (= Titel der Aenderung), max 120 Zeichen
                $blocks = $body -split "(?:\r?\n){2,}" | Where-Object { $_.Trim() }
                $titles = @(foreach ($block in $blocks) {
                    $line = ($block -split "\r?\n")[0].Trim()
                    $line = $line -replace '^[-*]\s+', ''
                    $line = ($line -replace '[^\x20-\x7E]', '').Trim()
                    if ($line -and $line.Length -le 120) { $line }
                })
                if ($titles.Count -gt 0) {
                    $pendingPath = Join-Path $PSScriptRoot ".pending-changelog.txt"
                    foreach ($title in $titles) {
                        "$Folder`: $title" | Add-Content $pendingPath -Encoding UTF8
                    }
                    Write-Host "  Changelog-Eintraege hinzugefuegt: $Folder ($($titles.Count))" -ForegroundColor DarkYellow
                }
            }
        }
    } catch {
        Write-Host "  Changelog-Fetch fehlgeschlagen: $Folder ($_)" -ForegroundColor DarkGray
    }

    Write-Host "  OK $Folder" -ForegroundColor Green
}

# Alle zutreffenden Eintraege sammeln
$map = [ordered]@{}
if ($Set -eq "MordWraith" -or $Set -eq "All") {
    foreach ($p in $sources.mordWraith.PSObject.Properties) { $map[$p.Name] = $p.Value }
}
if ($Set -eq "Upstream" -or $Set -eq "All") {
    foreach ($p in $sources.upstream.PSObject.Properties) { $map[$p.Name] = $p.Value }
}

if (-not [string]::IsNullOrWhiteSpace($Only)) {
    if (-not $map.Contains($Only)) {
        # Suche in allen Gruppen
        $allMap = [ordered]@{}
        foreach ($p in $sources.mordWraith.PSObject.Properties)  { $allMap[$p.Name] = $p.Value }
        foreach ($p in $sources.upstream.PSObject.Properties)    { $allMap[$p.Name] = $p.Value }
        if (-not $allMap.ContainsKey($Only)) { throw "Plugin '$Only' nicht in plugins-sources.json gefunden." }
        $map = [ordered]@{ $Only = $allMap[$Only] }
    } else {
        $map = [ordered]@{ $Only = $map[$Only] }
    }
}

$count = 0
foreach ($entry in ($map.GetEnumerator() | Sort-Object Name)) {
    Sync-Plugin -Folder $entry.Key -GithubRepo $entry.Value
    $count++
}

Write-Host ""
Write-Host "sync-plugin-repos complete: $count Plugin(s) synchronisiert ($Set)." -ForegroundColor Green

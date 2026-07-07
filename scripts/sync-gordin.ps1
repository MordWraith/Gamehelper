# Pull upstream Gordin/GameHelper2 into this fork (maintainer workflow).
param(
    [string]$TargetRoot = (Split-Path $PSScriptRoot -Parent),
    [ValidateSet("CoreOnly", "Plugins", "AllGordinPlugins")]
    [string]$Mode = "CoreOnly",
    [string[]]$PluginNames = @("AutoHotKeyTrigger", "Radar", "HealthBars", "PreloadAlert", "LootValue", "PickupHelper")  # nur fuer Mode=Plugins relevant
)

$ErrorActionPreference = "Stop"

function Invoke-RobocopyMirror {
    param([string]$Source, [string]$Destination)
    & robocopy $Source $Destination /MIR /XD bin obj .git .vs publish configs /XF Program.cs StashValueByZx0.csproj /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    $rc = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($rc -ge 8) { throw "robocopy failed ($rc): $Source -> $Destination" }
}

$upstream = Join-Path $env:TEMP "gordin-gh2-sync"
if (Test-Path $upstream) { Remove-Item $upstream -Recurse -Force }
git clone --depth 1 --progress https://github.com/Gordin/GameHelper2.git $upstream

# Eigene Version vor dem Sync merken
$csprojPath   = Join-Path $TargetRoot "GameHelper\GameHelper.csproj"
$overridePath = Join-Path $TargetRoot ".version-override"
$savedVersion = $null
if (Test-Path $overridePath) {
    $savedVersion = (Get-Content $overridePath -Raw).Trim()
} elseif (Test-Path $csprojPath) {
    try {
        $xml = [xml](Get-Content $csprojPath -Raw)
        $pg  = $xml.Project.PropertyGroup
        if ($pg -is [array]) { $pg = $pg[0] }
        if ($pg.Version) { $savedVersion = $pg.Version }
    } catch {}
}

$coreDirs = @("GameHelper", "GameOffsets")
if ($Mode -eq "CoreOnly" -or $Mode -eq "Plugins" -or $Mode -eq "AllGordinPlugins") {
    foreach ($dir in $coreDirs) {
        Invoke-RobocopyMirror -Source (Join-Path $upstream $dir) -Destination (Join-Path $TargetRoot $dir)
        Write-Host "Synced $dir from Gordin"
    }
}

# Version nach dem Sync wiederherstellen
if ($savedVersion -and (Test-Path $csprojPath)) {
    try {
        $xml  = [xml](Get-Content $csprojPath -Raw)
        $pgs  = $xml.Project.PropertyGroup
        if ($pgs -isnot [array]) { $pgs = @($pgs) }
        $vNode = $pgs | Where-Object { $_.Version } | Select-Object -First 1
        if ($vNode) {
            $gordinVer     = $vNode.Version
            $vNode.Version = $savedVersion
            $xml.Save($csprojPath)
        }
        foreach ($pg in $pgs) {
            if ($pg.AssemblyVersion) { $pg.AssemblyVersion = "$savedVersion.0" }
            if ($pg.FileVersion)     { $pg.FileVersion     = "$savedVersion.0" }
        }
        $xml.Save($csprojPath)
        Write-Host "Version wiederhergestellt: $gordinVer (Gordin) -> $savedVersion (stabil)" -ForegroundColor Cyan
    } catch {
        Write-Warning "Version konnte nicht wiederhergestellt werden: $_"
    }
}

if ($Mode -eq "AllGordinPlugins") {
    # Automatisch alle Plugins aus Gordins Repo erkennen
    $PluginNames = @(Get-ChildItem (Join-Path $upstream "Plugins") -Directory -ErrorAction SilentlyContinue |
        Where-Object { Get-ChildItem $_.FullName -Filter "*.csproj" -ErrorAction SilentlyContinue } |
        Select-Object -ExpandProperty Name)
    Write-Host "Gordin Plugins erkannt: $($PluginNames -join ', ')" -ForegroundColor DarkGray
}

if ($Mode -eq "Plugins" -or $Mode -eq "AllGordinPlugins") {
    $slnPath    = Join-Path $TargetRoot "GameOverlay.sln"
    $pluginsVF  = "{9FA3D6BD-1EC1-3BA5-80CB-CE02773A58D5}"
    $projType   = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}"

    foreach ($name in $PluginNames) {
        $src = Join-Path $upstream "Plugins\$name"
        $dst = Join-Path $TargetRoot "Plugins\$name"
        if (-not (Test-Path $src)) {
            Write-Warning "Gordin plugin not found: $name"
            continue
        }
        Invoke-RobocopyMirror -Source $src -Destination $dst
        Write-Host "Synced plugin $name from Gordin"

        # Automatisch in SLN eintragen wenn noch nicht vorhanden
        if (Test-Path $slnPath) {
            $slnContent = [System.IO.File]::ReadAllText($slnPath)
            $relPath = "Plugins\$name\$name.csproj"
            if ($slnContent -notmatch [regex]::Escape($relPath)) {
                $csprojActual = Get-ChildItem $dst -Filter "*.csproj" -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($csprojActual) {
                    $csprojName = [System.IO.Path]::GetFileNameWithoutExtension($csprojActual.Name)
                    $relPath    = "Plugins\$name\$csprojName.csproj"
                    $guid       = [System.Guid]::NewGuid().ToString("B").ToUpper()
                    $block      = "Project(`"$projType`") = `"$csprojName`", `"$relPath`", `"$guid`"`r`nEndProject`r`n"
                    $cfgBlock   = "		$guid.Debug|Any CPU.ActiveCfg = Debug|Any CPU`r`n		$guid.Debug|Any CPU.Build.0 = Debug|Any CPU`r`n		$guid.Release|Any CPU.ActiveCfg = Release|Any CPU`r`n		$guid.Release|Any CPU.Build.0 = Release|Any CPU`r`n"
                    $nested     = "`t`t$guid = $pluginsVF`r`n"
                    $insertPos  = $slnContent.IndexOf("`nProject(`"")
                    $slnContent = $slnContent.Insert($insertPos + 1, $block)
                    $slnContent = $slnContent.Replace("GlobalSection(ProjectConfigurationPlatforms) = postSolution`r`n", "GlobalSection(ProjectConfigurationPlatforms) = postSolution`r`n$cfgBlock")
                    $slnContent = $slnContent.Replace("GlobalSection(NestedProjects) = preSolution`r`n", "GlobalSection(NestedProjects) = preSolution`r`n$nested")
                    [System.IO.File]::WriteAllText($slnPath, $slnContent)
                    Write-Host "  -> SLN: $csprojName eingetragen" -ForegroundColor Cyan
                }
            }
        }
    }
}

$syncFile = Join-Path $TargetRoot ".gordin-last-sync"
try { (git -C $upstream rev-parse HEAD 2>$null).Trim() | Set-Content $syncFile -Encoding ASCII } catch {}

Write-Host ""
Write-Host "sync-gordin complete ($Mode)." -ForegroundColor Green

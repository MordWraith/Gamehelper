# Baut das Projekt und veroeffentlicht oeffentlich auf GitHub Releases.
param(
    [string]$Version,
    [string[]]$Changelog,
    [string]$Repository = "Pililink/Gamehelper",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [switch]$SkipDownloader,
    [switch]$FullUpload,
    [switch]$SkipRepoDocSync,
  # Letztes per-file Release vor ZIP-only (z. B. v1.1.10 Migrations-Patch).
    [switch]$LegacyManifest,
    [string]$MigrationTargetVersion = "1.2.0",
    [string]$MaxAutoUpdateVersion = "1.1.10"
)

$DownloaderRemoteName = "GameHelperDownloader.exe"
$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent

$configPath = Join-Path $Root "github.config.json"
if (Test-Path $configPath) {
    $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
    if ($cfg.repository) { $Repository = $cfg.repository }
}

if (-not $SkipBuild) {
    $buildArgs = @{ Configuration = $Configuration }
    if (-not [string]::IsNullOrEmpty($Version)) {
        $buildArgs.Version = $Version
    }
    & (Join-Path $PSScriptRoot "build.ps1") @buildArgs
}

$PublishDir = Join-Path $Root "publish"

if ([string]::IsNullOrEmpty($Version)) {
    $appExe = Join-Path $PublishDir "GameHelper.App.exe"
    if (-not (Test-Path $appExe)) { $appExe = Join-Path $PublishDir "GameHelper.exe" }
    $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($appExe)
    $Version = if ($vi.FileVersion) { ($vi.FileVersion -split '\.')[0..2] -join '.' } else { "1.0.0" }
}

$tag = if ($Version -match '^v') { $Version } else { "v$Version" }

Write-Host "=== Publish $tag nach GitHub ($Repository) ===" -ForegroundColor Cyan

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) fehlt. Installieren: winget install GitHub.cli && gh auth login"
}

function Ensure-GhKeyringAuth {
    # GH_TOKEN/GITHUB_TOKEN in der Umgebung ueberschreiben gh keyring und verursachen oft 401 beim Asset-Upload.
    foreach ($name in @('GH_TOKEN', 'GITHUB_TOKEN')) {
        $processVal = [Environment]::GetEnvironmentVariable($name, 'Process')
        $userVal = [Environment]::GetEnvironmentVariable($name, 'User')
        $machineVal = [Environment]::GetEnvironmentVariable($name, 'Machine')
        if ($processVal -or $userVal -or $machineVal) {
            Write-Host ("  Hinweis: {0} in Umgebungsvariablen gefunden - gh keyring wird stattdessen verwendet." -f $name) -ForegroundColor Yellow
            if ($userVal -or $machineVal) {
                Write-Host "  Dauerhaft entfernen: Systemsteuerung > Umgebungsvariablen > $name loeschen." -ForegroundColor DarkYellow
            }
        }
        if (Test-Path "env:$name") {
            Remove-Item "env:$name" -ErrorAction SilentlyContinue
        }
    }
}

function Test-GhPublishAuth {
    param([string]$Repo)

    Ensure-GhKeyringAuth

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    gh auth status 2>&1 | Out-Null
    $statusOk = ($LASTEXITCODE -eq 0)
    $whoami = (gh api user -q .login 2>&1 | Out-String).Trim()
    $whoOk = ($LASTEXITCODE -eq 0 -and $whoami)
    $canPush = (gh api "repos/$Repo" -q .permissions.push 2>&1 | Out-String).Trim()
    $pushOk = ($LASTEXITCODE -eq 0 -and $canPush -eq 'true')
    # Kein jq length(@) - PowerShell frisst @ und der Check schlaegt sonst faelschlich fehl.
    gh api "repos/$Repo/releases?per_page=1" 2>&1 | Out-Null
    $releasesOk = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEap

    if (-not $statusOk -or -not $whoOk) {
        throw ("GitHub-Authentifizierung fehlgeschlagen (Token abgelaufen oder nicht eingeloggt).`n" +
                "In PowerShell ausfuehren:`n  gh auth refresh -h github.com -s repo`n" +
                "oder neu anmelden:`n  gh auth login")
    }
    if (-not $pushOk -or -not $releasesOk) {
        throw ("Kein Schreibzugriff auf repos/$Repo (push=$canPush, releases=$releasesOk).`n" +
                "gh auth refresh -h github.com -s repo")
    }

    Write-Host "  GitHub: angemeldet als $whoami" -ForegroundColor DarkGray
}

Test-GhPublishAuth -Repo $Repository

function ConvertTo-PackageFileName {
    param([string]$RelativePath)
    # GitHub Releases: '/' in Pfaden wird zu '.' im Asset-Namen (~ wuerde ebenfalls zu '.' normalisiert).
    return ($RelativePath -replace '/', '.')
}

. (Join-Path $PSScriptRoot "set-version.ps1")

if (-not $Changelog -or $Changelog.Count -eq 0) {
    $Changelog = @(Get-ReleaseNotesLines -Root $Root)
    if ($Changelog.Count -gt 0) {
        Write-Host ("  Changelog aus release-notes.txt: {0} Zeilen" -f $Changelog.Count) -ForegroundColor DarkGray
    }
}

$excludePatterns = @(
    "update.config.json",
    "update.file-hashes.json",
    "update.state.json",
    "VERSION.txt",
    "VERTEILUNG-HINWEIS.txt",
    "imgui.ini",
    "price_cache.json",
    "prices.json",
    "github.config.json",
    "github.config.example.json",
    "*.pdb",
    "configs/",
    "Plugins/*/config/"
)

function Get-PublishManifestFiles {
    param(
        [string]$PublishDirectory,
        [string[]]$ExcludePatterns
    )

    $entries = @()
    Get-ChildItem -Path $PublishDirectory -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($PublishDirectory.Length + 1).Replace('\', '/')
        $skip = $false
        foreach ($pat in $ExcludePatterns) {
            if ($rel -like "*$pat*") { $skip = $true; break }
        }
        if ($rel -like "configs/*") { $skip = $true }
        if ($rel -like "*/config/*") { $skip = $true }
        if ($rel.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase)) { $skip = $true }
        if (-not $skip) {
            $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
            $entries += [ordered]@{
                path = $rel
                package = (ConvertTo-PackageFileName $rel)
                hash = $hash
                size = $_.Length
            }
        }
    }

    return $entries
}

function New-PublishZip {
    param(
        [string]$Version,
        [string]$PublishDirectory,
        [array]$FileEntries,
        [string]$OutputPath
    )

    $zipRoot = Join-Path $env:TEMP "gamehelper-zip-$Version"
    if (Test-Path $zipRoot) { Remove-Item $zipRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $zipRoot | Out-Null

    foreach ($entry in $FileEntries) {
        $local = Join-Path $PublishDirectory ($entry.path.Replace('/', '\'))
        $dest = Join-Path $zipRoot ($entry.path.Replace('/', '\'))
        $destDir = Split-Path $dest -Parent
        if ($destDir -and -not (Test-Path $destDir)) {
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        }
        Copy-Item $local $dest -Force
    }

    if (Test-Path $OutputPath) { Remove-Item $OutputPath -Force }
    Compress-Archive -Path (Join-Path $zipRoot '*') -DestinationPath $OutputPath -CompressionLevel Optimal
    if (Test-Path $zipRoot) { Remove-Item $zipRoot -Recurse -Force }
}

$filesForDiff = @(Get-PublishManifestFiles -PublishDirectory $PublishDir -ExcludePatterns $excludePatterns)
$Changelog = Merge-ReleaseChangelog -Root $Root -Repository $Repository -Version $Version -CurrentFiles $filesForDiff -UserLines $Changelog

$publishedAt = (Get-Date).ToUniversalTime().ToString("o")
Update-ChangelogHistory -Root $Root -Version $Version -Published $publishedAt -Changelog $Changelog
Copy-Item (Join-Path $Root "changelog-history.json") (Join-Path $PublishDir "changelog-history.json") -Force

# Paket-Inhalt erst NACH changelog-history.json berechnen (sonst Hash-Mismatch).
$publishFiles = @(Get-PublishManifestFiles -PublishDirectory $PublishDir -ExcludePatterns $excludePatterns)
$zipName = "GameHelper-$Version-full.zip"
$zipPath = Join-Path $env:TEMP "gamehelper-$zipName"
Write-Host "  Erstelle ZIP-Paket ($zipName) ..." -ForegroundColor DarkGray
New-PublishZip -Version $Version -PublishDirectory $PublishDir -FileEntries $publishFiles -OutputPath $zipPath
$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$zipSize = (Get-Item $zipPath).Length

$migrationMessage = @(
    "Auto-update works up to v$MaxAutoUpdateVersion. From v$MigrationTargetVersion onward, install manually via GameHelperDownloader.exe or the full ZIP from GitHub. || Auto-Update funktioniert bis v$MaxAutoUpdateVersion. Ab v$MigrationTargetVersion bitte manuell per GameHelperDownloader.exe oder ZIP von GitHub installieren."
) -join ''

# Ordner, die beim Update aus bestehenden Installationen entfernt werden sollen.
# Nur Plugins\<Name> erlaubt (wird in UpdateService validiert).
$RemoveFolders = @(
    "Plugins\RuneforgeHelper",
    "Plugins\FarmTracker",
    "Plugins\MapKillCounter",
    "Plugins\StashValue"
)

if ($LegacyManifest) {
    Write-Host "  Manifest: legacy per-file (+ Migrationshinweis fuer v$MigrationTargetVersion)" -ForegroundColor DarkYellow
    $manifest = [ordered]@{
        version = $Version
        published = $publishedAt
        changelog = @($Changelog)
        distribution = "legacy"
        remove = $RemoveFolders
        files = $publishFiles
        migration = [ordered]@{
            manualInstallVersion = $MigrationTargetVersion
            maxAutoUpdateVersion = $MaxAutoUpdateVersion
            message = $migrationMessage
        }
    }
}
else {
    $manifestFiles = @(
        $publishFiles | ForEach-Object {
            [ordered]@{ path = $_.path; hash = $_.hash }
        }
    )
    $manifest = [ordered]@{
        version = $Version
        published = $publishedAt
        changelog = @($Changelog)
        distribution = "zip"
        remove = $RemoveFolders
        package = [ordered]@{
            name = $zipName
            hash = $zipHash
            size = $zipSize
        }
        files = $manifestFiles
    }
    if ((Compare-ProjectVersion $Version $MigrationTargetVersion) -ge 0) {
        $manifest["migration"] = [ordered]@{
            manualInstallVersion = $MigrationTargetVersion
            maxAutoUpdateVersion = $MaxAutoUpdateVersion
            message = $migrationMessage
        }
        Write-Host "  Manifest: ZIP + Migrationshinweis (Auto bis v$MaxAutoUpdateVersion, manuell ab v$MigrationTargetVersion)" -ForegroundColor DarkYellow
    }
}
$manifestJson = $manifest | ConvertTo-Json -Depth 5
$manifestPath = Join-Path $env:TEMP "gamehelper-manifest.json"
$manifestJson | Set-Content $manifestPath -Encoding UTF8

Write-Host "  Signiere manifest.json ..." -ForegroundColor DarkGray
& (Join-Path $PSScriptRoot "ensure-update-signing-key.ps1")
& (Join-Path $PSScriptRoot "sign-manifest.ps1") -ManifestPath $manifestPath -Root $Root
$manifestSigPath = Join-Path (Split-Path $manifestPath -Parent) "manifest.sig"
if (-not (Test-Path $manifestSigPath)) {
    throw "manifest.sig fehlt nach dem Signieren."
}

function Ensure-GithubRepository {
    param(
        [string]$Repo,
        [string]$Root
    )

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    gh repo view $Repo 1>$null 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Erstelle oeffentliches GitHub-Repo $Repo ..." -ForegroundColor Yellow
        gh repo create $Repo --public --description "GameHelper for Path of Exile 2" --confirm 1>$null 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "GitHub-Repo $Repo konnte nicht erstellt werden (gh auth login / Berechtigung pruefen)."
        }
    }

    $repoJson = gh repo view $Repo --json isEmpty 2>$null
    $ErrorActionPreference = $prevEap
    if ($LASTEXITCODE -ne 0) { return }

    $repoInfo = $repoJson | ConvertFrom-Json
    if (-not $repoInfo.isEmpty) { return }

    Write-Host "  Leeres Repo - README anlegen (Pflicht fuer Releases)..." -ForegroundColor Yellow
    $localReadme = Join-Path $Root "README.md"
    if (Test-Path $localReadme) {
        $readme = Get-Content $localReadme -Raw -Encoding UTF8
        $readme = $readme -replace 'github\.com/[^/]+/[^/\s\)]+', "github.com/$Repo"
    }
    else {
        $readme = "# GameHelper`n`nPath of Exile 2 overlay.`n`n**Download:** https://github.com/$Repo/releases/latest/download/GameHelperDownloader.exe`n`nSee [Releases](https://github.com/$Repo/releases) for all files."
    }
    $contentB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($readme))
    $ErrorActionPreference = 'SilentlyContinue'
    gh api --method PUT "repos/$Repo/contents/README.md" -f message="Initial commit" -f content=$contentB64 1>$null 2>$null
    $ErrorActionPreference = $prevEap
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub-Repo $Repo ist leer. Bitte ein README auf github.com anlegen."
    }
}

function Sync-GithubRepoTextFile {
    param(
        [string]$Repo,
        [string]$Root,
        [string]$RelativePath,
        [string]$CommitMessage
    )

    $localPath = Join-Path $Root $RelativePath
    if (-not (Test-Path $localPath)) {
        Write-Host "  $RelativePath nicht gefunden, wird nicht synchronisiert." -ForegroundColor Yellow
        return $false
    }

    $content = Get-Content $localPath -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($content)) {
        Write-Host "  $RelativePath ist leer, wird nicht synchronisiert." -ForegroundColor Yellow
        return $false
    }

    $content = $content -replace 'github\.com/[^/]+/[^/\s\)]+', "github.com/$Repo"
    $contentB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($content))
    $apiPath = "repos/$Repo/contents/$($RelativePath -replace '\\', '/')"

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    $existing = gh api $apiPath 2>$null
    if ($LASTEXITCODE -eq 0 -and $existing) {
        $sha = ($existing | ConvertFrom-Json).sha
        gh api --method PUT $apiPath -f message=$CommitMessage -f content=$contentB64 -f sha=$sha 1>$null 2>$null
    }
    else {
        gh api --method PUT $apiPath -f message=$CommitMessage -f content=$contentB64 1>$null 2>$null
    }
    $uploaded = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEap

    if (-not $uploaded) {
        Write-Host "  Hinweis: $RelativePath konnte nicht auf GitHub aktualisiert werden ($apiPath)." -ForegroundColor Yellow
        Write-Host "  Publish/Release-Assets sind davon unabhaengig. Bei 401: gh auth refresh -s repo" -ForegroundColor DarkYellow
        return $false
    }

    Write-Host "  $RelativePath auf GitHub synchronisiert." -ForegroundColor Green
    return $true
}

function Update-GithubReadme {
    param(
        [string]$Repo,
        [string]$Root,
        [string]$ReleaseTag
    )

    $okReadme = Sync-GithubRepoTextFile -Repo $Repo -Root $Root -RelativePath "README.md" -CommitMessage "Update README for $ReleaseTag"
    $null = Sync-GithubRepoTextFile -Repo $Repo -Root $Root -RelativePath "CREDITS.md" -CommitMessage "Update CREDITS for $ReleaseTag"
    return $okReadme
}

function Test-CriticalReleaseAssets {
    param(
        [string]$Repo,
        [string]$ReleaseTag,
        [string]$DownloaderName,
        [string]$ZipName
    )

    $index = Get-GhReleaseAssetIndex -Repo $Repo -ReleaseTag $ReleaseTag
    $required = @('manifest.json', 'manifest.sig', $DownloaderName, $ZipName)
    $missing = @($required | Where-Object { -not $index.ContainsKey($_) })
    if ($missing.Count -gt 0) {
        throw ("Release $ReleaseTag unvollstaendig - fehlende kritische Assets: {0}. " -f ($missing -join ', ') +
                "Auto-Update und Downloader-Link funktionieren nicht. Publish erneut mit -SkipBuild nach gh auth refresh.")
    }

    $downloaderUrl = "https://github.com/$Repo/releases/download/$ReleaseTag/$DownloaderName"
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    curl.exe -L --fail --silent --output NUL $downloaderUrl 2>$null
    $ok = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEap
    if (-not $ok) {
        throw "Downloader nicht oeffentlich erreichbar: $downloaderUrl"
    }

    Write-Host "  Kritische Assets OK: $DownloaderName, $ZipName, manifest.json, manifest.sig" -ForegroundColor Green
}

function Get-GhReleaseAssetIndex {
    param(
        [string]$Repo,
        [string]$ReleaseTag
    )

    $index = @{}
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    $releaseRaw = gh api "repos/$Repo/releases/tags/$ReleaseTag"
    if ($LASTEXITCODE -ne 0) {
        $ErrorActionPreference = $prevEap
        return $index
    }

    $release = $releaseRaw | ConvertFrom-Json
    $assetsRaw = gh api "repos/$Repo/releases/$($release.id)/assets" --paginate
    $ErrorActionPreference = $prevEap
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($assetsRaw)) {
        return $index
    }

    $assets = $assetsRaw | ConvertFrom-Json
    if ($null -eq $assets) { return $index }
    if ($assets -isnot [array]) { $assets = @($assets) }
    foreach ($asset in $assets) {
        if ($asset.name) {
            $index[$asset.name] = $asset.id
        }
    }

    return $index
}

function Get-RemoteManifestContent {
    param([string]$Url)

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $response = Invoke-WebRequest -Uri $Url -Headers @{ 'User-Agent' = 'GameHelper-Publish' } -UseBasicParsing -TimeoutSec 45
        if (-not [string]::IsNullOrWhiteSpace($response.Content)) {
            $content = $response.Content
            if ($content.Length -gt 0 -and $content[0] -eq [char]0xFEFF) {
                $content = $content.Substring(1)
            }
            return $content
        }
    }
    catch { }

    $tmp = Join-Path $env:TEMP ("gamehelper-manifest-{0}.json" -f [guid]::NewGuid().ToString('N'))
    try {
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'SilentlyContinue'
        & curl.exe -sL -o $tmp $Url 2>$null
        $ok = ($LASTEXITCODE -eq 0)
        $ErrorActionPreference = $prevEap
        if ($ok -and (Test-Path $tmp) -and (Get-Item $tmp).Length -gt 0) {
            $content = Get-Content $tmp -Raw -Encoding UTF8
            if ($content.Length -gt 0 -and $content[0] -eq [char]0xFEFF) {
                $content = $content.Substring(1)
            }
            return $content
        }
    }
    finally {
        if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    }

    return $null
}

function Get-RemoteManifestPackageHashes {
    param(
        [string]$Repo,
        [string]$ReleaseTag
    )

    $hashes = @{}
    $tag = if ($ReleaseTag -match '^v') { $ReleaseTag } else { "v$ReleaseTag" }
    $url = "https://github.com/$Repo/releases/download/$tag/manifest.json"
    $json = Get-RemoteManifestContent -Url $url

    if ([string]::IsNullOrWhiteSpace($json)) {
        Write-Host ("  Kein Remote-Manifest fuer Hash-Vergleich ({0})." -f $tag) -ForegroundColor DarkGray
        return $hashes
    }

    try {
        $manifest = $json | ConvertFrom-Json
        if ($manifest.package -and $manifest.package.name -and $manifest.package.hash) {
            $hashes[[string]$manifest.package.name] = [string]$manifest.package.hash
        }
        elseif ($manifest.files) {
            foreach ($entry in @($manifest.files)) {
                if ($entry.package -and $entry.hash) {
                    $hashes[[string]$entry.package] = [string]$entry.hash
                }
            }
        }
    }
    catch {
        Write-Host ("  Remote-Manifest ungueltig ({0})." -f $tag) -ForegroundColor DarkYellow
    }

    return $hashes
}

function Remove-GhReleaseAssetByName {
    param(
        [string]$Repo,
        [hashtable]$AssetIndex,
        [string]$AssetName
    )

    if (-not $AssetIndex.ContainsKey($AssetName)) {
        return $false
    }

    $assetId = $AssetIndex[$AssetName]
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    gh api --method DELETE "repos/$Repo/releases/assets/$assetId" 1>$null 2>$null
    $deleted = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEap
    if ($deleted) {
        $AssetIndex.Remove($AssetName) | Out-Null
    }

    return $deleted
}

function Invoke-GhReleaseUploadBatch {
    param(
        [string]$Repo,
        [string]$ReleaseTag,
        [string[]]$BatchPaths,
        [hashtable]$AssetIndex,
        [int]$MaxAttempts = 3,
        [switch]$AllowSingleFileFallback
    )

    $lastError = ""
    $pathsToUpload = @($BatchPaths)
    $names = ($pathsToUpload | ForEach-Object { Split-Path $_ -Leaf }) -join ', '

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $output = gh release upload $ReleaseTag @pathsToUpload --repo $Repo --clobber 2>&1
        $exitCode = $LASTEXITCODE
        $ErrorActionPreference = $prevEap

        if ($exitCode -eq 0) {
            $uploaded = Get-GhReleaseAssetIndex -Repo $Repo -ReleaseTag $ReleaseTag
            foreach ($key in $uploaded.Keys) {
                $AssetIndex[$key] = $uploaded[$key]
            }
            return
        }

        $lastError = ($output | Out-String).Trim()
        $isAuthError = $lastError -match '401|Requires authentication'
        if ($isAuthError) {
            Ensure-GhKeyringAuth
        }

        if ($attempt -lt $MaxAttempts) {
            $delay = if ($isAuthError) { 3 } else { 1 }
            Write-Host ("  Upload fehlgeschlagen ({0}), erneuter Versuch in {1}s ({2}/{3}) ..." -f (Split-Path $pathsToUpload[0] -Leaf), $delay, $attempt, $MaxAttempts) -ForegroundColor DarkYellow
            Start-Sleep -Seconds $delay
        }
    }

    if ($AllowSingleFileFallback -and $pathsToUpload.Count -gt 1) {
        Write-Host ("  Batch-Upload fehlgeschlagen ({0} Dateien) - einzeln hochladen ..." -f $pathsToUpload.Count) -ForegroundColor DarkYellow
        foreach ($singlePath in $pathsToUpload) {
            Invoke-GhReleaseUploadBatch -Repo $Repo -ReleaseTag $ReleaseTag -BatchPaths @($singlePath) -AssetIndex $AssetIndex -MaxAttempts 2
        }
        return
    }

    if ([string]::IsNullOrWhiteSpace($lastError)) {
        $lastError = "gh exit code $exitCode (keine Details)"
    }
    if ($lastError -match '401|Requires authentication') {
        throw ("GitHub Release-Upload: HTTP 401 (Authentifizierung fehlgeschlagen).`n" +
                "Token vermutlich abgelaufen. In PowerShell:`n  gh auth refresh -h github.com -s repo`n" +
                "Publish danach ohne erneuten Build:`n  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version $Version -SkipBuild`n`n" +
                "Datei(en): $names`n$lastError")
    }
    throw "GitHub Release-Upload fehlgeschlagen ($names): $lastError"
}

function Invoke-GhReleaseUpload {
    param(
        [string]$Repo,
        [string]$ReleaseTag,
        [string[]]$AssetPaths,
        [hashtable]$PackageHashes,
        [string[]]$AlwaysUploadNames,
        [switch]$FullUpload,
        [int]$BatchSize = 12,
        [int]$MaxAttempts = 3
    )

    $assetIndex = Get-GhReleaseAssetIndex -Repo $Repo -ReleaseTag $ReleaseTag
    $remoteHashes = if ($FullUpload) { @{} } else { Get-RemoteManifestPackageHashes -Repo $Repo -ReleaseTag $ReleaseTag }

    $uploadQueue = New-Object System.Collections.Generic.List[string]
    $skipped = 0
    foreach ($asset in $AssetPaths) {
        $name = Split-Path $asset -Leaf
        $force = $AlwaysUploadNames -contains $name
        if (-not $FullUpload -and -not $force -and $PackageHashes.ContainsKey($name) -and $remoteHashes.ContainsKey($name) -and
            $assetIndex.ContainsKey($name) -and
            ($PackageHashes[$name].ToUpperInvariant() -eq $remoteHashes[$name].ToUpperInvariant())) {
            $skipped++
            continue
        }
        $uploadQueue.Add($asset)
    }

    Write-Host ("  Upload: {0} Dateien, {1} unveraendert uebersprungen." -f $uploadQueue.Count, $skipped) -ForegroundColor Cyan
    if ($uploadQueue.Count -eq 0) {
        Write-Host "  Alle Release-Dateien sind bereits aktuell." -ForegroundColor Green
    }
    else {
        $batchTotal = [math]::Ceiling($uploadQueue.Count / $BatchSize)
        for ($i = 0; $i -lt $uploadQueue.Count; $i += $BatchSize) {
            $batch = @($uploadQueue.GetRange($i, [math]::Min($BatchSize, $uploadQueue.Count - $i)).ToArray())
            $batchNum = [math]::Floor($i / $BatchSize) + 1
            Write-Host ("  Batch {0}/{1} ({2} Dateien) ..." -f $batchNum, $batchTotal, $batch.Count) -ForegroundColor DarkGray
            Invoke-GhReleaseUploadBatch -Repo $Repo -ReleaseTag $ReleaseTag -BatchPaths $batch -AssetIndex $assetIndex `
                -MaxAttempts $MaxAttempts -AllowSingleFileFallback
            if ($batchNum -lt $batchTotal) {
                Start-Sleep -Milliseconds 750
            }
        }
    }

    $legacyAssets = @(
        'Plugins.Autopot.Autopot.dll',
        'Plugins.Autopot.Autopot.pdb'
    )
    $finalIndex = Get-GhReleaseAssetIndex -Repo $Repo -ReleaseTag $ReleaseTag
    foreach ($legacyName in $legacyAssets) {
        if (Remove-GhReleaseAssetByName -Repo $Repo -AssetIndex $finalIndex -AssetName $legacyName) {
            Write-Host ("  Veraltetes Asset entfernt: {0}" -f $legacyName) -ForegroundColor DarkYellow
        }
    }

    foreach ($legacyName in @($finalIndex.Keys | Where-Object { $_ -like '*.pdb' })) {
        if (Remove-GhReleaseAssetByName -Repo $Repo -AssetIndex $finalIndex -AssetName $legacyName) {
            Write-Host ("  PDB entfernt: {0}" -f $legacyName) -ForegroundColor DarkYellow
        }
    }
}

function Remove-StaleReleaseAssets {
    param(
        [string]$Repo,
        [string]$ReleaseTag,
        [string[]]$KeepNames
    )

    $index = Get-GhReleaseAssetIndex -Repo $Repo -ReleaseTag $ReleaseTag
    $removed = 0
    foreach ($assetName in @($index.Keys)) {
        if ($KeepNames -contains $assetName) { continue }
        if (Remove-GhReleaseAssetByName -Repo $Repo -AssetIndex $index -AssetName $assetName) {
            $removed++
            Write-Host ("  Veraltetes Asset entfernt: {0}" -f $assetName) -ForegroundColor DarkYellow
        }
    }
    if ($removed -gt 0) {
        Write-Host ("  {0} veraltete Release-Datei(en) entfernt (ZIP-only)." -f $removed) -ForegroundColor Cyan
    }
}

function Get-ReleaseAssetNames {
    param(
        [string]$Repo,
        [string]$ReleaseTag
    )

    $index = Get-GhReleaseAssetIndex -Repo $Repo -ReleaseTag $ReleaseTag
    return @($index.Keys)
}

function Test-StagedManifestConsistency {
    param(
        [array]$ManifestEntries,
        [string]$StageDir
    )

    foreach ($entry in $ManifestEntries) {
        $package = [string]$entry.package
        if ([string]::IsNullOrWhiteSpace($package)) { continue }

        $stagedPath = Join-Path $StageDir $package
        if (-not (Test-Path $stagedPath)) {
            throw "Staging fehlt fuer Manifest-Eintrag: $package"
        }

        $actualHash = (Get-FileHash $stagedPath -Algorithm SHA256).Hash.ToUpperInvariant()
        $expectedHash = [string]$entry.hash
        if ($actualHash -ne $expectedHash.ToUpperInvariant()) {
            throw "Manifest-Hash stimmt nicht mit Staging ueberein: $package (manifest $expectedHash, staging $actualHash)"
        }
    }
}

function Test-ReleasePackageAssets {
    param(
        [string]$Repo,
        [string]$ReleaseTag,
        [array]$ManifestEntries,
        [string]$StageDir
    )

    $assetNames = Get-ReleaseAssetNames -Repo $Repo -ReleaseTag $ReleaseTag
    $missing = @()
    foreach ($entry in $ManifestEntries) {
        $package = [string]$entry.package
        if ([string]::IsNullOrWhiteSpace($package)) { continue }
        if ($assetNames -notcontains $package) {
            $missing += $package
        }
    }

    if ($missing.Count -eq 0) {
        return
    }

    Write-Host ("  {0} Manifest-Datei(en) fehlen auf dem Release - Einzel-Upload ..." -f $missing.Count) -ForegroundColor Yellow
    $assetIndex = Get-GhReleaseAssetIndex -Repo $Repo -ReleaseTag $ReleaseTag
    foreach ($package in $missing) {
        $localPath = Join-Path $StageDir $package
        if (-not (Test-Path $localPath)) {
            throw "Release-Asset fehlt lokal und remote: $package"
        }
        Invoke-GhReleaseUploadBatch -Repo $Repo -ReleaseTag $ReleaseTag -BatchPaths @($localPath) -AssetIndex $assetIndex
    }
}

function Test-StagedZipPackage {
    param(
        [string]$ZipName,
        [string]$ExpectedHash,
        [string]$StageDir
    )

    $stagedPath = Join-Path $StageDir $ZipName
    if (-not (Test-Path $stagedPath)) {
        throw "Staging fehlt fuer ZIP-Paket: $ZipName"
    }

    $actualHash = (Get-FileHash $stagedPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $ExpectedHash.ToUpperInvariant()) {
        throw "Manifest-Hash stimmt nicht mit ZIP ueberein: $ZipName (manifest $ExpectedHash, staging $actualHash)"
    }
}

Ensure-GithubRepository -Repo $Repository -Root $Root

$stageDir = Join-Path $env:TEMP "gamehelper-github-release-$tag"
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir | Out-Null

if ($LegacyManifest) {
    foreach ($entry in $publishFiles) {
        $local = Join-Path $PublishDir ($entry.path.Replace('/', '\'))
        Copy-Item $local (Join-Path $stageDir $entry.package) -Force
    }
}
Copy-Item $zipPath (Join-Path $stageDir $zipName) -Force
Copy-Item (Join-Path $PublishDir "changelog-history.json") (Join-Path $stageDir "changelog-history.json") -Force
Copy-Item $manifestPath (Join-Path $stageDir "manifest.json") -Force
Copy-Item $manifestSigPath (Join-Path $stageDir "manifest.sig") -Force

if (-not $SkipDownloader) {
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        Write-Host ""
        Write-Host "=== GameHelperDownloader bauen ===" -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot "build-downloader.ps1") -Configuration $Configuration
        $downloaderExe = Join-Path $Root $DownloaderRemoteName
        if (-not (Test-Path $downloaderExe)) {
            throw "GameHelperDownloader.exe fehlt nach dem Build."
        }
        Copy-Item $downloaderExe (Join-Path $stageDir $DownloaderRemoteName) -Force
    }
    else {
        Write-Host "Downloader-Build uebersprungen (nur unter Windows)." -ForegroundColor Yellow
    }
}

$assetPaths = @(Get-ChildItem $stageDir -File | ForEach-Object { $_.FullName })
$distLabel = if ($LegacyManifest) { "legacy per-file + ZIP" } else { "ZIP-only" }
Write-Host ("  {0} Release-Assets vorbereitet ({1})." -f $assetPaths.Count, $distLabel) -ForegroundColor DarkGray
if ($LegacyManifest) {
    Test-StagedManifestConsistency -ManifestEntries $publishFiles -StageDir $stageDir
}
else {
    Test-StagedZipPackage -ZipName $zipName -ExpectedHash $zipHash -StageDir $stageDir
}
Write-Host "  Staging/Manifest Hash-Check OK." -ForegroundColor DarkGray

$packageHashes = @{ $zipName = $zipHash }
if ($LegacyManifest) {
    foreach ($entry in $publishFiles) {
        $packageHashes[$entry.package] = [string]$entry.hash
    }
}
$alwaysUploadNames = @(
    'manifest.json',
    'manifest.sig',
    $zipName,
    $DownloaderRemoteName,
    'changelog-history.json'
)

# GitHub release notes: English half only (manifest keeps full bilingual lines).
$notes = ($Changelog | ForEach-Object {
    $en = $_
    if ($_ -match ' \|\| ') { $en = ($_ -split ' \|\| ', 2)[0].Trim() }
    "- $en"
}) -join "`n"
$notesPath = Join-Path $env:TEMP "gamehelper-github-notes-$tag.md"
$notes | Set-Content $notesPath -Encoding UTF8

$releaseExists = $false
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'SilentlyContinue'
gh release view $tag --repo $Repository 1>$null 2>$null
if ($LASTEXITCODE -eq 0) { $releaseExists = $true }
$ErrorActionPreference = $prevEap

if (-not $releaseExists) {
    Write-Host "  Erstelle Release $tag ..." -ForegroundColor Cyan
    gh release create $tag --repo $Repository --title "GameHelper $tag" --notes-file $notesPath --latest
    if ($LASTEXITCODE -ne 0) { throw "gh release create fehlgeschlagen" }
}
else {
    Write-Host "  Aktualisiere bestehendes Release $tag ..." -ForegroundColor Cyan
    gh release edit $tag --repo $Repository --notes-file $notesPath --title "GameHelper $tag"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Hinweis: Release Notes konnten nicht aktualisiert werden." -ForegroundColor Yellow
        Write-Host "  Bei 401 Unauthorized: gh auth refresh -s repo" -ForegroundColor DarkYellow
    }
}

$manifestStagePath = Join-Path $stageDir "manifest.json"
$assetPathsWithoutManifest = @($assetPaths | Where-Object { $_ -ne $manifestStagePath })
$alwaysUploadWithoutManifest = @($alwaysUploadNames | Where-Object { $_ -ne 'manifest.json' })

$uploadArgs = @{
    Repo              = $Repository
    ReleaseTag        = $tag
    AssetPaths        = $assetPathsWithoutManifest
    PackageHashes     = $packageHashes
    AlwaysUploadNames = $alwaysUploadWithoutManifest
}
if ($FullUpload) { $uploadArgs.FullUpload = $true }
Invoke-GhReleaseUpload @uploadArgs

if ($LegacyManifest) {
    Test-ReleasePackageAssets -Repo $Repository -ReleaseTag $tag -ManifestEntries $publishFiles -StageDir $stageDir
}

Write-Host "  manifest.json zuletzt hochladen (erst wenn Pakete auf dem Release sind) ..." -ForegroundColor DarkGray
$manifestAssetIndex = Get-GhReleaseAssetIndex -Repo $Repository -ReleaseTag $tag
Invoke-GhReleaseUploadBatch -Repo $Repository -ReleaseTag $tag -BatchPaths @($manifestStagePath) -AssetIndex $manifestAssetIndex

if (-not $LegacyManifest) {
    Remove-StaleReleaseAssets -Repo $Repository -ReleaseTag $tag -KeepNames @(
        'manifest.json',
        'manifest.sig',
        $zipName,
        $DownloaderRemoteName,
        'changelog-history.json'
    )
}

Test-CriticalReleaseAssets -Repo $Repository -ReleaseTag $tag -DownloaderName $DownloaderRemoteName -ZipName $zipName

Write-Host ""
Write-Host "Pruefe manifest.json (oeffentlich)..." -ForegroundColor Cyan
$manifestUrlLatest = "https://github.com/$Repository/releases/latest/download/manifest.json"
$manifestUrlTag = "https://github.com/$Repository/releases/download/$tag/manifest.json"
$remoteManifest = $null
$maxVerifyAttempts = 10

for ($attempt = 1; $attempt -le $maxVerifyAttempts; $attempt++) {
    $verifyUrl = if ($attempt -le 4) { $manifestUrlTag } else { $manifestUrlLatest }
    $verifyJson = Get-RemoteManifestContent -Url $verifyUrl

    if (-not [string]::IsNullOrWhiteSpace($verifyJson)) {
        try {
            $parsed = $verifyJson | ConvertFrom-Json
            if ($parsed.version) {
                $remoteManifest = $parsed
                break
            }
        }
        catch { }
    }

    if ($attempt -lt $maxVerifyAttempts) {
        $delay = if ($attempt -le 3) { 4 } else { 6 }
        Write-Host ("  Manifest noch nicht bereit, warte {0}s (Versuch {1}/{2})..." -f $delay, $attempt, $maxVerifyAttempts) -ForegroundColor DarkYellow
        Start-Sleep -Seconds $delay
    }
}

if (-not $remoteManifest) {
    $assetIndex = Get-GhReleaseAssetIndex -Repo $Repository -ReleaseTag $tag
    if ($assetIndex.ContainsKey('manifest.json') -and $assetIndex.ContainsKey('manifest.sig')) {
        Write-Host "  Warnung: manifest.json ist im Release, oeffentlicher Download noch nicht verifizierbar (GitHub CDN)." -ForegroundColor Yellow
        Write-Host "  Release ist trotzdem nutzbar; Auto-Update greift nach kurzer Verzoegerung." -ForegroundColor DarkYellow
        $localManifestJson = Get-Content $manifestPath -Raw -Encoding UTF8
        $remoteManifest = $localManifestJson | ConvertFrom-Json
    }
    else {
        throw "Manifest-Verifikation fehlgeschlagen: manifest.json nicht lesbar unter $manifestUrlTag"
    }
}

if ($remoteManifest.version -ne $Version) {
    Write-Host ("  Warnung: Remote-Version '{0}', erwartet '{1}'." -f $remoteManifest.version, $Version) -ForegroundColor Yellow
}
else {
    $pkgName = if ($remoteManifest.package) { $remoteManifest.package.name } else { "legacy per-file" }
    Write-Host ("  Manifest OK: v{0}, distribution={1}, package={2}" -f $Version, $remoteManifest.distribution, $pkgName) -ForegroundColor Green
}

$updateState = [ordered]@{
    lastPublished = $remoteManifest.published
    lastVersion   = $Version
    source        = "github-publish"
}
$updateState | ConvertTo-Json | Set-Content (Join-Path $PublishDir "update.state.json") -Encoding UTF8

. (Join-Path $PSScriptRoot "set-version.ps1")
Write-BuildInfoFiles -PublishDir $PublishDir -Version $Version -Source "github-publish"

if (-not $SkipRepoDocSync) {
    Write-Host "  Aktualisiere README auf GitHub ..." -ForegroundColor DarkGray
    Update-GithubReadme -Repo $Repository -Root $Root -ReleaseTag $tag
}

$downloaderUrl = "https://github.com/$Repository/releases/latest/download/$DownloaderRemoteName"
$zipUrl = "https://github.com/$Repository/releases/latest/download/GameHelper-$Version-full.zip"
$releaseUrl = "https://github.com/$Repository/releases/tag/$tag"

Write-Host ""
$publishMode = if ($LegacyManifest) { "legacy per-file (Migrations-Patch)" } else { "ZIP-only" }
Write-Host ("Publish abgeschlossen: {0} ({1}, {2} Release-Assets)" -f $tag, $publishMode, $assetPaths.Count) -ForegroundColor Green
Write-Host "GitHub Repo:     https://github.com/$Repository" -ForegroundColor Green
Write-Host "Release:         $releaseUrl" -ForegroundColor Green
Write-Host "Downloader:      $downloaderUrl" -ForegroundColor Green
Write-Host "Komplett-ZIP:    $zipUrl" -ForegroundColor Green
Write-Host "Manifest:        $manifestUrlLatest" -ForegroundColor DarkGray
Write-Host ""
if ($LegacyManifest) {
    Write-Host "Hinweis: Letztes per-file Release - Nutzer werden auf manuelles v$MigrationTargetVersion hingewiesen." -ForegroundColor Cyan
}
else {
    Write-Host "Hinweis: Release enthaelt nur ZIP + Downloader + Manifest (keine Einzel-DLLs mehr)." -ForegroundColor Cyan
}
Write-Host "Freunde: GameHelperDownloader.exe oder ZIP teilen (kein Token noetig)." -ForegroundColor Cyan

Clear-ReleaseNotes -Root $Root

exit 0

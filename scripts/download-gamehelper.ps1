# Laedt die aktuelle GameHelper-Version von GitHub Releases in einen Zielordner.
param(
    [string]$TargetDir = (Join-Path (Get-Location) "GameHelper"),
    [string]$Repository = "Pililink/Gamehelper",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent

$configPath = Join-Path $Root "github.config.json"
if (Test-Path $configPath) {
    $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
    if ($cfg.repository) { $Repository = $cfg.repository }
}

$manifestUrl = "https://github.com/$Repository/releases/latest/download/manifest.json"
$manifestSigUrl = "https://github.com/$Repository/releases/latest/download/manifest.sig"

function Test-SafeRelativePath {
    param([string]$RelativePath)
    if ([string]::IsNullOrWhiteSpace($RelativePath)) { return $false }
    if ([System.IO.Path]::IsPathRooted($RelativePath)) { return $false }
    $normalized = $RelativePath.Replace('\', '/').Trim()
    foreach ($segment in $normalized.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)) {
        if ($segment -eq '..') { return $false }
    }
    return $true
}

function Get-FileSha256Hex {
    param([string]$Path)
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash
}

function Invoke-DownloadFile {
    param(
        [string]$Url,
        [string]$DestinationPath,
        [int]$MaxAttempts = 5
    )

    $dir = Split-Path $DestinationPath -Parent
    if ($dir -and -not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Url -OutFile $DestinationPath -UseBasicParsing
            return
        }
        catch {
            if ($attempt -ge $MaxAttempts) { throw }
            $waitSec = [math]::Min(30, [math]::Pow(2, $attempt))
            Write-Host "  Netzwerkfehler, warte ${waitSec}s ..." -ForegroundColor Yellow
            Start-Sleep -Seconds $waitSec
        }
    }
}

function Test-ManifestSignature {
    param(
        [string]$ManifestJson,
        [string]$SignatureBase64
    )

    $publicKeySource = Join-Path $Root "Shared\UpdateSigningPublicKey.cs"
    if (-not (Test-Path $publicKeySource)) {
        throw "UpdateSigningPublicKey.cs fehlt."
    }

    $content = Get-Content $publicKeySource -Raw
    if ($content -notmatch 'Pem = @"([\s\S]*?)";') {
        throw "Oeffentlicher Signatur-Schluessel ist nicht konfiguriert."
    }

    $publicPem = $Matches[1].Trim()
    if ([string]::IsNullOrWhiteSpace($publicPem)) {
        throw "Oeffentlicher Signatur-Schluessel ist leer. Fuehre scripts/ensure-update-signing-key.ps1 aus und publiziere neu."
    }

    $rsa = [System.Security.Cryptography.RSA]::Create()
    $rsa.ImportFromPem($publicPem)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($ManifestJson))
    }
    finally {
        $sha.Dispose()
    }
    $signature = [Convert]::FromBase64String($SignatureBase64.Trim())
    if (-not $rsa.VerifyHash($hash, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
        throw "Manifest-Signatur ungueltig."
    }
}

if (-not $Force -and (Test-Path $TargetDir) -and (Get-ChildItem $TargetDir -Force | Select-Object -First 1)) {
    throw "Zielordner ist nicht leer: $TargetDir (nutze -Force)"
}

Write-Host "=== GameHelper Download (GitHub) ===" -ForegroundColor Cyan
Write-Host "Manifest: $manifestUrl" -ForegroundColor DarkGray
Write-Host "Ziel:     $TargetDir" -ForegroundColor DarkGray
Write-Host ""

$manifestResponse = Invoke-WebRequest -Uri $manifestUrl -Headers @{ "User-Agent" = "GameHelper-Downloader/1.0" } -UseBasicParsing
$manifestJson = $manifestResponse.Content
$signatureResponse = Invoke-WebRequest -Uri $manifestSigUrl -Headers @{ "User-Agent" = "GameHelper-Downloader/1.0" } -UseBasicParsing
Test-ManifestSignature -ManifestJson $manifestJson -SignatureBase64 $signatureResponse.Content

$manifest = $manifestJson | ConvertFrom-Json
$version = $manifest.version
$files = $manifest.files
if ([string]::IsNullOrWhiteSpace($version) -or -not $files -or $files.Count -eq 0) {
    throw "manifest.json ist ungueltig oder leer."
}

Write-Host "Version: $version ($($files.Count) Dateien)" -ForegroundColor Green
$targetFull = [System.IO.Path]::GetFullPath($TargetDir)
New-Item -ItemType Directory -Path $targetFull -Force | Out-Null

$tag = if ($version -match '^v') { $version } else { "v$version" }
$failed = @()
$hashCatalog = @{}
$index = 0

foreach ($entry in $files) {
    $index++
    $relativePath = $entry.path
    $expectedHash = $entry.hash
    $packageName = if ($entry.package) { $entry.package } else { $relativePath -replace '/', '.' }
    if ([string]::IsNullOrWhiteSpace($relativePath) -or [string]::IsNullOrWhiteSpace($expectedHash)) { continue }
    if (-not (Test-SafeRelativePath $relativePath)) {
        $failed += $relativePath
        Write-Host "  [$index/$($files.Count)] FEHLER: $relativePath - unsicherer Pfad" -ForegroundColor Red
        continue
    }

    $destPath = [System.IO.Path]::GetFullPath((Join-Path $targetFull ($relativePath -replace '/', '\')))
    if (-not $destPath.StartsWith($targetFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        $failed += $relativePath
        Write-Host "  [$index/$($files.Count)] FEHLER: $relativePath - Pfad ausserhalb des Zielordners" -ForegroundColor Red
        continue
    }

    $hashCatalog[$relativePath.Replace('\', '/')] = $expectedHash

    if ((Test-Path $destPath) -and ((Get-FileSha256Hex $destPath) -eq $expectedHash)) {
        Write-Host "  [$index/$($files.Count)] uebersprungen: $relativePath" -ForegroundColor DarkGray
        continue
    }

    $url = "https://github.com/$Repository/releases/download/$tag/$packageName"
    try {
        Invoke-DownloadFile -Url $url -DestinationPath $destPath
        $actualHash = Get-FileSha256Hex $destPath
        if ($actualHash -ne $expectedHash) {
            throw "Hash stimmt nicht (ist $actualHash, erwartet $expectedHash)"
        }
        Write-Host "  [$index/$($files.Count)] OK: $relativePath" -ForegroundColor Green
    }
    catch {
        $failed += $relativePath
        Write-Host "  [$index/$($files.Count)] FEHLER: $relativePath - $_" -ForegroundColor Red
    }
}

$hashCatalog | ConvertTo-Json | Set-Content (Join-Path $targetFull "update.file-hashes.json") -Encoding UTF8
"GameHelper $version`nDownloaded: $((Get-Date).ToUniversalTime().ToString('o'))" | Set-Content (Join-Path $targetFull "VERSION.txt") -Encoding UTF8

if ($failed.Count -gt 0) {
    throw "Download mit Fehlern beendet ($($failed.Count) Datei(en))."
}

Write-Host ""
Write-Host "Fertig: $targetFull" -ForegroundColor Green
Write-Host "Starten: $(Join-Path $targetFull 'GameHelper.exe')" -ForegroundColor Green

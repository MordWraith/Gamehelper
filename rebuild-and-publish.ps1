# GameHelper: komplett neu bauen und oeffentlich auf GitHub Releases hochladen.
# Doppelklick auf rebuild-and-publish.bat oder:
#   powershell -ExecutionPolicy Bypass -File rebuild-and-publish.ps1
#
# Optional (ohne Abfrage):
#   -Version 1.0.2
#   -SkipUpload
#   -PushSource          # Quellcode manuell committen und pushen (nicht Standard)
#   -FullUpload
#   -Configuration Debug

param(
    [string]$Version,
    [string[]]$Changelog,
    [switch]$SkipUpload,
    [switch]$PushSource,
    [switch]$FullUpload,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
. (Join-Path $Root "scripts\set-version.ps1")

$BuildScript = Join-Path $Root "scripts\build.ps1"
$PublishScript = Join-Path $Root "scripts\publish.ps1"
$SourcePushScript = Join-Path $Root "scripts\push-github-source.ps1"
$VerifyScript = Join-Path $Root "scripts\verify-github-publish.ps1"

if (-not (Test-Path $BuildScript)) {
    Write-Error "build.ps1 nicht gefunden. Bitte im Gamehelper-Projektordner starten."
}

$started = Get-Date
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " GameHelper Rebuild + Publish" -ForegroundColor Cyan
Write-Host " $(Get-Date -Format 'dd.MM.yyyy HH:mm:ss')" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Prompt-VersionInput -Root $Root
}
elseif (-not (Test-VersionFormat $Version)) {
    throw "Ungueltige Version '$Version'. Format: x.y.z"
}

Write-Host ""
Write-Host "Setze Projekt-Version auf $Version ..." -ForegroundColor Cyan
Set-ProjectVersion -Root $Root -Version $Version

if (-not $Changelog -or $Changelog.Count -eq 0) {
    $fromFile = Get-ReleaseNotesLines -Root $Root
    if ($fromFile.Count -gt 0) {
        $Changelog = $fromFile
        Write-Host ("  Changelog aus release-notes.txt: {0} Zeile(n)" -f $Changelog.Count) -ForegroundColor DarkGray
    }
    else {
        $Changelog = Prompt-ChangelogInput
        if ($Changelog.Count -gt 0) {
            $Changelog | Set-Content (Join-Path $Root "release-notes.txt") -Encoding UTF8
        }
    }
}

if ($SkipUpload) {
    Write-Host ""
    Write-Host "Modus: Nur Build (kein Upload)" -ForegroundColor Yellow
    & $BuildScript -Configuration $Configuration -Version $Version
}
else {
    if (-not (Test-Path $PublishScript)) {
        Write-Error "publish.ps1 nicht gefunden."
    }

    Write-Host ""
    Write-Host "Modus: Build v$Version + GitHub-Release" -ForegroundColor Yellow
    $publishArgs = @{ Configuration = $Configuration; Version = $Version }
    if ($Changelog -and $Changelog.Count -gt 0) {
        $publishArgs.Changelog = $Changelog
    }
    if ($FullUpload) {
        $publishArgs.FullUpload = $true
    }
    if ($PushSource) {
        $publishArgs.SkipRepoDocSync = $true
    }

    & $PublishScript @publishArgs

    if ($PushSource) {
        if (-not (Test-Path $SourcePushScript)) {
            Write-Error "push-github-source.ps1 nicht gefunden."
        }

        Write-Host ""
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host " Quellcode -> GitHub main" -ForegroundColor Cyan
        Write-Host "========================================" -ForegroundColor Cyan
        & $SourcePushScript -Version $Version

        if (Test-Path $VerifyScript) {
            Write-Host ""
            & $VerifyScript -ExpectedVersion $Version
        }
    }
    else {
        Write-Host ""
        Write-Host "Hinweis: Quellcode wird nicht automatisch gepusht." -ForegroundColor Yellow
        Write-Host "  Commits manuell mit aussagekraeftigen Messages, z.B. [Core] ..., [Radar] ..." -ForegroundColor Yellow
        Write-Host "  Optional: -PushSource oder scripts\push-github-source.ps1" -ForegroundColor DarkGray
    }
}

$publishDir = Join-Path $Root "publish"
if (-not (Test-Path (Join-Path $publishDir "GameHelper.exe"))) {
    throw "GameHelper.exe fehlt in publish\. Build unvollstaendig."
}

$elapsed = (Get-Date) - $started
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " Fertig: v$Version in $([math]::Round($elapsed.TotalSeconds, 1))s" -ForegroundColor Green
Write-Host " Ordner:  $publishDir" -ForegroundColor Green
Write-Host " Starten: $publishDir\GameHelper.exe" -ForegroundColor Green
Write-Host " Version: $publishDir\VERSION.txt" -ForegroundColor Green
if (-not $SkipUpload) {
    Write-Host " Releases:    https://github.com/Pililink/Gamehelper/releases" -ForegroundColor Green
    Write-Host " Downloader:  https://github.com/Pililink/Gamehelper/releases/latest/download/GameHelperDownloader.exe" -ForegroundColor Green
    if ($PushSource) {
        Write-Host " Source:      https://github.com/Pililink/Gamehelper/tree/main" -ForegroundColor Green
    }
}
Write-Host ""
Write-Host " Fuer deinen Freund:" -ForegroundColor Yellow
Write-Host "   - Nur GameHelperDownloader.exe teilen (oeffentlicher GitHub-Link oben)" -ForegroundColor Yellow
Write-Host "   - In LEEREN Ordner installieren lassen" -ForegroundColor Yellow
Write-Host "   - Kein Token / keine ZIP noetig" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Green
Write-Host ""

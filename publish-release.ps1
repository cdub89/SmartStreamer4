param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"

# Two-phase release script. Each phase is short, idempotent, and re-runnable.
# Human gates (live test, push tag, finalize release notes) sit BETWEEN phases,
# never inside a single script invocation -- pausing a long-running PowerShell
# session is fragile.
#
# Phase 1 (default):  .\publish-release.ps1
#   Tag at HEAD -> tests -> build -> embed-version gate -> zip -> SHA256
#   -> append/replace line in artifacts\release\SHA256SUMS.txt (uncommitted).
#
# Phase 2 (-Publish): .\publish-release.ps1 -Publish
#   Precondition checks (tag on origin, zip present, SHA256SUMS entry matches,
#   release notes file present + non-empty) -> commit + push SHA256SUMS bump
#   -> gh release create with --latest.
#
# Between phases, the human:
#   1. Live-tests the zip.
#   2. Pushes the tag:  git push origin <tag>.
#   3. Confirms RELEASE_NOTES-<tag>.md is finalized.
#
# Version source: the git tag at HEAD (e.g. v0.1.18b, v0.1.17b1). csproj
# <Version> stays at a clean numeric default; MSBuild's condition evaluator
# OOMs on any non-numeric character in <Version> (trailing 'b', '-b', etc.),
# so release labels are kept out of the csproj entirely.

$projectPath = Join-Path $PSScriptRoot "SmartSDRIQStreamer.csproj"
$publishDir = Join-Path $PSScriptRoot "bin/$Configuration/net8.0-windows/$Runtime/publish"
$sumsPath = Join-Path $PSScriptRoot "artifacts/release/SHA256SUMS.txt"

Write-Host "== SmartStreamer4 release publish ==" -ForegroundColor Cyan
Write-Host "Phase:          $(if ($Publish) { 'PUBLISH (gh release create)' } else { 'BUILD (zip + SHA256)' })"
Write-Host "Project:        $projectPath"
Write-Host "Runtime:        $Runtime"
Write-Host "Configuration:  $Configuration"

$tag = (& git describe --tags --exact-match HEAD 2>$null)
if (-not $tag) {
    Write-Host "`nERROR: HEAD has no release tag." -ForegroundColor Red
    Write-Host "Tag the release first, then re-run, e.g.:" -ForegroundColor Yellow
    Write-Host "  git tag v0.1.18b" -ForegroundColor White
    Write-Host "  .\publish-release.ps1" -ForegroundColor White
    exit 1
}
if ($tag -notmatch '^v\d+\.\d+\.\d+(b\d*)?$') {
    Write-Host "`nERROR: tag '$tag' does not match v<major>.<minor>.<patch>[b[<patch-num>]] (e.g. v0.1.18b, v0.1.17b1)." -ForegroundColor Red
    exit 1
}
$releaseLabel = $tag.Substring(1)   # strip leading 'v' for the embedded version string (SemVer convention)
$sha = (& git rev-parse HEAD).Substring(0, 8).ToLowerInvariant()
$infoVersion = "${releaseLabel}+${sha}"
$zipLabel = "SmartStreamer4-${tag}-${Runtime}.zip"
$zipPath = Join-Path $publishDir $zipLabel
$notesPath = Join-Path $PSScriptRoot "RELEASE_NOTES-${tag}.md"

Write-Host "Release tag:    $tag"
Write-Host "Embed version:  $infoVersion"
Write-Host "Zip:            $zipLabel"
Write-Host "Notes file:     RELEASE_NOTES-${tag}.md"

# -----------------------------------------------------------------------------
# Phase 2: Publish to GitHub.
# -----------------------------------------------------------------------------
if ($Publish) {
    Write-Host "`n[1/4] Checking preconditions..." -ForegroundColor Yellow

    $remoteTag = (& git ls-remote --tags origin "refs/tags/$tag" 2>$null)
    if (-not $remoteTag) {
        Write-Host "  ERROR: tag '$tag' not on origin. Push it first:  git push origin $tag" -ForegroundColor Red
        exit 1
    }
    Write-Host "  Tag on origin:           OK"

    if (-not (Test-Path $zipPath)) {
        Write-Host "  ERROR: '$zipLabel' not found at $publishDir." -ForegroundColor Red
        Write-Host "         Run the build phase first:  .\publish-release.ps1" -ForegroundColor Red
        exit 1
    }
    Write-Host "  Zip present:             OK"

    $actualHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
    $expectedLine = "$actualHash  $zipLabel"
    $sumsLines = if (Test-Path $sumsPath) { Get-Content $sumsPath } else { @() }
    if ($sumsLines -notcontains $expectedLine) {
        Write-Host "  ERROR: SHA256SUMS.txt missing matching entry for $zipLabel." -ForegroundColor Red
        Write-Host "         Expected line: $expectedLine" -ForegroundColor Red
        Write-Host "         Re-run the build phase to refresh." -ForegroundColor Red
        exit 1
    }
    Write-Host "  SHA256SUMS entry match:  OK"

    if (-not (Test-Path $notesPath) -or (Get-Item $notesPath).Length -eq 0) {
        Write-Host "  ERROR: RELEASE_NOTES-${tag}.md missing or empty." -ForegroundColor Red
        Write-Host "         Author release notes at $notesPath then re-run." -ForegroundColor Red
        exit 1
    }
    Write-Host "  Release notes present:   OK"

    Write-Host "`n[2/4] Committing SHA256SUMS.txt (if changed)..." -ForegroundColor Yellow
    # Idempotent: if the bump was already committed (e.g. publish phase ran before
    # and failed at gh release create), skip the commit step and proceed.
    $sumsStatus = (& git status --porcelain "artifacts/release/SHA256SUMS.txt")
    if ($sumsStatus) {
        & git add "artifacts/release/SHA256SUMS.txt"
        & git commit -m "Add SHA256 for $tag release"
        Write-Host "  Committed."
    } else {
        Write-Host "  No changes to commit (already up to date)."
    }

    Write-Host "`n[3/4] Pushing commit to origin..." -ForegroundColor Yellow
    & git push origin HEAD

    Write-Host "`n[4/4] Creating GitHub release..." -ForegroundColor Yellow
    # --latest is hard-coded. The 'b' suffix on beta tags has tricked the wrong-flag
    # mistake (--prerelease) twice before; this script does not expose that choice.
    & gh release create $tag $zipPath `
        --title "SmartStreamer4 $tag" `
        --notes-file $notesPath `
        --latest

    Write-Host "`nDone." -ForegroundColor Green
    Write-Host "`nPost-publish (manual):" -ForegroundColor Yellow
    Write-Host "  Install the prior release on a tester PC, confirm it sees $tag as an available update." -ForegroundColor White
    exit 0
}

# -----------------------------------------------------------------------------
# Phase 1: Build, verify, zip, hash, append SHA256SUMS.
# -----------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Host "`n[1/6] Running tests..." -ForegroundColor Yellow
    dotnet test
} else {
    Write-Host "`n[1/6] Skipping tests (--SkipTests)." -ForegroundColor Yellow
}

Write-Host "`n[2/6] Publishing self-contained single-file executable..." -ForegroundColor Yellow
# -p:InformationalVersion plumbs the git tag + commit sha into the published exe.
# The in-app About display and the update check both read this attribute at
# runtime; getting it wrong would either show v0.1.0 forever or report
# "update available" against every newer release indefinitely.
#
# -p:IncludeSourceRevisionInInformationalVersion=false suppresses the .NET SDK's
# default behavior of auto-appending the detected SourceRevisionId (full 40-char
# git sha) to InformationalVersion with a '.' separator. Left enabled, the
# embedded ProductVersion ends up "<label>+<our 8-char sha>.<full 40-char sha>",
# which fails the strict equality check in [3/6] and clutters the About display.
dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    -p:InformationalVersion=$infoVersion `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugSymbols=false `
    -p:DebugType=None

$exeName = "SmartStreamer4.exe"
$exePath = Join-Path $publishDir $exeName

Write-Host "`n[3/6] Verifying embedded version..." -ForegroundColor Yellow
# Win32 ProductVersion is sourced from AssemblyInformationalVersionAttribute by
# the .NET SDK. Refusing to package when it doesn't match the expected value
# eliminates a class of "shipped a build with the wrong embedded version" bugs.
$embedded = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion
if ($embedded -ne $infoVersion) {
    Write-Host "`nERROR: published exe has ProductVersion '$embedded' but expected '$infoVersion'." -ForegroundColor Red
    Write-Host "  In-app version display and update check would be wrong. Refusing to package." -ForegroundColor Red
    exit 1
}
Write-Host "  Embedded ProductVersion: $embedded" -ForegroundColor Green

Write-Host "`n[4/6] Cleaning non-exe publish artifacts..." -ForegroundColor Yellow
$dllConfigPath = Join-Path $publishDir "SmartStreamer4.dll.config"
if (Test-Path $dllConfigPath) {
    Remove-Item $dllConfigPath -Force
}

Write-Host "`n[5/6] Creating release zip..." -ForegroundColor Yellow
Compress-Archive -Path $exePath -DestinationPath $zipPath -Force
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
Write-Host "  $zipLabel" -ForegroundColor Green
Write-Host "  SHA256: $hash" -ForegroundColor Green

Write-Host "`n[6/6] Updating artifacts\release\SHA256SUMS.txt..." -ForegroundColor Yellow
# Idempotent: re-runs replace the existing line for this zip rather than appending
# duplicates, so a failed build + retag + rebuild leaves a clean SHA256SUMS.txt.
$newLine = "$hash  $zipLabel"
if (-not (Test-Path $sumsPath)) {
    Set-Content -Path $sumsPath -Value $newLine -Encoding ascii
    Write-Host "  Created SHA256SUMS.txt with first entry." -ForegroundColor Green
} else {
    $lines = @(Get-Content $sumsPath)
    $matchIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].EndsWith("  $zipLabel")) {
            $matchIdx = $i
            break
        }
    }
    if ($matchIdx -lt 0) {
        Add-Content -Path $sumsPath -Value $newLine -Encoding ascii
        Write-Host "  Appended new entry." -ForegroundColor Green
    } elseif ($lines[$matchIdx] -eq $newLine) {
        Write-Host "  Entry already present and matches (no change)." -ForegroundColor Green
    } else {
        $lines[$matchIdx] = $newLine
        Set-Content -Path $sumsPath -Value $lines -Encoding ascii
        Write-Host "  Replaced existing entry (hash changed)." -ForegroundColor Green
    }
}

Write-Host "`nBuild phase complete." -ForegroundColor Green
Write-Host "`nNext steps (manual):" -ForegroundColor Yellow
Write-Host "  1. Live-test the zip at $zipPath." -ForegroundColor White
Write-Host "  2. If good, push the tag:  git push origin $tag" -ForegroundColor White
Write-Host "  3. Confirm RELEASE_NOTES-${tag}.md is finalized at the repo root." -ForegroundColor White
Write-Host "  4. Run the publish phase:  .\publish-release.ps1 -Publish" -ForegroundColor White

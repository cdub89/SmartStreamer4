#Requires -Version 7.0
# pwsh 7 only. Under Windows PowerShell 5.1, `2>$null` on a native command with
# ErrorActionPreference = Stop raises a terminating NativeCommandError, so an
# untagged HEAD died inside `git describe` with a raw git error instead of the
# refusal below (Codex audit, 2026-09-20). 5.1 is refused rather than supported.
param(
    [switch]$Preview,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"

# Two modes, one per destination. Exactly one flag is required: the invocation
# states the intent, and the tag has to agree with it before anything is built.
#
#   .\publish-release.ps1 -Preview
#     Requires a vX.Y.Z-previewN tag. Tests, builds, verifies the embedded
#     version, zips, uploads the zip to R2, confirms the link serves it, and
#     prints the tester link. Never touches GitHub Releases.
#
#   .\publish-release.ps1 -Publish
#     Requires a clean vX.Y.Z tag. Same build, then writes SHA256SUMS.txt and
#     runs gh release create with --latest, attaching the zip and the sidecar.
#
# Flags rather than inferring the destination from the tag shape, and checked
# before the build rather than after it: inference cannot catch a tester build
# mistyped as a clean vX.Y.Z tag. History and rationale are in CLAUDE.md under
# Build & Release.
#
# Simplified 2026-09-20 (operator decision). Removed: the "is this R2 key free"
# precondition, the preview SHA256SUMS sidecar, a dead publish-dir cleanup step,
# and the -SkipTests / -Runtime / -Configuration parameters. Do not restore the
# key check: its pre-upload 404 was cached at the Cloudflare edge and made the
# post-upload verify fail on a good v0.3.3-preview2 upload. A re-run of the same
# tag now simply overwrites, which is what a re-run after a failed run needs;
# new code always gets a new tag, and the tag is in the filename.
#
# Version source: the git tag at HEAD. The csproj <Version> stays a clean
# numeric default, because MSBuild's condition evaluator OOMs on a non-numeric
# character there.

# One build shape. These were parameters nobody varied.
$Runtime = "win-x64"
$Configuration = "Release"

# R2 preview distribution. GA does not use this bucket: wx7v.net links GA
# straight from GitHub Releases.
$R2Bucket = "wx7v-downloads"
$R2Prefix = "smartstreamer4"
$R2PublicHost = "https://downloads.wx7v.net"

# How long to keep asking the public URL for the uploaded zip before giving up.
$VerifyTimeoutSeconds = 120
$VerifyRetrySeconds = 10

$projectPath = Join-Path $PSScriptRoot "SmartSDRIQStreamer.csproj"
$publishDir = Join-Path $PSScriptRoot "bin/$Configuration/net8.0-windows/$Runtime/publish"
$exePath = Join-Path $publishDir "SmartStreamer4.exe"
$noticesPath = Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.txt"

# One exit path for every refusal: red headline, yellow hints, non-zero exit.
# `exit` inside a function ends the whole script.
function Fail {
    param(
        [Parameter(Mandatory)][string]$Message,
        [string[]]$Hints = @(),
        [int]$Code = 1
    )
    Write-Host "`nERROR: $Message" -ForegroundColor Red
    foreach ($hint in $Hints) { Write-Host "  $hint" -ForegroundColor Yellow }
    exit $Code
}

function Step([string]$Title) { Write-Host "`n$Title" -ForegroundColor Yellow }

# -----------------------------------------------------------------------------
# Mode and tag validation, before any work.
# -----------------------------------------------------------------------------
if ($Preview -and $Publish) {
    Fail "-Preview and -Publish are mutually exclusive." -Hints @(
        "-Preview builds a tester zip and uploads it to R2."
        "-Publish builds a GA zip and creates the GitHub release."
    ) -Code 2
}
if (-not $Preview -and -not $Publish) {
    Fail "specify exactly one of -Preview or -Publish." -Hints @(
        ".\publish-release.ps1 -Preview   # tester build from a vX.Y.Z-previewN tag"
        ".\publish-release.ps1 -Publish   # GA release from a clean vX.Y.Z tag"
    ) -Code 2
}

$tag = (& git describe --tags --exact-match HEAD 2>$null)
if (-not $tag) {
    $exampleTag = if ($Publish) { "vX.Y.Z" } else { "vX.Y.Z-previewN" }
    Fail "HEAD has no release tag." -Hints @(
        "Tag the release first (annotated), then re-run, e.g.:"
        "  git tag -a $exampleTag -m `"SmartStreamer4 $exampleTag`""
    )
}
# Two tag shapes are minted: a clean vX.Y.Z for GA, or vX.Y.Z-previewN for a
# numbered tester build. The retired bN suffix is still parsed by
# ReleaseUpdateService for old tags but is not accepted here.
if ($tag -notmatch '^v\d+\.\d+\.\d+(-preview\d+)?$') {
    Fail "tag '$tag' does not match v<major>.<minor>.<patch>[-preview<N>] (e.g. v0.3.3, v0.3.3-preview1)."
}
$isPreviewTag = $tag -match '-preview\d+$'

# The tag has to match the stated intent. Previews and GA go to different
# destinations with different audiences, so a mismatch is always a mistake.
if ($Preview -and -not $isPreviewTag) {
    Fail "-Preview requires a vX.Y.Z-previewN tag; HEAD is '$tag'." -Hints @(
        "A clean tag is a GA tag. Either re-tag as a preview, or run -Publish."
    )
}
if ($Publish -and $isPreviewTag) {
    # -Publish hard-codes --latest, and the in-app updater in every build
    # fielded before 2026-09-08 reads the releases list without skipping
    # pre-releases, so a published preview would prompt every operator on the
    # previous GA. Previews reach testers through R2, never GitHub Releases.
    Fail "-Publish requires a clean vX.Y.Z tag; HEAD is '$tag'." -Hints @(
        "Previews are never published. Run -Preview to build and upload it."
    )
}

# dotnet publish builds the WORKING TREE, not the tagged commit, so a modified
# or untracked source file would ship inside a build that claims the tag's
# version and sha (Codex design review, 2026-09-20). Gitignored files, such as
# the release notes, do not count.
$dirty = @(& git status --porcelain)
if ($dirty) {
    Fail "the working tree is not clean, so the build would not match '$tag'." -Hints (
        @("Commit or discard these first, then re-tag if HEAD moves:") + ($dirty | Select-Object -First 10 | ForEach-Object { "  $_" })
    )
}

$sha = (& git rev-parse HEAD).Substring(0, 8).ToLowerInvariant()
$infoVersion = "$($tag.Substring(1))+$sha"   # leading 'v' dropped, SemVer style
$zipLabel = "SmartStreamer4-$tag-$Runtime.zip"
$zipPath = Join-Path $publishDir $zipLabel
$notesPath = Join-Path $PSScriptRoot "RELEASE_NOTES-$tag.md"

Write-Host "== SmartStreamer4 release ==" -ForegroundColor Cyan
Write-Host "Mode:           $(if ($Publish) { 'PUBLISH (build + gh release create)' } else { 'PREVIEW (build + R2 upload)' })"
Write-Host "Release tag:    $tag"
Write-Host "Embed version:  $infoVersion"
Write-Host "Zip:            $zipLabel"

# -----------------------------------------------------------------------------
# Publish preconditions, still before the build. -Preview has none.
# -----------------------------------------------------------------------------
if ($Publish) {
    Step "Checking publish preconditions..."

    # Existence is not enough: the remote tag has to name the commit we are
    # about to build. A tag moved locally after a fix, without a force-push,
    # would otherwise attach new assets to the OLD remote tag (Codex audit,
    # 2026-09-20).
    $remoteRefs = @(& git ls-remote origin "refs/tags/$tag" "refs/tags/$tag^{}" 2>$null)
    if (-not $remoteRefs) {
        Fail "tag '$tag' not on origin. Push it first:  git push origin $tag"
    }
    # Annotated tags list twice: the tag object, then the peeled '^{}' commit.
    # Prefer the peeled line; a lightweight tag has only the plain one.
    $peeled = $remoteRefs | Where-Object { $_ -match "refs/tags/$([regex]::Escape($tag))\^\{\}$" }
    $remoteLine = if ($peeled) { @($peeled)[0] } else { @($remoteRefs)[0] }
    $remoteSha = ($remoteLine -split "\s+")[0]
    $localSha = (& git rev-parse "$tag^{commit}").Trim()
    if ($remoteSha -ne $localSha) {
        Fail "tag '$tag' on origin points at $remoteSha but locally at $localSha." -Hints @(
            "The release would attach to the wrong commit. Reconcile first:"
            "  git push origin :refs/tags/$tag   # drop the stale remote tag"
            "  git push origin $tag              # push the current one"
        )
    }
    Write-Host "  Tag on origin:           OK ($localSha)"

    if (-not (Test-Path $notesPath) -or (Get-Item $notesPath).Length -eq 0) {
        Fail "RELEASE_NOTES-$tag.md missing or empty." -Hints @(
            "Author release notes at $notesPath then re-run."
        )
    }
    Write-Host "  Release notes present:   OK"
}

# The notices file is cheap to check and would otherwise fail after the build.
if (-not (Test-Path $noticesPath)) {
    Fail "'$noticesPath' not found. Refusing to package without it."
}

# -----------------------------------------------------------------------------
# Build.
# -----------------------------------------------------------------------------
Step "[1/4] Running tests..."
# Issue #50: name the solution and check the exit code. Bare `dotnet test` once
# fell back to the app csproj and passed while running zero tests.
dotnet test SmartStreamer4.sln
if ($LASTEXITCODE -ne 0) {
    Fail "Tests failed (exit $LASTEXITCODE). Aborting release build."
}

Step "[2/4] Publishing self-contained single-file executable..."
# InformationalVersion carries the tag and commit into the exe; About and the
# update check both read it. IncludeSourceRevisionInInformationalVersion=false
# stops the SDK appending the full 40-char sha, which would fail the equality
# check below and clutter About.
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
# A native non-zero exit is not a terminating error, so check it. Without this a
# failed publish can leave a stale exe carrying this same tag's version, which
# then passes the version gate below and ships (Codex audit, 2026-09-20).
if ($LASTEXITCODE -ne 0) {
    Fail "dotnet publish failed (exit $LASTEXITCODE)." -Hints @(
        "If this is a file-lock wall of MSB3021/MSB3027, close any running"
        "SmartStreamer4 instance and re-run."
    )
}

# Win32 ProductVersion comes from AssemblyInformationalVersionAttribute. A
# mismatch means About and the update check would both be wrong.
$embedded = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion
if ($embedded -ne $infoVersion) {
    Fail "published exe has ProductVersion '$embedded' but expected '$infoVersion'." -Hints @(
        "In-app version display and update check would be wrong. Refusing to package."
    )
}
Write-Host "  Embedded ProductVersion: $embedded" -ForegroundColor Green

Step "[3/4] Creating release zip..."
# Exactly two files ship: the exe and the third-party notices. wx7v.net will not
# host an artifact whose notices do not ship inside it, and the MIT and BSD
# dependencies require their notice text to travel with any redistribution. The
# project's own LICENSE is deliberately not packaged (operator, 2026-08-05): it
# is extensionless, so Windows would not open it, and we hold the copyright.
Compress-Archive -Path $exePath, $noticesPath -DestinationPath $zipPath -Force
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
$zipBytes = (Get-Item $zipPath).Length
Write-Host "  $zipLabel" -ForegroundColor Green
Write-Host "  SHA256: $hash" -ForegroundColor Green

# -----------------------------------------------------------------------------
# Ship.
# -----------------------------------------------------------------------------
if ($Preview) {
    Step "[4/4] Uploading to R2..."
    & npx wrangler r2 object put "$R2Bucket/$R2Prefix/$zipLabel" --file $zipPath --content-type "application/zip" --remote
    if ($LASTEXITCODE -ne 0) {
        Fail "upload of $zipLabel failed (exit $LASTEXITCODE)." -Hints @(
            "If this is an auth failure, run:  npx wrangler login"
            "A re-run of this same tag is safe."
        )
    }

    # Verification only: wrangler reports success without confirming anything
    # landed, and a truncated object still answers 200, so ask the public URL
    # and compare the byte count. Each probe carries a unique query string, which
    # is a separate cache key at the Cloudflare edge by default, so a cached
    # response cannot answer for the object. That proves the object is in R2; it
    # does NOT prove the plain tester link is fresh after an overwrite of a key
    # someone already fetched, which can serve the older copy until it expires.
    $zipUrl = "$R2PublicHost/$R2Prefix/$zipLabel"
    Write-Host "`n  Verifying $zipUrl ..." -ForegroundColor Yellow
    $deadline = (Get-Date).AddSeconds($VerifyTimeoutSeconds)
    $lastResult = "no response"
    $verified = $false
    do {
        try {
            $probe = Invoke-WebRequest -Uri "${zipUrl}?verify=$([guid]::NewGuid().ToString('N'))" `
                -Method Head -TimeoutSec 30 -SkipHttpErrorCheck
            $servedBytes = [int64](@($probe.Headers['Content-Length'])[0] ?? 0)
            $lastResult = "HTTP $($probe.StatusCode), $servedBytes bytes, cf-cache-status $(@($probe.Headers['cf-cache-status'])[0] ?? 'absent')"
            $verified = $probe.StatusCode -eq 200 -and $servedBytes -eq $zipBytes
        } catch {
            $lastResult = $_.Exception.Message
        }
        if (-not $verified -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds $VerifyRetrySeconds }
    } until ($verified -or (Get-Date) -ge $deadline)
    if (-not $verified) {
        Fail "$zipUrl did not serve the uploaded zip within $VerifyTimeoutSeconds s (last: $lastResult; expected $zipBytes bytes)." -Hints @(
            "The upload itself reported success. A re-run of this same tag is safe."
        )
    }
    Write-Host "  Live and $zipBytes bytes: OK" -ForegroundColor Green

    Write-Host "`nPreview published." -ForegroundColor Green
    Write-Host "`nTester link:" -ForegroundColor Yellow
    Write-Host "  $zipUrl" -ForegroundColor White
    Write-Host "`nWhen you send it, say up front that Windows SmartScreen will warn:" -ForegroundColor Yellow
    Write-Host "  no build is code-signed, and unmentioned the warning becomes the" -ForegroundColor White
    Write-Host "  feedback instead of the bug report." -ForegroundColor White
    exit 0
}

Step "[4/4] Creating GitHub release..."
# GA carries a checksum sidecar as a release asset; previews do not (no tester
# ever asked for one). --latest is hard-coded and --prerelease is not exposed:
# the preview guard above refuses the only tags that could tempt it. Nothing is
# committed, so origin/main HEAD == tag commit after publish.
$sumsPath = Join-Path $publishDir "SHA256SUMS.txt"
Set-Content -Path $sumsPath -Value "$hash  $zipLabel" -Encoding ascii
& gh release create $tag $zipPath $sumsPath `
    --title "SmartStreamer4 $tag" `
    --notes-file $notesPath `
    --latest
if ($LASTEXITCODE -ne 0) {
    Fail "gh release create failed (exit $LASTEXITCODE)."
}

Write-Host "`nDone." -ForegroundColor Green
Write-Host "`nPost-publish (manual):" -ForegroundColor Yellow
Write-Host "  Install the prior release on a tester PC, confirm it sees $tag as an available update." -ForegroundColor White
Write-Host "  Then install $tag and confirm it reports up to date." -ForegroundColor White

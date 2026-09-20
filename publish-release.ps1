#Requires -Version 7.0
# pwsh 7 only. Under Windows PowerShell 5.1, `2>$null` on a native command with
# ErrorActionPreference = Stop raises a terminating NativeCommandError, so an
# untagged HEAD died inside `git describe` with a raw git error instead of the
# refusal below (found by the Codex audit, 2026-09-20; it predates that audit).
# The script has only ever been exercised under pwsh, so refuse 5.1 outright
# rather than make each git call 5.1-safe.
param(
    [switch]$Preview,
    [switch]$Publish,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

# One exit path for every refusal: red headline, yellow hints, non-zero exit.
# `exit` inside a function still ends the whole script, including from inside a
# try block, which the R2 precondition check below relies on.
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

# Two modes, one per destination. Exactly one flag is required: the invocation
# states the intent, and the tag has to agree with it before anything is built.
#
#   .\publish-release.ps1 -Preview
#     Requires a vX.Y.Z-previewN tag. Tests, builds, verifies the embedded
#     version, zips, writes SHA256SUMS-<tag>.txt, uploads both to the R2
#     bucket, and prints the tester link. Never touches GitHub Releases.
#
#   .\publish-release.ps1 -Publish
#     Requires a clean vX.Y.Z tag. Same build, then writes SHA256SUMS.txt and
#     runs gh release create with --latest, attaching the zip and the sidecar.
#
# Why the tag/intent check runs first: a forgotten or mistyped tag used to mean
# the whole test run and build completed before anything noticed. SKCCLogger hit
# the same class of bug from the other side (build.py:2320, pre-GA review
# 2026-07-07: a full build plus three notarizations finished and then both
# publish steps silently printed "Skipping ... not a clean tag", shipping
# nothing). Fail before the expensive part, not after it.
#
# Flags rather than inferring the destination from the tag shape (2026-09-20,
# aligning with SKCCLogger's build.py). Inference could not catch the case that
# actually bites: intending a tester build, mistyping a clean vX.Y.Z tag, and
# getting a GA-shaped zip with no complaint.
#
# Version source: the git tag at HEAD. csproj <Version> stays at a clean numeric
# default; MSBuild's condition evaluator OOMs on any non-numeric character in
# <Version> (trailing 'b', '-b', etc.), so release labels are kept out of the
# csproj entirely. The v0.1.Xb beta series and the bN suffix are both retired.

# R2 preview distribution. Preview sidecars are tag-scoped
# (SHA256SUMS-<tag>.txt) rather than the bare SHA256SUMS.txt, for two reasons:
# consecutive previews would otherwise overwrite each other's checksum, and the
# bare name at this prefix is already taken by an object testers may hold a link
# to. Nothing here is reliably retractable once the edge has served it, so the
# rule is simply that a preview never writes a name it does not own.
#
# Checked 2026-09-20: wx7v.net links GA artifacts straight from GitHub Releases
# (releases/download/<tag>/...), NOT from this bucket. The bare SHA256SUMS.txt
# and the v0.3.2 zip sitting at this prefix are leftovers from the v0.3.2 tester
# build, not the GA download path. Do not restate that the page verifies GA
# against this file; it does not.
$R2Bucket = "wx7v-downloads"
$R2Prefix = "smartstreamer4"
$R2PublicHost = "https://downloads.wx7v.net"

$projectPath = Join-Path $PSScriptRoot "SmartSDRIQStreamer.csproj"
$publishDir = Join-Path $PSScriptRoot "bin/$Configuration/net8.0-windows/$Runtime/publish"

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

Write-Host "== SmartStreamer4 release ==" -ForegroundColor Cyan
Write-Host "Mode:           $(if ($Publish) { 'PUBLISH (build + gh release create)' } else { 'PREVIEW (build + R2 upload)' })"
Write-Host "Project:        $projectPath"
Write-Host "Runtime:        $Runtime"
Write-Host "Configuration:  $Configuration"

$tag = (& git describe --tags --exact-match HEAD 2>$null)
if (-not $tag) {
    $exampleTag = if ($Publish) { "v0.3.3" } else { "v0.3.3-preview1" }
    Fail "HEAD has no release tag." -Hints @(
        "Tag the release first, then re-run, e.g.:"
        "  git tag -a $exampleTag -m `"SmartStreamer4 $exampleTag`""
    )
}
# Two tag shapes are minted: a clean vX.Y.Z for GA, or vX.Y.Z-previewN for a
# numbered tester build (convention adopted 2026-09-08, mirroring SKCCLogger).
# The retired bN suffix is still parsed by ReleaseUpdateService for old tags
# but is no longer accepted here, so it cannot be minted by accident.
if ($tag -notmatch '^v\d+\.\d+\.\d+(-preview\d+)?$') {
    Fail "tag '$tag' does not match v<major>.<minor>.<patch>[-preview<N>] (e.g. v0.3.3, v0.3.3-preview1)."
}
$isPreviewTag = $tag -match '-preview\d+$'

# The tag has to match the stated intent. Previews and GA go to different
# destinations with different audiences, so a mismatch here is always a mistake
# rather than something to resolve silently in either direction.
if ($Preview -and -not $isPreviewTag) {
    Fail "-Preview requires a vX.Y.Z-previewN tag; HEAD is '$tag'." -Hints @(
        "A clean tag is a GA tag. Either re-tag as a preview, or run -Publish."
    )
}
if ($Publish -and $isPreviewTag) {
    # Phase 2 hard-codes --latest, and the in-app updater in every build fielded
    # before 2026-09-08 reads the releases list without skipping pre-releases,
    # so publishing a preview tag in any form would prompt every operator on the
    # previous GA. Previews reach testers through R2, never GitHub Releases.
    Fail "-Publish requires a clean vX.Y.Z tag; HEAD is '$tag'." -Hints @(
        "Previews are never published. Run -Preview to build and upload it."
    )
}

$releaseLabel = $tag.Substring(1)   # strip leading 'v' for the embedded version string (SemVer convention)
$sha = (& git rev-parse HEAD).Substring(0, 8).ToLowerInvariant()
$infoVersion = "${releaseLabel}+${sha}"
$zipLabel = "SmartStreamer4-${tag}-${Runtime}.zip"
$zipPath = Join-Path $publishDir $zipLabel
$sumsLabel = if ($Preview) { "SHA256SUMS-${tag}.txt" } else { "SHA256SUMS.txt" }
$sumsPath = Join-Path $publishDir $sumsLabel
$notesPath = Join-Path $PSScriptRoot "RELEASE_NOTES-${tag}.md"

Write-Host "Release tag:    $tag"
Write-Host "Embed version:  $infoVersion"
Write-Host "Zip:            $zipLabel"
Write-Host "Sidecar:        $sumsLabel"

# -----------------------------------------------------------------------------
# Destination preconditions, still before the build.
# -----------------------------------------------------------------------------
if ($Publish) {
    Write-Host "`n[0/7] Checking publish preconditions..." -ForegroundColor Yellow

    # Existence is not enough: the remote tag has to name the same commit we are
    # about to build. A tag retagged locally after a fix, without a force-push,
    # would otherwise publish new assets onto the OLD remote tag, and the release
    # would not match what was built (Codex audit, 2026-09-20).
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
        Fail "RELEASE_NOTES-${tag}.md missing or empty." -Hints @(
            "Author release notes at $notesPath then re-run."
        )
    }
    Write-Host "  Release notes present:   OK"
}

if ($Preview) {
    Write-Host "`n[0/7] Checking preview preconditions..." -ForegroundColor Yellow

    # Never re-upload different bytes under a key the edge may already be
    # serving: a tester can keep getting the superseded copy and report a bug
    # that is already fixed, and deleting the object does not reliably revoke
    # it. A rebuild of the same tag bumps to the next -previewN instead.
    # Fails CLOSED: only a confirmed 404 counts as "free". Treating every error
    # as absent would let a transient 5xx, a DNS blip or a timeout wave through
    # an upload over a key that is live (Codex audit, 2026-09-20).
    $existingUrl = "$R2PublicHost/$R2Prefix/$zipLabel"
    try {
        Invoke-WebRequest -Uri $existingUrl -Method Head -TimeoutSec 30 -ErrorAction Stop | Out-Null
        Fail "$zipLabel is already published at $existingUrl." -Hints @(
            "Re-uploading different bytes under a live key is not safe."
            "Tag the next preview instead, e.g. the -preview<N+1> of this line."
        )
    } catch {
        $status = $null
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
        }
        if ($status -ne 404) {
            $detail = if ($status) { "HTTP $status" } else { $_.Exception.Message }
            Fail "could not confirm whether $zipLabel already exists ($detail)." -Hints @(
                "Refusing to upload rather than risk overwriting a live key."
            )
        }
    }
    Write-Host "  Key is free on R2:       OK"
}

# -----------------------------------------------------------------------------
# Build.
# -----------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Host "`n[1/7] Running tests..." -ForegroundColor Yellow
    # Issue #50 fix 2026-07-23: bare `dotnet test` never discovered any test
    # project (the root .slnx was invisible to the .NET 8 CLI, so the command
    # fell back to the app csproj and ran zero tests), and no exit-code check
    # meant a failure would not have blocked the release. Name the solution
    # and fail fast so this gate is real.
    dotnet test SmartStreamer4.sln
    if ($LASTEXITCODE -ne 0) {
        Fail "Tests failed (exit $LASTEXITCODE). Aborting release build."
    }
} else {
    Write-Host "`n[1/7] Skipping tests (-SkipTests)." -ForegroundColor Yellow
}

Write-Host "`n[2/7] Publishing self-contained single-file executable..." -ForegroundColor Yellow
# -p:InformationalVersion plumbs the git tag + commit sha into the published exe.
# The in-app About display and the update check both read this attribute at
# runtime; getting it wrong would either show v0.1.0 forever or report
# "update available" against every newer release indefinitely.
#
# -p:IncludeSourceRevisionInInformationalVersion=false suppresses the .NET SDK's
# default behavior of auto-appending the detected SourceRevisionId (full 40-char
# git sha) to InformationalVersion with a '.' separator. Left enabled, the
# embedded ProductVersion ends up "<label>+<our 8-char sha>.<full 40-char sha>",
# which fails the strict equality check in [3/7] and clutters the About display.
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
# $ErrorActionPreference does not reliably turn a native non-zero exit into a
# terminating error, so check it explicitly. Without this a failed publish can
# leave a stale exe from an earlier attempt in the publish dir, and if that exe
# carries this same tag's version the embedded-version gate below passes and a
# stale build ships (Codex audit, 2026-09-20).
if ($LASTEXITCODE -ne 0) {
    Fail "dotnet publish failed (exit $LASTEXITCODE)." -Hints @(
        "If this is a file-lock wall of MSB3021/MSB3027, close any running"
        "SmartStreamer4 instance and re-run."
    )
}

$exeName = "SmartStreamer4.exe"
$exePath = Join-Path $publishDir $exeName

Write-Host "`n[3/7] Verifying embedded version..." -ForegroundColor Yellow
# Win32 ProductVersion is sourced from AssemblyInformationalVersionAttribute by
# the .NET SDK. Refusing to package when it doesn't match the expected value
# eliminates a class of "shipped a build with the wrong embedded version" bugs.
$embedded = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion
if ($embedded -ne $infoVersion) {
    Fail "published exe has ProductVersion '$embedded' but expected '$infoVersion'." -Hints @(
        "In-app version display and update check would be wrong. Refusing to package."
    )
}
Write-Host "  Embedded ProductVersion: $embedded" -ForegroundColor Green

Write-Host "`n[4/7] Cleaning non-exe publish artifacts..." -ForegroundColor Yellow
$dllConfigPath = Join-Path $publishDir "SmartStreamer4.dll.config"
if (Test-Path $dllConfigPath) {
    Remove-Item $dllConfigPath -Force
}

Write-Host "`n[5/7] Creating release zip..." -ForegroundColor Yellow
# The zip must carry the third-party notices alongside the exe; wx7v.net will
# not host an artifact whose notices do not ship inside it, and the MIT and BSD
# dependencies require their notice text to travel with any redistribution.
# THIRD-PARTY-NOTICES.txt keeps its extension so Windows can open it from the
# extracted folder on a double click.
#
# The project's own LICENSE is deliberately NOT packaged (operator-reported
# 2026-08-05): extensionless, Windows would not open it without a rename, and
# it is the one file here whose omission carries no external obligation since
# we hold the copyright. It stays in the repo for GitHub and for the README and
# CONTRIBUTING links; only the shipped zip drops it.
$noticesPath = Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.txt"
if (-not (Test-Path $noticesPath)) {
    Fail "'$noticesPath' not found. Refusing to package without it."
}
if (Select-String -Path $noticesPath -Pattern 'TODO' -Quiet) {
    Fail "THIRD-PARTY-NOTICES.txt still contains a TODO placeholder. Refusing to package."
}
Compress-Archive -Path $exePath, $noticesPath -DestinationPath $zipPath -Force
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
$zipBytes = (Get-Item $zipPath).Length
Write-Host "  $zipLabel" -ForegroundColor Green
Write-Host "  SHA256: $hash" -ForegroundColor Green

Write-Host "`n[6/7] Writing $sumsLabel sidecar..." -ForegroundColor Yellow
# Single-line per-release sidecar in the publish dir. Not tracked in git: the
# publish dir is under bin/ which is gitignored. GA attaches it as a release
# asset, which is where wx7v.net actually links GA from; a preview uploads it
# beside the zip under a tag-scoped name so it never writes a name it does not
# own (see the note at the top).
$newLine = "$hash  $zipLabel"
Set-Content -Path $sumsPath -Value $newLine -Encoding ascii
Write-Host "  Wrote $sumsPath" -ForegroundColor Green

# -----------------------------------------------------------------------------
# Ship.
# -----------------------------------------------------------------------------
if ($Preview) {
    Write-Host "`n[7/7] Uploading to R2..." -ForegroundColor Yellow

    & npx wrangler r2 object put "$R2Bucket/$R2Prefix/$zipLabel" --file $zipPath --content-type "application/zip" --remote
    if ($LASTEXITCODE -ne 0) {
        Fail "upload of $zipLabel failed (exit $LASTEXITCODE)." -Hints @(
            "If this is an auth failure, run:  npx wrangler login"
        )
    }

    & npx wrangler r2 object put "$R2Bucket/$R2Prefix/$sumsLabel" --file $sumsPath --content-type "text/plain; charset=utf-8" --remote
    if ($LASTEXITCODE -ne 0) {
        # The zip is live by now, so the precondition check refuses a re-run.
        # The sidecar is the one upload that is safe to repeat by hand: it is
        # tag-scoped, and this failure means nothing was written under its key.
        Fail "upload of $sumsLabel failed (exit $LASTEXITCODE)." -Hints @(
            "The zip is already live, so a re-run will be refused. Upload the sidecar by hand:"
            "  npx wrangler r2 object put `"$R2Bucket/$R2Prefix/$sumsLabel`" --file `"$sumsPath`" --content-type `"text/plain; charset=utf-8`" --remote"
        )
    }

    # The upload reports success without confirming anything landed, so the live
    # URL is the only real check. Content-length is compared too: a truncated
    # object still answers 200.
    $zipUrl = "$R2PublicHost/$R2Prefix/$zipLabel"
    Write-Host "`n  Verifying $zipUrl ..." -ForegroundColor Yellow
    try {
        $head = Invoke-WebRequest -Uri $zipUrl -Method Head -TimeoutSec 60 -ErrorAction Stop
    } catch {
        Fail "$zipUrl did not answer after upload: $($_.Exception.Message)"
    }
    # Windows PowerShell 5.1 hands back a bare string here while PowerShell 7
    # hands back a collection, so indexing [0] unconditionally would read the
    # first CHARACTER of the length on 5.1 (Codex audit, 2026-09-20).
    $clRaw = $head.Headers['Content-Length']
    $clValue = if ($clRaw -is [string]) { $clRaw } else { @($clRaw)[0] }
    $servedBytes = [int64]$clValue
    if ($servedBytes -ne $zipBytes) {
        Fail "$zipUrl served $servedBytes bytes, expected $zipBytes."
    }
    Write-Host "  Live and $servedBytes bytes: OK" -ForegroundColor Green

    Write-Host "`nPreview published." -ForegroundColor Green
    Write-Host "`nTester link:" -ForegroundColor Yellow
    Write-Host "  $zipUrl" -ForegroundColor White
    Write-Host "  $R2PublicHost/$R2Prefix/$sumsLabel" -ForegroundColor White
    Write-Host "`nWhen you send it, say up front that Windows SmartScreen will warn:" -ForegroundColor Yellow
    Write-Host "  no build is code-signed, and unmentioned the warning becomes the" -ForegroundColor White
    Write-Host "  feedback instead of the bug report." -ForegroundColor White
    exit 0
}

Write-Host "`n[7/7] Creating GitHub release..." -ForegroundColor Yellow
# --latest is hard-coded. The 'b' suffix on beta tags tricked the wrong-flag
# mistake (--prerelease) twice before; this script does not expose that choice,
# and the preview guard above refuses the only tags that could tempt it.
# SHA256SUMS.txt is attached alongside the zip; nothing is committed here, so
# origin/main HEAD == tag commit after publish.
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

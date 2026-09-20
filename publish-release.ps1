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
#     version, signs, zips, uploads the zip to R2, confirms the link serves it,
#     and prints the tester link. Never touches GitHub Releases.
#
#   .\publish-release.ps1 -Publish
#     Requires a clean vX.Y.Z tag. Same build and signing, then writes SHA256SUMS.txt and
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
# Every build is Authenticode-signed (operator decision, 2026-09-20): previews
# and GA alike, with no unsigned fallback and no skip flag. Setup, recipe and
# the Azure half are in CODE-SIGNING.md.
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

# Code signing: Azure Artifact Signing through jsign, the same recipe as the
# Linux seat. Everything lives outside the repo. The jar name is pinned, not
# globbed: the signer version is part of the release toolchain, not something
# to pick up by accident.
$SigningDir = Join-Path $env:USERPROFILE ".skcclogger-signing\windows"
$SigningConfigPath = Join-Path $SigningDir "azure.conf"
$JsignJarPath = Join-Path $SigningDir "jsign-7.5.jar"
$SigningConfigKeys = "tenant_id", "client_id", "client_secret", "endpoint", "account", "profile", "expected_cn"
$SigningScope = "https://codesigning.azure.net/.default"
# Mandatory, not optional: signing certificates live 72 hours, so an exe without
# a timestamp stops validating three days after release.
$TimestampUrl = "http://timestamp.acs.microsoft.com"
$SecretExpiryWarnDays = 30
$ExpectedIssuerOrg = "O=Microsoft Corporation"

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

# key=value, '#' comments, split on the FIRST '=' so a secret may contain one.
# Returns a hashtable; refuses a missing file or a blank required key.
function Read-SigningConfig {
    if (-not (Test-Path $SigningConfigPath)) {
        Fail "signing config not found at $SigningConfigPath." -Hints @(
            "Every build is signed; there is no unsigned fallback. See CODE-SIGNING.md."
        )
    }
    $config = @{}
    foreach ($line in Get-Content $SigningConfigPath) {
        if ($line -match '^\s*(#|$)' -or $line -notmatch '=') { continue }
        $key, $value = $line -split '=', 2
        $config[$key.Trim()] = $value.Trim()
    }
    $missing = @($SigningConfigKeys | Where-Object { [string]::IsNullOrWhiteSpace($config[$_]) })
    if ($missing) {
        Fail "signing config is missing: $($missing -join ', ')." -Hints @("File: $SigningConfigPath")
    }
    $config
}

# Client-credentials grant. The secret travels in the HTTPS body only, and on
# failure only Microsoft's own error code and first line are shown: never the
# request, the secret or a token.
function Get-SigningToken($Config) {
    try {
        $response = Invoke-RestMethod -Method Post -TimeoutSec 30 `
            -Uri "https://login.microsoftonline.com/$($Config.tenant_id)/oauth2/v2.0/token" `
            -Body @{
                grant_type    = "client_credentials"
                client_id     = $Config.client_id
                client_secret = $Config.client_secret
                scope         = $SigningScope
            }
        return $response.access_token
    } catch {
        $detail = $_.Exception.Message
        if ($_.ErrorDetails.Message) {
            try {
                $aad = $_.ErrorDetails.Message | ConvertFrom-Json
                $detail = "$($aad.error): $(($aad.error_description -split "`r?`n")[0])"
            } catch { }
        }
        Fail "could not get a signing token ($detail)." -Hints @(
            "invalid_client usually means a wrong or expired client secret in $SigningConfigPath."
        )
    }
}

# Signs in place. The token is in the environment only for the life of the jsign
# call, so wrangler, npx and gh further down can never inherit it, and it is
# never on a command line.
function Invoke-Signing($Config, [string]$Path) {
    $env:AZURE_ACCESS_TOKEN = Get-SigningToken $Config
    try {
        & java -jar $JsignJarPath `
            --storetype TRUSTEDSIGNING `
            --keystore $Config.endpoint `
            --storepass env:AZURE_ACCESS_TOKEN `
            --alias "$($Config.account)/$($Config.profile)" `
            --tsaurl $TimestampUrl `
            --tsmode RFC3161 `
            --name "SmartStreamer4" `
            $Path
        $exit = $LASTEXITCODE
    } finally {
        $env:AZURE_ACCESS_TOKEN = $null
    }
    if ($exit -ne 0) {
        Fail "jsign failed (exit $exit)." -Hints @(
            "HTTP 403 means the app registration lacks the Certificate Profile Signer role."
            "A re-run of this same tag is safe."
        )
    }
}

# Get-AuthenticodeSignature says Valid for ANY chain this PC trusts, including a
# local test root, so Valid alone does not prove who signed. The exact Common
# Name and a Microsoft issuer do (Codex design review, 2026-09-20). Exact, not
# "contains": a substring match would accept a longer name that starts the same.
function Assert-Signature($Config, [string]$Path) {
    $simpleName = [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName
    $signature = Get-AuthenticodeSignature $Path
    $commonName = $null
    $problems = @()
    if ($signature.Status -ne "Valid") { $problems += "status is $($signature.Status): $($signature.StatusMessage)" }
    if (-not $signature.TimeStamperCertificate) { $problems += "no timestamp, so it would stop validating in 72 hours" }
    if ($signature.SignerCertificate) {
        $commonName = $signature.SignerCertificate.GetNameInfo($simpleName, $false)
        if ($commonName -cne $Config.expected_cn) { $problems += "signer is '$commonName', expected '$($Config.expected_cn)'" }
        if ($signature.SignerCertificate.Issuer -notmatch [regex]::Escape($ExpectedIssuerOrg)) {
            $problems += "issuer is '$($signature.SignerCertificate.Issuer)', expected one under $ExpectedIssuerOrg"
        }
    } else {
        $problems += "no signer certificate"
    }
    if ($problems) { Fail "signature check failed for $Path." -Hints $problems }
    Write-Host "  Signed by:   $commonName" -ForegroundColor Green
    Write-Host "  Timestamped: $($signature.TimeStamperCertificate.GetNameInfo($simpleName, $false))" -ForegroundColor Green
}

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

# The mode picks the tag, not the other way round. GA is cut from the commit a
# preview already validated, so that commit carries BOTH vX.Y.Z-previewN and
# vX.Y.Z, and `git describe --exact-match` would hand back whichever it liked
# (Codex final audit, 2026-09-20). So: list every tag at HEAD, keep the ones of
# the shape this mode ships, and require exactly one.
#
# Two shapes are minted: a clean vX.Y.Z for GA, or vX.Y.Z-previewN for a tester
# build. Case-sensitive on purpose: the tag becomes the zip name and the version
# in About. The retired bN suffix is still parsed by ReleaseUpdateService for
# old tags but is not accepted here. A preview can never reach -Publish: it
# hard-codes --latest, and the updater in every build fielded before 2026-09-08
# reads the releases list without skipping pre-releases, so a published preview
# would prompt every operator on the previous GA.
$wantedShape = if ($Publish) { '^v\d+\.\d+\.\d+$' } else { '^v\d+\.\d+\.\d+-preview\d+$' }
$wantedExample = if ($Publish) { "vX.Y.Z" } else { "vX.Y.Z-previewN" }
$tagsAtHead = @(& git tag --points-at HEAD)
$candidates = @($tagsAtHead | Where-Object { $_ -cmatch $wantedShape })
if ($candidates.Count -ne 1) {
    $modeName = if ($Publish) { "-Publish" } else { "-Preview" }
    $found = if ($tagsAtHead) { "Tags at HEAD: $($tagsAtHead -join ', ')." } else { "HEAD has no tags." }
    $problem = if ($candidates.Count -gt 1) { "more than one" } else { "no" }
    Fail "$modeName needs exactly one $wantedExample tag at HEAD, and there is $problem such tag. $found" -Hints @(
        "Tag the release first (annotated, lowercase), then re-run, e.g.:"
        "  git tag -a $wantedExample -m `"SmartStreamer4 $wantedExample`""
        "A clean vX.Y.Z tag is a GA tag (-Publish); a -previewN tag is a tester build (-Preview)."
    )
}
$tag = $candidates[0]

# Annotated only. Lightweight tags have caused busted releases before, and
# `push.followTags` silently leaves them behind.
if ((& git cat-file -t $tag) -ne "tag") {
    Fail "tag '$tag' is a lightweight tag; release tags must be annotated." -Hints @(
        "  git tag -d $tag"
        "  git tag -a $tag -m `"SmartStreamer4 $tag`""
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
# Preconditions, still before the build.
# -----------------------------------------------------------------------------
Step "Checking the tag on origin..."
# Both modes: every release tag goes to origin, tester builds included, so an
# artifact in someone's hands always has a source reference that outlives this
# PC. Existence is not enough: the remote tag has to name the commit we are
# about to build. A tag moved locally after a fix, without a force-push, would
# otherwise ship under a name that means something else on origin (Codex
# audits, 2026-09-20). Annotated tags list twice, the tag object and then the
# peeled '^{}' commit; the peeled line is the one to compare.
$remoteRefs = @(& git ls-remote origin "refs/tags/$tag" "refs/tags/$tag^{}" 2>$null)
if (-not $remoteRefs) {
    Fail "tag '$tag' not on origin. Push it first:  git push origin $tag"
}
$peeled = $remoteRefs | Where-Object { $_ -match "refs/tags/$([regex]::Escape($tag))\^\{\}$" }
$remoteLine = if ($peeled) { @($peeled)[0] } else { @($remoteRefs)[0] }
$remoteSha = ($remoteLine -split "\s+")[0]
$localSha = (& git rev-parse "$tag^{commit}").Trim()
if ($remoteSha -ne $localSha) {
    Fail "tag '$tag' on origin points at $remoteSha but locally at $localSha." -Hints @(
        "The build would not match what origin calls '$tag'. Reconcile first:"
        "  git push origin :refs/tags/$tag   # drop the stale remote tag"
        "  git push origin $tag              # push the current one"
    )
}
Write-Host "  Tag on origin:           OK ($localSha)"

if ($Publish) {
    if (-not (Test-Path $notesPath) -or (Get-Item $notesPath).Length -eq 0) {
        Fail "RELEASE_NOTES-$tag.md missing or empty." -Hints @(
            "Author release notes at $notesPath then re-run."
        )
    }
    Write-Host "  Release notes present:   OK"
}

# Signing preflight, so a missing tool or a bad secret fails in seconds rather
# than after the tests and the build.
Step "Checking code signing..."
$signing = Read-SigningConfig
if ($signing.client_secret_expires) {
    $expires = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($signing.client_secret_expires, "yyyy-MM-dd",
            [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$expires)) {
        Fail "client_secret_expires '$($signing.client_secret_expires)' in $SigningConfigPath is not YYYY-MM-DD."
    }
    $daysLeft = [int]($expires.Date - (Get-Date).Date).TotalDays
    if ($daysLeft -lt 0) {
        Fail "the signing client secret expired on $($signing.client_secret_expires)." -Hints @(
            "Create a new one on the app registration and update $SigningConfigPath. See CODE-SIGNING.md."
        )
    }
    if ($daysLeft -le $SecretExpiryWarnDays) {
        Write-Host "  WARNING: the signing client secret expires in $daysLeft days ($($signing.client_secret_expires))." -ForegroundColor Magenta
    }
}
if (-not (Get-Command java -ErrorAction SilentlyContinue)) {
    Fail "java is not on the PATH; jsign needs it." -Hints @("Open a new terminal if Java was just installed. See CODE-SIGNING.md.")
}
if (-not (Test-Path $JsignJarPath)) {
    Fail "jsign not found at $JsignJarPath. See CODE-SIGNING.md."
}
$null = Get-SigningToken $signing
Write-Host "  Config, java, jsign, token: OK (publisher will be '$($signing.expected_cn)')"

# The notices file is cheap to check and would otherwise fail after the build.
if (-not (Test-Path $noticesPath)) {
    Fail "'$noticesPath' not found. Refusing to package without it."
}

# -----------------------------------------------------------------------------
# Build.
# -----------------------------------------------------------------------------
Step "[1/5] Running tests..."
# Issue #50: name the solution and check the exit code. Bare `dotnet test` once
# fell back to the app csproj and passed while running zero tests.
dotnet test SmartStreamer4.sln
if ($LASTEXITCODE -ne 0) {
    Fail "Tests failed (exit $LASTEXITCODE). Aborting release build."
}

Step "[2/5] Publishing self-contained single-file executable..."
# Start from no exe at all. A failed publish can then never leave an earlier exe
# behind to pass the version gate, and the signing step below never meets a
# file that already carries a signature (Codex design review, 2026-09-20).
if (Test-Path $exePath) { Remove-Item $exePath -Force }
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
# A native non-zero exit is not a terminating error, so check it.
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

Step "[3/5] Signing..."
# After the version check and before the zip, so the zip and its SHA256 cover
# the signed exe. A failure here aborts with no zip and nothing shipped.
Invoke-Signing $signing $exePath
Assert-Signature $signing $exePath

Step "[4/5] Creating release zip..."
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
    Step "[5/5] Uploading to R2..."
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
    Write-Host "`nWhen you send it, mention that the build is code-signed, and that" -ForegroundColor Yellow
    Write-Host "  SmartScreen may still caution for a while: its reputation builds with" -ForegroundColor White
    Write-Host "  downloads. Unmentioned, a warning becomes the feedback instead of the bug." -ForegroundColor White
    exit 0
}

Step "[5/5] Creating GitHub release..."
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

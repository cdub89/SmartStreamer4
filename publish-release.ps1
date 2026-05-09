param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "SmartSDRIQStreamer.csproj"
$publishDir = Join-Path $PSScriptRoot "bin/$Configuration/net8.0-windows/$Runtime/publish"

Write-Host "== SmartStreamer4 release publish ==" -ForegroundColor Cyan
Write-Host "Project: $projectPath"
Write-Host "Runtime: $Runtime"
Write-Host "Configuration: $Configuration"

if (-not $SkipTests) {
    Write-Host "`n[1/4] Running tests..." -ForegroundColor Yellow
    dotnet test
}
else {
    Write-Host "`n[1/4] Skipping tests (--SkipTests)." -ForegroundColor Yellow
}

Write-Host "`n[2/4] Publishing self-contained single-file executable..." -ForegroundColor Yellow
dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugSymbols=false `
    -p:DebugType=None

Write-Host "`n[3/4] Cleaning non-exe publish artifacts..." -ForegroundColor Yellow
$dllConfigPath = Join-Path $publishDir "SmartStreamer4.dll.config"
if (Test-Path $dllConfigPath) {
    Remove-Item $dllConfigPath -Force
}

# csproj <Version> keeps the dash (e.g. 0.1.18-b); release zip and tag
# strip it (v0.1.18b). Path A consolidation, 2026-05-09: script now
# emits the final release name directly, so no manual rename step.
$version = ([xml](Get-Content $projectPath)).Project.PropertyGroup.Version
$cleanVersion = $version -replace '-', ''
$exeName = "SmartStreamer4.exe"
$exePath = Join-Path $publishDir $exeName
$zipLabel = "SmartStreamer4-v${cleanVersion}.zip"
$zipPath = Join-Path $publishDir $zipLabel

Write-Host "`n[4/4] Creating release zip..." -ForegroundColor Yellow
Compress-Archive -Path $exePath -DestinationPath $zipPath -Force

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()

Write-Host "`nPublish output:" -ForegroundColor Green
Get-ChildItem $publishDir | Sort-Object Name | Format-Table Name, Length, LastWriteTime -AutoSize

Write-Host "`nSHA256: $hash  $zipLabel" -ForegroundColor Cyan
Write-Host "`nNext: bump SHA256SUMS.txt, commit release assets, then publish to GitHub:" -ForegroundColor Yellow
Write-Host "  gh release create v$cleanVersion `"$zipPath#$zipLabel`" --title `"SmartStreamer4 v$cleanVersion`" --notes `"...`" --latest" -ForegroundColor White

Write-Host "`nDone." -ForegroundColor Green

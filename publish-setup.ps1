# Builds the KalOS Setup wizard (unpackaged, self-contained, requireAdministrator)
# and packages it as
#   dist\KalOS-Setup-v{version}-win-x64.zip
#
# Attach that zip to the matching GitHub release (e.g. tag v1.0.0.6) alongside
# the consumer KalOS.zip. The wizard resolves the latest release at run time,
# downloads the consumer app, deploys it natively, and falls back to
# install-kalos.ps1 when the native path is unavailable.
#
# Usage:  powershell -ExecutionPolicy Bypass -File publish-setup.ps1
#         powershell -ExecutionPolicy Bypass -File publish-setup.ps1 -Platform ARM64
param(
    [string]$Config = "Release",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$installerProj = Join-Path $PSScriptRoot "Installer\KalOS.Installer.csproj"

Write-Host "Building KalOS Setup wizard ($Config, $Platform)..." -ForegroundColor Cyan
# Single-file self-contained: the wizard ships as one KalOS.Setup.exe with the
# WinAppSDK runtime embedded, so a fresh Windows install has zero prerequisites
# to run it (no .NET desktop runtime, no WinAppSDK download).
dotnet publish $installerProj -c $Config -p:Platform=$Platform -p:RuntimeIdentifier=win-$Platform -p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "Setup build failed with exit code $LASTEXITCODE" }

# The publish output lands under bin\<Platform>\<Config>\net9.0...\<RID>\publish
$publishDir = Join-Path $PSScriptRoot "Installer\bin\$Platform\$Config\net9.0-windows10.0.22621.0\win-$Platform\publish"
if (-not (Test-Path (Join-Path $publishDir "KalOS.Setup.exe"))) { throw "Publish output not found: $publishDir" }

# Strip debug symbols to slim the payload.
Get-ChildItem -Path $publishDir -Recurse -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

# Version comes from the installer csproj so the zip name tracks the release tag.
$csproj = Get-Content $installerProj -Raw
$match = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
if (-not $match.Success) { throw "Could not read <Version> from Installer\KalOS.Installer.csproj" }
$version = $match.Groups[1].Value

$dist = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Path $dist -Force | Out-Null
# Keep only this version's setup package — stale zips must not leak onto a release.
Get-ChildItem -Path $dist -Filter "KalOS-Setup-v*-win-$Platform.zip" -ErrorAction SilentlyContinue | Remove-Item -Force
$zip = Join-Path $dist "KalOS-Setup-v$version-win-$Platform.zip"

Write-Host "Packaging $zip ..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host ""
Write-Host "Setup package ready: $zip" -ForegroundColor Green
Write-Host "Next: attach this zip to the GitHub release tagged v$version on 1k09-byte/KalOSTOOLKIT"
Write-Host "      (alongside the consumer KalOS.zip; the wizard downloads and deploys it.)"

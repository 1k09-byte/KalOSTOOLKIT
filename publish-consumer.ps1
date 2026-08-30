# Builds the consumer (distributable) build of KalOS and packages it as
#   dist\KalOS-v{version}-win-x64.zip
# Attach that zip to the matching GitHub release (e.g. tag v1.0.0.4) and
# every installed copy of KalOS will auto-update to it.
#
# Usage:  powershell -ExecutionPolicy Bypass -File publish-consumer.ps1
param(
    [string]$Config = "Release",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Building KalOS ($Config, $Platform)..."
# CONSUMER_BUILD gates the update feature: only the distributed build checks
# for updates — the dev toolkit never nags about new versions.
dotnet build KalOS.csproj -c $Config -p:Platform=$Platform -p:RuntimeIdentifier=win-x64 -p:DefineConstants=CONSUMER_BUILD
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$out = Join-Path $PSScriptRoot "bin\$Platform\$Config\net9.0-windows10.0.22621.0\win-x64"
if (-not (Test-Path (Join-Path $out "KalOS.exe"))) { throw "Output not found: $out" }

# Strip debug symbols to slim the package.
Get-ChildItem -Path $out -Recurse -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

# OS-changes payload: os-changes.json + every .ps1 it references must sit next to
# KalOS.exe inside the zip, or the consumer app's "Apply changes" button has
# nothing to run. Copy from the repo root (source of truth) if the build didn't.
$osChanges = Join-Path $PSScriptRoot "os-changes.json"
if (Test-Path $osChanges) {
    Copy-Item $osChanges $out -Force
    # Parse the manifest (JSONC — strip // comments) and copy each referenced script.
    $raw = (Get-Content $osChanges -Raw) -replace '(?m)^\s*//.*$', ''
    try { $manifest = $raw | ConvertFrom-Json } catch { throw "os-changes.json is not valid JSON: $_" }
    foreach ($change in $manifest.changes) {
        if ($change.type -eq 'script' -and $change.script) {
            $scriptSrc = Join-Path $PSScriptRoot $change.script
            if (-not (Test-Path $scriptSrc)) { throw "os-changes.json references missing script: $change.script" }
            Copy-Item $scriptSrc $out -Force
            Write-Host "Packed OS script: $($change.script)"
        }
    }
    Write-Host "Packed os-changes.json (version $($manifest.version))"
}

# Version comes from the csproj so the zip name always matches the release tag.
$csproj = Get-Content (Join-Path $PSScriptRoot "KalOS.csproj") -Raw
$match = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
if (-not $match.Success) { throw "Could not read <Version> from KalOS.csproj" }
$version = $match.Groups[1].Value

$dist = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Path $dist -Force | Out-Null
# Keep only this version's package — stale zips from older builds can linger
# and get swept onto a release by a broad upload glob.
Get-ChildItem -Path $dist -Filter "KalOS.zip" -ErrorAction SilentlyContinue | Remove-Item -Force
$zip = Join-Path $dist "KalOS.zip"

Write-Host "Packaging $zip ..."
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host ""
Write-Host "Consumer package ready: $zip"
Write-Host "Next: attach this zip to the GitHub release tagged v$version on 1k09-byte/KalOSTOOLKIT."

# Builds the STANDALONE OFFLINE installer and publishes it as ONE exe:
#   dist\KaliteKit.Setup.exe
#
# The KaliteKit consumer release zip is embedded inside the installer exe
# (build property KaliteKitPayloadZip -> managed resource), so the resulting
# single file can install KaliteKit with zero network access: no GitHub lookup,
# no package download, no install script.
#
# The payload zip is only a build intermediate: once the exe is published,
# dist\ holds exactly one file (KaliteKit.Setup.exe) and nothing else.
#
# IMPORTANT: keep the published file named KaliteKit.Setup.exe. WinUI loads its
# ms-appx resources (the .pri resource map) by module name, so renaming the
# single-file exe breaks every XAML lookup ("Cannot locate resource from
# 'ms-appx:///MainWindow.xaml'"). Version the file metadata, not the name.
#
# Usage:  powershell -ExecutionPolicy Bypass -File publish-standalone.ps1
param(
    [string]$Config = "Release",
    [string]$Platform = "x64",
    # Reuse the newest existing dist\KaliteKit-v*.zip instead of rebuilding the
    # consumer app first (speeds up iteration on the installer itself; note the
    # payload zip is removed when the publish finishes, so the final release
    # run should go without this switch).
    [switch]$SkipConsumerZip
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# ---- Step 1: consumer payload -------------------------------------------
# The app zip is the offline payload the installer embeds and deploys.
if (-not $SkipConsumerZip) {
    Write-Host "Building the consumer KaliteKit payload..."
    & (Join-Path $PSScriptRoot "publish-consumer.ps1") -Config $Config -Platform $Platform
    if ($LASTEXITCODE -ne 0) { throw "Consumer publish failed with exit code $LASTEXITCODE" }
}

$zip = Get-ChildItem (Join-Path $PSScriptRoot "dist") -Filter "KaliteKit*.zip" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $zip) { throw "No consumer zip found in dist\ - run publish-consumer.ps1 first (or drop the -SkipConsumerZip switch)." }
$zipFull = $zip.FullName
Write-Host ("Embedding payload: " + $zipFull + " (" + [math]::Round($zip.Length / 1MB, 1) + " MB)")

# ---- Step 2: publish the installer as a single exe with the payload -----
$outDir = Join-Path $PSScriptRoot "Installer\output"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

Write-Host "Publishing the standalone installer (single-file, payload embedded)..."
dotnet publish (Join-Path $PSScriptRoot "Installer\KaliteKit.Installer.csproj") `
    -c $Config -p:Platform=$Platform -p:KaliteKitPayloadZip="$zipFull" -o $outDir
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $outDir "KaliteKit.Setup.exe"
if (-not (Test-Path $exe)) { throw "Single-file exe not found: $exe" }

# ---- Step 3: drop sidecar files and stage the release asset --------------
# Strip the .pdb a single-file publish can leave next to the exe (if any).
Get-ChildItem -Path $outDir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

# Only the one exe may remain next to the release.
$leftovers = Get-ChildItem -Path $outDir -File | Where-Object { $_.Name -ne "KaliteKit.Setup.exe" }
foreach ($leftover in $leftovers) { Remove-Item $leftover.FullName -Force }

$dist = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Copy-Item $exe $dist -Force

# The payload zip was only the embed source - dist ends with ONE file, the exe.
# (Run publish-consumer.ps1 separately if you also want the app zip for GitHub.)
Remove-Item $zipFull -Force

$sizeMB = [math]::Round((Get-Item (Join-Path $dist "KaliteKit.Setup.exe")).Length / 1MB, 1)
Write-Host ""
Write-Host "Offline installer ready:"
Write-Host ("  dist\KaliteKit.Setup.exe  (" + $sizeMB + " MB - the only file in dist, single self-contained exe)")

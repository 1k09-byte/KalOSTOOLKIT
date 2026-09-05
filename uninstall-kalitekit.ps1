<#
.SYNOPSIS
    Completely uninstalls the consumer build of KaliteKit.

.DESCRIPTION
    Safely terminates any running instances of KaliteKit and its hardware 
    monitoring worker, deletes the installation directory, and removes 
    Start Menu and Desktop shortcuts.

.EXAMPLE
    .\uninstall-kalitekit.ps1
    .\uninstall-kalitekit.ps1 -InstallDir "C:\Custom\KaliteKit"
    .\uninstall-kalitekit.ps1 -Silent

    Double-clicking the script also runs it. 
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\KaliteKit"),
    [switch]$Silent
)

$ErrorActionPreference = "Stop"
$canPrompt = -not $Silent

function Write-Step([string]$msg) {
    if (-not $Silent) {
        Write-Host ""
        Write-Host "==> $msg" -ForegroundColor Cyan
    }
}

function Write-OutputMsg([string]$msg, [string]$color = "White") {
    if (-not $Silent) { Write-Host $msg -ForegroundColor $color }
}

# Keep the window open if double clicked
$launchedByExplorer = $false
try {
    $currentPid = [System.Diagnostics.Process]::GetCurrentProcess().Id
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId = $currentPid").ParentProcessId
    $parentName = (Get-Process -Id $parent -ErrorAction SilentlyContinue).ProcessName
    $launchedByExplorer = $parentName -in @("explorer", "openwith")
} catch { }


Write-Step "Checking for running instances of KaliteKit..."
$processes = @("KaliteKit", "HardwareMonitorWorker")
foreach ($proc in $processes) {
    $running = Get-Process -Name $proc -ErrorAction SilentlyContinue
    if ($running) {
        Write-OutputMsg "Stopping $proc ..." "Yellow"
        Stop-Process -Name $proc -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

Write-Step "Removing Installation Directory..."
if (Test-Path $InstallDir) {
    try {
        Remove-Item -Path $InstallDir -Recurse -Force
        Write-OutputMsg "Removed: $InstallDir" "Green"
    } catch {
        Write-OutputMsg "Failed to completely remove $InstallDir. It might be locked by another process." "Red"
    }
} else {
    Write-OutputMsg "Install directory not found, skipping." "Gray"
}

Write-Step "Removing Shortcuts..."
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\KaliteKit.lnk"
if (Test-Path $startMenu) {
    Remove-Item $startMenu -Force
    Write-OutputMsg "Removed Start Menu shortcut." "Green"
}

$desktop = [Environment]::GetFolderPath("Desktop")
if (-not $desktop -or -not (Test-Path $desktop)) {
    $desktop = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) ""
}
$desktopLnk = Join-Path $desktop "KaliteKit.lnk"

if (Test-Path $desktopLnk) {
    Remove-Item $desktopLnk -Force
    Write-OutputMsg "Removed Desktop shortcut." "Green"
}

Write-Step "Uninstallation Complete!"
Write-OutputMsg "KaliteKit has been successfully removed from this system." "Green"

if ($launchedByExplorer -and -not $Silent) {
    Write-Host ""
    Read-Host "Press Enter to close this window"
}

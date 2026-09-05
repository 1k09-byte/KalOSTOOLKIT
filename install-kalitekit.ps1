<#
.SYNOPSIS
    Installs the KaliteKit app - one app that IS also the installer.

.DESCRIPTION
    Default: downloads the newest KaliteKit release (KaliteKit-v{version}-win-x64.zip)
    from GitHub Releases, installs it to %LOCALAPPDATA%\Programs\KaliteKit, and
    launches it. The app now contains the full setup wizard: on FIRST launch
    it opens as "KaliteKit Setup" (install KaliteKit, GPU drivers, browsers &
    software, customize) and once setup completes it turns into the normal
    consumer app. Re-run the wizard any time with: KaliteKit.exe --setup

    -SetupWizard: instead install the old standalone KaliteKit Setup wizard exe
    (KaliteKit.Setup.exe) to %LOCALAPPDATA%\Programs\KaliteKitSetup. Not needed for
    normal installs - the app has the wizard built in.

    Both modes run a quick prerequisite check first (administrator permission
    and an internet connection). The packages are self-contained - the .NET
    runtime and Windows App SDK are bundled, so nothing extra is installed.

.EXAMPLE
    .\install-kalitekit.ps1                      # install + launch the KaliteKit app
    .\install-kalitekit.ps1 -SetupWizard         # install the standalone wizard instead
    .\install-kalitekit.ps1 -InstallDir "$env:USERPROFILE\KaliteKit" -NoShortcut
    .\install-kalitekit.ps1 -Silent

    Double-clicking the script also runs it. If Windows blocks scripts, right-click
    the file and choose 'Run with PowerShell'.
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\KaliteKit"),
    [switch]$SetupWizard,
    [switch]$NoShortcut,
    [switch]$NoTaskbarPin,
    [switch]$Silent,
    # Back-compat no-op: releases are self-contained, so no .NET runtime is installed.
    [switch]$InstallDotNetRuntime,
    [switch]$SkipDependencyCheck
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Owner = "1k09-byte"
$Repo = "KaliteKit"
$ReleasesLatestUrl = "https://github.com/$Owner/$Repo/releases/latest"
$SetupDir = (Join-Path $env:LOCALAPPDATA "Programs\KaliteKitSetup")

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Write-ErrorAndExit([string]$msg) {
    Write-Host "ERROR: $msg" -ForegroundColor Red
    exit 1
}

function Test-InteractiveConsole {
    try { return $host.UI.RawUI -ne $null -and -not [Console]::IsInputRedirected } catch { return $false }
}

function Invoke-Verb([string]$targetPath, [string]$pattern) {
    try {
        if (-not (Test-Path $targetPath)) { return $false }
        $shellApp = New-Object -ComObject Shell.Application
        $item = $shellApp.Namespace((Split-Path $targetPath)).ParseName((Split-Path $targetPath -Leaf))
        foreach ($verb in $item.Verbs()) {
            if ($verb.Name -match $pattern) { $verb.DoIt(); return $true }
        }
    }
    catch { }
    return $false
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-InternetConnection {
    try {
        $response = Invoke-WebRequest -Uri "https://github.com/status" -Method Head -UseBasicParsing -TimeoutSec 15
        return $response.StatusCode -ge 200 -and $response.StatusCode -lt 500
    }
    catch { return $false }
}

function Ensure-RequiredRuntime {
    Write-Step "Checking required dependencies"

    if (-not (Test-Administrator)) {
        Write-ErrorAndExit "Administrator permission is required. Right-click PowerShell and choose 'Run as administrator', then run the installer again."
    }
    Write-Host "Dependency check passed: Administrator permission is available." -ForegroundColor Green

    if (-not (Test-InternetConnection)) {
        Write-ErrorAndExit "An internet connection is required to download KaliteKit and its dependencies."
    }
    Write-Host "Dependency check passed: Internet connection is available." -ForegroundColor Green

    # No .NET runtime check: the consumer app zip and the standalone Setup wizard
    # are both self-contained (the .NET runtime and Windows App SDK ship inside
    # the package), so there is nothing extra to install.
}

$interactive = Test-InteractiveConsole
$canPrompt = -not $Silent

# When launched by double-click, PowerShell may close as soon as the script
# exits. Keep the window open so users can see progress and errors. Command-line
# callers can still use -Silent or invoke the script normally.
$launchedByExplorer = $false
try {
    $currentPid = [System.Diagnostics.Process]::GetCurrentProcess().Id
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId = $currentPid").ParentProcessId
    $parentName = (Get-Process -Id $parent -ErrorAction SilentlyContinue).ProcessName
    $launchedByExplorer = $parentName -in @("explorer", "openwith")
}
catch { }

# --- Check dependencies before any release lookup ---------------------------
if (-not $SkipDependencyCheck) {
    Ensure-RequiredRuntime
}

# --- Resolve latest release -------------------------------------------------
Write-Step "Checking latest KaliteKit release on $Owner/$Repo ..."
try {
    $req = [System.Net.WebRequest]::Create($ReleasesLatestUrl)
    $req.AllowAutoRedirect = $false
    $req.Timeout = 15000
    $res = $req.GetResponse()
    $redirectUrl = $res.Headers["Location"]
    $res.Close()

    if (-not $redirectUrl) { throw "No redirect location returned from GitHub." }

    $versionMatch = [regex]::Match($redirectUrl, '/tag/v(.*)$')
    if (-not $versionMatch.Success) { throw "Could not parse version from redirect URL: $redirectUrl" }
    $version = $versionMatch.Groups[1].Value

    # Fetch the dynamically rendered expanded_assets DOM fragment directly
    $assetsUrl = "https://github.com/$Owner/$Repo/releases/expanded_assets/v$version"
    $html = (Invoke-WebRequest -Uri $assetsUrl -UseBasicParsing -TimeoutSec 15 -Headers @{ "Accept" = "text/html" }).Content

    # Pick the payload for this mode:
    #   default      -> the consumer app package (KaliteKit-v*-win-x64.zip), which
    #                   has the setup wizard built in
    #   -SetupWizard -> the standalone wizard package (KaliteKit-Setup-v*-win-x64.zip)
    if ($SetupWizard) {
        $assetMatch = [regex]::Match($html, 'href="(/[^"]+/releases/download/[^"]+KaliteKit-Setup-[^"]+win-x64\.zip)"')
        if (-not $assetMatch.Success) {
            $assetMatch = [regex]::Match($html, 'href="(/[^"]+/releases/download/[^"]+KaliteKit-Setup-[^"]+\.zip)"')
        }
        if (-not $assetMatch.Success) {
            throw "Could not locate a KaliteKit-Setup zip attached to release v$version. Please build the standalone wizard (Installer/KaliteKit.Installer.csproj) and upload its zip to the release."
        }
    }
    else {
        $assetMatch = [regex]::Match($html, 'href="(/[^"]+/releases/download/[^"]+\.zip)"')
        if (-not $assetMatch.Success) {
            throw "Could not locate a .zip payload attached to release v$version. Please ensure a zip file is uploaded to GitHub."
        }
    }

    $downloadUrl = "https://github.com" + $assetMatch.Groups[1].Value

    Write-Host "Latest version: $version" -ForegroundColor Green
}
catch {
    Write-ErrorAndExit "Failed to fetch latest release from GitHub: $_"
}

if (-not $SetupWizard) {
    # ---------------------------------------------------------------------
    # Default mode: download and install the KaliteKit app (wizard included).
    # ---------------------------------------------------------------------
    Write-Host "Dependency check passed. Continuing with KaliteKit installation..." -ForegroundColor Green

    # --- Download and extract ---------------------------------------------------
    $tmpZip = Join-Path $env:TEMP "KaliteKit-$version.zip"
    Write-Step "Downloading KaliteKit v$version ..."
    try {
        Invoke-WebRequest -Uri $downloadUrl -OutFile $tmpZip -UseBasicParsing
    }
    catch {
        Write-ErrorAndExit "Download failed: $($_.Exception.Message)"
    }

    $staging = Join-Path $env:TEMP "KaliteKit-$version-staging"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    try {
        Expand-Archive -Path $tmpZip -DestinationPath $staging -Force
    }
    catch {
        Write-ErrorAndExit "Extract failed (corrupt download?): $($_.Exception.Message)"
    }

    $requiredFiles = @(
        "KaliteKit.exe",
        "hostfxr.dll",
        "hostpolicy.dll",
        "coreclr.dll",
        "HardwareMonitorWorker.exe"
    )
    $missingFiles = $requiredFiles | Where-Object { -not (Test-Path (Join-Path $staging $_)) }
    if ($missingFiles) {
        Write-ErrorAndExit "The release package is missing required files: $($missingFiles -join ', ')"
    }
    Write-Host "Dependency check passed: release package contains all required files." -ForegroundColor Green

    Write-Step "Installing to $InstallDir ..."
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Get-ChildItem -Path $InstallDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    Copy-Item -Path "$staging\*" -Destination $InstallDir -Recurse -Force
    Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

    # --- Shortcuts --------------------------------------------------------------
    $exePath = Join-Path $InstallDir "KaliteKit.exe"
    if (-not $NoShortcut) {
        $shell = New-Object -ComObject WScript.Shell
        $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
        $lnkPath = Join-Path $startMenu "KaliteKit.lnk"
        $lnk = $shell.CreateShortcut($lnkPath)
        $lnk.TargetPath = $exePath
        $lnk.WorkingDirectory = $InstallDir
        $lnk.Description = "KaliteKit $version - Windows post-install utility"
        $lnk.Save()
        Write-Host "Shortcut created: $lnkPath"

        $desktop = [Environment]::GetFolderPath("Desktop")
        if (-not $desktop -or -not (Test-Path $desktop)) {
            $desktop = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) ""
        }
        $desktopLnk = Join-Path $desktop "KaliteKit.lnk"
        $lnk2 = $shell.CreateShortcut($desktopLnk)
        $lnk2.TargetPath = $exePath
        $lnk2.WorkingDirectory = $InstallDir
        $lnk2.Description = "KaliteKit $version - Windows post-install utility"
        $lnk2.Save()
        Write-Host "Shortcut created: $desktopLnk"
    }

    # --- Pin to taskbar (best effort) -------------------------------------------
    if (-not $NoTaskbarPin) {
        $openShell = Test-Path "HKCU:\Software\OpenShell\StartMenu" -ErrorAction SilentlyContinue
        $pinned = $false
        if (-not $openShell) {
            if (Invoke-Verb $exePath "(?i)^Pin to taskbar") { $pinned = $true }
            elseif (Test-Path $lnkPath) { $pinned = Invoke-Verb $lnkPath "(?i)^Pin to taskbar" }
        }
        if ($pinned) { Write-Host "Pinned to taskbar." }
        elseif ($openShell) { Write-Host "Skipped taskbar pin - Open-Shell is installed." }
        else { Write-Host "Already pinned to taskbar, or pinning unsupported - skipping." }
    }

    Write-Host ""
    Write-Host "KaliteKit $version installed successfully!" -ForegroundColor Green
    Write-Host "On first launch the app opens as KaliteKit Setup (install KaliteKit, GPU drivers,"
    Write-Host "browsers & software, customize) and turns into the full app when setup finishes."
    Write-Host "Launch it from the Start Menu (KaliteKit) or run:"
    Write-Host "    $exePath"
    Write-Host "Re-run setup any time with: KaliteKit.exe --setup"

    if ($canPrompt) {
        Start-Process $exePath
    }
}
else {
    # ---------------------------------------------------------------------
    # -SetupWizard: download and install the standalone KaliteKit Setup wizard.
    # ---------------------------------------------------------------------
    Write-Host "Dependency check passed. Continuing with KaliteKit Setup installation..." -ForegroundColor Green

    # --- Download and extract ---------------------------------------------------
    $tmpZip = Join-Path $env:TEMP "KaliteKit-Setup-$version.zip"
    Write-Step "Downloading KaliteKit Setup v$version ..."
    try {
        Invoke-WebRequest -Uri $downloadUrl -OutFile $tmpZip -UseBasicParsing
    }
    catch {
        Write-ErrorAndExit "Download failed: $($_.Exception.Message)"
    }

    $staging = Join-Path $env:TEMP "KaliteKit-Setup-$version-staging"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    try {
        Expand-Archive -Path $tmpZip -DestinationPath $staging -Force
    }
    catch {
        Write-ErrorAndExit "Extract failed (corrupt download?): $($_.Exception.Message)"
    }

    # The Setup wizard is a self-contained single-file exe - that one file is
    $setupExe = Join-Path $staging "KaliteKit.Setup.exe"
    if (-not (Test-Path $setupExe)) {
        Write-ErrorAndExit "The release package is missing KaliteKit.Setup.exe."
    }
    Write-Host "Dependency check passed: Setup package contains the wizard." -ForegroundColor Green

    Write-Step "Installing KaliteKit Setup to $SetupDir ..."
    New-Item -ItemType Directory -Path $SetupDir -Force | Out-Null
    Get-ChildItem -Path $SetupDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    Copy-Item -Path "$staging\*" -Destination $SetupDir -Recurse -Force
    Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

    # --- Shortcut ---------------------------------------------------------------
    $wizardPath = Join-Path $SetupDir "KaliteKit.Setup.exe"
    if (-not $NoShortcut) {
        $shell = New-Object -ComObject WScript.Shell
        $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
        $lnkPath = Join-Path $startMenu "KaliteKit Setup.lnk"
        $lnk = $shell.CreateShortcut($lnkPath)
        $lnk.TargetPath = $wizardPath
        $lnk.WorkingDirectory = $SetupDir
        $lnk.Description = "KaliteKit Setup $version - install KaliteKit, GPU drivers, browsers & software"
        $lnk.Save()
        Write-Host "Shortcut created: $lnkPath"
    }

    Write-Host ""
    Write-Host "KaliteKit Setup $version installed successfully!" -ForegroundColor Green
    Write-Host "The setup wizard opens next - it installs KaliteKit, GPU drivers, browsers & software."
    Write-Host "Relaunch it any time from the Start Menu (KaliteKit Setup) or run:"
    Write-Host "    $wizardPath"

    if ($canPrompt) {
        Start-Process $wizardPath -Verb RunAs
    }
}

if ($launchedByExplorer -and -not $Silent) {
    Write-Host ""
    Read-Host "Press Enter to close this window"
}

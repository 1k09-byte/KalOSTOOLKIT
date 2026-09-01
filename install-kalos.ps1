<#
.SYNOPSIS
    Installs the KalOS Setup wizard, or (with -InstallTool) the KalOS app itself.

.DESCRIPTION
    Default: downloads the newest KalOS Setup wizard (KalOS-Setup-v{version}-win-x64.zip)
    from GitHub Releases, installs it to %LOCALAPPDATA%\Programs\KalOSSetup, and
    launches it. The wizard then walks through deploying KalOS, GPU drivers,
    software, and tweaks.

    -InstallTool: legacy mode - downloads and installs the KalOS app directly
    (same behavior this script had before the Setup wizard existed). The Setup
    wizard's script fallback uses this mode.

    Both modes run the full dependency checker first: administrator permission,
    internet connection, and .NET 9 Desktop Runtime (auto-installed when
    missing) - the KalOS app deployed by the wizard needs that runtime.

.EXAMPLE
    .\install-kalos.ps1                # install + launch the Setup wizard
    .\install-kalos.ps1 -InstallTool   # install the KalOS app directly
    .\install-kalos.ps1 -InstallTool -InstallDir "$env:USERPROFILE\KalOS" -NoShortcut
    .\install-kalos.ps1 -Silent

    Double-clicking the script also runs it. If Windows blocks scripts, right-click
    the file and choose 'Run with PowerShell'.
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\KalOS"),
    [switch]$InstallTool,
    [switch]$NoShortcut,
    [switch]$NoTaskbarPin,
    [switch]$Silent,
    [switch]$InstallDotNetRuntime,
    [switch]$SkipDependencyCheck
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Owner = "1k09-byte"
$Repo = "KalOSTOOLKIT"
$AssetPrefix = "KalOS-v"
$ReleasesLatestUrl = "https://github.com/$Owner/$Repo/releases/latest"
$DotNetRuntimeUrl = "https://dotnet.microsoft.com/download/dotnet/thank-you/runtime-desktop-9.0.0-windows-x64-installer"
$RequiredOsBuild = 22621
$SetupDir = (Join-Path $env:LOCALAPPDATA "Programs\KalOSSetup")

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

function Test-DotNetDesktopRuntime {
    try {
        $runtimes = & dotnet --list-runtimes 2>$null
        return [bool]($runtimes -match "Microsoft\.WindowsDesktop\.App 9\.")
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
        Write-ErrorAndExit "An internet connection is required to download KalOS and its dependencies."
    }
    Write-Host "Dependency check passed: Internet connection is available." -ForegroundColor Green

    if (Test-DotNetDesktopRuntime) {
        Write-Host "Dependency check passed: .NET Desktop Runtime 9 is installed." -ForegroundColor Green
        return
    }

    Write-Host ".NET Desktop Runtime 9 is required and was not found." -ForegroundColor Yellow

    Write-Host "Auto-installing .NET Desktop Runtime 9..." -ForegroundColor Cyan

    $runtimeInstaller = Join-Path $env:TEMP "windowsdesktop-runtime-9-x64.exe"
    Write-Step "Downloading .NET 9 Desktop Runtime ..."
    try {
        Invoke-WebRequest -Uri $DotNetRuntimeUrl -OutFile $runtimeInstaller -UseBasicParsing
        Write-Step "Installing .NET 9 Desktop Runtime ..."
        Start-Process -FilePath $runtimeInstaller -ArgumentList "/install", "/quiet", "/norestart" -Wait
    }
    catch {
        Write-ErrorAndExit "Could not install .NET 9 Desktop Runtime: $($_.Exception.Message)"
    }
    finally {
        Remove-Item $runtimeInstaller -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-DotNetDesktopRuntime)) {
        Write-ErrorAndExit "The .NET 9 Desktop Runtime installation did not complete successfully."
    }
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
Write-Step "Checking latest KalOS release on $Owner/$Repo ..."
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
    #   default      -> the Setup wizard package (KalOS-Setup-v*-win-x64.zip)
    #   -InstallTool -> the consumer app package (KalOS-v*-win-x64.zip)
    if ($InstallTool) {
        $assetMatch = [regex]::Match($html, 'href="(/[^"]+/releases/download/[^"]+\.zip)"')
        if (-not $assetMatch.Success) {
            throw "Could not locate a .zip payload attached to release v$version. Please ensure a zip file is uploaded to GitHub."
        }
    }
    else {
        $assetMatch = [regex]::Match($html, 'href="(/[^"]+/releases/download/[^"]+KalOS-Setup-[^"]+win-x64\.zip)"')
        if (-not $assetMatch.Success) {
            $assetMatch = [regex]::Match($html, 'href="(/[^"]+/releases/download/[^"]+KalOS-Setup-[^"]+\.zip)"')
        }
        if (-not $assetMatch.Success) {
            throw "Could not locate a KalOS-Setup zip attached to release v$version. Please ensure publish-setup.ps1 output is uploaded to GitHub."
        }
    }

    $downloadUrl = "https://github.com" + $assetMatch.Groups[1].Value

    Write-Host "Latest version: $version" -ForegroundColor Green
}
catch {
    Write-ErrorAndExit "Failed to fetch latest release from GitHub: $_"
}

if ($InstallTool) {
    # ---------------------------------------------------------------------
    # Legacy mode: download and install the KalOS app directly.
    # ---------------------------------------------------------------------
    Write-Host "Dependency check passed. Continuing with KalOS installation..." -ForegroundColor Green

    # --- Download and extract ---------------------------------------------------
    $tmpZip = Join-Path $env:TEMP "KalOS-$version.zip"
    Write-Step "Downloading KalOS v$version ..."
    try {
        Invoke-WebRequest -Uri $downloadUrl -OutFile $tmpZip -UseBasicParsing
    }
    catch {
        Write-ErrorAndExit "Download failed: $($_.Exception.Message)"
    }

    $staging = Join-Path $env:TEMP "KalOS-$version-staging"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    try {
        Expand-Archive -Path $tmpZip -DestinationPath $staging -Force
    }
    catch {
        Write-ErrorAndExit "Extract failed (corrupt download?): $($_.Exception.Message)"
    }

    $requiredFiles = @(
        "KalOS.exe",
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
    $exePath = Join-Path $InstallDir "KalOS.exe"
    if (-not $NoShortcut) {
        $shell = New-Object -ComObject WScript.Shell
        $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
        $lnkPath = Join-Path $startMenu "KalOS.lnk"
        $lnk = $shell.CreateShortcut($lnkPath)
        $lnk.TargetPath = $exePath
        $lnk.WorkingDirectory = $InstallDir
        $lnk.Description = "KalOS $version - Windows post-install utility"
        $lnk.Save()
        Write-Host "Shortcut created: $lnkPath"

        $desktop = [Environment]::GetFolderPath("Desktop")
        if (-not $desktop -or -not (Test-Path $desktop)) {
            $desktop = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) ""
        }
        $desktopLnk = Join-Path $desktop "KalOS.lnk"
        $lnk2 = $shell.CreateShortcut($desktopLnk)
        $lnk2.TargetPath = $exePath
        $lnk2.WorkingDirectory = $InstallDir
        $lnk2.Description = "KalOS $version - Windows post-install utility"
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
    Write-Host "KalOS $version installed successfully!" -ForegroundColor Green
    Write-Host "Launch it from the Start Menu (KalOS) or run:"
    Write-Host "    $exePath"

    if ($canPrompt) {
        Start-Process $exePath
    }
}
else {
    # ---------------------------------------------------------------------
    # Default mode: download and install the KalOS Setup wizard.
    # ---------------------------------------------------------------------
    Write-Host "Dependency check passed. Continuing with KalOS Setup installation..." -ForegroundColor Green

    # --- Download and extract ---------------------------------------------------
    $tmpZip = Join-Path $env:TEMP "KalOS-Setup-$version.zip"
    Write-Step "Downloading KalOS Setup v$version ..."
    try {
        Invoke-WebRequest -Uri $downloadUrl -OutFile $tmpZip -UseBasicParsing
    }
    catch {
        Write-ErrorAndExit "Download failed: $($_.Exception.Message)"
    }

    $staging = Join-Path $env:TEMP "KalOS-Setup-$version-staging"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    try {
        Expand-Archive -Path $tmpZip -DestinationPath $staging -Force
    }
    catch {
        Write-ErrorAndExit "Extract failed (corrupt download?): $($_.Exception.Message)"
    }

    # The Setup wizard is a self-contained single-file exe - that one file is
    # the whole package. (KalOS-Installer.exe rides along when published.)
    $setupExe = Join-Path $staging "KalOS.Setup.exe"
    if (-not (Test-Path $setupExe)) {
        Write-ErrorAndExit "The release package is missing KalOS.Setup.exe."
    }
    Write-Host "Dependency check passed: Setup package contains the wizard." -ForegroundColor Green

    Write-Step "Installing KalOS Setup to $SetupDir ..."
    New-Item -ItemType Directory -Path $SetupDir -Force | Out-Null
    Get-ChildItem -Path $SetupDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    Copy-Item -Path "$staging\*" -Destination $SetupDir -Recurse -Force
    Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

    # --- Shortcut ---------------------------------------------------------------
    $wizardPath = Join-Path $SetupDir "KalOS.Setup.exe"
    if (-not $NoShortcut) {
        $shell = New-Object -ComObject WScript.Shell
        $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
        $lnkPath = Join-Path $startMenu "KalOS Setup.lnk"
        $lnk = $shell.CreateShortcut($lnkPath)
        $lnk.TargetPath = $wizardPath
        $lnk.WorkingDirectory = $SetupDir
        $lnk.Description = "KalOS Setup $version - install KalOS, drivers, software and tweaks"
        $lnk.Save()
        Write-Host "Shortcut created: $lnkPath"
    }

    Write-Host ""
    Write-Host "KalOS Setup $version installed successfully!" -ForegroundColor Green
    Write-Host "The setup wizard opens next - it installs KalOS, GPU drivers, software and tweaks."
    Write-Host "Relaunch it any time from the Start Menu (KalOS Setup) or run:"
    Write-Host "    $wizardPath"

    if ($canPrompt) {
        Start-Process $wizardPath -Verb RunAs
    }
}

if ($launchedByExplorer -and -not $Silent) {
    Write-Host ""
    Read-Host "Press Enter to close this window"
}

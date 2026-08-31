# KalOS

A native Windows 11 post-install utility built with **WinUI 3** (Windows App SDK 2.2.0) and **.NET 9**. Automates driver installation, browser & software setup, CPU/device affinity tuning, and personalization after a fresh Windows install.

Publisher: **KalOS** · 

## Features

- **Home** — System overview (CPU / GPU / memory / OS) plus Windows restore-point management (create, restore, list with empty state).
- **Browsers & Software** — Installs Brave, Thorium, LibreWolf, Zen Browser, Discord, Steam, 7-Zip, Spotify via `winget` with a direct-download fallback when winget is unavailable or broken. Browser extensions are force-installed via registry policy (Chromium) or `policies.json` + registry (Firefox family) and wiped on uninstall.
- **GPU Drivers** — _(work in progress)_ placeholder page under Hardware; the previous driver tooling was removed to restart the feature.
- **Per-cpu-scheduling** — CPU/device interrupt optimization: MSI policies, advanced policies, P-core thread mapping, and per-device affinity configuration.
- **BIOS Manager** — Export, edit offline, and re-apply current UEFI settings via vendor WMI with import/diff preview.
- **Settings** — App theme (light/dark), window backdrop (Acrylic / Mica / Mica Alt / None), and custom title-bar icon — all persisted across launches.

## Install

Download the latest release from the [Releases](https://github.com/1k09-byte/KalOSTOOLKIT/releases) page, or install/update from the command line:

```powershell
powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/1k09-byte/KalOSTOOLKIT/main/install-kalos.ps1 | iex"
```


The `install-kalos.ps1` script handles downloading and updating the application. It checks for the required **.NET 9 Desktop Runtime** and offers to download and install it automatically when missing, then fetches the newest `KalOS-v{version}-win-x64.zip` from GitHub Releases, extracts it to `%LOCALAPPDATA%\Programs\KalOS`, creates shortcuts, and can launch the app. The release is self-contained and includes the Windows App SDK runtime and hardware-monitor worker dependencies. 

To completely remove the app, run the `uninstall-kalos.ps1` script. This dedicated uninstaller safely terminates any running instances, removes the installation folder, deletes all shortcuts, and wipes the deployment clean.

## Tech stack

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.WindowsAppSDK` | 2.2.0 | WinUI 3, Mica/Acrylic backdrops |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.26100.4654 | Windows SDK |
| `CommunityToolkit.Mvvm` | 8.4.0 | MVVM source generators (`[ObservableProperty]`, `[RelayCommand]`) |
| `CommunityToolkit.WinUI.Controls.SettingsControls` | 8.2.251219 | `SettingsCard` / `SettingsExpander` |
| `Microsoft.Extensions.DependencyInjection` | 9.0.0 | DI container |
| `System.Management` | 10.0.9 | WMI queries (restore points, devices) |
| `WinUIEx` | 2.5.1 | Window management, custom title bar |
| `FluentIcons.WinUI` | 2.1.328 | Fluent icon glyphs |



## Notes

- Requires administrator privileges for registry writes, package removal, and service management.
- Crash logs: `%LOCALAPPDATA%\KalOS\CrashLogs\` (last 5 kept).
- License: KalOS is MIT — see `LICENSE.md`. Third-party components have their own terms (`THIRD-PARTY-NOTICES.md`).
- See `THIRD-PARTY-NOTICES.md` for dependency and asset licensing.

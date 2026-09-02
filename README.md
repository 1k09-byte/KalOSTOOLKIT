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

KalOS is **one app that is also its own installer**: the command line installs the app itself, and on first launch the app opens as the **KalOS Setup** wizard (install KalOS, GPU drivers, browsers & software, customize, tweaks & cleanup). When setup completes, the same window turns into the full consumer app. Re-run setup any time with `KalOS.exe --setup`.

```powershell
powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/1k09-byte/KalOSTOOLKIT/main/install-kalos.ps1 | iex"
```

The `install-kalos.ps1` script runs the full **dependency checker** first — administrator permission, internet connection, and the **.NET 9 Desktop Runtime**, which is downloaded and installed automatically when missing (the KalOS app needs it). It then fetches the newest `KalOS-v{version}-win-x64.zip` from GitHub Releases, installs the app to `%LOCALAPPDATA%\Programs\KalOS`, creates shortcuts, and launches it — first launch runs the built-in setup wizard.

Prefer downloading manually? Grab `KalOS-v{version}-win-x64.zip` from the [Releases](https://github.com/1k09-byte/KalOSTOOLKIT/releases) page, extract it anywhere, and run `KalOS.exe` — it is self-contained and includes the Windows App SDK runtime and hardware-monitor worker dependencies.

To completely remove the app, run the `uninstall-kalos.ps1` script. This dedicated uninstaller safely terminates any running instances, removes the installation folder, deletes all shortcuts, and wipes the deployment clean.

## KalOS Setup wizard

The setup wizard is **compiled into the main app** (`Installer/` sources are included by `KalOS.csproj`; a shared `SetupState` marker decides whether the app boots into the wizard or the consumer shell). It walks a fresh Windows install through the whole KalOS stack in one run:

1. **Welcome** — resolves the latest KalOS release and reports any existing install.
2. **GPU Driver** — detects adapters, offers a silent NVIDIA/AMD driver update (Intel opens the vendor page).
3. **Browsers & Software** — the same shared catalog the in-app page uses, with winget → Chocolatey → Scoop → direct-download fallbacks.
4. **Customize** — tint + background image for the installed app.
5. **Tweaks & Cleanup** — native privacy.sexy catalog, every category on by default.
6. **Progress** — live step log + overall bar.
7. **Finish** — per-step result list; closing swaps into the consumer app.

The wizard deploys KalOS **natively** (download → validate → wipe-and-copy → shortcuts → taskbar pin) with an automatic fallback to `install-kalos.ps1` (which now installs the app directly) when GitHub is unreachable or the package fails validation. It source-shares the WinUI-free backend (driver stack, package managers, the install services) with the main app, so there is exactly one implementation of everything.

The standalone wizard exe (`Installer/KalOS.Installer.csproj`) still builds for advanced use; normal releases don't ship it. Build and package the release payload:

```powershell
powershell -ExecutionPolicy Bypass -File publish-consumer.ps1
# → dist\KalOS-v{version}-win-x64.zip    (the app - setup wizard included; the only release asset)
```

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

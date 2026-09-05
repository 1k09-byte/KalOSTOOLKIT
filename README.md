# KaliteKit

A native Windows 11 post-install utility built with **WinUI 3** (Windows App SDK 2.2.0) and **.NET 9**. Automates driver installation, browser & software setup, CPU/device affinity tuning, and personalization after a fresh Windows install.

Publisher: **KaliteKit** · 

## Features

- **Home** — System overview (CPU / GPU / memory / OS) plus Windows restore-point management (create, restore, list with empty state).
- **Browsers & Software** — Installs Brave, Thorium, LibreWolf, Zen Browser, Discord, Steam, 7-Zip, Spotify via `winget` with a direct-download fallback when winget is unavailable or broken. Browser extensions are force-installed via registry policy (Chromium) or `policies.json` + registry (Firefox family) and wiped on uninstall.
- **GPU Drivers** — _(work in progress)_ placeholder page under Hardware; the previous driver tooling was removed to restart the feature.
- **Per-cpu-scheduling** — CPU/device interrupt optimization: MSI policies, advanced policies, P-core thread mapping, and per-device affinity configuration.
- **BIOS Manager** — Export, edit offline, and re-apply current UEFI settings via vendor WMI with import/diff preview.
- **Settings** — App theme (light/dark), window backdrop (Acrylic / Mica / Mica Alt / None), and custom title-bar icon — all persisted across launches.

## Install

KaliteKit is **one app that is also its own installer**: the command line installs the app itself, and on first launch the app opens as the **KaliteKit Setup** wizard (install KaliteKit, GPU drivers, browsers & software, customize, tweaks & cleanup). When setup completes, the same window turns into the full consumer app. Re-run setup any time with `KaliteKit.exe --setup`.

```powershell
powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/1k09-byte/KaliteKit/main/install-kalitekit.ps1 | iex"
```

The `install-kalitekit.ps1` script runs the full **dependency checker** first — administrator permission, internet connection, and the **.NET 9 Desktop Runtime**, which is downloaded and installed automatically when missing (the KaliteKit app needs it). It then fetches the newest `KaliteKit-v{version}-win-x64.zip` from GitHub Releases, installs the app to `%LOCALAPPDATA%\Programs\KaliteKit`, creates shortcuts, and launches it — first launch runs the built-in setup wizard.

Prefer downloading manually? Grab `KaliteKit-v{version}-win-x64.zip` from the [Releases](https://github.com/1k09-byte/KaliteKit/releases) page, extract it anywhere, and run `KaliteKit.exe` — it is self-contained and includes the Windows App SDK runtime and hardware-monitor worker dependencies.

To completely remove the app, run the `uninstall-kalitekit.ps1` script. This dedicated uninstaller safely terminates any running instances, removes the installation folder, deletes all shortcuts, and wipes the deployment clean.

## KaliteKit Setup wizard

The setup wizard is **compiled into the main app** (`Installer/` sources are included by `KaliteKit.csproj`; a shared `SetupState` marker decides whether the app boots into the wizard or the consumer shell). It walks a fresh Windows install through the whole KaliteKit stack in one run:

1. **Welcome** — resolves the latest KaliteKit release and reports any existing install.
2. **GPU Driver** — detects adapters, offers a silent NVIDIA/AMD driver update (Intel opens the vendor page).
3. **Browsers & Software** — the same shared catalog the in-app page uses, with winget → Chocolatey → Scoop → direct-download fallbacks.
4. **Customize** — tint + background image for the installed app.
5. **Progress** — live step log + overall bar.
6. **Finish** — per-step result list; closing swaps into the consumer app.

The wizard deploys KaliteKit **natively** (download → validate → wipe-and-copy → shortcuts → taskbar pin) with an automatic fallback to `install-kalitekit.ps1` (which now installs the app directly) when GitHub is unreachable or the package fails validation. It source-shares the WinUI-free backend (driver stack, package managers, the install services) with the main app, so there is exactly one implementation of everything.

The standalone wizard exe (`Installer/KaliteKit.Installer.csproj`) still builds for advanced use; normal releases don't ship it. It can also be published as a **single offline installer exe** — the KaliteKit consumer payload is embedded inside it, so it deploys KaliteKit with no internet connection (no GitHub lookup, no download, no install script; GPU-driver and software steps remain optional, and only run for what you explicitly select). The output is literally one file, nothing else:

```powershell
powershell -ExecutionPolicy Bypass -File publish-standalone.ps1
# → dist\KaliteKit.Setup.exe   (one self-contained file - installs KaliteKit offline)
```

Build and package the regular release payload:

```powershell
powershell -ExecutionPolicy Bypass -File publish-consumer.ps1
# → dist\KaliteKit-v{version}-win-x64.zip    (the app - setup wizard included; the only release asset)
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
- Crash logs: `%LOCALAPPDATA%\KaliteKit\CrashLogs\` (last 5 kept).
- License: KaliteKit is MIT — see `LICENSE.md`. Third-party components have their own terms (`THIRD-PARTY-NOTICES.md`).
- See `THIRD-PARTY-NOTICES.md` for dependency and asset licensing.

## Process Control

The **Process Control** page (nav: 🎚️) gives Process Lasso–class control over per-process CPU priority, I/O priority, memory priority, and core affinity, with persistent rules that auto-reapply whenever a matching process launches.

**Feature set** (all included, no gating):

- **Sticky Rules** — rules persist by process name, full path, or command line (opt-in per rule, so unrelated same-named processes can be told apart), with optional 1-based **instance targeting** ("2nd instance of X"). Rules reapply to every new instance, including multiple simultaneous copies.
- **AutoBalance** — background sampling of per-process CPU load; temporarily lowers the priority of hogging background processes above a configurable threshold and restores them when load drops. Conservative defaults, user-configurable exclusions, never terminates anything.
- **Core Isolation presets** — one-click E-Cores Off / P-Cores Off / CCD0 Off / CCD1 Off / SMT Off via the Windows **CPU Sets** API (`SetProcessDefaultCpuSets`). CCD count is detected from L3-cache topology (real AMD CCD detection) with an "estimated" label when the heuristic fallback is used. Presets that don't apply to the detected CPU are hidden/disabled.
- **Core Cap** — dynamic core-count/percent cap re-evaluated on a timer, plus **Hard Throttle** (suspend duty cycle) for a strict percentage ceiling.
- **Spread Balancer** — distributes N running copies of one exe across distinct core groups.
- **Blocklist** — auto-terminates named processes on sight; **Instance Count Limits** cap simultaneous copies.
- **AutoRevive** — auto-relaunches a process when it exits unexpectedly; **Keep Running** protects a process from being closed (with an explicit Allow Close override).
- **Prevent Sleep rules** — blocks system sleep while a guarded process runs.
- **Boost Mode** — one click disables core parking / frequency scaling system-wide (powercfg), one click to revert; the previous AC/DC values are saved and restored. In plain terms: idle cores wake instantly and every core holds its max turbo frequency, at the cost of slightly higher idle power.
- **Foreground Boost** — raises the active foreground app to Above Normal (off by default).
- **Monitoring view** — live per-core CPU bars, total CPU history, memory, and disk throughput (PDH).
- **Action log** — human-readable record of every automatic action (what, when, why), viewable and exportable.
- **Safety rail** — Realtime priority / single-core pins on system-critical processes require explicit confirmation, and **Restore all managed** resets every touched process in one click.
- **Processor Groups** — CPU Sets work across groups, so >64-logical-CPU systems are handled correctly.
- **Export/Import** — full rule bundles as portable JSON.

**Elevation.** KaliteKit runs elevated (`requireAdministrator` in `app.manifest`), so all scheduling calls work without a helper process.

**Background persistence.** Rules enforce while KaliteKit is open, and — with the hidden `--rules` login session — from login onward even when the window is closed. The consumer build registers `KaliteKit.exe --rules` in the HKCU Run key automatically; the Engine tab shows the status and has a register/repair button. `--rules` runs a 1×1 off-screen window and the same engine; it is the only always-on part of the app (no tray icon, no service).

**Storage.** Rules and the action log live in `%LOCALAPPDATA%\KaliteKit\process-control\` (`rules.json`, `actions.json`, `boost-saved.json`).

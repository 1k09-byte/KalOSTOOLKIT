# Third-Party Notices

KalOS uses the following third-party components. License texts are available from
the links below or from the corresponding files in the NuGet package cache.

## Runtime NuGet packages

| Package | Version | License |
|---------|---------|---------|
| Microsoft.WindowsAppSDK | 2.2.0 | MIT — https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE |
| Microsoft.Windows.SDK.BuildTools (build-time) | 10.0.26100.4654 | Windows SDK license (build tooling, not shipped) |
| CommunityToolkit.Mvvm | 8.4.0 | MIT — https://licenses.nuget.org/MIT |
| CommunityToolkit.WinUI.Controls.SettingsControls | 8.2.251219 | MIT — https://licenses.nuget.org/MIT |
| Microsoft.Extensions.DependencyInjection | 9.0.0 | MIT — https://licenses.nuget.org/MIT |
| System.Management | 10.0.9 | MIT — https://licenses.nuget.org/MIT |
| WinUIEx | 2.5.1 | MIT — https://licenses.nuget.org/MIT |
| FluentIcons.WinUI | 2.1.328 | MIT — https://licenses.nuget.org/MIT |
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 / mixed — https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LICENSE |

## LibreHardwareMonitor integration

KalOS references `LibreHardwareMonitorLib` as an unmodified NuGet dependency to read sensor values. KalOS does not modify or statically incorporate LibreHardwareMonitor source files. The dependency is redistributed in the application output under its own license; the upstream source and license are available at https://github.com/LibreHardwareMonitor/LibreHardwareMonitor. If a future release modifies MPL-covered source files, those specific changes must be made available under the MPL-2.0 terms.

LibreHardwareMonitor itself is installed separately by the user through the official winget package ID `LibreHardwareMonitor.LibreHardwareMonitor`; KalOS does not repackage or alter that third-party application. Users should review the upstream license, package publisher, and any bundled driver terms before installation. This attribution is informational and is not a substitute for project-specific legal advice.

## Test-only NuGet packages

| Package | Version | License |
|---------|---------|---------|
| xunit | 2.9.3 | Apache-2.0 — https://licenses.nuget.org/Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | MIT — https://licenses.nuget.org/MIT |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT — https://licenses.nuget.org/MIT |
| coverlet.collector | 6.0.4 | MIT — https://licenses.nuget.org/MIT |

## Brand assets

The following image assets in `Assets/` are third-party brand marks used nominatively
to identify the corresponding products inside the app (not redistributed as artwork):

- Brave (icons8-brave-web-browser-48.png) — © Brave Software, Inc.
- LibreWolf (librewolf.png) — © LibreWolf contributors
- Zen Browser (zen-browser-dark.png) — © Zen Browser contributors
- Thorium (thorium.png) — © Thorium project
- Discord (discord.png) — © Discord Inc.
- Steam (steam.png) — © Valve Corporation
- Spotify (spotify.png) — © Spotify AB
- 7-Zip (7zip.png) — © Igor Pavlov

These logos are the property of their respective owners and are used for
identification purposes only.

## Icons8 attribution (required by the Icons8 free license)

The navigation icons in `Assets/` are sourced from **Icons8** (https://icons8.com)
and used under the Icons8 free license, which requires attribution:

- icon-home.png (Home)
- icon-overview.png (Overview)
- icon-browser.png (Browsers & Software)
- icon-gpu.png (GPU drivers)
- icon-drivers.png (Device drivers)
- icon-bios.png (BIOS Manager)
- icon-cpu.png (Per-CPU scheduling)
- icon-personalization.png (Personalization)
- icon-tweaks.png (Tweaks)
- icons8-brave-web-browser-48.png (Brave entry icon)

**Icons by [Icons8](https://icons8.com).** This attribution satisfies the Icons8
free-license requirement; a paid Icons8 license would remove it.

## Third-party tools and content downloaded at runtime

KalOS fetches the following directly from the vendor at runtime and runs them as
separate programs — none of them are redistributed inside KalOS release archives.
Each is governed by its own license/terms, presented by the vendor on execution:

- **7-Zip standalone runner (7zr.exe)** — downloaded from https://www.7-zip.org/a/7zr.exe
  to extract NVIDIA/AMD driver packages silently. 7-Zip is licensed under the GNU
  LGPL-2.1-or-later (parts BSD 3-clause) with the unRAR restriction; see
  https://www.7-zip.org/license.txt. © Igor Pavlov.
- **Radeon Software Slimmer 1.12.0** — downloaded from
  https://github.com/GSDragoon/RadeonSoftwareSlimmer and launched as its own
  process for AMD package customization. GPL-3.0 — © GSDragoon. KalOS does not
  link against, bundle, or modify it.
- **AMD official tools** — the Adrenalin auto-detect installer and the AMD Cleanup
  Utility (`amdcleanuputility.exe`) are downloaded from https://drivers.amd.com and
  executed with user consent under AMD's terms. © Advanced Micro Devices, Inc.
- **NVIDIA driver packages** — downloaded from https://us.download.nvidia.com and
  installed via pnputil under NVIDIA's own license terms. © NVIDIA Corporation.
- **Windhawk 1.7.3 installer + mods** — installer pinned from
  https://github.com/ramensoftware/windhawk; mods fetched from
  https://mods.windhawk.net. Each mod carries its own license listed in its
  metadata. © Ramen Software / mod authors.
- **Snappy Driver Installer Origin (SDIO)** — installed through the official
  winget package and executed externally when present. GPL-3.0 — © SDIO
  contributors. KalOS does not redistribute it.

## NovaOS tweaks attribution

The NVIDIA post-install tweaks in the installation-tweaks dialog are ported from
NovaOS's "Disable Nvidia Telemetry" script. Credit to the **NovaOS project** —
the attribution is also shown in-app on the tweaks dialog.

## Note on the application itself

KalOS itself is licensed under the **MIT License** — see `LICENSE.md`. Nothing in
this file changes that; it only documents the third-party components listed above,
which keep their own licenses and attribution requirements.

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

## Note on the application itself

The application code in this repository has no license file yet — the license (if
any) is the choice of the repository owner. Nothing in this file grants a license
to the KalOS application code itself.

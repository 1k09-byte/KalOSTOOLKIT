using System;
using System.Collections.Generic;
using System.Linq;

namespace KaliteKit.Models
{
    /// <summary>Which installer group an item belongs to (drives UI grouping in both UIs).</summary>
    public enum SoftwareGroup
    {
        Browsers,
        Apps,
        Runtimes
    }

    /// <summary>How the direct-download fallback installer runs (matches the app's FallbackInstallerType).</summary>
    public enum CatalogInstallerKind
    {
        Exe,
        Msi
    }

    /// <summary>
    /// One installable item of the KaliteKit software catalog — pure data, no UI
    /// types. The Browsers &amp; Software page maps each entry onto an
    /// <c>InstallableItem</c> (icons, extensions, data paths live there); the
    /// KaliteKit Setup wizard consumes the entries directly.
    /// </summary>
    public sealed record CatalogEntry
    {
        /// <summary>Display name (also the cross-UI key for icons/extensions).</summary>
        public required string Name { get; init; }

        public required string Description { get; init; }

        public required SoftwareGroup Group { get; init; }

        public string WingetId { get; init; } = string.Empty;
        public string ChocolateyId { get; init; } = string.Empty;
        public string ScoopName { get; init; } = string.Empty;

        /// <summary>Direct-download fallback used when no package manager can install the item.</summary>
        public string FallbackDownloadUrl { get; init; } = string.Empty;
        public string FallbackInstallerArgs { get; init; } = string.Empty;
        public CatalogInstallerKind InstallerKind { get; init; } = CatalogInstallerKind.Exe;

        /// <summary>Browsers additionally get forced extensions and profile data paths.</summary>
        public bool IsBrowser { get; init; }
        public bool IsChromium { get; init; }
    }

    /// <summary>
    /// The single source of truth for every item KaliteKit can install through
    /// package managers or direct downloads. Kept in sync with the README's
    /// "Browsers &amp; Software" feature list.
    /// </summary>
    public static class SoftwareCatalog
    {
        public static IReadOnlyList<CatalogEntry> All { get; } = Build();

        public static IReadOnlyList<CatalogEntry> Browsers { get; } =
            All.Where(e => e.Group == SoftwareGroup.Browsers).ToArray();

        public static IReadOnlyList<CatalogEntry> Apps { get; } =
            All.Where(e => e.Group == SoftwareGroup.Apps).ToArray();

        public static IReadOnlyList<CatalogEntry> Runtimes { get; } =
            All.Where(e => e.Group == SoftwareGroup.Runtimes).ToArray();

        /// <summary>Lookup by display name (case-insensitive); null when unknown.</summary>
        public static CatalogEntry? Find(string name) =>
            All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        private static CatalogEntry[] Build() => new[]
        {
            // ── Browsers ────────────────────────────────────────────────
            new CatalogEntry
            {
                Name = "Brave",
                Description = "Chromium-based browser with ad and tracker blocking built in at the network level, so pages load faster and you're protected without installing extensions. Also includes HTTPS-Everywhere-style upgrading, fingerprinting resistance, and an optional built-in Tor tab for extra anonymity.",
                Group = SoftwareGroup.Browsers,
                WingetId = "Brave.Brave",
                ChocolateyId = "brave",
                ScoopName = "brave",
                IsBrowser = true,
                IsChromium = true,
                FallbackDownloadUrl = "https://laptop-updates.brave.com/latest/BraveBrowserWin64.msi",
                FallbackInstallerArgs = "/quiet /qn /norestart",
                InstallerKind = CatalogInstallerKind.Msi,
            },
            new CatalogEntry
            {
                Name = "Thorium",
                Description = "A Chromium fork rebuilt from source with aggressive compiler optimizations (AVX2/AVX-512, LTO, PGO) rather than feature changes — same Chrome experience and extension support, just noticeably faster page loads and video decoding on modern CPUs.",
                Group = SoftwareGroup.Browsers,
                WingetId = "Alex313031.Thorium.AVX2",
                IsBrowser = true,
                IsChromium = true,
                FallbackDownloadUrl = "https://github.com/Alex313031/Thorium-Win/releases/latest/download/thorium_AVX2_mini_installer.exe",
                FallbackInstallerArgs = "/silent /install",
            },
            new CatalogEntry
            {
                Name = "LibreWolf",
                Description = "Firefox with the telemetry, sponsored content, and Mozilla data collection stripped out, plus hardening patches pulled from the Tor Browser project. You get standard Firefox extension support and UI, just locked down by default instead of needing to configure privacy settings yourself.",
                Group = SoftwareGroup.Browsers,
                WingetId = "LibreWolf.LibreWolf",
                ChocolateyId = "librewolf",
                ScoopName = "librewolf",
                IsBrowser = true,
                IsChromium = false,
                FallbackDownloadUrl = "https://dl.librewolf.net/librewolf/150.0.1-1/librewolf-150.0.1-1-windows-x86_64-setup.exe",
                FallbackInstallerArgs = "/S",
            },
            new CatalogEntry
            {
                Name = "Zen Browser",
                Description = "A Firefox fork focused on interface and workflow rather than just privacy: split-screen tabs, workspaces for separating contexts (work/personal), and a minimal, highly themeable UI — while still inheriting Firefox's engine and privacy tooling underneath.",
                Group = SoftwareGroup.Browsers,
                WingetId = "Zen-Team.Zen-Browser",
                IsBrowser = true,
                IsChromium = false,
                FallbackDownloadUrl = "https://github.com/zen-browser/desktop/releases/latest/download/zen.installer.exe",
                FallbackInstallerArgs = "/S",
            },

            // ── Apps ────────────────────────────────────────────────────
            new CatalogEntry
            {
                Name = "Discord",
                Description = "Voice and text chat for gamers.",
                Group = SoftwareGroup.Apps,
                WingetId = "Discord.Discord",
                ChocolateyId = "discord",
                ScoopName = "discord",
                FallbackDownloadUrl = "https://discord.com/api/download?platform=win",
                FallbackInstallerArgs = "/silent /install",
            },
            new CatalogEntry
            {
                Name = "Steam",
                Description = "Video game digital distribution service.",
                Group = SoftwareGroup.Apps,
                WingetId = "Valve.Steam",
                ChocolateyId = "steam",
                ScoopName = "steam",
                FallbackDownloadUrl = "https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe",
                FallbackInstallerArgs = "/S",
            },
            new CatalogEntry
            {
                Name = "7-Zip",
                Description = "File archiver with a high compression ratio.",
                Group = SoftwareGroup.Apps,
                WingetId = "7zip.7zip",
                ChocolateyId = "7zip",
                ScoopName = "7zip",
                FallbackDownloadUrl = "https://www.7-zip.org/a/7z2409-x64.exe",
                FallbackInstallerArgs = "/S",
            },
            new CatalogEntry
            {
                Name = "Spotify",
                Description = "Digital music service that gives you access to millions of songs.",
                Group = SoftwareGroup.Apps,
                WingetId = "Spotify.Spotify",
                ChocolateyId = "spotify",
                ScoopName = "spotify",
                FallbackDownloadUrl = "https://download.scdn.co/SpotifySetup.exe",
                FallbackInstallerArgs = "/silent",
            },

            // ── Runtimes ────────────────────────────────────────────────
            new CatalogEntry
            {
                Name = "Visual C++ 2005 (x86)",
                Description = "Microsoft Visual C++ 2005 Redistributable (x86). Required by older games and legacy software.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.VCRedist.2005.x86",
                FallbackDownloadUrl = "https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x86.EXE",
                FallbackInstallerArgs = "/q",
            },
            new CatalogEntry
            {
                Name = "Visual C++ 2005 (x64)",
                Description = "Microsoft Visual C++ 2005 Redistributable (x64). Required by older games and legacy software.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.VCRedist.2005.x64",
                FallbackDownloadUrl = "https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x64.EXE",
                FallbackInstallerArgs = "/q",
            },
            new CatalogEntry
            {
                Name = "Visual C++ 2008 (x86)",
                Description = "Microsoft Visual C++ 2008 Redistributable (x86). Needed by many mid-2000s applications.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.VCRedist.2008.x86",
                FallbackDownloadUrl = "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x86.exe",
                FallbackInstallerArgs = "/q",
            },
            new CatalogEntry
            {
                Name = "Visual C++ 2008 (x64)",
                Description = "Microsoft Visual C++ 2008 Redistributable (x64). Needed by many mid-2000s applications.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.VCRedist.2008.x64",
                FallbackDownloadUrl = "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x64.exe",
                FallbackInstallerArgs = "/q",
            },
            new CatalogEntry
            {
                Name = "Visual C++ 2010 (x86)",
                Description = "Microsoft Visual C++ 2010 Redistributable (x86). Common dependency for games and apps from ~2010-2015.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.VCRedist.2010.x86",
                FallbackDownloadUrl = "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x86.exe",
                FallbackInstallerArgs = "/q",
            },
            new CatalogEntry
            {
                Name = "Visual C++ 2010 (x64)",
                Description = "Microsoft Visual C++ 2010 Redistributable (x64). Common dependency for games and apps from ~2010-2015.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.VCRedist.2010.x64",
                FallbackDownloadUrl = "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe",
                FallbackInstallerArgs = "/q",
            },
            new CatalogEntry
            {
                Name = "Visual C++ 2012 (x86)",
                Description = "Microsoft Visual C++ 2012 Redistributable (x86). Required by apps targeting VS 2012.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.VCRedist.2012.x86",
                FallbackDownloadUrl = "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x86.exe",
                FallbackInstallerArgs = "/quiet /norestart",
            },
            new CatalogEntry
            {
                Name = "Visual C++ 2012 (x64)",
                Description = "Microsoft Visual C++ 2012 Redistributable (x64). Required by apps targeting VS 2012.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.VCRedist.2012.x64",
                FallbackDownloadUrl = "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe",
                FallbackInstallerArgs = "/quiet /norestart",
            },
            new CatalogEntry
            {
                Name = "Visual C++ 2013 (x86)",
                Description = "Microsoft Visual C++ 2013 Redistributable (x86). Used by many modern games and applications.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.VCRedist.2013.x86",
                FallbackDownloadUrl = "https://aka.ms/highdpimfc2013x86enu",
                FallbackInstallerArgs = "/quiet /norestart",
            },
            new CatalogEntry
            {
                Name = "Visual C++ 2013 (x64)",
                Description = "Microsoft Visual C++ 2013 Redistributable (x64). Used by many modern games and applications.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.VCRedist.2013.x64",
                FallbackDownloadUrl = "https://aka.ms/highdpimfc2013x64enu",
                FallbackInstallerArgs = "/quiet /norestart",
            },
            new CatalogEntry
            {
                Name = "Visual C++ 2015-2022 (x86)",
                Description = "Microsoft Visual C++ 2015-2022 Redistributable (x86). The latest VC++ runtime — covers 2015, 2017, 2019 and 2022.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.VCRedist.2015+.x86",
                FallbackDownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x86.exe",
                FallbackInstallerArgs = "/quiet /norestart",
            },
            new CatalogEntry
            {
                Name = "Visual C++ 2015-2022 (x64)",
                Description = "Microsoft Visual C++ 2015-2022 Redistributable (x64). The latest VC++ runtime — covers 2015, 2017, 2019 and 2022.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.VCRedist.2015+.x64",
                FallbackDownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
                FallbackInstallerArgs = "/quiet /norestart",
            },
            new CatalogEntry
            {
                Name = ".NET 6.0 Desktop Runtime",
                Description = "Microsoft .NET 6.0 Desktop Runtime. Required by apps built with .NET 6.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.DotNet.DesktopRuntime.6",
                FallbackDownloadUrl = "https://aka.ms/dotnet/6.0/windowsdesktop-runtime-win-x64.exe",
                FallbackInstallerArgs = "/quiet /norestart",
            },
            new CatalogEntry
            {
                Name = ".NET 7.0 Desktop Runtime",
                Description = "Microsoft .NET 7.0 Desktop Runtime. Required by apps built with .NET 7.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.DotNet.DesktopRuntime.7",
                FallbackDownloadUrl = "https://aka.ms/dotnet/7.0/windowsdesktop-runtime-win-x64.exe",
                FallbackInstallerArgs = "/quiet /norestart",
            },
            new CatalogEntry
            {
                Name = ".NET 8.0 Desktop Runtime",
                Description = "Microsoft .NET 8.0 Desktop Runtime. Required by apps built with .NET 8.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.DotNet.DesktopRuntime.8",
                FallbackDownloadUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe",
                FallbackInstallerArgs = "/quiet /norestart",
            },
            new CatalogEntry
            {
                Name = ".NET 9.0 Desktop Runtime",
                Description = "Microsoft .NET 9.0 Desktop Runtime. Required by apps built with .NET 9.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.DotNet.DesktopRuntime.9",
                FallbackDownloadUrl = "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe",
                FallbackInstallerArgs = "/quiet /norestart",
            },
            new CatalogEntry
            {
                Name = "DirectX End-User Runtime",
                Description = "Microsoft DirectX End-User Runtime. Installs D3DX9/10/11, XAudio, XInput and other legacy DirectX components needed by older games.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.DirectX",
                FallbackDownloadUrl = "https://download.microsoft.com/download/1/7/1/1718CCC4-6315-4D8E-9543-8E28A4E18C4C/dxwebsetup.exe",
                FallbackInstallerArgs = "/q",
            },
            new CatalogEntry
            {
                Name = "XNA Framework 4.0",
                Description = "Microsoft XNA Framework Redistributable 4.0. Required by indie games built with XNA/MonoGame.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Microsoft.XNARedist",
                FallbackDownloadUrl = "https://download.microsoft.com/download/A/C/2/AC2C903B-E6E8-42C2-9FD7-BEBAC362A930/xnafx40_redist.msi",
                FallbackInstallerArgs = "/quiet /norestart",
                InstallerKind = CatalogInstallerKind.Msi,
            },
            new CatalogEntry
            {
                Name = "Java Runtime (JRE)",
                Description = "Oracle Java Runtime Environment. Required by Minecraft (Java Edition), Eclipse, and many enterprise apps.",
                Group = SoftwareGroup.Runtimes,
                WingetId = "Oracle.JavaRuntimeEnvironment",
                FallbackDownloadUrl = "https://javadl.oracle.com/webapps/download/AutoDL?BundleId=251406_d79360ef13234098800e0d23e7e2cbb8",
                FallbackInstallerArgs = "/s",
            },
            new CatalogEntry
            {
                Name = "OpenAL",
                Description = "OpenAL audio library. Required by some games (older Unreal Engine titles, Minecraft mods, etc.).",
                Group = SoftwareGroup.Runtimes,
                WingetId = "OpenAL.OpenAL",
                FallbackDownloadUrl = "https://www.openal.org/downloads/oalinst.zip",
                FallbackInstallerArgs = "/s",
            },
        };
    }
}

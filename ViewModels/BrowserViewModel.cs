using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using FluentIcons.Common;
using KalOS.Services;

namespace KalOS.ViewModels
{
    public partial class BrowserViewModel : ObservableObject
    {
        // Shared HttpClient for direct-download fallbacks. A single instance is reused
        // to avoid socket exhaustion; it is configured with a realistic user agent so
        // CDNs (Mozilla, Brave, etc.) do not block the request.
        private static readonly System.Net.Http.HttpClient _downloadClient = new()
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        static BrowserViewModel()
        {
            _downloadClient.DefaultRequestHeaders.Add(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        [ObservableProperty]
        private bool _isScanning;

        [ObservableProperty]
        private string _scanStatusText = "Checking browsers and external software...";

        [ObservableProperty]
        private bool _isWingetAvailable;

        partial void OnIsWingetAvailableChanged(bool value)
        {
            foreach (var item in Browsers.Cast<InstallableItem>().Concat(Software.Cast<InstallableItem>()).Concat(Runtimes.Cast<InstallableItem>()))
            {
                item.IsWingetAvailable = value;
            }
        }

        [ObservableProperty]
        private bool _isRepairingWinget;

        public bool HasScanned { get; private set; }

        public ObservableCollection<BrowserItem> Browsers { get; }
        public ObservableCollection<SoftwareItem> Software { get; }
        public ObservableCollection<RuntimeItem> Runtimes { get; }

        public BrowserViewModel()
        {
            Browsers = new ObservableCollection<BrowserItem>
            {
                new BrowserItem
                {
                    Name = "Brave",
                    Description = "Chromium-based browser with ad and tracker blocking built in at the network level, so pages load faster and you're protected without installing extensions. Also includes HTTPS-Everywhere-style upgrading, fingerprinting resistance, and an optional built-in Tor tab for extra anonymity.",
                    WingetId = "Brave.Brave",
                    ChocolateyId = "brave",
                    ScoopName = "brave",
                    IconSymbol = Symbol.ShieldKeyhole,
                    IconPath = "ms-appx:///Assets/icons8-brave-web-browser-48.png",
                    IsChromium = true,
                    DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser"),
                    Extensions = GetDefaultExtensions(),
                    FallbackDownloadUrl = "https://brave.com/latest/BraveBrowserWin64.msi",
                    FallbackInstallerArgs = "/quiet /qn /norestart",
                    InstallerType = FallbackInstallerType.Msi
                },
                new BrowserItem
                {
                    Name = "Thorium",
                    Description = "A Chromium fork rebuilt from source with aggressive compiler optimizations (AVX2/AVX-512, LTO, PGO) rather than feature changes — same Chrome experience and extension support, just noticeably faster page loads and video decoding on modern CPUs.",
                    WingetId = "Alex313031.Thorium.AVX2",
                    IconSymbol = Symbol.Flash,
                    IconPath = "ms-appx:///Assets/thorium.png",
                    IsChromium = true,
                    DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Thorium"),
                    Extensions = GetDefaultExtensions(),
                    FallbackDownloadUrl = "https://github.com/Alex313031/Thorium-Win/releases/latest/download/thorium_AVX2_mini_installer.exe",
                    FallbackInstallerArgs = "/silent /install"
                },
                new BrowserItem
                {
                    Name = "LibreWolf",
                    Description = "Firefox with the telemetry, sponsored content, and Mozilla data collection stripped out, plus hardening patches pulled from the Tor Browser project. You get standard Firefox extension support and UI, just locked down by default instead of needing to configure privacy settings yourself.",
                    WingetId = "LibreWolf.LibreWolf",
                    ChocolateyId = "librewolf",
                    ScoopName = "librewolf",
                    IconSymbol = Symbol.AnimalDog,
                    IconPath = "ms-appx:///Assets/librewolf.png",
                    IsChromium = false,
                    DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"LibreWolf"),
                    Extensions = GetDefaultExtensions(),
                    FallbackDownloadUrl = "https://dl.librewolf.net/librewolf/150.0.1-1/librewolf-150.0.1-1-windows-x86_64-setup.exe",
                    FallbackInstallerArgs = "/S"
                },
                new BrowserItem
                {
                    Name = "Zen Browser",
                    Description = "A Firefox fork focused on interface and workflow rather than just privacy: split-screen tabs, workspaces for separating contexts (work/personal), and a minimal, highly themeable UI — while still inheriting Firefox's engine and privacy tooling underneath.",
                    WingetId = "Zen-Team.Zen-Browser",
                    IconSymbol = Symbol.LeafTwo,
                    IconPath = "ms-appx:///Assets/zen-browser-dark.png",
                    IsChromium = false,
                    DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Zen"),
                    Extensions = GetDefaultExtensions(),
                    FallbackDownloadUrl = "https://github.com/zen-browser/desktop/releases/latest/download/zen.installer.exe",
                    FallbackInstallerArgs = "/S"
                }
            };

            Software = new ObservableCollection<SoftwareItem>
            {
                new SoftwareItem { Name = "Discord", Description = "Voice and text chat for gamers.", WingetId = "Discord.Discord", ChocolateyId = "discord", ScoopName = "discord", IconSymbol = Symbol.ChatMultiple, IconPath = "ms-appx:///Assets/discord.png", FallbackDownloadUrl = "https://discord.com/api/download?platform=win", FallbackInstallerArgs = "/silent /install" },
                new SoftwareItem { Name = "Steam", Description = "Video game digital distribution service.", WingetId = "Valve.Steam", ChocolateyId = "steam", ScoopName = "steam", IconSymbol = Symbol.Games, IconPath = "ms-appx:///Assets/steam.png", FallbackDownloadUrl = "https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe", FallbackInstallerArgs = "/S" },
                new SoftwareItem { Name = "7-Zip", Description = "File archiver with a high compression ratio.", WingetId = "7zip.7zip", ChocolateyId = "7zip", ScoopName = "7zip", IconSymbol = Symbol.FolderZip, IconPath = "ms-appx:///Assets/7zip.png", FallbackDownloadUrl = "https://www.7-zip.org/a/7z2409-x64.exe", FallbackInstallerArgs = "/S" },
                new SoftwareItem { Name = "Spotify", Description = "Digital music service that gives you access to millions of songs.", WingetId = "Spotify.Spotify", ChocolateyId = "spotify", ScoopName = "spotify", IconSymbol = Symbol.MusicNote2Play, IconPath = "ms-appx:///Assets/spotify.png", FallbackDownloadUrl = "https://download.scdn.co/SpotifySetup.exe", FallbackInstallerArgs = "/silent" }
            };

            Runtimes = new ObservableCollection<RuntimeItem>
            {
                new RuntimeItem { Name = "Visual C++ 2005 (x86)", Description = "Microsoft Visual C++ 2005 Redistributable (x86). Required by older games and legacy software.", WingetId = "Microsoft.VCRedist.2005.x86", IconSymbol = Symbol.CodeBlock, FallbackDownloadUrl = "https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x86.EXE", FallbackInstallerArgs = "/q" },
                new RuntimeItem { Name = "Visual C++ 2005 (x64)", Description = "Microsoft Visual C++ 2005 Redistributable (x64). Required by older games and legacy software.", WingetId = "Microsoft.VCRedist.2005.x64", IconSymbol = Symbol.CodeBlock, FallbackDownloadUrl = "https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x64.EXE", FallbackInstallerArgs = "/q" },
                new RuntimeItem { Name = "Visual C++ 2008 (x86)", Description = "Microsoft Visual C++ 2008 Redistributable (x86). Needed by many mid-2000s applications.", WingetId = "Microsoft.VCRedist.2008.x86", IconSymbol = Symbol.CodeBlock, FallbackDownloadUrl = "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x86.exe", FallbackInstallerArgs = "/q" },
                new RuntimeItem { Name = "Visual C++ 2008 (x64)", Description = "Microsoft Visual C++ 2008 Redistributable (x64). Needed by many mid-2000s applications.", WingetId = "Microsoft.VCRedist.2008.x64", IconSymbol = Symbol.CodeBlock, FallbackDownloadUrl = "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x64.exe", FallbackInstallerArgs = "/q" },
                new RuntimeItem { Name = "Visual C++ 2010 (x86)", Description = "Microsoft Visual C++ 2010 Redistributable (x86). Common dependency for games and apps from ~2010-2015.", WingetId = "Microsoft.VCRedist.2010.x86", IconSymbol = Symbol.CodeBlock, FallbackDownloadUrl = "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x86.exe", FallbackInstallerArgs = "/q" },
                new RuntimeItem { Name = "Visual C++ 2010 (x64)", Description = "Microsoft Visual C++ 2010 Redistributable (x64). Common dependency for games and apps from ~2010-2015.", WingetId = "Microsoft.VCRedist.2010.x64", IconSymbol = Symbol.CodeBlock, FallbackDownloadUrl = "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe", FallbackInstallerArgs = "/q" },
                new RuntimeItem { Name = "Visual C++ 2012 (x86)", Description = "Microsoft Visual C++ 2012 Redistributable (x86). Required by apps targeting VS 2012.", WingetId = "Microsoft.VCRedist.2012.x86", IconSymbol = Symbol.CodeBlock, FallbackDownloadUrl = "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x86.exe", FallbackInstallerArgs = "/quiet /norestart" },
                new RuntimeItem { Name = "Visual C++ 2012 (x64)", Description = "Microsoft Visual C++ 2012 Redistributable (x64). Required by apps targeting VS 2012.", WingetId = "Microsoft.VCRedist.2012.x64", IconSymbol = Symbol.CodeBlock, FallbackDownloadUrl = "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe", FallbackInstallerArgs = "/quiet /norestart" },
                new RuntimeItem { Name = "Visual C++ 2013 (x86)", Description = "Microsoft Visual C++ 2013 Redistributable (x86). Used by many modern games and applications.", WingetId = "Microsoft.VCRedist.2013.x86", IconSymbol = Symbol.CodeBlock, FallbackDownloadUrl = "https://aka.ms/highdpimfc2013x86enu", FallbackInstallerArgs = "/quiet /norestart" },
                new RuntimeItem { Name = "Visual C++ 2013 (x64)", Description = "Microsoft Visual C++ 2013 Redistributable (x64). Used by many modern games and applications.", WingetId = "Microsoft.VCRedist.2013.x64", IconSymbol = Symbol.CodeBlock, FallbackDownloadUrl = "https://aka.ms/highdpimfc2013x64enu", FallbackInstallerArgs = "/quiet /norestart" },
                new RuntimeItem { Name = "Visual C++ 2015-2022 (x86)", Description = "Microsoft Visual C++ 2015-2022 Redistributable (x86). The latest VC++ runtime — covers 2015, 2017, 2019 and 2022.", WingetId = "Microsoft.VCRedist.2015+.x86", IconSymbol = Symbol.CodeBlock, FallbackDownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x86.exe", FallbackInstallerArgs = "/quiet /norestart" },
                new RuntimeItem { Name = "Visual C++ 2015-2022 (x64)", Description = "Microsoft Visual C++ 2015-2022 Redistributable (x64). The latest VC++ runtime — covers 2015, 2017, 2019 and 2022.", WingetId = "Microsoft.VCRedist.2015+.x64", IconSymbol = Symbol.CodeBlock, FallbackDownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe", FallbackInstallerArgs = "/quiet /norestart" },
                new RuntimeItem { Name = ".NET 6.0 Desktop Runtime", Description = "Microsoft .NET 6.0 Desktop Runtime. Required by apps built with .NET 6.", WingetId = "Microsoft.DotNet.DesktopRuntime.6", IconSymbol = Symbol.WindowWrench, FallbackDownloadUrl = "https://aka.ms/dotnet/6.0/windowsdesktop-runtime-win-x64.exe", FallbackInstallerArgs = "/quiet /norestart" },
                new RuntimeItem { Name = ".NET 7.0 Desktop Runtime", Description = "Microsoft .NET 7.0 Desktop Runtime. Required by apps built with .NET 7.", WingetId = "Microsoft.DotNet.DesktopRuntime.7", IconSymbol = Symbol.WindowWrench, FallbackDownloadUrl = "https://aka.ms/dotnet/7.0/windowsdesktop-runtime-win-x64.exe", FallbackInstallerArgs = "/quiet /norestart" },
                new RuntimeItem { Name = ".NET 8.0 Desktop Runtime", Description = "Microsoft .NET 8.0 Desktop Runtime. Required by apps built with .NET 8.", WingetId = "Microsoft.DotNet.DesktopRuntime.8", IconSymbol = Symbol.WindowWrench, FallbackDownloadUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe", FallbackInstallerArgs = "/quiet /norestart" },
                new RuntimeItem { Name = ".NET 9.0 Desktop Runtime", Description = "Microsoft .NET 9.0 Desktop Runtime. Required by apps built with .NET 9.", WingetId = "Microsoft.DotNet.DesktopRuntime.9", IconSymbol = Symbol.WindowWrench, FallbackDownloadUrl = "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe", FallbackInstallerArgs = "/quiet /norestart" },
                new RuntimeItem { Name = "DirectX End-User Runtime", Description = "Microsoft DirectX End-User Runtime. Installs D3DX9/10/11, XAudio, XInput and other legacy DirectX components needed by older games.", WingetId = "Microsoft.DirectX", IconSymbol = Symbol.XboxController, FallbackDownloadUrl = "https://download.microsoft.com/download/1/7/1/1718CCC4-6315-4D8E-9543-8E28A4E18C4C/dxwebsetup.exe", FallbackInstallerArgs = "/q" },
                new RuntimeItem { Name = "XNA Framework 4.0", Description = "Microsoft XNA Framework Redistributable 4.0. Required by indie games built with XNA/MonoGame.", WingetId = "Microsoft.XNARedist", IconSymbol = Symbol.XboxController, FallbackDownloadUrl = "https://download.microsoft.com/download/A/C/2/AC2C903B-E6E8-42C2-9FD7-BEBAC362A930/xnafx40_redist.msi", FallbackInstallerArgs = "/quiet /norestart", InstallerType = FallbackInstallerType.Msi },
                new RuntimeItem { Name = "Java Runtime (JRE)", Description = "Oracle Java Runtime Environment. Required by Minecraft (Java Edition), Eclipse, and many enterprise apps.", WingetId = "Oracle.JavaRuntimeEnvironment", IconSymbol = Symbol.Braces, FallbackDownloadUrl = "https://javadl.oracle.com/webapps/download/AutoDL?BundleId=251406_d79360ef13234098800e0d23e7e2cbb8", FallbackInstallerArgs = "/s" },
                new RuntimeItem { Name = "OpenAL", Description = "OpenAL audio library. Required by some games (older Unreal Engine titles, Minecraft mods, etc.).", WingetId = "OpenAL.OpenAL", IconSymbol = Symbol.Speaker2, FallbackDownloadUrl = "https://www.openal.org/downloads/oalinst.zip", FallbackInstallerArgs = "/s" },
            };
        }

        public async Task ScanForInstalledBrowsersAsync()
        {
            if (HasScanned) return;

            var dispatcher = ((App)App.Current).MainWindow?.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null) return;

            dispatcher.TryEnqueue(() =>
            {
                IsScanning = true;
                ScanStatusText = "Checking Windows Package Manager availability...";
            });

            bool wingetAvailable = await WingetHelper.IsAvailableAsync();
            if (!wingetAvailable)
            {
                dispatcher.TryEnqueue(() => ScanStatusText = "Windows Package Manager not found. Attempting quick repair...");
                dispatcher.TryEnqueue(() => IsRepairingWinget = true);
                try
                {
                    using var repairCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var logService = App.Services.GetService<LogService>();
                    wingetAvailable = await WingetHelper.TryRepairAsync(cancellationToken: repairCts.Token, logService: logService);
                }
                catch (OperationCanceledException)
                {
                    dispatcher.TryEnqueue(() => ScanStatusText = "Winget repair timed out. Using direct download fallback.");
                }
                catch (Exception)
                {
                    dispatcher.TryEnqueue(() => ScanStatusText = "Winget repair failed. Using direct download fallback.");
                }
                dispatcher.TryEnqueue(() => IsRepairingWinget = false);
            }

            dispatcher.TryEnqueue(() => IsWingetAvailable = wingetAvailable);

            // Detect the other package managers (Chocolatey / Scoop) so installs can
            // chain winget -> Chocolatey -> Scoop -> direct download.
            var packageManager = App.Services.GetRequiredService<PackageManagerService>();
            var availability = await packageManager.DetectAsync();

            foreach (var item in Browsers.Cast<InstallableItem>().Concat(Software.Cast<InstallableItem>()).Concat(Runtimes.Cast<InstallableItem>()))
            {
                item.IsPackageManagerAvailable = availability.Any;
            }

            dispatcher.TryEnqueue(() =>
            {
                if (!availability.Any)
                {
                    ScanStatusText = "No package manager available (winget/choco/scoop). Use direct download buttons below.";
                }
                else if (!wingetAvailable)
                {
                    ScanStatusText = "winget unavailable — will fall back to Chocolatey or Scoop.";
                }
                else
                {
                    ScanStatusText = "Checking browsers and external software...";
                }
            });

            try
            {
                foreach (var item in Browsers.Cast<InstallableItem>().Concat(Software.Cast<InstallableItem>()).Concat(Runtimes.Cast<InstallableItem>()))
                {
                    bool isInstalled = await Task.Run(() => IsItemInstalled(item));

                    if (isInstalled)
                    {
                        if (dispatcher != null)
                        {
                            dispatcher.TryEnqueue(() =>
                            {
                                item.IsInstalled = true;
                                item.ShowSuccessNotice = true;
                                item.StatusText = "Already installed";

                                if (item is BrowserItem browser)
                                {
                                    foreach (var ext in browser.Extensions)
                                    {
                                        bool extInstalled = CheckIfExtensionIsInstalled(browser, ext);
                                        if (extInstalled)
                                        {
                                            ext.IsSelected = true;
                                        }
                                    }
                                }
                            });
                        }
                    }
                }
            }
            finally
            {
                dispatcher?.TryEnqueue(() =>
                {
                    IsScanning = false;
                    HasScanned = true;
                });
            }
        }

        private static bool IsItemInstalled(InstallableItem item)
        {
            switch (item.WingetId)
            {
                case "Brave.Brave":
                    return Directory.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"BraveSoftware\Brave-Browser\Application"));
                case "Alex313031.Thorium.AVX2":
                    return Directory.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Thorium\User Data"));
                case "LibreWolf.LibreWolf":
                    return Directory.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        @"LibreWolf\Profiles"));
                case "Zen-Team.Zen-Browser":
                    return Directory.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        @"Zen\Profiles")) ||
                            Directory.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Zen\User Data"));
                case "Discord.Discord":
                    return Directory.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Discord")) ||
                            File.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Discord\Update.exe"));
                case "Valve.Steam":
                    return File.Exists(@"C:\Program Files (x86)\Steam\steam.exe") ||
                            File.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        @"Steam\steam.exe"));
                case "7zip.7zip":
                    return File.Exists(@"C:\Program Files\7-Zip\7z.exe") ||
                            File.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        @"7-Zip\7z.exe"));
                case "Spotify.Spotify":
                    return File.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        @"Spotify\Spotify.exe")) ||
                            File.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Spotify\Spotify.exe"));
                // Visual C++ runtimes — check uninstall registry + SxSWinSxS
                case "Microsoft.VCRedist.2005.x86":
                    return IsVcRedistInstalled("2005", "x86");
                case "Microsoft.VCRedist.2005.x64":
                    return IsVcRedistInstalled("2005", "x64");
                case "Microsoft.VCRedist.2008.x86":
                    return IsVcRedistInstalled("2008", "x86");
                case "Microsoft.VCRedist.2008.x64":
                    return IsVcRedistInstalled("2008", "x64");
                case "Microsoft.VCRedist.2010.x86":
                    return IsVcRedistInstalled("2010", "x86");
                case "Microsoft.VCRedist.2010.x64":
                    return IsVcRedistInstalled("2010", "x64");
                case "Microsoft.VCRedist.2012.x86":
                    return IsVcRedistInstalled("2012", "x86");
                case "Microsoft.VCRedist.2012.x64":
                    return IsVcRedistInstalled("2012", "x64");
                case "Microsoft.VCRedist.2013.x86":
                    return IsVcRedistInstalled("2013", "x86");
                case "Microsoft.VCRedist.2013.x64":
                    return IsVcRedistInstalled("2013", "x64");
                case "Microsoft.VCRedist.2015+.x86":
                    return IsVcRedistInstalled("2015+", "x86");
                case "Microsoft.VCRedist.2015+.x64":
                    return IsVcRedistInstalled("2015+", "x64");
                case "Microsoft.DotNet.DesktopRuntime.6":
                    return IsDotNetRuntimeInstalled("6.");
                case "Microsoft.DotNet.DesktopRuntime.7":
                    return IsDotNetRuntimeInstalled("7.");
                case "Microsoft.DotNet.DesktopRuntime.8":
                    return IsDotNetRuntimeInstalled("8.");
                case "Microsoft.DotNet.DesktopRuntime.9":
                    return IsDotNetRuntimeInstalled("9.");
                case "Microsoft.DirectX":
                    return IsDirectXInstalled();
                case "Microsoft.XNARedist":
                    return IsXnaInstalled();
                case "Oracle.JavaRuntimeEnvironment":
                    return IsJavaInstalled();
                case "OpenAL.OpenAL":
                    return IsOpenALInstalled();
                default:
                    return false;
            }
        }

        private static bool IsVcRedistInstalled(string version, string arch)
        {
            try
            {
                // Check uninstall registry for DisplayName containing "Visual C++ 2005/2008 etc."
                string[] hives = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
                string search = version == "2015+" ? "2015" : version;
                foreach (var hive in hives)
                {
                    using var baseKey = Registry.LocalMachine.OpenSubKey(hive);
                    if (baseKey == null) continue;
                    foreach (var sub in baseKey.GetSubKeyNames())
                    {
                        using var k = baseKey.OpenSubKey(sub);
                        var name = k?.GetValue("DisplayName") as string;
                        if (name == null) continue;
                        if (name.IndexOf($"Visual C++ {search}", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (search == "2015" && name.IndexOf("Visual C++ 2015", StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (search == "2015" && name.IndexOf("Visual C++ 2017", StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (search == "2015" && name.IndexOf("Visual C++ 2019", StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (search == "2015" && name.IndexOf("Visual C++ 2022", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            // For x86/x64, check DisplayName contains arch or check RegistryView
                            if (arch == "x86" && name.IndexOf("x86", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                            if (arch == "x64" && name.IndexOf("x64", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                            if (name.IndexOf("Redistributable", StringComparison.OrdinalIgnoreCase) >= 0 && arch == "x64" && (hive.Contains("WOW6432") == false)) return true;
                        }
                    }
                }
                // Fallback file check for 2015+ (vcruntime140)
                if (version == "2015+")
                {
                    string sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vcruntime140.dll");
                    string wow64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "vcruntime140.dll");
                    if (arch == "x86") return File.Exists(wow64) || File.Exists(sys32);
                    return File.Exists(sys32);
                }
            }
            catch { }
            return false;
        }

        private static bool IsDotNetRuntimeInstalled(string majorPrefix)
        {
            try
            {
                // Check dotnet shared folder
                var dotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(dotnetRoot))
                {
                    foreach (var dir in Directory.GetDirectories(dotnetRoot))
                    {
                        var name = Path.GetFileName(dir);
                        if (name.StartsWith(majorPrefix, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                var x86Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(x86Root))
                {
                    foreach (var dir in Directory.GetDirectories(x86Root))
                    {
                        var name = Path.GetFileName(dir);
                        if (name.StartsWith(majorPrefix, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                // Check runtime folder
                var runtimeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.NETCore.App");
                if (Directory.Exists(runtimeRoot))
                {
                    foreach (var dir in Directory.GetDirectories(runtimeRoot))
                    {
                        var name = Path.GetFileName(dir);
                        if (name.StartsWith(majorPrefix, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool IsDirectXInstalled()
        {
            try
            {
                // DirectX 12 is inbox on Win10/11; check for legacy D3DX
                if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "d3d12.dll"))) return true;
                if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "d3dx9_43.dll"))) return true;
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX");
                var ver = key?.GetValue("Version") as string;
                if (!string.IsNullOrEmpty(ver) && ver.StartsWith("4.09", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        private static bool IsXnaInstalled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\XNA\Framework\v4.0");
                if (key != null) return true;
                using var wow = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\XNA\Framework\v4.0");
                if (wow != null) return true;
                if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft XNA\XNA Game Studio\v4.0\Redist\XNA Framework\xnafx40_redist.msi"))) return true;
            }
            catch { }
            return false;
        }

        private static bool IsJavaInstalled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\JavaSoft\Java Runtime Environment");
                if (key != null && key.GetSubKeyNames().Length > 0) return true;
                using var wow = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\JavaSoft\Java Runtime Environment");
                if (wow != null && wow.GetSubKeyNames().Length > 0) return true;
                // Check javaw
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (File.Exists(Path.Combine(pf, @"Java\jre1.8.0_361\bin\javaw.exe"))) return true;
                // Try where java
                var psi = new ProcessStartInfo("where", "java") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                try { using var p = Process.Start(psi); if (p != null) { p.WaitForExit(2000); return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(p.StandardOutput.ReadToEnd()); } } catch { }
            }
            catch { }
            return false;
        }

        private static bool IsOpenALInstalled()
        {
            try
            {
                if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenAL32.dll"))) return true;
                if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "OpenAL32.dll"))) return true;
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OpenAL");
                if (key != null) return true;
                using var wow = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\OpenAL");
                if (wow != null) return true;
            }
            catch { }
            return false;
        }

        private static bool CheckIfExtensionIsInstalled(BrowserItem browser, ExtensionItem ext)
        {
            try
            {
                if (browser.IsChromium)
                {
                    // 1. Check registry policy (forced installs)
                    string policyKeyPath = browser.Name switch
                    {
                        "Brave" => @"SOFTWARE\Policies\BraveSoftware\Brave\ExtensionInstallForcelist",
                        _ => @"SOFTWARE\Policies\Chromium\ExtensionInstallForcelist"
                    };

                    using (var key = Registry.LocalMachine.OpenSubKey(policyKeyPath))
                    {
                        if (key != null)
                        {
                            foreach (var valueName in key.GetValueNames())
                            {
                                string? val = key.GetValue(valueName) as string;
                                if (val != null && val.StartsWith(ext.ChromeId, StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }
                            }
                        }
                    }

                    // 2. Check user profile directory (manual installs)
                    string userDataPath = Path.Combine(browser.DataPath, "User Data");
                    if (Directory.Exists(userDataPath))
                    {
                        var profiles = Directory.GetDirectories(userDataPath)
                            .Where(d => Path.GetFileName(d).Equals("Default", StringComparison.OrdinalIgnoreCase) || 
                                        Path.GetFileName(d).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase));

                        foreach (var profile in profiles)
                        {
                            string extPath = Path.Combine(profile, "Extensions", ext.ChromeId);
                            if (Directory.Exists(extPath)) return true;
                        }
                    }
                }
                else
                {
                    // 1. Check registry policy (forced installs)
                    string policyKeyPath = browser.Name switch
                    {
                        "LibreWolf" => @"SOFTWARE\Policies\LibreWolf\ExtensionSettings",
                        "Zen Browser" => @"SOFTWARE\Policies\Zen\ExtensionSettings",
                        _ => @"SOFTWARE\Policies\Mozilla\Firefox\ExtensionSettings"
                    };
                    using (var key = Registry.LocalMachine.OpenSubKey(policyKeyPath))
                    {
                        if (key != null)
                        {
                            var subKeyNames = key.GetSubKeyNames();
                            foreach (var name in subKeyNames)
                            {
                                if (name.Equals(ext.FirefoxId, StringComparison.OrdinalIgnoreCase)) return true;
                            }
                        }
                    }

                    // 1b. Check policies.json (forced installs)
                    string installDir = GetFirefoxEngineInstallDir(browser.Name);
                    if (!string.IsNullOrEmpty(installDir))
                    {
                        string policyFile = Path.Combine(installDir, "distribution", "policies.json");
                        if (File.Exists(policyFile))
                        {
                            string json = File.ReadAllText(policyFile);
                            if (json.Contains(ext.FirefoxId)) return true;
                        }
                    }

                    // 2. Check user profile directory (manual installs)
                    string profilesPath = Path.Combine(browser.DataPath, "Profiles");
                    if (browser.Name == "Zen Browser")
                    {
                        // Zen can sometimes store directly in DataPath or under Profiles
                        profilesPath = Directory.Exists(Path.Combine(browser.DataPath, "Profiles")) 
                            ? Path.Combine(browser.DataPath, "Profiles") 
                            : browser.DataPath;
                    }

                    if (Directory.Exists(profilesPath))
                    {
                        var profiles = Directory.GetDirectories(profilesPath);
                        foreach (var profile in profiles)
                        {
                            string extDir = Path.Combine(profile, "extensions");
                            if (Directory.Exists(extDir))
                            {
                                if (File.Exists(Path.Combine(extDir, ext.FirefoxId + ".xpi")) || 
                                    Directory.Exists(Path.Combine(extDir, ext.FirefoxId)))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Best-effort
            }
            return false;
        }

        private static ObservableCollection<ExtensionItem> GetDefaultExtensions()
        {
            return new ObservableCollection<ExtensionItem>
            {
                new ExtensionItem { Name = "uBlock Origin", ChromeId = "cjpalhdlnbpafiamejdnhcphjbkeiagm", FirefoxId = "uBlock0@raymondhill.net", FirefoxUrl = "https://addons.mozilla.org/firefox/downloads/latest/ublock-origin/latest.xpi" },
                new ExtensionItem { Name = "Privacy Badger", ChromeId = "pkehgijcmpdhfbdbbnkijodmdjhbjlgp", FirefoxId = "jid1-MnnxcxisBPnSXQ@jetpack", FirefoxUrl = "https://addons.mozilla.org/firefox/downloads/latest/privacy-badger17/latest.xpi" },
                new ExtensionItem { Name = "I still don't care about cookies", ChromeId = "edibdbjcniadpccecjdfdjjppcpchdlm", FirefoxId = "idcac-pub@guus.ninja", FirefoxUrl = "https://addons.mozilla.org/firefox/downloads/latest/istilldontcareaboutcookies/latest.xpi" },
                new ExtensionItem { Name = "SponsorBlock", ChromeId = "mnjggcdmjocbbbhaepdhchncahnbgone", FirefoxId = "sponsorBlocker@ajay.app", FirefoxUrl = "https://addons.mozilla.org/firefox/downloads/latest/sponsorblock/latest.xpi" }
            };
        }

        [RelayCommand]
        private async Task RepairWingetAsync()
        {
            if (IsRepairingWinget) return;

            var dispatcher = ((App)App.Current).MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
            dispatcher?.TryEnqueue(() =>
            {
                IsRepairingWinget = true;
                ScanStatusText = "Downloading Windows Package Manager installer...";
            });

            try
            {
                var logService = App.Services.GetService<LogService>();
                logService?.WriteAsync("Winget", "Repair", "Manual repair started", isError: false);

                using var repairCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                bool available = await WingetHelper.TryRepairAsync(cancellationToken: repairCts.Token, logService: logService);

                var packageManager = App.Services.GetRequiredService<PackageManagerService>();
                packageManager.InvalidateCache();
                var availability = await packageManager.DetectAsync();
                foreach (var item in Browsers.Cast<InstallableItem>().Concat(Software.Cast<InstallableItem>()))
                {
                    item.IsPackageManagerAvailable = availability.Any;
                }

                dispatcher?.TryEnqueue(() =>
                {
                    IsWingetAvailable = available;
                    ScanStatusText = available
                        ? "Windows Package Manager is now available."
                        : "Package managers still unavailable. Use the direct download buttons instead.";
                });
                logService?.WriteAsync("Winget", "Repair", available ? "Manual repair succeeded" : "Manual repair failed", isError: !available);
            }
            catch (OperationCanceledException)
            {
                dispatcher?.TryEnqueue(() =>
                {
                    ScanStatusText = "Repair timed out. Use the direct download buttons instead.";
                });
            }
            catch (Exception ex)
            {
                var logService = App.Services.GetService<LogService>();
                logService?.WriteAsync("Winget", "Repair", $"Repair error: {ex.Message}", isError: true);
                dispatcher?.TryEnqueue(() =>
                {
                    ScanStatusText = $"Repair failed: {ex.Message}. Use the direct download buttons instead.";
                });
            }
            finally
            {
                dispatcher?.TryEnqueue(() => IsRepairingWinget = false);
            }
        }

        /// <summary>
        /// Bypasses winget entirely and installs the item from its direct-download URL.
        /// </summary>
        [RelayCommand]
        private async Task DirectInstallAsync(InstallableItem? item)
        {
            if (item == null || item.IsInstalling) return;
            if (string.IsNullOrEmpty(item.FallbackDownloadUrl))
            {
                item.StatusText = $"No direct download URL is configured for {item.Name}.";
                item.IsError = true;
                return;
            }

            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var logService = App.Services.GetService<LogService>();

            item.IsInstalling = true;
            item.IsError = false;
            item.IsInstalled = false;
            item.ShowSuccessNotice = false;
            item.ShowProgress = true;
            item.ProgressValue = 0;
            item.StatusText = $"Downloading {item.Name} directly...";

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                using var installCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, item.BeginOperation());
                await Task.Run(async () =>
                {
                    await InstallFromDirectDownloadAsync(item, dispatcher, installCts.Token);

                    if (item is BrowserItem browserExt)
                    {
                        dispatcher.TryEnqueue(() => item.ProgressValue = 80);
                        dispatcher.TryEnqueue(() => item.StatusText = $"Applying extensions for {item.Name}...");
                        ApplyExtensions(browserExt);
                    }

                    dispatcher.TryEnqueue(() => item.ProgressValue = 100);
                });

                item.StatusText = $"{item.Name} installed successfully!";
                item.IsInstalled = true;
                item.ShowSuccessNotice = true;
                logService?.WriteAsync("Install", item.Name, "Direct install succeeded", isError: false);
            }
            catch (OperationCanceledException)
            {
                item.StatusText = $"{item.Name} install canceled.";
                logService?.WriteAsync("Install", item.Name, "Install canceled by user", isError: false);
            }
            catch (Exception ex)
            {
                item.IsError = true;
                item.StatusText = $"Failed to install {item.Name}: {ex.Message}";
                logService?.WriteAsync("Install", item.Name, ex.Message, isError: true);
            }
            finally
            {
                item.IsInstalling = false;
                item.ShowProgress = false;
                item.ProgressValue = 0;
            }
        }

        [RelayCommand]
        private async Task InstallItemAsync(InstallableItem? item)
        {
            if (item == null || item.IsInstalling) return;

            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var logService = App.Services.GetService<LogService>();

            // If winget is unavailable, only proceed if a direct-download fallback exists.
            if (!IsWingetAvailable && string.IsNullOrEmpty(item.FallbackDownloadUrl))
            {
                item.StatusText = "Windows Package Manager is unavailable and no direct download fallback is configured.";
                item.IsError = true;
                logService?.WriteAsync("Install", item.Name, item.StatusText, isError: true);
                return;
            }

            bool isReinstall = item.IsInstalled;

            item.IsInstalling = true;
            item.IsError = false;
            item.IsInstalled = false;
            item.ShowSuccessNotice = false;
            item.ShowProgress = true;
            item.ProgressValue = 0;

            item.StatusText = isReinstall ? $"Reinstalling {item.Name}..." : $"Installing {item.Name}...";

            var packageManager = App.Services.GetRequiredService<PackageManagerService>();

            try
            {
                await Task.Run(async () =>
                {
                    // Step 1: Uninstall if reinstalling
                    if (isReinstall)
                    {
                        dispatcher.TryEnqueue(() => item.StatusText = $"Closing {item.Name}...");
                        dispatcher.TryEnqueue(() => item.ProgressValue = 3);
                        KillBrowserProcess(item.Name);

                        dispatcher.TryEnqueue(() => item.StatusText = $"Uninstalling {item.Name}...");
                        dispatcher.TryEnqueue(() => item.ProgressValue = 5);
                        using var uninstallTimeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                        using var uninstallCts = CancellationTokenSource.CreateLinkedTokenSource(uninstallTimeoutCts.Token, item.BeginOperation());
                        await packageManager.UninstallAsync(
                            item.WingetId,
                            item.ChocolateyId,
                            item.ScoopName,
                            text => dispatcher.TryEnqueue(() => item.StatusText = text),
                            uninstallCts.Token);

                        if (item is BrowserItem browser)
                        {
                            dispatcher.TryEnqueue(() => item.StatusText = $"Deleting {item.Name} data...");
                            dispatcher.TryEnqueue(() => item.ProgressValue = 15);
                            DeleteBrowserData(browser);
                            ClearExtensionPolicies(browser);
                        }
                    }

                    // Step 2: Install — chain winget -> Chocolatey -> Scoop -> direct download.
                    dispatcher.TryEnqueue(() => item.StatusText = $"Downloading {item.Name}...");
                    dispatcher.TryEnqueue(() => item.ProgressValue = isReinstall ? 25 : 10);

                    using var installTimeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                    using var installCts = CancellationTokenSource.CreateLinkedTokenSource(installTimeoutCts.Token, item.BeginOperation());
                    var result = await packageManager.InstallAsync(
                        item.WingetId,
                        item.ChocolateyId,
                        item.ScoopName,
                        status: text => dispatcher.TryEnqueue(() => item.StatusText = text),
                        installCts.Token);

                    if (!result.Success)
                    {
                        logService?.WriteAsync("Install", item.Name, $"Package managers failed: {result.Detail}", isError: true);

                        if (!string.IsNullOrEmpty(item.FallbackDownloadUrl))
                        {
                            dispatcher.TryEnqueue(() => item.StatusText = $"{result.Detail} Downloading {item.Name} directly...");
                            await InstallFromDirectDownloadAsync(item, dispatcher, installCts.Token);
                        }
                        else
                        {
                            throw new Exception($"All package managers failed ({result.Detail}) and no direct download fallback is available.");
                        }
                    }
                    else
                    {
                        logService?.WriteAsync("Install", item.Name, $"Installed via {result.Manager}", isError: false);
                    }

                    if (item is BrowserItem browserExt)
                    {
                        dispatcher.TryEnqueue(() => item.ProgressValue = isReinstall ? 80 : 60);
                        dispatcher.TryEnqueue(() => item.StatusText = $"Applying extensions for {item.Name}...");
                        ApplyExtensions(browserExt);
                    }

                    dispatcher.TryEnqueue(() => item.ProgressValue = 100);
                });

                item.StatusText = isReinstall
                    ? $"{item.Name} reinstalled successfully!"
                    : $"{item.Name} installed successfully!";
                item.IsInstalled = true;
                item.ShowSuccessNotice = true;
            }
            catch (OperationCanceledException)
            {
                item.StatusText = isReinstall
                    ? $"{item.Name} reinstall canceled."
                    : $"{item.Name} install canceled.";
            }
            catch (Exception ex)
            {
                item.IsError = true;
                item.StatusText = $"Failed to install {item.Name}: {ex.Message}";
            }
            finally
            {
                item.IsInstalling = false;
                item.ShowProgress = false;
                item.ProgressValue = 0;
            }
        }

        [RelayCommand]
        private async Task UninstallItemAsync(InstallableItem? item)
        {
            if (item == null || item.IsInstalling) return;

            var dispatcher = DispatcherQueue.GetForCurrentThread();

            item.IsInstalling = true;
            item.IsError = false;
            item.ShowSuccessNotice = false;
            item.ShowProgress = true;
            item.ProgressValue = 0;

            try
            {
                await Task.Run(async () =>
                {
                    dispatcher.TryEnqueue(() => item.StatusText = $"Closing {item.Name}...");
                    dispatcher.TryEnqueue(() => item.ProgressValue = 3);
                    KillBrowserProcess(item.Name);

                    dispatcher.TryEnqueue(() => item.StatusText = $"Uninstalling {item.Name}...");
                    dispatcher.TryEnqueue(() => item.ProgressValue = 5);
                    using var uninstallTimeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    using var uninstallCts = CancellationTokenSource.CreateLinkedTokenSource(uninstallTimeoutCts.Token, item.BeginOperation());
                    var packageManager = App.Services.GetRequiredService<PackageManagerService>();
                    await packageManager.UninstallAsync(
                        item.WingetId,
                        item.ChocolateyId,
                        item.ScoopName,
                        text => dispatcher.TryEnqueue(() => item.StatusText = text),
                        uninstallCts.Token);

                    if (item is BrowserItem browser)
                    {
                        dispatcher.TryEnqueue(() => item.StatusText = $"Erasing {item.Name} data and folders...");
                        dispatcher.TryEnqueue(() => item.ProgressValue = 50);
                        DeleteBrowserData(browser);
                        ClearExtensionPolicies(browser);
                    }

                    dispatcher.TryEnqueue(() => item.ProgressValue = 100);
                });

                item.StatusText = $"{item.Name} uninstalled and data erased successfully!";
                item.IsInstalled = false;
            }
            catch (OperationCanceledException)
            {
                item.StatusText = $"{item.Name} uninstall canceled.";
            }
            catch (Exception ex)
            {
                item.IsError = true;
                item.StatusText = $"Failed to uninstall {item.Name}: {ex.Message}";
            }
            finally
            {
                item.IsInstalling = false;
                item.ShowProgress = false;
                item.ProgressValue = 0;
            }
        }

        [RelayCommand]
        private void CancelInstall(InstallableItem? item)
        {
            if (item == null || !item.IsInstalling) return;
            item.CancelOperation();
            item.StatusText = $"Canceling {item.Name}...";
        }

        private static void KillBrowserProcess(string browserName)
        {
            string[] processNames = browserName switch
            {
                "Brave" => new[] { "brave", "BraveBrowser" },
                "Thorium" => new[] { "thorium", "Thorium" },
                "LibreWolf" => new[] { "librewolf" },
                "Zen Browser" => new[] { "zen", "zen-alpha", "zen-beta" },
                _ => Array.Empty<string>()
            };

            foreach (string name in processNames)
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try 
                    { 
                        proc.Kill(); 
                        proc.WaitForExit(3000);
                    } 
                    catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// Downloads the installer from the item's FallbackDownloadUrl and runs it silently.
        /// Streams the file to disk to support large installers, validates the download,
        /// and passes cancellation through to the network and process operations.
        /// Supports both EXE and MSI installers.
        /// </summary>
        private static async Task InstallFromDirectDownloadAsync(InstallableItem item, DispatcherQueue dispatcher, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(item.FallbackDownloadUrl))
                throw new InvalidOperationException("No fallback download URL configured.");

            string extension;
            var urlLower = item.FallbackDownloadUrl.ToLowerInvariant();
            if (urlLower.EndsWith(".appx") || urlLower.EndsWith(".msix") || urlLower.EndsWith(".appxbundle") || urlLower.EndsWith(".msixbundle"))
            {
                extension = System.IO.Path.GetExtension(item.FallbackDownloadUrl);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".appx";
                // Strip query string if present in extension
                var qIdx = extension.IndexOf('?');
                if (qIdx >= 0) extension = extension[..qIdx];
            }
            else
            {
                extension = item.InstallerType == FallbackInstallerType.Msi ? ".msi" : ".exe";
            }
            string installerPath = Path.Combine(Path.GetTempPath(), $"{item.Name.Replace(" ", "_")}_installer{extension}");
            var logService = App.Services.GetService<LogService>();

            try
            {
                logService?.WriteAsync("Install", item.Name, $"Starting direct download from {item.FallbackDownloadUrl}", isError: false);

                // Stream the installer to disk instead of buffering the whole file in memory.
                using (var response = await _downloadClient.GetAsync(item.FallbackDownloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    using var netStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var fs = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await netStream.CopyToAsync(fs, cancellationToken);
                }

                // Basic validation: reject tiny files that are likely HTML error pages or stubs.
                var fileInfo = new FileInfo(installerPath);
                if (fileInfo.Length < 100_000)
                {
                    throw new InvalidOperationException(
                        $"Downloaded installer for {item.Name} is only {fileInfo.Length} bytes and is likely invalid.");
                }

                logService?.WriteAsync("Install", item.Name, $"Downloaded {fileInfo.Length} bytes; running installer", isError: false);

                ProcessStartInfo psi;
                if (extension.Equals(".appx", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".msix", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".appxbundle", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".msixbundle", StringComparison.OrdinalIgnoreCase))
                {
                    // MSIX/AppX packages are installed via Add-AppxPackage, not by executing the file
                    psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Add-AppxPackage -Path '{installerPath}'\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                }
                else if (item.InstallerType == FallbackInstallerType.Msi)
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "msiexec.exe",
                        Arguments = $"/i \"{installerPath}\" {item.FallbackInstallerArgs}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = item.FallbackInstallerArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                }

                using var process = Process.Start(psi);
                if (process == null)
                {
                    throw new InvalidOperationException("Failed to start the downloaded installer.");
                }

                string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0)
                {
                    string detail = !string.IsNullOrEmpty(stderr) ? stderr : stdout;
                    logService?.WriteAsync("Install", item.Name, $"Installer exited with code {process.ExitCode}: {detail}", isError: true);
                    throw new InvalidOperationException($"Installer exited with code {process.ExitCode}: {detail}");
                }

                logService?.WriteAsync("Install", item.Name, "Installer completed successfully", isError: false);
            }
            catch (Exception ex)
            {
                logService?.WriteAsync("Install", item.Name, $"Direct download failed: {ex.Message}", isError: true);
                throw;
            }
            finally
            {
                try { File.Delete(installerPath); } catch { }
            }
        }

        private static void DeleteBrowserData(BrowserItem browser)
        {
            if (string.IsNullOrEmpty(browser.DataPath) || !Directory.Exists(browser.DataPath))
                return;

            try
            {
                Directory.Delete(browser.DataPath, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort — some files may be locked
            }
        }

        private static void ClearExtensionPolicies(BrowserItem browser)
        {
            // Clear Chromium registry policies
            if (browser.IsChromium)
            {
                string policyKeyPath = browser.Name switch
                {
                    "Brave" => @"SOFTWARE\Policies\BraveSoftware\Brave\ExtensionInstallForcelist",
                    _ => @"SOFTWARE\Policies\Chromium\ExtensionInstallForcelist"
                };

                try
                {
                    Registry.LocalMachine.DeleteSubKeyTree($@"{policyKeyPath}", throwOnMissingSubKey: false);
                }
                catch (Exception)
                {
                    // Best-effort
                }
            }
            else
            {
                string installDir = GetFirefoxEngineInstallDir(browser.Name);
                if (!string.IsNullOrEmpty(installDir))
                {
                    string policyFile = Path.Combine(installDir, "distribution", "policies.json");
                    if (File.Exists(policyFile))
                    {
                        try { File.Delete(policyFile); } catch { }
                    }
                }

                // Clear Firefox registry policies
                string policyKeyPath = browser.Name switch
                {
                    "LibreWolf" => @"SOFTWARE\Policies\LibreWolf\ExtensionSettings",
                    "Zen Browser" => @"SOFTWARE\Policies\Zen\ExtensionSettings",
                    _ => @"SOFTWARE\Policies\Mozilla\Firefox\ExtensionSettings"
                };
                try
                {
                    Registry.LocalMachine.DeleteSubKeyTree(policyKeyPath, throwOnMissingSubKey: false);
                }
                catch (Exception) { }
            }
        }

        private void ApplyExtensions(BrowserItem browser)
        {
            var selectedExtensions = browser.Extensions.Where(e => e.IsSelected).ToList();
            if (!selectedExtensions.Any()) return;

            if (browser.IsChromium)
            {
                string policyKeyPath = browser.Name switch
                {
                    "Brave" => @"SOFTWARE\Policies\BraveSoftware\Brave\ExtensionInstallForcelist",
                    "Thorium" => @"SOFTWARE\Policies\Chromium\ExtensionInstallForcelist",
                    _ => @"SOFTWARE\Policies\Chromium\ExtensionInstallForcelist"
                };

                using var key = Registry.LocalMachine.CreateSubKey(policyKeyPath);
                if (key != null)
                {
                    int index = 1;
                    foreach (var ext in selectedExtensions)
                    {
                        key.SetValue(index.ToString(), $"{ext.ChromeId};https://clients2.google.com/service/update2/crx", RegistryValueKind.String);
                        index++;
                    }
                }
            }
            else
            {
                // Write to policies.json if possible (reliable for forks like LibreWolf/Zen)
                string installDir = GetFirefoxEngineInstallDir(browser.Name);
                if (!string.IsNullOrEmpty(installDir))
                {
                    string distDir = Path.Combine(installDir, "distribution");
                    Directory.CreateDirectory(distDir);
                    string policyFile = Path.Combine(distDir, "policies.json");
                    
                    var policies = new System.Text.StringBuilder();
                    policies.AppendLine("{");
                    policies.AppendLine("  \"policies\": {");
                    policies.AppendLine("    \"ExtensionSettings\": {");
                    
                    for (int i = 0; i < selectedExtensions.Count; i++)
                    {
                        var ext = selectedExtensions[i];
                        policies.AppendLine($"      \"{ext.FirefoxId}\": {{");
                        policies.AppendLine("        \"installation_mode\": \"force_installed\",");
                        policies.AppendLine($"        \"install_url\": \"{ext.FirefoxUrl}\"");
                        policies.Append("      }");
                        if (i < selectedExtensions.Count - 1) policies.AppendLine(",");
                        else policies.AppendLine();
                    }
                    
                    policies.AppendLine("    }");
                    policies.AppendLine("  }");
                    policies.AppendLine("}");
                    
                    File.WriteAllText(policyFile, policies.ToString());
                }

                string policyKeyPath = browser.Name switch
                {
                    "LibreWolf" => @"SOFTWARE\Policies\LibreWolf\ExtensionSettings",
                    "Zen Browser" => @"SOFTWARE\Policies\Zen\ExtensionSettings",
                    _ => @"SOFTWARE\Policies\Mozilla\Firefox\ExtensionSettings"
                };
                
                using var key = Registry.LocalMachine.CreateSubKey(policyKeyPath);
                if (key != null)
                {
                    foreach (var ext in selectedExtensions)
                    {
                        using var extKey = key.CreateSubKey(ext.FirefoxId);
                        if (extKey != null)
                        {
                            extKey.SetValue("installation_mode", "force_installed", RegistryValueKind.String);
                            extKey.SetValue("install_url", ext.FirefoxUrl, RegistryValueKind.String);
                        }
                    }
                }
            }
        }

        private static string GetFirefoxEngineInstallDir(string browserName)
        {
            string[] possiblePaths = browserName switch
            {
                "LibreWolf" => new[] { @"C:\Program Files\LibreWolf" },
                "Zen Browser" => new[] { @"C:\Program Files\Zen Browser", @"C:\Program Files\Zen", @"C:\Program Files\Zen-Browser" },
                _ => Array.Empty<string>()
            };

            foreach (var path in possiblePaths)
            {
                if (Directory.Exists(path))
                {
                    return path;
                }
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (browserName == "Zen Browser")
            {
                string localZen = Path.Combine(localAppData, "Programs", "Zen Browser");
                if (Directory.Exists(localZen)) return localZen;
                string localZen2 = Path.Combine(localAppData, "Programs", "Zen");
                if (Directory.Exists(localZen2)) return localZen2;
            }

            return string.Empty;
        }
    }

}
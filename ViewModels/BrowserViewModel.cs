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
using KalOS.Models;
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
            // The catalog (Models/SoftwareCatalog) is the single source of truth for
            // what KalOS can install — it is shared with the KalOS Setup wizard.
            // This VM only maps entries onto UI items: icons, browser data paths,
            // and the forced-extension list stay presentation-side.
            Browsers = new ObservableCollection<BrowserItem>(SoftwareCatalog.Browsers.Select(BuildBrowserItem));
            Software = new ObservableCollection<SoftwareItem>(SoftwareCatalog.Apps.Select(BuildSoftwareItem));
            Runtimes = new ObservableCollection<RuntimeItem>(SoftwareCatalog.Runtimes.Select(BuildRuntimeItem));
        }

        // ── Catalog → UI item mapping ────────────────────────────────────

        private static BrowserItem BuildBrowserItem(CatalogEntry entry) => new()
        {
            Name = entry.Name,
            Description = entry.Description,
            WingetId = entry.WingetId,
            ChocolateyId = entry.ChocolateyId,
            ScoopName = entry.ScoopName,
            IconSymbol = BrowserSymbol(entry.Name),
            IconPath = BrowserIconPath(entry.Name),
            IsChromium = entry.IsChromium,
            DataPath = BrowserDataPath(entry.Name),
            Extensions = GetDefaultExtensions(),
            FallbackDownloadUrl = entry.FallbackDownloadUrl,
            FallbackInstallerArgs = entry.FallbackInstallerArgs,
            InstallerType = ToInstallerType(entry.InstallerKind),
        };

        private static SoftwareItem BuildSoftwareItem(CatalogEntry entry) => new()
        {
            Name = entry.Name,
            Description = entry.Description,
            WingetId = entry.WingetId,
            ChocolateyId = entry.ChocolateyId,
            ScoopName = entry.ScoopName,
            IconSymbol = AppSymbol(entry.Name),
            IconPath = AppIconPath(entry.Name),
            FallbackDownloadUrl = entry.FallbackDownloadUrl,
            FallbackInstallerArgs = entry.FallbackInstallerArgs,
            InstallerType = ToInstallerType(entry.InstallerKind),
        };

        private static RuntimeItem BuildRuntimeItem(CatalogEntry entry) => new()
        {
            Name = entry.Name,
            Description = entry.Description,
            WingetId = entry.WingetId,
            ChocolateyId = entry.ChocolateyId,
            ScoopName = entry.ScoopName,
            IconSymbol = RuntimeSymbol(entry.Name),
            FallbackDownloadUrl = entry.FallbackDownloadUrl,
            FallbackInstallerArgs = entry.FallbackInstallerArgs,
            InstallerType = ToInstallerType(entry.InstallerKind),
        };

        private static FallbackInstallerType ToInstallerType(CatalogInstallerKind kind) =>
            kind == CatalogInstallerKind.Msi ? FallbackInstallerType.Msi : FallbackInstallerType.Exe;

        private static Symbol BrowserSymbol(string name) => name switch
        {
            "Brave" => Symbol.ShieldKeyhole,
            "Thorium" => Symbol.Flash,
            "LibreWolf" => Symbol.AnimalDog,
            "Zen Browser" => Symbol.LeafTwo,
            _ => Symbol.Globe,
        };

        private static Symbol AppSymbol(string name) => name switch
        {
            "Discord" => Symbol.ChatMultiple,
            "Steam" => Symbol.Games,
            "7-Zip" => Symbol.FolderZip,
            "Spotify" => Symbol.MusicNote2Play,
            _ => Symbol.Globe,
        };

        private static Symbol RuntimeSymbol(string name)
        {
            if (name.StartsWith("Visual C++", StringComparison.OrdinalIgnoreCase)) return Symbol.CodeBlock;
            if (name.StartsWith(".NET", StringComparison.OrdinalIgnoreCase)) return Symbol.WindowWrench;
            if (name.Contains("DirectX", StringComparison.OrdinalIgnoreCase) || name.Contains("XNA", StringComparison.OrdinalIgnoreCase)) return Symbol.XboxController;
            if (name.Contains("Java", StringComparison.OrdinalIgnoreCase)) return Symbol.Braces;
            if (name.Contains("OpenAL", StringComparison.OrdinalIgnoreCase)) return Symbol.Speaker2;
            return Symbol.WindowWrench;
        }

        private static string BrowserIconPath(string name) => name switch
        {
            "Brave" => "ms-appx:///Assets/icons8-brave-web-browser-48.png",
            "Thorium" => "ms-appx:///Assets/thorium.png",
            "LibreWolf" => "ms-appx:///Assets/librewolf.png",
            "Zen Browser" => "ms-appx:///Assets/zen-browser-dark.png",
            _ => string.Empty,
        };

        private static string AppIconPath(string name) => name switch
        {
            "Discord" => "ms-appx:///Assets/discord.png",
            "Steam" => "ms-appx:///Assets/steam.png",
            "7-Zip" => "ms-appx:///Assets/7zip.png",
            "Spotify" => "ms-appx:///Assets/spotify.png",
            _ => string.Empty,
        };

        private static string BrowserDataPath(string name) => name switch
        {
            "Brave" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser"),
            "Thorium" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Thorium"),
            "LibreWolf" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"LibreWolf"),
            "Zen Browser" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Zen"),
            _ => string.Empty,
        };


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
                                item.ShowSuccessNotice = false;
                                item.StatusText = string.Empty;

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
            => BrowserExtensionService.IsExtensionInstalled(
                browser.Name, browser.IsChromium, browser.DataPath, ToBrowserExtension(ext));

        private static ObservableCollection<ExtensionItem> GetDefaultExtensions()
            => new(BrowserExtensionService.CreateDefaultExtensions().Select(ToExtensionItem));

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
                            uninstallCts.Token,
                            displayName: item.Name);

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
                    var result = await packageManager.UninstallAsync(
                        item.WingetId,
                        item.ChocolateyId,
                        item.ScoopName,
                        text => dispatcher.TryEnqueue(() => item.StatusText = text),
                        uninstallCts.Token,
                        displayName: item.Name);
                    if (!result.Success)
                    {
                        // Never fake success: report exactly which managers were
                        // tried and why they failed, and keep the item installed
                        // (and its data intact) so the user can retry.
                        throw new InvalidOperationException($"No uninstaller ran successfully — {result.Detail}");
                    }

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
            => BrowserExtensionService.ClearExtensionPolicies(browser.Name, browser.IsChromium);

        private void ApplyExtensions(BrowserItem browser)
        {
            var selected = browser.Extensions.Where(e => e.IsSelected).Select(ToBrowserExtension).ToList();
            if (selected.Count == 0) return;
            BrowserExtensionService.ApplyExtensions(browser.Name, browser.IsChromium, selected);
        }

        private static ExtensionItem ToExtensionItem(BrowserExtension e) => new()
        {
            Name = e.Name,
            ChromeId = e.ChromeId,
            FirefoxId = e.FirefoxId,
            FirefoxUrl = e.FirefoxUrl,
        };

        private static BrowserExtension ToBrowserExtension(ExtensionItem e) => new()
        {
            Name = e.Name,
            ChromeId = e.ChromeId,
            FirefoxId = e.FirefoxId,
            FirefoxUrl = e.FirefoxUrl,
        };
    }

}
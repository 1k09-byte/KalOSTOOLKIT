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
            foreach (var item in Browsers.Cast<InstallableItem>().Concat(Software.Cast<InstallableItem>()))
            {
                item.IsWingetAvailable = value;
            }
        }

        [ObservableProperty]
        private bool _isRepairingWinget;

        public bool HasScanned { get; private set; }

        public ObservableCollection<BrowserItem> Browsers { get; }
        public ObservableCollection<SoftwareItem> Software { get; }

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

            foreach (var item in Browsers.Cast<InstallableItem>().Concat(Software.Cast<InstallableItem>()))
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
                foreach (var item in Browsers.Cast<InstallableItem>().Concat(Software.Cast<InstallableItem>()))
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
                default:
                    return false;
            }
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
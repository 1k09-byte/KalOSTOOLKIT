using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KalOS.Models;
using KalOS.Services;
using KalOS.Setup.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KalOS.Setup
{
    /// <summary>
    /// Orchestrates the full wizard install run, end to end:
    ///
    /// 1. <b>KalOS consumer deploy</b> — the native path (resolve latest
    ///    release → download → validate → wipe-and-copy → shortcuts → taskbar
    ///    pin) with an automatic fallback to <c>install-kalos.ps1</c> when the
    ///    native deploy cannot reach GitHub or the package fails validation.
    /// 2. <b>Customization</b> — writes the Customize page's tint + background
    ///    image into the installed app's data folder.
    /// 3. <b>GPU driver update</b> — reuses <see cref="DriverService.UpdateAsync"/>,
    ///    the same silent pipeline the in-app GPU Drivers page uses. The user's
    ///    explicit driver pick is installed without a version gate (matching the
    ///    in-app page), but never downloads a pick that isn't newer than the
    ///    installed driver — those are reported as "already up to date" instead.
    ///    The whole step is skipped when the user opted out.
    /// 4. <b>Software install</b> — reuses <see cref="PackageManagerService"/>,
    ///    chaining winget → Chocolatey → Scoop → direct download (mirroring
    ///    <c>BrowserViewModel</c>'s fallback ladder).
    /// 5. <b>Tweaks &amp; cleanup</b> — runs the native <see cref="TweaksService"/>
    ///    catalog (generated from the privacy.sexy scripts) for every category
    ///    the user left checked on the Tweaks page. Runs last so the history /
    ///    log cleanup also covers this install's own tracks.
    ///
    /// Every step reports progress through the shared wizard VM so the
    /// Progress page renders a live log + overall bar. Steps never throw out
    /// of <see cref="RunAsync"/>: a failure is recorded as a failed log entry
    /// and the pipeline moves on so the user still gets the parts that worked.
    /// </summary>
    public sealed class InstallerPipeline
    {
        private readonly IServiceProvider _services;

        public InstallerPipeline(IServiceProvider services) => _services = services;

        public async Task RunAsync(InstallerViewModel vm, Action? onFinished = null)
        {
            var ct = CancellationToken.None; // the wizard's Cancel closes the process

            // ── Step 1: KalOS consumer deploy ───────────────────────────────
            // Progress budget: KalOS owns 0–45% (download reports it live).
            vm.CurrentStep = "Installing KalOS";
            bool deployOk = await RunStepAsync(vm, "KalOS", () => DeployKalOSAsync(vm, ct));
            vm.OverallProgress = 45;

            // ── Step 2: Customization (only makes sense once KalOS landed) ──
            // Writes the Customize page's tint + background image into the
            // installed app's data folder so it opens already personalized.
            // Customization only applies when the user actually picked a tint or
            // a background image on the Customize page — otherwise nothing was
            // changed, so no step is recorded (the Finish page's "What was
            // installed" must not claim customization that never happened).
            if (deployOk && (vm.TintTouched || vm.BackgroundTouched))
            {
                vm.CurrentStep = "Applying your customization";
                var (ok, detail) = SetupCustomization.Apply(vm);
                vm.LogStep("Customization", ok, detail);
            }
            vm.OverallProgress = 45;

            // ── Step 3: GPU driver update ──────────────────────────────────
            // Updates every adapter in the plan (selected one first). The user's
            // explicit driver pick for the selected adapter is installed without
            // a version gate — exactly like the in-app GPU Drivers page, which
            // never refuses an explicit pick (if the adapter is already current,
            // pnputil reports "nothing to install" and the step still succeeds).
            // The other adapters get the latest driver the vendor knows about,
            // resolved at run time, and are skipped when already up to date so a
            // ~1.5 GB package is never downloaded needlessly. Intel/unknown
            // adapters have no silent path — the pipeline opens the vendor page.
            // Progress budget: drivers own 45–70%.
            var gpusToUpdate = vm.GpusToUpdate;
            if (gpusToUpdate.Count == 0)
            {
                // Nothing was installed — record it as a skipped step (neutral
                // info in the live log, excluded from the Finish page's "What
                // was installed"), never as a success.
                vm.LogStep("GPU driver", true,
                    vm.SkipGpuDrivers
                        ? "Skipped — you chose not to install GPU drivers."
                        : "No graphics adapter detected — skipped.",
                    skipped: true);
            }

            foreach (var gpu in gpusToUpdate)
            {
                bool isPrimary = ReferenceEquals(gpu, vm.SelectedGpu);

                DriverInfo? info;
                DriverCheckResult? check;
                if (isPrimary && vm.SelectedDriver is not null)
                {
                    info = vm.SelectedDriver;

                    // The explicit pick is installed as-is (matching the in-app
                    // GPU Drivers page), but never download a ~1 GB package just
                    // to learn the machine already runs a newer or equal driver
                    // — pnputil would refuse with exit 259 anyway. The comparer
                    // returns null for unparseable versions; proceed in that
                    // case and let pnputil be the ground truth.
                    int? cmp = DriverVersionComparer.Compare(gpu.Vendor, gpu.DriverVersion, info.Version);
                    if (cmp is >= 0)
                    {
                        vm.LogStep($"Driver: {gpu.Name}", true,
                            $"Already up to date ({gpu.DriverVersion}) — nothing to install.",
                            skipped: true);
                        continue;
                    }
                }
                else
                {
                    // Intel/unknown and the non-selected adapters: resolve the
                    // vendor's latest (Intel's "latest" is its download page
                    // URL, which the pipeline opens in the browser).
                    check = await CheckDriverAsync(gpu);
                    info = check?.LatestDriver;

                    // Don't download a ~1.5 GB package just to learn the
                    // adapter is already current — pnputil would refuse with
                    // exit 259 anyway.
                    if (info is not null && check?.Status == DriverStatus.UpToDate)
                    {
                        vm.LogStep($"Driver: {gpu.Name}", true,
                            $"Already up to date ({gpu.DriverVersion}) — nothing to install.",
                            skipped: true);
                        continue;
                    }
                }

                if (info is null)
                {
                    vm.LogStep("GPU driver", true,
                        isPrimary && vm.SelectedGpu is { IsIntel: true }
                            ? "Intel GPU — no silent-install path; install manually from the vendor site."
                            : "Skipped (no silent-install driver selected).");
                    continue;
                }

                vm.CurrentStep = "Updating GPU driver";
                await RunStepAsync(vm, $"Driver: {gpu.Name}", () => UpdateGpuDriverAsync(vm, gpu, info, ct));
            }
            vm.OverallProgress = 70;

            // ── Step 4: Software ───────────────────────────────────────────
            // Progress budget: software owns 70–95%, split evenly per entry so
            // the bar moves even while a silent install runs.
            var software = vm.SelectedSoftware;
            for (int i = 0; i < software.Count; i++)
            {
                var entry = software[i];
                vm.OverallProgress = 70 + (double)i / Math.Max(software.Count, 1) * 18;
                vm.CurrentStep = $"Installing {entry.Name}";
                await RunStepAsync(vm, entry.Name, () => InstallSoftwareAsync(entry, ct, vm));
            }
            if (software.Count > 0) vm.OverallProgress = 88;

            // ── Step 5: Forced privacy extensions on the selected browsers ──
            // Progress budget: extensions own 88–90%.
            if (vm.InstallExtensions)
            {
                var browsers = vm.SelectedSoftware.Where(e => e.IsBrowser).ToList();
                for (int i = 0; i < browsers.Count; i++)
                {
                    var entry = browsers[i];
                    vm.OverallProgress = 88 + (double)(i + 1) / Math.Max(browsers.Count, 1) * 2;
                    vm.CurrentStep = $"Applying extensions to {entry.Name}";
                    vm.CurrentDetail = "Writing browser extension policies…";
                    var extensions = BrowserExtensionService.CreateDefaultExtensions();
                    bool ok;
                    try
                    {
                        BrowserExtensionService.ApplyExtensions(entry.Name, entry.IsChromium, extensions);
                        ok = true;
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        vm.CurrentDetail = ex.Message;
                    }
                    vm.LogStep($"Extensions: {entry.Name}", ok,
                        ok
                            ? $"{extensions.Count} privacy extensions force-installed via browser policy."
                            : "Failed to apply the extension policy.");
                }
            }

            if (vm.InstallExtensions) vm.OverallProgress = 90;

            // ── Step 6: Tweaks & cleanup (native, runs last) ──────────────
            // Progress budget: tweaks own 90–100%, one tick per tweak so the
            // bar moves even while a slow DISM operation runs. The step only
            // appears when at least one category is selected; an opted-out run
            // records a neutral skipped entry instead of a success.
            var tweakGroups = vm.SelectedTweakGroups;
            if (tweakGroups.Count == 0)
            {
                vm.LogStep("Tweaks & cleanup", true, "Skipped — you chose not to run any tweaks.", skipped: true);
            }
            else
            {
                var tweaks = _services.GetRequiredService<TweaksService>();
                var defs = tweaks.Catalog.Where(t => tweakGroups.Contains(t.Group)).ToList();
                vm.CurrentStep = "Applying tweaks & cleanup";
                bool tweaksOk;
                string tweakDetail;
                try
                {
                    var (applied, failed) = await tweaks.ApplyAsync(
                        defs,
                        report: s => vm.CurrentDetail = s,
                        progress: p => vm.OverallProgress = 90 + p * 10,
                        ct);
                    tweaksOk = failed == 0;
                    tweakDetail = $"{applied} tweaks applied"
                                  + (failed > 0 ? $", {failed} failed." : ".");
                }
                catch (OperationCanceledException)
                {
                    vm.LogStep("Tweaks & cleanup", false, "Canceled by user.");
                    throw;
                }
                catch (Exception ex)
                {
                    tweaksOk = false;
                    tweakDetail = ex.Message;
                }
                vm.LogStep("Tweaks & cleanup", tweaksOk, tweakDetail);
            }

            // ── Finish ──────────────────────────────────────────────────────
            vm.CurrentStep = "Done";
            vm.CurrentDetail = string.Empty;
            vm.OverallProgress = 100;
            vm.InstallSucceeded = deployOk && vm.StepLog.All(s => s.Success);
            vm.FinishSummary = vm.InstallSucceeded
                ? "KalOS and the selected software were installed successfully."
                : "Installation completed with some failures — see the log for details.";

            vm.CurrentStep = "Finished";
            onFinished?.Invoke();
        }

        // ── Step runners ───────────────────────────────────────────────────

        private async Task<bool> RunStepAsync(InstallerViewModel vm, string name, Func<Task<bool>> step)
        {
            vm.CurrentDetail = name;
            try
            {
                bool ok = await step();
                vm.LogStep(name, ok, ok ? null : "See the KalOS log for details.");
                return ok;
            }
            catch (OperationCanceledException)
            {
                vm.LogStep(name, false, "Canceled by user.");
                throw;
            }
            catch (Exception ex)
            {
                vm.LogStep(name, false, ex.Message);
                return false;
            }
        }

        // ── Step 1: KalOS deploy (native + script fallback) ───────────────

        private async Task<bool> DeployKalOSAsync(InstallerViewModel vm, CancellationToken ct)
        {
            vm.CurrentDetail = "Resolving the latest KalOS release…";
            await vm.ResolveReleaseAsync(ct);

            // Path A — native deploy. Falls through to the script on any failure.
            if (vm.ResolvedRelease is not null)
            {
                try
                {
                    bool ok = await DeployNativeAsync(vm.ResolvedRelease, vm, ct);
                    if (ok) return true;
                    vm.CurrentDetail = "Native deploy failed — falling back to the script…";
                }
                catch (Exception ex)
                {
                    vm.CurrentDetail = $"Native deploy failed ({ex.Message}) — falling back to the script…";
                }
            }

            // Path B — script fallback (the original install-kalos.ps1 one-liner).
            return await DeployViaScriptAsync(vm);
        }

        private async Task<bool> DeployNativeAsync(GitHubReleaseInfo release, InstallerViewModel vm, CancellationToken ct)
        {
            var downloader = _services.GetRequiredService<HttpFileDownloader>();
            var log = _services.GetRequiredService<LoggingService>();

            string zipPath = Path.Combine(Path.GetTempPath(), "KalOS-Setup", $"KalOS-{release.Version}.zip");
            vm.CurrentDetail = "Downloading KalOS…";
            var progress = new Progress<double>(p =>
            {
                vm.OverallProgress = p * 0.45; // KalOS deploy = 0–45% of the bar
            });
            await downloader.DownloadAsync(release.ZipUrl, zipPath, progress, ct, minBytes: 5_000_000);
            log.Info($"Downloaded KalOS {release.Version} to {zipPath}");

            vm.CurrentDetail = "Installing KalOS…";
            var result = ZipPackageInstaller.Install(zipPath, ZipPackageInstaller.DefaultInstallDir,
                status => vm.CurrentDetail = status);

            if (!result.Success)
            {
                foreach (var err in result.Errors) log.Error(err);
                return false;
            }
            foreach (var warn in result.Warnings) log.Warn(warn);

            // Shortcuts + taskbar pin.
            string exePath = Path.Combine(ZipPackageInstaller.DefaultInstallDir, "KalOS.exe");
            ShellLinkService.CreateAppShortcuts(exePath, ZipPackageInstaller.DefaultInstallDir, "KalOS");
            ShellLinkService.TryPinToTaskbar(exePath);

            log.Success($"KalOS {release.Version} installed to {ZipPackageInstaller.DefaultInstallDir}");
            vm.CurrentDetail = "KalOS installed.";
            return true;
        }

        /// <summary>
        /// The script fallback — the exact one-liner the original console
        /// installer ran. Used when GitHub is unreachable or the native deploy
        /// fails validation, so an install is still possible offline-or-not.
        /// </summary>
        private async Task<bool> DeployViaScriptAsync(InstallerViewModel vm)
        {
            vm.CurrentDetail = "Running the KalOS install script…";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    // -InstallTool: the script's default now installs the Setup
                    // wizard itself — this fallback must deploy the app directly.
                    Arguments = "-ExecutionPolicy Bypass -NoProfile -Command \"& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/1k09-byte/KalOSTOOLKIT/main/install-kalos.ps1'))) -InstallTool\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p is null) return false;

                // Stream output to the detail line so the user sees progress.
                p.OutputDataReceived += (_, a) => { if (a.Data is { } line) vm.CurrentDetail = line; };
                p.ErrorDataReceived += (_, a) => { if (a.Data is { } line) vm.CurrentDetail = line; };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _services.GetRequiredService<LoggingService>().Error($"Script fallback failed: {ex.Message}");
                return false;
            }
        }

        // ── Step 2: GPU driver update ──────────────────────────────────────

        /// <summary>Vendor update check for one adapter — never throws.</summary>
        private async Task<DriverCheckResult?> CheckDriverAsync(GpuInfo gpu)
        {
            try
            {
                return await _services.GetRequiredService<DriverService>().CheckForUpdateAsync(gpu);
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> UpdateGpuDriverAsync(InstallerViewModel vm, GpuInfo gpu, DriverInfo info, CancellationToken ct)
        {
            var driver = _services.GetRequiredService<DriverService>();

            var progress = new Progress<DriverUpdateProgress>(p =>
            {
                vm.CurrentDetail = p.Message;
                // KalOS deploy took 45%, the driver takes the next 25%.
                vm.OverallProgress = 45 + p.Percent * 0.25;
            });
            // NVIDIA/AMD: honor the strip/keep checklists from the Drivers page
            // (everything unchecked is stripped before the display-only pnputil
            // install; AMD scheduled tasks are kept when the user asked).
            return await driver.UpdateAsync(gpu, info, progress, ct,
                nvidiaComponents: gpu.IsNvidia ? vm.SelectedNvidiaComponents : null,
                amdComponents: gpu.IsAmd ? vm.SelectedAmdComponents : null);
        }

        // ── Step 3: Software install (PM chain + direct fallback) ───────────

        private async Task<bool> InstallSoftwareAsync(CatalogEntry entry, CancellationToken ct, InstallerViewModel vm)
        {
            var packageManager = _services.GetRequiredService<PackageManagerService>();
            var log = _services.GetRequiredService<LoggingService>();

            var result = await packageManager.InstallAsync(
                entry.WingetId, entry.ChocolateyId, entry.ScoopName,
                status => vm.CurrentDetail = status, ct);

            if (result.Success)
            {
                log.Success($"Installed {entry.Name} via {result.Manager}");
                return true;
            }

            // Direct-download fallback (mirrors BrowserViewModel's ladder).
            if (string.IsNullOrEmpty(entry.FallbackDownloadUrl))
            {
                log.Error($"{entry.Name}: {result.Detail}");
                return false;
            }

            vm.CurrentDetail = $"{entry.Name}: package managers failed — downloading directly…";
            return await InstallFromDirectDownloadAsync(entry, ct, vm);
        }

        /// <summary>
        /// Direct-download fallback: stream the installer to temp and run it
        /// silently with the catalog's args. The package name → extension and
        /// the MSI-vs-EXE decision come from the catalog entry, so this works
        /// for every item without per-item code in the wizard.
        /// </summary>
        private async Task<bool> InstallFromDirectDownloadAsync(CatalogEntry entry, CancellationToken ct, InstallerViewModel vm)
        {
            var log = _services.GetRequiredService<LoggingService>();
            string ext = entry.InstallerKind == CatalogInstallerKind.Msi ? ".msi" : ".exe";
            string path = Path.Combine(Path.GetTempPath(), $"KalOS-Setup\\{entry.Name.Replace(' ', '_')}{ext}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            try
            {
                var downloader = _services.GetRequiredService<HttpFileDownloader>();
                await downloader.DownloadAsync(
                    entry.FallbackDownloadUrl, path,
                    progress: new Progress<double>(p => vm.CurrentDetail = $"Downloading {entry.Name}… {p:0}%"),
                    cancellationToken: ct,
                    minBytes: 100_000);

                // MSI runs through msiexec so the silent flags are universal;
                // EXE runs directly with the catalog's args.
                string fileName, args;
                if (entry.InstallerKind == CatalogInstallerKind.Msi)
                {
                    fileName = "msiexec.exe";
                    args = $"/i \"{path}\" {entry.FallbackInstallerArgs}";
                }
                else
                {
                    fileName = path;
                    args = entry.FallbackInstallerArgs;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) return false;
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                {
                    log.Error($"{entry.Name}: installer exited {p.ExitCode}");
                    return false;
                }
                log.Success($"{entry.Name} installed via direct download");
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"{entry.Name}: {ex.Message}");
                return false;
            }
        }
    }
}

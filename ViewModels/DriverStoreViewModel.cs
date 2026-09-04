using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using KalOS.Models;
using KalOS.Services;

namespace KalOS.ViewModels
{
    /// <summary>Bindable row wrapper for the driver list.</summary>
    public partial class DriverPackageRow : ObservableObject
    {
        public DriverPackageRecord Record { get; }

        public string InfName => Record.InfName;
        public string PublishedName => Record.PublishedName;
        public string Provider => Record.Provider;
        public string Signer => Record.Signer;
        public string DriverClass => string.IsNullOrEmpty(Record.DriverClass) ? "Unknown" : Record.DriverClass;
        public string VersionText => Record.DriverVersion?.ToString() ?? "—";
        public string DateText => Record.DriverDate?.ToString("d") ?? "—";
        public string InstallDateText => Record.InstallDate?.ToString("g") ?? "—";
        public bool BootCritical => Record.BootCritical;
        public bool IsInbox => Record.IsInbox;
        public bool InUse => Record.InUseByPresentDevice;
        public string InUseText => Record.InUseByPresentDevice ? "In use" : "—";
        public string DevicesText => Record.AssociatedDevices.Count == 0
            ? "—"
            : string.Join(", ", Record.AssociatedDevices.Select(d => string.IsNullOrEmpty(d.Description) ? d.InstanceId : d.Description).Distinct().Take(3)) + (Record.AssociatedDevices.Count > 3 ? "…" : string.Empty);
        public bool ShowDevicesText => Record.AssociatedDevices.Count != 0;
        // Swapped: driver name as title, company as subtitle (was opposite)
        public string DriverDisplayName
        {
            get
            {
                var firstDevice = Record.AssociatedDevices.FirstOrDefault()?.Description;
                if (!string.IsNullOrWhiteSpace(firstDevice) && firstDevice != "—")
                    return firstDevice!;
                if (!string.IsNullOrWhiteSpace(Record.DriverClass) && !Record.DriverClass.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return Record.DriverClass;
                return Record.InfName;
            }
        }
        public string CompanyDisplayName => string.IsNullOrWhiteSpace(Provider) ? "Unknown" : Provider;
        public bool IsOffline => Record.IsOffline;

        [ObservableProperty]
        private string _sizeText = "…";

        [ObservableProperty]
        private bool _isSelected;

        public DriverPackageRow(DriverPackageRecord record) => Record = record;

        public void SetSize(long bytes) =>
            SizeText = bytes <= 0 ? "—" : FormatBytes(bytes);

        internal static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "\u2014"; // em dash for unknown/zero
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return $"{size:0.#} {units[unit]}";
        }
    }

    /// <summary>Bindable Smart Cleanup candidate row.</summary>
    public sealed partial class CleanupCandidateRow : ObservableObject
    {
        public CleanupCandidate Candidate { get; }
        public DriverPackageRecord Package => Candidate.Package;
        public string Name => Candidate.Package.DisplayName;
        public string InfName => Candidate.Package.InfName;
        public string VersionText => Candidate.Package.DriverVersion?.ToString() ?? "—";
        public string Reason => Candidate.Reason;
        public bool BootCritical => Candidate.Package.BootCritical;

        [ObservableProperty]
        private bool _isSelected;

        public CleanupCandidateRow(CleanupCandidate candidate)
        {
            Candidate = candidate;
            _isSelected = candidate.PreChecked;
        }
    }

    /// <summary>
    /// ViewModel for the Driver Store Manager page (spec section 5).
    /// Enumeration and all store operations run off the UI thread; folder
    /// sizes populate asynchronously after the list is shown (spec 6.1);
    /// batch operations report per-item progress and support cancellation
    /// between items (spec 6.3).
    /// </summary>
    public partial class DriverStoreViewModel : ObservableObject
    {
        private readonly LoggingService _log;
        private readonly RestorePointService _restorePoints;
        private Func<DriverStoreTarget, string, IDriverStoreProvider> _providerFactory;

        private CancellationTokenSource? _sizeCts;
        private CancellationTokenSource? _batchCts;

        /// <summary>Folder-size cache keyed by path + last-write time (spec 6.1).</summary>
        private readonly Dictionary<string, (DateTime Stamp, long Size)> _sizeCache = new();

        public DriverStoreViewModel(LoggingService log, RestorePointService restorePoints)
        {
            _log = log;
            _restorePoints = restorePoints;
            _providerFactory = (target, offlineRoot) => target == DriverStoreTarget.Offline
                ? new NativeDriverStoreProvider(offlineRoot)
                : new NativeDriverStoreProvider();
        }

        /// <summary>Test/interop-failure seam: swap in the pnputil fallback.</summary>
        public void UseFallbackProvider(ProcessManager processManager) =>
            _providerFactory = (_, _) => new PnputilDriverStoreProvider(processManager);

        public ObservableCollection<DriverPackageRow> Packages { get; } = new();
        public ObservableCollection<CleanupCandidateRow> CleanupCandidates { get; } = new();

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _busyText = string.Empty;

        [ObservableProperty]
        private string _statusText = "Not loaded yet.";

        [ObservableProperty]
        private bool _includeInbox;

        [ObservableProperty]
        private bool _isOfflineTarget;

        [ObservableProperty]
        private string _offlineRoot = string.Empty;

        [ObservableProperty]
        private bool _offlineRootConfirmed;

        [ObservableProperty]
        private string _totalSizeText = string.Empty;

        [ObservableProperty]
        private double _batchProgress;

        [ObservableProperty]
        private string _batchProgressText = string.Empty;

        [ObservableProperty]
        private bool _isBatchCancelable;

        /// <summary>Native interop failed → the page should offer the pnputil fallback (spec 8.1).</summary>
        [ObservableProperty]
        private bool _nativeInteropFailed;

        public string TargetDescription => IsOfflineTarget
            ? $"Offline image: {OfflineRoot}"
            : "This PC (online DriverStore)";

        public bool IsTargetConfirmed => !IsOfflineTarget || OfflineRootConfirmed;

        partial void OnIncludeInboxChanged(bool value) => _ = RefreshAsync();
        partial void OnIsOfflineTargetChanged(bool value) => OnPropertyChanged(nameof(TargetDescription));
        partial void OnOfflineRootChanged(string value)
        {
            OnPropertyChanged(nameof(TargetDescription));
            OfflineRootConfirmed = false; // re-confirm per session/change (spec 7.5)
        }

        public event EventHandler<string>? ErrorOccurred;

        private IDriverStoreProvider CreateProvider() => _providerFactory(
            IsOfflineTarget ? DriverStoreTarget.Offline : DriverStoreTarget.Online,
            OfflineRoot);

        [RelayCommand]
        private async Task BrowseOfflineRootAsync()
        {
            // Folder picking happens in the View (needs hwnd interop); the
            // View sets OfflineRoot directly. Kept as a command for symmetry.
            await Task.CompletedTask;
        }

        [RelayCommand]
        public async Task RefreshAsync()
        {
            if (IsBusy) return;            if (IsOfflineTarget && !OfflineStoreValidator.IsValidOfflineRoot(OfflineRoot))
            {
                ErrorOccurred?.Invoke(this, "The selected offline image does not contain a Windows\\System32\\DriverStore structure.");
                return;
            }

            IsBusy = true;
            BusyText = "Enumerating driver store…";
            StatusText = "Loading…";
            NativeInteropFailed = false;
            Packages.Clear();
            CleanupCandidates.Clear();
            _sizeCts?.Cancel();
            _sizeCts = new CancellationTokenSource();

            try
            {
                var provider = CreateProvider();
                var records = await Task.Run(() => provider.EnumeratePackages(IncludeInbox));

                // Hide boot-critical + dash-drivers entirely (second line would be "—" U+2014)
                // and drivers whose description is BOTH Unknown AND a dash (e.g. "Unknown —").
                var filtered = records.Where(r => !r.BootCritical && !IsUnknownAndDash(r.DriverClass) && r.AssociatedDevices.Count > 0).ToList();

                foreach (var r in filtered.OrderByDescending(r => r.BootCritical).ThenBy(r => r.Provider, StringComparer.OrdinalIgnoreCase))
                    Packages.Add(new DriverPackageRow(r));

                StatusText = $"{Packages.Count} package(s) — {TargetDescription}";
                _log.Info($"DriverStore: enumerated {Packages.Count} packages ({(provider.IsFallback ? "pnputil fallback" : "native")}, {(IsOfflineTarget ? "offline" : "online")}).");

                // Deferred folder sizes (spec 6.1): the list is already shown;
                // sizes fill in per row in the background, cache-checked.
                _ = ComputeSizesAsync(provider, _sizeCts.Token);
            }
            catch (Exception ex) when (IsNativeInteropFailure(ex))
            {
                NativeInteropFailed = true;
                StatusText = "Native driver-store API unavailable on this Windows build.";
                _log.Info($"DriverStore native enumeration failed: {ex.Message}");
                ErrorOccurred?.Invoke(this,
                    "The native driver-store API could not be loaded on this Windows build. " +
                    "You can retry using the built-in pnputil fallback (fewer details, fully supported by Microsoft).");
            }
            catch (Exception ex)
            {
                StatusText = "Enumeration failed.";
                _log.Info($"DriverStore enumeration error: {ex}");
                ErrorOccurred?.Invoke(this, $"Enumeration failed: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
                BusyText = string.Empty;
            }
        }

        private static bool IsUnknownAndDash(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var t = value.Trim();
            bool hasUnknown = t.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) >= 0;
            // Exact dash from code: "—" U+2014 em dash (hex E2 80 94) used everywhere as placeholder
            // e.g. VersionText => ?? "—", FormatBytes => "\u2014"
            bool hasDash = t.Contains('—');
            return hasUnknown && hasDash;
        }

        private static bool IsNativeInteropFailure(Exception ex) =>
            ex is DllNotFoundException or EntryPointNotFoundException
            || (ex is System.ComponentModel.Win32Exception && ex.Message.Contains("driver store", StringComparison.OrdinalIgnoreCase));

        /// <summary>Switch to the pnputil fallback provider and re-enumerate (spec 8.1 mitigation).</summary>
        [RelayCommand]
        public async Task UsePnputilFallbackAsync()
        {
            if (IsBusy) return;
            UseFallbackProvider(App.Services.GetRequiredService<ProcessManager>());
            await RefreshAsync();
        }


        private async Task ComputeSizesAsync(IDriverStoreProvider provider, CancellationToken ct)
        {
            try
            {
                long total = 0;
                foreach (var row in Packages)
                {
                    ct.ThrowIfCancellationRequested();
                    string folder = row.Record.FolderLocation;
                    if (string.IsNullOrEmpty(folder))
                    {
                        row.SetSize(0);
                        continue;
                    }

                    long size = GetCachedOrComputeSize(folder, ct);
                    row.SetSize(size);
                    total += size;
                }
                TotalSizeText = $"Total: {DriverPackageRow.FormatBytes(total)}";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Info($"DriverStore size computation failed: {ex.Message}");
            }
        }

        private long GetCachedOrComputeSize(string folder, CancellationToken ct)
        {
            try
            {
                var stamp = Directory.GetLastWriteTimeUtc(folder);
                if (_sizeCache.TryGetValue(folder, out var cached) && cached.Stamp == stamp)
                    return cached.Size;

                long size = ComputeFolderSize(folder, ct);
                _sizeCache[folder] = (stamp, size);
                return size;
            }
            catch
            {
                return 0;
            }
        }

        private static long ComputeFolderSize(string folder, CancellationToken ct)
        {
            long size = 0;
            var stack = new Stack<string>();
            stack.Push(folder);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        ct.ThrowIfCancellationRequested();
                        try { size += new FileInfo(f).Length; } catch { }
                    }
                    foreach (var sub in Directory.EnumerateDirectories(dir)) stack.Push(sub);
                }
                catch { }
            }
            return size;
        }

        // ── Selection helpers ───────────────────────────────────────────

        public List<DriverPackageRow> SelectedRows => Packages.Where(p => p.IsSelected).ToList();

        // ── Export ──────────────────────────────────────────────────────

        public async Task ExportSelectedAsync(string destinationFolder)
        {
            var rows = SelectedRows;
            if (rows.Count == 0) return;
            await ExportRowsAsync(rows, destinationFolder);
        }

        public async Task ExportRowsAsync(IReadOnlyList<DriverPackageRow> rows, string destinationFolder)
        {
            if (rows.Count == 0) return;
            await RunBatchAsync("Exporting {0} of {1}: {2}", async (provider, row, ct) =>
            {
                await Task.Run(() => provider.ExportDriver(row.Record, destinationFolder), ct);
            }, rows);
        }

        // ── Delete (with restore point + safety gating) ────────────────

        /// <summary>
        /// The actual delete worker. The VIEW owns all confirmation dialogs
        /// (boot-critical typing phrase, force-delete acknowledgment,
        /// restore-point opt-out warning) — this method assumes consent.
        /// </summary>
        public async Task DeleteConfirmedAsync(IReadOnlyList<DriverPackageRow> rows, bool createRestorePoint)
        {
            if (rows.Count == 0) return;

            if (createRestorePoint)
            {
                IsBusy = true;
                BusyText = "Creating a System Restore point…";
                bool ok = await _restorePoints.CreateAsync("KalOS Driver Store cleanup");
                IsBusy = false;
                BusyText = string.Empty;
                if (!ok)
                {
                    ErrorOccurred?.Invoke(this,
                        "A System Restore point could NOT be created (System Restore may be disabled). " +
                        "The deletion was NOT started — enable System Restore or disable the restore-point requirement in Settings.");
                    return; // fail-safe: no restore point, no delete
                }
            }

            var provider = CreateProvider();
            await RunBatchAsync("Deleting {0} of {1}: {2}", async (p, row, ct) =>
            {
                await Task.Run(() => p.DeleteDriver(row.Record, forceDelete: false), ct);
            }, rows, providerOverride: provider);
        }

        /// <summary>Force-delete one in-use package (spec 7.2 — single-item only, view confirms).</summary>
        public async Task ForceDeleteConfirmedAsync(DriverPackageRow row, bool createRestorePoint)
        {
            if (createRestorePoint)
            {
                bool ok = await _restorePoints.CreateAsync("KalOS force driver removal");
                if (!ok)
                {
                    ErrorOccurred?.Invoke(this, "A System Restore point could NOT be created — force delete was NOT started.");
                    return;
                }
            }

            IsBusy = true;
            BusyText = $"Force-deleting {row.Record.InfName}…";
            try
            {
                var provider = CreateProvider();
                await Task.Run(() => provider.DeleteDriver(row.Record, forceDelete: true));
                Packages.Remove(row);
                StatusText = $"Force-deleted {row.Record.InfName}.";
                _log.Info($"DriverStore: force-deleted {row.Record.InfName}.");
                _ = RefreshTotalsAfterDeleteAsync();
            }
            catch (Exception ex)
            {
                _log.Info($"DriverStore force-delete failed: {ex}");
                ErrorOccurred?.Invoke(this, $"Force delete failed: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
                BusyText = string.Empty;
            }
        }

        private async Task RefreshTotalsAfterDeleteAsync()
        {
            // Status text only; a full re-enumeration is user-initiated.
            await Task.CompletedTask;
        }

        // ── Add / install ───────────────────────────────────────────────

        public async Task AddDriverAsync(string infFullPath, bool install)
        {
            IsBusy = true;
            BusyText = install ? "Adding and installing driver…" : "Adding driver (staging only)…";
            try
            {
                var provider = CreateProvider();
                if (install && !provider.SupportsInstallToDevice)
                    throw new NotSupportedException("Installing onto devices is only supported for the online store.");
                await Task.Run(() => provider.AddDriver(infFullPath, install));
                StatusText = install ? $"Added and installed {Path.GetFileName(infFullPath)}." : $"Staged {Path.GetFileName(infFullPath)}.";
                _log.Info($"DriverStore: add {(install ? "+ install" : "only")}: {infFullPath}");
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                _log.Info($"DriverStore add failed: {ex}");
                ErrorOccurred?.Invoke(this, $"Add driver failed: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
                BusyText = string.Empty;
            }
        }

        // ── Smart Cleanup (spec 5.5) ────────────────────────────────────

        [RelayCommand]
        public void ComputeCleanupCandidates()
        {
            CleanupCandidates.Clear();
            var candidates = SmartCleanupClassifier.GetCandidates(Packages.Select(p => p.Record));

            // Show reasoning per candidate; boot-critical/in-use are already
            // excluded by the classifier (hard rules, tested).
            foreach (var c in candidates)
                CleanupCandidates.Add(new CleanupCandidateRow(c));

            StatusText = CleanupCandidates.Count == 0
                ? "Smart Cleanup: no safe cleanup candidates found."
                : $"Smart Cleanup: {CleanupCandidates.Count} candidate(s) for review — nothing is deleted until you confirm.";
        }

        public async Task DeleteCleanupConfirmedAsync(IReadOnlyList<CleanupCandidateRow> rows, bool createRestorePoint)
        {
            var provider = CreateProvider();
            var mapped = rows.Select(r => Packages.FirstOrDefault(p => p.Record == r.Package))
                .Where(p => p is not null)
                .Cast<DriverPackageRow>()
                .ToList();

            // Also handle packages that vanished between enumeration and confirm.
            var missing = rows.Where(r => !mapped.Any(m => m.Record == r.Package)).ToList();
            if (missing.Count > 0)
            {
                await RunBatchAsync("Deleting {0} of {1}: {2}",
                    async (p, row, ct) => await Task.Run(() => p.DeleteDriver(row.Record, false), ct),
                    missing.Select(r => new DriverPackageRow(r.Package)).ToList(),
                    providerOverride: provider);
            }

            if (mapped.Count > 0)
                await DeleteConfirmedAsync(mapped, createRestorePoint);
        }

        // ── Batch runner with per-item progress + cancellation (6.3) ───

        private async Task RunBatchAsync(string progressFormat, Func<IDriverStoreProvider, DriverPackageRow, CancellationToken, Task> operation,
            IReadOnlyList<DriverPackageRow> rows, IDriverStoreProvider? providerOverride = null)
        {
            var provider = providerOverride ?? CreateProvider();
            _batchCts = new CancellationTokenSource();
            var ct = _batchCts.Token;

            IsBusy = true;
            IsBatchCancelable = true;
            BatchProgress = 0;
            int done = 0, failed = 0;

            try
            {
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    BatchProgressText = string.Format(progressFormat, done + 1, rows.Count, row.Record.InfName);
                    try
                    {
                        await operation(provider, row, ct);
                        done++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _log.Info($"DriverStore batch item {row.Record.InfName} failed: {ex.Message}");
                        ErrorOccurred?.Invoke(this, $"{row.Record.InfName}: {ex.Message}");
                    }
                    BatchProgress = (double)(done + failed) / rows.Count * 100;
                }

                StatusText = $"Batch finished: {done} succeeded, {failed} failed.";
                _log.Info($"DriverStore batch: {done} ok, {failed} failed.");
            }
            catch (OperationCanceledException)
            {
                StatusText = $"Batch cancelled after {done + failed} of {rows.Count} items.";
            }
            finally
            {
                IsBusy = false;
                IsBatchCancelable = false;
                BatchProgressText = string.Empty;
                _batchCts.Dispose();
                _batchCts = null;
            }
        }

        [RelayCommand]
        public void CancelBatch() => _batchCts?.Cancel();

        public void CancelSizeComputation() => _sizeCts?.Cancel();

        [RelayCommand]
        public void SelectAll() => SetSelection(true);

        [RelayCommand]
        public void SelectNone() => SetSelection(false);

        private void SetSelection(bool value)
        {
            foreach (var p in Packages) p.IsSelected = value;
        }
    }
}

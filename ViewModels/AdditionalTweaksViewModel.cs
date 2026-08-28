using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using KalOS.Helpers;
using KalOS.Services;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace KalOS.ViewModels
{
    /// <summary>
    /// Registry values written to HKCU\System\GameConfigStore for the game
    /// fullscreen toggle. Right = Fullscreen Optimizations (FSO), Left =
    /// Fullscreen Exclusive (FSE) — exactly matching the FSO.reg / FSE.reg
    /// reference files.
    /// </summary>
    public static class FullscreenModePresets
    {
        public const string GameConfigStoreKey = @"HKCU\System\GameConfigStore";

        /// <summary>Right — Fullscreen Optimizations.</summary>
        public static IReadOnlyDictionary<string, int> Fso { get; } = new Dictionary<string, int>
        {
            ["GameDVR_DXGIHonorFSEWindowsCompatible"] = 0,
            ["GameDVR_HonorUserFSEBehaviorMode"] = 0,
            ["GameDVR_FSEBehaviorMode"] = 0,
            ["GameDVR_FSEBehavior"] = 0,
            ["GameDVR_DSEBehavior"] = 0,
        };

        /// <summary>Left — Fullscreen Exclusive.</summary>
        public static IReadOnlyDictionary<string, int> Fse { get; } = new Dictionary<string, int>
        {
            ["GameDVR_DXGIHonorFSEWindowsCompatible"] = 1,
            ["GameDVR_HonorUserFSEBehaviorMode"] = 0,
            ["GameDVR_FSEBehaviorMode"] = 2,
            ["GameDVR_FSEBehavior"] = 2,
            ["GameDVR_DSEBehavior"] = 2,
        };
    }

    /// <summary>
    /// A selectable Win32PrioritySeparation value. The label always shows the
    /// actual value in decimal and hex so the dropdown can't be ambiguous
    /// (guides mix "26 hex" and "38 decimal" for the same setting).
    /// </summary>
    public sealed record PrioritySeparationOption(int Value, string Description)
    {
        public string Label => $"{Value} (0x{Value:X2}) — {Description}";

        public override string ToString() => Label;
    }

    /// <summary>
    /// The popular Win32PrioritySeparation presets, from the community latency
    /// guides (Blur Busters / calypto guide, guru3d FAQ) — the quantum length
    /// (short/long), fixed-vs-variable, and foreground boost level. Packed into
    /// a tiny standalone class with no WinRT dependencies so tests can guard it.
    /// </summary>
    public static class PrioritySeparationPresets
    {
        /// <summary>0x02 — the Windows 10/11 client default.</summary>
        public static PrioritySeparationOption WindowsDefault { get; } = new(0x02, "Windows default");

        /// <summary>0x16 (22) — Long, Variable, High foreground boost (the "16" some guides quote).</summary>
        public static PrioritySeparationOption LongVariableHighBoost { get; } = new(0x16, "Long, Variable, High boost");

        /// <summary>0x18 (24) — Long, Fixed, No foreground boost.</summary>
        public static PrioritySeparationOption LongFixedNoBoost { get; } = new(0x18, "Long, Fixed, No boost");

        /// <summary>0x1A (26) — Long, Fixed, High foreground boost.</summary>
        public static PrioritySeparationOption LongFixedHighBoost { get; } = new(0x1A, "Long, Fixed, High boost");

        /// <summary>0x24 (36) — Short, Variable, No foreground boost.</summary>
        public static PrioritySeparationOption ShortVariableNoBoost { get; } = new(0x24, "Short, Variable, No boost");

        /// <summary>0x28 (40) — Short, Fixed, No foreground boost (best response time per Blur Busters).</summary>
        public static PrioritySeparationOption ShortFixedNoBoost { get; } = new(0x28, "Short, Fixed, No boost");

        /// <summary>0x2A (42) — Short, Fixed, High foreground boost.</summary>
        public static PrioritySeparationOption ShortFixedHighBoost { get; } = new(0x2A, "Short, Fixed, High boost");

        /// <summary>All presets, ascending by value.</summary>
        public static IReadOnlyList<PrioritySeparationOption> Presets { get; } = new[]
        {
            WindowsDefault,
            LongVariableHighBoost,
            LongFixedNoBoost,
            LongFixedHighBoost,
            ShortVariableNoBoost,
            ShortFixedNoBoost,
            ShortFixedHighBoost,
        };
    }

    /// <summary>
    /// Drives the Bluetooth and Wi-Fi toggles on the Additional Tweaks page, plus the
    /// Win32PrioritySeparation dropdown.
    /// Detection and live state sync use the same radio API as the Windows
    /// Action Center (Windows.Devices.Radios); toggling, however, performs a
    /// deep disable — Windows services are set to Disabled (registry Start=4)
    /// and stopped, and the adapter is disabled at the PnP device level — so
    /// the radio stays off across reboots. Enabling restores everything.
    /// </summary>
    public partial class AdditionalTweaksViewModel : ObservableObject
    {
        private const string PriorityControlKey = @"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl";
        private const string Win32PrioritySeparationValueName = "Win32PrioritySeparation";
        private const string UacKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        private const string UacValueName = "EnableLUA";

        private readonly RadioStackService _radioStack;

        private Radio? _bluetoothRadio;
        private Radio? _wifiRadio;
        private DispatcherQueue? _dispatcher;
        private bool _applying;
        private bool _syncing;
        private bool _loadingPriority;
        private bool _applyingPriority;
        private bool _loadingFullscreen;
        private bool _applyingFullscreen;
        private bool _loadingUac;
        private bool _applyingUac;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private bool _bluetoothDetected;

        [ObservableProperty]
        private bool _wifiDetected;

        [ObservableProperty]
        private bool _bluetoothEnabled;

        [ObservableProperty]
        private bool _wifiEnabled;

        [ObservableProperty]
        private string _statusText = string.Empty;

        [ObservableProperty]
        private PrioritySeparationOption? _selectedPrioritySeparation;

        [ObservableProperty]
        private IReadOnlyList<PrioritySeparationOption> _prioritySeparationItems = Array.Empty<PrioritySeparationOption>();

        [ObservableProperty]
        private string _prioritySeparationDescription = "How much extra CPU time the focused app gets versus background processes.";

        [ObservableProperty]
        private string _prioritySeparationCurrentText = "Current: not loaded";

        /// <summary>True = Fullscreen Optimizations (right), False = Fullscreen Exclusive (left).</summary>
        [ObservableProperty]
        private bool _fullscreenOptimizationsEnabled;

        [ObservableProperty]
        private bool _uacEnabled;

        [ObservableProperty]
        private string _uacDetectedValueText = "Checking current state...";

        [ObservableProperty]
        private bool _uacDetectedNeedsReboot;

        public string BluetoothStatusText => BluetoothEnabled ? "On" : "Off — services, registry, and adapter disabled";

        public string WifiStatusText => WifiEnabled ? "On" : "Off — services, registry, and adapter disabled";

        public string FullscreenModeDescription => "Right = Fullscreen Optimizations (FSO), left = Fullscreen Exclusive (FSE).";

        public string FullscreenCurrentText => $"Current: {(FullscreenOptimizationsEnabled ? "FSO" : "FSE")}";

        public string UacStatusText => UacEnabled
            ? "On — UAC prompts enabled (EnableLUA=1). Reboot required to apply."
            : "Off — UAC disabled (EnableLUA=0). OS ships disabled by default. Reboot required.";

        public string UacCurrentText => $"Current: {(UacEnabled ? "Enabled (1)" : "Disabled (0)")}";

        /// <summary>The popular Win32PrioritySeparation presets shown in the dropdown.</summary>
        public IReadOnlyList<PrioritySeparationOption> PrioritySeparationOptions => PrioritySeparationPresets.Presets;

        /// <summary>True once the current registry value has been loaded into the dropdown.</summary>
        public bool IsPrioritySeparationLoaded { get; private set; }

        public AdditionalTweaksViewModel(RadioStackService radioStack)
        {
            _radioStack = radioStack;
        }

        partial void OnBluetoothDetectedChanged(bool value) => OnPropertyChanged(nameof(BluetoothStatusText));

        partial void OnWifiDetectedChanged(bool value) => OnPropertyChanged(nameof(WifiStatusText));

        partial void OnBluetoothEnabledChanged(bool value)
        {
            OnPropertyChanged(nameof(BluetoothStatusText));
            if (_syncing || _applying) return;
            _ = ToggleDeepAsync(RadioKind.Bluetooth, value, "Bluetooth");
        }

        partial void OnWifiEnabledChanged(bool value)
        {
            OnPropertyChanged(nameof(WifiStatusText));
            if (_syncing || _applying) return;
            _ = ToggleDeepAsync(RadioKind.WiFi, value, "Wi-Fi");
        }

        partial void OnSelectedPrioritySeparationChanged(PrioritySeparationOption? value)
        {
            if (_loadingPriority || _applyingPriority || value == null) return;
            _ = ApplyPrioritySeparationAsync(value);
        }

        partial void OnFullscreenOptimizationsEnabledChanged(bool value)
        {
            OnPropertyChanged(nameof(FullscreenModeDescription));
            OnPropertyChanged(nameof(FullscreenCurrentText));
            if (_loadingFullscreen || _applyingFullscreen) return;
            _ = ApplyFullscreenModeAsync(value);
        }

        partial void OnUacEnabledChanged(bool value)
        {
            OnPropertyChanged(nameof(UacStatusText));
            OnPropertyChanged(nameof(UacCurrentText));
            if (_loadingUac || _applyingUac) return;
            _ = ApplyUacAsync(value);
        }

        /// <summary>
        /// Reads the current GameConfigStore state and sets the toggle: FSE when
        /// the forced-exclusive values are present, FSO otherwise.
        /// </summary>
        public void LoadFullscreenMode()
        {
            _loadingFullscreen = true;
            try
            {
                var fse = false;
                try
                {
                    var mode = RegistryHelper.GetRegistryValue(FullscreenModePresets.GameConfigStoreKey, "GameDVR_FSEBehaviorMode");
                    var honor = RegistryHelper.GetRegistryValue(FullscreenModePresets.GameConfigStoreKey, "GameDVR_DXGIHonorFSEWindowsCompatible");
                    fse = (mode is int mi && mi == 2) || (mode is uint mu && mu == 2)
                          || (honor is int hi && hi == 1) || (honor is uint hu && hu == 1);
                }
                catch
                {
                    // Key or value missing — falls through to the FSO default.
                }

                FullscreenOptimizationsEnabled = !fse;
            }
            finally
            {
                _loadingFullscreen = false;
            }
        }

        private async Task ApplyFullscreenModeAsync(bool fso)
        {
            if (_applyingFullscreen) return;

            _applyingFullscreen = true;
            IsBusy = true;
            try
            {
                var values = fso ? FullscreenModePresets.Fso : FullscreenModePresets.Fse;
                await Task.Run(() =>
                {
                    foreach (var (name, value) in values)
                    {
                        RegistryHelper.SetRegistryValue(FullscreenModePresets.GameConfigStoreKey, name, value, RegistryValueKind.DWord);
                    }
                });
                StatusText = string.Empty;
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to set game fullscreen mode: {ex.Message}";
                LoadFullscreenMode(); // snap the toggle back to the real state
            }
            finally
            {
                _applyingFullscreen = false;
                IsBusy = false;
            }
        }

        public void LoadUac()
        {
            _loadingUac = true;
            try
            {
                var enabled = false;
                string detected;
                try
                {
                    var raw = RegistryHelper.GetRegistryValue(UacKey, UacValueName);
                    if (raw == null)
                    {
                        enabled = false;
                        detected = "Detected: EnableLUA not set (missing) — treated as Disabled (0) per OS default. Path: HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System";
                    }
                    else
                    {
                        var intVal = raw switch
                        {
                            int i => i,
                            uint u => (int)u,
                            _ => Convert.ToInt32(raw),
                        };
                        enabled = intVal != 0;
                        var state = enabled ? "Enabled" : "Disabled";
                        detected = $"Detected: EnableLUA={intVal} (DWORD) at HKLM\\...\\System\\EnableLUA — currently {state} (EnableLUA={(enabled ? 1 : 0)})";
                    }
                }
                catch (Exception ex)
                {
                    enabled = false;
                    detected = $"Detection failed: {ex.Message}";
                }

                UacDetectedValueText = detected;
                UacDetectedNeedsReboot = false;
                UacEnabled = enabled;
                OnPropertyChanged(nameof(UacStatusText));
            }
            finally
            {
                _loadingUac = false;
            }
        }

        private async Task ApplyUacAsync(bool enabled)
        {
            if (_applyingUac) return;

            _applyingUac = true;
            IsBusy = true;
            try
            {
                var dword = enabled ? 1 : 0;
                await Task.Run(() =>
                {
                    RegistryHelper.BackupRegistryKey(UacKey);
                    RegistryHelper.SetRegistryValue(UacKey, UacValueName, dword, RegistryValueKind.DWord);
                });
                UacDetectedValueText = $"Set: EnableLUA={dword} written to HKLM\\...\\System. Reboot required for change to take effect.";
                UacDetectedNeedsReboot = true;
                StatusText = string.Empty;
                OnPropertyChanged(nameof(UacStatusText));
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to set UAC: {ex.Message}";
                LoadUac();
            }
            finally
            {
                _applyingUac = false;
                IsBusy = false;
            }
        }

        /// <summary>
        /// Reads the current Win32PrioritySeparation value from the registry and
        /// selects the matching preset (or a "Custom" entry if it isn't one of
        /// ours). Safe to call repeatedly — selection is guarded so it never
        /// re-applies the value it just loaded.
        /// </summary>
        public void LoadPrioritySeparation()
        {
            _loadingPriority = true;
            try
            {
                int? current = null;
                try
                {
                    var raw = RegistryHelper.GetRegistryValue(PriorityControlKey, Win32PrioritySeparationValueName);
                    current = raw switch
                    {
                        int i => i,
                        uint u => (int)u,
                        null => null,
                        _ => Convert.ToInt32(raw),
                    };
                }
                catch
                {
                    // Value absent or unreadable — treat as "not set yet".
                }

                // Build the display list from the presets, marking whichever one
                // is currently set as "(current)". A value outside the popular
                // set gets its own entry at the top so the real value is always
                // visible in the dropdown.
                var items = PrioritySeparationPresets.Presets.ToList();
                PrioritySeparationOption? selected;
                if (current is int c)
                {
                    PrioritySeparationDescription = "How much extra CPU time the focused app gets versus background processes.";
                    PrioritySeparationCurrentText = $"Current: {c} (0x{c:X2})";

                    var match = items.FirstOrDefault(o => o.Value == c);
                    if (match != null)
                    {
                        var idx = items.IndexOf(match);
                        items[idx] = match with { Description = match.Description + " (current)" };
                        selected = items[idx];
                    }
                    else
                    {
                        var custom = new PrioritySeparationOption(c, "Custom (current)");
                        items.Insert(0, custom);
                        selected = custom;
                    }
                }
                else
                {
                    PrioritySeparationDescription = "How much extra CPU time the focused app gets versus background processes.";
                    PrioritySeparationCurrentText = "Current: not set";
                    selected = null;
                }

                // ItemsSource first, then selection — setting the selection before
                // the items list updates can leave the ComboBox visually blank.
                PrioritySeparationItems = items;
                SelectedPrioritySeparation = selected;
                IsPrioritySeparationLoaded = true;
            }
            finally
            {
                _loadingPriority = false;
            }
        }

        /// <summary>Writes the picked value to the registry (with a backup first).</summary>
        private async Task ApplyPrioritySeparationAsync(PrioritySeparationOption option)
        {
            if (_applyingPriority) return;

            _applyingPriority = true;
            IsBusy = true;
            try
            {
                await Task.Run(() =>
                {
                    RegistryHelper.BackupRegistryKey(PriorityControlKey);
                    RegistryHelper.SetRegistryValue(PriorityControlKey, Win32PrioritySeparationValueName, option.Value, RegistryValueKind.DWord);
                });
                StatusText = string.Empty;
                LoadPrioritySeparation(); // refresh the "(current)" marker and card description
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to set CPU priority separation: {ex.Message}";
                LoadPrioritySeparation(); // snap the dropdown back to the real value
            }
            finally
            {
                _applyingPriority = false;
                IsBusy = false;
            }
        }

        /// <summary>
        /// Discovers the Wi-Fi and Bluetooth radios and reflects their current
        /// state. Safe to call repeatedly (re-navigation re-detects without
        /// double-subscribing).
        /// </summary>
        public async Task DetectAsync()
        {
            _dispatcher ??= DispatcherQueue.GetForCurrentThread();

            IReadOnlyList<Radio> radios;
            try
            {
                radios = await Radio.GetRadiosAsync();
            }
            catch (Exception ex)
            {
                StatusText = $"Radio detection unavailable: {ex.Message}";
                return;
            }

            var bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            var wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);

            if (_bluetoothRadio != null) _bluetoothRadio.StateChanged -= OnRadioStateChanged;
            if (_wifiRadio != null) _wifiRadio.StateChanged -= OnRadioStateChanged;

            _bluetoothRadio = bluetooth;
            _wifiRadio = wifi;

            if (_bluetoothRadio != null) _bluetoothRadio.StateChanged += OnRadioStateChanged;
            if (_wifiRadio != null) _wifiRadio.StateChanged += OnRadioStateChanged;

            BluetoothDetected = _bluetoothRadio != null;
            WifiDetected = _wifiRadio != null;
            
            // If WinRT couldn't find the radios, it's possible we disabled them completely.
            // Check the internal configuration snapshot.
            try
            {
                var configPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app-radiostack.json");
                if (System.IO.File.Exists(configPath))
                {
                    string json = System.IO.File.ReadAllText(configPath);
                    if (json.Contains("\"Bluetooth\"")) BluetoothDetected = true;
                    if (json.Contains("\"Wifi\"")) WifiDetected = true;
                    
                    // If they are detected via snapshot but missing from WinRT, they are explicitly disabled
                    if (_bluetoothRadio == null && BluetoothDetected) BluetoothEnabled = false;
                    if (_wifiRadio == null && WifiDetected) WifiEnabled = false;
                }
            }
            catch { }

            SyncFromRadios();
        }

        private void OnRadioStateChanged(Radio sender, object args)
        {
            _dispatcher?.TryEnqueue(SyncFromRadios);
        }

        /// <summary>Mirrors the hardware state without re-triggering the toggle handlers.</summary>
        private void SyncFromRadios()
        {
            _syncing = true;
            try
            {
                if (_bluetoothRadio != null && BluetoothDetected)
                {
                    // Do not snap back to On if we forcefully disabled it and state is ghosting.
                    if (!(_applying && !BluetoothEnabled))
                        BluetoothEnabled = _bluetoothRadio.State == RadioState.On;
                }
                if (_wifiRadio != null && WifiDetected)
                {
                    if (!(_applying && !WifiEnabled))
                        WifiEnabled = _wifiRadio.State == RadioState.On;
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        private async Task ToggleDeepAsync(RadioKind kind, bool enable, string name)
        {
            if (_applying) return;

            _applying = true;
            IsBusy = true;
            try
            {
                var result = enable
                    ? await _radioStack.EnableAsync(kind)
                    : await _radioStack.DisableAsync(kind);

                // Best-effort: bring the radio API in line with the deep change.
                var radio = kind == RadioKind.Bluetooth ? _bluetoothRadio : _wifiRadio;
                if (radio != null)
                {
                    try
                    {
                        await radio.SetStateAsync(enable ? RadioState.On : RadioState.Off);
                    }
                    catch { }
                }

                StatusText = result.Success ? string.Empty : result.Detail;
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to {(enable ? "enable" : "disable")} {name}: {ex.Message}";
            }
            finally
            {
                // Defer the final aggressive sync flag so ghost state events finish triggering
                await Task.Delay(2000);
                _applying = false;
                IsBusy = false;
                
                // Only sync from radios if the disable wasn't executed, avoiding loop snaps
                if (enable) SyncFromRadios();
            }
        }
    }
}

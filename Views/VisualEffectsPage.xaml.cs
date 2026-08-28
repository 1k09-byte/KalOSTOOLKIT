using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;

namespace KalOS.Views
{
    public sealed partial class VisualEffectsPage : Page
    {
        private bool _updatingPresets;
        private bool _loaded;
        private bool _refreshing;
        private readonly Dictionary<string, bool> _effects = new();

        public VisualEffectsPage()
        {
            this.InitializeComponent();
            Loaded += (_, _) => LoadFromSystem();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
            else if (App.Current is App { MainWindow: MainWindow window })
            {
                window.NavigateToPage(typeof(PersonalizationPage));
            }
            else
            {
                Frame.Navigate(typeof(PersonalizationPage));
            }
        }

        private void Preset_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingPresets || !_loaded || sender is not RadioButton { Tag: string tag })
            {
                return;
            }

            switch (tag)
            {
                case "BestAppearance":
                    SetAllEffects(true);
                    break;
                case "BestPerformance":
                    SetAllEffects(false);
                    break;
                case "Recommended":
                    ApplyRecommended();
                    break;
                case "Default":
                    ApplyWindowsDefault();
                    break;
            }

            // Apply immediately when the user switches presets.
            if (tag != "Custom")
            {
                ApplySettings();
            }
        }

        private void Effect_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingPresets || !_loaded)
            {
                return;
            }

            if (CustomRadio != null && CustomRadio.IsChecked != true)
            {
                _updatingPresets = true;
                CustomRadio.IsChecked = true;
                _updatingPresets = false;
            }

            ApplySettings();
        }

        private void RefreshFromWindows()
        {
            if (_refreshing || !_loaded) return;
            _refreshing = true;
            _updatingPresets = true;
            foreach (var (name, toggle) in GetEffectToggles())
            {
                if (SystemEffects.Map.TryGetValue(name, out var effect))
                {
                    try
                    {
                        var value = effect.Get();
                        _effects[name] = value;
                        toggle.IsOn = value;
                    }
                    catch { }
                }
            }
            _updatingPresets = false;
            _refreshing = false;
            UpdatePresetSelection();
        }

        private void UpdatePresetSelection()
        {
            _updatingPresets = true;
            if (AreAll(false)) BestPerformanceRadio.IsChecked = true;
            else if (AreAll(true)) BestAppearanceRadio.IsChecked = true;
            else CustomRadio.IsChecked = true;
            _updatingPresets = false;
        }

        private IEnumerable<(string name, ToggleSwitch toggle)> GetEffectToggles()
        {
            foreach (var child in EffectsListPanel.Children)
            {
                if (child is SettingsCard card)
                {
                    var toggle = GetToggleFromCard(card);
                    var name = card.Header?.ToString() ?? "";
                    if (toggle != null && !string.IsNullOrEmpty(name))
                    {
                        yield return (name, toggle);
                    }
                }
            }
        }

        private static ToggleSwitch? GetToggleFromCard(SettingsCard card)
        {
            if (card.Content is ToggleSwitch direct) return direct;
            // Fallback: search visual tree for ToggleSwitch
            return FindDescendant<ToggleSwitch>(card);
        }

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var found = FindDescendant<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void SetAllEffects(bool value)
        {
            _updatingPresets = true;
            foreach (var (_, toggle) in GetEffectToggles())
            {
                toggle.IsOn = value;
            }
            _updatingPresets = false;
        }

        private static readonly string[] RecommendedEffects =
        {
            "Animate controls and elements inside windows",
            "Animate windows when minimizing and maximizing",
            "Animations in the taskbar",
            "Enable Peek",
            "Fade or slide menus into view",
            "Show shadows under mouse pointer",
            "Show shadows under windows",
            "Show thumbnails instead of icons",
            "Show translucent selection rectangle",
            "Show window contents while dragging",
            "Smooth edges of screen fonts",
            "Use drop shadows for icon labels on the desktop",
        };

        private void ApplyRecommended()
        {
            _updatingPresets = true;
            foreach (var (name, toggle) in GetEffectToggles())
            {
                toggle.IsOn = RecommendedEffects.Contains(name);
            }
            _updatingPresets = false;
        }

        private void ApplyWindowsDefault()
        {
            _updatingPresets = true;
            var defaults = new Dictionary<string, bool>
            {
                ["Animate controls and elements inside windows"] = false,
                ["Animate windows when minimizing and maximizing"] = true,
                ["Animations in the taskbar"] = true,
                ["Enable Peek"] = true,
                ["Fade or slide menus into view"] = true,
                ["Fade or slide ToolTips into view"] = true,
                ["Fade out menu items after clicking"] = true,
                ["Save taskbar thumbnail previews"] = true,
                ["Show shadows under mouse pointer"] = true,
                ["Show shadows under windows"] = true,
                ["Show thumbnails instead of icons"] = true,
                ["Show translucent selection rectangle"] = true,
                ["Show window contents while dragging"] = true,
                ["Slide open combo boxes"] = false,
                ["Smooth edges of screen fonts"] = true,
                ["Smooth-scroll list boxes"] = true,
                ["Use drop shadows for icon labels on the desktop"] = false,
            };

            foreach (var (name, toggle) in GetEffectToggles())
            {
                if (defaults.TryGetValue(name, out var value))
                {
                    toggle.IsOn = value;
                }
            }
            _updatingPresets = false;
        }

        private void LoadFromSystem()
        {
            _updatingPresets = true;
            foreach (var (name, toggle) in GetEffectToggles())
            {
                if (SystemEffects.Map.TryGetValue(name, out var effect))
                {
                    try
                    {
                        var value = effect.Get();
                        _effects[name] = value;
                        toggle.IsOn = value;
                    }
                    catch { }
                }
            }
            _updatingPresets = false;
            _loaded = true;

            UpdatePresetSelection();
        }

        private bool AreAll(bool value) =>
            GetEffectToggles().All(t => t.toggle.IsOn == value);

        private void ApplySettings()
        {
            try
            {
                foreach (var (name, toggle) in GetEffectToggles())
                {
                    if (SystemEffects.Map.TryGetValue(name, out var effect))
                    {
                        var on = toggle.IsOn;
                        _effects[name] = on;
                        effect.Set(on);
                    }
                }

                // Keep Windows' master UI-effects switch enabled. Disabling it
                // when just one checkbox is off makes Windows report a state
                // different from the individual Visual Effects checkboxes.
                Native.SetBool(Native.SPI_SETUIEFFECTS, true);
                SaveVisualEffectsMask();

                StatusText.Text = "Settings applied.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to apply: {ex.Message}";
            }
        }

        private void SaveVisualEffectsMask()
        {
            var mask = 0;
            foreach (var value in _effects.Values)
            {
                if (value) mask++;
            }
            // Force Windows to flush the per-user visual-effects state and
            // notify Explorer; the individual SPI/registry writes above are
            // the source of truth, this refresh prevents restart reversion.
            Native.SetBool(Native.SPI_SETUIEFFECTS, true);
            Native.BroadcastSettingChange();
        }

        private static class Native
        {
            public const uint SPI_SETANIMATION = 0x0049;
            public const uint SPI_GETANIMATION = 0x0048;
            public const uint SPI_SETDRAGFULLWINDOWS = 0x0025;
            public const uint SPI_GETDRAGFULLWINDOWS = 0x0024;
            public const uint SPI_SETFONTSMOOTHING = 0x004B;
            public const uint SPI_SETMENUANIMATION = 0x1003;
            public const uint SPI_GETMENUANIMATION = 0x1002;
            public const uint SPI_SETCOMBOBOXANIMATION = 0x1005;
            public const uint SPI_GETCOMBOBOXANIMATION = 0x1004;
            public const uint SPI_SETLISTBOXSMOOTHSCROLLING = 0x1007;
            public const uint SPI_GETLISTBOXSMOOTHSCROLLING = 0x1006;
            public const uint SPI_SETMENUFADE = 0x1015;
            public const uint SPI_GETMENUFADE = 0x1014;
            public const uint SPI_SETTOOLTIPANIMATION = 0x1019;
            public const uint SPI_GETTOOLTIPANIMATION = 0x1018;
            public const uint SPI_SETCURSORSHADOW = 0x101F;
            public const uint SPI_GETCURSORSHADOW = 0x101E;
            public const uint SPI_SETDROPSHADOW = 0x1025;
            public const uint SPI_GETDROPSHADOW = 0x1024;
            public const uint SPI_SETDISABLEOVERLAPPEDCONTENT = 0x1041;
            public const uint SPI_GETDISABLEOVERLAPPEDCONTENT = 0x1040;
            public const uint SPI_SETCLIENTAREAANIMATION = 0x1043;
            public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
            public const uint SPI_SETUIEFFECTS = 0x104B;
            public const uint SPI_GETUIEFFECTS = 0x104A;
            public const uint SPIF_UPDATEINIFILE = 0x01;
            public const uint SPIF_SENDCHANGE = 0x02;

            [StructLayout(LayoutKind.Sequential)]
            private struct ANIMATIONINFO
            {
                public uint cbSize;
                public int iMinAnimate;
            }

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

            public static void BroadcastSettingChange()
            {
                SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "UserPreferencesMask", 0x0002, 1000, out _);
            }

            public static bool SetBool(uint spiSet, bool value) =>
                SystemParametersInfo(spiSet, 0, (IntPtr)(value ? 1 : 0), SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

            public static bool GetBool(uint spiGet)
            {
                var ptr = Marshal.AllocHGlobal(4);
                try
                {
                    return SystemParametersInfo(spiGet, 0, ptr, 0) && Marshal.ReadInt32(ptr) != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }

            public static bool GetMinAnimate()
            {
                var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<ANIMATIONINFO>());
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SystemParametersInfo(SPI_GETANIMATION, info.cbSize, ptr, 0))
                    {
                        return true;
                    }
                    return Marshal.PtrToStructure<ANIMATIONINFO>(ptr).iMinAnimate != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }

            public static void SetMinAnimate(bool value)
            {
                var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = value ? 1 : 0 };
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<ANIMATIONINFO>());
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    SystemParametersInfo(SPI_SETANIMATION, info.cbSize, ptr, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }

        private static class Reg
        {
            public static bool GetInt(string path, string name, bool fallback) =>
                Registry.GetValue(path, name, fallback ? 1 : 0) is int v ? v == 1 : fallback;

            public static void SetInt(string path, string name, bool value) =>
                Registry.SetValue(path, name, value ? 1 : 0);

            public static bool GetFontSmoothing() =>
                Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "FontSmoothing", "2")?.ToString() == "2";

            public static void SetFontSmoothing(bool value) =>
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "FontSmoothing", value ? "2" : "0");

            public static bool GetUserMaskBit(int byteIndex, byte mask, bool fallback)
            {
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "UserPreferencesMask", null) is not byte[] bytes ||
                    bytes.Length <= byteIndex)
                {
                    return fallback;
                }
                return (bytes[byteIndex] & mask) != 0;
            }

            public static void SetUserMaskBit(int byteIndex, byte mask, bool value)
            {
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "UserPreferencesMask", null) is not byte[] bytes ||
                    bytes.Length <= byteIndex)
                {
                    return;
                }
                var copy = (byte[])bytes.Clone();
                if (value) { copy[byteIndex] |= mask; }
                else { copy[byteIndex] &= unchecked((byte)~mask); }
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "UserPreferencesMask", copy);
            }
        }

        private sealed class Effect
        {
            public Func<bool> Get { get; init; } = () => true;
            public Action<bool> Set { get; init; } = _ => { };
        }

        private static class SystemEffects
        {
            private const string Desktop = @"HKEY_CURRENT_USER\Control Panel\Desktop";
            private const string Metrics = @"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics";
            private const string Dwm = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM";
            private const string VisualEffects = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
            private const string Advanced = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

            public static readonly Dictionary<string, Effect> Map = new()
            {
                ["Animate controls and elements inside windows"] = new Effect
                {
                    Get = () => Native.GetBool(Native.SPI_GETCLIENTAREAANIMATION),
                    Set = v => Native.SetBool(Native.SPI_SETCLIENTAREAANIMATION, v),
                },
                ["Animate windows when minimizing and maximizing"] = new Effect
                {
                    Get = () => Native.GetMinAnimate(),
                    Set = v => Native.SetMinAnimate(v),
                },
                ["Animations in the taskbar"] = new Effect
                {
                    Get = () => Reg.GetInt(VisualEffects, "TaskbarAnimations", true),
                    Set = v => Reg.SetInt(VisualEffects, "TaskbarAnimations", v),
                },
                ["Enable Peek"] = new Effect
                {
                    Get = () => Reg.GetInt(Advanced, "DisablePreviewDesktop", false) == false,
                    Set = v => Reg.SetInt(Advanced, "DisablePreviewDesktop", !v),
                },
                ["Fade or slide menus into view"] = new Effect
                {
                    Get = () => Native.GetBool(Native.SPI_GETMENUANIMATION),
                    Set = v => Native.SetBool(Native.SPI_SETMENUANIMATION, v),
                },
                ["Fade or slide ToolTips into view"] = new Effect
                {
                    Get = () => Native.GetBool(Native.SPI_GETTOOLTIPANIMATION),
                    Set = v => Native.SetBool(Native.SPI_SETTOOLTIPANIMATION, v),
                },
                ["Fade out menu items after clicking"] = new Effect
                {
                    Get = () => Native.GetBool(Native.SPI_GETMENUFADE),
                    Set = v => Native.SetBool(Native.SPI_SETMENUFADE, v),
                },
                ["Save taskbar thumbnail previews"] = new Effect
                {
                    Get = () => !Reg.GetInt(Dwm, "AlwaysHibernateThumbnails", false),
                    Set = v => Reg.SetInt(Dwm, "AlwaysHibernateThumbnails", !v),
                },
                ["Show shadows under mouse pointer"] = new Effect
                {
                    Get = () => Native.GetBool(Native.SPI_GETCURSORSHADOW),
                    Set = v => Native.SetBool(Native.SPI_SETCURSORSHADOW, v),
                },
                ["Show shadows under windows"] = new Effect
                {
                    Get = () => Native.GetBool(Native.SPI_GETDROPSHADOW),
                    Set = v => Native.SetBool(Native.SPI_SETDROPSHADOW, v),
                },
                ["Show thumbnails instead of icons"] = new Effect
                {
                    Get = () => !Reg.GetInt(Advanced, "IconsOnly", false),
                    Set = v => Reg.SetInt(Advanced, "IconsOnly", !v),
                },
                ["Show translucent selection rectangle"] = new Effect
                {
                    // Bit 0x80 of UserPreferencesMask byte 2 controls selection fade.
                    Get = () => Reg.GetUserMaskBit(2, 0x80, true),
                    Set = v => Reg.SetUserMaskBit(2, 0x80, v),
                },
                ["Show window contents while dragging"] = new Effect
                {
                    Get = () => Native.GetBool(Native.SPI_GETDRAGFULLWINDOWS),
                    Set = v => Native.SetBool(Native.SPI_SETDRAGFULLWINDOWS, v),
                },
                ["Slide open combo boxes"] = new Effect
                {
                    Get = () => Native.GetBool(Native.SPI_GETCOMBOBOXANIMATION),
                    Set = v => Native.SetBool(Native.SPI_SETCOMBOBOXANIMATION, v),
                },
                ["Smooth edges of screen fonts"] = new Effect
                {
                    Get = () => Reg.GetFontSmoothing(),
                    Set = v => { Reg.SetFontSmoothing(v); Native.SetBool(Native.SPI_SETFONTSMOOTHING, v); },
                },
                ["Smooth-scroll list boxes"] = new Effect
                {
                    Get = () => Native.GetBool(Native.SPI_GETLISTBOXSMOOTHSCROLLING),
                    Set = v => Native.SetBool(Native.SPI_SETLISTBOXSMOOTHSCROLLING, v),
                },
                ["Use drop shadows for icon labels on the desktop"] = new Effect
                {
                    Get = () => Reg.GetInt(Advanced, "ListviewShadow", true),
                    Set = v => Reg.SetInt(Advanced, "ListviewShadow", v),
                },
            };
        }
    }
}

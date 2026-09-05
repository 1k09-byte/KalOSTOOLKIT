using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using KaliteKit.Helpers;
using Microsoft.UI.Dispatching;

namespace KaliteKit.Services
{
    /// <summary>Persisted behavior toggles (app-behavior.json).</summary>
    public sealed class BehaviorConfig
    {
        /// <summary>When true, closing the window (X) hides KaliteKit to the system tray instead of exiting.</summary>
        public bool RunInBackground { get; set; } = TrayIconService.DefaultRunInBackground;
    }

    /// <summary>
    /// Owns the system-tray icon (Shell_NotifyIcon), the persisted
    /// "run in background" behavior, and the hide/restore window flow.
    ///
    /// Close semantics: when <see cref="RunInBackground"/> is on, pressing X
    /// hides the window to the tray (the process keeps running — sticky
    /// rules keep applying in-process); when off, X exits the app as before.
    ///
    /// Design notes (this file replaces an earlier custom-WinUI-window menu
    /// that crashed the runtime — .NET Runtime event 1023, 0x80131506 —
    /// because it created WinUI windows inside the native WndProc):
    ///   * The tray callback WndProc does NO WinUI work. Every real action
    ///     is marshaled onto the UI thread's DispatcherQueue.
    ///   * The context menu is a NATIVE Win32 popup menu — the canonical
    ///     shell pattern (SetForegroundWindow → TrackPopupMenuEx(TPM_RETURNCMD)
    ///     → PostMessage WM_NULL, per MSDN KB135788). It is modal, blocks its
    ///     own message pump, and its return value IS the selection — no
    ///     callback races, no foreground hacks.
    ///   * The menu is rendered dark via the uxtheme dark-mode entry points
    ///     (SetPreferredAppMode / FlushMenuThemes) resolved dynamically, so a
    ///     future Windows change degrades to a light menu instead of crashing.
    ///   * The tray icon exists only while the window is hidden.
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        /// <summary>Default for fresh installs: X exits the app (opt-in behavior).</summary>
        public const bool DefaultRunInBackground = false;

        private const string ConfigFile = "app-behavior.json";
        private const uint WM_TRAYICON = 0x8000; // WM_APP
        private const uint WM_SHOWWINDOW = 0x0018;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONUP = 0x0205;

        private static readonly uint WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

        // Menu command IDs (low word of TrackPopupMenuEx's TPM_RETURNCMD result).
        private const int MenuCmdOpen = 1;
        private const int MenuCmdExit = 2;

        private BehaviorConfig _config;
        private readonly IntPtr _hwnd;
        private readonly uint _callbackMessage;
        private readonly IntPtr _iconHandle;
        private readonly WndProcDelegate _wndProcDelegate;
        private readonly DispatcherQueue? _dispatcherQueue;
        private IntPtr _oldWndProc;
        private bool _trayAdded;
        private bool _disposed;

        /// <summary>Raised after the window is restored from the tray (re-focus, foreground).</summary>
        public event EventHandler? WindowRestored;

        /// <summary>Whether X currently hides to tray instead of exiting.</summary>
        public bool RunInBackground => _config.RunInBackground;

        public TrayIconService(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _config = LoadConfig();
            _iconHandle = LoadIconFromApp();
            _callbackMessage = WM_TRAYICON;

            // Captured on the UI thread that owns the main window. Tray
            // callbacks arrive inside the native WndProc — doing ANY WinUI
            // work there reenters the XAML core from a native callback and
            // terminates the runtime (0x80131506). Every real action is
            // therefore marshaled onto this dispatcher queue.
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            // Subclass the window so we receive the tray callback messages.
            _wndProcDelegate = WndProc;
            _oldWndProc = SetWindowLongPtr(_hwnd, GWL_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        }

        /// <summary>Handle an AppWindow.Closing event. Returns true when the close was converted to hide-to-tray.</summary>
        public bool HandleClosingRequest()
        {
            if (!RunInBackground)
                return false; // real close — caller continues teardown/exit

            HideToTray();
            return true;
        }

        /// <summary>Hide the main window and show the tray icon.</summary>
        public void HideToTray()
        {
            if (_trayAdded) return;

            ShowTrayIcon("KaliteKit — running in background (double-click to restore)");
            ShowWindow(_hwnd, SW_HIDE);
        }

        /// <summary>Restore the window, focus it, and remove the tray icon.</summary>
        public void RestoreFromTray()
        {
            if (!_trayAdded) return;
            RemoveTrayIcon();
            ShowWindow(_hwnd, SW_RESTORE);
            SetForegroundWindow(_hwnd);
            WindowRestored?.Invoke(this, EventArgs.Empty);
        }

        public bool IsHiddenToTray => _trayAdded;

        /// <summary>Persist the toggle. Turning it off while hidden restores the window.</summary>
        public async Task SetRunInBackgroundAsync(bool enabled)
        {
            _config.RunInBackground = enabled;
            try
            {
                Directory.CreateDirectory(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaliteKit", "Configs"));
                await JsonConfigHelper.SaveAsync(ConfigFile, _config);
            }
            catch { /* config write failures must not break the toggle */ }

            if (!enabled && _trayAdded)
                RestoreFromTray();
        }

        private static BehaviorConfig LoadConfig()
        {
            try
            {
                var cfg = JsonConfigHelper.LoadSync<BehaviorConfig>(ConfigFile);
                return cfg ?? new BehaviorConfig();
            }
            catch
            {
                return new BehaviorConfig();
            }
        }

        private static IntPtr LoadIconFromApp()
        {
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (File.Exists(iconPath))
                {
                    var h = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
                    if (h != IntPtr.Zero) return h;
                }
            }
            catch { }
            return LoadIcon(GetModuleHandle(null), "IDI_APPLICATION");
        }

        // ── Window procedure subclass ───────────────────────────────────

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TASKBARCREATED && _trayAdded)
            {
                // Explorer restarted → re-add the icon.
                RemoveTrayIcon();
                ShowTrayIcon("KaliteKit — running in background (double-click to restore)");
                return DefWindowProc(hwnd, msg, wParam, lParam);
            }

            if (msg == WM_TRAYICON)
            {
                var mouse = (uint)lParam.ToInt64() & 0xFFFF;
                if (mouse == WM_LBUTTONDBLCLK)
                {
                    EnqueueOnUi(RestoreFromTray);
                    return IntPtr.Zero;
                }
                if (mouse == WM_RBUTTONUP)
                {
                    EnqueueOnUi(ShowContextMenu);
                    return IntPtr.Zero;
                }
            }

            if (msg == WM_SHOWWINDOW && wParam != IntPtr.Zero && _trayAdded)
            {
                // The window became visible through an external path (e.g. a
                // second launch waking it) — keep tray state consistent.
                RemoveTrayIcon();
            }

            return CallWindowProc(_oldWndProc, hwnd, msg, wParam, lParam);
        }

        /// <summary>
        /// Run <paramref name="action"/> on the UI thread. WinUI work is
        /// FORBIDDEN inside the WndProc callback itself: creating or showing
        /// windows from a native window-procedure callback reenters the XAML
        /// core and terminates the process (0x80131506).
        /// </summary>
        private void EnqueueOnUi(Action action)
        {
            if (_dispatcherQueue is null)
            {
                action(); // no dispatcher captured — best effort inline
                return;
            }
            _dispatcherQueue.TryEnqueue(() => action());
        }

        /// <summary>
        /// Show the native context menu near the tray cursor. Called on the
        /// UI thread (via <see cref="EnqueueOnUi"/>) — TrackPopupMenuEx runs
        /// its own modal message pump, which is the canonical shell pattern
        /// and safe here (the main window is hidden; no XAML interaction is
        /// pending while the menu is open).
        /// </summary>
        private void ShowContextMenu()
        {
            if (!GetCursorPos(out var pt))
                return;

            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
                return;

            try
            {
                AppendMenu(menu, MF_STRING, (UIntPtr)MenuCmdOpen, "Open KaliteKit");
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenu(menu, MF_STRING, (UIntPtr)MenuCmdExit, "Exit");

                ApplyDarkMenuTheme();

                // MSDN KB135788 — the canonical tray-menu recipe:
                //   1. SetForegroundWindow so the menu dismisses on outside click,
                //   2. TrackPopupMenuEx with TPM_RETURNCMD (returns the selection),
                //   3. PostMessage WM_NULL so the taskbar cleans up its menu state.
                SetForegroundWindow(_hwnd);
                int cmd = TrackPopupMenuEx(menu,
                    TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                    pt.X, pt.Y, _hwnd, IntPtr.Zero);
                PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

                if (cmd == MenuCmdOpen)
                {
                    RestoreFromTray();
                }
                else if (cmd == MenuCmdExit)
                {
                    ExitApplication();
                }
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        /// <summary>
        /// Render the Win32 menu dark. Uses the uxtheme dark-mode entry points
        /// the shell itself uses for dark context menus, resolved by ordinal at
        /// run time (undocumented but stable for years — used by ExplorerPatcher,
        /// TranslucentTB et al). Any failure degrades to a standard light menu.
        /// </summary>
        private static void ApplyDarkMenuTheme()
        {
            try
            {
                var uxtheme = GetModuleHandle("uxtheme.dll");
                if (uxtheme == IntPtr.Zero) return;

                var setPreferredAppMode = Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeDelegate>(
                    GetProcAddress(uxtheme, MakeIntResource(135)));
                var flushMenuThemes = Marshal.GetDelegateForFunctionPointer<FlushMenuThemesDelegate>(
                    GetProcAddress(uxtheme, MakeIntResource(136)));
                var shouldSystemUseDarkMode = Marshal.GetDelegateForFunctionPointer<ShouldSystemUseDarkModeDelegate>(
                    GetProcAddress(uxtheme, MakeIntResource(138)));

                if (shouldSystemUseDarkMode is null || !shouldSystemUseDarkMode())
                    return; // system is light — leave the menu light too

                // 1 = AllowDark; 3 = ForceDark. ForceDark keeps the menu dark
                // even when the (hidden) app's own theme resolution is light.
                setPreferredAppMode?.Invoke(3);
                flushMenuThemes?.Invoke();
            }
            catch
            {
                // Theme resolution is cosmetic — never fail the menu over it.
            }
        }

        private static string? MakeIntResource(int id) =>
            id >= 0 && id <= 65535 ? "#" + id : null;

        private delegate int SetPreferredAppModeDelegate(int mode); // PreferredAppMode
        private delegate void FlushMenuThemesDelegate();
        private delegate bool ShouldSystemUseDarkModeDelegate();

        private void ExitApplication()
        {
            RemoveTrayIcon();
            Microsoft.UI.Xaml.Application.Current.Exit();
            // WinUI's Exit can be asynchronous (and in some unpackaged hosts
            // a no-op), which would present as a dead Exit button. Cleanup is
            // already done (tray removed, config saved), so guarantee it.
            Environment.Exit(0);
        }

        // ── Shell_NotifyIcon ────────────────────────────────────────────

        private void ShowTrayIcon(string tooltip)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = _callbackMessage,
                hIcon = _iconHandle,
                szTip = tooltip,
            };
            Shell_NotifyIcon(NIM_ADD, ref nid);
            _trayAdded = true;
        }

        private void RemoveTrayIcon()
        {
            if (!_trayAdded) return;
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _trayAdded = false;
        }

        // ── Interop ─────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        }

        private const uint NIM_ADD = 0;
        private const uint NIM_DELETE = 2;
        private const uint NIF_MESSAGE = 0x1;
        private const uint NIF_ICON = 0x2;
        private const uint NIF_TIP = 0x4;
        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x10;
        private const int GWL_WNDPROC = -4;
        private const int SW_HIDE = 0;
        private const int SW_RESTORE = 9;

        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_NONOTIFY = 0x0080;
        private const uint WM_NULL = 0x0000;

        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadIcon(IntPtr hInstance, string lpIconName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string? procName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RemoveTrayIcon();
            if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
            if (_oldWndProc != IntPtr.Zero) SetWindowLongPtr(_hwnd, GWL_WNDPROC, _oldWndProc);
        }
    }
}

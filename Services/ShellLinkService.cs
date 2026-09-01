using System;
using System.Diagnostics;

namespace KalOS.Services
{
    /// <summary>
    /// Shortcut creation and taskbar pinning through Windows Shell COM — the
    /// native equivalent of what <c>install-kalos.ps1</c> does with
    /// <c>WScript.Shell</c> and shell verbs. All operations are best-effort:
    /// a failure returns false instead of throwing, because a missing
    /// shortcut must never fail an otherwise-successful install.
    /// </summary>
    public static class ShellLinkService
    {
        private static readonly string StartMenuDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");

        /// <summary>
        /// Creates the Start-Menu and Desktop shortcuts for the app.
        /// Returns true when at least one shortcut was written.
        /// </summary>
        public static bool CreateAppShortcuts(string targetPath, string workingDir, string description)
        {
            bool any = false;
            any |= TryCreateShortcut(System.IO.Path.Combine(StartMenuDir, "KalOS.lnk"), targetPath, workingDir, description);

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop))
            {
                desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            }
            if (!string.IsNullOrEmpty(desktop))
            {
                any |= TryCreateShortcut(System.IO.Path.Combine(desktop, "KalOS.lnk"), targetPath, workingDir, description);
            }
            return any;
        }

        /// <summary>
        /// Best-effort taskbar pin through the shell's "Pin to taskbar" verb,
        /// skipped entirely when Open-Shell is installed (its Start menu owns
        /// pinning and the standard verb misfires).
        /// </summary>
        public static bool TryPinToTaskbar(string targetPath)
        {
            try
            {
                if (Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"Software\OpenShell\StartMenu") is not null)
                {
                    Debug.WriteLine("ShellLink: Open-Shell detected — taskbar pin skipped.");
                    return false;
                }

                return TryInvokeVerb(targetPath, "pin to taskbar")
                    || TryInvokeVerb(System.IO.Path.Combine(StartMenuDir, "KalOS.lnk"), "pin to taskbar");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Writes a .lnk via WScript.Shell COM. Returns false on any failure.</summary>
        public static bool TryCreateShortcut(string linkPath, string targetPath, string workingDir, string description)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null) return false;

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic link = shell.CreateShortcut(linkPath);
                link.TargetPath = targetPath;
                link.WorkingDirectory = workingDir;
                link.Description = description;
                link.Save();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Invokes a shell context-menu verb whose name contains <paramref name="pattern"/>.</summary>
        private static bool TryInvokeVerb(string targetPath, string pattern)
        {
            try
            {
                if (string.IsNullOrEmpty(targetPath) || !System.IO.File.Exists(targetPath)) return false;

                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null) return false;

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic folder = shell.Namespace(System.IO.Path.GetDirectoryName(targetPath));
                if (folder is null) return false;
                dynamic item = folder.ParseName(System.IO.Path.GetFileName(targetPath));
                if (item is null) return false;

                foreach (dynamic verb in item.Verbs())
                {
                    string name = (string)verb.Name;
                    if (name is not null && name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        verb.DoIt();
                        return true;
                    }
                }
            }
            catch
            {
                // Pinning is unsupported on some shell configurations.
            }
            return false;
        }
    }
}

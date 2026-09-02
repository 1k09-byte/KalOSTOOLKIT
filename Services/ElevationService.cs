using System;
using System.Diagnostics;
using System.Security.Principal;

namespace KalOS.Services
{
    public class ElevationService
    {
        public bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public void ShowElevationPrompt()
        {
            var exePath = Environment.ProcessPath;
            if (exePath == null) return;
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            try
            {
                Process.Start(startInfo);
            }
            catch
            {
            }
            // Deferred (one dispatcher pass later) so the UI thread unwinds
            // before this instance exits. The exit goes through the XAML
            // runtime (Application.Exit → PostQuitMessage) instead of
            // Environment.Exit: killing the process directly while window
            // teardown is still unwinding raises the native 0xc0000005
            // hard-error box on machines with Windows Error Reporting disabled.
            try
            {
                var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                if (queue is not null && queue.TryEnqueue(() =>
                    {
                        try { Microsoft.UI.Xaml.Application.Current?.Exit(); } catch { }
                    }))
                    return;
            }
            catch { /* no dispatcher on this thread — exit directly below */ }
            // No XAML dispatcher on this thread — nothing is being torn down.
            Environment.Exit(0);
        }
    }
}

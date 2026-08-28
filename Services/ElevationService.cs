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
            Environment.Exit(0);
        }
    }
}

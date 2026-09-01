using System;
using System.IO;

namespace KalOS.Setup
{
    /// <summary>
    /// Tracks whether the one-time KalOS setup (the wizard) has run on this
    /// machine. When the wizard is embedded in the main app, the app opens
    /// straight into the setup wizard until the install pipeline completes;
    /// the marker file is what flips it into the consumer app afterwards.
    /// The standalone Setup wizard writes the same marker (harmless there).
    /// </summary>
    public static class SetupState
    {
        /// <summary>True when the wizard window is hosted inside the main app
        /// instead of running as the standalone KalOS.Setup.exe. Used only to
        /// pick a friendlier window title ("KalOS Setup" vs "KalOS Installer").</summary>
        public static bool Embedded { get; set; }

        /// <summary>
        /// Set by the host app in embedded mode: invoked when the wizard wants
        /// to close after a completed setup, so the host can swap into the
        /// consumer UI instead of letting the process exit. WinUI's
        /// <c>Window.Close()</c> bypasses <c>AppWindow.Closing</c>, so the host
        /// cannot intercept this itself — it registers here instead.
        /// </summary>
        public static Action? EmbeddedCloseHandler { get; set; }

        public static string MarkerPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KalOS", "setup.complete");

        public static bool IsSetupComplete
        {
            get { try { return File.Exists(MarkerPath); } catch { return false; } }
        }

        /// <summary>Records that the wizard finished. Never throws.</summary>
        public static void MarkComplete()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                File.WriteAllText(MarkerPath,
                    $"KalOS setup completed {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            }
            catch
            {
                // A missing marker only means the wizard shows again next
                // launch (or the user can pass --setup) — never crash.
            }
        }

        /// <summary>Forgets the marker so the next launch opens the wizard
        /// again. Used by the --setup command line switch.</summary>
        public static void Reset()
        {
            try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { }
        }
    }
}

using System;
using System.IO;
using System.Text.Json;
using KaliteKit.Setup.ViewModels;

namespace KaliteKit.Setup
{
    /// <summary>
    /// Writes the Customize page's choices into the installed consumer app's
    /// data folder so KaliteKit opens already personalized:
    ///
    ///   %LOCALAPPDATA%\KaliteKit\Configs\app-backdrop.json → tint color
    ///   %LOCALAPPDATA%\KaliteKit\settings.json             → background image
    ///
    /// Both paths are hard-coded to the consumer app's home. The installer
    /// never compiles with CONSUMER_BUILD, so <c>UpdateService.AppDataFolder</c>
    /// would resolve to the dev folder (KaliteKit-Dev) — wrong target here.
    /// </summary>
    public static class SetupCustomization
    {
        private static string KaliteKitDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaliteKit");

        private static string BackdropConfigPath => Path.Combine(KaliteKitDataDir, "Configs", "app-backdrop.json");
        private static string SettingsPath => Path.Combine(KaliteKitDataDir, "settings.json");

        /// <summary>
        /// Applies the tint + background image the user picked. Never throws —
        /// returns a human-readable detail string for the progress log.
        /// </summary>
        public static (bool Ok, string Detail) Apply(InstallerViewModel vm)
        {
            try
            {
                var actions = new System.Collections.Generic.List<string>();

                // ── Tint (only when the user touched the palette — an explicit
                //    Default choice clears any previous tint) ────────────────
                if (vm.TintTouched)
                {
                    var backdrop = LoadJson<BackdropConfig>(BackdropConfigPath) ?? new BackdropConfig();
                    // Preserve any previously chosen backdrop material; only the
                    // tint changes here. (Empty Backdrop → the app's default.)
                    backdrop.TintColor = vm.EffectiveTintHex ?? string.Empty;
                    SaveJson(BackdropConfigPath, backdrop);
                    actions.Add(vm.EffectiveTintHex is { } tintHex
                        ? $"tint {tintHex}"
                        : "tint cleared (Default)");
                }

                // ── Background image (an explicit Clear removes it) ────────
                if (vm.BackgroundTouched)
                {
                    var settings = LoadJson<InstallerSettings>(SettingsPath) ?? new InstallerSettings();

                    if (vm.HasBackgroundImage)
                    {
                        string ext = Path.GetExtension(vm.BackgroundImagePath);
                        if (string.IsNullOrEmpty(ext)) ext = ".png";

                        string bgDir = Path.Combine(KaliteKitDataDir, "Backgrounds");
                        Directory.CreateDirectory(bgDir);
                        string dest = Path.Combine(bgDir, "background" + ext.ToLowerInvariant());
                        File.Copy(vm.BackgroundImagePath, dest, overwrite: true);

                        settings.BackgroundImagePath = dest;
                        actions.Add("background image copied to KaliteKit's data folder");
                    }
                    else
                    {
                        settings.BackgroundImagePath = string.Empty;
                        actions.Add("background image cleared");
                    }

                    SaveJson(SettingsPath, settings);
                }

                if (actions.Count == 0)
                {
                    return (true, "No customization chosen — skipped.");
                }
                return (true, "Applied: " + string.Join("; ", actions) + ".");
            }
            catch (Exception ex)
            {
                return (false, $"Could not apply customization: {ex.Message}");
            }
        }

        private static T? LoadJson<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static void SaveJson(string path, object data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(data));
        }

        /// <summary>Mirrors the app's BackdropService.BackdropConfig JSON shape.</summary>
        private sealed class BackdropConfig
        {
            public string Backdrop { get; set; } = string.Empty;
            public string TintColor { get; set; } = string.Empty;
        }

        /// <summary>Mirrors the app's UpdateSettings JSON shape (defaults match the app).</summary>
        private sealed class InstallerSettings
        {
            public bool AutoCheckForUpdates { get; set; } = true;
            public string BackgroundImagePath { get; set; } = string.Empty;
            public double BackgroundImageOpacity { get; set; } = 0.35;
            public string BackgroundImageFit { get; set; } = "UniformToFill";
            public string BackgroundImageHorizontalAlignment { get; set; } = "Center";
            public string BackgroundImageVerticalAlignment { get; set; } = "Center";
        }
    }
}

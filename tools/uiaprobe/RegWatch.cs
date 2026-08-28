using Microsoft.Win32;

namespace UiaProbe;

/// <summary>
/// Tight-loop registry watcher: samples the theme value of a mod's Settings
/// subkey as fast as possible and prints every transition, so we can see the
/// exact moment a deploy writes it and the engine wipes it.
/// </summary>
public static class RegWatch
{
    public static void Run(string modId, int seconds)
    {
        string basePath = $@"SOFTWARE\Windhawk\Engine\Mods\{modId}";
        string? last = "??UNSET??";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long endMs = seconds * 1000L;
        long reads = 0;

        while (sw.ElapsedMilliseconds < endMs)
        {
            string? theme = null;
            string? lf = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var settingsKey = baseKey.OpenSubKey($@"{basePath}\Settings");
                theme = settingsKey?.GetValue("theme") as string;
                using var modKey = baseKey.OpenSubKey(basePath);
                lf = modKey?.GetValue("LibraryFileName") as string;
                lf = lf?.Replace("windows-11-notification-center-styler_1.6_", "LF=");
            }
            catch { }

            if (theme != last)
            {
                Console.WriteLine($"{sw.ElapsedMilliseconds,7}ms theme=[{theme ?? "<none>"}] {lf} (reads={reads})");
                last = theme;
            }
            reads++;
        }
        Console.WriteLine($"done ({reads} reads in {sw.ElapsedMilliseconds}ms)");
    }
}

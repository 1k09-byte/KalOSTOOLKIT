using System;
using System.IO;
using System.Threading;

namespace KaliteKit.Services;

/// <summary>
/// Pins an application to the Windows taskbar without any NuGet dependency.
///
/// Two mechanisms, both late-bound COM (no package references needed):
///   1. Shell.Application → ParseName(...).InvokeVerb("taskbarpin") — the
///      classic per-user pin verb. Can be refused on locked-down systems.
///   2. WScript.Shell → write a .lnk directly into the "User Pinned\TaskBar"
///      folder, which is exactly the folder Explorer treats as pinned items.
///
/// All COM work runs on a dedicated STA thread (COM is STA-friendly; the
/// thread pool is MTA) with a hard 10 s join so a stuck shell never blocks
/// the caller.
/// </summary>
public static class TaskbarPinHelper
{
    private static readonly string TaskbarPinsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

    /// <summary>Pins the executable to the taskbar. Returns true when a taskbar shortcut now exists.</summary>
    public static bool PinToTaskbar(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;

        try
        {
            Directory.CreateDirectory(TaskbarPinsDir);

            if (IsPinned(exePath)) return true;

            // 1) Shell verb (preferred). Best-effort: it can be refused silently.
            string? pinError = RunOnSta(() =>
            {
                try
                {
                    dynamic shellApp = Activator.CreateInstance(
                        Type.GetTypeFromProgID("Shell.Application")!)!;
                    dynamic folder = shellApp.Namespace(Path.GetDirectoryName(exePath)!);
                    dynamic item = folder.ParseName(Path.GetFileName(exePath));
                    item.InvokeVerb("taskbarpin");
                    return null;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            });

            if (pinError == null && IsPinned(exePath)) return true;

            // 2) Fallback: write the .lnk directly. Explorer's folder watcher
            //    picks it up immediately on Windows 10/11.
            string? lnkError = RunOnSta(() =>
            {
                try
                {
                    string lnkPath = Path.Combine(
                        TaskbarPinsDir,
                        Path.GetFileNameWithoutExtension(exePath) + ".lnk");
                    if (File.Exists(lnkPath)) File.Delete(lnkPath);

                    dynamic shell = Activator.CreateInstance(
                        Type.GetTypeFromProgID("WScript.Shell")!)!;
                    dynamic shortcut = shell.CreateShortcut(lnkPath);
                    shortcut.TargetPath = exePath;
                    shortcut.Save();
                    return null;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            });

            if (lnkError != null)
            {
                System.Diagnostics.Debug.WriteLine($"TaskbarPinHelper fallback failed: {lnkError}");
            }

            return IsPinned(exePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TaskbarPinHelper failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>True when a pinned taskbar shortcut points at the executable.</summary>
    public static bool IsPinned(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !Directory.Exists(TaskbarPinsDir)) return false;

        string target = Path.GetFullPath(exePath);
        foreach (string lnk in Directory.EnumerateFiles(TaskbarPinsDir, "*.lnk"))
        {
            try
            {
                if (string.Equals(GetShortcutTarget(lnk), target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Unreadable shortcut — ignore.
            }
        }
        return false;
    }

    private static string? GetShortcutTarget(string lnkPath)
    {
        return RunOnSta(() =>
        {
            dynamic wsh = Activator.CreateInstance(
                Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = wsh.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath as string ?? string.Empty;
            return target;
        });
    }

    private static T? RunOnSta<T>(Func<T> func)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(10)))
        {
            return default;
        }
        if (error != null) throw error;
        return result;
    }
}
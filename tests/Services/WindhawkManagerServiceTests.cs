using System;
using System.IO;
using System.Runtime.InteropServices;
using KalOS.Services;

namespace KalOS.Tests.Services;

/// <summary>
/// Tests for the Explorer-relaunch plumbing. The key guarantee: processes the
/// app spawns as the "new shell" must NOT run with the app's elevated token —
/// the Windhawk engine runs at the user's normal integrity and cannot inject
/// mods into a High-integrity Explorer (that is why the mods previously only
/// took effect after the user manually restarted the shell).
/// </summary>
public class WindhawkManagerServiceTests
{
    [Fact]
    public void LaunchProcessAsStandardUser_RunsChildWithFilteredToken()
    {
        string outPath = Path.Combine(Path.GetTempPath(), $"kalos_token_probe_{Guid.NewGuid():N}.txt");
        try
        {
            string arguments = $"/c C:\\Windows\\System32\\whoami.exe /groups > \"{outPath}\"";
            bool launched = WindhawkManagerService.LaunchProcessAsStandardUser(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                arguments,
                out int pid,
                out int error,
                out string stage);

            Assert.True(launched, $"launch failed at {stage} with Win32 error {error}");
            Assert.True(pid > 0, "launched child reported no pid");

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(outPath) && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(200);
            }

            Assert.True(File.Exists(outPath), "probe child produced no output — child may not have run");
            string text = File.ReadAllText(outPath);

            // Medium integrity (S-1-16-8192) and NOT High (S-1-16-12288).
            Assert.Contains("S-1-16-8192", text);
            Assert.DoesNotContain("S-1-16-12288", text);

            // If the admin SID is present it must be deny-only, never an
            // enabled group (absent entirely is fine — a non-elevated test host).
            Assert.DoesNotContain("S-1-5-32-544 Mandatory group, Enabled", text);
        }
        finally
        {
            try { File.Delete(outPath); } catch { }
        }
    }
}
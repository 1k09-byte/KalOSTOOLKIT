using System;
using System.IO;
using System.Runtime.InteropServices;
using KaliteKit.Services;

namespace KaliteKit.Tests.Services;

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
        string outPath = Path.Combine(Path.GetTempPath(), $"kalitekit_token_probe_{Guid.NewGuid():N}.txt");
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
            // cmd holds the output file open until it exits, so the read can
            // race the write — retry briefly instead of failing on IOException.
            string text = string.Empty;
            var readDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < readDeadline)
            {
                try { text = File.ReadAllText(outPath); break; }
                catch (IOException) { System.Threading.Thread.Sleep(200); }
            }
            Assert.Contains("S-1-16-8192", text);

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

    [Theory]
    [InlineData("4036_134329178317214255_6460_windows-11-taskbar-styler", "windows-11-taskbar-styler")]
    [InlineData("4036_134329178317214255_6460_translucent-windows", "translucent-windows")]
    public void TryParseModStatusFileName_ParsesRealEngineFileNames(string fileName, string modId)
    {
        bool ok = WindhawkManagerService.TryParseModStatusFileName(fileName, modId, out long sessionPid, out long processPid);

        Assert.True(ok);
        Assert.Equal(4036, sessionPid);
        Assert.Equal(6460, processPid);
    }

    [Theory]
    [InlineData("4036_134329178317214255_6460_windows-11-taskbar-styler", "translucent-windows")] // wrong mod
    [InlineData("4036_134329178317214255_windows-11-taskbar-styler", "windows-11-taskbar-styler")] // missing a field
    [InlineData("notapid_134329178317214255_6460_translucent-windows", "translucent-windows")] // non-numeric session pid
    [InlineData("4036_134329178317214255_notapid_translucent-windows", "translucent-windows")] // non-numeric process pid
    [InlineData("4036_134329178317214255_0_translucent-windows", "translucent-windows")] // pid 0 is not a process
    public void TryParseModStatusFileName_RejectsMalformedNames(string fileName, string modId)
    {
        bool ok = WindhawkManagerService.TryParseModStatusFileName(fileName, modId, out long sessionPid, out long processPid);

        Assert.False(ok);
        Assert.Equal(0, sessionPid);
        Assert.Equal(0, processPid);
    }

    [Fact]
    public void IsProcessAlive_CurrentProcess_IsAlive()
    {
        Assert.True(WindhawkManagerService.IsProcessAlive(Environment.ProcessId, string.Empty));
    }

    [Fact]
    public void IsProcessAlive_BogusPid_IsNotAlive()
    {
        Assert.False(WindhawkManagerService.IsProcessAlive(0, string.Empty));
        Assert.False(WindhawkManagerService.IsProcessAlive(-5, string.Empty));
        Assert.False(WindhawkManagerService.IsProcessAlive(int.MaxValue, string.Empty));
    }
}
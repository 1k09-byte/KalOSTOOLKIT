using System.IO;
using System.Text.Json;
using KaliteKit.Services;
using Microsoft.Win32;
using Xunit;

namespace KaliteKit.Tests.Services;

public class OsChangeServiceTests : IDisposable
{
    private readonly string _tempDir;

    public OsChangeServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KaliteKit-OsChangeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // Isolate persisted apply-state from any real usage on the machine.
        OsChangeService.ResetStateForTest();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static OsChangeManifest SampleManifest() => new()
    {
        Version = "1.0.0.4",
        Changes = new()
        {
            new OsChangeEntry
            {
                Description = "Edited MMCSS settings",
                Type = OsChangeOp.Registry,
                Key = @"HKCU\Software\KaliteKit\Tests\OsChange",
                ValueName = "NetworkThrottlingIndex",
                Value = JsonDocument.Parse("4294967295").RootElement,
                ValueKind = RegistryValueKind.DWord,
            },
        }
    };

    // ── Validation ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyChanges_ReturnsFalse()
    {
        var m = SampleManifest();
        m.Changes.Clear();
        Assert.False(OsChangeService.Validate(m));
    }

    [Fact]
    public void Validate_NonWhitelistedRegistryHive_ReturnsFalse()
    {
        var m = SampleManifest();
        m.Changes[0].Key = @"FOO\Some\Key";
        Assert.False(OsChangeService.Validate(m));
    }

    [Fact]
    public void Validate_NonNumericRegistryValue_ReturnsFalse()
    {
        var m = SampleManifest();
        m.Changes[0].Value = JsonDocument.Parse("\"not-a-number\"").RootElement;
        Assert.False(OsChangeService.Validate(m));
    }

    [Fact]
    public void Validate_ServiceWithoutName_ReturnsFalse()
    {
        var m = SampleManifest();
        m.Changes[0] = new OsChangeEntry
        {
            Description = "svc",
            Type = OsChangeOp.Service,
            ServiceName = "",
            StartupType = "disabled",
        };
        Assert.False(OsChangeService.Validate(m));
    }

    [Fact]
    public void Validate_UnknownStartupType_ReturnsFalse()
    {
        var m = SampleManifest();
        m.Changes[0] = new OsChangeEntry
        {
            Description = "svc",
            Type = OsChangeOp.Service,
            ServiceName = "DiagTrack",
            StartupType = "nonsense",
        };
        Assert.False(OsChangeService.Validate(m));
    }

    [Fact]
    public void Validate_ValidManifest_ReturnsTrue()
    {
        Assert.True(OsChangeService.Validate(SampleManifest()));
    }

    // ── Load / parse ───────────────────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(OsChangeService.Load(Path.Combine(_tempDir, "nope.json")));
    }

    [Fact]
    public void Load_EmptyChangesArray_ReturnsNull_NoButton()
    {
        // An app-only update ships an empty manifest: it must be treated as
        // "no OS changes" so the apply-changes button never appears.
        var path = Path.Combine(_tempDir, "os-changes-empty.json");
        File.WriteAllText(path, """
        { "version": "1.0.0.5", "changes": [] }
        """);
        Assert.Null(OsChangeService.Load(path));
    }

    [Fact]
    public void Load_ValidJson_ReturnsManifest()
    {
        var path = Path.Combine(_tempDir, "os-changes.json");
        File.WriteAllText(path, """
        {
          "version": "1.0.0.4",
          "changes": [
            {
              "description": "Edited MMCSS settings",
              "type": "registry",
              "key": "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile",
              "valueName": "NetworkThrottlingIndex",
              "value": 4294967295,
              "valueKind": "DWord"
            },
            {
              "description": "Disable DiagTrack",
              "type": "service",
              "serviceName": "DiagTrack",
              "startupType": "disabled"
            }
          ]
        }
        """);
        var m = OsChangeService.Load(path);
        Assert.NotNull(m);
        Assert.Equal("1.0.0.4", m!.Version);
        Assert.Equal(2, m.Changes.Count);
        Assert.Equal(OsChangeOp.Registry, m.Changes[0].Type);
        Assert.Equal("NetworkThrottlingIndex", m.Changes[0].ValueName);
        Assert.Equal(4294967295L, m.Changes[0].Value!.Value.GetInt64());
        Assert.Equal(OsChangeOp.Service, m.Changes[1].Type);
        Assert.Equal("DiagTrack", m.Changes[1].ServiceName);
        Assert.Equal("disabled", m.Changes[1].StartupType);
    }

    [Fact]
    public void Load_InvalidJson_ReturnsNull()
    {
        var path = Path.Combine(_tempDir, "bad.json");
        File.WriteAllText(path, "{ not valid json !!");
        Assert.Null(OsChangeService.Load(path));
    }

    // ── Apply / state / rollback mapping ───────────────────────────────

    [Fact]
    public void Apply_RegistersState_AndRollbackClearsIt()
    {
        // Uses an HKCU test key so it works without admin; exercises the full
        // apply -> state -> rollback round trip against the real registry.
        var m = SampleManifest();
        const string key = @"HKCU\Software\KaliteKit\Tests\OsChange";
        const string valueName = "NetworkThrottlingIndex";

        var svc = new OsChangeService();
        Assert.False(OsChangeService.IsApplied(m));

        var result = new OsChangeResult();
        Assert.True(svc.TryApply(m, result), "TryApply failed: " + string.Join("; ", result.Errors));
        Assert.Single(result.Applied);
        Assert.Empty(result.Errors);
        Assert.True(OsChangeService.IsApplied(m));

        // The value actually landed.
        Assert.Equal(4294967295u, (uint)(int)KaliteKit.Helpers.RegistryHelper.GetRegistryValue(key, valueName)!);

        // Roll back.
        var rb = new OsChangeResult();
        Assert.True(svc.TryRollback(m, rb));
        Assert.Single(rb.Applied);
        Assert.False(OsChangeService.IsApplied(m));
    }

    [Fact]
    public void Apply_SameVersionTwice_RecordsOnce_AndRollsBackOnce()
    {
        var m = SampleManifest();
        var svc = new OsChangeService();

        var r1 = new OsChangeResult();
        svc.TryApply(m, r1);
        Assert.True(r1.Success, "TryApply 1 failed: " + string.Join("; ", r1.Errors));
        // Second apply on the same version: allowed (idempotent write), but the
        // state must not grow unbounded entries.
        var r2 = new OsChangeResult();
        svc.TryApply(m, r2);
        Assert.True(r2.Success, "TryApply 2 failed: " + string.Join("; ", r2.Errors));

        var state = System.Text.Json.JsonSerializer.Deserialize<OsChangeService.OsChangeState>(System.IO.File.ReadAllText(OsChangeService.StatePath));
        Assert.NotNull(state);
        Assert.Equal(1, state!.AppliedEntries.Count);

        var rb = new OsChangeResult();
        svc.TryRollback(m, rb);
        Assert.False(OsChangeService.IsApplied(m));
    }

    [Fact]
    public void Rollback_WhenNothingApplied_IsSuccess()
    {
        var m = SampleManifest();
        var svc = new OsChangeService();
        var result = new OsChangeResult();
        Assert.True(svc.TryRollback(m, result));
        Assert.Empty(result.Applied);
    }

    [Fact]
    public void StartValueToKeyword_MapsKnownValues()
    {
        Assert.Equal("boot", OsChangeService.StartValueToKeyword(0));
        Assert.Equal("system", OsChangeService.StartValueToKeyword(1));
        Assert.Equal("auto", OsChangeService.StartValueToKeyword(2));
        Assert.Equal("demand", OsChangeService.StartValueToKeyword(3));
        Assert.Equal("disabled", OsChangeService.StartValueToKeyword(4));
        Assert.Equal("demand", OsChangeService.StartValueToKeyword(99));
    }

    [Fact]
    public void Validate_ScriptEntry_RequiresRelativePath()
    {
        var m = new OsChangeManifest
        {
            Version = "1.0.0.4",
            Changes = new()
            {
                new OsChangeEntry
                {
                    Description = "Run script",
                    Type = OsChangeOp.Script,
                    Script = "C:\\absolute\\path.ps1",
                },
            },
        };
        Assert.False(OsChangeService.Validate(m));
    }

    [Fact]
    public void Validate_ScriptEntry_RejectsTraversal()
    {
        var m = new OsChangeManifest
        {
            Version = "1.0.0.4",
            Changes = new()
            {
                new OsChangeEntry
                {
                    Description = "Run script",
                    Type = OsChangeOp.Script,
                    Script = "..\\..\\evil.ps1",
                },
            },
        };
        Assert.False(OsChangeService.Validate(m));
    }

    [Fact]
    public void Validate_ScriptEntry_RejectsNonPs1()
    {
        var m = new OsChangeManifest
        {
            Version = "1.0.0.4",
            Changes = new()
            {
                new OsChangeEntry
                {
                    Description = "Run script",
                    Type = OsChangeOp.Script,
                    Script = "script.bat",
                },
            },
        };
        Assert.False(OsChangeService.Validate(m));
    }

    [Fact]
    public void Validate_ScriptEntry_AcceptsValid()
    {
        var m = new OsChangeManifest
        {
            Version = "1.0.0.4",
            Changes = new()
            {
                new OsChangeEntry
                {
                    Description = "Run script",
                    Type = OsChangeOp.Script,
                    Script = "post-update-tweaks.ps1",
                },
            },
        };
        Assert.True(OsChangeService.Validate(m));
    }

    [Fact]
    public void RunScript_SuccessfulScript_ReturnsTrue()
    {
        var script = Path.Combine(_tempDir, "test-script.ps1");
        File.WriteAllText(script, "Write-Output 'hello'\n");
        var (ok, output) = OsChangeService.RunScript(script);
        Assert.True(ok);
        Assert.Contains("hello", output);
    }

    [Fact]
    public void RunScript_FailingScript_ReturnsFalse()
    {
        var script = Path.Combine(_tempDir, "fail-script.ps1");
        File.WriteAllText(script, "exit 1\n");
        var (ok, output) = OsChangeService.RunScript(script);
        Assert.False(ok);
    }

    [Fact]
    public void TryApply_ScriptEntry_RunsScriptAndRecordsState()
    {
        var script = Path.Combine(_tempDir, "apply-script.ps1");
        File.WriteAllText(script, "Write-Output 'applied'\n");
        // Move script to the install dir so Apply can find it.
        var installDir = AppContext.BaseDirectory;
        var installScript = Path.Combine(installDir, "apply-script.ps1");
        File.Copy(script, installScript, overwrite: true);
        try
        {
            var m = new OsChangeManifest
            {
                Version = "1.0.0.4",
                Changes = new()
                {
                    new OsChangeEntry
                    {
                        Description = "Run script",
                        Type = OsChangeOp.Script,
                        Script = "apply-script.ps1",
                    },
                },
            };
            var svc = new OsChangeService();
            var result = new OsChangeResult();
            Assert.True(svc.TryApply(m, result));
            Assert.Single(result.Applied);
            Assert.Empty(result.Errors);
            Assert.True(OsChangeService.IsApplied(m));
        }
        finally
        {
            if (File.Exists(installScript)) File.Delete(installScript);
        }
    }
}

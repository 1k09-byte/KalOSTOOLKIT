using System.Linq;
using KaliteKit.ViewModels;

namespace KaliteKit.Tests.ViewModels;

/// <summary>
/// Guards the GameConfigStore values the fullscreen toggle writes — they must
/// exactly match the reference FSO.reg / FSE.reg files. A wrong DWORD silently
/// changes how games present fullscreen.
/// </summary>
public class FullscreenModePresetsTests
{
    [Fact]
    public void Fso_WritesTheAllZerosReferenceValues()
    {
        Assert.Equal(0, FullscreenModePresets.Fso["GameDVR_DXGIHonorFSEWindowsCompatible"]);
        Assert.Equal(0, FullscreenModePresets.Fso["GameDVR_HonorUserFSEBehaviorMode"]);
        Assert.Equal(0, FullscreenModePresets.Fso["GameDVR_FSEBehaviorMode"]);
        Assert.Equal(0, FullscreenModePresets.Fso["GameDVR_FSEBehavior"]);
        Assert.Equal(0, FullscreenModePresets.Fso["GameDVR_DSEBehavior"]);
    }

    [Fact]
    public void Fse_WritesTheReferenceValues()
    {
        Assert.Equal(1, FullscreenModePresets.Fse["GameDVR_DXGIHonorFSEWindowsCompatible"]);
        Assert.Equal(0, FullscreenModePresets.Fse["GameDVR_HonorUserFSEBehaviorMode"]);
        Assert.Equal(2, FullscreenModePresets.Fse["GameDVR_FSEBehaviorMode"]);
        Assert.Equal(2, FullscreenModePresets.Fse["GameDVR_FSEBehavior"]);
        Assert.Equal(2, FullscreenModePresets.Fse["GameDVR_DSEBehavior"]);
    }

    [Fact]
    public void BothPresets_HaveTheSameFiveKeys()
    {
        var fsoKeys = FullscreenModePresets.Fso.Keys.OrderBy(k => k).ToArray();
        var fseKeys = FullscreenModePresets.Fse.Keys.OrderBy(k => k).ToArray();

        Assert.Equal(5, fsoKeys.Length);
        Assert.Equal(fsoKeys, fseKeys);
    }
}

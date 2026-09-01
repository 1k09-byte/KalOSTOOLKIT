using System.Linq;
using KalOS.Services;

namespace KalOS.Tests.Services;

/// <summary>
/// Guards the feature manifest against typos and dead markers: every feature
/// the app is expected to ship must resolve in the compiled KalOS assembly.
/// If a feature is deliberately removed, remove its manifest row too.
/// </summary>
public class FeatureSelfCheckTests
{
    [Fact]
    public void Run_EveryManifestFeature_IsPresentInTheRunningBuild()
    {
        var results = FeatureSelfCheck.Run();

        Assert.NotEmpty(results);
        Assert.Equal(FeatureSelfCheck.Manifest.Count, results.Count);

        var missing = results.Where(r => !r.Present).ToList();
        Assert.True(missing.Count == 0,
            "Missing features: " + string.Join(", ", missing.Select(m => $"{m.Name} ({m.Marker})")));
    }
}

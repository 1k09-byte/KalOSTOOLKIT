using System.Collections.Generic;
using KalOS.Helpers;
using KalOS.ViewModels;
using Xunit;

namespace KalOS.Tests.Helpers;

public class AffinityAllocationPlanTests
{
    private static CpuCoreInfo Core(int id, int firstLp, int threads, int effClass = 0, int l3 = 0)
    {
        var core = new CpuCoreInfo { CoreId = id, EfficiencyClass = effClass, L3CacheId = l3 };
        ulong mask = 0;
        for (int i = 0; i < threads; i++) mask |= 1UL << (firstLp + i);
        core.FullCoreMask = mask;
        core.LogicalProcessorMask = 1UL << firstLp;
        for (int i = 0; i < threads; i++) core.Threads.Add(new CpuThreadInfo { ThreadId = firstLp + i });
        return core;
    }

    private static List<CpuCoreInfo> Homogeneous(int physical, int threadsPerCore = 2)
    {
        var cores = new List<CpuCoreInfo>();
        for (int i = 0; i < physical; i++)
        {
            cores.Add(Core(id: i, firstLp: i * threadsPerCore, threads: threadsPerCore));
        }
        return cores;
    }

    private static List<CpuCoreInfo> Hybrid(int pCores, int eCores, int threadsPerCore = 2)
    {
        // P-cores first (CPU 0 is a P-core on real hybrid CPUs), E-cores after.
        var cores = new List<CpuCoreInfo>();
        for (int i = 0; i < pCores; i++)
        {
            cores.Add(Core(id: i, firstLp: i * threadsPerCore, threads: threadsPerCore, effClass: 1));
        }
        for (int i = 0; i < eCores; i++)
        {
            cores.Add(Core(id: pCores + i, firstLp: (pCores + i) * threadsPerCore, threads: threadsPerCore, effClass: 0));
        }
        return cores;
    }

    [Fact]
    public void HomogeneousCpu_AudioSkipsCpu0_AndGivesEachTargetItsOwnCore()
    {
        var plan = AffinityAllocationPlan.Build(Homogeneous(8));

        Assert.Equal(0b1100UL, plan.AudioMask);          // procs 2,3 — CPU 0 skipped
        Assert.Equal(0b110000UL, plan.XhciMask);         // procs 4,5
        Assert.Equal(0b11000000UL, plan.NetworkMask);    // procs 6,7
        Assert.Equal(0b111100000000UL, plan.GpuMask);    // procs 8-11 (2 cores, 4 threads)
        Assert.True(plan.GpuUsable);
        Assert.Equal(6, plan.NetworkBaseProcessor);
        Assert.Equal(0UL, plan.AudioMask & plan.XhciMask & plan.NetworkMask & plan.GpuMask); // disjoint
    }

    [Fact]
    public void HybridCpu_AudioGoesToEcore_OthersToPCores()
    {
        var plan = AffinityAllocationPlan.Build(Hybrid(pCores: 8, eCores: 4));

        // Audio must land on an E-core (procs 16,17 — the first E core).
        Assert.Equal(0b11UL << 16, plan.AudioMask);
        // XHCI and Network take distinct P-cores (procs 2,3 and 4,5 — CPU 0 skipped).
        Assert.Equal(0b1100UL, plan.XhciMask);
        Assert.Equal(0b110000UL, plan.NetworkMask);
        // GPU takes the next 2 P-cores (procs 6,7,8,9).
        Assert.Equal(0b1111UL << 6, plan.GpuMask);
        Assert.True(plan.GpuUsable);
        Assert.Equal(0UL, plan.AudioMask & (plan.XhciMask | plan.NetworkMask | plan.GpuMask));
    }

    [Fact]
    public void TwoCoreCpu_NetworkSharesXhciCore_NeverSharesAudio_GpuSkipped()
    {
        var plan = AffinityAllocationPlan.Build(Homogeneous(2));

        // Audio owns the only non-CPU0 core.
        Assert.Equal(0b1100UL, plan.AudioMask);
        // XHCI falls back to CPU 0.
        Assert.Equal(0b11UL, plan.XhciMask);
        // Network shares the XHCI core (NOT the audio core).
        Assert.Equal(plan.XhciMask, plan.NetworkMask);
        Assert.Equal(0UL, plan.NetworkMask & plan.AudioMask);
        // No spare core for the GPU — skipped instead of pinning onto audio.
        Assert.False(plan.GpuUsable);
        Assert.Equal(0UL, plan.GpuMask);
    }

    [Fact]
    public void FourCoreCpu_AllTargetsGetDistinctCores()
    {
        var plan = AffinityAllocationPlan.Build(Homogeneous(4));

        Assert.NotEqual(plan.AudioMask, plan.XhciMask);
        Assert.NotEqual(plan.XhciMask, plan.NetworkMask);
        Assert.NotEqual(plan.AudioMask, plan.NetworkMask);
        Assert.Equal(0UL, plan.AudioMask & plan.XhciMask & plan.NetworkMask & plan.GpuMask);
        Assert.NotEqual(0UL, plan.AudioMask);
        Assert.NotEqual(0UL, plan.XhciMask);
        Assert.NotEqual(0UL, plan.NetworkMask);
    }

    [Fact]
    public void GpuMask_NeverExceedsFourLogicalProcessors()
    {
        // SMT=2 → 2 physical cores = exactly 4 threads.
        var smt2 = AffinityAllocationPlan.Build(Homogeneous(16, threadsPerCore: 2));
        Assert.True(System.Numerics.BitOperations.PopCount(smt2.GpuMask) <= AffinityAllocationPlan.MaxGpuLogicalProcessors);

        // SMT=4 rare layout → a single core already fills the 4-thread cap.
        var smt4 = AffinityAllocationPlan.Build(Homogeneous(8, threadsPerCore: 4));
        Assert.True(System.Numerics.BitOperations.PopCount(smt4.GpuMask) <= AffinityAllocationPlan.MaxGpuLogicalProcessors);
    }

    [Fact]
    public void EmptyTopology_ReturnsZeroedPlan_WithoutThrowing()
    {
        var plan = AffinityAllocationPlan.Build(new List<CpuCoreInfo>());
        Assert.Equal(0UL, plan.AudioMask);
        Assert.False(plan.GpuUsable);
        Assert.NotNull(plan.Notes);
    }

    [Fact]
    public void TopologySummary_ClassifiesHybridCores_AndCountsCcds()
    {
        // 2 CCDs of P-cores + one E cluster.
        var cores = new List<CpuCoreInfo>
        {
            Core(0, 0, 2, effClass: 1, l3: 0),
            Core(1, 2, 2, effClass: 1, l3: 0),
            Core(2, 4, 2, effClass: 1, l3: 1),
            Core(3, 6, 2, effClass: 1, l3: 1),
            Core(4, 8, 2, effClass: 0, l3: 2),
        };

        var summary = CpuTopologySummary.Build("Test CPU", cores);

        Assert.True(summary.IsHybrid);
        Assert.Equal(5, summary.PhysicalCores);
        Assert.Equal(10, summary.LogicalProcessors);
        Assert.Equal(4, summary.PCoreCount);
        Assert.Equal(1, summary.ECoreCount);
        Assert.Equal(3, summary.CcdCount);
        Assert.True(summary.SmtEnabled);
        Assert.True(summary.HasEcores);
        Assert.Contains("SMT on", summary.Describe());
        Assert.Contains("4 P · 1 E", summary.Describe());

        // P-cores split into two CCD groups; E-cores form their own group.
        Assert.Equal(3, summary.Groups.Count);
        Assert.Equal(2, summary.Groups.Count(g => g.Kind == "P"));
        Assert.Equal(1, summary.Groups.Count(g => g.Kind == "E"));
    }

    [Fact]
    public void TopologySummary_HomogeneousCpu_TreatsAllCoresAsP()
    {
        var summary = CpuTopologySummary.Build("AMD Test", Homogeneous(6));

        Assert.False(summary.IsHybrid);
        Assert.Equal(6, summary.PCoreCount);
        Assert.Equal(0, summary.ECoreCount);
        Assert.False(summary.HasEcores);
        Assert.Single(summary.Groups);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KaliteKit.ViewModels;

namespace KaliteKit.Helpers
{
    /// <summary>
    /// The resolved core assignments for one Optimize run. Masks are full-core
    /// masks (both SMT threads) ready to write into AssignmentSetOverride.
    /// </summary>
    /// <param name="AudioMask">E-core (hybrid) or first non-CPU0 core; Normal priority; MSI limit 1.</param>
    /// <param name="XhciMask">Dedicated performance core; High priority; MSI vector count untouched.</param>
    /// <param name="NetworkMask">Dedicated performance core; High priority; NDIS RSS base processor re-pointed.</param>
    /// <param name="GpuMask">Up to 2 physical performance cores (≤4 SMT threads); High priority; MSI limit 1.</param>
    /// <param name="NetworkBaseProcessor">Lowest logical processor of the network core for *RssBaseProcNumber, or null when network isn't pinned.</param>
    /// <param name="GpuUsable">False when the CPU couldn't spare any P-core for the GPU after audio/XHCI/NIC.</param>
    /// <param name="Notes">Human-readable summary of the allocation (shown in the confirm dialog).</param>
    public sealed record AffinityPlan(
        ulong AudioMask,
        ulong XhciMask,
        ulong NetworkMask,
        ulong GpuMask,
        int? NetworkBaseProcessor,
        bool GpuUsable,
        string Notes);

    /// <summary>
    /// Pure, dependency-free allocation strategy for the Per-CPU low-latency
    /// profile. Given the (group-0) physical-core topology it decides which
    /// cores Audio / XHCI / Network / GPU should pin to, honoring:
    ///
    ///   • Hybrid CPUs (Intel 12th gen+): Audio goes to a dedicated E-core so
    ///     its interrupts never contend with render threads; XHCI / Network /
    ///     GPU go to dedicated P-cores.
    ///   • Homogeneous CPUs (AMD Ryzen, older Intel): every core is treated as
    ///     a performance core; audio takes the first non-CPU0 core.
    ///   • Multi-CCD CPUs: preference order is by logical processor index, which
    ///     keeps the first CCD first — good cache locality for the hot devices.
    ///   • CPU 0 is never a preferred pinning target (HAL/SMI tick traffic);
    ///     it is only used as an absolute last resort on ≤2-core systems.
    ///   • 2-4 physical-core fallback: when there aren't enough distinct cores,
    ///     targets degrade gracefully — network shares the XHCI core before it
    ///     would ever share the audio core, and the GPU is skipped instead of
    ///     being pinned to a core that's already carrying audio interrupts.
    ///     No mask is ever written as 0, so Optimize never fails outright.
    /// </summary>
    public static class AffinityAllocationPlan
    {
        /// <summary>GPU is capped at 4 logical processors across its pinned cores.</summary>
        public const int MaxGpuLogicalProcessors = 4;

        /// <summary>GPU gets up to 2 dedicated physical cores.</summary>
        public const int MaxGpuPhysicalCores = 2;

        public static AffinityPlan Build(IReadOnlyList<CpuCoreInfo> cores)
        {
        if (cores.Count == 0)
        {
            return new AffinityPlan(0, 0, 0, 0, null, false, "No usable CPU topology detected.");
        }

            // Hybrid detection: EfficiencyClass > 0 exists → those are P-cores and
            // class 0 cores are E-cores. On homogeneous CPUs everything is class 0
            // and is treated as performance cores (E-pool stays empty).
            bool hasP = cores.Any(c => c.EfficiencyClass > 0);

            // Ordering: non-CPU0 cores first (mask 1 is the CPU0 core), then by
            // first logical processor so CCD 0's cores come before CCD 1's.
            IOrderedEnumerable<CpuCoreInfo> Order(IEnumerable<CpuCoreInfo> pool) =>
                pool.OrderBy(c => c.LogicalProcessorMask == 1UL ? 1 : 0)
                    .ThenBy(c => c.FirstLogicalProcessor);

            var pPool = Order(hasP ? cores.Where(c => c.EfficiencyClass > 0) : cores).ToList();
            var ePool = hasP ? Order(cores.Where(c => c.EfficiencyClass == 0)).ToList() : new List<CpuCoreInfo>();

            static CpuCoreInfo? Take(IReadOnlyList<CpuCoreInfo> pool, params CpuCoreInfo?[] used)
            {
                foreach (var core in pool)
                {
                    if (used.Contains(core)) continue;
                    return core;
                }
                return null;
            }

            // ── Audio: dedicated E-core (hybrid), else first non-CPU0 core ──
            var audio = (ePool.Count > 0 ? Take(ePool) : null) ?? Take(pPool) ?? cores[0];

            // ── XHCI: dedicated performance core, distinct from audio ──
            var xhci = Take(pPool, audio) ?? Take(ePool, audio) ?? audio;

            // ── Network: dedicated performance core, distinct from both ──
            // Fallback shares the XHCI core first — never the audio core — so a
            // 2-core system ends up with {audio, xhci+nic} instead of
            // {audio+nic, xhci} which would put NIC traffic on the audio core.
            var network = Take(pPool, audio, xhci) ?? Take(ePool, audio, xhci) ?? xhci;

            // ── GPU: up to 2 remaining physical P-cores, ≤4 logical processors ──
            ulong gpuMask = 0;
            int gpuCoresTaken = 0;
            foreach (var core in pPool)
            {
                if (core == audio || core == xhci || core == network) continue;
                if (gpuCoresTaken >= MaxGpuPhysicalCores) break;

                int nextBits = PopCount(gpuMask) + PopCount(core.FullCoreMask);
                if (nextBits > MaxGpuLogicalProcessors) continue; // SMT≥4 core would blow the cap
                gpuMask |= core.FullCoreMask;
                gpuCoresTaken++;
            }

            // Absolute fallback: if the P-pool was fully consumed by the first
            // three targets (tiny CPUs), let the GPU borrow an E-core before we
            // declare it unusable.
            if (gpuCoresTaken == 0)
            {
                foreach (var core in ePool)
                {
                    if (core == audio || core == xhci || core == network) continue;
                    int nextBits = PopCount(gpuMask) + PopCount(core.FullCoreMask);
                    if (nextBits > MaxGpuLogicalProcessors) continue;
                    gpuMask |= core.FullCoreMask;
                    gpuCoresTaken++;
                    break;
                }
            }

            bool gpuUsable = gpuMask != 0;

            var notes = new List<string>();
            if (ePool.Count > 0 && audio.EfficiencyClass == 0)
            {
                notes.Add("Audio pinned to a dedicated E-core.");
            }
            else if (audio.LogicalProcessorMask == 1UL)
            {
                notes.Add("Audio pinned to CPU 0 (small CPU — no spare core).");
            }
            else
            {
                notes.Add("Audio pinned to a dedicated core.");
            }
            if (network == xhci && network.CoreId == xhci.CoreId && pPool.Count + ePool.Count < 4)
            {
                notes.Add("Network shares the USB core (small CPU).");
            }
            if (!gpuUsable)
            {
                notes.Add("GPU skipped — no spare core after Audio/USB/Network.");
            }
            else
            {
                notes.Add($"GPU pinned to {gpuCoresTaken} core(s), {PopCount(gpuMask)} logical processors.");
            }

            return new AffinityPlan(
                AudioMask: audio?.FullCoreMask ?? 0UL,
                XhciMask: xhci?.FullCoreMask ?? 0UL,
                NetworkMask: network?.FullCoreMask ?? 0UL,
                GpuMask: gpuMask,
                NetworkBaseProcessor: network?.FirstLogicalProcessor,
                GpuUsable: gpuUsable,
                Notes: string.Join(" ", notes));
        }

        private static int PopCount(ulong mask) => BitOperations.PopCount(mask);
    }
}

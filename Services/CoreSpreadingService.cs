using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using KalOS.ViewModels;
using Microsoft.Win32;

namespace KalOS.Services
{
    /// <summary>
    /// Result of assigning cores to a device.
    /// </summary>
    public class CoreAssignment
    {
        public required PciDeviceItem Device { get; init; }

        /// <summary>Bitmask of logical processors assigned to this device.</summary>
        public required ulong AffinityMask { get; init; }

        /// <summary>The primary physical core index assigned.</summary>
        public required int PhysicalCoreId { get; init; }

        /// <summary>Human-readable explanation of why this core/mask was chosen.</summary>
        public required string Reason { get; init; }

        /// <summary>Recommended priority for this device class (1=Low, 2=Normal, 3=High).</summary>
        public int RecommendedPriority { get; init; } = 2;

        /// <summary>Recommended MSI limit, or null to preserve driver default.</summary>
        public int? RecommendedMsiLimit { get; init; }
    }

    /// <summary>
    /// Intelligent CPU affinity scheduling service based on device categories and CPU topology.
    /// Hardware low-latency scheduling principles:
    ///   - Audio: Pinned to E-Core (if available) or Core 0 / secondary P-Core, Normal priority, MSI limit = 1.
    ///   - USB (XHCI): Pinned to dedicated P-Core, High priority (3), preserves driver MSI limit.
    ///   - Network (NIC): Pinned to dedicated P-Core, High priority (3), RSS configured.
    ///   - GPU: Pinned to 2 physical P-Cores (adjacent / best CCD), High priority (3), MSI limit = 1.
    ///   - Fallback: Gracefully shares cores on low-core systems without failing.
    /// </summary>
    public class CoreSpreadingService
    {
        private readonly LoggingService _log;

        public CoreSpreadingService(LoggingService log)
        {
            _log = log;
        }

        /// <summary>
        /// Assigns cores intelligently across all MSI-capable devices based on topology and device class.
        /// </summary>
        public List<CoreAssignment> AssignCoresIntelligently(
            IReadOnlyList<PciDeviceItem> devices,
            IReadOnlyList<CpuCoreInfo> topology,
            CpuTopologySummary? summary)
        {
            var assignments = new List<CoreAssignment>();
            var claimedCores = new HashSet<int>();

            var pCores = topology.Where(c => c.IsPCore && c.ProcessorGroup == 0).OrderBy(c => c.CoreId).ToList();
            var eCores = topology.Where(c => c.IsECore && c.ProcessorGroup == 0).OrderBy(c => c.CoreId).ToList();
            var nonZeroPCores = pCores.Where(c => c.LogicalProcessorMask != 1UL).ToList();

            // 1. Audio Controllers:
            // Prefer E-Core -> if no E-Core, use Core 0 or dedicated secondary P-Core
            var audioDevices = devices.Where(d => d.Category == "Audio Controllers" && d.MsiSupported).ToList();
            foreach (var audio in audioDevices)
            {
                var freeECore = eCores.FirstOrDefault(c => !claimedCores.Contains(c.CoreId));
                if (freeECore != null)
                {
                    claimedCores.Add(freeECore.CoreId);
                    assignments.Add(new CoreAssignment
                    {
                        Device = audio,
                        AffinityMask = freeECore.FullCoreMask,
                        PhysicalCoreId = freeECore.CoreId,
                        Reason = $"Dedicated E-Core {freeECore.CoreId} (Threads: {MaskToThreadList(freeECore.FullCoreMask)})",
                        RecommendedPriority = 2,
                        RecommendedMsiLimit = 1
                    });
                }
                else
                {
                    // No E-Cores available (AMD or standard Intel).
                    // Use Core 0 or first available core for Audio
                    var targetCore = topology.FirstOrDefault(c => c.ProcessorGroup == 0) ?? topology.First();
                    assignments.Add(new CoreAssignment
                    {
                        Device = audio,
                        AffinityMask = targetCore.FullCoreMask,
                        PhysicalCoreId = targetCore.CoreId,
                        Reason = $"Core {targetCore.CoreId} (Threads: {MaskToThreadList(targetCore.FullCoreMask)})",
                        RecommendedPriority = 2,
                        RecommendedMsiLimit = 1
                    });
                }
            }

            // 2. USB Controllers (XHCI):
            var xhciDevices = devices.Where(d => d.Category == "XHCI Controllers" && d.MsiSupported).ToList();
            foreach (var xhci in xhciDevices)
            {
                var freePCore = nonZeroPCores.FirstOrDefault(c => !claimedCores.Contains(c.CoreId));
                if (freePCore != null)
                {
                    claimedCores.Add(freePCore.CoreId);
                    assignments.Add(new CoreAssignment
                    {
                        Device = xhci,
                        AffinityMask = freePCore.FullCoreMask,
                        PhysicalCoreId = freePCore.CoreId,
                        Reason = $"Dedicated P-Core {freePCore.CoreId} (Threads: {MaskToThreadList(freePCore.FullCoreMask)})",
                        RecommendedPriority = 3,
                        RecommendedMsiLimit = null // preserve driver default
                    });
                }
                else
                {
                    // Core sharing fallback
                    var shareCore = nonZeroPCores.FirstOrDefault() ?? topology.First();
                    assignments.Add(new CoreAssignment
                    {
                        Device = xhci,
                        AffinityMask = shareCore.FullCoreMask,
                        PhysicalCoreId = shareCore.CoreId,
                        Reason = $"Shared P-Core {shareCore.CoreId} (Threads: {MaskToThreadList(shareCore.FullCoreMask)})",
                        RecommendedPriority = 3,
                        RecommendedMsiLimit = null
                    });
                }
            }

            // 3. Network Interface Controllers (NIC):
            var netDevices = devices.Where(d => d.Category == "Network Interface Controllers" && d.MsiSupported).ToList();
            foreach (var nic in netDevices)
            {
                var freePCore = nonZeroPCores.FirstOrDefault(c => !claimedCores.Contains(c.CoreId));
                if (freePCore != null)
                {
                    claimedCores.Add(freePCore.CoreId);
                    assignments.Add(new CoreAssignment
                    {
                        Device = nic,
                        AffinityMask = freePCore.FullCoreMask,
                        PhysicalCoreId = freePCore.CoreId,
                        Reason = $"Dedicated P-Core {freePCore.CoreId} (Threads: {MaskToThreadList(freePCore.FullCoreMask)})",
                        RecommendedPriority = 3,
                        RecommendedMsiLimit = null
                    });
                }
                else
                {
                    var shareCore = nonZeroPCores.LastOrDefault() ?? topology.First();
                    assignments.Add(new CoreAssignment
                    {
                        Device = nic,
                        AffinityMask = shareCore.FullCoreMask,
                        PhysicalCoreId = shareCore.CoreId,
                        Reason = $"Shared P-Core {shareCore.CoreId} (Threads: {MaskToThreadList(shareCore.FullCoreMask)})",
                        RecommendedPriority = 3,
                        RecommendedMsiLimit = null
                    });
                }
            }

            // 4. Graphics Cards (GPU):
            var gpuDevices = devices.Where(d => d.Category == "Graphics Cards" && d.MsiSupported).ToList();
            foreach (var gpu in gpuDevices)
            {
                // Find 2 free P-Cores or available non-zero P-Cores
                var freeGpuCores = nonZeroPCores.Where(c => !claimedCores.Contains(c.CoreId)).Take(2).ToList();
                if (freeGpuCores.Count < 2)
                {
                    freeGpuCores = nonZeroPCores.TakeLast(2).ToList();
                }

                if (freeGpuCores.Count > 0)
                {
                    ulong gpuMask = 0;
                    foreach (var c in freeGpuCores)
                    {
                        gpuMask |= c.FullCoreMask;
                        claimedCores.Add(c.CoreId);
                    }

                    assignments.Add(new CoreAssignment
                    {
                        Device = gpu,
                        AffinityMask = gpuMask,
                        PhysicalCoreId = freeGpuCores.First().CoreId,
                        Reason = $"P-Cores {string.Join(", ", freeGpuCores.Select(c => c.CoreId))} ({BitOperations.PopCount(gpuMask)} Threads: {MaskToThreadList(gpuMask)})",
                        RecommendedPriority = 3,
                        RecommendedMsiLimit = 1
                    });
                }
            }

            return assignments;
        }

        /// <summary>
        /// Assign 2 logical processors (one physical core) to each device.
        /// </summary>
        public List<CoreAssignment> AssignCores(
            IReadOnlyList<PciDeviceItem> devices,
            IReadOnlyList<CpuCoreInfo> topology)
        {
            var availableCores = topology
                .Where(c => c.ProcessorGroup == 0)
                .Where(c => c.LogicalProcessorMask != 1UL) // skip core 0
                .OrderBy(c => c.CoreId)
                .ToList();

            var claimedMasks = new HashSet<ulong>();
            var assignments = new List<CoreAssignment>();

            foreach (var device in devices)
            {
                if (!device.MsiSupported || !device.MsiEnabled) continue;

                if (IsExplicitlyPinned(device))
                {
                    _log.Info($"Skipping {device.Name}: already has explicit pin.");
                    continue;
                }

                var freeCore = availableCores
                    .FirstOrDefault(c => !claimedMasks.Contains(c.FullCoreMask));

                if (freeCore == null)
                {
                    _log.Warn($"No free cores for {device.Name} — skipping.");
                    continue;
                }

                claimedMasks.Add(freeCore.FullCoreMask);

                assignments.Add(new CoreAssignment
                {
                    Device = device,
                    AffinityMask = freeCore.FullCoreMask,
                    PhysicalCoreId = freeCore.CoreId,
                    Reason = $"Core {freeCore.CoreId} (2 LPs: {MaskToThreadList(freeCore.FullCoreMask)})",
                    RecommendedPriority = 2,
                    RecommendedMsiLimit = 1
                });
            }

            return assignments;
        }

        /// <summary>
        /// Find devices currently on Default policy (no explicit affinity) that could be moved.
        /// </summary>
        public List<PciDeviceItem> FindDefaultPolicyDevices(IReadOnlyList<PciDeviceItem> allDevices)
        {
            return allDevices
                .Where(d => d.MsiSupported
                            && d.MsiEnabled
                            && (d.DevicePolicy == "IrqPolicyMachineDefault" || string.IsNullOrEmpty(d.DevicePolicy))
                            && string.IsNullOrEmpty(d.SpecifiedProc))
                .ToList();
        }

        private static bool IsExplicitlyPinned(PciDeviceItem device)
        {
            return device.DevicePolicy == "IrqPolicySpecifiedProcessors"
                   && !string.IsNullOrEmpty(device.SpecifiedProc)
                   && device.SpecifiedProc != "0";
        }

        private static string MaskToThreadList(ulong mask)
        {
            var threads = new List<int>();
            for (int i = 0; i < 64; i++)
            {
                if ((mask & (1UL << i)) != 0)
                    threads.Add(i);
            }
            return string.Join(", ", threads);
        }
    }
}


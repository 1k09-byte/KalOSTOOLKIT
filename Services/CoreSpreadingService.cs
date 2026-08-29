using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

        /// <summary>Bitmask of 2 logical processors (one physical core's SMT pair).</summary>
        public required ulong AffinityMask { get; init; }

        /// <summary>The physical core index that was assigned.</summary>
        public required int PhysicalCoreId { get; init; }

        /// <summary>Human-readable explanation of why this core was chosen.</summary>
        public required string Reason { get; init; }
    }

    /// <summary>
    /// Conservative core-spreading algorithm.
    ///
    /// Rules:
    ///   1. Every device gets exactly 2 logical processors (both SMT threads of one physical core).
    ///   2. Core 0 is never used as a pinning target — it handles system interrupts.
    ///   3. Each physical core is assigned to at most one device — no sharing.
    ///   4. If not enough free cores exist, the device is skipped with a warning.
    ///   5. Already-pinned devices (explicit AssignmentSetOverride) are left alone.
    ///   6. No multi-core spreading, no latency ranking, no NUMA logic — keep it simple.
    /// </summary>
    public class CoreSpreadingService
    {
        private readonly LoggingService _log;

        public CoreSpreadingService(LoggingService log)
        {
            _log = log;
        }

        /// <summary>
        /// Assign 2 logical processors (one physical core) to each device.
        /// </summary>
        /// <param name="devices">Devices to assign cores to.</param>
        /// <param name="topology">CPU topology from AffinityManagerViewModel.DetectCpuTopology().</param>
        /// <returns>Assignments for each device that was successfully assigned.</returns>
        public List<CoreAssignment> AssignCores(
            IReadOnlyList<PciDeviceItem> devices,
            IReadOnlyList<CpuCoreInfo> topology)
        {
            // Filter to Group-0 physical cores, excluding core 0
            var availableCores = topology
                .Where(c => c.ProcessorGroup == 0)
                .Where(c => c.LogicalProcessorMask != 1UL) // skip core 0
                .OrderBy(c => c.CoreId)
                .ToList();

            // Track which cores have been claimed
            var claimedMasks = new HashSet<ulong>();
            var assignments = new List<CoreAssignment>();

            foreach (var device in devices)
            {
                // Skip devices that don't support MSI
                if (!device.MsiSupported || !device.MsiEnabled) continue;

                // Skip devices already explicitly pinned by the user
                if (IsExplicitlyPinned(device))
                {
                    _log.Info($"Skipping {device.Name}: already has explicit pin.");
                    continue;
                }

                // Find the next free physical core
                var freeCore = availableCores
                    .FirstOrDefault(c => !claimedMasks.Contains(c.FullCoreMask));

                if (freeCore == null)
                {
                    _log.Warn($"No free cores for {device.Name} — skipping.");
                    continue;
                }

                // Claim this core
                claimedMasks.Add(freeCore.FullCoreMask);

                assignments.Add(new CoreAssignment
                {
                    Device = device,
                    AffinityMask = freeCore.FullCoreMask,
                    PhysicalCoreId = freeCore.CoreId,
                    Reason = $"Core {freeCore.CoreId} (2 LPs: {MaskToThreadList(freeCore.FullCoreMask)})"
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

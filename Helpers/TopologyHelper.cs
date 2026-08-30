using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KalOS.Helpers
{
    public class TopologyHelper
    {
        public enum LOGICAL_PROCESSOR_RELATIONSHIP
        {
            RelationProcessorCore,
            RelationNumaNode,
            RelationCache,
            RelationProcessorPackage,
            RelationGroup,
            RelationAll = 0xffff
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CACHE_DESCRIPTOR
        {
            public byte Level;
            public byte Associativity;
            public ushort LineSize;
            public uint Size;
            public uint Type; // 0=Unified, 1=Instruction, 2=Data, 3=Trace
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESSOR_RELATIONSHIP
        {
            public byte Flags; // 0 = not SMT, 1 = SMT
            public byte EfficiencyClass; // 0 = E-Core, >0 = P-Core (Win 10/11)
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
            public byte[] Reserved;
            public ushort GroupCount;
            // Variable length array of GROUP_AFFINITY follows, but we assume GroupCount=1 for simple parsing
            public GROUP_AFFINITY GroupMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NUMA_NODE_RELATIONSHIP
        {
            public uint NodeNumber;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
            public byte[] Reserved;
            public GROUP_AFFINITY GroupMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CACHE_RELATIONSHIP
        {
            public byte Level;
            public byte Associativity;
            public ushort LineSize;
            public uint CacheSize;
            public uint Type;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
            public byte[] Reserved;
            public GROUP_AFFINITY GroupMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GROUP_AFFINITY
        {
            public UIntPtr Mask;
            public ushort Group;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public ushort[] Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
        {
            public LOGICAL_PROCESSOR_RELATIONSHIP Relationship;
            public uint Size;
            // The rest is a union. We'll read the union dynamically.
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(
            LOGICAL_PROCESSOR_RELATIONSHIP RelationshipType,
            IntPtr Buffer,
            ref uint ReturnedLength);

        [DllImport("kernel32.dll")]
        public static extern uint GetActiveProcessorCount(ushort GroupNumber);

        public class CoreInfo
        {
            public int LogicalProcessorId { get; set; }
            public int PhysicalCoreId { get; set; }
            public int EfficiencyClass { get; set; }
            public int L3CacheId { get; set; } // Identifies the CCD/CCX it belongs to
            public int NumaNodeId { get; set; } = 0; // NUMA node this core belongs to
            // Windows Processor Group this logical processor belongs to. 0 for normal systems,
            // 1+ only on >64-thread CPUs where Windows splits logical processors across groups.
            // PCI MSI AssignmentSetOverride only addresses Group 0, so non-zero groups must be filtered.
            public ushort ProcessorGroup { get; set; }
        }

        public static List<CoreInfo> GetSystemTopology()
        {
            var cores = new List<CoreInfo>();
            uint returnLength = 0;
            
            // First call gets the required buffer size
            GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationAll, IntPtr.Zero, ref returnLength);
            
            if (returnLength == 0) return cores;

            IntPtr buffer = Marshal.AllocHGlobal((int)returnLength);
            try
            {
                if (GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationAll, buffer, ref returnLength))
                {
                    IntPtr ptr = buffer;
                    int physicalCoreCounter = 0;
                    int l3CacheCounter = 0;
                    
                    var l3CacheMasks = new List<ulong>();
                    // NUMA node → bitmask of logical processors on that node
                    var numaNodeMasks = new List<(uint NodeNumber, ulong Mask)>();

                    // First pass: Find L3 Caches and NUMA Nodes
                    IntPtr ptrPass1 = buffer;
                    while (ptrPass1.ToInt64() < buffer.ToInt64() + returnLength)
                    {
                        var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(ptrPass1);
                        IntPtr unionPtr = new IntPtr(ptrPass1.ToInt64() + 8);

                        if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationCache)
                        {
                            var cache = Marshal.PtrToStructure<CACHE_RELATIONSHIP>(unionPtr);
                            if (cache.Level == 3)
                            {
                                l3CacheMasks.Add((ulong)cache.GroupMask.Mask);
                                l3CacheCounter++;
                            }
                        }
                        else if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationNumaNode)
                        {
                            var numa = Marshal.PtrToStructure<NUMA_NODE_RELATIONSHIP>(unionPtr);
                            numaNodeMasks.Add((numa.NodeNumber, (ulong)numa.GroupMask.Mask));
                        }

                        ptrPass1 = new IntPtr(ptrPass1.ToInt64() + info.Size);
                    }

                    // Second pass: Find Cores
                    while (ptr.ToInt64() < buffer.ToInt64() + returnLength)
                    {
                        var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(ptr);
                        
                        if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                        {
                            IntPtr unionPtr = new IntPtr(ptr.ToInt64() + 8);
                            var processor = Marshal.PtrToStructure<PROCESSOR_RELATIONSHIP>(unionPtr);
                            ulong mask = (ulong)processor.GroupMask.Mask;
                            
                            // Find which L3 cache this core belongs to
                            int l3Id = 0;
                            for (int i = 0; i < l3CacheMasks.Count; i++)
                            {
                                if ((l3CacheMasks[i] & mask) != 0)
                                {
                                    l3Id = i;
                                    break;
                                }
                            }

                            // Determine NUMA node for this core
                            int numaNode = 0;
                            foreach (var (nodeNumber, nodeMask) in numaNodeMasks)
                            {
                                if ((nodeMask & mask) != 0)
                                {
                                    numaNode = (int)nodeNumber;
                                    break;
                                }
                            }

                            // A single physical core can have multiple logical processors (SMT/HyperThreading)
                            for (int i = 0; i < 64; i++)
                            {
                                if ((mask & (1UL << i)) != 0)
                                {
                                    cores.Add(new CoreInfo
                                    {
                                        LogicalProcessorId = i,
                                        PhysicalCoreId = physicalCoreCounter,
                                        EfficiencyClass = processor.EfficiencyClass,
                                        L3CacheId = l3Id,
                                        NumaNodeId = numaNode,
                                        ProcessorGroup = processor.GroupMask.Group
                                    });
                                }
                            }
                            physicalCoreCounter++;
                        }
                        
                        ptr = new IntPtr(ptr.ToInt64() + info.Size);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return cores;
        }

        private static string? _cachedCpuName;

        public static string GetCpuModelName()
        {
            if (_cachedCpuName != null) return _cachedCpuName;

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var name = key?.GetValue("ProcessorNameString") as string;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _cachedCpuName = name.Trim();
                    return _cachedCpuName;
                }
            }
            catch { }

            try
            {
                var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    var name = obj["Name"] as string;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _cachedCpuName = name.Trim();
                        return _cachedCpuName;
                    }
                }
            }
            catch { }

            _cachedCpuName = "Unknown Processor";
            return _cachedCpuName;
        }
    }
}


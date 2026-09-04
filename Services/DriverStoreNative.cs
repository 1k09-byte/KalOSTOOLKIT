using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KalOS.Services
{
    // ═══════════════════════════════════════════════════════════════════
    //  Native DriverStore interop — ISOLATION BOUNDARY (spec section 3)
    //
    //  Ported from DriverStore Explorer (RAPR) by lostindark
    //  (github.com/lostindark/DriverStoreExplorer), Rapr/Utils/
    //  NativeDriverStore.cs + SetupAPI.cs + DeviceHelper.cs, MIT licensed.
    //  drvstore.dll is an UNDOCUMENTED Windows component: the exact struct
    //  layouts and flag values below are community-reverse-engineered and
    //  carry no Microsoft compatibility guarantee. Every P/Invoke into
    //  drvstore.dll / setupapi.dll / newdev.dll lives in THIS file only —
    //  callers go through IDriverStoreProvider, so a future Windows
    //  compatibility break is mitigated by switching providers
    //  (PnputilDriverStoreProvider) without touching the UI.
    // ═══════════════════════════════════════════════════════════════════

    internal enum DevPropType : uint
    {
        Empty = 0x00000000,
        Sbyte = 0x00000002,
        Byte = 0x00000003,
        Int16 = 0x00000004,
        Uint16 = 0x00000005,
        Int32 = 0x00000006,
        Uint32 = 0x00000007,
        Int64 = 0x00000008,
        Uint64 = 0x00000009,
        Guid = 0x0000000d,
        FileTime = 0x00000010,
        Boolean = 0x00000011,
        String = 0x00000012,
        StringList = (String | 0x00002000), // TYPEMOD_LIST
        Binary = (Byte | 0x00001000),       // TYPEMOD_ARRAY
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DevPropKey
    {
        public Guid fmtid;
        public uint pid;

        public DevPropKey(uint a, ushort b, ushort c, byte d, byte e, byte f, byte g, byte h, byte i, byte j, byte k, uint pid)
        {
            fmtid = new Guid(a, b, c, d, e, f, g, h, i, j, k);
            this.pid = pid;
        }
    }

    /// <summary>DEVPKEY definitions used by the DriverStore enumeration (subset from RAPR DeviceHelper.cs).</summary>
    internal static class DriverDevKeys
    {
        internal static readonly DevPropKey DEVPKEY_DeviceClass_Name = new(0x259abffc, 0x50a7, 0x47ce, 0xaf, 0x8, 0x68, 0xc9, 0xa7, 0xd7, 0x33, 0x66, 2);
        internal static readonly DevPropKey DEVPKEY_DeviceClass_BootCritical = new(0x6a3433f4, 0x5626, 0x40e8, 0xa9, 0xb9, 0xdb, 0xd9, 0xec, 0xd2, 0x88, 0x4b, 3);
        internal static readonly DevPropKey DEVPKEY_DriverDatabase_ProcessorArchitecture = new(0x8163eb00, 0x142c, 0x4f7a, 0x94, 0xe1, 0xa2, 0x74, 0xcc, 0x47, 0xdb, 0xba, 3);
        internal static readonly DevPropKey DEVPKEY_DriverPackage_ExtensionId = new(0x8163eb01, 0x142c, 0x4f7a, 0x94, 0xe1, 0xa2, 0x74, 0xcc, 0x47, 0xdb, 0xba, 20);
        internal static readonly DevPropKey DEVPKEY_DriverPackage_ProviderName = new(0x8163eb01, 0x142c, 0x4f7a, 0x94, 0xe1, 0xa2, 0x74, 0xcc, 0x47, 0xdb, 0xba, 12);
        internal static readonly DevPropKey DEVPKEY_DriverPackage_ClassGuid = new(0x8163eb01, 0x142c, 0x4f7a, 0x94, 0xe1, 0xa2, 0x74, 0xcc, 0x47, 0xdb, 0xba, 13);
        internal static readonly DevPropKey DEVPKEY_DriverPackage_SignerName = new(0x8163eb01, 0x142c, 0x4f7a, 0x94, 0xe1, 0xa2, 0x74, 0xcc, 0x47, 0xdb, 0xba, 7);
        internal static readonly DevPropKey DEVPKEY_DriverPackage_DriverDate = new(0x8163eb01, 0x142c, 0x4f7a, 0x94, 0xe1, 0xa2, 0x74, 0xcc, 0x47, 0xdb, 0xba, 14);
        internal static readonly DevPropKey DEVPKEY_DriverPackage_DriverVersion = new(0x8163eb01, 0x142c, 0x4f7a, 0x94, 0xe1, 0xa2, 0x74, 0xcc, 0x47, 0xdb, 0xba, 15);
        internal static readonly DevPropKey DEVPKEY_DriverPackage_ImportDate = new(0x8163eb01, 0x142c, 0x4f7a, 0x94, 0xe1, 0xa2, 0x74, 0xcc, 0x47, 0xdb, 0xba, 26);
        internal static readonly DevPropKey DEVPKEY_Device_InstanceId = new(0x78c34fc8, 0x104a, 0x4aca, 0x9e, 0xa4, 0x52, 0x4d, 0x52, 0x99, 0x6e, 0x57, 256);
        internal static readonly DevPropKey DEVPKEY_Device_DriverDesc = new(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0xc, 0x75, 0xd6, 4);
        internal static readonly DevPropKey DEVPKEY_Device_DriverInfPath = new(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0xc, 0x75, 0xd6, 5);
        internal static readonly DevPropKey DEVPKEY_Device_DriverDate = new(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0xc, 0x75, 0xd6, 2);
        internal static readonly DevPropKey DEVPKEY_Device_DriverVersion = new(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0xc, 0x75, 0xd6, 3);
        internal static readonly DevPropKey DEVPKEY_Device_IsPresent = new(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2, 5);
        internal static readonly DevPropKey DEVPKEY_Device_DriverExtendedInfs = new(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0xc, 0x75, 0xd6, 20);
    }

    internal enum DriverStoreObjectType
    {
        DriverDatabase = 0x00000001,
        DriverPackage = 0x00000002,
        DriverInfFile = 0x00000003,
        DriverFile,
        DeviceId,
        DeviceSetupClass,
        DeviceNode,
        DeviceInterfaceClass,
        DeviceInterface,
        DeviceContainer,
        DriverService,
        DriverRegKey,
        DevicePanel,
    }

    [Flags]
    internal enum DriverStoreEnumFlags : uint
    {
        None = 0,
        InboxOnly = 0x00000001,
        OemOnly = 0x00000002,
        PublishedOnly = 0x00000004,
    }

    internal enum DRIVERSTORE_LOCK_LEVEL
    {
        NONE = 0,
        BASIC_PROTECTED,
        RUNTIME_ISOLATED,
        SYSTEM_PROTECTED,
    }

    [Flags]
    internal enum DriverStoreDeleteFlags : uint
    {
        None = 0,
        INBOX = 0x00000001,
        UNCONFIGURE = 0x00000002,
        UNCONFIGURE_ONLY = 0x00000004,
    }

    [Flags]
    internal enum DriverStoreCopyFlags : uint
    {
        None = 0,
    }

    [Flags]
    internal enum DriverStoreOfflineAddDriverPackageFlags : uint
    {
        None = 0,
        SkipInstall = 0x00000001,
        InstallOnly = 0x00000040,
        ReplacePackage = 0x00000080,
    }

    [Flags]
    internal enum DriverPackageOpenFlags
    {
        None = 0,
        VersionOnly = 0x00000001,
        FilesOnly = 0x00000002,
    }

    [Flags]
    internal enum DriverPackageEnumFilesFlags
    {
        None = 0,
        Copy = 0x00000001,
        Inf = 0x00000010,
        Catalog = 0x00000020,
        Binaries = 0x00000040,
    }

    [Flags]
    internal enum DriverPackageVersionInfoFlags : uint
    {
        None = 0,
        HAS_DEVICE_DRIVERS = 0x00000001,
        PNP_LOCKDOWN = 0x00000002,
        FORCE_BOOT_CRITICAL = 0x00000004,
        FORCE_NOT_BOOT_CRITICAL = 0x00000008,
    }

    internal enum ProcessorArchitecture : ushort
    {
        INTEL = 0,
        MIPS = 1,
        ALPHA = 2,
        PPC = 3,
        SHX = 4,
        ARM = 5,
        IA64 = 6,
        ALPHA64 = 7,
        MSIL = 8,
        AMD64 = 9,
        IA32_ON_WIN64 = 10,
        NEUTRAL = 11,
        ARM64 = 12,
        UNKNOWN = 0xFFFF,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DriverPackageInfo
    {
        public ProcessorArchitecture ProcessorArchitecture;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 85)] public string LocaleName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string PublishedInfName;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DriverPackageVersionInfo
    {
        public uint Size;
        public ProcessorArchitecture Architecture;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 85)] public string LocaleName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ProviderName;
        public System.Runtime.InteropServices.ComTypes.FILETIME DriverDate;
        public ulong DriverVersion;
        public Guid ClassGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ClassName;
        public uint ClassVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string CatalogFile;
        public DriverPackageVersionInfoFlags Flags;
    }

    internal enum DriverFileOperation : uint { Copy = 0, Delete, Rename }
    internal enum DriverFileType : uint { Inf = 0, Catalog, Binary, CopyInf, IncludeInf }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DriverFile
    {
        internal DriverFileOperation Operation;
        internal string ExternalFile;
        internal DriverFileType Type;
        internal uint Flags;
        internal string SourceFile;
        internal string SourcePath;
        internal string DestinationFile;
        internal string DestinationPath;
        internal string ArchiveFile;
        internal string SecurityDescriptor;
        internal string SectionName;
    }

    /// <summary>Device association info filled by SetupAPI (online) or drvstore device-node enum (offline).</summary>
    internal sealed record DeviceDriverInfo(
        string InstanceId,
        string Description,
        string InfPath,
        DateTime? DriverDate,
        Version? DriverVersion,
        bool? IsPresent,
        string[]? ExtendedInfs);

    /// <summary>
    /// The managed P/Invoke layer into drvstore.dll. THIS IS THE ONLY FILE
    /// THAT CALLS drvstore.dll DIRECTLY (plus setupapi/newdev for the online
    /// delete/add paths, matching RAPR's split).
    /// </summary>
    internal static partial class DriverStoreNative
    {
        // ── drvstore.dll ────────────────────────────────────────────────

        [DllImport("drvstore.dll", EntryPoint = "DriverStoreOpenW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr DriverStoreOpen(
            string? targetSystemPath,
            string? targetBootDrive,
            uint flags,
            IntPtr transactionHandle);

        [DllImport("drvstore.dll", SetLastError = true)]
        internal static extern bool DriverStoreClose(IntPtr driverStoreHandle);

        public delegate bool EnumDriverPackageDelegate(
            IntPtr driverStoreHandle,
            [MarshalAs(UnmanagedType.LPWStr, SizeConst = 256)] string driverStoreFilename,
            ref DriverPackageInfo driverPackageInfo,
            IntPtr lParam);

        [DllImport("drvstore.dll", EntryPoint = "DriverStoreEnumW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool DriverStoreEnum(
            IntPtr driverStoreHandle,
            DriverStoreEnumFlags flags,
            EnumDriverPackageDelegate CallbackRoutine,
            IntPtr lParam);

        public delegate bool EnumObjectsDelegate(
            IntPtr hDriverStore,
            DriverStoreObjectType ObjectType,
            [MarshalAs(UnmanagedType.LPWStr, SizeConst = 260)] string objectName,
            IntPtr lParam);

        [DllImport("drvstore.dll", EntryPoint = "DriverStoreEnumObjectsW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool DriverStoreEnumObjects(
            IntPtr hDriverStore,
            DriverStoreObjectType objectType,
            DRIVERSTORE_LOCK_LEVEL flags,
            EnumObjectsDelegate callbackRoutine,
            IntPtr lParam);

        [DllImport("drvstore.dll", EntryPoint = "DriverStoreDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern uint DriverStoreDelete(IntPtr hDriverStore, string driverStoreFilename, DriverStoreDeleteFlags flags);

        [DllImport("drvstore.dll", EntryPoint = "DriverStoreCopyW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern uint DriverStoreCopy(
            IntPtr driverPackageHandle,
            string driverPackageFilename,
            ProcessorArchitecture processorArchitecture,
            IntPtr localeName,
            DriverStoreCopyFlags flags,
            string destinationPath);

        [DllImport("drvstore.dll", EntryPoint = "DriverStoreOfflineAddDriverPackageW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern uint DriverStoreOfflineAddDriverPackage(
            string DriverPackageInfPath,
            DriverStoreOfflineAddDriverPackageFlags Flags,
            IntPtr Reserved,
            ushort ProcessorArchitecture,
            string? LocaleName,
            StringBuilder DestInfPath,
            ref int cchDestInfPath,
            string TargetSystemRoot,
            string TargetSystemDrive);

        [DllImport("drvstore.dll", EntryPoint = "DriverStoreGetObjectPropertyW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool DriverStoreGetObjectProperty(
            IntPtr driverStoreHandle,
            DriverStoreObjectType objectType,
            string objectName,
            ref DevPropKey propertyKey,
            out DevPropType propertyType,
            IntPtr propertyBuffer,
            int bufferSize,
            out uint propertySize,
            uint flag);

        public delegate bool EnumFilesDelegate(IntPtr driverPackageHandle, IntPtr pDriverFile, IntPtr lParam);

        [DllImport("drvstore.dll", EntryPoint = "DriverPackageEnumFilesW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool DriverPackageEnumFilesW(
            IntPtr driverPackageHandle,
            IntPtr enumContext,
            DriverPackageEnumFilesFlags flags,
            EnumFilesDelegate callbackRoutine,
            IntPtr lParam);

        [DllImport("drvstore.dll", EntryPoint = "DriverPackageOpenW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr DriverPackageOpen(
            string driverPackageFilename,
            ProcessorArchitecture processorArchitecture,
            string? localeName,
            DriverPackageOpenFlags flags,
            IntPtr resolveContext);

        [DllImport("drvstore.dll", EntryPoint = "DriverPackageGetVersionInfoW", SetLastError = true)]
        internal static extern bool DriverPackageGetVersionInfo(IntPtr driverPackageHandle, IntPtr pVersionInfo);

        [DllImport("drvstore.dll", SetLastError = true)]
        internal static extern void DriverPackageClose(IntPtr driverPackageHandle);

        // ── setupapi.dll / newdev.dll (documented; online delete/add) ──

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            private IntPtr Reserved;
        }

        internal const uint DIGCF_ALLCLASSES = 0x00000004;
        internal const int ERROR_NO_MORE_ITEMS = 259;

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, int memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceProperty(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            ref DevPropKey propertyKey,
            out DevPropType propertyType,
            IntPtr propertyBuffer,
            int propertyBufferSize,
            out uint requiredSize,
            uint flags);

        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool DiInstallDriver([In] IntPtr hwndParent, [In] string infPath, uint flags, [Out] out bool needReboot);

        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool DiUninstallDriver([In] IntPtr hwndParent, [In] string infPath, uint flags, [Out] out bool needReboot);

        internal const uint SPOST_PATH = 1;

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupCopyOEMInf(
            [MarshalAs(UnmanagedType.LPWStr)] string infName,
            [MarshalAs(UnmanagedType.LPWStr)] string? sourceMediaLocation,
            uint sourceMediaType,
            uint copyStyle,
            [In, Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder destInfFullPath,
            uint destInfFullPathSize,
            out uint requiredSize,
            IntPtr destInfFilename);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupUninstallOEMInf(
            [MarshalAs(UnmanagedType.LPWStr)] string infName,
            uint flags,
            IntPtr reserved);

        internal const uint SUOI_FORCEDELETE = 0x0001;
        internal const uint DIURFLAG_NO_REMOVE_INF = 0x00000001;
    }
}

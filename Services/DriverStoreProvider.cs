using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KaliteKit.Models;

namespace KaliteKit.Services
{
    /// <summary>
    /// Abstraction over the DriverStore so the UI/ViewModel never touches
    /// native interop directly (spec section 3). Two implementations:
    /// <see cref="NativeDriverStoreProvider"/> (drvstore.dll + SetupAPI —
    /// RAPR parity, primary) and <see cref="PnputilDriverStoreProvider"/>
    /// (shells out to pnputil.exe — supported-OS-component fallback).
    /// </summary>
    public interface IDriverStoreProvider
    {
        DriverStoreTarget Target { get; }

        /// <summary>Offline image root (empty for online).</summary>
        string OfflineRoot { get; }

        /// <summary>True when running through the pnputil safety net rather than native drvstore.</summary>
        bool IsFallback { get; }

        /// <summary>Force deletion of in-use drivers is only possible online.</summary>
        bool SupportsForceDelete { get; }

        /// <summary>Install onto connected devices is only possible online.</summary>
        bool SupportsInstallToDevice { get; }

        /// <summary>
        /// Enumerate all third-party (OEM) packages — and inbox packages when
        /// <paramref name="includeInbox"/> is set. Synchronous; callers run it
        /// on a background thread (spec 6.2).
        /// </summary>
        List<DriverPackageRecord> EnumeratePackages(bool includeInbox);

        void DeleteDriver(DriverPackageRecord package, bool forceDelete);

        /// <summary>Add (stage) a package; when <paramref name="install"/> is set also install it onto matching devices (online only).</summary>
        void AddDriver(string infFullPath, bool install);

        void ExportDriver(DriverPackageRecord package, string destinationPath);

        /// <summary>Referenced binary files of a package (for export verification, spec 10.2).</summary>
        IReadOnlyList<string> GetDriverFiles(DriverPackageRecord package);
    }

    /// <summary>Thrown when the store reports a failure the UI should describe precisely.</summary>
    public sealed class DriverStoreException : Exception
    {
        public int NativeErrorCode { get; }
        public DriverStoreException(string message, int errorCode) : base(message) => NativeErrorCode = errorCode;
    }

    /// <summary>Validates offline image roots (spec 5.1) — a shared helper, pure and testable.</summary>
    public static class OfflineStoreValidator
    {
        /// <summary>True when <paramref name="path"/> contains a Windows\System32\DriverStore structure.</summary>
        public static bool IsValidOfflineRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return Directory.Exists(Path.Combine(path, "Windows", "System32", "DriverStore"));
            }
            catch
            {
                return false;
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  Primary provider — drvstore.dll + SetupAPI (RAPR port)
    // ═════════════════════════════════════════════════════════════════

    public sealed class NativeDriverStoreProvider : IDriverStoreProvider
    {
        private const int MAX_PATH = 260;

        public DriverStoreTarget Target { get; }
        public string OfflineRoot { get; }
        public bool IsFallback => false;
        public bool SupportsForceDelete => Target == DriverStoreTarget.Online;
        public bool SupportsInstallToDevice => Target == DriverStoreTarget.Online;

        public NativeDriverStoreProvider() { Target = DriverStoreTarget.Online; OfflineRoot = string.Empty; }

        public NativeDriverStoreProvider(string offlineRoot)
        {
            if (!OfflineStoreValidator.IsValidOfflineRoot(offlineRoot))
                throw new ArgumentException($"'{offlineRoot}' does not contain a Windows\\System32\\DriverStore structure.");
            Target = DriverStoreTarget.Offline;
            OfflineRoot = offlineRoot;
        }

        private IntPtr OpenDriverStore()
        {
            IntPtr ptr = Target == DriverStoreTarget.Online
                ? DriverStoreNative.DriverStoreOpen(null, null, 0, IntPtr.Zero)
                : DriverStoreNative.DriverStoreOpen(Path.Combine(OfflineRoot, "Windows"), OfflineRoot, 0, IntPtr.Zero);
            if (ptr == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open the driver store.");
            return ptr;
        }

        public List<DriverPackageRecord> EnumeratePackages(bool includeInbox)
        {
            var ptr = OpenDriverStore();
            var entries = new List<DriverPackageRecord>();
            try
            {
                var handle = GCHandle.Alloc(entries);
                try
                {
                    DriverStoreNative.DriverStoreEnum(
                        ptr,
                        includeInbox ? DriverStoreEnumFlags.None : DriverStoreEnumFlags.OemOnly,
                        EnumDriverPackagesCallback,
                        GCHandle.ToIntPtr(handle));
                }
                finally { handle.Free(); }

                // Device association: online uses the live PnP manager
                // (SetupAPI); offline uses the store's own device-node
                // database — two genuinely different mechanisms (spec 4).
                if (Target == DriverStoreTarget.Online)
                {
                    var devices = SetupApiDeviceAssociation.EnumerateDevices();
                    FillDeviceInfo(entries, devices);
                }
                else
                {
                    var devices = new List<DeviceDriverInfo>();
                    var devHandle = GCHandle.Alloc(devices);
                    try
                    {
                        DriverStoreNative.DriverStoreEnumObjects(
                            ptr, DriverStoreObjectType.DeviceNode, DRIVERSTORE_LOCK_LEVEL.NONE,
                            EnumDeviceNodesCallback, GCHandle.ToIntPtr(devHandle));
                    }
                    finally { devHandle.Free(); }
                    FillDeviceInfo(entries, devices);
                }
            }
            finally
            {
                DriverStoreNative.DriverStoreClose(ptr);
            }
            return entries;
        }

        private static bool EnumDeviceNodesCallback(IntPtr hDriverStore, DriverStoreObjectType type, string objectName, IntPtr lParam)
        {
            try
            {
                var devices = (List<DeviceDriverInfo>)GCHandle.FromIntPtr(lParam).Target!;
                devices.Add(new DeviceDriverInfo(
                    GetObjectProperty<string>(hDriverStore, objectName, DriverDevKeys.DEVPKEY_Device_InstanceId, DriverStoreObjectType.DeviceNode) ?? string.Empty,
                    GetObjectProperty<string>(hDriverStore, objectName, DriverDevKeys.DEVPKEY_Device_DriverDesc, DriverStoreObjectType.DeviceNode) ?? string.Empty,
                    GetObjectProperty<string>(hDriverStore, objectName, DriverDevKeys.DEVPKEY_Device_DriverInfPath, DriverStoreObjectType.DeviceNode) ?? string.Empty,
                    GetObjectProperty<DateTime?>(hDriverStore, objectName, DriverDevKeys.DEVPKEY_Device_DriverDate, DriverStoreObjectType.DeviceNode),
                    GetObjectProperty<Version?>(hDriverStore, objectName, DriverDevKeys.DEVPKEY_Device_DriverVersion, DriverStoreObjectType.DeviceNode),
                    GetObjectProperty<bool?>(hDriverStore, objectName, DriverDevKeys.DEVPKEY_Device_IsPresent, DriverStoreObjectType.DeviceNode),
                    null));
            }
            catch
            {
                // A single unreadable device node must not break enumeration.
            }
            return true;
        }

        private static bool EnumDriverPackagesCallback(IntPtr driverStoreHandle, string driverStoreFilename, ref DriverPackageInfo packageInfo, IntPtr lParam)
        {
            try
            {
                var entries = (List<DriverPackageRecord>)GCHandle.FromIntPtr(lParam).Target!;
                var classGuid = GetObjectProperty<Guid>(driverStoreHandle, driverStoreFilename, DriverDevKeys.DEVPKEY_DriverPackage_ClassGuid);
                string folder = Path.GetDirectoryName(driverStoreFilename) ?? string.Empty;

                // Boot-critical: version-info force flags, else the setup
                // class's BootCritical property, else false (spec 7.1 source).
                bool bootCritical =
                    GetBootCriticalFromVersionInfo(driverStoreHandle, driverStoreFilename)
                    ?? GetObjectProperty<bool?>(driverStoreHandle, classGuid.ToString("B"), DriverDevKeys.DEVPKEY_DeviceClass_BootCritical, DriverStoreObjectType.DeviceSetupClass)
                    ?? false;

                entries.Add(new DriverPackageRecord
                {
                    DriverClass = GetObjectProperty<string>(driverStoreHandle, classGuid.ToString("B"), DriverDevKeys.DEVPKEY_DeviceClass_Name, DriverStoreObjectType.DeviceSetupClass) ?? string.Empty,
                    ClassGuid = classGuid,
                    InfName = Path.GetFileName(driverStoreFilename) ?? string.Empty,
                    PublishedName = packageInfo.PublishedInfName ?? string.Empty,
                    Provider = GetObjectProperty<string>(driverStoreHandle, driverStoreFilename, DriverDevKeys.DEVPKEY_DriverPackage_ProviderName) ?? string.Empty,
                    Signer = GetObjectProperty<string>(driverStoreHandle, driverStoreFilename, DriverDevKeys.DEVPKEY_DriverPackage_SignerName) ?? string.Empty,
                    ExtensionId = GetObjectProperty<Guid>(driverStoreHandle, driverStoreFilename, DriverDevKeys.DEVPKEY_DriverPackage_ExtensionId).ToString(),
                    DriverDate = GetObjectProperty<DateTime?>(driverStoreHandle, driverStoreFilename, DriverDevKeys.DEVPKEY_DriverPackage_DriverDate),
                    DriverVersion = GetObjectProperty<Version?>(driverStoreHandle, driverStoreFilename, DriverDevKeys.DEVPKEY_DriverPackage_DriverVersion),
                    FolderLocation = folder,
                    BootCritical = bootCritical,
                    InstallDate = GetObjectProperty<DateTime?>(driverStoreHandle, driverStoreFilename, DriverDevKeys.DEVPKEY_DriverPackage_ImportDate),
                    OfflineRoot = string.Empty,
                });
            }
            catch
            {
                // One malformed package must not break the whole enumeration.
            }
            return true;
        }

        private static void FillDeviceInfo(List<DriverPackageRecord> entries, List<DeviceDriverInfo> devices)
        {
            var byInf = devices
                .Where(d => !string.IsNullOrEmpty(d.InfPath))
                .GroupBy(d => Path.GetFileName(d.InfPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var e in entries)
            {
                if (byInf.TryGetValue(e.PublishedName, out var list))
                {
                    e.AssociatedDevices = list
                        .Select(d => new AssociatedDevice(d.InstanceId, d.Description, d.IsPresent ?? false))
                        .ToList();
                }
            }
        }

        /// <summary>Boot-critical from version-info force flags; null when undetermined.</summary>
        internal static bool? GetBootCriticalFromVersionInfo(IntPtr storeHandle, string packageInfPath)
        {
            var arch = GetProcessorArchitecture(storeHandle);
            IntPtr pkg = DriverStoreNative.DriverPackageOpen(packageInfPath, arch, null, DriverPackageOpenFlags.VersionOnly, IntPtr.Zero);
            if (pkg == IntPtr.Zero) return null;
            try
            {
                int size = Marshal.SizeOf<DriverPackageVersionInfo>();
                IntPtr pInfo = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(pInfo, size);
                    if (DriverStoreNative.DriverPackageGetVersionInfo(pkg, pInfo))
                    {
                        var info = Marshal.PtrToStructure<DriverPackageVersionInfo>(pInfo);
                        if (info.Flags.HasFlag(DriverPackageVersionInfoFlags.FORCE_BOOT_CRITICAL)) return true;
                        if (info.Flags.HasFlag(DriverPackageVersionInfoFlags.FORCE_NOT_BOOT_CRITICAL)) return false;
                    }
                }
                finally { Marshal.FreeHGlobal(pInfo); }
            }
            finally { DriverStoreNative.DriverPackageClose(pkg); }
            return null;
        }

        internal static ProcessorArchitecture GetProcessorArchitecture(IntPtr storeHandle)
        {
            var buffer = Marshal.AllocHGlobal(sizeof(short));
            try
            {
                var key = DriverDevKeys.DEVPKEY_DriverDatabase_ProcessorArchitecture;
                DriverStoreNative.DriverStoreGetObjectProperty(
                    storeHandle, DriverStoreObjectType.DriverDatabase, "SYSTEM", ref key,
                    out _, buffer, sizeof(short), out _, 0);
                return (ProcessorArchitecture)Marshal.ReadInt16(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static T? GetObjectProperty<T>(IntPtr storeHandle, string objectName, in DevPropKey key, DriverStoreObjectType objectType = DriverStoreObjectType.DriverPackage)
        {
            const int bufferSize = 2048;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var k = key;
                if (DriverStoreNative.DriverStoreGetObjectProperty(
                        storeHandle, objectType, objectName, ref k, out var propType,
                        buffer, bufferSize, out uint propertySize, 0) && propertySize > 0)
                {
                    return DriverStorePropertyConversion.Convert<T>(buffer, propType);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return default;
        }

        private static bool EnumBinaryFilesCallback(IntPtr pkgHandle, IntPtr pDriverFile, IntPtr lParam)
        {
            try
            {
                var files = (List<string>)GCHandle.FromIntPtr(lParam).Target!;
                var file = Marshal.PtrToStructure<DriverFile>(pDriverFile);
                if (file.Type == DriverFileType.Binary && !string.IsNullOrEmpty(file.DestinationFile))
                    files.Add(file.DestinationFile);
            }
            catch { }
            return true;
        }

        public IReadOnlyList<string> GetDriverFiles(DriverPackageRecord package)
        {
            var ptr = OpenDriverStore();
            try
            {
                var arch = GetProcessorArchitecture(ptr);
                string infPath = Path.Combine(package.FolderLocation, package.InfName);
                IntPtr pkg = DriverStoreNative.DriverPackageOpen(infPath, arch, null, DriverPackageOpenFlags.FilesOnly, IntPtr.Zero);
                var files = new List<string>();
                if (pkg == IntPtr.Zero) return files;
                try
                {
                    var handle = GCHandle.Alloc(files);
                    try
                    {
                        DriverStoreNative.DriverPackageEnumFilesW(
                            pkg, IntPtr.Zero,
                            DriverPackageEnumFilesFlags.Binaries | DriverPackageEnumFilesFlags.Copy,
                            EnumBinaryFilesCallback, GCHandle.ToIntPtr(handle));
                    }
                    finally { handle.Free(); }
                }
                finally { DriverStoreNative.DriverPackageClose(pkg); }
                return files;
            }
            finally { DriverStoreNative.DriverStoreClose(ptr); }
        }

        public void DeleteDriver(DriverPackageRecord package, bool forceDelete)
        {
            if (Target == DriverStoreTarget.Online)
            {
                // Force: DiUninstallDriverW (newdev) first — unconfigures the
                // driver from devices without removing the INF — then the
                // force-delete of the staged package itself (RAPR flow).
                if (forceDelete)
                {
                    string infPath = Path.Combine(package.FolderLocation, package.InfName);
                    if (!DriverStoreNative.DiUninstallDriver(IntPtr.Zero, infPath, DriverStoreNative.DIURFLAG_NO_REMOVE_INF, out _))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != 0 && err != 2) // 2 = no matching device: still delete below
                            throw new DriverStoreException($"Failed to uninstall driver from devices (error {err}).", err);
                    }
                }
                if (!DriverStoreNative.SetupUninstallOEMInf(
                        package.PublishedName,
                        forceDelete ? DriverStoreNative.SUOI_FORCEDELETE : 0,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            else
            {
                var ptr = OpenDriverStore();
                try
                {
                    string infPath = Path.Combine(package.FolderLocation, package.InfName);
                    uint status = DriverStoreNative.DriverStoreDelete(ptr, infPath, DriverStoreDeleteFlags.UNCONFIGURE);
                    if (status != 0) throw new DriverStoreException($"DriverStoreDelete failed (error {status}).", (int)status);
                }
                finally { DriverStoreNative.DriverStoreClose(ptr); }
            }
        }

        public void AddDriver(string infFullPath, bool install)
        {
            if (!File.Exists(infFullPath))
                throw new FileNotFoundException($"INF not found: {infFullPath}");

            if (Target == DriverStoreTarget.Online)
            {
                if (install)
                {
                    if (!DriverStoreNative.DiInstallDriver(IntPtr.Zero, infFullPath, 0, out _))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                else
                {
                    var dest = new StringBuilder(MAX_PATH);
                    if (!DriverStoreNative.SetupCopyOEMInf(infFullPath, null, DriverStoreNative.SPOST_PATH, 0, dest, (uint)dest.Capacity, out _, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            else
            {
                // Offline pre-staging: resolve the image's own processor
                // architecture — do NOT assume the host's (spec 5.3).
                string targetSystemRoot = Path.Combine(OfflineRoot, "Windows");
                var ptr = OpenDriverStore();
                ushort arch;
                try { arch = (ushort)GetProcessorArchitecture(ptr); }
                finally { DriverStoreNative.DriverStoreClose(ptr); }

                var dest = new StringBuilder(MAX_PATH);
                int cch = dest.Capacity;
                uint status = DriverStoreNative.DriverStoreOfflineAddDriverPackage(
                    infFullPath, DriverStoreOfflineAddDriverPackageFlags.None,
                    IntPtr.Zero, arch, null, dest, ref cch, targetSystemRoot, OfflineRoot);
                if (status != 0) throw new DriverStoreException($"Offline add failed (error {status}).", (int)status);
            }
        }

        public void ExportDriver(DriverPackageRecord package, string destinationPath)
        {
            string targetPath = Path.Combine(destinationPath, package.BackupFolderName);
            Directory.CreateDirectory(targetPath);

            var ptr = OpenDriverStore();
            try
            {
                var arch = GetProcessorArchitecture(ptr);
                string infPath = Path.Combine(package.FolderLocation, package.InfName);
                uint status = DriverStoreNative.DriverStoreCopy(ptr, infPath, arch, IntPtr.Zero, DriverStoreCopyFlags.None, targetPath);
                if (status != 0) throw new DriverStoreException($"Export of '{package.InfName}' failed (error {status}).", (int)status);
            }
            finally { DriverStoreNative.DriverStoreClose(ptr); }
        }
    }

    /// <summary>DevProp buffer → managed type conversion (from RAPR DeviceHelper.ConvertPropToType).</summary>
    internal static class DriverStorePropertyConversion
    {
        public static T? Convert<T>(IntPtr buffer, DevPropType type)
        {
            if (type == DevPropType.String && typeof(T) == typeof(string))
                return (T)(object)(Marshal.PtrToStringUni(buffer) ?? string.Empty);
            if (type == DevPropType.FileTime && (typeof(T) == typeof(DateTime) || typeof(T) == typeof(DateTime?)))
            {
                var ft = (System.Runtime.InteropServices.ComTypes.FILETIME)Marshal.PtrToStructure(buffer, typeof(System.Runtime.InteropServices.ComTypes.FILETIME))!;
                long fileTime = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
                return (T)(object)DateTime.FromFileTimeUtc(fileTime);
            }
            if (type == DevPropType.Uint64 && typeof(T) == typeof(Version))
            {
                ulong v = (ulong)Marshal.ReadInt64(buffer);
                return (T)(object)new Version(
                    (int)((v >> 48) & 0xFFFF), (int)((v >> 32) & 0xFFFF),
                    (int)((v >> 16) & 0xFFFF), (int)(v & 0xFFFF));
            }
            if (type == DevPropType.Uint64 && typeof(T) == typeof(ulong))
                return (T)(object)(ulong)Marshal.ReadInt64(buffer);
            if (type == DevPropType.Uint32 && typeof(T) == typeof(uint))
                return (T)(object)(uint)Marshal.ReadInt32(buffer);
            if (type == DevPropType.Uint16 && typeof(T) == typeof(ushort))
                return (T)(object)(ushort)Marshal.ReadInt16(buffer);
            if (type == DevPropType.Guid && typeof(T) == typeof(Guid))
                return (T)Marshal.PtrToStructure(buffer, typeof(Guid))!;
            if (type == DevPropType.Boolean && (typeof(T) == typeof(bool) || typeof(T) == typeof(bool?)))
                return (T)(object)(Marshal.ReadByte(buffer) != 0);
            return default;
        }
    }

    /// <summary>
    /// Device enumeration via the documented SetupAPI — shared by both
    /// providers for associating present devices with driver packages.
    /// </summary>
    internal static class SetupApiDeviceAssociation
    {
        public static List<DeviceDriverInfo> EnumerateDevices()
        {
            var results = new List<DeviceDriverInfo>();
            var set = DriverStoreNative.SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DriverStoreNative.DIGCF_ALLCLASSES);
            if (set == new IntPtr(-1) || set == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed.");
            try
            {
                var info = new DriverStoreNative.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<DriverStoreNative.SP_DEVINFO_DATA>() };
                for (int i = 0; DriverStoreNative.SetupDiEnumDeviceInfo(set, i, ref info); i++)
                {
                    try
                    {
                        results.Add(new DeviceDriverInfo(
                            GetProp<string>(set, ref info, DriverDevKeys.DEVPKEY_Device_InstanceId) ?? string.Empty,
                            GetProp<string>(set, ref info, DriverDevKeys.DEVPKEY_Device_DriverDesc) ?? string.Empty,
                            GetProp<string>(set, ref info, DriverDevKeys.DEVPKEY_Device_DriverInfPath) ?? string.Empty,
                            GetProp<DateTime?>(set, ref info, DriverDevKeys.DEVPKEY_Device_DriverDate),
                            GetProp<Version?>(set, ref info, DriverDevKeys.DEVPKEY_Device_DriverVersion),
                            GetProp<bool?>(set, ref info, DriverDevKeys.DEVPKEY_Device_IsPresent),
                            null));
                    }
                    catch { }
                }
            }
            finally { DriverStoreNative.SetupDiDestroyDeviceInfoList(set); }
            return results;
        }

        private static T? GetProp<T>(IntPtr set, ref DriverStoreNative.SP_DEVINFO_DATA info, in DevPropKey key)
        {
            const int size = 2048;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                var k = key;
                if (DriverStoreNative.SetupDiGetDeviceProperty(set, ref info, ref k, out var type, buffer, size, out uint required, 0) && required > 0)
                    return DriverStorePropertyConversion.Convert<T>(buffer, type);
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return default;
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  Fallback provider — pnputil.exe (shipped, supported OS component)
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parser for <c>pnputil /enum-drivers</c> text output. Pure and unit-testable.
    /// Labels are localized on non-English systems, so fields are anchored on
    /// value shape (oemNN.inf) and stable per-block field ORDER, not label text.
    /// </summary>
    public static class PnputilParser
    {
        private static readonly Regex PublishedNameRegex = new(@"^oem\d+\.inf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VersionRegex = new(@"^(?<date>\d{1,2}/\d{1,2}/\d{4})\s+(?<ver>\d+(\.\d+){1,3})$", RegexOptions.Compiled);
        private static readonly Regex GuidRegex = new(@"^\{[0-9a-fA-F\-]{36}\}$", RegexOptions.Compiled);

        public static List<DriverPackageRecord> Parse(string output)
        {
            var results = new List<DriverPackageRecord>();
            if (string.IsNullOrWhiteSpace(output)) return results;

            foreach (var block in output.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();

                // A driver block contains at least: published name, provider, class guid, version, signer.
                if (lines.Count < 5) continue;

                // Anchor: the line whose VALUE looks like oemNN.inf is the published name.
                int publishedIndex = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    var value = ValueOf(lines[i]);
                    if (value is not null && PublishedNameRegex.IsMatch(value)) { publishedIndex = i; break; }
                }
                if (publishedIndex < 0) continue;

                string published = ValueOf(lines[publishedIndex])!;
                string? original = null, provider = null, className = null, classGuid = null, versionLine = null, signer = null;

                int fieldsSeen = 0;
                for (int i = publishedIndex + 1; i < lines.Count; i++)
                {
                    string line = lines[i];
                    string value = ValueOf(line) ?? string.Empty;
                    fieldsSeen++;

                    switch (fieldsSeen)
                    {
                        case 1: original = value; break;
                        case 2: provider = value; break;
                        case 3: className = value; break;
                        case 4:
                            // Newer pnputil emits "Class Version" here; skip it.
                            if (GuidRegex.IsMatch(value)) { classGuid = value; fieldsSeen = 10; }
                            break;
                        case 5:
                            if (GuidRegex.IsMatch(value)) { classGuid = value; }
                            break;
                    }

                    // Driver version line: "MM/DD/YYYY a.b.c.d" — position varies.
                    var m = VersionRegex.Match(value);
                    if (m.Success) versionLine = value;

                    // Signer: last line of the block.
                    if (i == lines.Count - 1) signer = value;
                }

                if (classGuid is null) continue;

                DateTime? date = null;
                Version? version = null;
                if (versionLine is not null)
                {
                    var m = VersionRegex.Match(versionLine);
                    if (m.Success)
                    {
                        date = DateTime.TryParse(m.Groups["date"].Value, CultureInfo.InvariantCulture.DateTimeFormat, DateTimeStyles.None, out var d) ? d : null;
                        version = Version.TryParse(m.Groups["ver"].Value, out var v) ? v : null;
                    }
                }

                // pnputil cannot report boot-critical status, install date,
                // extension id, or file lists — those limitations are surfaced
                // in the UI when this fallback is active.
                results.Add(new DriverPackageRecord
                {
                    PublishedName = published,
                    OriginalInfName = original ?? string.Empty,
                    InfName = published,
                    Provider = provider ?? string.Empty,
                    Signer = signer ?? string.Empty,
                    DriverClass = className ?? string.Empty,
                    ClassGuid = Guid.TryParse(classGuid, out var g) ? g : Guid.Empty,
                    DriverDate = date,
                    DriverVersion = version,
                    FolderLocation = string.Empty,
                    IsInbox = IsInboxLikeSigner(signer),
                    InstallDate = null,
                });
            }
            return results;
        }

        /// <summary>Strip "Label:" prefix — handles the localized label having any text before the colon.</summary>
        private static string? ValueOf(string line)
        {
            int colon = line.IndexOf(':');
            return colon >= 0 && colon < line.Length - 1 ? line[(colon + 1)..].Trim() : null;
        }

        private static bool IsInboxLikeSigner(string? signer) =>
            signer is not null && signer.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase)
            && !signer.Contains("Hardware Compatibility", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class PnputilDriverStoreProvider : IDriverStoreProvider
    {
        private readonly ProcessManager _processManager;
        public DriverStoreTarget Target => DriverStoreTarget.Online;
        public string OfflineRoot => string.Empty;
        public bool IsFallback => true;
        public bool SupportsForceDelete => true;
        public bool SupportsInstallToDevice => true;

        public PnputilDriverStoreProvider(ProcessManager processManager) => _processManager = processManager;

        public List<DriverPackageRecord> EnumeratePackages(bool includeInbox)
        {
            var (output, exitCode) = _processManager.RunWithOutputAsync("pnputil.exe", "/enum-drivers").GetAwaiter().GetResult();
            if (exitCode != 0)
                throw new DriverStoreException($"pnputil /enum-drivers failed (exit {exitCode}).", exitCode);

            var packages = PnputilParser.Parse(output);
            if (!includeInbox)
                packages = packages.Where(p => !IsInboxLike(p)).ToList();

            // Device association still via documented SetupAPI (shared helper).
            var devices = SetupApiDeviceAssociation.EnumerateDevices();
            var byInf = devices
                .Where(d => !string.IsNullOrEmpty(d.InfPath))
                .GroupBy(d => Path.GetFileName(d.InfPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var p in packages)
            {
                if (byInf.TryGetValue(p.PublishedName, out var list))
                    p.AssociatedDevices = list.Select(d => new AssociatedDevice(d.InstanceId, d.Description, d.IsPresent ?? false)).ToList();
            }
            return packages;
        }

        internal static bool IsInboxLike(DriverPackageRecord p) =>
            p.Signer.Equals("Microsoft Windows", StringComparison.OrdinalIgnoreCase)
            || p.Provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase);

        public void DeleteDriver(DriverPackageRecord package, bool forceDelete)
        {
            string args = $"/delete-driver {package.PublishedName}" + (forceDelete ? " /force" : string.Empty);
            var (_, exitCode) = _processManager.RunWithOutputAsync("pnputil.exe", args).GetAwaiter().GetResult();
            if (exitCode != 0)
                throw new DriverStoreException($"pnputil {args} failed (exit {exitCode}).", exitCode);
        }

        public void AddDriver(string infFullPath, bool install)
        {
            string args = $"/add-driver {infFullPath}" + (install ? " /install" : string.Empty);
            var (_, exitCode) = _processManager.RunWithOutputAsync("pnputil.exe", args).GetAwaiter().GetResult();
            if (exitCode != 0)
                throw new DriverStoreException($"pnputil {args} failed (exit {exitCode}).", exitCode);
        }

        public void ExportDriver(DriverPackageRecord package, string destinationPath)
        {
            string targetPath = Path.Combine(destinationPath, package.BackupFolderName);
            Directory.CreateDirectory(targetPath);
            var (_, exitCode) = _processManager.RunWithOutputAsync("pnputil.exe", $"/export-driver {package.PublishedName} \"{targetPath}\"").GetAwaiter().GetResult();
            if (exitCode != 0)
                throw new DriverStoreException($"pnputil /export-driver {package.PublishedName} failed (exit {exitCode}).", exitCode);
        }

        public IReadOnlyList<string> GetDriverFiles(DriverPackageRecord package)
        {
            // pnputil cannot enumerate a package's referenced binaries. The
            // folder walk below is the best-effort equivalent for the
            // fallback (the store folder IS the package's files).
            if (string.IsNullOrEmpty(package.FolderLocation) || !Directory.Exists(package.FolderLocation))
                return Array.Empty<string>();
            try
            {
                return Directory.EnumerateFiles(package.FolderLocation, "*", SearchOption.AllDirectories).ToList();
            }
            catch { return Array.Empty<string>(); }
        }
    }
}

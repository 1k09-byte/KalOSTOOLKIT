using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using KalOS.Models.ProcessControl;

namespace KalOS.Services;

/// <summary>
/// Thin, isolated P/Invoke layer for per-process scheduling control and
/// system power/CPU sampling. Everything Win32 lives here so the engine and
/// tests never touch raw interop.
/// </summary>
internal static class ProcessControlNative
{
    // ── Access rights / priority classes ────────────────────────────────

    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint THREAD_SUSPEND_RESUME = 0x0002;

    private const uint IDLE_PRIORITY_CLASS = 0x00000040;
    private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    private const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    private const uint HIGH_PRIORITY_CLASS = 0x00000080;
    private const uint REALTIME_PRIORITY_CLASS = 0x00000100;

    // kernel32!SetProcessInformation class codes (PROCESS_INFORMATION_CLASS):
    // memory priority is class 0; I/O priority is NOT exposed by kernel32 at
    // all — it requires ntdll!NtSetInformationProcess (class 0x21). Passing
    // NT codes (33/36) to the kernel32 API fails with ERROR_INVALID_PARAMETER(87).
    private const int ProcessMemoryPriority = 0;
    private const int NtProcessIoPriority = 0x21;

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_INVALID_DATA = 0xC0000BC6;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int processInformationClass,
        ref uint processInformation, uint processInformationSize);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr process, int processInformationClass,
        ref uint processInformation, uint processInformationSize);

    // Whole-process suspension in one syscall — unlike a thread-walk it also
    // covers threads created mid-suspend, and on Windows builds whose security
    // baseline blocks cross-process SuspendThread it is the only path that works.
    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessAffinityMask(IntPtr process, IntPtr mask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(IntPtr process, out IntPtr processMask, out IntPtr systemMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSets(IntPtr process, uint[]? cpuSetIds, uint cpuSetCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessDefaultCpuSets(IntPtr process, uint[]? cpuSetIds, uint cpuSetCount, ref uint returnedCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint informationLength,
        ref uint returnedLength, IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Thread32First(IntPtr snapshot, ref THREADENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Thread32Next(IntPtr snapshot, ref THREADENTRY32 entry);

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public long tpBasePri;
        public long tpDeltaPri;
        public uint dwFlags;
    }

    private const uint TH32CS_SNAPTHREAD = 0x00000004;
    private const uint INVALID_HANDLE_VALUE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    /// <summary>PID of the process owning the foreground window (0 = none).</summary>
    public static int GetForegroundPid()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out int pid);
        return pid;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    // ── Generic helpers ─────────────────────────────────────────────────

    private static IntPtr OpenProcessHandle(int pid, uint access)
        => OpenProcess(access, false, pid);

    /// <summary>Maps a CPU priority level to its Win32 class constant.</summary>
    public static uint ToPriorityClass(CpuPriorityLevel level) => level switch
    {
        CpuPriorityLevel.Idle => IDLE_PRIORITY_CLASS,
        CpuPriorityLevel.BelowNormal => BELOW_NORMAL_PRIORITY_CLASS,
        CpuPriorityLevel.Normal => NORMAL_PRIORITY_CLASS,
        CpuPriorityLevel.AboveNormal => ABOVE_NORMAL_PRIORITY_CLASS,
        CpuPriorityLevel.High => HIGH_PRIORITY_CLASS,
        CpuPriorityLevel.Realtime => REALTIME_PRIORITY_CLASS,
        _ => NORMAL_PRIORITY_CLASS,
    };

    /// <summary>Maps a Win32 class constant back to a display name.</summary>
    public static string PriorityClassName(uint cls) => cls switch
    {
        IDLE_PRIORITY_CLASS => "Idle",
        BELOW_NORMAL_PRIORITY_CLASS => "Below Normal",
        NORMAL_PRIORITY_CLASS => "Normal",
        ABOVE_NORMAL_PRIORITY_CLASS => "Above Normal",
        HIGH_PRIORITY_CLASS => "High",
        REALTIME_PRIORITY_CLASS => "Realtime",
        _ => $"0x{cls:X8}",
    };

    private const uint AccessAll = PROCESS_TERMINATE | PROCESS_SUSPEND_RESUME |
        PROCESS_SET_INFORMATION | PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION;

    // ── Per-process setters (each opens/closes its own handle) ──────────

    public static bool TrySetCpuPriority(int pid, CpuPriorityLevel level, out string? error)
    {
        IntPtr h = OpenProcessHandle(pid, PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION);
        if (h == IntPtr.Zero) { error = $"OpenProcess failed ({Marshal.GetLastWin32Error()})"; return false; }
        try
        {
            if (!SetPriorityClass(h, ToPriorityClass(level)))
            {
                error = $"SetPriorityClass failed ({Marshal.GetLastWin32Error()})";
                return false;
            }
            error = null;
            return true;
        }
        finally { CloseHandle(h); }
    }

    public static bool TrySetIoPriority(int pid, IoPriorityLevel level, out string? error)
    {
        IntPtr h = OpenProcessHandle(pid, PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION);
        if (h == IntPtr.Zero) { error = $"OpenProcess failed ({Marshal.GetLastWin32Error()})"; return false; }
        try
        {
            // I/O priority is only reachable through ntdll (class 0x21).
            // VeryLow/Low also require the calling process to hold it, but High
            // and Normal always succeed from an elevated caller.
            uint value = (uint)level;
            int status = NtSetInformationProcess(h, NtProcessIoPriority, ref value, sizeof(uint));
            if (status != 0)
            {
                error = status == unchecked((int)0xC0000061) && level == IoPriorityLevel.High
                    ? "High I/O priority is reserved by Windows and cannot be set on any process"
                    : $"NtSetInformationProcess(IoPriority) failed (NTSTATUS 0x{status:X8})";
                return false;
            }
            error = null;
            return true;
        }
        finally { CloseHandle(h); }
    }

    public static bool TrySetMemoryPriority(int pid, MemoryPriorityLevel level, out string? error)
    {
        IntPtr h = OpenProcessHandle(pid, PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION);
        if (h == IntPtr.Zero) { error = $"OpenProcess failed ({Marshal.GetLastWin32Error()})"; return false; }
        try
        {
            // kernel32 class 0 = MemoryPriority; the value is the 1–5 LASSO level.
            uint value = (uint)level;
            if (!SetProcessInformation(h, ProcessMemoryPriority, ref value, sizeof(uint)))
            {
                error = $"SetProcessInformation(MemoryPriority) failed ({Marshal.GetLastWin32Error()})";
                return false;
            }
            error = null;
            return true;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Pins a process to CPU sets. Empty list clears the restriction.</summary>
    public static bool TrySetCpuSets(int pid, IReadOnlyList<uint> cpuSetIds, out string? error)
    {
        IntPtr h = OpenProcessHandle(pid, PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION);
        if (h == IntPtr.Zero) { error = $"OpenProcess failed ({Marshal.GetLastWin32Error()})"; return false; }
        try
        {
            uint[] ids = cpuSetIds.ToArray();
            if (!SetProcessDefaultCpuSets(h, ids.Length == 0 ? null : ids, (uint)ids.Length))
            {
                error = $"SetProcessDefaultCpuSets failed ({Marshal.GetLastWin32Error()})";
                return false;
            }
            error = null;
            return true;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Pins a process to a legacy group-0 affinity mask (0 restores the full system mask).</summary>
    public static bool TrySetAffinityMask(int pid, ulong mask, out string? error)
    {
        IntPtr h = OpenProcessHandle(pid, PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION);
        if (h == IntPtr.Zero) { error = $"OpenProcess failed ({Marshal.GetLastWin32Error()})"; return false; }
        try
        {
            if (mask == 0)
            {
                // An empty mask is rejected by Win32; "clear" means restore the full system mask.
                if (!GetProcessAffinityMask(GetCurrentProcess(), out _, out IntPtr system))
                {
                    error = "GetProcessAffinityMask failed";
                    return false;
                }
                mask = (ulong)system.ToInt64();
            }
            if (!SetProcessAffinityMask(h, new IntPtr((long)mask)))
            {
                error = $"SetProcessAffinityMask failed ({Marshal.GetLastWin32Error()})";
                return false;
            }
            error = null;
            return true;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>The system affinity mask (all logical processors in group 0).</summary>
    public static ulong GetSystemAffinityMask()
    {
        try
        {
            if (GetProcessAffinityMask(GetCurrentProcess(), out _, out IntPtr system))
                return (ulong)system.ToInt64();
        }
        catch { }
        return 0;
    }

    /// <summary>Current priority-class display text for a process.</summary>
    public static string GetPriorityText(int pid)
    {
        IntPtr h = OpenProcessHandle(pid, PROCESS_QUERY_LIMITED_INFORMATION);
        if (h == IntPtr.Zero) return "—";
        try { return PriorityClassName(GetPriorityClass(h)); }
        finally { CloseHandle(h); }
    }

    /// <summary>Total CPU seconds a process has consumed since launch (0 when unreadable).
    /// Used by the hard throttle to measure the real burn rate of its active window.</summary>
    public static double GetProcessCpuSeconds(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Refresh();
            return proc.TotalProcessorTime.TotalSeconds;
        }
        catch { return 0; }
    }

    /// <summary>Reads a process's current priority class back as a rule level (null when unreadable).</summary>
    public static CpuPriorityLevel? GetCpuPriorityLevel(int pid)
    {
        IntPtr h = OpenProcessHandle(pid, PROCESS_QUERY_LIMITED_INFORMATION);
        if (h == IntPtr.Zero) return null;
        try
        {
            return GetPriorityClass(h) switch
            {
                IDLE_PRIORITY_CLASS => CpuPriorityLevel.Idle,
                BELOW_NORMAL_PRIORITY_CLASS => CpuPriorityLevel.BelowNormal,
                NORMAL_PRIORITY_CLASS => CpuPriorityLevel.Normal,
                ABOVE_NORMAL_PRIORITY_CLASS => CpuPriorityLevel.AboveNormal,
                HIGH_PRIORITY_CLASS => CpuPriorityLevel.High,
                REALTIME_PRIORITY_CLASS => CpuPriorityLevel.Realtime,
                _ => null,
            };
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Current affinity display text. CPU-Set pins don't change the
    /// legacy mask, so this reads the process's default CPU sets first and only
    /// falls back to the mask when none are set.</summary>
    public static string GetAffinityText(int pid)
    {
        IntPtr h = OpenProcessHandle(pid, PROCESS_QUERY_LIMITED_INFORMATION);
        if (h == IntPtr.Zero) return "—";
        try
        {
            // CPU Sets first (what SetProcessDefaultCpuSets actually changed).
            uint len = 0;
            GetProcessDefaultCpuSets(h, null, 0, ref len);
            if (len > 0)
            {
                var ids = new uint[len];
                uint got = len;
                if (GetProcessDefaultCpuSets(h, ids, got, ref got) && got > 0)
                {
                    uint systemCount = (uint)GetCpuSets().Count;
                    if (got < systemCount) return $"{got}/{systemCount} set(s)";
                    return "All";
                }
            }

            // Legacy mask fallback.
            if (!GetProcessAffinityMask(h, out IntPtr mask, out IntPtr system)) return "—";
            ulong m = (ulong)mask.ToInt64();
            if (m == 0) return "—";
            if (m == (ulong)system.ToInt64()) return "All";
            var bits = new List<int>();
            for (int i = 0; i < 64; i++) if ((m & (1UL << i)) != 0) bits.Add(i);
            if (bits.Count == 0) return "—";
            return bits.Count <= 8 ? string.Join(",", bits) : $"{bits.Count} core(s)";
        }
        finally { CloseHandle(h); }
    }

    // ── CPU Sets enumeration (topology presets) ─────────────────────────

    /// <summary>Enumerates the system's CPU sets (works across Processor Groups, >64 logical CPUs).</summary>
    public static List<CpuSetInfo> GetCpuSets()
    {
        var result = new List<CpuSetInfo>();
        try
        {
            uint length = 0;
            GetSystemCpuSetInformation(IntPtr.Zero, 0, ref length, IntPtr.Zero, 0);
            if (length == 0) return result;
            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, length, ref length, IntPtr.Zero, 0)) return result;
                IntPtr ptr = buffer;
                IntPtr end = new IntPtr(buffer.ToInt64() + length);
                while (ptr.ToInt64() < end.ToInt64())
                {
                    uint size = (uint)Marshal.ReadInt32(ptr);
                    uint type = (uint)Marshal.ReadInt32(ptr, 4);
                    if (size == 0) break;
                    if (type == 0) // CPU_SET_INFORMATION_TYPE.CpuSet
                    {
                        result.Add(new CpuSetInfo
                        {
                            Id = (uint)Marshal.ReadInt32(ptr, 8),
                            Group = (ushort)Marshal.ReadInt16(ptr, 12),
                            LogicalProcessorIndex = Marshal.ReadByte(ptr, 14),
                            CoreIndex = Marshal.ReadByte(ptr, 15),
                            LastLevelCacheIndex = Marshal.ReadByte(ptr, 16),
                            EfficiencyClass = Marshal.ReadByte(ptr, 17),
                        });
                    }
                    ptr = new IntPtr(ptr.ToInt64() + size);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { }
        return result;
    }

    // ── Suspend / resume / terminate ────────────────────────────────────

    public static bool SuspendProcess(int pid)
    {
        // Preferred: one ntdll syscall for the whole process.
        IntPtr proc = OpenProcessHandle(pid, PROCESS_SUSPEND_RESUME);
        if (proc != IntPtr.Zero)
        {
            try
            {
                if (NtSuspendProcess(proc) == 0) return true;
            }
            finally { CloseHandle(proc); }
        }

        // Fallback: per-thread walk (blocked on some hardened Windows builds).
        int suspended = 0;
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == new IntPtr(INVALID_HANDLE_VALUE)) return false;
        try
        {
            var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            if (Thread32First(snapshot, ref entry))
            {
                do
                {
                    if (entry.th32OwnerProcessID != (uint)pid) continue;
                    IntPtr thread = OpenThread(THREAD_SUSPEND_RESUME, false, entry.th32ThreadID);
                    if (thread != IntPtr.Zero)
                    {
                        if (SuspendThread(thread) != uint.MaxValue) suspended++;
                        CloseHandle(thread);
                    }
                }
                while (Thread32Next(snapshot, ref entry));
            }
        }
        finally { CloseHandle(snapshot); }
        return suspended > 0;
    }

    public static int ResumeProcess(int pid)
    {
        // Preferred: one ntdll syscall (idempotent — resumes are reference-counted
        // per thread, and NtResumeProcess resumes exactly what NtSuspendProcess froze).
        IntPtr proc = OpenProcessHandle(pid, PROCESS_SUSPEND_RESUME);
        if (proc != IntPtr.Zero)
        {
            try
            {
                if (NtResumeProcess(proc) == 0) return 1;
            }
            finally { CloseHandle(proc); }
        }

        // Fallback: per-thread walk.
        int resumed = 0;
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == new IntPtr(INVALID_HANDLE_VALUE)) return 0;
        try
        {
            var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            if (Thread32First(snapshot, ref entry))
            {
                do
                {
                    if (entry.th32OwnerProcessID != (uint)pid) continue;
                    IntPtr thread = OpenThread(THREAD_SUSPEND_RESUME, false, entry.th32ThreadID);
                    if (thread != IntPtr.Zero)
                    {
                        ResumeThread(thread);
                        resumed++;
                        CloseHandle(thread);
                    }
                }
                while (Thread32Next(snapshot, ref entry));
            }
        }
        finally { CloseHandle(snapshot); }
        return resumed;
    }

    public static bool Terminate(int pid)
    {
        IntPtr h = OpenProcessHandle(pid, PROCESS_TERMINATE);
        if (h == IntPtr.Zero) return false;
        try { return TerminateProcess(h, 1); }
        finally { CloseHandle(h); }
    }

    // ── Sleep state ─────────────────────────────────────────────────────

    private static uint _sleepFlags;

    /// <summary>Called from the engine's long-lived heartbeat thread — the
    /// execution-state assertion is bound to the calling thread, so calling it
    /// from short-lived pool threads silently does nothing.</summary>
    public static void SetSleepRequired(bool required)
    {
        _sleepFlags = required ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS;
        SetThreadExecutionState(_sleepFlags);
    }

    /// <summary>Test hook — reports what the last SetSleepRequired call configured.</summary>
    internal static uint SleepFlagsForTests => _sleepFlags;

    // ── System sampling ─────────────────────────────────────────────────

    private static long _lastIdle, _lastKernel, _lastUser;
    private static bool _timesInitialized;

    /// <summary>Total CPU usage percent since the previous call.</summary>
    public static double GetSystemCpuPercent()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
        if (_timesInitialized)
        {
            long idleDelta = idle - _lastIdle;
            long totalDelta = (kernel + user) - (_lastKernel + _lastUser);
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
            if (totalDelta <= 0) return 0;
            return Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
        }
        _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
        _timesInitialized = true;
        return 0;
    }

    /// <summary>Returns (total MB, available MB).</summary>
    public static (ulong TotalMb, ulong AvailMb) GetMemoryStatus()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        return (status.ullTotalPhys / (1024 * 1024), status.ullAvailPhys / (1024 * 1024));
    }

    /// <summary>True when the foreground window covers the primary monitor (fullscreen detection).</summary>
    public static bool IsForegroundFullscreen(out int pid)
    {
        pid = 0;
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out pid);
            if (!GetWindowRect(hwnd, out RECT win)) return false;
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            int w = win.Right - win.Left, h = win.Bottom - win.Top;
            int mw = info.rcMonitor.Right - info.rcMonitor.Left, mh = info.rcMonitor.Bottom - info.rcMonitor.Top;
            return w >= mw && h >= mh;
        }
        catch { return false; }
    }

    // ── Power management (powercfg) ──────────────────────────────────────

    /// <summary>Enumerates power plans via powercfg /list.</summary>
    public static List<PowerPlan> GetPowerPlans()
    {
        var plans = new List<PowerPlan>();
        try
        {
            string output = RunPowerCfg("/list") ?? string.Empty;
            var regex = new Regex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)\s*(\*)?",
                RegexOptions.Compiled);
            foreach (Match m in regex.Matches(output))
            {
                plans.Add(new PowerPlan(m.Groups[1].Value, m.Groups[2].Value.Trim(), m.Groups[3].Success));
            }
        }
        catch { }
        return plans;
    }

    public static string? GetActivePowerPlanGuid()
    {
        try
        {
            string output = RunPowerCfg("/getactivescheme") ?? string.Empty;
            var m = Regex.Match(output, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }
        catch { return null; }
    }

    public static bool SetActivePowerPlan(string guid)
    {
        string? output = RunPowerCfg($"/setactive {guid}");
        return string.IsNullOrEmpty(output) || !output.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Disables core parking + frequency scaling (Boost Mode). Saves the prior
    /// AC/DC values to <paramref name="saved"/> so <see cref="RestoreBoostValues"/> can undo it.
    /// </summary>
    /// <summary>
    /// Disables core parking + frequency scaling (Boost Mode). Saves the prior
    /// AC/DC values ("scope:setting" → value) so <see cref="RestoreBoostValues"/> can undo it.
    /// </summary>
    public static bool ApplyBoostMode(out Dictionary<string, string> saved)
    {
        saved = new Dictionary<string, string>();
        try
        {
            string[] settings = { "CPMINCORES", "PROCTHROTTLEMAX", "PROCTHROTTLEMIN", "PERFBOOSTMODE" };
            string[] scopes = { "setacvalueindex", "setdcvalueindex" };
            foreach (string scope in scopes)
            {
                foreach (string setting in settings)
                {
                    saved[$"{scope}:{setting}"] = ReadPowerSetting(scope, setting) ?? string.Empty;
                }
            }
            foreach (string scope in scopes)
            {
                foreach (string setting in settings)
                {
                    string target = setting is "CPMINCORES" or "PROCTHROTTLEMIN" or "PROCTHROTTLEMAX" ? "100" : "2";
                    RunPowerCfg($"/{scope} SCHEME_CURRENT SUB_PROCESSOR {setting} {target}");
                }
            }
            RunPowerCfg("/setactive SCHEME_CURRENT");
            return true;
        }
        catch { return false; }
    }

    /// <summary>Reads one power setting ("setacvalueindex"/"setdcvalueindex" scope) as a raw value.</summary>
    private static string? ReadPowerSetting(string scope, string setting)
    {
        string getScope = "get" + scope.Substring(3);
        string? output = RunPowerCfg($"/{getScope} SCHEME_CURRENT SUB_PROCESSOR {setting}");
        if (string.IsNullOrEmpty(output)) return null;
        var hex = Regex.Match(output, @"0x[0-9a-fA-F]+");
        if (hex.Success) return hex.Value;
        var dec = Regex.Match(output, @"\(\s*(\d+)\s*\)");
        return dec.Success ? dec.Groups[1].Value : null;
    }

    public static bool RestoreBoostValues(Dictionary<string, string> saved)
    {
        try
        {
            foreach (var pair in saved)
            {
                int sep = pair.Key.IndexOf(':');
                if (sep <= 0 || string.IsNullOrWhiteSpace(pair.Value)) continue;
                string scope = pair.Key[..sep];
                string setting = pair.Key[(sep + 1)..];
                RunPowerCfg($"/{scope} SCHEME_CURRENT SUB_PROCESSOR {setting} {pair.Value}");
            }
            RunPowerCfg("/setactive SCHEME_CURRENT");
            return true;
        }
        catch { return false; }
    }

    private static string? RunPowerCfg(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd();
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            return (output + err).Trim();
        }
        catch { return null; }
    }

    // ── PDH sampling (per-core CPU + disk I/O for the monitor) ───────────

    /// <summary>
    /// Lightweight PDH sampler. Per-core counters use the "Processor Information(g,i)"
    /// instances, which are stable across Processor Groups. Disk uses PhysicalDisk(_Total).
    /// </summary>
    public sealed class PdhSampler : IDisposable
    {
        [DllImport("pdh.dll", SetLastError = true)]
        private static extern int PdhOpenQuery(IntPtr dataSource, UIntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int PdhAddEnglishCounter(IntPtr query, string path, UIntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern int PdhCollectQueryData(IntPtr query);

        [StructLayout(LayoutKind.Explicit)]
        private struct PDH_FMT_COUNTERVALUE
        {
            [FieldOffset(0)] public int CStatus;
            [FieldOffset(8)] public double DoubleValue;
        }

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format,
            out uint type, out PDH_FMT_COUNTERVALUE value);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern int PdhRemoveCounter(IntPtr counter);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern int PdhCloseQuery(IntPtr query);

        private IntPtr _query;
        private readonly List<IntPtr> _coreCounters = new();
        private IntPtr _diskCounter;
        private bool _primed;
        public int CoreCount { get; }

        public PdhSampler(int coreCount)
        {
            CoreCount = coreCount;
            try
            {
                if (PdhOpenQuery(IntPtr.Zero, UIntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
                // Build per-core counters from CPU-set enumeration so multi-group
                // systems get the right "g,i" instance names.
                var sets = GetCpuSets()
                    .OrderBy(s => s.Group).ThenBy(s => s.LogicalProcessorIndex)
                    .Take(coreCount).ToList();
                if (sets.Count == 0)
                {
                    for (int i = 0; i < coreCount && i < 64; i++)
                        sets.Add(new CpuSetInfo { Id = (uint)i, Group = 0, LogicalProcessorIndex = (byte)i });
                }
                foreach (var set in sets)
                {
                    string path = $"\\Processor Information({set.Group},{set.LogicalProcessorIndex})\\% Processor Time";
                    if (PdhAddEnglishCounter(_query, path, UIntPtr.Zero, out IntPtr counter) == 0)
                        _coreCounters.Add(counter);
                }
                if (PdhAddEnglishCounter(_query, "\\PhysicalDisk(_Total)\\Disk Bytes/sec",
                        UIntPtr.Zero, out _diskCounter) != 0)
                {
                    _diskCounter = IntPtr.Zero;
                }
            }
            catch { _query = IntPtr.Zero; }
        }

        /// <summary>Collects a sample. Returns per-core CPU percent + disk bytes/sec (null when unavailable).</summary>
        public (double[]? Cores, double DiskBytesPerSec)? Sample()
        {
            if (_query == IntPtr.Zero || _coreCounters.Count == 0) return null;
            try
            {
                int result = PdhCollectQueryData(_query);
                if (result != 0 && result != unchecked((int)PDH_INVALID_DATA)) return null;
                if (!_primed)
                {
                    // First collect only primes the counters; a second pass yields real deltas.
                    _primed = true;
                    if (result == unchecked((int)PDH_INVALID_DATA)) return null;
                }
                var cores = new double[_coreCounters.Count];
                for (int i = 0; i < _coreCounters.Count; i++)
                {
                    cores[i] = ReadDouble(_coreCounters[i]);
                }
                double disk = _diskCounter != IntPtr.Zero ? ReadDouble(_diskCounter) : 0;
                return (cores, disk);
            }
            catch { return null; }
        }

        private static double ReadDouble(IntPtr counter)
        {
            try
            {
                if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100,
                        out _, out var value) != 0) return 0;
                return value.DoubleValue;
            }
            catch { return 0; }
        }

        public void Dispose()
        {
            try
            {
                foreach (var c in _coreCounters) PdhRemoveCounter(c);
                if (_diskCounter != IntPtr.Zero) PdhRemoveCounter(_diskCounter);
                if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            }
            catch { }
            _query = IntPtr.Zero;
            _coreCounters.Clear();
        }
    }

    /// <summary>Builds the CPU set id list for a Core Isolation preset. Returns null when the preset doesn't apply.</summary>
    public static List<uint>? BuildPresetCpuSetIds(IReadOnlyList<CpuSetInfo> sets, CoreIsolationPreset preset)
    {
        switch (preset)
        {
            case CoreIsolationPreset.ECoresOff:
                if (!sets.Any(s => s.IsEfficiency)) return null; // no E-cores — hide the preset
                return sets.Where(s => s.IsPerformance).Select(s => s.Id).ToList();

            case CoreIsolationPreset.PCoresOff:
                if (!sets.Any(s => s.IsPerformance)) return null;
                return sets.Where(s => s.IsEfficiency).Select(s => s.Id).ToList();

            case CoreIsolationPreset.Ccd0Off:
                if (sets.Select(s => s.LastLevelCacheIndex).Distinct().Count() < 2) return null;
                return sets.Where(s => s.LastLevelCacheIndex != 0).Select(s => s.Id).ToList();

            case CoreIsolationPreset.Ccd1Off:
                if (sets.Select(s => s.LastLevelCacheIndex).Distinct().Count() < 2) return null;
                return sets.Where(s => s.LastLevelCacheIndex != 1).Select(s => s.Id).ToList();

            case CoreIsolationPreset.SmtOff:
                // Keep only the first logical processor of each physical core.
                return sets.GroupBy(s => (s.Group, s.CoreIndex))
                    .Select(g => g.OrderBy(s => s.LogicalProcessorIndex).First())
                    .Select(s => s.Id)
                    .ToList();

            default:
                return null;
        }
    }
}
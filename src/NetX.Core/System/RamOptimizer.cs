using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

using NetX.Core.Helpers;

namespace NetX.Core.Optimization;

/// <summary>
/// RAM Optimizer that clears memory cache and optimizes RAM usage
/// </summary>
public class RamOptimizer : IDisposable
{
    // Lazy: one instance (one auto-optimize timer) even if first touched from two threads.
    private static readonly Lazy<RamOptimizer> _instance = new(() => new RamOptimizer());
    public static RamOptimizer Instance => _instance.Value;

    private const string SettingsFileName = "ram_optimizer.json";

    // CPU time a process may use during the sampling window and still count as
    // idle (same 10 % of one core the old per-process 100 ms probe used).
    private const int ActivitySampleMs = 200;
    private const double ActiveCpuMs = 20;

    private Timer? _autoOptimizeTimer;
    private bool _isAutoOptimizeEnabled = false;
    private int _optimizeIntervalMinutes = 30;
    private int _memoryThresholdPercent = 80;
    private readonly object _timerLock = new();
    private readonly object _optimizeLock = new();

    // Native methods for memory management
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    // --- Standby-list purge (the real "clear cached memory") ---
    // NtSetSystemInformation(SystemMemoryListInformation, &command, sizeof(int)).
    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    private const int SystemMemoryListInformation = 0x50;
    private const int MemoryPurgeStandbyList = 4;      // free the standby cache
    private const int MemoryEmptyWorkingSets = 2;      // trim all working sets

    // --- Privilege elevation for the purge (needs SeProfileSingleProcessPrivilege) ---
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? host, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;
    private const string SE_PROFILE_SINGLE_PROCESS_NAME = "SeProfileSingleProcessPrivilege";

    private RamOptimizer()
    {
        LoadSettings();

        // Resume the saved schedule as soon as the singleton exists (App startup
        // touches RamOptimizer.Instance) instead of waiting for the RAM page.
        if (_isAutoOptimizeEnabled)
            StartAutoOptimize();
    }

    #region Properties

    public bool IsAutoOptimizeEnabled
    {
        get => _isAutoOptimizeEnabled;
        set
        {
            if (value == _isAutoOptimizeEnabled) return;
            _isAutoOptimizeEnabled = value;
            if (value)
                StartAutoOptimize();
            else
                StopAutoOptimize();
            SaveSettings();
        }
    }

    public int OptimizeIntervalMinutes
    {
        get => _optimizeIntervalMinutes;
        set
        {
            var clamped = ClampInterval(value);
            // The RAM page re-applies the saved value when it opens; restarting
            // the timer for an unchanged value would keep postponing the next run.
            if (clamped == _optimizeIntervalMinutes) return;
            _optimizeIntervalMinutes = clamped;
            if (_isAutoOptimizeEnabled)
                StartAutoOptimize();
            SaveSettings();
        }
    }

    public int MemoryThresholdPercent
    {
        get => _memoryThresholdPercent;
        set
        {
            var clamped = ClampThreshold(value);
            if (clamped == _memoryThresholdPercent) return;
            _memoryThresholdPercent = clamped;
            SaveSettings();
        }
    }

    private static int ClampInterval(int minutes) => Math.Max(5, Math.Min(120, minutes));
    private static int ClampThreshold(int percent) => Math.Max(50, Math.Min(95, percent));

    #endregion

    #region Memory Info

    public MemoryInfo GetMemoryInfo()
    {
        var info = new MemoryInfo();

        // Authoritative physical-RAM totals come from the kernel, NOT from
        // GC.GetGCMemoryInfo() (which reports the managed heap limit, not RAM).
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            info.TotalMemoryMB = (long)(memStatus.ullTotalPhys / (1024 * 1024));
            info.AvailableMemoryMB = (long)(memStatus.ullAvailPhys / (1024 * 1024));
            info.UsedMemoryMB = info.TotalMemoryMB - info.AvailableMemoryMB;
            info.UsagePercent = (int)memStatus.dwMemoryLoad;
        }

        // Cached / standby detail is best-effort (performance counters may be
        // disabled on some machines) and only enriches the display.
        try
        {
            using var cacheCounter = new PerformanceCounter("Memory", "Cache Bytes");
            info.CachedMemoryMB = (long)(cacheCounter.NextValue() / (1024 * 1024));

            using var standbyCounter = new PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes");
            info.StandbyMemoryMB = (long)(standbyCounter.NextValue() / (1024 * 1024));
        }
        catch { /* counters unavailable — totals above are still valid */ }

        return info;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
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

    #endregion

    #region Optimization

    public OptimizeResult OptimizeNow()
    {
        // Manual button, Dashboard and the auto timer can overlap — run one at a time.
        OptimizeResult result;
        lock (_optimizeLock)
        {
            result = OptimizeNowCore();
        }

        // Raised outside the lock so a handler that waits on the UI thread can't
        // deadlock against a UI-thread caller queued on the lock.
        OnOptimizationComplete?.Invoke(result);
        return result;
    }

    private OptimizeResult OptimizeNowCore()
    {
        var result = new OptimizeResult
        {
            StartTime = DateTime.Now,
            MemoryBefore = GetMemoryInfo()
        };

        try
        {
            // 1. Force .NET garbage collection
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // 2. Trim working set of all idle processes
            result.ProcessesOptimized = TrimIdleWorkingSets();

            // 3. Purge the standby (cached) memory list — real free, needs admin
            try
            {
                result.StandbyCleared = ClearStandbyList();
            }
            catch { }

            // Wait a moment for memory to settle
            Thread.Sleep(500);

            result.MemoryAfter = GetMemoryInfo();
            result.MemoryFreedMB = result.MemoryAfter.AvailableMemoryMB - result.MemoryBefore.AvailableMemoryMB;
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        result.EndTime = DateTime.Now;
        return result;
    }

    /// <summary>
    /// Trims the working set of every process that is idle right now and
    /// returns how many were actually trimmed. CPU use is sampled for all
    /// processes in ONE short window — the old code slept 100 ms per process,
    /// which took 30 s+ on a normal PC (and froze the Dashboard, which calls
    /// this on the UI thread).
    /// </summary>
    private static int TrimIdleWorkingSets()
    {
        var protectedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "smss", "csrss", "wininit", "services", "lsass",
            "svchost", "dwm", "explorer", "winlogon", "audiodg"
        };

        var processes = Process.GetProcesses();
        try
        {
            var cpuBefore = new Dictionary<int, TimeSpan>();
            foreach (var process in processes)
            {
                try
                {
                    if (!protectedProcesses.Contains(process.ProcessName))
                        cpuBefore[process.Id] = process.TotalProcessorTime;
                }
                catch { /* no access — skip */ }
            }

            Thread.Sleep(ActivitySampleMs);

            var trimmed = 0;
            foreach (var process in processes)
            {
                try
                {
                    if (!cpuBefore.TryGetValue(process.Id, out var before))
                        continue;

                    // Skip processes that are busy (they're actively working)
                    if ((process.TotalProcessorTime - before).TotalMilliseconds > ActiveCpuMs)
                        continue;

                    if (EmptyWorkingSet(process.Handle))
                        trimmed++;
                }
                catch { }
            }

            return trimmed;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    /// Actually purges the Windows standby (cached) memory list via
    /// NtSetSystemInformation. This is the real "free cached RAM" operation —
    /// the previous version only echoed text and did nothing. Requires elevation
    /// plus SeProfileSingleProcessPrivilege; returns false (no-op) when not admin.
    /// </summary>
    public bool ClearStandbyList()
    {
        try
        {
            if (!EnablePrivilege(SE_PROFILE_SINGLE_PROCESS_NAME))
                return false; // not elevated / privilege unavailable

            // Trim all working sets first, then purge the standby list.
            int emptyCmd = MemoryEmptyWorkingSets;
            NtSetSystemInformation(SystemMemoryListInformation, ref emptyCmd, sizeof(int));

            int purgeCmd = MemoryPurgeStandbyList;
            int status = NtSetSystemInformation(SystemMemoryListInformation, ref purgeCmd, sizeof(int));

            return status == 0; // STATUS_SUCCESS
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClearStandbyList failed: {ex.Message}");
            return false;
        }
    }

    private static bool EnablePrivilege(string privilegeName)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            var process = Process.GetCurrentProcess().Handle;
            if (!OpenProcessToken(process, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                return false;

            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
                return false;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };

            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            // AdjustTokenPrivileges can succeed but not assign the privilege
            // (ERROR_NOT_ALL_ASSIGNED = 1300) when the process isn't elevated.
            return Marshal.GetLastWin32Error() == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    public void OptimizeProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            EmptyWorkingSet(process.Handle);
        }
        catch { }
    }

    #endregion

    #region Auto Optimize

    private void StartAutoOptimize()
    {
        lock (_timerLock)
        {
            _autoOptimizeTimer?.Dispose();
            _autoOptimizeTimer = new Timer(AutoOptimizeCallback, null,
                TimeSpan.FromMinutes(_optimizeIntervalMinutes),
                TimeSpan.FromMinutes(_optimizeIntervalMinutes));
        }
    }

    private void StopAutoOptimize()
    {
        lock (_timerLock)
        {
            _autoOptimizeTimer?.Dispose();
            _autoOptimizeTimer = null;
        }
    }

    private void AutoOptimizeCallback(object? state)
    {
        try
        {
            var memInfo = GetMemoryInfo();

            // Only optimize if memory usage exceeds threshold
            if (memInfo.UsagePercent < _memoryThresholdPercent)
                return;

            // A manual run is in progress — this tick has nothing left to do.
            if (!Monitor.TryEnter(_optimizeLock))
                return;

            OptimizeResult result;
            try
            {
                result = OptimizeNowCore();
            }
            finally
            {
                Monitor.Exit(_optimizeLock);
            }

            OnOptimizationComplete?.Invoke(result);
        }
        catch { }
    }

    #endregion

    #region Top Memory Consumers

    public List<ProcessMemoryInfo> GetTopMemoryConsumers(int count = 10)
    {
        var list = new List<ProcessMemoryInfo>();

        try
        {
            var processes = Process.GetProcesses()
                .Select(p =>
                {
                    try
                    {
                        return new ProcessMemoryInfo
                        {
                            ProcessId = p.Id,
                            ProcessName = p.ProcessName,
                            MemoryMB = p.WorkingSet64 / (1024 * 1024),
                            PrivateMemoryMB = p.PrivateMemorySize64 / (1024 * 1024)
                        };
                    }
                    catch { return null; }
                })
                .Where(p => p != null)
                .OrderByDescending(p => p!.MemoryMB)
                .Take(count);

            list.AddRange(processes!);
        }
        catch { }

        return list;
    }

    #endregion

    #region Settings

    // Settings live in the admin-only store: the elevated app acts on them on its
    // own (timer), so a file in the user-writable %LocalAppData% can't be trusted.
    private void LoadSettings()
    {
        var settings = AdminOnlyStore.Load<RamOptimizerSettings>(SettingsFileName);
        if (settings != null)
        {
            _isAutoOptimizeEnabled = settings.AutoOptimizeEnabled;
            _optimizeIntervalMinutes = ClampInterval(settings.IntervalMinutes);
            _memoryThresholdPercent = ClampThreshold(settings.ThresholdPercent);
            return;
        }

        // Present but untrusted/corrupt: keep the defaults.
        if (AdminOnlyStore.Exists(SettingsFileName))
            return;

        // One-time import of the old per-user file (user-writable). Only bounded
        // values are taken: the switch plus a clamped 5–120 min interval and
        // 50–95 % threshold — the worst a planted file can do is a working-set
        // trim every few minutes. From now on only the admin-only copy is used.
        try
        {
            var legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetX", "ram_optimizer.json");
            if (File.Exists(legacyPath) && new FileInfo(legacyPath).Length <= 64 * 1024)
            {
                var legacy = JsonSerializer.Deserialize<RamOptimizerSettings>(File.ReadAllText(legacyPath));
                if (legacy != null)
                {
                    _isAutoOptimizeEnabled = legacy.AutoOptimizeEnabled;
                    _optimizeIntervalMinutes = ClampInterval(legacy.IntervalMinutes);
                    _memoryThresholdPercent = ClampThreshold(legacy.ThresholdPercent);
                    SaveSettings();
                }
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        var settings = new RamOptimizerSettings
        {
            AutoOptimizeEnabled = _isAutoOptimizeEnabled,
            IntervalMinutes = _optimizeIntervalMinutes,
            ThresholdPercent = _memoryThresholdPercent
        };

        if (!AdminOnlyStore.Save(SettingsFileName, settings))
            Debug.WriteLine("[RamOptimizer] Settings not persisted (admin-only store unavailable).");
    }

    #endregion

    public event Action<OptimizeResult>? OnOptimizationComplete;

    public void Dispose()
    {
        StopAutoOptimize();
    }
}

public class MemoryInfo
{
    public long TotalMemoryMB { get; set; }
    public long AvailableMemoryMB { get; set; }
    public long UsedMemoryMB { get; set; }
    public int UsagePercent { get; set; }
    public long CachedMemoryMB { get; set; }
    public long StandbyMemoryMB { get; set; }
}

public class OptimizeResult
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public MemoryInfo MemoryBefore { get; set; } = new();
    public MemoryInfo MemoryAfter { get; set; } = new();
    public long MemoryFreedMB { get; set; }
    public int ProcessesOptimized { get; set; }
    public bool StandbyCleared { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ProcessMemoryInfo
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public long MemoryMB { get; set; }
    public long PrivateMemoryMB { get; set; }
}

public class RamOptimizerSettings
{
    public bool AutoOptimizeEnabled { get; set; }
    public int IntervalMinutes { get; set; } = 30;
    public int ThresholdPercent { get; set; } = 80;
}

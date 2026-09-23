using System.Diagnostics;
using System.Security.Principal;

namespace NetX.Core.Optimization;

/// <summary>What a cleanup does. Each item is a separate, individually reported operation.</summary>
[Flags]
public enum MemoryCleanItems
{
    None = 0,
    /// <summary>Write the modified list to disk so it becomes available (standby). Safe.</summary>
    FlushModifiedList = 1 << 0,
    /// <summary>Drop the priority-0 standby list — the cache Windows itself values least. Safe.</summary>
    PurgeLowPriorityStandby = 1 << 1,
    /// <summary>Drop the whole standby list (what ISLC does). Files are re-read from disk afterwards.</summary>
    PurgeStandbyList = 1 << 2,
    /// <summary>Combine identical pages (Windows 10+). CPU-heavy for a few seconds.</summary>
    CombinePages = 1 << 3,
    /// <summary>Empty the working sets of idle background apps in the user's session. Can cause brief stutter.</summary>
    TrimWorkingSets = 1 << 4,
    /// <summary>Empty the system file cache working set.</summary>
    FlushSystemFileCache = 1 << 5,

    /// <summary>Default profile: touches no application.</summary>
    Safe = FlushModifiedList | PurgeLowPriorityStandby,
    All = FlushModifiedList | PurgeLowPriorityStandby | PurgeStandbyList | CombinePages | TrimWorkingSets | FlushSystemFileCache
}

/// <summary>Operations in the order a cleanup runs them.</summary>
public enum MemoryOperation
{
    CombinePages,
    TrimWorkingSets,
    FlushSystemFileCache,
    FlushModifiedList,
    PurgeStandbyList,
    PurgeLowPriorityStandby
}

public enum MemoryOperationStatus { Succeeded, Failed, Skipped }

public enum MemoryOperationError
{
    None,
    /// <summary>The token does not hold the privilege (not elevated, or the right was removed by policy).</summary>
    PrivilegeNotHeld,
    AccessDenied,
    NotSupported,
    Cancelled,
    Other
}

public sealed class MemoryOperationResult
{
    public MemoryOperation Operation { get; init; }
    public MemoryOperationStatus Status { get; init; }
    public MemoryOperationError Error { get; init; }

    /// <summary>NTSTATUS or Win32 error for diagnostics (0 on success). Never shown raw.</summary>
    public int Code { get; init; }

    /// <summary>
    /// Measured effect in MB, never an estimate: pages combined (CombinePages),
    /// rise in available memory (TrimWorkingSets, FlushSystemFileCache),
    /// shrink of the modified list (FlushModifiedList) or of the standby list
    /// being purged (PurgeStandbyList / PurgeLowPriorityStandby).
    /// </summary>
    public long AmountMB { get; init; }

    public int ProcessesTrimmed { get; init; }
    public int ProcessesSkipped { get; init; }

    public bool Succeeded => Status == MemoryOperationStatus.Succeeded;
}

public enum OptimizeTrigger
{
    Manual,
    /// <summary>Automatic: memory load reached the threshold.</summary>
    AutoMemoryLoad,
    /// <summary>Automatic (ISLC style): free memory low while the standby cache is large.</summary>
    AutoLowFreeMemory
}

public enum MemoryDataSource
{
    /// <summary>Only GlobalMemoryStatusEx totals.</summary>
    Basic,
    /// <summary>Memory-list sizes from performance counters (fallback).</summary>
    PerformanceCounters,
    /// <summary>Memory-list sizes straight from the kernel (NtQuerySystemInformation).</summary>
    KernelMemoryLists
}

public enum ProcessTrimOutcome { Trimmed, Protected, Self, AccessDenied, Gone, Failed }

public sealed class ProcessTrimResult
{
    public ProcessTrimOutcome Outcome { get; init; }
    public long BeforeMB { get; init; } = -1;
    public long AfterMB { get; init; } = -1;
}

/// <summary>
/// The RAM engine: truthful readings and the individual cleanup operations.
/// Stateless — <see cref="RamOptimizer"/> adds settings, the automatic mode,
/// history and "one cleanup at a time". Call <see cref="Run"/> off the UI thread.
/// </summary>
public static class MemoryCleaner
{
    private const long MB = 1024 * 1024;

    // Background-app trim: CPU is sampled for every process in one window; an app
    // that used more than 10 % of one core in it counts as busy and is left alone.
    private const int CpuSampleMilliseconds = 250;
    private const long BusyCpuTime100ns = 250_000; // 25 ms of CPU in the 250 ms window
    private const long MinTrimWorkingSetBytes = 8 * MB;

    // Counters are read again after this pause so the "after" numbers are settled.
    private const int SettleMilliseconds = 500;

    // Never trimmed (bulk or per row): kernel pseudo-processes, the compression
    // store, and the session/shell/audio/input processes whose page faults the
    // user would feel right away as desktop stutter.
    private static readonly HashSet<string> NeverTrim = new(StringComparer.OrdinalIgnoreCase)
    {
        "idle", "system", "registry", "memory compression", "secure system", "vmmem", "vmmemwsl",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm",
        "explorer", "sihost", "fontdrvhost", "ctfmon", "textinputhost", "shellexperiencehost",
        "startmenuexperiencehost", "searchhost", "searchapp", "lockapp", "logonui",
        "applicationframehost", "audiodg", "conhost", "taskmgr", "winxtools"
    };

    private static readonly Lazy<bool> _isElevated = new(() =>
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    });

    private static readonly Lazy<long> _installedMB = new(() => (long)(MemoryNative.GetInstalledMemoryBytes() / MB));
    private static readonly int _ownSessionId = GetOwnSessionId();

    /// <summary>True when WinXTools runs elevated (cleanups need it).</summary>
    public static bool IsElevated => _isElevated.Value;

    private static long PageBytes => Environment.SystemPageSize;

    private static int GetOwnSessionId()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.SessionId;
        }
        catch
        {
            return -1;
        }
    }

    #region Readings

    /// <summary>
    /// Current memory picture. Cheap (two system calls, plus one handle-free
    /// process snapshot when <paramref name="includeCompressedStore"/> is set),
    /// never throws, and never uses the slow first-sample-is-zero performance
    /// counters unless the kernel query is unavailable.
    /// </summary>
    public static MemoryInfo ReadMemoryInfo(bool includeCompressedStore = true)
    {
        var info = new MemoryInfo { Timestamp = DateTime.Now, InstalledMemoryMB = _installedMB.Value };

        if (MemoryNative.TryGetMemoryStatus(out var status))
        {
            info.TotalMemoryMB = (long)(status.ullTotalPhys / MB);
            info.AvailableMemoryMB = (long)(status.ullAvailPhys / MB);
            info.UsedMemoryMB = info.TotalMemoryMB - info.AvailableMemoryMB;
            info.UsagePercent = (int)status.dwMemoryLoad;
            info.CommitLimitMB = (long)(status.ullTotalPageFile / MB);
            info.CommitUsedMB = (long)((status.ullTotalPageFile - Math.Min(status.ullTotalPageFile, status.ullAvailPageFile)) / MB);
        }

        if (MemoryNative.QueryMemoryLists(out var lists) == MemoryNative.StatusSuccess)
        {
            info.Source = MemoryDataSource.KernelMemoryLists;
            info.FreeMemoryMB = PagesToMB(lists.ZeroPages + lists.FreePages);
            info.ModifiedMemoryMB = PagesToMB(lists.ModifiedPages + lists.ModifiedNoWritePages);
            info.StandbyMemoryMB = PagesToMB(lists.StandbyPages);
            info.StandbyLowPriorityMB = PagesToMB(lists.LowPriorityStandbyPages);
        }
        else if (CounterFallback.TryRead(out var standby, out var free, out var modified))
        {
            info.Source = MemoryDataSource.PerformanceCounters;
            info.FreeMemoryMB = free / MB;
            info.ModifiedMemoryMB = modified / MB;
            info.StandbyMemoryMB = standby / MB;
            info.StandbyLowPriorityMB = -1; // counters don't split out priority 0
        }
        else
        {
            info.Source = MemoryDataSource.Basic;
            info.FreeMemoryMB = info.ModifiedMemoryMB = info.StandbyMemoryMB = info.StandbyLowPriorityMB = -1;
        }

        info.CachedMemoryMB = info.HasListDetail ? info.StandbyMemoryMB + info.ModifiedMemoryMB : -1;
        info.CompressedMemoryMB = includeCompressedStore ? ReadCompressedStoreMB() : -1;
        return info;
    }

    private static long PagesToMB(ulong pages) => (long)(pages * (ulong)PageBytes / MB);

    private static readonly object CompressedStoreGate = new();
    private static long _compressedStoreMB = -1;
    private static long _compressedStoreReadTicks;

    /// <summary>
    /// RAM held by Windows' compression store ("Memory Compression" process), -1
    /// if unknown. Needs a full process snapshot, so it is re-read at most every
    /// few seconds — it changes slowly.
    /// </summary>
    private static long ReadCompressedStoreMB()
    {
        lock (CompressedStoreGate)
        {
            long now = Environment.TickCount64;
            if (_compressedStoreReadTicks != 0 && now - _compressedStoreReadTicks < 5000)
                return _compressedStoreMB;

            try
            {
                var snapshot = MemoryNative.SnapshotProcesses();
                var store = snapshot?.FirstOrDefault(p =>
                    p.Name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase));
                // No store process while the snapshot worked = compression is off (0), not unknown (-1).
                _compressedStoreMB = snapshot == null ? -1 : store == null ? 0 : store.WorkingSetBytes / MB;
            }
            catch
            {
                _compressedStoreMB = -1;
            }

            _compressedStoreReadTicks = now;
            return _compressedStoreMB;
        }
    }

    /// <summary>
    /// Processes using the most RAM, by private working set — the number Task
    /// Manager shows in its Memory column. Handle-free, so it works for every
    /// process; returns an empty list if the snapshot fails.
    /// </summary>
    public static List<ProcessMemoryInfo> GetTopProcesses(int count)
    {
        var snapshot = MemoryNative.SnapshotProcesses();
        if (snapshot == null)
            return new List<ProcessMemoryInfo>();

        int ownPid = Environment.ProcessId;
        return snapshot
            .Where(p => p.Pid > 0)
            .OrderByDescending(p => p.PrivateWorkingSetBytes)
            .Take(Math.Clamp(count, 1, 200))
            .Select(p => new ProcessMemoryInfo
            {
                ProcessId = p.Pid,
                ProcessName = string.IsNullOrEmpty(p.Name) ? $"PID {p.Pid}" : p.Name,
                MemoryMB = p.PrivateWorkingSetBytes / MB,
                PrivateMemoryMB = p.PrivateBytes / MB,
                WorkingSetMB = p.WorkingSetBytes / MB,
                CreateTime = p.CreateTime,
                SessionId = p.SessionId,
                IsSelf = p.Pid == ownPid,
                CanTrim = p.Pid != ownPid && p.Pid > 4 && !IsNeverTrim(p.Name),
                CanEnd = p.Pid != ownPid && p.Pid > 4 && p.Name.Length > 0 && !ProcessKiller.IsProtectedProcess(p.Name)
            })
            .ToList();
    }

    /// <summary>Processes WinXTools never trims (Windows core, shell, audio, input, itself).</summary>
    public static bool IsNeverTrim(string processName) =>
        string.IsNullOrEmpty(processName) || NeverTrim.Contains(processName) || ProcessKiller.IsProtectedProcess(processName);

    /// <summary>
    /// Lower-case bare program name for a user-entered name ("Discord.exe" → "discord"),
    /// or null when it isn't a plain program name.
    /// </summary>
    public static string? NormalizeProgramName(string? name)
    {
        var value = name?.Trim() ?? "";
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            value = value[..^4].TrimEnd();
        if (value.Length == 0 || value.Length > 100)
            return null;
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        return value.ToLowerInvariant();
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Runs the selected operations in a fixed, sensible order — combine pages,
    /// trim idle background apps, flush the system file cache, write the modified
    /// list, then purge the standby list (whole or priority 0) — and measures the
    /// result. Each operation succeeds or fails on its own; nothing throws.
    /// </summary>
    public static OptimizeResult Run(MemoryCleanItems items, OptimizeTrigger trigger,
        IReadOnlyCollection<string>? trimExclusions = null, CancellationToken cancellationToken = default)
    {
        items &= MemoryCleanItems.All;
        if (items.HasFlag(MemoryCleanItems.PurgeStandbyList))
            items &= ~MemoryCleanItems.PurgeLowPriorityStandby; // priority 0 is part of the whole list

        var result = new OptimizeResult
        {
            StartTime = DateTime.Now,
            Trigger = trigger,
            Items = items,
            MemoryBefore = ReadMemoryInfo(includeCompressedStore: false)
        };
        var operations = new List<MemoryOperationResult>();

        try
        {
            using var scope = NeedsPrivileges(items) ? MemoryNative.PrivilegedScope.Create() : null;

            foreach (var operation in Enum.GetValues<MemoryOperation>())
            {
                if (!items.HasFlag(ToItem(operation)))
                    continue;

                if (cancellationToken.IsCancellationRequested)
                {
                    operations.Add(Skipped(operation, MemoryOperationError.Cancelled));
                    continue;
                }

                try
                {
                    operations.Add(operation switch
                    {
                        MemoryOperation.CombinePages => CombinePages(scope),
                        MemoryOperation.TrimWorkingSets => TrimBackgroundApps(trimExclusions, cancellationToken),
                        MemoryOperation.FlushSystemFileCache => FlushFileCache(scope),
                        _ => MemoryListCommand(scope, operation)
                    });
                }
                catch (Exception ex)
                {
                    // One operation failing (e.g. impersonation refused) must not stop the others.
                    Debug.WriteLine($"[MemoryCleaner] {operation} failed: {ex}");
                    operations.Add(new MemoryOperationResult
                    {
                        Operation = operation,
                        Status = MemoryOperationStatus.Failed,
                        Error = MemoryOperationError.Other,
                        Code = ex.HResult
                    });
                }
            }

            if (operations.Count > 0)
                cancellationToken.WaitHandle.WaitOne(SettleMilliseconds);
        }
        catch (Exception ex)
        {
            // Diagnostics only — the UI words failures from Operations.
            result.ErrorMessage = ex.Message;
            Debug.WriteLine($"[MemoryCleaner] Cleanup failed: {ex}");
        }

        result.Operations = operations;
        result.MemoryAfter = ReadMemoryInfo(includeCompressedStore: false);
        result.EndTime = DateTime.Now;
        result.MemoryFreedMB = result.MemoryAfter.AvailableMemoryMB - result.MemoryBefore.AvailableMemoryMB;
        result.FreeGainedMB = result.MemoryBefore.HasListDetail && result.MemoryAfter.HasListDetail
            ? result.MemoryAfter.FreeMemoryMB - result.MemoryBefore.FreeMemoryMB
            : 0;
        result.ProcessesOptimized = operations.Sum(o => o.ProcessesTrimmed);
        result.StandbyCleared = operations.Any(o => o.Succeeded &&
            o.Operation is MemoryOperation.PurgeStandbyList or MemoryOperation.PurgeLowPriorityStandby);
        result.Success = result.ErrorMessage == null && operations.Count > 0 &&
                         operations.All(o => o.Status == MemoryOperationStatus.Succeeded);
        return result;
    }

    private static bool NeedsPrivileges(MemoryCleanItems items) =>
        (items & (MemoryCleanItems.CombinePages | MemoryCleanItems.FlushSystemFileCache |
                  MemoryCleanItems.FlushModifiedList | MemoryCleanItems.PurgeStandbyList |
                  MemoryCleanItems.PurgeLowPriorityStandby)) != 0;

    private static MemoryCleanItems ToItem(MemoryOperation operation) => operation switch
    {
        MemoryOperation.CombinePages => MemoryCleanItems.CombinePages,
        MemoryOperation.TrimWorkingSets => MemoryCleanItems.TrimWorkingSets,
        MemoryOperation.FlushSystemFileCache => MemoryCleanItems.FlushSystemFileCache,
        MemoryOperation.FlushModifiedList => MemoryCleanItems.FlushModifiedList,
        MemoryOperation.PurgeStandbyList => MemoryCleanItems.PurgeStandbyList,
        MemoryOperation.PurgeLowPriorityStandby => MemoryCleanItems.PurgeLowPriorityStandby,
        _ => MemoryCleanItems.None
    };

    private static MemoryOperationResult MemoryListCommand(MemoryNative.PrivilegedScope? scope, MemoryOperation operation)
    {
        int command = operation switch
        {
            MemoryOperation.FlushModifiedList => MemoryNative.MemoryFlushModifiedList,
            MemoryOperation.PurgeStandbyList => MemoryNative.MemoryPurgeStandbyList,
            _ => MemoryNative.MemoryPurgeLowPriorityStandbyList
        };

        if (scope == null || !scope.HasProfileSingleProcess)
            return Failed(operation, MemoryNative.StatusPrivilegeNotHeld);

        bool haveBefore = MemoryNative.QueryMemoryLists(out var before) == MemoryNative.StatusSuccess;
        int status = scope.Run(() => MemoryNative.SetMemoryListCommand(command));
        if (status != MemoryNative.StatusSuccess)
            return Failed(operation, status);

        long amount = 0;
        if (haveBefore && MemoryNative.QueryMemoryLists(out var after) == MemoryNative.StatusSuccess)
        {
            (ulong from, ulong to) = operation switch
            {
                MemoryOperation.FlushModifiedList => (before.ModifiedPages, after.ModifiedPages),
                MemoryOperation.PurgeStandbyList => (before.StandbyPages, after.StandbyPages),
                _ => (before.LowPriorityStandbyPages, after.LowPriorityStandbyPages)
            };
            amount = from > to ? PagesToMB(from - to) : 0;
        }

        return Succeeded(operation, amount);
    }

    private static MemoryOperationResult CombinePages(MemoryNative.PrivilegedScope? scope)
    {
        const MemoryOperation operation = MemoryOperation.CombinePages;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return Failed(operation, MemoryNative.StatusNotSupported);
        if (scope == null || !scope.HasProfileSingleProcess)
            return Failed(operation, MemoryNative.StatusPrivilegeNotHeld);

        long pages = 0;
        int status = scope.Run(() =>
        {
            int code = MemoryNative.CombineMemoryLists(out var combined);
            pages = combined;
            return code;
        });

        return status == MemoryNative.StatusSuccess
            ? Succeeded(operation, pages * PageBytes / MB)
            : Failed(operation, status);
    }

    private static MemoryOperationResult FlushFileCache(MemoryNative.PrivilegedScope? scope)
    {
        const MemoryOperation operation = MemoryOperation.FlushSystemFileCache;
        if (scope == null || !scope.HasIncreaseQuota)
            return Failed(operation, MemoryNative.StatusPrivilegeNotHeld);

        long before = ReadAvailableMB();
        int error = scope.Run(MemoryNative.FlushSystemFileCache);
        if (error != 0)
            return FailedWin32(operation, error);

        return Succeeded(operation, Math.Max(0, ReadAvailableMB() - before));
    }

    private static long ReadAvailableMB() =>
        MemoryNative.TryGetMemoryStatus(out var status) ? (long)(status.ullAvailPhys / MB) : 0;

    /// <summary>
    /// Trims idle background apps in the user's own Windows session. Left alone:
    /// services and other users' sessions, WinXTools, the app in the foreground
    /// (and every process of the same program, e.g. all of a browser's
    /// processes), Windows core/shell/audio/input processes, the user's exclusions,
    /// tiny processes and anything that used the CPU during the sampling window.
    /// </summary>
    private static MemoryOperationResult TrimBackgroundApps(IReadOnlyCollection<string>? exclusions, CancellationToken cancellationToken)
    {
        const MemoryOperation operation = MemoryOperation.TrimWorkingSets;

        var first = MemoryNative.SnapshotProcesses();
        if (first == null)
            return Failed(operation, MemoryNative.StatusNotSupported);
        if (cancellationToken.WaitHandle.WaitOne(CpuSampleMilliseconds))
            return Skipped(operation, MemoryOperationError.Cancelled);
        var second = MemoryNative.SnapshotProcesses();
        if (second == null)
            return Failed(operation, MemoryNative.StatusNotSupported);

        var cpuBefore = new Dictionary<int, (long CreateTime, long Cpu)>(first.Count);
        foreach (var p in first)
            cpuBefore[p.Pid] = (p.CreateTime, p.CpuTime);

        var excluded = new HashSet<string>(exclusions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        int ownPid = Environment.ProcessId;
        int foregroundPid = ProcessKiller.GetForegroundProcessId();
        string? foregroundName = second.FirstOrDefault(p => p.Pid == foregroundPid)?.Name;

        long availableBefore = ReadAvailableMB();
        int trimmed = 0, skipped = 0;

        foreach (var p in second)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Services (session 0) and other users' sessions are not ours to touch.
            if (p.Pid <= 4 || p.SessionId != _ownSessionId || _ownSessionId < 0)
                continue;
            if (p.WorkingSetBytes < MinTrimWorkingSetBytes)
                continue; // nothing worth moving

            bool isForeground = p.Pid == foregroundPid ||
                                (foregroundName != null && p.Name.Equals(foregroundName, StringComparison.OrdinalIgnoreCase));
            bool busy = !cpuBefore.TryGetValue(p.Pid, out var earlier) || earlier.CreateTime != p.CreateTime ||
                        p.CpuTime - earlier.Cpu > BusyCpuTime100ns;

            if (p.Pid == ownPid || isForeground || IsNeverTrim(p.Name) || excluded.Contains(p.Name) || busy)
            {
                skipped++;
                continue;
            }

            var outcome = MemoryNative.TrimWorkingSet(p.Pid, p.CreateTime, out _, out _);
            if (outcome == MemoryNative.NativeOutcome.Done)
                trimmed++;
            else if (outcome != MemoryNative.NativeOutcome.Gone)
                skipped++; // protected by Windows — skipped quietly
        }

        if (cancellationToken.IsCancellationRequested && trimmed == 0)
            return Skipped(operation, MemoryOperationError.Cancelled);

        return new MemoryOperationResult
        {
            Operation = operation,
            Status = MemoryOperationStatus.Succeeded,
            AmountMB = Math.Max(0, ReadAvailableMB() - availableBefore),
            ProcessesTrimmed = trimmed,
            ProcessesSkipped = skipped
        };
    }

    /// <summary>
    /// Trims one process the user picked in the list. Refuses WinXTools itself and
    /// Windows core processes, and never touches a process that merely reused the PID.
    /// </summary>
    public static ProcessTrimResult TrimProcess(int processId, string expectedName, long expectedCreateTime)
    {
        if (processId == Environment.ProcessId)
            return new ProcessTrimResult { Outcome = ProcessTrimOutcome.Self };
        if (processId <= 4 || IsNeverTrim(expectedName))
            return new ProcessTrimResult { Outcome = ProcessTrimOutcome.Protected };

        var outcome = MemoryNative.TrimWorkingSet(processId, expectedCreateTime, out var before, out var after);
        return new ProcessTrimResult
        {
            Outcome = outcome switch
            {
                MemoryNative.NativeOutcome.Done => ProcessTrimOutcome.Trimmed,
                MemoryNative.NativeOutcome.AccessDenied => ProcessTrimOutcome.AccessDenied,
                MemoryNative.NativeOutcome.Gone => ProcessTrimOutcome.Gone,
                _ => ProcessTrimOutcome.Failed
            },
            BeforeMB = before < 0 ? -1 : before / MB,
            AfterMB = after < 0 ? -1 : after / MB
        };
    }

    private static MemoryOperationResult Succeeded(MemoryOperation operation, long amountMB) => new()
    {
        Operation = operation,
        Status = MemoryOperationStatus.Succeeded,
        AmountMB = Math.Max(0, amountMB)
    };

    private static MemoryOperationResult Skipped(MemoryOperation operation, MemoryOperationError reason) => new()
    {
        Operation = operation,
        Status = MemoryOperationStatus.Skipped,
        Error = reason
    };

    private static MemoryOperationResult Failed(MemoryOperation operation, int ntStatus) => new()
    {
        Operation = operation,
        Status = MemoryOperationStatus.Failed,
        Code = ntStatus,
        Error = ntStatus switch
        {
            MemoryNative.StatusPrivilegeNotHeld => MemoryOperationError.PrivilegeNotHeld,
            MemoryNative.StatusAccessDenied => MemoryOperationError.AccessDenied,
            MemoryNative.StatusInvalidInfoClass or MemoryNative.StatusNotImplemented or
                MemoryNative.StatusNotSupported => MemoryOperationError.NotSupported,
            _ => MemoryOperationError.Other
        }
    };

    private static MemoryOperationResult FailedWin32(MemoryOperation operation, int win32Error) => new()
    {
        Operation = operation,
        Status = MemoryOperationStatus.Failed,
        Code = win32Error,
        Error = win32Error switch
        {
            MemoryNative.ErrorPrivilegeNotHeld => MemoryOperationError.PrivilegeNotHeld,
            MemoryNative.ErrorAccessDenied => MemoryOperationError.AccessDenied,
            MemoryNative.ErrorNotSupported or MemoryNative.ErrorCallNotImplemented => MemoryOperationError.NotSupported,
            _ => MemoryOperationError.Other
        }
    };

    #endregion

    /// <summary>
    /// Memory-list sizes from performance counters, only used if the kernel query
    /// ever fails. Counters are created once (creation is slow) and dropped for
    /// good if the category is unavailable, so a broken counter setup costs nothing.
    /// </summary>
    private static class CounterFallback
    {
        private static readonly object Gate = new();
        private static PerformanceCounter[]? _counters;
        private static bool _unavailable;

        public static bool TryRead(out long standbyBytes, out long freeBytes, out long modifiedBytes)
        {
            standbyBytes = freeBytes = modifiedBytes = 0;
            lock (Gate)
            {
                if (_unavailable)
                    return false;

                try
                {
                    _counters ??= new[]
                    {
                        new PerformanceCounter("Memory", "Standby Cache Core Bytes", readOnly: true),
                        new PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes", readOnly: true),
                        new PerformanceCounter("Memory", "Standby Cache Reserve Bytes", readOnly: true),
                        new PerformanceCounter("Memory", "Free & Zero Page List Bytes", readOnly: true),
                        new PerformanceCounter("Memory", "Modified Page List Bytes", readOnly: true)
                    };

                    // Raw values of these counters are byte counts — no two-sample dance.
                    standbyBytes = _counters[0].RawValue + _counters[1].RawValue + _counters[2].RawValue;
                    freeBytes = _counters[3].RawValue;
                    modifiedBytes = _counters[4].RawValue;
                    return true;
                }
                catch
                {
                    _unavailable = true;
                    if (_counters != null)
                        foreach (var counter in _counters)
                            counter.Dispose();
                    _counters = null;
                    return false;
                }
            }
        }
    }
}

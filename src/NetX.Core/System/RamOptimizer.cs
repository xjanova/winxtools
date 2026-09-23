using System.Diagnostics;
using System.Text.Json;

using NetX.Core.Helpers;

namespace NetX.Core.Optimization;

/// <summary>
/// Back end of the RAM page. <see cref="MemoryCleaner"/> does the actual work;
/// this class runs one cleanup at a time (double clicks and overlapping callers
/// share the run in progress), keeps recent results for the page, runs the
/// automatic mode and persists the settings in the admin-only store.
///
/// Automatic mode (like ISLC): every <see cref="AutoCheckInterval"/> it reads the
/// memory lists — a cheap call — and cleans only when a condition has held for
/// two samples in a row, and never more often than the cooldown allows.
/// </summary>
public class RamOptimizer : IDisposable
{
    // Lazy: one instance (one automatic timer) even if first touched from two threads.
    private static readonly Lazy<RamOptimizer> _instance = new(() => new RamOptimizer());
    public static RamOptimizer Instance => _instance.Value;

    private const string SettingsFileName = "ram_optimizer.json";
    private const int MaxRecentResults = 20;
    private const int SaveDelayMilliseconds = 600;
    private const int SamplesToTrigger = 2;

    /// <summary>How often the automatic mode looks at memory.</summary>
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromSeconds(5);

    public const int MinCooldownMinutes = 1;
    public const int MaxCooldownMinutes = 120;
    public const int MinThresholdPercent = 50;
    public const int MaxThresholdPercent = 95;
    public const int MinListThresholdMB = 256;
    public const int MaxListThresholdMB = 65536;
    public const int MaxTrimExclusions = 50;

    private readonly object _settingsLock = new();
    private readonly object _runLock = new();
    private readonly object _timerLock = new();
    private readonly object _historyLock = new();
    private readonly List<OptimizeResult> _recent = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer _saveTimer;
    private Timer? _autoTimer;
    private Task<OptimizeResult>? _running;
    private int _autoTickBusy;
    private int _loadStreak;
    private int _lowFreeStreak;
    private long _lastCleanupUtcTicks; // written by workers, read by the automatic timer
    private bool _savePending;
    private volatile bool _disposed;

    // Written by the UI thread, read by the automatic timer: plain int/bool reads
    // are atomic and the setters publish with Volatile.Write.
    private volatile bool _autoEnabled;
    private int _cooldownMinutes = 10;
    private int _thresholdPercent = 80;
    private bool _triggerOnLoad = true;
    private bool _triggerOnLowFree;
    private int _lowFreeMB = 1024;
    private int _minStandbyMB = 1024;
    private int _cleanItems = (int)MemoryCleanItems.Safe;
    private volatile string[] _trimExclusions = Array.Empty<string>();

    private RamOptimizer()
    {
        _saveTimer = new Timer(_ => FlushSettings(), null, Timeout.Infinite, Timeout.Infinite);
        LoadSettings();

        // Slider changes are saved a moment later; don't lose the last one on exit.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushSettings();

        // Resume the saved automatic mode as soon as the app starts (App startup
        // touches RamOptimizer.Instance), not only when the RAM page opens.
        if (_autoEnabled)
            StartAutoTimer();
    }

    #region Settings properties

    public bool IsAutoOptimizeEnabled
    {
        get => _autoEnabled;
        set
        {
            if (value == _autoEnabled) return;
            _autoEnabled = value;
            if (value)
                StartAutoTimer();
            else
                StopAutoTimer();
            ScheduleSave();
        }
    }

    /// <summary>Minimum minutes between two automatic cleanups (anti-thrash cooldown).</summary>
    public int AutoCooldownMinutes
    {
        get => _cooldownMinutes;
        set => SetInt(ref _cooldownMinutes, Math.Clamp(value, MinCooldownMinutes, MaxCooldownMinutes));
    }

    /// <summary>Automatic trigger 1: memory load (percent in use) at or above this.</summary>
    public int MemoryThresholdPercent
    {
        get => _thresholdPercent;
        set => SetInt(ref _thresholdPercent, Math.Clamp(value, MinThresholdPercent, MaxThresholdPercent));
    }

    public bool TriggerOnMemoryLoad
    {
        get => _triggerOnLoad;
        set => SetBool(ref _triggerOnLoad, value);
    }

    /// <summary>Automatic trigger 2 (ISLC style): free memory below X while the standby cache is at least Y.</summary>
    public bool TriggerOnLowFreeMemory
    {
        get => _triggerOnLowFree;
        set => SetBool(ref _triggerOnLowFree, value);
    }

    public int LowFreeThresholdMB
    {
        get => _lowFreeMB;
        set => SetInt(ref _lowFreeMB, Math.Clamp(value, MinListThresholdMB, MaxListThresholdMB));
    }

    public int MinStandbyMB
    {
        get => _minStandbyMB;
        set => SetInt(ref _minStandbyMB, Math.Clamp(value, MinListThresholdMB, MaxListThresholdMB));
    }

    /// <summary>What "Clean now" (and the memory-load trigger) does. Defaults to <see cref="MemoryCleanItems.Safe"/>.</summary>
    public MemoryCleanItems CleanItems
    {
        get => (MemoryCleanItems)_cleanItems;
        set => SetInt(ref _cleanItems, (int)(value & MemoryCleanItems.All));
    }

    /// <summary>Programs the background-app trim never touches (lower-case, no ".exe").</summary>
    public IReadOnlyList<string> TrimExclusions => _trimExclusions;

    /// <summary>
    /// Replaces the trim exclusions. Invalid names are returned in
    /// <paramref name="rejected"/> and not stored; at most <see cref="MaxTrimExclusions"/> are kept.
    /// </summary>
    public IReadOnlyList<string> SetTrimExclusions(IEnumerable<string> names, out List<string> rejected)
    {
        rejected = new List<string>();
        var accepted = new List<string>();
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var name = MemoryCleaner.NormalizeProgramName(raw);
            if (name == null)
                rejected.Add(raw.Trim());
            else if (!accepted.Contains(name) && accepted.Count < MaxTrimExclusions)
                accepted.Add(name);
        }

        var value = accepted.ToArray();
        if (!value.SequenceEqual(_trimExclusions))
        {
            _trimExclusions = value;
            ScheduleSave();
        }

        return value;
    }

    private void SetInt(ref int field, int value)
    {
        if (Volatile.Read(ref field) == value) return;
        Volatile.Write(ref field, value);
        ScheduleSave();
    }

    private void SetBool(ref bool field, bool value)
    {
        if (Volatile.Read(ref field) == value) return;
        Volatile.Write(ref field, value);
        ScheduleSave();
    }

    #endregion

    #region Memory info

    /// <summary>
    /// Current memory picture (see <see cref="MemoryCleaner.ReadMemoryInfo"/>), without
    /// the compression-store figure, so it costs microseconds even on a UI thread.
    /// </summary>
    public MemoryInfo GetMemoryInfo() => MemoryCleaner.ReadMemoryInfo(includeCompressedStore: false);

    /// <summary>Processes using the most RAM (private working set).</summary>
    public List<ProcessMemoryInfo> GetTopMemoryConsumers(int count = 10) => MemoryCleaner.GetTopProcesses(count);

    #endregion

    #region Cleanup

    /// <summary>True while a cleanup (manual or automatic) is running.</summary>
    public bool IsRunning
    {
        get { lock (_runLock) return _running is { IsCompleted: false }; }
    }

    /// <summary>The cleanup in progress, or null.</summary>
    public Task<OptimizeResult>? CurrentRun
    {
        get { lock (_runLock) return _running is { IsCompleted: false } running ? running : null; }
    }

    /// <summary>
    /// Starts a cleanup on a worker thread. If one is already running, that run is
    /// returned instead of starting a second one (double click, Dashboard + RAM
    /// page, or an automatic run in progress).
    /// </summary>
    public Task<OptimizeResult> RunAsync(MemoryCleanItems items, OptimizeTrigger trigger = OptimizeTrigger.Manual)
    {
        lock (_runLock)
        {
            if (_running is { IsCompleted: false } running)
                return running;
            return _running = StartLocked(items, trigger);
        }
    }

    /// <summary>Runs these exact items, waiting for any other cleanup to finish first.</summary>
    private OptimizeResult RunExclusive(MemoryCleanItems items)
    {
        Task<OptimizeResult> mine;
        while (true)
        {
            Task<OptimizeResult> other;
            lock (_runLock)
            {
                if (_running is not { IsCompleted: false } busy)
                {
                    mine = _running = StartLocked(items, OptimizeTrigger.Manual);
                    break;
                }
                other = busy;
            }

            try { other.Wait(); } catch { /* its failure is not ours */ }
        }

        return mine.GetAwaiter().GetResult();
    }

    private Task<OptimizeResult> StartLocked(MemoryCleanItems items, OptimizeTrigger trigger)
    {
        var exclusions = _trimExclusions;
        var token = _shutdown.Token;
        var task = Task.Run(() => Execute(items, trigger, exclusions, token));

        // Raised once the run has completed: handlers see IsRunning == false and
        // may start another cleanup without waiting on the one that raised it.
        task.ContinueWith(t => RaiseCompleted(t.Result), CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private void RaiseCompleted(OptimizeResult result)
    {
        // Worker thread, outside every lock. A UI handler must marshal to its
        // dispatcher and must not be able to break anything here.
        try { OnOptimizationComplete?.Invoke(result); }
        catch (Exception ex) { Debug.WriteLine($"[RamOptimizer] Completion handler failed: {ex.Message}"); }
    }

    private OptimizeResult Execute(MemoryCleanItems items, OptimizeTrigger trigger, string[] exclusions, CancellationToken token)
    {
        OptimizeResult result;
        try
        {
            result = MemoryCleaner.Run(items, trigger, exclusions, token);
        }
        catch (Exception ex)
        {
            // MemoryCleaner.Run doesn't throw; this only keeps a bug from killing the worker.
            var now = DateTime.Now;
            result = new OptimizeResult { StartTime = now, EndTime = now, Trigger = trigger, Items = items, ErrorMessage = ex.Message };
        }

        lock (_historyLock)
        {
            _recent.Insert(0, result);
            if (_recent.Count > MaxRecentResults)
                _recent.RemoveAt(_recent.Count - 1);
        }

        // Any cleanup — manual ones too — starts the automatic cooldown, so the
        // automatic mode doesn't clean again seconds after the user did.
        Volatile.Write(ref _lastCleanupUtcTicks, DateTime.UtcNow.Ticks);

        return result;
    }

    /// <summary>
    /// Runs the configured items now and waits for the result (Dashboard button).
    /// Blocks — call it off the UI thread.
    /// </summary>
    public OptimizeResult OptimizeNow()
    {
        var items = CleanItems == MemoryCleanItems.None ? MemoryCleanItems.Safe : CleanItems;
        return RunAsync(items).GetAwaiter().GetResult();
    }

    /// <summary>
    /// "Free standby memory" (Windows Tricks): trims idle background apps, then
    /// purges the whole standby list. Returns whether the purge succeeded.
    /// Blocks — call it off the UI thread.
    /// </summary>
    public bool ClearStandbyList() =>
        RunExclusive(MemoryCleanItems.TrimWorkingSets | MemoryCleanItems.PurgeStandbyList).StandbyCleared;

    /// <summary>Trims one process picked in the list (see <see cref="MemoryCleaner.TrimProcess"/>).</summary>
    public ProcessTrimResult TrimProcess(int processId, string expectedName, long expectedCreateTime) =>
        MemoryCleaner.TrimProcess(processId, expectedName, expectedCreateTime);

    /// <summary>Recent cleanups of this app session, newest first.</summary>
    public IReadOnlyList<OptimizeResult> GetRecentResults()
    {
        lock (_historyLock)
            return _recent.ToArray();
    }

    #endregion

    #region Automatic mode

    private void StartAutoTimer()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _loadStreak = _lowFreeStreak = 0;
            _autoTimer ??= new Timer(AutoTick, null, AutoCheckInterval, AutoCheckInterval);
        }
    }

    private void StopAutoTimer()
    {
        lock (_timerLock)
        {
            _autoTimer?.Dispose();
            _autoTimer = null;
        }
    }

    private void AutoTick(object? state)
    {
        if (!_autoEnabled || _disposed) return;
        if (Interlocked.Exchange(ref _autoTickBusy, 1) == 1) return; // previous tick still running

        try
        {
            if (IsRunning || !MemoryCleaner.IsElevated)
                return;

            var info = MemoryCleaner.ReadMemoryInfo(includeCompressedStore: false);
            if (info.TotalMemoryMB <= 0)
                return;

            bool loadHigh = _triggerOnLoad && info.UsagePercent >= _thresholdPercent;
            bool freeLow = _triggerOnLowFree && info.HasListDetail &&
                           info.FreeMemoryMB < _lowFreeMB && info.StandbyMemoryMB >= _minStandbyMB;

            // Two samples in a row: a one-off spike doesn't trigger a cleanup.
            _loadStreak = loadHigh ? _loadStreak + 1 : 0;
            _lowFreeStreak = freeLow ? _lowFreeStreak + 1 : 0;

            OptimizeTrigger trigger;
            MemoryCleanItems items;
            if (_loadStreak >= SamplesToTrigger)
            {
                trigger = OptimizeTrigger.AutoMemoryLoad;
                items = CleanItems;
            }
            else if (_lowFreeStreak >= SamplesToTrigger)
            {
                // ISLC: what helps when free memory runs out is emptying the cache.
                trigger = OptimizeTrigger.AutoLowFreeMemory;
                items = MemoryCleanItems.PurgeStandbyList;
            }
            else
            {
                return;
            }

            if (items == MemoryCleanItems.None)
                return;
            if (DateTime.UtcNow.Ticks - Volatile.Read(ref _lastCleanupUtcTicks) < TimeSpan.FromMinutes(_cooldownMinutes).Ticks)
                return;

            Volatile.Write(ref _lastCleanupUtcTicks, DateTime.UtcNow.Ticks);
            _loadStreak = _lowFreeStreak = 0;
            _ = RunAsync(items, trigger); // result goes to history and OnOptimizationComplete
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RamOptimizer] Automatic check failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _autoTickBusy, 0);
        }
    }

    #endregion

    #region Settings persistence

    // Settings live in the admin-only store: the elevated app acts on them on its
    // own (automatic mode), so a file in the user-writable %LocalAppData% can't be trusted.
    private void LoadSettings()
    {
        var settings = AdminOnlyStore.Load<RamOptimizerSettings>(SettingsFileName);
        if (settings != null)
        {
            Apply(settings, trusted: true);
            return;
        }

        // Present but untrusted/corrupt: keep the defaults.
        if (AdminOnlyStore.Exists(SettingsFileName))
            return;

        // One-time import of the old per-user file (user-writable). Only bounded
        // values are taken: the switch, a 5–120 min cooldown and a 50–95 %
        // threshold. The clean items stay at the Safe default, so the worst a
        // planted file can do is a safe cleanup every few minutes.
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
                    Apply(legacy, trusted: false);
                    lock (_settingsLock) _savePending = true;
                    FlushSettings();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RamOptimizer] Legacy settings ignored: {ex.Message}");
        }
    }

    /// <summary>Takes loaded values through the same bounds as the setters.</summary>
    private void Apply(RamOptimizerSettings settings, bool trusted)
    {
        _autoEnabled = settings.AutoOptimizeEnabled;
        _thresholdPercent = Math.Clamp(settings.ThresholdPercent, MinThresholdPercent, MaxThresholdPercent);

        if (!trusted)
        {
            _cooldownMinutes = Math.Clamp(settings.IntervalMinutes, 5, MaxCooldownMinutes);
            return;
        }

        _cooldownMinutes = Math.Clamp(settings.IntervalMinutes, MinCooldownMinutes, MaxCooldownMinutes);
        _triggerOnLoad = settings.TriggerOnMemoryLoad;
        _triggerOnLowFree = settings.TriggerOnLowFreeMemory;
        _lowFreeMB = Math.Clamp(settings.LowFreeMB, MinListThresholdMB, MaxListThresholdMB);
        _minStandbyMB = Math.Clamp(settings.MinStandbyMB, MinListThresholdMB, MaxListThresholdMB);
        _cleanItems = settings.CleanItems is int items
            ? items & (int)MemoryCleanItems.All
            : (int)MemoryCleanItems.Safe; // files from before the item list existed
        SetTrimExclusionsSilently(settings.TrimExclusions);
    }

    private void SetTrimExclusionsSilently(IEnumerable<string>? names)
    {
        _trimExclusions = (names ?? Enumerable.Empty<string>())
            .Select(MemoryCleaner.NormalizeProgramName)
            .Where(n => n != null)
            .Select(n => n!)
            .Distinct()
            .Take(MaxTrimExclusions)
            .ToArray();
    }

    private RamOptimizerSettings BuildSettings() => new()
    {
        AutoOptimizeEnabled = _autoEnabled,
        IntervalMinutes = _cooldownMinutes,
        ThresholdPercent = _thresholdPercent,
        TriggerOnMemoryLoad = _triggerOnLoad,
        TriggerOnLowFreeMemory = _triggerOnLowFree,
        LowFreeMB = _lowFreeMB,
        MinStandbyMB = _minStandbyMB,
        CleanItems = _cleanItems,
        TrimExclusions = _trimExclusions.ToList()
    };

    /// <summary>Saves a moment after the last change, so dragging a slider doesn't write the file on every step.</summary>
    private void ScheduleSave()
    {
        lock (_settingsLock) _savePending = true;
        try { _saveTimer.Change(SaveDelayMilliseconds, Timeout.Infinite); }
        catch (ObjectDisposedException) { FlushSettings(); }
    }

    /// <summary>Writes pending setting changes now (no-op when nothing changed).</summary>
    public void FlushSettings()
    {
        lock (_settingsLock)
        {
            if (!_savePending) return;
            _savePending = false;
            if (!AdminOnlyStore.Save(SettingsFileName, BuildSettings()))
                Debug.WriteLine("[RamOptimizer] Settings not persisted (admin-only store unavailable).");
        }
    }

    #endregion

    /// <summary>
    /// Raised on a worker thread after every cleanup (manual or automatic), with
    /// the result. Marshal to the UI thread before touching controls.
    /// </summary>
    public event Action<OptimizeResult>? OnOptimizationComplete;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAutoTimer();
        _shutdown.Cancel(); // a running cleanup stops between processes/operations
        FlushSettings();
        _saveTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class MemoryInfo
{
    public DateTime Timestamp { get; set; }

    /// <summary>Physical RAM Windows can use (installed minus hardware-reserved).</summary>
    public long TotalMemoryMB { get; set; }

    /// <summary>RAM installed in the PC, or 0 when the firmware doesn't say.</summary>
    public long InstalledMemoryMB { get; set; }

    /// <summary>Standby cache + free memory: what apps can get right away.</summary>
    public long AvailableMemoryMB { get; set; }

    /// <summary>Total minus available (in use + modified). Matches <see cref="UsagePercent"/>.</summary>
    public long UsedMemoryMB { get; set; }

    public int UsagePercent { get; set; }
    public long CommitUsedMB { get; set; }
    public long CommitLimitMB { get; set; }

    public MemoryDataSource Source { get; set; }

    /// <summary>True when the free/standby/modified split below is known.</summary>
    public bool HasListDetail => Source != MemoryDataSource.Basic;

    /// <summary>Free + zeroed pages: memory nothing is using at all. -1 when unknown.</summary>
    public long FreeMemoryMB { get; set; } = -1;

    /// <summary>Modified list: changed data waiting to be written to disk. -1 when unknown.</summary>
    public long ModifiedMemoryMB { get; set; } = -1;

    /// <summary>Standby list, all priorities (the "cache"). -1 when unknown.</summary>
    public long StandbyMemoryMB { get; set; } = -1;

    /// <summary>Priority-0 part of the standby list. -1 when unknown.</summary>
    public long StandbyLowPriorityMB { get; set; } = -1;

    /// <summary>Standby + modified — what Task Manager calls "Cached". -1 when unknown.</summary>
    public long CachedMemoryMB { get; set; } = -1;

    /// <summary>RAM held by the compression store (part of "in use"). -1 when unknown.</summary>
    public long CompressedMemoryMB { get; set; } = -1;

    /// <summary>Memory actively used by processes and Windows (Task Manager's "In use").</summary>
    public long InUseMemoryMB => HasListDetail
        ? Math.Max(0, TotalMemoryMB - FreeMemoryMB - StandbyMemoryMB - ModifiedMemoryMB)
        : UsedMemoryMB;
}

public class OptimizeResult
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public OptimizeTrigger Trigger { get; set; }
    public MemoryCleanItems Items { get; set; }
    public MemoryInfo MemoryBefore { get; set; } = new();
    public MemoryInfo MemoryAfter { get; set; } = new();

    /// <summary>
    /// Available memory after minus before, measured after a short settle. Can be
    /// zero or negative (other apps keep allocating) — never inflated. Purging the
    /// cache does not raise it: cache already counts as available.
    /// </summary>
    public long MemoryFreedMB { get; set; }

    /// <summary>Free memory (zero + free lists) after minus before — where a cache purge shows up.</summary>
    public long FreeGainedMB { get; set; }

    public int ProcessesOptimized { get; set; }
    public bool StandbyCleared { get; set; }

    /// <summary>Every operation that ran succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>English diagnostics only; the UI words results from <see cref="Operations"/>.</summary>
    public string? ErrorMessage { get; set; }

    public IReadOnlyList<MemoryOperationResult> Operations { get; set; } = Array.Empty<MemoryOperationResult>();

    public bool AnyOperationSucceeded => Operations.Any(o => o.Succeeded);
}

public class ProcessMemoryInfo
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";

    /// <summary>Private working set: RAM used only by this process (Task Manager's "Memory" column).</summary>
    public long MemoryMB { get; set; }

    /// <summary>Private bytes (commit), including what Windows moved to the page file.</summary>
    public long PrivateMemoryMB { get; set; }

    /// <summary>Full working set, including pages shared with other processes.</summary>
    public long WorkingSetMB { get; set; }

    /// <summary>Creation time (FILETIME, UTC). Identifies this exact process instance.</summary>
    public long CreateTime { get; set; }

    public int SessionId { get; set; }
    public bool IsSelf { get; set; }

    /// <summary>False for WinXTools itself and Windows core processes.</summary>
    public bool CanTrim { get; set; }

    /// <summary>False for WinXTools itself and protected Windows processes.</summary>
    public bool CanEnd { get; set; }
}

public class RamOptimizerSettings
{
    public bool AutoOptimizeEnabled { get; set; }

    /// <summary>
    /// Minimum minutes between automatic cleanups. (Older versions used it as the
    /// check period; it keeps its name so their files still load.)
    /// </summary>
    public int IntervalMinutes { get; set; } = 10;

    public int ThresholdPercent { get; set; } = 80;
    public bool TriggerOnMemoryLoad { get; set; } = true;
    public bool TriggerOnLowFreeMemory { get; set; }
    public int LowFreeMB { get; set; } = 1024;
    public int MinStandbyMB { get; set; } = 1024;

    /// <summary><see cref="MemoryCleanItems"/> flags; null in files written before it existed (→ Safe).</summary>
    public int? CleanItems { get; set; }

    public List<string>? TrimExclusions { get; set; }
}

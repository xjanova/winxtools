using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using NetX.Core.Helpers;
using NetX.Core.Optimization;

namespace NetX.Core.System.Storage;

public enum PlacementPhase { Planning, MeasuringBefore, Moving, MeasuringAfter, Done }

/// <summary>
/// One run of smart placement (one at a time, never together with Windows'
/// defragmenter): read what programs load from the drive, plan, optionally
/// measure, move, measure again. It outlives the page that started it, like
/// <see cref="DefragSession"/>, and closing WinXTools stops it.
/// </summary>
public sealed class SmartPlacementSession
{
    private static readonly object Gate = new();
    private static SmartPlacementSession? _current;

    private const long MeasureBytes = 512L << 20;
    private static readonly TimeSpan MeasureTime = TimeSpan.FromSeconds(20);

    private readonly SafeOperationGuard _guard;
    private readonly DriveVolume _volume;
    private readonly bool _measure;
    private readonly Thread _thread;

    public static SmartPlacementSession? Current
    {
        get { lock (Gate) return _current; }
    }

    public char Letter => _volume.Letter;
    public bool Automatic { get; }
    public bool Removable { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public PlacementPhase Phase { get; private set; } = PlacementPhase.Planning;
    public HotFileReport? Report { get; private set; }
    public PlacementPlan? Plan { get; private set; }
    public PlacementProgress? LastProgress { get; private set; }
    public PlacementResult? Result { get; private set; }

    /// <summary>MB/s reading the planned files in load order, straight from the disk, before and after.</summary>
    public double? SpeedBefore { get; private set; }
    public double? SpeedAfter { get; private set; }

    public bool IsRunning => Result == null;
    public bool StopRequested => _guard.Token.IsCancellationRequested;

    /// <summary>Phase or progress changed (worker thread).</summary>
    public event Action<SmartPlacementSession>? Changed;

    /// <summary>Finished, failed or stopped (worker thread).</summary>
    public event Action<SmartPlacementSession, PlacementResult>? Completed;

    private SmartPlacementSession(DriveVolume volume, bool measure, bool automatic)
    {
        _volume = volume;
        _measure = measure;
        Automatic = automatic;
        Removable = volume.IsRemovable || volume.Disk?.Bus is DriveBus.Usb or DriveBus.SdCard;
        _guard = new SafeOperationGuard(volume.Letter, $"WinXTools is arranging files on drive {volume.Letter}:",
            flushPeriodically: Removable, holdsHandles: true);
        _thread = new Thread(Run) { IsBackground = true, Name = "WinXTools smart placement" };
    }

    /// <summary>Null when something else is already moving data (WinXTools or Windows' defragmenter started from here).</summary>
    public static SmartPlacementSession? Start(DriveVolume volume, bool measure, bool automatic)
    {
        lock (DefragSession.DriveWorkGate)
        lock (Gate)
        {
            if (_current is { IsRunning: true } || DefragSession.Current is { IsRunning: true }) return null;
            var session = new SmartPlacementSession(volume, measure, automatic);
            _current = session;
            session._thread.Start();
            return session;
        }
    }

    public void Stop(StopReason reason = StopReason.User) => _guard.Stop(reason);

    /// <summary>WinXTools is closing: stop between two pieces and wait a little.</summary>
    public static void StopRunning(TimeSpan wait)
    {
        var session = Current;
        if (session is not { IsRunning: true }) return;
        session.Stop();
        session._thread.Join(wait);
    }

    private void Run()
    {
        PlacementResult result;
        var clock = Stopwatch.StartNew();
        var token = _guard.Token;
        try
        {
            Report = HotFiles.Read(Letter, token);
            Plan = SmartPlacement.Plan(Letter, Report.Files, SmartPlacement.DefaultBudgetBytes, token);
            Raise();

            if (Plan.Moves.Count == 0)
            {
                result = new PlacementResult { Outcome = PlacementOutcome.NothingToDo, Duration = clock.Elapsed };
            }
            else
            {
                var files = Plan.Moves.Select(m => m.Path).ToList();
                if (_measure)
                {
                    Phase = PlacementPhase.MeasuringBefore;
                    Raise();
                    SpeedBefore = MeasureFromDisk(files, token);
                }

                Phase = PlacementPhase.Moving;
                Raise();
                result = SmartPlacement.Execute(Plan, _guard, Removable, new InlineProgress(p =>
                {
                    LastProgress = p;
                    Raise();
                }));

                if (_measure && result.Outcome == PlacementOutcome.Completed && !token.IsCancellationRequested)
                {
                    Phase = PlacementPhase.MeasuringAfter;
                    Raise();
                    SpeedAfter = MeasureFromDisk(files, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = new PlacementResult { Outcome = PlacementOutcome.Stopped, StopReason = _guard.Reason ?? StopReason.User, Duration = clock.Elapsed };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmartPlacement] {ex}");
            result = new PlacementResult { Outcome = PlacementOutcome.Failed, LastError = ex.HResult, Duration = clock.Elapsed };
        }
        finally
        {
            _guard.MarkReleased();
        }

        if (result.Outcome != PlacementOutcome.Stopped && _guard.Reason != null)
            result = new PlacementResult
            {
                Outcome = PlacementOutcome.Stopped, Moved = result.Moved, Failed = result.Failed, BytesMoved = result.BytesMoved,
                StopReason = _guard.Reason, Duration = result.Duration, LastError = result.LastError
            };

        Phase = PlacementPhase.Done;
        Result = result;
        try { Completed?.Invoke(this, result); }
        catch (Exception ex) { Debug.WriteLine($"[SmartPlacement] Completed handler failed: {ex.Message}"); }
        _guard.Dispose();
    }

    /// <summary>
    /// Reads the files straight from the disk. The drive's own cache (up to a few
    /// hundred MB) is filled with other data first, so the number comes from the platter.
    /// </summary>
    private double? MeasureFromDisk(IReadOnlyList<string> files, CancellationToken token)
    {
        if (_volume.DiskNumber is int disk && _volume.Disk is { SizeBytes: > 0 } d)
            FileReadSpeedTest.EvictDriveCache(disk, d.SizeBytes, token);
        return FileReadSpeedTest.Measure(files, MeasureBytes, MeasureTime, token);
    }

    private void Raise()
    {
        try { Changed?.Invoke(this); }
        catch (Exception ex) { Debug.WriteLine($"[SmartPlacement] Changed handler failed: {ex.Message}"); }
    }

    private sealed class InlineProgress(Action<PlacementProgress> report) : IProgress<PlacementProgress>
    {
        public void Report(PlacementProgress value) => report(value);
    }
}

/// <summary>
/// The optional "while the PC is idle" mode: when nobody has touched the PC for
/// 10 minutes, it runs on mains power, nothing is full screen and nothing else
/// is moving data, it arranges one internal hard disk (at most once a day per
/// drive) and stops the moment the mouse or keyboard is used. Never on USB or
/// other removable drives — they could be unplugged without anyone knowing work
/// is in progress. Off unless the user turns it on.
/// </summary>
public sealed class SmartDefragService
{
    public static SmartDefragService Instance { get; } = new();

    private const string SettingsFile = "smart_defrag.json";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleNeeded = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OncePer = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private Settings _settings = new();
    private Timer? _timer;
    private Timer? _activityWatch;
    private int _busy;
    private long _nextScanUtcTicks;

    public sealed class Settings
    {
        public bool AutoWhenIdle { get; set; }
        public Dictionary<string, long> LastAutoRunUtcTicks { get; set; } = new();
    }

    /// <summary>The last automatic run of this app session (for the page).</summary>
    public (char Letter, DateTime EndedLocal, PlacementResult Result)? LastAutomatic { get; private set; }

    public bool AutoWhenIdle
    {
        get { lock (_gate) return _settings.AutoWhenIdle; }
        set
        {
            lock (_gate)
            {
                if (_settings.AutoWhenIdle == value) return;
                _settings.AutoWhenIdle = value;
                AdminOnlyStore.Save(SettingsFile, _settings);
                UpdateTimer();
            }
        }
    }

    /// <summary>App start (does nothing unless running as administrator with the idle mode on).</summary>
    public void Start()
    {
        if (!IsElevated()) return;
        lock (_gate)
        {
            // Admin-only store: the elevated app acts on this file by itself.
            _settings = AdminOnlyStore.Load<Settings>(SettingsFile) ?? new Settings();
            _settings.LastAutoRunUtcTicks ??= new();
            UpdateTimer();
        }
    }

    /// <summary>App exit: no new runs; a running one stops between two pieces.</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _activityWatch?.Dispose();
            _activityWatch = null;
        }
        SmartPlacementSession.StopRunning(TimeSpan.FromSeconds(10));
    }

    private void UpdateTimer()
    {
        if (_settings.AutoWhenIdle && _timer == null) _timer = new Timer(_ => Tick(), null, CheckEvery, CheckEvery);
        else if (!_settings.AutoWhenIdle && _timer != null)
        {
            _timer.Dispose();
            _timer = null;
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (!AutoWhenIdle) return;
            if (DefragSession.Current is { IsRunning: true } || SmartPlacementSession.Current is { IsRunning: true }) return;
            if (IdleTime() < IdleNeeded) return;
            var power = DriveSafety.PowerStatus();
            if (power.HasBattery && !power.OnAc) return;
            if (AppMemory.ForegroundIsFullscreen()) return;

            var now = DateTime.UtcNow.Ticks;
            if (now < Interlocked.Read(ref _nextScanUtcTicks)) return;
            foreach (var volume in DriveInventory.GetVolumes())
            {
                if (volume.Disk?.IsHdd != true || !volume.IsNtfs || volume.IsRemovable ||
                    volume.Disk.Bus is DriveBus.Usb or DriveBus.SdCard)
                    continue;
                var key = volume.Letter.ToString();
                lock (_gate)
                {
                    if (_settings.LastAutoRunUtcTicks.TryGetValue(key, out long last) && now - last < OncePer.Ticks) continue;
                }
                if (!DriveSafety.Check(volume, ownMoves: true).CanStart) continue;

                var session = SmartPlacementSession.Start(volume, measure: false, automatic: true);
                if (session == null) return;
                lock (_gate)
                {
                    _settings.LastAutoRunUtcTicks[key] = now;
                    AdminOnlyStore.Save(SettingsFile, _settings);
                }
                WatchForUser(session);
                return; // one drive per idle spell
            }
            // Every eligible drive is done for today: don't list the drives again for a while.
            Interlocked.Exchange(ref _nextScanUtcTicks, now + TimeSpan.FromHours(1).Ticks);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmartDefrag] Idle check failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>The moment the mouse or keyboard is used, the automatic run stops between two pieces.</summary>
    private void WatchForUser(SmartPlacementSession session)
    {
        uint startInput = LastInputTick();
        session.Completed += (s, result) =>
        {
            LastAutomatic = (s.Letter, DateTime.Now, result);
            lock (_gate)
            {
                _activityWatch?.Dispose();
                _activityWatch = null;
            }
        };
        lock (_gate)
        {
            _activityWatch?.Dispose();
            _activityWatch = new Timer(_ =>
            {
                if (session.IsRunning && LastInputTick() != startInput) session.Stop(StopReason.UserActive);
            }, null, 1000, 1000);
        }
    }

    private static TimeSpan IdleTime()
    {
        uint last = LastInputTick();
        return last == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - last));
    }

    private static uint LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.dwTime : 0;
    }

    private static bool IsElevated()
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}

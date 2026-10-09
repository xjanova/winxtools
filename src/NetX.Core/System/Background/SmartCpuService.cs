using System.Diagnostics;
using System.ServiceProcess;
using NetX.Core.Helpers;

namespace NetX.Core.System.Background;

/// <summary>One change Smart CPU made, with what is needed to undo it.</summary>
public sealed class SmartCpuChange
{
    /// <summary><see cref="AutostartEntry.Key"/>, or "Policy|setting" for a browser setting.</summary>
    public string Key { get; set; } = "";
    public AutostartKind? Kind { get; set; }
    public BrowserSetting? Setting { get; set; }
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public bool MachineWide { get; set; }

    /// <summary>The program it is about, for the list ("Google Updater").</summary>
    public string Display { get; set; } = "";

    public ServiceStartMode? OriginalStartMode { get; set; }
    public bool OriginalDelayed { get; set; }

    /// <summary>The browser policy's value before (null = not set).</summary>
    public int? OriginalPolicy { get; set; }

    public long ChangedUtcTicks { get; set; }

    /// <summary>Turn it off again if the program turns it back on (updates often do).</summary>
    public bool KeepOff { get; set; } = true;
    public int TurnedOffAgain { get; set; }
}

public sealed class SmartCpuSettings
{
    public bool AutoEfficiency { get; set; }
    public List<SmartCpuChange> Changes { get; set; } = new();
}

/// <summary>
/// Smart CPU's memory and watchdogs. Every change it makes is recorded with the
/// original state, so each one can be undone; programs that switch their
/// start-up back on (often after an update) are switched off again, as the user
/// asked; and, only if the user turns it on, background programs that keep the
/// CPU busy are put into Windows' Efficiency mode — never closed. Settings live
/// in the admin-only store: the elevated app acts on them by itself.
/// Smart CPU is a Pro feature: without Pro the watchdogs rest, but the changes
/// already made stay and can always be undone.
/// </summary>
public sealed class SmartCpuService
{
    public static SmartCpuService Instance { get; } = new();

    private const string SettingsFile = "smart_cpu.json";
    private const double BusyPercent = 5.0;              // of the whole CPU, over the last minute
    private static readonly TimeSpan WatchEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan EfficiencyEvery = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _busyStreak = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(DateTime When, string App, double Cpu)> _efficiencyLog = new();
    private SmartCpuSettings _settings = new();
    private Timer? _watchTimer, _efficiencyTimer;
    private int _watchBusy, _efficiencyBusy;
    private bool _started;

    /// <summary>Something happened on its own the user should hear about (worker thread).</summary>
    public event Action<string>? Notice;

    public IReadOnlyList<SmartCpuChange> Changes
    {
        get { lock (_gate) return _settings.Changes.ToList(); }
    }

    /// <summary>Programs put into Efficiency mode automatically in this session, newest first.</summary>
    public IReadOnlyList<(DateTime When, string App, double Cpu)> EfficiencyLog
    {
        get { lock (_gate) return _efficiencyLog.AsEnumerable().Reverse().ToList(); }
    }

    public bool AutoEfficiency
    {
        get { lock (_gate) return _settings.AutoEfficiency; }
        set
        {
            lock (_gate)
            {
                if (_settings.AutoEfficiency == value) return;
                _settings.AutoEfficiency = value;
                Save();
                UpdateTimers();
            }
        }
    }

    public void Start()
    {
        if (!Autostart.IsElevated()) return;
        lock (_gate)
        {
            _settings = AdminOnlyStore.Load<SmartCpuSettings>(SettingsFile) ?? new SmartCpuSettings();
            _settings.Changes ??= new();
            _started = true;
            UpdateTimers();
        }
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            _started = false;
            _watchTimer?.Dispose();
            _watchTimer = null;
            _efficiencyTimer?.Dispose();
            _efficiencyTimer = null;
        }
    }

    private void UpdateTimers()
    {
        if (!_started) return;
        bool watch = _settings.Changes.Any(c => c.KeepOff);
        if (watch && _watchTimer == null) _watchTimer = new Timer(_ => Watch(), null, TimeSpan.FromMinutes(2), WatchEvery);
        else if (!watch && _watchTimer != null)
        {
            _watchTimer.Dispose();
            _watchTimer = null;
        }

        if (_settings.AutoEfficiency && _efficiencyTimer == null)
            _efficiencyTimer = new Timer(_ => EfficiencyTick(), null, EfficiencyEvery, EfficiencyEvery);
        else if (!_settings.AutoEfficiency && _efficiencyTimer != null)
        {
            _efficiencyTimer.Dispose();
            _efficiencyTimer = null;
            _busyStreak.Clear();
        }
    }

    private void Save()
    {
        if (!AdminOnlyStore.Save(SettingsFile, _settings))
            Debug.WriteLine("[SmartCpu] Settings not saved (admin-only store unavailable).");
    }

    #region Changes and undo

    /// <summary>Turns an autostart entry off where it is set up, and remembers how to undo it. Null on success.</summary>
    public string? TurnOff(AutostartEntry entry, string display)
    {
        if (!entry.Enabled) return null;
        var error = AutostartControl.SetEnabled(entry, enabled: false);
        if (error != null) return error;
        lock (_gate)
        {
            _settings.Changes.RemoveAll(c => c.Key == entry.Key);
            _settings.Changes.Add(new SmartCpuChange
            {
                Key = entry.Key,
                Kind = entry.Kind,
                Name = entry.Name,
                Location = entry.Location,
                MachineWide = entry.MachineWide,
                Display = display,
                OriginalStartMode = entry.StartMode,
                OriginalDelayed = entry.DelayedStart,
                ChangedUtcTicks = DateTime.UtcNow.Ticks
            });
            Save();
            UpdateTimers();
        }
        return null;
    }

    /// <summary>Puts things back exactly as they were before the change. Null on success.</summary>
    public string? Undo(SmartCpuChange change)
    {
        string? error;
        if (change.Setting is BrowserSetting setting)
        {
            error = BrowserPolicies.Set(setting, change.OriginalPolicy);
        }
        else if (change.Kind is AutostartKind kind)
        {
            var entry = new AutostartEntry
            {
                Kind = kind, Name = change.Name, Location = change.Location, MachineWide = change.MachineWide,
                StartMode = change.OriginalStartMode, DelayedStart = change.OriginalDelayed
            };
            error = AutostartControl.SetEnabled(entry, enabled: true, change.OriginalStartMode, change.OriginalDelayed);
        }
        else
        {
            error = "Unknown change";
        }
        if (error != null) return error;
        lock (_gate)
        {
            _settings.Changes.RemoveAll(c => c.Key == change.Key);
            Save();
            UpdateTimers();
        }
        return null;
    }

    /// <summary>Undoes every change (with or without Pro). Returns what couldn't be put back.</summary>
    public List<string> UndoAll()
    {
        var errors = new List<string>();
        foreach (var change in Changes)
        {
            var error = Undo(change);
            if (error != null) errors.Add($"{change.Display}: {error}");
        }
        return errors;
    }

    /// <summary>Applies WinXTools' recommended value for a browser setting (or removes it), remembering the old one.</summary>
    public string? SetBrowserSetting(BrowserSetting setting, bool apply)
    {
        string key = "Policy|" + setting;
        SmartCpuChange? existing;
        lock (_gate) existing = _settings.Changes.FirstOrDefault(c => c.Key == key);

        // Switching back only undoes WinXTools' own change: a policy someone else set stays.
        if (!apply) return existing != null ? Undo(existing) : null;
        if (existing != null) return BrowserPolicies.Set(setting, BrowserPolicies.Recommended(setting));

        int? before = BrowserPolicies.Get(setting);
        var error = BrowserPolicies.Set(setting, BrowserPolicies.Recommended(setting));
        if (error != null) return error;
        lock (_gate)
        {
            if (!_settings.Changes.Any(c => c.Key == key))
                _settings.Changes.Add(new SmartCpuChange
                {
                    Key = key, Setting = setting, Name = setting.ToString(), Location = "Policies",
                    Display = setting.ToString(), OriginalPolicy = before, ChangedUtcTicks = DateTime.UtcNow.Ticks, KeepOff = false
                });
            Save();
        }
        return null;
    }

    #endregion

    #region Watchdogs

    /// <summary>Programs that switched their start-up back on are switched off again (as the user asked).</summary>
    private void Watch()
    {
        if (!HasProAccess()) return;
        if (Interlocked.Exchange(ref _watchBusy, 1) == 1) return;
        try
        {
            List<SmartCpuChange> keep;
            lock (_gate) keep = _settings.Changes.Where(c => c.KeepOff && c.Kind != null).ToList();
            if (keep.Count == 0) return;

            var entries = Autostart.Enumerate(includeServices: keep.Any(c => c.Kind == AutostartKind.Service),
                includeTasks: keep.Any(c => c.Kind == AutostartKind.ScheduledTask)).ToDictionary(e => e.Key, e => e);
            foreach (var change in keep)
            {
                if (!entries.TryGetValue(change.Key, out var entry) || !entry.Enabled) continue;
                if (AutostartControl.SetEnabled(entry, enabled: false) != null) continue;
                lock (_gate)
                {
                    change.TurnedOffAgain++;
                    Save();
                }
                try { Notice?.Invoke(change.Display); }
                catch (Exception ex) { Debug.WriteLine($"[SmartCpu] Notice handler failed: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmartCpu] Watch failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _watchBusy, 0);
        }
    }

    /// <summary>
    /// Background programs (no window, not a helper of an open program, not
    /// Windows, security software or a device helper) that used at least 5 % of
    /// the whole CPU over the last minute, twice in a row, go into Efficiency mode.
    /// </summary>
    private void EfficiencyTick()
    {
        if (!HasProAccess()) return;
        if (Interlocked.Exchange(ref _efficiencyBusy, 1) == 1) return;
        try
        {
            var report = BackgroundScanner.Scan(includeAutostart: false);
            var busyNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in report.Apps)
            {
                if (app.InEfficiencyMode || app.HelperOfOpenApp || app.Path == null) continue;
                if (app.Class is not (ProgramClass.App or ProgramClass.Updater or ProgramClass.Suspicious)) continue;
                if (app.Name.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)) continue;
                if (app.CpuLastMinute < BusyPercent) continue;

                busyNow.Add(app.Path);
                int streak;
                lock (_gate) streak = _busyStreak[app.Path] = _busyStreak.GetValueOrDefault(app.Path) + 1;
                if (streak < 2) continue;

                int done = app.Processes.Count(p => Efficiency.Set(p, on: true));
                if (done == 0) continue;
                lock (_gate)
                {
                    _efficiencyLog.Add((DateTime.Now, app.DisplayName, app.CpuLastMinute));
                    if (_efficiencyLog.Count > 50) _efficiencyLog.RemoveAt(0);
                }
            }
            lock (_gate)
                foreach (var key in _busyStreak.Keys.Where(k => !busyNow.Contains(k)).ToList()) _busyStreak.Remove(key);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmartCpu] Efficiency check failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _efficiencyBusy, 0);
        }
    }

    private static bool HasProAccess()
    {
        try
        {
            return TrialService.Instance.HasProAccess;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmartCpu] Pro check failed: {ex.Message}");
            return false;
        }
    }

    #endregion
}

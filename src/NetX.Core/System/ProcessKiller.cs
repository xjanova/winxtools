using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using NetX.Core.Helpers;

namespace NetX.Core.Optimization;

/// <summary>
/// Auto-kill system that prevents specified processes from running
/// and (opt-in) closes apps that stay frozen for a long time.
///
/// Safety rules for everything killed automatically:
///  * only the process itself is terminated — never its whole process tree —
///    and only the exact instance that was seen (PID + creation time), never a
///    program that happened to reuse its PID;
///  * only processes in the user's own Windows session: never services or
///    other users' apps;
///  * Windows/shell processes, WebView2, WinXTools itself and the app in the
///    foreground (the one the user is looking at) are never touched;
///  * a window must be "Not Responding" continuously for <see cref="FrozenKillThreshold"/>;
///  * every automatic kill raises <see cref="ProcessAutoKilled"/> so the UI can
///    tell the user what was closed and why.
/// Settings live in the admin-only store (<see cref="AdminOnlyStore"/>)
/// because the elevated app acts on them.
/// </summary>
public class ProcessKiller : IDisposable
{
    private static readonly Lazy<ProcessKiller> _instance = new(() => new ProcessKiller());
    public static ProcessKiller Instance => _instance.Value;

    /// <summary>A window must stay "Not Responding" this long before Smart Kill closes it.</summary>
    public static readonly TimeSpan FrozenKillThreshold = TimeSpan.FromSeconds(60);

    private const int WatchdogIntervalMs = 2000;
    private const int HealthCheckIntervalMs = 5000;
    private const int MaxRules = 200;
    private const int MaxRecentAutoKills = 20;
    private const string SettingsFileName = "process_killer.json";
    private static readonly TimeSpan RuleKillNoticeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan KillCountSaveInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, KillRule> _killRules = new();
    private readonly ConcurrentDictionary<int, ProcessHealthInfo> _processHealth = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastRuleKillNotice = new();
    private readonly List<ProcessAutoKilledEventArgs> _recentAutoKills = new();
    private readonly Timer _watchdogTimer;
    private readonly Timer _healthCheckTimer;
    private readonly object _settingsLock = new();
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly int _ownSessionId;
    private volatile bool _isAutoKillEnabled;
    private volatile bool _isSmartKillEnabled;
    private int _watchdogRunning;
    private int _healthCheckRunning;
    private DateTime _lastKillCountSaveUtc = DateTime.MinValue;

    // Known background bloat. Informational only (GetBloatwareRunning): nothing
    // is closed for being on this list. Smart Kill used to close these above
    // 2 GB without saying so — including updaters in the middle of an update.
    private static readonly HashSet<string> KnownBloatware = new(StringComparer.OrdinalIgnoreCase)
    {
        "yourphone", "gamebar", "gamebarpresencewriter", "cortana",
        "microsoftedgeupdate", "onedrivesetup", "skypeapp", "skypebridge",
        "peopleexperiencehost"
    };

    // Processes that should NEVER be killed
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "smss", "csrss", "wininit", "services", "lsass",
        "svchost", "dwm", "explorer", "winlogon", "taskmgr",
        "sihost", "fontdrvhost", "conhost", "ctfmon", "dllhost",
        "msiexec", "trustedinstaller", "tiworker", "wudfhost",
        "spoolsv", "lsm", "audiodg", "systemsettings", "registry",
        "idle", "memory compression", "secure system", "lsaiso",
        "msmpeng", "nissrv", "mpdefendercoreservice", "securityhealthservice", "sgrmbroker"
    };

    // On top of ProtectedProcesses: never closed AUTOMATICALLY (a user may still
    // end them by hand). Mostly shell hosts that own many windows at once.
    private static readonly HashSet<string> NeverAutoKill = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedgewebview2", "applicationframehost", "shellexperiencehost",
        "startmenuexperiencehost", "searchhost", "searchapp", "searchui",
        "textinputhost", "lockapp", "logonui", "consent", "mmc", "winxtools"
    };

    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr HungWindowFromGhostWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(IntPtr hwnd);

    private ProcessKiller()
    {
        using (var self = Process.GetCurrentProcess())
            _ownSessionId = self.SessionId;

        // Watchdog kills processes matching a rule; health check looks for frozen apps.
        _watchdogTimer = new Timer(WatchdogCallback, null, Timeout.Infinite, Timeout.Infinite);
        _healthCheckTimer = new Timer(HealthCheckCallback, null, Timeout.Infinite, Timeout.Infinite);

        LoadSettings();

        // Resume saved modes. Before, the toggles showed ON after a restart but
        // the timers were only started by the property setters.
        if (_isAutoKillEnabled)
            _watchdogTimer.Change(0, WatchdogIntervalMs);
        if (_isSmartKillEnabled)
            _healthCheckTimer.Change(HealthCheckIntervalMs, HealthCheckIntervalMs);
    }

    #region Auto-Kill Mode

    public bool IsAutoKillEnabled
    {
        get => _isAutoKillEnabled;
        set
        {
            _isAutoKillEnabled = value;
            _watchdogTimer.Change(value ? 0 : Timeout.Infinite, value ? WatchdogIntervalMs : Timeout.Infinite);
            SaveSettings();
        }
    }

    public bool IsSmartKillEnabled
    {
        get => _isSmartKillEnabled;
        set
        {
            _isSmartKillEnabled = value;
            if (!value)
                _processHealth.Clear(); // a later re-enable starts every 60 s window fresh
            _healthCheckTimer.Change(value ? HealthCheckIntervalMs : Timeout.Infinite,
                                     value ? HealthCheckIntervalMs : Timeout.Infinite);
            SaveSettings();
        }
    }

    /// <summary>
    /// Adds a rule and immediately ends running instances of that process (only
    /// the process itself, not its children). Returns false for names that are
    /// invalid or protected.
    /// </summary>
    public bool AddKillRule(string processName, string reason = "User requested")
    {
        var name = NormalizeRuleName(processName);
        if (name == null)
            return false;

        var rule = new KillRule
        {
            ProcessName = name,
            Reason = reason.Length > 200 ? reason[..200] : reason,
            CreatedAt = DateTime.Now,
            KillCount = 0
        };

        _killRules[name] = rule;
        SaveSettings();

        // Immediately kill if running
        KillProcess(name);
        return true;
    }

    public void RemoveKillRule(string processName)
    {
        _killRules.TryRemove(processName.ToLowerInvariant(), out _);
        _lastRuleKillNotice.TryRemove(processName.ToLowerInvariant(), out _);
        SaveSettings();
    }

    public List<KillRule> GetKillRules() => _killRules.Values.ToList();

    public bool IsProcessBlocked(string processName)
    {
        return _killRules.ContainsKey(processName.ToLowerInvariant());
    }

    private void WatchdogCallback(object? state)
    {
        if (!_isAutoKillEnabled || _killRules.IsEmpty) return;
        if (Interlocked.Exchange(ref _watchdogRunning, 1) == 1) return; // previous tick still running

        try
        {
            // One handle-free system call instead of Process.GetProcesses() every
            // 2 s (which built a Process object per process and per thread).
            var snapshot = MemoryNative.SnapshotProcesses();
            if (snapshot == null)
                return;

            bool killedAny = false;
            foreach (var process in snapshot)
            {
                try
                {
                    var name = process.Name.ToLowerInvariant();
                    if (process.Pid == _ownProcessId || process.Pid <= 4 || !_killRules.TryGetValue(name, out var rule))
                        continue;
                    // Rules are for the user's apps: never services or other users' sessions.
                    if (process.SessionId != _ownSessionId)
                        continue;
                    if (IsAutoKillExcluded(name))
                        continue; // defence in depth — such rules are rejected on add/load

                    if (MemoryNative.TerminateVerified(process.Pid, process.CreateTime, 0) != MemoryNative.NativeOutcome.Done)
                        continue; // exited meanwhile, PID reused or access denied

                    rule.KillCount++;
                    rule.LastKilled = DateTime.Now;
                    killedAny = true;

                    // A rule can fire every 2 s for an app that keeps respawning;
                    // tell the UI at most once a minute per process name.
                    var now = DateTime.UtcNow;
                    if (!_lastRuleKillNotice.TryGetValue(name, out var last) || now - last >= RuleKillNoticeInterval)
                    {
                        _lastRuleKillNotice[name] = now;
                        RaiseAutoKilled(process.Pid, process.Name, "", AutoKillReason.MatchedKillRule, 0);
                    }
                }
                catch { /* one process must not stop the pass */ }
            }

            // Keep the "×N" counters across restarts without writing on every kill.
            if (killedAny && DateTime.UtcNow - _lastKillCountSaveUtc >= KillCountSaveInterval)
            {
                _lastKillCountSaveUtc = DateTime.UtcNow;
                SaveSettings();
            }
        }
        catch { }
        finally
        {
            Volatile.Write(ref _watchdogRunning, 0);
        }
    }

    #endregion

    #region Smart Kill (Frozen/Problematic Processes)

    private void HealthCheckCallback(object? state)
    {
        if (!_isSmartKillEnabled) return;
        if (Interlocked.Exchange(ref _healthCheckRunning, 1) == 1) return; // previous tick still running

        try
        {
            var now = DateTime.UtcNow;
            var foregroundPid = GetForegroundProcessId();
            var windows = GetTopLevelWindowStates();
            var tracked = new HashSet<int>();

            var snapshot = MemoryNative.SnapshotProcesses();
            if (snapshot == null)
                return;

            foreach (var process in snapshot)
            {
                try
                {
                    var pid = process.Pid;
                    var name = process.Name;

                    // Never touch: WinXTools, the app the user is looking at,
                    // other users' sessions / services, system and shell processes.
                    if (pid == _ownProcessId || pid == foregroundPid || pid <= 4)
                        continue;
                    if (process.SessionId != _ownSessionId)
                        continue;
                    if (name.Length == 0 || IsAutoKillExcluded(name))
                        continue;

                    // Only a frozen window counts, hung continuously for FrozenKillThreshold.
                    if (!windows.TryGetValue(pid, out var window))
                        continue;

                    tracked.Add(pid);
                    var health = _processHealth.GetOrAdd(pid, _ => new ProcessHealthInfo
                    {
                        ProcessId = pid,
                        ProcessName = name,
                        CreateTime = process.CreateTime,
                        StartTime = DateTime.Now
                    });

                    if (health.CreateTime != process.CreateTime)
                    {
                        // PID was reused by a different process — start over.
                        health.ProcessName = name;
                        health.CreateTime = process.CreateTime;
                        health.StartTime = DateTime.Now;
                        health.NotRespondingSince = null;
                        health.NotRespondingCount = 0;
                    }

                    if (!window.IsFrozen)
                    {
                        health.NotRespondingSince = null;
                        health.NotRespondingCount = 0;
                        continue;
                    }

                    health.NotRespondingSince ??= now;
                    health.NotRespondingCount++;

                    var hungFor = now - health.NotRespondingSince.Value;
                    if (hungFor < FrozenKillThreshold)
                        continue;

                    if (MemoryNative.TerminateVerified(pid, process.CreateTime, 0) == MemoryNative.NativeOutcome.Done)
                    {
                        _processHealth.TryRemove(pid, out _);
                        RaiseAutoKilled(pid, name, window.Title, AutoKillReason.NotResponding, (long)hungFor.TotalSeconds);
                    }
                }
                catch { /* one process must not stop the pass */ }
            }

            // Forget processes that exited, lost their window, moved to the
            // foreground or are otherwise no longer watched: the 60 s window
            // must be continuous, so it starts over next time.
            foreach (var pid in _processHealth.Keys)
            {
                if (!tracked.Contains(pid))
                    _processHealth.TryRemove(pid, out _);
            }
        }
        catch { }
        finally
        {
            Volatile.Write(ref _healthCheckRunning, 0);
        }
    }

    /// <summary>
    /// PID owning the foreground window. A frozen window in front is swapped for
    /// a system "ghost" window, so map it back to the hung app it stands for.
    /// Also used by the RAM cleaner to leave the app in front alone.
    /// </summary>
    internal static int GetForegroundProcessId()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;

            var hung = HungWindowFromGhostWindow(hwnd);
            if (hung != IntPtr.Zero) hwnd = hung;

            GetWindowThreadProcessId(hwnd, out var pid);
            return (int)pid;
        }
        catch
        {
            return 0;
        }
    }

    private sealed class TopLevelWindowState
    {
        public bool AnyHung;
        public bool AnyResponsive;
        public string Title = "";

        /// <summary>Every visible top-level window of the process is hung.</summary>
        public bool IsFrozen => AnyHung && !AnyResponsive;
    }

    /// <summary>
    /// One EnumWindows pass: PID → state of its visible, unowned top-level
    /// windows. Process.MainWindowHandle can't be used for this: Windows hides a
    /// frozen window behind a system "ghost" copy, so the hung app would look
    /// window-less. Ghosts are mapped back to the hung window they stand for.
    /// IsHungAppWindow is what Windows itself uses for "(Not Responding)" and,
    /// unlike Process.Responding, never blocks.
    /// </summary>
    private static Dictionary<int, TopLevelWindowState> GetTopLevelWindowStates()
    {
        var states = new Dictionary<int, TopLevelWindowState>();

        EnumWindows((hwnd, _) =>
        {
            try
            {
                var target = HungWindowFromGhostWindow(hwnd);
                bool isGhost = target != IntPtr.Zero;
                if (!isGhost)
                {
                    if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
                        return true; // hidden, or a dialog/tool window that follows its owner
                    target = hwnd;
                }

                GetWindowThreadProcessId(target, out var pid);
                if (pid == 0)
                    return true;

                if (!states.TryGetValue((int)pid, out var state))
                    states[(int)pid] = state = new TopLevelWindowState();

                if (isGhost || IsHungAppWindow(target))
                {
                    state.AnyHung = true;
                    if (state.Title.Length == 0)
                    {
                        // For another process's window GetWindowText reads the stored
                        // caption without messaging it, so a hung app can't block us.
                        var title = new StringBuilder(256);
                        GetWindowText(target, title, title.Capacity);
                        state.Title = title.ToString();
                    }
                }
                else
                {
                    state.AnyResponsive = true;
                }
            }
            catch { }

            return true;
        }, IntPtr.Zero);

        return states;
    }

    public List<FrozenProcessInfo> GetFrozenProcesses()
    {
        var frozen = new List<FrozenProcessInfo>();

        try
        {
            var windows = GetTopLevelWindowStates();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (windows.TryGetValue(process.Id, out var window) && window.IsFrozen)
                        {
                            frozen.Add(new FrozenProcessInfo
                            {
                                ProcessId = process.Id,
                                ProcessName = process.ProcessName,
                                MainWindowTitle = window.Title,
                                MemoryMB = process.WorkingSet64 / (1024 * 1024)
                            });
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        return frozen;
    }

    public List<string> GetBloatwareRunning()
    {
        var running = new List<string>();

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (IsBloatware(process.ProcessName) && !running.Contains(process.ProcessName))
                        {
                            running.Add(process.ProcessName);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        return running;
    }

    #endregion

    #region Manual Kill

    /// <summary>
    /// Ends every running instance of <paramref name="processName"/> in the
    /// user's own Windows session — each process only, never its child processes,
    /// never services or other users' apps. Returns true if any was ended.
    /// </summary>
    public bool KillProcess(string processName)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        if (name.Length == 0 || IsProtectedProcess(name))
            return false;

        var snapshot = MemoryNative.SnapshotProcesses();
        if (snapshot == null)
            return false;

        var killed = false;
        foreach (var process in snapshot)
        {
            if (process.Pid == _ownProcessId || process.Pid <= 4 || process.SessionId != _ownSessionId)
                continue;
            if (!process.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (MemoryNative.TerminateVerified(process.Pid, process.CreateTime, 0) == MemoryNative.NativeOutcome.Done)
                killed = true;
        }

        return killed;
    }

    /// <summary>
    /// Ends one process the user picked from a list, but only if that exact
    /// instance is still running: a PID reused since the list was taken is left
    /// alone (reported as <see cref="ProcessEndResult.AlreadyGone"/>). Only that
    /// process is closed, never its children. Waits up to 3 s for it to exit.
    /// </summary>
    public ProcessEndResult EndProcess(int processId, string expectedName, long expectedCreateTime)
    {
        if (processId == _ownProcessId || processId <= 4 || IsProtectedProcess(expectedName))
            return ProcessEndResult.Protected;

        return MemoryNative.TerminateVerified(processId, expectedCreateTime, 3000) switch
        {
            MemoryNative.NativeOutcome.Done => ProcessEndResult.Ended,
            MemoryNative.NativeOutcome.Gone => ProcessEndResult.AlreadyGone,
            MemoryNative.NativeOutcome.AccessDenied => ProcessEndResult.AccessDenied,
            MemoryNative.NativeOutcome.StillRunning => ProcessEndResult.StillRunning,
            _ => ProcessEndResult.Failed
        };
    }

    /// <summary>
    /// Ends one process. Only that process is closed unless the caller
    /// explicitly asks for the whole tree. Returns true only once the process
    /// has actually exited (or was already gone).
    /// </summary>
    public bool KillProcess(int processId, bool killEntireTree = false)
    {
        if (processId == _ownProcessId)
            return false;

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true; // no such process any more — it is gone
        }
        catch
        {
            return false;
        }

        using (process)
        {
            try
            {
                if (IsProtectedProcess(process.ProcessName))
                    return false;

                process.Kill(killEntireTree);
                return process.WaitForExit(3000);
            }
            catch (InvalidOperationException)
            {
                return true; // it exited before we could kill it
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Last resort via taskkill /F. Returns the real outcome: true only if the
    /// process is no longer running afterwards.
    /// </summary>
    public bool ForceKillProcess(int processId, bool killEntireTree = false)
    {
        if (processId == _ownProcessId)
            return false;

        Process target;
        try
        {
            target = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch
        {
            return false;
        }

        using (target)
        {
            try
            {
                if (IsProtectedProcess(target.ProcessName))
                    return false;
            }
            catch { }

            try
            {
                // Full System32 path: an elevated app must not resolve "taskkill"
                // through the working directory or PATH.
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                    Arguments = killEntireTree ? $"/F /T /PID {processId}" : $"/F /PID {processId}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var taskkill = Process.Start(psi);
                if (taskkill != null && !taskkill.WaitForExit(5000))
                {
                    try { taskkill.Kill(); } catch { }
                }
            }
            catch { }

            try
            {
                return target.WaitForExit(2000);
            }
            catch
            {
                return false;
            }
        }
    }

    #endregion

    #region Helpers

    public static bool IsProtectedProcess(string processName)
    {
        return ProtectedProcesses.Contains(processName.ToLowerInvariant());
    }

    /// <summary>True for processes that are never closed automatically (protected or shell hosts).</summary>
    public static bool IsAutoKillExcluded(string processName)
    {
        return IsProtectedProcess(processName) || NeverAutoKill.Contains(processName);
    }

    public static bool IsBloatware(string processName)
    {
        return KnownBloatware.Contains(processName);
    }

    /// <summary>
    /// Returns the rule key for a user-entered name (lower case, no ".exe"),
    /// or null when it is not a plain process name or must never be killed.
    /// </summary>
    public static string? NormalizeRuleName(string? processName)
    {
        var name = processName?.Trim() ?? "";
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        if (name.Length == 0 || name.Length > 100)
            return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        name = name.ToLowerInvariant();
        return IsAutoKillExcluded(name) ? null : name;
    }

    private void RaiseAutoKilled(int pid, string name, string windowTitle, AutoKillReason reason, long detail)
    {
        var args = new ProcessAutoKilledEventArgs
        {
            ProcessId = pid,
            ProcessName = name,
            WindowTitle = windowTitle,
            Reason = reason,
            Detail = detail,
            Time = DateTime.Now
        };

        Debug.WriteLine($"[ProcessKiller] Auto-killed {name} ({pid}): {args.ReasonText}");

        lock (_recentAutoKills)
        {
            _recentAutoKills.Insert(0, args);
            if (_recentAutoKills.Count > MaxRecentAutoKills)
                _recentAutoKills.RemoveAt(_recentAutoKills.Count - 1);
        }

        try { ProcessAutoKilled?.Invoke(this, args); } catch { /* a UI handler must not stop the watchdog */ }
        try { OnProcessAutoKilled?.Invoke(name, args.ReasonText); } catch { }
    }

    /// <summary>Apps closed automatically during this app session, newest first (for pages opened later).</summary>
    public IReadOnlyList<ProcessAutoKilledEventArgs> GetRecentAutoKills()
    {
        lock (_recentAutoKills)
            return _recentAutoKills.ToArray();
    }

    /// <summary>
    /// Raised (on a background thread) after every automatic kill with what was
    /// closed and why. Marshal to the UI thread before touching controls.
    /// </summary>
    public event EventHandler<ProcessAutoKilledEventArgs>? ProcessAutoKilled;

    /// <summary>Legacy form of <see cref="ProcessAutoKilled"/>: (process name, English reason).</summary>
    public event Action<string, string>? OnProcessAutoKilled;

    #endregion

    #region Settings

    private void LoadSettings()
    {
        var settings = AdminOnlyStore.Load<KillSettings>(SettingsFileName);
        var fromLegacyFile = false;

        if (settings == null)
        {
            // Present but untrusted/corrupt: start clean rather than guess.
            if (AdminOnlyStore.Exists(SettingsFileName))
                return;

            settings = LoadLegacySettings();
            fromLegacyFile = settings != null;
        }

        if (settings == null)
            return;

        foreach (var saved in (settings.Rules ?? new List<KillRule>()).Take(MaxRules))
        {
            var name = NormalizeRuleName(saved?.ProcessName);
            if (name == null || saved == null)
                continue; // whitelist: plain, non-protected process names only

            _killRules[name] = new KillRule
            {
                ProcessName = name,
                Reason = (saved.Reason ?? "").Length > 200 ? saved.Reason![..200] : saved.Reason ?? "",
                CreatedAt = saved.CreatedAt,
                LastKilled = saved.LastKilled,
                KillCount = Math.Max(0, saved.KillCount)
            };
        }

        // The old file lived in a user-writable folder. Smart Kill only ever closes
        // hung windows under the rules above, so its switch is carried over; the
        // Auto-Kill switch is NOT — with rules anyone could have planted it would
        // make the elevated app kill arbitrary programs. It starts OFF and turning
        // it on shows the rule list. From now on only the admin-only copy is used.
        _isSmartKillEnabled = settings.SmartKillEnabled;
        if (fromLegacyFile)
            SaveSettings();
        else
            _isAutoKillEnabled = settings.AutoKillEnabled;
    }

    private static KillSettings? LoadLegacySettings()
    {
        try
        {
            var legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetX", "kill_rules.json");
            if (!File.Exists(legacyPath) || new FileInfo(legacyPath).Length > 1024 * 1024)
                return null;

            return JsonSerializer.Deserialize<KillSettings>(File.ReadAllText(legacyPath));
        }
        catch
        {
            return null;
        }
    }

    private void SaveSettings()
    {
        lock (_settingsLock)
        {
            var settings = new KillSettings
            {
                AutoKillEnabled = _isAutoKillEnabled,
                SmartKillEnabled = _isSmartKillEnabled,
                Rules = _killRules.Values.ToList()
            };

            if (!AdminOnlyStore.Save(SettingsFileName, settings))
                Debug.WriteLine("[ProcessKiller] Settings not persisted (admin-only store unavailable).");
        }
    }

    #endregion

    public void Dispose()
    {
        _watchdogTimer.Dispose();
        _healthCheckTimer.Dispose();
    }
}

public enum AutoKillReason
{
    /// <summary>Matched a user kill rule while Auto-Kill was on.</summary>
    MatchedKillRule,
    /// <summary>Window was "Not Responding" for at least the Smart Kill threshold.</summary>
    NotResponding,
    /// <summary>No longer raised (kept so existing handlers still compile): bloatware is never closed for its memory use.</summary>
    ExcessiveMemory
}

/// <summary>Outcome of <see cref="ProcessKiller.EndProcess"/>.</summary>
public enum ProcessEndResult
{
    Ended,
    /// <summary>It had exited, or its PID now belongs to a different process (which was left alone).</summary>
    AlreadyGone,
    /// <summary>Windows core process or WinXTools itself — not ended.</summary>
    Protected,
    AccessDenied,
    /// <summary>Terminate was accepted but the process hadn't exited after 3 s.</summary>
    StillRunning,
    Failed
}

public sealed class ProcessAutoKilledEventArgs : EventArgs
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string WindowTitle { get; init; } = "";
    public AutoKillReason Reason { get; init; }

    /// <summary>Seconds not responding (NotResponding) or working set in MB (ExcessiveMemory).</summary>
    public long Detail { get; init; }

    public DateTime Time { get; init; }

    /// <summary>English reason for logs; the UI should localize from <see cref="Reason"/>.</summary>
    public string ReasonText => Reason switch
    {
        AutoKillReason.NotResponding => $"Not responding for {Detail} s",
        AutoKillReason.ExcessiveMemory => $"Excessive memory ({Detail} MB)",
        _ => "Matched auto-kill rule"
    };
}

public class KillRule
{
    public string ProcessName { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastKilled { get; set; }
    public int KillCount { get; set; }
}

public class ProcessHealthInfo
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";

    /// <summary>Creation time (FILETIME) of the watched instance; a different value means the PID was reused.</summary>
    public long CreateTime { get; set; }

    public DateTime StartTime { get; set; }
    public int NotRespondingCount { get; set; }

    /// <summary>UTC time the window was first seen hung in the current unbroken streak.</summary>
    public DateTime? NotRespondingSince { get; set; }
}

public class FrozenProcessInfo
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string MainWindowTitle { get; set; } = "";
    public long MemoryMB { get; set; }
}

public class KillSettings
{
    public bool AutoKillEnabled { get; set; }
    public bool SmartKillEnabled { get; set; }
    public List<KillRule> Rules { get; set; } = new();
}

using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;

namespace NetX.Core.Optimization;

/// <summary>
/// Auto-kill system that prevents specified processes from running
/// and automatically kills frozen/problematic processes
/// </summary>
public class ProcessKiller : IDisposable
{
    private static ProcessKiller? _instance;
    public static ProcessKiller Instance => _instance ??= new ProcessKiller();

    private readonly ConcurrentDictionary<string, KillRule> _killRules = new();
    private readonly ConcurrentDictionary<int, ProcessHealthInfo> _processHealth = new();
    private readonly Timer _watchdogTimer;
    private readonly Timer _healthCheckTimer;
    private bool _isAutoKillEnabled = false;
    private bool _isSmartKillEnabled = false;
    private readonly string _settingsPath;

    // Known problematic/unnecessary processes that are safe to kill
    private static readonly HashSet<string> KnownBloatware = new(StringComparer.OrdinalIgnoreCase)
    {
        "yourphone", "gamebar", "gamebarpresencewriter", "gameoverlay",
        "cortana", "searchapp", "searchhost", "startmenuexperiencehost",
        "textinputhost", "lockapp", "shellexperiencehost",
        "msedgewebview2", "microsoftedgeupdate", "onedrivesetup",
        "skypeapp", "skypebridge", "peopleexperiencehost"
    };

    // Processes that should NEVER be killed
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "smss", "csrss", "wininit", "services", "lsass",
        "svchost", "dwm", "explorer", "winlogon", "taskmgr",
        "sihost", "fontdrvhost", "conhost", "ctfmon", "dllhost",
        "msiexec", "trustedinstaller", "tiworker", "wudfhost",
        "spoolsv", "lsm", "audiodg", "systemsettings", "registry"
    };

    private ProcessKiller()
    {
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetX", "kill_rules.json");

        LoadSettings();

        // Watchdog timer - checks for processes to auto-kill every 2 seconds
        _watchdogTimer = new Timer(WatchdogCallback, null, Timeout.Infinite, 2000);

        // Health check timer - monitors process health every 5 seconds
        _healthCheckTimer = new Timer(HealthCheckCallback, null, Timeout.Infinite, 5000);
    }

    #region Auto-Kill Mode

    public bool IsAutoKillEnabled
    {
        get => _isAutoKillEnabled;
        set
        {
            _isAutoKillEnabled = value;
            if (value)
                _watchdogTimer.Change(0, 2000);
            else
                _watchdogTimer.Change(Timeout.Infinite, 2000);
            SaveSettings();
        }
    }

    public bool IsSmartKillEnabled
    {
        get => _isSmartKillEnabled;
        set
        {
            _isSmartKillEnabled = value;
            if (value)
                _healthCheckTimer.Change(0, 5000);
            else
                _healthCheckTimer.Change(Timeout.Infinite, 5000);
            SaveSettings();
        }
    }

    public void AddKillRule(string processName, string reason = "User requested")
    {
        if (IsProtectedProcess(processName))
            return;

        var rule = new KillRule
        {
            ProcessName = processName.ToLowerInvariant(),
            Reason = reason,
            CreatedAt = DateTime.Now,
            KillCount = 0
        };

        _killRules[processName.ToLowerInvariant()] = rule;
        SaveSettings();

        // Immediately kill if running
        KillProcess(processName);
    }

    public void RemoveKillRule(string processName)
    {
        _killRules.TryRemove(processName.ToLowerInvariant(), out _);
        SaveSettings();
    }

    public List<KillRule> GetKillRules() => _killRules.Values.ToList();

    public bool IsProcessBlocked(string processName)
    {
        return _killRules.ContainsKey(processName.ToLowerInvariant());
    }

    private void WatchdogCallback(object? state)
    {
        if (!_isAutoKillEnabled) return;

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName.ToLowerInvariant();

                    if (_killRules.TryGetValue(name, out var rule))
                    {
                        process.Kill(true);
                        rule.KillCount++;
                        rule.LastKilled = DateTime.Now;
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    #endregion

    #region Smart Kill (Frozen/Problematic Processes)

    private void HealthCheckCallback(object? state)
    {
        if (!_isSmartKillEnabled) return;

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (IsProtectedProcess(process.ProcessName))
                        continue;

                    var pid = process.Id;

                    if (!_processHealth.TryGetValue(pid, out var health))
                    {
                        health = new ProcessHealthInfo
                        {
                            ProcessId = pid,
                            ProcessName = process.ProcessName,
                            StartTime = DateTime.Now
                        };
                        _processHealth[pid] = health;
                    }

                    // Check for frozen process (not responding)
                    if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                    {
                        if (!process.Responding)
                        {
                            health.NotRespondingCount++;

                            // Kill if not responding for 3 consecutive checks (15 seconds)
                            if (health.NotRespondingCount >= 3)
                            {
                                process.Kill(true);
                                _processHealth.TryRemove(pid, out _);
                                OnProcessAutoKilled?.Invoke(process.ProcessName, "Not responding (frozen)");
                            }
                        }
                        else
                        {
                            health.NotRespondingCount = 0;
                        }
                    }

                    // Check for excessive memory usage (> 2GB for non-system process)
                    if (!process.HasExited)
                    {
                        var memoryMB = process.WorkingSet64 / (1024 * 1024);
                        if (memoryMB > 2048 && IsBloatware(process.ProcessName))
                        {
                            process.Kill(true);
                            _processHealth.TryRemove(pid, out _);
                            OnProcessAutoKilled?.Invoke(process.ProcessName, $"Excessive memory ({memoryMB} MB)");
                        }
                    }
                }
                catch { }
            }

            // Cleanup dead process entries
            var deadPids = _processHealth.Keys.Where(pid =>
            {
                try { Process.GetProcessById(pid); return false; }
                catch { return true; }
            }).ToList();

            foreach (var pid in deadPids)
                _processHealth.TryRemove(pid, out _);
        }
        catch { }
    }

    public List<FrozenProcessInfo> GetFrozenProcesses()
    {
        var frozen = new List<FrozenProcessInfo>();

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero && !process.Responding)
                    {
                        frozen.Add(new FrozenProcessInfo
                        {
                            ProcessId = process.Id,
                            ProcessName = process.ProcessName,
                            MainWindowTitle = process.MainWindowTitle,
                            MemoryMB = process.WorkingSet64 / (1024 * 1024)
                        });
                    }
                }
                catch { }
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
        catch { }

        return running;
    }

    #endregion

    #region Manual Kill

    public bool KillProcess(string processName)
    {
        if (IsProtectedProcess(processName))
            return false;

        var killed = false;
        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill(true);
                    killed = true;
                }
                catch { }
            }
        }
        catch { }

        return killed;
    }

    public bool KillProcess(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            if (IsProtectedProcess(process.ProcessName))
                return false;

            process.Kill(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool ForceKillProcess(int processId)
    {
        try
        {
            // Use taskkill /F /PID for more forceful termination
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/F /PID {processId}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi)?.WaitForExit(5000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Helpers

    public static bool IsProtectedProcess(string processName)
    {
        return ProtectedProcesses.Contains(processName.ToLowerInvariant());
    }

    public static bool IsBloatware(string processName)
    {
        var name = processName.ToLowerInvariant();
        return KnownBloatware.Any(b => name.Contains(b));
    }

    public event Action<string, string>? OnProcessAutoKilled;

    #endregion

    #region Settings

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<KillSettings>(json);
                if (settings != null)
                {
                    _isAutoKillEnabled = settings.AutoKillEnabled;
                    _isSmartKillEnabled = settings.SmartKillEnabled;

                    foreach (var rule in settings.Rules)
                    {
                        _killRules[rule.ProcessName.ToLowerInvariant()] = rule;
                    }
                }
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var settings = new KillSettings
            {
                AutoKillEnabled = _isAutoKillEnabled,
                SmartKillEnabled = _isSmartKillEnabled,
                Rules = _killRules.Values.ToList()
            };

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    #endregion

    public void Dispose()
    {
        _watchdogTimer.Dispose();
        _healthCheckTimer.Dispose();
    }
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
    public DateTime StartTime { get; set; }
    public int NotRespondingCount { get; set; }
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

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NetX.Core.Helpers;

namespace NetX.Core.Network;

/// <summary>
/// Per-app speed limit / block. Speeds are bytes per second,
/// <see cref="RateLimit.Unlimited"/> or <see cref="RateLimit.Blocked"/>.
/// </summary>
public sealed class AppBandwidthRule
{
    /// <summary>Process name as shown to the user, without ".exe".</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>
    /// Executable path, learned while the app was running. Lets the firewall
    /// block keep working while the app (or WinXTools) is closed.
    /// </summary>
    public string? ExePath { get; set; }

    public long DownloadBps { get; set; } = RateLimit.Unlimited;
    public long UploadBps { get; set; } = RateLimit.Unlimited;

    /// <summary>No internet at all: Windows Firewall rule + live traffic dropped.</summary>
    public bool Blocked { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore] public string Key => PacketEngine.NormalizeAppKey(ProcessName);
    [JsonIgnore] public bool HasSpeedLimit => DownloadBps >= 0 || UploadBps >= 0;

    public AppBandwidthRule Clone() => (AppBandwidthRule)MemberwiseClone();
}

/// <summary>Limit for all traffic of this PC together.</summary>
public sealed class GlobalBandwidthRule
{
    public long DownloadBps { get; set; } = RateLimit.Unlimited;
    public long UploadBps { get; set; } = RateLimit.Unlimited;

    [JsonIgnore] public bool IsActive => DownloadBps >= 0 || UploadBps >= 0;

    public GlobalBandwidthRule Clone() => (GlobalBandwidthRule)MemberwiseClone();
}

public enum LimiterStatus
{
    /// <summary>No limits set — nothing to enforce.</summary>
    Idle,
    /// <summary>Limits are being enforced by the kernel packet engine.</summary>
    Active,
    /// <summary>Limits are saved but the WinDivert driver could not be loaded.</summary>
    DriverUnavailable,
    /// <summary>The driver loaded but diverting failed (see <see cref="BandwidthLimiter.EngineError"/>).</summary>
    Error
}

public sealed class LimitResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }

    public static LimitResult Ok(string? message = null) => new() { Success = true, Message = message };
    public static LimitResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>
/// Owns every bandwidth rule: persists them, pushes speed limits into the
/// kernel <see cref="PacketEngine"/>, and keeps Windows Firewall rules for
/// blocked apps.
///
/// Speed limits work while WinXTools is running. App blocks also create a
/// Windows Firewall rule, so they keep working after WinXTools is closed until
/// the user unblocks the app.
/// </summary>
public sealed class BandwidthLimiter
{
    private static readonly Lazy<BandwidthLimiter> _instance = new(() => new BandwidthLimiter());
    public static BandwidthLimiter Instance => _instance.Value;

    private const string FirewallPrefix = "WinXTools_Block_";

    private readonly object _lock = new();
    private readonly Dictionary<string, AppBandwidthRule> _apps = new(StringComparer.Ordinal);
    private GlobalBandwidthRule? _global;
    private readonly HashSet<string> _engineKeys = new(StringComparer.Ordinal);
    private const string RulesFileName = "bandwidth_rules_v2.json";
    private readonly string _legacyPath;

    // Cutting these off breaks Windows itself (DNS, updates, antivirus); a
    // tampered or mistaken rule must never do that.
    private static readonly HashSet<string> NeverBlock = new(StringComparer.Ordinal)
    {
        "system", "svchost", "lsass", "services", "wininit", "winlogon", "csrss", "smss",
        "msmpeng", "nissrv", "mpdefendercoreservice", "securityhealthservice", "winxtools"
    };

    public static bool CanBlock(string processName) => !NeverBlock.Contains(PacketEngine.NormalizeAppKey(processName));
    private readonly List<string> _legacyFirewallNames = new();
    private readonly SemaphoreSlim _firewallGate = new(1, 1);
    private Task? _initTask;

    /// <summary>Raised (on a background thread) whenever rules change.</summary>
    public event Action? RulesChanged;

    private BandwidthLimiter()
    {
        // Rules make the elevated app create firewall rules, so they live in the
        // admin-only store (AdminOnlyStore), not in the user-writable profile.
        _legacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetX", "bandwidth_rules.json");
        Load();
    }

    #region Queries

    public IReadOnlyList<AppBandwidthRule> GetAppRules()
    {
        lock (_lock) return _apps.Values.Select(r => r.Clone()).OrderBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public AppBandwidthRule? GetAppRule(string processName)
    {
        lock (_lock) return _apps.TryGetValue(PacketEngine.NormalizeAppKey(processName), out var rule) ? rule.Clone() : null;
    }

    public GlobalBandwidthRule? GlobalRule
    {
        get { lock (_lock) return _global?.Clone(); }
    }

    /// <summary>True if any speed limit or block needs the packet engine.</summary>
    public bool HasEngineWork
    {
        get
        {
            lock (_lock) return _global?.IsActive == true || _apps.Values.Any(r => r.Blocked || r.HasSpeedLimit);
        }
    }

    public LimiterStatus Status
    {
        get
        {
            if (!HasEngineWork) return LimiterStatus.Idle;
            var engine = PacketEngine.Instance;
            if (!engine.IsDriverLoaded) return LimiterStatus.DriverUnavailable;
            return engine.IsShaping ? LimiterStatus.Active : LimiterStatus.Error;
        }
    }

    public string? EngineError => PacketEngine.Instance.LastError;

    #endregion

    #region Startup / shutdown

    /// <summary>
    /// Re-applies saved rules. Safe to call more than once; the work runs once.
    /// </summary>
    public Task InitializeAsync() => _initTask ??= Task.Run(async () =>
    {
        await CleanupLegacyAsync();
        SyncEngine();

        List<AppBandwidthRule> blocked;
        lock (_lock) blocked = _apps.Values.Where(r => r.Blocked).Select(r => r.Clone()).ToList();
        foreach (var rule in blocked)
        {
            var path = rule.ExePath ?? PacketEngine.Instance.FindExecutablePath(rule.ProcessName);
            if (path != null) await AddFirewallBlockAsync(rule.Key, path);
        }

        RulesChanged?.Invoke();
    });

    /// <summary>
    /// Called on exit: sends packets still waiting in the pacer and releases the
    /// driver. Firewall blocks stay in place on purpose.
    /// </summary>
    public void Shutdown()
    {
        try { PacketEngine.Instance.Stop(); } catch { }
    }

    #endregion

    #region Per-app rules

    /// <summary>
    /// Sets download/upload speed limits for an app (all of its processes
    /// share the limit). Pass <see cref="RateLimit.Unlimited"/> for no limit.
    /// </summary>
    public Task<LimitResult> SetAppLimitAsync(string processName, long downloadBps, long uploadBps) => Task.Run(() =>
    {
        var key = PacketEngine.NormalizeAppKey(processName);
        if (key.Length == 0) return LimitResult.Fail("No app selected.");
        if ((downloadBps == RateLimit.Blocked || uploadBps == RateLimit.Blocked) && !CanBlock(key))
            return LimitResult.Fail($"{DisplayName(processName)} is part of Windows and can't be blocked (it would break DNS, updates or antivirus).");

        lock (_lock)
        {
            if (!_apps.TryGetValue(key, out var rule))
                rule = new AppBandwidthRule { ProcessName = DisplayName(processName) };

            rule.DownloadBps = NormalizeRate(downloadBps);
            rule.UploadBps = NormalizeRate(uploadBps);
            rule.ExePath ??= PacketEngine.Instance.FindExecutablePath(processName);

            if (rule.HasSpeedLimit || rule.Blocked) _apps[key] = rule;
            else _apps.Remove(key);
            Save();
        }

        var result = SyncEngine();
        RulesChanged?.Invoke();
        return result;
    });

    /// <summary>
    /// Cuts an app off the internet: live connections are dropped by the packet
    /// engine and a Windows Firewall rule stops new ones (even after restart).
    /// </summary>
    public async Task<LimitResult> BlockAppAsync(string processName, string? exePath = null)
    {
        var key = PacketEngine.NormalizeAppKey(processName);
        if (key.Length == 0) return LimitResult.Fail("No app selected.");
        if (!CanBlock(key))
            return LimitResult.Fail($"{DisplayName(processName)} is part of Windows and can't be blocked (it would break DNS, updates or antivirus).");

        string? path;
        lock (_lock)
        {
            if (!_apps.TryGetValue(key, out var rule))
                rule = new AppBandwidthRule { ProcessName = DisplayName(processName) };

            rule.Blocked = true;
            rule.ExePath = exePath ?? rule.ExePath ?? PacketEngine.Instance.FindExecutablePath(processName);
            path = rule.ExePath;
            _apps[key] = rule;
            Save();
        }

        var engineResult = await Task.Run(SyncEngine);
        bool firewall = path != null && await AddFirewallBlockAsync(key, path);
        RulesChanged?.Invoke();

        if (firewall) return LimitResult.Ok();
        if (engineResult.Success)
            return LimitResult.Ok(path == null
                ? "Live traffic is blocked. Start the app once while WinXTools is open so it can also add a permanent firewall rule."
                : "Live traffic is blocked, but the Windows Firewall rule could not be created.");
        return LimitResult.Fail(engineResult.Message ?? "Could not block this app.");
    }

    public async Task<LimitResult> UnblockAppAsync(string processName)
    {
        var key = PacketEngine.NormalizeAppKey(processName);
        lock (_lock)
        {
            if (_apps.TryGetValue(key, out var rule))
            {
                rule.Blocked = false;
                if (!rule.HasSpeedLimit) _apps.Remove(key);
                Save();
            }
        }

        var engineResult = await Task.Run(SyncEngine);
        await RemoveFirewallBlockAsync(key);
        RulesChanged?.Invoke();
        return engineResult.Success || !HasEngineWork ? LimitResult.Ok() : engineResult;
    }

    /// <summary>Removes every limit and block for an app.</summary>
    public async Task RemoveAppRuleAsync(string processName)
    {
        var key = PacketEngine.NormalizeAppKey(processName);
        bool wasBlocked;
        lock (_lock)
        {
            wasBlocked = _apps.TryGetValue(key, out var rule) && rule.Blocked;
            _apps.Remove(key);
            Save();
        }

        await Task.Run(SyncEngine);
        if (wasBlocked) await RemoveFirewallBlockAsync(key);
        RulesChanged?.Invoke();
    }

    #endregion

    #region Whole-PC limit

    public Task<LimitResult> SetGlobalLimitAsync(long downloadBps, long uploadBps) => Task.Run(() =>
    {
        lock (_lock)
        {
            var rule = new GlobalBandwidthRule
            {
                DownloadBps = NormalizeRate(downloadBps),
                UploadBps = NormalizeRate(uploadBps)
            };
            _global = rule.IsActive ? rule : null;
            Save();
        }

        var result = SyncEngine();
        RulesChanged?.Invoke();
        return result;
    });

    public Task<LimitResult> RemoveGlobalLimitAsync() => SetGlobalLimitAsync(RateLimit.Unlimited, RateLimit.Unlimited);

    /// <summary>Removes every app rule, block and the whole-PC limit.</summary>
    public async Task ResetAllAsync()
    {
        List<string> blockedKeys;
        lock (_lock)
        {
            blockedKeys = _apps.Values.Where(r => r.Blocked).Select(r => r.Key).ToList();
            _apps.Clear();
            _global = null;
            Save();
        }

        await Task.Run(SyncEngine);
        foreach (var key in blockedKeys) await RemoveFirewallBlockAsync(key);
        RulesChanged?.Invoke();
    }

    #endregion

    #region Engine sync

    /// <summary>
    /// Makes the packet engine's shapers match the saved rules and starts the
    /// engine if anything needs enforcing.
    /// </summary>
    private LimitResult SyncEngine()
    {
        var engine = PacketEngine.Instance;
        List<AppBandwidthRule> apps;
        GlobalBandwidthRule? global;
        lock (_lock)
        {
            apps = _apps.Values.Select(r => r.Clone()).ToList();
            global = _global?.Clone();
        }

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in apps)
        {
            if (rule.Blocked)
            {
                engine.SetAppLimit(rule.Key, RateLimit.Blocked, RateLimit.Blocked);
                wanted.Add(rule.Key);
            }
            else if (rule.HasSpeedLimit)
            {
                engine.SetAppLimit(rule.Key, rule.DownloadBps, rule.UploadBps);
                wanted.Add(rule.Key);
            }
        }

        lock (_engineKeys)
        {
            foreach (var stale in _engineKeys.Where(k => !wanted.Contains(k)).ToList())
                engine.RemoveAppLimit(stale);
            _engineKeys.Clear();
            _engineKeys.UnionWith(wanted);
        }

        if (global?.IsActive == true) engine.SetGlobalLimit(global.DownloadBps, global.UploadBps);
        else engine.RemoveGlobalLimit();

        if (!engine.HasLimits) return LimitResult.Ok();

        if (!engine.IsRunning && !engine.Start())
            return LimitResult.Fail(engine.IsDriverLoaded
                ? $"The packet engine could not start: {engine.LastError}"
                : "The WinDivert driver could not be loaded, so speed limits are saved but not active. Run WinXTools as Administrator and allow the driver in your antivirus.");

        return engine.IsShaping
            ? LimitResult.Ok()
            : LimitResult.Fail($"Limits are saved but not active: {engine.LastError ?? "packet diverting failed"}");
    }

    #endregion

    #region Windows Firewall (app blocks)

    private static string FirewallBaseName(string key)
    {
        var safe = Regex.Replace(key, @"[^a-z0-9._\-]", "_");
        if (safe == key) return FirewallPrefix + safe;

        // Keep names unique when non-ASCII characters were replaced.
        uint hash = 2166136261;
        foreach (var c in key) hash = (hash ^ c) * 16777619;
        return $"{FirewallPrefix}{safe}_{hash:x8}";
    }

    private async Task<bool> AddFirewallBlockAsync(string key, string exePath)
    {
        if (!File.Exists(exePath)) return false;

        var name = FirewallBaseName(key);
        await _firewallGate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                // Replace instead of duplicating rules on every start.
                RunNetsh($"advfirewall firewall delete rule name=\"{name}_Out\"");
                RunNetsh($"advfirewall firewall delete rule name=\"{name}_In\"");

                const string description = "Created by WinXTools. Unblock the app in WinXTools to remove this rule.";
                bool outOk = RunNetsh($"advfirewall firewall add rule name=\"{name}_Out\" dir=out action=block program=\"{exePath}\" enable=yes profile=any description=\"{description}\"");
                bool inOk = RunNetsh($"advfirewall firewall add rule name=\"{name}_In\" dir=in action=block program=\"{exePath}\" enable=yes profile=any description=\"{description}\"");
                return outOk && inOk;
            });
        }
        finally
        {
            _firewallGate.Release();
        }
    }

    private async Task RemoveFirewallBlockAsync(string key)
    {
        var name = FirewallBaseName(key);
        await _firewallGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                RunNetsh($"advfirewall firewall delete rule name=\"{name}_Out\"");
                RunNetsh($"advfirewall firewall delete rule name=\"{name}_In\"");
            });
        }
        finally
        {
            _firewallGate.Release();
        }
    }

    private static bool RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process == null) return false;
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10000))
            {
                try { process.Kill(); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"netsh failed: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Persistence

    private sealed class RulesFile
    {
        public int Version { get; set; } = 2;
        public List<AppBandwidthRule> Apps { get; set; } = new();
        public GlobalBandwidthRule? Global { get; set; }
    }

    /// <summary>Rule format written by versions before 2026-09.</summary>
    private sealed class LegacyRule
    {
        public string ProcessName { get; set; } = "";
        public long DownloadLimitKBps { get; set; }
        public long UploadLimitKBps { get; set; }
    }

    private void Load()
    {
        try
        {
            if (AdminOnlyStore.Exists(RulesFileName))
            {
                var file = AdminOnlyStore.Load<RulesFile>(RulesFileName);
                if (file != null)
                {
                    foreach (var rule in file.Apps.Where(r => !string.IsNullOrWhiteSpace(r.ProcessName)))
                    {
                        // Defense in depth: even a trusted file can't block Windows itself.
                        if (!CanBlock(rule.Key))
                        {
                            rule.Blocked = false;
                            if (rule.DownloadBps == RateLimit.Blocked) rule.DownloadBps = RateLimit.Unlimited;
                            if (rule.UploadBps == RateLimit.Blocked) rule.UploadBps = RateLimit.Unlimited;
                        }
                        if (rule.Blocked || rule.HasSpeedLimit) _apps[rule.Key] = rule;
                    }
                    _global = file.Global?.IsActive == true ? file.Global : null;
                }
                return;
            }

            if (File.Exists(_legacyPath)) MigrateLegacy();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading bandwidth rules: {ex.Message}");
        }
    }

    /// <summary>
    /// Old files mixed units: per-app values were what the UI showed as KB/s,
    /// the whole-PC value was Kbps (−1 unlimited, 0 blocked). The old file sits
    /// in the user-writable profile, so only speed limits are imported — never
    /// app blocks (the old UI could not create them anyway).
    /// </summary>
    private void MigrateLegacy()
    {
        var legacy = JsonSerializer.Deserialize<List<LegacyRule>>(File.ReadAllText(_legacyPath)) ?? new();

        foreach (var old in legacy)
        {
            if (old.ProcessName.StartsWith("interface:", StringComparison.OrdinalIgnoreCase))
            {
                static long FromKbps(long v) => v < 0 ? RateLimit.Unlimited : v == 0 ? RateLimit.Blocked : v * 125;
                var global = new GlobalBandwidthRule
                {
                    DownloadBps = FromKbps(old.DownloadLimitKBps),
                    UploadBps = FromKbps(old.UploadLimitKBps)
                };
                if (global.IsActive) _global = global;
                continue;
            }

            var name = DisplayName(old.ProcessName);
            if (name.Length == 0) continue;
            _legacyFirewallNames.Add($"NetX_Block_{name}");
            _legacyFirewallNames.Add($"NetX_Block_{name}_In");

            static long FromKBps(long v) => v <= 0 ? RateLimit.Unlimited : v * 1024;
            var rule = new AppBandwidthRule
            {
                ProcessName = name,
                DownloadBps = FromKBps(old.DownloadLimitKBps),
                UploadBps = FromKBps(old.UploadLimitKBps)
            };
            if (rule.HasSpeedLimit) _apps[rule.Key] = rule;
        }

        Save();
    }

    /// <summary>
    /// Removes firewall/QoS objects the old limiter created. Old versions also
    /// wiped every "NetX_*" rule (including the user's IP blocks) on each start
    /// and exit; that no longer happens.
    /// </summary>
    private async Task CleanupLegacyAsync()
    {
        if (!File.Exists(_legacyPath)) return;

        await _firewallGate.WaitAsync();
        try
        {
            foreach (var name in _legacyFirewallNames)
                RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");

            RunPowerShell(
                "Get-NetFirewallRule -DisplayName 'NetX_IBlock_*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue; " +
                "Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'NetX_QoS_*' } | Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue");

            File.Move(_legacyPath, _legacyPath + ".migrated", overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Legacy bandwidth cleanup failed: {ex.Message}");
        }
        finally
        {
            _firewallGate.Release();
        }
    }

    private static void RunPowerShell(string command)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process == null) return;
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(20000))
            {
                try { process.Kill(); } catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PowerShell failed: {ex.Message}");
        }
    }

    private void Save()
    {
        var file = new RulesFile { Apps = _apps.Values.ToList(), Global = _global };
        if (!AdminOnlyStore.Save(RulesFileName, file))
            Debug.WriteLine("Admin-only store unavailable — bandwidth rules are kept for this session only");
    }

    #endregion

    private static long NormalizeRate(long bps) => bps < 0 ? RateLimit.Unlimited : bps;

    private static string DisplayName(string processName)
    {
        var name = processName.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}

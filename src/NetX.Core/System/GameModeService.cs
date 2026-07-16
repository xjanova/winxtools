using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace NetX.Core.System;

/// <summary>
/// One-click, fully reversible "Gamer Mode".
///
/// Every registry value we touch is read first and its ORIGINAL state (value +
/// kind, or "did not exist") is saved to disk before we overwrite it. Revert
/// replays that snapshot exactly, so turning gamer mode off returns the machine
/// to precisely where it started — no guessing at "default" values.
///
/// Tweaks are version- and privilege-aware: HAGS is skipped on builds that don't
/// expose it, HKLM tweaks are skipped (and reported) when not elevated, etc.
/// </summary>
public sealed class GameModeService
{
    private static GameModeService? _instance;
    public static GameModeService Instance => _instance ??= new GameModeService();

    private readonly string _statePath;
    private readonly bool _isAdmin;
    private GameModeState _state;

    private GameModeService()
    {
        _statePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetX", "gamemode_state.json");
        _isAdmin = CheckAdmin();
        _state = LoadState();
    }

    public bool IsActive => _state.Active;
    public bool IsAdmin => _isAdmin;
    public DateTime? AppliedAt => _state.Active ? _state.AppliedAt : null;

    private static bool CheckAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    #region Tweak catalogue

    /// <summary>
    /// The curated set of gamer-mode registry tweaks. Order matters only for
    /// display; revert restores in reverse.
    /// </summary>
    private List<GameTweak> BuildTweaks()
    {
        var os = WindowsVersionInfo.Current;
        var tweaks = new List<GameTweak>
        {
            // ---- Xbox Game Bar / Game DVR (recording overlay steals FPS) ----
            new("Disable Game DVR (capture)", RegistryHive.CurrentUser,
                @"System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord),
            new("Disable Game DVR app capture", RegistryHive.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord),
            new("Disable Game DVR policy", RegistryHive.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, RegistryValueKind.DWord),

            // ---- Windows Game Mode ON ----
            new("Enable Windows Game Mode", RegistryHive.CurrentUser,
                @"SOFTWARE\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord),

            // ---- Disable fullscreen optimizations globally (exclusive fullscreen) ----
            new("Fullscreen exclusive behavior", RegistryHive.CurrentUser,
                @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, RegistryValueKind.DWord),
            new("Honor user FSE behavior", RegistryHive.CurrentUser,
                @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, RegistryValueKind.DWord),

            // ---- Multimedia system profile: prioritise the game over background ----
            new("System responsiveness (games first)", RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 0, RegistryValueKind.DWord),
            new("Remove network throttling", RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord),

            // ---- MMCSS "Games" task scheduling ----
            new("Games task GPU priority", RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 8, RegistryValueKind.DWord),
            new("Games task CPU priority", RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", 6, RegistryValueKind.DWord),
            new("Games task scheduling category", RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category", "High", RegistryValueKind.String),
            new("Games task SFIO priority", RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "SFIO Priority", "High", RegistryValueKind.String),

            // ---- CPU quantum: bias toward the foreground game ----
            new("Foreground CPU boost", RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26, RegistryValueKind.DWord),

            // ---- Stop the OS parking/throttling CPU cores ----
            new("Disable CPU power throttling", RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, RegistryValueKind.DWord),
        };

        // HAGS only where the OS exposes it (Win10 2004+/Win11) and needs a reboot.
        if (os.SupportsHags)
        {
            tweaks.Add(new("Hardware-accelerated GPU scheduling", RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord)
            { RequiresReboot = true });
        }

        return tweaks;
    }

    #endregion

    #region Apply / Revert

    public GameModeResult ApplyGameMode()
    {
        var result = new GameModeResult();
        var tweaks = BuildTweaks();

        // If gamer mode is already active, the CURRENT registry values are our own
        // tweaks. Re-reading them would poison the revert baseline, so reuse the
        // previously-captured TRUE originals instead of reading live values.
        bool wasActive = _state.Active;
        var snapshot = new GameModeState { Active = true, AppliedAt = DateTime.Now };

        SavedRegValue? FindPriorOriginal(GameTweak t)
        {
            if (!wasActive) return null;
            var hiveStr = t.Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
            return _state.Registry.FirstOrDefault(r =>
                r.Hive == hiveStr && r.Path == t.Path && r.Name == t.Name);
        }

        foreach (var tweak in tweaks)
        {
            bool needsAdmin = tweak.Hive == RegistryHive.LocalMachine;
            if (needsAdmin && !_isAdmin)
            {
                result.SkippedNeedAdmin.Add(tweak.Label);
                continue;
            }

            try
            {
                var saved = FindPriorOriginal(tweak)
                            ?? ReadCurrent(tweak.Hive, tweak.Path, tweak.Name);
                snapshot.Registry.Add(saved);

                WriteValue(tweak.Hive, tweak.Path, tweak.Name, tweak.Value, tweak.Kind);
                result.Applied.Add(tweak.Label);
                if (tweak.RequiresReboot) result.RebootRecommended = true;
            }
            catch (Exception ex)
            {
                result.Failed.Add($"{tweak.Label}: {ex.Message}");
            }
        }

        // Power plan → Ultimate/High Performance (reversible: remember the old one,
        // or keep the true previous one if we were already active).
        try
        {
            snapshot.PreviousPowerSchemeGuid = wasActive
                ? _state.PreviousPowerSchemeGuid
                : GetActivePowerSchemeGuid();
            if (SetHighestPerformancePowerPlan())
                result.Applied.Add("High/Ultimate performance power plan");
        }
        catch (Exception ex)
        {
            result.Failed.Add($"Power plan: {ex.Message}");
        }

        // Carry forward any originals we skipped this run (e.g. HKLM without admin)
        // so a later elevated revert can still restore them.
        if (wasActive)
        {
            foreach (var prev in _state.Registry)
            {
                if (!snapshot.Registry.Any(r => r.Hive == prev.Hive && r.Path == prev.Path && r.Name == prev.Name))
                    snapshot.Registry.Add(prev);
            }
            snapshot.PreviousPowerSchemeGuid ??= _state.PreviousPowerSchemeGuid;
        }

        _state = snapshot;
        SaveState();

        result.Success = result.Applied.Count > 0;
        return result;
    }

    public GameModeResult RevertGameMode()
    {
        var result = new GameModeResult();

        if (!_state.Active && _state.Registry.Count == 0)
        {
            result.Success = true;
            result.Notes = "Gamer mode was not active.";
            return result;
        }

        // Restore registry values in reverse order.
        for (int i = _state.Registry.Count - 1; i >= 0; i--)
        {
            var saved = _state.Registry[i];
            var hive = saved.Hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            if (hive == RegistryHive.LocalMachine && !_isAdmin)
            {
                result.SkippedNeedAdmin.Add($"{saved.Path}\\{saved.Name}");
                continue;
            }

            try
            {
                RestoreValue(saved);
                result.Applied.Add($"Restored {saved.Name}");
            }
            catch (Exception ex)
            {
                result.Failed.Add($"{saved.Name}: {ex.Message}");
            }
        }

        // Restore previous power plan.
        if (!string.IsNullOrEmpty(_state.PreviousPowerSchemeGuid))
        {
            try
            {
                RunPowerCfg($"/setactive {_state.PreviousPowerSchemeGuid}");
                result.Applied.Add("Restored power plan");
            }
            catch (Exception ex)
            {
                result.Failed.Add($"Power plan: {ex.Message}");
            }
        }

        _state = new GameModeState { Active = false };
        SaveState();

        result.Success = result.Failed.Count == 0;
        result.RebootRecommended = true; // HAGS/PriorityControl fully apply after reboot
        return result;
    }

    #endregion

    #region Registry helpers

    private static RegistryKey OpenBase(RegistryHive hive) =>
        hive == RegistryHive.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;

    private static SavedRegValue ReadCurrent(RegistryHive hive, string path, string name)
    {
        var saved = new SavedRegValue
        {
            Hive = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU",
            Path = path,
            Name = name,
            Existed = false
        };

        using var key = OpenBase(hive).OpenSubKey(path, writable: false);
        if (key == null) return saved;

        var value = key.GetValue(name, null);
        if (value == null) return saved;

        saved.Existed = true;
        try { saved.Kind = key.GetValueKind(name).ToString(); } catch { saved.Kind = "DWord"; }
        saved.Value = value is int i ? i.ToString() : value.ToString();
        return saved;
    }

    private static void WriteValue(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
    {
        using var key = OpenBase(hive).CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($"Cannot open/create {path}");
        key.SetValue(name, value, kind);
    }

    private static void RestoreValue(SavedRegValue saved)
    {
        var hive = saved.Hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;

        if (!saved.Existed)
        {
            // Value did not exist before — remove what we added.
            using var key = OpenBase(hive).OpenSubKey(saved.Path, writable: true);
            if (key != null)
            {
                try { key.DeleteValue(saved.Name, throwOnMissingValue: false); } catch { }
            }
            return;
        }

        var kind = Enum.TryParse<RegistryValueKind>(saved.Kind, out var k) ? k : RegistryValueKind.DWord;
        object restored = kind == RegistryValueKind.String
            ? saved.Value ?? ""
            : int.TryParse(saved.Value, out var iv) ? iv : 0;

        using var wkey = OpenBase(hive).CreateSubKey(saved.Path, writable: true);
        wkey?.SetValue(saved.Name, restored, kind);
    }

    #endregion

    #region Power plan

    private const string UltimatePerfGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string HighPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    private static string? GetActivePowerSchemeGuid()
    {
        var output = RunPowerCfgWithOutput("/getactivescheme");
        // "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)"
        var idx = output.IndexOf(':');
        if (idx < 0) return null;
        var tail = output[(idx + 1)..].Trim();
        var space = tail.IndexOf(' ');
        return space > 0 ? tail[..space].Trim() : tail;
    }

    private static bool SetHighestPerformancePowerPlan()
    {
        // Try to unlock + activate Ultimate Performance; fall back to High Performance.
        RunPowerCfg($"-duplicatescheme {UltimatePerfGuid}"); // no-op if it already exists
        if (RunPowerCfg($"/setactive {UltimatePerfGuid}")) return true;
        return RunPowerCfg($"/setactive {HighPerfGuid}");
    }

    private static bool RunPowerCfg(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string RunPowerCfgWithOutput(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return outp;
        }
        catch { return ""; }
    }

    #endregion

    #region State persistence

    private GameModeState LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var json = File.ReadAllText(_statePath);
                return JsonSerializer.Deserialize<GameModeState>(json) ?? new GameModeState();
            }
        }
        catch { }
        return new GameModeState();
    }

    private void SaveState()
    {
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_statePath,
                JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save gamer-mode state: {ex.Message}");
        }
    }

    #endregion

    #region Models

    private sealed class GameTweak
    {
        public string Label { get; }
        public RegistryHive Hive { get; }
        public string Path { get; }
        public string Name { get; }
        public object Value { get; }
        public RegistryValueKind Kind { get; }
        public bool RequiresReboot { get; init; }

        public GameTweak(string label, RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
        {
            Label = label; Hive = hive; Path = path; Name = name; Value = value; Kind = kind;
        }
    }

    #endregion
}

public sealed class SavedRegValue
{
    public string Hive { get; set; } = "HKCU";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public string? Kind { get; set; }
    public string? Value { get; set; }
}

public sealed class GameModeState
{
    public bool Active { get; set; }
    public DateTime AppliedAt { get; set; }
    public List<SavedRegValue> Registry { get; set; } = new();
    public string? PreviousPowerSchemeGuid { get; set; }
}

public sealed class GameModeResult
{
    public bool Success { get; set; }
    public List<string> Applied { get; set; } = new();
    public List<string> Failed { get; set; } = new();
    public List<string> SkippedNeedAdmin { get; set; } = new();
    public bool RebootRecommended { get; set; }
    public string? Notes { get; set; }

    public string BuildSummary()
    {
        var lines = new List<string>();
        if (Applied.Count > 0) lines.Add($"✓ Applied {Applied.Count} tweak(s).");
        if (SkippedNeedAdmin.Count > 0)
            lines.Add($"⚠ Skipped {SkippedNeedAdmin.Count} (need Administrator): {string.Join(", ", SkippedNeedAdmin)}");
        if (Failed.Count > 0)
            lines.Add($"✗ Failed {Failed.Count}: {string.Join(", ", Failed)}");
        if (RebootRecommended) lines.Add("↻ A restart is recommended for all changes to take effect.");
        if (!string.IsNullOrEmpty(Notes)) lines.Add(Notes!);
        return string.Join("\n", lines);
    }
}

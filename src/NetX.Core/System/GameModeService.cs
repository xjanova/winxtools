using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using NetX.Core.System.Tweaks;

namespace NetX.Core.System;

/// <summary>
/// One-click, fully reversible "Gamer Mode".
///
/// Every registry value we touch is read first and its ORIGINAL state (value +
/// kind, or "did not exist") is saved BEFORE the first write, so a crash or a
/// double click mid-apply never loses the user's originals. Revert replays that
/// snapshot exactly, so turning gamer mode off returns the machine to precisely
/// where it started — no guessing at "default" values.
///
/// Security: the snapshot is written back into HKLM by an elevated process, so
/// it lives in the admin-only %ProgramData%\WinXTools store, and on revert only
/// entries whose hive/path/name/kind match the known tweak list (with a sane
/// value) are ever written. The power-plan GUID is parsed and must exist.
///
/// Only tweaks with a real effect are applied. Values older versions set that do
/// nothing (SystemResponsiveness=0 is clamped to 20, Win32PrioritySeparation
/// 0x26 behaves like the client default, MMCSS "GPU Priority"/"SFIO Priority"
/// are unused) are no longer written, but are still restored from old backups.
/// </summary>
public sealed class GameModeService
{
    private static readonly object InstanceLock = new();
    private static GameModeService? _instance;

    public static GameModeService Instance
    {
        get
        {
            lock (InstanceLock) return _instance ??= new GameModeService();
        }
    }

    private const string StateFile = "gamemode_state.json";
    private const long MaxLegacyStateBytes = 1024 * 1024;

    private const string MmPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string MmGamesPath = MmPath + @"\Tasks\Games";

    private readonly string _legacyStatePath;
    private readonly bool _isAdmin;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GameModeState _state;

    private GameModeService()
    {
        // Pre-2.0 builds kept the snapshot in a user-writable folder.
        _legacyStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetX", "gamemode_state.json");
        _isAdmin = CheckAdmin();
        _state = LoadState();
    }

    public bool IsActive => _state.Active;
    public bool IsAdmin => _isAdmin;
    public DateTime? AppliedAt => _state.Active ? _state.AppliedAt : null;

    /// <summary>True while an apply/revert is running (a second call returns "busy").</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

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
    /// Every value Gamer Mode has ever written (current and older versions).
    /// This is the allow-list: a saved entry that doesn't match one of these
    /// exactly is never written back.
    /// </summary>
    private static readonly GameTweak[] KnownTweaks =
    {
        // ---- Xbox Game Bar / Game DVR (background recording costs FPS) ----
        new("dvr", L("Game Bar background recording off", "ปิดการอัดวิดีโอเบื้องหลังของ Game Bar"),
            RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord,
            windowsDefault: 1, isValid: v => v is 0 or 1),
        new("dvr-capture", L("Game Bar app capture off", "ปิดการจับภาพแอปของ Game Bar"),
            RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord,
            windowsDefault: 1, isValid: v => v is 0 or 1),
        new("dvr-policy", L("Game DVR policy off", "นโยบายปิด Game DVR"),
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, RegistryValueKind.DWord,
            windowsDefault: null, isValid: v => v is 0 or 1),

        // ---- Exclusive fullscreen instead of fullscreen optimizations ----
        new("fse", L("Exclusive fullscreen", "โหมดเต็มจอแบบ exclusive"),
            RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, RegistryValueKind.DWord,
            windowsDefault: 0, isValid: v => v is >= 0 and <= 2),
        new("fse-honor", L("Honor exclusive fullscreen setting", "ใช้ค่าเต็มจอแบบ exclusive"),
            RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, RegistryValueKind.DWord,
            windowsDefault: 0, isValid: v => v is 0 or 1),

        // ---- Windows Game Mode (only written when the user had turned it off) ----
        new("game-mode", L("Windows Game Mode on", "เปิด Game Mode ของ Windows"),
            RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord,
            windowsDefault: null, isValid: v => v is 0 or 1),

        // ---- MMCSS: don't throttle non-multimedia network traffic ----
        new("net-throttling", L("Multimedia network throttling off", "ปิดการจำกัดเครือข่ายขณะเล่นสื่อ"),
            RegistryHive.LocalMachine, MmPath, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord,
            windowsDefault: 10, isValid: v => v is int n && (n is >= 1 and <= 70 || n == -1), requiresReboot: true),

        // ---- Background power throttling (EcoQoS) off — desktops only ----
        new("power-throttling", L("Background power throttling off", "ปิดการลดพลังงานแอปเบื้องหลัง"),
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, RegistryValueKind.DWord,
            windowsDefault: null, isValid: v => v is 0 or 1, requiresReboot: true),

        // ---- Hardware-accelerated GPU scheduling (supported GPUs only) ----
        new("hags", L("Hardware-accelerated GPU scheduling", "Hardware-accelerated GPU scheduling"),
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord,
            windowsDefault: null, isValid: v => v is 1 or 2, requiresReboot: true),

        // ---- Legacy (no measurable effect; restore only) ----
        new("legacy-responsiveness", L("System responsiveness", "System responsiveness"),
            RegistryHive.LocalMachine, MmPath, "SystemResponsiveness", 0, RegistryValueKind.DWord,
            windowsDefault: 20, isValid: v => v is >= 0 and <= 100, legacy: true),
        new("legacy-gpu-priority", L("Games task GPU priority", "Games task GPU priority"),
            RegistryHive.LocalMachine, MmGamesPath, "GPU Priority", 8, RegistryValueKind.DWord,
            windowsDefault: 8, isValid: v => v is >= 0 and <= 31, legacy: true),
        new("legacy-priority", L("Games task priority", "Games task priority"),
            RegistryHive.LocalMachine, MmGamesPath, "Priority", 6, RegistryValueKind.DWord,
            windowsDefault: 2, isValid: v => v is >= 1 and <= 8, legacy: true),
        new("legacy-scheduling", L("Games task scheduling category", "Games task scheduling category"),
            RegistryHive.LocalMachine, MmGamesPath, "Scheduling Category", "High", RegistryValueKind.String,
            windowsDefault: "Medium", isValid: v => v is "Low" or "Medium" or "High", legacy: true),
        new("legacy-sfio", L("Games task SFIO priority", "Games task SFIO priority"),
            RegistryHive.LocalMachine, MmGamesPath, "SFIO Priority", "High", RegistryValueKind.String,
            windowsDefault: "Normal", isValid: v => v is "Idle" or "Low" or "Normal" or "High", legacy: true),
        new("legacy-priority-separation", L("Foreground CPU priority", "Foreground CPU priority"),
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26, RegistryValueKind.DWord,
            windowsDefault: 2, isValid: v => v is >= 0 and <= 0x3F, requiresReboot: true, legacy: true),
    };

    private static LocText L(string en, string th) => new(en, th);

    /// <summary>The tweaks that do something on THIS machine right now.</summary>
    private List<GameTweak> BuildTweaks()
    {
        var os = WindowsVersionInfo.Current;
        var list = new List<GameTweak>();
        foreach (var t in KnownTweaks.Where(t => !t.Legacy))
        {
            switch (t.Id)
            {
                case "game-mode":
                    // Game Mode is on by default; only a user who turned it off gets a real change.
                    if (Reg.ReadDword(t.Hive, t.Path, t.Name) != 0) continue;
                    break;
                case "power-throttling":
                    if (PowerPlans.HasBattery) continue; // laptops: more heat, less battery
                    break;
                case "hags":
                    if (!os.SupportsHags || NativeSettings.QueryHags() is { Supported: false }) continue;
                    break;
            }
            list.Add(t);
        }
        return list;
    }

    /// <summary>Human-readable list of what Apply would change (for the Tricks page).</summary>
    public string DescribeTweaks()
    {
        var lines = BuildTweaks().Select(t => $"{Reg.HiveName(t.Hive)}\\{t.Path}\\{t.Name} = {Reg.Format(t.Value)}").ToList();
        if (WantsPowerPlan()) lines.Add($"Power plan → {PowerPlans.AppUltimateName}");
        return string.Join("\n", lines);
    }

    private static bool WantsPowerPlan() => !PowerPlans.IsModernStandby && !PowerPlans.HasBattery;

    private static GameTweak? FindKnown(SavedRegValue saved) =>
        KnownTweaks.FirstOrDefault(t =>
            string.Equals(HiveString(t.Hive), saved.Hive, StringComparison.Ordinal) &&
            string.Equals(t.Path, saved.Path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Name, saved.Name, StringComparison.OrdinalIgnoreCase) &&
            (!saved.Existed || string.Equals(t.Kind.ToString(), saved.Kind, StringComparison.Ordinal)));

    #endregion

    #region Status

    /// <summary>State for the Tricks page badge.</summary>
    public TrickStatus DetectStatus()
    {
        if (!_state.Active) return TrickStatus.Default();
        if (_state.RevertIncomplete)
            return TrickStatus.Custom(new LocText(
                $"{_state.Registry.Count + (_state.PowerPlanChanged ? 1 : 0)} setting(s) still to restore.",
                $"ยังเหลือ {_state.Registry.Count + (_state.PowerPlanChanged ? 1 : 0)} ค่าที่ต้องคืน"));
        var note = new LocText($"Turned on {_state.AppliedAt:g}", $"เปิดเมื่อ {_state.AppliedAt:g}");
        return TrickStatus.Applied(note);
    }

    #endregion

    #region Apply / Revert

    public GameModeResult ApplyGameMode()
    {
        var result = new GameModeResult();
        if (!_gate.Wait(0))
        {
            result.Busy = true;
            result.AddNote(L("Gamer Mode is already being changed — please wait.", "กำลังเปลี่ยนโหมดเกมเมอร์อยู่ กรุณารอสักครู่"));
            return result;
        }

        try
        {
            if (!_isAdmin)
            {
                result.AddNote(TweakErrors.NeedAdmin);
                return result;
            }

            var tweaks = BuildTweaks();
            bool wasActive = _state.Active;
            bool planChangedBefore = wasActive && _state.PowerPlanChanged;
            var snapshot = new GameModeState { Active = true, AppliedAt = DateTime.Now };

            // 1) Originals. If gamer mode is already active, the CURRENT values are
            // our own tweaks — reuse the previously captured TRUE originals.
            foreach (var tweak in tweaks)
            {
                var prior = wasActive ? _state.Registry.FirstOrDefault(r => SameValue(r, tweak)) : null;
                snapshot.Registry.Add(prior ?? ReadCurrent(tweak));
            }
            if (wasActive)
            {
                // Keep originals we didn't touch this time (older tweaks, skipped ones)
                // so a later revert still restores them.
                foreach (var prev in _state.Registry)
                    if (!snapshot.Registry.Any(r => SameKey(r, prev)))
                        snapshot.Registry.Add(prev);
            }

            bool wantPlan = WantsPowerPlan();
            if (wantPlan || planChangedBefore)
            {
                snapshot.PowerPlanChanged = true;
                snapshot.PreviousPowerSchemeGuid = planChangedBefore
                    ? _state.PreviousPowerSchemeGuid
                    : PowerPlans.GetActive()?.ToString("D");
            }

            // 2) Persist the backup BEFORE the first write. No backup → no change.
            try
            {
                SecureAppData.Save(StateFile, snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Gamer mode: cannot save backup: {ex.Message}");
                result.AddFailed(L("Couldn't save a backup of your current settings, so nothing was changed.",
                    "บันทึกสำรองค่าปัจจุบันไม่ได้ จึงยังไม่ได้เปลี่ยนแปลงอะไร"));
                return result;
            }
            _state = snapshot;

            // 3) Write and read back.
            foreach (var tweak in tweaks)
            {
                try
                {
                    Reg.Write(tweak.Hive, tweak.Path, tweak.Name, tweak.Value, tweak.Kind);
                    if (!Reg.ValueEquals(Reg.Read(tweak.Hive, tweak.Path, tweak.Name), tweak.Value))
                    {
                        result.AddFailed(Combine(tweak.Label, TweakErrors.NotVerified));
                        continue;
                    }
                    result.AddApplied(tweak.Label);
                    if (tweak.RequiresReboot) result.RebootRecommended = true;
                }
                catch (Exception ex)
                {
                    result.AddFailed(Combine(tweak.Label, TweakErrors.Friendly(ex)));
                }
            }

            // 4) Power plan (desktops without Modern Standby only).
            if (wantPlan)
            {
                if (PowerPlans.EnsureAppUltimatePlan() && PowerPlans.SetActive(PowerPlans.AppUltimate))
                    result.AddApplied(L("Ultimate Performance power plan", "แผนพลังงาน Ultimate Performance"));
                else if (PowerPlans.Exists(PowerPlans.HighPerformance) && PowerPlans.SetActive(PowerPlans.HighPerformance))
                    result.AddApplied(L("High performance power plan", "แผนพลังงานประสิทธิภาพสูง"));
                else
                {
                    result.AddFailed(L("Power plan couldn't be changed.", "เปลี่ยนแผนพลังงานไม่ได้"));
                    // Nothing was switched now: revert must not touch the plan
                    // unless an earlier apply switched it.
                    _state.PowerPlanChanged = planChangedBefore;
                }
            }
            else
            {
                result.AddSkipped(L("Power plan kept (laptop or Modern Standby PC).",
                    "ไม่เปลี่ยนแผนพลังงาน (โน้ตบุ๊กหรือเครื่องที่ใช้ Modern Standby)"));
            }

            if (result.Applied.Count == 0 && !wasActive)
            {
                // Nothing changed: don't leave a misleading "active" backup behind.
                _state = new GameModeState();
                SecureAppData.Delete(StateFile);
            }
            else
            {
                TrySaveState(result);
            }

            result.Success = result.Applied.Count > 0 && result.Failed.Count == 0;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public GameModeResult RevertGameMode()
    {
        var result = new GameModeResult();
        if (!_gate.Wait(0))
        {
            result.Busy = true;
            result.AddNote(L("Gamer Mode is already being changed — please wait.", "กำลังเปลี่ยนโหมดเกมเมอร์อยู่ กรุณารอสักครู่"));
            return result;
        }

        try
        {
            if (!_state.Active && _state.Registry.Count == 0 && !_state.PowerPlanChanged)
            {
                result.Success = true;
                result.AddNote(L("Gamer Mode was not active.", "โหมดเกมเมอร์ไม่ได้เปิดอยู่"));
                return result;
            }

            if (!_isAdmin)
            {
                result.AddNote(TweakErrors.NeedAdmin);
                return result;
            }

            var remaining = new List<SavedRegValue>();

            // Restore registry values in reverse order.
            for (int i = _state.Registry.Count - 1; i >= 0; i--)
            {
                var saved = _state.Registry[i];
                var known = FindKnown(saved);
                if (known == null)
                {
                    Debug.WriteLine($"Gamer mode: ignoring unknown backup entry {saved.Hive}\\{saved.Path}\\{saved.Name}");
                    continue;
                }

                try
                {
                    RestoreValue(known, saved);
                    result.AddApplied(L($"Restored: {known.Label.En}", $"คืนค่า: {known.Label.Th}"));
                    if (known.RequiresReboot) result.RebootRecommended = true;
                }
                catch (Exception ex)
                {
                    remaining.Insert(0, saved); // keep for retry
                    result.AddFailed(Combine(known.Label, TweakErrors.Friendly(ex)));
                }
            }

            // Restore the previous power plan — only if a plan WE set is still active
            // (if the user picked another plan since, respect that choice).
            bool planPending = false;
            if (_state.PowerPlanChanged)
            {
                var active = PowerPlans.GetActive();
                bool oursActive = active == PowerPlans.AppUltimate || active == PowerPlans.HighPerformance ||
                                  active == PowerPlans.UltimateTemplate;
                if (oursActive)
                {
                    var target = ResolveRestorePlan(_state.PreviousPowerSchemeGuid);
                    if (target == null || active == target || PowerPlans.SetActive(target.Value))
                        result.AddApplied(L("Restored: previous power plan", "คืนค่า: แผนพลังงานเดิม"));
                    else
                    {
                        planPending = true;
                        result.AddFailed(L("Power plan couldn't be restored.", "คืนค่าแผนพลังงานไม่สำเร็จ"));
                    }
                }
                if (!planPending && !PowerPlans.AppPlanNeededByTricks())
                    PowerPlans.DeleteAppUltimatePlanIfUnused();
            }

            _state = new GameModeState
            {
                Active = remaining.Count > 0 || planPending,
                AppliedAt = _state.AppliedAt,
                Registry = remaining,
                PowerPlanChanged = planPending,
                PreviousPowerSchemeGuid = planPending ? _state.PreviousPowerSchemeGuid : null,
                RevertIncomplete = remaining.Count > 0 || planPending
            };

            if (_state.Active) TrySaveState(result);
            else SecureAppData.Delete(StateFile);

            result.Success = result.Failed.Count == 0;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Guid? ResolveRestorePlan(string? saved)
    {
        var plans = PowerPlans.List();
        if (Guid.TryParse(saved, out var g) && plans.Contains(g) && g != PowerPlans.UltimateTemplate)
            return g;
        return plans.Contains(PowerPlans.Balanced) ? PowerPlans.Balanced : null;
    }

    private void TrySaveState(GameModeResult result)
    {
        try
        {
            SecureAppData.Save(StateFile, _state);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Gamer mode: cannot update backup: {ex.Message}");
            result.AddNote(L("Warning: the backup file couldn't be updated.", "คำเตือน: อัปเดตไฟล์สำรองไม่ได้"));
        }
    }

    private static LocText Combine(LocText label, LocText reason) =>
        new($"{label.En}: {reason.En}", $"{label.Th}: {reason.Th}");

    #endregion

    #region Registry helpers

    private static string HiveString(RegistryHive hive) => hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";

    private static bool SameValue(SavedRegValue saved, GameTweak t) =>
        saved.Hive == HiveString(t.Hive) &&
        string.Equals(saved.Path, t.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(saved.Name, t.Name, StringComparison.OrdinalIgnoreCase);

    private static bool SameKey(SavedRegValue a, SavedRegValue b) =>
        a.Hive == b.Hive &&
        string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private static SavedRegValue ReadCurrent(GameTweak t)
    {
        var saved = new SavedRegValue { Hive = HiveString(t.Hive), Path = t.Path, Name = t.Name, Existed = false };
        var value = Reg.Read(t.Hive, t.Path, t.Name);
        if (value == null) return saved;

        saved.Existed = true;
        saved.Kind = t.Kind.ToString();
        saved.Value = value is int i ? i.ToString(CultureInfo.InvariantCulture) : value.ToString();
        return saved;
    }

    /// <summary>Parses a saved value for this tweak; null when it isn't a sane value.</summary>
    private static object? ParseSaved(GameTweak t, SavedRegValue saved)
    {
        object? value = t.Kind == RegistryValueKind.String
            ? saved.Value
            : int.TryParse(saved.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
        return value != null && t.IsValid(value) ? value : null;
    }

    private static void RestoreValue(GameTweak t, SavedRegValue saved)
    {
        if (!saved.Existed)
        {
            // Value did not exist before — remove what we added.
            Reg.DeleteValue(t.Hive, t.Path, t.Name);
            return;
        }

        // A malformed/out-of-range saved value is never written: use the Windows default instead.
        var value = ParseSaved(t, saved) ?? t.WindowsDefault;
        if (value == null) Reg.DeleteValue(t.Hive, t.Path, t.Name);
        else Reg.Write(t.Hive, t.Path, t.Name, value, t.Kind);
    }

    #endregion

    #region State persistence

    private GameModeState LoadState()
    {
        var state = SecureAppData.Load<GameModeState>(StateFile);
        if (state != null) return Sanitize(state);

        return MigrateLegacyState() ?? new GameModeState();
    }

    /// <summary>Drops everything that isn't on the allow-list or isn't a valid GUID.</summary>
    private static GameModeState Sanitize(GameModeState state)
    {
        state.Registry = (state.Registry ?? new List<SavedRegValue>())
            .Where(r => r != null && FindKnown(r) != null)
            .GroupBy(r => $"{r.Hive}\\{r.Path}\\{r.Name}".ToLowerInvariant())
            .Select(g => g.First())
            .ToList();
        if (!Guid.TryParse(state.PreviousPowerSchemeGuid, out _))
            state.PreviousPowerSchemeGuid = null;
        return state;
    }

    /// <summary>
    /// Imports a snapshot written by an older version (user-writable folder).
    /// Its values are NOT trusted for HKLM: machine-wide entries are restored to
    /// the known Windows defaults, only per-user (HKCU) values are taken from it.
    /// </summary>
    private GameModeState? MigrateLegacyState()
    {
        try
        {
            var info = new FileInfo(_legacyStatePath);
            if (!info.Exists) return null;
            if (info.Length > MaxLegacyStateBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                TryDeleteLegacy();
                return null;
            }

            var legacy = JsonSerializer.Deserialize<GameModeState>(File.ReadAllText(_legacyStatePath));
            if (legacy == null || !legacy.Active)
            {
                TryDeleteLegacy();
                return null;
            }

            var migrated = new GameModeState
            {
                Active = true,
                AppliedAt = legacy.AppliedAt,
                PowerPlanChanged = Guid.TryParse(legacy.PreviousPowerSchemeGuid, out _),
                PreviousPowerSchemeGuid = Guid.TryParse(legacy.PreviousPowerSchemeGuid, out var g) ? g.ToString("D") : null
            };

            foreach (var entry in legacy.Registry ?? new List<SavedRegValue>())
            {
                var known = entry == null ? null : FindKnown(entry);
                if (known == null) continue;

                if (known.Hive == RegistryHive.LocalMachine)
                {
                    migrated.Registry.Add(new SavedRegValue
                    {
                        Hive = "HKLM",
                        Path = known.Path,
                        Name = known.Name,
                        Existed = known.WindowsDefault != null,
                        Kind = known.Kind.ToString(),
                        Value = known.WindowsDefault is int i ? i.ToString(CultureInfo.InvariantCulture) : known.WindowsDefault as string
                    });
                }
                else
                {
                    migrated.Registry.Add(entry!);
                }
            }

            migrated = Sanitize(migrated);
            try
            {
                SecureAppData.Save(StateFile, migrated);
                TryDeleteLegacy();
            }
            catch (Exception ex)
            {
                // Not elevated: keep the legacy file so the next elevated run can migrate it.
                Debug.WriteLine($"Gamer mode: legacy migration postponed: {ex.Message}");
            }
            return migrated;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Gamer mode: legacy state ignored: {ex.Message}");
            return null;
        }
    }

    private void TryDeleteLegacy()
    {
        try { File.Delete(_legacyStatePath); } catch { }
    }

    #endregion

    #region Models

    private sealed class GameTweak
    {
        public GameTweak(string id, LocText label, RegistryHive hive, string path, string name, object value,
            RegistryValueKind kind, object? windowsDefault, Func<object, bool> isValid,
            bool requiresReboot = false, bool legacy = false)
        {
            Id = id; Label = label; Hive = hive; Path = path; Name = name; Value = value; Kind = kind;
            WindowsDefault = windowsDefault; IsValid = isValid; RequiresReboot = requiresReboot; Legacy = legacy;
        }

        public string Id { get; }
        public LocText Label { get; }
        public RegistryHive Hive { get; }
        public string Path { get; }
        public string Name { get; }
        public object Value { get; }
        public RegistryValueKind Kind { get; }
        /// <summary>Value of a clean Windows install (null = not present).</summary>
        public object? WindowsDefault { get; }
        /// <summary>Accepts only sane original values from a backup.</summary>
        public Func<object, bool> IsValid { get; }
        public bool RequiresReboot { get; }
        /// <summary>Written by older versions only; kept so their backups can be restored.</summary>
        public bool Legacy { get; }
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
    /// <summary>True when Gamer Mode switched the power plan (so revert should switch back).</summary>
    public bool PowerPlanChanged { get; set; }
    /// <summary>A revert ran but some values could not be restored yet (kept for retry).</summary>
    public bool RevertIncomplete { get; set; }
}

public sealed class GameModeResult
{
    public bool Success { get; set; }
    /// <summary>Another apply/revert was still running; nothing was done.</summary>
    public bool Busy { get; set; }
    public List<string> Applied { get; set; } = new();
    public List<string> Failed { get; set; } = new();
    public List<string> SkippedNeedAdmin { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
    public bool RebootRecommended { get; set; }
    public string? Notes { get; set; }

    private readonly List<LocText> _applied = new();
    private readonly List<LocText> _failed = new();
    private readonly List<LocText> _skipped = new();
    private readonly List<LocText> _notes = new();

    internal void AddApplied(LocText text) { _applied.Add(text); Applied.Add(text.En); }
    internal void AddFailed(LocText text) { _failed.Add(text); Failed.Add(text.En); }
    internal void AddSkipped(LocText text) { _skipped.Add(text); Skipped.Add(text.En); }
    internal void AddNote(LocText text)
    {
        _notes.Add(text);
        Notes = string.Join("\n", _notes.Select(n => n.En));
    }

    public string BuildSummary() => BuildSummary(thai: false);

    /// <summary>Summary in the UI language. Lists what failed, never a raw exception text.</summary>
    public string BuildSummary(bool thai)
    {
        string T(LocText t) => t.Get(thai);
        var lines = new List<string>();
        if (_applied.Count > 0)
            lines.Add(thai ? $"✓ สำเร็จ {_applied.Count} รายการ" : $"✓ Done: {_applied.Count} item(s).");
        foreach (var s in _skipped) lines.Add("• " + T(s));
        if (SkippedNeedAdmin.Count > 0)
            lines.Add(thai
                ? $"⚠ ข้าม {SkippedNeedAdmin.Count} รายการ (ต้องใช้สิทธิ์ผู้ดูแลระบบ)"
                : $"⚠ Skipped {SkippedNeedAdmin.Count} (need Administrator)");
        if (_failed.Count > 0)
        {
            lines.Add(thai ? $"✗ ไม่สำเร็จ {_failed.Count} รายการ:" : $"✗ Failed: {_failed.Count}:");
            lines.AddRange(_failed.Select(f => "   – " + T(f)));
        }
        if (RebootRecommended)
            lines.Add(thai ? "↻ แนะนำให้รีสตาร์ทเครื่องเพื่อให้การเปลี่ยนแปลงมีผลครบ" : "↻ Restart the PC for all changes to take effect.");
        lines.AddRange(_notes.Select(T));
        return string.Join("\n", lines);
    }

    /// <summary>Adapter for the Windows Tricks page.</summary>
    public TrickResult ToTrickResult() => new()
    {
        Success = Success,
        Message = new LocText(BuildSummary(false), BuildSummary(true)),
        Restart = RebootRecommended ? RestartScope.Reboot : RestartScope.None
    };
}

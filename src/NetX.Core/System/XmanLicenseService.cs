using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetX.Core.Data;
using NetX.Core.Helpers;

namespace NetX.Core.System;

/// <summary>
/// WinXTools Pro licenses through the xman studio license server — the same
/// /api/v1/product/{slug} API the studio's other apps use (register-device, activate,
/// validate, check-machine, deactivate, demo). Licenses and trials are tied to the
/// "winx-tools" product, so a key bought for another xman product does not unlock Pro.
///
/// The last good answer is kept in %ProgramData%\WinXTools (admin-only, HMAC-sealed to this
/// PC), so Pro works right away at startup and for a while without internet. Only a clear
/// answer from the server changes the license; offline, a 5xx or a rate limit never does.
/// </summary>
public sealed class XmanLicenseService
{
    private static readonly Lazy<XmanLicenseService> _instance = new(() => new XmanLicenseService());
    public static XmanLicenseService Instance => _instance.Value;

    private const string StoreFile = "license.json";
    private const string LegacyDbKey = "LicenseKey"; // where versions before 2026-09 kept the key
    /// <summary>How long Pro keeps working without reaching the server after the last good check.</summary>
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(30);
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> PaidTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "lifetime", "yearly", "monthly", "weekly", "daily", "product"
    };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loadLock = new();
    private LicenseStatus _status = LicenseStatus.None;
    private volatile bool _savedLoaded;

    /// <summary>Raised (on a worker thread) whenever <see cref="CachedStatus"/> changes.</summary>
    public event Action<LicenseStatus>? StatusChanged;

    private XmanLicenseService()
    {
        _http = XmanApi.CreateClient("WinXTools-License/" + AutoUpdateService.GetCurrentVersion(), TimeSpan.FromSeconds(15));
    }

    public LicenseStatus CachedStatus
    {
        get
        {
            EnsureSavedLoaded();
            return _status;
        }
    }

    private static string MachineId => MachineIdentity.MachineId;

    #region Startup

    /// <summary>
    /// Registers this PC with the server, then re-checks the saved key — or, when there is
    /// none, asks whether this PC already owns a license (e.g. after reinstalling WinXTools).
    /// </summary>
    public async Task InitializeAsync()
    {
        EnsureSavedLoaded();
        await _gate.WaitAsync();
        try
        {
            // Runs alongside the license check; its answer is not needed for anything.
            _ = RegisterDeviceCoreAsync();

            var saved = _status.LicenseKey;
            if (!string.IsNullOrEmpty(saved))
                await ValidateCoreAsync(saved);
            else
                await RestoreFromServerCoreAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"License init failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Checks the saved key with the server again (Settings, periodic refresh).</summary>
    public async Task<LicenseResult> RefreshAsync()
    {
        EnsureSavedLoaded();
        await _gate.WaitAsync();
        try
        {
            var saved = _status.LicenseKey;
            return string.IsNullOrEmpty(saved)
                ? await RestoreFromServerCoreAsync()
                : await ValidateCoreAsync(saved);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RegisterDeviceCoreAsync()
    {
        // Other xman apps register at every start; the server uses it for device lists and
        // trial-abuse checks. Nothing depends on the answer.
        var reply = await XmanApi.PostAsync(_http, "/register-device", new Dictionary<string, object?>
        {
            ["machine_id"] = MachineId,
            ["machine_name"] = Truncate(MachineIdentity.MachineName, 255),
            ["os_version"] = Truncate(MachineIdentity.OsVersion, 255),
            ["app_version"] = Truncate(AutoUpdateService.GetCurrentVersion(), 50),
            ["hardware_hash"] = MachineIdentity.HardwareHash
        });
        if (!reply.Success) Debug.WriteLine($"register-device: {(int?)reply.Status} {reply.ErrorCode}");
    }

    #endregion

    #region Activate / deactivate

    /// <summary>
    /// Activates <paramref name="licenseKey"/> on this PC. With <paramref name="moveFromOtherPc"/>
    /// the server unbinds it from the PC it was on first (only after the user agreed to that).
    /// A failure never touches the license that is already active here.
    /// </summary>
    public async Task<LicenseResult> ActivateAsync(string licenseKey, bool moveFromOtherPc = false)
    {
        var key = NormalizeKey(licenseKey);
        if (!LooksLikeKey(key)) return LicenseResult.Fail(LicenseCode.InvalidInput);

        EnsureSavedLoaded();
        await _gate.WaitAsync();
        try
        {
            var result = await ActivateCoreAsync(key, moveFromOtherPc);

            // A key that was activated by an older WinXTools on this same PC is bound to the
            // old machine id; move it over without bothering the user.
            if (result.Code == LicenseCode.OtherDevice && !moveFromOtherPc && await ReleaseLegacyBindingAsync(key))
                result = await ActivateCoreAsync(key, false);

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LicenseResult> ActivateCoreAsync(string key, bool force)
    {
        var body = new Dictionary<string, object?>
        {
            ["license_key"] = key,
            ["machine_id"] = MachineId,
            ["machine_fingerprint"] = MachineIdentity.HardwareHash ?? MachineId,
            ["app_version"] = Truncate(AutoUpdateService.GetCurrentVersion(), 50)
        };
        if (force) body["force_rebind"] = "true";

        var reply = await XmanApi.PostAsync(_http, "/activate", body);
        if (!reply.IsDefinitive) return Unanswered(reply);

        if (reply.Success)
        {
            var data = reply.Data;
            var type = data.Str("license_type") ?? "";
            if (!PaidTypes.Contains(type))
            {
                // A DEMO-/FREE- key: the trial is handled on its own; it is not a Pro license.
                return LicenseResult.Fail(LicenseCode.NotProKey, reply.ServerMessage);
            }

            SetStatus(LicenseState.Active, key, type, data.Date("expires_at"), verified: true);
            return LicenseResult.Ok(reply.ServerMessage);
        }

        return reply.ErrorCode switch
        {
            "INVALID_LICENSE" => LicenseResult.Fail(LicenseCode.InvalidKey, reply.ServerMessage),
            "LICENSE_EXPIRED" => LicenseResult.Fail(LicenseCode.Expired, reply.ServerMessage),
            "LICENSE_REVOKED" => LicenseResult.Fail(LicenseCode.Revoked, reply.ServerMessage),
            "ALREADY_ACTIVATED_OTHER_DEVICE" => LicenseResult.Fail(LicenseCode.OtherDevice, reply.ServerMessage),
            "PRODUCT_NOT_FOUND" => LicenseResult.Fail(LicenseCode.ServerBusy, reply.ServerMessage),
            _ when reply.Status == HttpStatusCode.UnprocessableEntity => LicenseResult.Fail(LicenseCode.InvalidInput, reply.ServerMessage),
            _ => LicenseResult.Fail(LicenseCode.Failed, reply.ServerMessage)
        };
    }

    /// <summary>
    /// Frees the license from this PC so it can be activated on another one. Offline, nothing
    /// changes here (the server would still count it as used on this PC).
    /// </summary>
    public async Task<LicenseResult> DeactivateAsync()
    {
        EnsureSavedLoaded();
        await _gate.WaitAsync();
        try
        {
            var key = _status.LicenseKey;
            if (string.IsNullOrEmpty(key))
            {
                SetStatus(LicenseState.None, "", "", null, verified: false);
                return LicenseResult.Ok();
            }

            var reply = await XmanApi.PostAsync(_http, "/deactivate", new Dictionary<string, object?>
            {
                ["license_key"] = key,
                ["machine_id"] = MachineId
            });
            if (!reply.IsDefinitive) return Unanswered(reply);

            // INVALID_LICENSE: it is not bound to this PC any more, so there is nothing to free.
            if (reply.Success || reply.ErrorCode == "INVALID_LICENSE")
            {
                SetStatus(LicenseState.None, "", "", null, verified: false);
                return LicenseResult.Ok(reply.ServerMessage);
            }
            return LicenseResult.Fail(LicenseCode.Failed, reply.ServerMessage);
        }
        finally
        {
            _gate.Release();
        }
    }

    #endregion

    #region Validate / restore

    private async Task<LicenseResult> ValidateCoreAsync(string key)
    {
        var reply = await XmanApi.PostAsync(_http, "/validate", new Dictionary<string, object?>
        {
            ["license_key"] = key,
            ["machine_id"] = MachineId
        });
        if (!reply.IsDefinitive || reply.ErrorCode == "PRODUCT_NOT_FOUND") return Unanswered(reply);

        if (reply.Success)
        {
            var data = reply.Data;
            var type = data.Str("license_type") ?? _status.LicenseType;
            var expires = data.Date("expires_at");

            if (reply.Json.Bool("is_valid") == true)
            {
                SetStatus(LicenseState.Active, key, type, expires, verified: true);
                return LicenseResult.Ok();
            }

            // The server knows the key on this PC but it no longer counts (expired or revoked).
            // The key stays so the user can see what happened and renew it.
            var revoked = string.Equals(data.Str("status"), "revoked", StringComparison.OrdinalIgnoreCase);
            SetStatus(revoked ? LicenseState.Revoked : LicenseState.Expired, key, type, expires, verified: true);
            return LicenseResult.Fail(revoked ? LicenseCode.Revoked : LicenseCode.Expired);
        }

        if (reply.ErrorCode == "INVALID_LICENSE")
        {
            // Not bound to this PC's id. Either an older WinXTools activated it under the old
            // id (move it), or this PC owns a license under another key, or it was moved away.
            if (await ReleaseLegacyBindingAsync(key))
            {
                var moved = await ActivateCoreAsync(key, false);
                if (moved.Success || moved.Code is LicenseCode.Offline or LicenseCode.ServerBusy) return moved;
            }

            var restored = await RestoreFromServerCoreAsync();
            if (restored.Success) return restored;

            SetStatus(LicenseState.OtherMachine, key, _status.LicenseType, _status.ExpiresAt, verified: true);
            return LicenseResult.Fail(LicenseCode.NotOnThisMachine);
        }

        return LicenseResult.Fail(LicenseCode.Failed, reply.ServerMessage);
    }

    /// <summary>Asks the server whether this PC already owns an active WinXTools license.</summary>
    private async Task<LicenseResult> RestoreFromServerCoreAsync()
    {
        var reply = await XmanApi.PostAsync(_http, "/check-machine", new Dictionary<string, object?>
        {
            ["machine_id"] = MachineId
        });
        if (!reply.IsDefinitive) return Unanswered(reply);

        if (reply.Success && reply.Json.Bool("has_license") == true)
        {
            var data = reply.Data;
            var key = NormalizeKey(data.Str("license_key") ?? "");
            var type = data.Str("license_type") ?? "";
            if (key.Length > 0 && PaidTypes.Contains(type))
            {
                SetStatus(LicenseState.Active, key, type, data.Date("expires_at"), verified: true);
                return LicenseResult.Ok();
            }
        }
        return LicenseResult.Fail(LicenseCode.NoLicense);
    }

    /// <summary>
    /// True when <paramref name="key"/> was bound to the id older WinXTools versions sent from
    /// this PC and the server has now released it. Only this PC can compute that old id.
    /// </summary>
    private async Task<bool> ReleaseLegacyBindingAsync(string key)
    {
        var legacy = MachineIdentity.LegacyMachineId;
        if (legacy == MachineId || legacy.Length < 32) return false;

        var reply = await XmanApi.PostAsync(_http, "/deactivate", new Dictionary<string, object?>
        {
            ["license_key"] = key,
            ["machine_id"] = legacy
        });
        return reply.Success;
    }

    #endregion

    #region Trial (server side)

    /// <summary>What the server knows about this PC's trial. Reached=false when it could not be asked.</summary>
    internal async Task<TrialReply> CheckDemoAsync()
    {
        // hardware_hash too: register-device runs alongside and may not have landed yet, and the
        // server refuses a second trial on the same motherboard (e.g. after reinstalling Windows).
        var reply = await XmanApi.PostAsync(_http, "/demo/check", new Dictionary<string, object?>
        {
            ["machine_id"] = MachineId,
            ["hardware_hash"] = MachineIdentity.HardwareHash
        });
        if (!reply.IsDefinitive || !reply.Success) return TrialReply.NotReached;

        var data = reply.Data;
        var info = data.Obj("trial_info");
        bool active = data.Bool("is_trial_active") == true;
        return new TrialReply(
            Reached: true,
            HasUsed: data.Bool("has_used_demo") == true,
            CanStart: data.Bool("can_start_demo") == true,
            Remaining: active ? Seconds(info.Long("seconds_remaining")) : null,
            ErrorCode: null);
    }

    /// <summary>Starts this PC's one server-tracked trial.</summary>
    internal async Task<TrialReply> StartDemoAsync()
    {
        var reply = await XmanApi.PostAsync(_http, "/demo", new Dictionary<string, object?>
        {
            ["machine_id"] = MachineId,
            ["hardware_hash"] = MachineIdentity.HardwareHash
        });
        if (!reply.IsDefinitive) return TrialReply.NotReached;

        if (reply.Success)
            return new TrialReply(true, true, false, Seconds(reply.Data.Long("seconds_remaining")), null);

        // Asked twice (e.g. two starts racing): the running trial comes back with the refusal.
        var code = reply.ErrorCode;
        if (code == "TRIAL_ACTIVE")
            return new TrialReply(true, true, false, Seconds(reply.Json.Obj("trial_info").Long("seconds_remaining")), code);

        return new TrialReply(true, true, false, null, code ?? "TRIAL_NOT_AVAILABLE");
    }

    private static TimeSpan? Seconds(long? seconds) =>
        seconds is > 0 ? TimeSpan.FromSeconds(Math.Min(seconds.Value, (long)TimeSpan.FromDays(366).TotalSeconds)) : null;

    #endregion

    #region Saved state

    private void EnsureSavedLoaded()
    {
        if (_savedLoaded) return;
        lock (_loadLock)
        {
            if (_savedLoaded) return;
            _status = LoadSaved();
            _savedLoaded = true;
        }
    }

    private static LicenseStatus LoadSaved()
    {
        try
        {
            var saved = AdminOnlyStore.Load<SavedLicense>(StoreFile);
            if (saved != null && !string.IsNullOrEmpty(saved.Key))
            {
                if (saved.MachineId == MachineId && saved.Mac == Seal(saved))
                    return FromSaved(saved);

                // Written for another machine id (new motherboard, restored backup) or edited:
                // trust nothing but the key itself, so the user does not have to dig it out of
                // an old email — the server decides what it is worth.
                return new LicenseStatus { State = LicenseState.Unverified, LicenseKey = NormalizeKey(saved.Key) };
            }

            // A key typed into an older WinXTools: unverified until the server confirms it.
            var legacyKey = DatabaseService.Instance.GetSetting(LegacyDbKey);
            if (!string.IsNullOrWhiteSpace(legacyKey))
                return new LicenseStatus { State = LicenseState.Unverified, LicenseKey = NormalizeKey(legacyKey) };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Saved license unreadable: {ex.Message}");
        }
        return LicenseStatus.None;
    }

    private static LicenseStatus FromSaved(SavedLicense saved)
    {
        var state = Enum.TryParse<LicenseState>(saved.State, out var parsed) ? parsed : LicenseState.Unverified;
        var now = DateTime.UtcNow;

        // Active offline only inside the grace window, and not with a clock set back before the
        // last check (that would stretch the window forever).
        if (state == LicenseState.Active
            && (now < saved.VerifiedAtUtc - ClockTolerance || now > saved.VerifiedAtUtc + OfflineGrace))
        {
            state = LicenseState.Unverified;
        }

        return new LicenseStatus
        {
            State = state,
            LicenseKey = saved.Key,
            LicenseType = saved.Type,
            ExpiresAt = saved.ExpiresAtUtc,
            VerifiedAtUtc = saved.VerifiedAtUtc,
            IsFromSavedCopy = true
        };
    }

    private void SetStatus(LicenseState state, string key, string type, DateTimeOffset? expires, bool verified) =>
        SetStatus(state, key, type, expires?.UtcDateTime, verified);

    private void SetStatus(LicenseState state, string key, string type, DateTime? expiresUtc, bool verified)
    {
        var status = new LicenseStatus
        {
            State = state,
            LicenseKey = key,
            LicenseType = string.IsNullOrEmpty(type) ? "free" : type.ToLowerInvariant(),
            ExpiresAt = expiresUtc,
            VerifiedAtUtc = verified ? DateTime.UtcNow : _status.VerifiedAtUtc
        };
        _status = status;
        Persist(status);
        StatusChanged?.Invoke(status);
    }

    private static void Persist(LicenseStatus status)
    {
        var saved = new SavedLicense
        {
            Key = status.LicenseKey,
            Type = status.LicenseType,
            ExpiresAtUtc = status.ExpiresAt,
            VerifiedAtUtc = status.VerifiedAtUtc ?? DateTime.UtcNow,
            State = status.State.ToString(),
            MachineId = MachineId
        };
        saved.Mac = Seal(saved);

        try
        {
            if (AdminOnlyStore.Save(StoreFile, saved))
            {
                // One home for the key from now on.
                if (!string.IsNullOrEmpty(DatabaseService.Instance.GetSetting(LegacyDbKey)))
                    DatabaseService.Instance.SetSetting(LegacyDbKey, "");
            }
            else
            {
                // Not elevated (e.g. a debug run): keep at least the key where it always was.
                DatabaseService.Instance.SetSetting(LegacyDbKey, status.LicenseKey);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Saving license failed: {ex.Message}");
        }
    }

    // Binds the saved copy to this PC and makes a hand edit (e.g. "Type": "lifetime") void.
    // The user is an administrator of their own PC, so this deters tampering; it is not a wall.
    private static string Seal(SavedLicense s)
    {
        var secret = SHA256.HashData(Encoding.UTF8.GetBytes("winxtools-license-cache-v1|" + MachineId + "|" + MachineIdentity.LegacyMachineId));
        var text = $"{s.Key}|{s.Type}|{s.ExpiresAtUtc?.ToString("O")}|{s.VerifiedAtUtc:O}|{s.State}|{s.MachineId}";
        return Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(text)));
    }

    private sealed class SavedLicense
    {
        public string Key { get; set; } = "";
        public string Type { get; set; } = "";
        public DateTime? ExpiresAtUtc { get; set; }
        public DateTime VerifiedAtUtc { get; set; }
        public string State { get; set; } = "";
        public string MachineId { get; set; } = "";
        public string Mac { get; set; } = "";
    }

    #endregion

    #region Helpers

    private static LicenseResult Unanswered(ApiReply reply) =>
        LicenseResult.Fail(reply.Reached ? LicenseCode.ServerBusy : LicenseCode.Offline, reply.ServerMessage);

    public static string NormalizeKey(string key) =>
        Regex.Replace(key ?? "", @"\s+", "").ToUpperInvariant();

    /// <summary>Local sanity check before asking the server (keys look like XXXX-XXXX-XXXX-XXXX).</summary>
    public static bool LooksLikeKey(string key) =>
        key.Length is >= 8 and <= 64 && Regex.IsMatch(key, "^[A-Z0-9]+(-[A-Z0-9]+)+$");

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    #endregion
}

public enum LicenseState
{
    /// <summary>No key on this PC.</summary>
    None,
    Active,
    Expired,
    Revoked,
    /// <summary>The key is now activated on another PC.</summary>
    OtherMachine,
    /// <summary>A key is saved but the server has not confirmed it recently (offline too long, or never).</summary>
    Unverified
}

public sealed class LicenseStatus
{
    public static readonly LicenseStatus None = new();

    public LicenseState State { get; init; } = LicenseState.None;
    public string LicenseKey { get; init; } = "";
    public string LicenseType { get; init; } = "free";
    /// <summary>UTC; null = never expires.</summary>
    public DateTime? ExpiresAt { get; init; }
    /// <summary>When the server last confirmed this state (UTC).</summary>
    public DateTime? VerifiedAtUtc { get; init; }
    /// <summary>Loaded from the saved copy; the server has not answered yet this session.</summary>
    public bool IsFromSavedCopy { get; init; }

    /// <summary>Active, and — for a license with an end date — not past it (checked locally too).</summary>
    public bool IsActive => State == LicenseState.Active && (ExpiresAt == null || ExpiresAt > DateTime.UtcNow);

    public bool IsLifetime => string.Equals(LicenseType, "lifetime", StringComparison.OrdinalIgnoreCase);

    public int DaysRemaining => ExpiresAt is { } end ? Math.Max(0, (int)Math.Ceiling((end - DateTime.UtcNow).TotalDays)) : int.MaxValue;

    public string DisplayType => LicenseType switch
    {
        "lifetime" => "Pro (Lifetime)",
        "yearly" => "Pro (Yearly)",
        "monthly" => "Pro (Monthly)",
        "weekly" => "Pro (Weekly)",
        "daily" => "Pro (Daily)",
        "product" => "Pro",
        _ => "Free"
    };

    /// <summary>An active paid license. The trial is not premium; it is granted through <see cref="TrialService"/>.</summary>
    public bool IsPremium =>
        IsActive && LicenseType is "lifetime" or "yearly" or "monthly" or "weekly" or "daily" or "product";
}

public enum LicenseCode
{
    Ok,
    /// <summary>Not a WinXTools key (or mistyped).</summary>
    InvalidKey,
    Expired,
    Revoked,
    /// <summary>Activated on another PC; can be moved here if the user agrees.</summary>
    OtherDevice,
    /// <summary>The saved key was moved to another PC.</summary>
    NotOnThisMachine,
    /// <summary>A trial/demo key, not a Pro license.</summary>
    NotProKey,
    NoLicense,
    InvalidInput,
    /// <summary>No answer at all (offline, blocked, certificate pin mismatch).</summary>
    Offline,
    /// <summary>Answered with an error page, 5xx or rate limit.</summary>
    ServerBusy,
    Failed
}

public sealed class LicenseResult
{
    public bool Success { get; init; }
    public LicenseCode Code { get; init; }
    /// <summary>The server's own message (Thai); the UI shows its own text per <see cref="Code"/>.</summary>
    public string? ServerMessage { get; init; }

    public static LicenseResult Ok(string? message = null) => new() { Success = true, Code = LicenseCode.Ok, ServerMessage = message };
    public static LicenseResult Fail(LicenseCode code, string? message = null) => new() { Success = false, Code = code, ServerMessage = message };
}

/// <summary>The server's view of this PC's trial.</summary>
internal sealed record TrialReply(bool Reached, bool HasUsed, bool CanStart, TimeSpan? Remaining, string? ErrorCode)
{
    public static readonly TrialReply NotReached = new(false, false, false, null, null);
    public bool IsActive => Remaining is { } left && left > TimeSpan.Zero;
}

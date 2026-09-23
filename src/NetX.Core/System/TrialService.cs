using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using NetX.Core.Data;

namespace NetX.Core.System;

/// <summary>
/// The free Pro trial (48 hours, once per PC).
///
/// The xman studio server decides: it tracks the trial per PC (and per motherboard, so a
/// Windows reinstall does not start a new one) exactly as for the studio's other apps.
/// The local copy below only matters offline — it keeps a running trial counting down,
/// and on a PC that has never reached the server it can start the one trial locally.
/// </summary>
public class TrialService
{
    private static TrialService? _instance;
    public static TrialService Instance => _instance ??= new TrialService();

    private const double TrialHours = 48;
    private const string TrialStartKey = "TrialStartUtc";
    private const string TrialExpiresKey = "TrialExpiresUtc";
    private const string TrialHashKey = "TrialVerifyHash";
    private const string LastSeenTimeKey = "LastSeenUtc";

    // Registry backup — survives deleting the database. HKLM as well as HKCU (the app runs
    // elevated), so another Windows account on the same PC does not get a fresh trial either.
    private const string RegistryPath = @"SOFTWARE\xman\WXT";
    private const string RegFlagKey = "tf";    // trial flag (obfuscated name)
    private const string RegHashKey = "th";    // trial hash
    private const string RegExpiresKey = "te"; // trial expires (encrypted)

    private DateTime? _trialExpiresUtc;

    public event Action? OnTrialStatusChanged;

    public bool IsTrialActive { get; private set; }
    public TimeSpan TimeRemaining { get; private set; }
    public bool TrialExpired { get; private set; }
    public bool TrialNeverStarted { get; private set; } = true;
    /// <summary>The server will not give this PC a trial (already used on this hardware, or blocked).</summary>
    public bool TrialUnavailable { get; private set; }
    /// <summary>When the running trial ends (UTC); null when no trial is running.</summary>
    public DateTime? ExpiresAtUtc => IsTrialActive ? _trialExpiresUtc : null;

    /// <summary>
    /// True if user has Pro access (valid premium license OR active trial)
    /// </summary>
    public bool HasProAccess =>
        XmanLicenseService.Instance.CachedStatus.IsPremium || IsTrialActive;

    private TrialService() { }

    /// <summary>
    /// Shows a trial that is already running as running from the very first frame, instead of
    /// locking the Pro pages until the server has answered. Local data only, no network;
    /// <see cref="InitializeAsync"/> then confirms or corrects it.
    /// </summary>
    public void RestoreLocalState()
    {
        try
        {
            if (!TryReadLocalTrial(out _, out var expires) || expires <= DateTime.UtcNow) return;

            // A clock set back behind the last-seen watermark does not get the benefit of the doubt.
            var lastSeen = DatabaseService.Instance.GetSetting(LastSeenTimeKey);
            if (DateTime.TryParse(lastSeen, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var seen)
                && DateTime.UtcNow < seen.ToUniversalTime().AddMinutes(-5))
                return;

            _trialExpiresUtc = expires;
            IsTrialActive = true;
            TrialExpired = false;
            TrialNeverStarted = false;
            TimeRemaining = expires - DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Restoring the trial failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Initialize trial system. Call after license validation.
    /// </summary>
    public async Task InitializeAsync()
    {
        // If user already has a valid Pro license, no trial needed
        if (XmanLicenseService.Instance.CachedStatus.IsPremium)
        {
            IsTrialActive = false;
            TrialExpired = false;
            TrialNeverStarted = false;
            OnTrialStatusChanged?.Invoke();
            return;
        }

        var server = await XmanLicenseService.Instance.CheckDemoAsync();
        if (server.Reached)
        {
            if (server.IsActive)
            {
                ApplyServerTrial(server.Remaining!.Value);
                return;
            }

            if (server.HasUsed)
            {
                ExpireTrial();
                return;
            }

            // New to the server, but this PC already had its trial (an older WinXTools, or one
            // started offline) and it is over: don't spend a server trial just to cap it to zero.
            var localEnd = LocalTrialEnd(out var tamperedLocal);
            if (tamperedLocal || localEnd <= DateTime.UtcNow)
            {
                TrialNeverStarted = false;
                ExpireTrial();
                return;
            }

            if (server.CanStart)
            {
                var started = await XmanLicenseService.Instance.StartDemoAsync();
                if (started.IsActive)
                {
                    ApplyServerTrial(started.Remaining!.Value);
                    return;
                }
                if (started.Reached)
                {
                    Debug.WriteLine($"Trial refused: {started.ErrorCode}");
                    TrialUnavailable = true;
                    ExpireTrial();
                    return;
                }
                // Lost the connection between the two calls: fall through to the local copy.
            }
            else
            {
                // Never used, yet not allowed (the server flagged this PC).
                TrialUnavailable = true;
                ExpireTrial();
                return;
            }
        }

        await InitializeOfflineAsync();
    }

    /// <summary>
    /// When the trial this PC already had locally ends (database, else the registry backup);
    /// null when there is no trace of one. <paramref name="tampered"/>: data exists but was edited.
    /// </summary>
    private static DateTime? LocalTrialEnd(out bool tampered)
    {
        if (TryReadLocalTrial(out tampered, out var end)) return end;
        if (tampered) return null;
        if (!WasTrialUsedFromRegistry()) return null;
        return ReadRegistryExpires() ?? DateTime.MinValue; // used, end unknown: treat as over
    }

    /// <summary>The server's clock decides; the remaining time is applied to this PC's clock.</summary>
    private void ApplyServerTrial(TimeSpan remaining)
    {
        var now = DateTime.UtcNow;
        var expiresUtc = now + remaining;

        // A trial started locally while offline is the same trial: never extend it.
        if (LocalTrialEnd(out _) is { } localEnd && localEnd < expiresUtc)
            expiresUtc = localEnd;

        var db = DatabaseService.Instance;
        var start = db.GetSetting(TrialStartKey);
        var startUtc = !string.IsNullOrEmpty(start)
            && DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : now;

        StoreTrialData(startUtc, expiresUtc);
        TrialNeverStarted = false;
        UpdateStatus(expiresUtc, now);
    }

    private async Task InitializeOfflineAsync()
    {
        if (TryReadLocalTrial(out var tampered, out var expires))
        {
            TrialNeverStarted = false;
            _trialExpiresUtc = expires;
            await RefreshStatusAsync();
            return;
        }
        if (tampered)
        {
            TrialNeverStarted = false;
            ExpireTrial();
            return;
        }

        // DB has no trial data — check registry backup before starting new trial
        if (WasTrialUsedFromRegistry())
        {
            // Trial was already used on this machine (DB was deleted to cheat)
            TrialNeverStarted = false;

            // Try to restore expiry from registry
            var regExpires = ReadRegistryExpires();
            if (regExpires.HasValue && regExpires.Value > DateTime.UtcNow)
            {
                // Trial still valid — restore it (start watermark = now)
                StoreTrialData(DateTime.UtcNow, regExpires.Value);
                UpdateStatus(regExpires.Value);
            }
            else
            {
                ExpireTrial();
            }
            return;
        }

        // First launch and the license server is out of reach: start the one trial locally,
        // timed by clocks the user does not control (NOT xman4289.com, which a hosts-file
        // entry could fake).
        var fallbackTime = await GetIndependentTimeAsync();
        if (fallbackTime.HasValue)
        {
            var expiresUtc = fallbackTime.Value.AddHours(TrialHours);
            StoreTrialData(fallbackTime.Value, expiresUtc);
            TrialNeverStarted = false;
            UpdateStatus(expiresUtc, fallbackTime.Value);
            return;
        }

        // Completely offline on first launch — cannot start trial
        TrialNeverStarted = true;
        OnTrialStatusChanged?.Invoke();
    }

    /// <summary>
    /// The trial saved in the database, if any and untampered. <paramref name="tampered"/> is
    /// true when data exists but its hash does not match.
    /// </summary>
    private static bool TryReadLocalTrial(out bool tampered, out DateTime expiresUtc)
    {
        tampered = false;
        expiresUtc = default;

        var db = DatabaseService.Instance;
        var storedStart = db.GetSetting(TrialStartKey);
        var storedExpires = db.GetSetting(TrialExpiresKey);
        if (string.IsNullOrEmpty(storedExpires) || string.IsNullOrEmpty(storedStart)) return false;

        if (db.GetSetting(TrialHashKey) != ComputeHash(storedStart, storedExpires)
            || !DateTime.TryParse(storedExpires, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expiresUtc))
        {
            tampered = true;
            return false;
        }
        expiresUtc = expiresUtc.ToUniversalTime();
        return true;
    }

    private async Task RefreshStatusAsync()
    {
        if (_trialExpiresUtc == null) return;

        var now = DateTime.UtcNow;

        // Anti-rollback: if local clock is significantly behind last seen time, expire
        var lastSeenStr = DatabaseService.Instance.GetSetting(LastSeenTimeKey);
        if (!string.IsNullOrEmpty(lastSeenStr)
            && DateTime.TryParse(lastSeenStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastSeen))
        {
            if (now < lastSeen.AddMinutes(-5)) // 5 min tolerance for clock drift
            {
                // Cross-check with independent source before expiring
                var independentTime = await GetIndependentTimeAsync();
                if (independentTime.HasValue && independentTime.Value < lastSeen.AddMinutes(-5))
                {
                    // Independent source also behind — clock was truly rolled back
                    ExpireTrial();
                    return;
                }
                else if (!independentTime.HasValue)
                {
                    // Can't verify — assume clock rolled back to be safe
                    ExpireTrial();
                    return;
                }
                // Independent source says time is fine — local clock glitch, continue
                now = independentTime.Value;
            }
        }

        var independentTimeCheck = await GetIndependentTimeAsync();
        var currentTime = independentTimeCheck ?? now;

        // Update last-seen watermark
        DatabaseService.Instance.SetSetting(LastSeenTimeKey, currentTime.ToString("O"));

        UpdateStatus(_trialExpiresUtc.Value, currentTime);
    }

    /// <summary>
    /// Called every second by UI timer to update countdown.
    /// Lightweight — no DB or network access.
    /// </summary>
    public void Tick()
    {
        if (_trialExpiresUtc == null || !IsTrialActive) return;

        var remaining = _trialExpiresUtc.Value - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            ExpireTrial();
        }
        else
        {
            TimeRemaining = remaining;
        }
    }

    private void UpdateStatus(DateTime expiresUtc, DateTime? currentUtc = null)
    {
        var now = currentUtc ?? DateTime.UtcNow;
        var remaining = expiresUtc - now;
        _trialExpiresUtc = expiresUtc;

        if (remaining > TimeSpan.Zero)
        {
            IsTrialActive = true;
            TrialExpired = false;
            TimeRemaining = remaining;
        }
        else
        {
            IsTrialActive = false;
            TrialExpired = true;
            TimeRemaining = TimeSpan.Zero;
            WriteRegistryFlag();
        }

        OnTrialStatusChanged?.Invoke();
    }

    private void ExpireTrial()
    {
        IsTrialActive = false;
        TrialExpired = true;
        TimeRemaining = TimeSpan.Zero;
        WriteRegistryFlag(); // Ensure registry records trial as used
        OnTrialStatusChanged?.Invoke();
    }

    private void StoreTrialData(DateTime startUtc, DateTime expiresUtc)
    {
        var startStr = startUtc.ToString("O");
        var expiresStr = expiresUtc.ToString("O");
        var hash = ComputeHash(startStr, expiresStr);

        var db = DatabaseService.Instance;
        db.SetSetting(TrialStartKey, startStr);
        db.SetSetting(TrialExpiresKey, expiresStr);
        db.SetSetting(TrialHashKey, hash);
        db.SetSetting(LastSeenTimeKey, DateTime.UtcNow.ToString("O"));

        _trialExpiresUtc = expiresUtc;

        // Write registry backup (survives DB deletion)
        WriteRegistryBackup(hash, expiresUtc);
    }

    #region Anti-Cheat: Registry Backup

    // Opened once and kept for the life of the process (base keys are cheap handles).
    private static readonly Lazy<RegistryKey?> LocalMachine64 = new(() =>
    {
        try { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); }
        catch (Exception ex) { Debug.WriteLine($"HKLM unavailable: {ex.Message}"); return null; }
    });

    private static IEnumerable<RegistryKey> RegistryRoots()
    {
        yield return Registry.CurrentUser;
        if (LocalMachine64.Value is { } hklm) yield return hklm;
    }

    /// <summary>
    /// Write trial state to Windows Registry — survives DB deletion
    /// </summary>
    private static void WriteRegistryBackup(string hash, DateTime expiresUtc)
    {
        // Encrypt expiry with machine-specific key so it can't be copied between machines
        var encryptedExpiry = EncryptForMachine(expiresUtc.ToString("O"));
        foreach (var root in RegistryRoots())
        {
            try
            {
                using var key = root.CreateSubKey(RegistryPath);
                if (key == null) continue;
                key.SetValue(RegFlagKey, "1", RegistryValueKind.String);
                key.SetValue(RegHashKey, hash, RegistryValueKind.String);
                key.SetValue(RegExpiresKey, encryptedExpiry, RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Registry backup write failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Write only the trial-used flag (when we don't have full data)
    /// </summary>
    private static void WriteRegistryFlag()
    {
        foreach (var root in RegistryRoots())
        {
            try
            {
                using var key = root.CreateSubKey(RegistryPath);
                key?.SetValue(RegFlagKey, "1", RegistryValueKind.String);
            }
            catch { }
        }
    }

    /// <summary>
    /// Check if trial was already used on this machine (from registry)
    /// </summary>
    private static bool WasTrialUsedFromRegistry()
    {
        foreach (var root in RegistryRoots())
        {
            try
            {
                using var key = root.OpenSubKey(RegistryPath);
                if (key?.GetValue(RegFlagKey)?.ToString() == "1") return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// Read encrypted trial expiry from registry (the earliest one wins)
    /// </summary>
    private static DateTime? ReadRegistryExpires()
    {
        DateTime? earliest = null;
        foreach (var root in RegistryRoots())
        {
            try
            {
                using var key = root.OpenSubKey(RegistryPath);
                var encrypted = key?.GetValue(RegExpiresKey)?.ToString();
                if (string.IsNullOrEmpty(encrypted)) continue;

                var decrypted = DecryptForMachine(encrypted);
                if (string.IsNullOrEmpty(decrypted)) continue;

                var value = DateTime.Parse(decrypted, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
                if (earliest == null || value < earliest) earliest = value;
            }
            catch { }
        }
        return earliest;
    }

    // Keyed on the id older versions used, so trial data they wrote still verifies.
    private static byte[] MachineSecret() =>
        Encoding.UTF8.GetBytes($"WXT-{MachineIdentity.LegacyMachineId}-trial-enc");

    /// <summary>
    /// Encrypt string using machine-specific key (DPAPI-like approach with SHA256)
    /// </summary>
    private static string EncryptForMachine(string plaintext)
    {
        var machineKey = MachineSecret();
        var iv = SHA256.HashData(machineKey)[..16]; // First 16 bytes as IV
        var key = SHA256.HashData(machineKey);       // Full 32 bytes as key

        using var aes = global::System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.IV = iv;

        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypt machine-specific encrypted string
    /// </summary>
    private static string? DecryptForMachine(string ciphertext)
    {
        try
        {
            var machineKey = MachineSecret();
            var iv = SHA256.HashData(machineKey)[..16];
            var key = SHA256.HashData(machineKey);

            using var aes = global::System.Security.Cryptography.Aes.Create();
            aes.Key = key;
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            var cipherBytes = Convert.FromBase64String(ciphertext);
            var decrypted = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Anti-Cheat: Time Sources

    /// <summary>
    /// Get time from independent sources NOT controlled by xman4289.com.
    /// This detects hosts-file spoofing — even if xman4289.com is redirected,
    /// Google/Cloudflare/Microsoft cannot all be spoofed simultaneously.
    /// </summary>
    private static async Task<DateTime?> GetIndependentTimeAsync()
    {
        // Multiple independent sources — attacker cannot spoof all of them
        var sources = new[]
        {
            "https://www.google.com",
            "https://www.cloudflare.com",
            "https://www.microsoft.com",
            "https://www.apple.com"
        };

        // Check multiple sources in parallel
        var tasks = sources.Select(async url =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                if (response.Headers.Date.HasValue)
                    return response.Headers.Date.Value.UtcDateTime;
            }
            catch { }
            return (DateTime?)null;
        });

        var results = await Task.WhenAll(tasks);
        var times = results.Where(r => r.HasValue).Select(r => r!.Value).ToList();
        if (times.Count == 0) return null;

        // Use median time (resists outliers from one spoofed source)
        times.Sort();
        return times[times.Count / 2];
    }

    #endregion

    #region Pro Page Gating

    // Pro-only page tags
    private static readonly HashSet<string> ProOnlyPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ProxyVpn", "Rules", "Tricks", "NetworkTools", "Network", "Packets"
    };

    public static bool IsProOnlyPage(string? pageTag) =>
        pageTag != null && ProOnlyPages.Contains(pageTag);

    public string FormatTimeRemaining()
    {
        var t = TimeRemaining;
        if (t.TotalHours >= 24)
            return $"{(int)t.TotalDays}d {t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
    }

    #endregion

    private static string ComputeHash(string start, string expires)
    {
        var data = $"{start}|{expires}|{MachineIdentity.LegacyMachineId}|xman-WinXTools-7f3a";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(bytes);
    }
}

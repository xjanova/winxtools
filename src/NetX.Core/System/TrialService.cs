using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using NetX.Core.Data;

namespace NetX.Core.System;

public class TrialService
{
    private static TrialService? _instance;
    public static TrialService Instance => _instance ??= new TrialService();

    private const double TrialHours = 48;
    // Anti-spoof ceiling only — blocks absurd future expiries from a spoofed
    // server without truncating a legitimate multi-day server demo (e.g. 3 days).
    private const double MaxDemoDays = 31;
    private const string TrialStartKey = "TrialStartUtc";
    private const string TrialExpiresKey = "TrialExpiresUtc";
    private const string TrialHashKey = "TrialVerifyHash";
    private const string LastSeenTimeKey = "LastSeenUtc";

    // Registry backup — survives DB deletion
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

    /// <summary>
    /// True if user has Pro access (valid premium license OR active trial)
    /// </summary>
    public bool HasProAccess =>
        XmanLicenseService.Instance.CachedStatus.IsPremium || IsTrialActive;

    private TrialService() { }

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
            return;
        }

        var db = DatabaseService.Instance;
        var storedStart = db.GetSetting(TrialStartKey);
        var storedExpires = db.GetSetting(TrialExpiresKey);
        var storedHash = db.GetSetting(TrialHashKey);

        if (!string.IsNullOrEmpty(storedExpires) && !string.IsNullOrEmpty(storedStart))
        {
            // Trial was started before — verify and refresh
            TrialNeverStarted = false;

            // Anti-tamper: verify hash (covers both DB and registry)
            var expectedHash = ComputeHash(storedStart, storedExpires);
            if (storedHash != expectedHash)
            {
                ExpireTrial();
                return;
            }

            _trialExpiresUtc = DateTime.Parse(storedExpires, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            await RefreshStatusAsync();
        }
        else
        {
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
                    var startUtc = DateTime.UtcNow;
                    StoreTrialData(startUtc, regExpires.Value);
                    UpdateStatus(regExpires.Value);
                }
                else
                {
                    // Trial expired — don't restart
                    ExpireTrial();
                }
                return;
            }

            // Genuinely first launch — auto-start trial
            await StartTrialAsync();
        }
    }

    private async Task StartTrialAsync()
    {
        // Double-check registry (race condition guard)
        if (WasTrialUsedFromRegistry())
        {
            TrialNeverStarted = false;
            ExpireTrial();
            return;
        }

        // Try server demo endpoint (server tracks per machine_id)
        var result = await XmanLicenseService.Instance.StartDemoAsync();
        var status = XmanLicenseService.Instance.CachedStatus;

        if (result.Success && status.ExpiresAt.HasValue)
        {
            var expiresUtc = status.ExpiresAt.Value.ToUniversalTime();

            // Cross-validate: check independent time source to detect spoofed server
            var independentTime = await GetIndependentTimeAsync();
            if (independentTime.HasValue)
            {
                // Only guard against absurd expiries (spoofed server). A real
                // multi-day demo must pass through untouched.
                var maxExpiry = independentTime.Value.AddDays(MaxDemoDays);
                if (expiresUtc > maxExpiry)
                {
                    Debug.WriteLine("Trial expiry exceeds sane maximum — possible spoofed server, capping.");
                    expiresUtc = maxExpiry;
                }
            }

            // Start watermark must be ~now, NOT expires-minus-48h; otherwise a
            // >48h demo stores a future LastSeen and false-trips the rollback guard.
            var startUtc = independentTime ?? DateTime.UtcNow;
            StoreTrialData(startUtc, expiresUtc);
            TrialNeverStarted = false;
            UpdateStatus(expiresUtc);
            return;
        }

        // Server rejected but NOT due to network error → demo already used on this machine
        if (!result.Success && !result.Message.Contains("connect", global::System.StringComparison.OrdinalIgnoreCase))
        {
            TrialNeverStarted = false;
            WriteRegistryFlag(); // Mark trial as used even without full data
            ExpireTrial();
            return;
        }

        // Network failure — use independent time sources (NOT xman4289.com)
        // This prevents the hosts-file attack from providing a fake time
        var fallbackTime = await GetIndependentTimeAsync();
        if (fallbackTime.HasValue)
        {
            var expiresUtc = fallbackTime.Value.AddHours(TrialHours);
            StoreTrialData(fallbackTime.Value, expiresUtc);
            TrialNeverStarted = false;
            UpdateStatus(expiresUtc);
            return;
        }

        // Completely offline on first launch — cannot start trial
        TrialNeverStarted = true;
    }

    private async Task RefreshStatusAsync()
    {
        if (_trialExpiresUtc == null) return;

        var now = DateTime.UtcNow;

        // Anti-rollback: if local clock is significantly behind last seen time, expire
        var lastSeenStr = DatabaseService.Instance.GetSetting(LastSeenTimeKey);
        if (!string.IsNullOrEmpty(lastSeenStr))
        {
            var lastSeen = DateTime.Parse(lastSeenStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
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

        // Get time from multiple sources for cross-validation
        var xmanTime = await GetServerTimeFromHeaderAsync();
        var independentTimeCheck = await GetIndependentTimeAsync();

        DateTime currentTime;
        if (xmanTime.HasValue && independentTimeCheck.HasValue)
        {
            var diff = Math.Abs((xmanTime.Value - independentTimeCheck.Value).TotalMinutes);
            if (diff > 10)
            {
                // xman4289.com time differs significantly from independent source
                // Likely spoofed — trust independent source
                Debug.WriteLine($"Time discrepancy: xman={xmanTime.Value:O} independent={independentTimeCheck.Value:O} diff={diff:F0}min");
                currentTime = independentTimeCheck.Value;
            }
            else
            {
                currentTime = xmanTime.Value;
            }
        }
        else if (independentTimeCheck.HasValue)
        {
            currentTime = independentTimeCheck.Value;
        }
        else if (xmanTime.HasValue)
        {
            currentTime = xmanTime.Value;
        }
        else
        {
            currentTime = now;
        }

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
        db.SetSetting(LastSeenTimeKey, startStr);

        _trialExpiresUtc = expiresUtc;

        // Write registry backup (survives DB deletion)
        WriteRegistryBackup(hash, expiresUtc);
    }

    #region Anti-Cheat: Registry Backup

    /// <summary>
    /// Write trial state to Windows Registry — survives DB deletion
    /// </summary>
    private static void WriteRegistryBackup(string hash, DateTime expiresUtc)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            if (key == null) return;

            key.SetValue(RegFlagKey, "1", RegistryValueKind.String);
            key.SetValue(RegHashKey, hash, RegistryValueKind.String);

            // Encrypt expiry with machine-specific key so it can't be copied between machines
            var encryptedExpiry = EncryptForMachine(expiresUtc.ToString("O"));
            key.SetValue(RegExpiresKey, encryptedExpiry, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Registry backup write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Write only the trial-used flag (when we don't have full data)
    /// </summary>
    private static void WriteRegistryFlag()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key?.SetValue(RegFlagKey, "1", RegistryValueKind.String);
        }
        catch { }
    }

    /// <summary>
    /// Check if trial was already used on this machine (from registry)
    /// </summary>
    private static bool WasTrialUsedFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue(RegFlagKey)?.ToString() == "1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Read encrypted trial expiry from registry
    /// </summary>
    private static DateTime? ReadRegistryExpires()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            var encrypted = key?.GetValue(RegExpiresKey)?.ToString();
            if (string.IsNullOrEmpty(encrypted)) return null;

            var decrypted = DecryptForMachine(encrypted);
            if (string.IsNullOrEmpty(decrypted)) return null;

            return DateTime.Parse(decrypted, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Encrypt string using machine-specific key (DPAPI-like approach with SHA256)
    /// </summary>
    private static string EncryptForMachine(string plaintext)
    {
        var machineKey = Encoding.UTF8.GetBytes(
            $"WXT-{AutoUpdateService.GenerateMachineId()}-trial-enc");
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
            var machineKey = Encoding.UTF8.GetBytes(
                $"WXT-{AutoUpdateService.GenerateMachineId()}-trial-enc");
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
    /// Get time from xman4289.com HTTP Date header
    /// </summary>
    private static async Task<DateTime?> GetServerTimeFromHeaderAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("User-Agent", "WinXTools-License");
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://xman4289.com"));
            if (response.Headers.Date.HasValue)
                return response.Headers.Date.Value.UtcDateTime;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"xman server time failed: {ex.Message}");
        }

        return null;
    }

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

        var times = new List<DateTime>();

        // Check multiple sources in parallel
        var tasks = sources.Select(async url =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                if (response.Headers.Date.HasValue)
                    return response.Headers.Date.Value.UtcDateTime;
            }
            catch { }
            return (DateTime?)null;
        });

        var results = await Task.WhenAll(tasks);
        times.AddRange(results.Where(r => r.HasValue).Select(r => r!.Value));

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
        var data = $"{start}|{expires}|{AutoUpdateService.GenerateMachineId()}|xman-WinXTools-7f3a";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(bytes);
    }
}

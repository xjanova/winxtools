using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace NetX.Core.System;

public class XmanLicenseService
{
    private static XmanLicenseService? _instance;
    public static XmanLicenseService Instance => _instance ??= new XmanLicenseService();

    // Real xman studio license API (Laravel). Routes: /activate /validate
    // /deactivate /demo — all POST, product selected by the "product" field.
    private const string ApiBase = "https://xman4289.com/api/v1/license";
    private const string Product = "winx-tools";

    private readonly HttpClient _httpClient;
    private readonly string _machineId;

    private LicenseStatus _cachedStatus = new();

    public event Action<LicenseStatus>? OnLicenseValidated;

    // SPKI (SubjectPublicKeyInfo) SHA256 pins for xman4289.com.
    // Prevents hosts-file redirect attacks even with a custom root CA installed.
    // The chain is validated if EITHER the leaf OR the intermediate matches, so
    // the leaf can rotate (~every 3 months) without breaking the app as long as
    // Google Trust Services WE1 remains the issuer.
    // Regenerate leaf: openssl s_client -connect xman4289.com:443 | openssl x509 -pubkey -noout | openssl pkey -pubin -outform DER | openssl dgst -sha256
    private static readonly HashSet<string> PinnedPublicKeyHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Leaf certificate (xman4289.com — rotates ~every 3 months; verified 2026-07-16)
        "C6DB13A55445F01F84B99A0E07BE76AE0DB6D6F37536CDDC86D4FD415DEC6F68",
        // Intermediate CA (Google Trust Services WE1 — stable, valid until 2029)
        "908769E8D34477CC2CBA0632C88605B22D7294C0840F78596D247C645B1AFC0E"
    };

    private XmanLicenseService()
    {
        var handler = new HttpClientHandler();

        // Certificate pinning: reject connections with unexpected certificates
        if (PinnedPublicKeyHashes.Count > 0)
        {
            handler.ServerCertificateCustomValidationCallback = ValidateServerCertificate;
        }

        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "WinXTools-License");
        // Laravel returns JSON validation errors only when the client asks for JSON.
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _machineId = AutoUpdateService.GenerateMachineId();
    }

    /// <summary>
    /// Validate server certificate against pinned SPKI hashes.
    /// Blocks hosts-file spoofing even with custom root CA installed.
    /// Checks leaf cert AND all intermediate certs in the chain against pins.
    /// </summary>
    private static bool ValidateServerCertificate(
        HttpRequestMessage request,
        X509Certificate2? cert,
        X509Chain? chain,
        SslPolicyErrors sslErrors)
    {
        // Standard SSL validation must pass first
        if (sslErrors != SslPolicyErrors.None) return false;
        if (cert == null) return false;

        // Check leaf certificate SPKI hash
        var leafSpki = cert.PublicKey.ExportSubjectPublicKeyInfo();
        var leafHash = Convert.ToHexString(SHA256.HashData(leafSpki));
        if (PinnedPublicKeyHashes.Contains(leafHash)) return true;

        // Check intermediate certificates in the chain (for rotation resilience)
        if (chain?.ChainElements != null)
        {
            foreach (var element in chain.ChainElements)
            {
                var spki = element.Certificate.PublicKey.ExportSubjectPublicKeyInfo();
                var hash = Convert.ToHexString(SHA256.HashData(spki));
                if (PinnedPublicKeyHashes.Contains(hash)) return true;
            }
        }

        return false;
    }

    public LicenseStatus CachedStatus => _cachedStatus;

    /// <summary>
    /// The stable machine fingerprint this device presents to the license server.
    /// </summary>
    public string MachineId => _machineId;

    /// <summary>
    /// Activate a license key on this machine.
    /// </summary>
    public async Task<LicenseResult> ActivateAsync(string licenseKey)
    {
        try
        {
            var request = new
            {
                product = Product,
                license_key = licenseKey.Trim(),
                machine_id = _machineId,
                machine_fingerprint = _machineId,
                device_name = Environment.MachineName,
                app_version = AutoUpdateService.GetCurrentVersion()
            };

            var response = await _httpClient.PostAsJsonAsync($"{ApiBase}/activate", request);
            var result = await ReadResponseAsync(response);

            if (result?.Success == true)
            {
                ApplyStatus(licenseKey.Trim(), result);
                return new LicenseResult { Success = true, Message = FallbackMessage(result, "License activated") };
            }

            return new LicenseResult { Success = false, Message = FallbackMessage(result, "Activation failed") };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"License activation failed: {ex.Message}");
            return new LicenseResult { Success = false, Message = "Could not connect to license server" };
        }
    }

    /// <summary>
    /// Validate an existing license key.
    /// </summary>
    public async Task<LicenseResult> ValidateAsync(string licenseKey)
    {
        try
        {
            var request = new
            {
                product = Product,
                license_key = licenseKey.Trim(),
                machine_id = _machineId,
                machine_fingerprint = _machineId,
                app_version = AutoUpdateService.GetCurrentVersion()
            };

            var response = await _httpClient.PostAsJsonAsync($"{ApiBase}/validate", request);
            var result = await ReadResponseAsync(response);

            if (result?.Success == true)
            {
                ApplyStatus(licenseKey.Trim(), result);
                return new LicenseResult { Success = true, Message = FallbackMessage(result, "License valid") };
            }

            // Only clear the cached status when the server actively says it's invalid
            // (not on a transient error), so a paid user isn't downgraded on a blip.
            _cachedStatus = new LicenseStatus();
            return new LicenseResult { Success = false, Message = FallbackMessage(result, "License invalid") };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"License validation failed: {ex.Message}");
            // Keep cached status on network failure (offline grace period)
            return new LicenseResult { Success = _cachedStatus.IsActive, Message = "Offline - using cached license" };
        }
    }

    /// <summary>
    /// Deactivate license from this machine.
    /// </summary>
    public async Task<LicenseResult> DeactivateAsync(string licenseKey)
    {
        try
        {
            var request = new
            {
                product = Product,
                license_key = licenseKey.Trim(),
                machine_id = _machineId,
                machine_fingerprint = _machineId
            };

            var response = await _httpClient.PostAsJsonAsync($"{ApiBase}/deactivate", request);
            var result = await ReadResponseAsync(response);

            _cachedStatus = new LicenseStatus();
            return new LicenseResult
            {
                Success = result?.Success ?? false,
                Message = FallbackMessage(result, "Deactivated")
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"License deactivation failed: {ex.Message}");
            _cachedStatus = new LicenseStatus();
            return new LicenseResult { Success = false, Message = "Could not connect to license server" };
        }
    }

    /// <summary>
    /// Start a demo/trial license for this machine (server tracks per fingerprint).
    /// </summary>
    public async Task<LicenseResult> StartDemoAsync()
    {
        try
        {
            var request = new
            {
                product = Product,
                machine_id = _machineId,
                machine_fingerprint = _machineId,
                device_name = Environment.MachineName,
                app_version = AutoUpdateService.GetCurrentVersion()
            };

            var response = await _httpClient.PostAsJsonAsync($"{ApiBase}/demo", request);
            var result = await ReadResponseAsync(response);

            if (result?.Success == true)
            {
                _cachedStatus = new LicenseStatus
                {
                    IsActive = true,
                    LicenseKey = "DEMO",
                    LicenseType = result.Data?.EffectiveType ?? "demo",
                    ExpiresAt = result.Data?.ExpiresAt,
                    DaysRemaining = result.Data?.DaysRemaining ?? 0
                };
                OnLicenseValidated?.Invoke(_cachedStatus);
                return new LicenseResult { Success = true, Message = FallbackMessage(result, "Demo started") };
            }

            return new LicenseResult { Success = false, Message = FallbackMessage(result, "Demo unavailable") };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Demo start failed: {ex.Message}");
            return new LicenseResult { Success = false, Message = "Could not connect to license server" };
        }
    }

    /// <summary>
    /// Device registration. The current server has no dedicated register-device
    /// endpoint — device details are recorded during activate/demo — so this is
    /// a no-op kept for API compatibility with callers.
    /// </summary>
    public Task RegisterDeviceAsync() => Task.CompletedTask;

    /// <summary>
    /// Check if a license key format is valid (local check only)
    /// </summary>
    public static bool IsValidKeyFormat(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        key = key.Trim().ToUpperInvariant();
        // Accept: WXT-XXXX-XXXX-XXXX or WINX-XXXX-XXXX-XXXX
        return key.StartsWith("WXT-") || key.StartsWith("WINX-");
    }

    #region Response handling

    private void ApplyStatus(string licenseKey, LicenseApiResponse result)
    {
        _cachedStatus = new LicenseStatus
        {
            IsActive = true,
            LicenseKey = licenseKey,
            LicenseType = result.Data?.EffectiveType ?? "free",
            ExpiresAt = result.Data?.ExpiresAt,
            DaysRemaining = result.Data?.DaysRemaining ?? 0
        };
        OnLicenseValidated?.Invoke(_cachedStatus);
    }

    private static async Task<LicenseApiResponse?> ReadResponseAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<LicenseApiResponse>();
        }
        catch
        {
            // Non-JSON body (e.g. an HTML 503 page) — treat as a failed call.
            return null;
        }
    }

    private static string FallbackMessage(LicenseApiResponse? result, string fallback)
    {
        var msg = result?.BestMessage();
        return string.IsNullOrWhiteSpace(msg) ? fallback : msg!;
    }

    #endregion
}

public class LicenseStatus
{
    public bool IsActive { get; set; }
    public string LicenseKey { get; set; } = "";
    public string LicenseType { get; set; } = "free";
    public DateTime? ExpiresAt { get; set; }
    public int DaysRemaining { get; set; }

    public string DisplayType => LicenseType switch
    {
        "lifetime" => "Lifetime",
        "yearly" => "Pro (Yearly)",
        "monthly" => "Pro (Monthly)",
        "weekly" => "Pro (Weekly)",
        "daily" => "Pro (Daily)",
        "demo" => "Demo",
        "free" => "Free",
        _ => "Free"
    };

    /// <summary>
    /// True for an active PAID license of any tier. Demo/free are not premium
    /// here — demo Pro access is granted through the trial system instead.
    /// </summary>
    public bool IsPremium =>
        IsActive && LicenseType is "lifetime" or "yearly" or "monthly" or "weekly" or "daily";
}

public class LicenseResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// Envelope returned by the xman studio license API. Success responses nest the
/// license details under "data"; errors use "error"+"code", and Laravel field
/// validation uses "message"+"errors".
/// </summary>
internal class LicenseApiResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("is_valid")]
    public bool? IsValid { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("data")]
    public LicenseData? Data { get; set; }

    [JsonPropertyName("errors")]
    public Dictionary<string, List<string>>? Errors { get; set; }

    /// <summary>Best human-readable message across the possible envelopes.</summary>
    public string BestMessage()
    {
        if (!string.IsNullOrWhiteSpace(Message)) return Message!;
        if (!string.IsNullOrWhiteSpace(Error)) return Error!;
        if (Errors is { Count: > 0 })
            return string.Join("\n", Errors.SelectMany(kv => kv.Value));
        return "";
    }
}

internal class LicenseData
{
    // Server returns "type"; keep "license_type" as a fallback for older shapes.
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("license_type")]
    public string? LicenseType { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTime? ExpiresAt { get; set; }

    [JsonPropertyName("days_remaining")]
    public int DaysRemaining { get; set; }

    [JsonPropertyName("already_started")]
    public bool AlreadyStarted { get; set; }

    public string EffectiveType => Type ?? LicenseType ?? "free";
}

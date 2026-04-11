using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NetX.Core.System;

public class XmanLicenseService
{
    private static XmanLicenseService? _instance;
    public static XmanLicenseService Instance => _instance ??= new XmanLicenseService();

    private const string ApiBase = "https://xman4289.com/api/v1/products/winxtools";
    private readonly HttpClient _httpClient;
    private readonly string _machineId;

    private LicenseStatus _cachedStatus = new();

    public event Action<LicenseStatus>? OnLicenseValidated;

    private XmanLicenseService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "WinXTools-License");
        _machineId = AutoUpdateService.GenerateMachineId();
    }

    public LicenseStatus CachedStatus => _cachedStatus;

    /// <summary>
    /// Activate a license key on this machine
    /// </summary>
    public async Task<LicenseResult> ActivateAsync(string licenseKey)
    {
        try
        {
            var request = new
            {
                license_key = licenseKey.Trim(),
                machine_id = _machineId,
                device_name = Environment.MachineName,
                app_version = AutoUpdateService.GetCurrentVersion()
            };

            var response = await _httpClient.PostAsJsonAsync($"{ApiBase}/activate", request);
            var result = await response.Content.ReadFromJsonAsync<LicenseApiResponse>();

            if (result?.Success == true)
            {
                _cachedStatus = new LicenseStatus
                {
                    IsActive = true,
                    LicenseKey = licenseKey.Trim(),
                    LicenseType = result.LicenseType ?? "free",
                    ExpiresAt = result.ExpiresAt,
                    DaysRemaining = result.DaysRemaining
                };
                OnLicenseValidated?.Invoke(_cachedStatus);
                return new LicenseResult { Success = true, Message = result.Message ?? "License activated" };
            }

            return new LicenseResult { Success = false, Message = result?.Message ?? "Activation failed" };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"License activation failed: {ex.Message}");
            return new LicenseResult { Success = false, Message = "Could not connect to license server" };
        }
    }

    /// <summary>
    /// Validate an existing license key
    /// </summary>
    public async Task<LicenseResult> ValidateAsync(string licenseKey)
    {
        try
        {
            var request = new
            {
                license_key = licenseKey.Trim(),
                machine_id = _machineId,
                app_version = AutoUpdateService.GetCurrentVersion()
            };

            var response = await _httpClient.PostAsJsonAsync($"{ApiBase}/validate", request);
            var result = await response.Content.ReadFromJsonAsync<LicenseApiResponse>();

            if (result?.Success == true)
            {
                _cachedStatus = new LicenseStatus
                {
                    IsActive = true,
                    LicenseKey = licenseKey.Trim(),
                    LicenseType = result.LicenseType ?? "free",
                    ExpiresAt = result.ExpiresAt,
                    DaysRemaining = result.DaysRemaining
                };
                OnLicenseValidated?.Invoke(_cachedStatus);
                return new LicenseResult { Success = true, Message = result.Message ?? "License valid" };
            }

            _cachedStatus = new LicenseStatus();
            return new LicenseResult { Success = false, Message = result?.Message ?? "License invalid" };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"License validation failed: {ex.Message}");
            // Keep cached status on network failure (grace period)
            return new LicenseResult { Success = _cachedStatus.IsActive, Message = "Offline - using cached license" };
        }
    }

    /// <summary>
    /// Deactivate license from this machine
    /// </summary>
    public async Task<LicenseResult> DeactivateAsync(string licenseKey)
    {
        try
        {
            var request = new
            {
                license_key = licenseKey.Trim(),
                machine_id = _machineId
            };

            var response = await _httpClient.PostAsJsonAsync($"{ApiBase}/deactivate", request);
            var result = await response.Content.ReadFromJsonAsync<LicenseApiResponse>();

            _cachedStatus = new LicenseStatus();
            return new LicenseResult
            {
                Success = result?.Success ?? false,
                Message = result?.Message ?? "Deactivated"
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
    /// Start a demo license
    /// </summary>
    public async Task<LicenseResult> StartDemoAsync()
    {
        try
        {
            var request = new
            {
                machine_id = _machineId,
                device_name = Environment.MachineName,
                app_version = AutoUpdateService.GetCurrentVersion()
            };

            var response = await _httpClient.PostAsJsonAsync($"{ApiBase}/demo", request);
            var result = await response.Content.ReadFromJsonAsync<LicenseApiResponse>();

            if (result?.Success == true)
            {
                _cachedStatus = new LicenseStatus
                {
                    IsActive = true,
                    LicenseKey = "DEMO",
                    LicenseType = "demo",
                    ExpiresAt = result.ExpiresAt,
                    DaysRemaining = result.DaysRemaining
                };
                OnLicenseValidated?.Invoke(_cachedStatus);
                return new LicenseResult { Success = true, Message = result.Message ?? "Demo started" };
            }

            return new LicenseResult { Success = false, Message = result?.Message ?? "Demo unavailable" };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Demo start failed: {ex.Message}");
            return new LicenseResult { Success = false, Message = "Could not connect to license server" };
        }
    }

    /// <summary>
    /// Register this device with the license server
    /// </summary>
    public async Task RegisterDeviceAsync()
    {
        try
        {
            var request = new
            {
                machine_id = _machineId,
                device_name = Environment.MachineName,
                os_version = Environment.OSVersion.VersionString,
                app_version = AutoUpdateService.GetCurrentVersion()
            };

            await _httpClient.PostAsJsonAsync($"{ApiBase}/register-device", request);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Device registration failed: {ex.Message}");
        }
    }

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
        "weekly" => "Weekly",
        "daily" => "Daily",
        "demo" => "Demo",
        "free" => "Free",
        _ => "Free"
    };

    public bool IsPremium => LicenseType is "lifetime" or "yearly" or "monthly";
}

public class LicenseResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

internal class LicenseApiResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("license_type")]
    public string? LicenseType { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTime? ExpiresAt { get; set; }

    [JsonPropertyName("days_remaining")]
    public int DaysRemaining { get; set; }
}

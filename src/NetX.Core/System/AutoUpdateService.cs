using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace NetX.Core.System;

public class AutoUpdateService
{
    private static AutoUpdateService? _instance;
    public static AutoUpdateService Instance => _instance ??= new AutoUpdateService();

    // Real xman studio version API: /api/v1/products/{slug}/... — slug is "winx-tools" (hyphen)
    private const string XmanApiBase = "https://xman4289.com/api/v1/products/winx-tools";
    private const string ProductPageUrl = "https://xman4289.com/products/winx-tools";
    private const string GitHubApiUrl = "https://api.github.com/repos/xjanova/winxtools/releases/latest";
    private const string ProductSlug = "winx-tools";

    private readonly HttpClient _httpClient;
    private readonly string _currentVersion;
    private readonly string _machineId;
    private bool _isDownloading;

    public event Action<int, string>? OnDownloadProgress;
    public event Action<string>? OnUpdateStatus;

    private AutoUpdateService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "WinXTools-AutoUpdate");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

        _currentVersion = GetCurrentVersion();
        _machineId = GenerateMachineId();
    }

    public string CurrentVersion => _currentVersion;
    public string MachineId => _machineId;

    /// <summary>
    /// Check for updates - tries xman API first, falls back to GitHub
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        // Try xman studio API first
        var info = await CheckXmanApiAsync();
        if (info != null) return info;

        // Fallback to GitHub
        return await CheckGitHubAsync();
    }

    private async Task<UpdateInfo?> CheckXmanApiAsync()
    {
        try
        {
            // check-update expects current_version WITHOUT a leading 'v' (server
            // ltrim's stored versions). license_key is optional (enhances can_download).
            var request = new
            {
                current_version = _currentVersion,
                machine_id = _machineId
            };

            var response = await _httpClient.PostAsJsonAsync($"{XmanApiBase}/check-update", request);
            // 404 = product has no published version yet -> fall back to GitHub.
            if (!response.IsSuccessStatusCode) return null;

            var result = await response.Content.ReadFromJsonAsync<XmanUpdateResponse>();
            if (result is not { Success: true }) return null;

            return new UpdateInfo
            {
                CurrentVersion = _currentVersion,
                LatestVersion = result.LatestVersion ?? result.Update?.Version ?? "",
                ReleaseNotes = result.Update?.Changelog ?? "",
                DownloadUrl = result.Update?.DownloadUrl ?? "",
                ReleaseUrl = ProductPageUrl,
                FileSize = result.Update?.FileSize ?? 0,
                PublishedAt = result.Update?.ReleasedAt,
                IsUpdateAvailable = result.HasUpdate,
                Source = "xman"
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Xman API check failed: {ex.Message}");
            return null;
        }
    }

    private async Task<UpdateInfo?> CheckGitHubAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<GitHubRelease>(GitHubApiUrl);
            if (response == null) return null;

            var latestVersionStr = response.TagName?.TrimStart('v') ?? "0.0.0";
            var versionParts = latestVersionStr.Split('-')[0];

            if (!Version.TryParse(versionParts, out var latestVersion))
                return null;

            if (!Version.TryParse(_currentVersion.Split('-')[0], out var currentVersion))
                currentVersion = new Version(0, 1, 0);

            bool hasUpdate = latestVersion > currentVersion;

            // Prefer .zip assets
            var asset = response.Assets?
                .FirstOrDefault(a => a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                ?? response.Assets?.FirstOrDefault();

            return new UpdateInfo
            {
                CurrentVersion = _currentVersion,
                LatestVersion = response.TagName ?? "unknown",
                ReleaseNotes = response.Body ?? "",
                DownloadUrl = asset?.BrowserDownloadUrl ?? response.HtmlUrl ?? "",
                ReleaseUrl = response.HtmlUrl ?? "",
                FileSize = asset?.Size ?? 0,
                PublishedAt = response.PublishedAt,
                IsUpdateAvailable = hasUpdate,
                Source = "github"
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GitHub check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Download and install the update
    /// </summary>
    public async Task<bool> DownloadAndInstallAsync(UpdateInfo updateInfo)
    {
        if (_isDownloading || string.IsNullOrEmpty(updateInfo.DownloadUrl))
            return false;

        _isDownloading = true;

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "WinXTools_Update");
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            var zipPath = Path.Combine(tempDir, "update.zip");

            // Download with progress
            OnUpdateStatus?.Invoke("Downloading update...");
            await DownloadFileAsync(updateInfo.DownloadUrl, zipPath, updateInfo.FileSize);

            // Extract
            OnUpdateStatus?.Invoke("Extracting update...");
            OnDownloadProgress?.Invoke(95, "Extracting...");

            var extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir, true);

            // Create update batch script
            OnUpdateStatus?.Invoke("Preparing installation...");
            var installDir = AppDomain.CurrentDomain.BaseDirectory;
            var batchPath = Path.Combine(tempDir, "update.bat");
            var exeName = Process.GetCurrentProcess().ProcessName + ".exe";

            var batchScript = $"""
                @echo off
                echo WinXTools Updater - Please wait...
                echo.
                :waitloop
                tasklist /FI "IMAGENAME eq {exeName}" 2>NUL | find /I "{exeName}" >NUL
                if not errorlevel 1 (
                    echo Waiting for WinXTools to close...
                    timeout /t 1 /nobreak >NUL
                    goto waitloop
                )
                echo Copying new files...
                xcopy /E /Y /I "{extractDir}\*" "{installDir}" >NUL 2>&1
                if errorlevel 1 (
                    echo Error copying files. Trying with elevated permissions...
                    timeout /t 2 /nobreak >NUL
                    xcopy /E /Y /I "{extractDir}\*" "{installDir}" >NUL 2>&1
                )
                echo Starting WinXTools...
                start "" "{Path.Combine(installDir, exeName)}"
                echo Cleaning up...
                rmdir /S /Q "{tempDir}" >NUL 2>&1
                exit
                """;

            File.WriteAllText(batchPath, batchScript);

            OnDownloadProgress?.Invoke(100, "Ready to install");
            OnUpdateStatus?.Invoke("Restarting to apply update...");

            // Launch updater and exit
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batchPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
                CreateNoWindow = false
            };
            Process.Start(psi);

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update download failed: {ex.Message}");
            OnUpdateStatus?.Invoke($"Update failed: {ex.Message}");
            return false;
        }
        finally
        {
            _isDownloading = false;
        }
    }

    private async Task DownloadFileAsync(string url, string filePath, long expectedSize)
    {
        // Use a dedicated client with longer timeout for file downloads
        using var downloadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        downloadClient.DefaultRequestHeaders.Add("User-Agent", "WinXTools-AutoUpdate");

        using var response = await downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
        var buffer = new byte[8192];
        long downloaded = 0;

        using var stream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            downloaded += bytesRead;

            if (totalBytes > 0)
            {
                int percent = (int)(downloaded * 90 / totalBytes); // 0-90%, leave 10% for extraction
                OnDownloadProgress?.Invoke(percent, $"Downloading... {downloaded / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB");
            }
        }
    }

    public static string GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (infoVersion != null)
        {
            // Remove build metadata (e.g., "0.1.0-beta+abc123")
            var plusIndex = infoVersion.IndexOf('+');
            return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    }

    public static string GenerateMachineId()
    {
        try
        {
            var raw = $"{Environment.MachineName}:{Environment.UserName}:{Environment.ProcessorCount}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return "unknown";
        }
    }
}

public class UpdateInfo
{
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime? PublishedAt { get; set; }
    public bool IsUpdateAvailable { get; set; }
    public string Source { get; set; } = "";
}

// xman studio version API response (VersionController@check).
// Fields are snake_case, so JsonPropertyName is required — the old model had none
// and silently parsed nothing.
internal class XmanUpdateResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("current_version")] public string? CurrentVersion { get; set; }
    [JsonPropertyName("latest_version")] public string? LatestVersion { get; set; }
    [JsonPropertyName("has_update")] public bool HasUpdate { get; set; }
    [JsonPropertyName("update")] public XmanUpdateData? Update { get; set; }
}

internal class XmanUpdateData
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("file_size")] public long FileSize { get; set; }
    [JsonPropertyName("changelog")] public string? Changelog { get; set; }
    [JsonPropertyName("released_at")] public DateTime? ReleasedAt { get; set; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
}

// GitHub API response models
internal class GitHubRelease
{
    public string? TagName { get; set; }
    public string? Name { get; set; }
    public string? Body { get; set; }
    public string? HtmlUrl { get; set; }
    public DateTime? PublishedAt { get; set; }
    public List<GitHubAsset>? Assets { get; set; }
}

internal class GitHubAsset
{
    public string? Name { get; set; }
    public string? BrowserDownloadUrl { get; set; }
    public long Size { get; set; }
}

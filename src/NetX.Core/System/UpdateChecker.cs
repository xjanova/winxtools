using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;

namespace NetX.Core.Optimization;

public class UpdateChecker
{
    private static UpdateChecker? _instance;
    public static UpdateChecker Instance => _instance ??= new UpdateChecker();

    private const string GitHubApiUrl = "https://api.github.com/repos/xjanova/winxtools/releases/latest";
    private readonly HttpClient _httpClient;
    private readonly Version _currentVersion;

    public event Action<UpdateInfo>? OnUpdateAvailable;

    private UpdateChecker()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "WinXTools-UpdateChecker");

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        _currentVersion = version ?? new Version(0, 1, 0);
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<GitHubRelease>(GitHubApiUrl);
            if (response == null) return null;

            var latestVersionStr = response.TagName?.TrimStart('v') ?? "0.0.0";
            // Handle semver with pre-release tags like "0.1.0-beta"
            var versionParts = latestVersionStr.Split('-')[0];
            if (!Version.TryParse(versionParts, out var latestVersion))
            {
                return null;
            }

            var updateInfo = new UpdateInfo
            {
                CurrentVersion = _currentVersion.ToString(3),
                LatestVersion = response.TagName ?? "unknown",
                ReleaseNotes = response.Body ?? "",
                DownloadUrl = response.Assets?.FirstOrDefault()?.BrowserDownloadUrl ?? response.HtmlUrl ?? "",
                ReleaseUrl = response.HtmlUrl ?? "",
                PublishedAt = response.PublishedAt,
                IsUpdateAvailable = latestVersion > _currentVersion
            };

            if (updateInfo.IsUpdateAvailable)
            {
                OnUpdateAvailable?.Invoke(updateInfo);
            }

            return updateInfo;
        }
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
            return null;
        }
    }

    public string GetCurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version?.ToString(3) ?? "0.1.0";
    }
}

public class UpdateInfo
{
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public DateTime? PublishedAt { get; set; }
    public bool IsUpdateAvailable { get; set; }
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

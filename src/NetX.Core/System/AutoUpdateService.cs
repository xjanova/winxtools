using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace NetX.Core.System;

/// <summary>Where an update install currently is (for progress UI).</summary>
public enum UpdateStage
{
    Idle,
    Downloading,
    Preparing,
    Restarting
}

/// <summary>Outcome of <see cref="AutoUpdateService.DownloadAndInstallAsync"/>.</summary>
public enum UpdateInstallResult
{
    /// <summary>The installer is running and waits for this process; the app must exit now.</summary>
    Started,
    /// <summary>Another download/install is already in progress.</summary>
    AlreadyRunning,
    /// <summary>No direct HTTPS link to an update file; use the release page instead.</summary>
    NoDirectDownload,
    DownloadFailed,
    /// <summary>The server sent a web page (e.g. a sign-in page) or an unknown file.</summary>
    NotAPackage,
    /// <summary>Size or SHA-256 differs from what the server announced.</summary>
    VerificationFailed,
    /// <summary>The package is damaged or does not contain WinXTools.exe.</summary>
    InvalidPackage,
    /// <summary>The package carries no release signature (e.g. a bare exe), so it is not installed.</summary>
    NotSigned,
    /// <summary>The release signature does not match the files or the announced version.</summary>
    SignatureInvalid,
    /// <summary>%ProgramData%\WinXTools could not be made admin-only, so nothing was run.</summary>
    FolderNotSecure,
    Failed
}

public class AutoUpdateService
{
    private static AutoUpdateService? _instance;
    public static AutoUpdateService Instance => _instance ??= new AutoUpdateService();

    // Same update check the studio's other apps use: GET /api/v1/product/{slug}/update/check.
    // The server re-reads GitHub releases on demand (5-minute cache), so a new release shows
    // up without anyone pressing Sync in the admin.
    private const string XmanUpdateCheckUrl = XmanApi.ProductApi + "/update/check";
    private const string ProductPageUrl = XmanApi.ProductPageUrl;
    // Fallback when xman4289.com cannot be reached (the repo is public).
    private const string GitHubApiUrl = "https://api.github.com/repos/xjanova/winxtools/releases/latest";

    // The exe inside every release package (AssemblyName in NetX.App.csproj).
    private const string ProductExeName = "WinXTools.exe";
    // Download cap when the server does not say how big the package is.
    private const long MaxPackageBytes = 512L * 1024 * 1024;
    // %ProgramData%\WinXTools\Update: download, unpacked files and the installer script.
    private const string StagingFolderName = "Update";

    private readonly HttpClient _xmanClient;
    private readonly HttpClient _gitHubClient;
    private readonly string _currentVersion;
    private int _installing; // 1 while a download/install runs (Interlocked)

    /// <summary>Bytes received and total bytes (0 = size unknown).</summary>
    public event Action<long, long>? OnDownloadProgress;
    public event Action<UpdateStage>? OnStageChanged;

    private AutoUpdateService()
    {
        _currentVersion = GetCurrentVersion();

        _xmanClient = XmanApi.CreateClient("WinXTools-AutoUpdate/" + _currentVersion, TimeSpan.FromSeconds(30));

        _gitHubClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _gitHubClient.DefaultRequestHeaders.Add("User-Agent", "WinXTools-AutoUpdate");
        _gitHubClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public string CurrentVersion => _currentVersion;

    /// <summary>True while an update is downloading or being prepared (also after it started, until exit).</summary>
    public bool IsInstalling => Volatile.Read(ref _installing) == 1;
    public UpdateStage Stage { get; private set; }

    /// <summary>Result of the last check that reached a server (null = none yet).</summary>
    public UpdateInfo? LastCheck { get; private set; }

    /// <summary>
    /// Check for updates - tries xman API first, falls back to GitHub.
    /// Returns null when neither could be checked (never "up to date" on failure).
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        // Try xman studio API first, then GitHub
        var info = await CheckXmanApiAsync() ?? await CheckGitHubAsync();
        if (info != null) LastCheck = info;
        return info;
    }

    private async Task<UpdateInfo?> CheckXmanApiAsync()
    {
        try
        {
            // current_version without a leading 'v' — the server compares it with version_compare().
            var url = $"{XmanUpdateCheckUrl}?current_version={Uri.EscapeDataString(TrimVersionPrefix(_currentVersion))}";
            using var response = await _xmanClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var result = await response.Content.ReadFromJsonAsync<XmanUpdateCheck>();
            // No release published yet: the server answers has_update=false with an empty
            // version. That is "nothing known", so let GitHub answer instead.
            if (result == null || string.IsNullOrWhiteSpace(result.LatestVersion)) return null;

            var latest = TrimVersionPrefix(result.LatestVersion);
            return new UpdateInfo
            {
                CurrentVersion = _currentVersion,
                LatestVersion = latest,
                ReleaseNotes = result.Changelog ?? "",
                DownloadUrl = result.DownloadUrl ?? "",
                ReleaseUrl = ProductPageUrl,
                FileSize = result.FileSize ?? 0,
                ExpectedSha256 = result.Sha256,
                // The server's has_update is not enough on its own: never "update" to an
                // older or equal version, whatever the server says.
                IsUpdateAvailable = result.HasUpdate && UpdatePackageVerifier.IsNewer(latest, _currentVersion),
                Source = "xman"
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Xman update check failed: {ex.Message}");
            return null;
        }
    }

    private async Task<UpdateInfo?> CheckGitHubAsync()
    {
        try
        {
            var release = await _gitHubClient.GetFromJsonAsync<GitHubRelease>(GitHubApiUrl);

            // A release without a readable tag is a failed check, not "up to date".
            if (release == null || !TryParseVersion(release.TagName, out _, out _))
                return null;

            // Only the release zip carries the signed manifest; a bare exe is never installed.
            var asset = release.Assets?.FirstOrDefault(a => IsDownloadable(a, "-win-x64.zip"))
                ?? release.Assets?.FirstOrDefault(a => IsDownloadable(a, ".zip"));
            var latest = TrimVersionPrefix(release.TagName!);

            return new UpdateInfo
            {
                CurrentVersion = _currentVersion,
                LatestVersion = latest,
                ReleaseNotes = release.Body ?? "",
                DownloadUrl = asset?.BrowserDownloadUrl ?? "",
                ReleaseUrl = release.HtmlUrl ?? "",
                FileSize = asset?.Size ?? 0,
                ExpectedSha256 = ParseGitHubDigest(asset?.Digest),
                PublishedAt = release.PublishedAt,
                IsUpdateAvailable = UpdatePackageVerifier.IsNewer(latest, _currentVersion),
                Source = "github"
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GitHub check failed: {ex.Message}");
            return null;
        }
    }

    private static bool IsDownloadable(GitHubAsset asset, string extension) =>
        !string.IsNullOrEmpty(asset.BrowserDownloadUrl)
        && asset.Name?.EndsWith(extension, StringComparison.OrdinalIgnoreCase) == true;

    // GitHub reports "sha256:<hex>" for assets uploaded since mid-2025; older assets have none.
    private static string? ParseGitHubDigest(string? digest) =>
        digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;

    private static string TrimVersionPrefix(string version) => version.Trim().TrimStart('v', 'V');

    // "v1.2.0", "1.2.0-beta", "1.2" -> 1.2.0 (+ prerelease flag). Build metadata is ignored.
    internal static bool TryParseVersion(string? text, out Version version, out bool isPrerelease)
    {
        version = new Version(0, 0, 0);
        isPrerelease = false;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = TrimVersionPrefix(text);
        int cut = value.IndexOf('+');
        if (cut >= 0) value = value[..cut];
        cut = value.IndexOf('-');
        if (cut >= 0)
        {
            isPrerelease = true;
            value = value[..cut];
        }
        if (!value.Contains('.')) value += ".0";

        if (!Version.TryParse(value, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>
    /// Downloads, verifies and stages the update in an admin-only folder, then starts an
    /// installer that waits for this process to exit. On <see cref="UpdateInstallResult.Started"/>
    /// the caller must shut the app down.
    /// </summary>
    public async Task<UpdateInstallResult> DownloadAndInstallAsync(UpdateInfo updateInfo)
    {
        if (Interlocked.Exchange(ref _installing, 1) == 1)
            return UpdateInstallResult.AlreadyRunning;

        var result = UpdateInstallResult.Failed;
        try
        {
            // Download, hashing and unzipping must not run on the UI thread.
            result = await Task.Run(() => InstallCoreAsync(updateInfo));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update failed: {ex}");
            result = UpdateInstallResult.Failed;
        }
        finally
        {
            if (result != UpdateInstallResult.Started)
            {
                AdminOnlyLocation.TryDeleteFolder(StagingFolderName);
                Volatile.Write(ref _installing, 0);
                SetStage(UpdateStage.Idle);
            }
        }
        return result;
    }

    private async Task<UpdateInstallResult> InstallCoreAsync(UpdateInfo updateInfo)
    {
        if (!Uri.TryCreate(updateInfo.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            return UpdateInstallResult.NoDirectDownload;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)
            || Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return UpdateInstallResult.Failed;

        // Everything the elevated installer touches lives in %ProgramData%\WinXTools\Update, which
        // only Administrators and SYSTEM can write. %TEMP% belongs to the user, so any non-elevated
        // process could swap the files there while the script waits for us to exit.
        var staging = AdminOnlyLocation.CreateFreshFolder(StagingFolderName);
        if (staging == null) return UpdateInstallResult.FolderNotSecure;
        var root = Path.GetDirectoryName(staging)!;

        SetStage(UpdateStage.Downloading);
        var packagePath = Path.Combine(staging, "package.download");
        var downloadError = await DownloadPackageAsync(updateInfo, url, packagePath);
        if (downloadError != null) return downloadError.Value;

        SetStage(UpdateStage.Preparing);
        var (prepareError, sourceDir) = PreparePayload(packagePath, Path.Combine(staging, "files"), Path.GetFileName(exePath),
            updateInfo.LatestVersion, _currentVersion);
        if (prepareError != null) return prepareError.Value;

        SetStage(UpdateStage.Restarting);
        return LaunchInstaller(root, staging, sourceDir, exePath)
            ? UpdateInstallResult.Started
            : UpdateInstallResult.Failed;
    }

    private async Task<UpdateInstallResult?> DownloadPackageAsync(UpdateInfo updateInfo, Uri url, string filePath)
    {
        try
        {
            // Use a dedicated client with longer timeout for file downloads
            using var downloadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            downloadClient.DefaultRequestHeaders.Add("User-Agent", "WinXTools-AutoUpdate");

            using var response = await downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return UpdateInstallResult.DownloadFailed;

            // Redirects must stay on HTTPS, and a web page or API error is not a package
            // (the xman download route answers anonymous requests with its sign-in page).
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                return UpdateInstallResult.NoDirectDownload;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
                return UpdateInstallResult.NotAPackage;

            long announced = updateInfo.FileSize;
            long? contentLength = response.Content.Headers.ContentLength;
            if (announced > 0 && contentLength.HasValue && contentLength.Value != announced)
                return UpdateInstallResult.VerificationFailed;

            long total = announced > 0 ? announced : contentLength ?? 0;
            long limit = total > 0 ? total : MaxPackageBytes;

            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long downloaded = 0;
            long lastReported = -1;

            // HttpClient.Timeout stops at the headers here; a connection that goes silent
            // mid-download would otherwise wait forever.
            using var idle = new CancellationTokenSource();

            await using (var stream = await response.Content.ReadAsStreamAsync())
            await using (var file = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
            {
                int read;
                while (true)
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(60));
                    read = await stream.ReadAsync(buffer, idle.Token);
                    if (read == 0) break;

                    downloaded += read;
                    if (downloaded > limit) return UpdateInstallResult.VerificationFailed;

                    sha256.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read));

                    // Report each whole percent (or each MB when the size is unknown).
                    long step = total > 0 ? downloaded * 100 / total : downloaded >> 20;
                    if (step != lastReported)
                    {
                        lastReported = step;
                        OnDownloadProgress?.Invoke(downloaded, total);
                    }
                }
            }

            // Truncated or padded downloads are rejected before anything is unpacked.
            if ((announced > 0 && downloaded != announced)
                || (contentLength.HasValue && downloaded != contentLength.Value))
                return UpdateInstallResult.VerificationFailed;

            var expectedHash = updateInfo.ExpectedSha256?.Trim();
            if (!string.IsNullOrEmpty(expectedHash)
                && !Convert.ToHexString(sha256.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                return UpdateInstallResult.VerificationFailed;

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or HttpIOException or OperationCanceledException)
        {
            Debug.WriteLine($"Update download failed: {ex.Message}");
            return UpdateInstallResult.DownloadFailed;
        }
    }

    private enum FileKind { Unknown, Zip, Exe }

    private static FileKind DetectFileKind(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        if (read == 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4)
            return FileKind.Zip;
        if (read >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z')
            return FileKind.Exe;
        return FileKind.Unknown;
    }

    /// <summary>
    /// Unpacks the download into <paramref name="payloadDir"/>, checks the release signature and
    /// returns the folder whose contents replace the install folder. Only the signed release
    /// zip is accepted; a bare exe cannot prove where it came from.
    /// </summary>
    private static (UpdateInstallResult? Error, string SourceDir) PreparePayload(
        string packagePath, string payloadDir, string exeName, string expectedVersion, string currentVersion)
    {
        Directory.CreateDirectory(payloadDir);

        switch (DetectFileKind(packagePath))
        {
            case FileKind.Exe:
                return (UpdateInstallResult.NotSigned, "");

            case FileKind.Zip:
                try
                {
                    using var archive = ZipFile.OpenRead(packagePath);
                    long unpacked = 0;
                    foreach (var entry in archive.Entries) unpacked += entry.Length;
                    if (archive.Entries.Count > 10_000 || unpacked > MaxPackageBytes * 4)
                        return (UpdateInstallResult.InvalidPackage, "");

                    // .NET refuses entries that would land outside payloadDir ("zip slip").
                    archive.ExtractToDirectory(payloadDir);
                }
                catch (InvalidDataException)
                {
                    return (UpdateInstallResult.InvalidPackage, "");
                }
                File.Delete(packagePath);
                break;

            default:
                return (UpdateInstallResult.NotAPackage, "");
        }

        // Release zips hold WinXTools.exe at the root; accept one wrapping folder too.
        var sourceDir = payloadDir;
        if (!File.Exists(Path.Combine(sourceDir, ProductExeName)))
        {
            var folders = Directory.GetDirectories(sourceDir);
            if (folders.Length != 1 || Directory.GetFiles(sourceDir).Length != 0
                || !File.Exists(Path.Combine(folders[0], ProductExeName)))
                return (UpdateInstallResult.InvalidPackage, "");
            sourceDir = folders[0];
        }

        // Every file must be exactly what the release pipeline signed — before anything runs.
        switch (UpdatePackageVerifier.Verify(sourceDir, expectedVersion, currentVersion))
        {
            case UpdatePackageVerifier.Result.Valid:
                break;
            case UpdatePackageVerifier.Result.Unsigned:
                return (UpdateInstallResult.NotSigned, "");
            default:
                return (UpdateInstallResult.SignatureInvalid, "");
        }

        var newExe = Path.Combine(sourceDir, ProductExeName);
        if (DetectFileKind(newExe) != FileKind.Exe)
            return (UpdateInstallResult.InvalidPackage, "");

        // Keep the file name the user runs (e.g. "WinXTools (1).exe") so shortcuts keep working
        // and the restart starts the new version.
        if (!exeName.Equals(ProductExeName, StringComparison.OrdinalIgnoreCase))
            File.Move(newExe, Path.Combine(sourceDir, exeName), overwrite: true);

        return (null, sourceDir);
    }

    // Runs elevated from the admin-only staging folder. It is plain ASCII and takes every path
    // from WXT_* environment variables: cmd does not re-parse a variable's value (so '%', '&',
    // '^' in a folder name stay literal) and Thai/non-ASCII names survive, which they would not
    // inside the script file. Paths are only ever used inside quotes, and tools are called by
    // full System32 path so nothing is picked up from the current folder or PATH.
    private const string InstallScript = """"
        @echo off
        setlocal EnableExtensions DisableDelayedExpansion

        rem 1) Wait up to 2 minutes for WinXTools (this PID) to exit.
        set /a waited=0
        :wait_exit
        "%WXT_SYS%\tasklist.exe" /FI "PID eq %WXT_PID%" /FO CSV /NH 2>nul | "%WXT_SYS%\find.exe" """%WXT_PID%""" >nul
        if errorlevel 1 goto copy_files
        set /a waited+=1
        if %waited% geq 120 exit /b 1
        "%WXT_SYS%\PING.EXE" -n 2 127.0.0.1 >nul
        goto wait_exit

        rem 2) Copy the new files over the install folder, retrying while files are still locked.
        :copy_files
        "%WXT_SYS%\robocopy.exe" "%WXT_SOURCE%" "%WXT_TARGET%" /E /R:10 /W:1 /NP /NJH /NJS /NFL /NDL >nul
        if errorlevel 8 (set "copied=0") else (set "copied=1")

        rem 3) Start WinXTools again (the old version if the copy failed).
        if "%WXT_ELEVATED_RESTART%"=="1" (
            start "" /D "%WXT_TARGET%" "%WXT_EXE%"
        ) else (
            start "" "%WXT_EXPLORER%" "%WXT_EXE%"
        )

        rem 4) Remove the staging folder, this script included, after a good copy.
        if "%copied%"=="0" exit /b 2
        cd /d "%WXT_ROOT%"
        (rmdir /s /q "%WXT_STAGING%") & exit 0
        """";

    private static bool LaunchInstaller(string root, string staging, string sourceDir, string exePath)
    {
        var scriptPath = Path.Combine(staging, "install.cmd");
        // Batch labels need CRLF; the source file may have been checked out with LF.
        File.WriteAllText(scriptPath, InstallScript.ReplaceLineEndings("\r\n"), Encoding.ASCII);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            // /d: skip AutoRun commands (HKCU is writable by any user process); /v:off: keep '!'
            // literal; /s /c ""script"": cmd strips only the outer pair of quotes.
            Arguments = $"/d /v:off /s /c \"\"{scriptPath}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root
        };
        psi.Environment["WXT_SYS"] = Environment.SystemDirectory;
        psi.Environment["WXT_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        psi.Environment["WXT_SOURCE"] = CopyDirArg(sourceDir);
        psi.Environment["WXT_TARGET"] = CopyDirArg(Path.GetDirectoryName(exePath)!);
        psi.Environment["WXT_EXE"] = exePath;
        psi.Environment["WXT_STAGING"] = staging;
        psi.Environment["WXT_ROOT"] = root;
        psi.Environment["WXT_EXPLORER"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        // Restart elevated only when no normal-user process can have swapped the new exe in the
        // install folder; otherwise start it through Explorer so Windows asks for consent, the
        // same as a normal launch of a copy kept in a user folder.
        psi.Environment["WXT_ELEVATED_RESTART"] = AdminOnlyLocation.IsAdminOnlyPath(exePath) ? "1" : "0";

        using var process = Process.Start(psi);
        return process != null;
    }

    // robocopy reads "C:\dir\" as an escaped quote; drop the trailing slash ("C:\" -> "C:\.").
    private static string CopyDirArg(string dir)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(dir);
        return trimmed.EndsWith('\\') ? trimmed + "." : trimmed;
    }

    private void SetStage(UpdateStage stage)
    {
        Stage = stage;
        OnStageChanged?.Invoke(stage);
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
}

/// <summary>
/// Locations the elevated app can trust. WinXTools runs as admin, so anything it runs or hands
/// to an elevated tool later must sit where a normal-user process cannot plant or swap files.
/// </summary>
public static class AdminOnlyLocation
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    // Rights that let a principal replace, add, delete or re-permission content. Same set as the
    // optimizer's secure store, which keeps its files in the same %ProgramData%\WinXTools folder.
    private const FileSystemRights WriteLikeRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | (FileSystemRights)0x40000000   // GENERIC_WRITE
        | (FileSystemRights)0x10000000;  // GENERIC_ALL

    private const AccessControlSections OwnerAndAccess = AccessControlSections.Owner | AccessControlSections.Access;

    /// <summary>%ProgramData%\WinXTools (may not exist or be secured yet; see <see cref="EnsureRoot"/>).</summary>
    public static string RootPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinXTools");

    /// <summary>
    /// Returns %ProgramData%\WinXTools, created if needed with owner Administrators and only
    /// Administrators + SYSTEM full control (inheritance disabled); null if that cannot be done.
    /// </summary>
    public static string? EnsureRoot()
    {
        try
        {
            var root = RootPath;
            var info = new DirectoryInfo(root);

            if (File.Exists(root) || info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !HasTrustedOwner(info)))
            {
                // Any user can create folders in ProgramData. A file, a link (junction/symlink) or a
                // folder a non-admin created there is not ours: its owner can re-grant itself access
                // or keep handles open to swap files later. Move it aside instead of trusting it
                // (renaming never touches a link's target; this fails safely while it is in use).
                var aside = $"{root}.untrusted-{Guid.NewGuid():N}";
                if (File.Exists(root)) File.Move(root, aside);
                else Directory.Move(root, aside);
                info.Refresh();
            }

            if (!info.Exists)
            {
                // Created together with its security descriptor, so there is no moment where the
                // folder exists with the permissive ProgramData ACL. If someone won a race and made
                // it first, Create does nothing and the check below fails.
                info.Create(BuildRootSecurity());
            }
            else if (!IsAdminOnlyFolder(root))
            {
                // Made by an administrator but with a loose ACL: take it back in place, like the
                // optimizer's store does, so files other features keep here are not lost.
                info.SetAccessControl(BuildRootSecurity());
            }

            return IsAdminOnlyFolder(root) ? root : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Admin-only folder unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates a fresh, empty %ProgramData%\WinXTools\<paramref name="name"/> (an old one is removed)
    /// for files an elevated tool will use; null if it cannot be made admin-only.
    /// </summary>
    public static string? CreateFreshFolder(string name)
    {
        var root = EnsureRoot();
        if (root == null) return null;

        try
        {
            var path = Path.Combine(root, name);
            DeleteTree(path); // only admins can have made anything inside the root

            // Everything created inside inherits Administrators + SYSTEM only. The OWNER RIGHTS
            // entry replaces a file owner's implicit right to re-permission it, in case policy
            // makes the signed-in user (not Administrators) the owner of files this app creates.
            var security = BuildRootSecurity();
            security.AddAccessRule(new FileSystemAccessRule(OwnerRights, FileSystemRights.ReadPermissions,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);

            return IsAdminOnlyFolder(path) ? path : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Admin-only folder {name} unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Removes %ProgramData%\WinXTools\<paramref name="name"/> if the root is still admin-only.</summary>
    public static void TryDeleteFolder(string name)
    {
        try
        {
            var root = RootPath;
            if (IsAdminOnlyFolder(root))
                DeleteTree(Path.Combine(root, name));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cleaning {name} failed: {ex.Message}");
        }
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private static DirectorySecurity BuildRootSecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static bool HasTrustedOwner(DirectoryInfo info) =>
        info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
        && IsTrusted(owner);

    /// <summary>
    /// True when <paramref name="path"/> is a real folder (no link) owned by Administrators, SYSTEM
    /// or TrustedInstaller and nobody else may write, delete or re-permission anything in it -
    /// including through inheritable entries that would apply to files created there later.
    /// </summary>
    public static bool IsAdminOnlyFolder(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            return OnlyTrustedCanWrite(info.GetAccessControl(OwnerAndAccess), includeInheritOnly: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ACL check failed for {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// True when a normal-user process cannot replace the file: after resolving links, the file,
    /// the files next to it and every folder above it (the drive root excepted, where users may
    /// only add new folders) are owned by and writable only by Administrators, SYSTEM or
    /// TrustedInstaller — e.g. C:\Program Files\WinXTools. False for user folders and network paths.
    /// </summary>
    public static bool IsAdminOnlyPath(string filePath)
    {
        try
        {
            var finalPath = GetFinalPath(filePath);
            if (finalPath == null || finalPath.StartsWith(@"\\", StringComparison.Ordinal)) return false;

            // Inherit-only entries (e.g. CREATOR OWNER on Program Files) only shape files created
            // later, and only admins can create files in an admin-only folder.
            var file = new FileInfo(finalPath);
            if (!OnlyTrustedCanWrite(file.GetAccessControl(OwnerAndAccess), includeInheritOnly: false)) return false;

            var folder = file.Directory!;
            foreach (var sibling in folder.EnumerateFiles())
            {
                if (!OnlyTrustedCanWrite(sibling.GetAccessControl(OwnerAndAccess), includeInheritOnly: false)) return false;
            }

            for (var dir = folder; dir.Parent != null; dir = dir.Parent)
            {
                if (!OnlyTrustedCanWrite(dir.GetAccessControl(OwnerAndAccess), includeInheritOnly: false)) return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Install location check failed: {ex.Message}");
            return false;
        }
    }

    private static bool OnlyTrustedCanWrite(FileSystemSecurity security, bool includeInheritOnly)
    {
        // An untrusted owner can always rewrite the ACL.
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
            return false;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!includeInheritOnly && rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && IsTrusted(sid)) continue;
            if ((rule.FileSystemRights & WriteLikeRights) != 0) return false;
        }
        return true;
    }

    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid == Administrators || sid == LocalSystem || sid == TrustedInstaller;

    /// <summary>
    /// The file's real path with junctions, symlinks and SUBST drives resolved
    /// (network paths come back as \\server\share\...); null if it cannot be opened.
    /// </summary>
    public static string? GetFinalPath(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new char[1024];
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length > buffer.Length)
            {
                buffer = new char[length];
                length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            }
            if (length == 0 || length > buffer.Length) return null;

            var result = new string(buffer, 0, (int)length);
            if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + result[8..];
            if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) return result[4..];
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetFinalPath failed: {ex.Message}");
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);
}

public class UpdateInfo
{
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public long FileSize { get; set; }
    /// <summary>Hex SHA-256 of the download when the server publishes one; verified before install.</summary>
    public string? ExpectedSha256 { get; set; }
    public DateTime? PublishedAt { get; set; }
    public bool IsUpdateAvailable { get; set; }
    public string Source { get; set; } = "";
}

// xman studio GET /api/v1/product/{slug}/update/check (VersionController@checkUpdate).
// Snake_case fields, so every property needs JsonPropertyName. sha256/file_size/filename
// were added for WinXTools; an older server leaves them out and only the signature and
// the download's own length are checked.
internal class XmanUpdateCheck
{
    [JsonPropertyName("has_update")] public bool HasUpdate { get; set; }
    [JsonPropertyName("latest_version")] public string? LatestVersion { get; set; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("changelog")] public string? Changelog { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
    [JsonPropertyName("filename")] public string? Filename { get; set; }
}

// GitHub API response models. GitHub uses snake_case, so every field needs JsonPropertyName;
// without it tag_name parsed as null and every check looked "up to date".
internal class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
}

internal class GitHubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

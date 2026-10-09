using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NetX.Core.System;

public enum CleanTarget
{
    TempFiles,
    BrowserCache,
    AppCache,
    WindowsUpdate,
    DeliveryOptimization,
    ErrorReports,
    Thumbnails,
    LogFiles,
    ShaderCache,
    RecycleBin,
    WindowsOld,
    StoreAppCache,
    InstallerLeftovers,
    DevCaches,
    WindowsSetupFiles,
    DefenderFiles,
    WindowsMisc,
    UpdateCleanup,
    DriverPackages,
    LanguagePacks,
    WindowsEsd,
    CapCut
}

public enum CleanRisk
{
    Safe,
    Medium,
    Caution
}

/// <summary>Display metadata for one cleanup category (text lives in the language files).</summary>
public sealed record CleanCategory(CleanTarget Target, string Key, CleanRisk Risk, bool DefaultOn);

public sealed class CleanScanResult
{
    public CleanTarget Target { get; init; }

    /// <summary>Disk space that cleaning would free right now (allocation size).</summary>
    public long Bytes { get; set; }

    /// <summary>Files that would be removed.</summary>
    public int FileCount { get; set; }

    /// <summary>Files open in another program (cannot be deleted now).</summary>
    public int InUseCount { get; set; }
    public long InUseBytes { get; set; }

    /// <summary>Files kept on purpose because they are too new (temp files).</summary>
    public int RecentCount { get; set; }

    /// <summary>Files Windows does not let us delete (permissions).</summary>
    public int DeniedCount { get; set; }

    public bool Found { get; set; } = true;

    /// <summary>Why something is skipped; the UI maps it to a localized message.</summary>
    public string? NoteCode { get; set; }
    public string? NoteArg { get; set; }
}

public sealed class CleanExecResult
{
    public CleanTarget Target { get; init; }
    public long BytesFreed { get; set; }
    public int FilesDeleted { get; set; }
    public int FilesSkipped { get; set; }
    public string? NoteCode { get; set; }
    public string? NoteArg { get; set; }
}

/// <summary>
/// File-cleanup engine built for accuracy:
/// • Scan opens every candidate the same way Clean will (DELETE access), so a
///   file another program has open is reported as "in use", not as space you
///   will get back.
/// • Sizes are the on-disk allocation, and hard-linked files count as zero
///   (deleting one link frees nothing) — so the number matches the free space
///   you actually gain.
/// • Deletion happens through the handle that was verified to live inside the
///   category's folder, so a junction or symlink swapped in by another
///   program can't redirect this elevated process to delete system files.
/// </summary>
public static class SystemCleaner
{
    public static readonly IReadOnlyList<CleanCategory> Categories =
    [
        new(CleanTarget.TempFiles, "Temp", CleanRisk.Safe, true),
        new(CleanTarget.BrowserCache, "Browser", CleanRisk.Safe, true),
        new(CleanTarget.AppCache, "Apps", CleanRisk.Safe, true),
        new(CleanTarget.CapCut, "CapCut", CleanRisk.Safe, true),
        new(CleanTarget.ErrorReports, "WER", CleanRisk.Safe, true),
        new(CleanTarget.Thumbnails, "Thumbs", CleanRisk.Safe, true),
        new(CleanTarget.LogFiles, "Logs", CleanRisk.Safe, true),
        new(CleanTarget.DeliveryOptimization, "DO", CleanRisk.Safe, true),
        new(CleanTarget.WindowsUpdate, "WU", CleanRisk.Medium, false),
        new(CleanTarget.ShaderCache, "Shaders", CleanRisk.Medium, false),
        new(CleanTarget.StoreAppCache, "StoreApps", CleanRisk.Safe, true),
        new(CleanTarget.InstallerLeftovers, "Installers", CleanRisk.Safe, true),
        new(CleanTarget.WindowsSetupFiles, "WinSetup", CleanRisk.Safe, true),
        new(CleanTarget.DefenderFiles, "Defender", CleanRisk.Safe, true),
        new(CleanTarget.WindowsMisc, "WinMisc", CleanRisk.Safe, true),
        new(CleanTarget.DevCaches, "DevCaches", CleanRisk.Medium, false),
        new(CleanTarget.UpdateCleanup, "UpdateCleanup", CleanRisk.Medium, false),
        new(CleanTarget.DriverPackages, "Drivers", CleanRisk.Medium, false),
        new(CleanTarget.LanguagePacks, "Languages", CleanRisk.Medium, false),
        new(CleanTarget.RecycleBin, "Recycle", CleanRisk.Medium, false),
        new(CleanTarget.WindowsEsd, "WinEsd", CleanRisk.Caution, false),
        new(CleanTarget.WindowsOld, "WinOld", CleanRisk.Caution, false),
    ];

    /// <summary>
    /// Categories cleaned by Windows' own Disk Cleanup handlers (system drive
    /// only). Overlapping handlers (temp files, thumbnails, Recycle Bin, error
    /// reports, Delivery Optimization, shader cache, Windows.old) are left out
    /// because WinXTools cleans those itself; Downloads and File History
    /// versions are user files and are never touched.
    /// </summary>
    private static readonly Dictionary<CleanTarget, string[]> WindowsHandlers = new()
    {
        [CleanTarget.WindowsSetupFiles] = ["Temporary Setup Files", "Windows Upgrade Log Files", "Upgrade Discarded Files",
                                           "Windows Reset Log Files", "Setup Log Files", "Old ChkDsk Files"],
        [CleanTarget.DefenderFiles] = ["Windows Defender"],
        [CleanTarget.WindowsMisc] = ["Internet Cache Files", "Diagnostic Data Viewer database files", "Feedback Hub Archive log files",
                                     "RetailDemo Offline Content", "BranchCache", "Downloaded Program Files", "Offline Pages Files",
                                     "Temporary Sync Files", "Active Setup Temp Folders", "Content Indexer Cleaner"],
        [CleanTarget.UpdateCleanup] = ["Update Cleanup"],
        [CleanTarget.DriverPackages] = ["Device Driver Packages"],
        [CleanTarget.LanguagePacks] = ["Language Pack"],
        [CleanTarget.WindowsEsd] = ["Windows ESD installation files"],
    };

    public static bool IsWindowsHandlerTarget(CleanTarget target) => WindowsHandlers.ContainsKey(target);

    // Windows Update cleanup works through the component store and can take a long time.
    private static TimeSpan HandlerTimeout(CleanTarget target, bool purge) => target switch
    {
        CleanTarget.UpdateCleanup => purge ? TimeSpan.FromMinutes(90) : TimeSpan.FromMinutes(15),
        _ => purge ? TimeSpan.FromMinutes(20) : TimeSpan.FromMinutes(5)
    };

    private static string SystemRoot => Path.GetPathRoot(WinDir) ?? @"C:\";

    // Temp files touched within this window may still belong to a running installer.
    private static readonly TimeSpan TempKeepWindow = TimeSpan.FromHours(24);

    // Leftovers whose name says what they are (and that nothing reads back later)
    // only need to be an hour old.
    private static readonly TimeSpan KnownJunkKeepWindow = TimeSpan.FromHours(1);

    private static readonly global::System.Text.RegularExpressions.Regex KnownTempJunk = new(
        @"^(Microsoft\.NET\.Workload_.*\.log" +          // .NET SDK workload installer log, one per dotnet build/run
        @"|__PSScriptPolicyTest_.*\.psm?1" +              // PowerShell's execution-policy probe
        @"|RazerTempPicture_.*\.png" +                     // Razer Synapse
        @"|mat-debug-\d+\.log" +
        @"|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.tmp)$", // GUID-named downloads (browsers)
        global::System.Text.RegularExpressions.RegexOptions.IgnoreCase | global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    #region Target folders

    private static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string LocalLow => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

    /// <summary>
    /// Every temp folder Windows uses on this PC: TEMP/TMP as set for the user
    /// and for the system (read fresh from the registry, so a TEMP moved to
    /// another drive is found), the default AppData\Local\Temp (programs that
    /// ignore TEMP still write there, and old leftovers stay behind after TEMP
    /// is moved), and Windows\Temp. Duplicates and folders inside another one
    /// in the list are dropped so nothing is counted twice.
    /// </summary>
    private static IEnumerable<string> TempDirs()
    {
        var system = new[] { Path.Combine(WinDir, "Temp"), Path.Combine(WinDir, "SystemTemp") }; // SystemTemp: Windows 11 24H2+
        var fromUser = new List<string?>
        {
            Path.GetTempPath(),
            Environment.GetEnvironmentVariable("TEMP"),
            Environment.GetEnvironmentVariable("TMP"),
            ReadEnvironmentKey(Registry.CurrentUser, "Environment", "TEMP"),
            ReadEnvironmentKey(Registry.CurrentUser, "Environment", "TMP"),
            ReadEnvironmentKey(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "TEMP"),
            ReadEnvironmentKey(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "TMP"),
            Path.Combine(LocalAppData, "Temp"),
        };

        var roots = new List<string>();
        foreach (var dir in system.Concat(fromUser))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string full;
            try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LongPath(dir))); }
            catch { continue; }

            bool trusted = system.Any(s => s.Equals(full, StringComparison.OrdinalIgnoreCase));
            // TEMP comes from settings a standard user can change: it must pass the safety check.
            if (!trusted && !IsSafeTempRoot(full)) continue;
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }

        foreach (var root in roots)
        {
            bool inside = roots.Any(other => !other.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                                             root.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase));
            if (!inside) yield return root;
        }
    }

    /// <summary>A TEMP/TMP value as stored in the registry, with %VARIABLES% expanded.</summary>
    private static string? ReadEnvironmentKey(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw) return null;
            // %SystemRoot% and %USERPROFILE% are the same for this process as for Windows' own expansion.
            return Environment.ExpandEnvironmentVariables(raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// %TEMP% comes from the user's (writable) environment. An elevated cleaner
    /// must never let it point at a drive root or a system/program folder.
    /// </summary>
    private static bool IsSafeTempRoot(string path)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LongPath(path)));
            if (Path.GetPathRoot(full)?.TrimEnd('\\').Equals(full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true)
                return false;

            // The folder itself or one of its parents must be called Temp/Tmp
            // (D:\Temp\User is fine; C:\Users\Me\Documents is not).
            var root = Path.GetPathRoot(full) ?? "";
            var parts = full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (!parts.Any(p => p.Equals("Temp", StringComparison.OrdinalIgnoreCase) || p.Equals("Tmp", StringComparison.OrdinalIgnoreCase)))
                return false;

            // ProgramData too: a "Temp" folder of another program there may hold files
            // that program still needs, and a standard user can point %TEMP% at it.
            string[] forbidden =
            [
                WinDir,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                ProgramData,
            ];
            return !forbidden.Any(f => !string.IsNullOrEmpty(f) &&
                (full.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                 full.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Cache folders of Chromium browsers and Firefox. Cache data only — never
    /// cookies, history, passwords, logins or site storage.
    /// </summary>
    private static IEnumerable<string> BrowserCacheDirs()
    {
        var chromiumRoots = new[]
        {
            Path.Combine(LocalAppData, "Google", "Chrome", "User Data"),
            Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data"),
            Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data"),
            Path.Combine(LocalAppData, "Vivaldi", "User Data"),
            Path.Combine(LocalAppData, "Chromium", "User Data"),
            Path.Combine(LocalAppData, "Yandex", "YandexBrowser", "User Data"),
            Path.Combine(LocalAppData, "CocCoc", "Browser", "User Data"),
            // Opera keeps the cache of its single profile directly under this folder.
            Path.Combine(LocalAppData, "Opera Software", "Opera Stable"),
            Path.Combine(LocalAppData, "Opera Software", "Opera GX Stable"),
        };

        foreach (var root in chromiumRoots.Where(Directory.Exists))
        {
            // Browser-wide GPU shader caches.
            foreach (var sub in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" })
                yield return Path.Combine(root, sub);

            var profiles = new List<string> { root };
            try
            {
                profiles.AddRange(Directory.EnumerateDirectories(root).Where(d =>
                {
                    var name = Path.GetFileName(d);
                    return name == "Default" || name == "Guest Profile" ||
                           name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
                }));
            }
            catch { }

            // Crash reports nobody will send.
            yield return Path.Combine(root, "Crashpad", "reports");

            foreach (var profile in profiles)
            {
                // "Cache" already contains "Cache\Cache_Data" — listing both
                // would count every file twice.
                foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
                                            Path.Combine("Service Worker", "ScriptCache") })
                    yield return Path.Combine(profile, sub);
            }
        }

        var ffProfiles = Path.Combine(LocalAppData, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(ffProfiles))
        {
            IEnumerable<string> profiles = [];
            try { profiles = Directory.EnumerateDirectories(ffProfiles).ToList(); } catch { }
            foreach (var profile in profiles)
            {
                yield return Path.Combine(profile, "cache2");
                yield return Path.Combine(profile, "startupCache");
                yield return Path.Combine(profile, "jumpListCache");
            }
        }
    }

    /// <summary>
    /// Web caches of popular desktop apps. They rebuild on their own; chat
    /// history, logins and downloads are in other folders and are not touched.
    /// </summary>
    private static IEnumerable<string> AppCacheDirs()
    {
        var electronApps = new[]
        {
            Path.Combine(RoamingAppData, "discord"),
            Path.Combine(RoamingAppData, "Microsoft", "Teams"),
            Path.Combine(RoamingAppData, "Code"),
            Path.Combine(RoamingAppData, "Slack"),
        };
        foreach (var app in electronApps)
        {
            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache" })
                yield return Path.Combine(app, sub);
        }

        yield return Path.Combine(RoamingAppData, "Code", "CachedData");

        // New Teams (Store app) keeps its web cache under EBWebView.
        var teams = Path.Combine(LocalAppData, "Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams", "EBWebView", "Default");
        foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
            yield return Path.Combine(teams, sub);

        // LINE: downloaded images/stickers cache (chats are stored elsewhere).
        yield return Path.Combine(LocalAppData, "LINE", "Cache");

        // Adobe Premiere/After Effects media cache, rebuilt when a project opens.
        yield return Path.Combine(RoamingAppData, "Adobe", "Common", "Media Cache Files");
        yield return Path.Combine(RoamingAppData, "Adobe", "Common", "Media Cache");
        yield return Path.Combine(LocalAppData, "Steam", "htmlcache");
        yield return Path.Combine(LocalAppData, "EpicGamesLauncher", "Saved", "webcache");
        yield return Path.Combine(LocalAppData, "EpicGamesLauncher", "Saved", "webcache_4147");
        yield return Path.Combine(LocalAppData, "EpicGamesLauncher", "Saved", "webcache_4430");
    }

    /// <summary>
    /// Temporary data of Microsoft Store apps: their web cache and the
    /// TempState folder Windows itself may clear at any time. Never
    /// LocalState/LocalCache, which hold the apps' real data.
    /// </summary>
    private static IEnumerable<string> StoreAppCacheDirs()
    {
        var packages = Path.Combine(LocalAppData, "Packages");
        List<string> apps;
        try { apps = Directory.EnumerateDirectories(packages).ToList(); }
        catch { yield break; }
        foreach (var app in apps)
        {
            yield return Path.Combine(app, "AC", "INetCache");
            yield return Path.Combine(app, "AC", "Temp");
            yield return Path.Combine(app, "TempState");
        }
    }

    /// <summary>Download caches of developer tools; they download again what a build needs.</summary>
    private static IEnumerable<string> DevCacheDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(LocalAppData, "npm-cache");
        yield return Path.Combine(LocalAppData, "pip", "Cache");
        yield return Path.Combine(LocalAppData, "Yarn", "Cache");
        yield return Path.Combine(LocalAppData, "NuGet", "v3-cache");
        yield return Path.Combine(LocalAppData, "NuGet", "plugins-cache");
        yield return Path.Combine(LocalAppData, "go-build");
        yield return Path.Combine(LocalAppData, "Composer");
        yield return Path.Combine(home, ".gradle", "caches");
        yield return Path.Combine(home, ".cargo", "registry", "cache");
        yield return Path.Combine(home, ".bun", "install", "cache");
    }

    #region CapCut

    private static readonly string[] CapCutProcesses = ["CapCut", "JianyingPro"];
    private static readonly string[] CapCutApps = ["CapCut", "JianyingPro"];
    private static readonly string[] CefCacheNames = ["Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "ShaderCache"];
    private static readonly global::System.Text.RegularExpressions.Regex CapCutProxyName =
        new(@"^[0-9a-f]{32}_\d{2,5}_\d{2,5}\.(agency|mp4)$", global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// CapCut (and its Chinese edition JianyingPro): every old version it keeps
    /// after each update (1.5–1.8 GB each — only the one named in
    /// Apps\ProductInfo.xml runs), and its caches wherever its own settings put
    /// them: effects, images, proxy videos, pre-renders, the built-in browser's
    /// cache, logs and crash dumps. Projects, presets, screen recordings and
    /// downloaded materials are never touched. Caches are only cleaned while
    /// CapCut is closed; old versions can go while it runs.
    /// </summary>
    private static void VisitCapCut(CleanScanResult scan, CleanExecResult? clean, CancellationToken token)
    {
        bool running = CapCutProcesses.Any(name =>
        {
            try
            {
                var found = Process.GetProcessesByName(name);
                foreach (var p in found) p.Dispose();
                return found.Length > 0;
            }
            catch
            {
                return false;
            }
        });

        foreach (var dir in CapCutOldVersions())
        {
            Visit(dir, scan, clean, token);
            if (clean != null) TryRemoveEmptyFolder(dir);
        }

        var caches = CapCutCaches(out var strays);
        foreach (var dir in caches)
        {
            if (running)
            {
                VisitSizeOnly(dir, scan, token, countAsInUse: true);
                continue;
            }
            Visit(dir, scan, clean, token);
        }
        if (!running)
            foreach (var dir in strays)
                Visit(dir, scan, clean, token, recursive: false, filter: name => CapCutProxyName.IsMatch(name));

        if (running && scan.InUseCount > 0)
        {
            scan.NoteCode = "AppRunning";
            scan.NoteArg = "CapCut";
            if (clean != null)
            {
                clean.NoteCode = "AppRunning";
                clean.NoteArg = "CapCut";
            }
        }
    }

    private static IEnumerable<string> CapCutRoots() =>
        CapCutApps.Select(name => Path.Combine(LocalAppData, name)).Where(Directory.Exists);

    /// <summary>Old version folders under Apps — never when the running version can't be told.</summary>
    private static List<string> CapCutOldVersions()
    {
        var old = new List<string>();
        foreach (var root in CapCutRoots())
        {
            var apps = Path.Combine(root, "Apps");
            string? current = null;
            try
            {
                var xml = global::System.Xml.Linq.XDocument.Load(Path.Combine(apps, "ProductInfo.xml"));
                current = xml.Descendants("full_appver").FirstOrDefault()?.Attribute("value")?.Value.Trim();
            }
            catch
            {
            }
            if (string.IsNullOrEmpty(current) || !Directory.Exists(Path.Combine(apps, current))) continue;

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(apps))
                {
                    var name = Path.GetFileName(dir);
                    if (Version.TryParse(name, out _) && !name.Equals(current, StringComparison.OrdinalIgnoreCase) &&
                        (File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0)
                        old.Add(dir);
                }
            }
            catch
            {
            }
        }
        return old;
    }

    /// <summary>
    /// The cache folders — the defaults and wherever CapCut's globalSetting moved
    /// them — with folders inside another one dropped so nothing counts twice.
    /// <paramref name="strays"/>: folders next to the projects folder where proxy
    /// videos are sometimes left behind.
    /// </summary>
    private static List<string> CapCutCaches(out List<string> strays)
    {
        var candidates = new List<string>();
        strays = new List<string>();
        foreach (var root in CapCutRoots())
        {
            var userData = Path.Combine(root, "User Data");
            var settings = CapCutSettings(Path.Combine(userData, "Config", "globalSetting"));
            candidates.Add(Path.Combine(userData, "Cache"));
            foreach (var key in new[] { "currentCachePath", "agencyFileLocation", "remuxFileLocation", "prerenderCacheLocation" })
                if (settings.TryGetValue(key, out var value) && IsCapCutCacheFolder(value, userData))
                    candidates.Add(value);
            candidates.AddRange(CefCaches(Path.Combine(userData, "CEF")));
            candidates.Add(Path.Combine(userData, "Log"));
            candidates.Add(Path.Combine(userData, "VELog"));
            candidates.Add(Path.Combine(userData, "Crash"));

            if (settings.TryGetValue("currentCustomDraftPath", out var drafts) && Path.GetDirectoryName(drafts.TrimEnd('\\')) is { Length: > 3 } parent &&
                Directory.Exists(parent))
                strays.Add(parent);
        }

        var result = new List<string>();
        foreach (var dir in candidates.Select(c => Path.TrimEndingDirectorySeparator(Path.GetFullPath(c))).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(c => c.Length))
        {
            if (!Directory.Exists(dir)) continue;
            if (result.Any(r => dir.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(dir);
        }
        return result;
    }

    /// <summary>CapCut's settings file: key=value lines, Windows paths written with doubled backslashes.</summary>
    private static Dictionary<string, string> CapCutSettings(string file)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) return values;
            foreach (var line in File.ReadLines(file))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var value = line[(eq + 1)..].Trim().Replace(@"\\", @"\").Replace('/', '\\');
                if (value.Length > 3 && value[1] == ':' && value[2] == '\\') values[line[..eq].Trim()] = value;
            }
        }
        catch
        {
        }
        return values;
    }

    /// <summary>
    /// A folder the settings point at is cleaned only when it is CapCut's own:
    /// inside its User Data, or holding its cache's tell-tale contents. Never a
    /// drive root, Windows, Program Files or the top of the user's own folders.
    /// </summary>
    private static bool IsCapCutCacheFolder(string path, string userData)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (full.Length <= 3 || !Directory.Exists(full)) return false;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] forbidden =
            [
                WinDir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), home,
                Path.Combine(home, "Desktop"), Path.Combine(home, "Documents"), Path.Combine(home, "Downloads"),
                Path.Combine(home, "Pictures"), Path.Combine(home, "Videos"), Path.Combine(home, "Music")
            ];
            if (forbidden.Any(f => f.Length > 3 && (full.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                                                    f.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))))
                return false;
            if (full.StartsWith(Path.TrimEndingDirectorySeparator(userData) + "\\", StringComparison.OrdinalIgnoreCase)) return true;

            // Moved elsewhere: it must look like CapCut's cache.
            return Directory.Exists(Path.Combine(full, "effect")) || Directory.Exists(Path.Combine(full, "agencycache")) ||
                   Directory.EnumerateFiles(full, "*.agency").Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Web cache folders of the built-in browser (never its cookies, logins or local storage).</summary>
    private static IEnumerable<string> CefCaches(string cefRoot)
    {
        if (!Directory.Exists(cefRoot)) yield break;
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((cefRoot, 0));
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).ToList(); }
            catch { continue; }
            foreach (var sub in subs)
            {
                if (CefCacheNames.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) yield return sub;
                else if (depth < 4) pending.Push((sub, depth + 1));
            }
        }
    }

    /// <summary>Removes a folder the clean emptied, when its parent is a real folder (no link swapped in).</summary>
    private static void TryRemoveEmptyFolder(string dir)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir));
        if (parent == null || VerifiedRoot(parent) is not { } verified) return;
        try
        {
            // Subfolders left empty by the clean go first.
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
                TryDeleteEmptyDirectory(sub, verified);
        }
        catch
        {
        }
        TryDeleteEmptyDirectory(dir, verified);
    }

    #endregion

    /// <summary>Unpacked driver installers that stay behind after the driver is installed.</summary>
    private static IEnumerable<string> InstallerLeftoverDirs()
    {
        yield return Path.Combine(SystemRoot, "NVIDIA");
        yield return Path.Combine(SystemRoot, "AMD");
        yield return Path.Combine(ProgramData, "NVIDIA Corporation", "Downloader");
    }

    private static IEnumerable<string> ErrorReportDirs()
    {
        yield return Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive");
        yield return Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue");
        yield return Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "Temp");
        yield return Path.Combine(LocalAppData, "Microsoft", "Windows", "WER", "ReportArchive");
        yield return Path.Combine(LocalAppData, "Microsoft", "Windows", "WER", "ReportQueue");
        yield return Path.Combine(LocalAppData, "CrashDumps");
        yield return Path.Combine(WinDir, "Minidump");
        yield return Path.Combine(WinDir, "LiveKernelReports");
    }

    private static IEnumerable<string> ShaderCacheDirs()
    {
        yield return Path.Combine(LocalAppData, "D3DSCache");
        yield return Path.Combine(LocalAppData, "NVIDIA", "DXCache");
        yield return Path.Combine(LocalAppData, "NVIDIA", "GLCache");
        yield return Path.Combine(LocalLow, "NVIDIA", "PerDriverVersion", "DXCache");
        yield return Path.Combine(LocalAppData, "AMD", "DxCache");
        yield return Path.Combine(LocalAppData, "AMD", "DxcCache");
        yield return Path.Combine(LocalAppData, "AMD", "GLCache");
        yield return Path.Combine(LocalAppData, "AMD", "VkCache");
        yield return Path.Combine(LocalLow, "Intel", "ShaderCache");
    }

    private static string WindowsUpdateDir => Path.Combine(WinDir, "SoftwareDistribution", "Download");
    private static string DeliveryOptimizationDir => Path.Combine(WinDir, "ServiceProfiles", "NetworkService",
        "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache");
    private static string ThumbnailDir => Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer");
    private static string LogsDir => Path.Combine(WinDir, "Logs");
    private static string MemoryDumpFile => Path.Combine(WinDir, "MEMORY.DMP");
    private static string WindowsOldDir => Path.Combine(Path.GetPathRoot(WinDir) ?? @"C:\", "Windows.old");

    private static bool IsThumbnailCache(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        return name.StartsWith("thumbcache_") || name.StartsWith("iconcache_");
    }

    #endregion

    #region Scan

    public static CleanScanResult Scan(CleanTarget target, CancellationToken token = default)
    {
        var result = new CleanScanResult { Target = target };

        switch (target)
        {
            case CleanTarget.TempFiles:
                foreach (var dir in TempDirs()) Visit(dir, result, null, token, keepNewerThan: DateTime.Now - TempKeepWindow);
                break;

            case CleanTarget.BrowserCache:
                foreach (var dir in BrowserCacheDirs()) Visit(dir, result, null, token);
                NoteRunningApps(result, BrowserProcesses);
                break;

            case CleanTarget.AppCache:
                foreach (var dir in AppCacheDirs()) Visit(dir, result, null, token);
                NoteRunningApps(result, AppProcesses);
                break;

            case CleanTarget.WindowsUpdate:
                if (IsRebootPending())
                {
                    result.NoteCode = "RebootPending";
                    VisitSizeOnly(WindowsUpdateDir, result, token, countAsInUse: true);
                    break;
                }
                Visit(WindowsUpdateDir, result, null, token);
                break;

            case CleanTarget.DeliveryOptimization:
                // Removed by Windows' own cmdlet, which also frees files DoSvc holds open.
                VisitSizeOnly(DeliveryOptimizationDir, result, token);
                break;

            case CleanTarget.ErrorReports:
                foreach (var dir in ErrorReportDirs()) Visit(dir, result, null, token);
                VisitSingleFile(MemoryDumpFile, result, clean: null);
                break;

            case CleanTarget.Thumbnails:
                Visit(ThumbnailDir, result, null, token, recursive: false, filter: IsThumbnailCache);
                if (result.InUseCount > 0) result.NoteCode = "ExplorerLocked";
                break;

            case CleanTarget.LogFiles:
                Visit(LogsDir, result, null, token);
                break;

            case CleanTarget.ShaderCache:
                foreach (var dir in ShaderCacheDirs()) Visit(dir, result, null, token);
                break;

            case CleanTarget.RecycleBin:
                if (QueryRecycleBin(out long binBytes, out long binItems))
                {
                    result.Bytes = binBytes;
                    result.FileCount = (int)Math.Min(binItems, int.MaxValue);
                }
                break;

            case CleanTarget.StoreAppCache:
                foreach (var dir in StoreAppCacheDirs()) Visit(dir, result, null, token);
                break;

            case CleanTarget.InstallerLeftovers:
                foreach (var dir in InstallerLeftoverDirs()) Visit(dir, result, null, token);
                break;

            case CleanTarget.DevCaches:
                foreach (var dir in DevCacheDirs()) Visit(dir, result, null, token);
                NoteRunningApps(result, DevProcesses);
                break;

            case CleanTarget.CapCut:
                VisitCapCut(result, null, token);
                break;

            case CleanTarget.WindowsSetupFiles or CleanTarget.DefenderFiles or CleanTarget.WindowsMisc or
                CleanTarget.UpdateCleanup or CleanTarget.DriverPackages or CleanTarget.LanguagePacks or CleanTarget.WindowsEsd:
                var measured = VolumeCacheHost.Measure(WindowsHandlers[target], SystemRoot, HandlerTimeout(target, false), token);
                result.Bytes = measured.Bytes;
                result.FileCount = -1; // Windows reports space, not files
                result.NoteCode = measured.TimedOut ? "HandlerTimeout" : null;
                break;

            case CleanTarget.WindowsOld:
                result.Found = Directory.Exists(WindowsOldDir);
                if (result.Found)
                {
                    // Removed through Disk Cleanup's own handler (it owns the ACLs),
                    // so every file counts.
                    VisitSizeOnly(WindowsOldDir, result, token);
                    if (!File.Exists(CleanmgrPath)) result.NoteCode = "NoCleanmgr";
                }
                break;
        }

        return result;
    }

    #endregion

    #region Clean

    public static CleanExecResult Clean(CleanTarget target, CancellationToken token = default)
    {
        var result = new CleanExecResult { Target = target };
        var scanLike = new CleanScanResult { Target = target };

        switch (target)
        {
            case CleanTarget.TempFiles:
                foreach (var dir in TempDirs()) Visit(dir, scanLike, result, token, keepNewerThan: DateTime.Now - TempKeepWindow);
                break;

            case CleanTarget.BrowserCache:
                foreach (var dir in BrowserCacheDirs()) Visit(dir, scanLike, result, token);
                break;

            case CleanTarget.AppCache:
                foreach (var dir in AppCacheDirs()) Visit(dir, scanLike, result, token);
                break;

            case CleanTarget.WindowsUpdate:
                if (IsRebootPending())
                {
                    result.NoteCode = "RebootPending";
                    break;
                }
                Visit(WindowsUpdateDir, scanLike, result, token);
                break;

            case CleanTarget.DeliveryOptimization:
                CleanDeliveryOptimization(result, token);
                break;

            case CleanTarget.ErrorReports:
                foreach (var dir in ErrorReportDirs()) Visit(dir, scanLike, result, token);
                VisitSingleFile(MemoryDumpFile, scanLike, result);
                break;

            case CleanTarget.Thumbnails:
                Visit(ThumbnailDir, scanLike, result, token, recursive: false, filter: IsThumbnailCache);
                if (result.FilesSkipped > 0) result.NoteCode = "ExplorerLocked";
                break;

            case CleanTarget.LogFiles:
                Visit(LogsDir, scanLike, result, token);
                break;

            case CleanTarget.ShaderCache:
                foreach (var dir in ShaderCacheDirs()) Visit(dir, scanLike, result, token);
                break;

            case CleanTarget.RecycleBin:
                QueryRecycleBin(out long queuedBytes, out long queuedItems);
                int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                if (hr == 0)
                {
                    result.BytesFreed = queuedBytes;
                    result.FilesDeleted = (int)Math.Min(queuedItems, int.MaxValue);
                }
                // E_UNEXPECTED (0x8000FFFF) means the bin was already empty.
                break;

            case CleanTarget.WindowsOld:
                CleanWindowsOld(result, token);
                break;

            case CleanTarget.StoreAppCache:
                foreach (var dir in StoreAppCacheDirs()) Visit(dir, scanLike, result, token);
                break;

            case CleanTarget.InstallerLeftovers:
                foreach (var dir in InstallerLeftoverDirs()) Visit(dir, scanLike, result, token);
                break;

            case CleanTarget.DevCaches:
                foreach (var dir in DevCacheDirs()) Visit(dir, scanLike, result, token);
                break;

            case CleanTarget.CapCut:
                VisitCapCut(scanLike, result, token);
                break;

            case CleanTarget.WindowsSetupFiles or CleanTarget.DefenderFiles or CleanTarget.WindowsMisc or
                CleanTarget.UpdateCleanup or CleanTarget.DriverPackages or CleanTarget.LanguagePacks or CleanTarget.WindowsEsd:
                var purged = VolumeCacheHost.Purge(WindowsHandlers[target], SystemRoot, HandlerTimeout(target, true), token);
                result.BytesFreed = purged.Bytes;
                result.FilesDeleted = -1; // Windows reports space, not files
                if (purged.TimedOut) result.NoteCode = "HandlerTimeout";
                else if (purged.HandlersFailed > 0) result.NoteCode = "HandlerPartial";
                break;
        }

        return result;
    }

    #endregion

    #region Walk + probe + delete

    /// <summary>
    /// Walks <paramref name="root"/> and, for every file, either measures what
    /// deleting it would free (scan: <paramref name="clean"/> is null) or
    /// deletes it (clean). Scan and clean apply exactly the same rules.
    /// </summary>
    private static void Visit(string root, CleanScanResult scan, CleanExecResult? clean, CancellationToken token,
        bool recursive = true, Func<string, bool>? filter = null, DateTime? keepNewerThan = null)
    {
        var verifiedRoot = VerifiedRoot(root);
        if (verifiedRoot == null) return;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };

        var pending = new Stack<string>();
        var visitedDirs = new List<string>();
        var files = new List<string>();
        pending.Push(verifiedRoot);

        // 1) Walk the tree (cheap, single thread).
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            if (!ReferenceEquals(dir, verifiedRoot)) visitedDirs.Add(dir);

            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList(); }
            catch { continue; }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo sub)
                {
                    // Never walk into junctions/symlinks.
                    if (recursive && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                        pending.Push(sub.FullName);
                    continue;
                }

                var file = (FileInfo)entry;
                if (filter != null && !filter(file.Name)) continue;

                if (keepNewerThan != null &&
                    (file.LastWriteTime > keepNewerThan || file.CreationTime > keepNewerThan) &&
                    !(KnownTempJunk.IsMatch(file.Name) && file.LastWriteTime < DateTime.Now - KnownJunkKeepWindow &&
                      file.CreationTime < DateTime.Now - KnownJunkKeepWindow))
                {
                    scan.RecentCount++;
                    if (clean != null) clean.FilesSkipped++;
                    continue;
                }

                files.Add(file.FullName);
            }
        }

        // 2) Probe/delete files in parallel: each one needs an open + path
        // check, which is slow one at a time on folders with 20k+ cache files.
        var gate = new object();
        Parallel.ForEach(files,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) },
            () => (Scan: new CleanScanResult { Target = scan.Target }, Clean: clean == null ? null : new CleanExecResult { Target = clean.Target }),
            (path, _, local) =>
            {
                ProcessFile(path, verifiedRoot, local.Scan, local.Clean);
                return local;
            },
            local =>
            {
                lock (gate)
                {
                    Merge(scan, local.Scan);
                    if (clean != null && local.Clean != null) Merge(clean, local.Clean);
                }
            });

        if (clean != null)
        {
            // Remove folders emptied by the clean, deepest first; keep the root.
            foreach (var dir in visitedDirs.OrderByDescending(d => d.Length))
                TryDeleteEmptyDirectory(dir, verifiedRoot);
        }
    }

    private static void Merge(CleanScanResult into, CleanScanResult part)
    {
        into.Bytes += part.Bytes;
        into.FileCount += part.FileCount;
        into.InUseCount += part.InUseCount;
        into.InUseBytes += part.InUseBytes;
        into.RecentCount += part.RecentCount;
        into.DeniedCount += part.DeniedCount;
    }

    private static void Merge(CleanExecResult into, CleanExecResult part)
    {
        into.BytesFreed += part.BytesFreed;
        into.FilesDeleted += part.FilesDeleted;
        into.FilesSkipped += part.FilesSkipped;
    }

    private static void VisitSingleFile(string path, CleanScanResult scan, CleanExecResult? clean)
    {
        if (!File.Exists(path)) return;
        var parent = VerifiedRoot(Path.GetDirectoryName(path)!);
        if (parent != null) ProcessFile(path, parent, scan, clean);
    }

    /// <summary>Sizes only (for folders removed by a Windows tool, not by us).</summary>
    private static void VisitSizeOnly(string root, CleanScanResult result, CancellationToken token, bool countAsInUse = false)
    {
        if (!Directory.Exists(root)) return;
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                token.ThrowIfCancellationRequested();
                if (countAsInUse)
                {
                    result.InUseCount++;
                    result.InUseBytes += file.Length;
                }
                else
                {
                    result.FileCount++;
                    result.Bytes += file.Length;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private static void ProcessFile(string path, string verifiedRoot, CleanScanResult scan, CleanExecResult? clean)
    {
        // Scan and clean open the file identically, so a scan predicts exactly
        // which files the clean will be able to delete.
        using var handle = CreateFileW(LongPrefix(path), DELETE | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) return; // already gone

            long size = SafeLength(path);
            if (error is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION)
            {
                scan.InUseCount++;
                scan.InUseBytes += size;
            }
            else
            {
                scan.DeniedCount++;
            }
            if (clean != null) clean.FilesSkipped++;
            return;
        }

        // The handle must point inside the folder we meant to clean.
        var finalPath = FinalPath(handle);
        if (finalPath == null || !finalPath.StartsWith(verifiedRoot + "\\", StringComparison.OrdinalIgnoreCase))
        {
            scan.DeniedCount++;
            if (clean != null) clean.FilesSkipped++;
            return;
        }

        long freed = 0;
        if (GetFileInformationByHandleEx(handle, FileStandardInfoClass, out FILE_STANDARD_INFO info, (uint)Marshal.SizeOf<FILE_STANDARD_INFO>()))
        {
            if (info.Directory) return;
            // Deleting one link of a hard-linked file frees nothing.
            freed = info.NumberOfLinks <= 1 ? info.AllocationSize : 0;
        }

        if (clean == null)
        {
            scan.FileCount++;
            scan.Bytes += freed;
            return;
        }

        if (DeleteByHandle(handle))
        {
            clean.FilesDeleted++;
            clean.BytesFreed += freed;
        }
        else
        {
            clean.FilesSkipped++;
        }
    }

    /// <summary>
    /// Deletes one file the user picked, through a handle that is checked to
    /// be exactly that path (a junction or link swapped in after the scan
    /// can't redirect this elevated delete). Read-only files are deleted too.
    /// </summary>
    public static bool DeleteFileVerified(string path, out long freed)
    {
        freed = 0;
        try
        {
            var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LongPath(path)));
            using var handle = CreateFileW(LongPrefix(expected), DELETE | FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (handle.IsInvalid) return false;

            var final = FinalPath(handle);
            if (final == null || !final.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;

            long size = 0;
            if (GetFileInformationByHandleEx(handle, FileStandardInfoClass, out FILE_STANDARD_INFO info, (uint)Marshal.SizeOf<FILE_STANDARD_INFO>()))
            {
                if (info.Directory) return false;
                size = info.NumberOfLinks <= 1 ? info.AllocationSize : 0;
            }
            if (!DeleteByHandle(handle)) return false;
            freed = size;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool DeleteByHandle(SafeFileHandle handle)
    {
        var ex = new FILE_DISPOSITION_INFO_EX
        {
            Flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE
        };
        if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref ex, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO_EX>()))
            return true;

        // Windows 10 before 1709: classic delete-on-close (read-only files stay).
        var classic = new FILE_DISPOSITION_INFO { DeleteFile = true };
        return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref classic, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO>());
    }

    private static void TryDeleteEmptyDirectory(string dir, string verifiedRoot)
    {
        try
        {
            if (Directory.EnumerateFileSystemEntries(dir).Any()) return;

            using var handle = CreateFileW(LongPrefix(dir), DELETE | FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid) return;

            var finalPath = FinalPath(handle);
            if (finalPath == null || !finalPath.StartsWith(verifiedRoot + "\\", StringComparison.OrdinalIgnoreCase)) return;

            var classic = new FILE_DISPOSITION_INFO { DeleteFile = true };
            SetFileInformationByHandle(handle, FileDispositionInfoClass, ref classic, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO>());
        }
        catch { }
    }

    /// <summary>
    /// Returns the folder's real path if it exists and no part of it is a
    /// junction/symlink (its final path equals its normal path); otherwise null.
    /// </summary>
    private static string? VerifiedRoot(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LongPath(root)));

            using var handle = CreateFileW(LongPrefix(expected), FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            var final = FinalPath(handle);
            return final != null && final.Equals(expected, StringComparison.OrdinalIgnoreCase) ? final : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) return null;
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return null;
        }

        var path = buffer.ToString(0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return null;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(path);
    }

    /// <summary>Expands 8.3 short names (e.g. C:\Users\ADMINI~1) so paths compare correctly.</summary>
    private static string LongPath(string path)
    {
        var buffer = new StringBuilder(1024);
        uint length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString(0, (int)length) : path;
    }

    private static string LongPrefix(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path;

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    #endregion

    #region Windows-managed categories

    private static readonly string[] BrowserProcesses = ["chrome", "msedge", "brave", "vivaldi", "opera", "firefox", "browser", "chromium"];
    private static readonly string[] AppProcesses = ["discord", "teams", "ms-teams", "code", "slack", "steamwebhelper", "epicgameslauncher", "line", "adobe premiere pro", "afterfx"];
    private static readonly string[] DevProcesses = ["devenv", "msbuild", "dotnet", "node", "java", "gradle", "go", "cargo", "python"];

    private static void NoteRunningApps(CleanScanResult result, string[] processNames)
    {
        if (result.InUseCount == 0) return;
        var running = processNames
            .Where(name => { try { var p = Process.GetProcessesByName(name); var any = p.Length > 0; foreach (var x in p) x.Dispose(); return any; } catch { return false; } })
            .ToList();
        if (running.Count == 0) return;
        result.NoteCode = "AppRunning";
        result.NoteArg = string.Join(", ", running);
    }

    /// <summary>
    /// True when Windows Update has installed something that finishes on the
    /// next restart — its downloaded files may still be needed then.
    /// </summary>
    public static bool IsRebootPending()
    {
        try
        {
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wu != null) return true;
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return cbs != null;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanDeliveryOptimization(CleanExecResult result, CancellationToken token)
    {
        var before = new CleanScanResult();
        VisitSizeOnly(DeliveryOptimizationDir, before, token);
        if (before.FileCount == 0) return;

        // Windows' own cleanup (it can remove files the service holds open).
        bool cmdletOk = RunHidden("powershell",
            "-NoProfile -NonInteractive -Command \"Delete-DeliveryOptimizationCache -Force -ErrorAction Stop\"",
            TimeSpan.FromMinutes(3), token) == 0;

        if (!cmdletOk)
        {
            // Older Windows: delete what isn't in use.
            var scanLike = new CleanScanResult();
            Visit(DeliveryOptimizationDir, scanLike, result, token);
            return;
        }

        var after = new CleanScanResult();
        VisitSizeOnly(DeliveryOptimizationDir, after, token);
        result.BytesFreed = Math.Max(0, before.Bytes - after.Bytes);
        result.FilesDeleted = Math.Max(0, before.FileCount - after.FileCount);
        result.FilesSkipped = after.FileCount;
    }

    private static string CleanmgrPath => Path.Combine(Environment.SystemDirectory, "cleanmgr.exe");

    /// <summary>
    /// Removes Windows.old with Disk Cleanup's "Previous Installations"
    /// handler — the supported way; it takes ownership of the protected files.
    /// </summary>
    private static void CleanWindowsOld(CleanExecResult result, CancellationToken token)
    {
        if (!Directory.Exists(WindowsOldDir)) return;

        if (!File.Exists(CleanmgrPath))
        {
            result.NoteCode = "NoCleanmgr";
            return;
        }

        var before = new CleanScanResult();
        VisitSizeOnly(WindowsOldDir, before, token);

        const string handlerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Previous Installations";
        const string flagName = "StateFlags0777";
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(handlerKey, writable: true))
            {
                if (key == null)
                {
                    result.NoteCode = "NoCleanmgr";
                    return;
                }
                key.SetValue(flagName, 2, RegistryValueKind.DWord);
            }

            RunHidden(CleanmgrPath, "/sagerun:777", TimeSpan.FromMinutes(45), token);
        }
        finally
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(handlerKey, writable: true);
                key?.DeleteValue(flagName, throwOnMissingValue: false);
            }
            catch { }
        }

        var after = new CleanScanResult();
        VisitSizeOnly(WindowsOldDir, after, token);
        result.BytesFreed = Math.Max(0, before.Bytes - after.Bytes);
        result.FilesDeleted = Math.Max(0, before.FileCount - after.FileCount);
        result.FilesSkipped = after.FileCount;
        if (after.FileCount > 0) result.NoteCode = "WindowsOldPartial";
    }

    private static int RunHidden(string fileName, string arguments, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null) return -1;

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            var deadline = DateTime.UtcNow + timeout;
            while (!process.WaitForExit(250))
            {
                if (token.IsCancellationRequested || DateTime.UtcNow > deadline)
                {
                    try { process.Kill(); } catch { }
                    token.ThrowIfCancellationRequested();
                    return -1;
                }
            }
            return process.ExitCode;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return -1;
        }
    }

    #endregion

    #region Native

    private const uint DELETE = 0x00010000;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;

    private const int FileStandardInfoClass = 1;
    private const int FileDispositionInfoClass = 4;
    private const int FileDispositionInfoExClass = 21;

    private const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
    private const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2;
    private const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO
    {
        [MarshalAs(UnmanagedType.U1)] public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO_EX
    {
        public uint Flags;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass,
        out FILE_STANDARD_INFO lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass,
        ref FILE_DISPOSITION_INFO_EX lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass,
        ref FILE_DISPOSITION_INFO lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);

    // shellapi.h packs this struct to 1 byte only in 32-bit builds; 64-bit
    // Windows expects natural alignment (24 bytes). The wrong size made
    // SHQueryRecycleBin fail, so the Recycle Bin always showed 0 bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO64
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SHQUERYRBINFO32
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin64(string? pszRootPath, ref SHQUERYRBINFO64 pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin32(string? pszRootPath, ref SHQUERYRBINFO32 pSHQueryRBInfo);

    /// <summary>Size and item count of the Recycle Bins of all drives.</summary>
    private static bool QueryRecycleBin(out long bytes, out long items)
    {
        if (Environment.Is64BitProcess)
        {
            var info = new SHQUERYRBINFO64 { cbSize = Marshal.SizeOf<SHQUERYRBINFO64>() };
            bool ok = SHQueryRecycleBin64(null, ref info) == 0;
            (bytes, items) = ok ? (info.i64Size, info.i64NumItems) : (0, 0);
            return ok;
        }
        var info32 = new SHQUERYRBINFO32 { cbSize = Marshal.SizeOf<SHQUERYRBINFO32>() };
        bool ok32 = SHQueryRecycleBin32(null, ref info32) == 0;
        (bytes, items) = ok32 ? (info32.i64Size, info32.i64NumItems) : (0, 0);
        return ok32;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    #endregion
}

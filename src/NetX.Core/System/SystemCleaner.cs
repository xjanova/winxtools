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
    WindowsOld
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
        new(CleanTarget.ErrorReports, "WER", CleanRisk.Safe, true),
        new(CleanTarget.Thumbnails, "Thumbs", CleanRisk.Safe, true),
        new(CleanTarget.LogFiles, "Logs", CleanRisk.Safe, true),
        new(CleanTarget.DeliveryOptimization, "DO", CleanRisk.Safe, true),
        new(CleanTarget.WindowsUpdate, "WU", CleanRisk.Medium, false),
        new(CleanTarget.ShaderCache, "Shaders", CleanRisk.Medium, false),
        new(CleanTarget.RecycleBin, "Recycle", CleanRisk.Medium, false),
        new(CleanTarget.WindowsOld, "WinOld", CleanRisk.Caution, false),
    ];

    // Temp files touched within this window may still belong to a running installer.
    private static readonly TimeSpan TempKeepWindow = TimeSpan.FromHours(24);

    #region Target folders

    private static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string LocalLow => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

    private static IEnumerable<string> TempDirs()
    {
        var userTemp = Path.GetTempPath();
        if (IsSafeTempRoot(userTemp)) yield return userTemp;
        yield return Path.Combine(WinDir, "Temp");
        yield return Path.Combine(WinDir, "SystemTemp"); // Windows 11 24H2+
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

            var leaf = Path.GetFileName(full);
            if (!leaf.Equals("Temp", StringComparison.OrdinalIgnoreCase) &&
                !leaf.Equals("Tmp", StringComparison.OrdinalIgnoreCase))
                return false;

            string[] forbidden =
            [
                WinDir,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
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
        yield return Path.Combine(LocalAppData, "Steam", "htmlcache");
        yield return Path.Combine(LocalAppData, "EpicGamesLauncher", "Saved", "webcache");
        yield return Path.Combine(LocalAppData, "EpicGamesLauncher", "Saved", "webcache_4147");
        yield return Path.Combine(LocalAppData, "EpicGamesLauncher", "Saved", "webcache_4430");
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
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                if (SHQueryRecycleBin(null, ref info) == 0)
                {
                    result.Bytes = info.i64Size;
                    result.FileCount = (int)Math.Min(info.i64NumItems, int.MaxValue);
                }
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
                var query = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                SHQueryRecycleBin(null, ref query);
                int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                if (hr == 0)
                {
                    result.BytesFreed = query.i64Size;
                    result.FilesDeleted = (int)Math.Min(query.i64NumItems, int.MaxValue);
                }
                // E_UNEXPECTED (0x8000FFFF) means the bin was already empty.
                break;

            case CleanTarget.WindowsOld:
                CleanWindowsOld(result, token);
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
                    (file.LastWriteTime > keepNewerThan || file.CreationTime > keepNewerThan))
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
    private static readonly string[] AppProcesses = ["discord", "teams", "code", "slack", "steamwebhelper", "epicgameslauncher"];

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

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    #endregion
}

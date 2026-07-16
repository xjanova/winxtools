using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetX.Core.System;

public enum CleanTarget
{
    TempFiles,
    BrowserCache,
    WindowsUpdate,
    RecycleBin,
    Thumbnails,
    LogFiles,
    WindowsOld
}

public sealed class CleanScanResult
{
    public CleanTarget Target { get; init; }
    public long Bytes { get; set; }
    public int FileCount { get; set; }
    public bool Found { get; set; } = true;
}

public sealed class CleanExecResult
{
    public CleanTarget Target { get; init; }
    public long BytesFreed { get; set; }
    public int FilesDeleted { get; set; }
    public int FilesSkipped { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Real file-cleanup engine. Every number reported comes from actually walking
/// the disk (scan) or from the bytes of files that were actually deleted
/// (clean) — never estimated or fabricated. Locked/in-use files are skipped
/// silently and counted as skipped.
/// </summary>
public static class SystemCleaner
{
    #region Target paths

    private static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static IEnumerable<string> TempDirs()
    {
        yield return Path.GetTempPath();
        yield return Path.Combine(WinDir, "Temp");
    }

    /// <summary>
    /// Cache directories of the browsers we know how to clean safely.
    /// Only cache/code-cache/GPU-cache folders — never cookies, history,
    /// passwords or other profile data.
    /// </summary>
    private static IEnumerable<string> BrowserCacheDirs()
    {
        // Chromium-family: <root>\User Data\<profile>\{Cache, Code Cache, GPUCache}
        var chromiumRoots = new[]
        {
            Path.Combine(LocalAppData, "Google", "Chrome", "User Data"),
            Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data"),
            Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data"),
            Path.Combine(LocalAppData, "Opera Software", "Opera Stable"),
        };

        foreach (var root in chromiumRoots)
        {
            if (!Directory.Exists(root)) continue;

            // Opera keeps caches directly under the root; Chrome/Edge/Brave per profile
            var profileDirs = new List<string> { root };
            try
            {
                profileDirs.AddRange(Directory.EnumerateDirectories(root)
                    .Where(d =>
                    {
                        var name = Path.GetFileName(d);
                        return name == "Default" || name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase);
                    }));
            }
            catch { }

            foreach (var profile in profileDirs)
            {
                foreach (var sub in new[] { "Cache", Path.Combine("Cache", "Cache_Data"), "Code Cache", "GPUCache" })
                {
                    var dir = Path.Combine(profile, sub);
                    if (Directory.Exists(dir)) yield return dir;
                }
            }
        }

        // Firefox: Profiles\<xyz>\cache2
        var ffProfiles = Path.Combine(LocalAppData, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(ffProfiles))
        {
            IEnumerable<string> profiles = [];
            try { profiles = Directory.EnumerateDirectories(ffProfiles); } catch { }
            foreach (var profile in profiles)
            {
                var cache = Path.Combine(profile, "cache2");
                if (Directory.Exists(cache)) yield return cache;
            }
        }
    }

    private static string WindowsUpdateDir => Path.Combine(WinDir, "SoftwareDistribution", "Download");
    private static string ThumbnailDir => Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer");
    private static string LogsDir => Path.Combine(WinDir, "Logs");
    private static string WindowsOldDir =>
        Path.Combine(Path.GetPathRoot(WinDir) ?? @"C:\", "Windows.old");

    #endregion

    #region Scan (real sizes only)

    public static CleanScanResult Scan(CleanTarget target)
    {
        var result = new CleanScanResult { Target = target };

        switch (target)
        {
            case CleanTarget.TempFiles:
                foreach (var dir in TempDirs()) AddDirSize(dir, result);
                break;

            case CleanTarget.BrowserCache:
                foreach (var dir in BrowserCacheDirs()) AddDirSize(dir, result);
                break;

            case CleanTarget.WindowsUpdate:
                AddDirSize(WindowsUpdateDir, result);
                break;

            case CleanTarget.RecycleBin:
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                if (SHQueryRecycleBin(null, ref info) == 0)
                {
                    result.Bytes = info.i64Size;
                    result.FileCount = (int)Math.Min(info.i64NumItems, int.MaxValue);
                }
                break;

            case CleanTarget.Thumbnails:
                foreach (var file in SafeEnumerateFiles(ThumbnailDir, recursive: false))
                {
                    var name = Path.GetFileName(file).ToLowerInvariant();
                    if (name.StartsWith("thumbcache_") || name.StartsWith("iconcache_"))
                    {
                        result.Bytes += SafeFileLength(file);
                        result.FileCount++;
                    }
                }
                break;

            case CleanTarget.LogFiles:
                AddDirSize(LogsDir, result);
                break;

            case CleanTarget.WindowsOld:
                result.Found = Directory.Exists(WindowsOldDir);
                if (result.Found) AddDirSize(WindowsOldDir, result);
                break;
        }

        return result;
    }

    #endregion

    #region Clean (real deletions only)

    public static CleanExecResult Clean(CleanTarget target)
    {
        var result = new CleanExecResult { Target = target };

        switch (target)
        {
            case CleanTarget.TempFiles:
                // Skip files touched in the last hour — likely still in use.
                var cutoff = DateTime.Now.AddHours(-1);
                foreach (var dir in TempDirs())
                    DeleteDirContents(dir, result, olderThan: cutoff);
                break;

            case CleanTarget.BrowserCache:
                foreach (var dir in BrowserCacheDirs())
                    DeleteDirContents(dir, result);
                break;

            case CleanTarget.WindowsUpdate:
                DeleteDirContents(WindowsUpdateDir, result);
                if (result.FilesSkipped > 0)
                    result.Note = "Some files were in use by Windows Update.";
                break;

            case CleanTarget.RecycleBin:
                var query = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                SHQueryRecycleBin(null, ref query);
                var hr = SHEmptyRecycleBin(IntPtr.Zero, null,
                    SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                if (hr == 0)
                {
                    result.BytesFreed = query.i64Size;
                    result.FilesDeleted = (int)Math.Min(query.i64NumItems, int.MaxValue);
                }
                // 0x8000FFFF (E_UNEXPECTED) commonly means the bin was already empty — not an error.
                break;

            case CleanTarget.Thumbnails:
                foreach (var file in SafeEnumerateFiles(ThumbnailDir, recursive: false))
                {
                    var name = Path.GetFileName(file).ToLowerInvariant();
                    if (!name.StartsWith("thumbcache_") && !name.StartsWith("iconcache_")) continue;
                    TryDeleteFile(file, result);
                }
                if (result.FilesSkipped > 0)
                    result.Note = "Some caches are locked by Explorer; they clear after a restart.";
                break;

            case CleanTarget.LogFiles:
                DeleteDirContents(LogsDir, result);
                break;

            case CleanTarget.WindowsOld:
                if (!Directory.Exists(WindowsOldDir)) break;
                DeleteDirContents(WindowsOldDir, result);
                TryDeleteEmptyDir(WindowsOldDir);
                if (result.FilesSkipped > 0)
                    result.Note = "Windows.old is partially protected — use Disk Cleanup (cleanmgr) to remove the rest.";
                break;
        }

        return result;
    }

    #endregion

    #region File helpers (exception-safe walkers)

    private static void AddDirSize(string root, CleanScanResult result)
    {
        foreach (var file in SafeEnumerateFiles(root, recursive: true))
        {
            result.Bytes += SafeFileLength(file);
            result.FileCount++;
        }
    }

    /// <summary>
    /// Recursive file enumeration that never throws mid-iteration: directories
    /// we cannot open (ACLs, reparse points) are skipped instead of aborting
    /// the whole walk. Symlinked directories are not followed.
    /// </summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root, bool recursive)
    {
        if (!Directory.Exists(root)) yield break;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            string[] files = [];
            try { files = Directory.GetFiles(dir); } catch { }
            foreach (var f in files) yield return f;

            if (!recursive) yield break;

            string[] subs = [];
            try { subs = Directory.GetDirectories(dir); } catch { }
            foreach (var sub in subs)
            {
                try
                {
                    // Don't follow directory symlinks/junctions out of the target tree
                    var attr = File.GetAttributes(sub);
                    if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                pending.Push(sub);
            }
        }
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static void DeleteDirContents(string root, CleanExecResult result, DateTime? olderThan = null)
    {
        if (!Directory.Exists(root)) return;

        foreach (var file in SafeEnumerateFiles(root, recursive: true))
        {
            if (olderThan != null)
            {
                try { if (File.GetLastWriteTime(file) > olderThan) { result.FilesSkipped++; continue; } }
                catch { result.FilesSkipped++; continue; }
            }
            TryDeleteFile(file, result);
        }

        // Sweep now-empty subdirectories (bottom-up), keep the root itself.
        try
        {
            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                                         .OrderByDescending(d => d.Length))
            {
                TryDeleteEmptyDir(dir);
            }
        }
        catch { }
    }

    private static void TryDeleteFile(string path, CleanExecResult result)
    {
        try
        {
            var size = SafeFileLength(path);
            File.SetAttributes(path, FileAttributes.Normal); // clear read-only
            File.Delete(path);
            result.BytesFreed += size;
            result.FilesDeleted++;
        }
        catch
        {
            result.FilesSkipped++; // in use / access denied — leave it
        }
    }

    private static void TryDeleteEmptyDir(string dir)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { }
    }

    #endregion

    #region Recycle Bin native

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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Management;
using System.Runtime.InteropServices;

namespace NetX.Core.System.Storage;

/// <summary>A folder and what it holds (sizes are the files' lengths).</summary>
public sealed class FolderNode
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public long Bytes { get; set; }
    public long Files { get; set; }
    public FolderNode? Parent { get; init; }

    /// <summary>Sub-folders of at least <see cref="SpaceAnalyzer.KeepFolderBytes"/>; smaller ones only add to <see cref="SmallBytes"/>.</summary>
    public List<FolderNode> Children { get; } = new();
    public long SmallBytes { get; set; }

    /// <summary>Windows or program files — shown, but WinXTools never deletes inside.</summary>
    public bool IsProtected { get; init; }
}

public sealed record BigFile(string Path, long Bytes, DateTime LastWriteUtc, bool IsProtected);

public enum SpaceFindingKind
{
    Hibernation,
    RestorePoints,
    RecycleBin,
    OldInstallers,
    OldArchives,
    DiscImages,
    VirtualDisks
}

/// <summary>Something that takes space and could go, with what deleting it costs the user.</summary>
public sealed class SpaceFinding
{
    public SpaceFindingKind Kind { get; init; }
    public long Bytes { get; init; }
    public int Count { get; init; }
    public IReadOnlyList<BigFile> Files { get; init; } = Array.Empty<BigFile>();
    public CleanRisk Risk { get; init; }
}

public sealed class SpaceScan
{
    public char Letter { get; init; }
    public FolderNode Root { get; init; } = new();
    public IReadOnlyList<BigFile> LargestFiles { get; init; } = Array.Empty<BigFile>();
    public IReadOnlyList<SpaceFinding> Findings { get; init; } = Array.Empty<SpaceFinding>();
    public long FileCount { get; init; }
    public long FolderCount { get; init; }
    public long DeniedFolders { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed record SpaceProgress(long Files, long Bytes, string? CurrentFolder);

/// <summary>
/// Finds what takes the space on a drive: the biggest folders and files, and
/// things that can be given back (hibernation file, restore points, old
/// installers and archives in Downloads, the Recycle Bin). It only reads;
/// deleting is a separate, confirmed step.
/// </summary>
public static class SpaceAnalyzer
{
    public const long KeepFolderBytes = 1L << 20;
    private const int LargestFileCount = 300;
    private static readonly TimeSpan OldDownload = TimeSpan.FromDays(30);

    private static readonly string[] InstallerExtensions = [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle"];
    private static readonly string[] ArchiveExtensions = [".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz"];
    // Only .iso: ".img" is also used by Android emulator and SDK system images.
    private static readonly string[] DiscImageExtensions = [".iso"];
    private static readonly string[] VirtualDiskExtensions = [".vhd", ".vhdx", ".vmdk", ".vdi", ".qcow2"];

    public static async Task<SpaceScan> ScanAsync(DriveVolume volume, IProgress<SpaceProgress>? progress, CancellationToken token)
    {
        return await Task.Run(() => Scan(volume, progress, token), token).ConfigureAwait(false);
    }

    private static SpaceScan Scan(DriveVolume volume, IProgress<SpaceProgress>? progress, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var root = new FolderNode { Name = volume.Root, Path = volume.Root };
        long files = 0, folders = 0, denied = 0, bytes = 0;
        var largest = new ConcurrentBag<PriorityQueue<BigFile, long>>();
        var lastReport = Stopwatch.StartNew();
        var reportGate = new object();

        void Report(string folder)
        {
            if (progress == null || lastReport.ElapsedMilliseconds < 250) return;
            lock (reportGate)
            {
                if (lastReport.ElapsedMilliseconds < 250) return;
                lastReport.Restart();
            }
            progress.Report(new SpaceProgress(Interlocked.Read(ref files), Interlocked.Read(ref bytes), folder));
        }

        // Files directly in the root, then one task per top-level folder.
        var top = new List<string>();
        var rootHeap = new PriorityQueue<BigFile, long>();
        foreach (var entry in Entries(volume.Root, ref denied))
        {
            if (entry.IsDirectory)
            {
                if (!entry.IsReparse) top.Add(entry.Path);
                continue;
            }
            root.Bytes += entry.Length;
            root.Files++;
            files++;
            bytes += entry.Length;
            Offer(rootHeap, new BigFile(entry.Path, entry.Length, entry.LastWriteUtc, IsProtectedPath(entry.Path)));
        }
        largest.Add(rootHeap);

        // Hard disks lose a lot to head movement when several walkers run at once.
        int parallel = volume.Disk?.IsHdd == true ? 2 : Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        var nodes = new FolderNode?[top.Count];
        Parallel.For(0, top.Count, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = token }, i =>
        {
            var heap = new PriorityQueue<BigFile, long>();
            long localDenied = 0, localFiles = 0, localFolders = 0;
            nodes[i] = Walk(top[i], root, heap, ref localFiles, ref localFolders, ref localDenied, token, (f, b, path) =>
            {
                Interlocked.Add(ref files, f);
                Interlocked.Add(ref bytes, b);
                Report(path);
            });
            Interlocked.Add(ref denied, localDenied);
            Interlocked.Add(ref folders, localFolders);
            largest.Add(heap);
        });

        foreach (var node in nodes)
        {
            if (node == null) continue;
            root.Bytes += node.Bytes;
            root.Files += node.Files;
            if (node.Bytes >= KeepFolderBytes) root.Children.Add(node);
            else root.SmallBytes += node.Bytes;
        }
        root.Children.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        var merged = largest.SelectMany(h => h.UnorderedItems.Select(x => x.Element))
            .OrderByDescending(f => f.Bytes).Take(LargestFileCount).ToList();

        token.ThrowIfCancellationRequested();
        var findings = FindReclaimable(volume, merged, token);

        return new SpaceScan
        {
            Letter = volume.Letter,
            Root = root,
            LargestFiles = merged,
            Findings = findings,
            FileCount = files,
            FolderCount = folders + 1,
            DeniedFolders = denied,
            Duration = sw.Elapsed
        };
    }

    private delegate void WalkReport(long files, long bytes, string path);

    private static FolderNode Walk(string path, FolderNode parent, PriorityQueue<BigFile, long> heap,
        ref long files, ref long folders, ref long denied, CancellationToken token, Action<long, long, string> report)
    {
        // Iterative post-order walk: a deep tree can't overflow the stack.
        var rootNode = new FolderNode { Name = global::System.IO.Path.GetFileName(path), Path = path, Parent = parent, IsProtected = IsProtectedPath(path) };
        var stack = new Stack<(FolderNode Node, List<string>? Pending, int Next)>();
        stack.Push((rootNode, null, 0));
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (node, pending, next) = stack.Pop();
            if (pending == null)
            {
                pending = new List<string>();
                long batchFiles = 0, batchBytes = 0;
                foreach (var entry in Entries(node.Path, ref denied))
                {
                    if (entry.IsDirectory)
                    {
                        if (!entry.IsReparse) pending.Add(entry.Path); // never follow junctions/symlinks
                        continue;
                    }
                    node.Bytes += entry.Length;
                    node.Files++;
                    batchFiles++;
                    batchBytes += entry.Length;
                    Offer(heap, new BigFile(entry.Path, entry.Length, entry.LastWriteUtc, node.IsProtected));
                }
                files += batchFiles;
                folders++;
                report(batchFiles, batchBytes, node.Path);
            }

            if (next < pending.Count)
            {
                stack.Push((node, pending, next + 1));
                var childPath = pending[next];
                var child = new FolderNode
                {
                    Name = global::System.IO.Path.GetFileName(childPath),
                    Path = childPath,
                    Parent = node,
                    IsProtected = node.IsProtected || IsProtectedPath(childPath)
                };
                stack.Push((child, null, 0));
                continue;
            }

            // Node complete: fold it into its parent.
            if (node.Parent is { } p && node != rootNode)
            {
                p.Bytes += node.Bytes;
                p.Files += node.Files;
                if (node.Bytes >= KeepFolderBytes) p.Children.Add(node);
                else p.SmallBytes += node.Bytes;
            }
            node.Children.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        }
        return rootNode;
    }

    private readonly record struct Entry(string Path, long Length, bool IsDirectory, bool IsReparse, DateTime LastWriteUtc);

    private static List<Entry> Entries(string directory, ref long denied)
    {
        try
        {
            var options = new global::System.IO.EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false
            };
            var enumerable = new FileSystemEnumerable<Entry>(directory,
                (ref FileSystemEntry e) => new Entry(e.ToFullPath(), e.IsDirectory ? 0 : e.Length, e.IsDirectory,
                    (e.Attributes & FileAttributes.ReparsePoint) != 0, e.LastWriteTimeUtc.UtcDateTime), options);
            return enumerable.ToList();
        }
        catch (UnauthorizedAccessException)
        {
            denied++;
            return new List<Entry>();
        }
        catch (Exception)
        {
            denied++;
            return new List<Entry>();
        }
    }

    private static void Offer(PriorityQueue<BigFile, long> heap, BigFile file)
    {
        if (file.Bytes < (16L << 20)) return;
        if (heap.Count < LargestFileCount) heap.Enqueue(file, file.Bytes);
        else if (heap.TryPeek(out _, out long smallest) && file.Bytes > smallest) heap.EnqueueDequeue(file, file.Bytes);
    }

    #region Protected locations

    private static readonly Lazy<string[]> ProtectedRoots = new(() =>
    {
        var list = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            global::System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft"),
            global::System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
        };
        return list.Where(p => !string.IsNullOrEmpty(p)).Select(p => global::System.IO.Path.TrimEndingDirectorySeparator(p)).ToArray();
    });

    private static readonly string[] ProtectedNames =
    [
        "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI", "$WinREAgent", "$SysReset", "Config.Msi"
    ];

    private static readonly string[] ProtectedFiles =
        ["pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp", "bootmgr", "BOOTNXT"];

    /// <summary>
    /// True for Windows, installed programs and system areas. WinXTools shows
    /// how big they are but never offers to delete anything inside them.
    /// </summary>
    public static bool IsProtectedPath(string path)
    {
        var full = global::System.IO.Path.TrimEndingDirectorySeparator(path);
        foreach (var root in ProtectedRoots.Value)
        {
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Second level from a drive root: X:\System Volume Information, X:\pagefile.sys …
        var rootDir = global::System.IO.Path.GetPathRoot(full) ?? "";
        var rest = full.Length > rootDir.Length ? full[rootDir.Length..] : "";
        var first = rest.Split('\\', 2)[0];
        if (ProtectedNames.Any(n => first.Equals(n, StringComparison.OrdinalIgnoreCase))) return true;
        if (!rest.Contains('\\') && ProtectedFiles.Any(n => rest.Equals(n, StringComparison.OrdinalIgnoreCase))) return true;

        // Store app installs, and Git's object database (deleting objects corrupts the repository).
        var name = global::System.IO.Path.GetFileName(full);
        if (name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) return true;
        if (full.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase) || full.EndsWith("\\.git", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    #endregion

    #region Reclaimable space

    private static List<SpaceFinding> FindReclaimable(DriveVolume volume, List<BigFile> largest, CancellationToken token)
    {
        var findings = new List<SpaceFinding>();

        if (volume.IsSystem)
        {
            var hiber = global::System.IO.Path.Combine(volume.Root, "hiberfil.sys");
            long hiberBytes = SizeOf(hiber);
            if (hiberBytes > 0)
                findings.Add(new SpaceFinding { Kind = SpaceFindingKind.Hibernation, Bytes = hiberBytes, Count = 1, Risk = CleanRisk.Medium });
        }

        var (shadowBytes, shadowCount) = ShadowStorage(volume.Letter);
        if (shadowCount > 1 && shadowBytes > 0)
            findings.Add(new SpaceFinding { Kind = SpaceFindingKind.RestorePoints, Bytes = shadowBytes, Count = shadowCount, Risk = CleanRisk.Caution });

        var bin = SystemCleaner.Scan(CleanTarget.RecycleBin, token);
        if (bin.Bytes > 0)
            findings.Add(new SpaceFinding { Kind = SpaceFindingKind.RecycleBin, Bytes = bin.Bytes, Count = bin.FileCount, Risk = CleanRisk.Medium });

        // Old installers / archives / disc images in the user's Downloads folder (on this drive).
        var downloads = DownloadsFolder();
        if (downloads != null && char.ToUpperInvariant(downloads[0]) == volume.Letter && Directory.Exists(downloads))
        {
            var old = new List<BigFile>();
            try
            {
                var options = new global::System.IO.EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var f in new DirectoryInfo(downloads).EnumerateFiles("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow - f.LastWriteTimeUtc < OldDownload) continue;
                    old.Add(new BigFile(f.FullName, f.Length, f.LastWriteTimeUtc, false));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            AddByExtension(findings, old, InstallerExtensions, SpaceFindingKind.OldInstallers);
            AddByExtension(findings, old, ArchiveExtensions, SpaceFindingKind.OldArchives);
        }

        // Disc images and virtual disks anywhere outside system folders.
        AddByExtension(findings, largest.Where(f => !f.IsProtected).ToList(), DiscImageExtensions, SpaceFindingKind.DiscImages, CleanRisk.Caution);
        AddByExtension(findings, largest.Where(f => !f.IsProtected).ToList(), VirtualDiskExtensions, SpaceFindingKind.VirtualDisks, CleanRisk.Caution);

        return findings.OrderByDescending(f => f.Bytes).ToList();
    }

    private static void AddByExtension(List<SpaceFinding> findings, List<BigFile> files, string[] extensions, SpaceFindingKind kind,
        CleanRisk risk = CleanRisk.Medium)
    {
        var matched = files.Where(f => extensions.Any(e => f.Path.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.Bytes).ToList();
        if (matched.Count == 0) return;
        long total = matched.Sum(f => f.Bytes);
        if (total < (8L << 20)) return;
        findings.Add(new SpaceFinding { Kind = kind, Bytes = total, Count = matched.Count, Files = matched, Risk = risk });
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path) is { Exists: true } f ? f.Length : 0; }
        catch { return 0; }
    }

    /// <summary>Space used by restore points / shadow copies on a volume, and how many there are.</summary>
    public static (long Bytes, int Count) ShadowStorage(char letter)
    {
        try
        {
            string deviceId = VolumeDeviceId(letter);
            if (deviceId.Length == 0) return (0, 0);
            long used = 0;
            using (var storage = new ManagementObjectSearcher(@"root\cimv2", "SELECT UsedSpace, Volume FROM Win32_ShadowStorage"))
            using (var results = storage.Get())
            {
                foreach (ManagementObject s in results)
                {
                    using (s)
                    {
                        if (s["Volume"]?.ToString()?.Contains(deviceId.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase) == true ||
                            s["Volume"]?.ToString()?.Contains(deviceId, StringComparison.OrdinalIgnoreCase) == true)
                            used += Convert.ToInt64(s["UsedSpace"] ?? 0L);
                    }
                }
            }
            int count = ShadowCopies(deviceId).Count;
            return (used, count);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpaceAnalyzer] Shadow storage: {ex.Message}");
            return (0, 0);
        }
    }

    private static string VolumeDeviceId(char letter)
    {
        using var searcher = new ManagementObjectSearcher(@"root\cimv2",
            $"SELECT DeviceID FROM Win32_Volume WHERE DriveLetter = '{char.ToUpperInvariant(letter)}:'");
        using var results = searcher.Get();
        foreach (ManagementObject v in results)
            using (v)
                return v["DeviceID"]?.ToString() ?? "";
        return "";
    }

    private static List<(string Id, DateTime Created)> ShadowCopies(string deviceId)
    {
        var list = new List<(string, DateTime)>();
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT ID, VolumeName, InstallDate FROM Win32_ShadowCopy");
        using var results = searcher.Get();
        foreach (ManagementObject c in results)
        {
            using (c)
            {
                if (!string.Equals(c["VolumeName"]?.ToString(), deviceId, StringComparison.OrdinalIgnoreCase)) continue;
                DateTime created = DateTime.MinValue;
                try { created = ManagementDateTimeConverter.ToDateTime(c["InstallDate"]?.ToString() ?? ""); } catch { }
                list.Add((c["ID"]?.ToString() ?? "", created));
            }
        }
        return list;
    }

    private static string? DownloadsFolder()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) == 0)
            {
                try { return Marshal.PtrToStringUni(ptr); }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
        }
        catch { }
        return global::System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    #endregion

    #region Actions (each one confirmed by the user first)

    /// <summary>Turns hibernation off; Windows deletes hiberfil.sys itself.</summary>
    public static async Task<bool> DisableHibernationAsync(CancellationToken token)
    {
        var result = await Tweaks.CommandRunner.RunToolAsync("powercfg.exe", "/hibernate off", TimeSpan.FromSeconds(60), token)
            .ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <summary>Deletes every restore point / shadow copy of the volume except the newest.</summary>
    public static int DeleteOlderRestorePoints(char letter)
    {
        int deleted = 0;
        try
        {
            string deviceId = VolumeDeviceId(letter);
            if (deviceId.Length == 0) return 0;
            var copies = ShadowCopies(deviceId).OrderByDescending(c => c.Created).ToList();
            foreach (var (id, _) in copies.Skip(1))
            {
                try
                {
                    using var copy = new ManagementObject($@"root\cimv2:Win32_ShadowCopy.ID='{id.Replace("'", "")}'");
                    copy.Delete();
                    deleted++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SpaceAnalyzer] Shadow copy {id}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpaceAnalyzer] Restore points: {ex.Message}");
        }
        return deleted;
    }

    /// <summary>
    /// Deletes files the user picked. Never inside Windows or program folders,
    /// and only the exact file that was listed (a junction swapped in later
    /// can't redirect the delete).
    /// </summary>
    public static (int Deleted, long Bytes, int Skipped) DeleteFiles(IEnumerable<string> paths, bool toRecycleBin)
    {
        int deleted = 0, skipped = 0;
        long bytes = 0;
        var list = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (toRecycleBin)
        {
            foreach (var path in list)
            {
                if (IsProtectedPath(path) || !File.Exists(path)) { skipped++; continue; }
                long size = SizeOf(path);
                if (RecycleFile(path)) { deleted++; bytes += size; }
                else skipped++;
            }
            return (deleted, bytes, skipped);
        }

        foreach (var path in list)
        {
            if (IsProtectedPath(path)) { skipped++; continue; }
            if (SystemCleaner.DeleteFileVerified(path, out long freed)) { deleted++; bytes += freed; }
            else skipped++;
        }
        return (deleted, bytes, skipped);
    }

    private static bool RecycleFile(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };
        return SHFileOperationW(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    #endregion
}

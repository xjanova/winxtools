using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetX.Core.System.Storage;

public sealed record PlannedMove(string Path, long Bytes, long Clusters, long FromLcn, int FromExtents, long TargetLcn);

/// <summary>Where each hot file will go.</summary>
public sealed class PlacementPlan
{
    public char Letter { get; init; }
    public long ClusterBytes { get; init; }
    public long TotalClusters { get; init; }
    public List<PlannedMove> Moves { get; } = new();

    /// <summary>Hot files looked at.</summary>
    public int Considered { get; set; }

    /// <summary>Already in one piece near the front: nothing to gain by moving them.</summary>
    public int AlreadyWellPlaced { get; set; }

    /// <summary>Gone, empty, too big, compressed, sparse, cloud-only, or no free gap big enough.</summary>
    public int Skipped { get; set; }

    public long BytesToMove => Moves.Sum(m => m.Bytes);
    public int FragmentedToFix => Moves.Count(m => m.FromExtents > 1);
}

/// <summary>Progress of a placement run; fractions are positions on the volume (0 = start, 1 = end).</summary>
public sealed record PlacementProgress(int Done, int Total, long BytesMoved, long BytesTotal, string? Current,
    double FromFraction, double ToFraction);

public enum PlacementOutcome { Completed, NothingToDo, Stopped, Failed }

public sealed class PlacementResult
{
    public PlacementOutcome Outcome { get; init; }
    public int Moved { get; init; }
    public int Failed { get; init; }
    public long BytesMoved { get; init; }
    public StopReason? StopReason { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>Windows' error code of the last file that couldn't be moved (diagnostics).</summary>
    public int LastError { get; init; }
}

/// <summary>
/// Puts the files a hard disk reads most next to each other, each in one
/// piece, as close to the start of the volume (the outer, fastest edge of the
/// platter) as the existing free space allows — loading them then takes short
/// head movements instead of long seeks. Every move is done by the file system
/// itself (FSCTL_MOVE_FILE, the API Windows' defragmenter uses): it copies a
/// piece into free space and only then switches the file over, journaled by
/// NTFS, so a crash or power cut leaves the file intact in its old or its new
/// place. Pieces are at most 16 MB, so stopping takes a moment at most.
/// </summary>
public static unsafe class SmartPlacement
{
    public const long MaxFileBytes = 1L << 30;
    public const long DefaultBudgetBytes = 4L << 30;
    private const int MaxFilesPerPlan = 2000;
    private const long ChunkBytes = 16L << 20;
    private const long MinRunClusters = 16;

    private const int ERROR_FILE_INVALID = 1006;
    private const int ERROR_CANCELLED = 1223;
    private const int ERROR_TARGET_TAKEN = 112; // ERROR_DISK_FULL: the planned gap was used meanwhile

    #region Plan

    /// <summary>Reads where the hot files are and picks a free gap for each (reads only, changes nothing).</summary>
    public static PlacementPlan Plan(char letter, IReadOnlyList<HotFile> hot, long budgetBytes = DefaultBudgetBytes,
        CancellationToken token = default)
    {
        letter = char.ToUpperInvariant(letter);
        using var volume = StorageNative.OpenVolumeForRead(letter);
        if (volume.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var ntfs = ReadNtfs(volume) ?? throw new NotSupportedException("Not an NTFS volume.");
        var plan = new PlacementPlan { Letter = letter, ClusterBytes = ntfs.ClusterBytes, TotalClusters = ntfs.TotalClusters };

        // 1. The hot files, hottest first, with their current layout.
        var files = new List<(string Full, long Clusters, long Lcn, int Extents)>();
        long budgetClusters = Math.Max(1, budgetBytes / ntfs.ClusterBytes);
        long wanted = 0;
        foreach (var h in hot)
        {
            token.ThrowIfCancellationRequested();
            plan.Considered++;
            var full = $"{letter}:{h.Path}";
            var layout = ReadLayout(full);
            if (layout is not { Clusters: > 0 } l || l.Clusters * ntfs.ClusterBytes > MaxFileBytes)
            {
                plan.Skipped++;
                continue;
            }
            files.Add((full, l.Clusters, l.FirstLcn, l.Extents));
            wanted += l.Clusters;
            if (files.Count >= MaxFilesPerPlan || wanted >= budgetClusters) break;
        }
        if (files.Count == 0) return plan;

        // 2. Free gaps from the start of the volume (the outer edge), outside the MFT's reserved zone.
        var runs = FreeRuns(volume, ntfs, wanted * 3, token);

        // 3. Hottest first, each into the earliest gap that holds it whole.
        long nearby = Math.Max(ntfs.TotalClusters / 100, (256L << 20) / ntfs.ClusterBytes);
        foreach (var f in files)
        {
            int index = runs.FindIndex(r => r.Count >= f.Clusters);
            if (index < 0)
            {
                plan.Skipped++;
                continue;
            }
            var run = runs[index];
            // In one piece and no farther out than where it would go: moving it gains nothing.
            if (f.Extents == 1 && f.Lcn >= 0 && f.Lcn <= run.Lcn + nearby)
            {
                plan.AlreadyWellPlaced++;
                continue;
            }
            plan.Moves.Add(new PlannedMove(f.Full, f.Clusters * ntfs.ClusterBytes, f.Clusters, f.Lcn, f.Extents, run.Lcn));
            if (run.Count == f.Clusters) runs.RemoveAt(index);
            else runs[index] = (run.Lcn + f.Clusters, run.Count - f.Clusters);
        }
        return plan;
    }

    private readonly record struct NtfsData(long TotalClusters, long ClusterBytes, long MftZoneStart, long MftZoneEnd);

    private static NtfsData? ReadNtfs(SafeFileHandle volume)
    {
        var buffer = stackalloc byte[128];
        if (!StorageNative.DeviceIoControl(volume, StorageNative.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, buffer, 128, out int returned, IntPtr.Zero) ||
            returned < 96)
            return null;
        // NTFS_VOLUME_DATA_BUFFER: TotalClusters @16, BytesPerCluster @44, MftZoneStart @80, MftZoneEnd @88.
        return new NtfsData(*(long*)(buffer + 16), *(uint*)(buffer + 44), *(long*)(buffer + 80), *(long*)(buffer + 88));
    }

    /// <summary>Free runs of at least 16 clusters, lowest first, until about <paramref name="wanted"/> clusters are found.</summary>
    private static List<(long Lcn, long Count)> FreeRuns(SafeFileHandle volume, NtfsData ntfs, long wanted, CancellationToken token)
    {
        const int Chunk = 1 << 20; // 1 MB of bitmap = 8 M clusters per call
        var runs = new List<(long Lcn, long Count)>();
        var output = (byte*)NativeMemory.Alloc(16 + Chunk);
        long found = 0, runStart = -1, lcn = 0;
        try
        {
            void Close(long end)
            {
                if (runStart < 0) return;
                AddRun(runs, runStart, end - runStart, ntfs, ref found);
                runStart = -1;
            }

            while (lcn < ntfs.TotalClusters && found < wanted)
            {
                token.ThrowIfCancellationRequested();
                long start = lcn;
                bool ok = StorageNative.DeviceIoControl(volume, StorageNative.FSCTL_GET_VOLUME_BITMAP, &start, 8, output, 16 + Chunk,
                    out int returned, IntPtr.Zero);
                if (!ok && Marshal.GetLastWin32Error() != StorageNative.ERROR_MORE_DATA) break;
                long first = *(long*)output;
                long bits = Math.Min(*(long*)(output + 8), (returned - 16) * 8L);
                if (bits <= 0) break;
                byte* map = output + 16;

                long i = 0;
                while (i < bits)
                {
                    // 64 clusters at a time while they are all used or all free.
                    if ((i & 63) == 0 && i + 64 <= bits)
                    {
                        ulong word = *(ulong*)(map + (i >> 3));
                        if (word == ulong.MaxValue) { Close(first + i); i += 64; continue; }
                        if (word == 0) { if (runStart < 0) runStart = first + i; i += 64; continue; }
                    }
                    bool used = (map[i >> 3] & (1 << (int)(i & 7))) != 0;
                    if (used) Close(first + i);
                    else if (runStart < 0) runStart = first + i;
                    i++;
                }
                lcn = first + bits;
                if (ok) break; // that was the end of the volume
            }
            Close(Math.Min(lcn, ntfs.TotalClusters));
        }
        finally
        {
            NativeMemory.Free(output);
        }
        return runs;
    }

    private static void AddRun(List<(long Lcn, long Count)> runs, long lcn, long count, NtfsData ntfs, ref long found)
    {
        // Keep out of the zone NTFS reserves for its file table.
        long zs = ntfs.MftZoneStart, ze = ntfs.MftZoneEnd;
        if (ze > zs && lcn < ze && lcn + count > zs)
        {
            if (lcn < zs) AddRun(runs, lcn, zs - lcn, default, ref found);
            if (lcn + count > ze) AddRun(runs, ze, lcn + count - ze, default, ref found);
            return;
        }
        if (count < MinRunClusters) return;
        runs.Add((lcn, count));
        found += count;
    }

    #endregion

    #region Execute

    /// <summary>
    /// Moves the planned files. Runs on the calling thread at background I/O
    /// priority (anything else the PC reads or writes goes first); call it on a
    /// thread of its own. Stops between two pieces as soon as the guard says so.
    /// </summary>
    public static PlacementResult Execute(PlacementPlan plan, SafeOperationGuard guard, bool removable,
        IProgress<PlacementProgress>? progress)
    {
        var clock = Stopwatch.StartNew();
        int moved = 0, failed = 0, lastError = 0;
        long bytesMoved = 0, total = plan.BytesToMove;
        bool background = SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
        try
        {
            using var volume = StorageNative.CreateFileW($@"\\.\{plan.Letter}:", StorageNative.GENERIC_READ | StorageNative.GENERIC_WRITE,
                StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE, IntPtr.Zero, StorageNative.OPEN_EXISTING, 0, IntPtr.Zero);
            if (volume.IsInvalid)
                return new PlacementResult { Outcome = PlacementOutcome.Failed, LastError = Marshal.GetLastWin32Error(), Duration = clock.Elapsed };

            long sinceFlush = 0;
            for (int i = 0; i < plan.Moves.Count && !guard.Token.IsCancellationRequested; i++)
            {
                var move = plan.Moves[i];
                progress?.Report(new PlacementProgress(i, plan.Moves.Count, bytesMoved, total, move.Path,
                    Fraction(plan, move.FromLcn), Fraction(plan, move.TargetLcn)));

                int error = MoveOne(volume, plan, move, guard.Token, ref bytesMoved);
                if (error == 0) moved++;
                else if (error != ERROR_CANCELLED)
                {
                    failed++;
                    lastError = error;
                }

                // A removable drive gets everything written through after every file.
                sinceFlush += move.Bytes;
                if (removable || sinceFlush >= (256L << 20))
                {
                    StorageNative.FlushFileBuffers(volume);
                    sinceFlush = 0;
                }
            }
            StorageNative.FlushFileBuffers(volume);
            progress?.Report(new PlacementProgress(plan.Moves.Count, plan.Moves.Count, bytesMoved, total, null, 0, 0));
        }
        finally
        {
            if (background) SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
            guard.MarkReleased(); // every handle on the drive is closed by now
        }

        var outcome = guard.Reason != null ? PlacementOutcome.Stopped
            : moved == 0 && failed > 0 ? PlacementOutcome.Failed
            : PlacementOutcome.Completed;
        return new PlacementResult
        {
            Outcome = outcome, Moved = moved, Failed = failed, BytesMoved = bytesMoved,
            StopReason = guard.Reason, Duration = clock.Elapsed, LastError = lastError
        };
    }

    private static double Fraction(PlacementPlan plan, long lcn) =>
        plan.TotalClusters > 0 && lcn >= 0 ? Math.Clamp(lcn / (double)plan.TotalClusters, 0, 1) : 0;

    /// <returns>0, or the Windows error that stopped this file (it stays intact either way).</returns>
    private static int MoveOne(SafeFileHandle volume, PlacementPlan plan, PlannedMove move, CancellationToken token, ref long bytesMoved)
    {
        using var file = OpenForLayout(move.Path);
        if (file.IsInvalid) return Marshal.GetLastWin32Error();

        // The file may have changed since the plan was made: check again.
        var layout = Layout(file);
        if (layout is not { } l || l.Clusters != move.Clusters) return ERROR_FILE_INVALID;
        if (!IsFree(volume, move.TargetLcn, move.Clusters)) return ERROR_TARGET_TAKEN;

        long chunk = Math.Max(1, ChunkBytes / plan.ClusterBytes);
        for (long vcn = 0; vcn < move.Clusters; vcn += chunk)
        {
            // Stopping here leaves the file whole: the pieces moved so far are in their new place, the rest in the old.
            if (token.IsCancellationRequested) return ERROR_CANCELLED;
            var data = new MOVE_FILE_DATA
            {
                FileHandle = file.DangerousGetHandle(),
                StartingVcn = vcn,
                StartingLcn = move.TargetLcn + vcn,
                ClusterCount = (uint)Math.Min(chunk, move.Clusters - vcn)
            };
            if (!StorageNative.DeviceIoControl(volume, StorageNative.FSCTL_MOVE_FILE, &data, sizeof(MOVE_FILE_DATA), null, 0, out _, IntPtr.Zero))
                return Marshal.GetLastWin32Error();
            bytesMoved += data.ClusterCount * plan.ClusterBytes;
        }
        return 0;
    }

    /// <summary>True when every cluster of the range is still free.</summary>
    private static bool IsFree(SafeFileHandle volume, long lcn, long count)
    {
        long start = lcn & ~63L; // the file system rounds the start down anyway
        long bits = lcn - start + count;
        int size = 16 + (int)((bits + 7) / 8) + 8;
        var output = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            bool ok = StorageNative.DeviceIoControl(volume, StorageNative.FSCTL_GET_VOLUME_BITMAP, &start, 8, output, size, out _, IntPtr.Zero);
            if (!ok && Marshal.GetLastWin32Error() != StorageNative.ERROR_MORE_DATA) return false;
            long first = *(long*)output;
            byte* map = output + 16;
            for (long c = lcn; c < lcn + count; c++)
            {
                long i = c - first;
                if (i < 0 || i >= (size - 16) * 8L) return false;
                if ((map[i >> 3] & (1 << (int)(i & 7))) != 0) return false;
            }
            return true;
        }
        finally
        {
            NativeMemory.Free(output);
        }
    }

    #endregion

    #region File layout

    private readonly record struct FileLayout(long Clusters, long FirstLcn, int Extents);

    /// <summary>Clusters and pieces of a regular file, or null when it can't or shouldn't be moved.</summary>
    private static FileLayout? ReadLayout(string path)
    {
        using var file = OpenForLayout(path);
        return file.IsInvalid ? null : Layout(file);
    }

    private static SafeFileHandle OpenForLayout(string path) =>
        // Attributes only (no access to the content); never follows a link to somewhere else.
        StorageNative.CreateFileW(path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path,
            StorageNative.FILE_READ_ATTRIBUTES | StorageNative.SYNCHRONIZE,
            StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE | StorageNative.FILE_SHARE_DELETE, IntPtr.Zero,
            StorageNative.OPEN_EXISTING, StorageNative.FILE_FLAG_BACKUP_SEMANTICS | StorageNative.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

    private static FileLayout? Layout(SafeFileHandle file)
    {
        if (!GetFileInformationByHandle(file, out var info)) return null;
        // Folders, links, compressed, sparse, encrypted and cloud placeholders are left alone.
        const uint Skip = 0x10 | 0x200 | 0x400 | 0x800 | 0x1000 | 0x4000 | 0x40000 | 0x400000;
        if ((info.FileAttributes & Skip) != 0) return null;
        if (((long)info.FileSizeHigh << 32 | info.FileSizeLow) == 0) return null;

        const int OutSize = 64 * 1024;
        var output = (byte*)NativeMemory.Alloc(OutSize);
        try
        {
            long vcn = 0, clusters = 0, firstLcn = -1, lastEnd = long.MinValue;
            int extents = 0;
            while (true)
            {
                long input = vcn;
                bool ok = StorageNative.DeviceIoControl(file, StorageNative.FSCTL_GET_RETRIEVAL_POINTERS, &input, 8, output, OutSize,
                    out _, IntPtr.Zero);
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != StorageNative.ERROR_MORE_DATA)
                    return null; // ERROR_HANDLE_EOF: tiny file kept inside the file table — nothing to move

                // RETRIEVAL_POINTERS_BUFFER: ExtentCount @0, StartingVcn @8, { NextVcn, Lcn } from @16.
                int count = *(int*)output;
                long at = *(long*)(output + 8);
                for (int i = 0; i < count; i++)
                {
                    long next = *(long*)(output + 16 + i * 16);
                    long lcn = *(long*)(output + 24 + i * 16);
                    if (lcn < 0) return null; // not physically stored (sparse/compressed)
                    if (firstLcn < 0) firstLcn = lcn;
                    if (lcn != lastEnd) extents++; // physically adjacent runs are one piece
                    lastEnd = lcn + (next - at);
                    clusters += next - at;
                    at = next;
                }
                if (ok || count == 0) break;
                vcn = at;
            }
            return clusters > 0 ? new FileLayout(clusters, firstLcn, extents) : null;
        }
        finally
        {
            NativeMemory.Free(output);
        }
    }

    #endregion

    #region Native

    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOVE_FILE_DATA
    {
        public IntPtr FileHandle;
        public long StartingVcn;
        public long StartingLcn;
        public uint ClusterCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    #endregion
}

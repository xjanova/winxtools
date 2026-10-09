using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NetX.Core.System.Storage;

/// <summary>
/// Which clusters of a volume are in use, folded into a fixed number of
/// blocks for drawing. Read straight from the file system's allocation
/// bitmap (FSCTL_GET_VOLUME_BITMAP), so it is the real layout of the disk.
/// </summary>
public sealed class VolumeMap
{
    public char Letter { get; init; }
    public long TotalClusters { get; init; }
    public int ClusterBytes { get; init; }

    /// <summary>Clusters per block (a multiple of 64).</summary>
    public long ClustersPerBlock { get; init; }
    public int BlockCount => Used.Length;

    /// <summary>Fraction of each block that is allocated (0..1).</summary>
    public float[] Used { get; init; } = Array.Empty<float>();

    /// <summary>A fingerprint of each block's allocation bits; it changes when data moves inside a block.</summary>
    public ulong[] Signature { get; init; } = Array.Empty<ulong>();

    public long UsedClusters { get; init; }
    public DateTime TakenUtc { get; init; } = DateTime.UtcNow;

    public long BytesPerBlock => ClustersPerBlock * ClusterBytes;
}

/// <summary>A file that is stored in more than one piece.</summary>
public sealed record FragmentedFile(string Path, long Bytes, int Fragments);

/// <summary>
/// Where fragmented files, file-system metadata and unmovable files sit on the
/// volume, from one pass over the MFT (FSCTL_QUERY_FILE_LAYOUT).
/// </summary>
public sealed class LayoutScan
{
    /// <summary>Fraction of each block held by files that are in pieces.</summary>
    public float[] Fragmented { get; init; } = Array.Empty<float>();

    /// <summary>Fraction of each block held by NTFS metadata ($MFT, $LogFile…).</summary>
    public float[] Metadata { get; init; } = Array.Empty<float>();

    /// <summary>Fraction of each block held by files Windows won't move (page file, pinned streams).</summary>
    public float[] Unmovable { get; init; } = Array.Empty<float>();

    public long FilesScanned { get; init; }
    public long FragmentedFiles { get; init; }

    /// <summary>Pieces beyond the first, summed over all files (Windows calls these "excess fragments").</summary>
    public long ExcessFragments { get; init; }

    /// <summary>Bytes in pieces smaller than 64 MB — the part of the data that fragmentation slows down.</summary>
    public long FragmentedBytes { get; init; }
    public IReadOnlyList<FragmentedFile> Worst { get; init; } = Array.Empty<FragmentedFile>();
}

/// <summary>Reads the allocation bitmap and file layout of an NTFS/FAT volume. Needs administrator rights.</summary>
public static unsafe class VolumeMapReader
{
    private const int BitmapChunkBytes = 4 * 1024 * 1024;

    /// <summary>Reads the whole allocation bitmap and folds it into about <paramref name="blockCount"/> blocks.</summary>
    /// <exception cref="Win32Exception">The volume can't be opened or read (not admin, locked, unsupported).</exception>
    public static VolumeMap ReadBitmap(char letter, int blockCount, CancellationToken token = default)
    {
        blockCount = Math.Clamp(blockCount, 64, 1 << 20);
        int clusterBytes = 4096;
        if (StorageNative.GetDiskFreeSpaceW($"{letter}:\\", out uint spc, out uint bps, out _, out _))
            clusterBytes = (int)(spc * bps);

        using var volume = OpenVolume(letter);
        var output = (byte*)NativeMemory.Alloc(BitmapChunkBytes + 16);
        try
        {
            long start = 0, total = -1, clustersPerBlock = 64, used = 0;
            float[] fractions = Array.Empty<float>();
            ulong[] signatures = Array.Empty<ulong>();
            long[] counts = Array.Empty<long>();

            while (true)
            {
                token.ThrowIfCancellationRequested();
                long input = start;
                bool ok = StorageNative.DeviceIoControl(volume, StorageNative.FSCTL_GET_VOLUME_BITMAP,
                    &input, sizeof(long), output, BitmapChunkBytes + 16, out int returned, IntPtr.Zero);
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != StorageNative.ERROR_MORE_DATA) throw new Win32Exception(error);
                if (returned < 16) throw new Win32Exception(StorageNative.ERROR_INSUFFICIENT_BUFFER);

                long chunkStart = *(long*)output;      // rounded down to a multiple of 8 by the file system
                long clustersLeft = *(long*)(output + 8);
                if (total < 0)
                {
                    total = chunkStart + clustersLeft;
                    if (total <= 0) throw new Win32Exception(StorageNative.ERROR_HANDLE_EOF);
                    clustersPerBlock = Math.Max(64, (total + blockCount - 1) / blockCount);
                    clustersPerBlock = (clustersPerBlock + 63) / 64 * 64;
                    int blocks = (int)((total + clustersPerBlock - 1) / clustersPerBlock);
                    fractions = new float[blocks];
                    signatures = new ulong[blocks];
                    counts = new long[blocks];
                }

                long bytes = returned - 16;
                long clusters = Math.Min(bytes * 8, clustersLeft);
                used += Accumulate(output + 16, chunkStart, clusters, clustersPerBlock, counts, signatures);

                if (ok || clusters <= 0) break;
                start = chunkStart + clusters;
                if (start >= total) break;
            }

            for (int b = 0; b < counts.Length; b++)
            {
                long blockClusters = Math.Min(clustersPerBlock, total - b * clustersPerBlock);
                fractions[b] = blockClusters <= 0 ? 0 : (float)Math.Min(1.0, counts[b] / (double)blockClusters);
            }

            return new VolumeMap
            {
                Letter = letter,
                TotalClusters = total,
                ClusterBytes = clusterBytes,
                ClustersPerBlock = clustersPerBlock,
                Used = fractions,
                Signature = signatures,
                UsedClusters = used
            };
        }
        finally
        {
            NativeMemory.Free(output);
        }
    }

    /// <summary>Counts set bits per block and updates each block's fingerprint.</summary>
    private static long Accumulate(byte* bits, long firstCluster, long clusters, long clustersPerBlock,
        long[] counts, ulong[] signatures)
    {
        long total = 0;
        long fullBytes = clusters / 8;
        bool aligned = firstCluster % 64 == 0;

        long i = 0;
        if (aligned)
        {
            // Fast path: 64 clusters at a time; a block is a multiple of 64 clusters,
            // so a word never straddles two blocks.
            long words = fullBytes / 8;
            var p = (ulong*)bits;
            for (long w = 0; w < words; w++)
            {
                ulong word = p[w];
                int block = (int)((firstCluster + w * 64) / clustersPerBlock);
                if ((uint)block >= (uint)counts.Length) break;
                int set = BitOperations.PopCount(word);
                counts[block] += set;
                total += set;
                signatures[block] = Mix(signatures[block], word);
            }
            i = words * 8;
        }

        for (; i < fullBytes; i++)
        {
            byte value = bits[i];
            int block = (int)((firstCluster + i * 8) / clustersPerBlock);
            if ((uint)block >= (uint)counts.Length) break;
            int set = BitOperations.PopCount(value);
            counts[block] += set;
            total += set;
            signatures[block] = Mix(signatures[block], value);
        }

        int tail = (int)(clusters % 8);
        if (tail > 0)
        {
            byte value = (byte)(bits[fullBytes] & ((1 << tail) - 1));
            int block = (int)((firstCluster + fullBytes * 8) / clustersPerBlock);
            if ((uint)block < (uint)counts.Length)
            {
                int set = BitOperations.PopCount(value);
                counts[block] += set;
                total += set;
                signatures[block] = Mix(signatures[block], value);
            }
        }
        return total;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value + 0x9E3779B97F4A7C15UL + (hash << 6) + (hash >> 2);
        return BitOperations.RotateLeft(hash, 17) * 0xBF58476D1CE4E5B9UL;
    }

    #region File layout (fragmentation map)

    private const uint QUERY_FILE_LAYOUT_RESTART = 0x1;
    private const uint QUERY_FILE_LAYOUT_INCLUDE_NAMES = 0x2;
    private const uint QUERY_FILE_LAYOUT_INCLUDE_STREAMS = 0x4;
    private const uint QUERY_FILE_LAYOUT_INCLUDE_EXTENTS = 0x8;
    private const uint QUERY_FILE_LAYOUT_INCLUDE_FULL_PATH_IN_NAMES = 0x40;
    private const uint FILE_LAYOUT_NAME_ENTRY_DOS = 0x2;
    private const uint STREAM_LAYOUT_ENTRY_IMMOVABLE = 0x1;
    private const uint STREAM_LAYOUT_ENTRY_PINNED = 0x2;
    private const uint STREAM_EXTENT_ENTRY_AS_RETRIEVAL_POINTERS = 0x1;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const int LayoutChunkBytes = 2 * 1024 * 1024;
    private const int WorstListSize = 40;

    // Like Windows' own analysis: pieces of 64 MB or more don't slow reading down
    // and are not counted as fragmentation.
    private const long LargeFragmentBytes = 64L << 20;

    private static readonly string[] UnmovableNames = ["pagefile.sys", "hiberfil.sys", "swapfile.sys"];

    /// <summary>
    /// Walks every file record once and marks where fragmented files, NTFS
    /// metadata and unmovable files are. <paramref name="progress"/> receives
    /// the number of files read so far. NTFS only.
    /// </summary>
    public static LayoutScan ScanLayout(char letter, VolumeMap geometry, IProgress<long>? progress = null,
        CancellationToken token = default)
    {
        int blocks = geometry.BlockCount;
        long cpb = geometry.ClustersPerBlock;
        var fragmented = new long[blocks];
        var metadata = new long[blocks];
        var unmovable = new long[blocks];
        var worst = new PriorityQueue<FragmentedFile, int>();
        long files = 0, fragmentedFiles = 0, excess = 0, fragmentedBytes = 0;

        using var volume = OpenVolume(letter);
        var input = stackalloc byte[32]; // QUERY_FILE_LAYOUT_INPUT with an empty filter
        var output = (byte*)NativeMemory.Alloc(LayoutChunkBytes);
        var runs = new List<(long Lcn, long Count)>(64);
        try
        {
            uint flags = QUERY_FILE_LAYOUT_RESTART | QUERY_FILE_LAYOUT_INCLUDE_NAMES | QUERY_FILE_LAYOUT_INCLUDE_STREAMS |
                         QUERY_FILE_LAYOUT_INCLUDE_EXTENTS | QUERY_FILE_LAYOUT_INCLUDE_FULL_PATH_IN_NAMES;
            bool fullPaths = true;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                new Span<byte>(input, 32).Clear();
                *(uint*)(input + 4) = flags;

                bool ok = StorageNative.DeviceIoControl(volume, StorageNative.FSCTL_QUERY_FILE_LAYOUT,
                    input, 32, output, LayoutChunkBytes, out int returned, IntPtr.Zero);
                if (!ok)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == StorageNative.ERROR_HANDLE_EOF) break;
                    if (error == ERROR_INVALID_PARAMETER && fullPaths && (flags & QUERY_FILE_LAYOUT_RESTART) != 0)
                    {
                        // Older Windows 10: no full paths, file names only.
                        fullPaths = false;
                        flags &= ~QUERY_FILE_LAYOUT_INCLUDE_FULL_PATH_IN_NAMES;
                        continue;
                    }
                    throw new Win32Exception(error);
                }
                flags &= ~QUERY_FILE_LAYOUT_RESTART;
                if (returned < 16) break;

                // QUERY_FILE_LAYOUT_OUTPUT { FileEntryCount; FirstFileOffset; Flags; Reserved }
                uint entryCount = *(uint*)output;
                uint offset = *(uint*)(output + 4);
                for (uint n = 0; n < entryCount && offset > 0 && offset + 40 <= (uint)returned; n++)
                {
                    byte* entry = output + offset;
                    ProcessFile(entry, output + returned, letter, cpb, geometry.ClusterBytes, runs,
                        fragmented, metadata, unmovable, worst,
                        ref fragmentedFiles, ref excess, ref fragmentedBytes);
                    files++;

                    uint next = *(uint*)(entry + 4);
                    if (next == 0) break;
                    offset += next;
                }

                progress?.Report(files);
            }
        }
        finally
        {
            NativeMemory.Free(output);
        }

        var worstList = new List<FragmentedFile>(worst.Count);
        while (worst.Count > 0) worstList.Add(worst.Dequeue());
        worstList.Reverse();

        return new LayoutScan
        {
            Fragmented = ToFractions(fragmented, cpb, geometry.TotalClusters),
            Metadata = ToFractions(metadata, cpb, geometry.TotalClusters),
            Unmovable = ToFractions(unmovable, cpb, geometry.TotalClusters),
            FilesScanned = files,
            FragmentedFiles = fragmentedFiles,
            ExcessFragments = excess,
            FragmentedBytes = fragmentedBytes,
            Worst = worstList
        };
    }

    private static void ProcessFile(byte* entry, byte* end, char letter, long cpb, int clusterBytes, List<(long Lcn, long Count)> runs,
        long[] fragmented, long[] metadata, long[] unmovable, PriorityQueue<FragmentedFile, int> worst,
        ref long fragmentedFiles, ref long excess, ref long fragmentedBytes)
    {
        // FILE_LAYOUT_ENTRY: FileReferenceNumber @16, FirstNameOffset @24, FirstStreamOffset @28.
        ulong frn = *(ulong*)(entry + 16);
        long record = (long)(frn & 0x0000FFFFFFFFFFFFUL);
        uint nameOffset = *(uint*)(entry + 24);
        uint streamOffset = *(uint*)(entry + 28);

        string? name = nameOffset != 0 ? ReadPrimaryName(entry + nameOffset, end) : null;
        // Records 0-15 are NTFS' own files; $Extend holds the rest ($UsnJrnl, $ObjId, $Quota…).
        bool isMetadata = record < 16 || (name != null && name.StartsWith(@"\$Extend\", StringComparison.OrdinalIgnoreCase));
        bool isUnmovableName = name != null && IsRootUnmovable(name);

        runs.Clear();
        long allocated = 0;
        bool immovable = false;

        byte* stream = streamOffset != 0 ? entry + streamOffset : null;
        int guard = 0;
        while (stream != null && stream + 48 <= end && guard++ < 4096)
        {
            // STREAM_LAYOUT_ENTRY: NextStreamOffset @4, Flags @8, ExtentInformationOffset @12, AllocationSize @16.
            uint next = *(uint*)(stream + 4);
            uint streamFlags = *(uint*)(stream + 8);
            uint extentOffset = *(uint*)(stream + 12);
            if ((streamFlags & (STREAM_LAYOUT_ENTRY_IMMOVABLE | STREAM_LAYOUT_ENTRY_PINNED)) != 0) immovable = true;

            if (extentOffset != 0 && stream + extentOffset + 24 <= end)
            {
                byte* extent = stream + extentOffset;
                // STREAM_EXTENT_ENTRY { Flags; RETRIEVAL_POINTERS_BUFFER @8 { ExtentCount; StartingVcn @16; Extents @24 } }
                if ((*(uint*)extent & STREAM_EXTENT_ENTRY_AS_RETRIEVAL_POINTERS) != 0)
                {
                    uint count = *(uint*)(extent + 8);
                    long vcn = *(long*)(extent + 16);
                    byte* pairs = extent + 24;
                    for (uint k = 0; k < count && pairs + 16 * (k + 1) <= end; k++)
                    {
                        long nextVcn = *(long*)(pairs + 16 * k);
                        long lcn = *(long*)(pairs + 16 * k + 8);
                        long length = nextVcn - vcn;
                        vcn = nextVcn;
                        if (length <= 0 || lcn < 0) continue; // sparse or compressed hole: no clusters
                        allocated += length;
                        if (runs.Count > 0 && runs[^1].Lcn + runs[^1].Count == lcn)
                            runs[^1] = (runs[^1].Lcn, runs[^1].Count + length);
                        else
                            runs.Add((lcn, length));
                    }
                }
            }

            if (next == 0) break;
            stream += next;
        }

        if (runs.Count == 0) return;

        if (isMetadata)
        {
            foreach (var run in runs) AddRun(metadata, run.Lcn, run.Count, cpb);
            return;
        }
        if (immovable || isUnmovableName)
        {
            foreach (var run in runs) AddRun(unmovable, run.Lcn, run.Count, cpb);
            return;
        }
        if (runs.Count < 2) return;
        long largeClusters = LargeFragmentBytes / Math.Max(512, clusterBytes);
        int smallPieces = runs.Count(r => r.Count < largeClusters);
        if (smallPieces == 0) return; // in pieces, but every piece is large enough to read at full speed

        fragmentedFiles++;
        excess += Math.Min(smallPieces, runs.Count - 1);

        // Only the small pieces are drawn as fragmentation: they are what makes the head jump.
        long smallClusters = 0;
        foreach (var run in runs)
        {
            if (run.Count >= largeClusters) continue;
            AddRun(fragmented, run.Lcn, run.Count, cpb);
            smallClusters += run.Count;
        }
        fragmentedBytes += smallClusters * clusterBytes;

        long bytes = allocated * clusterBytes;
        var path = name == null ? $"#{record}" : name.StartsWith('\\') ? $"{letter}:{name}" : name;
        if (worst.Count < WorstListSize)
            worst.Enqueue(new FragmentedFile(path, bytes, smallPieces), smallPieces);
        else if (worst.TryPeek(out _, out int smallest) && smallPieces > smallest)
            worst.EnqueueDequeue(new FragmentedFile(path, bytes, smallPieces), smallPieces);
    }

    /// <summary>First non-DOS (8.3) name of a file layout entry.</summary>
    private static string? ReadPrimaryName(byte* nameEntry, byte* end)
    {
        string? fallback = null;
        int guard = 0;
        while (nameEntry + 24 <= end && guard++ < 64)
        {
            // FILE_LAYOUT_NAME_ENTRY { NextNameOffset; Flags; ParentFRN @8; FileNameLength @16; Reserved; FileName @24 }
            uint next = *(uint*)nameEntry;
            uint flags = *(uint*)(nameEntry + 4);
            uint length = *(uint*)(nameEntry + 16);
            if (length > 0 && length < 65536 && nameEntry + 24 + length <= end)
            {
                var text = new string((char*)(nameEntry + 24), 0, (int)length / 2);
                if ((flags & FILE_LAYOUT_NAME_ENTRY_DOS) == 0) return text;
                fallback ??= text;
            }
            if (next == 0) break;
            nameEntry += next;
        }
        return fallback;
    }

    private static bool IsRootUnmovable(string name)
    {
        var trimmed = name.TrimStart('\\');
        return UnmovableNames.Any(n => trimmed.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddRun(long[] target, long lcn, long count, long cpb)
    {
        while (count > 0)
        {
            long block = lcn / cpb;
            if (block < 0 || block >= target.Length) return;
            long inBlock = Math.Min(count, (block + 1) * cpb - lcn);
            target[block] += inBlock;
            lcn += inBlock;
            count -= inBlock;
        }
    }

    private static float[] ToFractions(long[] counts, long cpb, long totalClusters)
    {
        var result = new float[counts.Length];
        for (int b = 0; b < counts.Length; b++)
        {
            long blockClusters = Math.Min(cpb, totalClusters - b * cpb);
            if (blockClusters > 0) result[b] = (float)Math.Min(1.0, counts[b] / (double)blockClusters);
        }
        return result;
    }

    #endregion

    private static SafeFileHandle OpenVolume(char letter)
    {
        var handle = StorageNative.OpenVolumeForRead(letter);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }
}

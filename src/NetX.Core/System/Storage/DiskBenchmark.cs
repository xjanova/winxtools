using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using NetX.Core.Data;

namespace NetX.Core.System.Storage;

public enum BenchKind
{
    SeqRead,
    SeqWrite,
    RandRead,
    RandWrite
}

/// <summary>One benchmark pattern, named the way CrystalDiskMark names it (SEQ1M Q8T1, RND4K Q32T1…).</summary>
public sealed record BenchPattern(string Id, BenchKind Kind, int BlockBytes, int QueueDepth)
{
    public bool IsRead => Kind is BenchKind.SeqRead or BenchKind.RandRead;
    public bool IsSequential => Kind is BenchKind.SeqRead or BenchKind.SeqWrite;
}

public sealed class BenchResult
{
    public string PatternId { get; set; } = "";
    public BenchKind Kind { get; set; }
    public int BlockBytes { get; set; }
    public int QueueDepth { get; set; }

    /// <summary>Decimal megabytes per second (1 MB = 1,000,000 bytes), the unit drive makers use.</summary>
    public double MBps { get; set; }
    public double Iops { get; set; }
    public double AvgLatencyMs { get; set; }

    /// <summary>Speed every quarter second during the best pass, for the time chart.</summary>
    public double[] Samples { get; set; } = Array.Empty<double>();
}

public sealed class SurfacePoint
{
    /// <summary>Position on the disk, 0 = start (outer edge of a hard disk) … 1 = end.</summary>
    public double Position { get; set; }
    public double MBps { get; set; }
}

/// <summary>One complete speed test of one drive, kept for before/after comparisons.</summary>
public sealed class BenchRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public char Letter { get; set; }
    public int? DiskNumber { get; set; }
    public string DiskModel { get; set; } = "";
    public DriveMedia Media { get; set; }
    public DriveBus Bus { get; set; }
    public long DiskBytes { get; set; }

    /// <summary>Free space on the volume when the test started (a nearly full SSD writes much slower).</summary>
    public long VolumeFreeBytes { get; set; }
    public long VolumeTotalBytes { get; set; }

    /// <summary>"before", "after" or "" — what the user was about to do / had just done.</summary>
    public string Label { get; set; } = "";
    public string Note { get; set; } = "";

    public int FileMB { get; set; }
    public int SecondsPerTest { get; set; }
    public List<BenchResult> Results { get; set; } = new();
    public List<SurfacePoint> Surface { get; set; } = new();

    /// <summary>Average time to read a random 4 KB block anywhere on the disk (seek + rotation on a hard disk).</summary>
    public double? AccessTimeMs { get; set; }

    public BenchResult? Find(string patternId) => Results.FirstOrDefault(r => r.PatternId == patternId);
}

public sealed class BenchOptions
{
    public long FileBytes { get; set; } = 1L << 30;
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(5);
    public int Passes { get; set; } = 1;
    public bool IncludeWrites { get; set; } = true;
    public bool IncludeSurface { get; set; } = true;
    public int SurfacePoints { get; set; } = 100;
    public string Label { get; set; } = "";
}

public enum BenchStage
{
    Preparing,
    Testing,
    Surface,
    AccessTime,
    Done
}

public sealed record BenchProgress(BenchStage Stage, string? PatternId, double Fraction, double CurrentMBps, BenchResult? Finished = null,
    SurfacePoint? Point = null);

/// <summary>
/// Measures how fast a drive really reads and writes.
/// • File tests write a temporary file and use unbuffered I/O (no Windows
///   cache), random incompressible data and real queue depths, like
///   CrystalDiskMark. The file is opened delete-on-close, so it disappears
///   even if WinXTools is killed mid-test.
/// • The surface scan reads the raw disk from start to end — READ ONLY, the
///   handle has no write access — and draws the transfer-rate curve a
///   healthy hard disk should show (fast outer tracks, slower inner tracks).
/// </summary>
public static class DiskBenchmark
{
    public static readonly BenchPattern Seq1MQ8 = new("SEQ1M Q8T1", BenchKind.SeqRead, 1 << 20, 8);
    public static readonly BenchPattern Seq1MQ8W = new("SEQ1M Q8T1 W", BenchKind.SeqWrite, 1 << 20, 8);
    public static readonly BenchPattern Seq1MQ1 = new("SEQ1M Q1T1", BenchKind.SeqRead, 1 << 20, 1);
    public static readonly BenchPattern Seq1MQ1W = new("SEQ1M Q1T1 W", BenchKind.SeqWrite, 1 << 20, 1);
    public static readonly BenchPattern Rnd4KQ32 = new("RND4K Q32T1", BenchKind.RandRead, 4096, 32);
    public static readonly BenchPattern Rnd4KQ32W = new("RND4K Q32T1 W", BenchKind.RandWrite, 4096, 32);
    public static readonly BenchPattern Rnd4KQ1 = new("RND4K Q1T1", BenchKind.RandRead, 4096, 1);
    public static readonly BenchPattern Rnd4KQ1W = new("RND4K Q1T1 W", BenchKind.RandWrite, 4096, 1);

    /// <summary>Reads first, then writes, in the order CrystalDiskMark shows them.</summary>
    public static IReadOnlyList<BenchPattern> Patterns(bool includeWrites) => includeWrites
        ? [Seq1MQ8, Seq1MQ1, Rnd4KQ32, Rnd4KQ1, Seq1MQ8W, Seq1MQ1W, Rnd4KQ32W, Rnd4KQ1W]
        : [Seq1MQ8, Seq1MQ1, Rnd4KQ32, Rnd4KQ1];

    private const long Headroom = 256L << 20;
    private const long MinFileBytes = 64L << 20;
    private const string TestFileName = "WinXTools-SpeedTest.tmp";

    /// <summary>The test file size that fits on the volume (null = not enough free space at all).</summary>
    public static long? FitFileSize(DriveVolume volume, long wanted)
    {
        long room = volume.FreeBytes - Headroom;
        if (room < MinFileBytes) return null;
        long size = Math.Min(wanted, room);
        return size / (1 << 20) * (1 << 20);
    }

    public static async Task<BenchRun> RunAsync(DriveVolume volume, BenchOptions options,
        IProgress<BenchProgress>? progress, CancellationToken token)
    {
        var run = new BenchRun
        {
            Letter = volume.Letter,
            DiskNumber = volume.DiskNumber,
            DiskModel = volume.Disk?.Model ?? "",
            Media = volume.Disk?.Media ?? DriveMedia.Unknown,
            Bus = volume.Disk?.Bus ?? DriveBus.Unknown,
            DiskBytes = volume.Disk?.SizeBytes ?? volume.TotalBytes,
            VolumeFreeBytes = volume.FreeBytes,
            VolumeTotalBytes = volume.TotalBytes,
            Label = options.Label,
            SecondsPerTest = (int)Math.Round(options.Duration.TotalSeconds)
        };

        var size = FitFileSize(volume, options.FileBytes)
                   ?? throw new IOException("NotEnoughSpace");
        run.FileMB = (int)(size >> 20);

        await Task.Run(() => RunFileTests(volume, size, options, run, progress, token), token).ConfigureAwait(false);

        if (options.IncludeSurface && volume.DiskNumber is int disk && (volume.Disk?.SizeBytes ?? 0) > 0)
        {
            try
            {
                run.Surface = await Task.Run(() => SurfaceScan(disk, volume.Disk!.SizeBytes, options.SurfacePoints, progress, token), token)
                    .ConfigureAwait(false);
                run.AccessTimeMs = await Task.Run(() => AccessTime(disk, volume.Disk!.SizeBytes, progress, token), token)
                    .ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                // Raw disk reads need administrator rights; the file tests still count.
                Debug.WriteLine($"[DiskBenchmark] Surface scan skipped: {ex.Message}");
            }
        }

        progress?.Report(new BenchProgress(BenchStage.Done, null, 1, 0));
        return run;
    }

    #region File tests

    private static void RunFileTests(DriveVolume volume, long size, BenchOptions options, BenchRun run,
        IProgress<BenchProgress>? progress, CancellationToken token)
    {
        var path = Path.Combine(volume.Root, TestFileName);
        using var owner = StorageNative.CreateFileW(path,
            StorageNative.GENERIC_READ | StorageNative.GENERIC_WRITE,
            StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE | StorageNative.FILE_SHARE_DELETE,
            IntPtr.Zero, StorageNative.CREATE_ALWAYS,
            StorageNative.FILE_ATTRIBUTE_HIDDEN | StorageNative.FILE_FLAG_DELETE_ON_CLOSE | StorageNative.FILE_FLAG_NO_BUFFERING,
            IntPtr.Zero);
        if (owner.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

        // Write the whole file once: unbuffered writes past the end of the
        // written data make NTFS zero-fill first, which would ruin random writes.
        Prepare(owner, size, progress, token);

        var patterns = Patterns(options.IncludeWrites);
        for (int i = 0; i < patterns.Count; i++)
        {
            var pattern = patterns[i];
            BenchResult? best = null;
            for (int pass = 0; pass < Math.Max(1, options.Passes); pass++)
            {
                token.ThrowIfCancellationRequested();
                double baseFraction = (i + pass / (double)Math.Max(1, options.Passes)) / patterns.Count;
                var result = RunPattern(path, size, pattern, options.Duration,
                    mbps => progress?.Report(new BenchProgress(BenchStage.Testing, pattern.Id, baseFraction, mbps)), token);
                if (best == null || result.MBps > best.MBps) best = result;
                // Give SSD caches and the drive's controller a moment between runs.
                Thread.Sleep(700);
            }
            run.Results.Add(best!);
            progress?.Report(new BenchProgress(BenchStage.Testing, pattern.Id, (i + 1.0) / patterns.Count, best!.MBps, best));
        }
    }

    private static unsafe void Prepare(SafeFileHandle handle, long size, IProgress<BenchProgress>? progress, CancellationToken token)
    {
        const int Block = 1 << 20;
        var buffer = (byte*)NativeMemory.AlignedAlloc(Block, 4096);
        try
        {
            Random.Shared.NextBytes(new Span<byte>(buffer, Block));
            if (!StorageNative.SetFilePointerEx(handle, 0, out _, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var sw = Stopwatch.StartNew();
            for (long written = 0; written < size; written += Block)
            {
                token.ThrowIfCancellationRequested();
                // A different first word per block keeps controllers from deduplicating.
                *(long*)buffer = written ^ 0x5DEECE66DL;
                if (!StorageNative.WriteFile(handle, buffer, Block, out int done, IntPtr.Zero) || done != Block)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((written / Block) % 32 == 0)
                    progress?.Report(new BenchProgress(BenchStage.Preparing, null, written / (double)size,
                        written / Math.Max(0.001, sw.Elapsed.TotalSeconds) / 1e6));
            }
            StorageNative.FlushFileBuffers(handle);
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    private static unsafe BenchResult RunPattern(string path, long size, BenchPattern pattern, TimeSpan duration,
        Action<double> live, CancellationToken token)
    {
        int threads = pattern.QueueDepth;
        long blocks = size / pattern.BlockBytes;
        long totalBytes = 0, totalOps = 0, totalLatencyTicks = 0;
        long nextSequential = -1;
        int stop = 0;
        Exception? failure = null;

        var handles = new SafeFileHandle[threads];
        try
        {
            for (int t = 0; t < threads; t++)
            {
                // Separate handles: synchronous I/O on one shared handle is serialized by Windows.
                handles[t] = StorageNative.CreateFileW(path,
                    StorageNative.GENERIC_READ | StorageNative.GENERIC_WRITE,
                    StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE | StorageNative.FILE_SHARE_DELETE,
                    IntPtr.Zero, StorageNative.OPEN_EXISTING, StorageNative.FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
                if (handles[t].IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var samples = new List<double>();
            var workers = new Thread[threads];
            var startGate = new ManualResetEventSlim(false);
            for (int t = 0; t < threads; t++)
            {
                int index = t;
                workers[t] = new Thread(() =>
                {
                    var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)pattern.BlockBytes, 4096);
                    var random = new Random(unchecked(Environment.TickCount * 31 + index * 7919));
                    try
                    {
                        random.NextBytes(new Span<byte>(buffer, pattern.BlockBytes));
                        long bytes = 0, ops = 0, latency = 0;
                        startGate.Wait();
                        while (Volatile.Read(ref stop) == 0)
                        {
                            long block = pattern.IsSequential
                                ? Interlocked.Increment(ref nextSequential) % blocks
                                : random.NextInt64(blocks);
                            if (!StorageNative.SetFilePointerEx(handles[index], block * pattern.BlockBytes, out _, 0))
                                throw new Win32Exception(Marshal.GetLastWin32Error());

                            long t0 = Stopwatch.GetTimestamp();
                            bool ok;
                            int done;
                            if (pattern.IsRead)
                            {
                                ok = StorageNative.ReadFile(handles[index], buffer, pattern.BlockBytes, out done, IntPtr.Zero);
                            }
                            else
                            {
                                *(long*)buffer = block ^ t0;
                                ok = StorageNative.WriteFile(handles[index], buffer, pattern.BlockBytes, out done, IntPtr.Zero);
                            }
                            long t1 = Stopwatch.GetTimestamp();
                            if (!ok || done != pattern.BlockBytes) throw new Win32Exception(Marshal.GetLastWin32Error());

                            latency += t1 - t0;
                            ops++;
                            bytes += done;
                            if ((ops & 15) == 0)
                            {
                                Interlocked.Add(ref totalBytes, bytes);
                                Interlocked.Add(ref totalOps, ops);
                                Interlocked.Add(ref totalLatencyTicks, latency);
                                bytes = ops = latency = 0;
                            }
                        }
                        Interlocked.Add(ref totalBytes, bytes);
                        Interlocked.Add(ref totalOps, ops);
                        Interlocked.Add(ref totalLatencyTicks, latency);
                    }
                    catch (Exception ex)
                    {
                        failure ??= ex;
                        Volatile.Write(ref stop, 1);
                    }
                    finally
                    {
                        NativeMemory.AlignedFree(buffer);
                    }
                }) { IsBackground = true, Name = $"Bench {pattern.Id} #{t}" };
                workers[t].Start();
            }

            var sw = Stopwatch.StartNew();
            startGate.Set();
            long lastBytes = 0;
            double lastTime = 0;
            while (sw.Elapsed < duration && Volatile.Read(ref stop) == 0 && !token.IsCancellationRequested)
            {
                Thread.Sleep(250);
                double now = sw.Elapsed.TotalSeconds;
                long bytesNow = Interlocked.Read(ref totalBytes);
                double mbps = (bytesNow - lastBytes) / Math.Max(0.001, now - lastTime) / 1e6;
                samples.Add(mbps);
                live(mbps);
                lastBytes = bytesNow;
                lastTime = now;
            }
            Volatile.Write(ref stop, 1);
            foreach (var worker in workers) worker.Join();
            double seconds = sw.Elapsed.TotalSeconds;

            token.ThrowIfCancellationRequested();
            if (failure != null) throw failure;

            return new BenchResult
            {
                PatternId = pattern.Id,
                Kind = pattern.Kind,
                BlockBytes = pattern.BlockBytes,
                QueueDepth = pattern.QueueDepth,
                MBps = totalBytes / seconds / 1e6,
                Iops = totalOps / seconds,
                AvgLatencyMs = totalOps == 0 ? 0 : totalLatencyTicks * 1000.0 / Stopwatch.Frequency / totalOps,
                Samples = samples.ToArray()
            };
        }
        finally
        {
            foreach (var h in handles) h?.Dispose();
        }
    }

    #endregion

    #region Surface scan and access time (raw disk, read only)

    private static SafeFileHandle OpenDiskReadOnly(int diskNumber)
    {
        // GENERIC_READ only: this handle cannot write to the disk, whatever happens.
        var handle = StorageNative.CreateFileW($@"\\.\PhysicalDrive{diskNumber}", StorageNative.GENERIC_READ,
            StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE, IntPtr.Zero, StorageNative.OPEN_EXISTING,
            StorageNative.FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }

    /// <summary>
    /// Reads a stretch of the disk at <paramref name="points"/> evenly spaced
    /// places and measures the transfer rate at each one.
    /// </summary>
    public static unsafe List<SurfacePoint> SurfaceScan(int diskNumber, long diskBytes, int points,
        IProgress<BenchProgress>? progress, CancellationToken token)
    {
        const int Block = 1 << 20;
        const int BlocksPerPoint = 24;
        points = Math.Clamp(points, 10, 400);
        var result = new List<SurfacePoint>(points);

        using var disk = OpenDiskReadOnly(diskNumber);
        var buffer = (byte*)NativeMemory.AlignedAlloc(Block, 4096);
        try
        {
            long usable = diskBytes / Block * Block - (long)(BlocksPerPoint + 1) * Block;
            if (usable <= 0) return result;
            for (int p = 0; p < points; p++)
            {
                token.ThrowIfCancellationRequested();
                long offset = usable * p / Math.Max(1, points - 1) / Block * Block;
                if (!StorageNative.SetFilePointerEx(disk, offset, out _, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());

                // The first read pays the seek; time only the streaming part.
                if (!StorageNative.ReadFile(disk, buffer, Block, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                long t0 = Stopwatch.GetTimestamp();
                long read = 0;
                for (int b = 0; b < BlocksPerPoint; b++)
                {
                    if (!StorageNative.ReadFile(disk, buffer, Block, out int got, IntPtr.Zero) || got <= 0)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    read += got;
                }
                double seconds = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
                var point = new SurfacePoint { Position = p / (double)Math.Max(1, points - 1), MBps = read / Math.Max(1e-6, seconds) / 1e6 };
                result.Add(point);
                progress?.Report(new BenchProgress(BenchStage.Surface, null, (p + 1.0) / points, point.MBps, null, point));
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
        return result;
    }

    /// <summary>Average milliseconds to read one random 4 KB block anywhere on the disk.</summary>
    public static unsafe double? AccessTime(int diskNumber, long diskBytes, IProgress<BenchProgress>? progress, CancellationToken token)
    {
        const int Block = 4096;
        const int Samples = 160;
        using var disk = OpenDiskReadOnly(diskNumber);
        var buffer = (byte*)NativeMemory.AlignedAlloc(Block, 4096);
        try
        {
            var random = new Random();
            long blocks = diskBytes / Block - 1;
            if (blocks <= 0) return null;
            long ticks = 0;
            for (int i = 0; i < Samples; i++)
            {
                token.ThrowIfCancellationRequested();
                long offset = random.NextInt64(blocks) * Block;
                long t0 = Stopwatch.GetTimestamp();
                if (!StorageNative.SetFilePointerEx(disk, offset, out _, 0) ||
                    !StorageNative.ReadFile(disk, buffer, Block, out _, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                ticks += Stopwatch.GetTimestamp() - t0;
                if (i % 16 == 0) progress?.Report(new BenchProgress(BenchStage.AccessTime, null, (i + 1.0) / Samples, 0));
            }
            return ticks * 1000.0 / Stopwatch.Frequency / Samples;
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    #endregion
}

/// <summary>Saved speed tests (newest first), for before/after charts.</summary>
public static class BenchHistory
{
    private const string SettingKey = "DiskBench.History";
    private const int MaxRuns = 40;
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static List<BenchRun> Load()
    {
        lock (Gate)
        {
            try
            {
                var text = DatabaseService.Instance.GetSetting(SettingKey);
                if (string.IsNullOrEmpty(text)) return new List<BenchRun>();
                return JsonSerializer.Deserialize<List<BenchRun>>(text, Json) ?? new List<BenchRun>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BenchHistory] Load failed: {ex.Message}");
                return new List<BenchRun>();
            }
        }
    }

    public static void Add(BenchRun run)
    {
        lock (Gate)
        {
            var runs = Load();
            runs.Insert(0, run);
            if (runs.Count > MaxRuns) runs.RemoveRange(MaxRuns, runs.Count - MaxRuns);
            Save(runs);
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            var runs = Load();
            if (runs.RemoveAll(r => r.Id == id) > 0) Save(runs);
        }
    }

    private static void Save(List<BenchRun> runs)
    {
        try
        {
            DatabaseService.Instance.SetSetting(SettingKey, JsonSerializer.Serialize(runs, Json));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BenchHistory] Save failed: {ex.Message}");
        }
    }
}

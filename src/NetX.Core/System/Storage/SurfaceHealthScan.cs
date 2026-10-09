using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NetX.Core.System.Tweaks;

namespace NetX.Core.System.Storage;

/// <summary>How well a stretch of the disk read (worst read wins).</summary>
public enum BlockHealth : byte
{
    Untested = 0,
    Good = 1,
    Slow = 2,
    VerySlow = 3,
    Error = 4
}

/// <summary>One 4 KB sector the drive couldn't read, with the file that uses it (when known).</summary>
public sealed record BadSector(long DiskOffset, int Win32Error)
{
    public char? Volume { get; init; }
    public string? File { get; init; }
}

/// <summary>Live state of a surface scan (shared with the page; read it from the UI thread on Changed).</summary>
public sealed class HealthScanState
{
    public int DiskNumber { get; init; }
    public long DiskBytes { get; init; }
    public bool Full { get; init; }
    public int Blocks { get; init; }

    /// <summary>Per display block, the worst <see cref="BlockHealth"/> seen there.</summary>
    public byte[] Map { get; init; } = Array.Empty<byte>();

    public long BytesRead;
    public long ReadsGood, ReadsSlow, ReadsVerySlow, ReadsError;
    public double MedianMs, LastMs, Mbps, Position;
    public double SlowMs, VerySlowMs;
    public readonly List<BadSector> BadSectors = new();
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public TimeSpan Elapsed;
    public bool Finished, Cancelled, Disconnected;
    public int FatalError;

    /// <summary>Second pass: slow areas are read again; only those still slow count as weak.</summary>
    public bool Verifying;
    public int VerifyDone, VerifyTotal;
    public TimeSpan VerifyStarted;

    /// <summary>Where the second pass is reading (0–1); <see cref="Position"/> keeps how far the scan got.</summary>
    public double VerifyPosition;

    /// <summary>Slow/very slow reads that were just interrupted the first time (other programs, the drive's housekeeping).</summary>
    public long Recovered;
}

/// <summary>
/// Reads a whole disk (or, quickly, thousands of points spread over it) straight
/// from the hardware, read-only, timing every 1 MB: healthy areas answer in the
/// drive's normal time, weak ones need retries (slow), and bad sectors can't be
/// read at all. A block that fails is read again 4 KB at a time to pin down the
/// exact sectors, and on NTFS the files using them are named. Nothing is ever
/// written — marking bad sectors so Windows never uses them again is
/// <see cref="BadClusterRepair"/>'s job (chkdsk /r, the file system's own tool).
/// </summary>
public sealed unsafe class SurfaceHealthScan
{
    private const int Chunk = 1 << 20;
    private const int Sector = 4096;
    private const int QuickPoints = 4096;
    private const int MaxBadSectors = 4096;
    private const int MaxRetests = 4096;

    private static readonly object Gate = new();
    private static SurfaceHealthScan? _current;

    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _running = new(true);
    private readonly Thread _thread;

    public static SurfaceHealthScan? Current
    {
        get { lock (Gate) return _current; }
    }

    public HealthScanState State { get; }
    public bool IsRunning => !State.Finished;
    public bool IsPaused => !_running.IsSet;

    /// <summary>Raised on the scan thread at most ten times a second.</summary>
    public event Action<SurfaceHealthScan>? Changed;

    /// <summary>Raised on the scan thread when it ends (finished, stopped or the drive went away).</summary>
    public event Action<SurfaceHealthScan>? Completed;

    private SurfaceHealthScan(int disk, long bytes, bool full, int blocks)
    {
        State = new HealthScanState { DiskNumber = disk, DiskBytes = bytes, Full = full, Blocks = blocks, Map = new byte[blocks] };
        _thread = new Thread(Run) { IsBackground = true, Name = "WinXTools surface scan" };
    }

    /// <summary>Null when a scan is already running.</summary>
    public static SurfaceHealthScan? Start(int diskNumber, long diskBytes, bool full, int blocks)
    {
        lock (Gate)
        {
            if (_current is { IsRunning: true }) return null;
            var scan = new SurfaceHealthScan(diskNumber, diskBytes, full, Math.Max(1, blocks));
            _current = scan;
            scan._thread.Start();
            return scan;
        }
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _running.Set();
    }

    public void Pause(bool pause)
    {
        if (pause) _running.Reset();
        else _running.Set();
    }

    /// <summary>WinXTools is closing.</summary>
    public static void StopRunning(TimeSpan wait)
    {
        var scan = Current;
        if (scan is not { IsRunning: true }) return;
        scan.Stop();
        scan._thread.Join(wait);
    }

    private void Run()
    {
        var s = State;
        var clock = Stopwatch.StartNew();
        long lastRaise = 0;
        var recent = new List<double>(256);
        var slowCount = new int[s.Blocks];
        var verySlowCount = new int[s.Blocks];
        var errorCount = new int[s.Blocks];
        var goodCount = new int[s.Blocks];
        var retest = new List<(long Offset, int Length, BlockHealth Health)>();
        var buffer = (byte*)NativeMemory.AlignedAlloc(Chunk, 4096);
        try
        {
            // GENERIC_READ only: this handle cannot write to the disk, whatever happens.
            using var disk = StorageNative.CreateFileW($@"\\.\PhysicalDrive{s.DiskNumber}", StorageNative.GENERIC_READ,
                StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE, IntPtr.Zero, StorageNative.OPEN_EXISTING,
                StorageNative.FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
            if (disk.IsInvalid)
            {
                s.FatalError = Marshal.GetLastWin32Error();
                return;
            }

            long usable = s.DiskBytes / Sector * Sector;
            long count = s.Full ? (usable + Chunk - 1) / Chunk : Math.Min(QuickPoints, Math.Max(1, usable / Chunk));

            BlockHealth Classify(double ms) =>
                s.MedianMs == 0 || ms <= s.SlowMs ? BlockHealth.Good : ms <= s.VerySlowMs ? BlockHealth.Slow : BlockHealth.VerySlow;

            void Tally(BlockHealth health, int delta)
            {
                switch (health)
                {
                    case BlockHealth.Good: s.ReadsGood += delta; break;
                    case BlockHealth.Slow: s.ReadsSlow += delta; break;
                    case BlockHealth.VerySlow: s.ReadsVerySlow += delta; break;
                    default: s.ReadsError += delta; break;
                }
            }

            // Each map cell counts the reads of every kind that landed in it; the worst one present sets its color.
            void Paint(long offset, int length, BlockHealth health, int delta)
            {
                var counts = health switch { BlockHealth.Error => errorCount, BlockHealth.VerySlow => verySlowCount, BlockHealth.Slow => slowCount, _ => goodCount };
                int first = (int)Math.Min(s.Blocks - 1, offset * (double)s.Blocks / usable);
                int last = (int)Math.Min(s.Blocks - 1, (offset + length - 1) * (double)s.Blocks / usable);
                for (int b = first; b <= last; b++)
                {
                    counts[b] = Math.Max(0, counts[b] + delta);
                    s.Map[b] = BlockColor(b, goodCount, slowCount, verySlowCount, errorCount);
                }
            }

            for (long i = 0; i < count; i++)
            {
                _running.Wait(_cts.Token);
                if (_cts.IsCancellationRequested) break;

                long offset = s.Full ? i * Chunk : (count <= 1 ? 0 : (usable - Chunk) / (count - 1) * i) / Sector * Sector;
                int length = (int)Math.Min(Chunk, usable - offset);
                if (length <= 0) break;

                long t0 = Stopwatch.GetTimestamp();
                int got = 0;
                bool ok = StorageNative.SetFilePointerEx(disk, offset, out _, 0) && StorageNative.ReadFile(disk, buffer, length, out got, IntPtr.Zero) && got > 0;
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && IsGone(error))
                {
                    s.Disconnected = true;
                    break;
                }

                BlockHealth health;
                if (!ok)
                {
                    health = BlockHealth.Error;
                    Localize(disk, buffer, offset, length);
                }
                else
                {
                    recent.Add(ms);
                    if (recent.Count > 256) recent.RemoveAt(0);
                    if (recent.Count >= 16 && (i % 32 == 0 || s.MedianMs == 0))
                    {
                        var sorted = recent.OrderBy(x => x).ToList();
                        s.MedianMs = sorted[sorted.Count / 2];
                        s.SlowMs = Math.Max(s.MedianMs * 4, s.MedianMs + 25);
                        s.VerySlowMs = Math.Max(s.MedianMs * 12, 150);
                    }
                    health = Classify(ms);
                    if (health is BlockHealth.Slow or BlockHealth.VerySlow && retest.Count < MaxRetests)
                        retest.Add((offset, length, health));
                    s.BytesRead += got;
                }

                Tally(health, +1);
                Paint(offset, length, health, +1);
                s.LastMs = ms;
                s.Position = (offset + length) / (double)usable;

                long now = clock.ElapsedMilliseconds;
                if (now - lastRaise >= 100)
                {
                    lastRaise = now;
                    s.Elapsed = clock.Elapsed;
                    s.Mbps = s.BytesRead / Math.Max(0.001, clock.Elapsed.TotalSeconds) / 1e6;
                    Raise(Changed);
                }
            }

            // Second pass: a read can be slow because something else used the disk at that moment.
            // Each slow area is read once more and keeps the better of its two readings: one that reads
            // normally now was only interrupted, one still slow is weak, and one that fails outright now
            // is a bad sector after all. A full scan reads in sequence, so the 1 MB before each area is
            // read first (untimed) to time it the same way — without a seek the first reading didn't have.
            if (!_cts.IsCancellationRequested && !s.Disconnected && retest.Count > 0)
            {
                s.VerifyTotal = retest.Count;
                s.VerifyStarted = clock.Elapsed;
                s.Verifying = true;
                foreach (var (offset, length, before) in retest)
                {
                    _running.Wait(_cts.Token);
                    if (_cts.IsCancellationRequested) break;

                    if (s.Full && offset >= Chunk && StorageNative.SetFilePointerEx(disk, offset - Chunk, out _, 0))
                        StorageNative.ReadFile(disk, buffer, Chunk, out _, IntPtr.Zero);

                    long t0 = Stopwatch.GetTimestamp();
                    int got = 0;
                    bool ok = StorageNative.SetFilePointerEx(disk, offset, out _, 0) && StorageNative.ReadFile(disk, buffer, length, out got, IntPtr.Zero) && got > 0;
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    int error = ok ? 0 : Marshal.GetLastWin32Error();
                    if (!ok && IsGone(error))
                    {
                        s.Disconnected = true;
                        break;
                    }

                    var after = ok ? Classify(ms) : BlockHealth.Error;
                    if (after == BlockHealth.Error) Localize(disk, buffer, offset, length);
                    else if (after > before) after = before;
                    if (after != before)
                    {
                        Tally(before, -1);
                        Tally(after, +1);
                        Paint(offset, length, before, -1);
                        Paint(offset, length, after, +1);
                        if (after == BlockHealth.Good) s.Recovered++;
                    }
                    s.VerifyDone++;
                    s.LastMs = ms;
                    s.VerifyPosition = (offset + length) / (double)usable;

                    long now = clock.ElapsedMilliseconds;
                    if (now - lastRaise >= 100)
                    {
                        lastRaise = now;
                        s.Elapsed = clock.Elapsed;
                        Raise(Changed);
                    }
                }
                s.Verifying = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SurfaceScan] {ex}");
            s.FatalError = ex.HResult;
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
            s.Elapsed = clock.Elapsed;
            s.Verifying = false;
            s.Cancelled = _cts.IsCancellationRequested;
            NameFiles(s);
            s.Finished = true;
            Raise(Changed);
            Raise(Completed);
        }
    }

    private static byte BlockColor(int b, int[] good, int[] slow, int[] verySlow, int[] error) =>
        (byte)(error[b] > 0 ? BlockHealth.Error
            : verySlow[b] > 0 ? BlockHealth.VerySlow
            : slow[b] > 0 ? BlockHealth.Slow
            : good[b] > 0 ? BlockHealth.Good
            : BlockHealth.Untested);

    /// <summary>A 1 MB read failed: read it again 4 KB at a time to find exactly which sectors are bad.</summary>
    private void Localize(SafeFileHandle disk, byte* buffer, long offset, int length)
    {
        var s = State;
        for (int at = 0; at < length && !_cts.IsCancellationRequested; at += Sector)
        {
            if (StorageNative.SetFilePointerEx(disk, offset + at, out _, 0) && StorageNative.ReadFile(disk, buffer, Sector, out int got, IntPtr.Zero) && got > 0)
            {
                s.BytesRead += got;
                continue;
            }
            int error = Marshal.GetLastWin32Error();
            lock (s.BadSectors)
                if (s.BadSectors.Count < MaxBadSectors) s.BadSectors.Add(new BadSector(offset + at, error));
        }
    }

    private static bool IsGone(int error) => error is 2 or 21 or 433 or 1167 or 1617; // not found / not ready / device gone

    private void Raise(Action<SurfaceHealthScan>? handler)
    {
        try { handler?.Invoke(this); }
        catch (Exception ex) { Debug.WriteLine($"[SurfaceScan] Handler failed: {ex.Message}"); }
    }

    /// <summary>For bad sectors inside an NTFS volume: which file each one belongs to.</summary>
    private static void NameFiles(HealthScanState s)
    {
        List<BadSector> bad;
        lock (s.BadSectors) bad = s.BadSectors.ToList();
        if (bad.Count == 0) return;
        try
        {
            var volumes = DriveInventory.GetVolumes().Where(v => v.DiskNumber == s.DiskNumber && v.IsNtfs && v.ClusterBytes > 0).ToList();
            var named = new List<BadSector>(bad.Count);
            foreach (var sector in bad)
            {
                var v = volumes.FirstOrDefault(x => sector.DiskOffset >= x.DiskOffset && sector.DiskOffset < x.DiskOffset + x.TotalBytes);
                if (v == null)
                {
                    named.Add(sector);
                    continue;
                }
                long cluster = (sector.DiskOffset - v.DiskOffset) / v.ClusterBytes;
                named.Add(sector with { Volume = v.Letter, File = ClusterOwners.Lookup(v.Letter, cluster) });
            }
            lock (s.BadSectors)
            {
                s.BadSectors.Clear();
                s.BadSectors.AddRange(named);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SurfaceScan] Naming files failed: {ex.Message}");
        }
    }
}

/// <summary>Which file uses a cluster (FSCTL_LOOKUP_STREAM_FROM_CLUSTER, NTFS, administrator).</summary>
public static unsafe class ClusterOwners
{
    private const uint FSCTL_LOOKUP_STREAM_FROM_CLUSTER = 0x000903F0;

    /// <returns>"\Games\x.pak", "(free space)" when nothing uses it, or null when it can't be read.</returns>
    public static string? Lookup(char letter, long cluster)
    {
        using var volume = StorageNative.OpenVolumeForRead(letter);
        if (volume.IsInvalid) return null;
        var input = stackalloc byte[16];
        *(uint*)input = 0;          // Flags
        *(uint*)(input + 4) = 1;    // NumberOfClusters
        *(long*)(input + 8) = cluster;
        int size = 4096;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var output = (byte*)NativeMemory.Alloc((nuint)size);
            try
            {
                bool ok = StorageNative.DeviceIoControl(volume, FSCTL_LOOKUP_STREAM_FROM_CLUSTER, input, 16, output, size, out int returned, IntPtr.Zero);
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                uint matches = *(uint*)(output + 4), required = *(uint*)(output + 8);
                if (!ok && error == StorageNative.ERROR_MORE_DATA && required > size && required < (1 << 20))
                {
                    size = (int)required;
                    continue;
                }
                if (!ok) return null;
                if (matches == 0) return "(free space)";
                uint entryOffset = *(uint*)output;
                if (entryOffset + 24 >= returned) return null;
                var name = new string((char*)(output + entryOffset + 24));
                int stream = name.IndexOf("::", StringComparison.Ordinal);
                return stream > 0 ? name[..stream] : name;
            }
            finally
            {
                NativeMemory.Free(output);
            }
        }
        return null;
    }
}

/// <summary>
/// Makes Windows stop using bad sectors: chkdsk /r reads every cluster of the
/// volume, moves whatever can still be read to healthy space and records the
/// bad clusters in NTFS's own bad-cluster list ($BadClus), which the file
/// system never allocates again. A data drive is locked while it runs (open
/// files on it are closed); the Windows drive is checked at the next restart.
/// </summary>
public static class BadClusterRepair
{
    public sealed record Outcome(bool Scheduled, bool Completed, int ExitCode, string LastLine);

    public static async Task<Outcome> RunAsync(char letter, bool isSystemDrive, IProgress<(string Line, int? Percent)>? progress, CancellationToken token)
    {
        var live = new ConsoleOutputBuffer();
        string args = isSystemDrive ? $"{char.ToUpperInvariant(letter)}: /r" : $"{char.ToUpperInvariant(letter)}: /r /x";
        // chkdsk asks "Would you like to schedule this volume to be checked the next time the system restarts? (Y/N)".
        var run = CommandRunner.RunAsync(CommandRunner.SystemTool("chkdsk.exe"), args, TimeSpan.FromHours(48), token, live, stdin: "Y\r\n");
        int seen = -1;
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
            if (live.Version == seen) continue;
            seen = live.Version;
            var line = live.GetText().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            if (line != null) progress?.Report((line, DefragEngine.ParsePercent(line)));
        }
        var result = await run.ConfigureAwait(false);
        var last = result.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        return new Outcome(isSystemDrive && result.ExitCode is 0 or 3, !isSystemDrive && result.ExitCode is 0 or 1, result.ExitCode, last);
    }
}

/// <summary>What the drive itself counts (Windows' storage reliability counters; administrator).</summary>
public sealed record ReliabilityCounters(long? ReadErrorsTotal, long? ReadErrorsUncorrected, long? WriteErrorsUncorrected,
    int? Temperature, int? TemperatureMax, int? Wear, long? PowerOnHours, long? StartStopCycles);

public static class DiskReliability
{
    public static ReliabilityCounters? Read(int diskNumber)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM MSFT_StorageReliabilityCounter WHERE DeviceId = '{diskNumber}'"),
                new global::System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = true });
            foreach (ManagementObject counter in searcher.Get())
            {
                using (counter)
                {
                    long? L(string name) => counter[name] is { } v ? Convert.ToInt64(v) : null;
                    int? I(string name) => counter[name] is { } v ? Convert.ToInt32(v) : null;
                    return new ReliabilityCounters(L("ReadErrorsTotal"), L("ReadErrorsUncorrected"), L("WriteErrorsUncorrected"),
                        I("Temperature"), I("TemperatureMax"), I("Wear"), L("PowerOnHours"), L("StartStopCycleCount"));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiskReliability] {ex.Message}");
        }
        return null;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetX.Core.System.Storage;

/// <summary>
/// The optimization that is running right now (one at a time). It outlives
/// the page that started it, so the user can leave the Defrag page and come
/// back to the live view; closing WinXTools stops it.
/// </summary>
public sealed class DefragSession
{
    private static readonly object Gate = new();
    private static DefragSession? _current;

    /// <summary>Taken by everything that starts moving data on a drive, so two never start together.</summary>
    internal static readonly object DriveWorkGate = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly bool _removable;

    public static DefragSession? Current
    {
        get { lock (Gate) return _current; }
    }

    public char Letter { get; }
    public IReadOnlyList<DefragOperation> Operations { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public int Step { get; private set; }
    public DefragProgress? LastProgress { get; private set; }
    public DefragRunResult? Result { get; private set; }
    public bool IsRunning => Result == null;
    public bool CancelRequested => _cts.IsCancellationRequested;

    /// <summary>Why it stopped by itself (power cable pulled, drive being removed), or null.</summary>
    public StopReason? StopReason { get; private set; }

    /// <summary>A USB (or other removable) drive: the page asks the user not to unplug it.</summary>
    public bool Removable => _removable;

    /// <summary>Anything the page wants to keep with the run (figures before, files measured…).</summary>
    public object? Tag { get; set; }

    /// <summary>Raised on a worker thread.</summary>
    public event Action<DefragSession, DefragProgress>? ProgressChanged;

    /// <summary>Raised on a worker thread when every step is done, failed or was stopped.</summary>
    public event Action<DefragSession, DefragRunResult>? Completed;

    public Task<DefragRunResult> Task { get; private set; } = null!;

    private DefragSession(char letter, IReadOnlyList<DefragOperation> operations, bool removable)
    {
        Letter = char.ToUpperInvariant(letter);
        Operations = operations;
        _removable = removable;
    }

    /// <summary>
    /// Starts the steps in order; returns null when another optimization (or a
    /// smart placement) is still running. <paramref name="removable"/>: a USB
    /// drive — its cache is flushed every few seconds while Windows works.
    /// </summary>
    public static DefragSession? Start(char letter, IReadOnlyList<DefragOperation> operations, object? tag = null, bool removable = false)
    {
        lock (DriveWorkGate)
        lock (Gate)
        {
            if (_current is { IsRunning: true } || SmartPlacementSession.Current is { IsRunning: true }) return null;
            var session = new DefragSession(letter, operations, removable) { Tag = tag };
            _current = session;
            session.Task = session.RunAsync();
            return session;
        }
    }

    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Stops a running optimization (WinXTools is closing) and waits a little for it.</summary>
    public static void StopRunning(TimeSpan wait)
    {
        var session = Current;
        if (session is not { IsRunning: true }) return;
        session.Cancel();
        try { session.Task.Wait(wait); } catch { }
    }

    private async Task<DefragRunResult> RunAsync()
    {
        DefragRunResult result = new() { Outcome = DefragOutcome.Completed };
        var total = TimeSpan.Zero;
        // Windows' defragmenter holds the drive itself and lets go when it is asked
        // to be removed; the guard keeps the PC awake and stops the run on power loss
        // or a removal request.
        using var guard = new SafeOperationGuard(Letter, $"WinXTools is optimizing drive {Letter}:",
            flushPeriodically: _removable, holdsHandles: false, _cts.Token);
        guard.StopRequested += reason => StopReason = reason;
        try
        {
            await global::System.Threading.Tasks.Task.Yield(); // leave Start() before the first step runs
            for (int i = 0; i < Operations.Count; i++)
            {
                Step = i;
                var reporter = new InlineProgress(p =>
                {
                    LastProgress = p;
                    ProgressChanged?.Invoke(this, p);
                });
                result = await DefragEngine.RunAsync(Letter, Operations[i], reporter, guard.Token).ConfigureAwait(false);
                total += result.Duration;
                if (result.Outcome != DefragOutcome.Completed) break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefragSession] {ex}");
            result = new DefragRunResult { Outcome = DefragOutcome.Failed, Message = ex.Message };
        }

        var final = new DefragRunResult
        {
            Outcome = result.Outcome,
            ExitCode = result.ExitCode,
            Duration = total,
            Message = result.Message,
            Output = result.Output
        };
        Result = final;
        Completed?.Invoke(this, final);
        return final;
    }

    /// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; would capture a context).</summary>
    private sealed class InlineProgress(Action<DefragProgress> report) : IProgress<DefragProgress>
    {
        public void Report(DefragProgress value) => report(value);
    }
}

/// <summary>
/// Reads a set of files straight from the disk (no Windows cache) and
/// reports the speed — run on the most fragmented files before and after
/// defragmenting to show what the user actually gains.
/// </summary>
public static unsafe class FileReadSpeedTest
{
    private const int Chunk = 1 << 20;

    /// <summary>
    /// Drives keep recently read and written data in a cache of their own (up to a
    /// few hundred MB). Reading 512 MB from the middle of the disk first (read-only)
    /// pushes that out, so a measurement right after comes from the platter.
    /// </summary>
    public static void EvictDriveCache(int diskNumber, long diskBytes, CancellationToken token)
    {
        const long Amount = 512L << 20;
        if (diskBytes < Amount * 4) return;
        using SafeFileHandle disk = StorageNative.CreateFileW($@"\\.\PhysicalDrive{diskNumber}", StorageNative.GENERIC_READ,
            StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE, IntPtr.Zero, StorageNative.OPEN_EXISTING,
            StorageNative.FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
        if (disk.IsInvalid) return;
        var buffer = (byte*)NativeMemory.AlignedAlloc(Chunk, 4096);
        try
        {
            long offset = diskBytes / 2 / Chunk * Chunk;
            if (!StorageNative.SetFilePointerEx(disk, offset, out _, 0)) return;
            for (long read = 0; read < Amount; read += Chunk)
            {
                token.ThrowIfCancellationRequested();
                if (!StorageNative.ReadFile(disk, buffer, Chunk, out int got, IntPtr.Zero) || got <= 0) break;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    /// <returns>Decimal MB/s, or null when nothing could be read.</returns>
    public static double? Measure(IReadOnlyList<string> files, long maxBytes, TimeSpan maxTime, CancellationToken token)
    {
        var buffer = (byte*)NativeMemory.AlignedAlloc(Chunk, 4096);
        try
        {
            long total = 0;
            var sw = Stopwatch.StartNew();
            foreach (var path in files)
            {
                if (total >= maxBytes || sw.Elapsed > maxTime) break;
                token.ThrowIfCancellationRequested();

                // NO_BUFFERING: the bytes come from the disk, not from RAM.
                using SafeFileHandle handle = StorageNative.CreateFileW(LongPath(path), StorageNative.GENERIC_READ,
                    StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE | StorageNative.FILE_SHARE_DELETE,
                    IntPtr.Zero, StorageNative.OPEN_EXISTING, StorageNative.FILE_FLAG_NO_BUFFERING | 0x08000000 /* sequential scan */,
                    IntPtr.Zero);
                if (handle.IsInvalid) continue;

                while (total < maxBytes && sw.Elapsed <= maxTime)
                {
                    token.ThrowIfCancellationRequested();
                    if (!StorageNative.ReadFile(handle, buffer, Chunk, out int read, IntPtr.Zero) || read <= 0) break;
                    total += read;
                    if (read < Chunk) break;
                }
            }
            double seconds = sw.Elapsed.TotalSeconds;
            return total < (1 << 20) || seconds <= 0 ? null : total / seconds / 1e6;
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    private static string LongPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path;
}

/// <summary>Where the NTFS file table starts (to point the head at it while it is read).</summary>
public static unsafe class NtfsInfo
{
    /// <returns>The MFT's first cluster and the volume's cluster count, or null.</returns>
    public static (long MftStartLcn, long TotalClusters)? Read(char letter)
    {
        using var volume = StorageNative.OpenVolumeForRead(letter);
        if (volume.IsInvalid) return null;
        var buffer = stackalloc byte[128];
        if (!StorageNative.DeviceIoControl(volume, StorageNative.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, buffer, 128,
                out int returned, IntPtr.Zero) || returned < 72)
            return null;
        // NTFS_VOLUME_DATA_BUFFER: TotalClusters @16, MftStartLcn @64.
        return (*(long*)(buffer + 64), *(long*)(buffer + 16));
    }
}

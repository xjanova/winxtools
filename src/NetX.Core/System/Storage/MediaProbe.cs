using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NetX.Core.Helpers;

namespace NetX.Core.System.Storage;

public enum MediaSource
{
    /// <summary>The drive answered (rotation rate in its SCSI characteristics page).</summary>
    Drive,
    /// <summary>The disk inside a USB case answered through the bridge (ATA IDENTIFY).</summary>
    Bridge,
    /// <summary>Measured: random reads that take milliseconds mean a moving head.</summary>
    Timing,
    /// <summary>The maker's product line in WinXTools' table of drives.</summary>
    ModelTable
}

/// <summary>What a probe found out about a drive Windows couldn't classify.</summary>
public sealed record MediaAnswer(DriveMedia Media, int? Rpm, MediaSource Source, string? InnerModel);

/// <summary>
/// Finds out whether a drive Windows can't classify (most USB drives) has a
/// spinning platter: first the drive's own answer (rotation rate in SCSI VPD
/// page B1h, then ATA IDENTIFY of the disk inside a USB case — both read-only
/// queries), then a timing probe (24 random 4 KB reads: a platter needs
/// milliseconds per read, flash well under one). Needs administrator rights.
/// Answers are remembered per drive in the admin-only store, so a drive is
/// probed once, not every time a page lists the drives.
/// </summary>
public static unsafe class MediaProbe
{
    private const string CacheFile = "disk_media.json";
    private const int MaxCacheEntries = 64;

    private static readonly object Gate = new();
    private static Dictionary<string, CachedMedia>? _cache;
    private static readonly HashSet<string> NoAnswerThisRun = new();

    private static readonly Lazy<bool> Elevated = new(() =>
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    });

    public sealed class CachedMedia
    {
        public DriveMedia Media { get; set; }
        public int? Rpm { get; set; }
        public MediaSource Source { get; set; }
        public string? InnerModel { get; set; }
    }

    /// <summary>The drive's media type, or null when it can't be found out (or not as administrator).</summary>
    public static MediaAnswer? Resolve(int diskNumber, string model, long sizeBytes, DriveBus bus, bool removableMedia)
    {
        if (!Elevated.Value || IntPtr.Size != 8 || sizeBytes <= 0) return null;
        var key = $"{model.Trim()}|{sizeBytes}";
        lock (Gate)
        {
            _cache ??= AdminOnlyStore.Load<Dictionary<string, CachedMedia>>(CacheFile) ?? new();
            if (_cache.TryGetValue(key, out var hit) && hit.Media != DriveMedia.Unknown)
                return new MediaAnswer(hit.Media, hit.Rpm, hit.Source, Clean(hit.InnerModel));
            if (NoAnswerThisRun.Contains(key)) return null;
        }

        MediaAnswer? answer = null;
        try
        {
            answer = FromVpd(diskNumber);
            if (answer is null or { Media: DriveMedia.Unknown } && bus == DriveBus.Usb)
                answer = FromAtaIdentify(diskNumber) ?? answer;
            if (answer is null or { Media: DriveMedia.Unknown })
            {
                var timed = FromTiming(diskNumber, sizeBytes, removableMedia);
                if (timed != null) answer = timed with { InnerModel = answer?.InnerModel };
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaProbe] disk {diskNumber}: {ex.Message}");
        }

        lock (Gate)
        {
            if (answer is { Media: not DriveMedia.Unknown })
            {
                _cache![key] = new CachedMedia { Media = answer.Media, Rpm = answer.Rpm, Source = answer.Source, InnerModel = answer.InnerModel };
                while (_cache.Count > MaxCacheEntries) _cache.Remove(_cache.Keys.First());
                AdminOnlyStore.Save(CacheFile, _cache);
            }
            else
            {
                NoAnswerThisRun.Add(key);
            }
        }
        return answer;
    }

    #region The drive's own answer

    /// <summary>INQUIRY, Block Device Characteristics VPD page: MEDIUM ROTATION RATE (bytes 4–5).</summary>
    private static MediaAnswer? FromVpd(int diskNumber)
    {
        using var disk = OpenForCommands(diskNumber);
        if (disk.IsInvalid) return null;
        var data = ScsiDataIn(disk, [0x12, 0x01, 0xB1, 0x00, 0x40, 0x00], 64);
        if (data == null || data.Length < 8 || data[1] != 0xB1) return null;
        return Classify((data[4] << 8) | data[5], MediaSource.Drive, null);
    }

    /// <summary>ATA IDENTIFY DEVICE through the USB bridge (SAT ATA PASS-THROUGH 16): word 217 is the rotation rate.</summary>
    private static MediaAnswer? FromAtaIdentify(int diskNumber)
    {
        using var disk = OpenForCommands(diskNumber);
        if (disk.IsInvalid) return null;
        // PIO data-in, data from the device, length in the sector count (one 512-byte block).
        var data = ScsiDataIn(disk, [0x85, 0x08, 0x0E, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0xEC, 0], 512);
        if (data == null || data.Length < 512 || !LooksLikeIdentify(data)) return null;
        string? inner = Clean(AtaString(data, 54, 40));
        return Classify(data[434] | (data[435] << 8), MediaSource.Bridge, inner);
    }

    private static MediaAnswer? Classify(int rotationRate, MediaSource source, string? innerModel) => rotationRate switch
    {
        1 => new MediaAnswer(DriveMedia.Ssd, 0, source, innerModel),
        >= 0x401 and <= 0xFFFE => new MediaAnswer(DriveMedia.Hdd, rotationRate, source, innerModel),
        _ => innerModel != null ? new MediaAnswer(DriveMedia.Unknown, null, source, innerModel) : null
    };

    private static bool LooksLikeIdentify(byte[] data)
    {
        // Word 255: signature A5h plus a checksum that makes all 512 bytes add up to zero.
        if (data[510] == 0xA5)
        {
            int sum = 0;
            for (int i = 0; i < 512; i++) sum += data[i];
            return (sum & 0xFF) == 0;
        }
        // Older drives have no checksum: accept a readable model name.
        return Clean(AtaString(data, 54, 40)) != null;
    }

    /// <summary>ATA strings are stored as 16-bit words with the two characters swapped.</summary>
    private static string AtaString(byte[] data, int offset, int length)
    {
        var chars = new char[length];
        for (int i = 0; i + 1 < length; i += 2)
        {
            chars[i] = (char)data[offset + i + 1];
            chars[i + 1] = (char)data[offset + i];
        }
        return new string(chars);
    }

    private static string? Clean(string? text)
    {
        var value = text?.Trim('\0', ' ');
        if (string.IsNullOrEmpty(value) || value.Length < 4 || value.Any(c => c < 0x20 || c > 0x7E)) return null;
        return value;
    }

    private static SafeFileHandle OpenForCommands(int diskNumber) =>
        // Pass-through needs read+write access to the handle; only read commands are ever sent.
        StorageNative.CreateFileW($@"\\.\PhysicalDrive{diskNumber}", StorageNative.GENERIC_READ | StorageNative.GENERIC_WRITE,
            StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE, IntPtr.Zero, StorageNative.OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>One SCSI command that reads <paramref name="dataLength"/> bytes (SCSI_PASS_THROUGH, x64 layout).</summary>
    private static byte[]? ScsiDataIn(SafeFileHandle disk, ReadOnlySpan<byte> cdb, int dataLength)
    {
        const int SptSize = 56, SenseOffset = 56, SenseLength = 32, DataOffset = 88;
        int total = DataOffset + dataLength;
        var buffer = new byte[total];
        fixed (byte* b = buffer)
        {
            *(ushort*)b = SptSize;                  // Length
            b[6] = (byte)cdb.Length;                // CdbLength
            b[7] = SenseLength;                     // SenseInfoLength
            b[8] = 1;                               // DataIn = SCSI_IOCTL_DATA_IN
            *(uint*)(b + 12) = (uint)dataLength;    // DataTransferLength
            *(uint*)(b + 16) = 3;                   // TimeOutValue, seconds
            *(ulong*)(b + 24) = DataOffset;         // DataBufferOffset
            *(uint*)(b + 32) = SenseOffset;         // SenseInfoOffset
            for (int i = 0; i < cdb.Length; i++) b[36 + i] = cdb[i];

            if (!StorageNative.DeviceIoControl(disk, StorageNative.IOCTL_SCSI_PASS_THROUGH, b, total, b, total, out _, IntPtr.Zero))
                return null;
            if (b[2] != 0) return null;             // ScsiStatus: the device refused the command
            int got = (int)*(uint*)(b + 12);        // bytes actually transferred
            if (got <= 0) return null;
            got = Math.Min(got, dataLength);
            return buffer.AsSpan(DataOffset, got).ToArray();
        }
    }

    #endregion

    #region Timing probe

    /// <summary>
    /// 24 random 4 KB reads straight from the disk (read-only handle, no cache).
    /// A moving head needs several milliseconds and varies a lot from read to
    /// read; flash answers in well under a millisecond. USB sticks and card
    /// readers (removable media) can be slow flash, so they are never called
    /// hard disks on timing alone.
    /// </summary>
    private static MediaAnswer? FromTiming(int diskNumber, long sizeBytes, bool removableMedia)
    {
        const int Block = 4096, Samples = 24;
        if (sizeBytes < (256L << 20)) return null;

        using var disk = StorageNative.CreateFileW($@"\\.\PhysicalDrive{diskNumber}", StorageNative.GENERIC_READ,
            StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE, IntPtr.Zero, StorageNative.OPEN_EXISTING,
            StorageNative.FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
        if (disk.IsInvalid) return null;

        var buffer = (byte*)NativeMemory.AlignedAlloc(Block, 4096);
        try
        {
            // Wake a sleeping drive first (spinning up takes seconds and isn't what we measure).
            if (!StorageNative.SetFilePointerEx(disk, 0, out _, 0) || !StorageNative.ReadFile(disk, buffer, Block, out _, IntPtr.Zero))
                return null;

            var random = new Random();
            long blocks = sizeBytes / Block - 1;
            var ms = new double[Samples];
            for (int i = 0; i < Samples; i++)
            {
                long offset = random.NextInt64(blocks) * Block;
                long t0 = Stopwatch.GetTimestamp();
                if (!StorageNative.SetFilePointerEx(disk, offset, out _, 0) || !StorageNative.ReadFile(disk, buffer, Block, out _, IntPtr.Zero))
                    return null;
                ms[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }

            Array.Sort(ms);
            double median = ms[Samples / 2];
            double spread = ms[Samples * 9 / 10] - ms[Samples / 10];
            Debug.WriteLine($"[MediaProbe] disk {diskNumber}: median {median:0.00} ms, spread {spread:0.00} ms");
            if (median >= 3.0 && spread >= 2.0 && !removableMedia) return new MediaAnswer(DriveMedia.Hdd, null, MediaSource.Timing, null);
            if (median <= 1.0) return new MediaAnswer(DriveMedia.Ssd, null, MediaSource.Timing, null);
            return null;
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    #endregion
}

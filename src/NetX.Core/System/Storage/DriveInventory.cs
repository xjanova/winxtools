using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace NetX.Core.System.Storage;

public enum DriveMedia
{
    Unknown,
    Hdd,
    Ssd
}

public enum DriveBus
{
    Unknown,
    Sata,
    Nvme,
    Usb,
    Raid,
    Sas,
    Scsi,
    SdCard,
    Virtual,
    StorageSpaces,
    Other
}

public enum DiskHealth
{
    Unknown,
    Healthy,
    Warning,
    Unhealthy
}

/// <summary>One physical disk (\\.\PhysicalDriveN) as Windows reports it.</summary>
public sealed class PhysicalDisk
{
    public int Number { get; init; }
    public string Model { get; init; } = "";
    public string Firmware { get; init; } = "";
    public long SizeBytes { get; init; }
    public DriveMedia Media { get; init; }
    public DriveBus Bus { get; init; }

    /// <summary>True when the media type came from the drive itself (seek-penalty query), not a guess.</summary>
    public bool MediaReportedByDrive { get; init; }

    /// <summary>How a type Windows didn't know was found out (USB drives), or null.</summary>
    public MediaSource? MediaProbedBy { get; init; }

    /// <summary>Model of the disk inside a USB case ("ST2000LM007-1R8174"), when the bridge says.</summary>
    public string? InnerModel { get; init; }

    /// <summary>Spindle speed reported by Windows; null when unknown.</summary>
    public int? Rpm { get; init; }

    public bool? TrimEnabled { get; init; }
    public DiskHealth Health { get; init; }
    public bool Removable { get; init; }

    public bool IsHdd => Media == DriveMedia.Hdd;
    public bool IsSsd => Media == DriveMedia.Ssd;
}

/// <summary>A drive letter, with the physical disk it lives on.</summary>
public sealed class DriveVolume
{
    public char Letter { get; init; }
    public string Root => $"{Letter}:\\";
    public string Label { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public int ClusterBytes { get; init; }
    public bool IsSystem { get; init; }
    public bool IsRemovable { get; init; }

    /// <summary>The disk this volume lives on; null when it spans several disks (RAID-0, spanned, Storage Spaces).</summary>
    public int? DiskNumber { get; init; }

    /// <summary>Byte offset of the volume on its disk (used to place it on the drawn platter).</summary>
    public long DiskOffset { get; init; }

    public PhysicalDisk? Disk { get; init; }

    public bool IsNtfs => FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? $"{Letter}:" : $"{Label} ({Letter}:)";
}

/// <summary>
/// Lists drive letters and the physical disks behind them: model, bus (SATA,
/// NVMe, USB…), HDD or SSD, spindle speed, TRIM and health. Works without
/// administrator rights and never writes to a disk. When Windows can't tell a
/// hard disk from an SSD (most USB drives) and WinXTools runs as administrator,
/// <see cref="MediaProbe"/> asks the drive or times a few reads, once per drive.
/// </summary>
public static class DriveInventory
{
    public static List<DriveVolume> GetVolumes()
    {
        var wmi = ReadWmiDisks();
        var disks = new Dictionary<int, PhysicalDisk?>();
        var systemLetter = char.ToUpperInvariant(Path.GetPathRoot(Environment.SystemDirectory)?[0] ?? 'C');
        var result = new List<DriveVolume>();

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return result; }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
                var root = drive.RootDirectory.FullName;
                if (root.Length < 2 || root[1] != ':') continue;
                char letter = char.ToUpperInvariant(root[0]);

                int cluster = 0;
                if (StorageNative.GetDiskFreeSpaceW(root, out uint spc, out uint bps, out _, out _))
                    cluster = (int)(spc * bps);

                var (diskNumber, offset) = GetVolumeExtent(letter);
                PhysicalDisk? disk = null;
                if (diskNumber is int n)
                {
                    if (!disks.TryGetValue(n, out disk))
                    {
                        disk = GetDisk(n, wmi);
                        disks[n] = disk;
                    }
                }

                string label = "";
                try { label = drive.VolumeLabel; } catch { }

                result.Add(new DriveVolume
                {
                    Letter = letter,
                    Label = label,
                    FileSystem = drive.DriveFormat,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    ClusterBytes = cluster,
                    IsSystem = letter == systemLetter,
                    IsRemovable = drive.DriveType == DriveType.Removable || disk?.Removable == true,
                    DiskNumber = diskNumber,
                    DiskOffset = offset,
                    Disk = disk
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DriveInventory] {drive.Name}: {ex.Message}");
            }
        }

        return result.OrderBy(v => v.Letter).ToList();
    }

    /// <summary>The disk a volume lives on, or (null, 0) when it spans several disks or can't be read.</summary>
    public static unsafe (int? DiskNumber, long Offset) GetVolumeExtent(char letter)
    {
        using var handle = StorageNative.OpenDeviceForQuery($@"\\.\{letter}:");
        if (handle.IsInvalid) return (null, 0);

        // VOLUME_DISK_EXTENTS { DWORD Count; (pad); DISK_EXTENT { DWORD Disk; (pad); INT64 Offset; INT64 Length; }[] }
        var buffer = stackalloc byte[8 + 24 * 8];
        if (!StorageNative.DeviceIoControl(handle, StorageNative.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                null, 0, buffer, 8 + 24 * 8, out int returned, IntPtr.Zero) || returned < 32)
            return (null, 0);

        int count = *(int*)buffer;
        if (count != 1) return (null, 0);
        int disk = *(int*)(buffer + 8);
        long offset = *(long*)(buffer + 16);
        return (disk, offset);
    }

    public static PhysicalDisk? GetDisk(int number) => GetDisk(number, ReadWmiDisks());

    private static unsafe PhysicalDisk? GetDisk(int number, Dictionary<int, WmiDisk> wmi)
    {
        using var handle = StorageNative.OpenDeviceForQuery($@"\\.\PhysicalDrive{number}");
        wmi.TryGetValue(number, out var w);
        if (handle.IsInvalid && w == null) return null;

        string vendor = "", product = "", revision = "";
        bool removable = false;
        DriveBus bus = DriveBus.Unknown;
        bool? seekPenalty = null, trim = null;
        long size = 0;

        if (!handle.IsInvalid)
        {
            // STORAGE_DEVICE_DESCRIPTOR: RemovableMedia @10, VendorIdOffset @12, ProductIdOffset @16,
            // ProductRevisionOffset @20, BusType @28.
            var device = StorageNative.QueryStorageProperty(handle, StorageNative.StorageDeviceProperty);
            if (device != null && device.Length >= 32)
            {
                removable = device[10] != 0;
                vendor = StorageNative.AsciiAt(device, BitConverter.ToInt32(device, 12));
                product = StorageNative.AsciiAt(device, BitConverter.ToInt32(device, 16));
                revision = StorageNative.AsciiAt(device, BitConverter.ToInt32(device, 20));
                bus = MapBus(BitConverter.ToInt32(device, 28));
            }

            // DEVICE_SEEK_PENALTY_DESCRIPTOR / DEVICE_TRIM_DESCRIPTOR: BOOLEAN @8.
            var seek = StorageNative.QueryStorageProperty(handle, StorageNative.StorageDeviceSeekPenaltyProperty, 16);
            if (seek is { Length: >= 9 }) seekPenalty = seek[8] != 0;
            var trimData = StorageNative.QueryStorageProperty(handle, StorageNative.StorageDeviceTrimProperty, 16);
            if (trimData is { Length: >= 9 }) trim = trimData[8] != 0;

            // DISK_GEOMETRY_EX: DISK_GEOMETRY (24 bytes) then DiskSize.
            var geometry = stackalloc byte[256];
            if (StorageNative.DeviceIoControl(handle, StorageNative.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                    null, 0, geometry, 256, out int got, IntPtr.Zero) && got >= 32)
                size = *(long*)(geometry + 24);
        }

        if (bus == DriveBus.Unknown && w != null) bus = MapBus(w.BusType);
        if (size <= 0 && w != null) size = w.Size;

        // The drive's own seek-penalty answer is the most reliable; Windows'
        // media type is next (it is also what "Optimize Drives" shows).
        DriveMedia media = DriveMedia.Unknown;
        bool reported = false;
        if (seekPenalty != null)
        {
            media = seekPenalty.Value ? DriveMedia.Hdd : DriveMedia.Ssd;
            reported = true;
        }
        else if (w != null)
        {
            media = w.MediaType switch
            {
                3 => DriveMedia.Hdd,
                4 or 5 => DriveMedia.Ssd,
                _ => DriveMedia.Unknown
            };
        }
        if (media == DriveMedia.Unknown && bus == DriveBus.Nvme) media = DriveMedia.Ssd;

        int? rpm = null;
        if (w != null && w.SpindleSpeed is > 0 and < 100_000) rpm = (int)w.SpindleSpeed;

        // ATA models come split into an 8-char vendor and 16-char product by the
        // SCSI translation ("WDC" + "WD10EZEX-08WN4A0"); Windows' own model string is whole.
        string model = FirstNonEmpty(w?.Model, w?.FriendlyName, (vendor + " " + product).Trim());

        MediaSource? probedBy = null;
        string? inner = null;
        if (media == DriveMedia.Unknown && size > 0 && bus is not (DriveBus.Virtual or DriveBus.StorageSpaces))
        {
            var probe = MediaProbe.Resolve(number, model, size, bus, removable);
            if (probe != null)
            {
                inner = probe.InnerModel;
                if (probe.Media != DriveMedia.Unknown)
                {
                    media = probe.Media;
                    probedBy = probe.Source;
                    reported = probe.Source is MediaSource.Drive or MediaSource.Bridge;
                    if (probe.Rpm is > 0) rpm = probe.Rpm;
                }
            }
        }
        if (media == DriveMedia.Unknown && bus is DriveBus.Usb or DriveBus.Unknown or DriveBus.Other)
        {
            // Last resort: the maker's product line ("One Touch HDD" is a hard disk in a case).
            media = DriveReference.MediaFromModel(model, inner, size);
            if (media != DriveMedia.Unknown) probedBy = MediaSource.ModelTable;
        }
        if (media == DriveMedia.Ssd) rpm = 0;

        return new PhysicalDisk
        {
            Number = number,
            Model = model,
            Firmware = FirstNonEmpty(w?.Firmware, revision),
            SizeBytes = size,
            Media = media,
            MediaReportedByDrive = reported,
            MediaProbedBy = probedBy,
            InnerModel = inner,
            Bus = bus,
            Rpm = rpm,
            TrimEnabled = trim,
            Health = w?.Health ?? DiskHealth.Unknown,
            Removable = removable || bus is DriveBus.Usb or DriveBus.SdCard
        };
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    /// <summary>STORAGE_BUS_TYPE (ntddstor.h) — MSFT_PhysicalDisk.BusType uses the same numbers.</summary>
    private static DriveBus MapBus(int busType) => busType switch
    {
        0x1 => DriveBus.Scsi,
        0x2 or 0x3 or 0xB => DriveBus.Sata, // ATAPI, ATA, SATA
        0x7 => DriveBus.Usb,
        0x8 => DriveBus.Raid,
        0xA => DriveBus.Sas,
        0xC or 0xD => DriveBus.SdCard,      // SD, MMC
        0xE or 0xF => DriveBus.Virtual,     // virtual, file-backed virtual (VHD)
        0x10 => DriveBus.StorageSpaces,
        0x11 => DriveBus.Nvme,
        0 => DriveBus.Unknown,
        _ => DriveBus.Other
    };

    #region Windows storage WMI (model, media type, spindle speed, health)

    private sealed class WmiDisk
    {
        public string Model = "";
        public string FriendlyName = "";
        public string Firmware = "";
        public int MediaType;
        public int BusType;
        public long SpindleSpeed;
        public long Size;
        public DiskHealth Health;
    }

    private static Dictionary<int, WmiDisk> ReadWmiDisks()
    {
        var result = new Dictionary<int, WmiDisk>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            var query = new ObjectQuery(
                "SELECT DeviceId, FriendlyName, Model, MediaType, BusType, SpindleSpeed, HealthStatus, Size, FirmwareVersion FROM MSFT_PhysicalDisk");
            using var searcher = new ManagementObjectSearcher(scope, query,
                new global::System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = true });
            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                {
                    if (!int.TryParse(disk["DeviceId"]?.ToString(), out int number)) continue;
                    result[number] = new WmiDisk
                    {
                        Model = disk["Model"]?.ToString()?.Trim() ?? "",
                        FriendlyName = disk["FriendlyName"]?.ToString()?.Trim() ?? "",
                        Firmware = disk["FirmwareVersion"]?.ToString()?.Trim() ?? "",
                        MediaType = ToInt(disk["MediaType"]),
                        BusType = ToInt(disk["BusType"]),
                        SpindleSpeed = ToLong(disk["SpindleSpeed"]),
                        Size = ToLong(disk["Size"]),
                        Health = ToInt(disk["HealthStatus"]) switch
                        {
                            0 => DiskHealth.Healthy,
                            1 => DiskHealth.Warning,
                            2 => DiskHealth.Unhealthy,
                            _ => DiskHealth.Unknown
                        }
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DriveInventory] Storage WMI unavailable: {ex.Message}");
        }
        return result;
    }

    private static int ToInt(object? value)
    {
        try { return value == null ? -1 : Convert.ToInt32(value); } catch { return -1; }
    }

    private static long ToLong(object? value)
    {
        try { return value == null ? -1 : Convert.ToInt64(value); } catch { return -1; }
    }

    #endregion
}

/// <summary>
/// Cumulative read/write counters of a physical disk (IOCTL_DISK_PERFORMANCE),
/// used to show live disk throughput. Works without administrator rights.
/// </summary>
public sealed class DiskIoCounter : IDisposable
{
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle;
    private long _lastRead, _lastWritten;
    private long _lastTicks;

    public DiskIoCounter(int diskNumber)
    {
        _handle = StorageNative.OpenDeviceForQuery($@"\\.\PhysicalDrive{diskNumber}");
        Sample(out _, out _);
    }

    public bool IsAvailable => !_handle.IsInvalid && _lastTicks != 0;

    /// <summary>Bytes per second read and written since the previous call.</summary>
    public unsafe bool Sample(out double readBytesPerSec, out double writeBytesPerSec)
    {
        readBytesPerSec = writeBytesPerSec = 0;
        if (_handle.IsInvalid) return false;

        // DISK_PERFORMANCE: BytesRead @0, BytesWritten @8 (88 bytes total).
        var buffer = stackalloc byte[128];
        if (!StorageNative.DeviceIoControl(_handle, StorageNative.IOCTL_DISK_PERFORMANCE, null, 0, buffer, 128,
                out int returned, IntPtr.Zero) || returned < 16)
            return false;

        long read = *(long*)buffer, written = *(long*)(buffer + 8);
        long now = Stopwatch.GetTimestamp();
        bool hasPrevious = _lastTicks != 0;
        if (hasPrevious)
        {
            double seconds = (now - _lastTicks) / (double)Stopwatch.Frequency;
            if (seconds > 0)
            {
                readBytesPerSec = Math.Max(0, read - _lastRead) / seconds;
                writeBytesPerSec = Math.Max(0, written - _lastWritten) / seconds;
            }
        }
        _lastRead = read;
        _lastWritten = written;
        _lastTicks = now;
        return hasPrevious;
    }

    public void Dispose() => _handle.Dispose();
}

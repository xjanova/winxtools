using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NetX.Core.System.Storage;

/// <summary>Win32 declarations shared by the drive, defrag and benchmark code.</summary>
internal static unsafe class StorageNative
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint FILE_SHARE_DELETE = 0x4;
    public const uint OPEN_EXISTING = 3;
    public const uint CREATE_ALWAYS = 2;
    public const uint FILE_ATTRIBUTE_HIDDEN = 0x2;
    public const uint FILE_ATTRIBUTE_TEMPORARY = 0x100;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_DISK_PERFORMANCE = 0x00070020;
    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    public const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
    public const uint FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073;
    public const uint FSCTL_MOVE_FILE = 0x00090074;
    public const uint FSCTL_IS_VOLUME_DIRTY = 0x00090078;
    public const uint FSCTL_QUERY_FILE_LAYOUT = 0x00090277;
    public const uint IOCTL_SCSI_PASS_THROUGH = 0x0004D004;

    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    // STORAGE_PROPERTY_ID
    public const int StorageDeviceProperty = 0;
    public const int StorageDeviceWriteCacheProperty = 4;
    public const int StorageDeviceSeekPenaltyProperty = 7;
    public const int StorageDeviceTrimProperty = 8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        void* lpInBuffer, int nInBufferSize, void* lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceW(string lpRootPathName, out uint lpSectorsPerCluster,
        out uint lpBytesPerSector, out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(SafeFileHandle hFile, byte* lpBuffer, int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteFile(SafeFileHandle hFile, byte* lpBuffer, int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetEndOfFile(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlushFileBuffers(SafeFileHandle hFile);

    /// <summary>Opens a device (\\.\C:, \\.\PhysicalDrive0) without asking for any data access.</summary>
    public static SafeFileHandle OpenDeviceForQuery(string path) =>
        CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>Opens a volume for reading its metadata (volume bitmap, file layout). Needs administrator rights.</summary>
    public static SafeFileHandle OpenVolumeForRead(char letter) =>
        CreateFileW($@"\\.\{letter}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>
    /// Runs IOCTL_STORAGE_QUERY_PROPERTY (standard query) and returns the
    /// descriptor bytes, or null when the device doesn't support the property.
    /// </summary>
    public static byte[]? QueryStorageProperty(SafeFileHandle device, int propertyId, int bufferSize = 1024)
    {
        // STORAGE_PROPERTY_QUERY { PropertyId; QueryType (0 = standard); AdditionalParameters[1] }
        var query = stackalloc int[3];
        query[0] = propertyId;
        query[1] = 0;
        query[2] = 0;

        var buffer = new byte[bufferSize];
        fixed (byte* p = buffer)
        {
            if (!DeviceIoControl(device, IOCTL_STORAGE_QUERY_PROPERTY, query, 12, p, buffer.Length, out int returned, IntPtr.Zero))
                return null;
            if (returned < 8) return null;
            Array.Resize(ref buffer, returned);
            return buffer;
        }
    }

    /// <summary>Reads a NUL-terminated ASCII string at <paramref name="offset"/> of a descriptor.</summary>
    public static string AsciiAt(byte[] data, int offset)
    {
        if (offset <= 0 || offset >= data.Length) return "";
        int end = offset;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, offset, end - offset).Trim();
    }
}

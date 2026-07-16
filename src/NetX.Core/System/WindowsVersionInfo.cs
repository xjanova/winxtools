using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NetX.Core.System;

/// <summary>
/// Authoritative Windows version / edition / hardware detection.
///
/// Uses ntdll!RtlGetVersion for the real build number (Environment.OSVersion is
/// gated by the app manifest and lies on un-manifested processes), plus the
/// CurrentVersion registry hive for the marketing DisplayVersion (e.g. "23H2"),
/// edition and UBR. Everything is computed once and cached.
///
/// The point of this class is to let the optimizer/gamer-mode code adapt to the
/// exact OS it is running on instead of blindly applying tweaks that only exist
/// on some builds (Recall/Copilot = Win11 24H2+, HAGS = Win10 2004+, etc.).
/// </summary>
public sealed class WindowsVersionInfo
{
    private static WindowsVersionInfo? _current;
    public static WindowsVersionInfo Current => _current ??= Detect();

    // ---- Core version numbers ----
    public int Major { get; private init; }
    public int Minor { get; private init; }

    /// <summary>OS build number (e.g. 19045, 22631, 26100).</summary>
    public int Build { get; private init; }

    /// <summary>Update Build Revision (the number after the dot, e.g. 3803).</summary>
    public int Ubr { get; private init; }

    /// <summary>Marketing product name, e.g. "Windows 11 Pro".</summary>
    public string ProductName { get; private init; } = "Windows";

    /// <summary>Feature-update label, e.g. "23H2", "24H2", "22H2".</summary>
    public string DisplayVersion { get; private init; } = "";

    /// <summary>Edition id, e.g. "Professional", "Core", "Enterprise".</summary>
    public string EditionId { get; private init; } = "";

    public bool Is64Bit { get; private init; } = Environment.Is64BitOperatingSystem;

    // ---- High-level classification ----
    public bool IsServer { get; private init; }
    public bool IsWindows10 => Major == 10 && Build < 22000;
    public bool IsWindows11 => Major == 10 && Build >= 22000;

    /// <summary>Human string like "Windows 11 Pro 24H2 (build 26100.2314)".</summary>
    public string FriendlyName
    {
        get
        {
            var name = ProductName;
            if (!string.IsNullOrEmpty(DisplayVersion)) name += $" {DisplayVersion}";
            var buildText = Ubr > 0 ? $"{Build}.{Ubr}" : Build.ToString();
            return $"{name} (build {buildText})";
        }
    }

    // ---- Capability flags (drive version-aware tweaks) ----

    /// <summary>Windows Recall exists only on Win11 24H2+ (build 26100+).</summary>
    public bool SupportsRecall => IsWindows11 && Build >= 26100;

    /// <summary>Copilot shipped on Win11 23H2 (22631) and later.</summary>
    public bool SupportsCopilot => IsWindows11 && Build >= 22621;

    /// <summary>Taskbar Widgets/Chat live on Win11 only.</summary>
    public bool SupportsWidgets => IsWindows11;

    /// <summary>
    /// Hardware-Accelerated GPU Scheduling: Win10 2004 (19041) and up. Still
    /// GPU-driver dependent — this only says the OS exposes the toggle.
    /// </summary>
    public bool SupportsHags => Build >= 19041;

    /// <summary>Windows 11 gained per-app "Optimizations for windowed games" (Auto-HDR pipeline) at 22H2.</summary>
    public bool SupportsWindowedGameOptimizations => IsWindows11 && Build >= 22621;

    // ---- Hardware ----

    private long _totalRamMb = -1;
    /// <summary>Total physical RAM in MB.</summary>
    public long TotalRamMb
    {
        get
        {
            if (_totalRamMb < 0)
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                _totalRamMb = GlobalMemoryStatusEx(ref status) ? (long)(status.ullTotalPhys / (1024 * 1024)) : 0;
            }
            return _totalRamMb;
        }
    }

    public int ProcessorCount => Environment.ProcessorCount;

    private bool? _systemDriveIsSsd;
    /// <summary>
    /// True if the OS drive is an SSD, false if HDD, null if unknown. Lazily
    /// resolved via the Storage WMI provider (can be slow, so only on demand).
    /// Superfetch/Prefetch advice depends on this.
    /// </summary>
    public bool? SystemDriveIsSsd => _systemDriveIsSsd ??= DetectSystemDriveIsSsd();

    #region Detection

    private static WindowsVersionInfo Detect()
    {
        var osv = new OSVERSIONINFOEX { dwOSVersionInfoSize = (uint)Marshal.SizeOf<OSVERSIONINFOEX>() };
        int major = Environment.OSVersion.Version.Major;
        int minor = Environment.OSVersion.Version.Minor;
        int build = Environment.OSVersion.Version.Build;
        bool isServer = false;

        try
        {
            if (RtlGetVersion(ref osv) == 0) // STATUS_SUCCESS
            {
                major = (int)osv.dwMajorVersion;
                minor = (int)osv.dwMinorVersion;
                build = (int)osv.dwBuildNumber;
                // wProductType: 1 = Workstation, 2/3 = Server
                isServer = osv.wProductType != 1;
            }
        }
        catch { /* fall back to Environment.OSVersion values above */ }

        string productName = "Windows";
        string displayVersion = "";
        string editionId = "";
        int ubr = 0;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                productName = key.GetValue("ProductName") as string ?? productName;
                // "DisplayVersion" exists on 20H2+; older builds use "ReleaseId"
                displayVersion = key.GetValue("DisplayVersion") as string
                                 ?? key.GetValue("ReleaseId") as string ?? "";
                editionId = key.GetValue("EditionID") as string ?? "";
                ubr = key.GetValue("UBR") is int u ? u : 0;

                // Registry ProductName still says "Windows 10" on Win11 — correct it.
                if (build >= 22000 && productName.Contains("Windows 10"))
                    productName = productName.Replace("Windows 10", "Windows 11");
            }
        }
        catch { }

        return new WindowsVersionInfo
        {
            Major = major,
            Minor = minor,
            Build = build,
            Ubr = ubr,
            ProductName = productName,
            DisplayVersion = displayVersion,
            EditionId = editionId,
            IsServer = isServer,
        };
    }

    private static bool? DetectSystemDriveIsSsd()
    {
        try
        {
            var sysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                ?.TrimEnd('\\'); // e.g. "C:"
            if (string.IsNullOrEmpty(sysDrive)) return null;

            // Map the volume -> physical disk(s) via the Storage WMI provider,
            // then read MediaType (3 = HDD, 4 = SSD, 5 = SCM).
            var scope = new ManagementScope(@"\\.\root\microsoft\windows\storage");
            scope.Connect();

            // Partition on this drive letter
            using var partSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter='{sysDrive[0]}'"));
            foreach (ManagementObject part in partSearcher.Get())
            {
                var diskNumber = Convert.ToString(part["DiskNumber"]);
                using var diskSearcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId='{diskNumber}'"));
                foreach (ManagementObject disk in diskSearcher.Get())
                {
                    var mediaType = Convert.ToInt32(disk["MediaType"]);
                    return mediaType switch
                    {
                        4 => true,   // SSD
                        5 => true,   // SCM (storage-class memory / NVMe-persistent)
                        3 => false,  // HDD
                        _ => (bool?)null
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SSD detection failed: {ex.Message}");
        }
        return null;
    }

    #endregion

    #region Native

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OSVERSIONINFOEX
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OSVERSIONINFOEX versionInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    #endregion
}

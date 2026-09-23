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
/// on some builds (Recall = Copilot+ PCs on Win11 24H2+, Copilot = Win11 22H2
/// Moment 4+, HAGS = Win10 2004+, gpedit = Pro and up, never Windows Server).
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

    /// <summary>
    /// Windows Server shares build numbers with the client (Server 2025 = 26100),
    /// so it must never be treated as Windows 10/11.
    /// </summary>
    public bool IsServer { get; private init; }
    public bool IsWindows10 => Major == 10 && Build < 22000 && !IsServer;
    public bool IsWindows11 => Major == 10 && Build >= 22000 && !IsServer;

    /// <summary>"Windows 11" / "Windows 10" (the name Windows Update policies expect), else ProductName.</summary>
    public string FamilyName => IsWindows11 ? "Windows 11" : IsWindows10 ? "Windows 10" : ProductName;

    /// <summary>Home editions: EditionID "Core", "CoreN", "CoreSingleLanguage", "CoreCountrySpecific".</summary>
    public bool IsHomeEdition => EditionId.StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pro/Enterprise/Education — editions that honor Windows Update and most local policies.</summary>
    public bool IsProOrHigher => EditionId.Length > 0 && !IsHomeEdition;

    /// <summary>gpedit.msc ships only with Pro and higher.</summary>
    public bool HasGroupPolicyEditor =>
        !IsHomeEdition && File.Exists(Path.Combine(Environment.SystemDirectory, "gpedit.msc"));

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

    /// <summary>
    /// Windows 11 22H2 "Moment 4" (22621.2361, Sept 2023) and 23H2 (22631+):
    /// Copilot in Windows, clock seconds on the taskbar, etc.
    /// </summary>
    public bool IsWindows11Moment4OrLater =>
        IsWindows11 && (Build > 22621 || (Build == 22621 && Ubr >= 2361));

    /// <summary>
    /// Windows Recall: needs Windows 11 24H2+ (26100) AND a Copilot+ PC. Only
    /// those PCs carry the Recall optional feature with its payload; everyone
    /// else has it "disabled with payload removed".
    /// </summary>
    public bool SupportsRecall => IsWindows11 && Build >= 26100 && IsRecallFeaturePresent;

    /// <summary>Copilot in Windows arrived with 22H2 Moment 4 (22621.2361) and 23H2 (22631).</summary>
    public bool SupportsCopilot => IsWindows11Moment4OrLater;

    /// <summary>Taskbar Widgets/Chat live on Win11 only.</summary>
    public bool SupportsWidgets => IsWindows11;

    /// <summary>"End task" in the taskbar right-click menu: Windows 11 23H2 (22631) and later.</summary>
    public bool SupportsTaskbarEndTask => IsWindows11 && Build >= 22631;

    /// <summary>Seconds in the taskbar clock: Windows 10, and Windows 11 from Moment 4 on.</summary>
    public bool SupportsClockSeconds => IsWindows10 || IsWindows11Moment4OrLater;

    private bool? _recallFeature;

    /// <summary>
    /// Reads the Recall optional-feature record kept by component servicing
    /// (a registry read — fast, no DISM/PowerShell). Cached.
    /// </summary>
    public bool IsRecallFeaturePresent => _recallFeature ??= DetectRecallFeature();

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

    private readonly object _ssdLock = new();
    private bool _ssdResolved;
    private bool? _systemDriveIsSsd;
    /// <summary>
    /// True if the OS drive is an SSD, false if HDD, null if unknown. Lazily
    /// resolved via the Storage WMI provider (can be slow, so only on demand
    /// and never on the UI thread; the query has a hard timeout).
    /// Superfetch/Prefetch advice depends on this.
    /// </summary>
    public bool? SystemDriveIsSsd
    {
        get
        {
            lock (_ssdLock)
            {
                if (!_ssdResolved)
                {
                    _systemDriveIsSsd = DetectSystemDriveIsSsd();
                    _ssdResolved = true;
                }
                return _systemDriveIsSsd;
            }
        }
    }

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
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                productName = key.GetValue("ProductName") as string ?? productName;
                // "DisplayVersion" exists on 20H2+; older builds use "ReleaseId"
                displayVersion = key.GetValue("DisplayVersion") as string
                                 ?? key.GetValue("ReleaseId") as string ?? "";
                editionId = key.GetValue("EditionID") as string ?? "";
                ubr = key.GetValue("UBR") is int u ? u : 0;

                // "Server" / "Server Core" — belt and braces next to wProductType.
                if ((key.GetValue("InstallationType") as string ?? "").StartsWith("Server", StringComparison.OrdinalIgnoreCase))
                    isServer = true;

                // Registry ProductName still says "Windows 10" on Win11 — correct it.
                if (build >= 22000 && !isServer && productName.Contains("Windows 10"))
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

    private static bool DetectRecallFeature()
    {
        try
        {
            // Component servicing records every optional feature here. PCs that
            // can't run Recall have it "disabled with payload removed" (Removed=1).
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Notifications\OptionalFeatures\Recall");
            if (key == null) return false;
            int selection = key.GetValue("Selection") is int s ? s : 0;
            int removed = key.GetValue("Removed") is int r ? r : 0;
            return selection == 1 || removed == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Recall feature detection failed: {ex.Message}");
            return false;
        }
    }

    private static bool? DetectSystemDriveIsSsd()
    {
        try
        {
            var sysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                ?.TrimEnd('\\'); // e.g. "C:"
            if (string.IsNullOrEmpty(sysDrive) || !char.IsAsciiLetter(sysDrive[0])) return null;

            // Map the volume -> physical disk(s) via the Storage WMI provider,
            // then read MediaType (3 = HDD, 4 = SSD, 5 = SCM). Every WMI call has
            // a timeout: a stuck storage provider must not hang the caller.
            var timeout = TimeSpan.FromSeconds(10);
            var scope = new ManagementScope(@"\\.\root\microsoft\windows\storage",
                new ConnectionOptions { Timeout = timeout });
            scope.Connect();
            var enumOptions = new global::System.Management.EnumerationOptions
            {
                Timeout = timeout,
                ReturnImmediately = true,
                Rewindable = false
            };

            // Partition on this drive letter
            using var partSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter='{sysDrive[0]}'"), enumOptions);
            foreach (ManagementObject part in partSearcher.Get())
            {
                var diskNumber = Convert.ToString(part["DiskNumber"]);
                if (!int.TryParse(diskNumber, out _)) continue;
                using var diskSearcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId='{diskNumber}'"), enumOptions);
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

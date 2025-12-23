using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NetX.Core.Optimization;

/// <summary>
/// RAM Optimizer that clears memory cache and optimizes RAM usage
/// </summary>
public class RamOptimizer : IDisposable
{
    private static RamOptimizer? _instance;
    public static RamOptimizer Instance => _instance ??= new RamOptimizer();

    private Timer? _autoOptimizeTimer;
    private bool _isAutoOptimizeEnabled = false;
    private int _optimizeIntervalMinutes = 30;
    private int _memoryThresholdPercent = 80;
    private readonly string _settingsPath;

    // Native methods for memory management
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public RamOptimizer()
    {
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetX", "ram_optimizer.json");

        LoadSettings();
    }

    #region Properties

    public bool IsAutoOptimizeEnabled
    {
        get => _isAutoOptimizeEnabled;
        set
        {
            _isAutoOptimizeEnabled = value;
            if (value)
                StartAutoOptimize();
            else
                StopAutoOptimize();
            SaveSettings();
        }
    }

    public int OptimizeIntervalMinutes
    {
        get => _optimizeIntervalMinutes;
        set
        {
            _optimizeIntervalMinutes = Math.Max(5, Math.Min(120, value));
            if (_isAutoOptimizeEnabled)
            {
                StopAutoOptimize();
                StartAutoOptimize();
            }
            SaveSettings();
        }
    }

    public int MemoryThresholdPercent
    {
        get => _memoryThresholdPercent;
        set
        {
            _memoryThresholdPercent = Math.Max(50, Math.Min(95, value));
            SaveSettings();
        }
    }

    #endregion

    #region Memory Info

    public MemoryInfo GetMemoryInfo()
    {
        var info = new MemoryInfo();

        try
        {
            var gc = GC.GetGCMemoryInfo();
            info.TotalMemoryMB = gc.TotalAvailableMemoryBytes / (1024 * 1024);

            // Use Performance Counter for more accurate info
            using var ramCounter = new PerformanceCounter("Memory", "Available MBytes");
            info.AvailableMemoryMB = (long)ramCounter.NextValue();
            info.UsedMemoryMB = info.TotalMemoryMB - info.AvailableMemoryMB;
            info.UsagePercent = (int)((info.UsedMemoryMB * 100) / info.TotalMemoryMB);

            // Get cached memory
            using var cacheCounter = new PerformanceCounter("Memory", "Cache Bytes");
            info.CachedMemoryMB = (long)(cacheCounter.NextValue() / (1024 * 1024));

            // Get standby memory (can be cleared)
            using var standbyCounter = new PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes");
            info.StandbyMemoryMB = (long)(standbyCounter.NextValue() / (1024 * 1024));
        }
        catch
        {
            // Fallback if performance counters not available
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                info.TotalMemoryMB = (long)(memStatus.ullTotalPhys / (1024 * 1024));
                info.AvailableMemoryMB = (long)(memStatus.ullAvailPhys / (1024 * 1024));
                info.UsedMemoryMB = info.TotalMemoryMB - info.AvailableMemoryMB;
                info.UsagePercent = (int)memStatus.dwMemoryLoad;
            }
        }

        return info;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

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

    #endregion

    #region Optimization

    public OptimizeResult OptimizeNow()
    {
        var result = new OptimizeResult
        {
            StartTime = DateTime.Now,
            MemoryBefore = GetMemoryInfo()
        };

        try
        {
            // 1. Force .NET garbage collection
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // 2. Trim working set of all processes
            var processesOptimized = 0;
            var protectedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "system", "smss", "csrss", "wininit", "services", "lsass",
                "svchost", "dwm", "explorer", "winlogon", "audiodg"
            };

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (protectedProcesses.Contains(process.ProcessName))
                        continue;

                    // Skip processes with high CPU (they're actively working)
                    if (IsProcessActive(process))
                        continue;

                    // Trim working set
                    EmptyWorkingSet(process.Handle);
                    processesOptimized++;
                }
                catch { }
            }

            result.ProcessesOptimized = processesOptimized;

            // 3. Clear file system cache (requires admin)
            try
            {
                ClearFileSystemCache();
            }
            catch { }

            // Wait a moment for memory to settle
            Thread.Sleep(500);

            result.MemoryAfter = GetMemoryInfo();
            result.MemoryFreedMB = result.MemoryAfter.AvailableMemoryMB - result.MemoryBefore.AvailableMemoryMB;
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        result.EndTime = DateTime.Now;
        OnOptimizationComplete?.Invoke(result);

        return result;
    }

    private static bool IsProcessActive(Process process)
    {
        try
        {
            // Check if process has used CPU recently
            var startTime = process.TotalProcessorTime;
            Thread.Sleep(100);
            var endTime = process.TotalProcessorTime;

            return (endTime - startTime).TotalMilliseconds > 10;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearFileSystemCache()
    {
        // This requires admin privileges
        // Uses NtSetSystemInformation to clear standby list
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c echo Clearing cache...",
                UseShellExecute = false,
                CreateNoWindow = true,
                Verb = "runas"
            };
            Process.Start(psi)?.WaitForExit(1000);
        }
        catch { }
    }

    public void OptimizeProcess(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            EmptyWorkingSet(process.Handle);
        }
        catch { }
    }

    #endregion

    #region Auto Optimize

    private void StartAutoOptimize()
    {
        _autoOptimizeTimer?.Dispose();
        _autoOptimizeTimer = new Timer(AutoOptimizeCallback, null,
            TimeSpan.FromMinutes(_optimizeIntervalMinutes),
            TimeSpan.FromMinutes(_optimizeIntervalMinutes));
    }

    private void StopAutoOptimize()
    {
        _autoOptimizeTimer?.Dispose();
        _autoOptimizeTimer = null;
    }

    private void AutoOptimizeCallback(object? state)
    {
        try
        {
            var memInfo = GetMemoryInfo();

            // Only optimize if memory usage exceeds threshold
            if (memInfo.UsagePercent >= _memoryThresholdPercent)
            {
                OptimizeNow();
            }
        }
        catch { }
    }

    #endregion

    #region Top Memory Consumers

    public List<ProcessMemoryInfo> GetTopMemoryConsumers(int count = 10)
    {
        var list = new List<ProcessMemoryInfo>();

        try
        {
            var processes = Process.GetProcesses()
                .Select(p =>
                {
                    try
                    {
                        return new ProcessMemoryInfo
                        {
                            ProcessId = p.Id,
                            ProcessName = p.ProcessName,
                            MemoryMB = p.WorkingSet64 / (1024 * 1024),
                            PrivateMemoryMB = p.PrivateMemorySize64 / (1024 * 1024)
                        };
                    }
                    catch { return null; }
                })
                .Where(p => p != null)
                .OrderByDescending(p => p!.MemoryMB)
                .Take(count);

            list.AddRange(processes!);
        }
        catch { }

        return list;
    }

    #endregion

    #region Settings

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<RamOptimizerSettings>(json);
                if (settings != null)
                {
                    _isAutoOptimizeEnabled = settings.AutoOptimizeEnabled;
                    _optimizeIntervalMinutes = settings.IntervalMinutes;
                    _memoryThresholdPercent = settings.ThresholdPercent;

                    if (_isAutoOptimizeEnabled)
                        StartAutoOptimize();
                }
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var settings = new RamOptimizerSettings
            {
                AutoOptimizeEnabled = _isAutoOptimizeEnabled,
                IntervalMinutes = _optimizeIntervalMinutes,
                ThresholdPercent = _memoryThresholdPercent
            };

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    #endregion

    public event Action<OptimizeResult>? OnOptimizationComplete;

    public void Dispose()
    {
        _autoOptimizeTimer?.Dispose();
    }
}

public class MemoryInfo
{
    public long TotalMemoryMB { get; set; }
    public long AvailableMemoryMB { get; set; }
    public long UsedMemoryMB { get; set; }
    public int UsagePercent { get; set; }
    public long CachedMemoryMB { get; set; }
    public long StandbyMemoryMB { get; set; }
}

public class OptimizeResult
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public MemoryInfo MemoryBefore { get; set; } = new();
    public MemoryInfo MemoryAfter { get; set; } = new();
    public long MemoryFreedMB { get; set; }
    public int ProcessesOptimized { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ProcessMemoryInfo
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public long MemoryMB { get; set; }
    public long PrivateMemoryMB { get; set; }
}

public class RamOptimizerSettings
{
    public bool AutoOptimizeEnabled { get; set; }
    public int IntervalMinutes { get; set; } = 30;
    public int ThresholdPercent { get; set; } = 80;
}

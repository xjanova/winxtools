using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NetX.Core.Optimization;

/// <summary>One program with all of its processes (a browser's 30 processes are one row).</summary>
public sealed class AppMemoryInfo
{
    public string Name { get; init; } = "";
    public string DisplayName { get; set; } = "";
    public long MemoryMB { get; init; }
    public long PrivateMemoryMB { get; init; }
    public IReadOnlyList<ProcessMemoryInfo> Processes { get; init; } = Array.Empty<ProcessMemoryInfo>();

    public bool CanTrim => Processes.Any(p => p.CanTrim);

    /// <summary>False when any of its processes is Windows itself or WinXTools.</summary>
    public bool CanEnd => Processes.Count > 0 && Processes.All(p => p.CanEnd);
}

/// <summary>What is using the RAM, per program, and closing a program politely.</summary>
public static class AppMemory
{
    private const long MB = 1024 * 1024;
    private static readonly ConcurrentDictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Programs using the most RAM (private working set, summed over their processes).</summary>
    public static List<AppMemoryInfo> GetTopApps(int count)
    {
        var snapshot = MemoryNative.SnapshotProcesses();
        if (snapshot == null) return new List<AppMemoryInfo>();

        int ownPid = Environment.ProcessId;
        var apps = snapshot
            .Where(p => p.Pid > 0)
            .GroupBy(p => string.IsNullOrEmpty(p.Name) ? $"PID {p.Pid}" : p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Name = g.Key,
                Bytes = g.Sum(p => p.PrivateWorkingSetBytes),
                Private = g.Sum(p => p.PrivateBytes),
                Entries = g.OrderByDescending(p => p.PrivateWorkingSetBytes).ToList()
            })
            .OrderByDescending(a => a.Bytes)
            .Take(Math.Clamp(count, 1, 100))
            .Select(a => new AppMemoryInfo
            {
                Name = a.Name,
                DisplayName = a.Name,
                MemoryMB = a.Bytes / MB,
                PrivateMemoryMB = a.Private / MB,
                Processes = a.Entries.Select(p => new ProcessMemoryInfo
                {
                    ProcessId = p.Pid,
                    ProcessName = string.IsNullOrEmpty(p.Name) ? $"PID {p.Pid}" : p.Name,
                    MemoryMB = p.PrivateWorkingSetBytes / MB,
                    PrivateMemoryMB = p.PrivateBytes / MB,
                    WorkingSetMB = p.WorkingSetBytes / MB,
                    CreateTime = p.CreateTime,
                    SessionId = p.SessionId,
                    IsSelf = p.Pid == ownPid,
                    CanTrim = p.Pid != ownPid && p.Pid > 4 && !MemoryCleaner.IsNeverTrim(p.Name),
                    CanEnd = p.Pid != ownPid && p.Pid > 4 && p.Name.Length > 0 && !ProcessKiller.IsProtectedProcess(p.Name)
                }).ToList()
            })
            .ToList();

        foreach (var app in apps)
            app.DisplayName = FriendlyName(app) ?? app.Name;
        return apps;
    }

    /// <summary>The program's own description ("Google Chrome"), read from its .exe.</summary>
    private static string? FriendlyName(AppMemoryInfo app)
    {
        if (FriendlyNames.TryGetValue(app.Name, out var cached)) return cached.Length == 0 ? null : cached;
        string name = "";
        foreach (var p in app.Processes.Take(3))
        {
            var path = ImagePath(p.ProcessId);
            if (path == null) continue;
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description) && description.Length <= 60 &&
                    !description.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
                    name = description;
            }
            catch { }
            break;
        }
        FriendlyNames[app.Name] = name;
        return name.Length == 0 ? null : name;
    }

    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Asks every window of these processes to close, exactly like clicking
    /// its X button, so the program can save or ask about unsaved work. Waits
    /// up to <paramref name="wait"/> and returns the processes still running.
    /// Nothing is forced here.
    /// </summary>
    public static async Task<List<ProcessMemoryInfo>> CloseGracefullyAsync(IReadOnlyList<ProcessMemoryInfo> processes, TimeSpan wait)
    {
        var targets = processes.Where(p => p.CanEnd && !ProcessKiller.IsProtectedProcess(p.ProcessName)).ToList();
        var pids = targets.Select(p => p.ProcessId).ToHashSet();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pids.Contains(pid)) PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);

        var deadline = DateTime.UtcNow + wait;
        List<ProcessMemoryInfo> running;
        do
        {
            await Task.Delay(300).ConfigureAwait(false);
            running = StillRunning(targets);
        } while (running.Count > 0 && DateTime.UtcNow < deadline);
        return running;
    }

    /// <summary>Programs holding the most committed memory (what pushes Windows toward "out of memory").</summary>
    public static List<AppMemoryInfo> GetTopCommitters(int count)
    {
        var snapshot = MemoryNative.SnapshotProcesses();
        if (snapshot == null) return new List<AppMemoryInfo>();
        var apps = snapshot
            .Where(p => p.Pid > 4 && p.Name.Length > 0)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppMemoryInfo
            {
                Name = g.Key,
                DisplayName = g.Key,
                MemoryMB = g.Sum(p => p.PrivateWorkingSetBytes) / MB,
                PrivateMemoryMB = g.Sum(p => p.PrivateBytes) / MB
            })
            .OrderByDescending(a => a.PrivateMemoryMB)
            .Take(Math.Clamp(count, 1, 20))
            .ToList();
        foreach (var app in apps)
        {
            var pid = snapshot.FirstOrDefault(p => p.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase))?.Pid ?? 0;
            app.DisplayName = FriendlyName(new AppMemoryInfo
            {
                Name = app.Name,
                Processes = pid == 0 ? Array.Empty<ProcessMemoryInfo>() : [new ProcessMemoryInfo { ProcessId = pid, ProcessName = app.Name }]
            }) ?? app.Name;
        }
        return apps;
    }

    /// <summary>
    /// True while a game, a full-screen video or a presentation is in front:
    /// CPU-heavy housekeeping waits until it isn't. Windows' own judgement
    /// first; otherwise a borderless window covering the whole monitor (an
    /// ordinary resizable window that happens to fill the screen doesn't count).
    /// </summary>
    public static bool ForegroundIsFullscreen()
    {
        if (SHQueryUserNotificationState(out int state) == 0 &&
            state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
            return true;

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var window)) return false;
        long style = GetWindowLongPtrW(hwnd, GWL_STYLE).ToInt64();
        if ((style & (WS_CAPTION | WS_THICKFRAME)) != 0) return false;
        var className = new StringBuilder(64);
        GetClassNameW(hwnd, className, className.Capacity);
        var name = className.ToString();
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // the desktop itself
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return false;
        return window.Left <= info.rcMonitor.Left && window.Top <= info.rcMonitor.Top &&
               window.Right >= info.rcMonitor.Right && window.Bottom >= info.rcMonitor.Bottom;
    }

    /// <summary>The processes from the list whose exact instance (PID + start time) is still alive.</summary>
    public static List<ProcessMemoryInfo> StillRunning(IEnumerable<ProcessMemoryInfo> processes)
    {
        var snapshot = MemoryNative.SnapshotProcesses();
        if (snapshot == null) return processes.ToList();
        var alive = snapshot.Select(p => (p.Pid, p.CreateTime)).ToHashSet();
        return processes.Where(p => alive.Contains((p.ProcessId, p.CreateTime))).ToList();
    }

    #region Native

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_THICKFRAME = 0x00040000;
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint WM_CLOSE = 0x0010;
    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    #endregion
}

/// <summary>CPU a process used, as a share of the whole CPU (all cores), like Task Manager shows it.</summary>
public readonly record struct CpuUsage(double LastMinute, double Last10Minutes, TimeSpan Watched);

/// <summary>
/// Remembers when each process last did any work (used the CPU) and how much CPU
/// it used over the last minutes, sampled every 10 seconds from one handle-free
/// snapshot (about a millisecond). Trimming then only touches programs that have
/// really been idle, and Smart CPU sees which background programs keep the CPU busy.
/// </summary>
public static class ProcessActivity
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private const long ActiveCpu100ns = 200_000; // 20 ms of CPU within a sample = in use
    private const int History = 61;              // 10 minutes of samples

    private sealed class Track
    {
        public long Cpu;
        public DateTime LastActiveUtc;
        public readonly long[] CpuAt = new long[History];
        public readonly long[] TicksAt = new long[History];
        public int Count, Head;

        public void Add(long ticks, long cpu)
        {
            Head = (Head + 1) % History;
            CpuAt[Head] = cpu;
            TicksAt[Head] = ticks;
            if (Count < History) Count++;
        }

        /// <summary>The newest sample at least <paramref name="window"/> old (or the oldest one).</summary>
        public (long Ticks, long Cpu) Back(long window)
        {
            long newest = TicksAt[Head];
            int index = Head;
            for (int i = 1; i < Count; i++)
            {
                index = (Head - i + History) % History;
                if (newest - TicksAt[index] >= window) break;
            }
            return (TicksAt[index], CpuAt[index]);
        }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<(int Pid, long Created), Track> Seen = new();
    private static DateTime _watchingSinceUtc = DateTime.MinValue;
    private static Timer? _timer;

    public static void Start()
    {
        lock (Gate) _timer ??= new Timer(_ => Sample(), null, TimeSpan.Zero, Interval);
    }

    private static void Sample()
    {
        // A timer callback that throws would end the whole app.
        try
        {
            var snapshot = MemoryNative.SnapshotProcesses();
            if (snapshot == null) return;
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                if (_watchingSinceUtc == DateTime.MinValue) _watchingSinceUtc = now;
                var alive = new HashSet<(int, long)>(snapshot.Count);
                foreach (var p in snapshot)
                {
                    var key = (p.Pid, p.CreateTime);
                    alive.Add(key);
                    if (!Seen.TryGetValue(key, out var track))
                    {
                        Seen[key] = track = new Track { Cpu = p.CpuTime, LastActiveUtc = now };
                    }
                    else
                    {
                        if (p.CpuTime - track.Cpu > ActiveCpu100ns) track.LastActiveUtc = now;
                        track.Cpu = p.CpuTime;
                    }
                    track.Add(now.Ticks, p.CpuTime);
                }
                foreach (var gone in Seen.Keys.Where(k => !alive.Contains(k)).ToList()) Seen.Remove(gone);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessActivity] Sample failed: {ex.Message}");
        }
    }

    /// <summary>How long the process has done nothing, or null while it hasn't been watched long enough to tell.</summary>
    public static TimeSpan? IdleFor(int pid, long createTime)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (_watchingSinceUtc == DateTime.MinValue || now - _watchingSinceUtc < TimeSpan.FromMinutes(1)) return null;
            return Seen.TryGetValue((pid, createTime), out var v) ? now - v.LastActiveUtc : null;
        }
    }

    /// <summary>CPU use over the last minute and the last 10 minutes, or null before two samples exist.</summary>
    public static CpuUsage? Usage(int pid, long createTime)
    {
        lock (Gate)
        {
            if (!Seen.TryGetValue((pid, createTime), out var track) || track.Count < 2) return null;
            long nowTicks = track.TicksAt[track.Head], nowCpu = track.CpuAt[track.Head];
            double Percent((long Ticks, long Cpu) then)
            {
                long wall = nowTicks - then.Ticks;
                return wall <= 0 ? 0 : Math.Clamp((nowCpu - then.Cpu) * 100.0 / (wall * (double)Environment.ProcessorCount), 0, 100);
            }
            var oldest = track.Back(long.MaxValue);
            return new CpuUsage(Percent(track.Back(TimeSpan.FromMinutes(1).Ticks)), Percent(oldest),
                TimeSpan.FromTicks(nowTicks - oldest.Ticks));
        }
    }
}

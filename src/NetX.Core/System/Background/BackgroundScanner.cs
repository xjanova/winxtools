using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NetX.Core.Optimization;

namespace NetX.Core.System.Background;

/// <summary>One running process, identified exactly (a reused PID is never mistaken for it).</summary>
public readonly record struct ProcessRef(int Pid, long CreateTime, string Name);

/// <summary>A program running without a window of its own, with what it costs and what started it.</summary>
public sealed class BackgroundApp
{
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public ProgramInfo? Info { get; init; }
    public ProgramClass Class => Info?.Class ?? ProgramClass.App;
    public string DisplayName => Info?.DisplayName ?? Name;
    public List<ProcessRef> Processes { get; } = new();

    /// <summary>Share of the whole CPU over the last minute and the last 10 minutes.</summary>
    public double CpuLastMinute { get; set; }
    public double CpuLast10Minutes { get; set; }

    /// <summary>RAM only it uses (private working set), all its processes together.</summary>
    public long RamMB { get; set; }

    /// <summary>Has a visible window: the user is (or can be) using it.</summary>
    public bool HasWindow { get; set; }
    public bool IsForeground { get; set; }

    /// <summary>Run keys, Startup items, tasks and services that start this program.</summary>
    public List<AutostartEntry> Origins { get; } = new();

    /// <summary>The program that started it, when that isn't Windows or the user's desktop.</summary>
    public string? StartedBy { get; set; }

    /// <summary>Started by a program that has a window open: a helper of something in use (WebView2, a browser's GPU process…).</summary>
    public bool HelperOfOpenApp { get; set; }

    /// <summary>Running as a service (Services list).</summary>
    public bool IsService { get; set; }

    public bool InEfficiencyMode { get; set; }
}

public sealed class BackgroundReport
{
    /// <summary>Programs without a window, heaviest first (CPU, then RAM).</summary>
    public List<BackgroundApp> Apps { get; init; } = new();

    /// <summary>Every program that starts by itself (Run keys, Startup folders, tasks, services).</summary>
    public List<AutostartEntry> Autostart { get; init; } = new();

    public Dictionary<string, ProgramInfo> Programs { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chrome / Edge right now: processes, RAM, and whether any window is open.</summary>
    public BrowserState? Chrome { get; init; }
    public BrowserState? Edge { get; init; }

    /// <summary>False without administrator rights: scheduled tasks couldn't be read.</summary>
    public bool TasksRead { get; init; }

    /// <summary>Seconds the CPU history covers (CPU figures are only meaningful after a minute or so).</summary>
    public double WatchedSeconds { get; init; }
}

public sealed record BrowserState(int Processes, long RamMB, double CpuLastMinute, bool HasWindow);

/// <summary>
/// Puts the pieces together: one process snapshot, the CPU history WinXTools
/// keeps anyway (<see cref="ProcessActivity"/>), visible windows, everything that
/// starts by itself, and who made each program. Read-only; call off the UI thread.
/// </summary>
public static class BackgroundScanner
{
    private const long MB = 1024 * 1024;

    /// <param name="known">An autostart list read earlier (it changes rarely and costs a second to read).</param>
    public static BackgroundReport Scan(bool includeAutostart = true, IReadOnlyList<AutostartEntry>? known = null)
    {
        ProcessActivity.Start();
        var snapshot = MemoryNative.SnapshotProcesses() ?? new List<MemoryNative.ProcessEntry>();
        bool elevated = Autostart.IsElevated();
        var autostart = known?.ToList() ??
                        (includeAutostart ? Autostart.Enumerate(includeServices: true, includeTasks: elevated) : new List<AutostartEntry>());
        var windows = WindowOwners();
        int foreground = ProcessKiller.GetForegroundProcessId();
        int own = Environment.ProcessId;
        int ownSession = SessionOf(own, snapshot);

        var byPid = snapshot.ToDictionary(p => p.Pid);
        var byExe = autostart.Where(e => e.ExecutablePath != null)
            .GroupBy(e => e.ExecutablePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var servicePids = autostart.Where(e => e.Kind == AutostartKind.Service && e.ServiceProcessId > 0)
            .GroupBy(e => e.ServiceProcessId).ToDictionary(g => g.Key, g => g.ToList());

        var programs = new Dictionary<string, ProgramInfo>(StringComparer.OrdinalIgnoreCase);
        var apps = new Dictionary<string, BackgroundApp>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<int, string?>();
        double watched = 0;

        foreach (var p in snapshot)
        {
            if (p.Pid <= 4 || p.Pid == own || p.Name.Length == 0) continue;
            var path = ImagePath(p.Pid);
            // Without administrator rights most services can't be opened: a service's own
            // registration says which program it is; anything else in session 0 is Windows'.
            if (path == null && servicePids.TryGetValue(p.Pid, out var hosted) && hosted.Count == 1)
                path = hosted[0].ExecutablePath;
            paths[p.Pid] = path;
        }

        // Who made each program: signature checks read whole files, so the first scan does them in parallel.
        var distinct = paths.Values.Concat(autostart.Select(e => e.ExecutablePath))
            .Where(p => p != null).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Parallel.ForEach(distinct, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) },
            path => ProgramCatalog.Describe(path));

        foreach (var p in snapshot)
        {
            if (!paths.TryGetValue(p.Pid, out var path)) continue;
            if (path == null && p.SessionId == 0) continue;
            var key = path ?? p.Name;
            if (!apps.TryGetValue(key, out var app))
            {
                ProgramInfo? info = null;
                if (path != null)
                {
                    if (!programs.TryGetValue(path, out info)) programs[path] = info = ProgramCatalog.Describe(path);
                }
                else if (ProcessKiller.IsProtectedProcess(p.Name))
                {
                    info = new ProgramInfo { Path = p.Name, Class = ProgramClass.Windows };
                }
                app = new BackgroundApp { Name = p.Name, Path = path, Info = info };
                if (path != null && byExe.TryGetValue(path, out var origins)) app.Origins.AddRange(origins);
                apps[key] = app;
            }

            app.Processes.Add(new ProcessRef(p.Pid, p.CreateTime, p.Name));
            app.RamMB += p.PrivateWorkingSetBytes / MB;
            if (ProcessActivity.Usage(p.Pid, p.CreateTime) is { } usage)
            {
                app.CpuLastMinute += usage.LastMinute;
                app.CpuLast10Minutes += usage.Last10Minutes;
                watched = Math.Max(watched, usage.Watched.TotalSeconds);
            }
            if (windows.Contains(p.Pid)) app.HasWindow = true;
            if (p.Pid == foreground) app.IsForeground = true;
            if (servicePids.TryGetValue(p.Pid, out var services))
            {
                app.IsService = true;
                foreach (var s in services) if (!app.Origins.Contains(s)) app.Origins.Add(s);
            }
            if (app.StartedBy == null && p.SessionId == ownSession) app.StartedBy = Parent(p, byPid, paths);
            // Started by a program with a window open — but not by the desktop (Explorer starts every Run entry).
            if (windows.Contains(p.ParentPid) && byPid.TryGetValue(p.ParentPid, out var opener) && opener.CreateTime <= p.CreateTime &&
                !ProcessKiller.IsProtectedProcess(opener.Name))
                app.HelperOfOpenApp = true;
        }

        // A program whose window belongs to the foreground program's family (browser helpers) counts as in use.
        var background = apps.Values
            .Where(a => !a.HasWindow && !a.IsForeground && a.Class != ProgramClass.Windows)
            .OrderByDescending(a => Math.Round(a.CpuLastMinute, 1))
            .ThenByDescending(a => a.RamMB)
            .ToList();

        foreach (var app in background)
            app.InEfficiencyMode = app.Processes.Any(p => Efficiency.IsOn(p));

        return new BackgroundReport
        {
            Apps = background,
            Autostart = autostart,
            Programs = programs,
            Chrome = Browser(apps.Values, "chrome"),
            Edge = Browser(apps.Values, "msedge"),
            TasksRead = elevated,
            WatchedSeconds = watched
        };
    }

    private static BrowserState? Browser(IEnumerable<BackgroundApp> apps, string name)
    {
        var all = apps.Where(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (all.Count == 0) return null;
        return new BrowserState(all.Sum(a => a.Processes.Count), all.Sum(a => a.RamMB), all.Sum(a => a.CpuLastMinute), all.Any(a => a.HasWindow));
    }

    /// <summary>"Started by Steam" — only when the parent is still the same process and isn't Windows or the desktop.</summary>
    private static string? Parent(MemoryNative.ProcessEntry child, Dictionary<int, MemoryNative.ProcessEntry> byPid, Dictionary<int, string?> paths)
    {
        if (child.ParentPid <= 4 || !byPid.TryGetValue(child.ParentPid, out var parent)) return null;
        if (parent.CreateTime > child.CreateTime) return null; // the PID was reused by a newer process
        if (parent.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase)) return null; // its own helper processes
        if (ProcessKiller.IsProtectedProcess(parent.Name) || parent.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return null;
        if (!paths.TryGetValue(parent.Pid, out var path)) path = ImagePath(parent.Pid);
        if (path != null && ProgramCatalog.Describe(path).Class == ProgramClass.Windows) return null;
        return path != null ? ProgramCatalog.Describe(path).DisplayName : parent.Name;
    }

    private static int SessionOf(int pid, List<MemoryNative.ProcessEntry> snapshot) =>
        snapshot.FirstOrDefault(p => p.Pid == pid)?.SessionId ?? -1;

    #region Native helpers

    /// <summary>Processes that own a visible top-level window.</summary>
    private static HashSet<int> WindowOwners()
    {
        var owners = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindow(hwnd, 4 /* GW_OWNER */) == IntPtr.Zero && GetWindowTextLengthW(hwnd) > 0 &&
                !IsCloaked(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out int pid);
                owners.Add(pid);
            }
            return true;
        }, IntPtr.Zero);
        return owners;
    }

    /// <summary>Windows 10/11 keep some windows "visible" but hidden (suspended apps): those don't count.</summary>
    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static string? ImagePath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
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

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    #endregion
}

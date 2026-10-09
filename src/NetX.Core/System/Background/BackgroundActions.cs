using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using NetX.Core.System.Tweaks;

namespace NetX.Core.System.Background;

/// <summary>
/// Windows 11's "Efficiency mode" (the leaf in Task Manager): the process runs
/// at idle priority on power-saving cores (EcoQoS). It keeps working — nothing
/// is closed — it just stops competing with what you are doing. Lasts until the
/// process exits or it is switched off again.
/// </summary>
public static class Efficiency
{
    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 0x1;
    private const uint IdlePriority = 0x40, NormalPriority = 0x20;
    private const uint PROCESS_SET_INFORMATION = 0x0200, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static readonly object Gate = new();
    private static readonly Dictionary<(int Pid, long Created), uint> Applied = new();

    public static bool IsOn(ProcessRef process)
    {
        lock (Gate) return Applied.ContainsKey((process.Pid, process.CreateTime));
    }

    /// <summary>True when Windows accepted it. Never for WinXTools itself or Windows' protected processes.</summary>
    public static bool Set(ProcessRef process, bool on)
    {
        if (process.Pid <= 4 || process.Pid == Environment.ProcessId || Optimization.ProcessKiller.IsProtectedProcess(process.Name))
            return false;
        var handle = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, process.Pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            // The exact instance only: a PID reused by another program is left alone.
            if (!GetProcessTimes(handle, out long created, out _, out _, out _) || created != process.CreateTime) return false;

            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = ExecutionSpeed,
                StateMask = on ? ExecutionSpeed : 0
            };
            if (!SetProcessInformation(handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
                return false;

            lock (Gate)
            {
                var key = (process.Pid, process.CreateTime);
                if (on)
                {
                    if (!Applied.ContainsKey(key)) Applied[key] = GetPriorityClass(handle);
                    SetPriorityClass(handle, IdlePriority);
                }
                else
                {
                    uint original = Applied.Remove(key, out var saved) && saved != 0 ? saved : NormalPriority;
                    SetPriorityClass(handle, original == IdlePriority ? NormalPriority : original);
                }
            }
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Turns autostart entries off and on where they are set up, the way Windows
/// itself does it, so every change can be undone and Task Manager agrees:
/// Run keys and Startup folders through StartupApproved (Task Manager's Startup
/// tab switch), scheduled tasks through Task Scheduler (disabled, not deleted),
/// services by setting them to start only when something asks for them (Manual).
/// Needs administrator rights for anything machine-wide.
/// </summary>
public static class AutostartControl
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <returns>Null on success, else an English diagnostic.</returns>
    public static string? SetEnabled(AutostartEntry entry, bool enabled, ServiceStartMode? restoreMode = null, bool restoreDelayed = false)
    {
        try
        {
            switch (entry.Kind)
            {
                case AutostartKind.RunKey:
                case AutostartKind.StartupFolder:
                    WriteApproved(entry, enabled);
                    return null;
                case AutostartKind.ScheduledTask:
                    return ChangeTask(entry.Name, enabled);
                case AutostartKind.Service:
                    if (enabled)
                    {
                        var mode = restoreMode ?? ServiceStartMode.Automatic;
                        ServiceTweak.SetStartMode(entry.Name, mode, mode == ServiceStartMode.Automatic ? restoreDelayed : null);
                        if (mode == ServiceStartMode.Automatic) ServiceTweak.StartIfStopped(entry.Name);
                    }
                    else
                    {
                        // Manual, not Disabled: anything that really needs it can still start it.
                        ServiceTweak.SetStartMode(entry.Name, ServiceStartMode.Manual, null);
                        ServiceTweak.StopIfRunning(entry.Name);
                    }
                    return null;
            }
            return "Unknown kind";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutostartControl] {entry.Key}: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>Task Manager's format: first byte 2 = on, 3 = off, then when it was turned off (FILETIME).</summary>
    private static void WriteApproved(AutostartEntry entry, bool enabled)
    {
        var hive = entry.MachineWide ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        string sub = entry.Kind == AutostartKind.StartupFolder ? "StartupFolder"
            : entry.Location.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase) ? "Run32" : "Run";
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.CreateSubKey(Approved + sub, writable: true);
        key.SetValue(entry.Name, data, RegistryValueKind.Binary);
    }

    private static string? ChangeTask(string taskPath, bool enabled)
    {
        // The name comes from the task's file path; refuse anything that could break out of the argument.
        if (taskPath.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) return "Invalid task name";
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add("/Change");
        psi.ArgumentList.Add("/TN");
        psi.ArgumentList.Add(taskPath);
        psi.ArgumentList.Add(enabled ? "/ENABLE" : "/DISABLE");
        using var process = Process.Start(psi);
        if (process == null) return "schtasks did not start";
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(15000))
        {
            try { process.Kill(); } catch { }
            return "schtasks timed out";
        }
        return process.ExitCode == 0 ? null : (error.Trim().Length > 0 ? error.Trim() : $"exit {process.ExitCode}");
    }
}

public enum BrowserSetting
{
    /// <summary>Chrome keeps running in the background after its last window is closed.</summary>
    ChromeBackgroundMode,
    /// <summary>Chrome's Memory Saver: inactive tabs are unloaded from RAM.</summary>
    ChromeMemorySaver,
    /// <summary>Edge keeps running in the background after its last window is closed.</summary>
    EdgeBackgroundMode,
    /// <summary>Edge starts at sign-in so it opens faster.</summary>
    EdgeStartupBoost,
    /// <summary>Edge's efficiency mode (sleeping tabs, lower power use).</summary>
    EdgeEfficiencyMode
}

/// <summary>
/// The browsers' own administrator settings (policies) under
/// HKLM\SOFTWARE\Policies — documented by Google and Microsoft, the same keys a
/// company's IT department uses. A browser with any policy set says it is
/// "managed by your organization"; deleting the value undoes it completely.
/// </summary>
public static class BrowserPolicies
{
    private const string ChromeKey = @"SOFTWARE\Policies\Google\Chrome";
    private const string EdgeKey = @"SOFTWARE\Policies\Microsoft\Edge";

    private static (string Key, string Value, int Recommended) Where(BrowserSetting setting) => setting switch
    {
        BrowserSetting.ChromeBackgroundMode => (ChromeKey, "BackgroundModeEnabled", 0),
        BrowserSetting.ChromeMemorySaver => (ChromeKey, "HighEfficiencyModeEnabled", 1),
        BrowserSetting.EdgeBackgroundMode => (EdgeKey, "BackgroundModeEnabled", 0),
        BrowserSetting.EdgeStartupBoost => (EdgeKey, "StartupBoostEnabled", 0),
        _ => (EdgeKey, "EfficiencyModeEnabled", 1)
    };

    /// <summary>The value WinXTools recommends (0 = off, 1 = on).</summary>
    public static int Recommended(BrowserSetting setting) => Where(setting).Recommended;

    /// <summary>The policy value, or null when not set (the browser's own setting applies).</summary>
    public static int? Get(BrowserSetting setting)
    {
        var (key, value, _) = Where(setting);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = root.OpenSubKey(key);
        return k?.GetValue(value) is int v ? v : null;
    }

    /// <summary>Sets the policy (null removes it). Needs administrator rights. Null on success, else a diagnostic.</summary>
    public static string? Set(BrowserSetting setting, int? enabled)
    {
        var (key, value, _) = Where(setting);
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (enabled is int v)
            {
                using var k = root.CreateSubKey(key, writable: true);
                k.SetValue(value, v, RegistryValueKind.DWord);
            }
            else
            {
                using var k = root.OpenSubKey(key, writable: true);
                k?.DeleteValue(value, throwOnMissingValue: false);
            }
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserPolicies] {setting}: {ex.Message}");
            return ex.Message;
        }
    }
}

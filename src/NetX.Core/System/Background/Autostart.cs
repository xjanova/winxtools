using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace NetX.Core.System.Background;

public enum AutostartKind
{
    /// <summary>A value under a Run key (what Task Manager's Startup tab lists).</summary>
    RunKey,
    /// <summary>A shortcut or file in a Startup folder.</summary>
    StartupFolder,
    /// <summary>A scheduled task that runs a program.</summary>
    ScheduledTask,
    /// <summary>A Windows service.</summary>
    Service
}

/// <summary>Something that starts a program by itself, and where it is set up.</summary>
public sealed class AutostartEntry
{
    public AutostartKind Kind { get; init; }

    /// <summary>Value name, shortcut file name, task path or service name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Registry key, folder, "Task Scheduler" or "Services" — shown to the user as the origin.</summary>
    public string Location { get; init; } = "";

    public string Command { get; init; } = "";
    public string? ExecutablePath { get; init; }

    /// <summary>For everyone on the PC (HKLM, the common Startup folder, a machine task or service).</summary>
    public bool MachineWide { get; init; }

    public bool Enabled { get; init; }

    /// <summary>Tasks: what starts it ("logon", "boot", "idle", "schedule", "event"…).</summary>
    public IReadOnlyList<string> Triggers { get; init; } = Array.Empty<string>();

    /// <summary>Services: current start type and display name.</summary>
    public ServiceStartMode? StartMode { get; init; }
    public bool DelayedStart { get; init; }
    public string? DisplayName { get; init; }
    public int ServiceProcessId { get; init; }

    /// <summary>Stable identity for the change journal.</summary>
    public string Key => $"{Kind}|{Location}|{Name}";
}

/// <summary>
/// Everything that starts a program without being asked: Run keys (with the
/// on/off state Task Manager's Startup tab keeps in StartupApproved), Startup
/// folders, scheduled tasks and services. Read-only; scheduled tasks need
/// administrator rights (their definitions are readable by administrators only).
/// </summary>
public static class Autostart
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<AutostartEntry> Enumerate(bool includeServices = true, bool includeTasks = true)
    {
        var list = new List<AutostartEntry>();
        Try(() => ReadRunKey(list, RegistryHive.CurrentUser, RegistryView.Registry64, "Run", machine: false));
        Try(() => ReadRunKey(list, RegistryHive.LocalMachine, RegistryView.Registry64, "Run", machine: true));
        Try(() => ReadRunKey(list, RegistryHive.LocalMachine, RegistryView.Registry32, "Run32", machine: true));
        Try(() => ReadStartupFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), machine: false));
        Try(() => ReadStartupFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), machine: true));
        if (includeTasks) Try(() => ReadTasks(list));
        if (includeServices) Try(() => ReadServices(list));
        return list;
    }

    private static void Try(Action read)
    {
        try { read(); }
        catch (Exception ex) { Debug.WriteLine($"[Autostart] {ex.Message}"); }
    }

    #region Run keys and Startup folders

    private static void ReadRunKey(List<AutostartEntry> list, RegistryHive hive, RegistryView view, string approvedName, bool machine)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(Run);
        if (key == null) return;
        string location = (hive == RegistryHive.CurrentUser ? @"HKCU\" : @"HKLM\") +
                          (view == RegistryView.Registry32 ? @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" : Run);
        foreach (var name in key.GetValueNames())
        {
            if (name.Length == 0 || key.GetValue(name) is not string command || command.Trim().Length == 0) continue;
            list.Add(new AutostartEntry
            {
                Kind = AutostartKind.RunKey,
                Name = name,
                Location = location,
                Command = command,
                ExecutablePath = CommandLine.Executable(command),
                MachineWide = machine,
                Enabled = IsApproved(hive, approvedName, name)
            });
        }
    }

    private static void ReadStartupFolder(List<AutostartEntry> list, string folder, bool machine)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            string command = file;
            string? exe = file;
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var (target, arguments) = Shortcut.Resolve(file);
                if (target != null)
                {
                    command = string.IsNullOrEmpty(arguments) ? $"\"{target}\"" : $"\"{target}\" {arguments}";
                    exe = CommandLine.Executable(command) ?? target;
                }
            }
            list.Add(new AutostartEntry
            {
                Kind = AutostartKind.StartupFolder,
                Name = name,
                Location = folder,
                Command = command,
                ExecutablePath = exe,
                MachineWide = machine,
                Enabled = IsApproved(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, "StartupFolder", name)
            });
        }
    }

    /// <summary>
    /// Task Manager's on/off switch: a binary value whose first byte is even
    /// (enabled) or odd (disabled). No value at all means enabled.
    /// </summary>
    private static bool IsApproved(RegistryHive hive, string approvedName, string valueName)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(Approved + approvedName);
        return key?.GetValue(valueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    #endregion

    #region Scheduled tasks (definitions under System32\Tasks)

    private static void ReadTasks(List<AutostartEntry> list)
    {
        var folder = Path.Combine(Environment.SystemDirectory, "Tasks");
        if (!Directory.Exists(folder)) return;
        var options = new global::System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 512 * 1024) continue;
                var doc = XDocument.Load(file);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                var exec = doc.Root?.Element(ns + "Actions")?.Elements(ns + "Exec").FirstOrDefault();
                var commandPath = exec?.Element(ns + "Command")?.Value.Trim();
                if (string.IsNullOrEmpty(commandPath)) continue; // COM handler tasks: nothing to point at

                var arguments = exec!.Element(ns + "Arguments")?.Value.Trim() ?? "";
                string command = (commandPath.StartsWith('"') ? commandPath : $"\"{commandPath}\"") + (arguments.Length > 0 ? " " + arguments : "");
                string path = "\\" + Path.GetRelativePath(folder, file);
                bool enabled = !string.Equals(doc.Root!.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value.Trim(), "false",
                    StringComparison.OrdinalIgnoreCase);
                var triggers = doc.Root.Element(ns + "Triggers")?.Elements().Select(t => TriggerName(t.Name.LocalName)).Distinct().ToList()
                               ?? new List<string>();

                list.Add(new AutostartEntry
                {
                    Kind = AutostartKind.ScheduledTask,
                    Name = path,
                    Location = "Task Scheduler",
                    Command = command,
                    ExecutablePath = CommandLine.Executable(command),
                    MachineWide = true,
                    Enabled = enabled,
                    Triggers = triggers
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Autostart] task {file}: {ex.Message}");
            }
        }
    }

    private static string TriggerName(string element) => element switch
    {
        "LogonTrigger" => "logon",
        "BootTrigger" => "boot",
        "IdleTrigger" => "idle",
        "TimeTrigger" or "CalendarTrigger" => "schedule",
        "EventTrigger" => "event",
        "SessionStateChangeTrigger" => "session",
        "RegistrationTrigger" => "install",
        _ => element
    };

    #endregion

    #region Services

    private static void ReadServices(List<AutostartEntry> list)
    {
        using var searcher = new ManagementObjectSearcher(@"root\cimv2",
            "SELECT Name, DisplayName, PathName, StartMode, ProcessId, ServiceType FROM Win32_Service");
        using var results = searcher.Get();
        foreach (ManagementObject service in results)
        {
            using (service)
            {
                var name = service["Name"]?.ToString();
                var path = service["PathName"]?.ToString();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) continue;
                var startMode = service["StartMode"]?.ToString() switch
                {
                    "Auto" => ServiceStartMode.Automatic,
                    "Manual" => ServiceStartMode.Manual,
                    "Disabled" => ServiceStartMode.Disabled,
                    _ => (ServiceStartMode?)null // boot/system: drivers, never ours to change
                };
                if (startMode == null) continue;
                bool delayed = false;
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                    delayed = key?.GetValue("DelayedAutostart") is int d && d != 0;
                }
                catch
                {
                }

                list.Add(new AutostartEntry
                {
                    Kind = AutostartKind.Service,
                    Name = name,
                    Location = "Services",
                    Command = path,
                    ExecutablePath = CommandLine.Executable(path),
                    MachineWide = true,
                    Enabled = startMode == ServiceStartMode.Automatic,
                    StartMode = startMode,
                    DelayedStart = delayed,
                    DisplayName = service["DisplayName"]?.ToString(),
                    ServiceProcessId = service["ProcessId"] is uint pid ? (int)pid : 0
                });
            }
        }
    }

    #endregion

    public static bool IsElevated()
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
    }
}

/// <summary>Finds the program a command line runs.</summary>
public static class CommandLine
{
    private static readonly string[] Hosts = { "rundll32.exe", "wscript.exe", "cscript.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "mshta.exe" };

    /// <summary>
    /// The program file of a command line: quoted or not, with environment
    /// variables expanded. For script hosts and rundll32 it is the script or DLL
    /// they run (that is what decides who made it). Null when nothing exists on disk.
    /// </summary>
    public static string? Executable(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.StartsWith(@"\??\", StringComparison.Ordinal)) text = text[4..];

        string? first = FirstPath(text, out string rest);
        if (first == null) return null;

        // Squirrel installers (Discord, Teams classic, Slack…): "Update.exe --processStart Discord.exe" runs the app itself.
        if (Path.GetFileName(first).Equals("Update.exe", StringComparison.OrdinalIgnoreCase) &&
            Tokens(rest).SkipWhile(t => !t.Equals("--processStart", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() is { } target &&
            SquirrelApp(Path.GetDirectoryName(first)!, target.Trim('"')) is { } app)
            return app;

        if (!Hosts.Contains(Path.GetFileName(first), StringComparer.OrdinalIgnoreCase)) return first;

        // rundll32 "C:\x\y.dll",Entry · wscript "C:\x\run.vbs" · cmd /c "C:\x\a.bat" · powershell -File x.ps1
        foreach (var token in Tokens(rest))
        {
            var candidate = token.Split(',')[0].Trim('"');
            if (candidate.Length > 3 && (candidate.Contains('\\') || candidate.Contains('/')) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        return first;
    }

    /// <summary>The newest "app-x.y.z\Name.exe" next to a Squirrel Update.exe.</summary>
    private static string? SquirrelApp(string folder, string exeName)
    {
        try
        {
            if (exeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            return Directory.EnumerateDirectories(folder, "app-*")
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d)[4..], out var v) ? v : new Version(0, 0))
                .Select(d => Path.Combine(d, exeName))
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstPath(string text, out string rest)
    {
        rest = "";
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            if (close <= 1) return null;
            rest = text[(close + 1)..];
            return Resolve(text[1..close]);
        }
        // Unquoted, possibly with spaces: grow word by word until a file exists.
        var words = text.Split(' ');
        for (int i = 1; i <= words.Length; i++)
        {
            var candidate = string.Join(' ', words.Take(i));
            if (Resolve(candidate) is { } found)
            {
                rest = string.Join(' ', words.Skip(i));
                return found;
            }
        }
        return null;
    }

    private static string? Resolve(string path)
    {
        try
        {
            if (path.Length == 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            if (File.Exists(path)) return Path.GetFullPath(path);
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path + ".exe")) return Path.GetFullPath(path + ".exe");
            if (!Path.IsPathRooted(path))
            {
                // "rundll32.exe …" and friends: the Windows folders.
                foreach (var dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
                {
                    var full = Path.Combine(dir, path);
                    if (File.Exists(full)) return full;
                    if (File.Exists(full + ".exe")) return full + ".exe";
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var current = new StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"') { quoted = !quoted; current.Append(c); continue; }
            if (c == ' ' && !quoted)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}

/// <summary>Reads where a .lnk shortcut points (the shell's own IShellLink).</summary>
internal static class Shortcut
{
    public static (string? Target, string? Arguments) Resolve(string lnk)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(lnk, 0);
                var target = new StringBuilder(1024);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                var arguments = new StringBuilder(2048);
                link.GetArguments(arguments, arguments.Capacity);
                return (target.Length > 0 ? target.ToString() : null, arguments.ToString());
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Shortcut] {lnk}: {ex.Message}");
            return (null, null);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
    }
}

using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NetX.Core.System;

#region Models

/// <summary>Which of the three Uninstall registry locations an entry was read from.</summary>
public enum UninstallScope
{
    /// <summary>HKLM, 64-bit view — installed for all users.</summary>
    Machine64,
    /// <summary>HKLM, 32-bit view (WOW6432Node) — 32-bit program installed for all users.</summary>
    Machine32,
    /// <summary>HKCU — installed for the current user only.</summary>
    CurrentUser,
}

/// <summary>One program registration, as Windows' Apps list reads it.</summary>
public sealed class UninstallEntry
{
    public string KeyName { get; init; } = "";
    public UninstallScope Scope { get; init; }
    public string DisplayName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string DisplayVersion { get; init; } = "";
    public long EstimatedSizeKB { get; init; }
    /// <summary>Raw InstallDate value (normally yyyyMMdd).</summary>
    public string InstallDate { get; init; } = "";
    public string UninstallString { get; init; } = "";
    /// <summary>Normalized local folder, or empty when not recorded / not usable.</summary>
    public string InstallLocation { get; init; } = "";
    public string DisplayIcon { get; init; } = "";
    public bool IsWindowsInstaller { get; init; }
    /// <summary>SystemComponent=1 — Windows hides these from the Apps list.</summary>
    public bool IsSystemComponent { get; init; }
    /// <summary>Update/patch (ParentKeyName or an update ReleaseType) — not a program of its own.</summary>
    public bool IsUpdate { get; init; }
    /// <summary>NoRemove=1 — the publisher disabled uninstalling from the Apps list.</summary>
    public bool NoRemove { get; init; }

    /// <summary>HKCU registration: the user (and anything running as the user) can edit it.</summary>
    public bool IsPerUser => Scope == UninstallScope.CurrentUser;
}

/// <summary>An uninstall command split into program + arguments. Never run through cmd.exe.</summary>
public sealed class UninstallCommand
{
    /// <summary>Full path of the program to start.</summary>
    public string FileName { get; init; } = "";
    public string Arguments { get; init; } = "";
    /// <summary>Windows Installer uninstall (msiexec /X{ProductCode}).</summary>
    public bool IsMsi { get; init; }
}

public enum UninstallCommandProblem
{
    None,
    /// <summary>No uninstall command registered (and no MSI product code to fall back on).</summary>
    NoCommand,
    /// <summary>The registered uninstaller file does not exist.</summary>
    UninstallerMissing,
}

public enum UninstallLaunchError
{
    Failed,
    /// <summary>A per-user uninstaller asked for admin rights (ERROR_ELEVATION_REQUIRED).</summary>
    ElevationRequired,
    /// <summary>Couldn't obtain the signed-in user's normal (non-elevated) rights.</summary>
    DesktopUserUnavailable,
}

public sealed class UninstallLaunchException : Exception
{
    public UninstallLaunchException(UninstallLaunchError error, int win32Code)
        : base($"Uninstaller launch failed ({error}, Win32 error {win32Code})")
    {
        Error = error;
        Win32Code = win32Code;
    }

    public UninstallLaunchError Error { get; }
    public int Win32Code { get; }
}

public enum UninstallWaitStage
{
    Running,
    /// <summary>Every process we started has exited but the program is still registered.</summary>
    ClosedButStillRegistered,
}

public enum UninstallWaitResult
{
    Finished,
    StoppedByUser,
}

public enum LeftoverKind
{
    InstallFolder,
    ProgramFiles,
    ProgramData,
    RoamingAppData,
    LocalAppData,
}

/// <summary>A folder an uninstalled program left behind (real size from walking it).</summary>
public sealed class LeftoverFolder
{
    public string Path { get; init; } = "";
    public LeftoverKind Kind { get; init; }
    public long Bytes { get; set; }
    public int FileCount { get; set; }
}

public enum RecycleStatus
{
    Moved,
    /// <summary>Folder no longer existed (e.g. the uninstaller finished removing it).</summary>
    AlreadyGone,
    /// <summary>Files in use or access denied — the folder is still there.</summary>
    InUseOrDenied,
    /// <summary>The user cancelled a Windows prompt (e.g. declined deleting permanently) — folder kept.</summary>
    Cancelled,
    /// <summary>Failed the safety re-check (became a link, now belongs to an installed program...).</summary>
    Changed,
    Failed,
}

public sealed class RecycleResult
{
    public LeftoverFolder Folder { get; init; } = null!;
    public RecycleStatus Status { get; init; }
    /// <summary>SHFileOperation return code when <see cref="Status"/> is Failed.</summary>
    public int ErrorCode { get; init; }
}

#endregion

/// <summary>
/// Uninstall engine for the Deep Uninstaller page: reads the same registry locations Windows'
/// Apps list uses, runs a program's own uninstaller safely (no cmd.exe, per-user uninstallers
/// without our admin rights), waits for it — including processes it spawns — and finds leftover
/// folders by exact match only. Leftovers are only ever moved to the Recycle Bin.
/// </summary>
public static class UninstallHelper
{
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    // Once the registration is gone, how long remaining uninstaller processes may keep running
    // (still deleting files) before we carry on anyway — e.g. a feedback page it opened in a browser.
    private static readonly TimeSpan FinishGrace = TimeSpan.FromSeconds(20);

    // Windows lists these under "Installed updates", not as programs
    private static readonly HashSet<string> UpdateReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Security Update", "Update Rollup", "Hotfix", "Update", "Service Pack",
    };

    private static readonly Regex ProductCodeExact = new(
        @"^\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProductCodeInText = new(
        @"\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // End of an unquoted program path: "C:\Program Files\App\uninst.exe /S"
    private static readonly Regex ExecutableExtension = new(
        @"\.(exe|com|bat|cmd)(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    #region Registry

    /// <summary>
    /// Programs as Windows' Apps list shows them: a display name and an uninstall command,
    /// updates/patches left out, duplicates removed. Hidden system components (SystemComponent=1)
    /// are included but flagged so the UI can hide them by default, like Windows does.
    /// </summary>
    public static List<UninstallEntry> GetInstalledPrograms()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var programs = new List<UninstallEntry>();

        // Visible entries first so a hidden twin never replaces the one Windows shows
        foreach (var entry in ReadAllEntries().OrderBy(e => e.IsSystemComponent))
        {
            if (entry.IsUpdate) continue;
            if (entry.UninstallString.Length == 0 && !(entry.IsWindowsInstaller && IsProductCode(entry.KeyName)))
                continue;

            // Same product registered twice (e.g. in both HKLM views) — keep the first
            if (!seen.Add(string.Join('\u0001', entry.DisplayName, entry.DisplayVersion, entry.Publisher)))
                continue;

            programs.Add(entry);
        }

        return programs;
    }

    /// <summary>
    /// Every registration with a display name from HKLM (64- and 32-bit views) and HKCU,
    /// hidden components and updates included. Unreadable keys are skipped.
    /// </summary>
    public static List<UninstallEntry> ReadAllEntries()
    {
        var entries = new List<UninstallEntry>();
        ReadScope(UninstallScope.Machine64, entries);
        // 32-bit Windows has no separate 32-bit view — reading it would list everything twice
        if (Environment.Is64BitOperatingSystem) ReadScope(UninstallScope.Machine32, entries);
        ReadScope(UninstallScope.CurrentUser, entries);
        return entries;
    }

    /// <summary>
    /// True while the entry's own Uninstall key (same hive and view) still has a display name.
    /// If the key can't be read we answer "still registered" — the safe side for Deep Clean.
    /// </summary>
    public static bool IsStillRegistered(UninstallEntry entry)
    {
        try
        {
            using var baseKey = OpenBaseKey(entry.Scope);
            using var key = baseKey.OpenSubKey(UninstallKeyPath + "\\" + entry.KeyName);
            return key != null && ReadString(key, "DisplayName").Length > 0;
        }
        catch
        {
            return true;
        }
    }

    private static RegistryKey OpenBaseKey(UninstallScope scope) => scope switch
    {
        UninstallScope.Machine64 => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        UninstallScope.Machine32 => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32),
        // HKCU\Software\...\Uninstall is shared between the 32- and 64-bit views
        _ => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
    };

    private static void ReadScope(UninstallScope scope, List<UninstallEntry> into)
    {
        try
        {
            using var baseKey = OpenBaseKey(scope);
            using var root = baseKey.OpenSubKey(UninstallKeyPath);
            if (root == null) return;

            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    var entry = key == null ? null : ReadEntry(key, name, scope);
                    if (entry != null) into.Add(entry);
                }
                catch
                {
                    // Unreadable entry (ACL, corrupt value) — skip it like Windows does
                }
            }
        }
        catch
        {
            // Whole location unreadable — nothing to list from it
        }
    }

    private static UninstallEntry? ReadEntry(RegistryKey key, string keyName, UninstallScope scope)
    {
        var displayName = ReadString(key, "DisplayName");
        if (displayName.Length == 0) return null;

        return new UninstallEntry
        {
            KeyName = keyName,
            Scope = scope,
            DisplayName = displayName,
            Publisher = ReadString(key, "Publisher"),
            DisplayVersion = ReadString(key, "DisplayVersion"),
            EstimatedSizeKB = ReadNumber(key, "EstimatedSize"),
            InstallDate = ReadString(key, "InstallDate"),
            UninstallString = ReadString(key, "UninstallString"),
            InstallLocation = NormalizeDirectory(ReadString(key, "InstallLocation")) ?? "",
            DisplayIcon = ReadString(key, "DisplayIcon"),
            IsWindowsInstaller = ReadNumber(key, "WindowsInstaller") == 1,
            IsSystemComponent = ReadNumber(key, "SystemComponent") == 1,
            NoRemove = ReadNumber(key, "NoRemove") == 1,
            IsUpdate = ReadString(key, "ParentKeyName").Length > 0 ||
                       UpdateReleaseTypes.Contains(ReadString(key, "ReleaseType")),
        };
    }

    /// <summary>REG_SZ / REG_EXPAND_SZ (expanded) as trimmed text; anything else is "".</summary>
    private static string ReadString(RegistryKey key, string name)
    {
        try
        {
            return (key.GetValue(name) as string)?.Replace("\0", "").Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>DWORD / QWORD, or a number stored as text; 0 when missing or not a number.</summary>
    private static long ReadNumber(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) switch
            {
                int i => i,
                long l => l,
                string s when long.TryParse(s.Trim(), out var v) => v,
                _ => 0,
            };
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsProductCode(string text) => ProductCodeExact.IsMatch(text);

    #endregion

    #region Uninstall command

    /// <summary>
    /// Turns the entry's UninstallString into a program + arguments we can start directly.
    /// Windows Installer entries always become <c>msiexec /X{ProductCode}</c> (a registered
    /// <c>/I</c> only opens the repair/modify screen). Returns null with a reason when there is
    /// nothing runnable.
    /// </summary>
    public static UninstallCommand? GetUninstallCommand(
        UninstallEntry entry, out UninstallCommandProblem problem, out string missingPath)
    {
        problem = UninstallCommandProblem.None;
        missingPath = "";

        string? productCode = null;
        var raw = entry.UninstallString.Trim();

        if (raw.Length == 0)
        {
            if (entry.IsWindowsInstaller && IsProductCode(entry.KeyName))
                productCode = entry.KeyName;
        }
        else
        {
            var (file, arguments) = SplitCommandLine(raw);
            var fileName = Path.GetFileName(file);

            if (fileName.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            {
                var match = ProductCodeInText.Match(arguments);
                productCode = match.Success ? match.Value
                            : IsProductCode(entry.KeyName) ? entry.KeyName
                            : null;
            }
            else
            {
                var resolved = ResolveExecutable(file);
                if (resolved == null)
                {
                    problem = UninstallCommandProblem.UninstallerMissing;
                    // Without an extension the split was a guess — show the whole command instead
                    missingPath = Environment.ExpandEnvironmentVariables(Path.HasExtension(file) ? file : raw);
                    return null;
                }

                return new UninstallCommand { FileName = resolved, Arguments = arguments };
            }
        }

        if (productCode == null)
        {
            problem = UninstallCommandProblem.NoCommand;
            return null;
        }

        return new UninstallCommand
        {
            FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            Arguments = "/X" + productCode.ToUpperInvariant(),
            IsMsi = true,
        };
    }

    /// <summary>
    /// Splits a command line into program and arguments the way Windows would, without cmd.exe:
    /// a quoted program path; an unquoted path containing spaces up to its ".exe" (or .com/.bat/.cmd);
    /// otherwise the first word. Arguments are returned untouched.
    /// </summary>
    public static (string File, string Arguments) SplitCommandLine(string commandLine)
    {
        var s = commandLine.Trim();

        if (s.StartsWith('"'))
        {
            var close = s.IndexOf('"', 1);
            return close < 0
                ? (s.Trim('"').Trim(), "")
                : (s[1..close].Trim(), s[(close + 1)..].Trim());
        }

        // Unquoted path with spaces: take the first "...exe" that is a real file (a folder may be
        // named like "Tools.com x"); ignore extensions inside a later quoted argument, as in
        // RunDll32 C:\...\Ctor.dll,LaunchSetup "C:\...\setup.exe".
        var firstQuote = s.IndexOf('"');
        string? firstCandidate = null;
        foreach (Match m in ExecutableExtension.Matches(s))
        {
            if (firstQuote >= 0 && m.Index > firstQuote) break;

            var end = m.Index + m.Length;
            var candidate = s[..end].Trim();
            firstCandidate ??= candidate;
            if (File.Exists(Environment.ExpandEnvironmentVariables(candidate)))
                return (candidate, s[end..].Trim());
        }

        if (firstCandidate != null)
            return (firstCandidate, s[firstCandidate.Length..].Trim());

        // Unquoted path without extension ("C:\Program Files\App\uninst /S"): like Windows, try the
        // words as a path + ".exe" — longest first, so "C:\Program.exe" can never beat the real one.
        if (s.Length > 2 && s[1] == ':')
        {
            var end = firstQuote >= 0 ? firstQuote : s.Length;
            while (end > 2)
            {
                var candidate = s[..end].TrimEnd();
                if (File.Exists(Environment.ExpandEnvironmentVariables(candidate + ".exe")))
                    return (candidate + ".exe", s[end..].Trim());
                end = s.LastIndexOfAny([' ', '\t'], end - 1);
            }
        }

        var space = s.IndexOfAny([' ', '\t']);
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
    }

    /// <summary>
    /// Full path of an existing program. Bare names ("RunDll32", "winget") are looked up in the
    /// Windows folders first, then PATH — never relative to the current directory.
    /// </summary>
    private static string? ResolveExecutable(string file)
    {
        file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
        if (file.Length == 0) return null;

        if (Path.IsPathFullyQualified(file))
            return File.Exists(file) ? Path.GetFullPath(file) : null;

        // A relative path with folders would depend on the current directory — refuse it
        if (file.IndexOfAny(['\\', '/']) >= 0) return null;

        string[] names = Path.HasExtension(file) ? [file] : [file + ".exe", file + ".com"];
        var folders = new List<string>
        {
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var expanded = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"'));
            if (expanded.Length > 0 && Path.IsPathFullyQualified(expanded)) folders.Add(expanded);
        }

        foreach (var folder in folders)
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(folder, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // Malformed PATH entry — ignore
                }
            }
        }

        return null;
    }

    #endregion

    #region Launch & wait

    /// <summary>
    /// Starts the uninstaller with its normal UI. Machine-wide entries run with WinXTools' admin
    /// rights. Per-user entries live in HKCU, which any program the user runs can edit, so they are
    /// started with the signed-in user's normal (non-elevated) rights — a tampered entry can't borrow
    /// our elevation. The process is created suspended and put in a job object before it runs, so
    /// processes it spawns (NSIS/Inno uninstallers relaunch themselves from %TEMP%) are tracked too.
    /// </summary>
    /// <exception cref="UninstallLaunchException">The process could not be started.</exception>
    public static UninstallerProcess Launch(UninstallCommand command, bool asDesktopUser)
    {
        var application = command.FileName;
        var arguments = command.Arguments;

        var extension = Path.GetExtension(application);
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // Batch files need the command interpreter; /d skips AutoRun hooks
            arguments = $"/d /c \"\"{application}\"{(arguments.Length > 0 ? " " + arguments : "")}\"";
            application = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }

        var commandLine = new StringBuilder($"\"{application}\"{(arguments.Length > 0 ? " " + arguments : "")}");

        // Uninstallers expect to start in their own folder
        var workingDirectory = Path.GetDirectoryName(command.FileName);
        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
            workingDirectory = null;

        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        const uint flags = CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_DEFAULT_ERROR_MODE;

        PROCESS_INFORMATION info;
        bool started;
        if (asDesktopUser)
        {
            using var token = OpenDesktopUserToken();
            // Null environment = the user's own environment, not ours
            started = CreateProcessWithTokenW(token, 0, application, commandLine, flags,
                IntPtr.Zero, workingDirectory, ref startup, out info);
        }
        else
        {
            started = CreateProcess(application, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                IntPtr.Zero, workingDirectory, ref startup, out info);
        }

        if (!started)
        {
            var error = Marshal.GetLastPInvokeError();
            var reason = error == ERROR_ELEVATION_REQUIRED
                ? UninstallLaunchError.ElevationRequired
                // CreateProcessWithTokenW needs the Secondary Logon service ("optimizer" tools often disable it)
                : asDesktopUser && error is ERROR_SERVICE_START_HANG or ERROR_SERVICE_DISABLED
                                         or ERROR_SERVICE_DOES_NOT_EXIST or ERROR_PRIVILEGE_NOT_HELD
                    ? UninstallLaunchError.DesktopUserUnavailable
                    : UninstallLaunchError.Failed;
            throw new UninstallLaunchException(reason, error);
        }

        var process = new SafeProcessHandle(info.hProcess, ownsHandle: true);
        try
        {
            // No kill-on-close: the uninstaller keeps running even if WinXTools exits
            SafeJobHandle? job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid || !AssignProcessToJobObject(job, process))
            {
                job.Dispose();
                job = null;
            }

            if (ResumeThread(info.hThread) == uint.MaxValue)
            {
                var error = Marshal.GetLastPInvokeError();
                TerminateProcess(process, 1);
                job?.Dispose();
                throw new UninstallLaunchException(UninstallLaunchError.Failed, error);
            }

            // Let the uninstaller's window come to the front over ours
            AllowSetForegroundWindow(info.dwProcessId);
            return new UninstallerProcess(process, job, info.dwProcessId);
        }
        catch
        {
            process.Dispose();
            throw;
        }
        finally
        {
            CloseHandle(info.hThread);
        }
    }

    /// <summary>
    /// Primary token of the desktop shell (Explorer): the signed-in user without elevation.
    /// Refused when Explorer runs as a different account than WinXTools — HKCU would then
    /// belong to someone else than the desktop user.
    /// </summary>
    private static SafeAccessTokenHandle OpenDesktopUserToken()
    {
        var unavailable = new UninstallLaunchException(UninstallLaunchError.DesktopUserUnavailable, 0);

        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero) throw unavailable;
        GetWindowThreadProcessId(shellWindow, out var shellPid);
        if (shellPid == 0) throw unavailable;

        using var shellProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
        if (shellProcess.IsInvalid) throw unavailable;
        if (!OpenProcessToken(shellProcess, TOKEN_DUPLICATE | TOKEN_QUERY, out var shellToken)) throw unavailable;

        using (shellToken)
        {
            try
            {
                using var shellUser = new WindowsIdentity(shellToken.DangerousGetHandle());
                using var us = WindowsIdentity.GetCurrent();
                if (shellUser.User == null || shellUser.User != us.User) throw unavailable;
            }
            catch (UninstallLaunchException) { throw; }
            catch { throw unavailable; }

            const uint access = TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE |
                                TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
            if (!DuplicateTokenEx(shellToken, access, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
                throw unavailable;
            return primary;
        }
    }

    /// <summary>
    /// Waits until the uninstall is really over. Done when every process we started (the job) has
    /// exited, or — once the registration is gone — after a short grace period for processes it
    /// leaves open. If all processes exit while the program is still registered, msiexec's answer
    /// is final; other uninstallers may have handed over to a process we can't track (one that
    /// elevated itself), so we keep watching the registry and report
    /// <see cref="UninstallWaitStage.ClosedButStillRegistered"/> until the caller cancels.
    /// Cancelling only stops waiting — the uninstaller itself is never killed.
    /// </summary>
    public static async Task<UninstallWaitResult> WaitForUninstallerAsync(
        UninstallerProcess process, UninstallEntry entry, bool isMsi,
        IProgress<UninstallWaitStage>? stage, CancellationToken ct)
    {
        DateTime? goneSince = null;
        var reportedClosed = false;

        while (!ct.IsCancellationRequested)
        {
            var active = process.ActiveProcessCount;          // -1 = children not tracked
            var allExited = active >= 0 ? active == 0 : process.HasExited;

            if (!IsStillRegistered(entry))
            {
                goneSince ??= DateTime.UtcNow;
                if (allExited || DateTime.UtcNow - goneSince >= FinishGrace)
                    return UninstallWaitResult.Finished;
            }
            else if (allExited)
            {
                if (isMsi) return UninstallWaitResult.Finished;
                if (!reportedClosed)
                {
                    reportedClosed = true;
                    stage?.Report(UninstallWaitStage.ClosedButStillRegistered);
                }
            }

            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return UninstallWaitResult.StoppedByUser;
    }

    #endregion

    #region Leftovers

    /// <summary>
    /// Folders the uninstalled program left behind — exact matches only: its recorded
    /// InstallLocation, and folders named exactly like it (or Publisher\Name) in Program Files,
    /// Program Files (x86), ProgramData, AppData\Roaming and AppData\Local. Never returned:
    /// anything that is, contains or is inside a folder of a program that is still installed;
    /// system and shell folders; reparse points / links; network paths; and for per-user
    /// programs anything outside the user's own areas. Sizes are real (walked, links not
    /// followed). Call only after <see cref="IsStillRegistered"/> returned false.
    /// </summary>
    public static List<LeftoverFolder> FindLeftovers(UninstallEntry removed, CancellationToken ct)
    {
        var guard = LeftoverGuard.Create(removed);
        var candidates = new List<(string Path, LeftoverKind Kind)>();

        if (removed.InstallLocation.Length > 0)
            candidates.Add((removed.InstallLocation, LeftoverKind.InstallFolder));

        // Name matches are skipped when another registered program has the same name
        if (!guard.NameIsAmbiguous && TryCleanFolderName(removed.DisplayName, out var name))
        {
            var names = new List<string> { name };
            if (TryCleanFolderName(removed.Publisher, out var publisher))
                names.Add(Path.Combine(publisher, name));

            foreach (var (baseDir, kind) in SearchBases(removed.IsPerUser))
                foreach (var relative in names)
                    candidates.Add((Path.Combine(baseDir, relative), kind));
        }

        var found = new List<LeftoverFolder>();
        foreach (var (raw, kind) in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var path = NormalizeDirectory(raw);
            if (path == null || !Directory.Exists(path) || !guard.IsAllowed(path)) continue;

            // Keep only the outermost folder when one match sits inside another
            if (found.Any(f => SameOrInside(path, f.Path))) continue;
            found.RemoveAll(f => SameOrInside(f.Path, path));
            found.Add(new LeftoverFolder { Path = path, Kind = kind });
        }

        foreach (var folder in found)
            Measure(folder, ct);

        return found;
    }

    /// <summary>Folders searched for exact-name matches. Per-user programs: the user's AppData only.</summary>
    private static IEnumerable<(string Dir, LeftoverKind Kind)> SearchBases(bool perUser)
    {
        var bases = new List<(string Dir, LeftoverKind Kind)>();
        if (!perUser)
        {
            bases.Add((ProgramFiles64(), LeftoverKind.ProgramFiles));
            bases.Add((Folder(Environment.SpecialFolder.ProgramFilesX86), LeftoverKind.ProgramFiles));
            bases.Add((Folder(Environment.SpecialFolder.CommonApplicationData), LeftoverKind.ProgramData));
        }
        bases.Add((Folder(Environment.SpecialFolder.ApplicationData), LeftoverKind.RoamingAppData));
        bases.Add((Folder(Environment.SpecialFolder.LocalApplicationData), LeftoverKind.LocalAppData));

        return bases
            .Where(b => b.Dir.Length > 0)
            .DistinctBy(b => b.Dir, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A program/publisher name usable as exactly one folder name (no separators, no "..").</summary>
    private static bool TryCleanFolderName(string raw, out string name)
    {
        // Windows drops trailing dots and spaces from folder names
        name = raw.Trim().TrimEnd('.', ' ');
        return name.Length >= 2 &&
               name != ".." &&
               name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               !SharedFolderNames.Contains(name);
    }

    // Shared/system folder names that must never be treated as "the program's folder"
    private static readonly HashSet<string> SharedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Common Files", "WindowsApps", "ModifiableWindowsApps", "Packages",
        "Programs", "Temp", "Package Cache", "Microsoft.NET", "dotnet", "Reference Assemblies",
        "MSBuild", "Internet Explorer", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "Windows Mail", "Windows Media Player", "Windows NT", "Windows Photo Viewer",
        "Windows Portable Devices", "Windows Security", "Windows Sidebar", "WindowsPowerShell",
        "Uninstall Information", "InstallShield Installation Information", "Application Data",
        "Desktop", "Documents", "Start Menu", "Templates", "Favorites", "History",
        "Temporary Internet Files", "VirtualStore", "Publishers", "ssh", "regid.1991-06.com.microsoft",
        // Windows-owned / shared folders in AppData
        "CrashDumps", "D3DSCache", "ConnectedDevicesPlatform", "PlaceholderTileLogoFolder", "Comms",
        "PeerDistRepub", "SquirrelTemp", "Local", "LocalLow", "Roaming", "tmp",
    };

    /// <summary>Real size and file count; reparse points are not followed, unreadable parts are skipped.</summary>
    private static void Measure(LeftoverFolder folder, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,          // hidden/system files count too
            RecurseSubdirectories = false,
        };

        long bytes = 0;
        var files = 0;
        var pending = new Stack<string>();
        pending.Push(folder.Path);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                foreach (var item in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    var isLink = (item.Attributes & FileAttributes.ReparsePoint) != 0;
                    if (item is DirectoryInfo)
                    {
                        if (!isLink) pending.Push(item.FullName);
                    }
                    else if (item is FileInfo file)
                    {
                        files++;
                        if (!isLink) bytes += file.Length;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Unreadable subfolder — the size stays a lower bound
            }
        }

        folder.Bytes = bytes;
        folder.FileCount = files;
    }

    /// <summary>
    /// Safety rules for leftover folders, built from the registry as it is right now
    /// (after the uninstall), so every program that is still installed is protected.
    /// </summary>
    private sealed class LeftoverGuard
    {
        private readonly List<string> _systemRoots = new();    // may not be equal to or contain these
        private readonly List<string> _noGoAreas = new();      // may not be inside these
        private readonly List<string> _installedDirs = new();  // may not be equal to, contain or be inside these
        private readonly List<string> _machineAreas = new();   // off-limits for per-user programs
        private string _usersRoot = "";
        private string _profile = "";
        private bool _perUser;

        public bool NameIsAmbiguous { get; private set; }

        public static LeftoverGuard Create(UninstallEntry removed)
        {
            var guard = new LeftoverGuard { _perUser = removed.IsPerUser };
            guard.AddSystemFolders();

            foreach (var entry in ReadAllEntries())
            {
                if (entry.DisplayName.Equals(removed.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase))
                    guard.NameIsAmbiguous = true;

                guard.AddInstalledDir(entry.InstallLocation);
                guard.AddInstalledDir(FolderOfIcon(entry.DisplayIcon));
                if (entry.UninstallString.Length > 0)
                    guard.AddInstalledDir(FolderOfProgram(SplitCommandLine(entry.UninstallString).File));
            }

            // WinXTools' own folder
            guard.AddInstalledDir(AppContext.BaseDirectory);
            return guard;
        }

        public bool IsAllowed(string path)
        {
            if (!IsLocalFixedDrive(path) || HasReparsePointInChain(path) || !IsFinalPath(path))
                return false;

            // Is, or contains, a system/shell folder (drive root, Program Files, user profile...)
            if (_systemRoots.Any(root => SameOrInside(root, path))) return false;

            // Inside Windows, shared component stores, caches...
            if (_noGoAreas.Any(area => SameOrInside(path, area))) return false;

            // Another account's profile, Public, Default
            if (_usersRoot.Length > 0 && SameOrInside(path, _usersRoot) &&
                !(_profile.Length > 0 && SameOrInside(path, _profile)))
                return false;

            // Per-user registrations are user-editable: never let them point us at machine folders
            if (_perUser && _machineAreas.Any(area => SameOrInside(path, area))) return false;

            // Is, contains or is inside a folder of a program that is still installed
            return !_installedDirs.Any(dir => SameOrInside(path, dir) || SameOrInside(dir, path));
        }

        private void AddInstalledDir(string? raw)
        {
            var dir = NormalizeDirectory(raw);
            if (dir == null) return;

            // A bogus broad location (e.g. "C:\Program Files") would block everything — the
            // system-root rule already covers such folders.
            if (_systemRoots.Any(root => SameOrInside(root, dir))) return;
            _installedDirs.Add(dir);
        }

        private void AddSystemFolders()
        {
            void Root(string? p) { var n = NormalizeDirectory(p); if (n != null) _systemRoots.Add(n); }
            void NoGo(string? p) { var n = NormalizeDirectory(p); if (n != null) _noGoAreas.Add(n); }

            foreach (var drive in SafeFixedDrives())
            {
                Root(drive);
                foreach (var special in new[] { "$Recycle.Bin", "System Volume Information", "Recovery", "Config.Msi", "Boot", "EFI" })
                    NoGo(Path.Combine(drive, special));
            }

            foreach (var special in new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
                Environment.SpecialFolder.Favorites, Environment.SpecialFolder.StartMenu,
                Environment.SpecialFolder.Programs, Environment.SpecialFolder.Startup,
                Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.CommonPrograms,
                Environment.SpecialFolder.CommonStartup, Environment.SpecialFolder.CommonDesktopDirectory,
                Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.CommonMusic,
                Environment.SpecialFolder.CommonPictures, Environment.SpecialFolder.CommonVideos,
                Environment.SpecialFolder.Templates, Environment.SpecialFolder.CommonTemplates,
                Environment.SpecialFolder.Fonts, Environment.SpecialFolder.Cookies,
                Environment.SpecialFolder.History, Environment.SpecialFolder.InternetCache,
                Environment.SpecialFolder.Recent, Environment.SpecialFolder.SendTo,
                Environment.SpecialFolder.AdminTools, Environment.SpecialFolder.CommonAdminTools,
            })
            {
                Root(Folder(special));
            }

            foreach (var variable in new[] { "ProgramW6432", "CommonProgramW6432", "PUBLIC", "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
                Root(Environment.GetEnvironmentVariable(variable));

            var profile = Folder(Environment.SpecialFolder.UserProfile);
            var roaming = Folder(Environment.SpecialFolder.ApplicationData);
            var local = Folder(Environment.SpecialFolder.LocalApplicationData);
            var programData = Folder(Environment.SpecialFolder.CommonApplicationData);
            var windows = Folder(Environment.SpecialFolder.Windows);

            if (profile.Length > 0)
            {
                foreach (var sub in new[] { "AppData", @"AppData\LocalLow", "Downloads", "Saved Games", "Contacts", "Links", "Searches", "3D Objects", "OneDrive" })
                    Root(Path.Combine(profile, sub));
                Root(Path.GetDirectoryName(profile));   // C:\Users
                _profile = NormalizeDirectory(profile) ?? "";
                _usersRoot = NormalizeDirectory(Path.GetDirectoryName(profile)) ?? "";
            }

            Root(Path.GetTempPath());
            if (local.Length > 0)
            {
                Root(Path.Combine(local, "Programs"));
                Root(Path.Combine(local, "Temp"));
                NoGo(Path.Combine(local, "Microsoft"));
                NoGo(Path.Combine(local, "Packages"));
                NoGo(Path.Combine(local, "Package Cache"));
            }
            if (roaming.Length > 0) NoGo(Path.Combine(roaming, "Microsoft"));
            if (programData.Length > 0)
            {
                NoGo(Path.Combine(programData, "Microsoft"));
                NoGo(Path.Combine(programData, "Package Cache"));
                NoGo(Path.Combine(programData, "Packages"));
            }

            NoGo(windows);
            NoGo(Folder(Environment.SpecialFolder.CommonProgramFiles));
            NoGo(Folder(Environment.SpecialFolder.CommonProgramFilesX86));
            NoGo(Environment.GetEnvironmentVariable("CommonProgramW6432"));
            foreach (var pf in new[] { ProgramFiles64(), Folder(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (pf.Length == 0) continue;
                NoGo(Path.Combine(pf, "WindowsApps"));
                NoGo(Path.Combine(pf, "ModifiableWindowsApps"));
                NoGo(Path.Combine(pf, "Windows Defender"));
                NoGo(Path.Combine(pf, "Windows Defender Advanced Threat Protection"));
                NoGo(Path.Combine(pf, "Microsoft Update Health Tools"));
                _machineAreas.Add(NormalizeDirectory(pf) ?? pf);
            }
            if (programData.Length > 0) _machineAreas.Add(NormalizeDirectory(programData) ?? programData);
        }

        private static IEnumerable<string> SafeFixedDrives()
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { yield break; }

            foreach (var drive in drives)
            {
                bool isFixed;
                try { isFixed = drive.DriveType == DriveType.Fixed; }
                catch { continue; }
                if (isFixed) yield return drive.Name;
            }
        }

        /// <summary>Folder of a DisplayIcon value like <c>"C:\App\app.exe",0</c>.</summary>
        private static string? FolderOfIcon(string displayIcon)
        {
            var s = displayIcon.Trim();
            if (s.Length == 0) return null;
            if (s.StartsWith('"'))
            {
                var close = s.IndexOf('"', 1);
                s = close > 0 ? s[1..close] : s.Trim('"');
            }
            else
            {
                var comma = s.LastIndexOf(',');
                if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma];
            }
            return FolderOfProgram(s);
        }

        private static string? FolderOfProgram(string file)
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
                return Path.IsPathFullyQualified(expanded) ? Path.GetDirectoryName(expanded) : null;
            }
            catch
            {
                return null;
            }
        }
    }

    #endregion

    #region Recycle Bin

    /// <summary>
    /// Moves the chosen leftover folders to the Recycle Bin, one at a time on an STA thread (the
    /// shell's requirement). Each folder is re-checked against the safety rules right before it is
    /// touched. If Windows can't recycle a folder (too big, no Recycle Bin on that drive) it asks
    /// the user before deleting permanently. Cancelling stops before the next folder; folders not
    /// reached are simply not in the result.
    /// </summary>
    public static Task<List<RecycleResult>> MoveToRecycleBinAsync(
        UninstallEntry removed, IReadOnlyList<LeftoverFolder> folders,
        IProgress<(int Index, int Total, string Path)>? progress, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<List<RecycleResult>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                var guard = LeftoverGuard.Create(removed);
                var results = new List<RecycleResult>();

                for (var i = 0; i < folders.Count && !ct.IsCancellationRequested; i++)
                {
                    progress?.Report((i, folders.Count, folders[i].Path));
                    results.Add(MoveOne(folders[i], guard));
                }

                tcs.TrySetResult(results);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "WinXTools leftover recycle",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private static RecycleResult MoveOne(LeftoverFolder folder, LeftoverGuard guard)
    {
        var path = folder.Path;
        if (!Directory.Exists(path))
            return new RecycleResult { Folder = folder, Status = RecycleStatus.AlreadyGone };

        // The world may have changed since the scan (new install, folder swapped for a link...)
        if (!guard.IsAllowed(path))
            return new RecycleResult { Folder = folder, Status = RecycleStatus.Changed };

        var code = ShellRecycle(path, out var aborted);

        // Judge by the disk, not the return code
        if (!Directory.Exists(path))
            return new RecycleResult { Folder = folder, Status = RecycleStatus.Moved };

        var status = aborted || code is ERROR_CANCELLED or DE_OPCANCELLED
            ? RecycleStatus.Cancelled
            : code is 0 or ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION or DE_ACCESSDENIEDSRC
                ? RecycleStatus.InUseOrDenied
                : RecycleStatus.Failed;

        return new RecycleResult { Folder = folder, Status = status, ErrorCode = code };
    }

    /// <summary>
    /// SHFileOperation delete with undo (= Recycle Bin). Windows' own prompts stay enabled on
    /// purpose: "delete permanently?" when an item can't be recycled, and "file in use — try again /
    /// skip" so the user can close a program and retry. Our own progress dialog replaces the shell's.
    /// </summary>
    private static int ShellRecycle(string path, out bool aborted)
    {
        const ushort flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT;
        var from = path + "\0";   // list must end with a double null; the marshaler adds the second

        if (IntPtr.Size == 8)
        {
            var op = new SHFILEOPSTRUCT64 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
            var code = SHFileOperation64(ref op);
            aborted = op.fAnyOperationsAborted != 0;
            return code;
        }
        else
        {
            var op = new SHFILEOPSTRUCT32 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
            var code = SHFileOperation32(ref op);
            aborted = op.fAnyOperationsAborted != 0;
            return code;
        }
    }

    #endregion

    #region Path helpers

    /// <summary>
    /// Canonical local folder path (quotes and environment variables removed, "/" → "\", 8.3 names
    /// expanded, no trailing separator except on a drive root), or null for anything that isn't an
    /// absolute drive-letter path (relative, UNC/network...).
    /// </summary>
    private static string? NormalizeDirectory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = Environment.ExpandEnvironmentVariables(raw.Replace("\0", "").Trim().Trim('"').Trim());
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        if (s.Length < 3 || !char.IsAsciiLetter(s[0]) || s[1] != ':' || (s[2] != '\\' && s[2] != '/'))
            return null;

        try
        {
            s = Path.GetFullPath(s);
        }
        catch
        {
            return null;
        }

        if (s.Contains('~')) s = ToLongPath(s);
        s = s.TrimEnd('\\');
        return s.Length == 2 ? s + "\\" : s;
    }

    private static bool SameOrInside(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalFixedDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return root != null && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True if the folder or any parent is a junction/symlink/mount point (or can't be checked).</summary>
    private static bool HasReparsePointInChain(string path)
    {
        try
        {
            for (var dir = new DirectoryInfo(path); dir.Parent != null; dir = dir.Parent)
            {
                if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// The folder's real path (links, SUBST drives and mount points resolved) must be the path
    /// itself — otherwise the protection rules above would be checking the wrong location.
    /// </summary>
    private static bool IsFinalPath(string path)
    {
        using var handle = CreateFile(path, FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return false;

        var final = buffer.ToString();
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return false;
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];

        return string.Equals(final.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static string ToLongPath(string path)
    {
        try
        {
            var buffer = new StringBuilder(1024);
            var length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
        }
        catch
        {
            return path;
        }
    }

    private static string Folder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>64-bit Program Files even from a 32-bit process.</summary>
    private static string ProgramFiles64() =>
        Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } pf
            ? pf
            : Folder(Environment.SpecialFolder.ProgramFiles);

    #endregion

    #region Native

    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_DEFAULT_ERROR_MODE = 0x04000000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int ERROR_ELEVATION_REQUIRED = 740;
    private const int ERROR_SERVICE_START_HANG = 1053;
    private const int ERROR_SERVICE_DISABLED = 1058;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_PRIVILEGE_NOT_HELD = 1314;

    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;
    private const int ERROR_CANCELLED = 1223;
    private const int DE_OPCANCELLED = 0x75;
    private const int DE_ACCESSDENIEDSRC = 0x78;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    // shellapi.h packs SHFILEOPSTRUCT to 1 byte on 32-bit Windows only
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT64
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct SHFILEOPSTRUCT32
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle hToken, uint dwLogonFlags,
        string lpApplicationName, StringBuilder lpCommandLine, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle phNewToken);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, StringBuilder lpszFilePath,
        uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation64(ref SHFILEOPSTRUCT64 lpFileOp);

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 lpFileOp);

    #endregion
}

/// <summary>
/// A running uninstaller. When the job object could be attached, <see cref="ActiveProcessCount"/>
/// also counts every process it started. Disposing closes our handles only — it never stops the
/// uninstaller.
/// </summary>
public sealed class UninstallerProcess : IDisposable
{
    private readonly SafeProcessHandle _process;
    private readonly SafeJobHandle? _job;

    internal UninstallerProcess(SafeProcessHandle process, SafeJobHandle? job, int processId)
    {
        _process = process;
        _job = job;
        ProcessId = processId;
    }

    public int ProcessId { get; }

    public bool HasExited => WaitForSingleObject(_process, 0) == WAIT_OBJECT_0;

    /// <summary>Exit code of the process we started, or null while it is still running.</summary>
    public int? ExitCode => HasExited && GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : null;

    /// <summary>Processes still alive in the job (the uninstaller and its children); -1 when not tracked.</summary>
    public int ActiveProcessCount
    {
        get
        {
            if (_job == null) return -1;
            return QueryInformationJobObject(_job, JobObjectBasicAccountingInformation, out var info,
                       Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
                ? (int)info.ActiveProcesses
                : -1;
        }
    }

    public void Dispose()
    {
        _job?.Dispose();
        _process.Dispose();
    }

    private const uint WAIT_OBJECT_0 = 0;
    private const int JobObjectBasicAccountingInformation = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeJobHandle hJob, int jobObjectInfoClass,
        out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength, IntPtr lpReturnLength);
}

/// <summary>Owned handle to a job object.</summary>
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle() : base(true) { }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

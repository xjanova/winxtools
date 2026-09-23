using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace NetX.Core.System.Tweaks;

/// <summary>
/// Starting things from an elevated app without making them elevated too.
///
/// Settings pages, web links and documents are handed to the running Explorer
/// (medium integrity), so a browser never runs as Administrator. Admin tools
/// (MMC snap-ins, regedit…) are started directly by full path.
/// </summary>
public static class ShellLauncher
{
    private static readonly string[] AllowedSchemes =
    {
        "ms-settings:", "windowsdefender:", "ms-windows-store:", "https:"
    };

    private static string ExplorerPath => Path.Combine(CommandRunner.WindowsDirectory, "explorer.exe");

    /// <summary>Opens a URI (ms-settings:, https:, …) or an existing file through Explorer, un-elevated.</summary>
    public static TrickResult OpenUnelevated(string target)
    {
        bool isUri = AllowedSchemes.Any(s => target.StartsWith(s, StringComparison.OrdinalIgnoreCase));
        bool isShellFolder = target.StartsWith("shell:::{", StringComparison.OrdinalIgnoreCase) && target.EndsWith("}");
        bool isFile = !isUri && !isShellFolder && Path.IsPathFullyQualified(target) &&
                      (File.Exists(target) || Directory.Exists(target));
        if (!isUri && !isShellFolder && !isFile)
            return TrickResult.Fail(new LocText("This link can't be opened.", "เปิดลิงก์นี้ไม่ได้"));

        try
        {
            using var p = Process.Start(new ProcessStartInfo(ExplorerPath, CommandRunner.Quote(target))
            {
                UseShellExecute = false,
                WorkingDirectory = CommandRunner.WindowsDirectory
            });
            return TrickResult.Ok();
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(TweakErrors.Friendly(ex));
        }
    }

    /// <summary>Starts a Windows tool by full path (MMC consoles, regedit, perfmon…).</summary>
    public static TrickResult StartTool(string fullPath, string arguments = "")
    {
        if (!File.Exists(fullPath))
            return TrickResult.Fail(new LocText("This tool isn't available on this PC.", "เครื่องนี้ไม่มีเครื่องมือนี้"));
        try
        {
            using var p = Process.Start(new ProcessStartInfo(fullPath, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.SystemDirectory
            });
            return TrickResult.Ok();
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(TweakErrors.Friendly(ex));
        }
    }

    /// <summary>Opens an MMC console such as devmgmt.msc from System32.</summary>
    public static TrickResult StartConsole(string mscName) =>
        StartTool(CommandRunner.SystemTool("mmc.exe"), CommandRunner.Quote(CommandRunner.SystemTool(mscName)));

    /// <summary>Opens a Control Panel applet such as ncpa.cpl.</summary>
    public static TrickResult StartControlPanel(string cplName) =>
        StartTool(CommandRunner.SystemTool("control.exe"), cplName);
}

/// <summary>
/// Restarts the Windows shell safely:
/// 1. asks Explorer to exit cleanly (the same "Exit Explorer" message the
///    taskbar's Ctrl+Shift+right-click menu sends) instead of taskkill /f;
/// 2. starts the new Explorer with the ORIGINAL shell's un-elevated token, so the
///    desktop/taskbar never ends up running as Administrator.
/// </summary>
public static class ExplorerRestarter
{
    public static TrickResult Restart()
    {
        IntPtr shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero || GetWindowThreadProcessId(shellWindow, out uint pid) == 0 || pid == 0)
            return TrickResult.Fail(ManualHint);

        Process shell;
        try
        {
            shell = Process.GetProcessById((int)pid);
            if (!shell.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                return TrickResult.Fail(new LocText(
                    "Your desktop isn't run by Windows Explorer, so it can't be restarted here.",
                    "เดสก์ท็อปของคุณไม่ได้ใช้ Windows Explorer จึงรีสตาร์ทจากที่นี่ไม่ได้"));
        }
        catch
        {
            return TrickResult.Fail(ManualHint);
        }

        // CreateProcessWithTokenW goes through the Secondary Logon service; check
        // BEFORE closing Explorer so we never leave the user without a taskbar.
        if (Reg.ReadDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\seclogon", "Start") == 4)
            return TrickResult.Fail(new LocText(
                "The Secondary Logon service is disabled, so Explorer can't be restarted safely. Sign out and back in instead.",
                "บริการ Secondary Logon ถูกปิดอยู่ จึงรีสตาร์ท Explorer อย่างปลอดภัยไม่ได้ — กรุณาออกจากระบบแล้วเข้าใหม่แทน"));

        IntPtr token = IntPtr.Zero, primary = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            using (shell)
            {
                IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProcess == IntPtr.Zero) return TrickResult.Fail(ManualHint);
                try
                {
                    if (!OpenProcessToken(hProcess, TOKEN_DUPLICATE | TOKEN_QUERY, out token))
                        return TrickResult.Fail(ManualHint);
                }
                finally
                {
                    CloseHandle(hProcess);
                }

                if (!DuplicateTokenEx(token, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                    return TrickResult.Fail(ManualHint);

                // Ask the shell to exit cleanly; only force it if it ignores us.
                IntPtr tray = FindWindowW("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero) PostMessageW(tray, WM_EXIT_EXPLORER, IntPtr.Zero, IntPtr.Zero);
                if (!shell.WaitForExit(8000))
                {
                    try { shell.Kill(); } catch { }
                    shell.WaitForExit(5000);
                }
            }

            if (!CreateEnvironmentBlock(out env, primary, false)) env = IntPtr.Zero;

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            string explorer = Path.Combine(CommandRunner.WindowsDirectory, "explorer.exe");
            if (!CreateProcessWithTokenW(primary, 0, explorer, null,
                    env != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0, env,
                    CommandRunner.WindowsDirectory, ref si, out var pi))
            {
                Debug.WriteLine($"CreateProcessWithTokenW failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                return TrickResult.Fail(ManualHint);
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);

            // Only report success once the taskbar is really back.
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero)
                    return TrickResult.Ok(new LocText("Explorer was restarted.", "รีสตาร์ท Explorer แล้ว"));
                Thread.Sleep(250);
            }
            return TrickResult.Fail(ManualHint);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Explorer restart failed: {ex.Message}");
            return TrickResult.Fail(ManualHint);
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static readonly LocText ManualHint = new(
        "Explorer could not be restarted automatically. If the taskbar is missing, press Ctrl+Shift+Esc, choose \"Run new task\", type explorer and press Enter.",
        "รีสตาร์ท Explorer อัตโนมัติไม่สำเร็จ ถ้าแถบงานหายไป ให้กด Ctrl+Shift+Esc เลือก \"เรียกใช้งานใหม่\" พิมพ์ explorer แล้วกด Enter");

    #region Native

    private const uint WM_EXIT_EXPLORER = 0x0400 + 436; // WM_USER + 436
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint access, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string applicationName,
        string? commandLine, uint creationFlags, IntPtr environment, string currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    #endregion
}

/// <summary>Who owns the desktop this elevated app is shown on.</summary>
public static class SessionInfo
{
    /// <summary>
    /// True when the app was elevated with a DIFFERENT account than the one
    /// signed in to the desktop (e.g. a standard user typed an admin password).
    /// Per-user (HKCU) tricks would then change the admin account, not the user's.
    /// </summary>
    public static bool IsElevatedAsOtherUser(out string? desktopUser)
    {
        desktopUser = null;
        IntPtr token = IntPtr.Zero;
        try
        {
            IntPtr shellWindow = GetShellWindow();
            if (shellWindow == IntPtr.Zero || GetWindowThreadProcessId(shellWindow, out uint pid) == 0 || pid == 0)
                return false;

            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return false;
            try
            {
                if (!OpenProcessToken(hProcess, TOKEN_QUERY | TOKEN_DUPLICATE, out token)) return false;
            }
            finally
            {
                CloseHandle(hProcess);
            }

            using var shellIdentity = new WindowsIdentity(token);
            using var me = WindowsIdentity.GetCurrent();
            desktopUser = shellIdentity.Name;
            return shellIdentity.User != null && me.User != null && shellIdentity.User != me.User;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
}

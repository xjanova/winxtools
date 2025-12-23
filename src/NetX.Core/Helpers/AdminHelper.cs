using System.Diagnostics;
using System.Security.Principal;

namespace NetX.Core.Helpers;

/// <summary>
/// Helper class for administrator privilege operations
/// </summary>
public static class AdminHelper
{
    /// <summary>
    /// Check if current process is running with administrator privileges
    /// </summary>
    public static bool IsRunAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Restart current application with administrator privileges
    /// </summary>
    public static void RestartAsAdmin()
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName,
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(processInfo);
            Environment.Exit(0);
        }
        catch (Exception)
        {
            // User cancelled UAC or other error
        }
    }

    /// <summary>
    /// Set process priority to High
    /// </summary>
    public static bool SetHighPriority()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.High;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Set process priority to Realtime (requires admin)
    /// </summary>
    public static bool SetRealtimePriority()
    {
        try
        {
            if (!IsRunAsAdmin()) return false;

            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.RealTime;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

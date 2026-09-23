using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;

namespace NetX.Core.System.Tweaks;

/// <summary>
/// Service start-type changes done in-process through the Service Control
/// Manager (no "sc stop X &amp;&amp; sc config X" chains that stop half-way when the
/// service is already stopped).
/// </summary>
public static class ServiceTweak
{
    public sealed record Config(ServiceStartMode StartMode, bool Delayed);

    private static readonly TimeSpan StopStartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Current start type, or null when the service doesn't exist.</summary>
    public static Config? Query(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            var mode = sc.StartType; // throws when the service is missing
            bool delayed = Reg.ReadDword(RegistryHive.LocalMachine,
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}", "DelayedAutostart") is int d && d != 0;
            return new Config(mode, delayed);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Disabled → Applied, the given default → Default, anything else → Custom.</summary>
    public static TrickStatus Detect(string serviceName, ServiceStartMode defaultMode)
    {
        var cfg = Query(serviceName);
        if (cfg == null)
            return TrickStatus.NotSupported(new LocText("This service isn't installed.", "ไม่มีบริการนี้ในเครื่อง"));
        if (cfg.StartMode == ServiceStartMode.Disabled) return TrickStatus.Applied();
        if (cfg.StartMode == defaultMode) return TrickStatus.Default();
        return TrickStatus.Custom(new LocText($"Start type: {cfg.StartMode}", $"รูปแบบการเริ่ม: {cfg.StartMode}"));
    }

    /// <summary>Stops the service (if running) and sets it to Disabled. Works when it is already stopped.</summary>
    public static TrickResult Disable(string serviceName)
    {
        try
        {
            if (Query(serviceName) == null)
                return TrickResult.Fail(new LocText("This service isn't installed.", "ไม่มีบริการนี้ในเครื่อง"));

            // Disable first so nothing restarts it while it is stopping.
            SetStartMode(serviceName, ServiceStartMode.Disabled, delayed: null);
            bool stopped = StopIfRunning(serviceName);

            var cfg = Query(serviceName);
            if (cfg?.StartMode != ServiceStartMode.Disabled)
                return TrickResult.Fail(TweakErrors.NotVerified);

            return stopped
                ? TrickResult.Ok(new LocText("Done — the service is stopped and disabled.", "เรียบร้อย — หยุดและปิดบริการแล้ว"))
                : TrickResult.Ok(new LocText(
                    "Disabled. The service didn't stop right away; it won't start again after a restart.",
                    "ปิดแล้ว แต่บริการยังไม่หยุดทันที — หลังรีสตาร์ทเครื่องจะไม่ทำงานอีก"), RestartScope.Reboot);
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(TweakErrors.Friendly(ex));
        }
    }

    /// <summary>Sets the Windows default start type and starts the service.</summary>
    public static TrickResult Restore(string serviceName, ServiceStartMode mode, bool delayed)
    {
        try
        {
            if (Query(serviceName) == null)
                return TrickResult.Fail(new LocText("This service isn't installed.", "ไม่มีบริการนี้ในเครื่อง"));

            SetStartMode(serviceName, mode, mode == ServiceStartMode.Automatic ? delayed : null);
            bool started = mode != ServiceStartMode.Automatic || StartIfStopped(serviceName);

            var cfg = Query(serviceName);
            if (cfg?.StartMode != mode)
                return TrickResult.Fail(TweakErrors.NotVerified);

            return started
                ? TrickResult.Ok(TweakErrors.RestoredText)
                : TrickResult.Ok(new LocText(
                    "Default start type restored. The service will start after a restart.",
                    "คืนค่าเริ่มต้นแล้ว บริการจะเริ่มทำงานหลังรีสตาร์ทเครื่อง"), RestartScope.Reboot);
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(TweakErrors.Friendly(ex));
        }
    }

    public static bool StopIfRunning(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Stopped) return true;
            if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, StopStartTimeout);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool StartIfStopped(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Running) return true;
            if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, StopStartTimeout);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <param name="delayed">null = leave the delayed-start flag alone.</param>
    public static void SetStartMode(string serviceName, ServiceStartMode mode, bool? delayed)
    {
        uint startType = mode switch
        {
            ServiceStartMode.Automatic => SERVICE_AUTO_START,
            ServiceStartMode.Manual => SERVICE_DEMAND_START,
            ServiceStartMode.Disabled => SERVICE_DISABLED,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr svc = OpenServiceW(scm, serviceName, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
            if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!ChangeServiceConfigW(svc, SERVICE_NO_CHANGE, startType, SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                if (delayed.HasValue)
                {
                    var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayed.Value ? 1 : 0 };
                    if (!ChangeServiceConfig2W(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    #region Native

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_AUTO_START = 2;
    private const uint SERVICE_DEMAND_START = 3;
    private const uint SERVICE_DISABLED = 4;
    private const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DELAYED_AUTO_START_INFO
    {
        public int fDelayedAutostart;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType,
        uint errorControl, string? binaryPathName, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, ref SERVICE_DELAYED_AUTO_START_INFO info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    #endregion
}

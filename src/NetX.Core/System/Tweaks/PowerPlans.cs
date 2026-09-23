using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NetX.Core.System.Tweaks;

/// <summary>
/// Power plans through powrprof.dll (no powercfg text parsing).
///
/// The app keeps ONE copy of the hidden "Ultimate Performance" template under a
/// fixed GUID, so applying it again never piles up duplicate plans (the old
/// "powercfg -duplicatescheme" call created a new random plan on every click).
/// </summary>
public static class PowerPlans
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>Fixed GUID of WinXTools' own Ultimate Performance plan.</summary>
    public static readonly Guid AppUltimate = new("c7e3a5d1-4b6f-4e8a-9d2c-5f1b8a7e6c34");

    public const string AppUltimateName = "Ultimate Performance (WinXTools)";

    /// <summary>Backup written by the Tricks page's "Ultimate Performance" card (previous plan).</summary>
    public const string TricksPlanStateFile = "ultimate_plan.json";

    /// <summary>True while the Tricks page still owns the app plan (its Restore hasn't run yet).</summary>
    public static bool AppPlanNeededByTricks()
    {
        try
        {
            return File.Exists(Path.Combine(SecureAppData.GetDirectory(), TricksPlanStateFile));
        }
        catch
        {
            return true; // when unsure, keep the plan
        }
    }

    public static Guid? GetActive()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStructure<Guid>(ptr); }
            finally { LocalFree(ptr); }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PowerGetActiveScheme failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Activates a plan and reads it back.</summary>
    public static bool SetActive(Guid scheme)
    {
        try
        {
            var g = scheme;
            return PowerSetActiveScheme(IntPtr.Zero, ref g) == 0 && GetActive() == scheme;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PowerSetActiveScheme failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Plans the user can see/select (hidden templates are not listed).</summary>
    public static List<Guid> List()
    {
        var result = new List<Guid>();
        try
        {
            var buffer = new byte[16];
            for (uint index = 0; ; index++)
            {
                uint size = (uint)buffer.Length;
                uint rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, index, buffer, ref size);
                if (rc != 0) break; // ERROR_NO_MORE_ITEMS
                result.Add(new Guid(buffer));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PowerEnumerate failed: {ex.Message}");
        }
        return result;
    }

    public static bool Exists(Guid scheme) => List().Contains(scheme);

    public static string? GetName(Guid scheme)
    {
        try
        {
            var g = scheme;
            uint size = 0;
            if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0)
                return null;
            var buffer = new byte[size];
            if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
                return null;
            return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Creates the app's Ultimate Performance plan once (fixed GUID). Returns true when it exists.</summary>
    public static bool EnsureAppUltimatePlan()
    {
        if (Exists(AppUltimate)) return true;

        IntPtr dest = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.StructureToPtr(AppUltimate, dest, false);
            var source = UltimateTemplate;
            var destPtr = dest;
            // A non-null destination pointer makes powrprof use OUR GUID for the copy.
            uint rc = PowerDuplicateScheme(IntPtr.Zero, ref source, ref destPtr);
            if (rc != 0)
            {
                Debug.WriteLine($"PowerDuplicateScheme failed: {rc}");
                return false;
            }
            if (destPtr != dest && destPtr != IntPtr.Zero)
                LocalFree(destPtr); // defensive: API allocated its own GUID

            try
            {
                var g = AppUltimate;
                var name = Encoding.Unicode.GetBytes(AppUltimateName + "\0");
                PowerWriteFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, name, (uint)name.Length);
            }
            catch { /* cosmetic */ }

            return Exists(AppUltimate);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"EnsureAppUltimatePlan failed: {ex.Message}");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(dest);
        }
    }

    /// <summary>Deletes the app's plan if it exists and is not the active one.</summary>
    public static bool DeleteAppUltimatePlanIfUnused()
    {
        try
        {
            if (!Exists(AppUltimate)) return true;
            if (GetActive() == AppUltimate) return false;
            var g = AppUltimate;
            return PowerDeleteScheme(IntPtr.Zero, ref g) == 0;
        }
        catch
        {
            return false;
        }
    }

    #region Hardware capabilities

    private static byte[]? _caps;

    private static byte[]? Capabilities()
    {
        if (_caps != null) return _caps;
        try
        {
            var buffer = new byte[SYSTEM_POWER_CAPABILITIES_SIZE];
            if (CallNtPowerInformation(SystemPowerCapabilities, IntPtr.Zero, 0, buffer, (uint)buffer.Length) == 0)
                _caps = buffer;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CallNtPowerInformation failed: {ex.Message}");
        }
        return _caps;
    }

    /// <summary>
    /// Modern Standby (S0 low-power idle, "AoAc"). These PCs only offer the
    /// Balanced plan; High/Ultimate Performance don't apply.
    /// </summary>
    public static bool IsModernStandby => Capabilities() is { } c && c[OffsetAoAc] != 0;

    /// <summary>True on laptops/tablets (a system battery is present).</summary>
    public static bool HasBattery => Capabilities() is { } c && c[OffsetSystemBatteriesPresent] != 0;

    #endregion

    #region Native

    private const uint ACCESS_SCHEME = 16;
    private const int SystemPowerCapabilities = 4;

    // SYSTEM_POWER_CAPABILITIES (winnt.h): BOOLEAN AoAc at byte 20,
    // BOOLEAN SystemBatteriesPresent at byte 30, total size 76 bytes.
    private const int SYSTEM_POWER_CAPABILITIES_SIZE = 76;
    private const int OffsetAoAc = 20;
    private const int OffsetSystemBatteriesPresent = 30;

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr rootPowerKey, ref Guid sourceSchemeGuid, ref IntPtr destinationSchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDeleteScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        uint accessFlags, uint index, byte[] buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid, byte[] buffer, uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferLength,
        byte[] outputBuffer, uint outputBufferLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    #endregion
}

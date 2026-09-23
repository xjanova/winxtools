using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetX.Core.System.Tweaks;

/// <summary>
/// Settings that Windows keeps in memory as well as in the registry. Writing
/// the registry alone would not take effect until the next sign-in, so these go
/// through SystemParametersInfo (which updates both and notifies running apps).
/// </summary>
public static class NativeSettings
{
    #region Accessibility shortcuts (Sticky / Filter / Toggle Keys)

    /// <summary>The "press Shift 5 times" style shortcut (SKF/FKF/TKF_HOTKEYACTIVE).</summary>
    private const uint HotkeyActive = 0x00000004;

    /// <summary>Ask before turning the feature on via the shortcut (SKF/FKF/TKF_CONFIRMHOTKEY).</summary>
    private const uint ConfirmHotkey = 0x00000008;

    public sealed record AccessibilityFlags(uint Sticky, uint Filter, uint Toggle);

    public static AccessibilityFlags GetAccessibilityFlags()
    {
        var sk = new STICKYKEYS { cbSize = (uint)Marshal.SizeOf<STICKYKEYS>() };
        var fk = new FILTERKEYS { cbSize = (uint)Marshal.SizeOf<FILTERKEYS>() };
        var tk = new TOGGLEKEYS { cbSize = (uint)Marshal.SizeOf<TOGGLEKEYS>() };
        Check(SystemParametersInfoW(SPI_GETSTICKYKEYS, sk.cbSize, ref sk, 0));
        Check(SystemParametersInfoW(SPI_GETFILTERKEYS, fk.cbSize, ref fk, 0));
        Check(SystemParametersInfoW(SPI_GETTOGGLEKEYS, tk.cbSize, ref tk, 0));
        return new AccessibilityFlags(sk.dwFlags, fk.dwFlags, tk.dwFlags);
    }

    /// <summary>
    /// Applied = no shortcut prompts for any of the three, both saved (registry)
    /// and live; Default = all three shortcuts on. Games often switch the
    /// shortcuts off only in memory while they run (Microsoft's recommended
    /// pattern) — that is reported as the default with a note, not as applied.
    /// </summary>
    public static TrickStatus DetectAccessibilityShortcuts()
    {
        var live = GetAccessibilityFlags();
        int liveActive = new[] { live.Sticky, live.Filter, live.Toggle }.Count(x => (x & HotkeyActive) != 0);

        int?[] saved =
        {
            SavedFlags(@"Control Panel\Accessibility\StickyKeys"),
            SavedFlags(@"Control Panel\Accessibility\Keyboard Response"),
            SavedFlags(@"Control Panel\Accessibility\ToggleKeys")
        };
        if (saved.Any(s => s == null))
        {
            // Can't read the saved values: go by the live state only.
            return liveActive switch { 0 => TrickStatus.Applied(), 3 => TrickStatus.Default(), _ => TrickStatus.Custom() };
        }
        int savedActive = saved.Count(s => (s!.Value & HotkeyActive) != 0);

        if (savedActive == 0 && liveActive == 0) return TrickStatus.Applied();
        if (savedActive == 3 && liveActive == 3) return TrickStatus.Default();
        if (savedActive == 3 && liveActive == 0)
            return TrickStatus.Default(new LocText(
                "A running app (often a game) has turned the shortcuts off for now; they come back when it closes.",
                "มีแอปที่เปิดอยู่ (มักเป็นเกม) ปิดปุ่มลัดไว้ชั่วคราว และจะกลับมาเมื่อปิดแอปนั้น"));
        return TrickStatus.Custom();
    }

    private static int? SavedFlags(string path) =>
        Reg.Read(Microsoft.Win32.RegistryHive.CurrentUser, path, "Flags") switch
        {
            string s when int.TryParse(s, out var v) => v,
            int i => i,
            _ => null
        };

    /// <summary>
    /// Turns the keyboard shortcuts on/off; every other accessibility flag is
    /// kept. Starts from the SAVED flags (a game may have changed the live ones
    /// temporarily); restoring also brings back the Windows default "ask first"
    /// confirmation (…_CONFIRMHOTKEY).
    /// </summary>
    public static void SetAccessibilityShortcuts(bool enabled)
    {
        var sk = new STICKYKEYS { cbSize = (uint)Marshal.SizeOf<STICKYKEYS>() };
        var fk = new FILTERKEYS { cbSize = (uint)Marshal.SizeOf<FILTERKEYS>() };
        var tk = new TOGGLEKEYS { cbSize = (uint)Marshal.SizeOf<TOGGLEKEYS>() };
        Check(SystemParametersInfoW(SPI_GETSTICKYKEYS, sk.cbSize, ref sk, 0));
        Check(SystemParametersInfoW(SPI_GETFILTERKEYS, fk.cbSize, ref fk, 0));
        Check(SystemParametersInfoW(SPI_GETTOGGLEKEYS, tk.cbSize, ref tk, 0));

        uint Next(uint live, string savedPath)
        {
            uint flags = SavedFlags(savedPath) is int saved ? unchecked((uint)saved) : live;
            return enabled ? flags | HotkeyActive | ConfirmHotkey : flags & ~HotkeyActive;
        }

        sk.dwFlags = Next(sk.dwFlags, @"Control Panel\Accessibility\StickyKeys");
        fk.dwFlags = Next(fk.dwFlags, @"Control Panel\Accessibility\Keyboard Response");
        tk.dwFlags = Next(tk.dwFlags, @"Control Panel\Accessibility\ToggleKeys");

        Check(SystemParametersInfoW(SPI_SETSTICKYKEYS, sk.cbSize, ref sk, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));
        Check(SystemParametersInfoW(SPI_SETFILTERKEYS, fk.cbSize, ref fk, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));
        Check(SystemParametersInfoW(SPI_SETTOGGLEKEYS, tk.cbSize, ref tk, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));
    }

    #endregion

    #region Mouse ("Enhance pointer precision")

    /// <summary>[threshold1, threshold2, acceleration]; Windows default is 6, 10, 1.</summary>
    public static int[] GetMouse()
    {
        var values = new int[3];
        Check(SystemParametersInfoW(SPI_GETMOUSE, 0, values, 0));
        return values;
    }

    public static void SetMouse(int threshold1, int threshold2, int acceleration)
    {
        var values = new[] { threshold1, threshold2, acceleration };
        Check(SystemParametersInfoW(SPI_SETMOUSE, 0, values, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));
    }

    #endregion

    #region Animations

    public static bool GetClientAreaAnimation()
    {
        int value = 0;
        Check(SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, ref value, 0));
        return value != 0;
    }

    public static void SetClientAreaAnimation(bool enabled) =>
        Check(SystemParametersInfoW(SPI_SETCLIENTAREAANIMATION, 0, (IntPtr)(enabled ? 1 : 0),
            SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));

    /// <summary>Window minimize/restore animation.</summary>
    public static bool GetMinimizeAnimation()
    {
        var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
        Check(SystemParametersInfoW(SPI_GETANIMATION, info.cbSize, ref info, 0));
        return info.iMinAnimate != 0;
    }

    public static void SetMinimizeAnimation(bool enabled)
    {
        var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = enabled ? 1 : 0 };
        Check(SystemParametersInfoW(SPI_SETANIMATION, info.cbSize, ref info, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));
    }

    #endregion

    #region Broadcast

    /// <summary>
    /// Tells running apps and the shell that a setting changed (e.g.
    /// "ImmersiveColorSet" after switching dark mode / transparency).
    /// </summary>
    public static void BroadcastSettingChange(string area)
    {
        try
        {
            SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, area,
                SMTO_ABORTIFHUNG, 2000, out _);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WM_SETTINGCHANGE broadcast failed: {ex.Message}");
        }
    }

    #endregion

    #region Compact OS (WOF)

    /// <summary>
    /// True when core Windows files are WOF-compressed (Compact OS), false when
    /// they are not, null when it can't be told.
    /// </summary>
    public static bool? IsCompactOs()
    {
        var sys = Environment.SystemDirectory;
        var probes = new[]
        {
            Path.Combine(sys, "kernel32.dll"),
            Path.Combine(sys, "shell32.dll"),
            Path.Combine(CommandRunner.WindowsDirectory, "explorer.exe")
        };

        int compressed = 0, checkedFiles = 0;
        foreach (var file in probes)
        {
            try
            {
                uint len = 0;
                if (WofIsExternalFile(file, out int external, out uint provider, IntPtr.Zero, ref len) != 0) continue;
                checkedFiles++;
                if (external != 0 && provider == WOF_PROVIDER_FILE) compressed++;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WofIsExternalFile failed: {ex.Message}");
            }
        }

        if (checkedFiles == 0) return null;
        if (compressed * 2 > checkedFiles) return true;
        if (compressed == 0) return false;
        return null;
    }

    #endregion

    #region GPU scheduling (HAGS) capability

    public sealed record HagsInfo(bool Supported, bool Enabled, bool EnabledByDefault);

    /// <summary>
    /// Asks the graphics kernel whether any GPU supports hardware-accelerated
    /// GPU scheduling and whether it is on for this boot (WDDM 2.7 caps).
    /// Null when the query is not available.
    /// </summary>
    public static HagsInfo? QueryHags()
    {
        try
        {
            var enumArgs = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref enumArgs) != 0 || enumArgs.NumAdapters == 0) return null;

            int size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            IntPtr buffer = Marshal.AllocHGlobal(size * (int)enumArgs.NumAdapters);
            try
            {
                enumArgs.pAdapters = buffer;
                if (D3DKMTEnumAdapters2(ref enumArgs) != 0) return null;

                bool supported = false, enabled = false, byDefault = false;
                for (int i = 0; i < enumArgs.NumAdapters; i++)
                {
                    var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buffer + i * size);
                    IntPtr caps = Marshal.AllocHGlobal(4);
                    try
                    {
                        Marshal.WriteInt32(caps, 0);
                        var query = new D3DKMT_QUERYADAPTERINFO
                        {
                            hAdapter = info.hAdapter,
                            Type = KMTQAITYPE_WDDM_2_7_CAPS,
                            pPrivateDriverData = caps,
                            PrivateDriverDataSize = 4
                        };
                        if (D3DKMTQueryAdapterInfo(ref query) == 0)
                        {
                            int v = Marshal.ReadInt32(caps);
                            if ((v & 1) != 0)
                            {
                                supported = true;
                                enabled |= (v & 2) != 0;
                                byDefault |= (v & 4) != 0;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(caps);
                        var close = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                        D3DKMTCloseAdapter(ref close);
                    }
                }
                return new HagsInfo(supported, enabled, byDefault);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"HAGS query failed: {ex.Message}");
            return null;
        }
    }

    #endregion

    private static void Check(bool ok)
    {
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    #region Native

    private const uint SPI_GETMOUSE = 0x0003;
    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPI_GETFILTERKEYS = 0x0032;
    private const uint SPI_SETFILTERKEYS = 0x0033;
    private const uint SPI_GETTOGGLEKEYS = 0x0034;
    private const uint SPI_SETTOGGLEKEYS = 0x0035;
    private const uint SPI_GETSTICKYKEYS = 0x003A;
    private const uint SPI_SETSTICKYKEYS = 0x003B;
    private const uint SPI_GETANIMATION = 0x0048;
    private const uint SPI_SETANIMATION = 0x0049;
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    private const uint SPI_SETCLIENTAREAANIMATION = 0x1043;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private const uint WOF_PROVIDER_FILE = 2;
    private const int KMTQAITYPE_WDDM_2_7_CAPS = 70;

    [StructLayout(LayoutKind.Sequential)]
    private struct STICKYKEYS { public uint cbSize; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOGGLEKEYS { public uint cbSize; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILTERKEYS
    {
        public uint cbSize;
        public uint dwFlags;
        public uint iWaitMSec;
        public uint iDelayMSec;
        public uint iRepeatMSec;
        public uint iBounceMSec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO { public uint cbSize; public int iMinAnimate; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref STICKYKEYS value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref FILTERKEYS value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref TOGGLEKEYS value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref ANIMATIONINFO value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, int[] value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref int value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, IntPtr value, uint winIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("wofutil.dll", CharSet = CharSet.Unicode)]
    private static extern int WofIsExternalFile(string filePath, out int isExternalFile, out uint provider,
        IntPtr externalFileInfo, ref uint bufferLength);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);

    #endregion
}

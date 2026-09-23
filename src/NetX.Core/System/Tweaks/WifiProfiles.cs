using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace NetX.Core.System.Tweaks;

/// <summary>
/// Saved Wi-Fi networks and their passwords, read in-process through the
/// Native Wi-Fi API (WlanGetProfile with WLAN_PROFILE_GET_PLAINTEXT_KEY, which
/// requires Administrator). Nothing is written to disk, and network names with
/// spaces or Thai characters work — unlike the old "netsh | findstr" pipeline.
/// </summary>
public static class WifiProfiles
{
    public sealed record Profile(string Name, string Ssid, string Security, string? Password, bool IsEnterprise);

    /// <summary>Builds the report text shown in the output dialog.</summary>
    public static TrickResult BuildReport(bool thai)
    {
        uint rc = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (rc == ERROR_SERVICE_NOT_ACTIVE || rc == ERROR_SERVICE_DISABLED)
            return TrickResult.Fail(new LocText(
                "This PC has no Wi-Fi (the WLAN AutoConfig service isn't running).",
                "เครื่องนี้ไม่มี Wi-Fi (บริการ WLAN AutoConfig ไม่ได้ทำงาน)"));
        if (rc != 0)
            return TrickResult.Fail(new LocText($"Could not open the Wi-Fi service (error {rc}).", $"เปิดบริการ Wi-Fi ไม่ได้ (รหัส {rc})"));

        var profiles = new List<Profile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool accessDenied = false;
        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var ifList) != 0)
                return TrickResult.Fail(new LocText("Could not list Wi-Fi adapters.", "อ่านรายการการ์ด Wi-Fi ไม่ได้"));
            try
            {
                int count = Marshal.ReadInt32(ifList);
                for (int i = 0; i < count; i++)
                {
                    // WLAN_INTERFACE_INFO_LIST: 2 DWORDs, then WLAN_INTERFACE_INFO[532 bytes] (GUID first).
                    var ifGuid = Marshal.PtrToStructure<Guid>(ifList + 8 + i * 532);
                    if (WlanGetProfileList(handle, ref ifGuid, IntPtr.Zero, out var profileList) != 0) continue;
                    try
                    {
                        int pCount = Marshal.ReadInt32(profileList);
                        for (int p = 0; p < pCount; p++)
                        {
                            // WLAN_PROFILE_INFO: WCHAR[256] name, DWORD flags (516 bytes).
                            var name = Marshal.PtrToStringUni(profileList + 8 + p * 516) ?? "";
                            if (name.Length == 0 || !seen.Add(name)) continue;

                            uint flags = WLAN_PROFILE_GET_PLAINTEXT_KEY;
                            uint get = WlanGetProfile(handle, ref ifGuid, name, IntPtr.Zero, out var xmlPtr, ref flags, out _);
                            if (get == ERROR_ACCESS_DENIED) { accessDenied = true; continue; }
                            if (get != 0 || xmlPtr == IntPtr.Zero) continue;
                            try
                            {
                                var xml = Marshal.PtrToStringUni(xmlPtr) ?? "";
                                profiles.Add(Parse(name, xml));
                            }
                            catch
                            {
                                profiles.Add(new Profile(name, name, "?", null, false));
                            }
                            finally
                            {
                                WlanFreeMemory(xmlPtr);
                            }
                        }
                    }
                    finally
                    {
                        WlanFreeMemory(profileList);
                    }
                }
            }
            finally
            {
                WlanFreeMemory(ifList);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }

        if (profiles.Count == 0)
        {
            if (accessDenied)
                return TrickResult.Fail(new LocText(
                    "Windows blocked access to Wi-Fi information. On Windows 11 24H2 and later, turn on Location services " +
                    "(Settings › Privacy & security › Location, including \"Let desktop apps access your location\") and try again.",
                    "Windows ไม่อนุญาตให้อ่านข้อมูล Wi-Fi — บน Windows 11 24H2 ขึ้นไป ให้เปิดบริการตำแหน่งที่ตั้ง " +
                    "(การตั้งค่า › ความเป็นส่วนตัวและความปลอดภัย › ตำแหน่งที่ตั้ง รวมถึง \"ให้แอปเดสก์ท็อปเข้าถึงตำแหน่งที่ตั้งของคุณ\") แล้วลองใหม่"));
            return TrickResult.Ok(new LocText("No saved Wi-Fi networks were found.", "ไม่พบเครือข่าย Wi-Fi ที่บันทึกไว้"));
        }

        var sb = new StringBuilder();
        sb.AppendLine(thai
            ? "รหัสผ่าน Wi-Fi ที่บันทึกไว้ในเครื่องนี้ (อย่าแชร์หน้าจอนี้ให้คนอื่นเห็น)"
            : "Wi-Fi passwords saved on this PC (don't share this screen with others)");
        sb.AppendLine(new string('─', 60));
        foreach (var p in profiles.OrderBy(p => p.Ssid, StringComparer.CurrentCultureIgnoreCase))
        {
            sb.AppendLine(p.Ssid);
            if (!string.Equals(p.Name, p.Ssid, StringComparison.Ordinal))
                sb.AppendLine((thai ? "  ชื่อโปรไฟล์: " : "  Profile:  ") + p.Name);
            sb.AppendLine((thai ? "  ความปลอดภัย: " : "  Security: ") + p.Security);
            string password = p.IsEnterprise
                ? (thai ? "(ใช้บัญชีองค์กรเข้าสู่ระบบ — ไม่มีรหัสผ่านร่วม)" : "(signs in with an account — no shared password)")
                : p.Password ?? (p.Security == "open"
                    ? (thai ? "(ไม่มีรหัสผ่าน)" : "(no password)")
                    : (thai ? "(อ่านไม่ได้)" : "(not available)"));
            sb.AppendLine((thai ? "  รหัสผ่าน:   " : "  Password: ") + password);
            sb.AppendLine();
        }

        return new TrickResult
        {
            Success = true,
            Output = sb.ToString(),
            Message = new LocText($"Found {profiles.Count} saved network(s).", $"พบเครือข่ายที่บันทึกไว้ {profiles.Count} รายการ")
        };
    }

    private static Profile Parse(string profileName, string xml)
    {
        var doc = XDocument.Parse(xml); // DTDs are prohibited by default
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        string ssid = doc.Root?.Element(ns + "SSIDConfig")?.Element(ns + "SSID")?.Element(ns + "name")?.Value
                      ?? profileName;
        var security = doc.Root?.Element(ns + "MSM")?.Element(ns + "security");
        string auth = security?.Element(ns + "authEncryption")?.Element(ns + "authentication")?.Value ?? "?";
        bool enterprise = (security?.Element(ns + "authEncryption")?.Element(ns + "useOneX")?.Value)
                          ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

        var sharedKey = security?.Element(ns + "sharedKey");
        string? password = null;
        if (sharedKey != null &&
            !(sharedKey.Element(ns + "protected")?.Value ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            password = sharedKey.Element(ns + "keyMaterial")?.Value;
        }

        return new Profile(profileName, ssid, auth, password, enterprise);
    }

    #region Native

    private const uint WLAN_PROFILE_GET_PLAINTEXT_KEY = 0x00000004;
    private const uint ERROR_ACCESS_DENIED = 5;
    private const uint ERROR_SERVICE_DISABLED = 1058;
    private const uint ERROR_SERVICE_NOT_ACTIVE = 1062;

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetProfileList(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved, out IntPtr profileList);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfile(IntPtr clientHandle, ref Guid interfaceGuid, string profileName,
        IntPtr reserved, out IntPtr profileXml, ref uint flags, out uint grantedAccess);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    #endregion
}

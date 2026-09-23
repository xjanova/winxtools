using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace NetX.Core.System.Tweaks;

/// <summary>Read-only security reports shown inside the app.</summary>
public static class SecurityReports
{
    /// <summary>
    /// Newest failed sign-ins (event 4625) read in-process from the Security
    /// log, newest first, with a readable reason instead of NTSTATUS codes.
    /// </summary>
    public static TrickResult FailedSignIns(bool thai, int max = 25)
    {
        var sb = new StringBuilder();
        int count = 0;
        try
        {
            var query = new EventLogQuery("Security", PathType.LogName, "*[System[(EventID=4625)]]")
            {
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);
            for (var ev = reader.ReadEvent(); ev != null && count < max; ev = reader.ReadEvent())
            {
                using (ev)
                {
                    var p = ev.Properties;
                    string Prop(int i) => i < p.Count ? Convert.ToString(p[i].Value, CultureInfo.InvariantCulture)?.Trim() ?? "" : "";

                    string user = Prop(5);
                    string domain = Prop(6);
                    string account = string.IsNullOrEmpty(domain) || domain == "-" ? user : $"{domain}\\{user}";
                    string ip = Prop(19);
                    string workstation = Prop(13);
                    string from = (ip is "" or "-" or "::1" or "127.0.0.1")
                        ? (thai ? "เครื่องนี้" : "this PC")
                        : ip + (workstation is "" or "-" ? "" : $" ({workstation})");
                    uint subStatus = p.Count > 9 && p[9].Value is uint s ? s : 0;
                    uint status = p.Count > 7 && p[7].Value is uint st ? st : 0;
                    int logonType = int.TryParse(Prop(10), out var lt) ? lt : 0;

                    string when = ev.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "?";
                    sb.AppendLine(when);
                    sb.AppendLine((thai ? "  บัญชี:   " : "  Account: ") + (account.Length == 0 ? "-" : account));
                    sb.AppendLine((thai ? "  วิธี:     " : "  How:     ") + LogonTypeText(logonType, thai));
                    sb.AppendLine((thai ? "  จาก:     " : "  From:    ") + from);
                    sb.AppendLine((thai ? "  สาเหตุ:   " : "  Reason:  ") + ReasonText(subStatus != 0 ? subStatus : status, thai));
                    sb.AppendLine();
                    count++;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return TrickResult.Fail(TweakErrors.NeedAdmin);
        }
        catch (EventLogException ex)
        {
            return TrickResult.Fail(new LocText(
                $"The Security log could not be read (0x{ex.HResult:X8}).",
                $"อ่าน Security log ไม่ได้ (0x{ex.HResult:X8})"));
        }

        if (count == 0)
            return TrickResult.Ok(new LocText(
                "No failed sign-ins were found. (The log keeps only recent events, and some PCs don't record failed sign-ins.)",
                "ไม่พบการเข้าสู่ระบบที่ล้มเหลว (log เก็บเฉพาะเหตุการณ์ล่าสุด และบางเครื่องไม่ได้เปิดการบันทึกการเข้าสู่ระบบที่ล้มเหลว)"));

        return new TrickResult
        {
            Success = true,
            Output = sb.ToString(),
            Message = new LocText($"{count} most recent failed sign-in(s), newest first.",
                $"การเข้าสู่ระบบที่ล้มเหลวล่าสุด {count} ครั้ง (ใหม่สุดอยู่บน)")
        };
    }

    private static string LogonTypeText(int type, bool thai) => type switch
    {
        2 => thai ? "ที่หน้าเครื่อง" : "at this PC",
        3 => thai ? "ผ่านเครือข่าย (เช่น แชร์ไฟล์)" : "over the network (e.g. file sharing)",
        4 => thai ? "งานตามกำหนดเวลา" : "scheduled task",
        5 => thai ? "บริการ (service)" : "service",
        7 => thai ? "ปลดล็อกหน้าจอ" : "unlocking the screen",
        8 => thai ? "ผ่านเครือข่าย (รหัสผ่านแบบข้อความ)" : "network (clear-text password)",
        10 => "Remote Desktop",
        11 => thai ? "ข้อมูลเข้าสู่ระบบที่แคชไว้" : "cached credentials",
        _ => type == 0 ? "-" : type.ToString(CultureInfo.InvariantCulture)
    };

    private static string ReasonText(uint code, bool thai) => code switch
    {
        0xC000006A => thai ? "รหัสผ่านผิด" : "wrong password",
        0xC0000064 => thai ? "ไม่มีบัญชีผู้ใช้นี้" : "no such user",
        0xC0000234 => thai ? "บัญชีถูกล็อก" : "account locked out",
        0xC0000072 => thai ? "บัญชีถูกปิดใช้งาน" : "account disabled",
        0xC000006F => thai ? "เข้าสู่ระบบนอกเวลาที่อนุญาต" : "outside allowed hours",
        0xC0000070 => thai ? "ไม่อนุญาตให้เข้าจากเครื่องนี้" : "not allowed from this workstation",
        0xC0000071 => thai ? "รหัสผ่านหมดอายุ" : "password expired",
        0xC0000193 => thai ? "บัญชีหมดอายุ" : "account expired",
        0xC0000224 => thai ? "ต้องเปลี่ยนรหัสผ่านก่อน" : "password must be changed",
        0xC0000133 => thai ? "เวลาเครื่องไม่ตรงกับเซิร์ฟเวอร์" : "clock out of sync",
        0xC000015B => thai ? "ไม่ได้รับสิทธิ์เข้าสู่ระบบแบบนี้" : "sign-in type not allowed",
        0xC000006D => thai ? "ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง" : "bad user name or password",
        0 => "-",
        _ => $"0x{code:X8}"
    };

    /// <summary>Secure Boot state from the firmware-backed registry flag.</summary>
    public static TrickResult SecureBoot()
    {
        int? enabled = Reg.ReadDword(RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");

        return enabled switch
        {
            1 => TrickResult.Ok(new LocText("Secure Boot is ON.", "Secure Boot เปิดอยู่")),
            0 => TrickResult.Ok(new LocText(
                "Secure Boot is OFF. Your PC supports it: it can be turned on in the UEFI (BIOS) setup, usually under Boot or Security.",
                "Secure Boot ปิดอยู่ เครื่องของคุณรองรับ สามารถเปิดได้ในหน้าตั้งค่า UEFI (BIOS) มักอยู่ในเมนู Boot หรือ Security")),
            _ => TrickResult.Ok(new LocText(
                "Secure Boot isn't available: the PC starts in legacy BIOS (CSM) mode or the firmware doesn't support it.",
                "ใช้ Secure Boot ไม่ได้: เครื่องบูตแบบ BIOS รุ่นเก่า (CSM) หรือเฟิร์มแวร์ไม่รองรับ"))
        };
    }
}

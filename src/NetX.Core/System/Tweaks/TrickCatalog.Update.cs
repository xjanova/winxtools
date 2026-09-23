namespace NetX.Core.System.Tweaks;

public static partial class TrickCatalog
{
    private static void AddUpdate(List<TrickDefinition> list, WindowsVersionInfo os)
    {
        list.Add(new TrickDefinition
        {
            Id = "pause-updates",
            Category = Update,
            Icon = "SettingsIcon",
            Name = L("Pause updates", "หยุดอัปเดตชั่วคราว"),
            Description = L("Opens Windows Update, where you can pause updates for up to 5 weeks — the supported way to do it.",
                            "เปิดหน้า Windows Update ซึ่งหยุดอัปเดตชั่วคราวได้สูงสุด 5 สัปดาห์ — เป็นวิธีที่ Windows รองรับ"),
            Risk = TrickRisk.Safe,
            Technical = "ms-settings:windowsupdate",
            SettingsUri = "ms-settings:windowsupdate",
            SettingsLabel = L("Open Windows Update", "เปิด Windows Update")
        });

        list.Add(RegToggle("no-driver-updates", Update, "SettingsIcon",
            L("Don't install drivers through Windows Update", "ไม่ให้ Windows Update ติดตั้งไดรเวอร์"),
            L("Stops Windows Update from replacing your graphics card and other drivers. You'll update drivers yourself from the maker's website.",
              "ไม่ให้ Windows Update ติดตั้งทับไดรเวอร์การ์ดจอและไดรเวอร์อื่น ๆ คุณจะต้องอัปเดตไดรเวอร์เองจากเว็บไซต์ผู้ผลิต"),
            TrickRisk.Moderate, RestartScope.None,
            new[]
            {
                Dword(HKLM, WindowsUpdatePolicy, "ExcludeWUDriversInQualityUpdate", 1, null, 0),
                Dword(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig", 0, 1)
            },
            tip: os.IsHomeEdition
                ? L("Fully honored on Pro, Enterprise and Education. Windows Home may still install some drivers.",
                    "มีผลเต็มที่บน Pro, Enterprise และ Education ส่วน Windows Home อาจยังติดตั้งไดรเวอร์บางตัว")
                : null,
            warning: L("Your hardware may miss important driver fixes. Check your PC or graphics card maker's website now and then.",
                       "ฮาร์ดแวร์อาจพลาดการแก้ไขไดรเวอร์ที่สำคัญ ควรเข้าเว็บไซต์ผู้ผลิตคอมหรือการ์ดจอเป็นระยะ")));

        if (os.IsProOrHigher && !os.IsServer)
        {
            list.Add(RegToggle("no-auto-restart", Update, "SettingsIcon",
                L("No automatic restart while you're signed in", "ไม่รีสตาร์ทอัตโนมัติขณะมีผู้ใช้เข้าสู่ระบบอยู่"),
                L("Windows Update won't restart the PC on its own while someone is signed in — it waits for you. Updates still install.",
                  "Windows Update จะไม่รีสตาร์ทเครื่องเองขณะมีคนเข้าสู่ระบบอยู่ แต่จะรอให้คุณรีสตาร์ทเอง อัปเดตยังติดตั้งตามปกติ"),
                TrickRisk.Safe, RestartScope.None,
                new[] { Dword(HKLM, WindowsUpdatePolicy + @"\AU", "NoAutoRebootWithLoggedOnUsers", 1, null, 0) },
                tip: L("Remember to restart when Windows says updates are waiting.", "อย่าลืมรีสตาร์ทเมื่อ Windows แจ้งว่ามีอัปเดตรออยู่")));

            if (!string.IsNullOrWhiteSpace(os.DisplayVersion) && (os.IsWindows10 || os.IsWindows11))
            {
                var version = os.DisplayVersion;
                var family = os.FamilyName;
                list.Add(RegToggle("stay-on-version", Update, "SettingsIcon",
                    L($"Stay on {family} {version}", $"อยู่กับ {family} {version} ต่อไป"),
                    L("Stops Windows Update from moving you to the next yearly version. Monthly security updates keep coming.",
                      "ไม่ให้ Windows Update อัปเกรดไปเวอร์ชันใหม่ประจำปี แต่ยังได้รับอัปเดตความปลอดภัยรายเดือนตามปกติ"),
                    TrickRisk.Moderate, RestartScope.None,
                    new[]
                    {
                        Dword(HKLM, WindowsUpdatePolicy, "TargetReleaseVersion", 1, null, 0),
                        Text(HKLM, WindowsUpdatePolicy, "ProductVersion", family, null),
                        Text(HKLM, WindowsUpdatePolicy, "TargetReleaseVersionInfo", version, null)
                    },
                    warning: L($"Each version gets security updates for a limited time only. Restore this before {version} reaches its end of " +
                               "servicing, or you will stop receiving security fixes.",
                               $"แต่ละเวอร์ชันได้รับอัปเดตความปลอดภัยในระยะเวลาจำกัด ควรคืนค่าก่อนที่ {version} จะหมดระยะการสนับสนุน " +
                               "มิฉะนั้นจะไม่ได้รับการแก้ไขด้านความปลอดภัยอีก"),
                    decorate: status =>
                    {
                        var pinned = Reg.ReadString(HKLM, WindowsUpdatePolicy, "TargetReleaseVersionInfo");
                        return status.State != TrickState.Default && !string.IsNullOrEmpty(pinned)
                            ? status with { Note = L($"Pinned to version {pinned}.", $"ตั้งให้อยู่ที่เวอร์ชัน {pinned}") }
                            : status;
                    }));
            }
        }
    }
}

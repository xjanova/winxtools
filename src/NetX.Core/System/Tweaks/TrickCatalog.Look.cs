namespace NetX.Core.System.Tweaks;

public static partial class TrickCatalog
{
    private static void AddLook(List<TrickDefinition> list, WindowsVersionInfo os)
    {
        Action colorsChanged = () => NativeSettings.BroadcastSettingChange("ImmersiveColorSet");

        // Windows 11 default: light apps + light system. Windows 10 default: light
        // apps + dark taskbar, so only the apps value changes there.
        var darkSettings = os.IsWindows11
            ? new[]
            {
                Dword(HKCU, Personalize, "AppsUseLightTheme", 0, 1),
                Dword(HKCU, Personalize, "SystemUsesLightTheme", 0, 1)
            }
            : new[] { Dword(HKCU, Personalize, "AppsUseLightTheme", 0, 1) };

        list.Add(RegToggle("dark-mode", Look, "SettingsIcon",
            L("Dark mode for Windows and apps", "โหมดมืดทั้ง Windows และแอป"),
            os.IsWindows11
                ? L("Switches the taskbar, Start, Settings and apps that follow Windows to dark mode.",
                    "เปลี่ยนแถบงาน เมนูเริ่ม การตั้งค่า และแอปที่ใช้ธีมตาม Windows เป็นโหมดมืด")
                : L("Switches Settings, File Explorer and apps that follow Windows to dark mode (the taskbar is already dark).",
                    "เปลี่ยนการตั้งค่า File Explorer และแอปที่ใช้ธีมตาม Windows เป็นโหมดมืด (แถบงานเป็นสีเข้มอยู่แล้ว)"),
            TrickRisk.Safe, RestartScope.None, darkSettings, afterChange: colorsChanged));

        list.Add(RegToggle("transparency", Look, "SettingsIcon",
            L("Turn off transparency effects", "ปิดเอฟเฟกต์ความโปร่งใส"),
            L("Makes the taskbar, Start and windows solid. Easier to read and slightly lighter on older graphics chips.",
              "ทำให้แถบงาน เมนูเริ่ม และหน้าต่างเป็นสีทึบ อ่านง่ายขึ้นและลดภาระการ์ดจอรุ่นเก่าเล็กน้อย"),
            TrickRisk.Safe, RestartScope.None,
            new[] { Dword(HKCU, Personalize, "EnableTransparency", 0, 1) },
            afterChange: colorsChanged));

        list.Add(new TrickDefinition
        {
            Id = "animations",
            Category = Look,
            Icon = "SpeedIcon",
            Name = L("Turn off animations", "ปิดภาพเคลื่อนไหว"),
            Description = L(
                "Windows open, close and minimize instantly, without fades or slides. The PC feels snappier; apps don't actually run faster.",
                "หน้าต่างเปิด ปิด และย่อทันทีโดยไม่มีเอฟเฟกต์เลื่อนหรือจาง ทำให้รู้สึกเร็วขึ้น แต่ไม่ได้ทำให้โปรแกรมทำงานเร็วขึ้นจริง"),
            Risk = TrickRisk.Safe,
            Technical = "SystemParametersInfo: SPI_SETCLIENTAREAANIMATION = off, SPI_SETANIMATION (minimize/restore) = off",
            Detect = () =>
            {
                bool client = NativeSettings.GetClientAreaAnimation();
                bool minimize = NativeSettings.GetMinimizeAnimation();
                if (!client && !minimize) return TrickStatus.Applied();
                if (client && minimize) return TrickStatus.Default();
                return TrickStatus.Custom();
            },
            Apply = _ => Task.Run(() => NativeChange(() =>
            {
                NativeSettings.SetClientAreaAnimation(false);
                NativeSettings.SetMinimizeAnimation(false);
            }, () => !NativeSettings.GetClientAreaAnimation() && !NativeSettings.GetMinimizeAnimation(), restore: false)),
            Revert = _ => Task.Run(() => NativeChange(() =>
            {
                NativeSettings.SetClientAreaAnimation(true);
                NativeSettings.SetMinimizeAnimation(true);
            }, () => NativeSettings.GetClientAreaAnimation() && NativeSettings.GetMinimizeAnimation(), restore: true))
        });

        list.Add(new TrickDefinition
        {
            Id = "mouse-acceleration",
            Category = Look,
            Icon = "SettingsIcon",
            Name = L("Turn off mouse acceleration", "ปิดการเร่งความเร็วเมาส์"),
            Description = L(
                "Turns off \"Enhance pointer precision\", so the pointer moves the same distance however fast you move the mouse — preferred for aiming in games.",
                "ปิด \"เพิ่มความแม่นยำของตัวชี้\" ให้ตัวชี้เคลื่อนที่ระยะเท่าเดิมไม่ว่าจะขยับเมาส์เร็วหรือช้า เหมาะกับเกมที่ต้องเล็ง"),
            Risk = TrickRisk.Safe,
            Technical = "SystemParametersInfo SPI_SETMOUSE {0, 0, 0} (Windows default {6, 10, 1})",
            Detect = () =>
            {
                var m = NativeSettings.GetMouse();
                if (m[2] == 0) return TrickStatus.Applied();
                if (m[0] == 6 && m[1] == 10 && m[2] == 1) return TrickStatus.Default();
                return TrickStatus.Custom();
            },
            Apply = _ => Task.Run(() => NativeChange(() => NativeSettings.SetMouse(0, 0, 0),
                () => NativeSettings.GetMouse()[2] == 0, restore: false)),
            Revert = _ => Task.Run(() => NativeChange(() => NativeSettings.SetMouse(6, 10, 1),
                () => NativeSettings.GetMouse() is [6, 10, 1], restore: true))
        });

        list.Add(new TrickDefinition
        {
            Id = "sticky-keys-prompts",
            Category = Look,
            Icon = "SettingsIcon",
            Name = L("Stop Sticky Keys pop-ups", "ปิดหน้าต่าง Sticky Keys ที่เด้งขึ้นระหว่างเล่นเกม"),
            Description = L(
                "Pressing Shift five times, holding Shift or holding Num Lock no longer opens the Sticky/Filter/Toggle Keys prompts. " +
                "You can still turn those features on in Settings.",
                "กด Shift 5 ครั้ง กด Shift ค้าง หรือกด Num Lock ค้าง จะไม่เด้งหน้าต่าง Sticky/Filter/Toggle Keys อีก " +
                "ยังเปิดฟีเจอร์เหล่านี้เองได้ในการตั้งค่า"),
            Risk = TrickRisk.Safe,
            Technical = "SystemParametersInfo: clear SKF/FKF/TKF_HOTKEYACTIVE (StickyKeys 510→506, Keyboard Response 126→122, ToggleKeys 62→58)",
            Detect = NativeSettings.DetectAccessibilityShortcuts,
            Apply = _ => Task.Run(() => NativeChange(() => NativeSettings.SetAccessibilityShortcuts(false),
                () => NativeSettings.DetectAccessibilityShortcuts().State == TrickState.Applied, restore: false)),
            Revert = _ => Task.Run(() => NativeChange(() => NativeSettings.SetAccessibilityShortcuts(true),
                () => NativeSettings.DetectAccessibilityShortcuts().State == TrickState.Default, restore: true))
        });

        // 0x80000000 = "Windows decides"; bit 1 (value 2) = Num Lock on.
        list.Add(RegToggle("numlock", Look, "SettingsIcon",
            L("Turn on Num Lock at startup", "เปิด Num Lock อัตโนมัติเมื่อเปิดเครื่อง"),
            L("Num Lock is on at the sign-in screen and after you sign in, so you can type a PIN on the number pad right away.",
              "Num Lock จะเปิดตั้งแต่หน้าจอเข้าสู่ระบบ พิมพ์ PIN ด้วยแป้นตัวเลขได้ทันที"),
            TrickRisk.Safe, RestartScope.None,
            new[]
            {
                Text(HKU, @".DEFAULT\Control Panel\Keyboard", "InitialKeyboardIndicators", "2147483650", "2147483648", "0"),
                Text(HKCU, @"Control Panel\Keyboard", "InitialKeyboardIndicators", "2", "2147483648", "0")
            },
            tip: L("You'll see the difference the next time Windows starts.", "จะเห็นผลเมื่อเปิด Windows ครั้งถัดไป")));

        list.Add(RegToggle("clipboard-history", Look, "CopyIcon",
            L("Clipboard history (Win+V)", "ประวัติคลิปบอร์ด (Win+V)"),
            L("Keeps the last items you copied, so Win+V lets you paste older ones too.",
              "เก็บสิ่งที่คัดลอกไว้หลายรายการ กด Win+V เพื่อเลือกวางรายการก่อนหน้าได้"),
            TrickRisk.Safe, RestartScope.None,
            new[] { Dword(HKCU, @"Software\Microsoft\Clipboard", "EnableClipboardHistory", 1, null, 0) }));

        if (os.IsWindows11 && os.Build >= 22621)
        {
            list.Add(RegToggle("education-themes", Look, "SettingsIcon",
                L("Unlock the extra Education themes", "ปลดล็อกธีมพิเศษ (Education themes)"),
                L("Turns on the themes Microsoft made for schools. After a restart Windows downloads them in the background and they " +
                  "appear in Settings › Personalization › Themes (this can take a while).",
                  "เปิดธีมพิเศษที่ Microsoft ทำไว้สำหรับโรงเรียน หลังรีสตาร์ท Windows จะดาวน์โหลดธีมอยู่เบื้องหลัง " +
                  "แล้วจะแสดงในการตั้งค่า › การปรับเปลี่ยนในแบบของคุณ › ธีม (อาจใช้เวลาสักพัก)"),
                TrickRisk.Safe, RestartScope.Reboot,
                new[] { Dword(HKLM, @"SOFTWARE\Microsoft\PolicyManager\current\device\Education", "EnableEduThemes", 1, null, 0) }));
        }

        list.Add(RegToggle("verbose-status", Look, "TerminalIcon",
            L("Detailed messages at startup and shutdown", "แสดงข้อความสถานะละเอียดตอนเปิด/ปิดเครื่อง"),
            L("Instead of \"Please wait\", Windows shows what it is doing (e.g. \"Stopping services\"). Handy when startup or shutdown hangs.",
              "แทนที่จะขึ้นแค่ \"โปรดรอสักครู่\" Windows จะบอกว่ากำลังทำอะไรอยู่ (เช่น กำลังหยุดบริการ) ช่วยหาสาเหตุเวลาเปิด/ปิดเครื่องค้าง"),
            TrickRisk.Safe, RestartScope.None,
            new[] { Dword(HKLM, SystemPolicies, "VerboseStatus", 1, null, 0) }));
    }

    /// <summary>Runs a SystemParametersInfo change and reports success only if the read-back matches.</summary>
    private static TrickResult NativeChange(Action change, Func<bool> verify, bool restore)
    {
        try
        {
            change();
            if (!verify()) return TrickResult.Fail(TweakErrors.NotVerified);
            return TrickResult.Ok(restore ? TweakErrors.RestoredText : TweakErrors.AppliedText);
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(TweakErrors.Friendly(ex));
        }
    }
}

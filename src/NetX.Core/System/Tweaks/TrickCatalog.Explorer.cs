using Microsoft.Win32;

namespace NetX.Core.System.Tweaks;

public static partial class TrickCatalog
{
    private const string ClassicMenuKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    private static void AddExplorer(List<TrickDefinition> list, WindowsVersionInfo os)
    {
        // Explorer reads most of these when it starts; the page offers "Restart Explorer now".
        Action notifyShell = () => NativeSettings.BroadcastSettingChange("ShellState");

        list.Add(RegToggle("file-extensions", Explorer, "FolderIcon",
            L("Show file extensions", "แสดงนามสกุลไฟล์"),
            L("Shows endings such as .pdf or .exe in File Explorer, so a fake \"photo.jpg.exe\" is easy to spot.",
              "แสดงนามสกุลไฟล์ เช่น .pdf หรือ .exe ใน File Explorer ทำให้เห็นไฟล์หลอกอย่าง \"photo.jpg.exe\" ได้ง่าย"),
            TrickRisk.Safe, RestartScope.Explorer,
            new[] { Dword(HKCU, ExplorerAdvanced, "HideFileExt", 0, 1) },
            afterChange: notifyShell));

        list.Add(RegToggle("hidden-files", Explorer, "FolderIcon",
            L("Show hidden files and folders", "แสดงไฟล์และโฟลเดอร์ที่ซ่อนอยู่"),
            L("Shows items marked as hidden, such as the AppData folder.",
              "แสดงไฟล์และโฟลเดอร์ที่ถูกตั้งค่าให้ซ่อน เช่น โฟลเดอร์ AppData"),
            TrickRisk.Safe, RestartScope.Explorer,
            new[] { Dword(HKCU, ExplorerAdvanced, "Hidden", 1, 2) },
            afterChange: notifyShell));

        list.Add(RegToggle("protected-files", Explorer, "WarningIcon",
            L("Show protected Windows system files", "แสดงไฟล์ระบบที่ Windows ป้องกันไว้"),
            L("Also shows files Windows hides to protect itself (desktop.ini, boot files).",
              "แสดงไฟล์ที่ Windows ซ่อนไว้เพื่อป้องกันตัวเองด้วย (เช่น desktop.ini และไฟล์บูต)"),
            TrickRisk.Moderate, RestartScope.Explorer,
            new[] { Dword(HKCU, ExplorerAdvanced, "ShowSuperHidden", 1, 0) },
            warning: L("Deleting or moving these files can stop Windows from starting. Turn this back off when you're done.",
                       "ถ้าลบหรือย้ายไฟล์เหล่านี้ Windows อาจเปิดไม่ขึ้น ใช้เสร็จแล้วควรปิดกลับ"),
            afterChange: notifyShell));

        list.Add(RegToggle("open-this-pc", Explorer, "FolderIcon",
            L("Open File Explorer to \"This PC\"", "ให้ File Explorer เปิดที่ \"พีซีเครื่องนี้\""),
            L("File Explorer opens on This PC (your drives) instead of Home / Quick access.",
              "เปิด File Explorer แล้วเห็นไดรฟ์ทั้งหมดทันที แทนหน้าแรก/การเข้าถึงด่วน"),
            TrickRisk.Safe, RestartScope.Explorer,
            new[] { Dword(HKCU, ExplorerAdvanced, "LaunchTo", 1, null) },
            afterChange: notifyShell));

        if (os.SupportsTaskbarEndTask)
        {
            list.Add(RegToggle("taskbar-end-task", Explorer, "TerminalIcon",
                L("Add \"End task\" to the taskbar menu", "เพิ่ม \"สิ้นสุดงาน\" ในเมนูคลิกขวาบนแถบงาน"),
                L("Right-click a frozen app on the taskbar and close it without opening Task Manager.",
                  "คลิกขวาที่แอปที่ค้างบนแถบงานแล้วปิดได้เลย ไม่ต้องเปิดตัวจัดการงาน"),
                TrickRisk.Safe, RestartScope.Explorer,
                new[] { Dword(HKCU, ExplorerAdvanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, null, 0) }));
        }

        if (os.IsWindows11)
        {
            list.Add(RegToggle("taskbar-left", Explorer, "SettingsIcon",
                L("Move taskbar icons to the left", "ย้ายไอคอนบนแถบงานไปชิดซ้าย"),
                L("Puts the Start button and taskbar icons on the left, like Windows 10.",
                  "ย้ายปุ่มเริ่มและไอคอนบนแถบงานไปชิดซ้ายเหมือน Windows 10"),
                TrickRisk.Safe, RestartScope.Explorer,
                new[] { Dword(HKCU, ExplorerAdvanced, "TaskbarAl", 0, null, 1) },
                afterChange: () => NativeSettings.BroadcastSettingChange("TraySettings")));

            list.Add(RegToggle("snap-flyout", Explorer, "SettingsIcon",
                L("Turn off Snap layout pop-ups", "ปิดหน้าต่าง Snap layouts ที่เด้งขึ้น"),
                L("No layout pop-up when you hover the maximize button or drag a window to the top, and no \"snap next\" suggestions. " +
                  "Win+Z and dragging windows to screen edges still work.",
                  "ไม่ให้เมนู Snap layouts เด้งขึ้นเมื่อชี้ที่ปุ่มขยายหรือลากหน้าต่างไปด้านบน และไม่แนะนำหน้าต่างถัดไปหลังจัดเรียง " +
                  "ยังใช้ Win+Z และลากหน้าต่างไปชิดขอบจอได้ตามปกติ"),
                TrickRisk.Safe, RestartScope.Explorer,
                new[]
                {
                    Dword(HKCU, ExplorerAdvanced, "EnableSnapAssistFlyout", 0, null, 1),
                    Dword(HKCU, ExplorerAdvanced, "SnapAssist", 0, null, 1),
                    Dword(HKCU, ExplorerAdvanced, "EnableSnapBar", 0, null, 1)
                },
                afterChange: notifyShell));

            list.Add(new TrickDefinition
            {
                Id = "classic-context-menu",
                Category = Explorer,
                Icon = "SettingsIcon",
                Name = L("Classic right-click menu", "เมนูคลิกขวาแบบเดิม"),
                Description = L("Shows the full Windows 10 style right-click menu right away, without \"Show more options\".",
                                "แสดงเมนูคลิกขวาแบบ Windows 10 ทันที ไม่ต้องกด \"แสดงตัวเลือกเพิ่มเติม\""),
                Risk = TrickRisk.Safe,
                Restart = RestartScope.Explorer,
                Technical = $@"HKCU\{ClassicMenuKey}\InprocServer32 (Default) = """"",
                Detect = DetectClassicMenu,
                Apply = _ => Task.Run(ApplyClassicMenu),
                Revert = _ => Task.Run(RevertClassicMenu)
            });
        }

        if (os.IsWindows11)
        {
            list.Add(RegToggle("start-recommendations", Explorer, "SettingsIcon",
                L("Turn off Start menu recommendations & promotions", "ปิดคำแนะนำและโปรโมชันในเมนูเริ่ม"),
                L("Hides tips, app promotions and account notifications in Start.",
                  "ซ่อนเคล็ดลับ การโปรโมตแอป และการแจ้งเตือนเกี่ยวกับบัญชีในเมนูเริ่ม"),
                TrickRisk.Safe, RestartScope.Explorer,
                new[]
                {
                    Dword(HKCU, ExplorerAdvanced, "Start_IrisRecommendations", 0, null, 1),
                    Dword(HKCU, ExplorerAdvanced, "Start_AccountNotifications", 0, null, 1)
                },
                afterChange: notifyShell));
        }
        else if (os.IsWindows10)
        {
            list.Add(RegToggle("start-recommendations", Explorer, "SettingsIcon",
                L("Turn off app suggestions in Start", "ปิดการแนะนำแอปในเมนูเริ่ม"),
                L("Stops Windows from showing suggested (promoted) apps in the Start menu.",
                  "ไม่ให้ Windows แสดงแอปแนะนำ (โฆษณา) ในเมนูเริ่ม"),
                TrickRisk.Safe, RestartScope.Explorer,
                new[]
                {
                    Dword(HKCU, ContentDelivery, "SubscribedContent-338388Enabled", 0, null, 1),
                    Dword(HKCU, ContentDelivery, "SystemPaneSuggestionsEnabled", 0, null, 1)
                }));
        }

        list.Add(RegToggle("start-recent-files", Explorer, "FolderIcon",
            L("Don't show recently opened files", "ไม่แสดงไฟล์ที่เพิ่งเปิด"),
            L("Stops listing recent files in Start, Jump Lists and File Explorer's Home / Quick access.",
              "ไม่แสดงรายการไฟล์ล่าสุดในเมนูเริ่ม Jump List และหน้าแรกของ File Explorer"),
            TrickRisk.Safe, RestartScope.Explorer,
            new[] { Dword(HKCU, ExplorerAdvanced, "Start_TrackDocs", 0, null, 1) },
            afterChange: notifyShell));

        if (os.SupportsClockSeconds)
        {
            list.Add(RegToggle("clock-seconds", Explorer, "SettingsIcon",
                L("Show seconds in the taskbar clock", "แสดงวินาทีบนนาฬิกาแถบงาน"),
                L("Adds seconds to the taskbar clock. Uses a tiny bit more power.",
                  "เพิ่มวินาทีให้นาฬิกาบนแถบงาน ใช้พลังงานเพิ่มขึ้นเล็กน้อย"),
                TrickRisk.Safe, RestartScope.Explorer,
                new[] { Dword(HKCU, ExplorerAdvanced, "ShowSecondsInSystemClock", 1, null, 0) },
                afterChange: () => NativeSettings.BroadcastSettingChange("TraySettings")));
        }

        if (os.SupportsWidgets)
        {
            // Windows' UCPD driver blocks every non-Microsoft program (WinXTools too)
            // from writing TaskbarDa and the Widgets policy key, so only Settings can change it.
            list.Add(new TrickDefinition
            {
                Id = "widgets",
                Category = Explorer,
                Icon = "SettingsIcon",
                Name = L("Hide the Widgets button", "ซ่อนปุ่มวิดเจ็ต"),
                Description = L(
                    "Windows protects this setting, so it can only be changed in Settings › Personalization › Taskbar. The button opens that page.",
                    "Windows ป้องกันการตั้งค่านี้ไว้ จึงเปลี่ยนได้เฉพาะในการตั้งค่า › การปรับเปลี่ยนในแบบของคุณ › แถบงาน ปุ่มด้านล่างจะเปิดหน้านั้นให้"),
                Risk = TrickRisk.Safe,
                Technical = "ms-settings:taskbar",
                SettingsUri = "ms-settings:taskbar",
                SettingsLabel = L("Open taskbar settings", "เปิดการตั้งค่าแถบงาน"),
                Detect = () => Reg.ReadDword(HKCU, ExplorerAdvanced, "TaskbarDa") == 0
                    ? TrickStatus.Applied(L("The Widgets button is hidden.", "ปุ่มวิดเจ็ตถูกซ่อนอยู่"))
                    : TrickStatus.Default(L("The Widgets button is shown.", "ปุ่มวิดเจ็ตแสดงอยู่"))
            });
        }
    }

    private static TrickStatus DetectClassicMenu()
    {
        using var key = Reg.OpenBase(HKCU).OpenSubKey(ClassicMenuKey + @"\InprocServer32");
        if (key == null) return TrickStatus.Default();
        return string.IsNullOrEmpty(key.GetValue("") as string) ? TrickStatus.Applied() : TrickStatus.Custom();
    }

    private static TrickResult ApplyClassicMenu()
    {
        try
        {
            using (var key = Reg.OpenBase(HKCU).CreateSubKey(ClassicMenuKey + @"\InprocServer32", writable: true))
                key.SetValue("", "", RegistryValueKind.String);
            return DetectClassicMenu().State == TrickState.Applied
                ? TrickResult.Ok(TweakErrors.AppliedText, RestartScope.Explorer)
                : TrickResult.Fail(TweakErrors.NotVerified);
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(TweakErrors.Friendly(ex));
        }
    }

    private static TrickResult RevertClassicMenu()
    {
        try
        {
            Reg.OpenBase(HKCU).DeleteSubKeyTree(ClassicMenuKey, throwOnMissingSubKey: false);
            return DetectClassicMenu().State == TrickState.Default
                ? TrickResult.Ok(TweakErrors.RestoredText, RestartScope.Explorer)
                : TrickResult.Fail(TweakErrors.NotVerified);
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(TweakErrors.Friendly(ex));
        }
    }
}

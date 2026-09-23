using System.ServiceProcess;

namespace NetX.Core.System.Tweaks;

public static partial class TrickCatalog
{
    private const string CopilotStoreUri = "ms-windows-store://pdp/?productid=9NHT9RB2F4HD";

    private static void AddPrivacy(List<TrickDefinition> list, WindowsVersionInfo os)
    {
        list.Add(RegToggle("windows-suggestions", Privacy, "ShieldIcon",
            L("Turn off Windows tips & \"finish setup\" screens", "ปิดเคล็ดลับและหน้าจอชวน \"ตั้งค่าให้เสร็จ\" ของ Windows"),
            L("Stops tip and suggestion notifications, suggested content in Settings, the welcome screen after updates and " +
              "\"Let's finish setting up your device\".",
              "ปิดการแจ้งเตือนเคล็ดลับและคำแนะนำ เนื้อหาแนะนำในแอปการตั้งค่า หน้าต้อนรับหลังอัปเดต " +
              "และหน้าจอ \"มาตั้งค่าอุปกรณ์ให้เสร็จกันเถอะ\""),
            TrickRisk.Safe, RestartScope.None,
            new[]
            {
                Dword(HKCU, ContentDelivery, "SubscribedContent-338389Enabled", 0, null, 1),
                Dword(HKCU, ContentDelivery, "SubscribedContent-310093Enabled", 0, null, 1),
                Dword(HKCU, ContentDelivery, "SubscribedContent-338393Enabled", 0, null, 1),
                Dword(HKCU, ContentDelivery, "SubscribedContent-353694Enabled", 0, null, 1),
                Dword(HKCU, ContentDelivery, "SubscribedContent-353696Enabled", 0, null, 1),
                Dword(HKCU, ContentDelivery, "SoftLandingEnabled", 0, null, 1),
                Dword(HKCU, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, null, 1)
            }));

        list.Add(RegToggle("lock-screen-ads", Privacy, "ShieldIcon",
            L("Turn off lock-screen tips & ads", "ปิดเคล็ดลับและโฆษณาบนหน้าจอล็อก"),
            L("Removes \"fun facts, tips and tricks\" and promotions from the lock screen. Your picture stays.",
              "ลบข้อความ \"เกร็ดความรู้ เคล็ดลับ\" และโฆษณาออกจากหน้าจอล็อก ภาพพื้นหลังยังอยู่เหมือนเดิม"),
            TrickRisk.Safe, RestartScope.None,
            new[]
            {
                Dword(HKCU, ContentDelivery, "RotatingLockScreenOverlayEnabled", 0, null, 1),
                Dword(HKCU, ContentDelivery, "SubscribedContent-338387Enabled", 0, null, 1)
            }));

        list.Add(RegToggle("advertising-id", Privacy, "ShieldIcon",
            L("Turn off the advertising ID", "ปิดรหัสโฆษณา (Advertising ID)"),
            L("Apps can no longer use your advertising ID to show personalized ads.",
              "แอปจะใช้รหัสโฆษณาของคุณเพื่อแสดงโฆษณาที่ตรงกับความสนใจไม่ได้"),
            TrickRisk.Safe, RestartScope.None,
            new[] { Dword(HKCU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1) }));

        list.Add(RegToggle("start-web-search", Privacy, "SearchIcon",
            L("Turn off web (Bing) results in Start search", "ปิดผลค้นหาจากเว็บ (Bing) ในเมนูเริ่ม"),
            L("Start search shows only apps, files and settings on this PC — faster and more private.",
              "การค้นหาในเมนูเริ่มจะแสดงเฉพาะแอป ไฟล์ และการตั้งค่าในเครื่อง เร็วขึ้นและเป็นส่วนตัวกว่า"),
            TrickRisk.Safe, RestartScope.Explorer,
            new[] { Dword(HKCU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null, 0) }));

        list.Add(new TrickDefinition
        {
            Id = "telemetry-service",
            Category = Privacy,
            Icon = "ShieldIcon",
            Name = L("Stop the telemetry service", "หยุดบริการส่งข้อมูลการใช้งาน (Telemetry)"),
            Description = L(
                "Disables \"Connected User Experiences and Telemetry\", the service that sends diagnostic data to Microsoft. " +
                "This is for privacy — it won't make the PC noticeably faster.",
                "ปิดบริการ \"Connected User Experiences and Telemetry\" ที่ส่งข้อมูลการวินิจฉัยไปยัง Microsoft " +
                "เพื่อความเป็นส่วนตัว — ไม่ได้ทำให้เครื่องเร็วขึ้นอย่างเห็นได้ชัด"),
            Tip = L("Feedback Hub and Windows Insider features may stop working until you restore it.",
                    "Feedback Hub และฟีเจอร์ Windows Insider อาจใช้งานไม่ได้จนกว่าจะคืนค่า"),
            Risk = TrickRisk.Safe,
            Technical = "Service DiagTrack → Disabled (Windows default: Automatic)",
            Detect = () => ServiceTweak.Detect("DiagTrack", ServiceStartMode.Automatic),
            Apply = _ => Task.Run(() => ServiceTweak.Disable("DiagTrack")),
            Revert = _ => Task.Run(() => ServiceTweak.Restore("DiagTrack", ServiceStartMode.Automatic, delayed: false))
        });

        list.Add(RegToggle("activity-history", Privacy, "ShieldIcon",
            L("Turn off activity history", "ปิดประวัติกิจกรรม"),
            L("Windows stops collecting and uploading the list of apps and files you used.",
              "Windows จะหยุดเก็บและอัปโหลดรายการแอปและไฟล์ที่คุณใช้งาน"),
            TrickRisk.Safe, RestartScope.None,
            new[]
            {
                Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, null, 1),
                Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, null, 1),
                Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, null, 1)
            }));

        list.Add(RegToggle("location", Privacy, "ShieldIcon",
            L("Turn off location services", "ปิดบริการตำแหน่งที่ตั้ง"),
            L("No app — and not Windows itself — can use this PC's location.",
              "ไม่มีแอปใด (รวมถึงตัว Windows เอง) ใช้ตำแหน่งที่ตั้งของเครื่องนี้ได้"),
            TrickRisk.Moderate, RestartScope.None,
            new[] { Text(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny", "Allow") },
            warning: L("Automatic time zone, Find my device, Weather, Maps and Wi-Fi tools that need your location will stop working " +
                       "until you turn it back on.",
                       "การตั้งเขตเวลาอัตโนมัติ, ค้นหาอุปกรณ์ของฉัน, สภาพอากาศ, แผนที่ และเครื่องมือ Wi-Fi ที่ต้องใช้ตำแหน่ง " +
                       "จะใช้งานไม่ได้จนกว่าจะเปิดกลับ")));

        if (os.IsWindows10)
        {
            list.Add(RegToggle("cortana", Privacy, "ShieldIcon",
                L("Turn off Cortana", "ปิด Cortana"),
                L("Turns Cortana off with the Windows policy.", "ปิด Cortana ด้วยนโยบายของ Windows"),
                TrickRisk.Safe, RestartScope.SignOut,
                new[] { Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, null, 1) }));
        }

        if (os.IsWindows11 || CopilotInstalled())
        {
            list.Add(new TrickDefinition
            {
                Id = "copilot-app",
                Category = Privacy,
                Icon = "ShieldIcon",
                Name = L("Remove the Copilot app", "ลบแอป Copilot"),
                Description = L(
                    "Uninstalls the Microsoft Copilot app for your account. You can install it again any time from the Microsoft Store.",
                    "ถอนการติดตั้งแอป Microsoft Copilot สำหรับบัญชีของคุณ ติดตั้งกลับได้ทุกเมื่อจาก Microsoft Store"),
                Warning = L("Copilot and its chat history on this PC will be removed for your account.",
                            "แอป Copilot และประวัติแชทในเครื่องนี้จะถูกลบออกจากบัญชีของคุณ"),
                Risk = TrickRisk.Moderate,
                Technical = "Get-AppxPackage -Name Microsoft.Copilot | Remove-AppxPackage",
                ApplyLabel = L("Remove", "ลบแอป"),
                RevertLabel = L("Get it from the Store", "ติดตั้งจาก Store"),
                AppliedLabel = L("Removed", "ลบแล้ว"),
                DefaultLabel = L("Installed", "ติดตั้งอยู่"),
                Detect = () => CopilotInstalled() ? TrickStatus.Default() : TrickStatus.Applied(),
                Apply = RemoveCopilotAsync,
                Revert = _ => Task.FromResult(OpenStoreForCopilot())
            });
        }

        if (os.SupportsRecall)
        {
            list.Add(RegToggle("recall", Privacy, "ShieldIcon",
                L("Turn off Recall", "ปิด Recall"),
                L("Stops Windows Recall from saving snapshots of your screen and blocks it from being turned on on this Copilot+ PC.",
                  "ไม่ให้ Windows Recall บันทึกภาพหน้าจอของคุณ และป้องกันไม่ให้เปิด Recall บนพีซี Copilot+ เครื่องนี้"),
                TrickRisk.Safe, RestartScope.Reboot,
                new[]
                {
                    Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", 0, null, 1),
                    Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, null, 0)
                }));
        }

        list.Add(RegToggle("edge-background", Privacy, "SpeedIcon",
            L("Stop Edge running in the background", "ไม่ให้ Edge ทำงานอยู่เบื้องหลัง"),
            L("Turns off Edge \"Startup boost\" and \"Continue running background extensions and apps when Microsoft Edge is closed\", " +
              "freeing memory after you sign in.",
              "ปิด \"Startup boost\" และ \"ให้ส่วนขยายและแอปเบื้องหลังทำงานต่อเมื่อปิด Microsoft Edge\" ช่วยคืนหน่วยความจำหลังเข้าสู่ระบบ"),
            TrickRisk.Safe, RestartScope.None,
            new[]
            {
                Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0, null, 1),
                Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0, null, 1)
            },
            tip: L("Edge will show \"Your browser is managed by your organization\" because this uses Edge policies. That is expected.",
                   "Edge จะแสดงข้อความ \"เบราว์เซอร์ของคุณได้รับการจัดการโดยองค์กร\" เพราะใช้นโยบายของ Edge ถือเป็นเรื่องปกติ")));

        list.Add(RegToggle("store-apps-background", Privacy, "ShieldIcon",
            L("Stop Store apps running in the background", "ไม่ให้แอปจาก Store ทำงานเบื้องหลัง"),
            L("Applies to every user of this PC. Only Microsoft Store apps are affected — normal desktop programs keep working as usual.",
              "มีผลกับผู้ใช้ทุกคนในเครื่อง และมีผลเฉพาะแอปจาก Microsoft Store — โปรแกรมทั่วไปบนเดสก์ท็อปยังทำงานตามปกติ"),
            TrickRisk.Moderate, RestartScope.None,
            new[] { Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2, null, 0) },
            warning: L("Store apps such as Mail, Calendar, Phone Link and some chat apps won't sync or show notifications until you open them.",
                       "แอปจาก Store เช่น เมล ปฏิทิน Phone Link และแอปแชทบางตัว จะไม่ซิงก์หรือแจ้งเตือนจนกว่าจะเปิดแอปนั้น")));
    }

    /// <summary>Per-user package registration of the Copilot app (fast registry check, no PowerShell).</summary>
    private static bool CopilotInstalled()
    {
        try
        {
            using var repo = Reg.OpenBase(HKCU).OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            return repo?.GetSubKeyNames().Any(n =>
                n.StartsWith("Microsoft.Copilot_", StringComparison.OrdinalIgnoreCase) &&
                n.EndsWith("_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<TrickResult> RemoveCopilotAsync(TrickRunContext ctx)
    {
        const string script =
            "$p = Get-AppxPackage -Name 'Microsoft.Copilot'\n" +
            "if ($p) { $p | Remove-AppxPackage }\n" +
            "if (Get-AppxPackage -Name 'Microsoft.Copilot') { exit 2 }";
        var r = await CommandRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(3), ctx.Token).ConfigureAwait(false);
        if (r.Cancelled) return TrickResult.Canceled(r.Output);
        if (r.Succeeded)
            return TrickResult.Ok(L("Done — the Copilot app was removed.", "เรียบร้อย — ลบแอป Copilot แล้ว"), output: r.Output);
        return TrickResult.Fail(r.ExitCode == 2
            ? L("Windows didn't remove the Copilot app (it may be reinstalled by your organization).",
                "Windows ไม่ยอมลบแอป Copilot (อาจถูกติดตั้งกลับโดยองค์กรของคุณ)")
            : TweakErrors.FromCommand(r, "PowerShell"), r.Output);
    }

    private static TrickResult OpenStoreForCopilot()
    {
        var open = ShellLauncher.OpenUnelevated(CopilotStoreUri);
        return open.Success
            ? TrickResult.Ok(L("Microsoft Store opened — click \"Get\" or \"Install\" to bring Copilot back.",
                               "เปิด Microsoft Store แล้ว — กด \"รับ\" หรือ \"ติดตั้ง\" เพื่อติดตั้ง Copilot กลับมา"))
            : open;
    }
}

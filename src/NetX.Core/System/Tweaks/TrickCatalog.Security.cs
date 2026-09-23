namespace NetX.Core.System.Tweaks;

public static partial class TrickCatalog
{
    private const string CfaKey = @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access";
    private const string CfaPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access";
    private const string FirewallKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
    private static readonly string[] FirewallProfiles = { "DomainProfile", "StandardProfile", "PublicProfile" };

    private static void AddSecurity(List<TrickDefinition> list, WindowsVersionInfo os)
    {
        list.Add(new TrickDefinition
        {
            Id = "failed-sign-ins",
            Category = Security,
            Icon = "ShieldIcon",
            Name = L("Failed sign-in attempts", "ความพยายามเข้าสู่ระบบที่ล้มเหลว"),
            Description = L("Shows the 25 most recent failed sign-ins from the Security log: when, which account and from where.",
                            "แสดงการเข้าสู่ระบบที่ล้มเหลว 25 ครั้งล่าสุดจาก Security log ว่าเกิดเมื่อไร บัญชีไหน และมาจากที่ใด"),
            Risk = TrickRisk.Safe,
            Technical = "Security log, event 4625 (newest first)",
            ShowsOutput = true,
            RunLabel = L("Show", "แสดง"),
            Run = ctx => Task.Run(() =>
            {
                var result = SecurityReports.FailedSignIns(ctx.Thai);
                if (!string.IsNullOrEmpty(result.Output)) ctx.Output.Append(result.Output);
                return result;
            })
        });

        list.Add(new TrickDefinition
        {
            Id = "controlled-folder-access",
            Category = Security,
            Icon = "ShieldIcon",
            Name = L("Ransomware protection (Controlled folder access)", "ป้องกันแรนซัมแวร์ (Controlled folder access)"),
            Description = L(
                "Microsoft Defender blocks unknown apps from changing files in Documents, Pictures, Desktop and other protected folders.",
                "Microsoft Defender จะบล็อกแอปที่ไม่รู้จักไม่ให้แก้ไขไฟล์ในโฟลเดอร์เอกสาร รูปภาพ เดสก์ท็อป และโฟลเดอร์ที่ป้องกันอื่น ๆ"),
            Warning = L("Many games and older apps will be blocked from saving to Documents (game saves, settings) until you allow them " +
                        "in Windows Security › Virus & threat protection › Ransomware protection.",
                        "เกมและแอปรุ่นเก่าหลายตัวจะบันทึกไฟล์ลงโฟลเดอร์เอกสารไม่ได้ (เช่น เซฟเกม การตั้งค่า) จนกว่าคุณจะอนุญาตใน " +
                        "ความปลอดภัยของ Windows › การป้องกันไวรัสและภัยคุกคาม › การป้องกันแรนซัมแวร์"),
            Risk = TrickRisk.Moderate,
            Technical = "Set-MpPreference -EnableControlledFolderAccess Enabled   (restore: Disabled)",
            SettingsUri = "windowsdefender://threat",
            SettingsLabel = L("Open Windows Security", "เปิดความปลอดภัยของ Windows"),
            AppliedLabel = L("On", "เปิดอยู่"),
            DefaultLabel = L("Off (Windows default)", "ปิดอยู่ (ค่าเริ่มต้นของ Windows)"),
            Detect = DetectControlledFolderAccess,
            Apply = ctx => SetControlledFolderAccess(ctx, enable: true),
            Revert = ctx => SetControlledFolderAccess(ctx, enable: false)
        });

        list.Add(new TrickDefinition
        {
            Id = "block-outbound",
            Category = Security,
            Icon = "WarningIcon",
            Name = L("Block all outgoing connections (firewall)", "บล็อกการเชื่อมต่อขาออกทั้งหมด (ไฟร์วอลล์)"),
            Description = L(
                "Windows Firewall blocks every program from reaching the network unless it has an \"allow\" rule. For advanced users who " +
                "manage their own firewall rules.",
                "ไฟร์วอลล์ของ Windows จะบล็อกทุกโปรแกรมไม่ให้เชื่อมต่อออกไป ยกเว้นโปรแกรมที่มีกฎ \"อนุญาต\" " +
                "เหมาะกับผู้ใช้ขั้นสูงที่จัดการกฎไฟร์วอลล์เอง"),
            Warning = L("Browsers, games, Windows Update and most apps lose internet access immediately. \"Restore default\" allows outgoing " +
                        "connections again — it does not delete any of your firewall rules.",
                        "เบราว์เซอร์ เกม Windows Update และแอปส่วนใหญ่จะใช้อินเทอร์เน็ตไม่ได้ทันที ปุ่ม \"คืนค่าเริ่มต้น\" จะกลับมาอนุญาตการเชื่อมต่อขาออก " +
                        "โดยไม่ลบกฎไฟร์วอลล์ใด ๆ ของคุณ"),
            Risk = TrickRisk.Dangerous,
            Technical = "netsh advfirewall set allprofiles firewallpolicy blockinbound,blockoutbound\n" +
                        "restore: netsh advfirewall set allprofiles firewallpolicy blockinbound,allowoutbound",
            Detect = DetectOutboundBlock,
            Apply = ctx => RunAndVerify(ctx, "netsh.exe", "advfirewall set allprofiles firewallpolicy blockinbound,blockoutbound", Short,
                () => DetectOutboundBlock().State == TrickState.Applied, TweakErrors.AppliedText),
            Revert = ctx => RunAndVerify(ctx, "netsh.exe", "advfirewall set allprofiles firewallpolicy blockinbound,allowoutbound", Short,
                () => DetectOutboundBlock().State == TrickState.Default, TweakErrors.RestoredText)
        });

        list.Add(new TrickDefinition
        {
            Id = "secure-boot",
            Category = Security,
            Icon = "ShieldIcon",
            Name = L("Check Secure Boot", "ตรวจสอบ Secure Boot"),
            Description = L("Tells you whether Secure Boot is on. Windows 11 and the anti-cheat systems of some games need it.",
                            "ตรวจว่า Secure Boot เปิดอยู่หรือไม่ ซึ่ง Windows 11 และระบบกันโกงของบางเกมต้องใช้"),
            Risk = TrickRisk.Safe,
            Technical = @"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled",
            RunLabel = L("Check", "ตรวจสอบ"),
            Run = _ => Task.FromResult(SecurityReports.SecureBoot())
        });

        list.Add(OpenTool("certificates", Security, "ShieldIcon",
            L("Certificate manager", "จัดการใบรับรอง (Certificates)"),
            L("Opens the certificate store of your account (certmgr.msc).", "เปิดที่เก็บใบรับรองของบัญชีคุณ (certmgr.msc)"),
            () => ShellLauncher.StartConsole("certmgr.msc"), technical: "certmgr.msc"));

        if (os.IsWindows10 || os.IsWindows11)
        {
            list.Add(new TrickDefinition
            {
                Id = "memory-integrity",
                Category = Security,
                Icon = "ShieldIcon",
                Name = L("Memory integrity (Core isolation)", "ความสมบูรณ์ของหน่วยความจำ (Core isolation)"),
                Description = L(
                    "Opens Windows Security › Device security › Core isolation. Turning Memory integrity off can give a few percent more FPS " +
                    "in some games, but it removes an important protection against malicious drivers. Microsoft recommends keeping it on.",
                    "เปิดหน้า ความปลอดภัยของ Windows › ความปลอดภัยของอุปกรณ์ › Core isolation การปิด Memory integrity อาจได้ FPS เพิ่มไม่กี่เปอร์เซ็นต์" +
                    "ในบางเกม แต่จะเสียการป้องกันสำคัญจากไดรเวอร์อันตราย Microsoft แนะนำให้เปิดไว้"),
                Risk = TrickRisk.Safe,
                Technical = "windowsdefender://coreisolation (WinXTools never changes this setting itself)",
                SettingsUri = "windowsdefender://coreisolation",
                SettingsLabel = L("Open Core isolation", "เปิด Core isolation"),
                Detect = () => Reg.ReadDword(HKLM,
                        @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") == 1
                    ? TrickStatus.Default(L("Memory integrity is ON.", "Memory integrity เปิดอยู่"))
                    : TrickStatus.Custom(L("Memory integrity is OFF.", "Memory integrity ปิดอยู่"))
            });
        }

        list.Add(RegToggle("uac-dimming", Security, "WarningIcon",
            L("Don't dim the screen for UAC prompts", "ไม่หรี่หน้าจอเมื่อมีหน้าต่าง UAC"),
            L("The \"Do you want to allow this app…\" prompt appears on your normal desktop instead of the dimmed secure desktop. " +
              "UAC itself stays on.",
              "หน้าต่าง \"คุณต้องการอนุญาตให้แอปนี้…\" จะแสดงบนเดสก์ท็อปปกติแทนหน้าจอที่หรี่มืด โดย UAC ยังเปิดอยู่เหมือนเดิม"),
            TrickRisk.Dangerous, RestartScope.None,
            new[] { Dword(HKLM, SystemPolicies, "PromptOnSecureDesktop", 0, 1) },
            warning: L("On the normal desktop, other programs could interfere with the prompt. Use this only if the dimming causes real " +
                       "problems (screen recording, remote support), and restore it afterwards.",
                       "บนเดสก์ท็อปปกติ โปรแกรมอื่นอาจเข้ามายุ่งกับหน้าต่างยืนยันได้ ใช้เฉพาะเมื่อการหรี่หน้าจอสร้างปัญหาจริง " +
                       "(เช่น อัดหน้าจอ หรือให้คนอื่นช่วยทางไกล) และควรคืนค่าหลังใช้เสร็จ")));
    }

    private static TrickStatus DetectControlledFolderAccess()
    {
        if (Reg.ReadDword(HKLM, CfaPolicyKey, "EnableControlledFolderAccess") is not null)
            return TrickStatus.NotSupported(L("Managed by your organization.", "ถูกควบคุมโดยองค์กรของคุณ"));

        return Reg.ReadDword(HKLM, CfaKey, "EnableControlledFolderAccess") switch
        {
            1 => TrickStatus.Applied(),
            null or 0 => TrickStatus.Default(),
            2 => TrickStatus.Custom(L("Audit mode (only logs, doesn't block).", "โหมดตรวจสอบ (บันทึกอย่างเดียว ไม่บล็อก)")),
            _ => TrickStatus.Custom()
        };
    }

    private static async Task<TrickResult> SetControlledFolderAccess(TrickRunContext ctx, bool enable)
    {
        var r = await CommandRunner.RunPowerShellAsync(
            $"Set-MpPreference -EnableControlledFolderAccess {(enable ? "Enabled" : "Disabled")}",
            TimeSpan.FromMinutes(1), ctx.Token).ConfigureAwait(false);
        if (r.Cancelled) return TrickResult.Canceled(r.Output);

        var state = DetectControlledFolderAccess().State;
        if (enable ? state == TrickState.Applied : state == TrickState.Default)
            return TrickResult.Ok(enable ? TweakErrors.AppliedText : TweakErrors.RestoredText);

        return TrickResult.Fail(r.Succeeded
            ? TweakErrors.NotVerified
            : L("Microsoft Defender didn't accept the change. It may be turned off because another antivirus is installed.",
                "Microsoft Defender ไม่ยอมรับการเปลี่ยนแปลง อาจเพราะถูกปิดอยู่เนื่องจากติดตั้งโปรแกรมป้องกันไวรัสตัวอื่นไว้"), r.Output);
    }

    /// <summary>Local firewall store: DefaultOutboundAction 1 = block (absent/0 = allow, the default).</summary>
    private static TrickStatus DetectOutboundBlock()
    {
        int blocked = FirewallProfiles.Count(p => Reg.ReadDword(HKLM, $@"{FirewallKey}\{p}", "DefaultOutboundAction") == 1);
        // Group Policy keeps its own copy (named PrivateProfile instead of StandardProfile).
        bool managed = new[] { "DomainProfile", "PrivateProfile", "PublicProfile" }.Any(p =>
            Reg.ReadDword(HKLM, $@"SOFTWARE\Policies\Microsoft\WindowsFirewall\{p}", "DefaultOutboundAction") is not null);
        var note = managed ? L("Your organization also sets firewall rules.", "องค์กรของคุณตั้งกฎไฟร์วอลล์ไว้ด้วย") : null;

        return blocked switch
        {
            3 => TrickStatus.Applied(note),
            0 => TrickStatus.Default(note),
            _ => TrickStatus.Custom(note ?? L("Blocked on some network profiles only.", "บล็อกเฉพาะบางโปรไฟล์เครือข่าย"))
        };
    }
}

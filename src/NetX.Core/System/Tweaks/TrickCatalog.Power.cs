using System.Globalization;
using Microsoft.Win32;

namespace NetX.Core.System.Tweaks;

public static partial class TrickCatalog
{
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";

    private sealed class PlanBackup
    {
        public string? Previous { get; set; }
    }

    private static void AddPower(List<TrickDefinition> list, WindowsVersionInfo os)
    {
        list.Add(RegToggle("fast-startup", Power, "SpeedIcon",
            L("Turn off Fast Startup", "ปิดการเริ่มต้นระบบอย่างรวดเร็ว (Fast Startup)"),
            L("Makes \"Shut down\" a real, full shutdown. Fixes drivers and updates that act up after shutting down, and lets a " +
              "second operating system read the Windows drive. Starting the PC may take a few seconds longer.",
              "ทำให้การ \"ปิดเครื่อง\" เป็นการปิดจริงทั้งหมด ช่วยแก้ปัญหาไดรเวอร์หรืออัปเดตที่ผิดปกติหลังเปิดเครื่อง " +
              "และทำให้ระบบปฏิบัติการอื่นในเครื่องอ่านไดรฟ์ Windows ได้ แต่อาจเปิดเครื่องช้าลงไม่กี่วินาที"),
            TrickRisk.Safe, RestartScope.None,
            new[] { Dword(HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1) },
            decorate: status =>
            {
                // Fast Startup only really runs when hibernation is enabled too.
                if (status.State == TrickState.Default && Reg.ReadDword(HKLM, PowerKey, "HibernateEnabled") == 0)
                    return status with
                    {
                        Note = L("Fast Startup is inactive anyway because hibernation is turned off.",
                                 "ตอนนี้ Fast Startup ไม่ได้ทำงานอยู่แล้ว เพราะปิดไฮเบอร์เนตไว้")
                    };
                return status;
            }));

        list.Add(new TrickDefinition
        {
            Id = "hibernation",
            Category = Power,
            Icon = "SettingsIcon",
            Name = L("Turn off hibernation", "ปิดไฮเบอร์เนต (Hibernate)"),
            Description = L(
                "Deletes hiberfil.sys and gives back disk space (roughly 40% of your RAM size). Hibernate and Fast Startup stop working.",
                "ลบไฟล์ hiberfil.sys คืนพื้นที่ดิสก์ประมาณ 40% ของขนาด RAM และจะใช้ไฮเบอร์เนตกับ Fast Startup ไม่ได้"),
            Warning = PowerPlans.HasBattery
                ? L("On a laptop, hibernation protects your open work when the battery runs out.",
                    "บนโน้ตบุ๊ก ไฮเบอร์เนตช่วยเก็บงานที่เปิดค้างไว้เมื่อแบตเตอรี่หมด")
                : null,
            Risk = PowerPlans.HasBattery ? TrickRisk.Moderate : TrickRisk.Safe,
            Technical = "powercfg /hibernate off   (restore: powercfg /hibernate on)",
            CopyText = "powercfg /hibernate off",
            Detect = () => Reg.ReadDword(HKLM, PowerKey, "HibernateEnabled") switch
            {
                0 => TrickStatus.Applied(),
                1 => TrickStatus.Default(),
                _ => TrickStatus.Unknown()
            },
            Apply = ctx => RunAndVerify(ctx, "powercfg.exe", "/hibernate off", Short,
                () => Reg.ReadDword(HKLM, PowerKey, "HibernateEnabled") == 0, TweakErrors.AppliedText),
            Revert = ctx => RunAndVerify(ctx, "powercfg.exe", "/hibernate on", Short,
                () => Reg.ReadDword(HKLM, PowerKey, "HibernateEnabled") == 1, TweakErrors.RestoredText)
        });

        if (!PowerPlans.IsModernStandby)
        {
            list.Add(new TrickDefinition
            {
                Id = "ultimate-plan",
                Category = Power,
                Icon = "SpeedIcon",
                Name = L("Ultimate Performance power plan", "แผนพลังงาน Ultimate Performance"),
                Description = L(
                    "Turns on Windows' hidden top-performance plan, which keeps the CPU ready at full speed. Meant for desktops; " +
                    "uses more power. WinXTools keeps a single copy of it and removes it again on Restore.",
                    "เปิดแผนพลังงานประสิทธิภาพสูงสุดที่ Windows ซ่อนไว้ ให้ CPU พร้อมทำงานเต็มที่ตลอดเวลา เหมาะกับคอมตั้งโต๊ะ ใช้ไฟมากขึ้น " +
                    "WinXTools สร้างแผนนี้ไว้เพียงชุดเดียว และลบออกเมื่อกดคืนค่า"),
                Warning = PowerPlans.HasBattery
                    ? L("This PC has a battery: expect much shorter battery life and more heat.", "เครื่องนี้มีแบตเตอรี่: แบตจะหมดเร็วขึ้นมากและเครื่องร้อนขึ้น")
                    : null,
                Risk = PowerPlans.HasBattery ? TrickRisk.Moderate : TrickRisk.Safe,
                Technical = $"Copy of e9a42b02-d5df-448d-aa00-03f14749eb61 as {PowerPlans.AppUltimate:D} → active plan",
                AppliedLabel = L("Active", "ใช้อยู่"),
                DefaultLabel = L("Not active", "ไม่ได้ใช้อยู่"),
                Detect = () => PlanStatus(PowerPlans.AppUltimate, null),
                Apply = _ => Task.Run(ApplyUltimatePlan),
                Revert = _ => Task.Run(RevertUltimatePlan)
            });

            if (PowerPlans.Exists(PowerPlans.HighPerformance))
            {
                list.Add(new TrickDefinition
                {
                    Id = "high-performance-plan",
                    Category = Power,
                    Icon = "SpeedIcon",
                    Name = L("High performance power plan", "แผนพลังงานประสิทธิภาพสูง (High performance)"),
                    Description = L(
                        "Switches to Windows' built-in High performance plan. Restore switches back to Balanced, the Windows default.",
                        "เปลี่ยนเป็นแผนพลังงานประสิทธิภาพสูงที่มากับ Windows ปุ่มคืนค่าจะเปลี่ยนกลับเป็นแผน \"สมดุล\" ซึ่งเป็นค่าเริ่มต้นของ Windows"),
                    Risk = PowerPlans.HasBattery ? TrickRisk.Moderate : TrickRisk.Safe,
                    Warning = PowerPlans.HasBattery
                        ? L("This PC has a battery: expect shorter battery life.", "เครื่องนี้มีแบตเตอรี่: แบตจะหมดเร็วขึ้น")
                        : null,
                    Technical = $"Active plan → {PowerPlans.HighPerformance:D} (restore: {PowerPlans.Balanced:D})",
                    AppliedLabel = L("Active", "ใช้อยู่"),
                    DefaultLabel = L("Balanced (Windows default)", "สมดุล (ค่าเริ่มต้นของ Windows)"),
                    CustomLabel = L("Another plan is active", "ใช้แผนพลังงานอื่นอยู่"),
                    Detect = () => PlanStatus(PowerPlans.HighPerformance, PowerPlans.Balanced),
                    Apply = _ => Task.Run(() => SwitchPlan(PowerPlans.HighPerformance, TweakErrors.AppliedText)),
                    Revert = _ => Task.Run(() => SwitchPlan(PowerPlans.Balanced, TweakErrors.RestoredText))
                });
            }
        }

        list.Add(RegToggle("startup-delay", Power, "SpeedIcon",
            L("Start startup apps without waiting", "ให้แอปเริ่มต้นเปิดทันทีหลังเข้าสู่ระบบ"),
            L("Windows waits a few seconds after sign-in before it starts your startup apps. This removes the wait; the first moments " +
              "after sign-in may feel busier.",
              "ปกติ Windows จะรอสักครู่หลังเข้าสู่ระบบก่อนเปิดแอปเริ่มต้น ตัวเลือกนี้ยกเลิกการรอ ช่วงแรกหลังเข้าสู่ระบบเครื่องอาจดูทำงานหนักขึ้น"),
            TrickRisk.Safe, RestartScope.None,
            new[] { Dword(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, null) },
            tip: L("Takes effect the next time you sign in.", "มีผลในการเข้าสู่ระบบครั้งถัดไป")));

        if (PowerPlans.HasBattery)
        {
            list.Add(new TrickDefinition
            {
                Id = "battery-report",
                Category = Power,
                Icon = "SpeedIcon",
                Name = L("Battery health report", "รายงานสุขภาพแบตเตอรี่"),
                Description = L(
                    "Creates battery-report.html on your Desktop — design vs. current capacity and usage history — and opens it.",
                    "สร้างไฟล์ battery-report.html บนเดสก์ท็อป แสดงความจุตอนใหม่เทียบกับปัจจุบันและประวัติการใช้งาน แล้วเปิดให้ดู"),
                Risk = TrickRisk.Safe,
                Technical = "powercfg /batteryreport /output \"%USERPROFILE%\\Desktop\\battery-report.html\"",
                CopyText = "powercfg /batteryreport",
                ShowsOutput = true,
                RunLabel = L("Create report", "สร้างรายงาน"),
                Run = ctx => CreateReport(ctx, "battery-report.html", "/batteryreport /output {0}", TimeSpan.FromMinutes(2))
            });
        }

        list.Add(new TrickDefinition
        {
            Id = "energy-report",
            Category = Power,
            Icon = "SpeedIcon",
            Name = L("Power efficiency report", "รายงานประสิทธิภาพการใช้พลังงาน"),
            Description = L(
                "Watches the PC for 60 seconds, then writes energy-report.html to your Desktop listing power problems it found " +
                "(e.g. devices that keep the PC awake).",
                "ตรวจสอบเครื่อง 60 วินาที แล้วสร้างไฟล์ energy-report.html บนเดสก์ท็อป บอกปัญหาด้านพลังงานที่พบ " +
                "(เช่น อุปกรณ์ที่ทำให้เครื่องไม่ยอมเข้าโหมดสลีป)"),
            Tip = L("Close other programs while it runs for a cleaner result.", "ปิดโปรแกรมอื่นระหว่างตรวจ เพื่อให้ผลแม่นยำขึ้น"),
            Risk = TrickRisk.Safe,
            Technical = "powercfg /energy /duration 60 /output \"%USERPROFILE%\\Desktop\\energy-report.html\"",
            CopyText = "powercfg /energy",
            ShowsOutput = true,
            Cancellable = true,
            RunLabel = L("Create report", "สร้างรายงาน"),
            Run = ctx => CreateReport(ctx, "energy-report.html", "/energy /duration 60 /output {0}", TimeSpan.FromMinutes(3))
        });

        list.Add(new TrickDefinition
        {
            Id = "shutdown-timer",
            Category = Power,
            Icon = "SettingsIcon",
            Name = L("Shut down after a timer", "ตั้งเวลาปิดเครื่อง"),
            Description = L("Turns the PC off after the number of minutes you choose.", "ปิดเครื่องอัตโนมัติเมื่อครบจำนวนนาทีที่กำหนด"),
            Warning = L("When the time is up, open apps are closed WITHOUT saving. Save your work first, or use \"Cancel scheduled shutdown\".",
                        "เมื่อครบเวลา แอปที่เปิดอยู่จะถูกปิดโดยไม่บันทึกงาน ควรบันทึกงานไว้ก่อน หรือใช้ \"ยกเลิกการปิดเครื่องที่ตั้งไว้\""),
            Risk = TrickRisk.Moderate,
            Technical = "shutdown /s /t <seconds>",
            RunLabel = L("Set timer", "ตั้งเวลา"),
            InputPrompt = L("Shut down in how many minutes? (1–1440)", "ปิดเครื่องในอีกกี่นาที? (1–1440)"),
            InputDefault = "60",
            Run = ScheduleShutdownAsync
        });

        list.Add(new TrickDefinition
        {
            Id = "cancel-shutdown",
            Category = Power,
            Icon = "SettingsIcon",
            Name = L("Cancel scheduled shutdown", "ยกเลิกการปิดเครื่องที่ตั้งไว้"),
            Description = L("Stops a shutdown or restart that is counting down.", "ยกเลิกการปิดหรือรีสตาร์ทเครื่องที่กำลังนับถอยหลังอยู่"),
            Risk = TrickRisk.Safe,
            Technical = "shutdown /a",
            CopyText = "shutdown /a",
            RunLabel = L("Cancel it", "ยกเลิกเลย"),
            Run = CancelShutdownAsync
        });

        list.Add(OpenTool("startup-apps", Power, "SpeedIcon",
            L("Manage startup apps", "จัดการแอปที่เปิดพร้อม Windows"),
            L("Opens Task Manager on the Startup apps page, where you can stop programs from starting with Windows.",
              "เปิดตัวจัดการงานที่หน้าแอปเริ่มต้น เลือกปิดโปรแกรมที่ไม่ต้องการให้เปิดพร้อม Windows ได้"),
            () => ShellLauncher.StartTool(CommandRunner.SystemTool("Taskmgr.exe"), "/0 /startup"),
            technical: "taskmgr /0 /startup",
            settingsUri: os.Build >= 19041 ? "ms-settings:startupapps" : null));

        list.Add(OpenTool("msconfig", Power, "SettingsIcon",
            L("System Configuration (msconfig)", "การกำหนดค่าระบบ (msconfig)"),
            L("Classic tool for boot options and diagnostic startup. Wrong boot settings can stop Windows from starting — " +
              "change only what you understand.",
              "เครื่องมือแบบเดิมสำหรับตั้งค่าการบูตและการเริ่มระบบแบบวินิจฉัย ถ้าตั้งค่าการบูตผิดอาจทำให้เปิด Windows ไม่ได้ — " +
              "ควรแก้เฉพาะสิ่งที่เข้าใจ"),
            () => ShellLauncher.StartTool(CommandRunner.SystemTool("msconfig.exe")),
            TrickRisk.Moderate, technical: "msconfig"));

        list.Add(OpenTool("autoruns", Power, "SpeedIcon",
            L("Autoruns (Microsoft Sysinternals)", "Autoruns (Microsoft Sysinternals)"),
            L("Opens Microsoft's download page for Autoruns, the most complete startup manager.",
              "เปิดหน้าดาวน์โหลด Autoruns ของ Microsoft ซึ่งเป็นเครื่องมือจัดการโปรแกรมเริ่มต้นที่ละเอียดที่สุด"),
            () => ShellLauncher.OpenUnelevated("https://learn.microsoft.com/sysinternals/downloads/autoruns"),
            technical: "https://learn.microsoft.com/sysinternals/downloads/autoruns"));
    }

    private static TrickStatus PlanStatus(Guid applied, Guid? defaultPlan)
    {
        var active = PowerPlans.GetActive();
        if (active == null) return TrickStatus.Unknown();
        if (active == applied) return TrickStatus.Applied();
        if (active == defaultPlan) return TrickStatus.Default();

        // Name the plan only when the badge doesn't already say which one it is.
        var name = PowerPlans.GetName(active.Value) ?? active.Value.ToString("D");
        var note = L($"Current plan: {name}", $"แผนปัจจุบัน: {name}");
        return defaultPlan == null ? TrickStatus.Default(note) : TrickStatus.Custom(note);
    }

    private static TrickResult SwitchPlan(Guid plan, LocText okText)
    {
        if (!PowerPlans.Exists(plan))
            return TrickResult.Fail(L("This power plan isn't available on this PC.", "เครื่องนี้ไม่มีแผนพลังงานนี้"));
        return PowerPlans.SetActive(plan)
            ? TrickResult.Ok(okText)
            : TrickResult.Fail(L("Windows didn't switch the power plan (it may be managed by your organization).",
                                 "Windows ไม่ยอมเปลี่ยนแผนพลังงาน (อาจถูกควบคุมโดยองค์กร)"));
    }

    private static TrickResult ApplyUltimatePlan()
    {
        var active = PowerPlans.GetActive();
        if (active == PowerPlans.AppUltimate) return TrickResult.Ok(TweakErrors.AppliedText);

        // Remember the plan to go back to BEFORE switching.
        try
        {
            SecureAppData.Save(PowerPlans.TricksPlanStateFile, new PlanBackup { Previous = active?.ToString("D") });
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(new LocText(
                "Couldn't save which power plan you use now, so nothing was changed. " + TweakErrors.Friendly(ex).En,
                "บันทึกแผนพลังงานปัจจุบันไม่ได้ จึงยังไม่ได้เปลี่ยนแปลงอะไร " + TweakErrors.Friendly(ex).Th));
        }

        if (!PowerPlans.EnsureAppUltimatePlan())
        {
            SecureAppData.Delete(PowerPlans.TricksPlanStateFile);
            return TrickResult.Fail(L("Windows couldn't create the Ultimate Performance plan on this PC.",
                                      "Windows สร้างแผน Ultimate Performance บนเครื่องนี้ไม่ได้"));
        }

        if (!PowerPlans.SetActive(PowerPlans.AppUltimate))
        {
            SecureAppData.Delete(PowerPlans.TricksPlanStateFile);
            PowerPlans.DeleteAppUltimatePlanIfUnused();
            return TrickResult.Fail(L("Windows didn't switch to the Ultimate Performance plan.",
                                      "Windows ไม่ยอมเปลี่ยนเป็นแผน Ultimate Performance"));
        }

        return TrickResult.Ok(TweakErrors.AppliedText);
    }

    private static TrickResult RevertUltimatePlan()
    {
        var backup = SecureAppData.Load<PlanBackup>(PowerPlans.TricksPlanStateFile);
        var plans = PowerPlans.List();

        Guid target = PowerPlans.Balanced;
        if (Guid.TryParse(backup?.Previous, out var previous) && plans.Contains(previous) && previous != PowerPlans.AppUltimate)
            target = previous;

        if (PowerPlans.GetActive() == PowerPlans.AppUltimate && !PowerPlans.SetActive(target))
            return TrickResult.Fail(L("Windows didn't switch back to your previous power plan.",
                                      "Windows ไม่ยอมเปลี่ยนกลับเป็นแผนพลังงานเดิม"));

        SecureAppData.Delete(PowerPlans.TricksPlanStateFile);

        // Gamer Mode may still be using the same plan; it removes it on its own revert.
        if (!GameModeService.Instance.IsActive)
            PowerPlans.DeleteAppUltimatePlanIfUnused();

        var name = PowerPlans.GetName(target) ?? target.ToString("D");
        return TrickResult.Ok(L($"Done — power plan is back to \"{name}\".", $"เรียบร้อย — กลับไปใช้แผนพลังงาน \"{name}\" แล้ว"));
    }

    /// <summary>powercfg report written to the Desktop, then opened un-elevated.</summary>
    private static async Task<TrickResult> CreateReport(TrickRunContext ctx, string fileName, string argsFormat, TimeSpan timeout)
    {
        var path = DesktopFile(fileName);
        var started = DateTime.UtcNow.AddSeconds(-5);
        var args = string.Format(CultureInfo.InvariantCulture, argsFormat, CommandRunner.Quote(path));

        ctx.Output.AppendLine($"> powercfg {args}");
        var r = await CommandRunner.RunToolAsync("powercfg.exe", args, timeout, ctx.Token, ctx.Output).ConfigureAwait(false);
        if (r.Cancelled) return TrickResult.Canceled(r.Output);

        // powercfg /energy exits non-zero when it FINDS problems, so trust the file, not the exit code.
        var info = new FileInfo(path);
        if (info.Exists && info.LastWriteTimeUtc >= started)
        {
            return new TrickResult
            {
                Success = true,
                Output = r.Output,
                OpenAfter = path,
                Message = L($"Report saved to your Desktop: {fileName}", $"บันทึกรายงานไว้บนเดสก์ท็อปแล้ว: {fileName}")
            };
        }
        return TrickResult.Fail(TweakErrors.FromCommand(r, "powercfg"), r.Output);
    }

    private static async Task<TrickResult> ScheduleShutdownAsync(TrickRunContext ctx)
    {
        if (!int.TryParse(ctx.Input?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
            minutes < 1 || minutes > 1440)
            return TrickResult.Fail(L("Please enter a whole number of minutes from 1 to 1440.", "กรุณาใส่จำนวนนาทีเป็นตัวเลข 1 ถึง 1440"));

        var r = await CommandRunner.RunToolAsync("shutdown.exe", $"/s /t {minutes * 60}", Short, ctx.Token).ConfigureAwait(false);
        if (r.Succeeded)
        {
            var at = DateTime.Now.AddMinutes(minutes);
            return TrickResult.Ok(L($"The PC will shut down at {at:HH:mm}.", $"เครื่องจะปิดเวลา {at:HH:mm} น."));
        }
        if (r.ExitCode == 1190) // ERROR_SHUTDOWN_IS_SCHEDULED
            return TrickResult.Fail(L("A shutdown is already scheduled. Cancel it first.", "มีการตั้งเวลาปิดเครื่องไว้แล้ว กรุณายกเลิกก่อน"));
        return TrickResult.Fail(TweakErrors.FromCommand(r, "shutdown"), r.Output);
    }

    private static async Task<TrickResult> CancelShutdownAsync(TrickRunContext ctx)
    {
        var r = await CommandRunner.RunToolAsync("shutdown.exe", "/a", Short, ctx.Token).ConfigureAwait(false);
        if (r.Succeeded)
            return TrickResult.Ok(L("The scheduled shutdown was cancelled.", "ยกเลิกการปิดเครื่องที่ตั้งไว้แล้ว"));
        if (r.ExitCode == 1116) // ERROR_NO_SHUTDOWN_IN_PROGRESS
            return TrickResult.Ok(L("No shutdown was scheduled.", "ไม่มีการตั้งเวลาปิดเครื่องไว้"));
        return TrickResult.Fail(TweakErrors.FromCommand(r, "shutdown"), r.Output);
    }
}

namespace NetX.Core.System.Tweaks;

public static partial class TrickCatalog
{
    private static void AddRepair(List<TrickDefinition> list, WindowsVersionInfo os)
    {
        list.Add(new TrickDefinition
        {
            Id = "repair-system-files",
            Category = Repair,
            Icon = "ShieldIcon",
            Name = L("Repair Windows system files (DISM + SFC)", "ซ่อมไฟล์ระบบ Windows (DISM + SFC)"),
            Description = L(
                "First repairs the Windows image (DISM /RestoreHealth — it may download files from Windows Update), then checks and fixes " +
                "system files (SFC). Takes 10–40 minutes.",
                "ซ่อมอิมเมจของ Windows ก่อน (DISM /RestoreHealth ซึ่งอาจดาวน์โหลดไฟล์จาก Windows Update) แล้วตรวจและซ่อมไฟล์ระบบ (SFC) " +
                "ใช้เวลา 10–40 นาที"),
            Risk = TrickRisk.Safe,
            Technical = "DISM /Online /Cleanup-Image /RestoreHealth\nsfc /scannow",
            CopyText = "DISM /Online /Cleanup-Image /RestoreHealth\r\nsfc /scannow",
            ShowsOutput = true,
            Cancellable = true,
            RunLabel = L("Start repair", "เริ่มซ่อม"),
            Run = RepairSystemFilesAsync
        });

        list.Add(new TrickDefinition
        {
            Id = "sfc",
            Category = Repair,
            Icon = "ShieldIcon",
            Name = L("Scan system files (SFC)", "สแกนไฟล์ระบบ (SFC)"),
            Description = L("Checks protected Windows files and replaces damaged ones from the local backup copy. Takes 5–20 minutes.",
                            "ตรวจไฟล์ระบบที่ Windows ป้องกันไว้ และแทนที่ไฟล์ที่เสียด้วยสำเนาในเครื่อง ใช้เวลา 5–20 นาที"),
            Risk = TrickRisk.Safe,
            Technical = "sfc /scannow",
            CopyText = "sfc /scannow",
            ShowsOutput = true,
            Cancellable = true,
            RunLabel = L("Scan", "สแกน"),
            Run = ctx => RunTool(ctx, "sfc.exe", "/scannow", TimeSpan.FromMinutes(60),
                L("SFC finished. The last lines above say whether it found or repaired anything; restart the PC if it did.",
                  "SFC ทำงานเสร็จแล้ว ดูบรรทัดท้าย ๆ ด้านบนว่าพบหรือซ่อมอะไรหรือไม่ ถ้ามีการซ่อมให้รีสตาร์ทเครื่อง"))
        });

        list.Add(new TrickDefinition
        {
            Id = "dism-checkhealth",
            Category = Repair,
            Icon = "ShieldIcon",
            Name = L("Quick Windows image check (DISM)", "ตรวจอิมเมจ Windows แบบเร็ว (DISM)"),
            Description = L("Checks whether Windows has already recorded damage in its component store. Takes a few seconds and changes nothing.",
                            "ตรวจว่า Windows เคยบันทึกความเสียหายในที่เก็บคอมโพเนนต์ไว้หรือไม่ ใช้เวลาไม่กี่วินาทีและไม่เปลี่ยนแปลงอะไร"),
            Risk = TrickRisk.Safe,
            Technical = "DISM /Online /Cleanup-Image /CheckHealth",
            CopyText = "DISM /Online /Cleanup-Image /CheckHealth",
            ShowsOutput = true,
            Cancellable = true,
            RunLabel = L("Check", "ตรวจ"),
            Run = ctx => RunTool(ctx, "dism.exe", "/Online /Cleanup-Image /CheckHealth", TimeSpan.FromMinutes(10),
                L("Check finished — see the result above.", "ตรวจเสร็จแล้ว — ดูผลด้านบน"))
        });

        var systemDrive = (Path.GetPathRoot(CommandRunner.WindowsDirectory) ?? @"C:\").TrimEnd('\\');
        list.Add(new TrickDefinition
        {
            Id = "chkdsk-scan",
            Category = Repair,
            Icon = "FolderIcon",
            Name = L($"Scan drive {systemDrive} for errors", $"สแกนไดรฟ์ {systemDrive} หาข้อผิดพลาด"),
            Description = L(
                "Checks the file system online (chkdsk /scan) while you keep working. If it finds problems it can't fix online, you'll be " +
                "told how to repair them at the next restart.",
                "ตรวจระบบไฟล์แบบออนไลน์ (chkdsk /scan) ระหว่างที่ใช้งานเครื่องได้ตามปกติ ถ้าพบปัญหาที่ซ่อมทันทีไม่ได้ " +
                "จะแจ้งวิธีซ่อมในการรีสตาร์ทครั้งถัดไป"),
            Risk = TrickRisk.Safe,
            Technical = $"chkdsk {systemDrive} /scan",
            CopyText = $"chkdsk {systemDrive} /scan",
            ShowsOutput = true,
            Cancellable = true,
            RunLabel = L("Scan", "สแกน"),
            Run = ctx => ChkdskScanAsync(ctx, systemDrive)
        });

        list.Add(new TrickDefinition
        {
            Id = "systeminfo",
            Category = Repair,
            Icon = "InfoIcon",
            Name = L("System information", "ข้อมูลระบบ"),
            Description = L("Hardware and Windows details: install date, uptime, memory, installed updates and network cards (systeminfo).",
                            "รายละเอียดฮาร์ดแวร์และ Windows เช่น วันที่ติดตั้ง ระยะเวลาที่เปิดเครื่อง หน่วยความจำ อัปเดต และการ์ดเครือข่าย (systeminfo)"),
            Risk = TrickRisk.Safe,
            Technical = "systeminfo",
            CopyText = "systeminfo",
            ShowsOutput = true,
            Cancellable = true,
            RunLabel = L("Show", "แสดง"),
            Run = ctx => RunTool(ctx, "systeminfo.exe", "", Medium, L("Done.", "เสร็จแล้ว"))
        });

        list.Add(new TrickDefinition
        {
            Id = "driverquery",
            Category = Repair,
            Icon = "SettingsIcon",
            Name = L("Installed drivers", "ไดรเวอร์ที่ติดตั้ง"),
            Description = L("Lists all installed drivers with their state and file path (driverquery).",
                            "แสดงไดรเวอร์ทั้งหมดที่ติดตั้ง พร้อมสถานะและที่อยู่ไฟล์ (driverquery)"),
            Risk = TrickRisk.Safe,
            Technical = "driverquery /v",
            CopyText = "driverquery /v",
            ShowsOutput = true,
            Cancellable = true,
            RunLabel = L("Show", "แสดง"),
            Run = ctx => RunTool(ctx, "driverquery.exe", "/v", Medium, L("Done.", "เสร็จแล้ว"))
        });

        list.Add(new TrickDefinition
        {
            Id = "tasklist",
            Category = Repair,
            Icon = "TerminalIcon",
            Name = L("Running processes", "โปรเซสที่กำลังทำงาน"),
            Description = L("Lists every running program with its memory use and window title (tasklist /v).",
                            "แสดงโปรแกรมทั้งหมดที่ทำงานอยู่ พร้อมการใช้หน่วยความจำและชื่อหน้าต่าง (tasklist /v)"),
            Risk = TrickRisk.Safe,
            Technical = "tasklist /v",
            CopyText = "tasklist /v",
            ShowsOutput = true,
            Cancellable = true,
            RunLabel = L("Show", "แสดง"),
            Run = ctx => RunTool(ctx, "tasklist.exe", "/v", Medium, L("Done.", "เสร็จแล้ว"))
        });

        list.Add(OpenTool("reliability-monitor", Repair, "InfoIcon",
            L("Reliability Monitor", "ตัวตรวจสอบความน่าเชื่อถือ (Reliability Monitor)"),
            L("A timeline of app crashes, failed updates and Windows errors — the quickest way to see what went wrong and when.",
              "ไทม์ไลน์ของแอปที่แครช อัปเดตที่ล้มเหลว และข้อผิดพลาดของ Windows ดูได้เร็วที่สุดว่าเกิดปัญหาอะไรเมื่อไร"),
            () => ShellLauncher.StartTool(CommandRunner.SystemTool("perfmon.exe"), "/rel"), technical: "perfmon /rel"));

        list.Add(OpenTool("resource-monitor", Repair, "SpeedIcon",
            L("Resource Monitor", "ตัวตรวจสอบทรัพยากร (Resource Monitor)"),
            L("Live CPU, memory, disk and network use per program.", "ดูการใช้ CPU หน่วยความจำ ดิสก์ และเครือข่ายของแต่ละโปรแกรมแบบเรียลไทม์"),
            () => ShellLauncher.StartTool(CommandRunner.SystemTool("resmon.exe")), technical: "resmon"));

        if (File.Exists(CommandRunner.SystemTool("psr.exe")))
        {
            list.Add(OpenTool("steps-recorder", Repair, "InfoIcon",
                L("Steps Recorder", "ตัวบันทึกขั้นตอน (Steps Recorder)"),
                L("Records your clicks with screenshots so you can show someone how a problem happens. Microsoft is retiring this tool.",
                  "บันทึกการคลิกพร้อมภาพหน้าจอ เพื่อส่งให้คนอื่นดูว่าปัญหาเกิดขึ้นอย่างไร (Microsoft กำลังจะเลิกใช้เครื่องมือนี้)"),
                () => ShellLauncher.StartTool(CommandRunner.SystemTool("psr.exe")), technical: "psr"));
        }

        list.Add(new TrickDefinition
        {
            Id = "restart-explorer",
            Category = Repair,
            Icon = "TerminalIcon",
            Name = L("Restart Windows Explorer", "รีสตาร์ท Windows Explorer"),
            Description = L("Restarts the taskbar, Start and File Explorer windows, so changes apply without restarting the PC.",
                            "รีสตาร์ทแถบงาน เมนูเริ่ม และหน้าต่าง File Explorer เพื่อให้การตั้งค่าใหม่มีผลโดยไม่ต้องรีสตาร์ทเครื่อง"),
            Warning = L("Open File Explorer windows will close. Wait for file copies or moves to finish first.",
                        "หน้าต่าง File Explorer ที่เปิดอยู่จะถูกปิด ควรรอให้การคัดลอกหรือย้ายไฟล์เสร็จก่อน"),
            Risk = TrickRisk.Moderate,
            Technical = "Exit Explorer cleanly, then start it again with your normal (non-administrator) account",
            RunLabel = L("Restart", "รีสตาร์ท"),
            Run = _ => Task.Run(ExplorerRestarter.Restart)
        });
    }

    private static async Task<TrickResult> RepairSystemFilesAsync(TrickRunContext ctx)
    {
        var dism = await RunTool(ctx, "dism.exe", "/Online /Cleanup-Image /RestoreHealth", Long,
            L("DISM finished.", "DISM ทำงานเสร็จแล้ว")).ConfigureAwait(false);
        if (dism.Cancelled) return dism;

        // SFC runs even if DISM couldn't reach Windows Update — it repairs from the local store.
        var sfc = await RunTool(ctx, "sfc.exe", "/scannow", TimeSpan.FromMinutes(60),
            L("SFC finished.", "SFC ทำงานเสร็จแล้ว")).ConfigureAwait(false);
        if (sfc.Cancelled) return sfc;

        if (dism.Success && sfc.Success)
            return TrickResult.Ok(L(
                "Repair finished. The last lines above say whether anything was repaired; restart the PC if it was.",
                "ซ่อมเสร็จแล้ว ดูบรรทัดท้าย ๆ ด้านบนว่ามีการซ่อมอะไรหรือไม่ ถ้ามีให้รีสตาร์ทเครื่อง"));

        return TrickResult.Fail(L(
            $"DISM: {(dism.Success ? "OK" : "reported an error")}. SFC: {(sfc.Success ? "OK" : "reported an error")}. " +
            "See the details above. If DISM couldn't download files, check your internet connection and try again.",
            $"DISM: {(dism.Success ? "สำเร็จ" : "มีข้อผิดพลาด")} SFC: {(sfc.Success ? "สำเร็จ" : "มีข้อผิดพลาด")} " +
            "ดูรายละเอียดด้านบน ถ้า DISM ดาวน์โหลดไฟล์ไม่ได้ ให้ตรวจสอบอินเทอร์เน็ตแล้วลองใหม่"));
    }

    private static async Task<TrickResult> ChkdskScanAsync(TrickRunContext ctx, string drive)
    {
        ctx.Output.AppendLine($"> chkdsk {drive} /scan");
        var r = await CommandRunner.RunToolAsync("chkdsk.exe", $"{drive} /scan", TimeSpan.FromMinutes(60), ctx.Token, ctx.Output)
            .ConfigureAwait(false);
        if (r.Cancelled) return TrickResult.Canceled(r.Output);
        if (r.StartFailed || r.TimedOut) return TrickResult.Fail(TweakErrors.FromCommand(r, "chkdsk"), r.Output);

        // chkdsk: 0 = no errors, 1 = errors fixed, 2/3 = problems that need an offline repair.
        return r.ExitCode switch
        {
            0 => TrickResult.Ok(L("No problems were found.", "ไม่พบปัญหา")),
            1 => TrickResult.Ok(L("Problems were found and fixed.", "พบปัญหาและซ่อมเรียบร้อยแล้ว")),
            _ => new TrickResult
            {
                Success = false,
                Restart = RestartScope.Reboot,
                Output = r.Output,
                Message = L(
                    $"Problems were found that need a repair while Windows is not running. Run \"chkdsk {drive} /spotfix\" " +
                    "in an administrator terminal and restart the PC (the repair takes a moment during startup).",
                    $"พบปัญหาที่ต้องซ่อมขณะ Windows ไม่ได้ทำงาน ให้รัน \"chkdsk {drive} /spotfix\" ในเทอร์มินัลแบบผู้ดูแลระบบ " +
                    "แล้วรีสตาร์ทเครื่อง (การซ่อมใช้เวลาสักครู่ระหว่างเปิดเครื่อง)")
            }
        };
    }
}

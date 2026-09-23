using System.Text;

namespace NetX.Core.System.Tweaks;

public static partial class TrickCatalog
{
    private static void AddGaming(List<TrickDefinition> list, WindowsVersionInfo os)
    {
        list.Add(new TrickDefinition
        {
            Id = "system-report",
            Category = Gaming,
            Icon = "InfoIcon",
            Name = L("What this PC supports", "เครื่องนี้รองรับอะไรบ้าง"),
            Description = L(
                $"{os.FriendlyName}. Shows your exact Windows version, edition and hardware, and which tricks apply to this PC.",
                $"{os.FriendlyName} — ดูเวอร์ชัน รุ่น และฮาร์ดแวร์ของ Windows เครื่องนี้ พร้อมบอกว่าเทคนิคไหนใช้ได้บ้าง"),
            Risk = TrickRisk.Safe,
            ShowsOutput = true,
            RunLabel = L("Show", "ดูรายละเอียด"),
            Run = ctx => Task.Run(() =>
            {
                ctx.Output.Append(SystemReport(os, ctx.Thai));
                return TrickResult.Ok();
            })
        });

        var gm = GameModeService.Instance;
        list.Add(new TrickDefinition
        {
            Id = "gamer-mode",
            Category = Gaming,
            Icon = "SpeedIcon",
            Name = L("Gamer Mode (one click)", "โหมดเกมเมอร์ (คลิกเดียว)"),
            Description = L(
                "Turns off Game Bar background recording, uses exclusive fullscreen, lifts multimedia network throttling, " +
                "turns on GPU scheduling if your graphics card supports it and, on desktops, stops background power throttling " +
                "and switches to the Ultimate Performance power plan. Your current values are backed up first.",
                "ปิดการอัดวิดีโอเบื้องหลังของ Game Bar ใช้โหมดเต็มจอแบบ exclusive ปิดการจำกัดเครือข่ายขณะเล่นสื่อ " +
                "เปิด GPU scheduling ถ้าการ์ดจอรองรับ และสำหรับคอมตั้งโต๊ะจะปิดการลดพลังงานแอปเบื้องหลังและเปลี่ยนเป็นแผนพลังงาน " +
                "Ultimate Performance — ค่าปัจจุบันทั้งหมดจะถูกสำรองไว้ก่อน"),
            Tip = L(
                "\"Restore default\" puts back exactly the values you had before. Restart the PC afterwards so every change takes effect.",
                "ปุ่ม \"คืนค่าเริ่มต้น\" จะคืนค่าเดิมของคุณทุกอย่าง แนะนำให้รีสตาร์ทเครื่องหลังเปิดใช้ เพื่อให้ทุกการเปลี่ยนแปลงมีผล"),
            Risk = TrickRisk.Moderate,
            Restart = RestartScope.Reboot,
            Technical = gm.DescribeTweaks(),
            AppliedLabel = L("Gamer Mode is on", "เปิดโหมดเกมเมอร์อยู่"),
            DefaultLabel = L("Off", "ปิดอยู่"),
            CustomLabel = L("Partly restored — press Restore again", "คืนค่ายังไม่ครบ — กดคืนค่าอีกครั้ง"),
            Detect = gm.DetectStatus,
            Apply = _ => Task.Run(() => gm.ApplyGameMode().ToTrickResult()),
            Revert = _ => Task.Run(() => gm.RevertGameMode().ToTrickResult())
        });

        list.Add(RegToggle("game-dvr", Gaming, "SpeedIcon",
            L("Turn off Game Bar background recording", "ปิดการอัดวิดีโอเบื้องหลังของ Game Bar"),
            L("Stops Xbox Game Bar from recording gameplay in the background (Game DVR). Can give a few more FPS on slower PCs. " +
              "Recording clips with Win+Alt+R won't work until you restore it.",
              "หยุดไม่ให้ Xbox Game Bar อัดวิดีโอเกมไว้เบื้องหลัง (Game DVR) อาจได้ FPS เพิ่มเล็กน้อยในเครื่องที่สเปกไม่แรง " +
              "แต่จะอัดคลิปด้วย Win+Alt+R ไม่ได้จนกว่าจะคืนค่า"),
            TrickRisk.Safe, RestartScope.None,
            new[]
            {
                Dword(HKCU, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                Dword(HKCU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1),
                Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, null, 1)
            },
            tip: L("Takes effect the next time you start a game.", "มีผลเมื่อเปิดเกมครั้งถัดไป")));

        list.Add(RegToggle("fso-off", Gaming, "SpeedIcon",
            L("Use exclusive fullscreen (turn off fullscreen optimizations)", "ใช้โหมดเต็มจอแบบ exclusive (ปิด Fullscreen Optimizations)"),
            L("Asks games to use classic exclusive fullscreen instead of Windows' optimized mode. It can lower input lag in some older " +
              "DirectX 9–11 games; on Windows 11 the optimized mode is usually just as fast — test with your own games.",
              "ให้เกมใช้โหมดเต็มจอแบบ exclusive แทนโหมดที่ Windows ปรับแต่งให้ อาจช่วยลดอาการหน่วงในเกมเก่าที่ใช้ DirectX 9–11 " +
              "แต่บน Windows 11 โหมดปกติมักเร็วพอกัน — ควรลองกับเกมที่เล่นจริง"),
            TrickRisk.Safe, RestartScope.None,
            new[]
            {
                Dword(HKCU, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, 0),
                Dword(HKCU, @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, 0)
            },
            tip: L("Takes effect the next time you start a game.", "มีผลเมื่อเปิดเกมครั้งถัดไป")));

        var hags = os.SupportsHags ? NativeSettings.QueryHags() : null;
        if (os.SupportsHags && hags is not { Supported: false })
        {
            list.Add(RegToggle("hags", Gaming, "SpeedIcon",
                L("Hardware-accelerated GPU scheduling", "เปิด Hardware-accelerated GPU scheduling"),
                L("Lets the graphics card manage its own work queue. May reduce latency on supported GPUs (NVIDIA GTX 10 series and newer, " +
                  "AMD RX 5000 and newer, Intel Arc). Results differ per game — try it on and off.",
                  "ให้การ์ดจอจัดการคิวงานเอง อาจช่วยลดความหน่วงในการ์ดจอที่รองรับ (NVIDIA GTX 10 ขึ้นไป, AMD RX 5000 ขึ้นไป, Intel Arc) " +
                  "ผลลัพธ์ต่างกันในแต่ละเกม ควรลองทั้งเปิดและปิด"),
                TrickRisk.Moderate, RestartScope.Reboot,
                new[] { Dword(HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, null) },
                tip: L("\"Restore default\" removes the setting so the graphics driver decides again.",
                       "ปุ่ม \"คืนค่าเริ่มต้น\" จะลบค่านี้ออก ให้ไดรเวอร์การ์ดจอเป็นผู้กำหนดเองเหมือนเดิม"),
                decorate: status =>
                {
                    var now = NativeSettings.QueryHags();
                    if (now == null) return status;
                    return status with
                    {
                        Note = now.Enabled
                            ? L("GPU scheduling is ON right now (changes apply after a restart).", "ตอนนี้ GPU scheduling เปิดอยู่ (การเปลี่ยนแปลงมีผลหลังรีสตาร์ท)")
                            : L("GPU scheduling is OFF right now (changes apply after a restart).", "ตอนนี้ GPU scheduling ปิดอยู่ (การเปลี่ยนแปลงมีผลหลังรีสตาร์ท)")
                    };
                }));
        }

        list.Add(RegToggle("power-throttling", Gaming, "SpeedIcon",
            L("Turn off background power throttling", "ปิดการลดพลังงานแอปเบื้องหลัง (Power Throttling)"),
            L("Stops Windows from putting background apps into power-saving mode (EcoQoS). Useful when you stream or record while gaming " +
              "on a desktop. Laptops will use more battery and run warmer.",
              "ไม่ให้ Windows ลดความเร็วแอปที่ทำงานเบื้องหลังเพื่อประหยัดไฟ (EcoQoS) เหมาะกับคอมตั้งโต๊ะที่สตรีมหรืออัดวิดีโอไปพร้อมกับเล่นเกม " +
              "ส่วนโน้ตบุ๊กจะกินแบตมากขึ้นและร้อนขึ้น"),
            PowerPlans.HasBattery ? TrickRisk.Moderate : TrickRisk.Safe, RestartScope.Reboot,
            new[] { Dword(HKLM, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, null, 0) },
            warning: PowerPlans.HasBattery
                ? L("This PC has a battery: expect shorter battery life and more heat.", "เครื่องนี้มีแบตเตอรี่: แบตจะหมดเร็วขึ้นและเครื่องร้อนขึ้น")
                : null));
    }

    private static string SystemReport(WindowsVersionInfo os, bool thai)
    {
        string YesNo(bool v) => thai ? (v ? "มี" : "ไม่มี") : (v ? "yes" : "no");
        string ssd = os.SystemDriveIsSsd switch
        {
            true => "SSD",
            false => thai ? "ฮาร์ดดิสก์ (HDD)" : "hard disk (HDD)",
            _ => thai ? "ไม่ทราบ" : "unknown"
        };
        var hags = NativeSettings.QueryHags();
        string hagsText = hags == null
            ? (thai ? "ไม่ทราบ" : "unknown")
            : hags.Supported
                ? (thai ? (hags.Enabled ? "รองรับ (เปิดอยู่)" : "รองรับ (ปิดอยู่)") : (hags.Enabled ? "supported (on)" : "supported (off)"))
                : (thai ? "การ์ดจอไม่รองรับ" : "not supported by your GPU");

        var sb = new StringBuilder();
        sb.AppendLine(thai ? "ระบบที่ตรวจพบ" : "Detected system");
        sb.AppendLine($"• {os.FriendlyName}");
        sb.AppendLine((thai ? "• รุ่น (Edition): " : "• Edition: ") + os.EditionId);
        sb.AppendLine((thai ? "• ตระกูล: " : "• Family: ") + (os.IsServer ? "Windows Server" : os.FamilyName));
        sb.AppendLine((thai ? "• สถาปัตยกรรม: " : "• Architecture: ") + (os.Is64Bit ? "64-bit" : "32-bit"));
        sb.AppendLine((thai ? "• จำนวนคอร์ CPU: " : "• CPU cores: ") + os.ProcessorCount);
        sb.AppendLine($"• RAM: {os.TotalRamMb / 1024.0:F1} GB");
        sb.AppendLine((thai ? "• ไดรฟ์ระบบ: " : "• System drive: ") + ssd);
        sb.AppendLine((thai ? "• แบตเตอรี่: " : "• Battery: ") + YesNo(PowerPlans.HasBattery));
        sb.AppendLine((thai ? "• Modern Standby: " : "• Modern Standby: ") + YesNo(PowerPlans.IsModernStandby));
        sb.AppendLine();
        sb.AppendLine(thai ? "ความสามารถที่เกี่ยวข้อง" : "Relevant features on this PC");
        sb.AppendLine($"• GPU scheduling (HAGS): {hagsText}");
        sb.AppendLine((thai ? "• Widgets บนแถบงาน: " : "• Taskbar Widgets: ") + YesNo(os.SupportsWidgets));
        sb.AppendLine((thai ? "• Recall (พีซี Copilot+): " : "• Recall (Copilot+ PC): ") + YesNo(os.SupportsRecall));
        sb.AppendLine((thai ? "• แอป Copilot: " : "• Copilot app: ") + YesNo(CopilotInstalled()));
        sb.AppendLine((thai ? "• ตัวแก้ไขนโยบายกลุ่ม (gpedit): " : "• Group Policy Editor (gpedit): ") + YesNo(os.HasGroupPolicyEditor));
        sb.AppendLine((thai ? "• แผนพลังงาน High/Ultimate Performance: " : "• High/Ultimate Performance plans: ") + YesNo(!PowerPlans.IsModernStandby));
        sb.AppendLine();
        sb.AppendLine(thai
            ? "WinXTools แสดงเฉพาะเทคนิคที่มีผลกับ Windows รุ่นและฮาร์ดแวร์ของเครื่องนี้"
            : "WinXTools only shows tricks that exist on this Windows version and hardware.");
        return sb.ToString();
    }
}

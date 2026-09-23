using System.ServiceProcess;
using NetX.Core.Optimization;

namespace NetX.Core.System.Tweaks;

public static partial class TrickCatalog
{
    private static void AddPerformance(List<TrickDefinition> list, WindowsVersionInfo os)
    {
        list.Add(new TrickDefinition
        {
            Id = "search-indexing",
            Category = Performance,
            Icon = "SearchIcon",
            Name = L("Turn off Windows Search indexing", "ปิดการทำดัชนีของ Windows Search"),
            Description = L(
                "Stops the indexer that scans your files in the background. Worth it only on old PCs with a hard disk (HDD) " +
                "that is always busy.",
                "หยุดตัวสร้างดัชนีที่คอยสแกนไฟล์อยู่เบื้องหลัง คุ้มเฉพาะเครื่องเก่าที่ใช้ฮาร์ดดิสก์ (HDD) และดิสก์ทำงานหนักตลอดเวลา"),
            Warning = L("Searching in Start, File Explorer and Outlook will be slower and may miss results.",
                        "การค้นหาในเมนูเริ่ม File Explorer และ Outlook จะช้าลงและอาจหาไม่ครบ"),
            Risk = TrickRisk.Moderate,
            Technical = "Service WSearch → Disabled (Windows default: Automatic, delayed start)",
            Detect = () => WithDriveNote(ServiceTweak.Detect("WSearch", ServiceStartMode.Automatic), os),
            Apply = _ => Task.Run(() => ServiceTweak.Disable("WSearch")),
            Revert = _ => Task.Run(() => ServiceTweak.Restore("WSearch", ServiceStartMode.Automatic, delayed: true))
        });

        list.Add(new TrickDefinition
        {
            Id = "sysmain",
            Category = Performance,
            Icon = "SpeedIcon",
            Name = L("Turn off SysMain (Superfetch)", "ปิด SysMain (Superfetch)"),
            Description = L(
                "SysMain preloads the apps you use often into memory. Turning it off can stop constant disk activity on old hard disks; " +
                "on SSDs it makes little difference either way.",
                "SysMain จะโหลดแอปที่ใช้บ่อยเข้าหน่วยความจำไว้ล่วงหน้า การปิดช่วยลดอาการดิสก์ทำงานตลอดเวลาในฮาร์ดดิสก์รุ่นเก่า " +
                "ส่วนบน SSD แทบไม่ต่างกัน"),
            Warning = L("Apps you use often may open a little slower.", "แอปที่ใช้บ่อยอาจเปิดช้าลงเล็กน้อย"),
            Risk = TrickRisk.Moderate,
            Technical = "Service SysMain → Disabled (Windows default: Automatic)",
            Detect = () => WithDriveNote(ServiceTweak.Detect("SysMain", ServiceStartMode.Automatic), os),
            Apply = _ => Task.Run(() => ServiceTweak.Disable("SysMain")),
            Revert = _ => Task.Run(() => ServiceTweak.Restore("SysMain", ServiceStartMode.Automatic, delayed: false))
        });

        list.Add(new TrickDefinition
        {
            Id = "standby-memory",
            Category = Performance,
            Icon = "SpeedIcon",
            Name = L("Free cached (standby) memory now", "ล้างหน่วยความจำแคช (Standby) ตอนนี้"),
            Description = L(
                "Empties Windows' file cache in RAM and trims the memory of idle background apps right now. Useful just before starting a " +
                "big game on a PC with little RAM. The app on screen, Windows itself and busy apps are left alone. Windows refills the " +
                "cache over time — that is normal.",
                "ล้างแคชไฟล์ใน RAM และลดหน่วยความจำของแอปเบื้องหลังที่ว่างอยู่ทันที เหมาะก่อนเปิดเกมใหญ่ในเครื่องที่ RAM น้อย " +
                "โดยไม่แตะแอปที่อยู่หน้าจอ ตัว Windows และแอปที่กำลังทำงานหนัก — Windows จะค่อย ๆ เติมแคชกลับมาเองซึ่งเป็นเรื่องปกติ"),
            Tip = L("Background apps may feel slow for a moment when you switch back to them. For everyday use, the RAM page's safe cleanup is gentler.",
                    "แอปเบื้องหลังอาจช้าลงชั่วครู่ตอนสลับกลับไปใช้ ถ้าใช้งานทั่วไป การล้างแบบปลอดภัยในหน้า RAM จะนุ่มนวลกว่า"),
            Risk = TrickRisk.Safe,
            Technical = "K32EmptyWorkingSet on idle background processes (not foreground/system/busy/excluded), then " +
                        "NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)",
            RunLabel = L("Free now", "ล้างตอนนี้"),
            Run = _ => Task.Run(ClearStandbyMemory)
        });
    }

    private static TrickStatus WithDriveNote(TrickStatus status, WindowsVersionInfo os)
    {
        var note = os.SystemDriveIsSsd switch
        {
            true => L("This PC starts from an SSD — keeping this service on is recommended.",
                      "เครื่องนี้ใช้ SSD เป็นไดรฟ์ระบบ — แนะนำให้เปิดบริการนี้ไว้"),
            false => L("This PC starts from a hard disk (HDD).", "เครื่องนี้ใช้ฮาร์ดดิสก์ (HDD) เป็นไดรฟ์ระบบ"),
            _ => null
        };
        return note == null || status.Note != null ? status : status with { Note = note };
    }

    private static TrickResult ClearStandbyMemory()
    {
        var ram = RamOptimizer.Instance;
        var before = ram.GetMemoryInfo();
        if (!ram.ClearStandbyList())
            return TrickResult.Fail(new LocText(
                "Windows didn't allow clearing standby memory (Administrator rights are needed).",
                "Windows ไม่อนุญาตให้ล้างหน่วยความจำแคช (ต้องใช้สิทธิ์ผู้ดูแลระบบ)"));

        Thread.Sleep(500);
        var after = ram.GetMemoryInfo();
        return TrickResult.Ok(new LocText(
            $"Done — free memory: {before.AvailableMemoryMB:N0} MB → {after.AvailableMemoryMB:N0} MB.",
            $"เรียบร้อย — หน่วยความจำว่าง: {before.AvailableMemoryMB:N0} MB → {after.AvailableMemoryMB:N0} MB"));
    }
}

using System.Text.RegularExpressions;

namespace NetX.Core.System.Tweaks;

public static partial class TrickCatalog
{
    private const string FileSystemKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";

    private static void AddStorage(List<TrickDefinition> list, WindowsVersionInfo os)
    {
        list.Add(new TrickDefinition
        {
            Id = "cleaner",
            Category = Storage,
            Icon = "FolderIcon",
            Name = L("Clean up junk files", "ล้างไฟล์ขยะ"),
            Description = L(
                "Windows Update leftovers, thumbnails, Delivery Optimization files and old Windows installations are cleaned safely on " +
                "the WinXTools Cleaner page.",
                "ไฟล์ค้างจาก Windows Update, ภาพย่อ, ไฟล์ Delivery Optimization และ Windows เวอร์ชันเก่า ล้างได้อย่างปลอดภัยในหน้า Cleaner ของ WinXTools"),
            Risk = TrickRisk.Safe,
            NavigateTo = "Cleaner",
            SettingsUri = "ms-settings:storagesense",
            SettingsLabel = L("Storage settings", "การตั้งค่าที่เก็บข้อมูล")
        });

        list.Add(new TrickDefinition
        {
            Id = "component-cleanup",
            Category = Storage,
            Icon = "FolderIcon",
            Name = L("Clean up the component store (WinSxS)", "ล้างที่เก็บคอมโพเนนต์ (WinSxS)"),
            Description = L(
                "Removes old versions of Windows components that updates have replaced. Can free several GB and takes 5–30 minutes. " +
                "Windows does the same thing on its own after 30 days; this just does it now.",
                "ลบคอมโพเนนต์ Windows รุ่นเก่าที่ถูกอัปเดตแทนที่แล้ว อาจได้พื้นที่คืนหลาย GB ใช้เวลา 5–30 นาที " +
                "ปกติ Windows จะล้างเองหลังผ่านไป 30 วัน ตัวเลือกนี้แค่ทำให้ทันที"),
            Warning = L("The old versions are deleted for good, so some older updates may no longer be uninstallable.",
                        "คอมโพเนนต์รุ่นเก่าจะถูกลบถาวร อัปเดตรุ่นก่อน ๆ บางตัวอาจถอนการติดตั้งไม่ได้อีก"),
            Risk = TrickRisk.Moderate,
            Technical = "DISM /Online /Cleanup-Image /StartComponentCleanup",
            CopyText = "DISM /Online /Cleanup-Image /StartComponentCleanup",
            ShowsOutput = true,
            Cancellable = true,
            RunLabel = L("Clean up", "เริ่มล้าง"),
            Run = ctx => RunTool(ctx, "dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", Long,
                L("Done — the component store was cleaned up.", "เรียบร้อย — ล้างที่เก็บคอมโพเนนต์แล้ว"))
        });

        list.Add(new TrickDefinition
        {
            Id = "compact-os",
            Category = Storage,
            Icon = "FolderIcon",
            Name = L("Compress Windows (Compact OS)", "บีบอัดไฟล์ Windows (Compact OS)"),
            Description = L(
                "Compresses the Windows system files to free about 2–4 GB. Meant for PCs with a small SSD/eMMC (64–128 GB). " +
                "Takes 10–20 minutes.",
                "บีบอัดไฟล์ระบบของ Windows ได้พื้นที่คืนประมาณ 2–4 GB เหมาะกับเครื่องที่ SSD/eMMC เล็ก (64–128 GB) ใช้เวลา 10–20 นาที"),
            Warning = L("On PCs with a slow CPU, Windows can feel a little slower. \"Restore default\" decompresses the files again.",
                        "เครื่องที่ CPU ไม่แรงอาจรู้สึกช้าลงเล็กน้อย กด \"คืนค่าเริ่มต้น\" เพื่อคลายการบีบอัดกลับได้"),
            Risk = TrickRisk.Moderate,
            Technical = "compact /compactos:always   (restore: compact /compactos:never)",
            AppliedLabel = L("Compressed", "บีบอัดอยู่"),
            DefaultLabel = L("Not compressed (Windows default)", "ไม่ได้บีบอัด (ค่าเริ่มต้นของ Windows)"),
            ShowsOutput = true,
            Detect = () => NativeSettings.IsCompactOs() switch
            {
                true => TrickStatus.Applied(),
                false => TrickStatus.Default(),
                _ => TrickStatus.Unknown()
            },
            Apply = ctx => RunAndVerify(ctx, "compact.exe", "/compactos:always", Long,
                () => NativeSettings.IsCompactOs() == true, TweakErrors.AppliedText),
            Revert = ctx => RunAndVerify(ctx, "compact.exe", "/compactos:never", Long,
                () => NativeSettings.IsCompactOs() == false, TweakErrors.RestoredText)
        });

        if (os.Build >= 19041)
        {
            list.Add(new TrickDefinition
            {
                Id = "reserved-storage",
                Category = Storage,
                Icon = "FolderIcon",
                Name = L("Turn off Reserved Storage", "ปิดพื้นที่สำรองของ Windows (Reserved Storage)"),
                Description = L(
                    "Windows keeps about 7 GB aside for updates. Turning this off gives that space back; updates then need that much free space themselves.",
                    "Windows กันพื้นที่ไว้ประมาณ 7 GB สำหรับอัปเดต การปิดจะคืนพื้นที่นี้ให้ แต่เวลาอัปเดตต้องมีพื้นที่ว่างเพียงพอเอง"),
                Warning = L("If the disk gets full, Windows updates can fail until you free up space.",
                            "ถ้าดิสก์เต็ม การอัปเดต Windows อาจล้มเหลวจนกว่าจะเคลียร์พื้นที่"),
                Tip = L("Can't be changed while an update is being installed. Available on Windows 10 version 2004 and later.",
                        "เปลี่ยนไม่ได้ขณะกำลังติดตั้งอัปเดต ใช้ได้บน Windows 10 เวอร์ชัน 2004 ขึ้นไป"),
                Risk = TrickRisk.Moderate,
                Technical = "DISM /Online /Set-ReservedStorageState /State:Disabled   (restore: /State:Enabled)",
                ShowsOutput = true,
                Detect = DetectReservedStorage,
                Apply = ctx => RunAndVerify(ctx, "dism.exe", "/Online /Set-ReservedStorageState /State:Disabled", Medium,
                    () => DetectReservedStorage().State == TrickState.Applied, TweakErrors.AppliedText),
                Revert = ctx => RunAndVerify(ctx, "dism.exe", "/Online /Set-ReservedStorageState /State:Enabled", Medium,
                    () => DetectReservedStorage().State == TrickState.Default, TweakErrors.RestoredText)
            });
        }

        list.Add(RegToggle("long-paths", Storage, "FolderIcon",
            L("Allow long file paths (over 260 characters)", "รองรับที่อยู่ไฟล์ยาวเกิน 260 ตัวอักษร"),
            L("Lets apps that support it use very long folder paths (common with Node.js, Python and game mods). " +
              "Apps pick it up the next time they start.",
              "ให้แอปที่รองรับใช้งานโฟลเดอร์ที่มีที่อยู่ยาวมากได้ (พบบ่อยกับ Node.js, Python และม็อดเกม) แอปจะใช้ได้เมื่อเปิดใหม่ครั้งถัดไป"),
            TrickRisk.Safe, RestartScope.None,
            new[] { Dword(HKLM, FileSystemKey, "LongPathsEnabled", 1, 0) }));

        list.Add(new TrickDefinition
        {
            Id = "ntfs-last-access",
            Category = Storage,
            Icon = "FolderIcon",
            Name = L("Stop updating \"last accessed\" times", "ไม่บันทึกเวลา \"เข้าถึงล่าสุด\" ของไฟล์"),
            Description = L(
                "NTFS normally records when each file was last opened. Turning it off saves a little disk activity, mostly on hard disks. " +
                "Restore puts back Windows' automatic setting.",
                "ปกติ NTFS จะบันทึกเวลาที่เปิดไฟล์แต่ละไฟล์ครั้งล่าสุด การปิดช่วยลดการเขียนดิสก์เล็กน้อย เห็นผลกับฮาร์ดดิสก์เป็นหลัก " +
                "ปุ่มคืนค่าจะกลับไปใช้ค่าอัตโนมัติของ Windows"),
            Risk = TrickRisk.Safe,
            Restart = RestartScope.Reboot,
            Technical = "fsutil behavior set disablelastaccess 1   (restore: 2 = system managed, the Windows default)",
            CopyText = "fsutil behavior set disablelastaccess 1",
            Detect = DetectLastAccess,
            Apply = ctx => RunAndVerify(ctx, "fsutil.exe", "behavior set disablelastaccess 1", Short,
                () => DetectLastAccess().State == TrickState.Applied, TweakErrors.AppliedText, RestartScope.Reboot),
            Revert = ctx => RunAndVerify(ctx, "fsutil.exe", "behavior set disablelastaccess 2", Short,
                () => DetectLastAccess().State == TrickState.Default, TweakErrors.RestoredText, RestartScope.Reboot)
        });
    }

    /// <summary>
    /// NtfsDisableLastAccessUpdate: low bit 1 = updates off; 2 = "system managed,
    /// on" (0x80000002 on a clean install); 0 = user managed, on.
    /// </summary>
    private static TrickStatus DetectLastAccess()
    {
        var v = Reg.ReadDword(HKLM, FileSystemKey, "NtfsDisableLastAccessUpdate");
        if (v == null) return TrickStatus.Default();
        if ((v.Value & 1) == 1) return TrickStatus.Applied();
        if ((v.Value & 3) == 2) return TrickStatus.Default();
        return TrickStatus.Custom(L("Updates are on (set by hand).", "เปิดการบันทึกอยู่ (ตั้งค่าเอง)"));
    }

    /// <summary>Reads the reserved-storage state through the DISM PowerShell module (language-neutral enum names).</summary>
    private static TrickStatus DetectReservedStorage()
    {
        const string script =
            "$s = Get-WindowsReservedStorageState\n" +
            "if ($null -ne $s.ReservedStorageState) { [string]$s.ReservedStorageState } else { $s | Format-List | Out-String }";
        var r = CommandRunner.RunPowerShellAsync(script, TimeSpan.FromSeconds(45)).GetAwaiter().GetResult();
        if (!r.Succeeded) return TrickStatus.Unknown();

        // Enum names are not localized. Check "Disabled" first ("Enabled" is not a substring of it).
        var text = r.StdOut;
        if (Regex.IsMatch(text, @"\bDisabled\b", RegexOptions.IgnoreCase))
            return TrickStatus.Applied();
        if (Regex.IsMatch(text, @"\bEnabled\b", RegexOptions.IgnoreCase))
            return TrickStatus.Default();
        return TrickStatus.Unknown();
    }
}

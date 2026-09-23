using Microsoft.Win32;

namespace NetX.Core.System.Tweaks;

public static partial class TrickCatalog
{
    private const string GodModeClsid = "{ED7BA470-8E54-465E-825C-99712043E01C}";
    private const string PhotoViewerAssociations = @"SOFTWARE\Microsoft\Windows Photo Viewer\Capabilities\FileAssociations";
    private const string PhotoViewerProgId = "PhotoViewer.FileAssoc.Tiff";
    private const string PhotoViewerStateFile = "photoviewer_added.json";

    /// <summary>Extensions Windows Photo Viewer can open but isn't registered for on Windows 10/11 (.tif/.tiff are).</summary>
    private static readonly string[] PhotoViewerExtensions =
        { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".wdp", ".jxr" };

    private sealed class PhotoViewerBackup
    {
        public List<string> Added { get; set; } = new();
    }

    private static void AddTools(List<TrickDefinition> list, WindowsVersionInfo os)
    {
        list.Add(OpenTool("god-mode", Tools, "SettingsIcon",
            L("God Mode (all settings)", "God Mode (รวมการตั้งค่าทั้งหมด)"),
            L("One window with 200+ Control Panel settings, grouped by topic.",
              "หน้าต่างเดียวรวมการตั้งค่าใน Control Panel กว่า 200 รายการ แยกตามหมวดหมู่"),
            () => ShellLauncher.OpenUnelevated($"shell:::{GodModeClsid}"), technical: $"shell:::{GodModeClsid}"));

        var godModeFolder = DesktopFile("GodMode." + GodModeClsid);
        list.Add(new TrickDefinition
        {
            Id = "god-mode-folder",
            Category = Tools,
            Icon = "FolderIcon",
            Name = L("God Mode folder on the Desktop", "สร้างโฟลเดอร์ God Mode บนเดสก์ท็อป"),
            Description = L("Creates a \"GodMode\" folder on your Desktop that always opens the full settings list.",
                            "สร้างโฟลเดอร์ \"GodMode\" บนเดสก์ท็อป เปิดเมื่อไรก็เจอรายการการตั้งค่าทั้งหมด"),
            Risk = TrickRisk.Safe,
            Technical = godModeFolder,
            ApplyLabel = L("Create", "สร้าง"),
            RevertLabel = L("Remove", "ลบออก"),
            AppliedLabel = L("Created", "สร้างแล้ว"),
            DefaultLabel = L("Not created", "ยังไม่ได้สร้าง"),
            Detect = () => Directory.Exists(godModeFolder) ? TrickStatus.Applied() : TrickStatus.Default(),
            Apply = _ => Task.Run(() => CreateGodModeFolder(godModeFolder)),
            Revert = _ => Task.Run(() => RemoveGodModeFolder(godModeFolder))
        });

        list.Add(OpenTool("all-control-panel", Tools, "SettingsIcon",
            L("All Control Panel items", "รายการทั้งหมดใน Control Panel"),
            L("The classic Control Panel with every item shown as an icon.", "Control Panel แบบเดิม แสดงทุกรายการเป็นไอคอน"),
            () => ShellLauncher.OpenUnelevated("shell:::{21EC2020-3AEA-1069-A2DD-08002B30309D}"),
            technical: "shell:::{21EC2020-3AEA-1069-A2DD-08002B30309D}"));

        list.Add(OpenTool("network-connections", Tools, "NetworkIcon",
            L("Network Connections", "การเชื่อมต่อเครือข่าย"),
            L("The classic list of network adapters — change IP/DNS settings, turn adapters on or off.",
              "รายการการ์ดเครือข่ายแบบเดิม ใช้ตั้งค่า IP/DNS หรือเปิด/ปิดการ์ดเครือข่าย"),
            () => ShellLauncher.StartControlPanel("ncpa.cpl"), technical: "ncpa.cpl"));

        list.Add(OpenTool("programs-features", Tools, "FolderIcon",
            L("Programs and Features", "โปรแกรมและฟีเจอร์"),
            L("The classic uninstall list, including \"Turn Windows features on or off\".",
              "รายการถอนการติดตั้งโปรแกรมแบบเดิม รวมถึง \"เปิดหรือปิดฟีเจอร์ของ Windows\""),
            () => ShellLauncher.StartControlPanel("appwiz.cpl"), technical: "appwiz.cpl"));

        list.Add(OpenTool("device-manager", Tools, "SettingsIcon",
            L("Device Manager", "ตัวจัดการอุปกรณ์"),
            L("See your hardware and update or roll back drivers.", "ดูฮาร์ดแวร์ในเครื่อง อัปเดตหรือย้อนกลับไดรเวอร์"),
            () => ShellLauncher.StartConsole("devmgmt.msc"), technical: "devmgmt.msc"));

        list.Add(OpenTool("disk-management", Tools, "FolderIcon",
            L("Disk Management", "การจัดการดิสก์"),
            L("Create, resize and format partitions. Mistakes here can erase data — double-check the disk before every change.",
              "สร้าง ปรับขนาด และฟอร์แมตพาร์ติชัน ถ้าทำผิดข้อมูลอาจหายได้ ตรวจสอบดิสก์ให้แน่ใจก่อนเปลี่ยนแปลงทุกครั้ง"),
            () => ShellLauncher.StartConsole("diskmgmt.msc"), TrickRisk.Moderate, technical: "diskmgmt.msc"));

        list.Add(OpenTool("services", Tools, "SettingsIcon",
            L("Services", "บริการ (Services)"),
            L("Start, stop and configure Windows services. Turning off the wrong service can break Windows features.",
              "เริ่ม หยุด และตั้งค่าบริการของ Windows ถ้าปิดบริการผิดตัว ฟีเจอร์ของ Windows อาจใช้งานไม่ได้"),
            () => ShellLauncher.StartConsole("services.msc"), TrickRisk.Moderate, technical: "services.msc"));

        if (os.HasGroupPolicyEditor)
        {
            list.Add(OpenTool("group-policy", Tools, "ShieldIcon",
                L("Local Group Policy Editor", "ตัวแก้ไขนโยบายกลุ่มภายในเครื่อง"),
                L("Advanced Windows policies (Pro, Enterprise and Education).", "นโยบายขั้นสูงของ Windows (Pro, Enterprise และ Education)"),
                () => ShellLauncher.StartConsole("gpedit.msc"), TrickRisk.Moderate, technical: "gpedit.msc"));
        }

        list.Add(OpenTool("registry-editor", Tools, "TerminalIcon",
            L("Registry Editor", "ตัวแก้ไขรีจิสทรี"),
            L("Edit the Windows registry directly. Export a backup (File › Export) before changing anything.",
              "แก้ไขรีจิสทรีของ Windows โดยตรง ควรส่งออกสำรองไว้ก่อน (ไฟล์ › ส่งออก) ทุกครั้งก่อนแก้ไข"),
            () => ShellLauncher.StartTool(Path.Combine(CommandRunner.WindowsDirectory, "regedit.exe")),
            TrickRisk.Moderate, technical: "regedit"));

        list.Add(OpenTool("volume-mixer", Tools, "SettingsIcon",
            L("Classic Volume Mixer", "ตัวผสมเสียงแบบเดิม (Volume Mixer)"),
            L("Opens the classic per-app volume mixer (SndVol). It only opens the old mixer; it doesn't replace the new one.",
              "เปิดตัวปรับระดับเสียงแยกตามแอปแบบเดิม (SndVol) เป็นการเปิดตัวเก่าเท่านั้น ไม่ได้แทนที่ตัวใหม่"),
            () => ShellLauncher.StartTool(CommandRunner.SystemTool("SndVol.exe")), technical: "sndvol"));

        var photoViewerDll = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Photo Viewer", "PhotoViewer.dll");
        if (File.Exists(photoViewerDll) && Reg.KeyExists(HKLM, @"SOFTWARE\Classes\" + PhotoViewerProgId))
        {
            list.Add(new TrickDefinition
            {
                Id = "photo-viewer",
                Category = Tools,
                Icon = "FolderIcon",
                Name = L("Bring back Windows Photo Viewer", "เปิดใช้ Windows Photo Viewer แบบเดิม"),
                Description = L(
                    "Lets you choose the classic Photo Viewer for JPG, PNG, GIF and BMP. Afterwards Default apps opens so you can pick it — " +
                    "Windows doesn't let programs change your default apps for you.",
                    "ทำให้เลือก Photo Viewer แบบเดิมเปิดไฟล์ JPG, PNG, GIF, BMP ได้ จากนั้นจะเปิดหน้าแอปเริ่มต้นให้คุณเลือกเอง " +
                    "(Windows ไม่อนุญาตให้โปรแกรมเปลี่ยนแอปเริ่มต้นแทนผู้ใช้)"),
                Risk = TrickRisk.Safe,
                Technical = $@"HKLM\{PhotoViewerAssociations}: {string.Join(" ", PhotoViewerExtensions)} = {PhotoViewerProgId}",
                SettingsUri = "ms-settings:defaultapps",
                SettingsLabel = L("Default apps", "แอปเริ่มต้น"),
                Detect = DetectPhotoViewer,
                Apply = _ => Task.Run(ApplyPhotoViewer),
                Revert = _ => Task.Run(RevertPhotoViewer)
            });
        }
    }

    private static TrickResult CreateGodModeFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return Directory.Exists(path)
                ? TrickResult.Ok(L("Done — the God Mode folder is on your Desktop.", "เรียบร้อย — สร้างโฟลเดอร์ God Mode บนเดสก์ท็อปแล้ว"))
                : TrickResult.Fail(TweakErrors.NotVerified);
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(TweakErrors.Friendly(ex));
        }
    }

    private static TrickResult RemoveGodModeFolder(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.Exists)
            {
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(path); // a link: remove the link only
                else if (!info.EnumerateFileSystemInfos().Any())
                    info.Delete(recursive: false);
                else
                    return TrickResult.Fail(L("The folder isn't empty, so it was left alone.", "โฟลเดอร์ไม่ว่าง จึงไม่ได้ลบ"));
            }
            return Directory.Exists(path)
                ? TrickResult.Fail(TweakErrors.NotVerified)
                : TrickResult.Ok(L("Done — the God Mode folder was removed.", "เรียบร้อย — ลบโฟลเดอร์ God Mode แล้ว"));
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(TweakErrors.Friendly(ex));
        }
    }

    private static TrickStatus DetectPhotoViewer()
    {
        int registered = PhotoViewerExtensions.Count(ext =>
            string.Equals(Reg.ReadString(HKLM, PhotoViewerAssociations, ext), PhotoViewerProgId, StringComparison.OrdinalIgnoreCase));
        if (registered == PhotoViewerExtensions.Length) return TrickStatus.Applied();
        if (registered == 0) return TrickStatus.Default();
        return TrickStatus.Custom();
    }

    private static TrickResult ApplyPhotoViewer()
    {
        try
        {
            var toAdd = PhotoViewerExtensions
                .Where(ext => Reg.Read(HKLM, PhotoViewerAssociations, ext) == null)
                .ToList();

            // Record exactly which values WE add (before adding), so Restore removes only those.
            var backup = SecureAppData.Load<PhotoViewerBackup>(PhotoViewerStateFile) ?? new PhotoViewerBackup();
            backup.Added = backup.Added.Where(PhotoViewerExtensions.Contains).Union(toAdd).Distinct().ToList();
            SecureAppData.Save(PhotoViewerStateFile, backup);

            foreach (var ext in toAdd)
                Reg.Write(HKLM, PhotoViewerAssociations, ext, PhotoViewerProgId, RegistryValueKind.String);

            if (DetectPhotoViewer().State == TrickState.Default)
                return TrickResult.Fail(TweakErrors.NotVerified);

            return new TrickResult
            {
                Success = true,
                OpenAfter = "ms-settings:defaultapps",
                Message = L("Done — now pick \"Windows Photo Viewer\" for your image types in Default apps (opening now).",
                            "เรียบร้อย — เลือก \"Windows Photo Viewer\" ให้ไฟล์รูปภาพในหน้าแอปเริ่มต้น (กำลังเปิดให้)")
            };
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(TweakErrors.Friendly(ex));
        }
    }

    private static TrickResult RevertPhotoViewer()
    {
        try
        {
            var backup = SecureAppData.Load<PhotoViewerBackup>(PhotoViewerStateFile);
            // Without a record, remove only values that point to Photo Viewer (Windows ships none of these).
            var candidates = backup?.Added.Where(PhotoViewerExtensions.Contains).ToList() ?? PhotoViewerExtensions.ToList();

            foreach (var ext in candidates)
            {
                if (string.Equals(Reg.ReadString(HKLM, PhotoViewerAssociations, ext), PhotoViewerProgId, StringComparison.OrdinalIgnoreCase))
                    Reg.DeleteValue(HKLM, PhotoViewerAssociations, ext);
            }
            SecureAppData.Delete(PhotoViewerStateFile);

            return DetectPhotoViewer().State == TrickState.Default
                ? TrickResult.Ok(TweakErrors.RestoredText)
                : TrickResult.Ok(L("Removed the entries WinXTools added. Other programs had added some too, so those were kept.",
                                   "ลบรายการที่ WinXTools เพิ่มไว้แล้ว ส่วนรายการที่โปรแกรมอื่นเพิ่มไว้ยังคงอยู่"));
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(TweakErrors.Friendly(ex));
        }
    }
}

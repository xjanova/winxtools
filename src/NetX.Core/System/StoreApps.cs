using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using NetX.Core.Helpers;
using NetX.Core.Optimization;
using NetX.Core.System.Tweaks;

namespace NetX.Core.System;

#region Models

/// <summary>What WinXTools says about removing a Microsoft Store / MSIX app.</summary>
public enum StoreAppVerdict
{
    /// <summary>An ordinary app that is not in the catalog: listed and removable, no advice.</summary>
    None,
    /// <summary>A pure extra Windows preinstalls or promotes; nothing in Windows needs it.</summary>
    SafeToRemove,
    /// <summary>Useful to many people (Photos, Calculator, codecs…): the user decides, with a warning.</summary>
    YourCall,
    /// <summary>Settings app of a driver or device (NVIDIA, Realtek, printer…): the user decides, with a warning.</summary>
    Hardware,
    /// <summary>Never offered and never removed by WinXTools.</summary>
    Protected,
}

/// <summary>Why a package is <see cref="StoreAppVerdict.Protected"/>.</summary>
public enum StoreAppProtection
{
    None,
    /// <summary>A name Windows reported does not look like a package name; it is never put into a command.</summary>
    InvalidName,
    Framework,
    ResourcePackage,
    /// <summary>Windows marks it NonRemovable.</summary>
    NonRemovable,
    /// <summary>SignatureKind System: shipped inside Windows itself.</summary>
    SystemSigned,
    /// <summary>On the fixed list (Store, App Installer, Windows Security, Edge, runtimes…).</summary>
    ProtectedFamily,
    /// <summary>A Microsoft.Windows.* / MicrosoftWindows.* shell or system component.</summary>
    WindowsComponent,
    /// <summary>The identity package of a desktop program (WinRAR, VS Code, Office…): removed with that program.</summary>
    DesktopCompanion,
    /// <summary>Only a provisioned copy is known and it is not in the catalog, so its kind cannot be checked.</summary>
    Unverified,
}

/// <summary>One row of the curated table of what Windows preinstalls or promotes.</summary>
public sealed class StoreAppCatalogEntry
{
    /// <summary>Package name, or a prefix ending in '*'.</summary>
    public required string Pattern { get; init; }
    /// <summary>Product name (kept in English in both UI languages).</summary>
    public required string Name { get; init; }
    /// <summary>What it is, in one sentence.</summary>
    public required LocText Description { get; init; }
    public StoreAppVerdict Verdict { get; init; }
    /// <summary>What the user loses; shown in the confirmation.</summary>
    public LocText? Warning { get; init; }
    /// <summary>Can be installed again from the Microsoft Store.</summary>
    public bool InStore { get; init; } = true;
    /// <summary>A Copilot app: "keep it from coming back" also turns Windows Copilot off.</summary>
    public bool IsCopilot { get; init; }
    /// <summary>A third-party app Windows promotes. The user may have installed it on purpose, so it is never bulk-selected.</summary>
    public bool Promoted { get; init; }

    public bool IsExact => !Pattern.EndsWith('*');

    public bool Matches(string packageName) => IsExact
        ? packageName.Equals(Pattern, StringComparison.OrdinalIgnoreCase)
        : packageName.StartsWith(Pattern[..^1], StringComparison.OrdinalIgnoreCase);
}

/// <summary>A Microsoft Store / MSIX app (one package family) as the Uninstaller lists it.</summary>
public sealed class StoreApp
{
    /// <summary>Package name, e.g. Microsoft.BingNews.</summary>
    public string PackageName { get; init; } = "";
    public string PublisherId { get; init; } = "";
    /// <summary>Name_PublisherId — also the folder name under %LOCALAPPDATA%\Packages.</summary>
    public string FamilyName => PackageName + "_" + PublisherId;

    /// <summary>The name the user knows: Start menu name, else catalog name, else the manifest's name, else the package name.</summary>
    public string DisplayName { get; init; } = "";
    /// <summary>Catalog product name when it differs from <see cref="DisplayName"/>; otherwise empty.</summary>
    public string CatalogName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string SignatureKind { get; init; } = "";
    /// <summary>Folder of the newest package of the family, or empty.</summary>
    public string InstallLocation { get; init; } = "";
    public IReadOnlyList<string> FullNames { get; init; } = [];
    /// <summary>When the installed version was put on this PC (folder creation time), if readable.</summary>
    public DateTime? InstalledOn { get; init; }

    public bool InstalledForCurrentUser { get; init; }
    /// <summary>Installed for another account, or staged for accounts that do not exist yet.</summary>
    public bool OnOtherAccounts { get; init; }
    /// <summary>A provisioned copy exists (Windows installs it for every new account); null when that could not be read.</summary>
    public bool? IsProvisioned { get; init; }

    public StoreAppVerdict Verdict { get; init; }
    public StoreAppProtection Protection { get; init; }
    public StoreAppCatalogEntry? Catalog { get; init; }
    /// <summary>One sentence about the app (catalog or the generic hardware text), or null.</summary>
    public LocText? Description { get; init; }
    public LocText? Warning { get; init; }
    public bool ReinstallFromStore { get; init; }

    /// <summary>False for everything WinXTools must never remove.</summary>
    public bool IsOffered => Verdict != StoreAppVerdict.Protected;
    /// <summary>Preinstalled or promoted by Windows (it is in the catalog).</summary>
    public bool ComesWithWindows => Catalog != null;
}

public enum StoreAppListError
{
    None,
    /// <summary>PowerShell could not be started or reported an error.</summary>
    Failed,
    TimedOut,
    Cancelled,
    /// <summary>PowerShell answered with something that is not the expected JSON.</summary>
    BadOutput,
}

public sealed class StoreAppListResult
{
    public StoreAppListError Error { get; init; }
    /// <summary>Windows' own message when <see cref="Error"/> is Failed.</summary>
    public string ErrorDetail { get; init; } = "";
    public bool Succeeded => Error == StoreAppListError.None;

    /// <summary>False: only the signed-in account could be read (Get-AppxPackage -AllUsers was refused).</summary>
    public bool AllUsers { get; init; }
    /// <summary>False: the provisioned list could not be read.</summary>
    public bool ProvisionedKnown { get; init; }
    /// <summary>Packages Windows reported, before grouping into families.</summary>
    public int PackageCount { get; init; }

    /// <summary>Apps that may be removed.</summary>
    public List<StoreApp> Apps { get; init; } = [];
    /// <summary>Everything that is never offered (frameworks, system components, protected families…).</summary>
    public List<StoreApp> Protected { get; init; } = [];
}

public enum StoreAppRemovalStatus
{
    /// <summary>Windows no longer reports the app for any account, nor a provisioned copy it could check.</summary>
    Removed,
    /// <summary>It was already gone when WinXTools looked (removed elsewhere while the page was open).</summary>
    AlreadyGone,
    /// <summary>Dry run: <see cref="StoreAppRemovalResult.Commands"/> holds what would be run; nothing was run.</summary>
    DryRun,
    /// <summary>WinXTools refuses: protected package or a name that failed validation. Nothing was run.</summary>
    Refused,
    /// <summary>Windows does not allow removing this one (0x80073CFA and friends).</summary>
    WindowsRefused,
    /// <summary>The app is running or being updated.</summary>
    InUse,
    /// <summary>Gone for the signed-in account but still present for another account or as a provisioned copy.</summary>
    PartlyRemoved,
    /// <summary>Still installed for another reason; see the error code.</summary>
    StillInstalled,
    /// <summary>Windows did not answer when asked what is installed, so nothing was run.</summary>
    NotStarted,
    /// <summary>The commands ran but the state afterwards could not be read, so nothing is claimed.</summary>
    Unknown,
    /// <summary>Cancelled before anything was run.</summary>
    Cancelled,
}

/// <summary>What happened to one app — always judged from a fresh query after the commands ran.</summary>
public sealed class StoreAppRemovalResult
{
    public string PackageName { get; init; } = "";
    public string PublisherId { get; init; } = "";
    public StoreAppRemovalStatus Status { get; set; }
    public StoreAppProtection Protection { get; set; }

    /// <summary>The exact PowerShell commands, in order (also filled on a dry run).</summary>
    public List<string> Commands { get; } = [];
    /// <summary>Full names Windows still reports after the commands ran.</summary>
    public List<string> StillInstalled { get; } = [];
    /// <summary>Provisioned copies Windows still reports after the commands ran.</summary>
    public List<string> StillProvisioned { get; } = [];
    /// <summary>What is left is installed for no account: Windows only still keeps the package files (staged).</summary>
    public bool StagedOnly { get; set; }
    /// <summary>False: other accounts could not be checked (not elevated).</summary>
    public bool AllUsersChecked { get; set; }
    /// <summary>False: the provisioned copy could not be checked.</summary>
    public bool ProvisionedChecked { get; set; }

    /// <summary>First Windows error, e.g. 0x80073CFA; empty when there was none.</summary>
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";

    /// <summary>%LOCALAPPDATA%\Packages\family — Windows deletes it with the package.</summary>
    public string AppDataPath { get; set; } = "";
    /// <summary>True when the app is gone but that folder is still on disk.</summary>
    public bool AppDataLeft { get; set; }
}

#endregion

#region Catalog

/// <summary>
/// What Windows preinstalls or promotes, and what must never be removed. A static table like the
/// app's other catalogs: package name (or prefix*) → product name, one sentence, verdict. Apps
/// that are not in it are ordinary Store apps (listed, removable, no advice) unless a protection
/// rule applies.
/// </summary>
public static class StoreAppCatalog
{
    /// <summary>Package names, full names and provisioned names that may be put into a command.</summary>
    // \z, not $: "$" would also accept a name that ends in a line break.
    private static readonly Regex SafeToken = new(@"\A[A-Za-z0-9._\-~]+\z", RegexOptions.CultureInvariant);

    /// <summary>Windows' own packages (shell, system apps) are published under this id.</summary>
    private const string WindowsPublisherId = "cw5n1h2txyewy";

    /// <summary>True when the text may be embedded (quoted) in a PowerShell command.</summary>
    public static bool IsValidToken(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 255 && SafeToken.IsMatch(value);

    #region Texts shared by several entries

    private static readonly LocText XboxWarning = new(
        "Game Pass and Xbox games from the Microsoft Store need Gaming Services and Xbox Identity Provider. Keep those two if you play such games.",
        "เกมจาก Game Pass และเกม Xbox ใน Microsoft Store ต้องใช้ Gaming Services และ Xbox Identity Provider ถ้ายังเล่นเกมเหล่านี้ให้เก็บสองตัวนี้ไว้");

    private static readonly LocText PromotedDescription = new(
        "An app Windows promotes. Safe to remove if you don't use it.",
        "แอปที่ Windows โปรโมตให้ ถ้าไม่ได้ใช้ก็ลบได้");

    private static readonly LocText CodecDescription = new(
        "A codec from the Microsoft Store: lets Windows and apps open photos or videos in this format.",
        "ตัวถอดรหัสจาก Microsoft Store ช่วยให้ Windows และแอปต่างๆ เปิดรูปหรือวิดีโอรูปแบบนี้ได้");

    private static readonly LocText CodecWarning = new(
        "Photos or videos in this format may stop opening or lose their thumbnails.",
        "รูปหรือวิดีโอรูปแบบนี้อาจเปิดไม่ได้ หรือไม่แสดงภาพตัวอย่าง");

    private static readonly LocText HardwareDescription = new(
        "The settings app of a device or driver on this PC.",
        "แอปตั้งค่าของอุปกรณ์หรือไดรเวอร์ในเครื่องนี้");

    private static readonly LocText HardwareWarning = new(
        "This belongs to your hardware. The driver keeps working, but its settings (display, sound, printer…) can no longer be opened, and Windows may install it again with the driver.",
        "แอปนี้เป็นของฮาร์ดแวร์ในเครื่อง ไดรเวอร์ยังทำงานต่อได้ แต่จะเปิดหน้าตั้งค่า (จอภาพ เสียง เครื่องพิมพ์…) ไม่ได้ และ Windows อาจติดตั้งกลับมาพร้อมไดรเวอร์");

    /// <summary>Shown instead of offering Edge.</summary>
    public static readonly LocText EdgeNote = new(
        "Microsoft Edge is part of Windows. Outside the European Economic Area Windows does not allow removing it, so it is not offered here.",
        "Microsoft Edge เป็นส่วนหนึ่งของ Windows นอกเขตเศรษฐกิจยุโรป (EEA) Windows ไม่อนุญาตให้ลบ จึงไม่มีให้เลือกในหน้านี้");

    #endregion

    #region Never offered

    // No exception is possible for these, whatever Windows reports about them.
    private static readonly string[] ProtectedNames =
    [
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller",
        "Microsoft.Winget.Source", "Microsoft.SecHealthUI", "Microsoft.Services.Store.Engagement",
        "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.LockApp",
        "Microsoft.Win32WebViewHost", "windows.immersivecontrolpanel", "Microsoft.BioEnrollment",
        "Microsoft.CredDialogHost", "Microsoft.ECApp", "Microsoft.AsyncTextService",
        "Microsoft.XboxGameCallableUI", "Microsoft.ApplicationCompatibilityEnhancements",
        "Microsoft.PPIProjection", "InputApp", "NcsiUwpApp",
    ];

    private static readonly string[] ProtectedPrefixes =
    [
        "Microsoft.MicrosoftEdge",            // Edge, Edge Stable/Beta/Dev, DevTools client
        "Microsoft.UI.Xaml", "Microsoft.VCLibs", "Microsoft.NET.Native", "Microsoft.NET.CoreRuntime",
        "Microsoft.NET.CoreFramework", "Microsoft.WindowsAppRuntime", "Microsoft.WinAppRuntime",
        "MicrosoftCorporationII.WinAppRuntime", "Microsoft.DirectXRuntime", "Microsoft.Advertising.Xaml",
        "Microsoft.AIFabric", "Microsoft.LanguageExperiencePack", "Microsoft.Ink.Handwriting",
    ];

    // Shell / system components. Only an exact catalog entry (Photos, Dev Home, Widgets…) lifts these.
    private static readonly string[] WindowsComponentPrefixes =
    [
        "Microsoft.Windows.", "MicrosoftWindows.", "Windows.",
    ];

    #endregion

    #region Hardware support apps

    // Package-name prefixes of device makers (the part before the first dot is the Store publisher name).
    private static readonly string[] HardwarePackagePrefixes =
    [
        "NVIDIACorp.", "AdvancedMicroDevicesInc", "AppUp.Intel", "AppUp.Thunderbolt", "RealtekSemiconductorCorp.",
        "DolbyLaboratories.", "SynapticsIncorporated.", "ELANMicroelectronicsCorpo.", "WavesAudio.", "DTSInc.",
        "A-Volute.", "CirrusLogic", "ConexantSystems", "QualcommTechnologies", "MediaTek", "RivetNetworks.",
        "KillerNetworking", "DellInc.", "AD2F1837.", "E046963F.", "LenovoCorporation.", "B9ECED6F.", "ASUSTeK",
        "AcerIncorporated.", "9426MICRO-STARINTERNATION.", "SAMSUNGELECTRONICSCO", "LGElectronics.", "HUAWEI",
    ];

    private static readonly string[] HardwareMakers =
    [
        "NVIDIA", "Advanced Micro Devices", "Intel", "Realtek", "Synaptics", "ELAN", "Dolby", "DTS", "Waves Audio",
        "Qualcomm", "MediaTek", "Broadcom", "Dell", "HP Inc", "Hewlett", "Lenovo", "ASUS", "Acer", "Micro-Star",
        "GIGA-BYTE", "Gigabyte", "ASRock", "Samsung", "LG Electronics", "Logitech", "Razer", "Corsair", "SteelSeries",
        "Canon", "Epson", "Brother", "Huawei",
    ];

    private static readonly string[] HardwareWords =
    [
        "Driver", "Control Panel", "ControlPanel", "Audio Console", "AudioConsole", "Audio Control", "AudioControl",
        "Command Center", "Print Support",
    ];

    #endregion

    #region The table

    private static StoreAppCatalogEntry Safe(string pattern, string name, string en, string th,
        LocText? warning = null, bool inStore = true, bool copilot = false) => new()
    {
        Pattern = pattern, Name = name, Description = new LocText(en, th), Verdict = StoreAppVerdict.SafeToRemove,
        Warning = warning, InStore = inStore, IsCopilot = copilot,
    };

    private static StoreAppCatalogEntry Promoted(string pattern, string name) => new()
    {
        Pattern = pattern, Name = name, Description = PromotedDescription, Verdict = StoreAppVerdict.SafeToRemove,
        Promoted = true,
    };

    private static StoreAppCatalogEntry YourCall(string pattern, string name, string en, string th,
        string warnEn, string warnTh, bool inStore = true) => new()
    {
        Pattern = pattern, Name = name, Description = new LocText(en, th), Verdict = StoreAppVerdict.YourCall,
        Warning = new LocText(warnEn, warnTh), InStore = inStore,
    };

    private static StoreAppCatalogEntry Codec(string pattern, string name) => new()
    {
        Pattern = pattern, Name = name, Description = CodecDescription, Verdict = StoreAppVerdict.YourCall,
        Warning = CodecWarning,
    };

    /// <summary>The curated table. Exact names win over prefix patterns; among prefixes the first match wins.</summary>
    public static IReadOnlyList<StoreAppCatalogEntry> Entries { get; } =
    [
        // ---- Safe to remove: Copilot, Microsoft 365, mail and chat ----
        Safe("Microsoft.Copilot", "Microsoft Copilot",
            "Microsoft's AI chat assistant. Windows works the same without it.",
            "ผู้ช่วย AI แบบแชตของ Microsoft ไม่มีก็ใช้ Windows ได้ตามปกติ", copilot: true),
        Safe("Microsoft.Windows.Ai.Copilot.Provider", "Copilot in Windows",
            "The Copilot button and side panel of earlier Windows 11 versions.",
            "ปุ่มและแถบ Copilot ของ Windows 11 รุ่นก่อน", inStore: false, copilot: true),
        Safe("Microsoft.MicrosoftOfficeHub", "Microsoft 365 Copilot (Office hub)",
            "A launcher that advertises Microsoft 365 and Copilot. Word, Excel and the other Office programs you installed stay.",
            "หน้ารวมที่ชวนให้ใช้ Microsoft 365 และ Copilot ลบแล้ว Word, Excel และโปรแกรม Office ที่ติดตั้งไว้ยังอยู่ครบ", copilot: true),
        Safe("Microsoft.OutlookForWindows", "Outlook (new)",
            "The new Outlook mail app Windows installs by itself. Classic Outlook from Office is a different program and stays.",
            "แอปอีเมล Outlook ตัวใหม่ที่ Windows ติดตั้งให้เอง ไม่เกี่ยวกับ Outlook ตัวเดิมในชุด Office ซึ่งยังอยู่"),
        Safe("MSTeams", "Microsoft Teams",
            "The chat and meetings app Windows preinstalls. Keep it only if you use Teams.",
            "แอปแชตและประชุมที่ Windows ติดตั้งมาให้ เก็บไว้เฉพาะถ้าคุณใช้ Teams"),
        Safe("MicrosoftTeams", "Microsoft Teams (personal)",
            "The personal \"Chat\" edition of Teams from earlier Windows 11 versions.",
            "Teams รุ่นส่วนตัว (ปุ่ม Chat) ของ Windows 11 รุ่นก่อน", inStore: false),
        Safe("microsoft.windowscommunicationsapps", "Mail and Calendar",
            "The old Mail and Calendar apps. Microsoft switched them off at the end of 2024.",
            "แอป Mail และ Calendar ตัวเก่า ซึ่ง Microsoft ปิดการใช้งานไปเมื่อสิ้นปี 2024", inStore: false),
        Safe("Microsoft.SkypeApp", "Skype",
            "Skype was shut down in May 2025.",
            "Skype ปิดให้บริการไปแล้วเมื่อเดือนพฤษภาคม 2025", inStore: false),

        // ---- Safe to remove: news, feeds, games, help ----
        Safe("Clipchamp.Clipchamp", "Microsoft Clipchamp",
            "A video editor Windows promotes.",
            "โปรแกรมตัดต่อวิดีโอที่ Windows แถมมา"),
        Safe("Microsoft.BingNews", "News",
            "The MSN news app.",
            "แอปอ่านข่าวจาก MSN"),
        Safe("Microsoft.BingWeather", "Weather",
            "The MSN weather app.",
            "แอปพยากรณ์อากาศจาก MSN"),
        Safe("Microsoft.BingSearch", "Bing Search",
            "Adds Bing web results and suggestions to Windows Search.",
            "เพิ่มผลค้นหาเว็บและคำแนะนำจาก Bing ลงในช่องค้นหาของ Windows"),
        Safe("Microsoft.BingFinance", "Money",
            "The MSN finance app.",
            "แอปข่าวการเงินจาก MSN", inStore: false),
        Safe("Microsoft.BingSports", "Sports",
            "The MSN sports app.",
            "แอปข่าวกีฬาจาก MSN", inStore: false),
        Safe("Microsoft.BingTravel", "Travel",
            "The MSN travel app.",
            "แอปท่องเที่ยวจาก MSN", inStore: false),
        Safe("Microsoft.BingFoodAndDrink", "Food & Drink",
            "The MSN food app.",
            "แอปอาหารจาก MSN", inStore: false),
        Safe("Microsoft.BingHealthAndFitness", "Health & Fitness",
            "The MSN health app.",
            "แอปสุขภาพจาก MSN", inStore: false),
        Safe("Microsoft.MicrosoftSolitaireCollection", "Solitaire & Casual Games",
            "Card games with ads.",
            "เกมไพ่ที่มีโฆษณา"),
        Safe("Microsoft.GetHelp", "Get Help",
            "Microsoft's support app. Some troubleshooters in Settings open in it.",
            "แอปขอความช่วยเหลือจาก Microsoft ตัวแก้ปัญหาบางตัวในการตั้งค่าจะเปิดผ่านแอปนี้"),
        Safe("Microsoft.WindowsFeedbackHub", "Feedback Hub",
            "Sends suggestions and problem reports to Microsoft.",
            "ใช้ส่งข้อเสนอแนะและรายงานปัญหาให้ Microsoft"),
        Safe("Microsoft.549981C3F5F10", "Cortana",
            "Microsoft's retired voice assistant. It no longer works.",
            "ผู้ช่วยเสียงของ Microsoft ที่เลิกให้บริการแล้ว ใช้งานไม่ได้แล้ว", inStore: false),
        Safe("Microsoft.WindowsMaps", "Maps",
            "Microsoft's maps app, which Microsoft has discontinued.",
            "แอปแผนที่ของ Microsoft ซึ่งเลิกพัฒนาแล้ว", inStore: false),
        Safe("Microsoft.People", "People",
            "The old contacts app.",
            "แอปสมุดรายชื่อผู้ติดต่อตัวเก่า", inStore: false),
        Safe("Microsoft.Microsoft3DViewer", "3D Viewer",
            "Opens 3D model files.",
            "แอปดูไฟล์โมเดล 3 มิติ"),
        Safe("Microsoft.MSPaint", "Paint 3D",
            "The discontinued 3D drawing app. The normal Paint is a separate app and stays.",
            "แอปวาดภาพ 3 มิติที่เลิกพัฒนาแล้ว ไม่เกี่ยวกับ Paint ปกติซึ่งยังอยู่", inStore: false),
        Safe("Microsoft.Print3D", "Print 3D",
            "Sends 3D models to a 3D printer.",
            "ส่งโมเดล 3 มิติไปยังเครื่องพิมพ์ 3 มิติ", inStore: false),
        Safe("Microsoft.MixedReality.Portal", "Mixed Reality Portal",
            "For Windows Mixed Reality headsets, which Windows no longer supports.",
            "สำหรับแว่น Windows Mixed Reality ซึ่ง Windows เลิกรองรับแล้ว", inStore: false),
        Safe("Microsoft.Getstarted", "Tips",
            "Tips about using Windows.",
            "แอปแนะนำวิธีใช้ Windows", inStore: false),
        Safe("Microsoft.Messaging", "Messaging",
            "An old SMS app left over from Windows 10.",
            "แอป SMS ตัวเก่าที่ตกค้างมาจาก Windows 10", inStore: false),
        Safe("Microsoft.OneConnect", "Mobile Plans",
            "Sells mobile data plans for PCs with a SIM.",
            "ขายแพ็กเกจอินเทอร์เน็ตมือถือสำหรับเครื่องที่ใส่ซิมได้", inStore: false),
        Safe("Microsoft.Windows.DevHome", "Dev Home",
            "A dashboard for software developers, discontinued by Microsoft.",
            "หน้ารวมเครื่องมือสำหรับนักพัฒนา ซึ่ง Microsoft เลิกพัฒนาแล้ว", inStore: false),
        Safe("Microsoft.PowerAutomateDesktop", "Power Automate",
            "Builds and runs desktop automation flows.",
            "เครื่องมือสร้างขั้นตอนทำงานอัตโนมัติบนเดสก์ท็อป"),
        Safe("Microsoft.Todos", "Microsoft To Do",
            "A to-do list app.",
            "แอปรายการสิ่งที่ต้องทำ"),
        Safe("MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family",
            "The parental controls app (Family Safety).",
            "แอปควบคุมการใช้งานของบุตรหลาน (Family Safety)"),
        Safe("MicrosoftCorporationII.QuickAssist", "Quick Assist",
            "Lets someone you trust see or control this PC to help you.",
            "ให้คนที่คุณไว้ใจดูหรือควบคุมเครื่องนี้จากระยะไกลเพื่อช่วยเหลือ"),
        Safe("Microsoft.YourPhone", "Phone Link",
            "Shows your phone's notifications, messages and photos on the PC.",
            "แสดงการแจ้งเตือน ข้อความ และรูปจากมือถือบนคอมพิวเตอร์"),
        Safe("MicrosoftWindows.Client.WebExperience", "Widgets (Windows Web Experience Pack)",
            "The Widgets board with news and weather on the taskbar.",
            "แผง Widgets ที่แสดงข่าวและสภาพอากาศบนแถบงาน"),

        // ---- Safe to remove: Xbox apps and Game Bar ----
        Safe("Microsoft.GamingApp", "Xbox",
            "The Xbox and Game Pass app.",
            "แอป Xbox และ Game Pass", XboxWarning),
        Safe("Microsoft.XboxApp", "Xbox Console Companion",
            "The old Xbox app from Windows 10.",
            "แอป Xbox ตัวเก่าจาก Windows 10", XboxWarning, inStore: false),
        Safe("Microsoft.XboxGamingOverlay", "Game Bar",
            "The Win+G overlay for recording games and showing game widgets.",
            "แถบซ้อนหน้าจอ Win+G สำหรับบันทึกวิดีโอเกมและแสดงวิดเจ็ตเกม",
            new LocText(
                "Without Game Bar, Windows may ask for an app to open \"ms-gamingoverlay\" when a game starts. Turn off Settings › Gaming › Game Bar first. " + XboxWarning.En,
                "เมื่อไม่มี Game Bar แล้ว Windows อาจถามหาแอปสำหรับเปิด \"ms-gamingoverlay\" ตอนเริ่มเกม ให้ปิดที่ การตั้งค่า › เกม › Game Bar ก่อน " + XboxWarning.Th)),
        Safe("Microsoft.XboxGameOverlay", "Xbox Game Bar plugin",
            "A helper of Game Bar.",
            "ส่วนเสริมของ Game Bar", XboxWarning),
        Safe("Microsoft.XboxSpeechToTextOverlay", "Xbox speech-to-text overlay",
            "Turns game voice chat into text on screen.",
            "แปลงเสียงสนทนาในเกมเป็นข้อความบนหน้าจอ", XboxWarning),
        Safe("Microsoft.Edge.GameAssist", "Game Assist (Microsoft Edge)",
            "An Edge browser panel inside Game Bar.",
            "แผงเบราว์เซอร์ Edge ที่อยู่ใน Game Bar", inStore: false),

        // ---- Safe to remove: third-party apps Windows promotes (never bulk-selected) ----
        Promoted("SpotifyAB.SpotifyMusic", "Spotify"),
        Promoted("BytedancePte.Ltd.TikTok", "TikTok"),
        Promoted("Disney.37853FC22B2CE", "Disney+"),
        Promoted("king.com.*", "Candy Crush / King games"),
        Promoted("7EE7776C.LinkedInforWindows", "LinkedIn"),
        Promoted("Facebook.Facebook", "Facebook"),
        Promoted("FACEBOOK.FACEBOOK", "Facebook"),
        Promoted("Facebook.Instagram*", "Instagram"),
        Promoted("4DF9E0F8.Netflix", "Netflix"),
        Promoted("AmazonVideo.PrimeVideo", "Prime Video"),

        // ---- Your call: apps many people use ----
        YourCall("Microsoft.Windows.Photos", "Photos",
            "Windows' photo and video viewer.",
            "แอปดูรูปและวิดีโอของ Windows",
            "Photos is the default app for pictures. Have another viewer ready before removing it.",
            "Photos เป็นแอปเริ่มต้นสำหรับเปิดรูปภาพ ควรมีแอปดูรูปตัวอื่นก่อนลบ"),
        YourCall("Microsoft.WindowsCamera", "Camera",
            "Takes photos and video with the webcam.",
            "แอปถ่ายรูปและวิดีโอด้วยกล้องของเครื่อง",
            "Windows has no other built-in app for taking photos (video calls still work).",
            "Windows ไม่มีแอปถ่ายรูปตัวอื่นในตัว (วิดีโอคอลยังใช้ได้ตามปกติ)"),
        YourCall("Microsoft.WindowsCalculator", "Calculator",
            "The Windows calculator.",
            "เครื่องคิดเลขของ Windows",
            "Windows has no other built-in calculator.",
            "Windows ไม่มีเครื่องคิดเลขตัวอื่นในตัว"),
        YourCall("Microsoft.WindowsNotepad", "Notepad",
            "The plain text editor.",
            "โปรแกรมแก้ไขข้อความธรรมดา",
            "Text files will open in another editor.",
            "ไฟล์ข้อความจะไปเปิดด้วยโปรแกรมอื่นแทน"),
        YourCall("Microsoft.Paint", "Paint",
            "The simple image editor.",
            "โปรแกรมวาดและแก้ไขรูปอย่างง่าย",
            "Windows has no other built-in image editor.",
            "Windows ไม่มีโปรแกรมแก้ไขรูปตัวอื่นในตัว"),
        YourCall("Microsoft.ScreenSketch", "Snipping Tool",
            "Screenshots and screen recording.",
            "จับภาพและบันทึกวิดีโอหน้าจอ",
            "Win+Shift+S and the Print Screen screenshot tool stop working.",
            "Win+Shift+S และการจับภาพด้วยปุ่ม Print Screen จะใช้ไม่ได้"),
        YourCall("Microsoft.ZuneMusic", "Media Player",
            "Windows' music and video player.",
            "โปรแกรมเล่นเพลงและวิดีโอของ Windows",
            "Media Player is the default player. Have another player ready before removing it.",
            "Media Player เป็นโปรแกรมเล่นสื่อเริ่มต้น ควรมีโปรแกรมเล่นตัวอื่นก่อนลบ"),
        YourCall("Microsoft.ZuneVideo", "Movies & TV",
            "Plays video files and films bought from Microsoft.",
            "เล่นไฟล์วิดีโอและภาพยนตร์ที่ซื้อจาก Microsoft",
            "Films you bought from Microsoft can only be watched in this app.",
            "ภาพยนตร์ที่ซื้อจาก Microsoft ดูได้ผ่านแอปนี้เท่านั้น"),
        YourCall("Microsoft.MicrosoftStickyNotes", "Sticky Notes",
            "Notes on the desktop.",
            "กระดาษโน้ตบนเดสก์ท็อป",
            "Notes that are not synced to a Microsoft account are deleted with the app.",
            "โน้ตที่ไม่ได้ซิงค์กับบัญชี Microsoft จะถูกลบไปพร้อมแอป"),
        YourCall("Microsoft.WindowsTerminal", "Terminal",
            "The command-line window of Windows 11.",
            "หน้าต่างบรรทัดคำสั่งของ Windows 11",
            "Command Prompt and PowerShell will open in the old console window instead.",
            "Command Prompt และ PowerShell จะกลับไปเปิดในหน้าต่างคอนโซลแบบเก่า"),
        YourCall("Microsoft.WindowsAlarms", "Clock",
            "Alarms, timers and focus sessions.",
            "นาฬิกาปลุก ตัวจับเวลา และโหมดโฟกัส",
            "Your alarms and timers go with it.",
            "นาฬิกาปลุกและตัวจับเวลาที่ตั้งไว้จะหายไปด้วย"),
        YourCall("Microsoft.WindowsSoundRecorder", "Sound Recorder",
            "Records sound from the microphone.",
            "บันทึกเสียงจากไมโครโฟน",
            "Windows has no other built-in sound recorder.",
            "Windows ไม่มีโปรแกรมบันทึกเสียงตัวอื่นในตัว"),
        YourCall("Microsoft.Office.OneNote", "OneNote for Windows 10",
            "The older OneNote notebook app.",
            "แอปสมุดโน้ต OneNote ตัวเก่า",
            "Pages that have not synced to your Microsoft account are lost.",
            "หน้าที่ยังไม่ได้ซิงค์กับบัญชี Microsoft จะหายไป", inStore: false),

        // ---- Your call: needed by something else ----
        YourCall("Microsoft.XboxIdentityProvider", "Xbox Identity Provider",
            "Signs games in to Xbox Live.",
            "ใช้ลงชื่อเข้า Xbox Live ให้เกมต่างๆ",
            XboxWarning.En, XboxWarning.Th),
        YourCall("Microsoft.GamingServices", "Gaming Services",
            "Installs and runs Game Pass and Xbox games.",
            "ใช้ติดตั้งและเปิดเกมจาก Game Pass และ Xbox",
            XboxWarning.En, XboxWarning.Th),
        YourCall("Microsoft.Xbox.TCUI", "Xbox TCUI",
            "The Xbox friends and invitation windows inside games.",
            "หน้าต่างเพื่อนและคำเชิญของ Xbox ภายในเกม",
            XboxWarning.En, XboxWarning.Th),
        YourCall("Microsoft.StartExperiencesApp", "Start Experiences App",
            "Supplies the MSN feed shown in Widgets.",
            "ป้อนฟีดข่าวจาก MSN ให้ Widgets",
            "Remove it together with Widgets; on its own, Widgets may show an empty feed.",
            "ควรลบพร้อมกับ Widgets ถ้าลบตัวเดียว Widgets อาจแสดงฟีดว่าง"),
        YourCall("Microsoft.WidgetsPlatformRuntime", "Widgets Platform Runtime",
            "Lets other apps add their widgets to the Widgets board.",
            "ช่วยให้แอปอื่นเพิ่มวิดเจ็ตของตัวเองลงในแผง Widgets",
            "Widgets from other apps stop working.",
            "วิดเจ็ตของแอปอื่นจะใช้ไม่ได้"),
        YourCall("MicrosoftWindows.CrossDevice", "Cross Device Experience Host",
            "Shows your phone in File Explorer and powers Phone Link features.",
            "แสดงมือถือของคุณใน File Explorer และใช้กับฟีเจอร์ของ Phone Link",
            "Phone features in File Explorer and Phone Link stop working.",
            "ฟีเจอร์เกี่ยวกับมือถือใน File Explorer และ Phone Link จะใช้ไม่ได้"),

        // ---- Your call: codecs and extensions delivered through the Store ----
        Codec("Microsoft.HEIFImageExtension", "HEIF Image Extensions"),
        Codec("Microsoft.HEVCVideoExtension*", "HEVC Video Extensions"),
        Codec("Microsoft.VP9VideoExtensions", "VP9 Video Extensions"),
        Codec("Microsoft.AV1VideoExtension", "AV1 Video Extension"),
        Codec("Microsoft.MPEG2VideoExtension", "MPEG-2 Video Extension"),
        Codec("Microsoft.AVCEncoderVideoExtension", "AVC Encoder Video Extension"),
        Codec("Microsoft.WebMediaExtensions", "Web Media Extensions"),
        Codec("Microsoft.WebpImageExtension", "WebP Image Extensions"),
        Codec("Microsoft.RawImageExtension", "Raw Image Extension"),
        Codec("Microsoft.JpegXLImageExtension", "JPEG XL Image Extension"),
        Codec("Microsoft.DolbyAudioExtensions", "Dolby Digital audio extension"),
    ];

    #endregion

    /// <summary>The catalog row for a package name, or null.</summary>
    public static StoreAppCatalogEntry? Find(string packageName)
    {
        StoreAppCatalogEntry? prefix = null;
        foreach (var entry in Entries)
        {
            if (!entry.Matches(packageName)) continue;
            if (entry.IsExact) return entry;
            prefix ??= entry;
        }
        return prefix;
    }

    /// <summary>
    /// The protection a package has from its name and publisher alone — enough to refuse a
    /// removal before anything is asked of Windows.
    /// </summary>
    public static StoreAppProtection NameProtection(string packageName, string publisherId)
    {
        if (!IsValidToken(packageName) || !IsValidToken(publisherId))
            return StoreAppProtection.InvalidName;

        if (ProtectedNames.Any(n => packageName.Equals(n, StringComparison.OrdinalIgnoreCase)) ||
            ProtectedPrefixes.Any(p => packageName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return StoreAppProtection.ProtectedFamily;

        var isComponent = publisherId.Equals(WindowsPublisherId, StringComparison.OrdinalIgnoreCase) ||
                          WindowsComponentPrefixes.Any(p => packageName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (isComponent && Find(packageName) is not { IsExact: true })
            return StoreAppProtection.WindowsComponent;

        return StoreAppProtection.None;
    }

    /// <summary>What is known about a package when it is classified.</summary>
    public sealed class Facts
    {
        public string PackageName { get; init; } = "";
        public string PublisherId { get; init; } = "";
        /// <summary>False: Windows gave no flags for it (only a provisioned copy is known).</summary>
        public bool FlagsKnown { get; init; } = true;
        public bool IsFramework { get; init; }
        public bool IsResourcePackage { get; init; }
        public bool NonRemovable { get; init; }
        public string SignatureKind { get; init; } = "";
        /// <summary>The manifest could be read.</summary>
        public bool ManifestRead { get; init; }
        /// <summary>AllowExternalContent: the identity package of a desktop program installed elsewhere.</summary>
        public bool ExternalContent { get; init; }
        /// <summary>Publisher as shown (manifest display name and certificate subject).</summary>
        public string Publisher { get; init; } = "";
        public string DisplayName { get; init; } = "";
    }

    public sealed record Classification(
        StoreAppVerdict Verdict, StoreAppProtection Protection, StoreAppCatalogEntry? Entry,
        LocText? Description, LocText? Warning);

    private static Classification Blocked(StoreAppProtection why, StoreAppCatalogEntry? entry = null) =>
        new(StoreAppVerdict.Protected, why, entry, null, null);

    /// <summary>
    /// The one place that decides whether a package may be offered. Listing and removal both call
    /// it, so a package the list hides is also one the removal function refuses.
    /// </summary>
    public static Classification Classify(Facts facts)
    {
        var named = NameProtection(facts.PackageName, facts.PublisherId);
        if (named == StoreAppProtection.InvalidName) return Blocked(named);

        // What Windows itself says comes first: no catalog entry can lift these
        if (facts.IsFramework) return Blocked(StoreAppProtection.Framework);
        if (facts.IsResourcePackage) return Blocked(StoreAppProtection.ResourcePackage);
        if (facts.NonRemovable) return Blocked(StoreAppProtection.NonRemovable);
        if (facts.SignatureKind.Equals("System", StringComparison.OrdinalIgnoreCase))
            return Blocked(StoreAppProtection.SystemSigned);

        if (named != StoreAppProtection.None) return Blocked(named);

        var entry = Find(facts.PackageName);
        if (entry?.Verdict == StoreAppVerdict.Protected) return Blocked(StoreAppProtection.ProtectedFamily, entry);

        if (entry == null)
        {
            // Without flags from Windows an unknown package could be a framework
            if (!facts.FlagsKnown) return Blocked(StoreAppProtection.Unverified);

            // Identity package of a desktop program (context menu, notifications): it goes when that
            // program is uninstalled. An unreadable manifest of a developer-signed package is treated the same.
            var developerSigned = facts.SignatureKind.Equals("Developer", StringComparison.OrdinalIgnoreCase);
            if (facts.ExternalContent || (!facts.ManifestRead && developerSigned))
                return Blocked(StoreAppProtection.DesktopCompanion);

            return IsHardwareSupport(facts)
                ? new Classification(StoreAppVerdict.Hardware, StoreAppProtection.None, null, HardwareDescription, HardwareWarning)
                : new Classification(StoreAppVerdict.None, StoreAppProtection.None, null, null, null);
        }

        return new Classification(entry.Verdict, StoreAppProtection.None, entry, entry.Description, entry.Warning);
    }

    /// <summary>Driver control panels and device apps: by the maker's package prefix, or a hardware maker plus a telling word.</summary>
    private static bool IsHardwareSupport(Facts facts)
    {
        if (HardwarePackagePrefixes.Any(p => facts.PackageName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        var byMaker = HardwareMakers.Any(m => facts.Publisher.Contains(m, StringComparison.OrdinalIgnoreCase));
        if (!byMaker) return false;

        var text = facts.DisplayName + " " + facts.PackageName;
        return HardwareWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }
}

#endregion

/// <summary>
/// Microsoft Store / MSIX apps for the Uninstaller: lists them, says which ones Windows preinstalls,
/// and removes them completely — for every account and the provisioned copy — through Windows
/// PowerShell (the projects have no WinRT projections). Every result is judged from a fresh query
/// after the commands ran, never from "the command returned".
/// </summary>
public static class StoreAppService
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(60);
    // The provisioned list is a bonus for the list (DISM can be slow); removal reads it again itself
    private static readonly TimeSpan ProvisionedWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RemovalTimeout = TimeSpan.FromMinutes(5);

    private const long MaxManifestBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Regex HResultInText = new(@"0x8[0-9A-Fa-f]{7}", RegexOptions.CultureInvariant);

    // Windows will not remove the package at all
    private static readonly HashSet<string> RefusedCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "0x80073CFA",   // ERROR_REMOVE_FAILED — "part of Windows and cannot be uninstalled"
        "0x80070032",   // ERROR_NOT_SUPPORTED
        "0x80073D01",   // ERROR_DEPLOYMENT_BLOCKED_BY_POLICY
    };

    // Try again after closing the app
    private static readonly HashSet<string> InUseCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "0x80073D02",   // ERROR_PACKAGES_IN_USE
        "0x80073D00",   // ERROR_PACKAGE_UPDATING
    };

    #region JSON from PowerShell

    private sealed class QueryDto
    {
        public bool AllUsers { get; set; }
        public List<PackageDto>? Packages { get; set; }
        public bool StartOk { get; set; }
        public List<StartDto>? Start { get; set; }
        public bool ProvisionedOk { get; set; }
        public List<ProvisionedDto>? Provisioned { get; set; }
        public List<ErrorDto>? Errors { get; set; }
    }

    private sealed class PackageDto
    {
        public string? Name { get; set; }
        public string? FullName { get; set; }
        public string? Publisher { get; set; }
        public string? PublisherId { get; set; }
        public string? Version { get; set; }
        public bool IsFramework { get; set; }
        public bool IsResource { get; set; }
        public bool NonRemovable { get; set; }
        public string? Signature { get; set; }
        public string? Location { get; set; }
        public List<UserDto>? Users { get; set; }
    }

    private sealed class UserDto
    {
        public string? Sid { get; set; }
        public string? State { get; set; }
    }

    private sealed class StartDto
    {
        public string? Name { get; set; }
        public string? AppId { get; set; }
    }

    private sealed class ProvisionedDto
    {
        public string? DisplayName { get; set; }
        public string? PackageName { get; set; }
        public string? Version { get; set; }
        public string? PublisherId { get; set; }
    }

    private sealed class ErrorDto
    {
        public string? Step { get; set; }
        public string? Target { get; set; }
        public string? Code { get; set; }
        public string? Message { get; set; }
    }

    #endregion

    #region Scripts

    private const string ScriptHead =
        "$r = @{ AllUsers = $true; Packages = @(); StartOk = $false; Start = @(); ProvisionedOk = $false; Provisioned = @(); Errors = @() }\n";

    private const string ScriptTail =
        "[Console]::Out.Write((ConvertTo-Json -InputObject $r -Depth 6 -Compress))\n";

    /// <summary>
    /// Installed packages of every account; the signed-in account only when Windows refuses
    /// -AllUsers (not elevated). <paramref name="quotedName"/> must come from <see cref="CommandRunner.PsQuote"/>.
    /// </summary>
    private static string PackagesSection(string? quotedName)
    {
        var filter = quotedName == null ? "" : " -Name " + quotedName;
        return
            "$pk = @()\n" +
            "try { $pk = @(Get-AppxPackage -AllUsers" + filter + ") }\n" +
            "catch { $r.AllUsers = $false; $pk = @(Get-AppxPackage" + filter + ") }\n" +
            "$list = New-Object System.Collections.ArrayList\n" +
            "foreach ($p in $pk) {\n" +
            "  $users = New-Object System.Collections.ArrayList\n" +
            "  if ($r.AllUsers) { foreach ($u in @($p.PackageUserInformation)) { if ($u) { [void]$users.Add(@{ Sid = \"$($u.UserSecurityId.Sid)\"; State = \"$($u.InstallState)\" }) } } }\n" +
            "  [void]$list.Add(@{ Name = \"$($p.Name)\"; FullName = \"$($p.PackageFullName)\"; Publisher = \"$($p.Publisher)\"; PublisherId = \"$($p.PublisherId)\"; Version = \"$($p.Version)\"; IsFramework = [bool]$p.IsFramework; IsResource = [bool]$p.IsResourcePackage; NonRemovable = [bool]$p.NonRemovable; Signature = \"$($p.SignatureKind)\"; Location = \"$($p.InstallLocation)\"; Users = $users })\n" +
            "}\n" +
            "$r.Packages = $list\n";
    }

    /// <summary>Start menu names (Name + AppID, where AppID starts with the package family name).</summary>
    private const string StartSection =
        "try {\n" +
        "  $sa = New-Object System.Collections.ArrayList\n" +
        "  foreach ($a in @(Get-StartApps)) { [void]$sa.Add(@{ Name = \"$($a.Name)\"; AppId = \"$($a.AppID)\" }) }\n" +
        "  $r.Start = $sa; $r.StartOk = $true\n" +
        "} catch { }\n";

    /// <summary>Provisioned copies (needs elevation; failure is reported, not fatal).</summary>
    private static string ProvisionedSection(string? quotedName)
    {
        var filter = quotedName == null ? "" : "if (\"$($v.DisplayName)\" -ne " + quotedName + ") { continue }; ";
        return
            "try {\n" +
            "  $pv = New-Object System.Collections.ArrayList\n" +
            "  foreach ($v in @(Get-AppxProvisionedPackage -Online)) { " + filter +
            "[void]$pv.Add(@{ DisplayName = \"$($v.DisplayName)\"; PackageName = \"$($v.PackageName)\"; Version = \"$($v.Version)\"; PublisherId = \"$($v.PublisherId)\" }) }\n" +
            "  $r.Provisioned = $pv; $r.ProvisionedOk = $true\n" +
            "} catch { }\n";
    }

    /// <summary>One removal command. The same object gives the text shown on a dry run and the text that is run.</summary>
    private sealed record RemovalStep(bool Provisioned, string Target)
    {
        /// <summary>What a person would type; this is what a dry run prints.</summary>
        public string Display => Provisioned
            ? "Remove-AppxProvisionedPackage -Online -PackageName " + CommandRunner.PsQuote(Target)
            : "Remove-AppxPackage -Package " + CommandRunner.PsQuote(Target) + " -AllUsers";

        /// <summary>The same command with its error recorded instead of ending the script.</summary>
        public string Script
        {
            get
            {
                var target = CommandRunner.PsQuote(Target);
                return Provisioned
                    ? "try { " + Display + " | Out-Null } catch { Note 'provisioned' " + target + " $_ }\n"
                    // Older builds have no -AllUsers for removal: fall back to the signed-in account
                    : "try { " + Display + " } catch { Note 'allusers' " + target + " $_; " +
                      "try { Remove-AppxPackage -Package " + target + " } catch { Note 'user' " + target + " $_ } }\n";
            }
        }
    }

    private const string RemovalHead =
        "$errs = New-Object System.Collections.ArrayList\n" +
        "function Note($step, $target, $e) { [void]$errs.Add(@{ Step = $step; Target = $target; Code = ('0x{0:X8}' -f $e.Exception.HResult); Message = \"$($e.Exception.Message)\" }) }\n";

    #endregion

    #region List

    /// <summary>
    /// Every Store / MSIX app on this PC, classified. Never throws: a failure (PowerShell missing,
    /// an error, a hang cut off after 60 seconds, cancellation) comes back in the result so the
    /// classic program list keeps working.
    /// </summary>
    public static async Task<StoreAppListResult> ListAsync(CancellationToken ct)
    {
        using var provisionedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var packagesTask = CommandRunner.RunPowerShellAsync(
                ScriptHead + PackagesSection(null) + StartSection + ScriptTail, ListTimeout, ct);
            var provisionedTask = CommandRunner.RunPowerShellAsync(
                ScriptHead + ProvisionedSection(null) + ScriptTail, ListTimeout, provisionedCts.Token);

            var run = await packagesTask.ConfigureAwait(false);
            var failure = FailureOf(run);
            var query = failure == null ? Parse(run.StdOut) : null;
            if (query?.Packages == null)
            {
                return failure ?? new StoreAppListResult { Error = StoreAppListError.BadOutput };
            }

            QueryDto? provisioned = null;
            try
            {
                var provisionedRun = await provisionedTask.WaitAsync(ProvisionedWait, ct).ConfigureAwait(false);
                if (provisionedRun.Succeeded) provisioned = Parse(provisionedRun.StdOut);
            }
            catch (TimeoutException)
            {
                // Too slow for a list — the packages are shown without it
            }

            if (ct.IsCancellationRequested) return new StoreAppListResult { Error = StoreAppListError.Cancelled };

            var provisionedKnown = provisioned is { ProvisionedOk: true };
            return await Task.Run(() => Build(query, provisionedKnown ? provisioned!.Provisioned : null), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new StoreAppListResult { Error = StoreAppListError.Cancelled };
        }
        catch (Exception ex)
        {
            return new StoreAppListResult { Error = StoreAppListError.Failed, ErrorDetail = ex.Message };
        }
        finally
        {
            // Never leave the provisioned query running behind a list that has already returned
            provisionedCts.Cancel();
        }
    }

    private static StoreAppListResult? FailureOf(CommandResult run)
    {
        if (run.Cancelled) return new StoreAppListResult { Error = StoreAppListError.Cancelled };
        if (run.TimedOut) return new StoreAppListResult { Error = StoreAppListError.TimedOut };
        if (run.StartFailed) return new StoreAppListResult { Error = StoreAppListError.Failed };
        if (run.ExitCode != 0)
            return new StoreAppListResult { Error = StoreAppListError.Failed, ErrorDetail = FirstLine(run.StdErr) };
        return null;
    }

    private static QueryDto? Parse(string stdout)
    {
        try
        {
            var start = stdout.IndexOf('{');
            var end = stdout.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            return JsonSerializer.Deserialize<QueryDto>(stdout[start..(end + 1)], JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > 300 ? line[..300] : line;
    }

    /// <summary>Groups packages into families, finds names, classifies. <paramref name="provisioned"/> null = not known.</summary>
    private static StoreAppListResult Build(QueryDto query, List<ProvisionedDto>? provisioned)
    {
        var packages = query.Packages ?? [];
        var result = new StoreAppListResult
        {
            AllUsers = query.AllUsers,
            ProvisionedKnown = provisioned != null,
            PackageCount = packages.Count,
        };

        var startNames = StartNames(query);
        var currentSid = CurrentUserSid();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var family in packages
                     .Where(p => !string.IsNullOrEmpty(p.Name))
                     .GroupBy(p => (p.Name ?? "") + "_" + (p.PublisherId ?? ""), StringComparer.OrdinalIgnoreCase))
        {
            var app = BuildApp(family.ToList(), provisioned, startNames, query.AllUsers, currentSid);
            seen.Add(app.FamilyName);
            (app.IsOffered ? result.Apps : result.Protected).Add(app);
        }

        // A provisioned copy without any installed package: Windows would install it for the next new account
        foreach (var copy in provisioned ?? [])
        {
            var name = copy.DisplayName ?? "";
            var publisherId = PublisherIdOf(copy);
            if (name.Length == 0 || !seen.Add(name + "_" + publisherId)) continue;

            var app = BuildProvisionedOnly(name, publisherId, copy.Version ?? "");
            (app.IsOffered ? result.Apps : result.Protected).Add(app);
        }

        return result;
    }

    private static Dictionary<string, List<string>> StartNames(QueryDto query)
    {
        var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in query.Start ?? [])
        {
            var appId = entry.AppId ?? "";
            var bang = appId.IndexOf('!');
            var name = (entry.Name ?? "").Trim();
            if (bang <= 0 || name.Length == 0) continue;

            var family = appId[..bang];
            if (!names.TryGetValue(family, out var list)) names[family] = list = [];
            if (!list.Contains(name, StringComparer.CurrentCultureIgnoreCase)) list.Add(name);
        }
        return names;
    }

    private static string PublisherIdOf(ProvisionedDto copy)
    {
        if (!string.IsNullOrEmpty(copy.PublisherId)) return copy.PublisherId!;
        var packageName = copy.PackageName ?? "";
        var underscore = packageName.LastIndexOf('_');
        return underscore >= 0 ? packageName[(underscore + 1)..] : "";
    }

    private static bool IsCopyOf(ProvisionedDto copy, string packageName, string publisherId) =>
        packageName.Equals(copy.DisplayName, StringComparison.OrdinalIgnoreCase) &&
        publisherId.Equals(PublisherIdOf(copy), StringComparison.OrdinalIgnoreCase);

    private static string CurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static StoreApp BuildApp(List<PackageDto> family, List<ProvisionedDto>? provisioned,
        Dictionary<string, List<string>> startNames, bool allUsers, string currentSid)
    {
        var newest = family.OrderByDescending(p => ParseVersion(p.Version)).First();
        var name = newest.Name ?? "";
        var publisherId = newest.PublisherId ?? "";
        var location = newest.Location ?? "";
        var signature = newest.Signature ?? "";

        var manifest = ReadManifest(location);
        var entry = StoreAppCatalog.Find(name);

        // Name the user knows: Start menu → catalog → manifest → package name
        var startName = startNames.TryGetValue(name + "_" + publisherId, out var starts) ? string.Join(" / ", starts.Take(3)) : "";
        var manifestName = startName.Length > 0 || entry != null
            ? ""
            : ResolveText(manifest.DisplayName, newest.FullName ?? "", name, location, manifest.ExternalContent);
        var displayName = startName.Length > 0 ? startName
                        : entry != null ? entry.Name
                        : manifestName.Length > 0 ? manifestName
                        : name;

        var publisher = ResolveText(manifest.PublisherDisplayName, newest.FullName ?? "", name, location, manifest.ExternalContent);
        var subject = CommonName(newest.Publisher ?? "");
        if (publisher.Length == 0) publisher = subject;

        var classification = StoreAppCatalog.Classify(new StoreAppCatalog.Facts
        {
            PackageName = name,
            PublisherId = publisherId,
            // One framework or system package in the family protects the whole family
            IsFramework = family.Any(p => p.IsFramework),
            IsResourcePackage = family.Any(p => p.IsResource),
            NonRemovable = family.Any(p => p.NonRemovable),
            SignatureKind = family.Any(p => "System".Equals(p.Signature, StringComparison.OrdinalIgnoreCase)) ? "System" : signature,
            ManifestRead = manifest.Read,
            ExternalContent = manifest.ExternalContent,
            Publisher = publisher + " " + subject,
            DisplayName = displayName,
        });

        var fullNames = family.Select(p => p.FullName ?? "").Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var verdict = classification.Verdict;
        var protection = classification.Protection;
        if (verdict != StoreAppVerdict.Protected && fullNames.Any(f => !StoreAppCatalog.IsValidToken(f)))
        {
            verdict = StoreAppVerdict.Protected;
            protection = StoreAppProtection.InvalidName;
        }

        var users = family.SelectMany(p => p.Users ?? []).ToList();
        var mine = !allUsers || users.Any(u => IsInstalled(u) && currentSid.Equals(u.Sid, StringComparison.OrdinalIgnoreCase));
        var others = allUsers && users.Any(u => !currentSid.Equals(u.Sid, StringComparison.OrdinalIgnoreCase));

        return new StoreApp
        {
            PackageName = name,
            PublisherId = publisherId,
            DisplayName = displayName,
            CatalogName = entry != null && !entry.Name.Equals(displayName, StringComparison.CurrentCultureIgnoreCase) ? entry.Name : "",
            Publisher = publisher,
            Version = newest.Version ?? "",
            SignatureKind = signature,
            InstallLocation = location,
            FullNames = fullNames,
            InstalledOn = FolderDate(location),
            InstalledForCurrentUser = mine,
            OnOtherAccounts = others,
            IsProvisioned = provisioned == null ? null : provisioned.Any(c => IsCopyOf(c, name, publisherId)),
            Verdict = verdict,
            Protection = protection,
            Catalog = classification.Entry,
            Description = classification.Description,
            Warning = classification.Warning,
            ReinstallFromStore = classification.Entry?.InStore ?? signature.Equals("Store", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static StoreApp BuildProvisionedOnly(string name, string publisherId, string version)
    {
        var classification = StoreAppCatalog.Classify(new StoreAppCatalog.Facts
        {
            PackageName = name,
            PublisherId = publisherId,
            FlagsKnown = false,
        });

        return new StoreApp
        {
            PackageName = name,
            PublisherId = publisherId,
            DisplayName = classification.Entry?.Name ?? name,
            Version = version,
            IsProvisioned = true,
            OnOtherAccounts = true,
            Verdict = classification.Verdict,
            Protection = classification.Protection,
            Catalog = classification.Entry,
            Description = classification.Description,
            Warning = classification.Warning,
            ReinstallFromStore = classification.Entry?.InStore ?? false,
        };
    }

    private static bool IsInstalled(UserDto user) =>
        "Installed".Equals(user.State, StringComparison.OrdinalIgnoreCase);

    private static Version ParseVersion(string? text) =>
        Version.TryParse(text, out var version) ? version : new Version(0, 0);

    /// <summary>"CN=Microsoft Corporation, O=…" → "Microsoft Corporation"; a GUID subject (Store-signed third party) → "".</summary>
    private static string CommonName(string subject)
    {
        foreach (var part in subject.Split(','))
        {
            var trimmed = part.Trim();
            if (!trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) continue;
            var name = trimmed[3..].Trim().Trim('"');
            return Guid.TryParse(name, out _) ? "" : name;
        }
        return "";
    }

    private static DateTime? FolderDate(string installLocation)
    {
        try
        {
            if (installLocation.Length == 0) return null;
            var folder = new DirectoryInfo(installLocation);
            return folder.Exists ? folder.CreationTime : null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Manifest and names

    private sealed record ManifestInfo(bool Read, string DisplayName, string PublisherDisplayName, bool ExternalContent)
    {
        public static readonly ManifestInfo Unread = new(false, "", "", false);
    }

    /// <summary>DisplayName, PublisherDisplayName and AllowExternalContent from AppxManifest.xml (no DTD, no external entities).</summary>
    private static ManifestInfo ReadManifest(string installLocation)
    {
        try
        {
            if (installLocation.Length == 0) return ManifestInfo.Unread;
            var file = new FileInfo(Path.Combine(installLocation, "AppxManifest.xml"));
            if (!file.Exists || file.Length > MaxManifestBytes) return ManifestInfo.Unread;

            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var stream = file.OpenRead();
            using var reader = XmlReader.Create(stream, settings);
            var properties = XDocument.Load(reader).Root?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Properties");
            if (properties == null) return new ManifestInfo(true, "", "", false);

            string Value(string localName) =>
                properties.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim() ?? "";

            return new ManifestInfo(true, Value("DisplayName"), Value("PublisherDisplayName"),
                Value("AllowExternalContent").Equals("true", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return ManifestInfo.Unread;
        }
    }

    /// <summary>
    /// A manifest text as a person reads it. Plain text is returned as is; an "ms-resource:" reference
    /// is looked up in the package's resources — only for packages in a WindowsApps folder (written by
    /// Windows only), so an elevated WinXTools never parses resource files from a user-writable place.
    /// Returns "" when there is nothing readable.
    /// </summary>
    private static string ResolveText(string value, string fullName, string packageName, string installLocation, bool externalContent)
    {
        if (value.Length == 0) return "";
        if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return value;
        if (externalContent || fullName.Length == 0 || !IsInWindowsApps(installLocation)) return "";

        var key = value["ms-resource:".Length..];
        var candidates = new List<string>();
        if (key.StartsWith("//", StringComparison.Ordinal)) candidates.Add("ms-resource:" + key);
        else if (key.StartsWith('/')) candidates.Add("ms-resource://" + packageName + key);
        else if (key.Contains('/'))
        {
            candidates.Add("ms-resource://" + packageName + "/" + key);
            candidates.Add("ms-resource://" + packageName + "/resources/" + key);
        }
        else
        {
            candidates.Add("ms-resource://" + packageName + "/resources/" + key);
            candidates.Add("ms-resource://" + packageName + "/" + key);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var buffer = new StringBuilder(512);
                var hr = SHLoadIndirectString("@{" + fullName + "?" + candidate + "}", buffer, (uint)buffer.Capacity, IntPtr.Zero);
                var text = buffer.ToString().Trim();
                if (hr == 0 && text.Length > 0 && !text.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    return text;
            }
            catch
            {
                // Resource lookup is a nicety — fall back to the next name source
            }
        }

        return "";
    }

    /// <summary>…\WindowsApps\PackageFullName on any drive: only TrustedInstaller writes there.</summary>
    private static bool IsInWindowsApps(string installLocation)
    {
        try
        {
            var parent = Path.GetDirectoryName(installLocation.TrimEnd('\\'));
            return parent != null &&
                   Path.GetFileName(parent).Equals("WindowsApps", StringComparison.OrdinalIgnoreCase) &&
                   Path.IsPathFullyQualified(installLocation);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, uint cchOutBuf, IntPtr ppvReserved);

    #endregion

    #region Size

    /// <summary>
    /// Size of the app's package folder (links not followed), or null when it can't be read. Walks
    /// the disk, so the list shows first and sizes are filled in afterwards.
    /// </summary>
    public static long? MeasureSize(StoreApp app, CancellationToken ct)
    {
        if (!IsInWindowsApps(app.InstallLocation)) return null;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
        };

        long bytes = 0;
        var any = false;
        var pending = new Stack<string>();
        pending.Push(app.InstallLocation);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                foreach (var item in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    var isLink = (item.Attributes & FileAttributes.ReparsePoint) != 0;
                    if (item is DirectoryInfo)
                    {
                        if (!isLink) pending.Push(item.FullName);
                    }
                    else if (item is FileInfo file && !isLink)
                    {
                        bytes += file.Length;
                        any = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Unreadable folder — the size stays a lower bound
            }
        }

        return any ? bytes : null;
    }

    #endregion

    #region Remove

    /// <summary>
    /// Removes one app completely: the provisioned copy (so Windows does not install it again for
    /// new accounts or after a feature update) and the package for every account.
    ///
    /// Refused without running anything when the name fails validation or the package is protected
    /// — decided from a fresh query, by the same rules the list uses. With <paramref name="dryRun"/>
    /// nothing is removed: the exact commands come back in <see cref="StoreAppRemovalResult.Commands"/>.
    /// The result is what Windows reports afterwards. <paramref name="ct"/> stops the work before
    /// the commands start; a removal that has started is never interrupted.
    /// </summary>
    public static async Task<StoreAppRemovalResult> RemoveAsync(
        string packageName, string publisherId, bool dryRun, CancellationToken ct)
    {
        var result = new StoreAppRemovalResult { PackageName = packageName ?? "", PublisherId = publisherId ?? "" };

        // Wrong input never reaches PowerShell
        var named = StoreAppCatalog.NameProtection(result.PackageName, result.PublisherId);
        if (named != StoreAppProtection.None)
        {
            result.Status = StoreAppRemovalStatus.Refused;
            result.Protection = named;
            return result;
        }

        try
        {
            var quotedName = CommandRunner.PsQuote(result.PackageName);
            var queryScript = ScriptHead + PackagesSection(quotedName) + ProvisionedSection(quotedName) + ScriptTail;

            // 1. What is really there right now
            var beforeRun = await CommandRunner.RunPowerShellAsync(queryScript, ListTimeout, ct).ConfigureAwait(false);
            if (beforeRun.Cancelled || ct.IsCancellationRequested)
            {
                result.Status = StoreAppRemovalStatus.Cancelled;
                return result;
            }

            var before = beforeRun.Succeeded ? Parse(beforeRun.StdOut) : null;
            if (before?.Packages == null)
            {
                // No answer (PowerShell missing, an error, the 60-second limit): nothing is run blind
                result.Status = StoreAppRemovalStatus.NotStarted;
                result.ErrorMessage = beforeRun.TimedOut ? "" : FirstLine(beforeRun.StdErr);
                return result;
            }

            var packages = PackagesOf(before, result.PackageName, result.PublisherId);
            var copies = CopiesOf(before, result.PackageName, result.PublisherId);
            var sid = CurrentUserSid();
            var mineBefore = InstalledFor(packages, before.AllUsers, sid, mine: true);
            result.AllUsersChecked = before.AllUsers;
            result.ProvisionedChecked = before.ProvisionedOk;

            if (packages.Count == 0 && copies.Count == 0)
            {
                result.Status = StoreAppRemovalStatus.AlreadyGone;
                return result;
            }

            // 2. Same rules as the list, on the fresh data
            var app = packages.Count > 0
                ? BuildApp(packages, before.ProvisionedOk ? before.Provisioned : null, [], before.AllUsers, sid)
                : BuildProvisionedOnly(result.PackageName, result.PublisherId, "");
            if (!app.IsOffered)
            {
                result.Status = StoreAppRemovalStatus.Refused;
                result.Protection = app.Protection;
                return result;
            }

            // 3. The commands — every name validated, then quoted
            var steps = new List<RemovalStep>();
            foreach (var copy in copies)
                steps.Add(new RemovalStep(true, copy.PackageName ?? ""));
            foreach (var fullName in app.FullNames)
                steps.Add(new RemovalStep(false, fullName));

            if (steps.Count == 0 || steps.Any(s => !StoreAppCatalog.IsValidToken(s.Target)))
            {
                result.Status = StoreAppRemovalStatus.Refused;
                result.Protection = StoreAppProtection.InvalidName;
                return result;
            }

            result.Commands.AddRange(steps.Select(s => s.Display));
            if (dryRun)
            {
                result.Status = StoreAppRemovalStatus.DryRun;
                return result;
            }

            if (ct.IsCancellationRequested)
            {
                result.Status = StoreAppRemovalStatus.Cancelled;
                return result;
            }

            // 4. Run them, then ask Windows again in the same script. Not cancellable: stopping
            //    PowerShell in the middle of a removal could leave a package half removed.
            var script = ScriptHead + RemovalHead + string.Concat(steps.Select(s => s.Script)) +
                         "$r.Errors = $errs\n" +
                         PackagesSection(quotedName) + ProvisionedSection(quotedName) + ScriptTail;
            var run = await CommandRunner.RunPowerShellAsync(script, RemovalTimeout, CancellationToken.None).ConfigureAwait(false);

            var after = run.Succeeded ? Parse(run.StdOut) : null;
            var errors = after?.Errors ?? [];
            if (after?.Packages == null)
            {
                // Timed out or unreadable output: what happened is still decided by a fresh query
                var again = await CommandRunner.RunPowerShellAsync(queryScript, ListTimeout, CancellationToken.None).ConfigureAwait(false);
                after = again.Succeeded ? Parse(again.StdOut) : null;
            }

            Judge(result, after, errors, sid, mineBefore);

            if (result.Status == StoreAppRemovalStatus.Removed)
                await CheckAppDataAsync(result).ConfigureAwait(false);

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Status = StoreAppRemovalStatus.Cancelled;
            return result;
        }
        catch (Exception ex)
        {
            result.Status = StoreAppRemovalStatus.Unknown;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    private static List<PackageDto> PackagesOf(QueryDto query, string packageName, string publisherId) =>
        (query.Packages ?? [])
            .Where(p => packageName.Equals(p.Name, StringComparison.OrdinalIgnoreCase) &&
                        publisherId.Equals(p.PublisherId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static List<ProvisionedDto> CopiesOf(QueryDto query, string packageName, string publisherId) =>
        (query.Provisioned ?? []).Where(c => IsCopyOf(c, packageName, publisherId)).ToList();

    /// <summary>
    /// Is the package installed for the signed-in account (<paramref name="mine"/>) or for any other
    /// account? Without -AllUsers data Windows only reported the signed-in account's packages.
    /// </summary>
    private static bool InstalledFor(List<PackageDto> packages, bool allUsers, string sid, bool mine)
    {
        if (!allUsers) return mine && packages.Count > 0;
        return packages.SelectMany(p => p.Users ?? [])
            .Any(u => IsInstalled(u) && sid.Equals(u.Sid, StringComparison.OrdinalIgnoreCase) == mine);
    }

    /// <summary>Decides the outcome from the query made after the commands ran.</summary>
    private static void Judge(StoreAppRemovalResult result, QueryDto? after, List<ErrorDto> errors, string sid, bool mineBefore)
    {
        var first = errors.FirstOrDefault(e => e.Step != "user") ?? errors.FirstOrDefault();
        if (first != null)
        {
            // Appx puts the real code in the message ("Deployment failed with HRESULT: 0x80073CFA…")
            var inText = HResultInText.Match(first.Message ?? "");
            result.ErrorCode = (inText.Success ? inText.Value : first.Code ?? "").ToUpperInvariant().Replace("0X", "0x");
            result.ErrorMessage = FirstLine(first.Message ?? "");
        }

        if (after?.Packages == null)
        {
            result.Status = StoreAppRemovalStatus.Unknown;
            return;
        }

        var left = PackagesOf(after, result.PackageName, result.PublisherId);
        var copies = CopiesOf(after, result.PackageName, result.PublisherId);
        result.AllUsersChecked = after.AllUsers;
        result.ProvisionedChecked = after.ProvisionedOk;
        result.StillInstalled.AddRange(left.Select(p => p.FullName ?? ""));
        result.StillProvisioned.AddRange(copies.Select(c => c.PackageName ?? ""));

        if (left.Count == 0)
        {
            result.Status = copies.Count == 0 ? StoreAppRemovalStatus.Removed : StoreAppRemovalStatus.PartlyRemoved;
            return;
        }

        // Still there. Why, as far as Windows said
        var codes = errors.Select(e => HResultInText.Match(e.Message ?? "") is { Success: true } m ? m.Value : e.Code ?? "").ToList();
        var byError = codes.Any(RefusedCodes.Contains) ? StoreAppRemovalStatus.WindowsRefused
                    : codes.Any(InUseCodes.Contains) ? StoreAppRemovalStatus.InUse
                    : StoreAppRemovalStatus.StillInstalled;

        if (InstalledFor(left, after.AllUsers, sid, mine: true))
        {
            result.Status = byError;
        }
        else if (InstalledFor(left, after.AllUsers, sid, mine: false))
        {
            // It left the signed-in account but another account still has it — unless it never was
            // installed for the signed-in account, then nothing was removed at all
            result.Status = mineBefore ? StoreAppRemovalStatus.PartlyRemoved : byError;
        }
        else if (errors.Count > 0)
        {
            result.Status = byError;
        }
        else
        {
            // Installed for nobody any more; Windows only still keeps the package files
            result.Status = StoreAppRemovalStatus.PartlyRemoved;
            result.StagedOnly = true;
        }
    }

    /// <summary>Windows deletes %LOCALAPPDATA%\Packages\family with the package; give it a moment, then report.</summary>
    private static async Task CheckAppDataAsync(StoreAppRemovalResult result)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length == 0) return;

            var path = Path.Combine(local, "Packages", result.PackageName + "_" + result.PublisherId);
            for (var attempt = 0; attempt < 5 && Directory.Exists(path); attempt++)
                await Task.Delay(400).ConfigureAwait(false);

            if (Directory.Exists(path))
            {
                result.AppDataPath = path;
                result.AppDataLeft = true;
            }
        }
        catch
        {
            // Can't look — say nothing rather than guess
        }
    }

    #endregion
}

#region Keep it from coming back

public enum KeepAwayValueStatus
{
    /// <summary>The value now has the "keep away" data (checked by reading it back).</summary>
    Applied,
    /// <summary>It already had that data before WinXTools touched it; nothing recorded, nothing to undo.</summary>
    AlreadySet,
    /// <summary>Put back to what it was (checked by reading it back).</summary>
    Restored,
    /// <summary>Dry run: what would be written.</summary>
    Planned,
    Failed,
}

public sealed record KeepAwayChange(string Location, string ValueName, string Before, string After, KeepAwayValueStatus Status);

public sealed class KeepAwayResult
{
    public List<KeepAwayChange> Changes { get; } = [];
    public bool Success => Changes.All(c => c.Status != KeepAwayValueStatus.Failed);
    public string Error { get; set; } = "";
}

/// <summary>
/// The documented settings that stop Windows pushing apps again: no consumer features (HKLM policy),
/// no silently installed / preinstalled / suggested apps (ContentDeliveryManager), and — when a
/// Copilot app is removed — Windows Copilot off. Only ever applied when the user ticks it in the
/// confirmation. What each value was before (data + kind, or "absent") is recorded first, the way
/// WindowsOptimizer does for its registry switches (same value type, same admin-only store), so
/// Undo puts back exactly what this PC had.
/// </summary>
public static class StoreAppKeepAway
{
    // Own file: WindowsOptimizer keeps registry_originals.json in memory and rewrites it whole,
    // so a second writer to that file would lose entries.
    private const string OriginalsFileName = "storeapp_keepaway_originals.json";

    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    private static readonly object Gate = new();

    private sealed record PolicyValue(RegistryPath Path, bool CopilotOnly);

    private static PolicyValue Value(RegistryHive hive, string path, string name, int data, bool copilotOnly = false) =>
        new(new RegistryPath { Hive = hive, Path = path, ValueName = name, DisableValue = data }, copilotOnly);

    private static readonly PolicyValue[] Values =
    [
        Value(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1),
        Value(RegistryHive.CurrentUser, ContentDelivery, "SilentInstalledAppsEnabled", 0),
        Value(RegistryHive.CurrentUser, ContentDelivery, "ContentDeliveryAllowed", 0),
        Value(RegistryHive.CurrentUser, ContentDelivery, "PreInstalledAppsEnabled", 0),
        Value(RegistryHive.CurrentUser, ContentDelivery, "OemPreInstalledAppsEnabled", 0),
        Value(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, copilotOnly: true),
    ];

    private static string OriginalKey(RegistryPath path) => $@"{path.Hive}\{path.Path}\{path.ValueName}";

    private static string Location(RegistryPath path) =>
        (path.Hive == RegistryHive.LocalMachine ? @"HKLM\" : @"HKCU\") + path.Path;

    private static RegistryKey OpenBase(RegistryHive hive) =>
        RegistryKey.OpenBaseKey(hive, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default);

    /// <summary>The recorded originals, limited to the values this class manages and to kinds it can write back.</summary>
    private static Dictionary<string, WindowsOptimizer.SavedRegistryValue> LoadOriginals()
    {
        var known = Values.Select(v => OriginalKey(v.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loaded = new Dictionary<string, WindowsOptimizer.SavedRegistryValue>(StringComparer.OrdinalIgnoreCase);

        var state = AdminOnlyStore.Load<WindowsOptimizer.RegistryOriginalsState>(OriginalsFileName);
        foreach (var (key, saved) in state?.Values ?? [])
        {
            if (saved == null || !known.Contains(key)) continue;
            if (saved.Existed && !IsRestorable((RegistryValueKind)saved.Kind)) continue;
            loaded[key] = saved;
        }
        return loaded;
    }

    private static bool SaveOriginals(Dictionary<string, WindowsOptimizer.SavedRegistryValue> originals) =>
        AdminOnlyStore.Save(OriginalsFileName, new WindowsOptimizer.RegistryOriginalsState
        {
            Values = new Dictionary<string, WindowsOptimizer.SavedRegistryValue>(originals),
        });

    private static bool IsRestorable(RegistryValueKind kind) =>
        kind is RegistryValueKind.DWord or RegistryValueKind.QWord or RegistryValueKind.String or RegistryValueKind.ExpandString;

    /// <summary>True when WinXTools changed something here that Undo can put back.</summary>
    public static bool HasUndo
    {
        get
        {
            lock (Gate)
            {
                try { return LoadOriginals().Count > 0; }
                catch { return false; }
            }
        }
    }

    /// <summary>The values that would be written, for the confirmation text (e.g. "HKCU\…\SilentInstalledAppsEnabled = 0").</summary>
    public static List<string> Describe(bool includeCopilot) =>
        Values.Where(v => includeCopilot || !v.CopilotOnly)
              .Select(v => $@"{Location(v.Path)}\{v.Path.ValueName} = {v.Path.DisableValue}")
              .ToList();

    private static string Show(object? data, RegistryValueKind? kind) =>
        data == null ? "(absent)" : $"{Convert.ToString(data, CultureInfo.InvariantCulture)} ({kind})";

    /// <summary>
    /// Records what each value is, then writes the "keep away" data and reads it back. A value is
    /// only written once its original is safely stored; with <paramref name="dryRun"/> nothing is
    /// written or stored and the result lists what would change.
    /// </summary>
    public static KeepAwayResult Apply(bool includeCopilot, bool dryRun)
    {
        var result = new KeepAwayResult();
        lock (Gate)
        {
            Dictionary<string, WindowsOptimizer.SavedRegistryValue> originals;
            try { originals = LoadOriginals(); }
            catch (Exception ex) { result.Error = ex.Message; originals = new(StringComparer.OrdinalIgnoreCase); }

            foreach (var value in Values.Where(v => includeCopilot || !v.CopilotOnly))
            {
                var path = value.Path;
                var after = $"{path.DisableValue} ({RegistryValueKind.DWord})";
                var before = "?";
                try
                {
                    using var root = OpenBase(path.Hive);

                    object? current;
                    RegistryValueKind? kind = null;
                    using (var read = root.OpenSubKey(path.Path))
                    {
                        current = read?.GetValue(path.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (current != null) kind = read!.GetValueKind(path.ValueName);
                    }
                    before = Show(current, kind);

                    if (kind == RegistryValueKind.DWord && current is int number && number == path.DisableValue)
                    {
                        result.Changes.Add(new KeepAwayChange(Location(path), path.ValueName, before, after, KeepAwayValueStatus.AlreadySet));
                        continue;
                    }

                    if (dryRun)
                    {
                        result.Changes.Add(new KeepAwayChange(Location(path), path.ValueName, before, after, KeepAwayValueStatus.Planned));
                        continue;
                    }

                    // The first value seen is the original; a kind that can't be written back exactly is left alone
                    var id = OriginalKey(path);
                    if (!originals.ContainsKey(id))
                    {
                        if (kind is { } existing && !IsRestorable(existing))
                            throw new InvalidOperationException($"unexpected value type {existing}");

                        originals[id] = current == null
                            ? new WindowsOptimizer.SavedRegistryValue { Existed = false }
                            : new WindowsOptimizer.SavedRegistryValue
                            {
                                Existed = true,
                                Kind = (int)kind!.Value,
                                Data = Convert.ToString(current, CultureInfo.InvariantCulture),
                            };
                        if (!SaveOriginals(originals))
                        {
                            originals.Remove(id);
                            throw new InvalidOperationException("the original value could not be stored, so nothing was changed");
                        }
                    }

                    using (var write = root.CreateSubKey(path.Path, true))
                        write.SetValue(path.ValueName, path.DisableValue, RegistryValueKind.DWord);

                    // Report what the registry now says, not what we meant to write
                    using var check = root.OpenSubKey(path.Path);
                    var now = check?.GetValue(path.ValueName);
                    var applied = now is int written && written == path.DisableValue;
                    result.Changes.Add(new KeepAwayChange(Location(path), path.ValueName, before, after,
                        applied ? KeepAwayValueStatus.Applied : KeepAwayValueStatus.Failed));
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                    result.Changes.Add(new KeepAwayChange(Location(path), path.ValueName, before, after, KeepAwayValueStatus.Failed));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Puts every recorded value back (deletes it when it did not exist) and reads it back. Values
    /// that were already set before WinXTools touched them have no record and are left as they are.
    /// </summary>
    public static KeepAwayResult Undo(bool dryRun)
    {
        var result = new KeepAwayResult();
        lock (Gate)
        {
            Dictionary<string, WindowsOptimizer.SavedRegistryValue> originals;
            try { originals = LoadOriginals(); }
            catch (Exception ex) { result.Error = ex.Message; return result; }

            foreach (var value in Values)
            {
                var path = value.Path;
                var id = OriginalKey(path);
                if (!originals.TryGetValue(id, out var saved)) continue;

                var kind = (RegistryValueKind)saved.Kind;
                var target = saved.Existed ? $"{saved.Data} ({kind})" : "(absent)";
                var before = "?";
                try
                {
                    using var root = OpenBase(path.Hive);
                    using (var read = root.OpenSubKey(path.Path))
                    {
                        var current = read?.GetValue(path.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        before = Show(current, current == null ? null : read!.GetValueKind(path.ValueName));
                    }

                    if (dryRun)
                    {
                        result.Changes.Add(new KeepAwayChange(Location(path), path.ValueName, before, target, KeepAwayValueStatus.Planned));
                        continue;
                    }

                    bool restored;
                    if (!saved.Existed)
                    {
                        using (var key = root.OpenSubKey(path.Path, true))
                            key?.DeleteValue(path.ValueName, throwOnMissingValue: false);
                        using var check = root.OpenSubKey(path.Path);
                        restored = check?.GetValue(path.ValueName) == null;
                    }
                    else
                    {
                        object data = kind switch
                        {
                            RegistryValueKind.DWord => int.Parse(saved.Data ?? "0", CultureInfo.InvariantCulture),
                            RegistryValueKind.QWord => long.Parse(saved.Data ?? "0", CultureInfo.InvariantCulture),
                            _ => saved.Data ?? "",
                        };
                        using (var key = root.CreateSubKey(path.Path, true))
                            key.SetValue(path.ValueName, data, kind);
                        using var check = root.OpenSubKey(path.Path);
                        var now = check?.GetValue(path.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        restored = now != null &&
                                   Convert.ToString(now, CultureInfo.InvariantCulture) == Convert.ToString(data, CultureInfo.InvariantCulture);
                    }

                    if (restored)
                    {
                        originals.Remove(id);
                        if (!SaveOriginals(originals))
                            Debug.WriteLine("[StoreAppKeepAway] Originals not persisted (admin-only store unavailable).");
                    }
                    result.Changes.Add(new KeepAwayChange(Location(path), path.ValueName, before, target,
                        restored ? KeepAwayValueStatus.Restored : KeepAwayValueStatus.Failed));
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                    result.Changes.Add(new KeepAwayChange(Location(path), path.ValueName, before, target, KeepAwayValueStatus.Failed));
                }
            }
        }
        return result;
    }
}

#endregion

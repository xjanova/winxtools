using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using NetX.Core.Optimization;
using NetX.Core.System.Tweaks;

namespace NetX.Core.System.Background;

/// <summary>What WinXTools thinks about something that starts with Windows.</summary>
public enum StartupVerdict
{
    /// <summary>Look at this now: a remote-control program under another name, or an unsigned program hiding in a temp folder.</summary>
    Alert,
    /// <summary>Not needed at start-up: switching it off costs nothing you would notice.</summary>
    TurnOff,
    /// <summary>Works either way; depends on whether you use it.</summary>
    YourChoice,
    /// <summary>Something else depends on it.</summary>
    Keep
}

public enum StartupGroup
{
    Security, Windows, Hardware, DeviceExtra, AntiCheat, GameLauncher, Updater, Telemetry, RemoteAccess, App, Leftover, Suspicious
}

/// <summary>What the game profile does with an item.</summary>
public enum GameRole
{
    /// <summary>Games (or the PC itself) need it: stays on.</summary>
    Needed,
    /// <summary>Device extras — lighting effects, a maker's dashboard: off only in the lightest setting.</summary>
    Extra,
    /// <summary>No use while playing: switched off.</summary>
    Off
}

/// <summary>One thing that starts by itself, with what it is and what to do about it.</summary>
public sealed class StartupItem
{
    public AutostartEntry? Entry { get; init; }
    public BrowserSetting? Setting { get; init; }

    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Publisher { get; init; }
    public string? ExecutablePath { get; init; }

    public LocText What { get; init; } = new("", "");
    public LocText Why { get; init; } = new("", "");
    public StartupVerdict Verdict { get; init; }
    public StartupGroup Group { get; init; }
    public GameRole Game { get; init; }

    /// <summary>WinXTools never switches it (security software, parts of Windows it doesn't know to be optional, a policy).</summary>
    public bool Locked { get; init; }

    public bool Enabled { get; init; }

    /// <summary>Running right now: how many processes and how much memory they hold.</summary>
    public int ProcessCount { get; init; }
    public long MemoryBytes { get; init; }

    /// <summary>Set when WinXTools switched it off (by hand or through a profile) and can put it back.</summary>
    public SmartCpuChange? Change { get; init; }
}

/// <summary>
/// Knows what starts with Windows and what each thing is for: programs in the Run
/// keys and Startup folders, Store apps' start-up tasks, services, scheduled tasks
/// and the browsers' "start with Windows" settings — including the ones Windows
/// sets up by itself. Each item gets a verdict with its reason, and a role in the
/// game profile (the special start-up that leaves only what playing needs).
///
/// Everything is switched the way Windows itself does it and recorded by
/// <see cref="SmartCpuService"/>, so every change — one by one or a whole
/// profile — can be undone exactly. Parts of Windows that are not known to be
/// optional, and security software, are never switched.
/// </summary>
public static class StartupAdvisor
{
    public const string GameProfile = "game";
    public const string RecommendedProfile = "recommended";

    private static LocText L(string en, string th) => new(en, th);

    #region What is known

    private sealed record Known(string Match, string? Name, LocText What, StartupVerdict Verdict, GameRole Game, StartupGroup Group, LocText? Why = null);

    private static readonly LocText WhyTelemetry = L("It only collects usage data for Microsoft. Windows works the same without it.",
        "มีไว้เก็บข้อมูลการใช้งานส่งให้ Microsoft เท่านั้น ปิดแล้ว Windows ทำงานเหมือนเดิม");
    private static readonly LocText WhyXbox = L("Xbox and Game Pass games sign in and save through it.",
        "เกม Xbox และ Game Pass ใช้ล็อกอินและเซฟผ่านตัวนี้");
    private static readonly LocText WhyLauncher = L("It opens when you start a game from it; it doesn't need to wait in the background from boot.",
        "มันเปิดเองเมื่อคุณเริ่มเกม ไม่จำเป็นต้องรอเบื้องหลังตั้งแต่เปิดเครื่อง");
    private static readonly LocText WhyAntiCheat = L("Games protected by it refuse to start without it (some must load when Windows boots).",
        "เกมที่ใช้ตัวนี้จะไม่ยอมเปิดถ้าไม่มีมัน (บางตัวต้องโหลดตั้งแต่ Windows บูต)");
    private static readonly LocText WhyChat = L("You get its messages only while it runs. Open it yourself when you want it.",
        "จะได้รับข้อความเฉพาะตอนที่มันเปิดอยู่ เปิดเองเมื่อต้องการได้");
    private static readonly LocText WhySync = L("Files sync only while it runs.", "ไฟล์จะซิงก์เฉพาะตอนที่มันทำงานอยู่");
    private static readonly LocText WhyDeviceExtra = L("Lighting effects and the maker's dashboard. The device itself works without it.",
        "ใช้กับเอฟเฟกต์ไฟและหน้าควบคุมของผู้ผลิต ตัวอุปกรณ์ทำงานได้โดยไม่มีมัน");
    private static readonly LocText WhyBrowser = L("A browser opens in a moment anyway; started at sign-in it only holds memory.",
        "เบราว์เซอร์เปิดได้ในพริบตาอยู่แล้ว การเปิดรอไว้ตั้งแต่ล็อกอินมีแต่กินแรม");
    private static readonly LocText WhyPrinter = L("Only needed while printing or scanning.", "ใช้เฉพาะตอนพิมพ์หรือสแกน");

    /// <summary>Windows' own services that are known to be optional (or known to be needed for games). Anything else of Windows is left alone.</summary>
    private static readonly Known[] KnownServices =
    [
        new("DiagTrack", null, L("Connected User Experiences and Telemetry", "บริการเก็บข้อมูลการใช้งาน (Telemetry)"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Telemetry, WhyTelemetry),
        new("dmwappushservice", null, L("Telemetry message routing", "ตัวส่งต่อข้อมูล Telemetry"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Telemetry, WhyTelemetry),
        // SysMain's verdict depends on the system disk; Describe() fills it in.
        new("SysMain", null, L("SysMain (preloads programs you use often)", "SysMain (โหลดโปรแกรมที่ใช้บ่อยรอไว้)"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.Windows),
        new("WSearch", null, L("Windows Search indexing", "ตัวทำดัชนีค้นหาของ Windows"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.Windows,
            L("Searching in Start and File Explorer gets slower without it; it reads the disk in the background.", "ปิดแล้วการค้นหาใน Start และ File Explorer จะช้าลง แต่มันอ่านดิสก์เบื้องหลังอยู่ตลอด")),
        new("Spooler", null, L("Print Spooler", "ตัวจัดคิวงานพิมพ์"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.Windows,
            L("Needed for printing only. Nothing prints while it is off.", "ใช้กับการพิมพ์เท่านั้น ระหว่างที่ปิดจะพิมพ์งานไม่ได้")),
        new("Fax", null, L("Fax", "แฟกซ์"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Windows, L("Almost nobody sends faxes from a PC.", "แทบไม่มีใครส่งแฟกซ์จากคอมแล้ว")),
        new("RetailDemo", null, L("Retail demo mode", "โหมดสาธิตหน้าร้าน"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Windows, L("For shop display PCs.", "สำหรับเครื่องโชว์ในร้าน")),
        new("MapsBroker", null, L("Downloaded maps manager", "ตัวจัดการแผนที่ออฟไลน์"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Windows, L("Only for the Maps app's offline maps.", "ใช้กับแผนที่ออฟไลน์ของแอป Maps เท่านั้น")),
        new("WMPNetworkSvc", null, L("Windows Media Player network sharing", "แชร์สื่อของ Windows Media Player"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Windows, L("Shares your music library with other devices.", "ใช้แชร์คลังเพลงให้อุปกรณ์อื่น")),
        new("PcaSvc", null, L("Program Compatibility Assistant", "ตัวช่วยความเข้ากันได้ของโปรแกรม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.Windows,
            L("Watches old programs for known problems. Current games don't need it.", "คอยดูโปรแกรมเก่าว่ามีปัญหาที่รู้จักไหม เกมปัจจุบันไม่ต้องใช้")),
        new("XblAuthManager", null, L("Xbox Live sign-in", "ล็อกอิน Xbox Live"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.Windows, WhyXbox),
        new("XblGameSave", null, L("Xbox Live game saves", "เซฟเกม Xbox Live"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.Windows, WhyXbox),
        new("XboxNetApiSvc", null, L("Xbox Live networking", "เครือข่าย Xbox Live"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.Windows, WhyXbox),
        new("XboxGipSvc", null, L("Xbox accessories", "อุปกรณ์เสริม Xbox"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.Windows, L("Xbox controllers and headsets use it.", "จอยและหูฟัง Xbox ใช้ตัวนี้")),
        new("GamingServices", null, L("Gaming Services", "Gaming Services"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.Windows, WhyXbox),
        new("GamingServicesNet", null, L("Gaming Services (network)", "Gaming Services (เครือข่าย)"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.Windows, WhyXbox),
        new("ClickToRunSvc", null, L("Microsoft Office Click-to-Run", "บริการของ Microsoft Office"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.App,
            L("Office programs need it to start and to update.", "โปรแกรม Office ต้องใช้ตัวนี้ในการเปิดและอัปเดต")),
        new("vgc", "Riot Vanguard", L("Riot's anti-cheat", "ระบบกันโกงของ Riot"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.AntiCheat, WhyAntiCheat),
        new("EasyAntiCheat", "Easy Anti-Cheat", L("Anti-cheat", "ระบบกันโกง"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.AntiCheat, WhyAntiCheat),
        new("EasyAntiCheat_EOS", "Easy Anti-Cheat (Epic)", L("Anti-cheat", "ระบบกันโกง"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.AntiCheat, WhyAntiCheat),
        new("BEService", "BattlEye", L("Anti-cheat", "ระบบกันโกง"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.AntiCheat, WhyAntiCheat),
        new("FACEIT", "FACEIT Anti-Cheat", L("Anti-cheat", "ระบบกันโกง"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.AntiCheat, WhyAntiCheat),
    ];

    /// <summary>Windows' own scheduled tasks that only collect usage data (matched inside the task path).</summary>
    private static readonly Known[] KnownTasks =
    [
        new(@"\Application Experience\Microsoft Compatibility Appraiser", null, L("Compatibility Appraiser", "ตัวประเมินความเข้ากันได้"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Telemetry,
            L("Scans your programs and reports to Microsoft; known for long bursts of disk and CPU use.", "สแกนโปรแกรมในเครื่องแล้วรายงานให้ Microsoft ขึ้นชื่อเรื่องกินดิสก์และ CPU เป็นช่วงยาว")),
        new(@"\Application Experience\ProgramDataUpdater", null, L("Program data collector", "ตัวเก็บข้อมูลโปรแกรม"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Telemetry, WhyTelemetry),
        new(@"\Customer Experience Improvement Program\", null, L("Customer Experience Improvement Program", "โครงการปรับปรุงประสบการณ์ลูกค้า"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Telemetry, WhyTelemetry),
        new(@"\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector", null, L("Disk diagnostic data collector", "ตัวเก็บข้อมูลวินิจฉัยดิสก์"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Telemetry, WhyTelemetry),
        new(@"\Feedback\Siuf\", null, L("Feedback prompts", "ตัวถามความคิดเห็น"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Telemetry, WhyTelemetry),
        new(@"\Office\OfficeTelemetry", null, L("Office telemetry", "Telemetry ของ Office"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Telemetry, WhyTelemetry),
        new(@"\XblGameSave\", null, L("Xbox Live game save sync", "ซิงก์เซฟเกม Xbox Live"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.Windows, WhyXbox),
    ];

    /// <summary>Store apps' start-up tasks, by the start of the package family name.</summary>
    private static readonly Known[] KnownStore =
    [
        new("Microsoft.Copilot", "Copilot", L("Microsoft's AI assistant", "ผู้ช่วย AI ของ Microsoft"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.App,
            L("It opens when you click it; nothing needs it at sign-in.", "กดเมื่อไรก็เปิด ไม่มีอะไรต้องใช้มันตอนล็อกอิน")),
        new("Microsoft.Windows.Ai.Copilot", "Copilot", L("Microsoft's AI assistant", "ผู้ช่วย AI ของ Microsoft"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.App),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 Copilot", L("The Microsoft 365 hub app", "แอปศูนย์รวม Microsoft 365"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.App,
            L("A shortcut page to Office; Word and Excel don't need it.", "เป็นแค่หน้ารวมทางลัดไป Office ตัว Word และ Excel ไม่ต้องใช้มัน")),
        new("Microsoft.549981C3F5F10", "Cortana", L("Voice assistant (retired by Microsoft)", "ผู้ช่วยเสียง (Microsoft เลิกพัฒนาแล้ว)"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.App),
        new("Clipchamp.Clipchamp", "Clipchamp", L("Video editor", "โปรแกรมตัดต่อวิดีโอ"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.App),
        new("Microsoft.Windows.DevHome", "Dev Home", L("Developer dashboard", "แดชบอร์ดสำหรับนักพัฒนา"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.App),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", L("Automation tool", "เครื่องมือทำงานอัตโนมัติ"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.App),
        new("MSTeams", "Microsoft Teams", L("Chat and meetings", "แชตและประชุม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhyChat),
        new("MicrosoftTeams", "Microsoft Teams", L("Chat and meetings", "แชตและประชุม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhyChat),
        new("Microsoft.SkypeApp", "Skype", L("Chat and calls", "แชตและโทร"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhyChat),
        new("5319275A.WhatsAppDesktop", "WhatsApp", L("Chat", "แชต"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhyChat),
        new("Microsoft.YourPhone", "Phone Link", L("Shows your phone's messages and calls on the PC", "แสดงข้อความและสายเรียกเข้าของมือถือบนคอม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App,
            L("Only useful if your phone is linked to this PC.", "มีประโยชน์เฉพาะเมื่อผูกมือถือกับเครื่องนี้ไว้")),
        new("MicrosoftWindows.CrossDevice", "Cross-Device Experience", L("Phone features inside Windows", "ฟีเจอร์เชื่อมมือถือใน Windows"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App,
            L("Only useful if your phone is linked to this PC.", "มีประโยชน์เฉพาะเมื่อผูกมือถือกับเครื่องนี้ไว้")),
        new("Microsoft.GamingApp", "Xbox app", L("Xbox / Game Pass store and library", "ร้านและคลังเกม Xbox / Game Pass"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.GameLauncher, WhyLauncher),
        new("Microsoft.XboxGamingOverlay", "Xbox Game Bar", L("In-game overlay (Win+G)", "แถบซ้อนในเกม (Win+G)"), StartupVerdict.YourChoice, GameRole.Needed, StartupGroup.Windows,
            L("Recording and the in-game widgets come from it.", "การอัดคลิปและวิดเจ็ตในเกมมาจากตัวนี้")),
        new("SpotifyAB.SpotifyMusic", "Spotify", L("Music", "เพลง"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App),
        new("Microsoft.Todos", "Microsoft To Do", L("To-do lists", "รายการสิ่งที่ต้องทำ"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App),
        new("Microsoft.OutlookForWindows", "Outlook", L("Mail", "อีเมล"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhyChat),
        new("Microsoft.WindowsTerminal", "Windows Terminal", L("Command line", "หน้าต่างคำสั่ง"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App),
        new("NVIDIACorp.NVIDIAControlPanel", "NVIDIA Control Panel", L("Graphics card settings", "ตั้งค่าการ์ดจอ"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.Hardware),
        new("AppUp.IntelGraphicsExperience", "Intel Graphics Command Center", L("Graphics settings", "ตั้งค่ากราฟิก"), StartupVerdict.YourChoice, GameRole.Extra, StartupGroup.DeviceExtra,
            L("The settings stay applied without it; it only needs to run while you change them.", "ค่าที่ตั้งไว้ยังมีผลแม้ไม่เปิด ต้องเปิดเฉพาะตอนจะเปลี่ยนค่า")),
    ];

    /// <summary>Programs, by the name of the file they run (lower case, no ".exe"; a trailing * matches the start).</summary>
    private static readonly Known[] KnownPrograms =
    [
        // Game launchers
        new("steam", "Steam", L("Game launcher", "ตัวเปิดเกม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.GameLauncher, WhyLauncher),
        new("epicgameslauncher", "Epic Games Launcher", L("Game launcher", "ตัวเปิดเกม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.GameLauncher, WhyLauncher),
        new("battle.net", "Battle.net", L("Game launcher", "ตัวเปิดเกม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.GameLauncher, WhyLauncher),
        new("galaxyclient", "GOG Galaxy", L("Game launcher", "ตัวเปิดเกม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.GameLauncher, WhyLauncher),
        new("upc", "Ubisoft Connect", L("Game launcher", "ตัวเปิดเกม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.GameLauncher, WhyLauncher),
        new("ubisoftconnect", "Ubisoft Connect", L("Game launcher", "ตัวเปิดเกม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.GameLauncher, WhyLauncher),
        new("eadesktop", "EA app", L("Game launcher", "ตัวเปิดเกม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.GameLauncher, WhyLauncher),
        new("origin", "Origin", L("Game launcher", "ตัวเปิดเกม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.GameLauncher, WhyLauncher),
        new("riotclientservices", "Riot Client", L("Game launcher", "ตัวเปิดเกม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.GameLauncher, WhyLauncher),
        // Anti-cheat
        new("vgtray", "Riot Vanguard", L("Riot's anti-cheat (tray icon)", "ระบบกันโกงของ Riot (ไอคอนถาด)"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.AntiCheat, WhyAntiCheat),
        new("faceitclient", "FACEIT", L("Anti-cheat client", "ไคลเอนต์กันโกง"), StartupVerdict.Keep, GameRole.Needed, StartupGroup.AntiCheat, WhyAntiCheat),
        // Game tools
        new("msiafterburner", "MSI Afterburner", L("Graphics card tuning and in-game overlay", "ปรับแต่งการ์ดจอและแถบซ้อนในเกม"), StartupVerdict.YourChoice, GameRole.Needed, StartupGroup.Hardware,
            L("Your fan curve and overclock apply only while it runs.", "ค่าพัดลมและโอเวอร์คล็อกที่ตั้งไว้มีผลเฉพาะตอนที่มันทำงาน")),
        new("rtss", "RivaTuner Statistics Server", L("Frame counter and frame limiter", "ตัวนับและจำกัดเฟรม"), StartupVerdict.YourChoice, GameRole.Needed, StartupGroup.Hardware),
        // Chat
        new("discord", "Discord", L("Voice and chat", "แชตและคุยเสียง"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhyChat),
        new("slack", "Slack", L("Chat", "แชต"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhyChat),
        new("telegram", "Telegram", L("Chat", "แชต"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhyChat),
        new("line", "LINE", L("Chat", "แชต"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhyChat),
        new("zoom", "Zoom", L("Meetings", "ประชุม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhyChat),
        new("ms-teams", "Microsoft Teams", L("Chat and meetings", "แชตและประชุม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhyChat),
        new("teams", "Microsoft Teams", L("Chat and meetings", "แชตและประชุม"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhyChat),
        // Sync
        new("onedrive", "OneDrive", L("Cloud file sync", "ซิงก์ไฟล์ขึ้นคลาวด์"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhySync),
        new("dropbox", "Dropbox", L("Cloud file sync", "ซิงก์ไฟล์ขึ้นคลาวด์"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhySync),
        new("googledrivefs", "Google Drive", L("Cloud file sync", "ซิงก์ไฟล์ขึ้นคลาวด์"), StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, WhySync),
        // Browsers started at sign-in
        new("msedge", "Microsoft Edge", L("Edge starting by itself at sign-in", "Edge เปิดตัวเองตอนล็อกอิน"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.App, WhyBrowser),
        new("chrome", "Google Chrome", L("Chrome starting by itself at sign-in", "Chrome เปิดตัวเองตอนล็อกอิน"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.App, WhyBrowser),
        new("firefox", "Firefox", L("Firefox starting by itself at sign-in", "Firefox เปิดตัวเองตอนล็อกอิน"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.App, WhyBrowser),
        new("brave", "Brave", L("Brave starting by itself at sign-in", "Brave เปิดตัวเองตอนล็อกอิน"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.App, WhyBrowser),
        new("opera", "Opera", L("Opera starting by itself at sign-in", "Opera เปิดตัวเองตอนล็อกอิน"), StartupVerdict.TurnOff, GameRole.Off, StartupGroup.App, WhyBrowser),
        // Device extras: lights and dashboards
        new("lightingservice", "ASUS Aura lighting", L("RGB lighting", "ไฟ RGB"), StartupVerdict.YourChoice, GameRole.Extra, StartupGroup.DeviceExtra, WhyDeviceExtra),
        new("armourycrate*", "Armoury Crate", L("ASUS dashboard", "หน้าควบคุมของ ASUS"), StartupVerdict.YourChoice, GameRole.Extra, StartupGroup.DeviceExtra, WhyDeviceExtra),
        new("roglive*", "ROG Live Service", L("ASUS dashboard", "หน้าควบคุมของ ASUS"), StartupVerdict.YourChoice, GameRole.Extra, StartupGroup.DeviceExtra, WhyDeviceExtra),
        new("gamesdk", "ASUS GameSDK", L("ASUS dashboard helper", "ตัวช่วยหน้าควบคุมของ ASUS"), StartupVerdict.YourChoice, GameRole.Extra, StartupGroup.DeviceExtra, WhyDeviceExtra),
        new("rzsdk*", "Razer Chroma", L("RGB lighting", "ไฟ RGB"), StartupVerdict.YourChoice, GameRole.Extra, StartupGroup.DeviceExtra, WhyDeviceExtra),
        new("rzdiagnosticservice", "Razer Chroma diagnostics", L("RGB lighting helper", "ตัวช่วยไฟ RGB"), StartupVerdict.YourChoice, GameRole.Extra, StartupGroup.DeviceExtra, WhyDeviceExtra),
        new("razerappengine", "Razer Synapse", L("Razer dashboard", "หน้าควบคุมของ Razer"), StartupVerdict.YourChoice, GameRole.Extra, StartupGroup.DeviceExtra,
            L("Lighting, macros and cloud profiles. Profiles saved on the device keep working.", "ใช้กับไฟ มาโคร และโปรไฟล์บนคลาวด์ โปรไฟล์ที่บันทึกในตัวอุปกรณ์ยังใช้ได้")),
        new("icue*", "Corsair iCUE", L("Corsair dashboard", "หน้าควบคุมของ Corsair"), StartupVerdict.YourChoice, GameRole.Extra, StartupGroup.DeviceExtra, WhyDeviceExtra),
        new("lghub*", "Logitech G HUB", L("Logitech dashboard", "หน้าควบคุมของ Logitech"), StartupVerdict.YourChoice, GameRole.Extra, StartupGroup.DeviceExtra,
            L("Lighting, macros and per-game profiles. Profiles saved on the device keep working.", "ใช้กับไฟ มาโคร และโปรไฟล์รายเกม โปรไฟล์ที่บันทึกในตัวอุปกรณ์ยังใช้ได้")),
        new("signalrgb", "SignalRGB", L("RGB lighting", "ไฟ RGB"), StartupVerdict.YourChoice, GameRole.Extra, StartupGroup.DeviceExtra, WhyDeviceExtra),
        new("openrgb", "OpenRGB", L("RGB lighting", "ไฟ RGB"), StartupVerdict.YourChoice, GameRole.Extra, StartupGroup.DeviceExtra, WhyDeviceExtra),
    ];

    /// <summary>Remote-control programs: the file name they run and the words an honest installation carries in its name.</summary>
    private static readonly (string File, string Product, string[] HonestWords)[] RemoteTools =
    [
        ("screenconnect.", "ScreenConnect (ConnectWise)", ["screenconnect", "connectwise"]),
        ("anydesk", "AnyDesk", ["anydesk"]),
        ("teamviewer", "TeamViewer", ["teamviewer"]),
        ("rustdesk", "RustDesk", ["rustdesk"]),
        ("ateraagent", "Atera", ["atera"]),
        ("srservice", "Splashtop", ["splashtop"]),
        ("sragent", "Splashtop", ["splashtop"]),
        ("ninjarmmagent", "NinjaOne", ["ninja"]),
        ("logmein", "LogMeIn", ["logmein"]),
        ("tvnserver", "TightVNC", ["vnc"]),
        ("winvnc", "UltraVNC", ["vnc"]),
        ("vncserver", "VNC", ["vnc"]),
        ("supremo", "Supremo", ["supremo"]),
        ("remotepc", "RemotePC", ["remotepc"]),
        ("meshagent", "MeshCentral", ["mesh"]),
        ("tacticalrmm", "Tactical RMM", ["tactical"]),
        ("dwagsvc", "DWService", ["dwagent", "dwservice"]),
        ("client32", "NetSupport", ["netsupport"]),
        ("rutserv", "Remote Utilities", ["remote utilities"]),
        ("aeroadmin", "AeroAdmin", ["aeroadmin"]),
        ("zaservice", "Zoho Assist", ["zoho"]),
        ("remoting_host", "Chrome Remote Desktop", ["chrome remote", "chromoting"]),
    ];

    #endregion

    #region Scan

    /// <summary>Everything that starts with Windows, with a verdict each. Reads only; a second or two on a PC with many programs.</summary>
    public static List<StartupItem> Scan()
    {
        bool elevated = Autostart.IsElevated();
        var entries = Autostart.Enumerate(includeServices: true, includeTasks: elevated, includeStoreTasks: true);
        var changes = SmartCpuService.Instance.Changes.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First());
        var running = RunningPrograms();
        bool? ssd = WindowsVersionInfo.Current.SystemDriveIsSsd;

        var items = new List<StartupItem>();
        foreach (var entry in entries)
        {
            try
            {
                changes.TryGetValue(entry.Key, out var change);
                var item = Describe(entry, change, running, ssd);
                if (item != null) items.Add(item);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StartupAdvisor] {entry.Key}: {ex.Message}");
            }
        }
        items.AddRange(BrowserItems(changes));

        return items
            .GroupBy(i => i.Key).Select(g => g.First())
            .OrderBy(i => i.Verdict).ThenByDescending(i => i.Enabled).ThenByDescending(i => i.MemoryBytes)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static StartupItem? Describe(AutostartEntry entry, SmartCpuChange? change, Dictionary<string, (int Count, long Bytes)> running, bool? ssd)
    {
        if (entry.Kind == AutostartKind.StoreTask) return DescribeStore(entry, change);

        var info = entry.ExecutablePath != null ? ProgramCatalog.Describe(entry.ExecutablePath) : null;
        var cls = info?.Class ?? ProgramClass.App;
        string file = entry.ExecutablePath != null ? Path.GetFileNameWithoutExtension(entry.ExecutablePath).ToLowerInvariant() : "";

        // A file inside the Windows folder that somebody else made (a printer's status monitor,
        // a sound card's helper) is a device helper, not a part of Windows.
        if (cls == ProgramClass.Windows && info?.Publisher is { Length: > 0 } maker && !maker.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            cls = ProgramClass.Hardware;

        Known? known = entry.Kind switch
        {
            AutostartKind.Service => KnownServices.FirstOrDefault(k => k.Match.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)),
            AutostartKind.ScheduledTask => KnownTasks.FirstOrDefault(k => entry.Name.Contains(k.Match, StringComparison.OrdinalIgnoreCase)),
            _ => null
        };
        bool namedByTable = known != null;
        // Windows' own programs are matched by service or task only: "msedge" in the table is the Run entry, not a part of Windows.
        if (known == null && cls != ProgramClass.Windows && file.Length > 0)
            known = KnownPrograms.FirstOrDefault(k => k.Match.EndsWith('*') ? file.StartsWith(k.Match[..^1], StringComparison.Ordinal) : file == k.Match);

        // What is shown at all. A part of Windows that isn't known to be optional is not offered.
        bool ours = change != null;
        if (!ours)
        {
            switch (entry.Kind)
            {
                case AutostartKind.Service:
                    if (entry.StartMode != ServiceStartMode.Automatic) return null;
                    if (known == null && cls == ProgramClass.Windows) return null;
                    break;
                case AutostartKind.ScheduledTask:
                    bool atStart = entry.Triggers.Contains("logon") || entry.Triggers.Contains("boot");
                    if (known == null && (cls == ProgramClass.Windows || !(atStart || cls == ProgramClass.Updater))) return null;
                    break;
            }
        }

        // A service keeps its own name (two services of one program must not look alike) unless the
        // service table names it; everything else takes the known program's name.
        string name = entry.Kind switch
        {
            AutostartKind.Service => (namedByTable ? known!.Name : null) ?? entry.DisplayName ?? known?.Name ?? entry.Name,
            AutostartKind.ScheduledTask => known?.Name ?? info?.DisplayName ?? entry.Name[(entry.Name.LastIndexOf('\\') + 1)..],
            _ => known?.Name ?? info?.DisplayName ?? Path.GetFileNameWithoutExtension(entry.Name)
        };

        LocText what, why;
        StartupVerdict verdict;
        GameRole game;
        StartupGroup group;
        bool locked = false;

        var remote = RemoteTools.FirstOrDefault(t => file.StartsWith(t.File, StringComparison.Ordinal) || file == t.File.TrimEnd('.'));
        if (remote.File != null)
        {
            // A remote-control program is honest when its entry says what it is.
            string label = $"{entry.Name} {entry.DisplayName}".ToLowerInvariant();
            bool honest = remote.HonestWords.Any(w => label.Contains(w, StringComparison.Ordinal));
            group = StartupGroup.RemoteAccess;
            game = GameRole.Off;
            what = L($"Remote control: {remote.Product}", $"โปรแกรมควบคุมเครื่องจากระยะไกล: {remote.Product}");
            if (honest)
            {
                verdict = StartupVerdict.YourChoice;
                why = L("It lets someone operate this PC from elsewhere. Keep it on only if you use it.",
                    "ทำให้คนอื่นควบคุมเครื่องนี้จากที่อื่นได้ ควรเปิดไว้เฉพาะเมื่อคุณใช้งานมันจริง");
            }
            else
            {
                verdict = StartupVerdict.Alert;
                name = $"{name}  →  {remote.Product}";
                why = L($"A remote-control program installed under another name (\"{entry.DisplayName ?? entry.Name}\"). That is how hidden access to a PC is usually set up. If you did not install it yourself, switch it off and have this PC checked.",
                    $"เป็นโปรแกรมควบคุมเครื่องระยะไกลที่ติดตั้งโดยใช้ชื่ออื่น (\"{entry.DisplayName ?? entry.Name}\") ซึ่งเป็นวิธีที่มักใช้แอบฝังช่องทางเข้าเครื่อง ถ้าคุณไม่ได้ติดตั้งเอง ให้ปิดแล้วตรวจเครื่องนี้");
            }
        }
        else if (known != null)
        {
            what = known.What;
            verdict = known.Verdict;
            game = known.Game;
            group = known.Group;
            why = known.Why ?? DefaultWhy(verdict, group);
        }
        else if (entry.ExecutablePath == null)
        {
            group = StartupGroup.Leftover;
            verdict = StartupVerdict.TurnOff;
            game = GameRole.Off;
            what = L("A start-up entry left behind", "รายการเปิดพร้อมเครื่องที่ตกค้าง");
            why = L("The program it starts is no longer on this PC.", "โปรแกรมที่มันเรียกไม่มีอยู่ในเครื่องแล้ว");
        }
        else
        {
            (what, why, verdict, game, group, locked) = ByClass(cls, info, name);
        }

        // SysMain is the one whose answer depends on the disk.
        if (entry.Kind == AutostartKind.Service && entry.Name.Equals("SysMain", StringComparison.OrdinalIgnoreCase))
        {
            group = StartupGroup.Windows;
            what = L("SysMain (preloads programs you use often)", "SysMain (โหลดโปรแกรมที่ใช้บ่อยรอไว้)");
            locked = false;
            if (ssd == false)
            {
                verdict = StartupVerdict.Keep;
                game = GameRole.Needed;
                why = L("This PC starts from a hard disk: programs and games load faster with it.", "เครื่องนี้บูตจากฮาร์ดดิสก์ โปรแกรมและเกมจะโหลดเร็วขึ้นเมื่อเปิดไว้");
            }
            else
            {
                verdict = StartupVerdict.YourChoice;
                game = GameRole.Off;
                why = L("On an SSD it changes little; it reads the disk in the background to fill memory.", "บน SSD แทบไม่ต่าง แต่มันอ่านดิสก์เบื้องหลังเพื่อเติมแรม");
            }
        }

        (int Count, long Bytes) use = default;
        if (entry.Kind == AutostartKind.Service)
        {
            // A service's memory is its own process only; one that shares svchost.exe with others can't be told apart.
            if (entry.ServiceProcessId > 4 && file != "svchost")
            {
                try
                {
                    using var process = Process.GetProcessById(entry.ServiceProcessId);
                    use = (1, process.WorkingSet64);
                }
                catch
                {
                }
            }
        }
        else
        {
            running.TryGetValue(entry.ExecutablePath ?? "", out use);
        }
        return new StartupItem
        {
            Entry = entry,
            Key = entry.Key,
            Name = name,
            Publisher = info?.Publisher,
            ExecutablePath = entry.ExecutablePath,
            What = what,
            Why = why,
            Verdict = verdict,
            Group = group,
            Game = locked ? GameRole.Needed : game,
            Locked = locked,
            Enabled = entry.Enabled,
            ProcessCount = use.Count,
            MemoryBytes = use.Bytes,
            Change = change
        };
    }

    private static (LocText What, LocText Why, StartupVerdict Verdict, GameRole Game, StartupGroup Group, bool Locked) ByClass(ProgramClass cls, ProgramInfo? info, string name)
    {
        string maker = info?.Publisher ?? "";
        switch (cls)
        {
            case ProgramClass.Security:
                return (L("Security software", "โปรแกรมรักษาความปลอดภัย"),
                    L("Protects this PC. WinXTools never switches it off.", "คอยปกป้องเครื่องนี้ WinXTools จะไม่ปิดมัน"),
                    StartupVerdict.Keep, GameRole.Needed, StartupGroup.Security, true);
            case ProgramClass.Windows:
                return (L("Part of Windows", "ส่วนหนึ่งของ Windows"),
                    L("Windows needs it, or it isn't known to be safe to switch off.", "Windows ต้องใช้ หรือยังไม่มีข้อมูลว่าปิดได้อย่างปลอดภัย"),
                    StartupVerdict.Keep, GameRole.Needed, StartupGroup.Windows, true);
            case ProgramClass.Updater:
                return (L("Background updater", "ตัวอัปเดตเบื้องหลัง"),
                    L("The program checks for updates when you open it; its updater doesn't have to run all the time.", "โปรแกรมจะตรวจอัปเดตเองตอนเปิดใช้ ตัวอัปเดตไม่ต้องรันตลอด"),
                    StartupVerdict.TurnOff, GameRole.Off, StartupGroup.Updater, false);
            case ProgramClass.Hardware:
                if (IsPrinterMaker(maker, name))
                    return (L("Printer / scanner helper", "ตัวช่วยเครื่องพิมพ์ / สแกนเนอร์"), WhyPrinter, StartupVerdict.YourChoice, GameRole.Off, StartupGroup.Hardware, false);
                return (L("Device helper (graphics, sound, mouse, keyboard…)", "ตัวช่วยอุปกรณ์ (การ์ดจอ เสียง เมาส์ คีย์บอร์ด…)"),
                    L("A device's features may depend on it.", "ฟีเจอร์ของอุปกรณ์อาจต้องใช้ตัวนี้"),
                    StartupVerdict.Keep, GameRole.Needed, StartupGroup.Hardware, false);
            case ProgramClass.Suspicious:
                return (L("Unsigned program in a temporary or download folder", "โปรแกรมไม่มีลายเซ็น อยู่ในโฟลเดอร์ชั่วคราวหรือดาวน์โหลด"),
                    L("Nobody vouches for who made it, and it runs from a place where malware likes to hide. Check what it is.", "ไม่มีใครรับรองว่าใครทำ และมันรันจากที่ที่มัลแวร์ชอบซ่อน ควรตรวจว่ามันคืออะไร"),
                    StartupVerdict.Alert, GameRole.Off, StartupGroup.Suspicious, false);
            default:
                if (IsPrinterMaker(maker, name))
                    return (L("Printer / scanner helper", "ตัวช่วยเครื่องพิมพ์ / สแกนเนอร์"), WhyPrinter, StartupVerdict.YourChoice, GameRole.Off, StartupGroup.Hardware, false);
                return (L("Program", "โปรแกรม"),
                    L("It starts by itself; you can open it yourself when you need it.", "มันเปิดเอง ทั้งที่คุณเปิดเองได้เมื่อต้องการใช้"),
                    StartupVerdict.YourChoice, GameRole.Off, StartupGroup.App, false);
        }
    }

    private static bool IsPrinterMaker(string maker, string name)
    {
        string text = $"{maker} {name}";
        if (new[] { "EPSON", "Canon", "Brother Industries", "Lexmark", "Kyocera" }.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return true;
        // HP makes PCs too: only its printing and scanning helpers.
        bool hp = maker.Contains("HP Inc", StringComparison.OrdinalIgnoreCase) || maker.Contains("Hewlett", StringComparison.OrdinalIgnoreCase) ||
                  name.StartsWith("HP", StringComparison.OrdinalIgnoreCase);
        return hp && new[] { "print", "scan", "smart tank", "deskjet", "officejet", "laserjet", "envy" }.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static LocText DefaultWhy(StartupVerdict verdict, StartupGroup group) => verdict switch
    {
        StartupVerdict.TurnOff => L("Nothing you use depends on it starting with Windows.", "ไม่มีอะไรที่คุณใช้ต้องพึ่งการเปิดพร้อม Windows ของมัน"),
        StartupVerdict.Keep when group == StartupGroup.Hardware => L("A device's features may depend on it.", "ฟีเจอร์ของอุปกรณ์อาจต้องใช้ตัวนี้"),
        StartupVerdict.Keep => L("Something you use depends on it.", "มีสิ่งที่คุณใช้ต้องพึ่งมัน"),
        _ => L("Works either way; it depends on whether you use it.", "เปิดหรือปิดก็ได้ ขึ้นกับว่าคุณใช้มันหรือไม่")
    };

    private static StartupItem DescribeStore(AutostartEntry entry, SmartCpuChange? change)
    {
        string family = entry.Name.Split('\\')[0];
        var known = KnownStore.FirstOrDefault(k => family.StartsWith(k.Match, StringComparison.OrdinalIgnoreCase));
        bool policy = entry.StoreState is 3 or 4;
        var verdict = known?.Verdict ?? StartupVerdict.YourChoice;
        var group = known?.Group ?? StartupGroup.App;
        return new StartupItem
        {
            Entry = entry,
            Key = entry.Key,
            Name = known?.Name ?? PrettyFamily(family),
            Publisher = PublisherOf(family),
            What = known?.What ?? L("Microsoft Store app", "แอปจาก Microsoft Store"),
            Why = policy
                ? L("A policy on this PC decides whether it starts.", "มีนโยบาย (policy) ของเครื่องนี้กำหนดไว้")
                : known?.Why ?? DefaultWhy(verdict, group),
            Verdict = verdict,
            Group = group,
            Game = policy ? GameRole.Needed : known?.Game ?? GameRole.Off,
            Locked = policy,
            Enabled = entry.Enabled,
            Change = change
        };
    }

    /// <summary>"Microsoft.YourPhone_8wekyb3d8bbwe" → "YourPhone"; "5319275A.WhatsAppDesktop_…" → "WhatsAppDesktop".</summary>
    private static string PrettyFamily(string family)
    {
        string name = family.Split('_')[0];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        var text = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) text.Append(' ');
            text.Append(name[i]);
        }
        return text.ToString();
    }

    private static string? PublisherOf(string family)
    {
        string name = family.Split('_')[0];
        int dot = name.IndexOf('.');
        string first = dot > 0 ? name[..dot] : name;
        return first.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || first == "MSTeams" ? "Microsoft" : null;
    }

    /// <summary>The browsers' own "start with Windows / keep running" settings, when the browser is installed.</summary>
    private static IEnumerable<StartupItem> BrowserItems(Dictionary<string, SmartCpuChange> changes)
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        bool edge = File.Exists(Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe")) || File.Exists(Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"));
        bool chrome = new[] { pf, pf86, local }.Any(root => File.Exists(Path.Combine(root, @"Google\Chrome\Application\chrome.exe")));

        var settings = new List<(BrowserSetting Setting, string Name, LocText What)>();
        if (edge)
        {
            settings.Add((BrowserSetting.EdgeStartupBoost, "Microsoft Edge — Startup boost",
                L("Edge starts hidden at sign-in so its window opens a little faster", "Edge แอบเปิดรอไว้ตอนล็อกอิน เพื่อให้หน้าต่างขึ้นเร็วขึ้นนิดหน่อย")));
            settings.Add((BrowserSetting.EdgeBackgroundMode, "Microsoft Edge — keeps running when closed",
                L("Edge stays in the background after its last window is closed", "Edge ทำงานเบื้องหลังต่อแม้ปิดหน้าต่างสุดท้ายแล้ว")));
        }
        if (chrome)
            settings.Add((BrowserSetting.ChromeBackgroundMode, "Google Chrome — keeps running when closed",
                L("Chrome stays in the background after its last window is closed", "Chrome ทำงานเบื้องหลังต่อแม้ปิดหน้าต่างสุดท้ายแล้ว")));

        foreach (var (setting, name, what) in settings)
        {
            int? value;
            try { value = BrowserPolicies.Get(setting); }
            catch { continue; }
            string key = "Policy|" + setting;
            changes.TryGetValue(key, out var change);
            yield return new StartupItem
            {
                Setting = setting,
                Key = key,
                Name = name,
                Publisher = setting == BrowserSetting.ChromeBackgroundMode ? "Google" : "Microsoft",
                What = what,
                Why = WhyBrowser,
                Verdict = StartupVerdict.TurnOff,
                Group = StartupGroup.App,
                Game = GameRole.Off,
                Enabled = value != 0, // no policy = the browser's own default, which is on
                Change = change
            };
        }
    }

    #endregion

    #region Profiles and switching

    /// <summary>The game profile is in force: WinXTools switched things off for it and can put them all back.</summary>
    public static bool GameProfileActive => SmartCpuService.Instance.ProfileChanges(GameProfile).Count > 0;

    /// <summary>What "Apply recommended" would switch off: only what costs nothing to lose.</summary>
    public static List<StartupItem> RecommendedPlan(IEnumerable<StartupItem> items) =>
        items.Where(i => i.Enabled && !i.Locked && i.Verdict == StartupVerdict.TurnOff).ToList();

    /// <summary>
    /// What the game profile would switch off: everything that is of no use while
    /// playing. <paramref name="lightest"/> also takes the device extras (lights,
    /// makers' dashboards).
    /// </summary>
    public static List<StartupItem> GamePlan(IEnumerable<StartupItem> items, bool lightest) =>
        items.Where(i => i.Enabled && !i.Locked && (i.Game == GameRole.Off || (lightest && i.Game == GameRole.Extra))).ToList();

    /// <summary>Switches a list of items off, recording each under <paramref name="profile"/>. Returns how many were done and what Windows refused.</summary>
    public static (int Done, List<string> Errors) Apply(IEnumerable<StartupItem> plan, string? profile)
    {
        int done = 0;
        var errors = new List<string>();
        foreach (var item in plan)
        {
            var error = SetEnabled(item, false, profile);
            if (error == null) done++;
            else errors.Add($"{item.Name}: {error}");
        }
        return (done, errors);
    }

    /// <summary>Leaves the game profile: everything it switched off is put back exactly as it was.</summary>
    public static List<string> LeaveGameProfile() => SmartCpuService.Instance.UndoProfile(GameProfile);

    /// <summary>Switches one item. Null on success, else what went wrong (English, from Windows where possible).</summary>
    public static string? SetEnabled(StartupItem item, bool on, string? profile = null)
    {
        if (item.Locked) return "WinXTools does not switch this one";
        var service = SmartCpuService.Instance;

        if (item.Setting is BrowserSetting setting)
        {
            if (!on) return service.SetBrowserSetting(setting, apply: true, profile);
            var own = service.Changes.FirstOrDefault(c => c.Key == item.Key);
            // Only WinXTools' own switch is taken back: a policy somebody else set stays theirs.
            return own != null ? service.Undo(own) : "This was set outside WinXTools (a policy on this PC), so it is left as it is";
        }

        if (item.Entry is not { } entry) return "Nothing to switch";
        if (!on) return service.TurnOff(entry, item.Name, profile);

        var change = service.Changes.FirstOrDefault(c => c.Key == entry.Key);
        if (change != null) return service.Undo(change);
        // Switched off by something else (Task Manager, the program itself): simply on again.
        return AutostartControl.SetEnabled(entry, enabled: true, entry.StartMode == ServiceStartMode.Disabled ? ServiceStartMode.Manual : ServiceStartMode.Automatic);
    }

    /// <summary>
    /// Closes the programs of the given items that are running right now, so the
    /// memory comes back without waiting for a restart: asks each window to close
    /// (a program asking about unsaved work is left to the user), and ends the
    /// windowless background ones that are still there after a few seconds. Services are not touched
    /// here (switching a service off already stops it). Returns how many processes ended.
    /// </summary>
    public static int CloseRunning(IEnumerable<StartupItem> items)
    {
        var paths = items.Where(i => i.Entry?.Kind is AutostartKind.RunKey or AutostartKind.StartupFolder && i.ExecutablePath != null)
            .Select(i => i.ExecutablePath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0) return 0;

        int own = Environment.ProcessId, session = Process.GetCurrentProcess().SessionId, ended = 0;
        var targets = new List<(Process Process, bool HasWindow)>();
        foreach (var process in Process.GetProcesses())
        {
            bool keep = false;
            try
            {
                if (process.Id != own && process.Id > 4 && process.SessionId == session && !ProcessKiller.IsProtectedProcess(process.ProcessName) &&
                    ImagePath(process.Id) is { } path && paths.Contains(path))
                {
                    bool hasWindow = process.MainWindowHandle != IntPtr.Zero;
                    targets.Add((process, hasWindow));
                    keep = true;
                    if (hasWindow) process.CloseMainWindow();
                }
            }
            catch
            {
            }
            finally
            {
                if (!keep) process.Dispose();
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(4);
        foreach (var (process, hasWindow) in targets)
        {
            try
            {
                int wait = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                if (process.WaitForExit(wait)) ended++;
                // A program with an open window was asked to close and may be asking about
                // unsaved work: that stays the user's to answer. Only background ones are ended.
                else if (!hasWindow)
                {
                    process.Kill();
                    ended++;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return ended;
    }

    #endregion

    #region Running programs

    /// <summary>Program file → how many processes run from it and the memory they hold.</summary>
    private static Dictionary<string, (int Count, long Bytes)> RunningPrograms()
    {
        var map = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id <= 4 || ImagePath(process.Id) is not { } path) continue;
                    map.TryGetValue(path, out var use);
                    map[path] = (use.Count + 1, use.Bytes + process.WorkingSet64);
                }
                catch
                {
                }
            }
        }
        return map;
    }

    private static string? ImagePath(int pid)
    {
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    #endregion
}

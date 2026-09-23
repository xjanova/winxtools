using Microsoft.Win32;

namespace NetX.Core.System.Tweaks;

/// <summary>
/// Every card on the Windows Tricks page. Each trick can detect its real state
/// on this PC, apply itself, and restore the true Windows default. Tricks that
/// can't exist on this build/edition/hardware are not added at all.
///
/// Split by category into TrickCatalog.*.cs.
/// </summary>
public static partial class TrickCatalog
{
    public const string Gaming = "gaming";
    public const string Explorer = "explorer";
    public const string Look = "look";
    public const string Privacy = "privacy";
    public const string Performance = "performance";
    public const string Power = "power";
    public const string Network = "network";
    public const string Update = "update";
    public const string Security = "security";
    public const string Storage = "storage";
    public const string Repair = "repair";
    public const string Tools = "tools";

    /// <summary>Tab order and names.</summary>
    public static readonly IReadOnlyList<(string Id, LocText Name)> Categories = new List<(string, LocText)>
    {
        (Gaming, new LocText("Gaming", "เกม")),
        (Explorer, new LocText("Explorer & Taskbar", "Explorer และแถบงาน")),
        (Look, new LocText("Look & Input", "หน้าตาและการใช้งาน")),
        (Privacy, new LocText("Privacy & Ads", "ความเป็นส่วนตัวและโฆษณา")),
        (Performance, new LocText("Performance", "ประสิทธิภาพ")),
        (Power, new LocText("Power & Startup", "พลังงานและการเปิดเครื่อง")),
        (Network, new LocText("Network", "เครือข่าย")),
        (Update, new LocText("Windows Update", "Windows Update")),
        (Security, new LocText("Security", "ความปลอดภัย")),
        (Storage, new LocText("Storage", "พื้นที่จัดเก็บ")),
        (Repair, new LocText("Repair & Reports", "ซ่อมแซมและรายงาน")),
        (Tools, new LocText("Windows Tools", "เครื่องมือ Windows")),
    };

    private const RegistryHive HKCU = RegistryHive.CurrentUser;
    private const RegistryHive HKLM = RegistryHive.LocalMachine;
    private const RegistryHive HKU = RegistryHive.Users;

    private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string SystemPolicies = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string WindowsUpdatePolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string MultimediaProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Medium = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(90);

    /// <summary>Builds the tricks that apply to this PC.</summary>
    public static List<TrickDefinition> Build(WindowsVersionInfo os)
    {
        var list = new List<TrickDefinition>();
        AddGaming(list, os);
        AddExplorer(list, os);
        AddLook(list, os);
        AddPrivacy(list, os);
        AddPerformance(list, os);
        AddPower(list, os);
        AddNetwork(list, os);
        AddUpdate(list, os);
        AddSecurity(list, os);
        AddStorage(list, os);
        AddRepair(list, os);
        AddTools(list, os);
        return list;
    }

    #region Builders

    private static LocText L(string en, string th) => new(en, th);

    private static RegSetting Dword(RegistryHive hive, string path, string name, int applied, int? restore, params object[] alsoDefault) =>
        new(hive, path, name, RegistryValueKind.DWord, applied, restore, alsoDefault);

    private static RegSetting Text(RegistryHive hive, string path, string name, string applied, string? restore, params object[] alsoDefault) =>
        new(hive, path, name, RegistryValueKind.String, applied, restore, alsoDefault);

    /// <summary>A trick made of registry values only: detect, apply, restore and verify in-process.</summary>
    private static TrickDefinition RegToggle(
        string id, string category, string icon, LocText name, LocText description,
        TrickRisk risk, RestartScope restart, RegSetting[] settings,
        LocText? tip = null, LocText? warning = null, Action? afterChange = null,
        Func<TrickStatus, TrickStatus>? decorate = null) => new()
    {
        Id = id,
        Category = category,
        Icon = icon,
        Name = name,
        Description = description,
        Tip = tip,
        Warning = warning,
        Risk = risk,
        Restart = restart,
        Technical = Reg.Describe(settings),
        Detect = () =>
        {
            var status = Reg.Detect(settings);
            return decorate != null ? decorate(status) : status;
        },
        Apply = _ => Task.Run(() => After(Reg.ApplyAll(settings, restart), afterChange)),
        Revert = _ => Task.Run(() => After(Reg.RestoreAll(settings, restart), afterChange))
    };

    private static TrickResult After(TrickResult result, Action? afterChange)
    {
        if (afterChange != null)
        {
            try { afterChange(); } catch { /* notification only */ }
        }
        return result;
    }

    /// <summary>Runs a System32 tool, streams its output and maps the exit code.</summary>
    private static async Task<TrickResult> RunTool(
        TrickRunContext ctx, string exe, string args, TimeSpan timeout, LocText okMessage,
        RestartScope restart = RestartScope.None, params int[] okExitCodes)
    {
        ctx.Output.AppendLine($"> {Path.GetFileNameWithoutExtension(exe)} {args}");
        var r = await CommandRunner.RunToolAsync(exe, args, timeout, ctx.Token, ctx.Output).ConfigureAwait(false);
        ctx.Output.AppendLine();
        if (r.Cancelled) return TrickResult.Canceled(r.Output);
        bool ok = !r.StartFailed && !r.TimedOut && (r.ExitCode == 0 || okExitCodes.Contains(r.ExitCode));
        return ok
            ? TrickResult.Ok(okMessage, restart, r.Output)
            : TrickResult.Fail(TweakErrors.FromCommand(r, Path.GetFileNameWithoutExtension(exe)), r.Output);
    }

    /// <summary>Runs a tool whose effect is checked by <paramref name="verify"/> afterwards (exit codes aren't trusted alone).</summary>
    private static async Task<TrickResult> RunAndVerify(
        TrickRunContext ctx, string exe, string args, TimeSpan timeout, Func<bool> verify,
        LocText okMessage, RestartScope restart = RestartScope.None)
    {
        ctx.Output.AppendLine($"> {Path.GetFileNameWithoutExtension(exe)} {args}");
        var r = await CommandRunner.RunToolAsync(exe, args, timeout, ctx.Token, ctx.Output).ConfigureAwait(false);
        ctx.Output.AppendLine();
        if (r.Cancelled) return TrickResult.Canceled(r.Output);

        bool verified;
        try { verified = verify(); } catch { verified = false; }
        if (verified) return TrickResult.Ok(okMessage, restart, r.Output);
        return TrickResult.Fail(r.Succeeded ? TweakErrors.NotVerified : TweakErrors.FromCommand(r, Path.GetFileNameWithoutExtension(exe)), r.Output);
    }

    private static TrickDefinition OpenTool(string id, string category, string icon, LocText name, LocText description,
        Func<TrickResult> open, TrickRisk risk = TrickRisk.Safe, string? technical = null, LocText? tip = null,
        string? settingsUri = null) => new()
    {
        Id = id,
        Category = category,
        Icon = icon,
        Name = name,
        Description = description,
        Tip = tip,
        Risk = risk,
        Technical = technical,
        Open = open,
        SettingsUri = settingsUri
    };

    private static string DesktopFile(string fileName) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), fileName);

    #endregion
}

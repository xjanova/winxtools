using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetX.App.Helpers;
using NetX.Core.Helpers;
using NetX.Core.Optimization;
using NetX.Core.System.Background;

namespace NetX.App.Views;

/// <summary>
/// Smart CPU: what runs in the background, what it costs, where it starts from
/// (Run key, Startup folder, task, service, another program), and the gentle
/// to firm ways of dealing with it — Efficiency mode, end now, turn off its
/// start-up where it is set up (undoable), uninstall — plus Chrome/Edge's own
/// background and memory settings. Windows itself and security software are
/// listed but never offered for change.
/// </summary>
public partial class SmartCpuView : Page
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AutostartEvery = TimeSpan.FromMinutes(2);

    private readonly DispatcherTimer _timer = new() { Interval = RefreshEvery };
    private BackgroundReport? _report;
    private List<AutostartEntry>? _autostart;
    private DateTime _autostartReadUtc = DateTime.MinValue;
    private bool _isActive, _scanning, _isAdmin, _applying;

    public SmartCpuView()
    {
        InitializeComponent();
        _timer.Tick += async (s, e) => await ScanAsync(rereadAutostart: false);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    #region Lifetime and scanning

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isActive = true;
        _isAdmin = AdminHelper.IsRunAsAdmin();
        ProcessActivity.Start();
        ApplySettings();
        await ScanAsync(rereadAutostart: true);
        if (_isActive) _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isActive = false;
        _timer.Stop();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ScanAsync(rereadAutostart: true);

    private async Task ScanAsync(bool rereadAutostart)
    {
        if (_scanning || !_isActive) return;
        _scanning = true;
        RefreshButton.IsEnabled = false;
        if (_report == null) BackgroundState.Text = Loc.T("Cpu_Scanning", "Looking at what runs on this PC and where it starts from…");
        try
        {
            bool reread = rereadAutostart || _autostart == null || DateTime.UtcNow - _autostartReadUtc > AutostartEvery;
            var known = reread ? null : _autostart;
            var report = await Task.Run(() => BackgroundScanner.Scan(includeAutostart: true, known));
            if (!_isActive) return;
            _report = report;
            if (reread)
            {
                _autostart = report.Autostart;
                _autostartReadUtc = DateTime.UtcNow;
            }
            Render();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmartCpuView] Scan failed: {ex}");
            if (_isActive) BackgroundState.Text = Loc.F("Cpu_ScanFailed", "Couldn't read the running programs: {0}", ErrorText.From(ex));
        }
        finally
        {
            _scanning = false;
            if (_isActive) RefreshButton.IsEnabled = true;
        }
    }

    private void ApplySettings()
    {
        _applying = true;
        try
        {
            AutoEfficiencyToggle.IsChecked = SmartCpuService.Instance.AutoEfficiency;
            AutoEfficiencyToggle.IsEnabled = _isAdmin;
            foreach (var toggle in BrowserToggles())
            {
                var setting = (BrowserSetting)Enum.Parse(typeof(BrowserSetting), (string)toggle.Tag);
                toggle.IsChecked = BrowserPolicies.Get(setting) == BrowserPolicies.Recommended(setting);
                toggle.IsEnabled = _isAdmin;
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private IEnumerable<CheckBox> BrowserToggles() =>
        new[] { ChromeBackgroundToggle, ChromeMemoryToggle, EdgeBackgroundToggle, EdgeBoostToggle, EdgeEfficiencyToggle };

    #endregion

    #region Rendering

    private void Render()
    {
        var report = _report;
        if (report == null) return;
        var changes = SmartCpuService.Instance.Changes;

        // Background programs (not Windows): heaviest first.
        var apps = report.Apps.Where(a => a.Class != ProgramClass.Windows).Take(40).Select(a => new BackgroundRow(a, report)).ToList();
        BackgroundList.ItemsSource = apps;
        double cpu = report.Apps.Sum(a => a.CpuLastMinute);
        long ram = report.Apps.Sum(a => a.RamMB);
        StatBackground.Text = report.Apps.Count.ToString("N0");
        StatBackgroundNote.Text = Loc.F("Cpu_StatBackgroundNote", "CPU {0:0.#}% · RAM {1}", cpu, RamOptimizerView.FormatMB(ram));
        BackgroundState.Text = report.WatchedSeconds < 50
            ? Loc.T("Cpu_Warming", "CPU figures settle after a minute — WinXTools measures what each program really used.")
            : Loc.T("Cpu_BackgroundReady", "Sorted by CPU over the last minute, then by RAM. Hover the buttons for what each one does.");

        // Starts by itself.
        var rows = AutostartRows(report, changes);
        AutostartList.ItemsSource = rows;
        int on = report.Autostart.Count(e => e.Enabled && Describe(e)?.Class is not ProgramClass.Windows);
        int recommended = rows.Count(r => r.IsRecommended && r.Entry.Enabled);
        StatAutostart.Text = on.ToString("N0");
        StatAutostartNote.Text = recommended > 0
            ? Loc.F("Cpu_StatAutostartNote", "{0} can be turned off safely", recommended)
            : Loc.T("Cpu_StatAutostartNone", "nothing needs turning off");
        AutostartState.Text = report.TasksRead
            ? Loc.F("Cpu_AutostartCount", "{0:N0} items start by themselves (Windows' own hidden unless ticked).", rows.Count)
            : Loc.T("Cpu_TasksNeedAdmin", "Run WinXTools as administrator to see scheduled tasks and to change anything.");

        // Browsers.
        if (report.Chrome is { } chrome)
        {
            StatChrome.Text = RamOptimizerView.FormatMB(chrome.RamMB);
            StatChromeNote.Text = chrome.HasWindow
                ? Loc.F("Cpu_ChromeOpen", "{0:N0} processes · CPU {1:0.#}%", chrome.Processes, chrome.CpuLastMinute)
                : Loc.F("Cpu_ChromeRunsHidden", "{0:N0} processes with no window open — it keeps running in the background", chrome.Processes);
            StatChromeNote.Foreground = (Brush)FindResource(chrome.HasWindow ? "TextTertiaryBrush" : "WarningBrush");
        }
        else
        {
            StatChrome.Text = "–";
            StatChromeNote.Text = Loc.T("Cpu_ChromeNotRunning", "Chrome isn't running");
            StatChromeNote.Foreground = (Brush)FindResource("TextTertiaryBrush");
        }
        BrowserState.Text = BrowserLine(report);

        // Changes made.
        StatChanges.Text = changes.Count.ToString("N0");
        StatChangesNote.Text = changes.Count == 0 ? Loc.T("Cpu_StatChangesNone", "nothing changed yet") : Loc.T("Cpu_StatChangesNote", "every one can be undone");
        ChangesList.ItemsSource = changes.OrderByDescending(c => c.ChangedUtcTicks).Select(c => new ChangeRow(c)).ToList();
        ChangesEmpty.Visibility = changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var log = SmartCpuService.Instance.EfficiencyLog;
        AutoLog.Text = string.Join("\n", log.Take(4).Select(l => Loc.F("Cpu_AutoLogLine", "{0:HH:mm} {1} ({2:0.#}% CPU) → Efficiency mode", l.When, l.App, l.Cpu)));
        AutoLog.Visibility = log.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BrowserLine(BackgroundReport report)
    {
        var parts = new List<string>();
        if (report.Chrome is { } c)
            parts.Add(Loc.F("Cpu_BrowserChrome", "Chrome: {0:N0} processes, {1}", c.Processes, RamOptimizerView.FormatMB(c.RamMB)));
        if (report.Edge is { } e)
            parts.Add(Loc.F("Cpu_BrowserEdge", "Edge: {0:N0} processes, {1}", e.Processes, RamOptimizerView.FormatMB(e.RamMB)));
        return parts.Count > 0 ? string.Join(" · ", parts) : Loc.T("Cpu_BrowsersNone", "Neither Chrome nor Edge is running right now.");
    }

    private static ProgramInfo? Describe(AutostartEntry entry) =>
        entry.ExecutablePath != null ? ProgramCatalog.Describe(entry.ExecutablePath) : null;

    private List<AutostartRow> AutostartRows(BackgroundReport report, IReadOnlyList<SmartCpuChange> changes)
    {
        bool showWindows = ShowWindowsItems.IsChecked == true;
        var byKey = changes.ToDictionary(c => c.Key, c => c);
        return report.Autostart
            .Select(e => new AutostartRow(e, Describe(e), byKey.GetValueOrDefault(e.Key), _isAdmin))
            // Services that start only when asked are part of the furniture: listed only with Windows items.
            .Where(r => showWindows || (r.Class != ProgramClass.Windows && !(r.Entry.Kind == AutostartKind.Service && !r.Entry.Enabled && r.Change == null)))
            .OrderByDescending(r => r.IsRecommended && r.Entry.Enabled)
            .ThenByDescending(r => r.Entry.Enabled)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void ShowWindows_Click(object sender, RoutedEventArgs e) => Render();

    /// <summary>A list inside the page's scroll viewer: let the wheel scroll the page.</summary>
    private void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var parent = (UIElement)((FrameworkElement)sender).Parent;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }

    #endregion

    #region Actions on running programs

    private void ShowAction(string text, bool problem)
    {
        ActionText.Text = text;
        ActionText.Foreground = (Brush)FindResource(problem ? "WarningBrush" : "SuccessBrush");
        ActionText.Visibility = Visibility.Visible;
    }

    private async void Efficiency_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BackgroundRow row } button || !row.CanAct) return;
        bool on = !row.App.InEfficiencyMode;
        button.IsEnabled = false;
        int done = await Task.Run(() => row.App.Processes.Count(p => Efficiency.Set(p, on)));
        if (!_isActive) return;
        ShowAction(done == 0
                ? Loc.F("Cpu_EfficiencyFailed", "Windows didn't allow changing {0} (it may need administrator rights).", row.Name)
                : on ? Loc.F("Cpu_EfficiencyDone", "{0} now runs in Efficiency mode: it keeps working, using less CPU and power.", row.Name)
                     : Loc.F("Cpu_EfficiencyOff", "{0} runs normally again.", row.Name),
            done == 0);
        await ScanAsync(rereadAutostart: false);
    }

    private async void End_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BackgroundRow row } button || !row.CanAct) return;
        var question = Loc.F("Cpu_ConfirmEnd",
            "End {0} now? ({1:N0} processes)\n\nIt has no window, but anything it was doing is stopped. If it starts by itself it will come back next time — use \"Turn off start-up\" for that.",
            row.Name, row.App.Processes.Count);
        if (row.App.Class == ProgramClass.Hardware)
            question += "\n\n" + Loc.T("Cpu_HardwareWarning", "⚠ This helps a device work (graphics, sound, mouse, keyboard lighting…). The device may lose some features until it starts again.");
        if (MessageBox.Show(question, Loc.T("Cpu_Title", "Smart CPU"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        button.IsEnabled = false;
        var results = await Task.Run(() => row.App.Processes.Select(p => ProcessKiller.Instance.EndProcess(p.Pid, p.Name, p.CreateTime)).ToList());
        if (!_isActive) return;
        int ended = results.Count(r => r is ProcessEndResult.Ended or ProcessEndResult.AlreadyGone);
        ShowAction(ended == results.Count
                ? Loc.F("Cpu_Ended", "{0} was ended.", row.Name)
                : Loc.F("Cpu_EndPartial", "{0}: {1:N0} of {2:N0} processes ended (Windows protects the rest, or it needs administrator rights).", row.Name, ended, results.Count),
            ended != results.Count);
        await ScanAsync(rereadAutostart: false);
    }

    private async void TurnOffOrigin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BackgroundRow row } || !row.CanTurnOff) return;
        var entries = row.App.Origins.Where(o => o.Enabled).ToList();
        await TurnOffAsync(entries, row.Name, row.App.Class);
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BackgroundRow row } || !row.CanUninstall) return;
        UninstallerView.PendingSearch = row.UninstallSearch;
        (Application.Current.MainWindow as MainWindow)?.NavigateToUninstaller();
    }

    #endregion

    #region Start-up entries

    private async void AutostartToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AutostartRow row } || !row.CanToggle) return;
        if (row.Entry.Enabled)
        {
            await TurnOffAsync(new List<AutostartEntry> { row.Entry }, row.Name, row.Class);
            return;
        }

        // Turning back on: WinXTools' own change is undone exactly; anything else is switched on as asked.
        string? error = row.Change != null
            ? await Task.Run(() => SmartCpuService.Instance.Undo(row.Change))
            : await Task.Run(() => AutostartControl.SetEnabled(row.Entry, enabled: true));
        if (!_isActive) return;
        ShowAction(error == null ? Loc.F("Cpu_TurnedOn", "{0} starts by itself again.", row.Name)
                                 : Loc.F("Cpu_ChangeFailed", "Couldn't change {0}: {1}", row.Name, error), error != null);
        await ScanAsync(rereadAutostart: true);
    }

    /// <summary>Turns start-up entries off where they are set up, after saying exactly what changes.</summary>
    private async Task TurnOffAsync(List<AutostartEntry> entries, string name, ProgramClass cls)
    {
        if (entries.Count == 0) return;
        if (cls is ProgramClass.Windows or ProgramClass.Security) return;

        var lines = entries.Select(e => "• " + AutostartRow.WhereText(e));
        var question = Loc.F("Cpu_ConfirmTurnOff",
            "Stop {0} from starting by itself?\n\n{1}\n\nIt is switched off where it is set up (services are set to start only when needed), the same way Windows does it — nothing is deleted, and you can undo it from \"Changes made\". If the program turns it back on after an update, WinXTools turns it off again.",
            name, string.Join("\n", lines));
        if (cls == ProgramClass.Hardware)
            question += "\n\n" + Loc.T("Cpu_HardwareWarning", "⚠ This helps a device work (graphics, sound, mouse, keyboard lighting…). The device may lose some features until it starts again.");
        if (entries.Any(e => AutostartRow.IsGameProtection(e.Name + " " + e.ExecutablePath)))
            question += "\n\n" + Loc.T("Cpu_AntiCheatWarning", "⚠ This is a game's anti-cheat: games that need it won't start while it is off.");
        if (MessageBox.Show(question, Loc.T("Cpu_Title", "Smart CPU"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var errors = await Task.Run(() => entries.Select(entry => SmartCpuService.Instance.TurnOff(entry, name)).Where(err => err != null).ToList());
        if (!_isActive) return;
        ShowAction(errors.Count == 0
                ? Loc.F("Cpu_TurnedOff", "{0} no longer starts by itself. Undo it any time under \"Changes made\".", name)
                : Loc.F("Cpu_ChangeFailed", "Couldn't change {0}: {1}", name, errors[0]),
            errors.Count > 0);
        await ScanAsync(rereadAutostart: true);
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChangeRow row } button) return;
        button.IsEnabled = false;
        var error = await Task.Run(() => SmartCpuService.Instance.Undo(row.Change));
        if (!_isActive) return;
        ShowAction(error == null ? Loc.F("Cpu_Undone", "{0} is back the way it was.", row.Name)
                                 : Loc.F("Cpu_ChangeFailed", "Couldn't change {0}: {1}", row.Name, error), error != null);
        ApplySettings();
        await ScanAsync(rereadAutostart: true);
    }

    #endregion

    #region Browsers and automatic mode

    private async void BrowserToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_applying || sender is not CheckBox toggle) return;
        var setting = (BrowserSetting)Enum.Parse(typeof(BrowserSetting), (string)toggle.Tag);
        bool apply = toggle.IsChecked == true;
        if (apply && SmartCpuService.Instance.Changes.All(c => c.Setting == null) &&
            MessageBox.Show(Loc.T("Cpu_ConfirmPolicy",
                    "WinXTools sets this through the browser's own administrator settings (the same ones companies use). The browser will then show \"Managed by your organization\" — that only means a setting is fixed; nothing is monitored. Switch it back here any time.\n\nContinue?"),
                Loc.T("Cpu_Title", "Smart CPU"), MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            _applying = true;
            toggle.IsChecked = false;
            _applying = false;
            return;
        }

        var error = await Task.Run(() => SmartCpuService.Instance.SetBrowserSetting(setting, apply));
        if (!_isActive) return;
        if (error != null) ShowAction(Loc.F("Cpu_ChangeFailed", "Couldn't change {0}: {1}", setting, error), true);
        else ShowAction(Loc.T("Cpu_PolicyDone", "Done — it applies the next time the browser starts."), false);
        ApplySettings();
        Render();
    }

    private void AutoEfficiency_Click(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        SmartCpuService.Instance.AutoEfficiency = AutoEfficiencyToggle.IsChecked == true;
    }

    #endregion
}

/// <summary>A background program in the list.</summary>
public sealed class BackgroundRow
{
    public BackgroundRow(BackgroundApp app, BackgroundReport report)
    {
        App = app;
        Name = app.DisplayName;
        Path = app.Path ?? app.Name;
        Icon = app.Path != null ? ProcessIconHelper.GetIconFromPath(app.Path) : ProcessIconHelper.GetProcessIcon(app.Name);
        ClassText = AutostartRow.ClassName(app.Class);
        ClassBrush = AutostartRow.ClassColor(app.Class);
        CpuText = app.CpuLastMinute >= 0.05 ? $"{app.CpuLastMinute:0.0}%" : "0%";
        CpuBrush = (Brush)(Application.Current.TryFindResource(app.CpuLastMinute >= 5 ? "DangerBrush" : app.CpuLastMinute >= 1 ? "WarningBrush" : "TextSecondaryBrush") ?? Brushes.Gray);
        RamText = RamOptimizerView.FormatMB(app.RamMB);

        var detail = new List<string>();
        if (!string.IsNullOrEmpty(app.Info?.Publisher)) detail.Add(app.Info!.Publisher!);
        detail.Add(Loc.F("Cpu_Processes", "{0:N0} processes", app.Processes.Count));
        if (app.HelperOfOpenApp) detail.Add(Loc.T("Cpu_HelperOfOpen", "helper of a program you have open"));
        else if (app.StartedBy != null) detail.Add(Loc.F("Cpu_StartedBy", "started by {0}", app.StartedBy));
        if (app.IsService) detail.Add(Loc.T("Cpu_IsService", "Windows service"));
        Detail = string.Join(" · ", detail);

        OriginText = app.Origins.Count > 0
            ? Loc.F("Cpu_StartsFrom", "Starts from: {0}", string.Join(" · ", app.Origins.Select(AutostartRow.WhereText)))
            : "";
        OriginVisibility = OriginText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool changeable = app.Class is not (ProgramClass.Windows or ProgramClass.Security);
        CanAct = changeable;
        CanTurnOff = changeable && app.Origins.Any(o => o.Enabled);
        CanUninstall = app.Class is ProgramClass.App or ProgramClass.Updater or ProgramClass.Suspicious && app.Path != null;
        UninstallSearch = app.Info?.Product ?? app.DisplayName;
        EfficiencyVisibility = app.InEfficiencyMode ? Visibility.Visible : Visibility.Collapsed;
        EfficiencyTip = app.InEfficiencyMode
            ? Loc.T("Cpu_EfficiencyOffTip", "Back to normal speed")
            : Loc.T("Cpu_EfficiencyTip", "Efficiency mode: keeps it running, but at the lowest priority and on power-saving cores (like Task Manager's leaf)");
        TurnOffTip = CanTurnOff
            ? Loc.T("Cpu_TurnOffTip", "Turn off its start-up where it is set up (undoable)")
            : Loc.T("Cpu_NoOriginTip", "It doesn't start by itself (or WinXTools can't see from where)");
    }

    public BackgroundApp App { get; }
    public string Name { get; }
    public string Path { get; }
    public ImageSource? Icon { get; }
    public string ClassText { get; }
    public Brush ClassBrush { get; }
    public string CpuText { get; }
    public Brush CpuBrush { get; }
    public string RamText { get; }
    public string Detail { get; }
    public string OriginText { get; }
    public Visibility OriginVisibility { get; }
    public Visibility EfficiencyVisibility { get; }
    public string EfficiencyTip { get; }
    public string TurnOffTip { get; }
    public bool CanAct { get; }
    public bool CanTurnOff { get; }
    public bool CanUninstall { get; }
    public string UninstallSearch { get; }
}

/// <summary>One thing that starts by itself.</summary>
public sealed class AutostartRow
{
    private static readonly string[] Launchers =
    {
        "steam", "epicgameslauncher", "eadesktop", "origin", "battle.net", "galaxyclient", "ubisoftconnect", "upc", "riotclientservices",
        "overwolf", "spotify", "zoom", "skype", "msedge", "chrome", "ccxprocess", "adobecollabsync", "creative cloud", "acrotray"
    };

    private static readonly string[] GameProtection = { "anticheat", "anti-cheat", "vanguard", "vgc", "vgtray", "battleye", "easyanticheat", "ace-service", "faceit", "denuvo" };

    public AutostartRow(AutostartEntry entry, ProgramInfo? info, SmartCpuChange? change, bool admin)
    {
        Entry = entry;
        Info = info;
        Change = change;
        Class = info?.Class ?? ProgramClass.App;
        Name = entry.Kind == AutostartKind.Service && !string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.DisplayName!
             : info?.Description ?? entry.Name;
        Icon = entry.ExecutablePath != null ? ProcessIconHelper.GetIconFromPath(entry.ExecutablePath) : ProcessIconHelper.GetDefaultIcon();
        ClassText = ClassName(Class);
        ClassBrush = ClassColor(Class);
        StatusText = entry.Enabled ? Loc.T("Cpu_StatusOn", "starts by itself") : Loc.T("Cpu_StatusOff", "off");
        StatusBrush = (Brush)(Application.Current.TryFindResource(entry.Enabled ? "WarningBrush" : "SuccessBrush") ?? Brushes.Gray);
        Command = entry.Command;
        Where = WhereText(entry);

        var detail = new List<string>();
        if (!string.IsNullOrEmpty(info?.Publisher)) detail.Add(info!.Publisher!);
        if (entry.ExecutablePath != null) detail.Add(entry.ExecutablePath);
        if (entry.Triggers.Count > 0) detail.Add(Loc.F("Cpu_Triggers", "runs at: {0}", string.Join(", ", entry.Triggers.Select(TriggerName))));
        Detail = string.Join(" · ", detail);

        bool antiCheat = IsGameProtection(entry.Name + " " + entry.ExecutablePath);
        string exe = System.IO.Path.GetFileNameWithoutExtension(entry.ExecutablePath ?? "").ToLowerInvariant();
        IsRecommended = !antiCheat && entry.Enabled &&
                        (Class == ProgramClass.Updater || Class == ProgramClass.Suspicious ||
                         (Class == ProgramClass.App && entry.Kind is AutostartKind.RunKey or AutostartKind.StartupFolder && Launchers.Contains(exe)));
        RecommendText = Class == ProgramClass.Suspicious ? Loc.T("Cpu_RecommendCheck", "check with Microsoft Defender")
                      : Class == ProgramClass.Updater ? Loc.T("Cpu_RecommendUpdater", "not needed at start-up")
                      : Loc.T("Cpu_RecommendLauncher", "can start when you open it");
        RecommendVisibility = IsRecommended ? Visibility.Visible : Visibility.Collapsed;

        CanToggle = admin || (!entry.MachineWide && entry.Kind is AutostartKind.RunKey or AutostartKind.StartupFolder);
        if (Class is ProgramClass.Windows or ProgramClass.Security) CanToggle = false;
        ToggleText = entry.Enabled ? Loc.T("Cpu_TurnOff", "Turn off") : change != null ? Loc.T("Cpu_Undo", "Undo") : Loc.T("Cpu_TurnOn", "Turn on");
        ToggleTip = Class is ProgramClass.Windows ? Loc.T("Cpu_WindowsTip", "Part of Windows — not changed here")
                  : Class is ProgramClass.Security ? Loc.T("Cpu_SecurityTip", "Protects this PC — not changed here")
                  : !CanToggle ? Loc.T("Cpu_NeedsAdminTip", "Needs WinXTools to run as administrator")
                  : Where;
    }

    public AutostartEntry Entry { get; }
    public ProgramInfo? Info { get; }
    public SmartCpuChange? Change { get; }
    public ProgramClass Class { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
    public string ClassText { get; }
    public Brush ClassBrush { get; }
    public string StatusText { get; }
    public Brush StatusBrush { get; }
    public string Command { get; }
    public string Where { get; }
    public string Detail { get; }
    public bool IsRecommended { get; }
    public string RecommendText { get; }
    public Visibility RecommendVisibility { get; }
    public bool CanToggle { get; }
    public string ToggleText { get; }
    public string ToggleTip { get; }

    public static bool IsGameProtection(string text) =>
        GameProtection.Any(g => text.Contains(g, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where it is set up, in words the user can find again ("Registry Run (this user): Steam").</summary>
    public static string WhereText(AutostartEntry e) => e.Kind switch
    {
        AutostartKind.RunKey => Loc.F("Cpu_WhereRun", "Registry {0} → {1}", e.Location, e.Name),
        AutostartKind.StartupFolder => Loc.F("Cpu_WhereStartup", "Startup folder → {0}", e.Name),
        AutostartKind.ScheduledTask => Loc.F("Cpu_WhereTask", "Task Scheduler → {0}", e.Name),
        _ => Loc.F("Cpu_WhereService", "Service → {0} ({1})", e.DisplayName ?? e.Name, e.Name)
    };

    private static string TriggerName(string trigger) => trigger switch
    {
        "logon" => Loc.T("Cpu_TrigLogon", "sign-in"),
        "boot" => Loc.T("Cpu_TrigBoot", "start-up"),
        "idle" => Loc.T("Cpu_TrigIdle", "idle"),
        "schedule" => Loc.T("Cpu_TrigSchedule", "schedule"),
        "event" => Loc.T("Cpu_TrigEvent", "event"),
        _ => trigger
    };

    public static string ClassName(ProgramClass cls) => cls switch
    {
        ProgramClass.Windows => Loc.T("Cpu_ClassWindows", "Windows"),
        ProgramClass.Security => Loc.T("Cpu_ClassSecurity", "Protection"),
        ProgramClass.Hardware => Loc.T("Cpu_ClassHardware", "Device helper"),
        ProgramClass.Updater => Loc.T("Cpu_ClassUpdater", "Updater"),
        ProgramClass.Suspicious => Loc.T("Cpu_ClassSuspicious", "Unsigned"),
        _ => Loc.T("Cpu_ClassApp", "Program")
    };

    public static Brush ClassColor(ProgramClass cls) => (Brush)(Application.Current.TryFindResource(cls switch
    {
        ProgramClass.Windows => "SteelLightBrush",
        ProgramClass.Security => "SuccessBrush",
        ProgramClass.Hardware => "AccentLightBrush",
        ProgramClass.Updater => "WarningBrush",
        ProgramClass.Suspicious => "DangerBrush",
        _ => "AccentPrimaryBrush"
    }) ?? Brushes.Gray);
}

/// <summary>One change Smart CPU made, in the undo list.</summary>
public sealed class ChangeRow
{
    public ChangeRow(SmartCpuChange change)
    {
        Change = change;
        Name = change.Setting is BrowserSetting s ? SettingName(s) : change.Display;
        var when = new DateTime(change.ChangedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
        Detail = change.Setting != null
            ? when
            : change.TurnedOffAgain > 0
                ? Loc.F("Cpu_ChangeDetailAgain", "{0} · turned off again {1:N0}×", when, change.TurnedOffAgain)
                : $"{when} · {change.Name}";
    }

    public SmartCpuChange Change { get; }
    public string Name { get; }
    public string Detail { get; }

    private static string SettingName(BrowserSetting setting) => setting switch
    {
        BrowserSetting.ChromeBackgroundMode => Loc.T("Cpu_ChromeBackground", "Chrome: stop running in the background after closing"),
        BrowserSetting.ChromeMemorySaver => Loc.T("Cpu_ChromeMemorySaver", "Chrome: Memory Saver (unload inactive tabs)"),
        BrowserSetting.EdgeBackgroundMode => Loc.T("Cpu_EdgeBackground", "Edge: stop running in the background after closing"),
        BrowserSetting.EdgeStartupBoost => Loc.T("Cpu_EdgeStartupBoost", "Edge: don't preload at sign-in (Startup boost)"),
        _ => Loc.T("Cpu_EdgeEfficiency", "Edge: efficiency mode")
    };
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetX.Core.Rules;

namespace NetX.App.Views;

public partial class RulesView : Page
{
    private const int MaxActivityItems = 200;

    private readonly RuleEngine _ruleEngine;
    private readonly ObservableCollection<RuleDisplayItem> _rules = new();
    private readonly ObservableCollection<ActivityDisplayItem> _activity = new();
    private int _rulesRefreshQueued;

    public RulesView()
    {
        InitializeComponent();
        _ruleEngine = RuleEngine.Instance;
        RulesList.ItemsSource = _rules;
        ActivityList.ItemsSource = _activity;

        // The engine is a singleton and pages are recreated on every visit, so
        // listen only while this page is shown.
        Loaded += RulesView_Loaded;
        Unloaded += RulesView_Unloaded;
    }

    /// <summary>
    /// Starts the rule engine and shows "Show notification" actions. Call once
    /// from App.OnStartup; calling it again does nothing. Loading (and the
    /// one-time migration of old rules) runs off the UI thread.
    /// </summary>
    /// <param name="showNotice">
    /// Optional in-app notice, called on the UI thread with (rule name, text),
    /// e.g. the main window's toast. Without it a small non-activating toast
    /// appears in the bottom-right corner of the screen.
    /// </param>
    public static void StartAutomation(Action<string, string>? showNotice = null)
    {
        var dispatcher = Application.Current.Dispatcher;
        Task.Run(() =>
        {
            try
            {
                var engine = RuleEngine.Instance;
                RuleNotificationToast.Attach(engine, dispatcher, showNotice);
                engine.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not start automation rules: {ex.Message}");
            }
        });
    }

    private void RulesView_Loaded(object sender, RoutedEventArgs e)
    {
        Subscribe(true);
        LoadRules();
        LoadActivity();
        UpdateStatus();
    }

    private void RulesView_Unloaded(object sender, RoutedEventArgs e) => Subscribe(false);

    private void Subscribe(bool subscribe)
    {
        // Always detach first so a second Loaded never doubles the handlers.
        _ruleEngine.RulesChanged -= Engine_RulesChanged;
        _ruleEngine.ActivityLogged -= Engine_ActivityLogged;
        _ruleEngine.StateChanged -= Engine_StateChanged;
        if (!subscribe) return;

        _ruleEngine.RulesChanged += Engine_RulesChanged;
        _ruleEngine.ActivityLogged += Engine_ActivityLogged;
        _ruleEngine.StateChanged += Engine_StateChanged;
    }

    #region Engine events (raised on background threads)

    private void Engine_RulesChanged(object? sender, EventArgs e)
    {
        // A rule run raises several changes; refresh the list once per burst.
        if (Interlocked.Exchange(ref _rulesRefreshQueued, 1) == 1) return;
        Dispatcher.InvokeAsync(() =>
        {
            Volatile.Write(ref _rulesRefreshQueued, 0);
            LoadRules();
        }, DispatcherPriority.Background);
    }

    private void Engine_ActivityLogged(object? sender, RuleActivityEventArgs e) =>
        Dispatcher.InvokeAsync(() => AddActivity(e.Entry));

    private void Engine_StateChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(UpdateStatus);

    #endregion

    private void LoadRules()
    {
        var rules = _ruleEngine.GetAllRules();

        // Update rows in place when the list is the same, so a rule firing does
        // not reset the scroll position.
        if (rules.Select(r => r.Id).SequenceEqual(_rules.Select(r => r.Id)))
        {
            for (int i = 0; i < rules.Count; i++) _rules[i].Update(rules[i]);
        }
        else
        {
            _rules.Clear();
            foreach (var rule in rules) _rules.Add(new RuleDisplayItem(rule));
        }

        EmptyState.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RulesList.Visibility = rules.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RulesCountText.Text = rules.Count > 0
            ? RuleText.F("Rules_CountFormat", "{0} rule(s) configured", rules.Count)
            : RuleText.T("Rules_Subtitle", "Run actions automatically when conditions are met");
    }

    private void LoadActivity()
    {
        _activity.Clear();
        foreach (var entry in _ruleEngine.GetRecentActivity())
            _activity.Add(new ActivityDisplayItem(entry));
        UpdateActivityEmptyState();
    }

    private void AddActivity(RuleActivityEntry entry)
    {
        // Entries logged while the page loaded arrive twice; keep newest first.
        if (_activity.Any(a => a.Sequence == entry.Sequence)) return;

        int index = 0;
        while (index < _activity.Count && _activity[index].Sequence > entry.Sequence) index++;
        _activity.Insert(index, new ActivityDisplayItem(entry));

        while (_activity.Count > MaxActivityItems) _activity.RemoveAt(_activity.Count - 1);
        UpdateActivityEmptyState();
    }

    private void UpdateActivityEmptyState()
    {
        ActivityEmptyText.Visibility = _activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatus()
    {
        var state = _ruleEngine.State;
        (string text, string brushKey) = state switch
        {
            RuleEngineState.Running => (RuleText.T("Rules_StatusRunning", "Automation is on: rules are checked every 5 seconds"), "SuccessBrush"),
            RuleEngineState.PausedNoPro => (RuleText.T("Rules_StatusNoPro", "Paused: Automation Rules need WinXTools Pro"), "WarningBrush"),
            RuleEngineState.StorageUnavailable => (RuleText.StorageMessage(_ruleEngine.StorageError, _ruleEngine.StorageFolder), "DangerBrush"),
            _ => (RuleText.T("Rules_StatusStopped", "Automation is not running"), "TextTertiaryBrush")
        };

        StatusText.Text = text;
        StatusDot.Fill = RuleText.Brush(brushKey, Colors.Gray);

        // Without storage nothing can be saved, so don't offer to create rules.
        bool canEdit = state != RuleEngineState.StorageUnavailable;
        NewRuleButton.IsEnabled = canEdit;
        CreateRuleButton.IsEnabled = canEdit;
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RuleEditorDialog(null, _ruleEngine.AddRule) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) LoadRules();
    }

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string ruleId }) return;

        var rule = _ruleEngine.GetRuleById(ruleId);
        if (rule == null)
        {
            LoadRules(); // deleted meanwhile
            return;
        }

        var dialog = new RuleEditorDialog(rule, _ruleEngine.UpdateRule) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) LoadRules();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string ruleId }) return;

        var name = _rules.FirstOrDefault(r => r.Id == ruleId)?.Name ?? string.Empty;
        var answer = ShowMessage(
            RuleText.F("Rules_DeleteConfirm", "Delete the rule \"{0}\"? This cannot be undone.", name),
            RuleText.T("Rules_DeleteConfirmTitle", "Delete Rule"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _ruleEngine.DeleteRule(ruleId);
        }
        catch (Exception ex) when (ex is RuleStorageException or RuleValidationException)
        {
            ShowError(ex);
        }
        LoadRules();
    }

    private void ToggleRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string ruleId } toggle) return;

        try
        {
            _ruleEngine.SetRuleEnabled(ruleId, toggle.IsChecked == true);
        }
        catch (Exception ex) when (ex is RuleStorageException or RuleValidationException)
        {
            ShowError(ex);
        }
        // Show what is stored; this also flips the switch back if it failed.
        LoadRules();
    }

    private void ShowError(Exception ex)
    {
        ShowMessage(RuleText.ErrorMessage(ex),
            RuleText.T("Rules_SaveFailedTitle", "Could not save the rule"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private MessageBoxResult ShowMessage(string text, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = Window.GetWindow(this);
        return owner != null
            ? MessageBox.Show(owner, text, title, buttons, image)
            : MessageBox.Show(text, title, buttons, image);
    }
}

/// <summary>
/// Localized text for the rules UI, from the active language dictionary with
/// an English fallback. Shared by the Rules page, the editor and the toasts.
/// </summary>
internal static class RuleText
{
    public static string T(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as string ?? fallback;

    public static string F(string key, string fallback, params object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, T(key, fallback), args);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.CurrentCulture, fallback, args);
        }
    }

    public static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    public static string ConditionName(ConditionType type) => type switch
    {
        ConditionType.TimeRange => T("RuleEditor_CondTimeRange", "Time range"),
        ConditionType.DayOfWeek => T("RuleEditor_CondDayOfWeek", "Day of week"),
        ConditionType.ProcessRunning => T("RuleEditor_CondProcessRunning", "App running"),
        ConditionType.BandwidthExceeds => T("RuleEditor_CondBandwidth", "Total network speed"),
        ConditionType.ConnectionCount => T("RuleEditor_CondConnectionCount", "Connection count"),
        ConditionType.IPConnected => T("RuleEditor_CondIpConnected", "IP connected"),
        ConditionType.PortInUse => T("RuleEditor_CondPortInUse", "Port in use"),
        ConditionType.Always => T("RuleEditor_CondAlways", "When the rule starts"),
        _ => type.ToString()
    };

    public static string ActionName(ActionType type) => type switch
    {
        ActionType.BlockIP => T("RuleEditor_ActBlockIP", "Block IP"),
        ActionType.UnblockIP => T("RuleEditor_ActUnblockIP", "Unblock IP"),
        ActionType.BlockPort => T("RuleEditor_ActBlockPort", "Block port"),
        ActionType.UnblockPort => T("RuleEditor_ActUnblockPort", "Unblock port"),
        ActionType.LimitBandwidth => T("RuleEditor_ActLimitBandwidth", "Limit speed"),
        ActionType.UnlimitBandwidth => T("RuleEditor_ActUnlimitBandwidth", "Remove speed limit"),
        ActionType.KillProcess => T("RuleEditor_ActKillProcess", "Close app"),
        ActionType.WriteToFile => T("RuleEditor_ActWriteToFile", "Write to log file"),
        ActionType.ShowNotification => T("RuleEditor_ActShowNotification", "Show notification"),
        ActionType.SendWebhook => T("RuleEditor_ActSendWebhook", "Send webhook"),
        _ => type.ToString()
    };

    public static string ValidationMessage(RuleValidationError error)
    {
        var text = error.Problem switch
        {
            RuleProblem.NameRequired => T("RuleEditor_ErrNameRequired", "Please enter a rule name."),
            RuleProblem.NoActions => T("RuleEditor_ErrNoActions", "Please add at least one action."),
            RuleProblem.TooManyRules => F("RuleEditor_ErrTooManyRules", "You can have at most {0} rules. Delete a rule you no longer need first.", RuleValidator.MaxRules),
            RuleProblem.TooManyItems => F("RuleEditor_ErrTooMany", "A rule can have at most {0} conditions and {0} actions.", RuleValidator.MaxConditions),
            RuleProblem.InvalidCooldown => F("RuleEditor_ErrCooldown", "Cooldown must be a whole number of seconds from 0 to {0}.", RuleValidator.MaxCooldownSeconds),
            RuleProblem.InvalidTargetApp => T("RuleEditor_ErrTargetApp", "Target app must be just an app name, e.g. chrome."),
            RuleProblem.UnknownType => T("RuleEditor_ErrUnknownType", "choose a type."),
            RuleProblem.InvalidTime => T("RuleEditor_ErrTime", "enter times as HH:mm, e.g. 22:00."),
            RuleProblem.SameStartEnd => T("RuleEditor_ErrSameTime", "start and end time must be different."),
            RuleProblem.NoDays => T("RuleEditor_ErrNoDays", "pick at least one day."),
            RuleProblem.AppRequired => T("RuleEditor_ErrAppRequired", "enter an app name, or set the rule's target app."),
            RuleProblem.InvalidAppName => T("RuleEditor_ErrAppName", "enter just the app name, e.g. chrome (no folders or wildcards)."),
            RuleProblem.InvalidNumber => T("RuleEditor_ErrNumber", "enter a whole number (0 or more)."),
            RuleProblem.InvalidIp => T("RuleEditor_ErrIp", "enter a valid IP address, e.g. 203.0.113.5."),
            RuleProblem.InvalidPort => T("RuleEditor_ErrPort", "enter a port from 1 to 65535."),
            RuleProblem.InvalidProtocol => T("RuleEditor_ErrProtocol", "choose TCP or UDP."),
            RuleProblem.InvalidLimit => F("RuleEditor_ErrLimit", "enter a speed in KB/s from 0 to {0}.", RuleValidator.MaxLimitKBps),
            RuleProblem.InvalidFileName => T("RuleEditor_ErrFileName", "use a simple file name ending in .log or .txt, e.g. rules.log."),
            RuleProblem.InvalidUrl => T("RuleEditor_ErrUrl", "enter a full address starting with https:// or http://."),
            RuleProblem.ProtectedProcess => T("RuleEditor_ErrProtected", "Windows system processes and WinXTools itself cannot be closed by a rule."),
            RuleProblem.RemovedAction => T("RuleEditor_ErrRemoved", "this action is no longer supported."),
            _ => error.Problem.ToString()
        };

        if (error.ConditionIndex >= 0)
            return F("RuleEditor_ErrCondition", "Condition {0}: {1}", error.ConditionIndex + 1, text);
        if (error.ActionIndex >= 0)
            return F("RuleEditor_ErrAction", "Action {0}: {1}", error.ActionIndex + 1, text);
        return text;
    }

    public static string StorageMessage(RuleStorageError? error, string folder) => error switch
    {
        RuleStorageError.AccessDenied => F("Rules_StorageAccessDenied",
            "Rules are off: WinXTools could not protect its rules folder ({0}). Run WinXTools as administrator.", folder),
        RuleStorageError.NotSecure => F("Rules_StorageUnsafe",
            "Rules are off: the rules folder ({0}) is not safe to use. Delete that folder and restart WinXTools.", folder),
        _ => F("Rules_StorageIoError",
            "Rules are off: the rules file in {0} could not be read or saved.", folder)
    };

    /// <summary>Friendly text for an error from saving a rule; never a raw exception dump.</summary>
    public static string ErrorMessage(Exception ex) => ex switch
    {
        RuleValidationException { Errors.Count: > 0 } invalid => ValidationMessage(invalid.Errors[0]),
        RuleStorageException { Error: RuleStorageError.IoError } failed =>
            F("Rules_SaveFailed", "The change could not be saved: {0}", failed.InnerException?.Message ?? failed.Location),
        RuleStorageException storage => StorageMessage(storage.Error, RuleEngine.Instance.StorageFolder),
        _ => F("Rules_SaveFailed", "The change could not be saved: {0}", ex.Message)
    };

    /// <summary>One line of the activity log.</summary>
    public static string DescribeActivity(RuleActivityEntry entry) => entry.Kind switch
    {
        RuleActivityKind.Fired => F("Rules_LogFired", "Rule \"{0}\" fired", entry.RuleName),
        RuleActivityKind.Notification => F("Rules_LogNotification", "{0}: notification \"{1}\"", entry.RuleName,
            string.IsNullOrEmpty(entry.Detail) ? F("Rules_LogFired", "Rule \"{0}\" fired", entry.RuleName) : entry.Detail),
        RuleActivityKind.Succeeded => F(entry.Code == RuleActivityCode.Requested ? "Rules_LogRequested" : "Rules_LogDone",
            entry.Code == RuleActivityCode.Requested ? "{0}: requested" : "{0}: done", ActionLabel(entry)),
        RuleActivityKind.Skipped => F("Rules_LogSkipped", "{0}: skipped ({1})", ActionLabel(entry), Reason(entry)),
        RuleActivityKind.Failed => F("Rules_LogFailed", "{0}: failed ({1})", ActionLabel(entry), Reason(entry)),
        _ => EngineNotice(entry)
    };

    private static string ActionLabel(RuleActivityEntry entry)
    {
        var action = entry.Action is { } type ? ActionName(type) : string.Empty;
        var target = entry.Target == RuleValidator.AllApps ? T("RuleEditor_WholePc", "whole PC") : entry.Target;
        if (entry.Amount is { } kbps)
        {
            var amount = kbps == 0 ? T("RuleEditor_Blocked", "blocked") : $"{kbps} KB/s";
            target = string.IsNullOrEmpty(target) ? amount : $"{target} ({amount})";
        }

        var label = string.IsNullOrEmpty(target) ? action : $"{action} {target}";
        return $"{entry.RuleName} › {label}";
    }

    private static string Reason(RuleActivityEntry entry) => entry.Code switch
    {
        RuleActivityCode.AlreadyBlocked => T("Rules_ReasonAlreadyBlocked", "already blocked"),
        RuleActivityCode.NotRunning => T("Rules_ReasonNotRunning", "the app is not running"),
        RuleActivityCode.ProtectedProcess => T("Rules_ReasonProtected", "Windows system processes cannot be closed"),
        RuleActivityCode.InvalidSettings => T("Rules_ReasonInvalid", "the action settings are not valid, edit the rule"),
        RuleActivityCode.FirewallFailed => T("Rules_ReasonFirewall", "Windows Firewall did not accept the change"),
        RuleActivityCode.KillFailed => T("Rules_ReasonKill", "the app could not be closed"),
        RuleActivityCode.WriteFailed => T("Rules_ReasonWrite", "the log file could not be written"),
        RuleActivityCode.WebhookFailed => F("Rules_ReasonWebhook", "the server answered {0}", entry.Detail ?? string.Empty),
        RuleActivityCode.WebhookTimeout => T("Rules_ReasonWebhookTimeout", "no answer within 10 seconds"),
        RuleActivityCode.WebhookUnreachable => T("Rules_ReasonWebhookUnreachable", "the server could not be reached"),
        RuleActivityCode.ActionRemoved => T("Rules_ReasonRemoved", "this action is no longer supported"),
        _ => F("Rules_ReasonError", "unexpected error: {0}", entry.Detail ?? string.Empty)
    };

    private static string EngineNotice(RuleActivityEntry entry) => entry.Code switch
    {
        RuleActivityCode.RulesImported => F("Rules_LogImported",
            "Imported {0} rule(s) from the previous version. They are turned off: check each rule, then turn it on.", entry.Amount ?? 0),
        RuleActivityCode.RemovedActionsDropped => F("Rules_LogRemovedActions",
            "Removed {0} action(s) that are no longer supported (Execute Command, LINE Notify, SMS, Email).", entry.Amount ?? 0),
        RuleActivityCode.InvalidRulesDropped => F("Rules_LogInvalidRules",
            "Skipped {0} rule(s) that had no usable actions left.", entry.Amount ?? 0),
        RuleActivityCode.UnknownSettingsTurnedOff => F("Rules_LogUnknownSettings",
            "Turned off {0} rule(s) with settings this version does not understand. Edit them before turning them on.", entry.Amount ?? 0),
        RuleActivityCode.UntrustedFileSetAside => F("Rules_LogUntrustedFile",
            "Rules that other programs could have changed were moved aside and not used: {0}", entry.Detail ?? string.Empty),
        RuleActivityCode.UnreadableFileSetAside => F("Rules_LogUnreadableFile",
            "The rules file could not be read and was moved aside: {0}", entry.Detail ?? string.Empty),
        RuleActivityCode.SaveFailed => F("Rules_SaveFailed",
            "The change could not be saved: {0}", entry.Detail ?? string.Empty),
        RuleActivityCode.ConditionError => F("Rules_LogConditionError",
            "{0}: a condition could not be checked ({1})", entry.RuleName, entry.Detail ?? string.Empty),
        RuleActivityCode.StorageAccessDenied => StorageMessage(RuleStorageError.AccessDenied, entry.Detail ?? string.Empty),
        RuleActivityCode.StorageNotSecure => StorageMessage(RuleStorageError.NotSecure, entry.Detail ?? string.Empty),
        RuleActivityCode.StorageIoError => StorageMessage(RuleStorageError.IoError, entry.Detail ?? string.Empty),
        _ => entry.Detail ?? string.Empty
    };
}

public class RuleDisplayItem : INotifyPropertyChanged
{
    private static readonly Brush EnabledColor = Frozen("#10b981");
    private static readonly Brush EnabledBackground = Frozen("#1510b981");
    private static readonly Brush DisabledColor = Frozen("#6b7280");
    private static readonly Brush DisabledBackground = Frozen("#156b7280");

    public string Id { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public bool HasDescription => Description.Length > 0;
    public bool IsEnabled { get; private set; }
    public string ConditionsText { get; private set; } = string.Empty;
    public string ActionsText { get; private set; } = string.Empty;
    public string TriggerCountText { get; private set; } = string.Empty;
    public string LastTriggeredText { get; private set; } = string.Empty;
    public string LastResultText { get; private set; } = string.Empty;
    public Brush LastResultBrush { get; private set; } = Brushes.Transparent;
    public Brush StatusColor { get; private set; } = DisabledColor;
    public Brush StatusBackground { get; private set; } = DisabledBackground;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RuleDisplayItem(NetworkRule rule)
    {
        Update(rule);
    }

    public void Update(NetworkRule rule)
    {
        Id = rule.Id;
        Name = rule.Name;
        Description = rule.Description ?? string.Empty;
        IsEnabled = rule.IsEnabled;

        ConditionsText = rule.Conditions.Count == 0
            ? RuleText.T("Rules_RunsAtStart", "Runs once when turned on")
            : string.Join(rule.ConditionLogic == ConditionLogic.Or ? " / " : " + ",
                rule.Conditions.Select(c => RuleText.ConditionName(c.Type)));
        ActionsText = string.Join(", ", rule.Actions.Select(a => RuleText.ActionName(a.Type)));

        TriggerCountText = RuleText.F("Rules_FiredCount", "Fired {0} time(s)", rule.TriggerCount);
        LastTriggeredText = rule.LastTriggeredAt is { } last
            ? RuleText.F("Rules_LastFired", "Last: {0}", last.ToString("d MMM HH:mm", CultureInfo.CurrentCulture))
            : RuleText.T("Rules_NeverFired", "Never fired yet");

        // Both counts are 0 while the actions are still running.
        if (rule.LastRunFailed > 0)
        {
            LastResultText = RuleText.F("Rules_LastRunMixed", "{0} OK, {1} failed", rule.LastRunSucceeded, rule.LastRunFailed);
            LastResultBrush = RuleText.Brush("DangerBrush", Colors.IndianRed);
        }
        else if (rule.LastRunSucceeded > 0)
        {
            LastResultText = RuleText.F("Rules_LastRunOk", "{0} action(s) OK", rule.LastRunSucceeded);
            LastResultBrush = RuleText.Brush("SuccessBrush", Colors.MediumSeaGreen);
        }
        else
        {
            LastResultText = string.Empty;
            LastResultBrush = Brushes.Transparent;
        }

        StatusColor = IsEnabled ? EnabledColor : DisabledColor;
        StatusBackground = IsEnabled ? EnabledBackground : DisabledBackground;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}

public class ActivityDisplayItem
{
    public long Sequence { get; }
    public string Time { get; }
    public string Text { get; }
    public Brush DotBrush { get; }

    public ActivityDisplayItem(RuleActivityEntry entry)
    {
        Sequence = entry.Sequence;
        Time = entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Text = RuleText.DescribeActivity(entry);
        DotBrush = entry.Kind switch
        {
            RuleActivityKind.Fired => RuleText.Brush("AccentPrimaryBrush", Colors.SteelBlue),
            RuleActivityKind.Succeeded => RuleText.Brush("SuccessBrush", Colors.MediumSeaGreen),
            RuleActivityKind.Failed => RuleText.Brush("DangerBrush", Colors.IndianRed),
            RuleActivityKind.Notification => RuleText.Brush("InfoBrush", Colors.DeepSkyBlue),
            RuleActivityKind.Skipped or RuleActivityKind.Warning => RuleText.Brush("WarningBrush", Colors.Orange),
            _ => RuleText.Brush("TextTertiaryBrush", Colors.Gray)
        };
    }
}

/// <summary>
/// Shows "Show notification" rule actions as small toasts in the bottom-right
/// corner of the screen. They never take focus or block, close by themselves
/// (hovering keeps them open), and at most three are shown at once; every
/// notification is also in the Rules page activity log.
/// </summary>
public static class RuleNotificationToast
{
    private const int MaxVisible = 3;
    private const double ToastWidth = 320;
    private static readonly TimeSpan ShowTime = TimeSpan.FromSeconds(6);
    private static readonly List<Window> OpenToasts = new();
    private static int _attached;

    /// <param name="showNotice">Optional in-app notice used instead of the corner toast.</param>
    public static void Attach(RuleEngine engine, Dispatcher dispatcher, Action<string, string>? showNotice = null)
    {
        if (Interlocked.Exchange(ref _attached, 1) == 1) return;

        engine.NotificationRequested += (_, e) =>
        {
            if (dispatcher.HasShutdownStarted) return;
            dispatcher.InvokeAsync(() =>
            {
                var text = e.Message.Length > 0 ? e.Message : RuleText.F("Rules_LogFired", "Rule \"{0}\" fired", e.RuleName);
                try
                {
                    if (showNotice != null) showNotice(e.RuleName, text);
                    else Show(e.RuleName, text);
                }
                catch (Exception ex)
                {
                    // A notice must never take the app down; the activity log still has it.
                    Debug.WriteLine($"Could not show rule notification: {ex.Message}");
                }
            });
        };
    }

    private static void Show(string ruleName, string body)
    {
        var app = Application.Current;
        if (app == null || app.Dispatcher.HasShutdownStarted) return;

        while (OpenToasts.Count >= MaxVisible)
        {
            var oldest = OpenToasts[0];
            OpenToasts.RemoveAt(0);
            oldest.Close();
        }

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = RuleText.T("Rules_ToastCaption", "WinXTools · Automation"),
            FontSize = 10,
            Foreground = RuleText.Brush("TextTertiaryBrush", Colors.Gray),
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = ruleName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = RuleText.Brush("TextPrimaryBrush", Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 12,
            Foreground = RuleText.Brush("TextSecondaryBrush", Colors.LightGray),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 64,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var card = new Border
        {
            Background = RuleText.Brush("BgSecondaryBrush", Color.FromRgb(0x1b, 0x22, 0x33)),
            BorderBrush = RuleText.Brush("AccentPrimaryBrush", Colors.SteelBlue),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            Cursor = Cursors.Hand,
            Child = panel
        };
        if (app.TryFindResource("PrimaryFont") is FontFamily font)
            TextElement.SetFontFamily(card, font);

        // Measure first so the toast gets its final height before it is placed.
        card.Measure(new Size(ToastWidth, double.PositiveInfinity));
        var toast = new Window
        {
            Content = card,
            Width = ToastWidth,
            Height = Math.Ceiling(card.DesiredSize.Height),
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false, // never steal focus from what the user is doing
            Topmost = true,
            Focusable = false
        };

        var timer = new DispatcherTimer { Interval = ShowTime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            toast.Close();
        };
        card.MouseEnter += (_, _) => timer.Stop();
        card.MouseLeave += (_, _) => timer.Start();
        card.MouseLeftButtonUp += (_, _) =>
        {
            toast.Close();
            BringAppToFront();
        };
        toast.Closed += (_, _) =>
        {
            timer.Stop();
            OpenToasts.Remove(toast);
            ArrangeToasts();
        };

        OpenToasts.Add(toast);
        ArrangeToasts();
        toast.Show();
        timer.Start();
    }

    /// <summary>Stacks the toasts up from the bottom-right corner, newest at the bottom.</summary>
    private static void ArrangeToasts()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - 12;
        for (int i = OpenToasts.Count - 1; i >= 0; i--)
        {
            var toast = OpenToasts[i];
            toast.Left = area.Right - toast.Width - 12;
            toast.Top = bottom - toast.Height;
            bottom = toast.Top - 8;
        }
    }

    private static void BringAppToFront()
    {
        if (Application.Current?.MainWindow is not { IsVisible: true } main) return;
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
    }
}

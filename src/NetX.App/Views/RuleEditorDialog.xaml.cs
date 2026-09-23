using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetX.Core.Rules;

namespace NetX.App.Views;

public partial class RuleEditorDialog : Window
{
    /// <summary>The rule as it was saved (a copy; the engine owns the stored rule).</summary>
    public NetworkRule Rule { get; private set; }

    private readonly Action<NetworkRule> _save;
    private readonly ObservableCollection<ConditionEditModel> _conditions = new();
    private readonly ObservableCollection<ActionEditModel> _actions = new();
    private bool _saved;

    /// <param name="existingRule">Rule to edit, or null for a new rule.</param>
    /// <param name="save">
    /// Stores the rule. A <see cref="RuleValidationException"/> or
    /// <see cref="RuleStorageException"/> keeps the dialog open with the user's input.
    /// </param>
    public RuleEditorDialog(NetworkRule? existingRule, Action<NetworkRule> save)
    {
        InitializeComponent();
        _save = save;

        ConditionsList.ItemsSource = _conditions;
        ActionsList.ItemsSource = _actions;

        if (existingRule != null)
        {
            // Work on a copy: the stored rule only changes when the save succeeds.
            Rule = existingRule.Clone();
            SetResourceReference(TitleProperty, "RuleEditor_TitleEdit");
            TitleText.SetResourceReference(TextBlock.TextProperty, "RuleEditor_TitleEdit");
            LoadExistingRule();
        }
        else
        {
            Rule = new NetworkRule();
            CooldownBox.Text = Rule.CooldownSeconds.ToString(CultureInfo.InvariantCulture);
        }

        UpdateConditionsVisibility();
        UpdateActionsVisibility();
        Loaded += (_, _) => RuleNameBox.Focus();
        KeyDown += RuleEditorDialog_KeyDown;
    }

    private void RuleEditorDialog_KeyDown(object sender, KeyEventArgs e)
    {
        // Esc cancels, unless a control (e.g. an open drop-down) used it already.
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        DialogResult = false;
    }

    private void LoadExistingRule()
    {
        RuleNameBox.Text = Rule.Name;
        DescriptionBox.Text = Rule.Description;
        ProcessNameBox.Text = Rule.TargetProcessName ?? string.Empty;
        CooldownBox.Text = Rule.CooldownSeconds.ToString(CultureInfo.InvariantCulture);
        EnabledCheckBox.IsChecked = Rule.IsEnabled;
        // Anything but a real "Or" shows as AND, the same way the engine treats it.
        ConditionLogicBox.SelectedIndex = Rule.ConditionLogic == ConditionLogic.Or ? 1 : 0;

        foreach (var condition in Rule.Conditions)
            _conditions.Add(ConditionEditModel.From(condition));

        // Removed actions (Execute Command, LINE Notify, SMS, Email) are dropped
        // when rules load; skip any that remain so they are not saved again.
        foreach (var action in Rule.Actions.Where(a => RuleValidator.IsSupportedAction(a.Type)))
            _actions.Add(ActionEditModel.From(action));
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    // Setting DialogResult closes a dialog shown with ShowDialog.
    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // A second click while the first save closes the dialog must not add the rule twice.
        if (_saved) return;

        // Blank cooldown means the default.
        var cooldownText = CooldownBox.Text.Trim();
        int cooldown = RuleValidator.DefaultCooldownSeconds;
        if (cooldownText.Length > 0 &&
            (!int.TryParse(cooldownText, NumberStyles.None, CultureInfo.InvariantCulture, out cooldown) ||
             cooldown > RuleValidator.MaxCooldownSeconds))
        {
            ShowProblem(RuleText.ValidationMessage(new RuleValidationError(RuleProblem.InvalidCooldown)));
            CooldownBox.Focus();
            return;
        }

        // Start from a copy of the rule so the id, run history and anything this
        // dialog does not edit are kept.
        var rule = Rule.Clone();
        rule.Name = RuleNameBox.Text.Trim();
        rule.Description = DescriptionBox.Text?.Trim() ?? string.Empty;
        var targetApp = RuleValidator.NormalizeProcessName(ProcessNameBox.Text);
        rule.TargetProcessName = targetApp.Length == 0 ? null : targetApp;
        rule.CooldownSeconds = cooldown;
        rule.IsEnabled = EnabledCheckBox.IsChecked ?? true;
        rule.ConditionLogic = ConditionLogicBox.SelectedIndex == 1 ? ConditionLogic.Or : ConditionLogic.And;
        rule.Conditions = _conditions.Select(c => c.ToCondition()).ToList();
        rule.Actions = _actions.Select(a => a.ToAction()).ToList();

        var errors = RuleValidator.Validate(rule);
        if (errors.Count > 0)
        {
            ShowProblem(RuleText.ValidationMessage(errors[0]));
            if (errors[0].Problem == RuleProblem.NameRequired) RuleNameBox.Focus();
            return;
        }

        try
        {
            _save(rule);
        }
        catch (Exception ex) when (ex is RuleValidationException or RuleStorageException)
        {
            // Keep the dialog open so nothing the user typed is lost.
            ShowProblem(RuleText.ErrorMessage(ex));
            return;
        }

        _saved = true;
        Rule = rule;
        DialogResult = true;
    }

    private void ShowProblem(string message)
    {
        MessageBox.Show(this, message,
            RuleText.T("RuleEditor_ErrTitle", "Check the rule"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void AddCondition_Click(object sender, RoutedEventArgs e)
    {
        _conditions.Add(new ConditionEditModel { Type = nameof(ConditionType.TimeRange) });
        UpdateConditionsVisibility();
    }

    private void RemoveCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ConditionEditModel condition })
        {
            _conditions.Remove(condition);
            UpdateConditionsVisibility();
        }
    }

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        _actions.Add(new ActionEditModel { Type = nameof(ActionType.ShowNotification) });
        UpdateActionsVisibility();
    }

    private void RemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ActionEditModel action })
        {
            _actions.Remove(action);
            UpdateActionsVisibility();
        }
    }

    private void UpdateConditionsVisibility()
    {
        NoConditionsText.Visibility = _conditions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateActionsVisibility()
    {
        NoActionsText.Visibility = _actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

public abstract class EditModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// One condition row. The template shows different fields per Type, so it
/// raises change notifications for Type.
/// </summary>
public class ConditionEditModel : EditModelBase
{
    private string _type = nameof(ConditionType.TimeRange);

    public string Type { get => _type; set => SetField(ref _type, value); }
    /// <summary>"=" / "≠" for app, IP and port conditions.</summary>
    public string MatchOperator { get; set; } = nameof(ConditionOperator.Equals);
    /// <summary>Comparison for speed and connection count.</summary>
    public string CompareOperator { get; set; } = nameof(ConditionOperator.GreaterThan);
    public string Value { get; set; } = string.Empty;
    public string Value2 { get; set; } = string.Empty;

    public bool Monday { get; set; }
    public bool Tuesday { get; set; }
    public bool Wednesday { get; set; }
    public bool Thursday { get; set; }
    public bool Friday { get; set; }
    public bool Saturday { get; set; }
    public bool Sunday { get; set; }

    /// <summary>Kept as loaded; this editor has no fields for them.</summary>
    public Dictionary<string, string>? Parameters { get; set; }

    public static ConditionEditModel From(RuleCondition condition)
    {
        var model = new ConditionEditModel
        {
            Type = condition.Type.ToString(),
            Value = condition.Value ?? string.Empty,
            Value2 = condition.Value2 ?? string.Empty,
            Parameters = condition.Parameters == null ? null : new Dictionary<string, string>(condition.Parameters)
        };

        if (condition.Operator is ConditionOperator.Equals or ConditionOperator.NotEquals)
            model.MatchOperator = condition.Operator.ToString();
        if (condition.Operator is ConditionOperator.Equals or ConditionOperator.NotEquals or
            ConditionOperator.GreaterThan or ConditionOperator.LessThan or
            ConditionOperator.GreaterOrEqual or ConditionOperator.LessOrEqual)
            model.CompareOperator = condition.Operator.ToString();

        if (condition.Type == ConditionType.DayOfWeek && RuleValidator.TryParseDays(condition.Value, out var days))
        {
            model.Monday = days.Contains(DayOfWeek.Monday);
            model.Tuesday = days.Contains(DayOfWeek.Tuesday);
            model.Wednesday = days.Contains(DayOfWeek.Wednesday);
            model.Thursday = days.Contains(DayOfWeek.Thursday);
            model.Friday = days.Contains(DayOfWeek.Friday);
            model.Saturday = days.Contains(DayOfWeek.Saturday);
            model.Sunday = days.Contains(DayOfWeek.Sunday);
        }
        return model;
    }

    public RuleCondition ToCondition()
    {
        // An unknown type stays unknown so validation reports it.
        var type = Enum.TryParse<ConditionType>(Type, out var parsed) ? parsed : (ConditionType)(-1);
        var condition = new RuleCondition
        {
            Type = type,
            Parameters = Parameters == null ? null : new Dictionary<string, string>(Parameters)
        };

        switch (type)
        {
            case ConditionType.TimeRange:
                condition.Value = NormalizeTime(Value);
                condition.Value2 = NormalizeTime(Value2);
                break;

            case ConditionType.DayOfWeek:
                condition.Value = RuleValidator.FormatDays(SelectedDays());
                break;

            case ConditionType.ProcessRunning:
                condition.Operator = ParseOperator(MatchOperator);
                condition.Value = RuleValidator.NormalizeProcessName(Value);
                break;

            case ConditionType.IPConnected:
                condition.Operator = ParseOperator(MatchOperator);
                condition.Value = RuleValidator.TryParseIp(Value, out var address) ? address.ToString() : Value.Trim();
                break;

            case ConditionType.PortInUse:
                condition.Operator = ParseOperator(MatchOperator);
                condition.Value = Value.Trim();
                break;

            case ConditionType.BandwidthExceeds:
            case ConditionType.ConnectionCount:
                condition.Operator = ParseOperator(CompareOperator);
                condition.Value = Value.Trim();
                break;
        }
        return condition;
    }

    private IEnumerable<DayOfWeek> SelectedDays()
    {
        if (Monday) yield return DayOfWeek.Monday;
        if (Tuesday) yield return DayOfWeek.Tuesday;
        if (Wednesday) yield return DayOfWeek.Wednesday;
        if (Thursday) yield return DayOfWeek.Thursday;
        if (Friday) yield return DayOfWeek.Friday;
        if (Saturday) yield return DayOfWeek.Saturday;
        if (Sunday) yield return DayOfWeek.Sunday;
    }

    private static string NormalizeTime(string value) =>
        RuleValidator.TryParseTime(value, out var time) ? RuleValidator.FormatTime(time) : value.Trim();

    private static ConditionOperator ParseOperator(string value) =>
        Enum.TryParse<ConditionOperator>(value, out var op) ? op : ConditionOperator.Equals;
}

/// <summary>
/// One action row. Parameters keys this editor does not know are kept as they
/// were, so editing a rule never drops settings.
/// </summary>
public class ActionEditModel : EditModelBase
{
    private const string ProcessKey = "process";
    private const string ProtocolKey = "protocol";
    private const string MessageKey = "message";

    private string _type = nameof(ActionType.ShowNotification);

    public string Type { get => _type; set => SetField(ref _type, value); }
    public string Target { get; set; } = string.Empty;
    /// <summary>App for "Limit speed" ("*" = whole PC, blank = rule's target app).</summary>
    public string Process { get; set; } = string.Empty;
    public string Protocol { get; set; } = "TCP";
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, string>? Parameters { get; set; }

    public static ActionEditModel From(RuleAction action)
    {
        var parameters = action.Parameters;
        return new ActionEditModel
        {
            Type = action.Type.ToString(),
            Target = action.Target ?? string.Empty,
            Process = parameters?.GetValueOrDefault(ProcessKey) ?? string.Empty,
            Protocol = RuleValidator.NormalizeProtocol(parameters?.GetValueOrDefault(ProtocolKey)) ?? "TCP",
            Message = parameters?.GetValueOrDefault(MessageKey) ?? string.Empty,
            Parameters = parameters == null ? null : new Dictionary<string, string>(parameters)
        };
    }

    public RuleAction ToAction()
    {
        // An unknown type stays unknown so validation reports it.
        var type = Enum.TryParse<ActionType>(Type, out var parsed) ? parsed : (ActionType)(-1);

        var parameters = Parameters == null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(Parameters);
        SetOrRemove(parameters, ProcessKey, type == ActionType.LimitBandwidth ? RuleValidator.NormalizeProcessName(Process) : null);
        SetOrRemove(parameters, ProtocolKey, type is ActionType.BlockPort or ActionType.UnblockPort ? Protocol : null);
        SetOrRemove(parameters, MessageKey,
            type is ActionType.WriteToFile or ActionType.ShowNotification or ActionType.SendWebhook ? Message.Trim() : null);

        string? target = type switch
        {
            ActionType.ShowNotification => null,
            ActionType.BlockIP or ActionType.UnblockIP =>
                RuleValidator.TryNormalizeIpTarget(Target, out var address) ? address : Target.Trim(),
            ActionType.KillProcess or ActionType.UnlimitBandwidth => RuleValidator.NormalizeProcessName(Target),
            ActionType.WriteToFile => RuleValidator.NormalizeLogFileName(Target) ?? Target.Trim(),
            _ => Target.Trim()
        };

        return new RuleAction
        {
            Type = type,
            Target = target,
            Parameters = parameters.Count > 0 ? parameters : null
        };
    }

    private static void SetOrRemove(Dictionary<string, string> parameters, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) parameters.Remove(key);
        else parameters[key] = value;
    }
}

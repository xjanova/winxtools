using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using NetX.Core.Rules;

namespace NetX.App.Views;

public partial class RuleEditorDialog : Window
{
    public NetworkRule Rule { get; private set; }

    private readonly ObservableCollection<ConditionEditModel> _conditions = new();
    private readonly ObservableCollection<ActionEditModel> _actions = new();
#pragma warning disable CS0414 // Reserved for future edit-mode UI
    private readonly bool _isEditMode;
#pragma warning restore CS0414

    public RuleEditorDialog(NetworkRule? existingRule = null)
    {
        InitializeComponent();

        ConditionsList.ItemsSource = _conditions;
        ActionsList.ItemsSource = _actions;

        if (existingRule != null)
        {
            _isEditMode = true;
            Rule = existingRule;
            TitleText.Text = "Edit Rule";
            LoadExistingRule();
        }
        else
        {
            Rule = new NetworkRule();
        }

        UpdateConditionsVisibility();
        UpdateActionsVisibility();
    }

    private void LoadExistingRule()
    {
        RuleNameBox.Text = Rule.Name;
        DescriptionBox.Text = Rule.Description;
        ProcessNameBox.Text = Rule.TargetProcessName;
        EnabledCheckBox.IsChecked = Rule.IsEnabled;
        ConditionLogicBox.SelectedIndex = Rule.ConditionLogic == ConditionLogic.And ? 0 : 1;

        foreach (var condition in Rule.Conditions)
        {
            _conditions.Add(new ConditionEditModel
            {
                Type = condition.Type.ToString(),
                Operator = condition.Operator.ToString(),
                Value = condition.Value ?? "",
                Value2 = condition.Value2 ?? ""
            });
        }

        foreach (var action in Rule.Actions)
        {
            _actions.Add(new ActionEditModel
            {
                Type = action.Type.ToString(),
                Target = action.Target ?? ""
            });
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RuleNameBox.Text))
        {
            MessageBox.Show("Please enter a rule name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_actions.Count == 0)
        {
            MessageBox.Show("Please add at least one action.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Rule.Name = RuleNameBox.Text.Trim();
        Rule.Description = DescriptionBox.Text?.Trim() ?? "";
        Rule.TargetProcessName = string.IsNullOrWhiteSpace(ProcessNameBox.Text) ? null : ProcessNameBox.Text.Trim();
        Rule.IsEnabled = EnabledCheckBox.IsChecked ?? true;
        Rule.ConditionLogic = ConditionLogicBox.SelectedIndex == 0 ? ConditionLogic.And : ConditionLogic.Or;

        Rule.Conditions = _conditions.Select(c => new RuleCondition
        {
            Type = Enum.TryParse<ConditionType>(c.Type, out var type) ? type : ConditionType.Always,
            Operator = Enum.TryParse<ConditionOperator>(c.Operator, out var op) ? op : ConditionOperator.Equals,
            Value = c.Value,
            Value2 = c.Value2
        }).ToList();

        Rule.Actions = _actions.Select(a => new RuleAction
        {
            Type = Enum.TryParse<ActionType>(a.Type, out var type) ? type : ActionType.ShowNotification,
            Target = a.Target
        }).ToList();

        DialogResult = true;
        Close();
    }

    private void AddCondition_Click(object sender, RoutedEventArgs e)
    {
        _conditions.Add(new ConditionEditModel
        {
            Type = "TimeRange",
            Operator = "Equals",
            Value = "",
            Value2 = ""
        });
        UpdateConditionsVisibility();
    }

    private void RemoveCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is ConditionEditModel condition)
        {
            _conditions.Remove(condition);
            UpdateConditionsVisibility();
        }
    }

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        _actions.Add(new ActionEditModel
        {
            Type = "ShowNotification",
            Target = ""
        });
        UpdateActionsVisibility();
    }

    private void RemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is ActionEditModel action)
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

public class ConditionEditModel
{
    public string Type { get; set; } = "";
    public string Operator { get; set; } = "";
    public string Value { get; set; } = "";
    public string Value2 { get; set; } = "";
}

public class ActionEditModel
{
    public string Type { get; set; } = "";
    public string Target { get; set; } = "";
}

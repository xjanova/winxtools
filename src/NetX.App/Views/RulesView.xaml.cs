using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetX.Core.Rules;

namespace NetX.App.Views;

public partial class RulesView : Page
{
    private readonly RuleEngine _ruleEngine;

    public RulesView()
    {
        InitializeComponent();
        _ruleEngine = RuleEngine.Instance;
        LoadRules();

        // Subscribe to rule events
        _ruleEngine.RuleTriggered += OnRuleTriggered;
    }

    private void LoadRules()
    {
        var rules = _ruleEngine.GetAllRules();
        var displayItems = rules.Select(r => new RuleDisplayItem(r)).ToList();

        RulesList.ItemsSource = displayItems;

        EmptyState.Visibility = displayItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RulesList.Visibility = displayItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RulesCountText.Text = displayItems.Count > 0
            ? $"{displayItems.Count} rule(s) configured"
            : "Create powerful rules to automate your network management";
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RuleEditorDialog();
        if (dialog.ShowDialog() == true)
        {
            _ruleEngine.AddRule(dialog.Rule);
            LoadRules();
        }
    }

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string ruleId)
        {
            var rule = _ruleEngine.GetRuleById(ruleId);
            if (rule != null)
            {
                var dialog = new RuleEditorDialog(rule);
                if (dialog.ShowDialog() == true)
                {
                    _ruleEngine.UpdateRule(dialog.Rule);
                    LoadRules();
                }
            }
        }
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string ruleId)
        {
            var result = MessageBox.Show(
                "Are you sure you want to delete this rule?",
                "Delete Rule",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _ruleEngine.DeleteRule(ruleId);
                LoadRules();
            }
        }
    }

    private void ToggleRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle && toggle.Tag is string ruleId)
        {
            var rule = _ruleEngine.GetRuleById(ruleId);
            if (rule != null)
            {
                rule.IsEnabled = toggle.IsChecked ?? false;
                _ruleEngine.UpdateRule(rule);
            }
        }
    }

    private void OnRuleTriggered(object? sender, RuleTriggeredEventArgs e)
    {
        Dispatcher.Invoke(() => LoadRules());
    }
}

public class RuleDisplayItem
{
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public bool IsEnabled { get; }
    public string ConditionsText { get; }
    public string ActionsText { get; }
    public string TriggerCountText { get; }
    public string LastTriggeredText { get; }
    public Brush StatusColor { get; }
    public Brush StatusBackground { get; }

    public RuleDisplayItem(NetworkRule rule)
    {
        Id = rule.Id;
        Name = rule.Name;
        Description = rule.Description;
        IsEnabled = rule.IsEnabled;

        ConditionsText = $"{rule.Conditions?.Count ?? 0} conditions";
        ActionsText = $"{rule.Actions?.Count ?? 0} actions";
        TriggerCountText = $"Triggered {rule.TriggerCount}x";
        LastTriggeredText = rule.LastTriggeredAt.HasValue
            ? $"Last: {rule.LastTriggeredAt.Value:MMM dd, HH:mm}"
            : "Never triggered";

        if (IsEnabled)
        {
            StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10b981"));
            StatusBackground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1510b981"));
        }
        else
        {
            StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6b7280"));
            StatusBackground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#156b7280"));
        }
    }
}

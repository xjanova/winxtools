using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetX.App.Views;

public partial class BandwidthControlView : Page
{
    public BandwidthControlView()
    {
        InitializeComponent();
        LoadRules();
        UpdateModeCardStyles();
    }

    private void LoadRules()
    {
        // Sample rules for demonstration
        var rules = new List<BandwidthRule>
        {
            new BandwidthRule
            {
                ProcessName = "chrome.exe",
                DownloadLimit = "5 MB/s",
                UploadLimit = "2 MB/s",
                Status = "Active",
                IsEnabled = true,
                StatusColor = new SolidColorBrush(Color.FromRgb(0, 255, 136))
            },
            new BandwidthRule
            {
                ProcessName = "steam.exe",
                DownloadLimit = "Blocked",
                UploadLimit = "Blocked",
                Status = "Blocked",
                IsEnabled = true,
                StatusColor = new SolidColorBrush(Color.FromRgb(255, 68, 102))
            },
            new BandwidthRule
            {
                ProcessName = "discord.exe",
                DownloadLimit = "Unlimited",
                UploadLimit = "2 MB/s",
                Status = "Paused",
                IsEnabled = false,
                StatusColor = new SolidColorBrush(Color.FromRgb(102, 102, 102))
            }
        };

        RulesList.ItemsSource = rules;
        EmptyState.Visibility = rules.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateModeCardStyles()
    {
        // Guard against null controls during initialization
        if (BasicModeRadio == null || BasicModeCard == null || AdvancedModeCard == null) return;

        if (BasicModeRadio.IsChecked == true)
        {
            BasicModeCard.BorderBrush = (Brush)FindResource("AccentPrimaryBrush");
            BasicModeCard.BorderThickness = new Thickness(2);
            AdvancedModeCard.BorderBrush = (Brush)FindResource("BorderBrush");
            AdvancedModeCard.BorderThickness = new Thickness(1);
        }
        else
        {
            AdvancedModeCard.BorderBrush = (Brush)FindResource("AccentPrimaryBrush");
            AdvancedModeCard.BorderThickness = new Thickness(2);
            BasicModeCard.BorderBrush = (Brush)FindResource("BorderBrush");
            BasicModeCard.BorderThickness = new Thickness(1);
        }
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        UpdateModeCardStyles();
    }

    private void BasicMode_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        BasicModeRadio.IsChecked = true;
        AdvancedModeRadio.IsChecked = false;
        UpdateModeCardStyles();
    }

    private void AdvancedMode_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var result = MessageBox.Show(
            "Advanced Mode uses Windows Filtering Platform (WFP) for kernel-level control.\n\n" +
            "This mode:\n" +
            "• Requires Administrator privileges\n" +
            "• Cannot be bypassed by applications\n" +
            "• Provides the most powerful control\n\n" +
            "Do you want to enable Advanced Mode?",
            "Enable Advanced Mode",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            BasicModeRadio.IsChecked = false;
            AdvancedModeRadio.IsChecked = true;
            UpdateModeCardStyles();
        }
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Add Rule dialog will open here.\n\nYou can:\n• Select a process\n• Set download speed limit\n• Set upload speed limit\n• Block completely\n• Set schedule",
            "Add Bandwidth Rule",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is BandwidthRule rule)
        {
            MessageBox.Show(
                $"Edit rule for: {rule.ProcessName}\n\nCurrent settings:\n• Download: {rule.DownloadLimit}\n• Upload: {rule.UploadLimit}\n• Status: {rule.Status}",
                "Edit Rule",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is BandwidthRule rule)
        {
            var result = MessageBox.Show(
                $"Delete rule for {rule.ProcessName}?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                // Remove rule from list
                var rules = RulesList.ItemsSource as List<BandwidthRule>;
                rules?.Remove(rule);
                RulesList.ItemsSource = null;
                RulesList.ItemsSource = rules;
                EmptyState.Visibility = (rules?.Count ?? 0) > 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }
}

public class BandwidthRule
{
    public string ProcessName { get; set; } = string.Empty;
    public string DownloadLimit { get; set; } = "Unlimited";
    public string UploadLimit { get; set; } = "Unlimited";
    public string Status { get; set; } = "Active";
    public bool IsEnabled { get; set; } = true;
    public Brush StatusColor { get; set; } = Brushes.Gray;
    public ImageSource? Icon { get; set; }
}

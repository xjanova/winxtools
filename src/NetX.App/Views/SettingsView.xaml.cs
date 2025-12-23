using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetX.Core.Helpers;
using NetX.Core.Data;
using NetX.Core.Optimization;
using System.Threading.Tasks;

namespace NetX.App.Views;

public partial class SettingsView : Page
{
    private readonly DispatcherTimer _ramUpdateTimer;

    public SettingsView()
    {
        InitializeComponent();
        LoadSettings();
        CheckAdminStatus();
        LoadRamOptimizerSettings();

        // Update RAM stats periodically
        _ramUpdateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _ramUpdateTimer.Tick += (s, e) => UpdateRamStats();
        _ramUpdateTimer.Start();
        UpdateRamStats();

        Unloaded += (s, e) => _ramUpdateTimer.Stop();
    }

    private void LoadSettings()
    {
        try
        {
            var settings = DatabaseService.Instance.GetAllSettings();

            // Language
            var lang = settings.GetValueOrDefault("Language", "en-US");
            foreach (ComboBoxItem item in LanguageCombo.Items)
            {
                if (item.Tag?.ToString() == lang || (lang == "auto" && item.Tag?.ToString() == "en-US"))
                {
                    item.IsSelected = true;
                    break;
                }
            }

            // Refresh Rate
            var refreshRate = settings.GetValueOrDefault("RefreshRateMs", "1000");
            foreach (ComboBoxItem item in RefreshRateCombo.Items)
            {
                if (item.Tag?.ToString() == refreshRate)
                {
                    item.IsSelected = true;
                    break;
                }
            }

            // Other settings
            StartWithWindowsToggle.IsChecked = settings.GetValueOrDefault("StartWithWindows", "false") == "true";
            MinimizeToTrayToggle.IsChecked = settings.GetValueOrDefault("MinimizeToTray", "true") == "true";
            NotificationsToggle.IsChecked = settings.GetValueOrDefault("ShowNotifications", "true") == "true";
        }
        catch
        {
            // Use defaults if database fails
        }
    }

    private void CheckAdminStatus()
    {
        bool isAdmin = AdminHelper.IsRunAsAdmin();

        if (isAdmin)
        {
            AdminStatusText.Text = "Running with administrator privileges";
            AdminBadge.Background = new SolidColorBrush(Color.FromRgb(0, 255, 136));
            AdminBadgeText.Text = "Admin";
        }
        else
        {
            AdminStatusText.Text = "Running without administrator privileges (limited functionality)";
            AdminBadge.Background = new SolidColorBrush(Color.FromRgb(255, 170, 0));
            AdminBadgeText.Text = "Limited";
            AdminBadgeText.Foreground = Brushes.Black;
        }
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            var langCode = item.Tag.ToString();
            if (langCode != null)
            {
                App.ChangeLanguage(langCode);
                DatabaseService.Instance.SetSetting("Language", langCode);
            }
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button != null)
        {
            button.IsEnabled = false;
            button.Content = "Checking...";
        }

        try
        {
            var updateInfo = await UpdateChecker.Instance.CheckForUpdatesAsync();

            if (button != null)
            {
                button.IsEnabled = true;
                button.Content = FindResource("Settings_CheckUpdates") as string ?? "Check for Updates";
            }

            if (updateInfo == null)
            {
                MessageBox.Show(
                    "Unable to check for updates.\n\nPlease check your internet connection.",
                    "Update Check Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (updateInfo.IsUpdateAvailable)
            {
                var result = MessageBox.Show(
                    $"A new version is available!\n\n" +
                    $"Current version: {updateInfo.CurrentVersion}\n" +
                    $"Latest version: {updateInfo.LatestVersion}\n\n" +
                    $"Would you like to download the update?",
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes && !string.IsNullOrEmpty(updateInfo.ReleaseUrl))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = updateInfo.ReleaseUrl,
                        UseShellExecute = true
                    });
                }
            }
            else
            {
                MessageBox.Show(
                    $"WinXTools is up to date!\n\nVersion: {updateInfo.CurrentVersion}",
                    "No Updates Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            if (button != null)
            {
                button.IsEnabled = true;
                button.Content = FindResource("Settings_CheckUpdates") as string ?? "Check for Updates";
            }

            MessageBox.Show(
                $"Failed to check for updates: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Reset all settings to default values?\n\nThis will:\n• Reset language to system default\n• Clear all bandwidth rules\n• Reset all preferences\n\nThis action cannot be undone.",
            "Reset Settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                // Reset settings in database
                DatabaseService.Instance.SetSetting("Language", "auto");
                DatabaseService.Instance.SetSetting("Theme", "dark");
                DatabaseService.Instance.SetSetting("RefreshRateMs", "1000");
                DatabaseService.Instance.SetSetting("StartWithWindows", "false");
                DatabaseService.Instance.SetSetting("MinimizeToTray", "true");
                DatabaseService.Instance.SetSetting("ShowNotifications", "true");
                DatabaseService.Instance.SetSetting("BandwidthMode", "basic");
                DatabaseService.Instance.SetSetting("DataRetentionDays", "30");

                LoadSettings();

                MessageBox.Show(
                    "Settings have been reset to defaults.",
                    "Reset Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to reset settings: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    #region RAM Optimizer

    private void LoadRamOptimizerSettings()
    {
        try
        {
            var optimizer = RamOptimizer.Instance;

            // Set auto optimize toggle
            AutoOptimizeRamToggle.IsChecked = optimizer.IsAutoOptimizeEnabled;

            // Set interval combo
            foreach (ComboBoxItem item in RamIntervalCombo.Items)
            {
                if (item.Tag?.ToString() == optimizer.OptimizeIntervalMinutes.ToString())
                {
                    item.IsSelected = true;
                    break;
                }
            }

            // Set threshold combo
            foreach (ComboBoxItem item in RamThresholdCombo.Items)
            {
                if (item.Tag?.ToString() == optimizer.MemoryThresholdPercent.ToString())
                {
                    item.IsSelected = true;
                    break;
                }
            }

            // Subscribe to optimization events
            optimizer.OnOptimizationComplete += result =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (result.Success && result.MemoryFreedMB > 0)
                    {
                        RamStatusText.Text = $"Freed {result.MemoryFreedMB} MB";
                    }
                });
            };
        }
        catch { }
    }

    private void UpdateRamStats()
    {
        try
        {
            var memInfo = RamOptimizer.Instance.GetMemoryInfo();

            TotalRamText.Text = $"{memInfo.TotalMemoryMB / 1024.0:F1} GB";
            UsedRamText.Text = $"{memInfo.UsedMemoryMB / 1024.0:F1} GB";
            AvailableRamText.Text = $"{memInfo.AvailableMemoryMB / 1024.0:F1} GB";
            RamStatusText.Text = $"{memInfo.UsagePercent}% Used";

            // Change status color based on usage
            if (memInfo.UsagePercent >= 90)
                RamStatusText.Foreground = Brushes.White;
            else if (memInfo.UsagePercent >= 80)
                RamStatusText.Foreground = Brushes.White;
            else
                RamStatusText.Foreground = Brushes.White;
        }
        catch { }
    }

    private void AutoOptimizeRam_Click(object sender, RoutedEventArgs e)
    {
        RamOptimizer.Instance.IsAutoOptimizeEnabled = AutoOptimizeRamToggle.IsChecked == true;
    }

    private void RamInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RamIntervalCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            if (int.TryParse(item.Tag.ToString(), out int interval))
            {
                RamOptimizer.Instance.OptimizeIntervalMinutes = interval;
            }
        }
    }

    private void RamThreshold_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RamThresholdCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            if (int.TryParse(item.Tag.ToString(), out int threshold))
            {
                RamOptimizer.Instance.MemoryThresholdPercent = threshold;
            }
        }
    }

    private void OptimizeRamNow_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button != null)
        {
            button.IsEnabled = false;
            button.Content = "Optimizing...";
        }

        Task.Run(() =>
        {
            var result = RamOptimizer.Instance.OptimizeNow();

            Dispatcher.Invoke(() =>
            {
                if (button != null)
                {
                    button.IsEnabled = true;
                    button.Content = "Optimize RAM Now";
                }

                UpdateRamStats();

                if (result.Success)
                {
                    var message = result.MemoryFreedMB > 0
                        ? $"RAM optimization complete!\n\nFreed: {result.MemoryFreedMB} MB\nProcesses optimized: {result.ProcessesOptimized}"
                        : $"RAM optimization complete!\n\nProcesses optimized: {result.ProcessesOptimized}\nNo significant memory was freed (system is running efficiently).";

                    MessageBox.Show(message, "RAM Optimized", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        $"RAM optimization failed: {result.ErrorMessage}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            });
        });
    }

    #endregion
}

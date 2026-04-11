using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetX.Core.Helpers;
using NetX.Core.Data;
using NetX.Core.System;

namespace NetX.App.Views;

public partial class SettingsView : Page
{
    private UpdateInfo? _pendingUpdate;
    private readonly Action<int, string> _progressHandler;
    private readonly Action<string> _statusHandler;

    public SettingsView()
    {
        InitializeComponent();
        LoadSettings();
        CheckAdminStatus();
        LoadLicenseStatus();
        LoadVersionInfo();

        // Subscribe to update events
        _progressHandler = OnDownloadProgress;
        _statusHandler = OnUpdateStatus;
        AutoUpdateService.Instance.OnDownloadProgress += _progressHandler;
        AutoUpdateService.Instance.OnUpdateStatus += _statusHandler;

        Loaded += SettingsView_Loaded;
        Unloaded += SettingsView_Unloaded;
    }

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        // Re-subscribe in case we were unloaded and reloaded
    }

    private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
    {
        AutoUpdateService.Instance.OnDownloadProgress -= _progressHandler;
        AutoUpdateService.Instance.OnUpdateStatus -= _statusHandler;
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

    private void LoadLicenseStatus()
    {
        try
        {
            var savedKey = DatabaseService.Instance.GetSetting("LicenseKey");
            var status = XmanLicenseService.Instance.CachedStatus;

            if (!string.IsNullOrEmpty(savedKey) && status.IsActive)
            {
                LicenseKeyInput.Text = savedKey;
                LicenseKeyInput.IsEnabled = false;
                ActivateButton.Visibility = Visibility.Collapsed;
                DeactivateButton.Visibility = Visibility.Visible;

                LicenseStatusText.Text = $"{status.DisplayType} - {(status.ExpiresAt.HasValue ? $"Expires: {status.ExpiresAt:yyyy-MM-dd}" : "No expiration")}";
                LicenseBadgeText.Text = status.DisplayType;

                if (status.IsPremium)
                {
                    LicenseBadge.Background = new SolidColorBrush(Color.FromRgb(0, 255, 136));
                    LicenseBadgeText.Foreground = Brushes.Black;
                }
                else
                {
                    LicenseBadge.Background = new SolidColorBrush(Color.FromRgb(100, 100, 120));
                }
            }
            else if (!string.IsNullOrEmpty(savedKey))
            {
                LicenseKeyInput.Text = savedKey;
                LicenseStatusText.Text = "Validating...";
                _ = ValidateSavedKeyAsync(savedKey);
            }
            else
            {
                LicenseStatusText.Text = "No license key - using free version";
            }
        }
        catch
        {
            LicenseStatusText.Text = "Free version";
        }
    }

    private async Task ValidateSavedKeyAsync(string key)
    {
        var result = await XmanLicenseService.Instance.ValidateAsync(key);
        Dispatcher.Invoke(() =>
        {
            if (result.Success)
            {
                LoadLicenseStatus();
            }
            else
            {
                LicenseStatusText.Text = result.Message;
            }
        });
    }

    private void LoadVersionInfo()
    {
        var version = AutoUpdateService.GetCurrentVersion();
        VersionText.Text = $"Version {version}";
        UpdateVersionText.Text = $"Current: v{version}";
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

    #region License

    private async void ActivateLicense_Click(object sender, RoutedEventArgs e)
    {
        var key = LicenseKeyInput.Text.Trim();
        if (string.IsNullOrEmpty(key))
        {
            MessageBox.Show("Please enter a license key.", "License", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ActivateButton.IsEnabled = false;
        ActivateButton.Content = "Activating...";

        try
        {
            var result = await XmanLicenseService.Instance.ActivateAsync(key);

            if (result.Success)
            {
                DatabaseService.Instance.SetSetting("LicenseKey", key);
                LoadLicenseStatus();
                MessageBox.Show(result.Message, "License Activated", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(result.Message, "Activation Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Activation error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ActivateButton.IsEnabled = true;
            ActivateButton.Content = FindResource("Settings_Activate") as string ?? "Activate";
        }
    }

    private async void DeactivateLicense_Click(object sender, RoutedEventArgs e)
    {
        var confirmResult = MessageBox.Show(
            "Deactivate this license from this machine?\n\nYou can reactivate it later.",
            "Deactivate License",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmResult != MessageBoxResult.Yes) return;

        var savedKey = DatabaseService.Instance.GetSetting("LicenseKey");
        if (string.IsNullOrEmpty(savedKey)) return;

        var result = await XmanLicenseService.Instance.DeactivateAsync(savedKey);

        DatabaseService.Instance.SetSetting("LicenseKey", "");
        LicenseKeyInput.Text = "";
        LicenseKeyInput.IsEnabled = true;
        ActivateButton.Visibility = Visibility.Visible;
        DeactivateButton.Visibility = Visibility.Collapsed;
        LicenseStatusText.Text = "No license key - using free version";
        LicenseBadgeText.Text = "Free";
        LicenseBadge.Background = new SolidColorBrush(Color.FromRgb(100, 100, 120));
        LicenseBadgeText.Foreground = (Brush)FindResource("TextSecondaryBrush");

        MessageBox.Show(result.Message, "License", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    #endregion

    #region Updates

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "Checking...";

        try
        {
            var updateInfo = await AutoUpdateService.Instance.CheckForUpdatesAsync();

            if (updateInfo == null)
            {
                UpdateStatusText.Text = "Unable to check for updates";
                MessageBox.Show(
                    "Unable to check for updates.\n\nPlease check your internet connection.",
                    "Update Check Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (updateInfo.IsUpdateAvailable)
            {
                _pendingUpdate = updateInfo;
                UpdateStatusText.Text = $"Update available: v{updateInfo.LatestVersion}";
                UpdateVersionText.Text = $"Current: v{updateInfo.CurrentVersion} -> New: v{updateInfo.LatestVersion}";
                UpdateNowButton.Visibility = Visibility.Visible;

                var result = MessageBox.Show(
                    $"A new version is available!\n\n" +
                    $"Current: v{updateInfo.CurrentVersion}\n" +
                    $"New: v{updateInfo.LatestVersion}\n\n" +
                    $"Would you like to download and install the update?",
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    await StartUpdateAsync(updateInfo);
                }
            }
            else
            {
                UpdateStatusText.Text = FindResource("Settings_UpToDate") as string ?? "WinXTools is up to date";
                MessageBox.Show(
                    $"WinXTools is up to date!\n\nVersion: v{updateInfo.CurrentVersion}",
                    "No Updates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to check for updates: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            CheckUpdateButton.Content = FindResource("Settings_CheckUpdates") as string ?? "Check for Updates";
        }
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate != null)
        {
            await StartUpdateAsync(_pendingUpdate);
        }
    }

    private async Task StartUpdateAsync(UpdateInfo updateInfo)
    {
        UpdateNowButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        DownloadProgressPanel.Visibility = Visibility.Visible;

        var success = await AutoUpdateService.Instance.DownloadAndInstallAsync(updateInfo);

        if (success)
        {
            // App will close and restart via batch script
            Application.Current.Shutdown();
        }
        else
        {
            UpdateNowButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
            DownloadProgressPanel.Visibility = Visibility.Collapsed;

            MessageBox.Show(
                "Update download failed. You can download manually from GitHub.",
                "Update Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            // Offer manual download
            if (!string.IsNullOrEmpty(updateInfo.ReleaseUrl))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = updateInfo.ReleaseUrl,
                    UseShellExecute = true
                });
            }
        }
    }

    private void OnDownloadProgress(int percent, string status)
    {
        Dispatcher.Invoke(() =>
        {
            DownloadProgressBar.Value = percent;
            DownloadProgressText.Text = status;
        });
    }

    private void OnUpdateStatus(string status)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateStatusText.Text = status;
        });
    }

    #endregion

    #region Settings

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Reset all settings to default values?\n\nThis will:\n- Reset language to system default\n- Clear all bandwidth rules\n- Reset all preferences\n\nNote: License key will NOT be removed.\n\nThis action cannot be undone.",
            "Reset Settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
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

    private void OpenRamOptimizer_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.NavigateToRamOptimizer();
        }
    }

    #endregion
}

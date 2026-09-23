using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using NetX.App.Helpers;
using NetX.Core.Helpers;
using NetX.Core.Data;
using NetX.Core.System;

namespace NetX.App.Views;

public partial class SettingsView : Page
{
    // Saved UI language: "auto" (follow Windows), "en-US" or "th-TH".
    private const string LanguageSettingKey = "Language";
    // Mirror of the real Task Scheduler state, only used to pre-fill the toggle.
    private const string StartWithWindowsSettingKey = "StartWithWindows";

    private enum UpdateCheckState { NotChecked, UpToDate, Available, CheckFailed }

    private UpdateInfo? _pendingUpdate;
    private UpdateInfo? _installOnLoad; // set when the "update now?" prompt at startup sent the user here
    private UpdateCheckState _checkState = UpdateCheckState.NotChecked;
    private bool _isLoading = true; // true while the page fills its controls from saved settings
    private bool _updateEventsHooked;
    private bool _licenseBusy;

    public SettingsView(UpdateInfo? installUpdate = null)
    {
        InitializeComponent();
        _installOnLoad = installUpdate;
        LoadSettings();
        CheckAdminStatus();
        RenderLicense();
        LoadVersionInfo();
        _isLoading = false;

        Loaded += SettingsView_Loaded;
        Unloaded += SettingsView_Unloaded;
    }

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_updateEventsHooked)
        {
            AutoUpdateService.Instance.OnDownloadProgress += OnDownloadProgress;
            AutoUpdateService.Instance.OnStageChanged += OnUpdateStageChanged;
            XmanLicenseService.Instance.StatusChanged += OnLicenseStatusChanged;
            TrialService.Instance.OnTrialStatusChanged += OnTrialStatusChanged;
            _updateEventsHooked = true;
        }

        // An update started on an earlier visit to this page may still be downloading.
        SetUpdateBusy(AutoUpdateService.Instance.IsInstalling);
        RenderLicense();

        _ = RefreshStartWithWindowsAsync();

        if (_installOnLoad is { } update)
        {
            _installOnLoad = null;
            ApplyCheckResult(update);
            if (!AutoUpdateService.Instance.IsInstalling) _ = StartUpdateAsync(update);
        }
    }

    private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_updateEventsHooked) return;
        AutoUpdateService.Instance.OnDownloadProgress -= OnDownloadProgress;
        AutoUpdateService.Instance.OnStageChanged -= OnUpdateStageChanged;
        XmanLicenseService.Instance.StatusChanged -= OnLicenseStatusChanged;
        TrialService.Instance.OnTrialStatusChanged -= OnTrialStatusChanged;
        _updateEventsHooked = false;
    }

    // Both raised on worker threads.
    private void OnLicenseStatusChanged(LicenseStatus _) => Dispatcher.BeginInvoke(RenderLicense);
    private void OnTrialStatusChanged() => Dispatcher.BeginInvoke(RenderLicense);

    private void LoadSettings()
    {
        // Show the language actually in use; a saved "auto" follows the Windows display language.
        SelectLanguage(GetAppliedLanguage());

        try
        {
            // Last known state as a placeholder; RefreshStartWithWindowsAsync replaces it
            // with what Task Scheduler really has.
            StartWithWindowsToggle.IsChecked =
                DatabaseService.Instance.GetSetting(StartWithWindowsSettingKey) == "true";
        }
        catch (Exception ex)
        {
            // Use defaults if database fails
            Debug.WriteLine($"Loading settings failed: {ex.Message}");
        }
    }

    private void LoadVersionInfo()
    {
        // Reuse a check made elsewhere (e.g. the one at startup) instead of "not checked yet".
        var lastCheck = AutoUpdateService.Instance.LastCheck;
        if (lastCheck != null)
            ApplyCheckResult(lastCheck);
        else
            RenderUpdateTexts();
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

    // Localized text for code-behind; the English fallback shows until the key exists.
    private static string Res(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as string ?? fallback;

    private static string ResF(string key, string fallback, params object[] args)
    {
        try
        {
            return string.Format(Res(key, fallback), args);
        }
        catch (FormatException)
        {
            return string.Format(fallback, args);
        }
    }

    #region Language

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Selections the page makes while filling itself in are not user choices.
        if (_isLoading || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string langCode }) return;

        App.ChangeLanguage(langCode);
        RenderUpdateTexts();
        RenderLicense();

        try
        {
            DatabaseService.Instance.SetSetting(LanguageSettingKey, langCode);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Saving language failed: {ex.Message}");
            MessageBox.Show(
                Res("Settings_SaveFailed", "The language was changed, but it could not be saved. WinXTools may start in the previous language next time."),
                Res("Settings_Language", "Language"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SelectLanguage(string langCode)
    {
        bool wasLoading = _isLoading;
        _isLoading = true;
        try
        {
            LanguageCombo.SelectedItem = LanguageCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag as string == langCode);
        }
        finally
        {
            _isLoading = wasLoading;
        }
    }

    // The language dictionary App.ChangeLanguage merged last.
    private static string GetAppliedLanguage()
    {
        var dictionary = Application.Current.Resources.MergedDictionaries
            .LastOrDefault(d => d.Source?.OriginalString.Contains("Languages/") == true);
        return dictionary?.Source?.OriginalString.Contains("th-TH") == true ? "th-TH" : "en-US";
    }

    // Same rule as App.InitializeLanguage: Thai Windows gets Thai, anything else English.
    private static string GetSystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "th" ? "th-TH" : "en-US";

    #endregion

    #region Start with Windows

    private async Task RefreshStartWithWindowsAsync()
    {
        SetStartupBusy(true);
        try
        {
            var enabled = await StartupTask.IsEnabledAsync();
            if (enabled.HasValue)
            {
                StartWithWindowsToggle.IsChecked = enabled.Value;
                SaveStartWithWindows(enabled.Value);
            }
        }
        finally
        {
            SetStartupBusy(false);
        }
    }

    private async void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        bool enable = StartWithWindowsToggle.IsChecked == true;
        SetStartupBusy(true);

        try
        {
            var result = enable ? await StartupTask.EnableAsync() : await StartupTask.DisableAsync();

            // Show what Task Scheduler really has now, not what was asked for.
            bool actual = await StartupTask.IsEnabledAsync()
                ?? (result == StartupTask.Result.Done ? enable : !enable);
            StartWithWindowsToggle.IsChecked = actual;
            SaveStartWithWindows(actual);

            if (result == StartupTask.Result.UnsafeLocation)
            {
                MessageBox.Show(
                    ResF("Settings_StartupUnsafeFolder",
                        "WinXTools can only start with Windows from a folder that only administrators can change, such as C:\\Program Files\\WinXTools.\n\nIt is running from:\n{0}\n\nMove the WinXTools folder there, then turn this on again.",
                        Path.GetDirectoryName(Environment.ProcessPath) ?? ""),
                    Res("Settings_StartWithWindows", "Start with Windows"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else if (result != StartupTask.Result.Done || actual != enable)
            {
                ShowStartupFailed();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Start with Windows failed: {ex.Message}");
            StartWithWindowsToggle.IsChecked = !enable;
            ShowStartupFailed();
        }
        finally
        {
            SetStartupBusy(false);
        }
    }

    private static void ShowStartupFailed() =>
        MessageBox.Show(
            Res("Settings_StartupFailed", "Windows Task Scheduler did not accept the change, so Start with Windows was not changed."),
            Res("Settings_StartWithWindows", "Start with Windows"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    // One Task Scheduler change at a time: the toggle and Reset wait for each other.
    private void SetStartupBusy(bool busy)
    {
        StartWithWindowsToggle.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy;
    }

    private static void SaveStartWithWindows(bool enabled)
    {
        try
        {
            DatabaseService.Instance.SetSetting(StartWithWindowsSettingKey, enabled ? "true" : "false");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Saving StartWithWindows failed: {ex.Message}");
        }
    }

    #endregion

    #region License

    // Shows the saved license (or the trial) as it is now. Called again whenever the license
    // service or the trial changes, so the page never shows a state that is no longer true.
    private void RenderLicense()
    {
        var status = XmanLicenseService.Instance.CachedStatus;
        var trial = TrialService.Instance;
        string text;
        string? detail = null;
        string badge;
        var tone = BadgeTone.Neutral;

        if (status.IsPremium)
        {
            text = status.IsLifetime
                ? Res("License_ProLifetime", "WinXTools Pro — lifetime license, no expiry")
                : ResF("License_ProUntil", "WinXTools Pro — valid until {0}", FormatDate(status.ExpiresAt));
            if (status.IsFromSavedCopy && status.VerifiedAtUtc is { } verified)
            {
                detail = ResF("License_OfflineSince",
                    "Not confirmed online yet this session (last confirmed {0}). Pro keeps working for up to {1} days without internet.",
                    FormatDate(verified), (int)XmanLicenseService.OfflineGrace.TotalDays);
            }
            badge = "PRO";
            tone = BadgeTone.Good;
        }
        else
        {
            switch (status.State)
            {
                case LicenseState.Active:
                case LicenseState.Expired:
                    text = status.ExpiresAt is { } ended
                        ? ResF("License_ExpiredOn", "Your Pro license expired on {0}.", FormatDate(ended))
                        : Res("License_Expired", "Your Pro license has expired.");
                    detail = Res("License_ExpiredHelp", "Buy Pro again to keep using the Pro features. The free features keep working.");
                    badge = Res("License_BadgeExpired", "EXPIRED");
                    tone = BadgeTone.Warning;
                    break;
                case LicenseState.Revoked:
                    text = Res("License_Revoked", "This license was cancelled by xman studio.");
                    detail = Res("License_RevokedHelp", "If you think this is a mistake, contact xman studio support with your license key.");
                    badge = Res("License_BadgeRevoked", "CANCELLED");
                    tone = BadgeTone.Bad;
                    break;
                case LicenseState.OtherMachine:
                    text = Res("License_OtherMachine", "This license is now activated on another PC.");
                    detail = Res("License_OtherMachineHelp", "Press Activate to move it back to this PC (Pro then stops on the other PC).");
                    badge = "FREE";
                    tone = BadgeTone.Warning;
                    break;
                case LicenseState.Unverified:
                    text = Res("License_Unverified", "Your license could not be confirmed.");
                    detail = ResF("License_UnverifiedHelp",
                        "WinXTools needs to reach the license server at least once every {0} days. Connect to the internet and press Check again.",
                        (int)XmanLicenseService.OfflineGrace.TotalDays);
                    badge = "?";
                    tone = BadgeTone.Warning;
                    break;
                default:
                    if (trial.IsTrialActive)
                    {
                        text = trial.ExpiresAtUtc is { } end
                            ? ResF("License_TrialUntil", "Free Pro trial — every feature unlocked until {0}", FormatDateTime(end))
                            : Res("Pro_TrialDesc", "All features unlocked");
                        detail = Res("License_TrialHelp", "After the trial the free features keep working. Buy Pro (฿199, one-time) to keep everything.");
                        badge = Res("License_BadgeTrial", "TRIAL");
                        tone = BadgeTone.Good;
                    }
                    else if (trial.TrialExpired || trial.TrialUnavailable)
                    {
                        text = Res("License_FreeTrialUsed", "Free version — the Pro trial on this PC has been used.");
                        badge = "FREE";
                    }
                    else
                    {
                        text = Res("License_Free", "Free version");
                        badge = "FREE";
                    }
                    break;
            }
        }

        LicenseStatusText.Text = text;
        LicenseDetailText.Text = detail ?? "";
        LicenseDetailText.Visibility = detail == null ? Visibility.Collapsed : Visibility.Visible;
        LicenseBadgeText.Text = badge;
        ApplyBadgeTone(tone);

        bool hasKey = !string.IsNullOrEmpty(status.LicenseKey);
        bool activeHere = status.IsPremium || status.State == LicenseState.Unverified;

        // Show the saved key, but never wipe a key the user is typing or has typed.
        if (activeHere || (hasKey && !LicenseKeyInput.IsKeyboardFocusWithin && LicenseKeyInput.Text.Length == 0))
            LicenseKeyInput.Text = status.LicenseKey;
        LicenseKeyInput.IsEnabled = !_licenseBusy && !activeHere;
        ActivateButton.Visibility = activeHere ? Visibility.Collapsed : Visibility.Visible;
        ActivateButton.IsEnabled = !_licenseBusy;
        DeactivateButton.Visibility = activeHere ? Visibility.Visible : Visibility.Collapsed;
        DeactivateButton.IsEnabled = !_licenseBusy;
        RefreshLicenseButton.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        RefreshLicenseButton.IsEnabled = !_licenseBusy;
        BuyProButton.Visibility = status.IsPremium ? Visibility.Collapsed : Visibility.Visible;
    }

    private enum BadgeTone { Neutral, Good, Warning, Bad }

    private void ApplyBadgeTone(BadgeTone tone)
    {
        (Color back, Brush fore) = tone switch
        {
            BadgeTone.Good => (Color.FromRgb(0, 255, 136), Brushes.Black),
            BadgeTone.Warning => (Color.FromRgb(255, 170, 0), Brushes.Black),
            BadgeTone.Bad => (Color.FromRgb(255, 82, 82), Brushes.White),
            _ => (Color.FromRgb(100, 100, 120), (Brush)FindResource("TextSecondaryBrush"))
        };
        LicenseBadge.Background = new SolidColorBrush(back);
        LicenseBadgeText.Foreground = fore;
    }

    private static string FormatDate(DateTime? utc) =>
        utc is { } value ? value.ToLocalTime().ToString("d MMM yyyy", CultureForDates()) : "-";

    private static string FormatDateTime(DateTime utc) =>
        utc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureForDates());

    // Thai UI gets Thai month names (and the Buddhist year Thai users expect).
    private static CultureInfo CultureForDates() =>
        Loc.IsThai ? CultureInfo.GetCultureInfo("th-TH") : CultureInfo.GetCultureInfo("en-GB");

    private void SetLicenseBusy(bool busy, string? activateText = null)
    {
        _licenseBusy = busy;
        if (busy && activateText != null)
            ActivateButton.Content = activateText;
        else
            // Back to the dynamic resource so a later language switch still updates it.
            ActivateButton.SetResourceReference(ContentControl.ContentProperty, "Settings_Activate");
        RenderLicense();
    }

    private void LicenseKeyInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && ActivateButton.IsVisible && ActivateButton.IsEnabled)
        {
            e.Handled = true;
            ActivateLicense_Click(sender, e);
        }
    }

    private async void ActivateLicense_Click(object sender, RoutedEventArgs e)
    {
        if (_licenseBusy) return;

        var key = XmanLicenseService.NormalizeKey(LicenseKeyInput.Text);
        if (key.Length == 0)
        {
            ShowLicenseMessage(Res("License_EnterKey", "Please enter your license key. You get it by email (and in your xman studio account) after buying Pro."), MessageBoxImage.Warning);
            LicenseKeyInput.Focus();
            return;
        }
        if (!XmanLicenseService.LooksLikeKey(key))
        {
            ShowLicenseMessage(Res("License_KeyFormat", "That doesn't look like a license key. A key looks like ABCD-1234-EFGH-5678 (letters, numbers and dashes)."), MessageBoxImage.Warning);
            LicenseKeyInput.Focus();
            return;
        }

        SetLicenseBusy(true, Res("Settings_Activating", "Activating..."));
        try
        {
            bool move = false;
            while (true)
            {
                var result = await XmanLicenseService.Instance.ActivateAsync(key, move);

                if (result.Success)
                {
                    ShowLicenseMessage(Res("License_Activated", "WinXTools Pro is now active on this PC. Thank you for your support!"), MessageBoxImage.Information);
                    break;
                }

                if (result.Code == LicenseCode.OtherDevice && !move)
                {
                    var answer = MessageBox.Show(
                        Res("License_MoveConfirm", "This license is already activated on another PC.\n\nMove it to this PC? WinXTools Pro will stop working on the other PC (you can move it back later the same way)."),
                        Res("Settings_License", "License"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question,
                        MessageBoxResult.No);
                    if (answer == MessageBoxResult.Yes)
                    {
                        move = true;
                        continue;
                    }
                    break;
                }

                ShowLicenseMessage(DescribeLicenseFailure(result), MessageBoxImage.Warning);
                break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Activation failed: {ex}");
            ShowLicenseMessage(Res("License_Failed", "The license could not be activated. Please try again."), MessageBoxImage.Error);
        }
        finally
        {
            SetLicenseBusy(false);
        }
    }

    private async void DeactivateLicense_Click(object sender, RoutedEventArgs e)
    {
        if (_licenseBusy) return;

        var confirm = MessageBox.Show(
            Res("License_DeactivateConfirm", "Remove the license from this PC?\n\nPro stops on this PC and the key becomes free to activate on another PC. You can activate it here again later."),
            Res("Settings_Deactivate", "Deactivate"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        var key = XmanLicenseService.Instance.CachedStatus.LicenseKey;
        SetLicenseBusy(true);
        try
        {
            var result = await XmanLicenseService.Instance.DeactivateAsync();
            if (result.Success)
            {
                ShowLicenseMessage(ResF("License_Deactivated", "The license was removed from this PC. Keep your key to activate it again:\n\n{0}", key), MessageBoxImage.Information);
            }
            else
            {
                ShowLicenseMessage(result.Code switch
                {
                    LicenseCode.Offline or LicenseCode.ServerBusy =>
                        Res("License_DeactivateOffline", "The license server can't be reached, so the license is still registered to this PC. Nothing was changed — please try again when you're online."),
                    LicenseCode.Failed =>
                        Res("License_DeactivateFailed", "The license could not be removed from this PC. Nothing was changed — please try again."),
                    _ => DescribeLicenseFailure(result)
                }, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Deactivation failed: {ex}");
            ShowLicenseMessage(Res("License_DeactivateFailed", "The license could not be removed from this PC. Nothing was changed — please try again."), MessageBoxImage.Error);
        }
        finally
        {
            SetLicenseBusy(false);
        }
    }

    private async void RefreshLicense_Click(object sender, RoutedEventArgs e)
    {
        if (_licenseBusy) return;
        SetLicenseBusy(true);
        try
        {
            var result = await XmanLicenseService.Instance.RefreshAsync();
            ShowLicenseMessage(result.Success
                ? Res("License_Confirmed", "The license server confirmed your license.")
                : DescribeLicenseFailure(result),
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"License refresh failed: {ex}");
            ShowLicenseMessage(Res("License_ErrBusy", "The license server is busy or under maintenance. Please try again in a few minutes."), MessageBoxImage.Warning);
        }
        finally
        {
            SetLicenseBusy(false);
        }
    }

    private void BuyPro_Click(object sender, RoutedEventArgs e) => OpenPurchasePage();

    /// <summary>Opens the WinXTools page on xman4289.com in the user's browser (not elevated).</summary>
    public static void OpenPurchasePage()
    {
        var opened = NetX.Core.System.Tweaks.ShellLauncher.OpenUnelevated(XmanApi.ProductPageUrl);
        if (!opened.Success)
        {
            MessageBox.Show(
                ResF("License_OpenPageFailed", "The browser could not be opened. Please visit:\n{0}", XmanApi.ProductPageUrl),
                Res("Settings_License", "License"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static string DescribeLicenseFailure(LicenseResult result)
    {
        var text = result.Code switch
        {
            LicenseCode.InvalidKey => Res("License_ErrInvalidKey", "This key is not a WinXTools license. Check it for typos — keys bought for other xman studio apps do not work in WinXTools."),
            LicenseCode.Expired => Res("License_ErrExpired", "This license has expired. Buy Pro again to keep the Pro features."),
            LicenseCode.Revoked => Res("License_ErrRevoked", "This license was cancelled by xman studio. Contact support if you think this is a mistake."),
            LicenseCode.OtherDevice => Res("License_ErrOtherDevice", "This license stays on the other PC. Nothing was changed."),
            LicenseCode.NotOnThisMachine => Res("License_OtherMachine", "This license is now activated on another PC."),
            LicenseCode.NotProKey => Res("License_ErrTrialKey", "This is a trial key, not a Pro license. Buy WinXTools Pro to get a license key."),
            LicenseCode.NoLicense => Res("License_ErrNoLicense", "The license server has no license for this PC."),
            LicenseCode.InvalidInput => Res("License_KeyFormat", "That doesn't look like a license key. A key looks like ABCD-1234-EFGH-5678 (letters, numbers and dashes)."),
            LicenseCode.Offline => Res("License_ErrOffline", "Can't reach the license server. Check your internet connection (and that no firewall or hosts entry blocks xman4289.com), then try again."),
            LicenseCode.ServerBusy => Res("License_ErrBusy", "The license server is busy or under maintenance. Please try again in a few minutes."),
            _ => Res("License_Failed", "The license could not be activated. Please try again.")
        };

        // The server explains itself in Thai; that extra detail only helps a Thai reader.
        if (Loc.IsThai && result.Code == LicenseCode.Failed && !string.IsNullOrWhiteSpace(result.ServerMessage))
            text += "\n\n" + result.ServerMessage;
        return text;
    }

    private static void ShowLicenseMessage(string message, MessageBoxImage icon) =>
        MessageBox.Show(message, Res("Settings_License", "License"), MessageBoxButton.OK, icon);

    #endregion

    #region Updates

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = Res("Settings_Checking", "Checking...");

        try
        {
            var updateInfo = await AutoUpdateService.Instance.CheckForUpdatesAsync();
            ApplyCheckResult(updateInfo);

            if (updateInfo == null)
            {
                // Neither xman studio nor GitHub answered: never report "up to date" here.
                MessageBox.Show(
                    Res("Settings_UpdateCheckFailed", "Couldn't check for updates.\n\nPlease check your internet connection and try again."),
                    Res("Settings_Updates", "Updates"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (updateInfo.IsUpdateAvailable)
            {
                var result = MessageBox.Show(
                    ResF("Settings_UpdatePrompt",
                        "A new version of WinXTools is available.\n\nCurrent: v{0}\nNew: v{1}\n\nDownload and install it now? WinXTools will close and restart.",
                        updateInfo.CurrentVersion, updateInfo.LatestVersion),
                    Res("Settings_Updates", "Updates"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    await StartUpdateAsync(updateInfo);
                }
            }
            else
            {
                MessageBox.Show(
                    Res("Settings_UpToDate", "WinXTools is up to date") + "\n\n"
                        + ResF("Settings_VersionFormat", "Version {0}", updateInfo.CurrentVersion),
                    Res("Settings_Updates", "Updates"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex}");
            MessageBox.Show(
                Res("Settings_UpdateCheckFailed", "Couldn't check for updates.\n\nPlease check your internet connection and try again."),
                Res("Settings_Updates", "Updates"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = !AutoUpdateService.Instance.IsInstalling;
            // Back to the dynamic resource so a later language switch still updates it.
            CheckUpdateButton.SetResourceReference(ContentControl.ContentProperty, "Settings_CheckUpdates");
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
        SetUpdateBusy(true);

        var result = await AutoUpdateService.Instance.DownloadAndInstallAsync(updateInfo);

        if (result == UpdateInstallResult.Started)
        {
            // The installer waits for this process to exit, copies the files and restarts WinXTools.
            Application.Current.Shutdown();
            return;
        }

        // An install that is already running keeps the progress UI.
        if (result == UpdateInstallResult.AlreadyRunning) return;

        SetUpdateBusy(false);
        ShowUpdateFailure(result, updateInfo);
    }

    private void ShowUpdateFailure(UpdateInstallResult result, UpdateInfo updateInfo)
    {
        var message = result switch
        {
            UpdateInstallResult.NoDirectDownload or UpdateInstallResult.NotAPackage =>
                Res("Settings_UpdateErrManual", "This update can't be downloaded automatically (the download may require signing in on the website). Nothing was installed."),
            UpdateInstallResult.DownloadFailed =>
                Res("Settings_UpdateErrDownload", "The update could not be downloaded. Please check your internet connection and try again."),
            UpdateInstallResult.VerificationFailed =>
                Res("Settings_UpdateErrVerify", "The downloaded file did not match what the server announced, so it was deleted and nothing was installed."),
            UpdateInstallResult.InvalidPackage =>
                Res("Settings_UpdateErrInvalid", "The downloaded file is not a valid WinXTools update. Nothing was installed."),
            UpdateInstallResult.NotSigned =>
                Res("Settings_UpdateErrNotSigned", "This update is not signed by xman studio, so it was not installed. Nothing was changed."),
            UpdateInstallResult.SignatureInvalid =>
                Res("Settings_UpdateErrSignature", "The update failed the signature check — it may have been altered on the way. It was deleted and nothing was installed."),
            UpdateInstallResult.FolderNotSecure =>
                Res("Settings_UpdateErrFolder", "WinXTools could not prepare a protected folder for the update, so nothing was installed."),
            _ =>
                Res("Settings_UpdateErrGeneric", "The update could not be installed. Nothing was changed.")
        };

        bool canOpenPage = Uri.TryCreate(updateInfo.ReleaseUrl, UriKind.Absolute, out var page)
            && page.Scheme == Uri.UriSchemeHttps;
        if (canOpenPage)
        {
            message += "\n\n" + Res("Settings_UpdateOpenPage", "Open the download page in your browser?");
        }

        var answer = MessageBox.Show(
            message,
            Res("Settings_UpdateFailedTitle", "Update not installed"),
            canOpenPage ? MessageBoxButton.YesNo : MessageBoxButton.OK,
            MessageBoxImage.Warning);

        if (canOpenPage && answer == MessageBoxResult.Yes)
        {
            OpenInBrowser(page!);
        }
    }

    // Through Explorer, so the browser runs as the signed-in user instead of elevated like this app.
    private static void OpenInBrowser(Uri link)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { link.AbsoluteUri } });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Opening {link} failed: {ex.Message}");
        }
    }

    private void ApplyCheckResult(UpdateInfo? info)
    {
        _pendingUpdate = info is { IsUpdateAvailable: true } ? info : null;
        _checkState = info == null ? UpdateCheckState.CheckFailed
            : info.IsUpdateAvailable ? UpdateCheckState.Available
            : UpdateCheckState.UpToDate;

        UpdateNowButton.Visibility = _pendingUpdate != null ? Visibility.Visible : Visibility.Collapsed;
        RenderUpdateTexts();
    }

    private void SetUpdateBusy(bool busy)
    {
        CheckUpdateButton.IsEnabled = !busy;
        UpdateNowButton.IsEnabled = !busy;
        DownloadProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            // Until the first progress report arrives.
            DownloadProgressBar.IsIndeterminate = true;
            DownloadProgressText.Text = "";
        }
        RenderUpdateTexts();
    }

    // Built from state each time, so switching language re-renders these texts too.
    private void RenderUpdateTexts()
    {
        var version = AutoUpdateService.GetCurrentVersion();
        VersionText.Text = ResF("Settings_VersionFormat", "Version {0}", version);
        UpdateVersionText.Text = _pendingUpdate != null
            ? ResF("Settings_VersionChange", "Current: v{0} → New: v{1}", version, _pendingUpdate.LatestVersion)
            : ResF("Settings_CurrentVersion", "Current: v{0}", version);

        var updater = AutoUpdateService.Instance;
        UpdateStatusText.Text = updater.IsInstalling
            ? updater.Stage switch
            {
                UpdateStage.Preparing => Res("Settings_UpdatePreparing", "Checking and preparing the update..."),
                UpdateStage.Restarting => Res("Settings_UpdateRestarting", "Restarting WinXTools to finish the update..."),
                _ => Res("Settings_Downloading", "Downloading update...")
            }
            : _checkState switch
            {
                UpdateCheckState.UpToDate => Res("Settings_UpToDate", "WinXTools is up to date"),
                UpdateCheckState.Available => ResF("Settings_UpdateAvailable", "Update available: v{0}", _pendingUpdate?.LatestVersion ?? ""),
                UpdateCheckState.CheckFailed => Res("Settings_UpdateCheckFailedShort", "Couldn't check for updates"),
                _ => Res("Settings_UpdateNotChecked", "Updates not checked yet")
            };
    }

    // Raised on a worker thread.
    private void OnDownloadProgress(long downloaded, long total)
    {
        Dispatcher.InvokeAsync(() =>
        {
            DownloadProgressBar.IsIndeterminate = total <= 0;
            if (total > 0) DownloadProgressBar.Value = Math.Min(100, downloaded * 100.0 / total);
            DownloadProgressText.Text = total > 0
                ? ResF("Settings_DownloadProgress", "Downloaded {0:0.0} MB of {1:0.0} MB", downloaded / 1048576.0, total / 1048576.0)
                : ResF("Settings_DownloadProgressUnknown", "Downloaded {0:0.0} MB", downloaded / 1048576.0);
        });
    }

    // Raised on a worker thread.
    private void OnUpdateStageChanged(UpdateStage stage)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (stage == UpdateStage.Idle)
            {
                SetUpdateBusy(false);
                return;
            }
            if (stage != UpdateStage.Downloading)
            {
                DownloadProgressBar.IsIndeterminate = true;
                DownloadProgressText.Text = "";
            }
            RenderUpdateTexts();
        });
    }

    #endregion

    #region Settings

    private async void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            Res("Settings_ResetConfirm", "Reset settings to their defaults?\n\n• Language: follow the Windows display language\n• Start with Windows: off\n\nYour license key is kept."),
            Res("Settings_ResetSettings", "Reset Settings"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        SetStartupBusy(true);
        try
        {
            // Language: back to "auto" and applied right away.
            DatabaseService.Instance.SetSetting(LanguageSettingKey, "auto");
            var systemLanguage = GetSystemLanguage();
            App.ChangeLanguage(systemLanguage);
            SelectLanguage(systemLanguage);
            RenderUpdateTexts();
            RenderLicense();

            // Start with Windows: off (removes the logon task).
            var startup = await StartupTask.DisableAsync();
            bool stillOn = await StartupTask.IsEnabledAsync() ?? false;
            StartWithWindowsToggle.IsChecked = stillOn;
            SaveStartWithWindows(stillOn);

            // Bandwidth limits and blocks are not touched. If Reset should clear them too,
            // this is the place: await BandwidthLimiter.Instance.ResetAllAsync(); (and add it
            // to the Settings_ResetConfirm text).

            bool complete = startup == StartupTask.Result.Done;
            MessageBox.Show(
                complete
                    ? Res("Settings_ResetDone", "Settings have been reset to their defaults.")
                    : Res("Settings_ResetPartial", "The language was reset, but Start with Windows could not be turned off. Please try again."),
                Res("Settings_ResetSettings", "Reset Settings"),
                MessageBoxButton.OK,
                complete ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reset settings failed: {ex.Message}");
            MessageBox.Show(
                Res("Settings_ResetFailed", "The settings could not be reset. Please try again."),
                Res("Settings_ResetSettings", "Reset Settings"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetStartupBusy(false);
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

/// <summary>
/// "Start with Windows" as a Task Scheduler logon task. WinXTools requires admin, so a Run key
/// would raise a UAC prompt at every sign-in; a task with the highest run level starts it
/// elevated without one. Because that runs the exe elevated with no prompt, the task is only
/// created while the exe is in a folder that only administrators can change.
/// </summary>
internal static class StartupTask
{
    public enum Result { Done, UnsafeLocation, Failed }

    private const string TaskName = "WinXTools";

    private static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    // The running exe with links resolved, so the task and its marker use one canonical path.
    private static string? ExePath =>
        Environment.ProcessPath is { } path ? AdminOnlyLocation.GetFinalPath(path) : null;

    /// <summary>True/false = this exe's task exists or not; null = Task Scheduler could not be asked.</summary>
    public static async Task<bool?> IsEnabledAsync()
    {
        var exe = ExePath;
        if (exe == null) return null;

        var (exitCode, output) = await RunAsync("/Query", "/TN", TaskName, "/XML");
        if (exitCode == null) return null;

        // Our tasks carry a hash of the exe path, so a task left behind by a copy in another
        // folder (which would start that copy) does not show as "on" here.
        return exitCode == 0 && output.Contains(Marker(exe), StringComparison.Ordinal);
    }

    public static Task<Result> EnableAsync() => Task.Run(async () =>
    {
        var exe = ExePath;
        var userSid = WindowsIdentity.GetCurrent().User?.Value;
        if (exe == null || userSid == null) return Result.Failed;

        // The task runs this exe elevated with no prompt at every sign-in. If a normal-user
        // process could replace the exe, that would hand it administrator rights.
        if (!AdminOnlyLocation.IsAdminOnlyPath(exe)) return Result.UnsafeLocation;

        // schtasks reads the definition from a file, so keep it where only admins can write
        // (a swapped file would register any command to run elevated at every sign-in).
        var folder = AdminOnlyLocation.CreateFreshFolder("Startup");
        if (folder == null) return Result.Failed;

        try
        {
            var xmlPath = Path.Combine(folder, "task.xml");
            WriteTaskXml(xmlPath, exe, userSid);
            var (exitCode, _) = await RunAsync("/Create", "/TN", TaskName, "/XML", xmlPath, "/F");
            return exitCode == 0 ? Result.Done : Result.Failed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Creating the startup task failed: {ex.Message}");
            return Result.Failed;
        }
        finally
        {
            AdminOnlyLocation.TryDeleteFolder("Startup");
        }
    });

    public static async Task<Result> DisableAsync()
    {
        await RunAsync("/Delete", "/TN", TaskName, "/F");

        // Done once no WinXTools task is left (also when there was none to delete).
        var (exitCode, _) = await RunAsync("/Query", "/TN", TaskName);
        return exitCode is not null and not 0 ? Result.Done : Result.Failed;
    }

    // Written as a full definition because "schtasks /Create /SC ONLOGON" keeps Task Scheduler's
    // defaults: start only on AC power, stop on battery and stop after 3 days - so WinXTools
    // would not start on a laptop running on battery and would be killed after 72 hours.
    private static void WriteTaskXml(string path, string exe, string userSid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo",
                new XElement(ns + "Source", Marker(exe)),
                new XElement(ns + "Author", "xman studio"),
                new XElement(ns + "Description", "Starts WinXTools when you sign in.")),
            new XElement(ns + "Triggers",
                new XElement(ns + "LogonTrigger",
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "UserId", userSid))),
            new XElement(ns + "Principals",
                new XElement(ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(ns + "UserId", userSid),
                    new XElement(ns + "LogonType", "InteractiveToken"),
                    new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(ns + "StopIfGoingOnBatteries", "false"),
                new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                new XElement(ns + "Priority", "5")),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(ns + "Exec",
                    new XElement(ns + "Command", exe),
                    new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(exe)))));

        // UTF-16, the format Task Scheduler itself exports and schtasks reads.
        using var writer = new StreamWriter(path, append: false, Encoding.Unicode);
        new XDocument(new XDeclaration("1.0", "UTF-16", null), task).Save(writer);
    }

    private static string Marker(string exe) =>
        "WinXTools " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exe.ToUpperInvariant())))[..16];

    private static async Task<(int? ExitCode, string Output)> RunAsync(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Schtasks)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Output uses the console code page. Only ASCII markers are searched and Latin-1
                // maps every byte, so this is safe whatever that code page is.
                StandardOutputEncoding = Encoding.Latin1,
                StandardErrorEncoding = Encoding.Latin1,
                WorkingDirectory = Environment.SystemDirectory
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null) return (null, "");

            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (null, "");
            }

            var text = await output;
            var errorText = await errors;
            if (process.ExitCode != 0)
                Debug.WriteLine($"schtasks {string.Join(' ', args)} -> {process.ExitCode}: {errorText.Trim()}");
            return (process.ExitCode, text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"schtasks could not run: {ex.Message}");
            return (null, "");
        }
    }
}

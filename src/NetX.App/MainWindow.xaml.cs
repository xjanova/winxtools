using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NetX.App.Helpers;
using NetX.App.Views;
using NetX.Core.Network;
using NetX.Core.Optimization;
using NetX.Core.System;

namespace NetX.App;

public partial class MainWindow : Window
{
    private Button? _activeNavButton;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _trialTimer;
    private readonly NetworkMonitor _networkMonitor;
    private double _maxDownloadSpeed = 1;
    private double _maxUploadSpeed = 1;
    private string? _currentPageTag = "Dashboard";

    public MainWindow()
    {
        InitializeComponent();

        // Every nav click creates a new page; don't let the frame's back
        // history keep all of them (and their timers/charts) alive.
        MainFrame.Navigated += (s, e) =>
        {
            while (MainFrame.CanGoBack) MainFrame.RemoveBackEntry();
        };

        // Set initial page
        _activeNavButton = NavDashboard;
        MainFrame.Navigate(new DashboardView());

        // Handle window state changes for maximize icon
        StateChanged += MainWindow_StateChanged;

        // Setup status bar updates - use longer interval to reduce CPU usage
        _networkMonitor = NetworkMonitor.Instance;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();

        Closed += MainWindow_Closed;

        // Trial countdown timer (1 second interval)
        _trialTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _trialTimer.Tick += TrialTimer_Tick;
        _trialTimer.Start();

        // Subscribe to trial and license status changes (raised on worker threads)
        TrialService.Instance.OnTrialStatusChanged += OnTrialStatusChanged;
        XmanLicenseService.Instance.StatusChanged += OnLicenseStatusChanged;

        // Tell the user whenever Smart Kill / Auto-Kill closes an app on its own.
        ProcessKiller.Instance.ProcessAutoKilled += (_, args) => Dispatcher.BeginInvoke(() => ShowAutoKillNotice(args));

        // Smart RAM: when Windows gets close to its commit limit, say which programs hold it.
        RamOptimizer.Instance.MemoryAdvice += OnMemoryAdvice;

        // Smart CPU: a program switched its start-up back on and WinXTools switched it off again.
        NetX.Core.System.Background.SmartCpuService.Instance.Notice += OnSmartCpuNotice;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _toastTimer.Tick += (s, e) => HideToast();

        // Initial UI state
        UpdateTrialUI();
    }

    private void TrialTimer_Tick(object? sender, EventArgs e)
    {
        var trial = TrialService.Instance;
        if (trial.IsTrialActive)
        {
            trial.Tick();
            TrialCountdown.Text = trial.FormatTimeRemaining();
        }
    }

    private void OnTrialStatusChanged() => Dispatcher.BeginInvoke(UpdateTrialUI);
    private void OnLicenseStatusChanged(LicenseStatus _) => Dispatcher.BeginInvoke(UpdateTrialUI);

    private void UpdateTrialUI()
    {
        if (ProBanner == null) return;

        var trial = TrialService.Instance;
        var license = XmanLicenseService.Instance.CachedStatus;

        if (trial.IsTrialActive && !_trialTimer.IsEnabled && !license.IsPremium) _trialTimer.Start();

        if (license.IsPremium)
        {
            // Licensed Pro user — hide banner, no overlay
            ProBanner.Visibility = Visibility.Collapsed;
            ProOverlay.Visibility = Visibility.Collapsed;
            _trialTimer.Stop();
        }
        else if (trial.IsTrialActive)
        {
            // Trial active — show countdown banner, no overlay
            ProBanner.Visibility = Visibility.Visible;
            ProBannerTitle.Text = FindResource("Pro_TrialTitle") as string ?? "PRO Trial";
            TrialCountdown.Text = trial.FormatTimeRemaining();
            TrialCountdown.Visibility = Visibility.Visible;
            ProBannerDesc.Text = FindResource("Pro_TrialDesc") as string ?? "All features unlocked";
            ProOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Trial expired or free — show upgrade banner (saying why, when a license stopped counting)
            ProBanner.Visibility = Visibility.Visible;
            TrialCountdown.Visibility = Visibility.Collapsed;
            (ProBannerTitle.Text, ProBannerDesc.Text) = license.State switch
            {
                LicenseState.Active or LicenseState.Expired =>
                    (Loc.T("Pro_BannerExpiredTitle", "Pro license expired"), Loc.T("Pro_BannerExpiredDesc", "Renew to unlock the Pro features again")),
                LicenseState.OtherMachine =>
                    (Loc.T("Pro_BannerMovedTitle", "License is on another PC"), Loc.T("Pro_BannerMovedDesc", "Open Settings → License to move it back")),
                LicenseState.Unverified =>
                    (Loc.T("Pro_BannerUnverifiedTitle", "License not confirmed"), Loc.T("Pro_BannerUnverifiedDesc", "Connect to the internet to confirm your Pro license")),
                LicenseState.Revoked =>
                    (Loc.T("Pro_BannerRevokedTitle", "License cancelled"), Loc.T("Pro_UpgradeDesc", "Unlock all premium features")),
                _ =>
                    (Loc.T("Pro_UpgradeTitle", "Upgrade to Pro"), Loc.T("Pro_UpgradeDesc", "Unlock all premium features"))
            };
            UpdateProOverlay();

            // The bad-block scan is Pro: one still running when the trial or license ends stops (it only reads).
            NetX.Core.System.Storage.SurfaceHealthScan.Current?.Stop();
        }

        if (ProOverlay.Visibility != Visibility.Visible) MainFrame.IsEnabled = true;
    }

    private void UpdateProOverlay()
    {
        bool locked = !TrialService.Instance.HasProAccess && TrialService.IsProOnlyPage(_currentPageTag);
        ProOverlay.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;

        // The overlay only blocks the mouse; disabling the page also stops
        // Tab/keyboard from reaching the locked controls underneath.
        MainFrame.IsEnabled = !locked;

        // Smart CPU's changes outlive Pro (start-ups stay off, browser policies stay set),
        // so putting them back never needs Pro.
        int changes = locked && _currentPageTag == "SmartCpu" ? NetX.Core.System.Background.SmartCpuService.Instance.Changes.Count : 0;
        ProOverlayUndo.Visibility = changes > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (changes > 0)
            ProOverlayUndoText.Text = Loc.F("Pro_SmartCpuChangesStay", "{0} changes Smart CPU made while you had Pro are still in place.", changes);
    }

    private async void ProOverlayUndo_Click(object sender, RoutedEventArgs e)
    {
        var smartCpu = NetX.Core.System.Background.SmartCpuService.Instance;
        int count = smartCpu.Changes.Count;
        if (count == 0) return;
        if (MessageBox.Show(Loc.F("Pro_ConfirmUndoSmartCpu",
                    "Put back the {0} things Smart CPU changed?\n\nStart-ups, services and browser settings go back to exactly how they were before. Programs that started with Windows will start with Windows again.",
                    count),
                Loc.T("Cpu_Title", "Smart CPU"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        ProOverlayUndoButton.IsEnabled = false;
        try
        {
            var errors = await Task.Run(smartCpu.UndoAll);
            ShowToast(errors.Count == 0
                ? Loc.F("Pro_SmartCpuUndone", "Smart CPU's changes were put back ({0}).", count)
                : Loc.F("Pro_SmartCpuUndoPartly", "{0} put back, {1} couldn't be: {2}", count - errors.Count, errors.Count, errors[0]));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] Smart CPU undo: {ex}");
            int left = smartCpu.Changes.Count;
            ShowToast(Loc.F("Pro_SmartCpuUndoPartly", "{0} put back, {1} couldn't be: {2}", count - left, left, ErrorText.From(ex)));
        }
        finally
        {
            ProOverlayUndoButton.IsEnabled = true;
            UpdateProOverlay();
        }
    }

    #region Notices

    private readonly DispatcherTimer _toastTimer;

    private Action? _toastAction;

    /// <summary>
    /// Shows a short non-blocking notice in the bottom-right corner. With <paramref name="onClick"/>
    /// clicking the notice runs it (e.g. "update available — click to install").
    /// </summary>
    public void ShowToast(string message, Action? onClick = null)
    {
        ToastText.Text = message;
        _toastAction = onClick;
        ToastHost.Cursor = onClick != null ? Cursors.Hand : null;
        ToastHost.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(onClick != null ? 20 : 8);
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        _toastAction = null;
        ToastHost.Visibility = Visibility.Collapsed;
    }

    private void ToastHost_Click(object sender, MouseButtonEventArgs e)
    {
        var action = _toastAction;
        HideToast();
        action?.Invoke();
    }

    private void ShowAutoKillNotice(ProcessAutoKilledEventArgs args)
    {
        string reason = args.Reason switch
        {
            AutoKillReason.NotResponding => Loc.F("Ram_ReasonNotResponding", "Not responding for {0} s", args.Detail),
            AutoKillReason.ExcessiveMemory => Loc.F("Ram_ReasonMemory", "Using {0} MB", args.Detail),
            _ => Loc.T("Ram_ReasonRule", "Kill rule")
        };
        var who = string.IsNullOrWhiteSpace(args.WindowTitle) ? args.ProcessName : $"{args.ProcessName} — {args.WindowTitle}";
        ShowToast(Loc.F("Ram_AutoClosed", "Auto-closed: {0}", who) + "\n" + reason);
    }

    // Raised on the automatic-mode timer thread.
    private void OnMemoryAdvice(MemoryAssessment advice) => Dispatcher.BeginInvoke(() => ShowMemoryAdvice(advice));

    // Raised on Smart CPU's watchdog thread.
    private void OnSmartCpuNotice(string program) => Dispatcher.BeginInvoke(() =>
        ShowToast(Loc.F("Cpu_TurnedOffAgainToast", "{0} switched its start-up back on — WinXTools turned it off again, as you asked.", program),
            NavigateToSmartCpu));

    private void ShowMemoryAdvice(MemoryAssessment advice)
    {
        if (advice.Pressure != MemoryPressure.CommitHigh) return;
        var who = string.Join(", ", advice.TopApps.Take(3)
            .Select(a => $"{a.DisplayName} {RamOptimizerView.FormatMB(a.PrivateMemoryMB)}"));
        ShowToast(Loc.F("Ram_CommitToast",
                "Windows is close to running out of memory ({0}% committed). Cleaning can't fix that — closing a program can. Holding the most: {1}. Click for details.",
                advice.CommitPercent, who),
            NavigateToRamOptimizer);
    }

    #endregion

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _trialTimer.Stop();
        _toastTimer.Stop();
        TrialService.Instance.OnTrialStatusChanged -= OnTrialStatusChanged;
        XmanLicenseService.Instance.StatusChanged -= OnLicenseStatusChanged;
        RamOptimizer.Instance.MemoryAdvice -= OnMemoryAdvice;
        NetX.Core.System.Background.SmartCpuService.Instance.Notice -= OnSmartCpuNotice;
        NetX.Core.System.Background.SmartCpuService.Instance.Shutdown();

        // Put the user's own proxy settings back if a free proxy is still applied.
        ProxyService.DisconnectOnExit();

        // A defragmentation started here stops with the app (Windows finishes the file it is moving).
        NetX.Core.System.Storage.DefragSession.StopRunning(TimeSpan.FromSeconds(10));
        NetX.Core.System.Storage.SmartDefragService.Instance.Shutdown();
        NetX.Core.System.Storage.SurfaceHealthScan.StopRunning(TimeSpan.FromSeconds(5));

        // Release the packet driver; packets still paced are sent first. App
        // blocks (firewall rules) intentionally stay until the user unblocks.
        try
        {
            BandwidthLimiter.Instance.Shutdown();
        }
        catch { }
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var stats = _networkMonitor.GetCurrentStats();
            var totalBytes = _networkMonitor.GetTotalBytes();

            // Update speed displays
            StatusDownload.Text = FormatSpeed(stats.TotalDownloadSpeed);
            StatusUpload.Text = FormatSpeed(stats.TotalUploadSpeed);

            // Update total data
            StatusTotalReceived.Text = FormatBytes(totalBytes.received);
            StatusTotalSent.Text = FormatBytes(totalBytes.sent);

            // Update counts
            StatusConnections.Text = stats.TotalConnections.ToString();
            StatusProcesses.Text = stats.ActiveProcessCount.ToString();

            // Update mini bandwidth bars (with adaptive scaling)
            if (stats.TotalDownloadSpeed > _maxDownloadSpeed) _maxDownloadSpeed = stats.TotalDownloadSpeed;
            if (stats.TotalUploadSpeed > _maxUploadSpeed) _maxUploadSpeed = stats.TotalUploadSpeed;

            // Decay max values slowly for adaptive scaling
            _maxDownloadSpeed *= 0.99;
            _maxUploadSpeed *= 0.99;
            if (_maxDownloadSpeed < 1024) _maxDownloadSpeed = 1024;
            if (_maxUploadSpeed < 1024) _maxUploadSpeed = 1024;

            double downloadRatio = Math.Min(1.0, stats.TotalDownloadSpeed / _maxDownloadSpeed);
            double uploadRatio = Math.Min(1.0, stats.TotalUploadSpeed / _maxUploadSpeed);

            StatusDownloadBar.Width = downloadRatio * 29; // Half of 60 width minus spacing
            StatusUploadBar.Width = uploadRatio * 29;

            // Update bandwidth limit status
            UpdateBandwidthLimitStatus();
        }
        catch { }
    }

    private void UpdateBandwidthLimitStatus()
    {
        try
        {
            var limiter = BandwidthLimiter.Instance;
            var global = limiter.GlobalRule;
            int appRules = limiter.GetAppRules().Count;

            if (global == null && appRules == 0)
            {
                BandwidthLimitBadge.Visibility = Visibility.Collapsed;
                return;
            }

            var parts = new List<string>(2);
            if (global != null)
                parts.Add($"{FormatLimitShort(global.DownloadBps)}/{FormatLimitShort(global.UploadBps)}");
            if (appRules > 0)
                parts.Add($"{appRules} app");

            // Flag limits that are saved but not being enforced.
            if (limiter.Status is LimiterStatus.DriverUnavailable or LimiterStatus.Error)
                parts.Add("!");

            StatusBandwidthLimit.Text = string.Join(" · ", parts);
            BandwidthLimitBadge.Visibility = Visibility.Visible;
        }
        catch
        {
            BandwidthLimitBadge.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Compact limit for the status bar: "-", "⛔", "10M" (Mbps), "512K" (Kbps).</summary>
    private static string FormatLimitShort(long bytesPerSecond)
    {
        if (bytesPerSecond < 0) return "-";
        if (bytesPerSecond == RateLimit.Blocked) return "⛔";
        double kbps = bytesPerSecond / 125.0;
        if (kbps >= 1_000_000) return $"{kbps / 1_000_000:0.#}G";
        if (kbps >= 1000) return $"{kbps / 1000:0.#}M";
        return $"{kbps:0}K";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_073_741_824)
            return $"{bytesPerSecond / 1_073_741_824:F1} GB/s";
        if (bytesPerSecond >= 1_048_576)
            return $"{bytesPerSecond / 1_048_576:F1} MB/s";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:F1} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_099_511_627_776)
            return $"{bytes / 1_099_511_627_776.0:F1} TB";
        if (bytes >= 1_073_741_824)
            return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)
            return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    #region Window Chrome Handlers

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaximizeButton_Click(sender, e);
        }
        else
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Hide window immediately for instant feedback
        Hide();

        // Stop timer immediately
        _statusTimer.Stop();

        // Shutdown properly via WPF (Closed releases the packet driver)
        Application.Current.Shutdown();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // Update maximize/restore icon based on window state
        if (WindowState == WindowState.Maximized)
        {
            MaximizeIcon.Data = (System.Windows.Media.Geometry)FindResource("RestoreIcon");
            MaximizeButton.ToolTip = "Restore";
        }
        else
        {
            MaximizeIcon.Data = (System.Windows.Media.Geometry)FindResource("MaximizeIcon");
            MaximizeButton.ToolTip = "Maximize";
        }
    }

    #endregion

    #region Navigation

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        // Update active button style
        if (_activeNavButton != null)
        {
            _activeNavButton.Style = (Style)FindResource("NavButtonStyle");
        }

        button.Style = (Style)FindResource("NavButtonActiveStyle");
        _activeNavButton = button;

        // Navigate to page
        string? tag = button.Tag?.ToString();
        var installUpdate = _updateForSettings;
        _updateForSettings = null;
        Page? page = tag switch
        {
            "Dashboard" => new DashboardView(),
            "Network" => new NetworkMonitorView(),
            "Bandwidth" => new BandwidthControlView(),
            "Connections" => new ConnectionMonitorView(),
            "Packets" => new PacketMonitorView(),
            "NetworkTools" => new NetworkToolsView(),
            "ProxyVpn" => new ProxyVpnView(),
            "RamOptimizer" => new RamOptimizerView(),
            "SmartCpu" => new SmartCpuView(),
            "Uninstaller" => new UninstallerView(),
            "Cleaner" => new CleanerView(),
            "DiskSpace" => new DiskSpaceView(),
            "Defrag" => new DefragView(),
            "DiskSpeed" => new DiskSpeedView(),
            "DiskHealth" => new DiskHealthView(),
            "WinOptimizer" => new WindowsOptimizerView(),
            "Rules" => new RulesView(),
            "Tricks" => new WindowsTricksView(),
            "Settings" => new SettingsView(installUpdate),
            _ => null
        };

        if (page != null)
        {
            MainFrame.Navigate(page);
            UpdatePageTitle(tag);
            _currentPageTag = tag;
            UpdateProOverlay();
        }
    }

    private void UpdatePageTitle(string? pageTag)
    {
        string resourceKey = pageTag switch
        {
            "Dashboard" => "Nav_Dashboard",
            "Network" => "Nav_NetworkMonitor",
            "Bandwidth" => "Nav_BandwidthControl",
            "Connections" => "Nav_ConnectionMonitor",
            "Packets" => "Nav_PacketMonitor",
            "NetworkTools" => "Nav_NetworkTools",
            "ProxyVpn" => "Nav_ProxyVpn",
            "RamOptimizer" => "Nav_RamOptimizer",
            "SmartCpu" => "Nav_SmartCpu",
            "Uninstaller" => "Nav_Uninstaller",
            "Cleaner" => "Nav_Cleaner",
            "DiskSpace" => "Nav_DiskSpace",
            "Defrag" => "Nav_Defrag",
            "DiskSpeed" => "Nav_DiskSpeed",
            "DiskHealth" => "Nav_DiskHealth",
            "WinOptimizer" => "Nav_WinOptimizer",
            "Rules" => "Nav_Rules",
            "Tricks" => "Nav_Tricks",
            "Settings" => "Nav_Settings",
            _ => "Nav_Dashboard"
        };

        CurrentPageTitle.Text = (string)FindResource(resourceKey);
    }

    private UpdateInfo? _updateForSettings;

    /// <summary>
    /// Opens Settings; with <paramref name="installUpdate"/> the page starts installing that
    /// update right away (the user already said yes) and shows the download progress.
    /// </summary>
    public void NavigateToSettings(UpdateInfo? installUpdate = null)
    {
        _updateForSettings = installUpdate;
        NavButton_Click(NavSettings, new RoutedEventArgs());
    }

    /// <summary>Opens a page the way its menu button does ("Defrag", "SmartCpu", "Cleaner"…).</summary>
    public void NavigateTo(string tag)
    {
        if (FindName("Nav" + tag) is Button button) NavButton_Click(button, new RoutedEventArgs());
    }

    public void NavigateToUninstaller()
    {
        if (NavUninstaller != null) NavButton_Click(NavUninstaller, new RoutedEventArgs());
    }

    public void NavigateToSmartCpu()
    {
        if (NavSmartCpu != null) NavButton_Click(NavSmartCpu, new RoutedEventArgs());
    }

    public void NavigateToRamOptimizer()
    {
        // Find and click the RAM Optimizer nav button
        if (NavRamOptimizer != null)
        {
            NavButton_Click(NavRamOptimizer, new RoutedEventArgs());
        }
        else
        {
            // Direct navigation if button not found
            MainFrame.Navigate(new RamOptimizerView());
            UpdatePageTitle("RamOptimizer");
        }
    }

    #endregion

    #region Pro Banner

    private void ProBanner_Click(object sender, MouseButtonEventArgs e)
    {
        OpenProUpgradeUrl();
    }

    private void ProOverlay_UpgradeClick(object sender, MouseButtonEventArgs e)
    {
        OpenProUpgradeUrl();
    }

    // Opens the purchase page in the user's own (non-elevated) browser, and Settings → License
    // so the key can be pasted as soon as it arrives.
    private void OpenProUpgradeUrl()
    {
        SettingsView.OpenPurchasePage();
        if (_currentPageTag != "Settings") NavigateToSettings();
    }

    #endregion
}

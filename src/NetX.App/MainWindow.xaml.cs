using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NetX.App.Views;
using NetX.Core.Network;

namespace NetX.App;

public partial class MainWindow : Window
{
    private Button? _activeNavButton;
    private readonly DispatcherTimer _statusTimer;
    private readonly NetworkMonitor _networkMonitor;
    private double _maxDownloadSpeed = 1;
    private double _maxUploadSpeed = 1;

    public MainWindow()
    {
        InitializeComponent();

        // Set initial page
        _activeNavButton = NavDashboard;
        MainFrame.Navigate(new DashboardView());

        // Handle window state changes for maximize icon
        StateChanged += MainWindow_StateChanged;

        // Setup status bar updates
        _networkMonitor = NetworkMonitor.Instance;
        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();

        Closed += (s, e) => _statusTimer.Stop();
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
        }
        catch { }
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
        Close();
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
        Page? page = tag switch
        {
            "Dashboard" => new DashboardView(),
            "Network" => new NetworkMonitorView(),
            "Connections" => new ConnectionMonitorView(),
            "Packets" => new PacketMonitorView(),
            "Bandwidth" => new BandwidthControlView(),
            "Uninstaller" => new UninstallerView(),
            "Cleaner" => new CleanerView(),
            "Rules" => new RulesView(),
            "Tricks" => new WindowsTricksView(),
            "Settings" => new SettingsView(),
            _ => null
        };

        if (page != null)
        {
            MainFrame.Navigate(page);
            UpdatePageTitle(tag);
        }
    }

    private void UpdatePageTitle(string? pageTag)
    {
        string resourceKey = pageTag switch
        {
            "Dashboard" => "Nav_Dashboard",
            "Network" => "Nav_NetworkMonitor",
            "Connections" => "Nav_ConnectionMonitor",
            "Packets" => "Nav_PacketMonitor",
            "Bandwidth" => "Nav_BandwidthControl",
            "Uninstaller" => "Nav_Uninstaller",
            "Cleaner" => "Nav_Cleaner",
            "Rules" => "Nav_Rules",
            "Tricks" => "Nav_Tricks",
            "Settings" => "Nav_Settings",
            _ => "Nav_Dashboard"
        };

        CurrentPageTitle.Text = (string)FindResource(resourceKey);
    }

    #endregion

    #region Sponsor Banner

    private void SponsorBanner_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://thaiprompt.online",
                UseShellExecute = true
            });
        }
        catch { }
    }

    #endregion
}

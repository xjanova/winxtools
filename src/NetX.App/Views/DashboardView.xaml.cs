using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using NetX.Core.Network;
using NetX.Core.Optimization;
using NetX.Core.System;
using NetX.App.Helpers;

namespace NetX.App.Views;

public partial class DashboardView : Page
{
    private readonly DispatcherTimer _updateTimer;
    private readonly NetworkMonitor _networkMonitor;
    private readonly ChartDataCache _chartCache = ChartDataCache.Instance;
    private readonly ObservableCollection<double> _downloadHistory;
    private readonly ObservableCollection<double> _uploadHistory;
    private readonly ObservableCollection<double> _downloadBarHistory = new();
    private readonly ObservableCollection<double> _uploadBarHistory = new();
    private const int MaxDataPoints = 60;
    private const int MaxBarDataPoints = 20;
    private bool _isLineChart = true;
    private ISeries[] _lineSeries = [];
    private ISeries[] _columnSeries = [];
    private List<TopConsumerItem> _topConsumers = new();

    // Responsive layout constants
    private const double MinCardWidth = 120;
    private const int CardCount = 6;

    public DashboardView()
    {
        InitializeComponent();

        _networkMonitor = NetworkMonitor.Instance;

        // Use cached chart data for continuity across page navigation
        _downloadHistory = _chartCache.DashboardDownloadHistory;
        _uploadHistory = _chartCache.DashboardUploadHistory;

        // Setup chart
        InitializeChart();

        // Setup timer for real-time updates
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();

        // Initial update
        UpdateStats();

        // Setup responsive layout
        SizeChanged += DashboardView_SizeChanged;
        Loaded += DashboardView_Loaded;
        Unloaded += (s, e) => _updateTimer.Stop();
    }

    private void DashboardView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateResponsiveLayout(ActualWidth);
    }

    private void DashboardView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout(e.NewSize.Width);
    }

    private void UpdateResponsiveLayout(double availableWidth)
    {
        if (availableWidth <= 0) return;

        // Available width for cards (minus padding)
        double contentWidth = availableWidth - 48;

        // Calculate how many cards fit per row (with min width and margin)
        int cardsPerRow = Math.Max(1, (int)(contentWidth / (MinCardWidth + 12)));
        cardsPerRow = Math.Min(cardsPerRow, CardCount);

        // Calculate optimal card width to fill the row
        double cardWidth = (contentWidth - (cardsPerRow * 12)) / cardsPerRow;

        // Update each card's width
        UpdateCardWidths(cardWidth);

        // Calculate font size based on card width
        double fontSize = CalculateCardFontSize(cardWidth);

        // Update stat value font sizes
        UpdateStatCardFontSizes(fontSize);
    }

    private void UpdateCardWidths(double cardWidth)
    {
        // Update each card's width to fill available space
        if (Card1 != null) Card1.Width = cardWidth;
        if (Card2 != null) Card2.Width = cardWidth;
        if (Card3 != null) Card3.Width = cardWidth;
        if (Card4 != null) Card4.Width = cardWidth;
        if (Card5 != null) Card5.Width = cardWidth;
        if (Card6 != null) Card6.Width = cardWidth;
    }

    private static double CalculateCardFontSize(double cardWidth)
    {
        // Base font size 24 at 150px width, scale down to min 12 at 80px
        if (cardWidth >= 150) return 24;
        if (cardWidth <= 80) return 12;

        // Linear interpolation
        double ratio = (cardWidth - 80) / (150 - 80);
        return 12 + (24 - 12) * ratio;
    }

    private void UpdateStatCardFontSizes(double fontSize)
    {
        // Update stat value TextBlocks - only if they exist
        if (DownloadSpeed != null) DownloadSpeed.FontSize = fontSize;
        if (UploadSpeed != null) UploadSpeed.FontSize = fontSize;
        if (ActiveAppsCount != null) ActiveAppsCount.FontSize = fontSize;
        if (ConnectionsCount != null) ConnectionsCount.FontSize = fontSize;
        if (TotalDownloaded != null) TotalDownloaded.FontSize = Math.Max(12, fontSize - 4);
        if (TotalUploaded != null) TotalUploaded.FontSize = Math.Max(12, fontSize - 4);
    }

    private void InitializeChart()
    {
        // Initialize with empty data only if not already initialized (preserves data across navigation)
        _chartCache.InitializeCollection(_downloadHistory, MaxDataPoints);
        _chartCache.InitializeCollection(_uploadHistory, MaxDataPoints);

        // Bar history is local (not cached)
        if (_downloadBarHistory.Count == 0)
        {
            for (int i = 0; i < MaxBarDataPoints; i++)
            {
                _downloadBarHistory.Add(0);
                _uploadBarHistory.Add(0);
            }
        }

        // Create series ONCE - no animations for smooth updates
        _lineSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = _downloadHistory,
                Name = "Download",
                Stroke = new SolidColorPaint(SKColor.Parse("#4dc9ff")) { StrokeThickness = 2 },
                Fill = new SolidColorPaint(SKColor.Parse("#2000a8e8")),
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0.65,
                AnimationsSpeed = TimeSpan.Zero,
                EnableNullSplitting = false
            },
            new LineSeries<double>
            {
                Values = _uploadHistory,
                Name = "Upload",
                Stroke = new SolidColorPaint(SKColor.Parse("#00d4aa")) { StrokeThickness = 2 },
                Fill = new SolidColorPaint(SKColor.Parse("#2000d4aa")),
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0.65,
                AnimationsSpeed = TimeSpan.Zero,
                EnableNullSplitting = false
            }
        };

        _columnSeries = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values = _downloadBarHistory,
                Name = "Download",
                Fill = new SolidColorPaint(SKColor.Parse("#4dc9ff")),
                AnimationsSpeed = TimeSpan.Zero
            },
            new ColumnSeries<double>
            {
                Values = _uploadBarHistory,
                Name = "Upload",
                Fill = new SolidColorPaint(SKColor.Parse("#00d4aa")),
                AnimationsSpeed = TimeSpan.Zero
            }
        };

        // Configure axes ONCE - no animations
        TrafficChart.XAxes = new Axis[]
        {
            new Axis
            {
                ShowSeparatorLines = false,
                IsVisible = false,
                AnimationsSpeed = TimeSpan.Zero
            }
        };

        TrafficChart.YAxes = new Axis[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#6b8eab")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#1a4dc9ff")),
                Labeler = value => FormatSpeed(value),
                MinLimit = 0,
                AnimationsSpeed = TimeSpan.Zero
            }
        };

        TrafficChart.AnimationsSpeed = TimeSpan.Zero;
        TrafficChart.Series = _lineSeries;
    }

    private void UpdateChartType(bool isLineChart)
    {
        if (_isLineChart == isLineChart) return;
        _isLineChart = isLineChart;
        TrafficChart.Series = isLineChart ? _lineSeries : _columnSeries;
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        UpdateStats();
    }

    private void UpdateStats()
    {
        try
        {
            var stats = _networkMonitor.GetCurrentStats();
            var totalBytes = _networkMonitor.GetTotalBytes();

            // Update speed displays
            DownloadSpeed.Text = FormatSpeed(stats.TotalDownloadSpeed);
            UploadSpeed.Text = FormatSpeed(stats.TotalUploadSpeed);
            ActiveAppsCount.Text = stats.ActiveProcessCount.ToString();
            ConnectionsCount.Text = stats.TotalConnections.ToString();

            // Update total received/sent stat cards
            TotalDownloaded.Text = FormatBytes(totalBytes.received);
            TotalUploaded.Text = FormatBytes(totalBytes.sent);

            // Update total data display (using card sub-labels)
            // Show total received/sent data below speed if elements exist
            UpdateTotalDataDisplay(totalBytes.received, totalBytes.sent);

            // Update chart data in-place (no new list creation)
            _downloadHistory.RemoveAt(0);
            _downloadHistory.Add(stats.TotalDownloadSpeed);

            _uploadHistory.RemoveAt(0);
            _uploadHistory.Add(stats.TotalUploadSpeed);

            // Update bar history too
            _downloadBarHistory.RemoveAt(0);
            _downloadBarHistory.Add(stats.TotalDownloadSpeed);
            _uploadBarHistory.RemoveAt(0);
            _uploadBarHistory.Add(stats.TotalUploadSpeed);

            // Only switch chart type if needed
            bool isLineChart = ChartLineBtn.IsChecked == true;
            UpdateChartType(isLineChart);

            // Update top consumers list - only if changed to avoid flickering
            UpdateTopConsumers(stats);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error updating stats: {ex.Message}");
        }
    }

    private void UpdateTotalDataDisplay(long received, long sent)
    {
        // Update tooltips on the stat cards with total data
        if (DownloadSpeed.Parent is Grid parentGrid && parentGrid.Parent is Border border)
        {
            border.ToolTip = $"Total Downloaded: {FormatBytes(received)}";
        }
        if (UploadSpeed.Parent is Grid parentGrid2 && parentGrid2.Parent is Border border2)
        {
            border2.ToolTip = $"Total Uploaded: {FormatBytes(sent)}";
        }
    }

    private void UpdateTopConsumers(NetworkStats stats)
    {
        var newConsumers = stats.ProcessStats
            .OrderByDescending(p => p.DownloadSpeed + p.UploadSpeed)
            .Take(5)
            .Select(p => new TopConsumerItem
            {
                ProcessName = p.ProcessName,
                Speed = FormatSpeed(p.DownloadSpeed + p.UploadSpeed),
                Percentage = CalculatePercentage(p.DownloadSpeed + p.UploadSpeed,
                    stats.TotalDownloadSpeed + stats.TotalUploadSpeed),
                Icon = ProcessIconHelper.GetProcessIcon(p.ProcessName)
            })
            .ToList();

        // Only update if the list has changed (reduces UI flickering)
        bool hasChanged = _topConsumers.Count != newConsumers.Count;
        if (!hasChanged)
        {
            for (int i = 0; i < _topConsumers.Count; i++)
            {
                if (_topConsumers[i].ProcessName != newConsumers[i].ProcessName ||
                    _topConsumers[i].Speed != newConsumers[i].Speed)
                {
                    hasChanged = true;
                    break;
                }
            }
        }

        if (hasChanged)
        {
            _topConsumers = newConsumers;
            TopConsumersList.ItemsSource = _topConsumers;
        }
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_073_741_824) // GB
            return $"{bytesPerSecond / 1_073_741_824:F2} GB/s";
        if (bytesPerSecond >= 1_048_576) // MB
            return $"{bytesPerSecond / 1_048_576:F2} MB/s";
        if (bytesPerSecond >= 1024) // KB
            return $"{bytesPerSecond / 1024:F2} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_099_511_627_776) // TB
            return $"{bytes / 1_099_511_627_776.0:F2} TB";
        if (bytes >= 1_073_741_824) // GB
            return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576) // MB
            return $"{bytes / 1_048_576.0:F2} MB";
        if (bytes >= 1024) // KB
            return $"{bytes / 1024.0:F2} KB";
        return $"{bytes} B";
    }

    private static string CalculatePercentage(double value, double total)
    {
        if (total <= 0) return "0%";
        return $"{(value / total * 100):F0}%";
    }

    private void ChartType_Changed(object sender, RoutedEventArgs e)
    {
        if (TrafficChart == null) return;

        bool isLineChart = ChartLineBtn.IsChecked == true;
        UpdateChartType(isLineChart);
    }

    /// <summary>
    /// Emergency cut-off: blocks all internet traffic of this PC through the
    /// packet engine (the whole-PC limit set to Blocked). Closing WinXTools
    /// always restores the connection.
    /// </summary>
    private async void BlockAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Loc.T("Dash_BlockAllConfirm", "Block all internet traffic of this PC?"),
                Loc.T("Dash_BlockAll", "Block All"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        try
        {
            var result = await BandwidthLimiter.Instance.SetGlobalLimitAsync(RateLimit.Blocked, RateLimit.Blocked);
            MessageBox.Show(result.Success
                    ? Loc.T("Dash_BlockAllDone", "All internet traffic is blocked. Press Resume All to reconnect.")
                    : $"{Loc.T("BW_ActionFailed", "Failed")}: {result.Message}",
                Loc.T("Dash_BlockAll", "Block All"), MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        finally
        {
            if (button != null) button.IsEnabled = true;
        }
    }

    /// <summary>Removes the whole-PC block/limit. Per-app rules stay as they are.</summary>
    private async void ResumeAll_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        try
        {
            bool hadGlobal = BandwidthLimiter.Instance.GlobalRule != null;
            var result = await BandwidthLimiter.Instance.RemoveGlobalLimitAsync();
            int appRules = BandwidthLimiter.Instance.GetAppRules().Count;

            string message = !result.Success
                ? $"{Loc.T("BW_ActionFailed", "Failed")}: {result.Message}"
                : hadGlobal
                    ? Loc.T("Dash_ResumeDone", "Internet traffic is back to normal.")
                    : Loc.T("Dash_ResumeNothing", "Nothing was blocked for the whole PC.");
            if (result.Success && appRules > 0)
                message += "\n\n" + Loc.F("Dash_ResumeAppRules", "{0} per-app limit/block rule(s) are still active — manage them in Bandwidth Control.", appRules);

            MessageBox.Show(message, Loc.T("Dash_ResumeAll", "Resume All"), MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        finally
        {
            if (button != null) button.IsEnabled = true;
        }
    }

    // Categories that are always safe to clean without reviewing them first.
    private static readonly CleanTarget[] QuickCleanTargets =
        { CleanTarget.TempFiles, CleanTarget.BrowserCache, CleanTarget.Thumbnails, CleanTarget.ErrorReports };

    private async void QuickClean_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Loc.T("Dash_QuickCleanConfirm", "Remove temporary files, browser cache, thumbnail cache and crash reports?"),
                Loc.T("Dash_QuickClean", "Quick Clean"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        try
        {
            var results = await Task.Run(() => QuickCleanTargets.Select(t => SystemCleaner.Clean(t)).ToList());
            long freed = results.Sum(r => r.BytesFreed);
            int deleted = results.Sum(r => r.FilesDeleted);
            int skipped = results.Sum(r => r.FilesSkipped);

            var message = Loc.F("Dash_QuickCleanDone", "Freed {0} ({1:N0} files).", FormatBytes(freed), deleted);
            if (skipped > 0)
                message += "\n" + Loc.F("Cleaner_SkippedTotal", "{0:N0} files were in use or protected and were left alone.", skipped);
            MessageBox.Show(message, Loc.T("Dash_QuickClean", "Quick Clean"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{Loc.T("BW_ActionFailed", "Failed")}: {ex.Message}", Loc.T("Dash_QuickClean", "Quick Clean"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (button != null) button.IsEnabled = true;
        }
    }

    private async void OptimizeRam_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        try
        {
            var ramOptimizer = RamOptimizer.Instance;

            // The same cleanup (the page's safe default) and the same wording as the RAM page:
            // what really came back, and a line per step. Never on the UI thread.
            var result = await Task.Run(() => ramOptimizer.OptimizeNow());

            var details = string.Join("\n", result.Operations.Select(RamOptimizerView.DescribeOperation));
            MessageBox.Show(
                RamOptimizerView.SummaryText(result) + (details.Length > 0 ? "\n\n" + details : ""),
                Loc.T("Dash_Ram", "RAM Optimization"),
                MessageBoxButton.OK,
                result.AnyOperationSucceeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{Loc.T("BW_ActionFailed", "Failed")}: {ex.Message}", Loc.T("Dash_Ram", "RAM Optimization"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (button != null) button.IsEnabled = true;
        }
    }

    private async void FlushDns_Click(object sender, RoutedEventArgs e)
    {
        var (exitCode, _) = await RunHiddenAsync("ipconfig", "/flushdns");
        MessageBox.Show(exitCode == 0
                ? Loc.T("Dash_DnsDone", "DNS cache has been flushed.")
                : Loc.F("Dash_DnsFailed", "Flushing the DNS cache failed (exit code {0}).", exitCode),
            "Flush DNS", MessageBoxButton.OK, exitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Error);
    }

    /// <summary>
    /// Disables and re-enables every physical adapter that is connected, using
    /// the adapters' real names, and reports what actually happened.
    /// </summary>
    private async void ResetNetwork_Click(object sender, RoutedEventArgs e)
    {
        var adapters = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                         ni.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                             or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211
                             or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet &&
                         !ni.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase) &&
                         !ni.Description.Contains("hyper-v", StringComparison.OrdinalIgnoreCase))
            .Select(ni => ni.Name)
            .ToList();

        if (adapters.Count == 0)
        {
            MessageBox.Show(Loc.T("Dash_ResetNoAdapter", "No connected network adapter was found."), "WinXTools",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(Loc.F("Dash_ResetConfirm", "Restart these network adapters? You will be offline for a few seconds.\n\n{0}",
                    string.Join("\n", adapters)),
                Loc.T("Dash_Reset", "Reset Network Adapter"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        var failed = new List<string>();
        try
        {
            foreach (var name in adapters)
            {
                var (disableCode, _) = await RunHiddenAsync("netsh", $"interface set interface name=\"{name}\" admin=disable");
                await Task.Delay(1500);
                // Always try to bring the adapter back, even if disabling failed.
                var (enableCode, _) = await RunHiddenAsync("netsh", $"interface set interface name=\"{name}\" admin=enable");
                if (disableCode != 0 || enableCode != 0) failed.Add(name);
            }
        }
        finally
        {
            if (button != null) button.IsEnabled = true;
        }

        MessageBox.Show(failed.Count == 0
                ? Loc.T("Dash_ResetDone", "Network adapters restarted. The connection comes back in a few seconds.")
                : Loc.F("Dash_ResetFailed", "Could not restart: {0}", string.Join(", ", failed)),
            Loc.T("Dash_Reset", "Reset Network Adapter"), MessageBoxButton.OK,
            failed.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static async Task<(int ExitCode, string Output)> RunHiddenAsync(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null) return (-1, "");

            var output = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                return (-1, "");
            }
            return (process.ExitCode, await output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}

public class TopConsumerItem
{
    public string ProcessName { get; set; } = string.Empty;
    public string Speed { get; set; } = string.Empty;
    public string Percentage { get; set; } = "0%";
    public ImageSource? Icon { get; set; }
}

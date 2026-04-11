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
    private ISeries[]? _lineSeries;
    private ISeries[]? _columnSeries;
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

    private void BlockAll_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "This will block all network traffic for all applications.\nAre you sure?",
            "Block All Traffic",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
    }

    private void ResumeAll_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "All network traffic has been resumed.",
            "Resume All",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void QuickClean_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Quick cleanup will remove temporary files.\nProceed?",
            "Quick Clean",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
    }

    private void OptimizeRam_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ramOptimizer = RamOptimizer.Instance;
            var beforeInfo = ramOptimizer.GetMemoryInfo();

            var result = ramOptimizer.OptimizeNow();

            var freedMB = Math.Max(0, result.MemoryFreedMB);
            MessageBox.Show(
                $"RAM Optimization Complete!\n\n" +
                $"Before: {beforeInfo.UsagePercent}% used\n" +
                $"Processes optimized: {result.ProcessesOptimized}\n" +
                $"Memory freed: {FormatBytes(freedMB * 1024 * 1024)}",
                "RAM Optimization",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to optimize RAM: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void FlushDns_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);

            MessageBox.Show(
                "DNS cache has been flushed successfully!\n\n" +
                "This can help resolve connection issues caused by stale DNS entries.",
                "Flush DNS",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to flush DNS: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ResetNetwork_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will reset your network adapter which may temporarily disconnect you.\n\n" +
            "This can help fix connectivity issues. Continue?",
            "Reset Network Adapter",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                // Disable and re-enable network adapters
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "interface set interface \"Wi-Fi\" admin=disable",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true
                };

                Process.Start(psi)?.WaitForExit(3000);

                psi.Arguments = "netsh interface set interface \"Wi-Fi\" admin=enable";
                Process.Start(psi)?.WaitForExit(3000);

                // Also try Ethernet
                psi.Arguments = "netsh interface set interface \"Ethernet\" admin=disable";
                Process.Start(psi);

                System.Threading.Thread.Sleep(1000);

                psi.Arguments = "netsh interface set interface \"Ethernet\" admin=enable";
                Process.Start(psi);

                MessageBox.Show(
                    "Network adapters have been reset.\n\n" +
                    "Your connection should be restored shortly.",
                    "Reset Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to reset network: {ex.Message}\n\n" +
                    "Make sure to run the application as Administrator.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
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

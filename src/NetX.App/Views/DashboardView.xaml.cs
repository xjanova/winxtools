using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using NetX.Core.Network;

namespace NetX.App.Views;

public partial class DashboardView : Page
{
    private readonly DispatcherTimer _updateTimer;
    private readonly NetworkMonitor _networkMonitor;
    private readonly ObservableCollection<double> _downloadHistory = new();
    private readonly ObservableCollection<double> _uploadHistory = new();
    private readonly ObservableCollection<double> _downloadBarHistory = new();
    private readonly ObservableCollection<double> _uploadBarHistory = new();
    private const int MaxDataPoints = 60;
    private const int MaxBarDataPoints = 20;
    private bool _isLineChart = true;
    private ISeries[]? _lineSeries;
    private ISeries[]? _columnSeries;
    private List<TopConsumerItem> _topConsumers = new();

    public DashboardView()
    {
        InitializeComponent();

        _networkMonitor = NetworkMonitor.Instance;

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

        Unloaded += (s, e) => _updateTimer.Stop();
    }

    private void InitializeChart()
    {
        // Initialize with empty data
        for (int i = 0; i < MaxDataPoints; i++)
        {
            _downloadHistory.Add(0);
            _uploadHistory.Add(0);
        }
        for (int i = 0; i < MaxBarDataPoints; i++)
        {
            _downloadBarHistory.Add(0);
            _uploadBarHistory.Add(0);
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
                    stats.TotalDownloadSpeed + stats.TotalUploadSpeed)
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
}

public class TopConsumerItem
{
    public string ProcessName { get; set; } = string.Empty;
    public string Speed { get; set; } = string.Empty;
    public string Percentage { get; set; } = "0%";
    public object? Icon { get; set; }
}

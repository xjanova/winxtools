using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using NetX.Core.Network;
using NetX.Core.Optimization;

namespace NetX.App.Views;

public partial class NetworkMonitorView : Page
{
    private readonly DispatcherTimer _updateTimer;
    private readonly NetworkMonitor _networkMonitor;
    private readonly ObservableCollection<double> _selectedDownloadHistory = new();
    private readonly ObservableCollection<double> _selectedUploadHistory = new();
    private List<ProcessDisplayItem> _allProcesses = new();
    private const int MaxDataPoints = 30;
    private bool _isLineChart = true;
    private ISeries[]? _lineSeries;
    private ISeries[]? _columnSeries;

    // Sorting
    private string _currentSortColumn = "DownloadSpeed";
    private bool _sortAscending = false;
    private int? _selectedProcessId;

    public NetworkMonitorView()
    {
        InitializeComponent();

        _networkMonitor = NetworkMonitor.Instance;

        // Initialize chart data
        for (int i = 0; i < MaxDataPoints; i++)
        {
            _selectedDownloadHistory.Add(0);
            _selectedUploadHistory.Add(0);
        }

        InitializeDetailChart();

        // Setup timer
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();

        UpdateProcessList();

        // Initialize kill toggles
        AutoKillToggle.IsChecked = ProcessKiller.Instance.IsAutoKillEnabled;
        SmartKillToggle.IsChecked = ProcessKiller.Instance.IsSmartKillEnabled;

        Unloaded += (s, e) => _updateTimer.Stop();
    }

    private void InitializeDetailChart()
    {
        // Create series once and reuse them - no animations, ocean theme colors
        _lineSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = _selectedDownloadHistory,
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
                Values = _selectedUploadHistory,
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
                Values = _selectedDownloadHistory,
                Name = "Download",
                Fill = new SolidColorPaint(SKColor.Parse("#4dc9ff")),
                AnimationsSpeed = TimeSpan.Zero
            },
            new ColumnSeries<double>
            {
                Values = _selectedUploadHistory,
                Name = "Upload",
                Fill = new SolidColorPaint(SKColor.Parse("#00d4aa")),
                AnimationsSpeed = TimeSpan.Zero
            }
        };

        DetailChart.XAxes = new Axis[]
        {
            new Axis
            {
                IsVisible = false,
                AnimationsSpeed = TimeSpan.Zero
            }
        };

        DetailChart.YAxes = new Axis[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#6b8eab")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#1a4dc9ff")),
                MinLimit = 0,
                Labeler = value => FormatSpeed(value),
                AnimationsSpeed = TimeSpan.Zero
            }
        };

        DetailChart.AnimationsSpeed = TimeSpan.Zero;

        DetailChart.Series = _lineSeries;
    }

    private void UpdateDetailChartSeries(bool isLineChart)
    {
        if (_isLineChart == isLineChart) return;
        _isLineChart = isLineChart;
        DetailChart.Series = isLineChart ? _lineSeries : _columnSeries;
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        UpdateProcessList();
    }

    private void UpdateProcessList()
    {
        try
        {
            var stats = _networkMonitor.GetCurrentStats();

            // Calculate max speeds for bar chart scaling
            var maxDownload = stats.ProcessStats.Any() ? stats.ProcessStats.Max(p => p.DownloadSpeed) : 1;
            var maxUpload = stats.ProcessStats.Any() ? stats.ProcessStats.Max(p => p.UploadSpeed) : 1;
            if (maxDownload < 1) maxDownload = 1;
            if (maxUpload < 1) maxUpload = 1;

            _allProcesses = stats.ProcessStats
                .Select(p => new ProcessDisplayItem
                {
                    ProcessId = p.ProcessId,
                    ProcessName = p.ProcessName,
                    DownloadSpeed = p.DownloadSpeed,
                    UploadSpeed = p.UploadSpeed,
                    DownloadSpeedText = FormatSpeed(p.DownloadSpeed),
                    UploadSpeedText = FormatSpeed(p.UploadSpeed),
                    ConnectionCount = p.ConnectionCount,
                    // Calculate bar heights (0-20 pixels)
                    DownloadBarHeight = (int)Math.Min(20, (p.DownloadSpeed / maxDownload) * 20),
                    UploadBarHeight = (int)Math.Min(20, (p.UploadSpeed / maxUpload) * 20),
                    IsAutoKillEnabled = ProcessKiller.Instance.IsProcessBlocked(p.ProcessName)
                })
                .ToList();

            ApplyFilters();

            StatusText.Text = $"Monitoring {stats.ActiveProcessCount} processes, {stats.TotalConnections} connections";

            // Update selected process chart and bandwidth display
            if (_selectedProcessId.HasValue)
            {
                var currentStats = _allProcesses.FirstOrDefault(p => p.ProcessId == _selectedProcessId.Value);
                if (currentStats != null)
                {
                    _selectedDownloadHistory.RemoveAt(0);
                    _selectedDownloadHistory.Add(currentStats.DownloadSpeed);

                    _selectedUploadHistory.RemoveAt(0);
                    _selectedUploadHistory.Add(currentStats.UploadSpeed);

                    // Update real-time speed display
                    CurrentDownloadSpeed.Text = currentStats.DownloadSpeedText;
                    CurrentUploadSpeed.Text = currentStats.UploadSpeedText;
                }
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void ApplyFilters()
    {
        // Guard against null controls during initialization
        if (SearchBox == null || ActiveOnlyBtn == null || ProcessList == null) return;

        var searchText = SearchBox.Text?.ToLower() ?? "";
        var showActiveOnly = ActiveOnlyBtn.IsChecked == true;

        var filtered = _allProcesses.Where(p =>
        {
            // Search filter
            if (!string.IsNullOrEmpty(searchText))
            {
                if (!p.ProcessName.ToLower().Contains(searchText) &&
                    !p.ProcessId.ToString().Contains(searchText))
                    return false;
            }

            // Active filter
            if (showActiveOnly && p.DownloadSpeed + p.UploadSpeed < 100)
                return false;

            return true;
        });

        // Apply sorting
        filtered = ApplySorting(filtered);

        ProcessList.ItemsSource = filtered.ToList();
    }

    private IEnumerable<ProcessDisplayItem> ApplySorting(IEnumerable<ProcessDisplayItem> items)
    {
        return _currentSortColumn switch
        {
            "ProcessId" => _sortAscending ? items.OrderBy(p => p.ProcessId) : items.OrderByDescending(p => p.ProcessId),
            "ProcessName" => _sortAscending ? items.OrderBy(p => p.ProcessName) : items.OrderByDescending(p => p.ProcessName),
            "DownloadSpeed" => _sortAscending ? items.OrderBy(p => p.DownloadSpeed) : items.OrderByDescending(p => p.DownloadSpeed),
            "UploadSpeed" => _sortAscending ? items.OrderBy(p => p.UploadSpeed) : items.OrderByDescending(p => p.UploadSpeed),
            "ConnectionCount" => _sortAscending ? items.OrderBy(p => p.ConnectionCount) : items.OrderByDescending(p => p.ConnectionCount),
            _ => items.OrderByDescending(p => p.DownloadSpeed + p.UploadSpeed)
        };
    }

    private void SortColumn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string column)
        {
            if (_currentSortColumn == column)
            {
                _sortAscending = !_sortAscending;
            }
            else
            {
                _currentSortColumn = column;
                _sortAscending = column == "ProcessName"; // Default ascending for name, descending for others
            }

            UpdateSortIndicators();
            ApplyFilters();
        }
    }

    private void UpdateSortIndicators()
    {
        // Hide all sort indicators
        SortPID.Visibility = Visibility.Collapsed;
        SortName.Visibility = Visibility.Collapsed;
        SortDownload.Visibility = Visibility.Collapsed;
        SortUpload.Visibility = Visibility.Collapsed;
        SortConn.Visibility = Visibility.Collapsed;

        // Get the right icon data
        var sortIcon = _sortAscending
            ? FindResource("SortUpIcon") as Geometry
            : FindResource("SortDownIcon") as Geometry;

        // Show the active sort indicator
        Path? sortPath = _currentSortColumn switch
        {
            "ProcessId" => SortPID,
            "ProcessName" => SortName,
            "DownloadSpeed" => SortDownload,
            "UploadSpeed" => SortUpload,
            "ConnectionCount" => SortConn,
            _ => null
        };

        if (sortPath != null)
        {
            sortPath.Visibility = Visibility.Visible;
            sortPath.Data = sortIcon;
        }
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_073_741_824)
            return $"{bytesPerSecond / 1_073_741_824:F2} GB/s";
        if (bytesPerSecond >= 1_048_576)
            return $"{bytesPerSecond / 1_048_576:F2} MB/s";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:F2} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        ApplyFilters();
    }

    private void ChartType_Changed(object sender, RoutedEventArgs e)
    {
        if (DetailChart == null) return;
        bool isLineChart = LineChartBtn.IsChecked == true;
        UpdateDetailChartSeries(isLineChart);
    }

    private void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessList.SelectedItem is ProcessDisplayItem item)
        {
            _selectedProcessId = item.ProcessId;
            SelectedProcessName.Text = item.ProcessName;
            SelectedProcessDetails.Text = $"PID: {item.ProcessId} | Connections: {item.ConnectionCount}";

            // Update speed displays
            CurrentDownloadSpeed.Text = item.DownloadSpeedText;
            CurrentUploadSpeed.Text = item.UploadSpeedText;

            // Reset chart history for new selection
            for (int i = 0; i < MaxDataPoints; i++)
            {
                _selectedDownloadHistory[i] = 0;
                _selectedUploadHistory[i] = 0;
            }

            // Load connections for this process
            LoadProcessConnections(item.ProcessId);

            // Reset bandwidth slider
            BandwidthLimitSlider.Value = 0;
        }
    }

    private void LoadProcessConnections(int processId)
    {
        var connections = ConnectionMonitor.Instance.GetConnectionsByProcess(processId);
        ConnectionsList.ItemsSource = connections.Select(c => new
        {
            c.RemoteAddress,
            Status = $":{c.RemotePort} - {c.State}"
        }).ToList();
    }

    private void BandwidthLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_selectedProcessId.HasValue) return;

        var value = e.NewValue;
        if (value <= 0)
        {
            BandwidthLimitText.Text = "Unlimited";
        }
        else if (value >= 100)
        {
            BandwidthLimitText.Text = "Blocked";
        }
        else
        {
            // Calculate actual limit based on slider
            var limitKBps = (100 - value) * 10; // 0-100 -> 1000-0 KB/s
            if (limitKBps >= 1000)
                BandwidthLimitText.Text = $"{limitKBps / 1000:F1} MB/s";
            else
                BandwidthLimitText.Text = $"{limitKBps:F0} KB/s";
        }

        // TODO: Apply actual bandwidth limit using WFP or QoS
        StatusText.Text = $"Bandwidth limit set to {BandwidthLimitText.Text} for PID {_selectedProcessId}";
    }

    private void SpeedPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, out var value))
        {
            BandwidthLimitSlider.Value = value;
        }
    }

    private void BlockProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int processId)
        {
            var result = MessageBox.Show(
                $"Block all network traffic for process ID {processId}?",
                "Block Process",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var connections = ConnectionMonitor.Instance.GetConnectionsByProcess(processId);
                int blocked = 0;
                foreach (var conn in connections)
                {
                    if (!string.IsNullOrEmpty(conn.RemoteAddress) && conn.RemoteAddress != "*")
                    {
                        FirewallManager.Instance.BlockIP(conn.RemoteAddress);
                        blocked++;
                    }
                }
                StatusText.Text = $"Blocked {blocked} connections for PID {processId}";
            }
        }
    }

    private void ViewConnections_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int processId)
        {
            // Select the process to show details
            var item = _allProcesses.FirstOrDefault(p => p.ProcessId == processId);
            if (item != null)
            {
                ProcessList.SelectedItem = item;
            }
        }
    }

    private void BlockConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string ipAddress)
        {
            var result = MessageBox.Show(
                $"Block IP address {ipAddress}?",
                "Block IP",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                FirewallManager.Instance.BlockIP(ipAddress);
                StatusText.Text = $"Blocked IP: {ipAddress}";

                // Refresh connections list
                if (_selectedProcessId.HasValue)
                {
                    LoadProcessConnections(_selectedProcessId.Value);
                }
            }
        }
    }

    private void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int processId)
        {
            var process = _allProcesses.FirstOrDefault(p => p.ProcessId == processId);
            var processName = process?.ProcessName ?? $"PID {processId}";

            if (ProcessKiller.IsProtectedProcess(processName))
            {
                MessageBox.Show(
                    $"Cannot kill {processName}.\n\nThis is a protected system process.",
                    "Protected Process",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"Kill process {processName} (PID: {processId})?\n\nThis will immediately terminate the process.",
                "Kill Process",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                if (ProcessKiller.Instance.KillProcess(processId))
                {
                    StatusText.Text = $"Killed process: {processName}";
                    UpdateProcessList();
                }
                else
                {
                    // Try force kill
                    if (ProcessKiller.Instance.ForceKillProcess(processId))
                    {
                        StatusText.Text = $"Force killed process: {processName}";
                        UpdateProcessList();
                    }
                    else
                    {
                        MessageBox.Show(
                            $"Failed to kill {processName}.\n\nThe process may be protected or require administrator privileges.",
                            "Kill Failed",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
            }
        }
    }

    private void AutoKillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string processName)
        {
            if (ProcessKiller.IsProtectedProcess(processName))
            {
                MessageBox.Show(
                    $"Cannot auto-kill {processName}.\n\nThis is a protected system process.",
                    "Protected Process",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (ProcessKiller.Instance.IsProcessBlocked(processName))
            {
                // Remove from auto-kill list
                ProcessKiller.Instance.RemoveKillRule(processName);
                StatusText.Text = $"Removed {processName} from auto-kill list";
            }
            else
            {
                // Add to auto-kill list
                var result = MessageBox.Show(
                    $"Add {processName} to auto-kill list?\n\n" +
                    "This will:\n" +
                    "1. Kill the process now\n" +
                    "2. Automatically kill it if it reopens\n\n" +
                    "Make sure 'Kill Auto' mode is enabled for this to work.",
                    "Auto-Kill Process",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    ProcessKiller.Instance.AddKillRule(processName, "User requested auto-kill");
                    StatusText.Text = $"Added {processName} to auto-kill list";
                    UpdateProcessList();
                }
            }
        }
    }

    private void AutoKillToggle_Click(object sender, RoutedEventArgs e)
    {
        ProcessKiller.Instance.IsAutoKillEnabled = AutoKillToggle.IsChecked == true;
        StatusText.Text = ProcessKiller.Instance.IsAutoKillEnabled
            ? "Kill Auto mode ENABLED - Blocked processes will be killed automatically"
            : "Kill Auto mode DISABLED";
    }

    private void SmartKillToggle_Click(object sender, RoutedEventArgs e)
    {
        ProcessKiller.Instance.IsSmartKillEnabled = SmartKillToggle.IsChecked == true;
        StatusText.Text = ProcessKiller.Instance.IsSmartKillEnabled
            ? "Smart Kill ENABLED - Frozen/problematic processes will be auto-killed"
            : "Smart Kill DISABLED";
    }
}

public class ProcessDisplayItem
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public double DownloadSpeed { get; set; }
    public double UploadSpeed { get; set; }
    public string DownloadSpeedText { get; set; } = "0 B/s";
    public string UploadSpeedText { get; set; } = "0 B/s";
    public int ConnectionCount { get; set; }
    public int DownloadBarHeight { get; set; }
    public int UploadBarHeight { get; set; }
    public bool IsAutoKillEnabled { get; set; }
}

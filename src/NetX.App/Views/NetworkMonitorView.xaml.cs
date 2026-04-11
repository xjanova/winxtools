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
using NetX.Core.Helpers;
using NetX.App.Helpers;

namespace NetX.App.Views;

public partial class NetworkMonitorView : Page
{
    private readonly DispatcherTimer _updateTimer;
    private readonly NetworkMonitor _networkMonitor;
    private readonly ChartDataCache _chartCache = ChartDataCache.Instance;
    private readonly ObservableCollection<double> _selectedDownloadHistory;
    private readonly ObservableCollection<double> _selectedUploadHistory;
    private readonly ObservableCollection<ProcessDisplayItem> _displayedProcesses = new();
    private readonly Dictionary<int, ProcessDisplayItem> _processMap = new();
    private const int MaxDataPoints = 30;
    private bool _isLineChart = true;
    private ISeries[]? _lineSeries;
    private ISeries[]? _columnSeries;

    // Sorting
    private string _currentSortColumn = "DownloadSpeed";
    private bool _sortAscending = false;
    private int? _selectedProcessId;

    // Pagination
    private int _currentPage = 1;
    private int _itemsPerPage = 10;
    private int _totalPages = 1;
    private List<ProcessDisplayItem> _allFilteredItems = new();

    // Debounce timer for applying bandwidth limits (1 second delay after slider stops)
    private DispatcherTimer? _bandwidthDebounceTimer;

    public NetworkMonitorView()
    {
        InitializeComponent();

        _networkMonitor = NetworkMonitor.Instance;

        // Use cached chart data for continuity across page navigation
        _selectedDownloadHistory = _chartCache.NetworkMonitorDownloadHistory;
        _selectedUploadHistory = _chartCache.NetworkMonitorUploadHistory;
        _selectedProcessId = _chartCache.NetworkMonitorSelectedProcessId;

        // Initialize chart data only if not already initialized
        _chartCache.InitializeCollection(_selectedDownloadHistory, MaxDataPoints);
        _chartCache.InitializeCollection(_selectedUploadHistory, MaxDataPoints);

        InitializeDetailChart();

        // Setup timer - use 2 seconds for smoother performance
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();

        UpdateProcessList();

        // Initialize kill toggles
        AutoKillToggle.IsChecked = ProcessKiller.Instance.IsAutoKillEnabled;
        SmartKillToggle.IsChecked = ProcessKiller.Instance.IsSmartKillEnabled;

        Unloaded += (s, e) =>
        {
            _updateTimer.Stop();
            _bandwidthDebounceTimer?.Stop();
        };
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

            // Update existing items or add new ones (avoids recreating the entire list)
            var currentProcessIds = new HashSet<int>();

            // Only show top processes by activity (limit to 50 for performance)
            var topProcesses = stats.ProcessStats
                .OrderByDescending(p => p.DownloadSpeed + p.UploadSpeed)
                .Take(50)
                .ToList();

            foreach (var p in topProcesses)
            {
                currentProcessIds.Add(p.ProcessId);

                if (_processMap.TryGetValue(p.ProcessId, out var existing))
                {
                    // Update existing item
                    existing.DownloadSpeed = p.DownloadSpeed;
                    existing.UploadSpeed = p.UploadSpeed;
                    existing.DownloadSpeedText = FormatSpeed(p.DownloadSpeed);
                    existing.UploadSpeedText = FormatSpeed(p.UploadSpeed);
                    existing.ConnectionCount = p.ConnectionCount;
                    existing.IsAutoKillEnabled = ProcessKiller.Instance.IsProcessBlocked(p.ProcessName);
                }
                else
                {
                    // Add new item (defer icon loading)
                    var newItem = new ProcessDisplayItem
                    {
                        ProcessId = p.ProcessId,
                        ProcessName = p.ProcessName,
                        DownloadSpeed = p.DownloadSpeed,
                        UploadSpeed = p.UploadSpeed,
                        DownloadSpeedText = FormatSpeed(p.DownloadSpeed),
                        UploadSpeedText = FormatSpeed(p.UploadSpeed),
                        ConnectionCount = p.ConnectionCount,
                        IsAutoKillEnabled = ProcessKiller.Instance.IsProcessBlocked(p.ProcessName),
                        Icon = ProcessIconHelper.GetProcessIcon(p.ProcessName)
                    };
                    _processMap[p.ProcessId] = newItem;
                }
            }

            // Remove processes that are no longer active
            var toRemove = _processMap.Keys.Where(id => !currentProcessIds.Contains(id)).ToList();
            foreach (var id in toRemove)
            {
                _processMap.Remove(id);
            }

            ApplyFilters();

            // Show total speed from interface (accurate measurement)
            StatusText.Text = $"↓ {FormatSpeed(stats.TotalDownloadSpeed)} | ↑ {FormatSpeed(stats.TotalUploadSpeed)} | {stats.ActiveProcessCount} apps, {stats.TotalConnections} connections";

            // Update selected process chart and bandwidth display
            if (_selectedProcessId.HasValue && _processMap.TryGetValue(_selectedProcessId.Value, out var currentStats))
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
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private List<ProcessDisplayItem>? _lastDisplayedList;
    private string _lastSearchText = "";
    private bool _lastShowActiveOnly;

    private void ApplyFilters()
    {
        // Guard against null controls during initialization
        if (SearchBox == null || ActiveOnlyBtn == null || ProcessList == null) return;

        var searchText = SearchBox.Text?.ToLower() ?? "";
        var showActiveOnly = ActiveOnlyBtn.IsChecked == true;

        var filtered = _processMap.Values.Where(p =>
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
        _allFilteredItems = filtered.ToList();

        // Calculate pagination
        _totalPages = Math.Max(1, (int)Math.Ceiling((double)_allFilteredItems.Count / _itemsPerPage));
        if (_currentPage > _totalPages) _currentPage = _totalPages;

        // Get current page items
        var pageItems = _allFilteredItems
            .Skip((_currentPage - 1) * _itemsPerPage)
            .Take(_itemsPerPage)
            .ToList();

        // Only rebind ItemsSource if the list composition changed
        bool needsRebind = _lastDisplayedList == null ||
                          _lastSearchText != searchText ||
                          _lastShowActiveOnly != showActiveOnly ||
                          !AreListsEqual(_lastDisplayedList, pageItems);

        if (needsRebind)
        {
            ProcessList.ItemsSource = pageItems;
            _lastDisplayedList = pageItems;
            _lastSearchText = searchText;
            _lastShowActiveOnly = showActiveOnly;
        }

        // Update pagination UI
        UpdatePaginationUI();
    }

    private void UpdatePaginationUI()
    {
        if (PageInfoText == null) return;

        CurrentPageText.Text = _currentPage.ToString();
        PageInfoText.Text = $"Page {_currentPage} of {_totalPages} ({_allFilteredItems.Count} apps)";

        // Enable/disable pagination buttons
        FirstPageBtn.IsEnabled = _currentPage > 1;
        PrevPageBtn.IsEnabled = _currentPage > 1;
        NextPageBtn.IsEnabled = _currentPage < _totalPages;
        LastPageBtn.IsEnabled = _currentPage < _totalPages;
    }

    #region Pagination Events

    private void FirstPage_Click(object sender, RoutedEventArgs e)
    {
        _currentPage = 1;
        ApplyFilters();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 1)
        {
            _currentPage--;
            ApplyFilters();
        }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage < _totalPages)
        {
            _currentPage++;
            ApplyFilters();
        }
    }

    private void LastPage_Click(object sender, RoutedEventArgs e)
    {
        _currentPage = _totalPages;
        ApplyFilters();
    }

    private void ItemsPerPage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsPerPageCombo?.SelectedItem is ComboBoxItem item)
        {
            if (int.TryParse(item.Content?.ToString(), out var count))
            {
                _itemsPerPage = count;
                _currentPage = 1; // Reset to first page
                ApplyFilters();
            }
        }
    }

    #endregion

    private static bool AreListsEqual(List<ProcessDisplayItem> a, List<ProcessDisplayItem> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].ProcessId != b[i].ProcessId) return false;
        }
        return true;
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
            _chartCache.NetworkMonitorSelectedProcessId = item.ProcessId; // Cache for navigation persistence
            SelectedProcessName.Text = item.ProcessName;
            SelectedProcessDetails.Text = $"PID: {item.ProcessId} | Connections: {item.ConnectionCount}";

            // Show process icon in detail panel
            if (item.Icon != null)
            {
                SelectedProcessName.Text = item.ProcessName;
            }

            // Update speed displays
            CurrentDownloadSpeed.Text = item.DownloadSpeedText;
            CurrentUploadSpeed.Text = item.UploadSpeedText;

            // Reset chart history for new selection (new process = new chart data)
            for (int i = 0; i < MaxDataPoints; i++)
            {
                _selectedDownloadHistory[i] = 0;
                _selectedUploadHistory[i] = 0;
            }

            // Load connections for this process
            LoadProcessConnections(item.ProcessId);

            // Load existing bandwidth limits for this process
            LoadProcessBandwidthLimits(item.ProcessName);
        }
    }

    private void LoadProcessBandwidthLimits(string processName)
    {
        var existingRule = BandwidthLimiter.Instance.GetLimit(processName);
        if (existingRule != null)
        {
            // Set sliders to existing limits
            DownloadLimitSlider.Value = KBpsToSlider(existingRule.DownloadLimitKBps);
            UploadLimitSlider.Value = KBpsToSlider(existingRule.UploadLimitKBps);
        }
        else
        {
            // Reset to unlimited
            DownloadLimitSlider.Value = 0;
            UploadLimitSlider.Value = 0;
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

    private long _currentDownloadLimitKBps = 0;
    private long _currentUploadLimitKBps = 0;

    private void DownloadLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DownloadLimitText == null) return;

        var value = e.NewValue;
        _currentDownloadLimitKBps = SliderToKBps(value);
        DownloadLimitText.Text = FormatLimitText(_currentDownloadLimitKBps);

        // Debounce - apply after 1 second of no changes
        ScheduleBandwidthApply();
    }

    private void UploadLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (UploadLimitText == null) return;

        var value = e.NewValue;
        _currentUploadLimitKBps = SliderToKBps(value);
        UploadLimitText.Text = FormatLimitText(_currentUploadLimitKBps);

        // Debounce - apply after 1 second of no changes
        ScheduleBandwidthApply();
    }

    /// <summary>
    /// Schedule applying bandwidth limits with 1 second debounce
    /// </summary>
    private void ScheduleBandwidthApply()
    {
        // Create timer if needed
        if (_bandwidthDebounceTimer == null)
        {
            _bandwidthDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _bandwidthDebounceTimer.Tick += (s, e) =>
            {
                _bandwidthDebounceTimer.Stop();
                ApplyBandwidthLimits();
            };
        }

        // Reset the timer (restart the 1 second countdown)
        _bandwidthDebounceTimer.Stop();
        _bandwidthDebounceTimer.Start();
    }

    private long SliderToKBps(double sliderValue)
    {
        // 0 = unlimited, 100 = blocked
        // Exponential scale for better control
        if (sliderValue <= 0) return -1; // Unlimited
        if (sliderValue >= 100) return 0; // Blocked

        // Map 1-99 to reasonable speed range (100MB/s down to 1KB/s)
        double ratio = (100 - sliderValue) / 100.0;
        double maxKBps = 102400; // 100 MB/s
        double minKBps = 1;

        return (long)(minKBps + (maxKBps - minKBps) * Math.Pow(ratio, 2));
    }

    private string FormatLimitText(long kbps)
    {
        if (kbps < 0) return "Unlimited";
        if (kbps == 0) return "Blocked";
        if (kbps >= 1024) return $"{kbps / 1024.0:F1} MB/s";
        return $"{kbps} KB/s";
    }

    private void ApplyBandwidthLimits()
    {
        if (!_selectedProcessId.HasValue) return;
        if (_processMap.TryGetValue(_selectedProcessId.Value, out var process))
        {
            var downloadLimit = _currentDownloadLimitKBps < 0 ? 0 : _currentDownloadLimitKBps;
            var uploadLimit = _currentUploadLimitKBps < 0 ? 0 : _currentUploadLimitKBps;

            if (downloadLimit == 0 && uploadLimit == 0)
            {
                BandwidthLimiter.Instance.RemoveLimit(process.ProcessName);
                StatusText.Text = $"Removed limits for {process.ProcessName}";
            }
            else
            {
                BandwidthLimiter.Instance.SetProcessLimit(process.ProcessName, downloadLimit, uploadLimit);
                BandwidthLimiter.Instance.IsEnabled = true;
                StatusText.Text = $"Set limits for {process.ProcessName}: ↓{FormatLimitText(_currentDownloadLimitKBps)} ↑{FormatLimitText(_currentUploadLimitKBps)}";
            }

            // Update process display
            process.DownloadLimitText = FormatLimitText(_currentDownloadLimitKBps);
            process.UploadLimitText_Limit = FormatLimitText(_currentUploadLimitKBps);
        }
    }

    private void SpeedPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr)
        {
            if (tagStr == "unlimited")
            {
                DownloadLimitSlider.Value = 0;
                UploadLimitSlider.Value = 0;
            }
            else if (long.TryParse(tagStr, out var kbps))
            {
                // Convert KBps to slider value
                var sliderValue = KBpsToSlider(kbps);
                DownloadLimitSlider.Value = sliderValue;
                UploadLimitSlider.Value = sliderValue;
            }
        }
    }

    private double KBpsToSlider(long kbps)
    {
        if (kbps <= 0) return 100; // Blocked
        if (kbps >= 102400) return 0; // Unlimited

        double maxKBps = 102400;
        double minKBps = 1;
        double ratio = Math.Sqrt((kbps - minKBps) / (maxKBps - minKBps));
        return 100 - (ratio * 100);
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
            if (_processMap.TryGetValue(processId, out var item))
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
            _processMap.TryGetValue(processId, out var process);
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

public class ProcessDisplayItem : INotifyPropertyChanged
{
    private double _downloadSpeed;
    private double _uploadSpeed;
    private string _downloadSpeedText = "0 B/s";
    private string _uploadSpeedText = "0 B/s";
    private int _connectionCount;
    private bool _isAutoKillEnabled;
    private string _downloadLimitText = "";
    private string _uploadLimitText = "";
    private bool _hasLimit;

    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public ImageSource? Icon { get; set; }

    public double DownloadSpeed
    {
        get => _downloadSpeed;
        set { _downloadSpeed = value; OnPropertyChanged(nameof(DownloadSpeed)); }
    }

    public double UploadSpeed
    {
        get => _uploadSpeed;
        set { _uploadSpeed = value; OnPropertyChanged(nameof(UploadSpeed)); }
    }

    public string DownloadSpeedText
    {
        get => _downloadSpeedText;
        set { _downloadSpeedText = value; OnPropertyChanged(nameof(DownloadSpeedText)); }
    }

    public string UploadSpeedText
    {
        get => _uploadSpeedText;
        set { _uploadSpeedText = value; OnPropertyChanged(nameof(UploadSpeedText)); }
    }

    public int ConnectionCount
    {
        get => _connectionCount;
        set { _connectionCount = value; OnPropertyChanged(nameof(ConnectionCount)); }
    }

    public bool IsAutoKillEnabled
    {
        get => _isAutoKillEnabled;
        set { _isAutoKillEnabled = value; OnPropertyChanged(nameof(IsAutoKillEnabled)); }
    }

    public string DownloadLimitText
    {
        get => _downloadLimitText;
        set
        {
            _downloadLimitText = value;
            OnPropertyChanged(nameof(DownloadLimitText));
            UpdateHasLimit();
        }
    }

    public string UploadLimitText_Limit
    {
        get => _uploadLimitText;
        set
        {
            _uploadLimitText = value;
            OnPropertyChanged(nameof(UploadLimitText_Limit));
            UpdateHasLimit();
        }
    }

    public bool HasLimit
    {
        get => _hasLimit;
        private set { _hasLimit = value; OnPropertyChanged(nameof(HasLimit)); }
    }

    public string LimitDisplayText => HasLimit ? $"↓{DownloadLimitText} ↑{UploadLimitText_Limit}" : "";

    private void UpdateHasLimit()
    {
        HasLimit = !string.IsNullOrEmpty(_downloadLimitText) || !string.IsNullOrEmpty(_uploadLimitText);
        OnPropertyChanged(nameof(LimitDisplayText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

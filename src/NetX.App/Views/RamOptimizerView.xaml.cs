using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using NetX.Core.Optimization;
using NetX.Core.Helpers;
using NetX.App.Helpers;

namespace NetX.App.Views;

public partial class RamOptimizerView : Page
{
    private readonly DispatcherTimer _updateTimer;
    private readonly RamOptimizer _ramOptimizer;
    private readonly ProcessKiller _processKiller;
    private readonly ChartDataCache _chartCache = ChartDataCache.Instance;
    private readonly ObservableCollection<double> _memoryHistory;
    private readonly ObservableCollection<OptimizationHistoryItem> _history = new();
    private readonly ObservableCollection<KillRuleDisplayItem> _killRules = new();
    private const int MaxHistoryPoints = 60;

    public RamOptimizerView()
    {
        InitializeComponent();

        _ramOptimizer = RamOptimizer.Instance;
        _ramOptimizer.OnOptimizationComplete += OnOptimizationComplete;

        _processKiller = ProcessKiller.Instance;
        _processKiller.OnProcessAutoKilled += OnProcessAutoKilled;

        // Use cached chart data for continuity across page navigation
        _memoryHistory = _chartCache.RamOptimizerHistory;
        _chartCache.InitializeCollection(_memoryHistory, MaxHistoryPoints);

        InitializeChart();
        LoadSettings();
        LoadProcessKillerSettings();

        // Setup timer for real-time updates
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();

        HistoryList.ItemsSource = _history;
        KillRulesList.ItemsSource = _killRules;

        UpdateMemoryInfo();
        UpdateProcessList();
        UpdateKillRulesList();

        Unloaded += (s, e) =>
        {
            _updateTimer.Stop();
            _ramOptimizer.OnOptimizationComplete -= OnOptimizationComplete;
            _processKiller.OnProcessAutoKilled -= OnProcessAutoKilled;
        };
    }

    private void InitializeChart()
    {
        var series = new LineSeries<double>
        {
            Values = _memoryHistory,
            Name = "Memory Usage",
            Stroke = new SolidColorPaint(SKColor.Parse("#4dc9ff")) { StrokeThickness = 2 },
            Fill = new SolidColorPaint(SKColor.Parse("#2000a8e8")),
            GeometryFill = null,
            GeometryStroke = null,
            LineSmoothness = 0.65,
            AnimationsSpeed = TimeSpan.Zero,
            EnableNullSplitting = false
        };

        MemoryChart.Series = new ISeries[] { series };

        MemoryChart.XAxes = new Axis[]
        {
            new Axis
            {
                IsVisible = false,
                AnimationsSpeed = TimeSpan.Zero
            }
        };

        MemoryChart.YAxes = new Axis[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#6b8eab")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#1a4dc9ff")),
                MinLimit = 0,
                MaxLimit = 100,
                Labeler = value => $"{value:F0}%",
                AnimationsSpeed = TimeSpan.Zero
            }
        };

        MemoryChart.AnimationsSpeed = TimeSpan.Zero;
    }

    private void LoadSettings()
    {
        AutoOptimizeToggle.IsChecked = _ramOptimizer.IsAutoOptimizeEnabled;
        ThresholdSlider.Value = _ramOptimizer.MemoryThresholdPercent;
        IntervalSlider.Value = _ramOptimizer.OptimizeIntervalMinutes;

        ThresholdText.Text = $"{_ramOptimizer.MemoryThresholdPercent}%";
        IntervalText.Text = $"{_ramOptimizer.OptimizeIntervalMinutes} min";
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        UpdateMemoryInfo();
    }

    private void UpdateMemoryInfo()
    {
        try
        {
            var info = _ramOptimizer.GetMemoryInfo();

            // Update stat cards
            TotalMemoryText.Text = FormatMemory(info.TotalMemoryMB);
            UsedMemoryText.Text = FormatMemory(info.UsedMemoryMB);
            AvailableMemoryText.Text = FormatMemory(info.AvailableMemoryMB);
            UsagePercentText.Text = $"{info.UsagePercent}%";
            GaugePercentText.Text = $"{info.UsagePercent}%";

            // Update gauge color based on usage
            if (info.UsagePercent >= 90)
            {
                UsagePercentText.Foreground = (Brush)FindResource("DangerBrush");
            }
            else if (info.UsagePercent >= 75)
            {
                UsagePercentText.Foreground = (Brush)FindResource("WarningBrush");
            }
            else
            {
                UsagePercentText.Foreground = (Brush)FindResource("AccentPrimaryBrush");
            }

            // Update gauge stroke dash offset (simulate percentage)
            double circumference = Math.PI * 120; // Diameter * PI
            double dashLength = (info.UsagePercent / 100.0) * circumference;
            MemoryGauge.StrokeDashArray = new DoubleCollection { dashLength / 12, (circumference - dashLength) / 12 };

            // Update chart history
            _memoryHistory.RemoveAt(0);
            _memoryHistory.Add(info.UsagePercent);
        }
        catch
        {
            // Ignore errors during update
        }
    }

    private void UpdateProcessList()
    {
        try
        {
            var topProcesses = _ramOptimizer.GetTopMemoryConsumers(15);
            var displayItems = topProcesses.Select(p => new ProcessMemoryDisplayItem
            {
                ProcessId = p.ProcessId,
                ProcessName = p.ProcessName,
                MemoryMB = p.MemoryMB,
                PrivateMemoryMB = p.PrivateMemoryMB,
                MemoryText = FormatMemory(p.MemoryMB),
                PrivateMemoryText = FormatMemory(p.PrivateMemoryMB),
                Icon = ProcessIconHelper.GetProcessIcon(p.ProcessName)
            }).ToList();

            ProcessList.ItemsSource = displayItems;
        }
        catch
        {
            // Ignore errors
        }
    }

    private static string FormatMemory(long mb)
    {
        if (mb >= 1024)
            return $"{mb / 1024.0:F1} GB";
        return $"{mb} MB";
    }

    private void OptimizeNowBtn_Click(object sender, RoutedEventArgs e)
    {
        OptimizeNowBtn.IsEnabled = false;
        OptimizeNowBtn.Content = "Optimizing...";

        // Run optimization on background thread
        Task.Run(() =>
        {
            var result = _ramOptimizer.OptimizeNow();

            Dispatcher.Invoke(() =>
            {
                OptimizeNowBtn.IsEnabled = true;
                OptimizeNowBtn.Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new System.Windows.Shapes.Path
                        {
                            Data = (Geometry)FindResource("OptimizeIcon"),
                            Fill = System.Windows.Media.Brushes.White,
                            Width = 18,
                            Height = 18,
                            Stretch = Stretch.Uniform,
                            Margin = new Thickness(0, 0, 8, 0)
                        },
                        new TextBlock
                        {
                            Text = (string)FindResource("RamOptimizer_OptimizeNow"),
                            FontSize = 14,
                            FontWeight = FontWeights.SemiBold
                        }
                    }
                };

                UpdateProcessList();
            });
        });
    }

    private void OnOptimizationComplete(OptimizeResult result)
    {
        Dispatcher.Invoke(() =>
        {
            // Update last optimize text
            LastOptimizeText.Text = $"Last: {result.EndTime:HH:mm:ss} - Freed {FormatMemory(Math.Max(0, result.MemoryFreedMB))}";

            // Add to history
            _history.Insert(0, new OptimizationHistoryItem
            {
                TimeText = result.EndTime.ToString("HH:mm:ss"),
                ResultText = $"{result.ProcessesOptimized} processes optimized",
                FreedText = result.MemoryFreedMB > 0 ? $"+{FormatMemory(result.MemoryFreedMB)}" : "0 MB"
            });

            // Keep only last 20 items
            while (_history.Count > 20)
            {
                _history.RemoveAt(_history.Count - 1);
            }

            UpdateMemoryInfo();
        });
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateProcessList();
    }

    private void OptimizeProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int processId)
        {
            _ramOptimizer.OptimizeProcess(processId);
            UpdateProcessList();
        }
    }

    private void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int processId)
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(processId);
                var name = process.ProcessName;

                var result = MessageBox.Show(
                    $"End process {name} (PID: {processId})?\n\nThis will immediately terminate the process.",
                    "End Process",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    process.Kill();
                    UpdateProcessList();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to end process: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private void AutoOptimizeToggle_Click(object sender, RoutedEventArgs e)
    {
        _ramOptimizer.IsAutoOptimizeEnabled = AutoOptimizeToggle.IsChecked == true;
    }

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Ensure controls and fields are initialized before accessing them
        if (ThresholdText == null || _ramOptimizer == null) return;

        var value = (int)e.NewValue;
        ThresholdText.Text = $"{value}%";
        _ramOptimizer.MemoryThresholdPercent = value;
    }

    private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Ensure controls and fields are initialized before accessing them
        if (IntervalText == null || _ramOptimizer == null) return;

        var value = (int)e.NewValue;
        IntervalText.Text = $"{value} min";
        _ramOptimizer.OptimizeIntervalMinutes = value;
    }

    #region Process Killer

    private void LoadProcessKillerSettings()
    {
        SmartKillToggle.IsChecked = _processKiller.IsSmartKillEnabled;
        AutoKillToggle.IsChecked = _processKiller.IsAutoKillEnabled;
    }

    private void UpdateKillRulesList()
    {
        _killRules.Clear();
        foreach (var rule in _processKiller.GetKillRules())
        {
            _killRules.Add(new KillRuleDisplayItem
            {
                ProcessName = rule.ProcessName,
                Reason = rule.Reason,
                KillCount = rule.KillCount,
                KillCountText = rule.KillCount > 0 ? $"×{rule.KillCount}" : "",
                LastKilled = rule.LastKilled
            });
        }
    }

    private void OnProcessAutoKilled(string processName, string reason)
    {
        Dispatcher.Invoke(() =>
        {
            // Add to history
            _history.Insert(0, new OptimizationHistoryItem
            {
                TimeText = DateTime.Now.ToString("HH:mm:ss"),
                ResultText = $"Auto-killed: {processName}",
                FreedText = reason
            });

            // Keep only last 20 items
            while (_history.Count > 20)
            {
                _history.RemoveAt(_history.Count - 1);
            }

            UpdateKillRulesList();
        });
    }

    private void SmartKillToggle_Click(object sender, RoutedEventArgs e)
    {
        _processKiller.IsSmartKillEnabled = SmartKillToggle.IsChecked == true;
    }

    private void AutoKillToggle_Click(object sender, RoutedEventArgs e)
    {
        _processKiller.IsAutoKillEnabled = AutoKillToggle.IsChecked == true;
    }

    private void AddKillRule_Click(object sender, RoutedEventArgs e)
    {
        // Show input dialog for process name
        var dialog = new Dialogs.InputDialog(
            (string)FindResource("RamOptimizer_AddKillRuleTitle") ?? "Add Kill Rule",
            (string)FindResource("RamOptimizer_AddKillRuleMessage") ?? "Enter process name to auto-kill (without .exe):");

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
        {
            var processName = dialog.ResponseText.Trim().Replace(".exe", "");

            if (ProcessKiller.IsProtectedProcess(processName))
            {
                MessageBox.Show(
                    $"Cannot add '{processName}' - it is a protected system process.",
                    "Protected Process",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _processKiller.AddKillRule(processName, "User requested");
            UpdateKillRulesList();
        }
    }

    private void RemoveKillRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string processName)
        {
            _processKiller.RemoveKillRule(processName);
            UpdateKillRulesList();
        }
    }

    #endregion
}

public class ProcessMemoryDisplayItem
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public long MemoryMB { get; set; }
    public long PrivateMemoryMB { get; set; }
    public string MemoryText { get; set; } = "";
    public string PrivateMemoryText { get; set; } = "";
    public ImageSource? Icon { get; set; }
}

public class OptimizationHistoryItem
{
    public string TimeText { get; set; } = "";
    public string ResultText { get; set; } = "";
    public string FreedText { get; set; } = "";
}

public class KillRuleDisplayItem
{
    public string ProcessName { get; set; } = "";
    public string Reason { get; set; } = "";
    public int KillCount { get; set; }
    public string KillCountText { get; set; } = "";
    public DateTime? LastKilled { get; set; }
}

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
        _processKiller.ProcessAutoKilled += OnProcessAutoKilled;

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
            _processKiller.ProcessAutoKilled -= OnProcessAutoKilled;
        };
    }

    private static string T(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as string ?? fallback;

    private static string F(string key, string fallback, params object[] args)
    {
        try { return string.Format(T(key, fallback), args); }
        catch (FormatException) { return string.Format(fallback, args); }
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
        OptimizeNowBtn.Content = T("Common_Processing", "Processing...");

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
        // Raised on a worker/timer thread — queue the UI update instead of blocking it.
        Dispatcher.InvokeAsync(() =>
        {
            // Update last optimize text
            LastOptimizeText.Text = F("Ram_LastOptimized", "Last: {0} – freed {1}",
                result.EndTime.ToString("HH:mm:ss"), FormatMemory(Math.Max(0, result.MemoryFreedMB)));

            // Add to history
            _history.Insert(0, new OptimizationHistoryItem
            {
                TimeText = result.EndTime.ToString("HH:mm:ss"),
                ResultText = F("Ram_ProcessesTrimmed", "{0} processes trimmed", result.ProcessesOptimized) +
                             (result.StandbyCleared ? " • " + T("Ram_StandbyCleared", "standby cache cleared") : ""),
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

    private async void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int processId)
            return;

        var name = (btn.DataContext as ProcessMemoryDisplayItem)?.ProcessName ?? $"PID {processId}";
        var title = T("Ram_EndProcessTitle", "End Process");

        // This list is "top memory users" — it routinely contains dwm, svchost,
        // explorer… Ending those crashes or destabilises Windows (the old code
        // didn't check at all).
        if (ProcessKiller.IsProtectedProcess(name))
        {
            MessageBox.Show(
                F("Ram_ProtectedProcess", "{0} is a protected Windows process and cannot be ended here.", name),
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            F("Ram_ConfirmEndProcess",
                "End {0} (PID {1})?\n\nOnly this process is closed, not the processes it started. Any unsaved work in it will be lost.",
                name, processId),
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        btn.IsEnabled = false;
        try
        {
            // Kill + wait for exit off the UI thread; the result is the real outcome.
            var ended = await Task.Run(() => _processKiller.KillProcess(processId));
            if (!ended)
            {
                MessageBox.Show(
                    F("Ram_EndProcessFailed",
                        "Could not end {0}. Windows may be protecting it, or it is still shutting down.", name),
                    T("Common_Error", "Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            btn.IsEnabled = true;
            UpdateProcessList();
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

    private void OnProcessAutoKilled(object? sender, ProcessAutoKilledEventArgs e)
    {
        // Raised on the watchdog thread — queue the UI update instead of blocking it.
        Dispatcher.InvokeAsync(() =>
        {
            var reason = e.Reason switch
            {
                AutoKillReason.NotResponding => F("Ram_ReasonNotResponding", "Not responding for {0} s", e.Detail),
                AutoKillReason.ExcessiveMemory => F("Ram_ReasonMemory", "Using {0} MB", e.Detail),
                _ => T("Ram_ReasonRule", "Kill rule")
            };

            // Add to history: what was closed and why
            _history.Insert(0, new OptimizationHistoryItem
            {
                TimeText = e.Time.ToString("HH:mm:ss"),
                ResultText = F("Ram_AutoClosed", "Auto-closed: {0}", e.ProcessName),
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
        bool turnOn = SmartKillToggle.IsChecked == true;

        // Closing apps loses unsaved work — make the user opt in knowingly.
        if (turnOn && !_processKiller.IsSmartKillEnabled)
        {
            var confirm = MessageBox.Show(
                F("Ram_SmartKillConfirm",
                    "Turn on Smart Kill?\n\nApps whose window stays \"Not Responding\" for {0} seconds or more will be closed automatically. Unsaved work in them will be lost.\n\nNever closed: the app you are using right now, Windows and Explorer processes, WebView2 (used by Outlook and Teams) and WinXTools itself. Every app that gets closed is listed in the history.",
                    (int)ProcessKiller.FrozenKillThreshold.TotalSeconds),
                T("RamOptimizer_SmartKill", "Smart Kill"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                SmartKillToggle.IsChecked = false;
                return;
            }
        }

        _processKiller.IsSmartKillEnabled = turnOn;
    }

    private void AutoKillToggle_Click(object sender, RoutedEventArgs e)
    {
        bool turnOn = AutoKillToggle.IsChecked == true;

        // Turning it on immediately closes every running app that matches a rule.
        var rules = _processKiller.GetKillRules();
        if (turnOn && !_processKiller.IsAutoKillEnabled && rules.Count > 0)
        {
            var names = string.Join("\n", rules.Take(10).Select(r => "• " + r.ProcessName));
            if (rules.Count > 10)
                names += $"\n… (+{rules.Count - 10})";

            var confirm = MessageBox.Show(
                F("Ram_AutoKillConfirm",
                    "Turn on Auto Kill?\n\nThese apps will be closed now and every time they start. Unsaved work in them will be lost:\n{0}",
                    names),
                T("RamOptimizer_AutoKill", "Auto Kill Rules"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                AutoKillToggle.IsChecked = false;
                return;
            }
        }

        _processKiller.IsAutoKillEnabled = turnOn;
    }

    private async void AddKillRule_Click(object sender, RoutedEventArgs e)
    {
        var title = T("RamOptimizer_AddKillRuleTitle", "Add Kill Rule");

        // Show input dialog for process name
        var dialog = new Dialogs.InputDialog(
            title,
            T("RamOptimizer_AddKillRuleMessage", "Enter process name to auto-kill (without .exe):"));

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.ResponseText))
            return;

        var entered = dialog.ResponseText.Trim();
        var bareName = entered.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? entered[..^4] : entered;

        if (ProcessKiller.IsAutoKillExcluded(bareName))
        {
            MessageBox.Show(
                F("Ram_ProtectedRule", "Cannot add \"{0}\": it is a protected Windows process.", bareName),
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var processName = ProcessKiller.NormalizeRuleName(entered);
        if (processName == null)
        {
            MessageBox.Show(
                F("Ram_InvalidProcessName", "\"{0}\" is not a valid program name. Type the name without .exe, for example: notepad", entered),
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // Adding a rule closes running copies right away — say so first.
        var confirm = MessageBox.Show(
            F("Ram_AddRuleConfirm",
                "Add \"{0}\" to the kill rules?\n\nIf it is running it will be closed now (unsaved work will be lost), and it will be closed automatically whenever it starts while Auto Kill is on.",
                processName),
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        await Task.Run(() => _processKiller.AddKillRule(processName, "User requested"));
        UpdateKillRulesList();
        UpdateProcessList();
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

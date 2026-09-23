using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using NetX.Core.Optimization;
using NetX.App.Helpers;

namespace NetX.App.Views;

/// <summary>
/// RAM page. Every reading and every action runs off the UI thread; the page
/// only subscribes to engine events while it is loaded, and anything that
/// finishes after the user has left the page is dropped (the result is still
/// kept by <see cref="RamOptimizer"/> and shown when the page opens again).
/// </summary>
public partial class RamOptimizerView : Page
{
    private const int MaxChartPoints = 60;
    private const int TopProcessCount = 15;
    private const int MaxHistoryItems = 20;
    private static readonly TimeSpan StaleChartAfter = TimeSpan.FromMinutes(1);

    // When the chart got its last point (any page instance): older data is
    // dropped instead of being joined to "now" as if it were continuous.
    private static DateTime _lastChartSampleUtc = DateTime.MinValue;

    private readonly RamOptimizer _ram = RamOptimizer.Instance;
    private readonly ProcessKiller _killer = ProcessKiller.Instance;
    private readonly DispatcherTimer _refreshTimer;
    private readonly ObservableCollection<double> _usageHistory;
    private readonly ObservableCollection<OptimizationHistoryItem> _history = new();
    private readonly ObservableCollection<KillRuleDisplayItem> _killRules = new();
    private readonly ObservableCollection<OperationLine> _lastRunLines = new();

    private readonly Brush _accentBrush;
    private readonly Brush _accentGradientBrush;
    private readonly Brush _warningBrush;
    private readonly Brush _dangerBrush;
    private readonly Brush _successBrush;
    private readonly Brush _mutedBrush;

    // True while controls are being filled from settings (and during
    // InitializeComponent), so change handlers don't write the values back.
    private bool _applyingSettings = true;
    private bool _isActive;
    private bool _refreshInFlight;
    private bool _listInFlight;
    private bool _listLoadedOnce;
    private bool _cleanInFlight;
    private bool _trackingRun;

    public RamOptimizerView()
    {
        InitializeComponent();

        _accentBrush = (Brush)FindResource("AccentPrimaryBrush");
        _accentGradientBrush = (Brush)FindResource("AccentGradientBrush");
        _warningBrush = (Brush)FindResource("WarningBrush");
        _dangerBrush = (Brush)FindResource("DangerBrush");
        _successBrush = (Brush)FindResource("SuccessBrush");
        _mutedBrush = (Brush)FindResource("TextTertiaryBrush");

        // The fallback icon is drawn with WPF visuals: create it here, on the UI
        // thread, so the background icon lookups only ever reuse it.
        ProcessIconHelper.GetDefaultIcon();

        _usageHistory = ChartDataCache.Instance.RamOptimizerHistory;
        if (DateTime.UtcNow - _lastChartSampleUtc > StaleChartAfter)
            _usageHistory.Clear();
        ChartDataCache.Instance.InitializeCollection(_usageHistory, MaxChartPoints);
        InitializeChart();

        HistoryList.ItemsSource = _history;
        KillRulesList.ItemsSource = _killRules;
        LastRunOps.ItemsSource = _lastRunLines;

        bool elevated = MemoryCleaner.IsElevated;
        NotAdminBanner.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
        CleanNowBtn.IsEnabled = elevated;

        ApplySettingsToControls();

        // First paint with real numbers straight away (microseconds, no process
        // snapshot), so the cards never show placeholder zeros.
        ApplyMemoryInfo(MemoryCleaner.ReadMemoryInfo(includeCompressedStore: false));

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => _ = RefreshMemoryAsync();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    #region Lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isActive) return;
        _isActive = true;

        _ram.OnOptimizationComplete += OnOptimizationComplete;
        _killer.ProcessAutoKilled += OnProcessAutoKilled;

        // Returning user: everything below comes from the engines, not from this page.
        LoadProcessKillerSettings();
        UpdateKillRulesList();
        RebuildHistory();
        ShowLastResult(_ram.GetRecentResults().FirstOrDefault(), joinedRunningCleanup: false);

        if (IsVisible)
            _refreshTimer.Start();
        _ = RefreshMemoryAsync();
        _ = RefreshProcessListAsync();
        _ = TrackRunningCleanupAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isActive = false;
        _refreshTimer.Stop();
        _ram.OnOptimizationComplete -= OnOptimizationComplete;
        _killer.ProcessAutoKilled -= OnProcessAutoKilled;

        // Don't lose a slider change made just before leaving the page.
        _ = Task.Run(_ram.FlushSettings);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_isActive) return;

        // Hidden (e.g. main window hidden to the tray): stop polling.
        if (IsVisible)
        {
            _refreshTimer.Start();
            _ = RefreshMemoryAsync();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    /// <summary>A cleanup started elsewhere (automatic, Dashboard, or before the user left the page) shows as busy here too.</summary>
    private async Task TrackRunningCleanupAsync()
    {
        if (_trackingRun || _ram.CurrentRun is not { } run) return;
        _trackingRun = true;
        UpdateCleanButton();
        try
        {
            await run;
        }
        catch
        {
            // Its result (or failure) arrives through OnOptimizationComplete.
        }
        finally
        {
            _trackingRun = false;
            if (_isActive) UpdateCleanButton();
        }
    }

    #endregion

    #region Memory readings

    private void InitializeChart()
    {
        MemoryChart.Series = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = _usageHistory,
                Name = Loc.T("RamOptimizer_Usage", "Usage"),
                Stroke = new SolidColorPaint(SKColor.Parse("#4dc9ff")) { StrokeThickness = 2 },
                Fill = new SolidColorPaint(SKColor.Parse("#2000a8e8")),
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0.65,
                AnimationsSpeed = TimeSpan.Zero,
                EnableNullSplitting = false
            }
        };

        MemoryChart.XAxes = new Axis[] { new Axis { IsVisible = false, AnimationsSpeed = TimeSpan.Zero } };
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

    private async Task RefreshMemoryAsync()
    {
        if (_refreshInFlight || !_isActive) return;
        _refreshInFlight = true;
        try
        {
            var info = await Task.Run(() => MemoryCleaner.ReadMemoryInfo());
            if (!_isActive) return;
            ApplyMemoryInfo(info);
            AddChartPoint(info.UsagePercent);
        }
        catch (Exception ex)
        {
            // Keep showing the last good numbers; the next tick tries again.
            Debug.WriteLine($"[RamOptimizerView] Refresh failed: {ex.Message}");
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private void AddChartPoint(double percent)
    {
        while (_usageHistory.Count >= MaxChartPoints)
            _usageHistory.RemoveAt(0);
        _usageHistory.Add(percent);
        _lastChartSampleUtc = DateTime.UtcNow;
    }

    private void ApplyMemoryInfo(MemoryInfo info)
    {
        if (info.TotalMemoryMB <= 0)
            return; // nothing trustworthy to show — keep the previous values

        TotalMemoryText.Text = FormatMB(info.TotalMemoryMB);
        TotalCaption.Text = info.InstalledMemoryMB > info.TotalMemoryMB
            ? Loc.F("Ram_InstalledCaption", "of {0} installed", FormatMB(info.InstalledMemoryMB))
            : "";

        UsedMemoryText.Text = FormatMB(info.UsedMemoryMB);
        UsedCaption.Text = info.CommitLimitMB > 0
            ? Loc.F("Ram_CommitCaption", "Committed {0}", FormatPair(info.CommitUsedMB, info.CommitLimitMB))
            : "";

        AvailableMemoryText.Text = FormatMB(info.AvailableMemoryMB);
        FreeCaption.Text = info.HasListDetail ? Loc.F("Ram_FreeCaption", "Free {0}", FormatMB(info.FreeMemoryMB)) : "";
        CacheCaption.Text = info.HasListDetail ? Loc.F("Ram_CacheCaption", "Cache {0}", FormatMB(info.StandbyMemoryMB)) : "";

        var percent = Math.Clamp(info.UsagePercent, 0, 100);
        UsagePercentText.Text = $"{percent}%";
        GaugePercentText.Text = $"{percent}%";

        var levelBrush = percent >= 90 ? _dangerBrush : percent >= 75 ? _warningBrush : null;
        UsagePercentText.Foreground = levelBrush ?? _accentBrush;
        MemoryGauge.Stroke = levelBrush ?? _accentGradientBrush;

        // The stroke runs along the ellipse's centre line (diameter minus one
        // stroke width); dash lengths are in multiples of the stroke width.
        const double diameter = 120, thickness = 12;
        double circumference = Math.PI * (diameter - thickness);
        double filled = percent / 100.0 * circumference;
        MemoryGauge.StrokeDashArray = new DoubleCollection { filled / thickness, (circumference - filled) / thickness + 1 };

        if (info.HasListDetail)
        {
            BreakdownPanel.Visibility = Visibility.Visible;
            BreakdownUnavailableText.Visibility = Visibility.Collapsed;

            SegInUseColumn.Width = Star(info.InUseMemoryMB);
            SegModifiedColumn.Width = Star(info.ModifiedMemoryMB);
            SegStandbyColumn.Width = Star(info.StandbyMemoryMB);
            SegFreeColumn.Width = Star(info.FreeMemoryMB);

            SegInUseText.Text = FormatMB(info.InUseMemoryMB);
            SegModifiedText.Text = FormatMB(info.ModifiedMemoryMB);
            SegStandbyText.Text = FormatMB(info.StandbyMemoryMB);
            SegFreeText.Text = FormatMB(info.FreeMemoryMB);
            SegStandbyLowText.Text = info.StandbyLowPriorityMB >= 0
                ? Loc.F("Ram_LowPriorityPart", "low priority {0}", FormatMB(info.StandbyLowPriorityMB))
                : "";
        }
        else
        {
            BreakdownPanel.Visibility = Visibility.Collapsed;
            BreakdownUnavailableText.Visibility = Visibility.Visible;
        }

        if (info.CompressedMemoryMB > 0)
        {
            CompressedText.Text = Loc.F("Ram_CompressedValue", "Compressed store: {0}", FormatMB(info.CompressedMemoryMB));
            CompressedText.Visibility = Visibility.Visible;
        }
        else if (info.CompressedMemoryMB == 0)
        {
            CompressedText.Visibility = Visibility.Collapsed; // compression off
        }
        // -1: not read this time (cheap first paint) — keep what was shown
    }

    private static GridLength Star(long value) => new(Math.Max(0, value), GridUnitType.Star);

    private void BreakdownBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Border.CornerRadius doesn't clip its children: round the bar itself.
        BreakdownBar.Clip = new RectangleGeometry(new Rect(e.NewSize), 6, 6);
    }

    #endregion

    #region Top processes

    private async Task RefreshProcessListAsync()
    {
        if (_listInFlight || !_isActive) return;
        _listInFlight = true;
        RefreshBtn.IsEnabled = false;
        if (!_listLoadedOnce)
            ShowListMessage(Loc.T("Ram_ListLoading", "Loading…"));

        // Localized strings are read here, on the UI thread.
        var texts = new RowTexts(
            Loc.T("Ram_TrimTip", "Trim: move this app's memory out of RAM (it loads back what it needs)"),
            Loc.T("Ram_EndTip", "End this process"),
            Loc.T("Ram_RowProtectedTip", "Windows process or WinXTools itself — not available here"));

        try
        {
            var rows = await Task.Run(() => BuildRows(MemoryCleaner.GetTopProcesses(TopProcessCount), texts));
            if (!_isActive) return;

            ProcessList.ItemsSource = rows;
            _listLoadedOnce = true;
            ShowListMessage(rows.Count == 0 ? Loc.T("Ram_ListEmpty", "Couldn't read the list of processes.") : null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RamOptimizerView] Process list failed: {ex.Message}");
            if (_isActive && !_listLoadedOnce)
                ShowListMessage(Loc.T("Ram_ListEmpty", "Couldn't read the list of processes."));
        }
        finally
        {
            _listInFlight = false;
            RefreshBtn.IsEnabled = true;
        }
    }

    private sealed record RowTexts(string Trim, string End, string Protected);

    private static List<ProcessMemoryDisplayItem> BuildRows(List<ProcessMemoryInfo> processes, RowTexts texts) =>
        processes.Select(p => new ProcessMemoryDisplayItem
        {
            ProcessId = p.ProcessId,
            ProcessName = p.ProcessName,
            CreateTime = p.CreateTime,
            MemoryMB = p.MemoryMB,
            PrivateMemoryMB = p.PrivateMemoryMB,
            MemoryText = FormatMB(p.MemoryMB),
            PrivateMemoryText = FormatMB(p.PrivateMemoryMB),
            DetailText = $"{p.ProcessName} · PID {p.ProcessId}",
            CanTrim = p.CanTrim,
            CanEnd = p.CanEnd,
            TrimToolTip = p.CanTrim ? texts.Trim : texts.Protected,
            EndToolTip = p.CanEnd ? texts.End : texts.Protected,
            Icon = ProcessIconHelper.GetProcessIcon(p.ProcessName)
        }).ToList();

    private void ShowListMessage(string? message)
    {
        ProcessListMessage.Text = message ?? "";
        ProcessListMessage.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowProcessAction(string message, bool isProblem)
    {
        ProcessActionText.Text = message;
        ProcessActionText.Foreground = isProblem ? _warningBrush : _successBrush;
        ProcessActionText.Visibility = Visibility.Visible;
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => _ = RefreshProcessListAsync();

    private async void TrimProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProcessMemoryDisplayItem item } button || !item.CanTrim)
            return;

        button.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _ram.TrimProcess(item.ProcessId, item.ProcessName, item.CreateTime));
            if (!_isActive) return;

            ShowProcessAction(DescribeTrim(item.ProcessName, result), result.Outcome != ProcessTrimOutcome.Trimmed);
            await RefreshProcessListAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RamOptimizerView] Trim failed: {ex.Message}");
            if (_isActive)
                ShowProcessAction(Loc.F("Ram_TrimFailed", "Couldn't trim {0}.", item.ProcessName), true);
        }
        finally
        {
            button.IsEnabled = item.CanTrim;
        }
    }

    /// <summary>User-facing text for a per-process trim (public for the test harness).</summary>
    public static string DescribeTrim(string name, ProcessTrimResult result) => result.Outcome switch
    {
        ProcessTrimOutcome.Trimmed when result.BeforeMB >= 0 && result.AfterMB >= 0 =>
            Loc.F("Ram_TrimDone", "{0}: RAM in use {1} → {2}. It loads back what it needs.",
                name, FormatMB(result.BeforeMB), FormatMB(result.AfterMB)),
        ProcessTrimOutcome.Trimmed =>
            Loc.F("Ram_TrimDone", "{0}: RAM in use {1} → {2}. It loads back what it needs.", name, "–", "–"),
        ProcessTrimOutcome.Protected => Loc.F("Ram_TrimProtected", "{0} is a Windows process, so it isn't trimmed.", name),
        ProcessTrimOutcome.Self => Loc.T("Ram_TrimSelf", "WinXTools doesn't trim itself — that would make this window stutter."),
        ProcessTrimOutcome.AccessDenied => Loc.F("Ram_TrimDenied", "Windows didn't allow trimming {0}.", name),
        ProcessTrimOutcome.Gone => Loc.F("Ram_ProcessGone", "{0} had already closed — nothing was done.", name),
        _ => Loc.F("Ram_TrimFailed", "Couldn't trim {0}.", name)
    };

    private async void EndProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProcessMemoryDisplayItem item } button)
            return;

        var name = item.ProcessName;
        var title = Loc.T("Ram_EndProcessTitle", "End Process");

        // This list routinely contains dwm, svchost, explorer… ending those
        // crashes or destabilises Windows.
        if (!item.CanEnd || ProcessKiller.IsProtectedProcess(name))
        {
            MessageBox.Show(
                Loc.F("Ram_ProtectedProcess", "{0} is a protected Windows process and cannot be ended here.", name),
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            Loc.F("Ram_ConfirmEndProcess",
                "End {0} (PID {1})?\n\nOnly this process is closed, not the processes it started. Any unsaved work in it will be lost.",
                name, item.ProcessId),
            title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        button.IsEnabled = false;
        try
        {
            // Ends exactly the process that was listed: if its PID was reused by
            // another program since the list was taken, nothing is ended.
            var outcome = await Task.Run(() => _killer.EndProcess(item.ProcessId, name, item.CreateTime));
            if (!_isActive) return;

            switch (outcome)
            {
                case ProcessEndResult.Ended:
                    ShowProcessAction(Loc.F("Ram_EndDone", "{0} was ended.", name), false);
                    break;
                case ProcessEndResult.AlreadyGone:
                    ShowProcessAction(Loc.F("Ram_ProcessGone", "{0} had already closed — nothing was done.", name), true);
                    break;
                case ProcessEndResult.Protected:
                    MessageBox.Show(
                        Loc.F("Ram_ProtectedProcess", "{0} is a protected Windows process and cannot be ended here.", name),
                        title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                default:
                    MessageBox.Show(
                        Loc.F("Ram_EndProcessFailed", "Could not end {0}. Windows may be protecting it, or it is still shutting down.", name),
                        Loc.T("Common_Error", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RamOptimizerView] End process failed: {ex.Message}");
            if (_isActive)
                MessageBox.Show(
                    Loc.F("Ram_EndProcessFailed", "Could not end {0}. Windows may be protecting it, or it is still shutting down.", name),
                    Loc.T("Common_Error", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.IsEnabled = item.CanEnd;
            _ = RefreshProcessListAsync();
        }
    }

    #endregion

    #region Clean now and results

    private async void CleanNowBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanInFlight) return;

        var items = _ram.CleanItems;
        if (items == MemoryCleanItems.None)
        {
            ShowNotice(Loc.T("Ram_NothingSelected", "Choose at least one item under “What to clean”."));
            return;
        }

        _cleanInFlight = true;
        UpdateCleanButton();
        var clickedAt = DateTime.Now;
        try
        {
            // Runs on a worker thread; a cleanup already running (automatic,
            // Dashboard, double click) is joined instead of started twice.
            var result = await _ram.RunAsync(items, OptimizeTrigger.Manual);
            if (!_isActive) return;

            ShowLastResult(result, joinedRunningCleanup: result.StartTime < clickedAt.AddMilliseconds(-100));
            RebuildHistory();
            _ = RefreshMemoryAsync();
            _ = RefreshProcessListAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RamOptimizerView] Cleanup failed: {ex.Message}");
            if (_isActive)
                ShowNotice(Loc.T("Ram_CleanFailed", "The cleanup couldn't run. Nothing was changed."));
        }
        finally
        {
            _cleanInFlight = false;
            if (_isActive) UpdateCleanButton();
        }
    }

    private void UpdateCleanButton()
    {
        bool busy = _cleanInFlight || _trackingRun;
        CleanNowBtn.IsEnabled = !busy && MemoryCleaner.IsElevated;
        // SetResourceReference keeps the label following a language switch.
        CleanNowText.SetResourceReference(TextBlock.TextProperty, busy ? "Ram_Cleaning" : "RamOptimizer_OptimizeNow");
    }

    private void OnOptimizationComplete(OptimizeResult result)
    {
        // Raised on a worker thread — queue the UI update, never block the worker.
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isActive) return;
            // A click on this page shows its own result (and whether it joined a run).
            if (!_cleanInFlight)
                ShowLastResult(result, joinedRunningCleanup: false);
            RebuildHistory();
            _ = RefreshMemoryAsync();
        });
    }

    private void ShowNotice(string message)
    {
        LastRunNotice.Text = message;
        LastRunNotice.Visibility = Visibility.Visible;
    }

    private void ShowLastResult(OptimizeResult? result, bool joinedRunningCleanup)
    {
        LastRunNotice.Visibility = Visibility.Collapsed;
        _lastRunLines.Clear();

        if (result == null)
        {
            LastRunText.SetResourceReference(TextBlock.TextProperty, "RamOptimizer_NeverOptimized");
            LastRunSummary.Visibility = Visibility.Collapsed;
            return;
        }

        LastRunText.Text = Loc.F("Ram_LastRun", "Last cleanup: {0} ({1})",
            result.EndTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture), TriggerText(result));

        if (joinedRunningCleanup)
            ShowNotice(Loc.T("Ram_JoinedRun", "A cleanup was already running, so this is its result."));

        LastRunSummary.Text = SummaryText(result);
        LastRunSummary.Foreground = result.AnyOperationSucceeded ? _successBrush : _warningBrush;
        LastRunSummary.Visibility = Visibility.Visible;

        foreach (var operation in result.Operations)
        {
            _lastRunLines.Add(new OperationLine
            {
                Glyph = operation.Status switch
                {
                    MemoryOperationStatus.Succeeded => "✓",
                    MemoryOperationStatus.Failed => "✗",
                    _ => "–"
                },
                GlyphBrush = operation.Status switch
                {
                    MemoryOperationStatus.Succeeded => _successBrush,
                    MemoryOperationStatus.Failed => _dangerBrush,
                    _ => _mutedBrush
                },
                Text = DescribeOperation(operation)
            });
        }
    }

    private static string TriggerText(OptimizeResult result) => result.Trigger switch
    {
        OptimizeTrigger.AutoMemoryLoad => Loc.F("Ram_TriggerAutoLoad", "automatic – memory {0}%", result.MemoryBefore.UsagePercent),
        OptimizeTrigger.AutoLowFreeMemory => Loc.T("Ram_TriggerAutoFree", "automatic – free memory low"),
        _ => Loc.T("Ram_TriggerManual", "by you")
    };

    /// <summary>
    /// One honest headline: the rise in available memory (never inflated —
    /// "no increase" when it didn't rise) and the free-memory change, which is
    /// where a cache purge shows up. Public for the test harness.
    /// </summary>
    public static string SummaryText(OptimizeResult result)
    {
        if (!result.AnyOperationSucceeded)
            return Loc.T("Ram_NothingCleaned", "Nothing was cleaned.");

        var gain = result.MemoryFreedMB > 0 ? "+" + FormatMB(result.MemoryFreedMB) : Loc.T("Ram_NoGain", "no increase");
        if (result.MemoryBefore.HasListDetail && result.MemoryAfter.HasListDetail)
            return Loc.F("Ram_ResultSummary", "Available {0} · free {1} → {2}",
                gain, FormatMB(result.MemoryBefore.FreeMemoryMB), FormatMB(result.MemoryAfter.FreeMemoryMB));
        return Loc.F("Ram_ResultSummaryBasic", "Available {0}", gain);
    }

    private static string OperationName(MemoryOperation operation) => operation switch
    {
        MemoryOperation.CombinePages => Loc.T("Ram_OpCombine", "Combine identical pages"),
        MemoryOperation.TrimWorkingSets => Loc.T("Ram_OpTrim", "Trim background apps"),
        MemoryOperation.FlushSystemFileCache => Loc.T("Ram_OpFileCache", "System file cache"),
        MemoryOperation.FlushModifiedList => Loc.T("Ram_OpFlushModified", "Write pending changes to disk"),
        MemoryOperation.PurgeStandbyList => Loc.T("Ram_OpStandby", "Entire cache (standby list)"),
        _ => Loc.T("Ram_OpLowStandby", "Low-priority cache")
    };

    /// <summary>One localized line per operation — never raw exception text. Public for the test harness.</summary>
    public static string DescribeOperation(MemoryOperationResult operation)
    {
        var name = OperationName(operation.Operation);

        if (operation.Status == MemoryOperationStatus.Succeeded)
        {
            var amount = FormatMB(operation.AmountMB);
            return operation.Operation switch
            {
                MemoryOperation.CombinePages => Loc.F("Ram_ResCombine", "{0}: {1} merged", name, amount),
                MemoryOperation.TrimWorkingSets => Loc.F("Ram_ResTrim", "{0}: {1} apps trimmed, {2} left alone · available +{3}",
                    name, operation.ProcessesTrimmed, operation.ProcessesSkipped, amount),
                MemoryOperation.FlushSystemFileCache => Loc.F("Ram_ResAvailable", "{0}: available +{1}", name, amount),
                MemoryOperation.FlushModifiedList => Loc.F("Ram_ResWritten", "{0}: {1} written to disk", name, amount),
                _ => Loc.F("Ram_ResReleased", "{0}: {1} of cache released", name, amount)
            };
        }

        var reason = operation.Error switch
        {
            MemoryOperationError.PrivilegeNotHeld =>
                Loc.T("Ram_ErrPrivilege", "Windows didn't grant the permission this needs (run WinXTools as administrator)"),
            MemoryOperationError.AccessDenied => Loc.T("Ram_ErrAccessDenied", "Windows refused access"),
            MemoryOperationError.NotSupported => Loc.T("Ram_ErrNotSupported", "not supported on this version of Windows"),
            MemoryOperationError.Cancelled => Loc.T("Ram_ErrCancelled", "stopped because WinXTools is closing"),
            _ => Loc.F("Ram_ErrCode", "Windows reported error 0x{0}", operation.Code.ToString("X8", CultureInfo.InvariantCulture))
        };

        return operation.Status == MemoryOperationStatus.Skipped
            ? Loc.F("Ram_ResSkipped", "{0}: not run – {1}", name, reason)
            : Loc.F("Ram_ResFailed", "{0}: failed – {1}", name, reason);
    }

    #endregion

    #region Activity (cleanups + automatic kills)

    private void RebuildHistory()
    {
        var entries = new List<(DateTime Time, OptimizationHistoryItem Item)>();

        foreach (var result in _ram.GetRecentResults())
            entries.Add((result.EndTime, ToHistoryItem(result)));
        foreach (var kill in _killer.GetRecentAutoKills())
            entries.Add((kill.Time, ToHistoryItem(kill)));

        _history.Clear();
        foreach (var entry in entries.OrderByDescending(e => e.Time).Take(MaxHistoryItems))
            _history.Add(entry.Item);

        HistoryEmptyText.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = _history.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private OptimizationHistoryItem ToHistoryItem(OptimizeResult result)
    {
        int succeeded = result.Operations.Count(o => o.Succeeded);
        string freed;
        Brush brush;
        if (!result.AnyOperationSucceeded)
        {
            freed = Loc.T("Ram_HistoryFailed", "failed");
            brush = _dangerBrush;
        }
        else if (result.MemoryFreedMB > 0)
        {
            freed = "+" + FormatMB(result.MemoryFreedMB);
            brush = _successBrush;
        }
        else if (result.FreeGainedMB > 0)
        {
            freed = Loc.F("Ram_HistoryFreeGain", "free +{0}", FormatMB(result.FreeGainedMB));
            brush = _successBrush;
        }
        else
        {
            freed = Loc.T("Ram_NoGain", "no increase");
            brush = _mutedBrush;
        }

        return new OptimizationHistoryItem
        {
            TimeText = result.EndTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            ResultText = Loc.F("Ram_HistoryClean", "Cleanup ({0}) – {1} of {2} steps done",
                TriggerText(result), succeeded, result.Operations.Count),
            FreedText = freed,
            FreedBrush = brush
        };
    }

    private OptimizationHistoryItem ToHistoryItem(ProcessAutoKilledEventArgs e) => new()
    {
        TimeText = e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        ResultText = Loc.F("Ram_AutoClosed", "Auto-closed: {0}", e.ProcessName),
        FreedText = e.Reason switch
        {
            AutoKillReason.NotResponding => Loc.F("Ram_ReasonNotResponding", "Not responding for {0} s", e.Detail),
            AutoKillReason.ExcessiveMemory => Loc.F("Ram_ReasonMemory", "Using {0} MB", e.Detail),
            _ => Loc.T("Ram_ReasonRule", "Kill rule")
        },
        FreedBrush = _warningBrush
    };

    private void OnProcessAutoKilled(object? sender, ProcessAutoKilledEventArgs e)
    {
        // Raised on the watchdog thread — queue the UI update instead of blocking it.
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isActive) return;
            RebuildHistory();
            UpdateKillRulesList();
        });
    }

    #endregion

    #region Settings: what to clean, automatic cleanup

    private void ApplySettingsToControls()
    {
        _applyingSettings = true;
        try
        {
            var items = _ram.CleanItems;
            OptFlushModified.IsChecked = items.HasFlag(MemoryCleanItems.FlushModifiedList);
            OptLowStandby.IsChecked = items.HasFlag(MemoryCleanItems.PurgeLowPriorityStandby);
            OptStandby.IsChecked = items.HasFlag(MemoryCleanItems.PurgeStandbyList);
            OptCombine.IsChecked = items.HasFlag(MemoryCleanItems.CombinePages);
            OptFileCache.IsChecked = items.HasFlag(MemoryCleanItems.FlushSystemFileCache);
            OptTrim.IsChecked = items.HasFlag(MemoryCleanItems.TrimWorkingSets);
            TrimExclusionsBox.Text = string.Join(", ", _ram.TrimExclusions);
            TrimExclusionsStatus.Visibility = Visibility.Collapsed;
            UpdateOptionStates();

            AutoCleanToggle.IsChecked = _ram.IsAutoOptimizeEnabled;
            TriggerLoadToggle.IsChecked = _ram.TriggerOnMemoryLoad;
            TriggerLowFreeToggle.IsChecked = _ram.TriggerOnLowFreeMemory;

            // The ISLC thresholds only make sense up to half of this PC's RAM.
            var info = MemoryCleaner.ReadMemoryInfo(includeCompressedStore: false);
            double listMax = Math.Clamp(Math.Round(info.TotalMemoryMB / 2.0 / 256) * 256, 2048, 32768);
            LowFreeSlider.Maximum = listMax;
            MinStandbySlider.Maximum = listMax;

            ThresholdSlider.Value = _ram.MemoryThresholdPercent;
            LowFreeSlider.Value = Math.Min(_ram.LowFreeThresholdMB, listMax);
            MinStandbySlider.Value = Math.Min(_ram.MinStandbyMB, listMax);
            CooldownSlider.Value = _ram.AutoCooldownMinutes;
            UpdateSettingLabels();
            UpdateAutoHint();
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void CleanOption_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;

        var items = MemoryCleanItems.None;
        if (OptFlushModified.IsChecked == true) items |= MemoryCleanItems.FlushModifiedList;
        if (OptLowStandby.IsChecked == true) items |= MemoryCleanItems.PurgeLowPriorityStandby;
        if (OptStandby.IsChecked == true) items |= MemoryCleanItems.PurgeStandbyList;
        if (OptCombine.IsChecked == true) items |= MemoryCleanItems.CombinePages;
        if (OptFileCache.IsChecked == true) items |= MemoryCleanItems.FlushSystemFileCache;
        if (OptTrim.IsChecked == true) items |= MemoryCleanItems.TrimWorkingSets;

        _ram.CleanItems = items;
        UpdateOptionStates();
    }

    private void UpdateOptionStates()
    {
        // Emptying the whole standby list already includes its priority-0 part.
        bool entireCache = OptStandby.IsChecked == true;
        OptLowStandby.IsEnabled = !entireCache;
        OptLowStandby.ToolTip = entireCache ? Loc.T("Ram_IncludedInEntireCache", "Already included in “Entire cache”") : null;

        TrimExclusionsPanel.Visibility = OptTrim.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UseSafeDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (_ram.CleanItems == MemoryCleanItems.Safe)
            return; // already the defaults — nothing to overwrite

        // Overwrites the user's own selection: ask first.
        var confirm = MessageBox.Show(
            Loc.T("Ram_ConfirmSafeDefaults",
                "Switch back to the safe defaults?\n\nOnly “Write pending changes to disk” and “Low-priority cache” stay on. Your list of programs that are never trimmed is kept."),
            Loc.T("Ram_UseSafeDefaults", "Safe defaults"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        _ram.CleanItems = MemoryCleanItems.Safe;
        ApplySettingsToControls();
    }

    private void TrimExclusionsBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitExclusions();

    private void TrimExclusionsBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitExclusions();
        e.Handled = true;
    }

    private void CommitExclusions()
    {
        if (_applyingSettings) return;

        var parts = TrimExclusionsBox.Text.Split(new[] { ',', ';', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var stored = _ram.SetTrimExclusions(parts, out var rejected);
        TrimExclusionsBox.Text = string.Join(", ", stored);

        string? status = null;
        bool problem = false;
        if (rejected.Count > 0)
        {
            var shown = rejected.Take(3).Select(r => r.Length > 40 ? r[..40] + "…" : r);
            status = Loc.F("Ram_NeverTrimInvalid", "Not saved – not a valid program name: {0}", string.Join(", ", shown));
            problem = true;
        }
        else if (parts.Distinct(StringComparer.OrdinalIgnoreCase).Count() > RamOptimizer.MaxTrimExclusions)
        {
            status = Loc.F("Ram_NeverTrimTooMany", "Only the first {0} names are kept.", RamOptimizer.MaxTrimExclusions);
            problem = true;
        }
        else if (stored.Count > 0)
        {
            status = Loc.F("Ram_NeverTrimSaved", "Saved – {0} program(s) will never be trimmed.", stored.Count);
        }

        TrimExclusionsStatus.Text = status ?? "";
        TrimExclusionsStatus.Foreground = problem ? _warningBrush : _mutedBrush;
        TrimExclusionsStatus.Visibility = status == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AutoCleanToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        _ram.IsAutoOptimizeEnabled = AutoCleanToggle.IsChecked == true;
        UpdateAutoHint();
    }

    private void TriggerToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        _ram.TriggerOnMemoryLoad = TriggerLoadToggle.IsChecked == true;
        _ram.TriggerOnLowFreeMemory = TriggerLowFreeToggle.IsChecked == true;
        UpdateAutoHint();
    }

    private void SettingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Also fires while InitializeComponent sets the XAML values.
        if (_applyingSettings) return;

        var value = (int)Math.Round(e.NewValue);
        if (sender == ThresholdSlider) _ram.MemoryThresholdPercent = value;
        else if (sender == LowFreeSlider) _ram.LowFreeThresholdMB = value;
        else if (sender == MinStandbySlider) _ram.MinStandbyMB = value;
        else if (sender == CooldownSlider) _ram.AutoCooldownMinutes = value;

        // The engine saves a moment after the last change, not on every step of a drag.
        UpdateSettingLabels();
    }

    private void UpdateSettingLabels()
    {
        ThresholdText.Text = $"{(int)ThresholdSlider.Value}%";
        LowFreeText.Text = FormatMB((long)LowFreeSlider.Value);
        MinStandbyText.Text = FormatMB((long)MinStandbySlider.Value);
        CooldownText.Text = Loc.F("Ram_Minutes", "{0} min", (int)CooldownSlider.Value);
    }

    private void UpdateAutoHint()
    {
        bool noCondition = AutoCleanToggle.IsChecked == true &&
                           TriggerLoadToggle.IsChecked != true && TriggerLowFreeToggle.IsChecked != true;
        AutoNoTriggerText.Visibility = noCondition ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    #region Process protection (Smart Kill / Auto Kill)

    private void LoadProcessKillerSettings()
    {
        SmartKillToggle.IsChecked = _killer.IsSmartKillEnabled;
        AutoKillToggle.IsChecked = _killer.IsAutoKillEnabled;
    }

    private void UpdateKillRulesList()
    {
        _killRules.Clear();
        foreach (var rule in _killer.GetKillRules().OrderBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase))
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

        KillRulesEmptyText.Visibility = _killRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SmartKillToggle_Click(object sender, RoutedEventArgs e)
    {
        bool turnOn = SmartKillToggle.IsChecked == true;

        // Closing apps loses unsaved work — make the user opt in knowingly.
        if (turnOn && !_killer.IsSmartKillEnabled)
        {
            var confirm = MessageBox.Show(
                Loc.F("Ram_SmartKillConfirm",
                    "Turn on Smart Kill?\n\nApps whose window stays \"Not Responding\" for {0} seconds or more will be closed automatically. Unsaved work in them will be lost.\n\nNever closed: the app you are using right now, Windows and Explorer processes, WebView2 (used by Outlook and Teams) and WinXTools itself. Every app that gets closed is listed in the history.",
                    (int)ProcessKiller.FrozenKillThreshold.TotalSeconds),
                Loc.T("RamOptimizer_SmartKill", "Smart Kill"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
            {
                SmartKillToggle.IsChecked = false;
                return;
            }
        }

        _killer.IsSmartKillEnabled = turnOn;
    }

    private void AutoKillToggle_Click(object sender, RoutedEventArgs e)
    {
        bool turnOn = AutoKillToggle.IsChecked == true;

        // Turning it on immediately closes every running app that matches a rule.
        var rules = _killer.GetKillRules();
        if (turnOn && !_killer.IsAutoKillEnabled && rules.Count > 0)
        {
            var names = string.Join("\n", rules.Take(10).Select(r => "• " + r.ProcessName));
            if (rules.Count > 10)
                names += $"\n… (+{rules.Count - 10})";

            var confirm = MessageBox.Show(
                Loc.F("Ram_AutoKillConfirm",
                    "Turn on Auto Kill?\n\nThese apps will be closed now and every time they start. Unsaved work in them will be lost:\n{0}",
                    names),
                Loc.T("RamOptimizer_AutoKill", "Auto Kill Rules"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
            {
                AutoKillToggle.IsChecked = false;
                return;
            }
        }

        _killer.IsAutoKillEnabled = turnOn;
    }

    private async void AddKillRule_Click(object sender, RoutedEventArgs e)
    {
        var title = Loc.T("RamOptimizer_AddKillRuleTitle", "Add Kill Rule");

        var dialog = new Dialogs.InputDialog(
            title,
            Loc.T("RamOptimizer_AddKillRuleMessage", "Enter process name to auto-kill (without .exe):"));

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.ResponseText))
            return;

        var entered = dialog.ResponseText.Trim();
        var bareName = entered.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? entered[..^4] : entered;

        if (ProcessKiller.IsAutoKillExcluded(bareName))
        {
            MessageBox.Show(
                Loc.F("Ram_ProtectedRule", "Cannot add \"{0}\": it is a protected Windows process.", bareName),
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var processName = ProcessKiller.NormalizeRuleName(entered);
        if (processName == null)
        {
            MessageBox.Show(
                Loc.F("Ram_InvalidProcessName", "\"{0}\" is not a valid program name. Type the name without .exe, for example: notepad", entered),
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Adding a rule closes running copies right away — say so first.
        var confirm = MessageBox.Show(
            Loc.F("Ram_AddRuleConfirm",
                "Add \"{0}\" to the kill rules?\n\nIf it is running it will be closed now (unsaved work will be lost), and it will be closed automatically whenever it starts while Auto Kill is on.",
                processName),
            title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await Task.Run(() => _killer.AddKillRule(processName, "User requested"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RamOptimizerView] Add rule failed: {ex.Message}");
        }

        if (!_isActive) return;
        UpdateKillRulesList();
        _ = RefreshProcessListAsync();
    }

    private void RemoveKillRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string processName })
        {
            _killer.RemoveKillRule(processName);
            UpdateKillRulesList();
        }
    }

    #endregion

    /// <summary>"812 MB" / "1.5 GB"; "–" when unknown.</summary>
    public static string FormatMB(long mb)
    {
        if (mb < 0) return "–";
        if (mb >= 1024)
            return (mb / 1024.0).ToString(mb >= 10 * 1024 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " GB";
        return mb.ToString(CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>"46.6 / 53.8 GB" — one unit for both, so it fits a small card.</summary>
    private static string FormatPair(long usedMB, long limitMB)
    {
        if (limitMB < 1024)
            return $"{Math.Max(0, usedMB)} / {limitMB} MB";
        return (Math.Max(0, usedMB) / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " / " +
               (limitMB / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
    }
}

public class ProcessMemoryDisplayItem
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public long CreateTime { get; set; }
    public long MemoryMB { get; set; }
    public long PrivateMemoryMB { get; set; }
    public string MemoryText { get; set; } = "";
    public string PrivateMemoryText { get; set; } = "";
    public string DetailText { get; set; } = "";
    public bool CanTrim { get; set; }
    public bool CanEnd { get; set; }
    public string TrimToolTip { get; set; } = "";
    public string EndToolTip { get; set; } = "";
    public ImageSource? Icon { get; set; }
}

public class OptimizationHistoryItem
{
    public string TimeText { get; set; } = "";
    public string ResultText { get; set; } = "";
    public string FreedText { get; set; } = "";
    public Brush? FreedBrush { get; set; }
}

public class OperationLine
{
    public string Glyph { get; set; } = "";
    public Brush? GlyphBrush { get; set; }
    public string Text { get; set; } = "";
}

public class KillRuleDisplayItem
{
    public string ProcessName { get; set; } = "";
    public string Reason { get; set; } = "";
    public int KillCount { get; set; }
    public string KillCountText { get; set; } = "";
    public DateTime? LastKilled { get; set; }
}

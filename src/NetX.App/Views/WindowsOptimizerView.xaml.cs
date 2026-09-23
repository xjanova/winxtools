using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetX.Core.Optimization;
using NetX.Core.System;

namespace NetX.App.Views;

public partial class WindowsOptimizerView : Page
{
    private readonly WindowsOptimizer _optimizer;
    private readonly ObservableCollection<OptimizationDisplayItem> _currentItems = new();
    private readonly ObservableCollection<RestorePointDisplayItem> _restorePoints = new();
    private string _currentCategory = "Services";

    // One system change at a time — guards double-clicks and overlapping batches
    // while a restore point is created or services stop/start in the background.
    private bool _isBusy;

    private const int MaxListedNames = 12;

    public WindowsOptimizerView()
    {
        InitializeComponent();

        _optimizer = WindowsOptimizer.Instance;
        _optimizer.OnOptimizationComplete += OnOptimizationComplete;

        ItemsList.ItemsSource = _currentItems;
        RestorePointsList.ItemsSource = _restorePoints;

        LoadCategory("Services");
        _ = LoadRestorePointsAsync();
        UpdateSummary();
        UpdateGameModeUi();

        Unloaded += (s, e) =>
        {
            _optimizer.OnOptimizationComplete -= OnOptimizationComplete;
        };
    }

    private static string T(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as string ?? fallback;

    private static string F(string key, string fallback, params object[] args)
    {
        try { return string.Format(T(key, fallback), args); }
        catch (FormatException) { return string.Format(fallback, args); }
    }

    #region Category Navigation

    private void CategoryTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton radio && radio.Tag is string category)
        {
            LoadCategory(category);
        }
    }

    private void LoadCategory(string category)
    {
        _currentCategory = category;
        _currentItems.Clear();

        var items = category switch
        {
            "Services" => _optimizer.Services,
            "AI" => _optimizer.AIFeatures,
            "Telemetry" => _optimizer.TelemetryFeatures,
            "Startup" => _optimizer.StartupOptimizations,
            _ => _optimizer.Services
        };

        // Refresh states
        _optimizer.RefreshAllStates();

        foreach (var item in items)
        {
            _currentItems.Add(CreateDisplayItem(item));
        }

        SelectAllCheckBox.IsChecked = false;
        UpdateSummary();
    }

    private OptimizationDisplayItem CreateDisplayItem(OptimizationItem item)
    {
        var displayItem = new OptimizationDisplayItem
        {
            Id = item.Id,
            Name = item.Name,
            Description = item.Description,
            Category = item.Category,
            Type = item.Type,
            RiskLevel = item.RiskLevel,
            RamSavingMB = item.RamSavingMB,
            IsDisabled = item.IsDisabled,
            CurrentStatus = item.CurrentStatus,
            SourceItem = item,
            IsSelected = false
        };

        // Set risk badge
        displayItem.RiskText = item.RiskLevel switch
        {
            RiskLevel.Safe => "Safe",
            RiskLevel.Medium => "Medium",
            RiskLevel.High => "Caution",
            _ => "Safe"
        };

        displayItem.RiskBadgeBackground = item.RiskLevel switch
        {
            RiskLevel.Safe => new SolidColorBrush(Color.FromArgb(40, 16, 185, 129)),
            RiskLevel.Medium => new SolidColorBrush(Color.FromArgb(40, 245, 158, 11)),
            RiskLevel.High => new SolidColorBrush(Color.FromArgb(40, 239, 68, 68)),
            _ => new SolidColorBrush(Color.FromArgb(40, 16, 185, 129))
        };

        displayItem.RiskBadgeForeground = item.RiskLevel switch
        {
            RiskLevel.Safe => new SolidColorBrush(Color.FromRgb(16, 185, 129)),
            RiskLevel.Medium => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            RiskLevel.High => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            _ => new SolidColorBrush(Color.FromRgb(16, 185, 129))
        };

        // Set status — for services show whether it actually runs right now
        if (item.IsDisabled)
        {
            displayItem.StatusText = item.Type == OptimizationType.Service && item.IsRunning
                ? T("WinOpt_StatusDisabledRunning", "Disabled · still running until restart")
                : T("WinOpt_StatusDisabled", "Disabled");
        }
        else if (item.Type == OptimizationType.Service)
        {
            displayItem.StatusText = item.IsRunning
                ? T("WinOpt_StatusRunning", "Running")
                : T("WinOpt_StatusStopped", "Stopped");
        }
        else
        {
            displayItem.StatusText = T("WinOpt_StatusEnabled", "Enabled");
        }
        displayItem.StatusColor = item.IsDisabled
            ? (TryFindResource("SuccessBrush") as Brush ?? Brushes.Green)
            : (TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray);

        // RAM figure is a catalogue guess, not measured on this PC — label it so
        displayItem.RamSavingText = item.RamSavingMB > 0
            ? F("WinOpt_RamEstimateItem", "~{0} MB (estimate)", item.RamSavingMB)
            : "";

        // Set category icon (use TryFindResource to avoid exceptions)
        displayItem.CategoryIcon = item.Category switch
        {
            OptimizationCategory.AI => TryFindResource("AIIcon") as Geometry,
            OptimizationCategory.Telemetry => TryFindResource("TelemetryIcon") as Geometry,
            OptimizationCategory.Privacy => TryFindResource("PrivacyIcon") as Geometry,
            OptimizationCategory.Performance => TryFindResource("PerformanceIcon") as Geometry,
            OptimizationCategory.Gaming => TryFindResource("GamingIcon") as Geometry,
            _ => TryFindResource("ServiceIcon") as Geometry
        };

        // Set toggle button style and text
        displayItem.ToggleButtonStyle = item.IsDisabled
            ? TryFindResource("SecondaryButtonStyle") as Style
            : TryFindResource("PrimaryButtonStyle") as Style;
        displayItem.ToggleButtonText = item.IsDisabled
            ? T("WinOpt_Enable", "Enable")
            : T("WinOpt_Disable", "Disable");

        return displayItem;
    }

    #endregion

    #region Item Actions

    private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateSummary();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool isChecked = SelectAllCheckBox.IsChecked == true;
        foreach (var item in _currentItems)
        {
            item.IsSelected = isChecked;
        }
        ItemsList.Items.Refresh();
        UpdateSummary();
    }

    private async void ToggleItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (sender is not Button { Tag: OptimizationDisplayItem displayItem } || displayItem.SourceItem is not { } item)
            return;

        // Same safety as the batch actions: confirm, restore point first, run
        // off the UI thread (stopping a service can take up to 30 s).
        bool disable = !item.IsDisabled;
        string message = disable
            ? F("WinOpt_ConfirmDisableOne", "Disable \"{0}\"?", item.Name)
              + "\n\n" + T("WinOpt_RestorePointPromise",
                  "WinXTools will first try to create a System Restore point. Windows allows only one every 24 hours and System Protection must be on; you will see whether it worked when the changes finish.")
            : F("WinOpt_ConfirmEnableOne", "Turn \"{0}\" back on?", item.Name)
              + "\n\n" + DescribeRestore(item);

        var confirm = MessageBox.Show(message,
            T("WinOptimizer_Title", "Windows Optimizer"),
            MessageBoxButton.YesNo,
            disable ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        await RunChangesAsync(new List<OptimizationItem> { item }, disable, explicitEnable: !disable, busyItem: displayItem);
    }

    /// <summary>Tells the user exactly what "Enable" will restore for this item.</summary>
    private string DescribeRestore(OptimizationItem item)
    {
        if (item.Type != OptimizationType.Service)
            return T("WinOpt_PlanFeature", "The Windows default setting will be restored.");

        var plan = _optimizer.GetServiceRestorePlan(item.Id, explicitEnable: true);
        string startType = plan.StartValue switch
        {
            2 when plan.IsDelayedAutomatic => T("WinOpt_StartAutomaticDelayed", "Automatic (Delayed Start)"),
            2 => T("WinOpt_StartAutomatic", "Automatic"),
            4 => T("WinOpt_StartDisabled", "Disabled"),
            _ => T("WinOpt_StartManual", "Manual")
        };
        string source = plan.Source switch
        {
            ServiceRestoreSource.SavedOriginal => T("WinOpt_PlanSourceSaved", "the setting it had before WinXTools disabled it"),
            ServiceRestoreSource.WindowsDefault => T("WinOpt_PlanSourceDefault", "the Windows default"),
            _ => T("WinOpt_PlanSourceManual", "Windows ships this service disabled; Manual lets it run only when something needs it")
        };

        var text = F("WinOpt_PlanService", "Startup type will be set to {0} ({1}).", startType, source);
        if (plan.StartNow)
            text += "\n" + T("WinOpt_PlanStartsNow", "The service will also be started now.");
        return text;
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        LoadCategory(_currentCategory);
    }

    #endregion

    #region Batch Actions

    private async void ApplySelectedBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var selectedItems = _currentItems
            .Where(i => i.IsSelected && i.SourceItem != null && !i.SourceItem.IsDisabled)
            .Select(i => i.SourceItem!)
            .ToList();

        if (selectedItems.Count == 0)
        {
            MessageBox.Show(
                T("WinOpt_NoSelection", "Please select items to disable."),
                T("WinOptimizer_ApplySelected", "Apply Selected"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Confirm with user — the restore point is promised as an attempt, not a given
        var result = MessageBox.Show(
            BuildDisableConfirmation(selectedItems),
            T("WinOptimizer_ApplySelected", "Apply Selected"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            await RunChangesAsync(selectedItems, disable: true);
        }
    }

    private async void SafeOptimizeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        // Decide on the current state, not on what the list showed minutes ago.
        _optimizer.RefreshAllStates();
        var safeItems = _optimizer.GetSafeOptimizations();

        if (safeItems.Count == 0)
        {
            MessageBox.Show(
                T("WinOpt_AlreadyOptimized", "All safe optimizations are already applied."),
                T("WinOptimizer_SafeOptimize", "Safe Optimize"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            BuildDisableConfirmation(safeItems),
            T("WinOptimizer_SafeOptimize", "Safe Optimize"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await RunChangesAsync(safeItems, disable: true);
        }
    }

    private async void RestoreAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        _optimizer.RefreshAllStates();
        var restorableItems = _optimizer.GetRestorableItems();

        if (restorableItems.Count == 0)
        {
            MessageBox.Show(
                T("WinOpt_NothingToRestore", "Nothing to restore: every item is already at its original setting."),
                T("WinOptimizer_RestoreAll", "Restore All"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            F("WinOpt_ConfirmRestoreAll", "This will turn {0} item(s) back on:\n{1}",
                restorableItems.Count, ListNames(restorableItems))
            + "\n\n" + T("WinOpt_RestoreAllExplain",
                "Services get back the startup type they had before WinXTools disabled them (or the Windows default if that was not recorded) and are started again if they were running.")
            + "\n\n" + T("WinOpt_Continue", "Continue?"),
            T("WinOptimizer_RestoreAll", "Restore All"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await RunChangesAsync(restorableItems, disable: false);
        }
    }

    private string BuildDisableConfirmation(List<OptimizationItem> items)
    {
        var message = F("WinOpt_ConfirmDisableList", "This will disable {0} item(s):\n{1}", items.Count, ListNames(items));

        var estimateMB = _optimizer.GetEstimatedRamSaving(items);
        if (estimateMB > 0)
        {
            message += "\n\n" + F("WinOpt_RamEstimateLine",
                "Rough RAM estimate (typical usage, not measured on this PC): ~{0} MB", estimateMB);
        }

        return message
               + "\n\n" + T("WinOpt_RestorePointPromise",
                   "WinXTools will first try to create a System Restore point. Windows allows only one every 24 hours and System Protection must be on; you will see whether it worked when the changes finish.")
               + "\n\n" + T("WinOpt_Continue", "Continue?");
    }

    private static string ListNames(List<OptimizationItem> items)
    {
        var lines = items.Take(MaxListedNames).Select(i => "• " + i.Name).ToList();
        if (items.Count > MaxListedNames)
            lines.Add(F("WinOpt_AndMore", "…and {0} more", items.Count - MaxListedNames));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Runs the change off the UI thread with every action disabled, then shows
    /// what really happened (per item + restore point) and reloads the states.
    /// </summary>
    private async Task RunChangesAsync(List<OptimizationItem> items, bool disable,
        bool explicitEnable = false, OptimizationDisplayItem? busyItem = null)
    {
        if (_isBusy || items.Count == 0) return;
        SetBusy(true, busyItem);

        try
        {
            var result = await Task.Run(() => _optimizer.ApplyOptimizations(items, disable, explicitEnable));
            ShowResult(result, disable);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                F("WinOpt_UnexpectedError", "Windows settings could not be changed:\n{0}", ex.Message),
                T("Common_Error", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            LoadCategory(_currentCategory);
            if (disable)
                _ = LoadRestorePointsAsync(); // show the restore point made before the changes
        }
    }

    private void SetBusy(bool busy, OptimizationDisplayItem? busyItem = null)
    {
        _isBusy = busy;
        ItemsList.IsEnabled = !busy;
        SelectAllCheckBox.IsEnabled = !busy;
        RefreshBtn.IsEnabled = !busy;
        ApplySelectedBtn.IsEnabled = !busy;
        SafeOptimizeBtn.IsEnabled = !busy;
        RestoreAllBtn.IsEnabled = !busy;
        CreateRestoreBtn.IsEnabled = !busy;
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        if (busy && busyItem != null)
            busyItem.ToggleButtonText = T("Common_Processing", "Processing...");
    }

    private void ShowResult(OptimizationResult result, bool disable)
    {
        var parts = new List<string>();

        if (result.SuccessCount > 0)
        {
            parts.Add(disable
                ? F("WinOpt_ResultDisabled", "Disabled {0} item(s).", result.SuccessCount)
                : F("WinOpt_ResultRestored", "Turned {0} item(s) back on with their original settings.", result.SuccessCount));
        }
        else if (result.FailedItems.Count == 0)
        {
            parts.Add(T("WinOpt_ResultNoChange", "No changes were needed."));
        }

        if (result.AlreadyInStateItems.Count > 0)
            parts.Add(F("WinOpt_ResultAlready", "Already in the right state: {0}", string.Join(", ", result.AlreadyInStateItems)));

        if (result.NotAvailableItems.Count > 0)
            parts.Add(F("WinOpt_ResultNotPresent", "Not present on this PC (skipped): {0}", string.Join(", ", result.NotAvailableItems)));

        if (result.StillRunningItems.Count > 0)
            parts.Add(F("WinOpt_ResultStillRunning",
                "Disabled, but could not be stopped right now (they stop after the next restart): {0}",
                string.Join(", ", result.StillRunningItems)));

        if (result.NotStartedItems.Count > 0)
            parts.Add(F("WinOpt_ResultNotStarted",
                "Turned back on, but could not be started right now (they start after the next restart): {0}",
                string.Join(", ", result.NotStartedItems)));

        if (result.FailedItems.Count > 0)
        {
            var failedList = string.Join("\n", result.FailedItems.Take(5));
            if (result.FailedItems.Count > 5)
                failedList += "\n" + F("WinOpt_AndMore", "…and {0} more", result.FailedItems.Count - 5);

            parts.Add(F("WinOpt_ResultFailed", "Could not change {0} item(s):\n{1}", result.FailedItems.Count, failedList));
        }

        if (disable && result.RestorePoint != null)
            parts.Add(DescribeRestorePoint(result.RestorePoint));

        if (result.SuccessCount > 0)
            parts.Add(T("WinOpt_ResultRestartHint", "Some changes take full effect only after a restart."));

        bool ok = result.FailedItems.Count == 0;
        MessageBox.Show(
            string.Join("\n\n", parts),
            ok ? T("Common_Success", "Success") : T("Common_Warning", "Warning"),
            MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static string DescribeRestorePoint(RestorePointAttempt attempt)
    {
        switch (attempt.Outcome)
        {
            case RestorePointOutcome.Created:
                return F("WinOpt_RestorePointCreated", "Restore point created ({0}).",
                    (attempt.CreatedAt ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm"));

            case RestorePointOutcome.SkippedRecentExists:
                return F("WinOpt_RestorePointSkipped",
                    "Windows did not create a new restore point because one was already made in the last 24 hours ({0}). You can use that one to undo these changes.",
                    attempt.LatestExisting?.ToString("yyyy-MM-dd HH:mm") ?? "");

            default:
                var text = T("WinOpt_RestorePointFailed",
                    "No restore point was created. Make sure System Protection is turned on for your system drive.");
                return string.IsNullOrWhiteSpace(attempt.Error) ? text : $"{text}\n({attempt.Error})";
        }
    }

    private void OnOptimizationComplete(OptimizationResult result)
    {
        // Raised on the worker thread — don't block it waiting for the UI.
        Dispatcher.BeginInvoke(new Action(UpdateSummary));
    }

    #endregion

    #region Gamer Mode

    private void UpdateGameModeUi()
    {
        var os = WindowsVersionInfo.Current;
        var gm = GameModeService.Instance;

        var osText = os.FriendlyName;
        if (!gm.IsAdmin)
        {
            var needAdmin = TryFindResource("GameMode_NeedAdmin") as string ?? "Run as Administrator to apply all tweaks";
            osText += $"  •  {needAdmin}";
        }
        GameModeOsText.Text = osText;

        bool on = gm.IsActive;
        GameModeStatusText.Text = TryFindResource(on ? "GameMode_On" : "GameMode_Off") as string
                                  ?? (on ? "ON" : "OFF");
        GameModeStatusText.Foreground = on
            ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
            : Brushes.Gray;
        GameModeStatusBadge.Background = on
            ? new SolidColorBrush(Color.FromArgb(40, 16, 185, 129))
            : new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
    }

    private void GameModeApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        var message = TryFindResource("GameMode_ConfirmApply") as string
                      ?? "Apply Gamer Mode? Every setting is saved first and fully reversible.";
        var confirm = MessageBox.Show(message,
            TryFindResource("GameMode_Title") as string ?? "Gamer Mode",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        RunGameMode(apply: true);
    }

    private void GameModeRevertBtn_Click(object sender, RoutedEventArgs e)
    {
        RunGameMode(apply: false);
    }

    private async void RunGameMode(bool apply)
    {
        GameModeApplyBtn.IsEnabled = false;
        GameModeRevertBtn.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => apply
                ? GameModeService.Instance.ApplyGameMode()
                : GameModeService.Instance.RevertGameMode());

            MessageBox.Show(result.BuildSummary(NetX.App.Helpers.Loc.IsThai),
                TryFindResource("GameMode_Title") as string ?? "Gamer Mode",
                MessageBoxButton.OK,
                result.Failed.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message,
                TryFindResource("GameMode_Title") as string ?? "Gamer Mode",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            GameModeApplyBtn.IsEnabled = true;
            GameModeRevertBtn.IsEnabled = true;
            UpdateGameModeUi();
        }
    }

    #endregion

    #region Restore Points

    private async void CreateRestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var confirm = MessageBox.Show(
            T("WinOpt_ConfirmCreateRestore",
                "Create a System Restore point now?\n\nIt lets you undo changes if something goes wrong. Windows allows only one every 24 hours, and creating it can take a minute."),
            T("WinOptimizer_CreateRestore", "Create Restore Point"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            var description = $"WinXTools - Windows Optimizer ({DateTime.Now:yyyy-MM-dd HH:mm})";
            var attempt = await Task.Run(() => _optimizer.TryCreateRestorePoint(description));

            MessageBox.Show(
                DescribeRestorePoint(attempt),
                T("WinOptimizer_CreateRestore", "Create Restore Point"),
                MessageBoxButton.OK,
                attempt.Outcome == RestorePointOutcome.Failed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                F("WinOpt_UnexpectedError", "Windows settings could not be changed:\n{0}", ex.Message),
                T("Common_Error", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            _ = LoadRestorePointsAsync();
        }
    }

    private async Task LoadRestorePointsAsync()
    {
        try
        {
            // WMI query — keep it off the UI thread.
            var points = await Task.Run(() => _optimizer.GetRestorePoints().Take(10).ToList());

            _restorePoints.Clear();
            foreach (var point in points)
            {
                _restorePoints.Add(new RestorePointDisplayItem
                {
                    SequenceNumber = point.SequenceNumber,
                    Description = point.Description,
                    CreationTime = point.CreationTime,
                    CreationTimeText = point.CreationTime.ToString("yyyy-MM-dd HH:mm")
                });
            }

            NoRestorePointsText.Visibility = _restorePoints.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch
        {
            NoRestorePointsText.Visibility = Visibility.Visible;
        }
    }

    #endregion

    #region Summary

    private void UpdateSummary()
    {
        var allItems = _optimizer.GetAllOptimizations();
        var selectedCount = _currentItems.Count(i => i.IsSelected);
        var disabledCount = allItems.Count(i => i.IsDisabled);
        var estimatedRam = _currentItems
            .Where(i => i.IsSelected && i.SourceItem != null && !i.SourceItem.IsDisabled)
            .Sum(i => i.RamSavingMB);

        SelectedCountText.Text = selectedCount.ToString();
        DisabledCountText.Text = disabledCount.ToString();
        EstimatedRamText.Text = $"~{estimatedRam} MB"; // rough estimate (label + tooltip say so)
        TotalItemsText.Text = allItems.Count.ToString();
    }

    #endregion
}

#region Display Models

public class OptimizationDisplayItem : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;
    private string _toggleButtonText = "";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public OptimizationCategory Category { get; set; }
    public OptimizationType Type { get; set; }
    public RiskLevel RiskLevel { get; set; }
    public long RamSavingMB { get; set; }
    public bool IsDisabled { get; set; }
    public string? CurrentStatus { get; set; }
    public OptimizationItem? SourceItem { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    // Display properties
    public string RiskText { get; set; } = "";
    public Brush RiskBadgeBackground { get; set; } = Brushes.Gray;
    public Brush RiskBadgeForeground { get; set; } = Brushes.White;
    public string StatusText { get; set; } = "";
    public Brush StatusColor { get; set; } = Brushes.Gray;
    public string RamSavingText { get; set; } = "";
    public Geometry? CategoryIcon { get; set; }
    public Style? ToggleButtonStyle { get; set; }

    public string ToggleButtonText
    {
        get => _toggleButtonText;
        set
        {
            _toggleButtonText = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ToggleButtonText)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public class RestorePointDisplayItem
{
    public int SequenceNumber { get; set; }
    public string Description { get; set; } = "";
    public DateTime CreationTime { get; set; }
    public string CreationTimeText { get; set; } = "";
}

#endregion

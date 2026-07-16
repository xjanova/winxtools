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

    public WindowsOptimizerView()
    {
        InitializeComponent();

        _optimizer = WindowsOptimizer.Instance;
        _optimizer.OnOptimizationComplete += OnOptimizationComplete;

        ItemsList.ItemsSource = _currentItems;
        RestorePointsList.ItemsSource = _restorePoints;

        LoadCategory("Services");
        LoadRestorePoints();
        UpdateSummary();
        UpdateGameModeUi();

        Unloaded += (s, e) =>
        {
            _optimizer.OnOptimizationComplete -= OnOptimizationComplete;
        };
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

        // Set status
        displayItem.StatusText = item.IsDisabled ? "Disabled" : (item.CurrentStatus ?? "Enabled");
        displayItem.StatusColor = item.IsDisabled
            ? (TryFindResource("SuccessBrush") as Brush ?? Brushes.Green)
            : (TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray);

        // Set RAM saving text
        displayItem.RamSavingText = item.RamSavingMB > 0 ? $"~{item.RamSavingMB} MB" : "";

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
        displayItem.ToggleButtonText = item.IsDisabled ? "Enable" : "Disable";

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

    private void ToggleItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is OptimizationDisplayItem displayItem)
        {
            ToggleSingleItem(displayItem);
        }
    }

    private void ToggleSingleItem(OptimizationDisplayItem displayItem)
    {
        var item = displayItem.SourceItem;
        if (item == null) return;

        bool success;
        if (item.IsDisabled)
        {
            // Enable
            if (item.Type == OptimizationType.Service)
            {
                success = _optimizer.EnableService(item.Id);
            }
            else
            {
                success = _optimizer.EnableFeature(item);
            }
        }
        else
        {
            // Disable
            if (item.Type == OptimizationType.Service)
            {
                success = _optimizer.DisableService(item.Id);
            }
            else
            {
                success = _optimizer.DisableFeature(item);
            }
        }

        if (success)
        {
            item.IsDisabled = !item.IsDisabled;
            // Refresh display
            var index = _currentItems.IndexOf(displayItem);
            if (index >= 0)
            {
                _currentItems[index] = CreateDisplayItem(item);
            }
            UpdateSummary();
        }
        else
        {
            MessageBox.Show(
                $"Failed to change setting for '{item.Name}'.\n\n{_optimizer.LastError}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        LoadCategory(_currentCategory);
    }

    #endregion

    #region Batch Actions

    private void ApplySelectedBtn_Click(object sender, RoutedEventArgs e)
    {
        var selectedItems = _currentItems
            .Where(i => i.IsSelected && i.SourceItem != null && !i.SourceItem.IsDisabled)
            .Select(i => i.SourceItem!)
            .ToList();

        if (selectedItems.Count == 0)
        {
            MessageBox.Show(
                "Please select items to disable.",
                "No Selection",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Confirm with user
        var result = MessageBox.Show(
            $"This will disable {selectedItems.Count} item(s).\n\nA system restore point will be created automatically first (best effort).\n\nContinue?",
            "Confirm Changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            ApplyOptimizations(selectedItems, true);
        }
    }

    private void SafeOptimizeBtn_Click(object sender, RoutedEventArgs e)
    {
        var safeItems = _optimizer.GetSafeOptimizations();

        if (safeItems.Count == 0)
        {
            MessageBox.Show(
                "All safe optimizations are already applied!",
                "Already Optimized",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"This will disable {safeItems.Count} safe item(s).\n\nEstimated RAM saving: ~{_optimizer.GetEstimatedRamSaving(safeItems)} MB\n\nA system restore point will be created automatically first (best effort).\n\nContinue?",
            "Safe Optimization",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            ApplyOptimizations(safeItems, true);
        }
    }

    private void RestoreAllBtn_Click(object sender, RoutedEventArgs e)
    {
        var disabledItems = _optimizer.GetAllOptimizations()
            .Where(i => i.IsDisabled)
            .ToList();

        if (disabledItems.Count == 0)
        {
            MessageBox.Show(
                "No items are currently disabled.",
                "Nothing to Restore",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"This will re-enable {disabledItems.Count} item(s).\n\nContinue?",
            "Restore All",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            ApplyOptimizations(disabledItems, false);
        }
    }

    private void ApplyOptimizations(List<OptimizationItem> items, bool disable)
    {
        ApplySelectedBtn.IsEnabled = false;
        SafeOptimizeBtn.IsEnabled = false;
        RestoreAllBtn.IsEnabled = false;

        Task.Run(() =>
        {
            var result = _optimizer.ApplyOptimizations(items, disable);

            Dispatcher.Invoke(() =>
            {
                ApplySelectedBtn.IsEnabled = true;
                SafeOptimizeBtn.IsEnabled = true;
                RestoreAllBtn.IsEnabled = true;

                string restoreNote = !disable ? "" : result.RestorePointCreated
                    ? "\nRestore point created: yes"
                    : "\nRestore point created: no (Windows limits creation to one per 24h)";

                if (result.Success)
                {
                    MessageBox.Show(
                        $"Successfully {(disable ? "disabled" : "enabled")} {result.SuccessCount} item(s).\n" +
                        (disable ? $"Estimated RAM saved: ~{result.EstimatedRamSavedMB} MB" : "") +
                        restoreNote,
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    var failedList = string.Join("\n", result.FailedItems.Take(5));
                    if (result.FailedItems.Count > 5)
                        failedList += $"\n... and {result.FailedItems.Count - 5} more";

                    MessageBox.Show(
                        $"Completed with {result.FailedItems.Count} error(s).\n\nSuccessful: {result.SuccessCount}\n\nFailed:\n{failedList}",
                        "Partial Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                LoadCategory(_currentCategory);
                LoadRestorePoints(); // show the auto-created restore point
            });
        });
    }

    private void OnOptimizationComplete(OptimizationResult result)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateSummary();
        });
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

            MessageBox.Show(result.BuildSummary(),
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

    private void CreateRestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Create a system restore point?\n\nThis allows you to undo changes if something goes wrong.",
            "Create Restore Point",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            CreateRestoreBtn.IsEnabled = false;

            Task.Run(() =>
            {
                var description = $"WinXTools - Windows Optimizer ({DateTime.Now:yyyy-MM-dd HH:mm})";
                var success = _optimizer.CreateRestorePoint(description);

                Dispatcher.Invoke(() =>
                {
                    CreateRestoreBtn.IsEnabled = true;

                    if (success)
                    {
                        MessageBox.Show(
                            "Restore point created successfully!",
                            "Success",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        LoadRestorePoints();
                    }
                    else
                    {
                        MessageBox.Show(
                            $"Failed to create restore point.\n\n{_optimizer.LastError}\n\nMake sure System Protection is enabled for your system drive.",
                            "Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                });
            });
        }
    }

    private void LoadRestorePoints()
    {
        try
        {
            _restorePoints.Clear();
            var points = _optimizer.GetRestorePoints().Take(10);

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
        EstimatedRamText.Text = $"{estimatedRam} MB";
        TotalItemsText.Text = allItems.Count.ToString();
    }

    #endregion
}

#region Display Models

public class OptimizationDisplayItem : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;

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
    public string ToggleButtonText { get; set; } = "";

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

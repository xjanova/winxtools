using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetX.App.Helpers;
using NetX.Core.Data;
using NetX.Core.System;

namespace NetX.App.Views;

public partial class CleanerView : Page
{
    private const string SelectionSettingKey = "Cleaner.Selection";

    private readonly ObservableCollection<CleanCategoryItem> _items = new();
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private bool _hasScan;

    public CleanerView()
    {
        InitializeComponent();

        var saved = LoadSelection();
        foreach (var category in SystemCleaner.Categories)
        {
            var item = new CleanCategoryItem(category, saved?.Contains(category.Key) ?? category.DefaultOn);
            item.PropertyChanged += Item_PropertyChanged;
            _items.Add(item);
        }
        CategoryList.ItemsSource = _items;

        // Leaving the page stops a running scan/clean instead of letting it
        // keep deleting in the background.
        Unloaded += (s, e) => _cts?.Cancel();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CleanCategoryItem.IsSelected)) return;
        SaveSelection();
        UpdateSummary();
    }

    #region Scan

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        CleanProgress.IsIndeterminate = false;
        CleanProgress.Value = 0;

        foreach (var item in _items) item.ResetForScan();

        try
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                StatusText.Text = Loc.F("Cleaner_Scanning", "Scanning {0}…", item.Title);
                item.SizeText = "…";

                var scan = await Task.Run(() => SystemCleaner.Scan(item.Target, token), token);
                item.ApplyScan(scan);

                CleanProgress.Value = (i + 1) * 100.0 / _items.Count;
                UpdateSummary();
            }

            _hasScan = true;
            StatusText.Text = Loc.F("Cleaner_ScanDone", "Scan complete — {0} can be freed.", FormatSize(SelectedBytes()));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.T("Cleaner_Cancelled", "Cancelled");
            foreach (var item in _items.Where(i => i.Scan == null)) item.SizeText = "--";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{Loc.T("BW_ActionFailed", "Failed")}: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
            UpdateSummary();
        }
    }

    #endregion

    #region Clean

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !_hasScan) return;

        var selected = _items.Where(i => i.IsSelected && i.Scan != null && (i.Scan.Bytes > 0 || i.Scan.FileCount > 0)).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(Loc.T("Cleaner_NothingSelected", "Select at least one item that has something to clean."),
                "WinXTools", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lines = new StringBuilder();
        foreach (var item in selected)
        {
            lines.AppendLine($"• {item.Title} — {FormatSize(item.Scan!.Bytes)}");
            var warning = Loc.T($"CleanCat_{item.Key}_Warn", "");
            if (warning.Length > 0) lines.AppendLine($"   ⚠ {warning}");
        }

        var confirm = Loc.F("Cleaner_Confirm",
            "Clean these items?\n\n{0}\nTotal: {1}\n\nFiles in use are skipped automatically. This cannot be undone.",
            lines.ToString(), FormatSize(selected.Sum(i => i.Scan!.Bytes)));
        bool risky = selected.Any(i => i.Risk != CleanRisk.Safe);
        if (MessageBox.Show(confirm, Loc.T("Cleaner_ConfirmTitle", "Confirm cleanup"), MessageBoxButton.YesNo,
                risky ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        CleanProgress.Value = 0;

        long freeBefore = TotalFreeSpace();
        long totalFreed = 0;
        int totalDeleted = 0, totalSkipped = 0;
        bool cancelled = false;

        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var item = selected[i];
                StatusText.Text = Loc.F("Cleaner_CleaningItem", "Cleaning {0}…", item.Title);
                item.SizeText = "…";

                var result = await Task.Run(() => SystemCleaner.Clean(item.Target, token), token);
                item.ApplyClean(result);

                totalFreed += result.BytesFreed;
                totalDeleted += result.FilesDeleted;
                totalSkipped += result.FilesSkipped;
                CleanProgress.Value = (i + 1) * 100.0 / selected.Count;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{Loc.T("BW_ActionFailed", "Failed")}: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }

        // The drive's own free-space change is the ground truth (it can differ a
        // little because other programs write while we clean).
        long freeDelta = TotalFreeSpace() - freeBefore;
        var summary = Loc.F("Cleaner_Done",
            "Freed {0} ({1:N0} files). Drive free space changed by {2}.",
            FormatSize(totalFreed), totalDeleted, (freeDelta >= 0 ? "+" : "−") + FormatSize(Math.Abs(freeDelta)));
        if (totalSkipped > 0)
            summary += "\n" + Loc.F("Cleaner_SkippedTotal", "{0:N0} files were in use or protected and were left alone.", totalSkipped);
        if (cancelled)
            summary = Loc.T("Cleaner_Cancelled", "Cancelled") + " — " + summary;

        StatusText.Text = summary;
        TotalSizeText.Text = FormatSize(totalFreed);
        FilesCountText.Text = Loc.F("Cleaner_Files", "{0:N0} files", totalDeleted);
        SkippedText.Text = "";

        // Old numbers no longer describe the disk; a new scan is needed.
        _hasScan = false;
        CleanButton.IsEnabled = false;

        MessageBox.Show(summary, Loc.T("Cleaner_Complete", "Cleanup complete"), MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
    }

    #endregion

    #region Helpers

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        ScanButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy && _hasScan;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;
        CleanProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in _items) item.CanToggle = !busy;
    }

    private long SelectedBytes() => _items.Where(i => i.IsSelected && i.Scan != null).Sum(i => i.Scan!.Bytes);

    private void UpdateSummary()
    {
        var scanned = _items.Where(i => i.IsSelected && i.Scan != null).ToList();
        if (scanned.Count == 0)
        {
            TotalSizeText.Text = "--";
            FilesCountText.Text = "";
            SkippedText.Text = "";
            return;
        }

        TotalSizeText.Text = FormatSize(scanned.Sum(i => i.Scan!.Bytes));
        FilesCountText.Text = Loc.F("Cleaner_Files", "{0:N0} files", scanned.Sum(i => i.Scan!.FileCount));

        int inUse = scanned.Sum(i => i.Scan!.InUseCount);
        int recent = scanned.Sum(i => i.Scan!.RecentCount);
        var parts = new List<string>();
        if (inUse > 0) parts.Add(Loc.F("Cleaner_InUse", "{0:N0} in use (skipped)", inUse));
        if (recent > 0) parts.Add(Loc.F("Cleaner_Recent", "{0:N0} recent (kept)", recent));
        SkippedText.Text = string.Join(" · ", parts);
    }

    private static long TotalFreeSpace()
    {
        long total = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady) total += drive.AvailableFreeSpace;
            }
            catch { }
        }
        return total;
    }

    private static HashSet<string>? LoadSelection()
    {
        try
        {
            var value = DatabaseService.Instance.GetSetting(SelectionSettingKey);
            return value == null ? null : value.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        }
        catch
        {
            return null;
        }
    }

    private void SaveSelection()
    {
        try
        {
            DatabaseService.Instance.SetSetting(SelectionSettingKey,
                string.Join(",", _items.Where(i => i.IsSelected).Select(i => i.Key)));
        }
        catch { }
    }

    internal static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    #endregion
}

/// <summary>One cleanup category row: selection, scan numbers and result.</summary>
public class CleanCategoryItem : INotifyPropertyChanged
{
    private readonly CleanCategory _category;

    public CleanCategoryItem(CleanCategory category, bool selected)
    {
        _category = category;
        _isSelected = selected;
        Title = Loc.T($"CleanCat_{category.Key}_Title", category.Target.ToString());
        Description = Loc.T($"CleanCat_{category.Key}_Desc", "");

        (RiskText, RiskBrush, RiskForeground) = category.Risk switch
        {
            CleanRisk.Safe => (Loc.T("Risk_Safe", "Safe"), ResourceBrush("SuccessBrush"), (Brush)Brushes.Black),
            CleanRisk.Medium => (Loc.T("Risk_Medium", "Check first"), ResourceBrush("WarningBrush"), (Brush)Brushes.Black),
            _ => (Loc.T("Risk_Caution", "Caution"), ResourceBrush("DangerBrush"), (Brush)Brushes.White)
        };
    }

    private static Brush ResourceBrush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    public CleanTarget Target => _category.Target;
    public string Key => _category.Key;
    public CleanRisk Risk => _category.Risk;
    public string Title { get; }
    public string Description { get; }
    public string RiskText { get; }
    public Brush RiskBrush { get; }
    public Brush RiskForeground { get; }

    public CleanScanResult? Scan { get; private set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
    }

    private bool _canToggle = true;
    public bool CanToggle
    {
        get => _canToggle;
        set { _canToggle = value; OnPropertyChanged(nameof(CanToggle)); }
    }

    private string _sizeText = "--";
    public string SizeText
    {
        get => _sizeText;
        set { _sizeText = value; OnPropertyChanged(nameof(SizeText)); }
    }

    private string _detail = "";
    public string Detail
    {
        get => _detail;
        private set { _detail = value; OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(DetailVisibility)); }
    }
    public Visibility DetailVisibility => string.IsNullOrEmpty(_detail) ? Visibility.Collapsed : Visibility.Visible;

    private string _note = "";
    public string Note
    {
        get => _note;
        private set { _note = value; OnPropertyChanged(nameof(Note)); OnPropertyChanged(nameof(NoteVisibility)); }
    }
    public Visibility NoteVisibility => string.IsNullOrEmpty(_note) ? Visibility.Collapsed : Visibility.Visible;

    public void ResetForScan()
    {
        Scan = null;
        SizeText = "--";
        Detail = "";
        Note = "";
    }

    public void ApplyScan(CleanScanResult scan)
    {
        Scan = scan;
        if (!scan.Found)
        {
            SizeText = Loc.T("Cleaner_NotFound", "Not found");
            Detail = "";
            Note = "";
            return;
        }

        SizeText = CleanerView.FormatSize(scan.Bytes);

        var parts = new List<string> { Loc.F("Cleaner_Files", "{0:N0} files", scan.FileCount) };
        if (scan.InUseCount > 0)
            parts.Add(Loc.F("Cleaner_InUseSize", "{0:N0} in use ({1}) skipped", scan.InUseCount, CleanerView.FormatSize(scan.InUseBytes)));
        if (scan.RecentCount > 0)
            parts.Add(Loc.F("Cleaner_Recent", "{0:N0} recent (kept)", scan.RecentCount));
        if (scan.DeniedCount > 0)
            parts.Add(Loc.F("Cleaner_Denied", "{0:N0} protected (skipped)", scan.DeniedCount));
        Detail = string.Join(" · ", parts);
        Note = NoteText(scan.NoteCode, scan.NoteArg);
    }

    public void ApplyClean(CleanExecResult result)
    {
        SizeText = Loc.F("Cleaner_Freed", "freed {0}", CleanerView.FormatSize(result.BytesFreed));
        var detail = Loc.F("Cleaner_Deleted", "{0:N0} deleted", result.FilesDeleted);
        if (result.FilesSkipped > 0)
            detail += " · " + Loc.F("Cleaner_Skipped", "{0:N0} skipped", result.FilesSkipped);
        Detail = detail;
        Note = NoteText(result.NoteCode, result.NoteArg);
    }

    private static string NoteText(string? code, string? arg) =>
        code == null ? "" : Loc.F($"Cleaner_Note_{code}", code, arg ?? "");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetX.App.Helpers;
using NetX.Core.System;
using NetX.Core.System.Storage;

namespace NetX.App.Views;

public partial class DiskSpaceView : Page
{
    private static SpaceScan? _lastScan;

    private readonly ObservableCollection<DefragDriveItem> _drives = new();
    private DefragDriveItem? _selected;
    private SpaceScan? _scan;
    private FolderNode? _folder;
    private List<FileRow> _fileRows = new();
    private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private bool _isActive, _busy;

    public DiskSpaceView()
    {
        InitializeComponent();
        DriveList.ItemsSource = _drives;
        Loaded += OnLoaded;
        Unloaded += (s, e) =>
        {
            _isActive = false;
            _cts?.Cancel();
        };
    }

    #region Drives

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isActive = true;
        await LoadDrivesAsync(_lastScan?.Letter);
    }

    private async Task LoadDrivesAsync(char? prefer)
    {
        List<DriveVolume> volumes;
        try { volumes = await Task.Run(DriveInventory.GetVolumes); }
        catch { volumes = new List<DriveVolume>(); }
        if (!_isActive) return;

        _drives.Clear();
        foreach (var v in volumes) _drives.Add(new DefragDriveItem(v));
        var pick = _drives.FirstOrDefault(d => d.Volume.Letter == prefer)
                   ?? _drives.FirstOrDefault(d => d.Volume.IsSystem) ?? _drives.FirstOrDefault();
        if (pick == null) return;
        pick.IsSelected = true;
        if (_selected?.Volume.Letter != pick.Volume.Letter) SelectDrive(pick);
        else _selected = pick;
    }

    private void DriveCard_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: DefragDriveItem item } && item != _selected) SelectDrive(item);
    }

    private void SelectDrive(DefragDriveItem item)
    {
        if (_busy) return;
        _selected = item;
        if (_lastScan?.Letter == item.Volume.Letter)
        {
            ShowScan(_lastScan);
        }
        else
        {
            _scan = null;
            FindingList.ItemsSource = null;
            FolderList.ItemsSource = null;
            FileList.ItemsSource = null;
            PathText.Text = "";
            UpButton.IsEnabled = OpenFolderButton.IsEnabled = false;
            StatusText.Text = Loc.F("Space_HintDrive", "Press Scan to see what takes the space on {0}.", item.Volume.DisplayName);
            UpdateSelection();
        }
    }

    #endregion

    #region Scan

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected == null) return;
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var volume = _selected.Volume;
        var progress = new Progress<SpaceProgress>(p =>
        {
            if (_isActive)
                StatusText.Text = Loc.F("Space_Scanning", "Scanning… {0:N0} files · {1}", p.Files, CleanerView.FormatSize(p.Bytes));
        });
        try
        {
            var scan = await SpaceAnalyzer.ScanAsync(volume, progress, _cts.Token);
            if (!_isActive) return;
            _lastScan = scan;
            _deleted.Clear();
            ShowScan(scan);
        }
        catch (OperationCanceledException)
        {
            if (_isActive) StatusText.Text = Loc.T("Space_Cancelled", "Scan stopped.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiskSpaceView] {ex}");
            if (_isActive) StatusText.Text = Loc.F("Space_Failed", "Scan failed: {0}", ErrorText.From(ex));
        }
        finally
        {
            if (_isActive) SetBusy(false);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        foreach (var d in _drives) d.IsEnabled = !busy;
        UpdateSelection();
    }

    private void ShowScan(SpaceScan scan)
    {
        _scan = scan;
        var status = Loc.F("Space_Done", "{0:N0} files in {1:N0} folders · {2} · scanned in {3:0} s",
            scan.FileCount, scan.FolderCount, CleanerView.FormatSize(scan.Root.Bytes), scan.Duration.TotalSeconds);
        if (scan.DeniedFolders > 0)
            status += " · " + Loc.F("Space_Denied", "{0:N0} folders Windows didn't let us read", scan.DeniedFolders);
        StatusText.Text = status;

        FindingList.ItemsSource = scan.Findings.Select(f => new FindingRow(f)).ToList();
        ShowFolder(scan.Root);
        ShowFiles(scan.LargestFiles, Loc.T("Space_LargestFiles", "Largest files"), preselect: false, isFinding: false);
    }

    #endregion

    #region Folders

    private void ShowFolder(FolderNode node)
    {
        _folder = node;
        PathText.Text = node.Path;
        UpButton.IsEnabled = node.Parent != null;
        OpenFolderButton.IsEnabled = true;

        long max = Math.Max(1, node.Children.FirstOrDefault()?.Bytes ?? 1);
        var rows = node.Children.Select(c => new FolderRow(c, node.Bytes, max)).ToList();
        if (node.SmallBytes > 0)
            rows.Add(new FolderRow(null, node.Bytes, max, Loc.T("Space_SmallItems", "(files here and small folders)"),
                node.SmallBytes + node.Bytes - node.Children.Sum(c => c.Bytes) - node.SmallBytes));
        FolderList.ItemsSource = rows;
    }

    private void FolderList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderList.SelectedItem is FolderRow { Node: { } node }) ShowFolder(node);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_folder?.Parent is { } parent) ShowFolder(parent);
    }

    private void OpenCurrent_Click(object sender, RoutedEventArgs e)
    {
        var path = (FolderList.SelectedItem as FolderRow)?.Node?.Path ?? _folder?.Path;
        if (path != null) OpenInExplorer(path, select: false);
    }

    #endregion

    #region Files

    private void ShowFiles(IEnumerable<BigFile> files, string title, bool preselect, bool isFinding)
    {
        _fileRows = files.Where(f => !_deleted.Contains(f.Path))
            .Select(f => new FileRow(f) { IsChecked = preselect && !f.IsProtected }).ToList();
        FileList.ItemsSource = _fileRows;
        FilesTitle.Text = title;
        ShowLargestButton.Visibility = isFinding ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private void ShowLargest_Click(object sender, RoutedEventArgs e)
    {
        if (_scan != null) ShowFiles(_scan.LargestFiles, Loc.T("Space_LargestFiles", "Largest files"), preselect: false, isFinding: false);
    }

    private void FileCheck_Changed(object sender, RoutedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var chosen = _fileRows.Where(r => r.IsChecked).ToList();
        SelectionText.Text = chosen.Count == 0
            ? Loc.T("Space_SelectHint", "Tick files to delete them.")
            : Loc.F("Space_Selected", "{0:N0} selected · {1}", chosen.Count, CleanerView.FormatSize(chosen.Sum(r => r.File.Bytes)));
        DeleteButton.IsEnabled = !_busy && chosen.Count > 0;
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _fileRows.Where(r => r.IsChecked && r.CanDelete).ToList();
        if (_busy || chosen.Count == 0) return;

        var list = string.Join("\n", chosen.Take(8).Select(r => $"• {r.Name} — {r.SizeText}"));
        if (chosen.Count > 8) list += "\n" + Loc.F("Space_AndMore", "…and {0:N0} more", chosen.Count - 8);
        var answer = MessageBox.Show(
            Loc.F("Space_ConfirmDelete",
                "Delete {0:N0} files ({1})?\n\n{2}\n\nYes — delete permanently (the space is free right away, can't be undone)\nNo — move to the Recycle Bin (you can restore them; the space comes back when you empty it)\nCancel — keep everything",
                chosen.Count, CleanerView.FormatSize(chosen.Sum(r => r.File.Bytes)), list),
            Loc.T("Space_ConfirmTitle", "Delete files"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer is not (MessageBoxResult.Yes or MessageBoxResult.No)) return;
        bool toBin = answer == MessageBoxResult.No;

        SetBusy(true);
        try
        {
            var paths = chosen.Select(r => r.File.Path).ToList();
            var (deleted, bytes, skipped) = await Task.Run(() => SpaceAnalyzer.DeleteFiles(paths, toBin));
            if (!_isActive) return;
            foreach (var p in paths.Where(p => !File.Exists(p))) _deleted.Add(p);
            _fileRows = _fileRows.Where(r => !_deleted.Contains(r.File.Path)).ToList();
            FileList.ItemsSource = _fileRows;

            var msg = toBin
                ? Loc.F("Space_Recycled", "Moved {0:N0} files ({1}) to the Recycle Bin.", deleted, CleanerView.FormatSize(bytes))
                : Loc.F("Space_DeletedDone", "Deleted {0:N0} files — {1} freed.", deleted, CleanerView.FormatSize(bytes));
            if (skipped > 0) msg += " " + Loc.F("Space_SkippedFiles", "{0:N0} were in use or protected and were kept.", skipped);
            StatusText.Text = msg;
            await RefreshDriveCardAsync();
        }
        finally
        {
            if (_isActive) SetBusy(false);
        }
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        var row = FileList.SelectedItem as FileRow ?? _fileRows.FirstOrDefault(r => r.IsChecked);
        if (row != null) OpenInExplorer(row.File.Path, select: true);
    }

    private static void OpenInExplorer(string path, bool select)
    {
        try
        {
            var args = select ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiskSpaceView] Explorer: {ex.Message}");
        }
    }

    #endregion

    #region Findings (things that can be freed)

    private async void Finding_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: FindingRow row } || _selected == null) return;
        var f = row.Finding;
        var title = row.Title;

        switch (f.Kind)
        {
            case SpaceFindingKind.OldInstallers:
            case SpaceFindingKind.OldArchives:
                ShowFiles(f.Files, title, preselect: true, isFinding: true);
                return;
            case SpaceFindingKind.DiscImages:
            case SpaceFindingKind.VirtualDisks:
                ShowFiles(f.Files, title, preselect: false, isFinding: true);
                return;
        }

        string question = f.Kind switch
        {
            SpaceFindingKind.Hibernation => Loc.F("Space_ConfirmHibernate",
                "Turn off hibernation and delete hiberfil.sys ({0})?\n\nThe PC can no longer hibernate, and \"Fast startup\" turns off (Windows starts a few seconds slower from a full shutdown). Sleep still works. You can turn it back on with: powercfg /hibernate on",
                row.SizeText),
            SpaceFindingKind.RestorePoints => Loc.F("Space_ConfirmRestore",
                "Delete all restore points except the newest ({0:N0} restore points, {1})?\n\nYou can still go back to the newest one. Older ones are gone for good.",
                f.Count - 1, row.SizeText),
            _ => Loc.F("Space_ConfirmRecycle", "Empty the Recycle Bin ({0})?\n\nThe files in it can't be restored afterwards.", row.SizeText)
        };
        if (MessageBox.Show(question, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        try
        {
            long freeBefore = FreeBytes(_selected.Volume.Letter);
            string done;
            switch (f.Kind)
            {
                case SpaceFindingKind.Hibernation:
                    bool ok = await SpaceAnalyzer.DisableHibernationAsync(CancellationToken.None);
                    done = ok ? Loc.T("Space_HibernateOff", "Hibernation is off.") : Loc.T("Space_HibernateFailed", "Windows didn't turn hibernation off.");
                    break;
                case SpaceFindingKind.RestorePoints:
                    var letter = _selected.Volume.Letter;
                    int n = await Task.Run(() => SpaceAnalyzer.DeleteOlderRestorePoints(letter));
                    done = Loc.F("Space_RestoreDeleted", "Deleted {0:N0} older restore points.", n);
                    break;
                default:
                    await Task.Run(() => SystemCleaner.Clean(CleanTarget.RecycleBin));
                    done = Loc.T("Space_BinEmptied", "The Recycle Bin is empty.");
                    break;
            }
            if (!_isActive) return;
            long gained = FreeBytes(_selected.Volume.Letter) - freeBefore;
            StatusText.Text = done + (gained > 0 ? " " + Loc.F("Space_Gained", "{0} more free space.", CleanerView.FormatSize(gained)) : "");
            row.MarkDone(done);
            await RefreshDriveCardAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiskSpaceView] Finding: {ex}");
            if (_isActive) StatusText.Text = Loc.F("Space_ActionFailed", "That didn't work: {0}", ErrorText.From(ex));
        }
        finally
        {
            if (_isActive) SetBusy(false);
        }
    }

    private static long FreeBytes(char letter)
    {
        try { return new DriveInfo(letter.ToString()).AvailableFreeSpace; }
        catch { return 0; }
    }

    private async Task RefreshDriveCardAsync()
    {
        var letter = _selected?.Volume.Letter;
        var keepScan = _scan;
        await LoadDrivesAsync(letter);
        if (keepScan != null && _isActive) _scan = keepScan;
    }

    #endregion
}

/// <summary>One folder row with its share of the parent.</summary>
public sealed class FolderRow
{
    public FolderRow(FolderNode? node, long parentBytes, long maxBytes, string? name = null, long? bytes = null)
    {
        Node = node;
        long size = bytes ?? node?.Bytes ?? 0;
        Name = name ?? node?.Name ?? "";
        Path = node?.Path ?? "";
        SizeText = CleanerView.FormatSize(size);
        PercentText = parentBytes > 0 ? $"{size * 100.0 / parentBytes:0.0}%" : "";
        BarWidth = Math.Max(2, 300.0 * size / Math.Max(1, maxBytes));
        BarBrush = (node?.IsProtected == true
            ? Application.Current.TryFindResource("SteelMidBrush")
            : Application.Current.TryFindResource("AccentGradientHorizontalBrush")) as Brush ?? Brushes.SteelBlue;
        LockVisibility = node?.IsProtected == true ? Visibility.Visible : Visibility.Collapsed;
    }

    public FolderNode? Node { get; }
    public string Name { get; }
    public string Path { get; }
    public string SizeText { get; }
    public string PercentText { get; }
    public double BarWidth { get; }
    public Brush BarBrush { get; }
    public Visibility LockVisibility { get; }
}

/// <summary>One file in the "largest files" (or a finding's) list.</summary>
public sealed class FileRow : INotifyPropertyChanged
{
    public FileRow(BigFile file)
    {
        File = file;
        Name = System.IO.Path.GetFileName(file.Path);
        var age = DateTime.UtcNow - file.LastWriteUtc;
        string ageText = age.TotalDays >= 365 ? Loc.F("Space_AgeYears", "{0:0.#} years old", age.TotalDays / 365)
            : age.TotalDays >= 1 ? Loc.F("Space_AgeDays", "{0:N0} days old", age.TotalDays)
            : Loc.T("Space_AgeToday", "changed today");
        Detail = (System.IO.Path.GetDirectoryName(file.Path) ?? "") + " · " + ageText +
                 (file.IsProtected ? " · " + Loc.T("Space_Protected", "Windows/program file — not deletable here") : "");
        SizeText = CleanerView.FormatSize(file.Bytes);
    }

    public BigFile File { get; }
    public string Path => File.Path;
    public string Name { get; }
    public string Detail { get; }
    public string SizeText { get; }
    public bool CanDelete => !File.IsProtected;

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked != value) { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A "can be freed" card.</summary>
public sealed class FindingRow : INotifyPropertyChanged
{
    public FindingRow(SpaceFinding finding)
    {
        Finding = finding;
        string key = finding.Kind.ToString();
        Title = Loc.T($"Space_Find_{key}_Title", key);
        Description = Loc.F($"Space_Find_{key}_Desc", "", finding.Count);
        SizeText = CleanerView.FormatSize(finding.Bytes);
        _actionText = finding.Kind is SpaceFindingKind.OldInstallers or SpaceFindingKind.OldArchives
            or SpaceFindingKind.DiscImages or SpaceFindingKind.VirtualDisks
            ? Loc.F("Space_Review", "Review {0:N0} files", finding.Count)
            : Loc.F("Space_Free", "Free {0}", SizeText);
        (RiskText, RiskBrush, RiskForeground) = finding.Risk switch
        {
            CleanRisk.Safe => (Loc.T("Risk_Safe", "Safe"), Res("SuccessBrush"), (Brush)Brushes.Black),
            CleanRisk.Medium => (Loc.T("Risk_Medium", "Check first"), Res("WarningBrush"), (Brush)Brushes.Black),
            _ => (Loc.T("Risk_Caution", "Caution"), Res("DangerBrush"), (Brush)Brushes.White)
        };
    }

    private static Brush Res(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    public SpaceFinding Finding { get; }
    public string Title { get; }
    public string Description { get; private set; }
    public string SizeText { get; }
    public string RiskText { get; }
    public Brush RiskBrush { get; }
    public Brush RiskForeground { get; }

    private string _actionText;
    public string ActionText => _actionText;
    public bool CanAct { get; private set; } = true;

    public void MarkDone(string text)
    {
        CanAct = false;
        _actionText = "✓";
        Description = text;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAct)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

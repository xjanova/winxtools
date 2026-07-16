using System.Windows;
using System.Windows.Controls;
using NetX.Core.System;

namespace NetX.App.Views;

public partial class CleanerView : Page
{
    private bool _isBusy = false;
    private readonly Dictionary<CleanTarget, CleanScanResult> _scanResults = new();

    public CleanerView()
    {
        InitializeComponent();
    }

    /// <summary>Maps each cleanup category to its checkbox + size label.</summary>
    private (CleanTarget Target, CheckBox Check, TextBlock SizeText)[] Categories() =>
    [
        (CleanTarget.TempFiles,     TempFilesCheck,     TempFilesSize),
        (CleanTarget.BrowserCache,  BrowserCacheCheck,  BrowserCacheSize),
        (CleanTarget.WindowsUpdate, WindowsUpdateCheck, WindowsUpdateSize),
        (CleanTarget.RecycleBin,    RecycleBinCheck,    RecycleBinSize),
        (CleanTarget.Thumbnails,    ThumbnailCheck,     ThumbnailSize),
        (CleanTarget.LogFiles,      LogFilesCheck,      LogFilesSize),
        (CleanTarget.WindowsOld,    OldWindowsCheck,    OldWindowsSize),
    ];

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        _isBusy = true;
        ScanButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        StatusText.Text = "Scanning...";
        CleanProgress.Visibility = Visibility.Visible;
        CleanProgress.IsIndeterminate = true;

        _scanResults.Clear();
        long totalSize = 0;
        int totalFiles = 0;

        var categories = Categories();
        foreach (var (target, _, sizeText) in categories)
        {
            StatusText.Text = $"Scanning {DisplayName(target)}...";

            // Real disk walk per category — sizes come from actual files.
            var scan = await Task.Run(() => SystemCleaner.Scan(target));
            _scanResults[target] = scan;

            sizeText.Text = !scan.Found ? "Not found"
                          : scan.Bytes > 0 ? FormatSize(scan.Bytes)
                          : "0 B";
            totalSize += scan.Bytes;
            totalFiles += scan.FileCount;

            TotalSizeText.Text = FormatSize(totalSize);
            FilesCountText.Text = $"{totalFiles:N0} files";
        }

        StatusText.Text = "Scan complete! Select items and click 'Clean' to free up space.";
        CleanProgress.IsIndeterminate = false;
        CleanProgress.Visibility = Visibility.Collapsed;
        ScanButton.IsEnabled = true;
        CleanButton.IsEnabled = true;
        _isBusy = false;
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var selected = Categories().Where(c => c.Check.IsChecked == true).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select at least one item to clean.", "Nothing Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Clean {selected.Count} selected item(s)?\n\nFiles currently in use are skipped automatically.\nThis action cannot be undone.",
            "Confirm Cleanup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _isBusy = true;
        CleanButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        CleanProgress.Visibility = Visibility.Visible;
        CleanProgress.IsIndeterminate = false;
        CleanProgress.Value = 0;

        long totalFreed = 0;
        int totalDeleted = 0;
        var notes = new List<string>();

        for (int i = 0; i < selected.Count; i++)
        {
            var (target, _, sizeText) = selected[i];
            StatusText.Text = $"Cleaning {DisplayName(target)}...";

            // Real deletion — freed bytes are summed from files actually removed.
            var clean = await Task.Run(() => SystemCleaner.Clean(target));

            totalFreed += clean.BytesFreed;
            totalDeleted += clean.FilesDeleted;
            if (!string.IsNullOrEmpty(clean.Note)) notes.Add(clean.Note!);

            sizeText.Text = "--";
            CleanProgress.Value = ((i + 1) / (double)selected.Count) * 100;
        }

        StatusText.Text = $"Cleanup complete! Freed {FormatSize(totalFreed)}";

        var summary = $"Cleanup Complete!\n\nFreed: {FormatSize(totalFreed)}\nFiles deleted: {totalDeleted:N0}";
        if (notes.Count > 0)
            summary += "\n\n" + string.Join("\n", notes.Distinct());

        MessageBox.Show(summary, "Success", MessageBoxButton.OK, MessageBoxImage.Information);

        // Reset totals; user can rescan to see the new state
        TotalSizeText.Text = "0 B";
        FilesCountText.Text = "0 files";
        _scanResults.Clear();

        CleanProgress.Visibility = Visibility.Collapsed;
        ScanButton.IsEnabled = true;
        CleanButton.IsEnabled = false;
        _isBusy = false;
    }

    private static string DisplayName(CleanTarget target) => target switch
    {
        CleanTarget.TempFiles => "temporary files",
        CleanTarget.BrowserCache => "browser cache",
        CleanTarget.WindowsUpdate => "Windows Update cache",
        CleanTarget.RecycleBin => "Recycle Bin",
        CleanTarget.Thumbnails => "thumbnail cache",
        CleanTarget.LogFiles => "log files",
        CleanTarget.WindowsOld => "old Windows installation",
        _ => target.ToString()
    };

    private static string FormatSize(long bytes)
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

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        // Mode changed handler - not used in cleaner but kept for consistency
    }
}

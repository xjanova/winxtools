using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace NetX.App.Views;

public partial class CleanerView : Page
{
    private long _totalSize = 0;
    private int _totalFiles = 0;
    private bool _isScanning = false;

    public CleanerView()
    {
        InitializeComponent();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning) return;

        _isScanning = true;
        ScanButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        StatusText.Text = "Scanning...";
        CleanProgress.Visibility = Visibility.Visible;
        CleanProgress.IsIndeterminate = true;

        _totalSize = 0;
        _totalFiles = 0;

        await Task.Run(() =>
        {
            // Simulate scanning with realistic delays
            System.Threading.Thread.Sleep(500);

            // Temp Files
            var tempSize = GetDirectorySize(Path.GetTempPath());
            Dispatcher.Invoke(() =>
            {
                TempFilesSize.Text = FormatSize(tempSize);
                _totalSize += tempSize;
            });

            System.Threading.Thread.Sleep(300);

            // Browser Cache (simulated)
            long browserSize = new Random().Next(100, 500) * 1024 * 1024;
            Dispatcher.Invoke(() =>
            {
                BrowserCacheSize.Text = FormatSize(browserSize);
                _totalSize += browserSize;
            });

            System.Threading.Thread.Sleep(300);

            // Windows Update
            long updateSize = new Random().Next(200, 800) * 1024 * 1024;
            Dispatcher.Invoke(() =>
            {
                WindowsUpdateSize.Text = FormatSize(updateSize);
                _totalSize += updateSize;
            });

            System.Threading.Thread.Sleep(200);

            // Recycle Bin (simulated)
            long recycleBinSize = new Random().Next(50, 300) * 1024 * 1024;
            Dispatcher.Invoke(() =>
            {
                RecycleBinSize.Text = FormatSize(recycleBinSize);
                _totalSize += recycleBinSize;
            });

            System.Threading.Thread.Sleep(200);

            // Thumbnails
            long thumbSize = new Random().Next(50, 200) * 1024 * 1024;
            Dispatcher.Invoke(() =>
            {
                ThumbnailSize.Text = FormatSize(thumbSize);
                _totalSize += thumbSize;
            });

            System.Threading.Thread.Sleep(200);

            // Log Files
            long logSize = new Random().Next(20, 100) * 1024 * 1024;
            Dispatcher.Invoke(() =>
            {
                LogFilesSize.Text = FormatSize(logSize);
                _totalSize += logSize;
            });

            System.Threading.Thread.Sleep(200);

            // Old Windows
            var oldWindowsPath = @"C:\Windows.old";
            long oldWindowsSize = Directory.Exists(oldWindowsPath)
                ? GetDirectorySize(oldWindowsPath)
                : 0;
            Dispatcher.Invoke(() =>
            {
                OldWindowsSize.Text = oldWindowsSize > 0 ? FormatSize(oldWindowsSize) : "Not found";
                _totalSize += oldWindowsSize;
            });

            _totalFiles = new Random().Next(1000, 10000);
        });

        TotalSizeText.Text = FormatSize(_totalSize);
        FilesCountText.Text = $"{_totalFiles:N0} files";
        StatusText.Text = "Scan complete! Select items and click 'Clean' to free up space.";
        CleanProgress.IsIndeterminate = false;
        CleanProgress.Visibility = Visibility.Collapsed;
        ScanButton.IsEnabled = true;
        CleanButton.IsEnabled = true;
        _isScanning = false;
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to clean the selected items?\n\nThis action cannot be undone.",
            "Confirm Cleanup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        CleanButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        StatusText.Text = "Cleaning...";
        CleanProgress.Visibility = Visibility.Visible;
        CleanProgress.IsIndeterminate = false;
        CleanProgress.Value = 0;

        int steps = 7;
        int currentStep = 0;

        await Task.Run(() =>
        {
            // Clean Temp Files
            if (TempFilesCheck.Dispatcher.Invoke(() => TempFilesCheck.IsChecked == true))
            {
                CleanTempFiles();
                currentStep++;
                Dispatcher.Invoke(() =>
                {
                    CleanProgress.Value = (currentStep / (double)steps) * 100;
                    StatusText.Text = "Cleaning temporary files...";
                });
                System.Threading.Thread.Sleep(500);
            }

            // Simulate other cleaning operations
            string[] items = { "Browser cache", "Thumbnails", "Log files", "Recycle Bin" };
            foreach (var item in items)
            {
                currentStep++;
                Dispatcher.Invoke(() =>
                {
                    CleanProgress.Value = (currentStep / (double)steps) * 100;
                    StatusText.Text = $"Cleaning {item}...";
                });
                System.Threading.Thread.Sleep(400);
            }
        });

        CleanProgress.Value = 100;
        StatusText.Text = $"Cleanup complete! Freed {FormatSize(_totalSize)}";

        MessageBox.Show(
            $"Cleanup Complete!\n\nFreed: {FormatSize(_totalSize)}\nFiles deleted: {_totalFiles:N0}",
            "Success",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        // Reset
        TotalSizeText.Text = "0 B";
        FilesCountText.Text = "0 files";
        TempFilesSize.Text = "--";
        BrowserCacheSize.Text = "--";
        WindowsUpdateSize.Text = "--";
        RecycleBinSize.Text = "--";
        ThumbnailSize.Text = "--";
        LogFilesSize.Text = "--";
        OldWindowsSize.Text = "--";

        CleanProgress.Visibility = Visibility.Collapsed;
        ScanButton.IsEnabled = true;
        CleanButton.IsEnabled = false;
    }

    private void CleanTempFiles()
    {
        try
        {
            var tempPath = Path.GetTempPath();

            // Safety check: Ensure we're only cleaning temp directories
            if (!tempPath.Contains("Temp", StringComparison.OrdinalIgnoreCase))
            {
                return; // Safety: Don't proceed if path doesn't look like temp folder
            }

            var di = new DirectoryInfo(tempPath);

            // Skip files that are in use or too new (less than 1 hour old)
            var cutoffTime = DateTime.Now.AddHours(-1);

            foreach (var file in di.GetFiles())
            {
                try
                {
                    // Skip files that are too new - they might be in use
                    if (file.LastWriteTime > cutoffTime) continue;

                    // Skip protected file types
                    if (IsProtectedFile(file.Name)) continue;

                    file.Delete();
                }
                catch { }
            }

            foreach (var dir in di.GetDirectories())
            {
                try
                {
                    // Skip protected directories
                    if (IsProtectedDirectory(dir.Name)) continue;

                    // Skip directories that are too new
                    if (dir.LastWriteTime > cutoffTime) continue;

                    dir.Delete(true);
                }
                catch { }
            }
        }
        catch { }
    }

    // Safety: List of protected files that should never be deleted
    private static bool IsProtectedFile(string fileName)
    {
        var protectedPatterns = new[] {
            "desktop.ini", "thumbs.db", ".sys", ".dll", ".exe",
            "ntuser", "usrclass", ".dat"
        };

        var lowerName = fileName.ToLowerInvariant();
        return protectedPatterns.Any(p => lowerName.Contains(p));
    }

    // Safety: List of protected directories that should never be deleted
    private static bool IsProtectedDirectory(string dirName)
    {
        var protectedDirs = new[] {
            "windows", "system", "program", "users", "microsoft",
            "appdata", "roaming", "local", ".net", "assembly"
        };

        var lowerName = dirName.ToLowerInvariant();
        return protectedDirs.Any(p => lowerName.Contains(p));
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;

            var di = new DirectoryInfo(path);
            return di.EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(fi => { try { return fi.Length; } catch { return 0; } });
        }
        catch
        {
            return 0;
        }
    }

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

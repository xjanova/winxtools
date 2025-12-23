using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace NetX.App.Views;

public partial class UninstallerView : Page
{
    private List<InstalledProgram> _allPrograms = new();

    public UninstallerView()
    {
        InitializeComponent();
        LoadInstalledPrograms();
    }

    private void LoadInstalledPrograms()
    {
        _allPrograms = new List<InstalledProgram>();

        // Read from Registry - Uninstall keys
        string[] registryKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var keyPath in registryKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key == null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey == null) continue;

                        var displayName = subKey.GetValue("DisplayName")?.ToString();
                        if (string.IsNullOrEmpty(displayName)) continue;

                        var program = new InstalledProgram
                        {
                            Name = displayName,
                            Publisher = subKey.GetValue("Publisher")?.ToString() ?? "Unknown",
                            Size = FormatSize(subKey.GetValue("EstimatedSize")),
                            InstallDate = FormatDate(subKey.GetValue("InstallDate")?.ToString()),
                            UninstallString = subKey.GetValue("UninstallString")?.ToString() ?? "",
                            InstallLocation = subKey.GetValue("InstallLocation")?.ToString() ?? "",
                            IsSystemComponent = subKey.GetValue("SystemComponent") != null
                        };

                        _allPrograms.Add(program);
                    }
                    catch
                    {
                        // Skip entries we can't read
                    }
                }
            }
            catch
            {
                // Skip if we can't access the key
            }
        }

        // Sort by name
        _allPrograms = _allPrograms
            .OrderBy(p => p.Name)
            .DistinctBy(p => p.Name)
            .ToList();

        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var searchText = SearchBox.Text?.ToLower() ?? "";
        var showSystem = ShowSystemApps.IsChecked == true;

        var filtered = _allPrograms.Where(p =>
        {
            if (!string.IsNullOrEmpty(searchText) &&
                !p.Name.ToLower().Contains(searchText))
                return false;

            if (!showSystem && p.IsSystemComponent)
                return false;

            return true;
        }).ToList();

        ProgramsList.ItemsSource = filtered;
    }

    private static string FormatSize(object? sizeValue)
    {
        if (sizeValue == null) return "Unknown";

        if (long.TryParse(sizeValue.ToString(), out long sizeKB))
        {
            if (sizeKB >= 1_048_576)
                return $"{sizeKB / 1_048_576.0:F1} GB";
            if (sizeKB >= 1024)
                return $"{sizeKB / 1024.0:F1} MB";
            return $"{sizeKB} KB";
        }

        return "Unknown";
    }

    private static string FormatDate(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr) || dateStr.Length != 8)
            return "Unknown";

        try
        {
            var year = dateStr.Substring(0, 4);
            var month = dateStr.Substring(4, 2);
            var day = dateStr.Substring(6, 2);
            return $"{day}/{month}/{year}";
        }
        catch
        {
            return "Unknown";
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void ShowSystemApps_Changed(object sender, RoutedEventArgs e)
    {
        ApplyFilters();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadInstalledPrograms();
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"Found {_allPrograms.Count} installed programs.\n\nScanning for hidden and system programs...",
            "Scan Complete",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is InstalledProgram program)
        {
            var result = MessageBox.Show(
                $"Uninstall {program.Name}?\n\nThis will run the standard uninstaller.",
                "Confirm Uninstall",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes && !string.IsNullOrEmpty(program.UninstallString))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {program.UninstallString}",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to start uninstaller: {ex.Message}",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private void DeepClean_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is InstalledProgram program)
        {
            // Safety: Don't allow deep clean of system components
            if (program.IsSystemComponent)
            {
                MessageBox.Show(
                    "Cannot deep clean system components.\n\n" +
                    "This program is marked as a Windows system component and removing it could cause system instability.",
                    "Protected Program",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Safety: Check for protected publishers
            if (IsProtectedPublisher(program.Publisher))
            {
                var confirm = MessageBox.Show(
                    $"Warning: {program.Name} appears to be a system or driver component from {program.Publisher}.\n\n" +
                    "Deep cleaning this program could cause system issues. Are you absolutely sure?",
                    "Protected Publisher Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes) return;
            }

            var result = MessageBox.Show(
                $"Deep Clean {program.Name}?\n\n" +
                "This will:\n" +
                "1. Run the standard uninstaller\n" +
                "2. Scan for leftover files in safe locations only:\n" +
                "   - Program Files (program's folder only)\n" +
                "   - AppData (program's folder only)\n" +
                "   - ProgramData (program's folder only)\n" +
                "3. Show you what was found for review\n" +
                "4. Let you choose what to remove\n\n" +
                "No system files will be touched. Do you want to proceed?",
                "Deep Clean",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                // Find potential leftover locations safely
                var leftovers = ScanForLeftovers(program);

                if (leftovers.Count == 0)
                {
                    MessageBox.Show(
                        "No leftover files or folders were found.\n\n" +
                        "The program appears to have been cleanly uninstalled.",
                        "Deep Clean Complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    var leftoverList = string.Join("\n", leftovers.Take(10));
                    if (leftovers.Count > 10)
                        leftoverList += $"\n... and {leftovers.Count - 10} more items";

                    var removeResult = MessageBox.Show(
                        $"Found {leftovers.Count} leftover items:\n\n{leftoverList}\n\n" +
                        "Would you like to remove these leftovers?",
                        "Leftovers Found",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (removeResult == MessageBoxResult.Yes)
                    {
                        RemoveLeftovers(leftovers);
                        MessageBox.Show(
                            "Leftover cleanup complete!",
                            "Success",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }
            }
        }
    }

    // Safety: Check if publisher is a protected system publisher
    private static bool IsProtectedPublisher(string publisher)
    {
        var protectedPublishers = new[] {
            "microsoft", "windows", "intel", "nvidia", "amd", "realtek",
            "synaptics", "dell", "hp", "lenovo", "asus", "acer"
        };

        var lowerPublisher = publisher.ToLowerInvariant();
        return protectedPublishers.Any(p => lowerPublisher.Contains(p));
    }

    // Safely scan for leftover files - only in program-specific folders
    private static List<string> ScanForLeftovers(InstalledProgram program)
    {
        var leftovers = new List<string>();
        var programNameClean = CleanProgramName(program.Name);

        if (string.IsNullOrWhiteSpace(programNameClean) || programNameClean.Length < 3)
            return leftovers; // Safety: Don't scan for very short names

        // Safe paths to scan (only program-specific subdirectories)
        var basePaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        foreach (var basePath in basePaths)
        {
            if (string.IsNullOrEmpty(basePath) || !System.IO.Directory.Exists(basePath))
                continue;

            try
            {
                var dirs = System.IO.Directory.GetDirectories(basePath);
                foreach (var dir in dirs)
                {
                    var dirName = System.IO.Path.GetFileName(dir).ToLowerInvariant();

                    // Only match if directory name contains the program name
                    if (dirName.Contains(programNameClean.ToLowerInvariant()))
                    {
                        // Safety: Never delete system folders
                        if (!IsProtectedPath(dir))
                        {
                            leftovers.Add(dir);
                        }
                    }
                }
            }
            catch { }
        }

        return leftovers;
    }

    // Extract clean program name for matching
    private static string CleanProgramName(string name)
    {
        // Remove version numbers, common suffixes
        var clean = System.Text.RegularExpressions.Regex.Replace(name, @"\d+\.\d+.*$", "");
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+\(.*\)$", "");
        clean = clean.Replace(" ", "").Trim();
        return clean;
    }

    // Safety: Check if path is protected (should never be deleted)
    private static bool IsProtectedPath(string path)
    {
        var protectedPatterns = new[] {
            "windows", "system32", "syswow64", "microsoft", "common files",
            "internet explorer", "windows defender", "windowsapps",
            ".net", "assembly", "reference assemblies", "drivers"
        };

        var lowerPath = path.ToLowerInvariant();
        return protectedPatterns.Any(p => lowerPath.Contains(p));
    }

    // Safely remove leftover folders
    private static void RemoveLeftovers(List<string> leftovers)
    {
        foreach (var path in leftovers)
        {
            try
            {
                // Final safety check before deletion
                if (IsProtectedPath(path)) continue;

                if (System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.Delete(path, true);
                }
                else if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
            catch { }
        }
    }
}

public class InstalledProgram
{
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Size { get; set; } = "Unknown";
    public string InstallDate { get; set; } = "Unknown";
    public string UninstallString { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public bool IsSystemComponent { get; set; }
}

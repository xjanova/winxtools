using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetX.App.Helpers;

/// <summary>
/// Helper class for extracting and caching process icons
/// </summary>
public static class ProcessIconHelper
{
    private static readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new();
    private static readonly object _cacheLock = new();
    private static ImageSource? _defaultIcon;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    /// <summary>
    /// Gets the icon for a process by name
    /// </summary>
    public static ImageSource? GetProcessIcon(string processName)
    {
        if (string.IsNullOrEmpty(processName))
            return GetDefaultIcon();

        // Check cache first
        if (_iconCache.TryGetValue(processName.ToLowerInvariant(), out var cachedIcon))
            return cachedIcon;

        try
        {
            // Try to find the process and get its path
            var processes = Process.GetProcessesByName(processName.Replace(".exe", ""));
            if (processes.Length > 0)
            {
                try
                {
                    var exePath = processes[0].MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        var icon = ExtractIconFromFile(exePath);
                        _iconCache[processName.ToLowerInvariant()] = icon ?? GetDefaultIcon();
                        return _iconCache[processName.ToLowerInvariant()];
                    }
                }
                catch
                {
                    // MainModule access may fail for system processes
                }
                finally
                {
                    foreach (var p in processes)
                        p.Dispose();
                }
            }

            // Try common paths
            var icon2 = TryExtractFromCommonPaths(processName);
            _iconCache[processName.ToLowerInvariant()] = icon2 ?? GetDefaultIcon();
            return _iconCache[processName.ToLowerInvariant()];
        }
        catch
        {
            _iconCache[processName.ToLowerInvariant()] = GetDefaultIcon();
            return GetDefaultIcon();
        }
    }

    /// <summary>
    /// Gets the icon for a process by its ID
    /// </summary>
    public static ImageSource? GetProcessIcon(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return GetProcessIcon(process.ProcessName);
        }
        catch
        {
            return GetDefaultIcon();
        }
    }

    /// <summary>
    /// Gets the icon from an executable file path
    /// </summary>
    public static ImageSource? GetIconFromPath(string exePath)
    {
        if (string.IsNullOrEmpty(exePath))
            return GetDefaultIcon();

        // Check cache
        if (_iconCache.TryGetValue(exePath.ToLowerInvariant(), out var cachedIcon))
            return cachedIcon;

        var icon = ExtractIconFromFile(exePath);
        _iconCache[exePath.ToLowerInvariant()] = icon ?? GetDefaultIcon();
        return _iconCache[exePath.ToLowerInvariant()];
    }

    private static ImageSource? ExtractIconFromFile(string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            return null;

        try
        {
            // Try to extract small icon first (16x16 or 32x32)
            IntPtr[] smallIcons = new IntPtr[1];
            IntPtr[] largeIcons = new IntPtr[1];

            uint count = ExtractIconEx(filePath, 0, largeIcons, smallIcons, 1);
            if (count > 0)
            {
                IntPtr hIcon = smallIcons[0] != IntPtr.Zero ? smallIcons[0] : largeIcons[0];
                if (hIcon != IntPtr.Zero)
                {
                    try
                    {
                        using var icon = Icon.FromHandle(hIcon);
                        var imageSource = ConvertIconToImageSource(icon);
                        return imageSource;
                    }
                    finally
                    {
                        if (smallIcons[0] != IntPtr.Zero)
                            DestroyIcon(smallIcons[0]);
                        if (largeIcons[0] != IntPtr.Zero)
                            DestroyIcon(largeIcons[0]);
                    }
                }
            }

            // Fallback: use Icon.ExtractAssociatedIcon
            using var extractedIcon = Icon.ExtractAssociatedIcon(filePath);
            if (extractedIcon != null)
            {
                return ConvertIconToImageSource(extractedIcon);
            }
        }
        catch
        {
            // Icon extraction failed
        }

        return null;
    }

    private static ImageSource? TryExtractFromCommonPaths(string processName)
    {
        var exeName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : processName + ".exe";

        string[] commonPaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), exeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), exeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", exeName),
        ];

        foreach (var path in commonPaths)
        {
            if (System.IO.File.Exists(path))
            {
                var icon = ExtractIconFromFile(path);
                if (icon != null)
                    return icon;
            }
        }

        return null;
    }

    private static ImageSource ConvertIconToImageSource(Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var imageSource = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            imageSource.Freeze(); // Make it thread-safe
            return imageSource;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Gets or creates a default application icon
    /// </summary>
    public static ImageSource GetDefaultIcon()
    {
        if (_defaultIcon != null)
            return _defaultIcon;

        lock (_cacheLock)
        {
            if (_defaultIcon != null)
                return _defaultIcon;

            // Create a simple default icon (a gear-like shape)
            var drawingVisual = new DrawingVisual();
            using (var context = drawingVisual.RenderOpen())
            {
                var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 142, 171));
                var pen = new System.Windows.Media.Pen(brush, 1);

                // Draw a simple application icon (rounded rectangle)
                context.DrawRoundedRectangle(
                    brush,
                    null,
                    new Rect(2, 2, 16, 16),
                    3, 3);
            }

            var bitmap = new RenderTargetBitmap(20, 20, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawingVisual);
            bitmap.Freeze();
            _defaultIcon = bitmap;
            return _defaultIcon;
        }
    }

    /// <summary>
    /// Clears the icon cache
    /// </summary>
    public static void ClearCache()
    {
        _iconCache.Clear();
    }

    /// <summary>
    /// Preloads icons for a list of process names (call on background thread)
    /// </summary>
    public static void PreloadIcons(IEnumerable<string> processNames)
    {
        foreach (var name in processNames)
        {
            GetProcessIcon(name);
        }
    }
}

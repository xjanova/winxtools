using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using NetX.Core.Network;

namespace NetX.App.Views;

public partial class BandwidthControlView : Page
{
    private readonly DispatcherTimer _updateTimer;
    private readonly NetworkMonitor _networkMonitor;
    private readonly ObservableCollection<InterfaceControlItem> _interfaces = new();
    private const int MaxDataPoints = 60;

    // Debounce timers for applying limits (0.5 second delay after slider stops)
    private readonly Dictionary<string, DispatcherTimer> _debounceTimers = new();
    private readonly Dictionary<string, (int download, int upload)> _pendingLimits = new();

    public BandwidthControlView()
    {
        InitializeComponent();

        _networkMonitor = NetworkMonitor.Instance;

        InterfacesList.ItemsSource = _interfaces;

        // Setup timer for real-time updates (2 seconds interval for smooth performance)
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _updateTimer.Tick += UpdateTimer_Tick;

        // Defer interface initialization to after page is loaded (prevents initial lag)
        Loaded += (s, e) =>
        {
            if (_interfaces.Count == 0)
            {
                InitializeInterfaces();
                UpdateEngineStatus();
                _updateTimer.Start();
            }
        };

        Unloaded += (s, e) =>
        {
            _updateTimer.Stop();
            // Stop all debounce timers
            foreach (var timer in _debounceTimers.Values)
            {
                timer.Stop();
            }
        };
    }

    private void InitializeInterfaces()
    {
        try
        {
            // Get all physical adapters that are UP
            var allInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                          && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                          && IsPhysicalAdapter(ni))
                .ToList();

            // If only one interface exists, show it
            // If multiple interfaces exist, filter to only those with actual traffic
            var interfaces = allInterfaces;
            if (allInterfaces.Count > 1)
            {
                // Check which interfaces have traffic
                var activeInterfaces = allInterfaces.Where(ni =>
                {
                    try
                    {
                        var stats = ni.GetIPStatistics();
                        // Consider active if has any bytes sent/received
                        return stats.BytesReceived > 0 || stats.BytesSent > 0;
                    }
                    catch { return false; }
                }).ToList();

                // Use active interfaces if any found, otherwise use all
                if (activeInterfaces.Count > 0)
                {
                    interfaces = activeInterfaces;
                }
            }

            // If still multiple interfaces with traffic, pick the one with most traffic
            if (interfaces.Count > 1)
            {
                interfaces = interfaces
                    .Select(ni => {
                        try
                        {
                            var stats = ni.GetIPStatistics();
                            return (ni, total: stats.BytesReceived + stats.BytesSent);
                        }
                        catch { return (ni, total: 0L); }
                    })
                    .OrderByDescending(x => x.total)
                    .Take(1) // Only show the most active interface
                    .Select(x => x.ni)
                    .ToList();
            }

            foreach (var ni in interfaces)
            {
                var item = new InterfaceControlItem
                {
                    InterfaceId = ni.Id,
                    Name = ni.Name,
                    Description = ni.Description,
                    InterfaceType = GetInterfaceTypeName(ni.NetworkInterfaceType),
                    IsWiFi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    DownloadSliderValue = 30, // Rightmost = Unlimited
                    UploadSliderValue = 30,   // Rightmost = Unlimited
                    DownloadLimitText = "Unlimited",
                    UploadLimitText = "Unlimited"
                };

                // Set icon based on type
                item.UpdateIcon();

                // Initialize chart for this interface
                item.InitializeChart(MaxDataPoints);

                // Load existing limits (stored as Kbps)
                var existingRule = BandwidthLimiter.Instance.GetLimit($"interface:{ni.Id}");
                if (existingRule != null)
                {
                    // DownloadLimitKBps/UploadLimitKBps store Kbps values
                    int dlKbps = (int)existingRule.DownloadLimitKBps;
                    int ulKbps = (int)existingRule.UploadLimitKBps;

                    item.DownloadSliderValue = KbpsToSlider(dlKbps);
                    item.UploadSliderValue = KbpsToSlider(ulKbps);
                    item.UpdateLimitText(dlKbps, ulKbps);

                    // Re-apply the saved limits immediately on startup
                    ApplyInterfaceLimit(ni.Id, dlKbps, ulKbps);
                }

                // Subscribe to slider changes
                item.PropertyChanged += InterfaceItem_PropertyChanged;

                _interfaces.Add(item);
            }

            // Select this interface in NetworkMonitor for accurate stats
            if (interfaces.Count == 1)
            {
                _networkMonitor.SelectInterface(interfaces[0].Id);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error initializing interfaces: {ex.Message}");
        }
    }

    /// <summary>
    /// Filter to only show physical hardware adapters (WiFi and Ethernet)
    /// </summary>
    private bool IsPhysicalAdapter(NetworkInterface ni)
    {
        // Skip virtual adapters
        var desc = ni.Description.ToLowerInvariant();
        var name = ni.Name.ToLowerInvariant();

        // Skip virtual and software adapters
        if (desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("virtualbox") ||
            desc.Contains("hyper-v") || desc.Contains("vpn") || desc.Contains("tap-") ||
            desc.Contains("tunnel") || desc.Contains("pseudo") || desc.Contains("miniport") ||
            desc.Contains("wan") || desc.Contains("teredo") || desc.Contains("isatap") ||
            desc.Contains("6to4") || desc.Contains("bluetooth") ||
            name.Contains("vethernet") || name.Contains("docker") || name.Contains("wsl"))
        {
            return false;
        }

        // Only include Ethernet and WiFi
        return ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
               ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
               ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet;
    }

    private string GetInterfaceTypeName(NetworkInterfaceType type)
    {
        return type switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet => "Ethernet",
            NetworkInterfaceType.GigabitEthernet => "Gigabit Ethernet",
            _ => type.ToString()
        };
    }

    private void InterfaceItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not InterfaceControlItem item) return;

        // Handle Download slider change - slider value is now direct index (0-30)
        if (e.PropertyName == nameof(InterfaceControlItem.DownloadSliderValue))
        {
            var downloadKbps = SliderToKbps(item.DownloadSliderValue);

            // Update download limit text
            item.UpdateDownloadLimitText(downloadKbps);

            // Get current upload value and store pending limits
            var uploadKbps = SliderToKbps(item.UploadSliderValue);

            _pendingLimits[item.InterfaceId] = (downloadKbps, uploadKbps);
            ScheduleApplyLimit(item.InterfaceId);
        }
        // Handle Upload slider change - slider value is now direct index (0-30)
        else if (e.PropertyName == nameof(InterfaceControlItem.UploadSliderValue))
        {
            var uploadKbps = SliderToKbps(item.UploadSliderValue);

            // Update upload limit text
            item.UpdateUploadLimitText(uploadKbps);

            // Get current download value and store pending limits
            var downloadKbps = SliderToKbps(item.DownloadSliderValue);

            _pendingLimits[item.InterfaceId] = (downloadKbps, uploadKbps);
            ScheduleApplyLimit(item.InterfaceId);
        }
    }

    /// <summary>
    /// Schedule applying the limit with 0.5 second debounce for quick response
    /// </summary>
    private void ScheduleApplyLimit(string interfaceId)
    {
        // Create or reset the debounce timer for this interface
        if (!_debounceTimers.TryGetValue(interfaceId, out var timer))
        {
            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();

                // Apply the pending limit
                if (_pendingLimits.TryGetValue(interfaceId, out var limits))
                {
                    ApplyInterfaceLimit(interfaceId, limits.download, limits.upload);
                    Debug.WriteLine($"Applied limit for {interfaceId}: DL={limits.download} KB/s, UL={limits.upload} KB/s");
                }
            };
            _debounceTimers[interfaceId] = timer;
        }

        // Reset the timer (restart the 1 second countdown)
        timer.Stop();
        timer.Start();
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var interfaceBandwidth = _networkMonitor.GetInterfaceBandwidth();

            foreach (var item in _interfaces)
            {
                if (interfaceBandwidth.TryGetValue(item.InterfaceId, out var bandwidth))
                {
                    item.CurrentDownloadSpeed = FormatSpeed(bandwidth.download);
                    item.CurrentUploadSpeed = FormatSpeed(bandwidth.upload);

                    // Update chart data
                    item.UpdateChartData(bandwidth.download, bandwidth.upload);
                }
            }

            // Update PacketEngine status periodically
            UpdateEngineStatus();
        }
        catch { }
    }

    private void UpdateEngineStatus()
    {
        try
        {
            var isActive = _networkMonitor.IsPacketEngineActive;

            if (isActive)
            {
                EngineStatusDot.Fill = (Brush)FindResource("SuccessBrush");
                EngineStatusText.Text = "Packet Control Active";
            }
            else
            {
                EngineStatusDot.Fill = (Brush)FindResource("WarningBrush");
                EngineStatusText.Text = "Run as Admin for best control";
            }
        }
        catch { }
    }

    private void ApplyInterfaceLimit(string interfaceId, int downloadKbps, int uploadKbps)
    {
        try
        {
            var ruleName = $"interface:{interfaceId}";

            // Convert Kbps to bytes per second for PacketEngine
            // -1 (unlimited) becomes 0 (no limit)
            // 0 (blocked) becomes 1 byte/sec (effectively blocks by extreme throttling)
            long dlBytesPerSec;
            long ulBytesPerSec;

            if (downloadKbps == 0)
                dlBytesPerSec = 1; // Block = 1 byte/sec (extremely slow = blocked)
            else if (downloadKbps < 0)
                dlBytesPerSec = 0;  // Unlimited (no limit)
            else
                dlBytesPerSec = KbpsToBytesPerSec(downloadKbps);

            if (uploadKbps == 0)
                ulBytesPerSec = 1; // Block = 1 byte/sec (extremely slow = blocked)
            else if (uploadKbps < 0)
                ulBytesPerSec = 0;  // Unlimited (no limit)
            else
                ulBytesPerSec = KbpsToBytesPerSec(uploadKbps);

            // Use PacketEngine for throttling and blocking
            var packetEngine = PacketEngine.Instance;
            if (packetEngine.IsRunning)
            {
                // If both are unlimited (-1 Kbps), remove throttle entirely
                if (downloadKbps < 0 && uploadKbps < 0)
                {
                    packetEngine.SetGlobalThrottle(0, 0); // Remove throttle
                    Debug.WriteLine("PacketEngine: All limits removed");
                }
                else
                {
                    // Apply limits (1 = blocked/extremely slow, 0 = unlimited, >1 = limit in B/s)
                    packetEngine.SetGlobalThrottle(dlBytesPerSec, ulBytesPerSec);
                    Debug.WriteLine($"PacketEngine: DL={downloadKbps} Kbps ({dlBytesPerSec} B/s), UL={uploadKbps} Kbps ({ulBytesPerSec} B/s)");
                }
            }
            else
            {
                Debug.WriteLine("PacketEngine not running - limits will not be applied");
            }

            // Store in BandwidthLimiter for persistence (store as Kbps)
            if (downloadKbps < 0 && uploadKbps < 0)
            {
                BandwidthLimiter.Instance.RemoveLimit(ruleName);
            }
            else
            {
                // Store Kbps values
                BandwidthLimiter.Instance.SetProcessLimit(ruleName, downloadKbps, uploadKbps);
                BandwidthLimiter.Instance.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error applying interface limit: {ex.Message}");
        }
    }

    private void BlockInterface(string interfaceId, bool blockDownload, bool blockUpload)
    {
        try
        {
            // Find the interface to get its name for the firewall rule
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == interfaceId);

            if (ni == null) return;

            var ruleName = $"NetX_Block_{ni.Name.Replace(" ", "_")}";

            // Remove existing rules first
            UnblockInterface(interfaceId);

            // Create a firewall rule to block traffic on this interface
            if (blockDownload)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall add rule name=\"{ruleName}_In\" dir=in action=block interface=\"{ni.Name}\" enable=yes",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi)?.WaitForExit(5000);
                Debug.WriteLine($"Created inbound block rule for {ni.Name}");
            }

            if (blockUpload)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall add rule name=\"{ruleName}_Out\" dir=out action=block interface=\"{ni.Name}\" enable=yes",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi)?.WaitForExit(5000);
                Debug.WriteLine($"Created outbound block rule for {ni.Name}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error blocking interface: {ex.Message}");
            MessageBox.Show(
                $"Failed to block interface: {ex.Message}\n\nPlease run the application as Administrator.",
                "Administrator Required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void UnblockInterface(string interfaceId)
    {
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == interfaceId);

            if (ni == null) return;

            var ruleName = $"NetX_Block_{ni.Name.Replace(" ", "_")}";

            // Remove firewall rules - use UseShellExecute=true for elevation
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall delete rule name=\"{ruleName}_In\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi)?.WaitForExit(3000);

            psi.Arguments = $"advfirewall firewall delete rule name=\"{ruleName}_Out\"";
            Process.Start(psi)?.WaitForExit(3000);

            Debug.WriteLine($"Removed block rules for {ni.Name}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error unblocking interface: {ex.Message}");
        }
    }

    // Predefined speed limits in Kbps (kilobits per second)
    // Index 0 = Blocked (left), Last index = Unlimited (right)
    // More granular at low speeds (Kbps range), then Mbps range up to 2048 Mbps
    private static readonly int[] SpeedPresetsKbps =
    {
        0,          // 0: Blocked (leftmost)
        64,         // 1: 64 Kbps
        128,        // 2: 128 Kbps
        256,        // 3: 256 Kbps
        384,        // 4: 384 Kbps
        512,        // 5: 512 Kbps
        768,        // 6: 768 Kbps
        1024,       // 7: 1 Mbps
        1536,       // 8: 1.5 Mbps
        2048,       // 9: 2 Mbps
        3072,       // 10: 3 Mbps
        4096,       // 11: 4 Mbps
        5120,       // 12: 5 Mbps
        6144,       // 13: 6 Mbps
        8192,       // 14: 8 Mbps
        10240,      // 15: 10 Mbps
        15360,      // 16: 15 Mbps
        20480,      // 17: 20 Mbps
        30720,      // 18: 30 Mbps
        51200,      // 19: 50 Mbps
        76800,      // 20: 75 Mbps
        102400,     // 21: 100 Mbps
        153600,     // 22: 150 Mbps
        204800,     // 23: 200 Mbps
        307200,     // 24: 300 Mbps
        512000,     // 25: 500 Mbps
        768000,     // 26: 750 Mbps
        1024000,    // 27: 1000 Mbps (1 Gbps)
        1536000,    // 28: 1500 Mbps
        2097152,    // 29: 2048 Mbps (max)
        -1          // 30: Unlimited (rightmost)
    };

    /// <summary>
    /// Convert slider index (0-30) to Kbps - slider value IS the index
    /// </summary>
    private static int SliderToKbps(double sliderValue)
    {
        int index = (int)Math.Round(sliderValue);
        index = Math.Clamp(index, 0, SpeedPresetsKbps.Length - 1);
        return SpeedPresetsKbps[index];
    }

    /// <summary>
    /// Convert Kbps to slider index (0-30)
    /// 0 = Blocked (left), 30 = Unlimited (right)
    /// </summary>
    private static int KbpsToSlider(int kbps)
    {
        if (kbps < 0) return 30; // Unlimited = last index (rightmost)
        if (kbps == 0) return 0; // Blocked = index 0 (leftmost)

        // Find exact preset index
        for (int i = 0; i < SpeedPresetsKbps.Length; i++)
        {
            if (SpeedPresetsKbps[i] == kbps)
            {
                return i;
            }
        }

        // If not exact match, find closest (skip blocked=0 and unlimited=-1)
        int closestIndex = 1;
        int closestDiff = int.MaxValue;

        for (int i = 1; i < SpeedPresetsKbps.Length - 1; i++)
        {
            int preset = SpeedPresetsKbps[i];
            int diff = Math.Abs(preset - kbps);
            if (diff < closestDiff)
            {
                closestDiff = diff;
                closestIndex = i;
            }
        }

        return closestIndex;
    }

    /// <summary>
    /// Convert Kbps to bytes per second for PacketEngine
    /// Kbps = kilobits per second, so multiply by 1000 / 8 = 125
    /// </summary>
    private static long KbpsToBytesPerSec(int kbps)
    {
        if (kbps <= 0) return kbps; // -1 (unlimited) or 0 (blocked)
        return (long)kbps * 125; // Kbps -> bytes/sec (1 Kbps = 125 bytes/sec)
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_073_741_824)
            return $"{bytesPerSecond / 1_073_741_824:F2} GB/s";
        if (bytesPerSecond >= 1_048_576)
            return $"{bytesPerSecond / 1_048_576:F2} MB/s";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:F2} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }
}

public class InterfaceControlItem : INotifyPropertyChanged
{
    private static readonly Brush AccentBrush = (Brush)Application.Current.FindResource("AccentPrimaryBrush");
    private static readonly Brush SuccessBrush = (Brush)Application.Current.FindResource("SuccessBrush");
    private static readonly Brush DangerBrush = (Brush)Application.Current.FindResource("DangerBrush");
    private static readonly Brush BgTertiary = (Brush)Application.Current.FindResource("BgTertiaryBrush");

    public string InterfaceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string InterfaceType { get; set; } = "";
    public bool IsWiFi { get; set; }

    public Brush IconBrush { get; set; } = Brushes.Gray;
    public string IconPath { get; set; } = "";

    // Use cached data for chart persistence across navigation
    private ObservableCollection<double> _downloadHistory = null!;
    private ObservableCollection<double> _uploadHistory = null!;

    private string _currentDownloadSpeed = "0 B/s";
    public string CurrentDownloadSpeed
    {
        get => _currentDownloadSpeed;
        set { _currentDownloadSpeed = value; OnPropertyChanged(nameof(CurrentDownloadSpeed)); }
    }

    private string _currentUploadSpeed = "0 B/s";
    public string CurrentUploadSpeed
    {
        get => _currentUploadSpeed;
        set { _currentUploadSpeed = value; OnPropertyChanged(nameof(CurrentUploadSpeed)); }
    }

    private double _downloadSliderValue;
    public double DownloadSliderValue
    {
        get => _downloadSliderValue;
        set
        {
            if (Math.Abs(_downloadSliderValue - value) > 0.01)
            {
                _downloadSliderValue = value;
                OnPropertyChanged(nameof(DownloadSliderValue));
            }
        }
    }

    private double _uploadSliderValue;
    public double UploadSliderValue
    {
        get => _uploadSliderValue;
        set
        {
            if (Math.Abs(_uploadSliderValue - value) > 0.01)
            {
                _uploadSliderValue = value;
                OnPropertyChanged(nameof(UploadSliderValue));
            }
        }
    }

    private string _downloadLimitText = "Unlimited";
    public string DownloadLimitText
    {
        get => _downloadLimitText;
        set { _downloadLimitText = value; OnPropertyChanged(nameof(DownloadLimitText)); }
    }

    private string _uploadLimitText = "Unlimited";
    public string UploadLimitText
    {
        get => _uploadLimitText;
        set { _uploadLimitText = value; OnPropertyChanged(nameof(UploadLimitText)); }
    }

    public Brush DownloadLimitBackground => _downloadLimitText == "Blocked" ? DangerBrush : BgTertiary;
    public Brush DownloadLimitForeground => _downloadLimitText == "Blocked" ? Brushes.White : AccentBrush;
    public Brush UploadLimitBackground => _uploadLimitText == "Blocked" ? DangerBrush : BgTertiary;
    public Brush UploadLimitForeground => _uploadLimitText == "Blocked" ? Brushes.White : SuccessBrush;

    public ISeries[]? ChartSeries { get; private set; }
    public Axis[]? XAxes { get; private set; }
    public Axis[]? YAxes { get; private set; }

    public void UpdateIcon()
    {
        if (IsWiFi)
        {
            IconBrush = (Brush)Application.Current.FindResource("AccentGradientBrush");
            IconPath = "M12,21L15.6,16.2C14.6,15.45 13.35,15 12,15C10.65,15 9.4,15.45 8.4,16.2L12,21M12,3C7.95,3 4.21,4.34 1.2,6.6L3,9C5.5,7.12 8.62,6 12,6C15.38,6 18.5,7.12 21,9L22.8,6.6C19.79,4.34 16.05,3 12,3M12,9C9.3,9 6.81,9.89 4.8,11.4L6.6,13.8C8.1,12.67 9.97,12 12,12C14.03,12 15.9,12.67 17.4,13.8L19.2,11.4C17.19,9.89 14.7,9 12,9Z";
        }
        else
        {
            IconBrush = (Brush)Application.Current.FindResource("SuccessGradientBrush");
            IconPath = "M11,3V7H13V3H11M8,4V11H10V4H8M14,4V11H16V4H14M4,7V10H7V7H4M4,11V21H7V11H4M17,7V10H20V7H17M17,11V21H20V11H17M8,12V21H10V12H8M11,8V21H13V8H11M14,12V21H16V12H14Z";
        }
    }

    public void InitializeChart(int maxDataPoints)
    {
        // Get cached chart data for this interface (persists across page navigation)
        var cachedData = Helpers.ChartDataCache.Instance.GetInterfaceChartData(InterfaceId, maxDataPoints);
        _downloadHistory = cachedData.download;
        _uploadHistory = cachedData.upload;

        ChartSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = _downloadHistory,
                Name = "Download",
                Stroke = new SolidColorPaint(SKColor.Parse("#4dc9ff")) { StrokeThickness = 2 },
                Fill = new SolidColorPaint(SKColor.Parse("#1a4dc9ff")),
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0.65,
                AnimationsSpeed = TimeSpan.Zero
            },
            new LineSeries<double>
            {
                Values = _uploadHistory,
                Name = "Upload",
                Stroke = new SolidColorPaint(SKColor.Parse("#00d4aa")) { StrokeThickness = 2 },
                Fill = new SolidColorPaint(SKColor.Parse("#1a00d4aa")),
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0.65,
                AnimationsSpeed = TimeSpan.Zero
            }
        };

        XAxes = new Axis[]
        {
            new Axis
            {
                ShowSeparatorLines = false,
                IsVisible = false,
                AnimationsSpeed = TimeSpan.Zero
            }
        };

        YAxes = new Axis[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#6b8eab")),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#1a4dc9ff")),
                Labeler = value => FormatSpeedShort(value),
                MinLimit = 0,
                AnimationsSpeed = TimeSpan.Zero
            }
        };
    }

    public void UpdateChartData(double download, double upload)
    {
        _downloadHistory.RemoveAt(0);
        _downloadHistory.Add(download);
        _uploadHistory.RemoveAt(0);
        _uploadHistory.Add(upload);
    }

    public void UpdateLimitText(int downloadKbps, int uploadKbps)
    {
        UpdateDownloadLimitText(downloadKbps);
        UpdateUploadLimitText(uploadKbps);
    }

    public void UpdateDownloadLimitText(int downloadKbps)
    {
        DownloadLimitText = FormatLimitText(downloadKbps);
        OnPropertyChanged(nameof(DownloadLimitBackground));
        OnPropertyChanged(nameof(DownloadLimitForeground));
    }

    public void UpdateUploadLimitText(int uploadKbps)
    {
        UploadLimitText = FormatLimitText(uploadKbps);
        OnPropertyChanged(nameof(UploadLimitBackground));
        OnPropertyChanged(nameof(UploadLimitForeground));
    }

    private static string FormatLimitText(int kbps)
    {
        if (kbps < 0) return "Unlimited";
        if (kbps == 0) return "Blocked";
        if (kbps >= 1024)
            return $"{kbps / 1024.0:F1} Mbps".Replace(".0 ", " ");
        return $"{kbps} Kbps";
    }

    private static string FormatSpeedShort(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_048_576)
            return $"{bytesPerSecond / 1_048_576:F0}M";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:F0}K";
        return $"{bytesPerSecond:F0}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

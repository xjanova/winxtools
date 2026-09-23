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
using NetX.App.Helpers;
using NetX.Core.Network;

namespace NetX.App.Views;

public partial class BandwidthControlView : Page
{
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _applyDebounce;
    private readonly NetworkMonitor _networkMonitor;
    private readonly BandwidthLimiter _limiter = BandwidthLimiter.Instance;
    private readonly ObservableCollection<InterfaceControlItem> _interfaces = new();
    private readonly ObservableCollection<AppRuleItem> _appRules = new();
    private const int MaxDataPoints = 60;
    private bool _suppressApply;
    private string? _actionMessage;
    private DateTime _actionMessageUntil;

    // Whole-PC presets in kilobits/second, decimal like internet plans
    // (100 Mbps plan = 100 000 Kbps). Index 0 = blocked, last = unlimited.
    internal static readonly int[] SpeedPresetsKbps =
    {
        0,
        64, 128, 256, 384, 512, 768,
        1_000, 1_500, 2_000, 3_000, 4_000, 5_000, 6_000, 8_000,
        10_000, 15_000, 20_000, 30_000, 50_000, 75_000,
        100_000, 150_000, 200_000, 300_000, 500_000, 750_000,
        1_000_000, 1_500_000, 2_000_000,
        -1
    };

    public BandwidthControlView()
    {
        InitializeComponent();

        _networkMonitor = NetworkMonitor.Instance;
        InterfacesList.ItemsSource = _interfaces;
        AppRulesList.ItemsSource = _appRules;

        _updateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _updateTimer.Tick += UpdateTimer_Tick;

        _applyDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _applyDebounce.Tick += async (s, e) =>
        {
            _applyDebounce.Stop();
            await ApplyWholePcLimitAsync();
        };

        Loaded += (s, e) =>
        {
            if (_interfaces.Count == 0) InitializeInterfaces();
            _limiter.RulesChanged += Limiter_RulesChanged;
            RefreshAppRules();
            UpdateEngineStatus();
            _updateTimer.Start();
        };

        Unloaded += (s, e) =>
        {
            // Pages are recreated on every navigation; unsubscribe so the
            // singleton limiter doesn't keep old pages alive.
            _limiter.RulesChanged -= Limiter_RulesChanged;
            _updateTimer.Stop();
            if (_applyDebounce.IsEnabled)
            {
                _applyDebounce.Stop();
                _ = ApplyWholePcLimitAsync();
            }
        };
    }

    private void Limiter_RulesChanged() =>
        Dispatcher.BeginInvoke(() =>
        {
            RefreshAppRules();
            UpdateEngineStatus();
        });

    #region Whole-PC limit

    private void InitializeInterfaces()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                          && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                          && IsPhysicalAdapter(ni))
                .Select(ni =>
                {
                    long total = 0;
                    try { var s = ni.GetIPStatistics(); total = s.BytesReceived + s.BytesSent; } catch { }
                    return (ni, total);
                })
                .OrderByDescending(x => x.total)
                .ToList();

            // The limit is for the whole PC; the card shows the busiest adapter's
            // live traffic, or all adapters when no physical one is up.
            var ni = candidates.FirstOrDefault().ni;
            var item = new InterfaceControlItem
            {
                InterfaceId = ni?.Id,
                Name = ni?.Name ?? "All network adapters",
                InterfaceType = ni != null ? GetInterfaceTypeName(ni.NetworkInterfaceType) : "",
                IsWiFi = ni?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                SliderMaximum = SpeedPresetsKbps.Length - 1
            };
            item.UpdateIcon();
            item.InitializeChart(MaxDataPoints);

            var rule = _limiter.GlobalRule;
            _suppressApply = true;
            try
            {
                int dlKbps = BpsToKbps(rule?.DownloadBps ?? RateLimit.Unlimited);
                int ulKbps = BpsToKbps(rule?.UploadBps ?? RateLimit.Unlimited);
                item.DownloadSliderValue = KbpsToSlider(dlKbps);
                item.UploadSliderValue = KbpsToSlider(ulKbps);
                item.UpdateLimitText(SliderToKbps(item.DownloadSliderValue), SliderToKbps(item.UploadSliderValue));
            }
            finally
            {
                _suppressApply = false;
            }

            item.PropertyChanged += InterfaceItem_PropertyChanged;
            _interfaces.Add(item);

            if (ni != null) _networkMonitor.SelectInterface(ni.Id);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error initializing interfaces: {ex.Message}");
        }
    }

    private static bool IsPhysicalAdapter(NetworkInterface ni)
    {
        var desc = ni.Description.ToLowerInvariant();
        var name = ni.Name.ToLowerInvariant();

        if (desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("virtualbox") ||
            desc.Contains("hyper-v") || desc.Contains("vpn") || desc.Contains("tap-") ||
            desc.Contains("tunnel") || desc.Contains("pseudo") || desc.Contains("miniport") ||
            desc.Contains("wan") || desc.Contains("teredo") || desc.Contains("isatap") ||
            desc.Contains("6to4") || desc.Contains("bluetooth") ||
            name.Contains("vethernet") || name.Contains("docker") || name.Contains("wsl"))
        {
            return false;
        }

        return ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
               ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
               ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet;
    }

    private static string GetInterfaceTypeName(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet => "Ethernet",
        NetworkInterfaceType.GigabitEthernet => "Gigabit Ethernet",
        _ => type.ToString()
    };

    private void InterfaceItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not InterfaceControlItem item) return;
        if (e.PropertyName != nameof(InterfaceControlItem.DownloadSliderValue) &&
            e.PropertyName != nameof(InterfaceControlItem.UploadSliderValue))
            return;

        item.UpdateLimitText(SliderToKbps(item.DownloadSliderValue), SliderToKbps(item.UploadSliderValue));
        if (_suppressApply) return;

        _applyDebounce.Stop();
        _applyDebounce.Start();
    }

    private async Task ApplyWholePcLimitAsync()
    {
        var item = _interfaces.FirstOrDefault();
        if (item == null) return;

        int dlKbps = SliderToKbps(item.DownloadSliderValue);
        int ulKbps = SliderToKbps(item.UploadSliderValue);

        var result = await _limiter.SetGlobalLimitAsync(KbpsToBps(dlKbps), KbpsToBps(ulKbps));
        if (!result.Success)
        {
            _actionMessage = result.Message;
            _actionMessageUntil = DateTime.Now.AddSeconds(15);
        }
        UpdateEngineStatus();
    }

    private static int SliderToKbps(double sliderValue)
    {
        int index = Math.Clamp((int)Math.Round(sliderValue), 0, SpeedPresetsKbps.Length - 1);
        return SpeedPresetsKbps[index];
    }

    private static int KbpsToSlider(int kbps)
    {
        if (kbps < 0) return SpeedPresetsKbps.Length - 1;
        if (kbps == 0) return 0;

        int best = 1;
        for (int i = 1; i < SpeedPresetsKbps.Length - 1; i++)
        {
            if (Math.Abs(SpeedPresetsKbps[i] - kbps) < Math.Abs(SpeedPresetsKbps[best] - kbps))
                best = i;
        }
        return best;
    }

    // 1 Kbps = 1000 bits/s = 125 bytes/s.
    private static long KbpsToBps(int kbps) => kbps < 0 ? RateLimit.Unlimited : kbps == 0 ? RateLimit.Blocked : kbps * 125L;

    private static int BpsToKbps(long bps) => bps < 0 ? -1 : bps == 0 ? 0 : (int)Math.Min(int.MaxValue, bps / 125);

    #endregion

    #region Status + app rules

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var item = _interfaces.FirstOrDefault();
            if (item != null)
            {
                double down, up;
                if (item.InterfaceId != null && _networkMonitor.GetInterfaceBandwidth().TryGetValue(item.InterfaceId, out var bw))
                {
                    down = bw.download;
                    up = bw.upload;
                }
                else
                {
                    var stats = _networkMonitor.GetCurrentStats();
                    down = stats.TotalDownloadSpeed;
                    up = stats.TotalUploadSpeed;
                }
                item.CurrentDownloadSpeed = SpeedFormat.Speed(down);
                item.CurrentUploadSpeed = SpeedFormat.Speed(up);
                item.UpdateChartData(down, up);
            }

            RefreshAppRuleSpeeds();
            UpdateEngineStatus();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Bandwidth page update failed: {ex.Message}");
        }
    }

    private void UpdateEngineStatus()
    {
        try
        {
            string text;
            Brush dot;
            switch (_limiter.Status)
            {
                case LimiterStatus.Active:
                    text = Loc.T("BW_Status_Active", "Limits active");
                    dot = (Brush)FindResource("SuccessBrush");
                    break;
                case LimiterStatus.DriverUnavailable:
                    text = Loc.T("BW_Status_NoDriver", "Driver unavailable — limits saved but NOT active");
                    dot = (Brush)FindResource("DangerBrush");
                    break;
                case LimiterStatus.Error:
                    text = Loc.F("BW_Status_Error", "Limits not active: {0}", _limiter.EngineError ?? "?");
                    dot = (Brush)FindResource("DangerBrush");
                    break;
                default:
                    text = Loc.T("BW_Status_Idle", "Ready — no limits set");
                    dot = (Brush)FindResource("TextTertiaryBrush");
                    break;
            }

            if (_actionMessage != null && DateTime.Now < _actionMessageUntil)
            {
                text = _actionMessage;
                dot = (Brush)FindResource("DangerBrush");
            }

            EngineStatusText.Text = text;
            EngineStatusDot.Fill = dot;
        }
        catch { }
    }

    private void RefreshAppRules()
    {
        var rules = _limiter.GetAppRules();
        _appRules.Clear();
        foreach (var rule in rules)
        {
            _appRules.Add(new AppRuleItem
            {
                Name = rule.ProcessName,
                IsBlocked = rule.Blocked,
                LimitText = rule.HasSpeedLimit
                    ? $"↓{SpeedFormat.Limit(rule.DownloadBps)}  ↑{SpeedFormat.Limit(rule.UploadBps)}"
                    : ""
            });
        }
        AppRulesEmptyText.Visibility = _appRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetAllButton.IsEnabled = _appRules.Count > 0 || _limiter.GlobalRule != null;
        RefreshAppRuleSpeeds();
    }

    private void RefreshAppRuleSpeeds()
    {
        var engine = PacketEngine.Instance;
        foreach (var item in _appRules)
        {
            var (down, up) = engine.GetAppSpeed(item.Name);
            item.NowText = Loc.F("BW_NowSpeed", "now ↓{0}  ↑{1}", SpeedFormat.Speed(down), SpeedFormat.Speed(up));
        }
    }

    private async void UnblockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } button) return;
        if (MessageBox.Show(Loc.F("BW_UnblockConfirm", "Unblock {0}?", name), "WinXTools",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        button.IsEnabled = false;
        var result = await _limiter.UnblockAppAsync(name);
        if (!result.Success)
        {
            MessageBox.Show(result.Message, "WinXTools", MessageBoxButton.OK, MessageBoxImage.Warning);
            button.IsEnabled = true;
        }
    }

    private async void RemoveAppRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } button) return;
        if (MessageBox.Show($"{Loc.T("BW_Remove", "Remove")}: {name}?", "WinXTools",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        button.IsEnabled = false;
        await _limiter.RemoveAppRuleAsync(name);
    }

    private async void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Loc.T("BW_ResetAllConfirm", "Remove every limit and block?"), "WinXTools",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        ResetAllButton.IsEnabled = false;
        _applyDebounce.Stop();
        await _limiter.ResetAllAsync();

        var item = _interfaces.FirstOrDefault();
        if (item != null)
        {
            _suppressApply = true;
            try
            {
                item.DownloadSliderValue = SpeedPresetsKbps.Length - 1;
                item.UploadSliderValue = SpeedPresetsKbps.Length - 1;
                item.UpdateLimitText(-1, -1);
            }
            finally
            {
                _suppressApply = false;
            }
        }
    }

    #endregion
}

public class AppRuleItem : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public bool IsBlocked { get; set; }
    public string LimitText { get; set; } = "";
    public Visibility BlockedVisibility => IsBlocked ? Visibility.Visible : Visibility.Collapsed;

    private string _nowText = "";
    public string NowText
    {
        get => _nowText;
        set { _nowText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NowText))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class InterfaceControlItem : INotifyPropertyChanged
{
    private static readonly Brush AccentBrush = (Brush)Application.Current.FindResource("AccentPrimaryBrush");
    private static readonly Brush SuccessBrush = (Brush)Application.Current.FindResource("SuccessBrush");
    private static readonly Brush DangerBrush = (Brush)Application.Current.FindResource("DangerBrush");
    private static readonly Brush BgTertiary = (Brush)Application.Current.FindResource("BgTertiaryBrush");

    public string? InterfaceId { get; set; }
    public string Name { get; set; } = "";
    public string InterfaceType { get; set; } = "";
    public bool IsWiFi { get; set; }
    public int SliderMaximum { get; set; } = 30;

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

    private string _downloadLimitText = "";
    public string DownloadLimitText
    {
        get => _downloadLimitText;
        set { _downloadLimitText = value; OnPropertyChanged(nameof(DownloadLimitText)); }
    }

    private string _uploadLimitText = "";
    public string UploadLimitText
    {
        get => _uploadLimitText;
        set { _uploadLimitText = value; OnPropertyChanged(nameof(UploadLimitText)); }
    }

    private bool _downloadBlocked;
    private bool _uploadBlocked;
    public Brush DownloadLimitBackground => _downloadBlocked ? DangerBrush : BgTertiary;
    public Brush DownloadLimitForeground => _downloadBlocked ? Brushes.White : AccentBrush;
    public Brush UploadLimitBackground => _uploadBlocked ? DangerBrush : BgTertiary;
    public Brush UploadLimitForeground => _uploadBlocked ? Brushes.White : SuccessBrush;

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
        var cachedData = Helpers.ChartDataCache.Instance.GetInterfaceChartData(InterfaceId ?? "all", maxDataPoints);
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
        DownloadLimitText = SpeedFormat.Kbps(downloadKbps);
        UploadLimitText = SpeedFormat.Kbps(uploadKbps);
        _downloadBlocked = downloadKbps == 0;
        _uploadBlocked = uploadKbps == 0;
        OnPropertyChanged(nameof(DownloadLimitBackground));
        OnPropertyChanged(nameof(DownloadLimitForeground));
        OnPropertyChanged(nameof(UploadLimitBackground));
        OnPropertyChanged(nameof(UploadLimitForeground));
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

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetX.Core.Network;

namespace NetX.App.Views;

public partial class PacketMonitorView : Page
{
    private readonly DispatcherTimer _updateTimer;
    private readonly ObservableCollection<PacketDisplayItem> _packets = new();
    private readonly List<PacketDisplayItem> _allPackets = new();
    private readonly object _packetLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _pidNames = new();
    private bool _isCapturing = false;
    private bool _startedEngine = false;
    private int _packetNumber = 0;
    private int _inboundCount = 0;
    private int _outboundCount = 0;
    private int _lastPacketCount = 0;
    private DateTime _lastRateCheck = DateTime.Now;
    private const int MaxPackets = 10000;

    // Get brushes for binding
    private readonly Brush _inboundBrush;
    private readonly Brush _outboundBrush;
    private readonly Brush _tcpBrush;
    private readonly Brush _udpBrush;
    private readonly Brush _icmpBrush;
    private readonly Geometry _inboundIcon;
    private readonly Geometry _outboundIcon;

    public PacketMonitorView()
    {
        InitializeComponent();

        // Get resources
        _inboundBrush = (Brush)FindResource("AccentPrimaryBrush");
        _outboundBrush = (Brush)FindResource("AccentSecondaryBrush");
        _tcpBrush = (Brush)FindResource("AccentPrimaryBrush");
        _udpBrush = (Brush)FindResource("WarningBrush");
        _icmpBrush = (Brush)FindResource("DangerBrush");
        _inboundIcon = (Geometry)FindResource("InboundIcon");
        _outboundIcon = (Geometry)FindResource("OutboundIcon");

        PacketList.ItemsSource = _packets;

        // Load process list
        LoadProcessList();

        // Setup update timer
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _updateTimer.Tick += UpdateTimer_Tick;

        Unloaded += (s, e) =>
        {
            StopCapture();
            _updateTimer.Stop();
        };
    }

    private void LoadProcessList()
    {
        try
        {
            ProcessFilter.Items.Clear();
            ProcessFilter.Items.Add(new ComboBoxItem { Content = "All Processes", IsSelected = true });

            var processes = Process.GetProcesses()
                .Where(p => !string.IsNullOrEmpty(p.ProcessName))
                .OrderBy(p => p.ProcessName)
                .Select(p => new { p.ProcessName, p.Id })
                .Distinct()
                .Take(100);

            foreach (var proc in processes)
            {
                ProcessFilter.Items.Add(new ComboBoxItem
                {
                    Content = $"{proc.ProcessName} ({proc.Id})",
                    Tag = proc.Id
                });
            }
        }
        catch { }
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_isCapturing)
        {
            StopCapture();
        }
        else
        {
            StartCapture();
        }
    }

    private void StartCapture()
    {
        var engine = PacketEngine.Instance;

        // Honest failure instead of fabricated packets: no driver = no capture.
        if (!engine.IsDriverLoaded)
        {
            StatusText.Text = "WinDivert driver unavailable — run WinXTools as Administrator to capture live packets.";
            CaptureIndicator.Fill = (Brush)FindResource("DangerBrush");
            return;
        }

        _isCapturing = true;
        StartStopText.Text = "Stop Capture";
        StartStopIcon.Data = (Geometry)FindResource("StopIcon");
        CaptureIndicator.Fill = (Brush)FindResource("SuccessBrush");
        StatusText.Text = "Capturing live packets (WinDivert)...";

        _updateTimer.Start();

        // Real capture: start the kernel engine if it isn't already running for
        // bandwidth limiting, and receive every packet via its event.
        if (!engine.IsRunning)
        {
            engine.Start();
            _startedEngine = true;
        }
        engine.OnPacketCaptured += OnEnginePacket;
    }

    private void StopCapture()
    {
        _isCapturing = false;
        StartStopText.Text = "Start Capture";
        StartStopIcon.Data = (Geometry)FindResource("StartIcon");
        CaptureIndicator.Fill = (Brush)FindResource("TextTertiaryBrush");
        StatusText.Text = $"Capture stopped. {_allPackets.Count} packets captured.";

        _updateTimer.Stop();

        var engine = PacketEngine.Instance;
        engine.OnPacketCaptured -= OnEnginePacket;

        // Only stop the engine if WE started it and no bandwidth limit needs it.
        if (_startedEngine && !engine.HasThrottleRules)
        {
            engine.Stop();
        }
        _startedEngine = false;
    }

    /// <summary>
    /// Real packet handler — called on WinDivert capture threads for every
    /// captured packet. All fields come straight from the kernel; nothing is
    /// fabricated. Runs off the UI thread, so it only mutates locked state; the
    /// DispatcherTimer renders it on the UI thread.
    /// </summary>
    private void OnEnginePacket(PacketInfo info)
    {
        if (!_isCapturing) return;

        bool isInbound = !info.IsOutbound;
        string protocol = info.Protocol switch { 6 => "TCP", 17 => "UDP", 1 => "ICMP", _ => "OTHER" };

        // Source pairs with the actual packet source address/port.
        string srcPort = (info.IsOutbound ? info.LocalPort : info.RemotePort).ToString();
        string dstPort = (info.IsOutbound ? info.RemotePort : info.LocalPort).ToString();

        var packet = new PacketDisplayItem
        {
            Number = 0, // assigned under lock below
            Time = info.Timestamp,
            TimeStr = info.Timestamp.ToString("HH:mm:ss.fff"),
            IsInbound = isInbound,
            DirectionIcon = isInbound ? _inboundIcon : _outboundIcon,
            DirectionColor = isInbound ? _inboundBrush : _outboundBrush,
            Protocol = protocol,
            ProtocolColor = protocol == "TCP" ? _tcpBrush : (protocol == "UDP" ? _udpBrush : _icmpBrush),
            SourceIP = info.SourceAddress ?? "-",
            SourcePort = srcPort,
            DestIP = info.DestAddress ?? "-",
            DestPort = dstPort,
            Size = info.Length,
            SizeStr = $"{info.Length} B",
            ProcessName = ResolveProcessName(info.ProcessId),
            ProcessId = info.ProcessId,
            State = info.TcpFlags,
            TcpFlags = info.TcpFlags
        };

        lock (_packetLock)
        {
            packet.Number = ++_packetNumber;
            _allPackets.Add(packet);

            if (isInbound) _inboundCount++;
            else _outboundCount++;

            while (_allPackets.Count > MaxPackets)
                _allPackets.RemoveAt(0);
        }
    }

    private string ResolveProcessName(int pid)
    {
        if (pid == 0) return "System/Unknown";
        return _pidNames.GetOrAdd(pid, id =>
        {
            try { using var p = Process.GetProcessById(id); return p.ProcessName; }
            catch { return $"PID {id}"; }
        });
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        lock (_packetLock)
        {
            // Calculate packet rate
            var now = DateTime.Now;
            var elapsed = (now - _lastRateCheck).TotalSeconds;
            if (elapsed >= 1)
            {
                var rate = (int)((_allPackets.Count - _lastPacketCount) / elapsed);
                PacketRateText.Text = rate.ToString();
                _lastPacketCount = _allPackets.Count;
                _lastRateCheck = now;
            }

            // Update statistics
            TotalPacketsText.Text = _allPackets.Count.ToString("N0");
            InboundPacketsText.Text = _inboundCount.ToString("N0");
            OutboundPacketsText.Text = _outboundCount.ToString("N0");
            BufferStatus.Text = $"Buffer: {_allPackets.Count:N0} / {MaxPackets:N0} packets";

            // Apply filters and update display
            ApplyFilters();
        }
    }

    private void ApplyFilters()
    {
        var showTCP = FilterTCP.IsChecked == true;
        var showUDP = FilterUDP.IsChecked == true;
        var showICMP = FilterICMP.IsChecked == true;
        var showInbound = FilterInbound.IsChecked == true;
        var showOutbound = FilterOutbound.IsChecked == true;
        var filterText = FilterExpression.Text?.ToLower() ?? "";

        int? processIdFilter = null;
        if (ProcessFilter.SelectedItem is ComboBoxItem item && item.Tag is int pid)
        {
            processIdFilter = pid;
        }

        var filtered = _allPackets.Where(p =>
        {
            // Protocol filter
            if (p.Protocol == "TCP" && !showTCP) return false;
            if (p.Protocol == "UDP" && !showUDP) return false;
            if (p.Protocol == "ICMP" && !showICMP) return false;

            // Direction filter
            if (p.IsInbound && !showInbound) return false;
            if (!p.IsInbound && !showOutbound) return false;

            // Process filter
            if (processIdFilter.HasValue && p.ProcessId != processIdFilter.Value) return false;

            // Text filter (simple implementation)
            if (!string.IsNullOrEmpty(filterText))
            {
                if (!p.SourceIP.ToLower().Contains(filterText) &&
                    !p.DestIP.ToLower().Contains(filterText) &&
                    !p.SourcePort.Contains(filterText) &&
                    !p.DestPort.Contains(filterText) &&
                    !(p.ProcessName?.ToLower().Contains(filterText) ?? false))
                {
                    return false;
                }
            }

            return true;
        }).TakeLast(500).ToList(); // Show last 500 filtered packets

        // Update display
        _packets.Clear();
        foreach (var packet in filtered)
        {
            _packets.Add(packet);
        }

        // Auto-scroll to bottom
        if (_packets.Count > 0)
        {
            PacketList.ScrollIntoView(_packets[^1]);
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_updateTimer == null) return;
        ApplyFilters();
    }

    private void FilterExpression_Changed(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void ProcessFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        lock (_packetLock)
        {
            _allPackets.Clear();
            _packets.Clear();
            _packetNumber = 0;
            _inboundCount = 0;
            _outboundCount = 0;
            _lastPacketCount = 0;
        }

        TotalPacketsText.Text = "0";
        InboundPacketsText.Text = "0";
        OutboundPacketsText.Text = "0";
        PacketRateText.Text = "0";
        BufferStatus.Text = "Buffer: 0 / 10,000 packets";

        NoPacketSelected.Visibility = Visibility.Visible;
        DetailSection.Visibility = Visibility.Collapsed;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv|Text Files (*.txt)|*.txt",
            DefaultExt = ".csv",
            FileName = $"packets_{DateTime.Now:yyyyMMdd_HHmmss}"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                lock (_packetLock)
                {
                    using var writer = new System.IO.StreamWriter(dialog.FileName);
                    writer.WriteLine("Number,Time,Direction,Protocol,SourceIP,SourcePort,DestIP,DestPort,Size,Process");

                    foreach (var packet in _allPackets)
                    {
                        writer.WriteLine($"{packet.Number},{packet.TimeStr},{(packet.IsInbound ? "IN" : "OUT")}," +
                            $"{packet.Protocol},{packet.SourceIP},{packet.SourcePort}," +
                            $"{packet.DestIP},{packet.DestPort},{packet.Size},{packet.ProcessName}");
                    }
                }

                StatusText.Text = $"Exported {_allPackets.Count} packets to {dialog.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void PacketList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PacketList.SelectedItem is PacketDisplayItem packet)
        {
            ShowPacketDetails(packet);
        }
    }

    private void ShowPacketDetails(PacketDisplayItem packet)
    {
        NoPacketSelected.Visibility = Visibility.Collapsed;
        DetailSection.Visibility = Visibility.Visible;

        DetailTime.Text = packet.Time.ToString("yyyy-MM-dd HH:mm:ss.fff");
        DetailProtocol.Text = packet.Protocol;
        DetailDirection.Text = packet.IsInbound ? "Inbound (Received)" : "Outbound (Sent)";
        DetailSize.Text = $"{packet.Size} bytes";
        DetailProcess.Text = $"{packet.ProcessName ?? "Unknown"} (PID: {packet.ProcessId})";

        DetailSource.Text = $"{packet.SourceIP}:{packet.SourcePort}";
        DetailDest.Text = $"{packet.DestIP}:{packet.DestPort}";

        // Show TCP flags if applicable
        FlagsHeader.Visibility = packet.Protocol == "TCP" ? Visibility.Visible : Visibility.Collapsed;
        FlagsPanel.Visibility = packet.Protocol == "TCP" ? Visibility.Visible : Visibility.Collapsed;

        if (packet.Protocol == "TCP")
        {
            FlagsPanel.Children.Clear();
            var allFlags = new[] { "SYN", "ACK", "FIN", "RST", "PSH", "URG" };
            var setFlags = (packet.TcpFlags ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var flag in allFlags)
            {
                var isSet = setFlags.Contains(flag); // real flags from the captured packet
                var border = new Border
                {
                    Background = isSet ? FindResource("AccentPrimaryBrush") as Brush : FindResource("BgTertiaryBrush") as Brush,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 6, 6)
                };
                border.Child = new TextBlock
                {
                    Text = flag,
                    FontSize = 11,
                    Foreground = isSet ? Brushes.White : FindResource("TextTertiaryBrush") as Brush
                };
                FlagsPanel.Children.Add(border);
            }
        }

        // Headers are captured live; raw payload bytes are not buffered (kept
        // lightweight for line-rate capture), so show the real header summary.
        HexView.Text =
            $"{packet.Protocol}  {packet.SourceIP}:{packet.SourcePort} → {packet.DestIP}:{packet.DestPort}\n" +
            $"Direction: {(packet.IsInbound ? "Inbound" : "Outbound")}   Size: {packet.Size} bytes\n" +
            (packet.Protocol == "TCP"
                ? $"TCP flags: {(string.IsNullOrEmpty(packet.TcpFlags) ? "(none)" : packet.TcpFlags)}\n"
                : "") +
            "\n(Live headers captured via WinDivert. Raw payload buffering is disabled for performance.)";
    }

    private void BlockIP_Click(object sender, RoutedEventArgs e)
    {
        if (PacketList.SelectedItem is PacketDisplayItem packet)
        {
            var ip = packet.IsInbound ? packet.SourceIP : packet.DestIP;
            var result = MessageBox.Show(
                $"Block IP address {ip}?",
                "Block IP",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                FirewallManager.Instance.BlockIP(ip);
                StatusText.Text = $"Blocked IP: {ip}";
            }
        }
    }

    private void CopyPacket_Click(object sender, RoutedEventArgs e)
    {
        if (PacketList.SelectedItem is PacketDisplayItem packet)
        {
            var details = $"""
                Packet #{packet.Number}
                Time: {packet.Time:yyyy-MM-dd HH:mm:ss.fff}
                Direction: {(packet.IsInbound ? "Inbound" : "Outbound")}
                Protocol: {packet.Protocol}
                Source: {packet.SourceIP}:{packet.SourcePort}
                Destination: {packet.DestIP}:{packet.DestPort}
                Size: {packet.Size} bytes
                Process: {packet.ProcessName} (PID: {packet.ProcessId})
                TCP Flags: {(string.IsNullOrEmpty(packet.TcpFlags) ? "-" : packet.TcpFlags)}
                """;

            Clipboard.SetText(details);
            StatusText.Text = "Packet details copied to clipboard";
        }
    }
}

public class PacketDisplayItem
{
    public int Number { get; set; }
    public DateTime Time { get; set; }
    public string TimeStr { get; set; } = "";
    public bool IsInbound { get; set; }
    public Geometry? DirectionIcon { get; set; }
    public Brush? DirectionColor { get; set; }
    public string Protocol { get; set; } = "";
    public Brush? ProtocolColor { get; set; }
    public string SourceIP { get; set; } = "";
    public string SourcePort { get; set; } = "";
    public string DestIP { get; set; } = "";
    public string DestPort { get; set; } = "";
    public int Size { get; set; }
    public string SizeStr { get; set; } = "";
    public string? ProcessName { get; set; }
    public int ProcessId { get; set; }
    public string? State { get; set; }
    public string? Payload { get; set; }
    public string? TcpFlags { get; set; }
}

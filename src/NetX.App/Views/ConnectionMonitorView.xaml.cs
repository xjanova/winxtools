using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetX.Core.Network;

namespace NetX.App.Views;

public partial class ConnectionMonitorView : Page
{
    private readonly DispatcherTimer _updateTimer;
    private readonly ConnectionMonitor _connectionMonitor;
    private readonly FirewallManager _firewallManager;
    private List<ConnectionDisplayItem> _allConnections = new();

    public ConnectionMonitorView()
    {
        InitializeComponent();

        _connectionMonitor = ConnectionMonitor.Instance;
        _firewallManager = FirewallManager.Instance;

        // Setup timer
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();

        UpdateConnectionList();

        Unloaded += (s, e) => _updateTimer.Stop();
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (AutoRefreshBtn.IsChecked == true)
        {
            UpdateConnectionList();
        }
    }

    private void UpdateConnectionList()
    {
        try
        {
            var connections = _connectionMonitor.GetActiveConnections();
            var blockedIPs = _firewallManager.GetNetXRules()
                .Where(r => r.Action == FirewallAction.Block)
                .Select(r => r.IPAddress)
                .ToHashSet();

            _allConnections = connections
                .Where(c => c.RemoteAddress != "*" && c.RemoteAddress != "0.0.0.0" && c.RemoteAddress != "::")
                .OrderByDescending(c => c.Timestamp)
                .Select(c => new ConnectionDisplayItem
                {
                    Protocol = c.Protocol,
                    LocalAddress = c.LocalAddress,
                    LocalPort = c.LocalPort,
                    RemoteAddress = c.RemoteAddress,
                    RemotePort = c.RemotePort,
                    RemoteHostname = c.RemoteHostname,
                    State = c.State,
                    Direction = c.Direction,
                    IsBlocked = blockedIPs.Contains(c.RemoteAddress),
                    Timestamp = c.Timestamp
                })
                .ToList();

            ApplyFilters();

            // Update stats
            var incoming = _allConnections.Count(c => c.Direction == ConnectionDirection.Incoming);
            var outgoing = _allConnections.Count(c => c.Direction == ConnectionDirection.Outgoing);

            TotalConnectionsText.Text = _allConnections.Count.ToString();
            IncomingText.Text = incoming.ToString();
            OutgoingText.Text = outgoing.ToString();
            BlockedText.Text = blockedIPs.Count.ToString();

            StatusText.Text = $"Monitoring {_allConnections.Count} active connections • Last updated: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void ApplyFilters()
    {
        if (SearchBox == null || ConnectionsList == null) return;

        var searchText = SearchBox.Text?.ToLower() ?? "";
        var directionIndex = DirectionFilter?.SelectedIndex ?? 0;
        var protocolIndex = ProtocolFilter?.SelectedIndex ?? 0;

        var filtered = _allConnections.Where(c =>
        {
            // Search filter
            if (!string.IsNullOrEmpty(searchText))
            {
                var matchesSearch =
                    c.RemoteAddress.ToLower().Contains(searchText) ||
                    c.RemoteHostname.ToLower().Contains(searchText) ||
                    c.RemotePort.ToString().Contains(searchText) ||
                    c.LocalPort.ToString().Contains(searchText);

                if (!matchesSearch) return false;
            }

            // Direction filter
            if (directionIndex == 1 && c.Direction != ConnectionDirection.Incoming) return false;
            if (directionIndex == 2 && c.Direction != ConnectionDirection.Outgoing) return false;

            // Protocol filter
            if (protocolIndex == 1 && c.Protocol != "TCP") return false;
            if (protocolIndex == 2 && c.Protocol != "UDP") return false;

            return true;
        }).ToList();

        ConnectionsList.ItemsSource = filtered;
        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateConnectionList();
    }

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshBtn.IsChecked == true)
        {
            _updateTimer.Start();
        }
        else
        {
            _updateTimer.Stop();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void BlockConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string ipAddress)
        {
            var result = MessageBox.Show(
                $"Block all connections to/from {ipAddress}?\n\nThis will add a Windows Firewall rule to block this IP address.",
                "Block IP Address",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                if (_firewallManager.BlockIP(ipAddress))
                {
                    UpdateConnectionList();
                    MessageBox.Show($"Successfully blocked {ipAddress}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Failed to block {ipAddress}. Make sure you're running as Administrator.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private void UnblockConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string ipAddress)
        {
            if (_firewallManager.UnblockIP(ipAddress))
            {
                UpdateConnectionList();
            }
        }
    }
}

public class ConnectionDisplayItem
{
    public string Protocol { get; set; } = string.Empty;
    public string LocalAddress { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = string.Empty;
    public int RemotePort { get; set; }
    public string RemoteHostname { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public ConnectionDirection Direction { get; set; }
    public bool IsBlocked { get; set; }
    public DateTime Timestamp { get; set; }

    public string DisplayEndpoint => RemotePort > 0
        ? $"{RemoteAddress}:{RemotePort}"
        : RemoteAddress;

    public string LocalPortDisplay => $"Local: {LocalPort}";

    public bool HasHostname => !string.IsNullOrEmpty(RemoteHostname) && RemoteHostname != RemoteAddress;

    public bool CanBlock => !IsBlocked && !string.IsNullOrEmpty(RemoteAddress) &&
                            RemoteAddress != "127.0.0.1" && RemoteAddress != "::1";

    public Brush DirectionColor => Direction switch
    {
        ConnectionDirection.Incoming => new SolidColorBrush(Color.FromRgb(0x06, 0xb6, 0xd4)), // Cyan
        ConnectionDirection.Outgoing => new SolidColorBrush(Color.FromRgb(0x10, 0xb9, 0x81)), // Green
        _ => new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8b)) // Gray
    };

    public Brush StateColor => State switch
    {
        "Established" => new SolidColorBrush(Color.FromRgb(0x10, 0xb9, 0x81)), // Green
        "Listening" => new SolidColorBrush(Color.FromRgb(0x06, 0xb6, 0xd4)), // Cyan
        "TimeWait" or "CloseWait" => new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b)), // Yellow
        "Close" or "Closed" => new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)), // Red
        _ => new SolidColorBrush(Color.FromRgb(0x94, 0xa3, 0xb8)) // Gray
    };

    public Geometry DirectionIcon => Direction switch
    {
        ConnectionDirection.Incoming => Geometry.Parse("M12,4L12,20M12,20L6,14M12,20L18,14"),
        ConnectionDirection.Outgoing => Geometry.Parse("M12,20L12,4M12,4L6,10M12,4L18,10"),
        _ => Geometry.Parse("M4,12H20")
    };
}

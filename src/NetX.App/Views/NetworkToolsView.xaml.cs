using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetX.Core.Network;

namespace NetX.App.Views;

public partial class NetworkToolsView : Page
{
    private CancellationTokenSource? _cts;
    private string _currentTool = "Ping";
    private bool _isRunning;

    public NetworkToolsView()
    {
        InitializeComponent();

        // Stop any in-flight work when the user navigates away from the page.
        Unloaded += (s, e) => _cts?.Cancel();
    }

    // Localized string lookup with an English fallback (see Languages/*.xaml).
    private static string Tr(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as string ?? fallback;

    private static string Tr(string key, string fallback, params object[] args)
    {
        var fmt = Application.Current?.TryFindResource(key) as string ?? fallback;
        try { return string.Format(fmt, args); }
        catch (FormatException) { return string.Format(fallback, args); }
    }

    private void Tool_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.IsChecked == true)
        {
            _currentTool = rb.Content?.ToString() ?? "Ping";
            UpdateToolUI();
        }
    }

    private void UpdateToolUI()
    {
        // Ensure controls are initialized before accessing them
        if (ToolTitle == null || OutputText == null || InputHost == null || InputPanel == null)
            return;

        ToolTitle.Text = _currentTool;

        // Reset output
        OutputText.Text = GetToolDescription();

        // Update input hint based on tool
        InputHost.Text = _currentTool switch
        {
            "Ping" or "Traceroute" or "DNS Lookup" or "Whois" or "SSL Checker" or "HTTP Headers" => "google.com",
            "Port Scanner" => "127.0.0.1",
            "IP Converter" => "192.168.1.1",
            "Subnet Calculator" => "192.168.1.0/24",
            "Wake-on-LAN" => "00:1A:2B:3C:4D:5E",
            "Packet Sender" => "TCP:google.com:80:GET / HTTP/1.1\\r\\n\\r\\n",
            _ => ""
        };

        // Hide only the text box for tools that need no input; the Execute button
        // stays visible so those tools can still run.
        InputHost.Visibility = _currentTool switch
        {
            "My IP" or "ARP Table" or "Route Table" or "Speed Test" or "Network Stats" => Visibility.Collapsed,
            _ => Visibility.Visible
        };
    }

    private string GetToolDescription()
    {
        return _currentTool switch
        {
            "Ping" => "Enter a hostname or IP address and click Execute to ping.",
            "Traceroute" => "Enter a hostname or IP to trace the route to.",
            "DNS Lookup" => "Enter a domain name to lookup its DNS records.",
            "Port Scanner" => "Enter a hostname or IP. Common ports will be scanned.",
            "Whois" => "Enter a domain name to lookup its registration info.",
            "SSL Checker" => "Enter a hostname to check its SSL/TLS certificate.",
            "HTTP Headers" => "Enter a URL to view HTTP response headers and security info.",
            "My IP" => "Click Execute to see your local and public IP addresses.",
            "Subnet Calculator" => "Enter IP/CIDR (e.g., 192.168.1.0/24) to calculate subnet info.",
            "IP Converter" => "Enter an IPv4 address to convert to different formats.",
            "ARP Table" => "Click Execute to view the ARP cache.",
            "Route Table" => "Click Execute to view the routing table.",
            "Network Stats" => "Click Execute to view TCP/UDP/ICMP statistics.",
            "Wake-on-LAN" => "Enter a MAC address to send a Wake-on-LAN magic packet.",
            "Speed Test" => "Click Execute to test your download speed.",
            "Packet Sender" => "Send custom packets. Format: PROTOCOL:HOST:PORT:DATA\n" +
                               "  - TCP:google.com:80:GET / HTTP/1.1\\r\\n\\r\\n\n" +
                               "  - UDP:8.8.8.8:53:hex:AA BB CC DD\n" +
                               "  - ICMP:google.com:Hello World\n" +
                               "  - FLOOD:TCP:host:port:count:delay:data",
            _ => "Select a tool from the left panel."
        };
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ExecuteBtn_Click(sender, e);
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelBtn.IsEnabled = false;
    }

    private async void ExecuteBtn_Click(object sender, RoutedEventArgs e)
    {
        // Prevent re-entry: a second Enter / click while a run is in progress must
        // not start an overlapping operation.
        if (_isRunning) return;
        _isRunning = true;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        ExecuteBtn.IsEnabled = false;
        ExecuteBtn.Content = Tr("NetTools_Running", "Running...");
        CancelBtn.IsEnabled = true;
        CancelBtn.Visibility = Visibility.Visible;
        OutputText.Text = "";

        try
        {
            var input = InputHost.Text.Trim();

            switch (_currentTool)
            {
                case "Ping":
                    await ExecutePingAsync(input, token);
                    break;
                case "Traceroute":
                    await ExecuteTracerouteAsync(input, token);
                    break;
                case "DNS Lookup":
                    await ExecuteDnsLookupAsync(input);
                    break;
                case "Port Scanner":
                    await ExecutePortScanAsync(input, token);
                    break;
                case "Whois":
                    await ExecuteWhoisAsync(input, token);
                    break;
                case "SSL Checker":
                    await ExecuteSslCheckAsync(input, token);
                    break;
                case "HTTP Headers":
                    await ExecuteHttpHeadersAsync(input, token);
                    break;
                case "My IP":
                    await ExecuteMyIpAsync();
                    break;
                case "Subnet Calculator":
                    ExecuteSubnetCalculator(input);
                    break;
                case "IP Converter":
                    ExecuteIpConverter(input);
                    break;
                case "ARP Table":
                    await ExecuteArpAsync();
                    break;
                case "Route Table":
                    await ExecuteRouteAsync();
                    break;
                case "Network Stats":
                    ExecuteNetworkStats();
                    break;
                case "Wake-on-LAN":
                    await ExecuteWolAsync(input);
                    break;
                case "Speed Test":
                    await ExecuteSpeedTestAsync(token);
                    break;
                case "Packet Sender":
                    await ExecutePacketSenderAsync(input, token);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            OutputText.Text += "\n\n" + Tr("NetTools_Cancelled", "[Cancelled]");
        }
        catch (Exception ex)
        {
            OutputText.Text = Tr("NetTools_Error", "Error: {0}", ex.Message);
        }
        finally
        {
            _isRunning = false;
            ExecuteBtn.IsEnabled = true;
            ExecuteBtn.Content = Tr("Common_Execute", "Execute");
            CancelBtn.Visibility = Visibility.Collapsed;
            CancelBtn.IsEnabled = true;
        }
    }

    private async Task ExecutePingAsync(string host, CancellationToken token)
    {
        if (string.IsNullOrEmpty(host))
        {
            OutputText.Text = Tr("NetTools_EnterHost", "Please enter a hostname or IP address.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Pinging {host}...\n");
        OutputText.Text = sb.ToString();

        var results = await NetworkTools.PingMultipleAsync(host, 4, 5000, token);

        int successCount = 0;
        long totalTime = 0;

        foreach (var result in results)
        {
            if (result.Success)
            {
                sb.AppendLine($"Reply from {result.Address}: bytes={result.BufferSize} time={result.RoundtripTime}ms TTL={result.Ttl}");
                successCount++;
                totalTime += result.RoundtripTime;
            }
            else
            {
                sb.AppendLine($"Request timed out. ({result.Status})");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Ping statistics for {host}:");
        sb.AppendLine($"    Packets: Sent = {results.Count}, Received = {successCount}, Lost = {results.Count - successCount} ({(results.Count - successCount) * 100 / results.Count}% loss)");

        if (successCount > 0)
        {
            sb.AppendLine($"Approximate round trip times:");
            sb.AppendLine($"    Average = {totalTime / successCount}ms");
        }

        OutputText.Text = sb.ToString();
    }

    private async Task ExecuteTracerouteAsync(string host, CancellationToken token)
    {
        if (string.IsNullOrEmpty(host))
        {
            OutputText.Text = Tr("NetTools_EnterHost", "Please enter a hostname or IP address.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Tracing route to {host}...\n");
        sb.AppendLine("Hop    Address                         Time      Hostname");
        sb.AppendLine("---    -------                         ----      --------");
        OutputText.Text = sb.ToString();

        var progress = new Progress<TracerouteHop>(hop =>
        {
            var hostname = hop.Hostname ?? hop.Address;
            if (hostname.Length > 30) hostname = hostname[..27] + "...";

            // Timed-out hops have no round-trip time; show "*" instead of "0ms".
            var timeStr = hop.Address == "*" ? "*" : $"{hop.RoundtripTime}ms";

            sb.AppendLine($"{hop.HopNumber,-6} {hop.Address,-31} {timeStr,-9} {hostname}");
            OutputText.Text = sb.ToString();
        });

        await NetworkTools.TracerouteAsync(host, 30, 3000, progress, token);

        sb.AppendLine("\nTrace complete.");
        OutputText.Text = sb.ToString();
    }

    private async Task ExecuteDnsLookupAsync(string hostname)
    {
        if (string.IsNullOrEmpty(hostname))
        {
            OutputText.Text = Tr("NetTools_EnterHostname", "Please enter a hostname.");
            return;
        }

        var result = await NetworkTools.DnsLookupAsync(hostname);

        var sb = new StringBuilder();
        sb.AppendLine($"DNS Lookup for: {hostname}");
        sb.AppendLine($"Lookup time: {result.LookupTime}ms\n");

        if (result.Success)
        {
            sb.AppendLine($"Canonical Name: {result.CanonicalName}");
            sb.AppendLine();
            sb.AppendLine("Addresses:");
            foreach (var addr in result.Addresses)
            {
                sb.AppendLine($"  {addr.Address} ({addr.AddressFamily})");
            }

            if (result.Aliases.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Aliases:");
                foreach (var alias in result.Aliases)
                {
                    sb.AppendLine($"  {alias}");
                }
            }
        }
        else
        {
            sb.AppendLine($"Lookup failed: {result.ErrorMessage}");
        }

        OutputText.Text = sb.ToString();
    }

    private async Task ExecutePortScanAsync(string host, CancellationToken token)
    {
        if (string.IsNullOrEmpty(host))
        {
            OutputText.Text = Tr("NetTools_EnterHost", "Please enter a hostname or IP address.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Scanning common ports on {host}...\n");
        sb.AppendLine("Port      Service         Status      Response Time");
        sb.AppendLine("----      -------         ------      -------------");
        OutputText.Text = sb.ToString();

        var progress = new Progress<PortScanResult>(result =>
        {
            var status = result.IsOpen ? "OPEN" : "closed";
            var time = result.IsOpen ? $"{result.ResponseTime}ms" : "-";
            sb.AppendLine($"{result.Port,-9} {result.ServiceName,-15} {status,-11} {time}");
            OutputText.Text = sb.ToString();
        });

        var results = await NetworkTools.ScanCommonPortsAsync(host, 2000, progress, token);

        var openPorts = results.Count(r => r.IsOpen);
        sb.AppendLine();
        sb.AppendLine($"Scan complete. {openPorts} open ports found out of {results.Count} scanned.");

        OutputText.Text = sb.ToString();
    }

    private async Task ExecuteWhoisAsync(string domain, CancellationToken token)
    {
        if (string.IsNullOrEmpty(domain))
        {
            OutputText.Text = Tr("NetTools_EnterDomain", "Please enter a domain name.");
            return;
        }

        OutputText.Text = $"Looking up whois for {domain}...\n";

        var result = await NetworkTools.WhoisAsync(domain, token);
        OutputText.Text = result;
    }

    private async Task ExecuteMyIpAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Network Interface Information\n");
        sb.AppendLine("=" + new string('=', 60));

        var info = NetworkTools.GetLocalNetworkInfo();

        sb.AppendLine("\nLocal IPv4 Addresses:");
        foreach (var addr in info.LocalIPv4)
        {
            sb.AppendLine($"  Interface: {addr.InterfaceName}");
            sb.AppendLine($"    IP: {addr.Address}");
            sb.AppendLine($"    Subnet: {addr.SubnetMask}");
            sb.AppendLine($"    MAC: {FormatMac(addr.MacAddress)}");
            sb.AppendLine();
        }

        if (info.LocalIPv6.Count > 0)
        {
            sb.AppendLine("\nLocal IPv6 Addresses:");
            foreach (var addr in info.LocalIPv6)
            {
                sb.AppendLine($"  {addr.InterfaceName}: {addr.Address}");
            }
        }

        sb.AppendLine($"\nGateway: {info.Gateway ?? "N/A"}");

        sb.AppendLine("\nDNS Servers:");
        foreach (var dns in info.DnsServers)
        {
            sb.AppendLine($"  {dns}");
        }

        OutputText.Text = sb.ToString();

        sb.AppendLine("\nGetting public IP...");
        OutputText.Text = sb.ToString();

        var publicIp = await NetworkTools.GetPublicIPAsync();
        sb.AppendLine($"Public IP: {publicIp ?? "Could not determine"}");

        OutputText.Text = sb.ToString();
    }

    private static string FormatMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac) || mac.Length < 12) return mac ?? "N/A";

        var parts = new List<string>();
        for (int i = 0; i < mac.Length; i += 2)
        {
            parts.Add(mac.Substring(i, Math.Min(2, mac.Length - i)));
        }
        return string.Join(":", parts);
    }

    private void ExecuteIpConverter(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            OutputText.Text = "Please enter an IP address.";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"IP Conversion for: {input}\n");
        sb.AppendLine("=" + new string('=', 50));

        // Try to parse as IPv4
        if (input.Contains('.') && !input.Contains(':'))
        {
            sb.AppendLine($"\nIPv4: {input}");
            sb.AppendLine($"Decimal: {NetworkTools.IPv4ToDecimal(input)}");
            sb.AppendLine($"Binary: {NetworkTools.IPv4ToBinary(input)}");
        }
        // Try to parse as decimal
        else if (input.All(char.IsDigit))
        {
            sb.AppendLine($"\nDecimal: {input}");
            sb.AppendLine($"IPv4: {NetworkTools.DecimalToIPv4(input)}");
        }
        // Try to parse as binary
        else if (input.Replace(".", "").Replace(" ", "").All(c => c == '0' || c == '1'))
        {
            sb.AppendLine($"\nBinary: {input}");
            sb.AppendLine($"IPv4: {NetworkTools.BinaryToIPv4(input)}");
        }
        else
        {
            sb.AppendLine("Could not recognize input format.");
            sb.AppendLine("Supported formats:");
            sb.AppendLine("  - IPv4: 192.168.1.1");
            sb.AppendLine("  - Decimal: 3232235777");
            sb.AppendLine("  - Binary: 11000000.10101000.00000001.00000001");
        }

        OutputText.Text = sb.ToString();
    }

    private async Task ExecuteArpAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ARP Cache\n");
        sb.AppendLine("IP Address            MAC Address             Type");
        sb.AppendLine("---------             -----------             ----");

        var entries = await NetworkTools.GetArpTableAsync();

        foreach (var entry in entries)
        {
            sb.AppendLine($"{entry.IpAddress,-21} {entry.MacAddress,-23} {entry.Type}");
        }

        sb.AppendLine($"\nTotal entries: {entries.Count}");

        OutputText.Text = sb.ToString();
    }

    private async Task ExecuteRouteAsync()
    {
        OutputText.Text = await NetworkTools.GetRouteTableAsync();
    }

    private async Task ExecuteSpeedTestAsync(CancellationToken token)
    {
        var header = "Speed Test\n" + new string('=', 50) + "\n";
        OutputText.Text = header + Tr("NetTools_SpeedStarting", "Starting download test...");

        // One live line instead of ~1,200 appended lines; float math avoids the
        // integer-truncation that made every size read x.00.
        var progress = new Progress<SpeedTestProgress>(p =>
        {
            var mb = p.BytesTransferred / 1_048_576.0;
            var mbps = p.Speed * 8 / 1_000_000.0;
            OutputText.Text = header + Tr("NetTools_SpeedProgress",
                "Downloaded {0:F1} MB — {1:F1} Mbps", mb, mbps);
        });

        var result = await NetworkTools.SpeedTestAsync(progress, token);

        if (result.Success)
        {
            var downloadMbps = result.DownloadSpeed * 8 / 1_000_000.0;
            var sb = new StringBuilder(header);
            sb.AppendLine($"Download Speed: {downloadMbps:F2} Mbps");
            sb.AppendLine($"Downloaded:     {result.DownloadBytes / 1_048_576.0:F2} MB");
            sb.AppendLine($"Time:           {result.DownloadTime:F2} s");
            OutputText.Text = sb.ToString();
        }
        else
        {
            OutputText.Text = header + Tr("NetTools_SpeedFailed", "Speed test failed: {0}", result.ErrorMessage ?? "");
        }
    }

    private async Task ExecuteSslCheckAsync(string host, CancellationToken token)
    {
        if (string.IsNullOrEmpty(host))
        {
            OutputText.Text = Tr("NetTools_EnterHostname", "Please enter a hostname.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Checking SSL certificate for {host}...\n");
        OutputText.Text = sb.ToString();

        var result = await NetworkTools.CheckSslCertificateAsync(host, 443, token);

        sb.AppendLine("=" + new string('=', 60));
        sb.AppendLine($"SSL Certificate Report for: {host}");
        sb.AppendLine("=" + new string('=', 60));

        if (result.Success)
        {
            // Status must reflect trust, not just the expiry date: a self-signed or
            // name-mismatched cert is NOT valid even when its dates are fine.
            string statusIcon;
            if (result.IsExpired)
                statusIcon = Tr("NetTools_SslExpired", "[EXPIRED]");
            else if (!result.IsValid)
                statusIcon = Tr("NetTools_SslInvalid", "[INVALID]");
            else if (result.DaysUntilExpiry < 30)
                statusIcon = Tr("NetTools_SslWarning", "[WARNING]");
            else
                statusIcon = Tr("NetTools_SslValid", "[VALID]");

            sb.AppendLine($"\nStatus: {statusIcon}");
            if (!result.IsValid && !string.IsNullOrEmpty(result.ValidationError))
            {
                sb.AppendLine(Tr("NetTools_SslReasonLabel", "Validation errors:"));
                sb.AppendLine($"  {result.ValidationError}");
            }
            sb.AppendLine($"Days until expiry: {result.DaysUntilExpiry}");

            sb.AppendLine($"\nSubject: {result.Subject}");
            sb.AppendLine($"Issuer: {result.Issuer}");
            sb.AppendLine($"\nValid From: {result.ValidFrom:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Valid To: {result.ValidTo:yyyy-MM-dd HH:mm:ss}");

            sb.AppendLine($"\nProtocol: {result.Protocol}");
            sb.AppendLine($"Cipher: {result.CipherAlgorithm}");
            sb.AppendLine($"Signature Algorithm: {result.SignatureAlgorithm}");

            sb.AppendLine($"\nThumbprint: {result.Thumbprint}");
            sb.AppendLine($"Serial Number: {result.SerialNumber}");

            if (!string.IsNullOrEmpty(result.SubjectAlternativeNames))
            {
                sb.AppendLine($"\nSubject Alternative Names:");
                sb.AppendLine(result.SubjectAlternativeNames);
            }
        }
        else
        {
            sb.AppendLine($"\nSSL check failed: {result.ErrorMessage}");
        }

        OutputText.Text = sb.ToString();
    }

    private async Task ExecuteHttpHeadersAsync(string url, CancellationToken token)
    {
        if (string.IsNullOrEmpty(url))
        {
            OutputText.Text = Tr("NetTools_EnterUrl", "Please enter a URL.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Fetching HTTP headers for {url}...\n");
        OutputText.Text = sb.ToString();

        var result = await NetworkTools.GetHttpHeadersAsync(url, token);

        sb.AppendLine("=" + new string('=', 60));
        sb.AppendLine($"HTTP Headers for: {result.Url}");
        sb.AppendLine("=" + new string('=', 60));

        if (result.Success)
        {
            sb.AppendLine($"\nStatus: {result.StatusCode} {result.StatusDescription}");
            sb.AppendLine($"Response Time: {result.ResponseTime}ms");

            sb.AppendLine("\n--- Security Headers Analysis ---");
            sb.AppendLine($"  HSTS (Strict-Transport-Security): {(result.HasHSTS ? "Present" : "MISSING")}");
            sb.AppendLine($"  X-Frame-Options: {(result.HasXFrameOptions ? "Present" : "MISSING")}");
            sb.AppendLine($"  X-Content-Type-Options: {(result.HasXContentTypeOptions ? "Present" : "MISSING")}");
            sb.AppendLine($"  Content-Security-Policy: {(result.HasCSP ? "Present" : "MISSING")}");

            sb.AppendLine("\n--- All Headers ---");
            foreach (var header in result.Headers.OrderBy(h => h.Key))
            {
                sb.AppendLine($"  {header.Key}: {header.Value}");
            }
        }
        else
        {
            sb.AppendLine($"\nFailed to fetch headers: {result.ErrorMessage}");
        }

        OutputText.Text = sb.ToString();
    }

    private void ExecuteSubnetCalculator(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            OutputText.Text = Tr("NetTools_SubnetPrompt", "Please enter an IP address with CIDR (e.g., 192.168.1.0/24).");
            return;
        }

        var result = NetworkTools.CalculateSubnet(input);

        var sb = new StringBuilder();
        sb.AppendLine("=" + new string('=', 50));
        sb.AppendLine($"Subnet Calculator");
        sb.AppendLine("=" + new string('=', 50));

        if (result.Success)
        {
            sb.AppendLine($"\nInput: {result.InputIP}/{result.CIDR}");
            sb.AppendLine();
            sb.AppendLine($"Network Address:    {result.NetworkAddress}");
            sb.AppendLine($"Broadcast Address:  {result.BroadcastAddress}");
            sb.AppendLine($"Subnet Mask:        {result.SubnetMask}");
            sb.AppendLine($"Wildcard Mask:      {result.WildcardMask}");
            sb.AppendLine();
            sb.AppendLine($"First Usable Host:  {result.FirstUsable}");
            sb.AppendLine($"Last Usable Host:   {result.LastUsable}");
            sb.AppendLine();
            sb.AppendLine($"Total Addresses:    {result.TotalHosts:N0}");
            sb.AppendLine($"Usable Hosts:       {result.UsableHosts:N0}");
        }
        else
        {
            // Turn the Core error code into a localized, human message.
            var msg = result.ErrorMessage switch
            {
                NetworkTools.SubnetErrorIPv4Only => Tr("NetTools_SubnetIpv4Only", "Only IPv4 addresses are supported."),
                NetworkTools.SubnetErrorCidrRange => Tr("NetTools_SubnetCidrRange", "CIDR prefix must be between 0 and 32."),
                NetworkTools.SubnetErrorFormat => Tr("NetTools_SubnetFormat", "Invalid format. Use: 192.168.1.0/24"),
                _ => result.ErrorMessage
            };
            sb.AppendLine($"\nError: {msg}");
            sb.AppendLine("\nExpected format: 192.168.1.0/24");
        }

        OutputText.Text = sb.ToString();
    }

    private void ExecuteNetworkStats()
    {
        var stats = NetworkTools.GetNetworkStatistics();

        var sb = new StringBuilder();
        sb.AppendLine("=" + new string('=', 50));
        sb.AppendLine("Network Statistics");
        sb.AppendLine("=" + new string('=', 50));

        if (stats.Success)
        {
            sb.AppendLine($"\n--- TCP Statistics ---");
            sb.AppendLine($"  Current Connections:    {stats.TcpConnectionsEstablished:N0}");
            sb.AppendLine($"  Active Connections:     {stats.ActiveConnections:N0}");
            sb.AppendLine($"  Segments Sent:          {stats.TcpSegmentsSent:N0}");
            sb.AppendLine($"  Segments Received:      {stats.TcpSegmentsReceived:N0}");
            sb.AppendLine($"  Segments Retransmitted: {stats.TcpSegmentsRetransmitted:N0}");
            sb.AppendLine($"  Errors Received:        {stats.TcpErrorsReceived:N0}");

            sb.AppendLine($"\n--- UDP Statistics ---");
            sb.AppendLine($"  Datagrams Sent:         {stats.UdpDatagramsSent:N0}");
            sb.AppendLine($"  Datagrams Received:     {stats.UdpDatagramsReceived:N0}");

            sb.AppendLine($"\n--- ICMP Statistics ---");
            sb.AppendLine($"  Messages Sent:          {stats.IcmpMessagesSent:N0}");
            sb.AppendLine($"  Messages Received:      {stats.IcmpMessagesReceived:N0}");

            sb.AppendLine($"\n--- IP Statistics ---");
            sb.AppendLine($"  Packets Received:       {stats.IpPacketsReceived:N0}");
            sb.AppendLine($"  Packets Delivered:      {stats.IpPacketsDelivered:N0}");
            sb.AppendLine($"  Packets Discarded:      {stats.IpPacketsDiscarded:N0}");
        }
        else
        {
            sb.AppendLine($"\nFailed to get statistics: {stats.ErrorMessage}");
        }

        OutputText.Text = sb.ToString();
    }

    private async Task ExecuteWolAsync(string macAddress)
    {
        if (string.IsNullOrEmpty(macAddress))
        {
            OutputText.Text = "Please enter a MAC address (e.g., 00:1A:2B:3C:4D:5E).";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Sending Wake-on-LAN magic packet...\n");
        sb.AppendLine($"MAC Address: {macAddress}");
        OutputText.Text = sb.ToString();

        var success = await NetworkTools.SendWakeOnLanAsync(macAddress);

        sb.AppendLine();
        if (success)
        {
            sb.AppendLine("Magic packet sent successfully!");
            sb.AppendLine("\nNote: The target device must:");
            sb.AppendLine("  - Have Wake-on-LAN enabled in BIOS");
            sb.AppendLine("  - Have WoL enabled in network adapter settings");
            sb.AppendLine("  - Be connected via Ethernet (not WiFi)");
            sb.AppendLine("  - Be on the same local network");
        }
        else
        {
            sb.AppendLine("Failed to send magic packet.");
            sb.AppendLine("Please check the MAC address format.");
        }

        OutputText.Text = sb.ToString();
    }

    private async Task ExecutePacketSenderAsync(string input, CancellationToken token)
    {
        if (string.IsNullOrEmpty(input))
        {
            OutputText.Text = GetToolDescription();
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("=" + new string('=', 60));
        sb.AppendLine("Packet Sender");
        sb.AppendLine("=" + new string('=', 60));

        try
        {
            // Parse input format: PROTOCOL:HOST:PORT:DATA or FLOOD:PROTOCOL:HOST:PORT:COUNT:DELAY:DATA
            var parts = input.Split(':', 4);

            if (parts.Length < 2)
            {
                OutputText.Text = "Invalid format. Use: PROTOCOL:HOST:PORT:DATA\n\n" + GetToolDescription();
                return;
            }

            var protocol = parts[0].ToUpper();

            // Check for FLOOD mode
            if (protocol == "FLOOD")
            {
                await ExecutePacketFloodAsync(input, sb, token);
                return;
            }

            // Single packet mode
            var host = parts.Length > 1 ? parts[1] : "";
            var port = 0;
            var dataStr = "";

            if (protocol == "ICMP")
            {
                // ICMP:HOST:DATA
                dataStr = parts.Length > 2 ? parts[2] : "Ping Test";
            }
            else
            {
                // TCP/UDP:HOST:PORT:DATA
                if (parts.Length < 3 || !int.TryParse(parts[2].Split(':')[0], out port))
                {
                    OutputText.Text = "Invalid format. TCP/UDP requires port number.\n\n" + GetToolDescription();
                    return;
                }

                // Handle case where port and data are in same part due to split limit
                if (parts.Length >= 4)
                {
                    dataStr = parts[3];
                }
                else if (parts[2].Contains(':'))
                {
                    var portParts = parts[2].Split(':', 2);
                    port = int.Parse(portParts[0]);
                    dataStr = portParts.Length > 1 ? portParts[1] : "";
                }
            }

            // Parse data (support hex: prefix)
            byte[] data;
            if (dataStr.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
            {
                data = NetworkTools.ParseHexString(dataStr[4..]);
                sb.AppendLine($"Data format: Hex ({data.Length} bytes)");
            }
            else
            {
                // Handle escape sequences
                dataStr = dataStr.Replace("\\r\\n", "\r\n").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
                data = Encoding.UTF8.GetBytes(dataStr);
                sb.AppendLine($"Data format: Text ({data.Length} bytes)");
            }

            sb.AppendLine($"Protocol: {protocol}");
            sb.AppendLine($"Target: {host}" + (port > 0 ? $":{port}" : ""));
            sb.AppendLine($"Data size: {data.Length} bytes");
            sb.AppendLine();
            sb.AppendLine("Sending packet...");
            OutputText.Text = sb.ToString();

            PacketSendResult result;

            switch (protocol)
            {
                case "TCP":
                    result = await NetworkTools.SendTcpPacketAsync(host, port, data, 10000);
                    break;
                case "UDP":
                    result = await NetworkTools.SendUdpPacketAsync(host, port, data, 5000);
                    break;
                case "ICMP":
                    result = await NetworkTools.SendIcmpPacketAsync(host, data, 5000);
                    break;
                default:
                    OutputText.Text = $"Unknown protocol: {protocol}. Use TCP, UDP, or ICMP.";
                    return;
            }

            sb.AppendLine();
            sb.AppendLine("--- Result ---");

            if (result.Success)
            {
                sb.AppendLine($"Status: SUCCESS");
                sb.AppendLine($"Bytes sent: {result.BytesSent}");
                sb.AppendLine($"Bytes received: {result.BytesReceived}");
                sb.AppendLine($"Round-trip time: {result.RoundtripTime}ms");

                if (protocol == "ICMP")
                {
                    sb.AppendLine($"TTL: {result.Ttl}");
                }

                if (result.ResponseData != null && result.ResponseData.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("--- Response Data ---");

                    // Show hex dump
                    sb.AppendLine("Hex:");
                    var hexLines = FormatHexDump(result.ResponseData);
                    foreach (var line in hexLines)
                    {
                        sb.AppendLine($"  {line}");
                    }

                    // Show ASCII if printable
                    var ascii = NetworkTools.FormatAsAscii(result.ResponseData);
                    if (ascii.Length <= 500)
                    {
                        sb.AppendLine();
                        sb.AppendLine("ASCII:");
                        sb.AppendLine($"  {ascii}");
                    }
                }
            }
            else
            {
                sb.AppendLine($"Status: FAILED");
                sb.AppendLine($"Error: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"\nError: {ex.Message}");
        }

        OutputText.Text = sb.ToString();
    }

    private async Task ExecutePacketFloodAsync(string input, StringBuilder sb, CancellationToken token)
    {
        // FLOOD:PROTOCOL:HOST:PORT:COUNT:DELAY:DATA
        var parts = input.Split(':', 7);

        if (parts.Length < 5)
        {
            OutputText.Text = Tr("NetTools_FloodFormat",
                "Invalid FLOOD format. Use: FLOOD:PROTOCOL:HOST:PORT:COUNT:DELAY:DATA");
            return;
        }

        var protocol = parts[1].ToUpper();
        var host = parts[2];

        // Parse numbers defensively so a typo shows a friendly message, not a crash.
        int port = 0, requestedCount, requestedDelay;
        string dataStr;
        if (protocol == "ICMP")
        {
            if (!int.TryParse(parts[3], out requestedCount))
            {
                OutputText.Text = Tr("NetTools_FloodBadNumbers", "Count and delay must be whole numbers.");
                return;
            }
            requestedDelay = parts.Length > 4 && int.TryParse(parts[4], out var d) ? d : 100;
            dataStr = parts.Length > 5 ? parts[5] : "Flood Test";
        }
        else
        {
            if (!int.TryParse(parts[3], out port) || !int.TryParse(parts[4], out requestedCount))
            {
                OutputText.Text = Tr("NetTools_FloodBadNumbers", "Count and delay must be whole numbers.");
                return;
            }
            requestedDelay = parts.Length > 5 && int.TryParse(parts[5], out var d) ? d : 100;
            dataStr = parts.Length > 6 ? parts[6] : "Flood Test";
        }

        // Enforce caps: at most 10,000 packets and no faster than ~1,000/second.
        var count = Math.Clamp(requestedCount, 1, NetworkTools.MaxFloodPackets);
        var delayMs = Math.Max(NetworkTools.MinFloodDelayMs, requestedDelay);
        bool capped = count != requestedCount || delayMs != requestedDelay;

        byte[] data;
        if (dataStr.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
        {
            data = NetworkTools.ParseHexString(dataStr[4..]);
        }
        else
        {
            dataStr = dataStr.Replace("\\r\\n", "\r\n").Replace("\\n", "\n");
            data = Encoding.UTF8.GetBytes(dataStr);
        }

        sb.AppendLine(Tr("NetTools_FloodWarning",
            "WARNING: FLOOD sends many packets quickly. Only target hosts you own or have permission to test."));
        if (capped)
        {
            sb.AppendLine(Tr("NetTools_FloodCapped",
                "Capped to {0} packets, min {1} ms delay (max ~{2}/s).",
                count, delayMs, 1000 / delayMs));
        }
        sb.AppendLine();
        sb.AppendLine($"Protocol: {protocol}");
        sb.AppendLine($"Target: {host}" + (port > 0 ? $":{port}" : ""));
        sb.AppendLine($"Packet count: {count}");
        sb.AppendLine($"Delay between packets: {delayMs}ms");
        sb.AppendLine($"Data size: {data.Length} bytes");
        sb.AppendLine();
        sb.AppendLine("Sending packets... (press Cancel to stop)");
        sb.AppendLine();
        sb.AppendLine("Seq      Status    Time      Bytes Sent/Recv");
        sb.AppendLine("---      ------    ----      ---------------");
        OutputText.Text = sb.ToString();

        var progress = new Progress<PacketSendResult>(result =>
        {
            var status = result.Success ? "OK" : "FAIL";
            var time = result.Success ? $"{result.RoundtripTime}ms" : "-";
            var bytes = result.Success ? $"{result.BytesSent}/{result.BytesReceived}" : "-";
            sb.AppendLine($"{result.SequenceNumber,-8} {status,-9} {time,-9} {bytes}");
            OutputText.Text = sb.ToString();
        });

        var floodResult = await NetworkTools.SendPacketFloodAsync(
            protocol, host, port, data, count, delayMs, 5000, progress, token);

        sb.AppendLine();
        sb.AppendLine("=" + new string('=', 50));
        sb.AppendLine("Statistics:");
        sb.AppendLine($"  Packets sent: {floodResult.TotalPackets}");
        sb.AppendLine($"  Successful: {floodResult.SuccessCount}");
        sb.AppendLine($"  Failed: {floodResult.FailedCount}");
        sb.AppendLine($"  Packet loss: {floodResult.PacketLossPercent:F1}%");
        sb.AppendLine();
        sb.AppendLine($"  Total bytes sent: {floodResult.TotalBytesSent}");
        sb.AppendLine($"  Total bytes received: {floodResult.TotalBytesReceived}");
        sb.AppendLine($"  Total time: {floodResult.TotalTime:F0}ms");

        if (floodResult.SuccessCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Latency:");
            sb.AppendLine($"  Min: {floodResult.MinLatency}ms");
            sb.AppendLine($"  Max: {floodResult.MaxLatency}ms");
            sb.AppendLine($"  Avg: {floodResult.AvgLatency:F1}ms");
        }

        OutputText.Text = sb.ToString();
    }

    private static List<string> FormatHexDump(byte[] data, int bytesPerLine = 16)
    {
        var lines = new List<string>();
        for (int i = 0; i < data.Length; i += bytesPerLine)
        {
            var lineBytes = data.Skip(i).Take(bytesPerLine).ToArray();
            var hex = string.Join(" ", lineBytes.Select(b => b.ToString("X2")));
            var ascii = NetworkTools.FormatAsAscii(lineBytes);
            lines.Add($"{i:X4}: {hex.PadRight(bytesPerLine * 3 - 1)}  {ascii}");
        }
        return lines;
    }
}

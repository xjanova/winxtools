using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace NetX.Core.Network;

/// <summary>
/// Collection of network diagnostic and analysis tools
/// </summary>
public static class NetworkTools
{
    #region Ping

    public static async Task<PingResult> PingAsync(string host, int timeout = 5000, int ttl = 128)
    {
        var result = new PingResult { Host = host };

        try
        {
            using var ping = new Ping();
            var options = new PingOptions { Ttl = ttl, DontFragment = true };
            var buffer = Encoding.ASCII.GetBytes("WinXTools Ping Test");

            var stopwatch = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(host, timeout, buffer, options);
            stopwatch.Stop();

            result.Success = reply.Status == IPStatus.Success;
            result.RoundtripTime = reply.RoundtripTime;
            result.Ttl = reply.Options?.Ttl ?? 0;
            result.Status = reply.Status.ToString();
            result.Address = reply.Address?.ToString() ?? host;
            result.BufferSize = buffer.Length;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Status = "Error";
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public static async Task<List<PingResult>> PingMultipleAsync(string host, int count = 4, int timeout = 5000)
    {
        var results = new List<PingResult>();

        for (int i = 0; i < count; i++)
        {
            results.Add(await PingAsync(host, timeout));
            if (i < count - 1)
                await Task.Delay(1000);
        }

        return results;
    }

    #endregion

    #region Traceroute

    public static async Task<List<TracerouteHop>> TracerouteAsync(string host, int maxHops = 30, int timeout = 3000,
        IProgress<TracerouteHop>? progress = null)
    {
        var hops = new List<TracerouteHop>();

        try
        {
            var targetAddress = await ResolveHostAsync(host);
            if (targetAddress == null)
            {
                hops.Add(new TracerouteHop { HopNumber = 1, Status = "Could not resolve host" });
                return hops;
            }

            using var ping = new Ping();
            var buffer = Encoding.ASCII.GetBytes("WinXTools Trace");

            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                var hop = new TracerouteHop { HopNumber = ttl };

                try
                {
                    var options = new PingOptions { Ttl = ttl, DontFragment = true };
                    var stopwatch = Stopwatch.StartNew();
                    var reply = await ping.SendPingAsync(targetAddress, timeout, buffer, options);
                    stopwatch.Stop();

                    hop.Address = reply.Address?.ToString() ?? "*";
                    hop.RoundtripTime = reply.RoundtripTime;
                    hop.Status = reply.Status.ToString();

                    // Try to resolve hostname
                    if (reply.Address != null)
                    {
                        try
                        {
                            var hostEntry = await Dns.GetHostEntryAsync(reply.Address);
                            hop.Hostname = hostEntry.HostName;
                        }
                        catch
                        {
                            hop.Hostname = hop.Address;
                        }
                    }

                    hops.Add(hop);
                    progress?.Report(hop);

                    // Reached destination
                    if (reply.Status == IPStatus.Success)
                        break;
                }
                catch
                {
                    hop.Address = "*";
                    hop.Status = "Timeout";
                    hops.Add(hop);
                    progress?.Report(hop);
                }
            }
        }
        catch (Exception ex)
        {
            hops.Add(new TracerouteHop { HopNumber = 1, Status = $"Error: {ex.Message}" });
        }

        return hops;
    }

    #endregion

    #region DNS Lookup

    public static async Task<DnsResult> DnsLookupAsync(string hostname)
    {
        var result = new DnsResult { Hostname = hostname };

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var hostEntry = await Dns.GetHostEntryAsync(hostname);
            stopwatch.Stop();

            result.Success = true;
            result.LookupTime = stopwatch.ElapsedMilliseconds;
            result.Addresses = hostEntry.AddressList.Select(a => new DnsAddress
            {
                Address = a.ToString(),
                AddressFamily = a.AddressFamily.ToString()
            }).ToList();
            result.Aliases = hostEntry.Aliases.ToList();
            result.CanonicalName = hostEntry.HostName;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public static async Task<string?> ReverseDnsLookupAsync(string ipAddress)
    {
        try
        {
            var ip = IPAddress.Parse(ipAddress);
            var hostEntry = await Dns.GetHostEntryAsync(ip);
            return hostEntry.HostName;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Port Scanner

    public static async Task<PortScanResult> ScanPortAsync(string host, int port, int timeout = 2000)
    {
        var result = new PortScanResult { Host = host, Port = port };

        try
        {
            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();

            var connectTask = client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(timeout);

            if (await Task.WhenAny(connectTask, timeoutTask) == connectTask)
            {
                await connectTask;
                stopwatch.Stop();
                result.IsOpen = true;
                result.ResponseTime = stopwatch.ElapsedMilliseconds;
                result.ServiceName = GetServiceName(port);
            }
            else
            {
                result.IsOpen = false;
                result.Status = "Timeout";
            }
        }
        catch (SocketException ex)
        {
            result.IsOpen = false;
            result.Status = ex.SocketErrorCode.ToString();
        }
        catch
        {
            result.IsOpen = false;
            result.Status = "Error";
        }

        return result;
    }

    public static async Task<List<PortScanResult>> ScanPortRangeAsync(string host, int startPort, int endPort,
        int timeout = 1000, int concurrency = 100, IProgress<PortScanResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PortScanResult>();
        var semaphore = new SemaphoreSlim(concurrency);

        var tasks = new List<Task>();

        for (int port = startPort; port <= endPort; port++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int currentPort = port;
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var result = await ScanPortAsync(host, currentPort, timeout);
                    lock (results)
                    {
                        results.Add(result);
                    }
                    progress?.Report(result);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.Port).ToList();
    }

    public static async Task<List<PortScanResult>> ScanCommonPortsAsync(string host, int timeout = 1000,
        IProgress<PortScanResult>? progress = null)
    {
        int[] commonPorts = { 21, 22, 23, 25, 53, 80, 110, 135, 139, 143, 443, 445, 993, 995,
            1433, 1521, 3306, 3389, 5432, 5900, 6379, 8080, 8443, 27017 };

        var results = new List<PortScanResult>();
        var tasks = commonPorts.Select(port => Task.Run(async () =>
        {
            var result = await ScanPortAsync(host, port, timeout);
            lock (results)
            {
                results.Add(result);
            }
            progress?.Report(result);
        }));

        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.Port).ToList();
    }

    private static string GetServiceName(int port)
    {
        return port switch
        {
            21 => "FTP",
            22 => "SSH",
            23 => "Telnet",
            25 => "SMTP",
            53 => "DNS",
            80 => "HTTP",
            110 => "POP3",
            135 => "RPC",
            139 => "NetBIOS",
            143 => "IMAP",
            443 => "HTTPS",
            445 => "SMB",
            993 => "IMAPS",
            995 => "POP3S",
            1433 => "MSSQL",
            1521 => "Oracle",
            3306 => "MySQL",
            3389 => "RDP",
            5432 => "PostgreSQL",
            5900 => "VNC",
            6379 => "Redis",
            8080 => "HTTP-Alt",
            8443 => "HTTPS-Alt",
            27017 => "MongoDB",
            _ => "Unknown"
        };
    }

    #endregion

    #region Whois

    public static async Task<string> WhoisAsync(string domain)
    {
        try
        {
            // Get TLD to determine whois server
            var parts = domain.Split('.');
            var tld = parts.Length > 1 ? parts[^1] : "com";

            var whoisServer = tld.ToLower() switch
            {
                "com" or "net" => "whois.verisign-grs.com",
                "org" => "whois.pir.org",
                "io" => "whois.nic.io",
                "co" => "whois.nic.co",
                "me" => "whois.nic.me",
                "info" => "whois.afilias.net",
                "biz" => "whois.biz",
                "us" => "whois.nic.us",
                "uk" => "whois.nic.uk",
                "de" => "whois.denic.de",
                "fr" => "whois.nic.fr",
                "jp" => "whois.jprs.jp",
                "th" => "whois.thnic.co.th",
                _ => $"whois.nic.{tld}"
            };

            using var client = new TcpClient();
            await client.ConnectAsync(whoisServer, 43);

            using var stream = client.GetStream();
            var query = Encoding.ASCII.GetBytes(domain + "\r\n");
            await stream.WriteAsync(query);

            using var reader = new StreamReader(stream, Encoding.ASCII);
            return await reader.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            return $"Whois lookup failed: {ex.Message}";
        }
    }

    #endregion

    #region IP Information

    public static LocalNetworkInfo GetLocalNetworkInfo()
    {
        var info = new LocalNetworkInfo();

        try
        {
            // Get all network interfaces
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    var props = ni.GetIPProperties();

                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            info.LocalIPv4.Add(new InterfaceAddress
                            {
                                InterfaceName = ni.Name,
                                Address = addr.Address.ToString(),
                                SubnetMask = addr.IPv4Mask?.ToString() ?? "",
                                MacAddress = ni.GetPhysicalAddress().ToString()
                            });
                        }
                        else if (addr.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            info.LocalIPv6.Add(new InterfaceAddress
                            {
                                InterfaceName = ni.Name,
                                Address = addr.Address.ToString()
                            });
                        }
                    }

                    // Get gateway
                    foreach (var gateway in props.GatewayAddresses)
                    {
                        if (gateway.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            info.Gateway = gateway.Address.ToString();
                        }
                    }

                    // Get DNS servers
                    foreach (var dns in props.DnsAddresses)
                    {
                        if (dns.AddressFamily == AddressFamily.InterNetwork)
                        {
                            info.DnsServers.Add(dns.ToString());
                        }
                    }
                }
            }
        }
        catch { }

        return info;
    }

    public static async Task<string?> GetPublicIPAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetStringAsync("https://api.ipify.org");
            return response.Trim();
        }
        catch
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetStringAsync("https://icanhazip.com");
                return response.Trim();
            }
            catch
            {
                return null;
            }
        }
    }

    #endregion

    #region IP Converter

    public static string IPv4ToDecimal(string ipv4)
    {
        try
        {
            var ip = IPAddress.Parse(ipv4);
            var bytes = ip.GetAddressBytes();
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToUInt32(bytes, 0).ToString();
        }
        catch
        {
            return "Invalid IP";
        }
    }

    public static string DecimalToIPv4(string decimalIp)
    {
        try
        {
            var dec = uint.Parse(decimalIp);
            var bytes = BitConverter.GetBytes(dec);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return new IPAddress(bytes).ToString();
        }
        catch
        {
            return "Invalid decimal";
        }
    }

    public static string IPv4ToBinary(string ipv4)
    {
        try
        {
            var parts = ipv4.Split('.');
            return string.Join(".", parts.Select(p => Convert.ToString(int.Parse(p), 2).PadLeft(8, '0')));
        }
        catch
        {
            return "Invalid IP";
        }
    }

    public static string BinaryToIPv4(string binary)
    {
        try
        {
            binary = binary.Replace(".", "").Replace(" ", "");
            if (binary.Length != 32) return "Invalid binary (need 32 bits)";

            var parts = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                parts.Add(Convert.ToInt32(binary.Substring(i * 8, 8), 2).ToString());
            }
            return string.Join(".", parts);
        }
        catch
        {
            return "Invalid binary";
        }
    }

    #endregion

    #region ARP & Route

    public static async Task<List<ArpEntry>> GetArpTableAsync()
    {
        var entries = new List<ArpEntry>();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "arp",
                Arguments = "-a",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi);
            if (process == null) return entries;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Parse ARP output - handle various Windows locale formats
            // Format: IP Address          Physical Address      Type
            // Example: 192.168.1.1        00-1a-2b-3c-4d-5e     dynamic
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Skip header lines and interface lines
                if (string.IsNullOrWhiteSpace(trimmedLine) ||
                    trimmedLine.StartsWith("Interface:") ||
                    trimmedLine.StartsWith("Internet") ||
                    trimmedLine.Contains("Physical Address") ||
                    trimmedLine.Contains("---"))
                    continue;

                // Try to extract IP, MAC, and Type using flexible pattern
                // Matches: IP (with dots) followed by MAC (with dashes or colons) followed by type
                var regex = new Regex(@"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\s+([0-9a-fA-F]{2}[-:][0-9a-fA-F]{2}[-:][0-9a-fA-F]{2}[-:][0-9a-fA-F]{2}[-:][0-9a-fA-F]{2}[-:][0-9a-fA-F]{2})\s+(\S+)");
                var match = regex.Match(trimmedLine);

                if (match.Success)
                {
                    entries.Add(new ArpEntry
                    {
                        IpAddress = match.Groups[1].Value,
                        MacAddress = match.Groups[2].Value.Replace("-", ":").ToUpper(),
                        Type = match.Groups[3].Value
                    });
                }
            }
        }
        catch { }

        return entries;
    }

    public static async Task<string> GetRouteTableAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "route",
                Arguments = "print",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return "Failed to get route table";

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return output;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    #endregion

    #region Speed Test

    public static async Task<SpeedTestResult> SpeedTestAsync(IProgress<SpeedTestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new SpeedTestResult();

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // Test download speed using a reliable CDN file
            var testUrls = new[]
            {
                "https://speed.cloudflare.com/__down?bytes=10000000",
                "https://proof.ovh.net/files/10Mb.dat"
            };

            string testUrl = testUrls[0];
            long totalBytes = 0;
            var stopwatch = Stopwatch.StartNew();

            var response = await client.GetAsync(testUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[8192];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                totalBytes += bytesRead;
                var elapsed = stopwatch.Elapsed.TotalSeconds;
                if (elapsed > 0)
                {
                    var currentSpeed = totalBytes / elapsed;
                    progress?.Report(new SpeedTestProgress
                    {
                        BytesTransferred = totalBytes,
                        Speed = currentSpeed,
                        IsDownload = true
                    });
                }
            }

            stopwatch.Stop();
            result.DownloadBytes = totalBytes;
            result.DownloadTime = stopwatch.Elapsed.TotalSeconds;
            result.DownloadSpeed = totalBytes / result.DownloadTime;
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    #endregion

    #region Subnet Calculator

    public static SubnetInfo CalculateSubnet(string ipAddress, int cidr)
    {
        var result = new SubnetInfo { InputIP = ipAddress, CIDR = cidr };

        try
        {
            var ip = IPAddress.Parse(ipAddress);
            var ipBytes = ip.GetAddressBytes();
            var ipValue = BitConverter.ToUInt32(ipBytes.Reverse().ToArray(), 0);

            // Create subnet mask
            uint mask = cidr == 0 ? 0 : ~((1u << (32 - cidr)) - 1);
            result.SubnetMask = new IPAddress(BitConverter.GetBytes(mask).Reverse().ToArray()).ToString();

            // Calculate network address
            uint networkValue = ipValue & mask;
            result.NetworkAddress = new IPAddress(BitConverter.GetBytes(networkValue).Reverse().ToArray()).ToString();

            // Calculate broadcast address
            uint broadcastValue = networkValue | ~mask;
            result.BroadcastAddress = new IPAddress(BitConverter.GetBytes(broadcastValue).Reverse().ToArray()).ToString();

            // Calculate first and last usable
            result.FirstUsable = new IPAddress(BitConverter.GetBytes(networkValue + 1).Reverse().ToArray()).ToString();
            result.LastUsable = new IPAddress(BitConverter.GetBytes(broadcastValue - 1).Reverse().ToArray()).ToString();

            // Calculate total hosts
            result.TotalHosts = (int)Math.Pow(2, 32 - cidr);
            result.UsableHosts = result.TotalHosts > 2 ? result.TotalHosts - 2 : 0;

            // Wildcard mask
            uint wildcardValue = ~mask;
            result.WildcardMask = new IPAddress(BitConverter.GetBytes(wildcardValue).Reverse().ToArray()).ToString();

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public static SubnetInfo CalculateSubnet(string ipWithCidr)
    {
        var parts = ipWithCidr.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int cidr))
        {
            return new SubnetInfo { Success = false, ErrorMessage = "Invalid format. Use: 192.168.1.0/24" };
        }
        return CalculateSubnet(parts[0], cidr);
    }

    #endregion

    #region SSL Certificate Checker

    public static async Task<SslCertificateInfo> CheckSslCertificateAsync(string hostname, int port = 443)
    {
        var result = new SslCertificateInfo { Hostname = hostname, Port = port };

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(hostname, port);

            using var sslStream = new System.Net.Security.SslStream(
                client.GetStream(),
                false,
                (sender, cert, chain, errors) =>
                {
                    if (cert != null)
                    {
                        var x509 = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
                        result.Subject = x509.Subject;
                        result.Issuer = x509.Issuer;
                        result.ValidFrom = x509.NotBefore;
                        result.ValidTo = x509.NotAfter;
                        result.Thumbprint = x509.Thumbprint;
                        result.SerialNumber = x509.SerialNumber;
                        result.SignatureAlgorithm = x509.SignatureAlgorithm.FriendlyName;

                        // Get SAN (Subject Alternative Names)
                        foreach (var ext in x509.Extensions)
                        {
                            if (ext.Oid?.Value == "2.5.29.17") // SAN OID
                            {
                                result.SubjectAlternativeNames = ext.Format(true);
                            }
                        }

                        // Check validity
                        var now = DateTime.Now;
                        result.IsExpired = now > x509.NotAfter;
                        result.DaysUntilExpiry = (int)(x509.NotAfter - now).TotalDays;
                        result.IsValid = errors == System.Net.Security.SslPolicyErrors.None;
                    }
                    return true;
                });

            await sslStream.AuthenticateAsClientAsync(hostname);
            result.Protocol = sslStream.SslProtocol.ToString();
            result.CipherAlgorithm = sslStream.CipherAlgorithm.ToString();
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    #endregion

    #region HTTP Headers

    public static async Task<HttpHeadersResult> GetHttpHeadersAsync(string url)
    {
        var result = new HttpHeadersResult { Url = url };

        try
        {
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url;

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var stopwatch = Stopwatch.StartNew();
            var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await client.SendAsync(request);
            stopwatch.Stop();

            result.StatusCode = (int)response.StatusCode;
            result.StatusDescription = response.StatusCode.ToString();
            result.ResponseTime = stopwatch.ElapsedMilliseconds;

            foreach (var header in response.Headers)
            {
                result.Headers[header.Key] = string.Join(", ", header.Value);
            }

            foreach (var header in response.Content.Headers)
            {
                result.Headers[header.Key] = string.Join(", ", header.Value);
            }

            // Security headers check
            result.HasHSTS = result.Headers.ContainsKey("Strict-Transport-Security");
            result.HasXFrameOptions = result.Headers.ContainsKey("X-Frame-Options");
            result.HasXContentTypeOptions = result.Headers.ContainsKey("X-Content-Type-Options");
            result.HasCSP = result.Headers.ContainsKey("Content-Security-Policy");

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    #endregion

    #region Wake-on-LAN

    public static async Task<bool> SendWakeOnLanAsync(string macAddress)
    {
        try
        {
            // Parse MAC address
            macAddress = macAddress.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpper();
            if (macAddress.Length != 12)
                return false;

            var macBytes = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                macBytes[i] = Convert.ToByte(macAddress.Substring(i * 2, 2), 16);
            }

            // Build magic packet: 6x 0xFF + 16x MAC
            var packet = new byte[102];
            for (int i = 0; i < 6; i++)
                packet[i] = 0xFF;

            for (int i = 1; i <= 16; i++)
            {
                Array.Copy(macBytes, 0, packet, i * 6, 6);
            }

            // Send via UDP broadcast
            using var client = new UdpClient();
            client.EnableBroadcast = true;
            await client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            await client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 7));

            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Network Statistics

    public static NetworkStatistics GetNetworkStatistics()
    {
        var stats = new NetworkStatistics();

        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();

            // TCP Statistics
            var tcpStats = properties.GetTcpIPv4Statistics();
            stats.TcpConnectionsEstablished = (int)tcpStats.CurrentConnections;
            stats.TcpSegmentsSent = tcpStats.SegmentsSent;
            stats.TcpSegmentsReceived = tcpStats.SegmentsReceived;
            stats.TcpSegmentsRetransmitted = tcpStats.FailedConnectionAttempts; // Using FailedConnectionAttempts as proxy
            stats.TcpErrorsReceived = tcpStats.ErrorsReceived;

            // UDP Statistics
            var udpStats = properties.GetUdpIPv4Statistics();
            stats.UdpDatagramsSent = udpStats.DatagramsSent;
            stats.UdpDatagramsReceived = udpStats.DatagramsReceived;

            // ICMP Statistics
            var icmpStats = properties.GetIcmpV4Statistics();
            stats.IcmpMessagesSent = icmpStats.MessagesSent;
            stats.IcmpMessagesReceived = icmpStats.MessagesReceived;

            // IP Statistics
            var ipStats = properties.GetIPv4GlobalStatistics();
            stats.IpPacketsReceived = ipStats.ReceivedPackets;
            stats.IpPacketsDelivered = ipStats.ReceivedPacketsDelivered;
            stats.IpPacketsDiscarded = ipStats.ReceivedPacketsDiscarded;

            // Active connections
            stats.ActiveConnections = properties.GetActiveTcpConnections().Length;

            stats.Success = true;
        }
        catch (Exception ex)
        {
            stats.Success = false;
            stats.ErrorMessage = ex.Message;
        }

        return stats;
    }

    #endregion

    #region MAC Vendor Lookup

    private static readonly Dictionary<string, string> MacVendors = new()
    {
        { "00:00:5E", "IANA" },
        { "00:0C:29", "VMware" },
        { "00:50:56", "VMware" },
        { "00:1C:42", "Parallels" },
        { "08:00:27", "VirtualBox" },
        { "00:15:5D", "Hyper-V" },
        { "00:03:FF", "Microsoft" },
        { "3C:5A:B4", "Google" },
        { "00:1A:11", "Google" },
        { "F4:F5:D8", "Google" },
        { "00:17:88", "Philips" },
        { "B8:27:EB", "Raspberry Pi" },
        { "DC:A6:32", "Raspberry Pi" },
        { "E4:5F:01", "Raspberry Pi" },
        { "00:1E:C2", "Apple" },
        { "00:1F:F3", "Apple" },
        { "00:21:E9", "Apple" },
        { "00:22:41", "Apple" },
        { "00:23:12", "Apple" },
        { "00:23:32", "Apple" },
        { "00:23:6C", "Apple" },
        { "00:23:DF", "Apple" },
        { "00:24:36", "Apple" },
        { "00:25:00", "Apple" },
        { "00:25:4B", "Apple" },
        { "00:25:BC", "Apple" },
        { "00:26:08", "Apple" },
        { "00:26:4A", "Apple" },
        { "00:26:B0", "Apple" },
        { "00:26:BB", "Apple" },
        { "3C:15:C2", "Apple" },
        { "B8:E8:56", "Apple" },
        { "00:16:CB", "Apple" },
        { "04:0C:CE", "Apple" },
        { "98:01:A7", "Apple" },
        { "D4:9A:20", "Apple" },
        { "F0:B4:79", "Apple" },
        { "FC:25:3F", "Apple" },
        { "00:30:65", "Dell" },
        { "18:03:73", "Dell" },
        { "00:14:22", "Dell" },
        { "00:1D:09", "Dell" },
        { "00:1E:4F", "Dell" },
        { "00:21:70", "Dell" },
        { "00:22:19", "Dell" },
        { "00:24:E8", "Dell" },
        { "00:1A:A0", "Dell" },
        { "00:26:B9", "Dell" },
        { "F8:B1:56", "Dell" },
        { "00:1A:4D", "HP" },
        { "00:21:5A", "HP" },
        { "00:22:64", "HP" },
        { "00:24:81", "HP" },
        { "00:25:B3", "HP" },
        { "00:26:55", "HP" },
        { "2C:44:FD", "HP" },
        { "00:0A:F7", "Intel" },
        { "00:02:B3", "Intel" },
        { "00:03:47", "Intel" },
        { "00:04:23", "Intel" },
        { "00:0C:F1", "Intel" },
        { "00:0E:0C", "Intel" },
        { "00:11:11", "Intel" },
        { "00:12:F0", "Intel" },
        { "00:13:02", "Intel" },
        { "00:13:20", "Intel" },
        { "00:13:CE", "Intel" },
        { "00:13:E8", "Intel" },
        { "00:15:00", "Intel" },
        { "00:15:17", "Intel" },
        { "00:16:6F", "Intel" },
        { "00:16:76", "Intel" },
        { "00:16:EA", "Intel" },
        { "00:16:EB", "Intel" },
        { "00:17:35", "Intel" },
        { "00:18:DE", "Intel" },
        { "00:19:D1", "Intel" },
        { "00:19:D2", "Intel" },
        { "00:1B:21", "Intel" },
        { "00:1B:77", "Intel" },
        { "00:1C:BF", "Intel" },
        { "00:1C:C0", "Intel" },
        { "00:1D:E0", "Intel" },
        { "00:1D:E1", "Intel" },
        { "00:1E:64", "Intel" },
        { "00:1E:65", "Intel" },
        { "00:1E:67", "Intel" },
        { "00:1F:3B", "Intel" },
        { "00:1F:3C", "Intel" },
        { "00:20:E0", "Intel" },
        { "00:21:5C", "Intel" },
        { "00:21:5D", "Intel" },
        { "00:21:6A", "Intel" },
        { "00:21:6B", "Intel" },
        { "00:22:FA", "Intel" },
        { "00:22:FB", "Intel" },
        { "00:24:D6", "Intel" },
        { "00:24:D7", "Intel" },
        { "00:26:C6", "Intel" },
        { "00:26:C7", "Intel" },
        { "00:27:10", "Intel" },
        { "3C:97:0E", "Intel" },
        { "40:25:C2", "Intel" },
        { "48:51:B7", "Intel" },
        { "50:76:AF", "Intel" },
        { "58:94:6B", "Intel" },
        { "64:80:99", "Intel" },
        { "68:05:CA", "Intel" },
        { "6C:88:14", "Intel" },
        { "70:85:C2", "Intel" },
        { "78:92:9C", "Intel" },
        { "80:86:F2", "Intel" },
        { "88:53:2E", "Intel" },
        { "8C:EC:4B", "Intel" },
        { "94:65:9C", "Intel" },
        { "98:4F:EE", "Intel" },
        { "A0:36:9F", "Intel" },
        { "A4:4E:31", "Intel" },
        { "A4:C4:94", "Intel" },
        { "B4:E1:0F", "Intel" },
        { "C8:0A:A9", "Intel" },
        { "CC:3D:82", "Intel" },
        { "D4:3D:7E", "Intel" },
        { "DC:53:60", "Intel" },
        { "E8:6A:64", "Intel" },
        { "EC:0E:C4", "Intel" },
        { "F4:06:69", "Intel" },
        { "F8:16:54", "Intel" }
    };

    public static string LookupMacVendor(string macAddress)
    {
        macAddress = macAddress.Replace("-", ":").ToUpper();
        var prefix = macAddress.Length >= 8 ? macAddress[..8] : macAddress;

        return MacVendors.TryGetValue(prefix, out var vendor) ? vendor : "Unknown";
    }

    #endregion

    #region Packet Sender

    /// <summary>
    /// Send a TCP packet to a target host and port
    /// </summary>
    public static async Task<PacketSendResult> SendTcpPacketAsync(string host, int port, byte[] data, int timeout = 5000)
    {
        var result = new PacketSendResult
        {
            Protocol = "TCP",
            Host = host,
            Port = port,
            DataSize = data.Length
        };

        try
        {
            var stopwatch = Stopwatch.StartNew();

            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(timeout);

            if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
            {
                result.Success = false;
                result.ErrorMessage = "Connection timed out";
                return result;
            }

            await connectTask;
            result.ConnectionTime = stopwatch.ElapsedMilliseconds;

            using var stream = client.GetStream();
            stream.WriteTimeout = timeout;
            stream.ReadTimeout = timeout;

            // Send data
            await stream.WriteAsync(data);
            result.BytesSent = data.Length;

            // Try to receive response
            var buffer = new byte[4096];
            try
            {
                var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                var readTimeoutTask = Task.Delay(timeout);

                if (await Task.WhenAny(readTask, readTimeoutTask) == readTask)
                {
                    result.BytesReceived = await readTask;
                    result.ResponseData = buffer.Take(result.BytesReceived).ToArray();
                }
            }
            catch (IOException)
            {
                // No response or timeout - that's OK for some protocols
            }

            stopwatch.Stop();
            result.RoundtripTime = stopwatch.ElapsedMilliseconds;
            result.Success = true;
        }
        catch (SocketException ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Socket error: {ex.SocketErrorCode} - {ex.Message}";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Send a UDP packet to a target host and port
    /// </summary>
    public static async Task<PacketSendResult> SendUdpPacketAsync(string host, int port, byte[] data, int timeout = 5000)
    {
        var result = new PacketSendResult
        {
            Protocol = "UDP",
            Host = host,
            Port = port,
            DataSize = data.Length
        };

        try
        {
            var stopwatch = Stopwatch.StartNew();

            using var client = new UdpClient();
            client.Client.ReceiveTimeout = timeout;

            // Resolve host
            var addresses = await Dns.GetHostAddressesAsync(host);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (address == null)
            {
                result.Success = false;
                result.ErrorMessage = "Could not resolve host";
                return result;
            }

            var endpoint = new IPEndPoint(address, port);

            // Send data
            result.BytesSent = await client.SendAsync(data, data.Length, endpoint);
            result.ConnectionTime = stopwatch.ElapsedMilliseconds;

            // Try to receive response
            try
            {
                var receiveTask = client.ReceiveAsync();
                var timeoutTask = Task.Delay(timeout);

                if (await Task.WhenAny(receiveTask, timeoutTask) == receiveTask)
                {
                    var response = await receiveTask;
                    result.BytesReceived = response.Buffer.Length;
                    result.ResponseData = response.Buffer;
                }
            }
            catch (SocketException)
            {
                // No response - normal for UDP
            }

            stopwatch.Stop();
            result.RoundtripTime = stopwatch.ElapsedMilliseconds;
            result.Success = true;
        }
        catch (SocketException ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Socket error: {ex.SocketErrorCode} - {ex.Message}";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Send ICMP Echo (Ping) with custom data
    /// </summary>
    public static async Task<PacketSendResult> SendIcmpPacketAsync(string host, byte[] data, int timeout = 5000, int ttl = 128)
    {
        var result = new PacketSendResult
        {
            Protocol = "ICMP",
            Host = host,
            Port = 0,
            DataSize = data.Length
        };

        try
        {
            using var ping = new Ping();
            var options = new PingOptions { Ttl = ttl, DontFragment = true };

            var stopwatch = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(host, timeout, data, options);
            stopwatch.Stop();

            result.Success = reply.Status == IPStatus.Success;
            result.RoundtripTime = reply.RoundtripTime;
            result.BytesSent = data.Length;
            result.BytesReceived = reply.Buffer?.Length ?? 0;
            result.ResponseData = reply.Buffer;
            result.Ttl = reply.Options?.Ttl ?? 0;

            if (!result.Success)
            {
                result.ErrorMessage = reply.Status.ToString();
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Send multiple packets and get statistics
    /// </summary>
    public static async Task<PacketFloodResult> SendPacketFloodAsync(
        string protocol, string host, int port, byte[] data,
        int count, int delayMs = 100, int timeout = 5000,
        IProgress<PacketSendResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new PacketFloodResult
        {
            Protocol = protocol.ToUpper(),
            Host = host,
            Port = port,
            TotalPackets = count
        };

        var results = new List<PacketSendResult>();
        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PacketSendResult packetResult;

            switch (protocol.ToUpper())
            {
                case "TCP":
                    packetResult = await SendTcpPacketAsync(host, port, data, timeout);
                    break;
                case "UDP":
                    packetResult = await SendUdpPacketAsync(host, port, data, timeout);
                    break;
                case "ICMP":
                    packetResult = await SendIcmpPacketAsync(host, data, timeout);
                    break;
                default:
                    throw new ArgumentException($"Unknown protocol: {protocol}");
            }

            packetResult.SequenceNumber = i + 1;
            results.Add(packetResult);
            progress?.Report(packetResult);

            if (packetResult.Success)
            {
                result.SuccessCount++;
                result.TotalBytesSent += packetResult.BytesSent;
                result.TotalBytesReceived += packetResult.BytesReceived;
            }
            else
            {
                result.FailedCount++;
            }

            if (delayMs > 0 && i < count - 1)
            {
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        stopwatch.Stop();
        result.TotalTime = stopwatch.Elapsed.TotalMilliseconds;

        // Calculate statistics
        var successResults = results.Where(r => r.Success).ToList();
        if (successResults.Count > 0)
        {
            result.MinLatency = successResults.Min(r => r.RoundtripTime);
            result.MaxLatency = successResults.Max(r => r.RoundtripTime);
            result.AvgLatency = successResults.Average(r => r.RoundtripTime);
        }

        result.PacketLossPercent = count > 0 ? (double)result.FailedCount / count * 100 : 0;
        result.Success = result.SuccessCount > 0;

        return result;
    }

    /// <summary>
    /// Parse hex string to bytes
    /// </summary>
    public static byte[] ParseHexString(string hex)
    {
        hex = hex.Replace(" ", "").Replace("-", "").Replace(":", "");
        if (hex.Length % 2 != 0)
            throw new ArgumentException("Invalid hex string length");

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    /// <summary>
    /// Format bytes as hex string
    /// </summary>
    public static string FormatAsHex(byte[] data, bool spaced = true)
    {
        if (data == null || data.Length == 0) return "";
        return spaced
            ? BitConverter.ToString(data).Replace("-", " ")
            : BitConverter.ToString(data).Replace("-", "");
    }

    /// <summary>
    /// Format bytes as ASCII with dots for non-printable
    /// </summary>
    public static string FormatAsAscii(byte[] data)
    {
        if (data == null || data.Length == 0) return "";
        var sb = new StringBuilder();
        foreach (var b in data)
        {
            sb.Append(b >= 32 && b < 127 ? (char)b : '.');
        }
        return sb.ToString();
    }

    #endregion

    #region Helper Methods

    private static async Task<IPAddress?> ResolveHostAsync(string host)
    {
        try
        {
            if (IPAddress.TryParse(host, out var ip))
                return ip;

            var addresses = await Dns.GetHostAddressesAsync(host);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            return null;
        }
    }

    #endregion
}

#region Result Classes

public class PingResult
{
    public string Host { get; set; } = "";
    public bool Success { get; set; }
    public long RoundtripTime { get; set; }
    public int Ttl { get; set; }
    public string Status { get; set; } = "";
    public string Address { get; set; } = "";
    public int BufferSize { get; set; }
    public string? ErrorMessage { get; set; }
}

public class TracerouteHop
{
    public int HopNumber { get; set; }
    public string Address { get; set; } = "";
    public string? Hostname { get; set; }
    public long RoundtripTime { get; set; }
    public string Status { get; set; } = "";
}

public class DnsResult
{
    public string Hostname { get; set; } = "";
    public bool Success { get; set; }
    public long LookupTime { get; set; }
    public List<DnsAddress> Addresses { get; set; } = new();
    public List<string> Aliases { get; set; } = new();
    public string? CanonicalName { get; set; }
    public string? ErrorMessage { get; set; }
}

public class DnsAddress
{
    public string Address { get; set; } = "";
    public string AddressFamily { get; set; } = "";
}

public class PortScanResult
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool IsOpen { get; set; }
    public long ResponseTime { get; set; }
    public string? ServiceName { get; set; }
    public string? Status { get; set; }
}

public class LocalNetworkInfo
{
    public List<InterfaceAddress> LocalIPv4 { get; set; } = new();
    public List<InterfaceAddress> LocalIPv6 { get; set; } = new();
    public string? Gateway { get; set; }
    public List<string> DnsServers { get; set; } = new();
}

public class InterfaceAddress
{
    public string InterfaceName { get; set; } = "";
    public string Address { get; set; } = "";
    public string? SubnetMask { get; set; }
    public string? MacAddress { get; set; }
}

public class ArpEntry
{
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string Type { get; set; } = "";
}

public class SpeedTestResult
{
    public bool Success { get; set; }
    public long DownloadBytes { get; set; }
    public double DownloadTime { get; set; }
    public double DownloadSpeed { get; set; }
    public long UploadBytes { get; set; }
    public double UploadTime { get; set; }
    public double UploadSpeed { get; set; }
    public string? ErrorMessage { get; set; }
}

public class SpeedTestProgress
{
    public long BytesTransferred { get; set; }
    public double Speed { get; set; }
    public bool IsDownload { get; set; }
}

public class SubnetInfo
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string InputIP { get; set; } = "";
    public int CIDR { get; set; }
    public string SubnetMask { get; set; } = "";
    public string WildcardMask { get; set; } = "";
    public string NetworkAddress { get; set; } = "";
    public string BroadcastAddress { get; set; } = "";
    public string FirstUsable { get; set; } = "";
    public string LastUsable { get; set; } = "";
    public int TotalHosts { get; set; }
    public int UsableHosts { get; set; }
}

public class SslCertificateInfo
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string Hostname { get; set; } = "";
    public int Port { get; set; }
    public string? Subject { get; set; }
    public string? Issuer { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    public string? Thumbprint { get; set; }
    public string? SerialNumber { get; set; }
    public string? SignatureAlgorithm { get; set; }
    public string? SubjectAlternativeNames { get; set; }
    public string? Protocol { get; set; }
    public string? CipherAlgorithm { get; set; }
    public bool IsValid { get; set; }
    public bool IsExpired { get; set; }
    public int DaysUntilExpiry { get; set; }
}

public class HttpHeadersResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string Url { get; set; } = "";
    public int StatusCode { get; set; }
    public string? StatusDescription { get; set; }
    public long ResponseTime { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public bool HasHSTS { get; set; }
    public bool HasXFrameOptions { get; set; }
    public bool HasXContentTypeOptions { get; set; }
    public bool HasCSP { get; set; }
}

public class NetworkStatistics
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int TcpConnectionsEstablished { get; set; }
    public long TcpSegmentsSent { get; set; }
    public long TcpSegmentsReceived { get; set; }
    public long TcpSegmentsRetransmitted { get; set; }
    public long TcpErrorsReceived { get; set; }
    public long UdpDatagramsSent { get; set; }
    public long UdpDatagramsReceived { get; set; }
    public long IcmpMessagesSent { get; set; }
    public long IcmpMessagesReceived { get; set; }
    public long IpPacketsReceived { get; set; }
    public long IpPacketsDelivered { get; set; }
    public long IpPacketsDiscarded { get; set; }
    public int ActiveConnections { get; set; }
}

public class PacketSendResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string Protocol { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public int SequenceNumber { get; set; }
    public int DataSize { get; set; }
    public int BytesSent { get; set; }
    public int BytesReceived { get; set; }
    public byte[]? ResponseData { get; set; }
    public long ConnectionTime { get; set; }
    public long RoundtripTime { get; set; }
    public int Ttl { get; set; }
}

public class PacketFloodResult
{
    public bool Success { get; set; }
    public string Protocol { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public int TotalPackets { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public long TotalBytesSent { get; set; }
    public long TotalBytesReceived { get; set; }
    public double TotalTime { get; set; }
    public double MinLatency { get; set; }
    public double MaxLatency { get; set; }
    public double AvgLatency { get; set; }
    public double PacketLossPercent { get; set; }
}

#endregion

using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NetX.Core.Network;

public class ConnectionMonitor
{
    private static readonly Lazy<ConnectionMonitor> _instance = new(() => new ConnectionMonitor());
    public static ConnectionMonitor Instance => _instance.Value;

    // Reverse-DNS cache. A resolved entry holds the hostname; a failed/timed-out
    // entry holds the IP itself as a sentinel so it is never looked up again.
    private readonly ConcurrentDictionary<string, string> _dnsCache = new();
    private readonly ConcurrentDictionary<string, byte> _dnsPending = new();
    private readonly ConcurrentQueue<string> _dnsQueue = new();
    private int _dnsWorkerRunning; // 0 = idle, 1 = a worker task is draining the queue

    /// <summary>
    /// Raised (on a background thread) when a reverse-DNS name arrives for an IP.
    /// The UI subscribes to update the matching rows without blocking.
    /// </summary>
    public event Action<string, string>? HostnameResolved;

    public List<ConnectionInfo> GetActiveConnections()
    {
        var connections = new List<ConnectionInfo>();

        try
        {
            // Get TCP connections
            var tcpConnections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
            foreach (var conn in tcpConnections)
            {
                var info = new ConnectionInfo
                {
                    Protocol = "TCP",
                    LocalAddress = conn.LocalEndPoint.Address.ToString(),
                    LocalPort = conn.LocalEndPoint.Port,
                    RemoteAddress = conn.RemoteEndPoint.Address.ToString(),
                    RemotePort = conn.RemoteEndPoint.Port,
                    State = conn.State.ToString(),
                    Direction = DetermineDirection(conn.LocalEndPoint.Port, conn.RemoteEndPoint.Port),
                    Timestamp = DateTime.Now
                };

                // Try to resolve hostname
                info.RemoteHostname = ResolveHostname(info.RemoteAddress);

                connections.Add(info);
            }

            // Get TCP listeners (incoming)
            var tcpListeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            foreach (var listener in tcpListeners)
            {
                connections.Add(new ConnectionInfo
                {
                    Protocol = "TCP",
                    LocalAddress = listener.Address.ToString(),
                    LocalPort = listener.Port,
                    RemoteAddress = "*",
                    RemotePort = 0,
                    State = "Listening",
                    Direction = ConnectionDirection.Incoming,
                    Timestamp = DateTime.Now
                });
            }

            // Get UDP listeners
            var udpListeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners();
            foreach (var listener in udpListeners)
            {
                connections.Add(new ConnectionInfo
                {
                    Protocol = "UDP",
                    LocalAddress = listener.Address.ToString(),
                    LocalPort = listener.Port,
                    RemoteAddress = "*",
                    RemotePort = 0,
                    State = "Open",
                    // An open UDP port is not a connection and has no direction.
                    Direction = ConnectionDirection.Unknown,
                    Timestamp = DateTime.Now
                });
            }
        }
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine($"Error getting connections: {ex.Message}");
        }

        return connections;
    }

    /// <summary>
    /// The TCP connections one process owns, read straight from Windows' owner
    /// tables for IPv4 and IPv6. (It used to read the IPv4 table only and match
    /// rows by port numbers, so an app talking over IPv6 showed an empty list.)
    /// </summary>
    public List<ConnectionInfo> GetConnectionsByProcess(int processId)
    {
        var connections = new List<ConnectionInfo>();
        foreach (bool ipv6 in new[] { false, true })
        {
            try
            {
                ReadTcpOwnerTable(ipv6, (local, localPort, remote, remotePort, state, pid) =>
                {
                    if (pid != processId) return;
                    bool listening = state == 2; // MIB_TCP_STATE_LISTEN
                    var info = new ConnectionInfo
                    {
                        Protocol = "TCP",
                        ProcessId = processId,
                        LocalAddress = local.ToString(),
                        LocalPort = localPort,
                        RemoteAddress = listening ? "*" : remote.ToString(),
                        RemotePort = listening ? 0 : remotePort,
                        State = listening ? "Listening" : ((TcpState)state).ToString(),
                        Direction = listening ? ConnectionDirection.Incoming : DetermineDirection(localPort, remotePort),
                        Timestamp = DateTime.Now
                    };
                    if (!listening) info.RemoteHostname = ResolveHostname(info.RemoteAddress);
                    connections.Add(info);
                });
            }
            catch (Exception ex)
            {
                global::System.Diagnostics.Debug.WriteLine($"Error reading the {(ipv6 ? "IPv6" : "IPv4")} connection table: {ex.Message}");
            }
        }
        return connections;
    }

    /// <summary>Walks MIB_TCPTABLE_OWNER_PID (24-byte rows) or MIB_TCP6TABLE_OWNER_PID (56-byte rows).</summary>
    private static void ReadTcpOwnerTable(bool ipv6, Action<IPAddress, int, IPAddress, int, int, int> row)
    {
        int family = ipv6 ? 23 : 2; // AF_INET6 : AF_INET
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return;
        size += 4096; // the table can grow between the two calls

        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, true, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0) != 0) return;

            int count = Marshal.ReadInt32(table);
            int rowSize = ipv6 ? 56 : 24;
            var localBytes = new byte[ipv6 ? 16 : 4];
            var remoteBytes = new byte[ipv6 ? 16 : 4];
            static int Port(int raw) => (ushort)IPAddress.NetworkToHostOrder((short)raw);

            for (int i = 0; i < count; i++)
            {
                IntPtr p = table + 4 + i * rowSize;
                int state, localPort, remotePort, pid;
                if (ipv6)
                {
                    // ucLocalAddr[16], dwLocalScopeId, dwLocalPort, ucRemoteAddr[16], dwRemoteScopeId, dwRemotePort, dwState, dwOwningPid
                    Marshal.Copy(p, localBytes, 0, 16);
                    localPort = Port(Marshal.ReadInt32(p, 20));
                    Marshal.Copy(p + 24, remoteBytes, 0, 16);
                    remotePort = Port(Marshal.ReadInt32(p, 44));
                    state = Marshal.ReadInt32(p, 48);
                    pid = Marshal.ReadInt32(p, 52);
                }
                else
                {
                    // dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid
                    state = Marshal.ReadInt32(p, 0);
                    Marshal.Copy(p + 4, localBytes, 0, 4);
                    localPort = Port(Marshal.ReadInt32(p, 8));
                    Marshal.Copy(p + 12, remoteBytes, 0, 4);
                    remotePort = Port(Marshal.ReadInt32(p, 16));
                    pid = Marshal.ReadInt32(p, 20);
                }
                row(new IPAddress(localBytes), localPort, new IPAddress(remoteBytes), remotePort, state, pid);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private ConnectionDirection DetermineDirection(int localPort, int remotePort)
    {
        // Common server ports indicate incoming connections
        var serverPorts = new[] { 80, 443, 21, 22, 23, 25, 53, 110, 143, 993, 995, 3306, 5432, 1433, 3389, 8080, 8443 };

        if (serverPorts.Contains(localPort))
            return ConnectionDirection.Incoming;

        if (serverPorts.Contains(remotePort))
            return ConnectionDirection.Outgoing;

        // Ephemeral ports (high ports) are usually client-side
        if (localPort > 49152 && remotePort < 49152)
            return ConnectionDirection.Outgoing;

        if (remotePort > 49152 && localPort < 49152)
            return ConnectionDirection.Incoming;

        return ConnectionDirection.Unknown;
    }

    /// <summary>
    /// Returns a cached reverse-DNS name for the IP, or an empty string if it is
    /// not resolved yet. Never blocks the caller: unknown IPs are queued for a
    /// background lookup, and <see cref="HostnameResolved"/> fires when a name
    /// arrives. Failures/timeouts are cached so they are not retried every refresh.
    /// </summary>
    private string ResolveHostname(string ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress) || ipAddress == "*" || ipAddress == "0.0.0.0" || ipAddress == "::")
            return string.Empty;

        if (_dnsCache.TryGetValue(ipAddress, out var cached))
            return cached == ipAddress ? string.Empty : cached; // sentinel => no name

        // Not known yet — queue it once and return empty for now.
        if (_dnsPending.TryAdd(ipAddress, 0))
        {
            _dnsQueue.Enqueue(ipAddress);
            EnsureDnsWorker();
        }

        return string.Empty;
    }

    private void EnsureDnsWorker()
    {
        if (Interlocked.CompareExchange(ref _dnsWorkerRunning, 1, 0) == 0)
            _ = Task.Run(ProcessDnsQueueAsync);
    }

    private async Task ProcessDnsQueueAsync()
    {
        try
        {
            while (_dnsQueue.TryDequeue(out var ip))
            {
                string result = ip; // sentinel: assume no name unless a lookup succeeds
                try
                {
                    var lookup = Dns.GetHostEntryAsync(ip);
                    // Cap each lookup so one slow/unreachable IP can't stall the queue.
                    if (await Task.WhenAny(lookup, Task.Delay(2000)).ConfigureAwait(false) == lookup)
                    {
                        var name = (await lookup.ConfigureAwait(false)).HostName;
                        if (!string.IsNullOrEmpty(name)) result = name;
                    }
                    else
                    {
                        // Timed out — observe the abandoned task's exception if it ever faults.
                        _ = lookup.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                    }
                }
                catch { /* failed lookup -> keep sentinel */ }

                _dnsCache[ip] = result;
                _dnsPending.TryRemove(ip, out _);
                if (result != ip)
                    HostnameResolved?.Invoke(ip, result);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _dnsWorkerRunning, 0);
            // A producer may have enqueued between the empty check and the reset.
            if (!_dnsQueue.IsEmpty)
                EnsureDnsWorker();
        }
    }

    private List<TcpConnectionRow> GetExtendedTcpTable()
    {
        var rows = new List<TcpConnectionRow>();

        try
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);

            IntPtr tcpTablePtr = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(tcpTablePtr, ref size, true, 2, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                {
                    int rowCount = Marshal.ReadInt32(tcpTablePtr);
                    IntPtr rowPtr = tcpTablePtr + 4;

                    for (int i = 0; i < rowCount; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                        rows.Add(new TcpConnectionRow
                        {
                            LocalPort = (ushort)IPAddress.NetworkToHostOrder((short)row.localPort),
                            RemotePort = (ushort)IPAddress.NetworkToHostOrder((short)row.remotePort),
                            ProcessId = (int)row.owningPid
                        });
                        rowPtr += Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tcpTablePtr);
            }
        }
        catch { }

        return rows;
    }

    #region P/Invoke

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, TCP_TABLE_CLASS tblClass, uint reserved);

    private enum TCP_TABLE_CLASS
    {
        TCP_TABLE_BASIC_LISTENER,
        TCP_TABLE_BASIC_CONNECTIONS,
        TCP_TABLE_BASIC_ALL,
        TCP_TABLE_OWNER_PID_LISTENER,
        TCP_TABLE_OWNER_PID_CONNECTIONS,
        TCP_TABLE_OWNER_PID_ALL,
        TCP_TABLE_OWNER_MODULE_LISTENER,
        TCP_TABLE_OWNER_MODULE_CONNECTIONS,
        TCP_TABLE_OWNER_MODULE_ALL
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    private class TcpConnectionRow
    {
        public int LocalPort { get; set; }
        public int RemotePort { get; set; }
        public int ProcessId { get; set; }
    }

    #endregion
}

public class ConnectionInfo
{
    public string Protocol { get; set; } = string.Empty;
    public string LocalAddress { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = string.Empty;
    public int RemotePort { get; set; }
    public string RemoteHostname { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public ConnectionDirection Direction { get; set; }
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool IsBlocked { get; set; }
    public bool IsAllowed { get; set; } = true;

    public string DisplayAddress => !string.IsNullOrEmpty(RemoteHostname) && RemoteHostname != RemoteAddress
        ? $"{RemoteHostname} ({RemoteAddress})"
        : RemoteAddress;

    public string DisplayEndpoint => RemotePort > 0
        ? $"{DisplayAddress}:{RemotePort}"
        : DisplayAddress;
}

public enum ConnectionDirection
{
    Unknown,
    Incoming,
    Outgoing
}

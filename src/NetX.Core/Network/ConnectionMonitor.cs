using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NetX.Core.Network;

public class ConnectionMonitor
{
    private static readonly Lazy<ConnectionMonitor> _instance = new(() => new ConnectionMonitor());
    public static ConnectionMonitor Instance => _instance.Value;

    private readonly Dictionary<string, string> _dnsCache = new();
    private readonly object _lock = new();

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
                    Direction = ConnectionDirection.Incoming,
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

    public List<ConnectionInfo> GetConnectionsByProcess(int processId)
    {
        var allConnections = GetActiveConnections();
        var processConnections = new List<ConnectionInfo>();

        try
        {
            // Use netstat-like approach to get process connections
            var tcpTable = GetExtendedTcpTable();
            foreach (var row in tcpTable)
            {
                if (row.ProcessId == processId)
                {
                    var conn = allConnections.FirstOrDefault(c =>
                        c.LocalPort == row.LocalPort &&
                        c.RemotePort == row.RemotePort);

                    if (conn != null)
                    {
                        conn.ProcessId = processId;
                        processConnections.Add(conn);
                    }
                }
            }
        }
        catch
        {
            // Fallback: return all connections if we can't get process info
        }

        return processConnections;
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

    private string ResolveHostname(string ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress) || ipAddress == "*" || ipAddress == "0.0.0.0" || ipAddress == "::")
            return ipAddress;

        lock (_lock)
        {
            if (_dnsCache.TryGetValue(ipAddress, out var cached))
                return cached;
        }

        try
        {
            // Don't block for too long on DNS resolution
            var task = Task.Run(() =>
            {
                try
                {
                    var entry = Dns.GetHostEntry(ipAddress);
                    return entry.HostName;
                }
                catch
                {
                    return ipAddress;
                }
            });

            if (task.Wait(TimeSpan.FromMilliseconds(100)))
            {
                var hostname = task.Result;
                lock (_lock)
                {
                    _dnsCache[ipAddress] = hostname;
                }
                return hostname;
            }
        }
        catch { }

        return ipAddress;
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

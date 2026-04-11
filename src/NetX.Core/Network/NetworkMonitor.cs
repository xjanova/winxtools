using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NetX.Core.Network;

/// <summary>
/// Network monitoring service that tracks real bandwidth usage per process and per interface.
/// Uses WinDivert PacketEngine for accurate real-time packet-level measurement when available,
/// falls back to interface statistics when PacketEngine is not running.
/// </summary>
public class NetworkMonitor
{
    private static readonly Lazy<NetworkMonitor> _instance = new(() => new NetworkMonitor());
    public static NetworkMonitor Instance => _instance.Value;

    private readonly Dictionary<int, ProcessBandwidthTracker> _processTrackers = new();
    private readonly Dictionary<string, InterfaceStats> _interfaceStats = new();
    private readonly object _lock = new();
    private DateTime _lastUpdate = DateTime.MinValue;
    private long _totalBytesReceived = 0;
    private long _totalBytesSent = 0;
    private long _previousBytesReceived = 0;
    private long _previousBytesSent = 0;
    private long _currentDownloadSpeed = 0;
    private long _currentUploadSpeed = 0;
    private string? _selectedInterfaceId = null; // null = all interfaces

    // Caching for performance optimization
    private NetworkStats? _cachedStats = null;
    private DateTime _lastCacheTime = DateTime.MinValue;
    private const double CACHE_DURATION_SECONDS = 0.8; // Cache results for 0.8 seconds (faster updates with PacketEngine)

    // Process stats cache (separate from full stats)
    private List<ProcessNetworkStats>? _cachedProcessStats = null;
    private DateTime _lastProcessCacheTime = DateTime.MinValue;
    private const double PROCESS_CACHE_DURATION = 1.0; // Cache process stats for 1 second

    // Interface bandwidth cache
    private Dictionary<string, (long download, long upload, string name)>? _cachedInterfaceBandwidth = null;
    private DateTime _lastInterfaceCacheTime = DateTime.MinValue;
    private const double INTERFACE_CACHE_DURATION = 1.0; // Cache interface stats for 1 second

    // PacketEngine integration
    private bool _packetEngineStarted = false;
    private bool _packetEngineAvailable = false;

    public bool IsPacketEngineActive => _packetEngineStarted && PacketEngine.Instance.IsRunning;

    private NetworkMonitor()
    {
        InitializeInterfaces();
        TryStartPacketEngine();
    }

    /// <summary>
    /// Try to start the PacketEngine for accurate packet-level monitoring
    /// </summary>
    private void TryStartPacketEngine()
    {
        try
        {
            var engine = PacketEngine.Instance;
            _packetEngineAvailable = engine.IsDriverLoaded;

            if (_packetEngineAvailable)
            {
                _packetEngineStarted = engine.Start();
                if (_packetEngineStarted)
                {
                    Debug.WriteLine("PacketEngine started successfully - using real-time packet capture");
                }
                else
                {
                    Debug.WriteLine("PacketEngine failed to start - falling back to interface stats");
                }
            }
            else
            {
                Debug.WriteLine("WinDivert driver not available - using interface stats mode");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PacketEngine initialization error: {ex.Message}");
            _packetEngineAvailable = false;
            _packetEngineStarted = false;
        }
    }

    /// <summary>
    /// Manually start PacketEngine (requires admin)
    /// </summary>
    public bool StartPacketEngine()
    {
        if (_packetEngineStarted) return true;
        TryStartPacketEngine();
        return _packetEngineStarted;
    }

    /// <summary>
    /// Stop PacketEngine
    /// </summary>
    public void StopPacketEngine()
    {
        if (_packetEngineStarted)
        {
            PacketEngine.Instance.Stop();
            _packetEngineStarted = false;
        }
    }

    #region Interface Management

    /// <summary>
    /// Get all available network interfaces
    /// </summary>
    public List<NetworkInterfaceInfo> GetNetworkInterfaces()
    {
        var result = new List<NetworkInterfaceInfo>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var ni in interfaces)
            {
                try
                {
                    var stats = ni.GetIPStatistics();
                    var isActive = ni.OperationalStatus == OperationalStatus.Up;

                    result.Add(new NetworkInterfaceInfo
                    {
                        Id = ni.Id,
                        Name = ni.Name,
                        Description = ni.Description,
                        Type = ni.NetworkInterfaceType.ToString(),
                        Speed = ni.Speed,
                        IsActive = isActive,
                        MacAddress = ni.GetPhysicalAddress().ToString(),
                        BytesReceived = stats.BytesReceived,
                        BytesSent = stats.BytesSent
                    });
                }
                catch { }
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// Select which interface to monitor (null = all interfaces)
    /// </summary>
    public void SelectInterface(string? interfaceId)
    {
        lock (_lock)
        {
            _selectedInterfaceId = interfaceId;
            InitializeInterfaces();
        }
    }

    /// <summary>
    /// Get currently selected interface ID
    /// </summary>
    public string? SelectedInterfaceId => _selectedInterfaceId;

    private void InitializeInterfaces()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                          && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            _interfaceStats.Clear();
            _totalBytesReceived = 0;
            _totalBytesSent = 0;

            foreach (var ni in interfaces)
            {
                if (_selectedInterfaceId != null && ni.Id != _selectedInterfaceId)
                    continue;

                try
                {
                    var stats = ni.GetIPStatistics();
                    _interfaceStats[ni.Id] = new InterfaceStats
                    {
                        Name = ni.Name,
                        LastBytesReceived = stats.BytesReceived,
                        LastBytesSent = stats.BytesSent,
                        LastUpdate = DateTime.Now
                    };

                    _totalBytesReceived += stats.BytesReceived;
                    _totalBytesSent += stats.BytesSent;
                }
                catch { }
            }

            _previousBytesReceived = _totalBytesReceived;
            _previousBytesSent = _totalBytesSent;
        }
        catch { }
    }

    #endregion

    #region Real Bandwidth Measurement

    /// <summary>
    /// Get current network statistics with real bandwidth measurement.
    /// Uses PacketEngine for accurate per-process measurement when available,
    /// falls back to interface statistics otherwise.
    /// </summary>
    public NetworkStats GetCurrentStats()
    {
        var now = DateTime.Now;

        // Return cached stats if available and not expired
        lock (_lock)
        {
            if (_cachedStats != null && (now - _lastCacheTime).TotalSeconds < CACHE_DURATION_SECONDS)
            {
                return _cachedStats;
            }
        }

        var stats = new NetworkStats();
        var elapsed = (now - _lastUpdate).TotalSeconds;

        if (elapsed < 0.3) elapsed = 1; // Minimum for measurement

        try
        {
            // If PacketEngine is running, use its accurate data
            if (IsPacketEngineActive)
            {
                var engineStats = PacketEngine.Instance.GetStats();

                stats.TotalDownloadSpeed = engineStats.TotalDownloadSpeed;
                stats.TotalUploadSpeed = engineStats.TotalUploadSpeed;
                stats.TotalBytesReceived = engineStats.TotalBytesReceived;
                stats.TotalBytesSent = engineStats.TotalBytesSent;
                stats.ActiveProcessCount = engineStats.ActiveProcessCount;

                // Convert PacketEngine stats to our format
                stats.ProcessStats = engineStats.ProcessStats.Select(ps => new ProcessNetworkStats
                {
                    ProcessId = ps.ProcessId,
                    ProcessName = ps.ProcessName,
                    DownloadSpeed = ps.DownloadSpeed,
                    UploadSpeed = ps.UploadSpeed,
                    TotalDownloaded = ps.TotalBytesReceived,
                    TotalUploaded = ps.TotalBytesSent,
                    ConnectionCount = 1 // PacketEngine doesn't track connections
                }).ToList();

                stats.TotalConnections = stats.ProcessStats.Count;

                // Also update interface stats for interface view
                UpdateInterfaceStats(elapsed);
            }
            else
            {
                // Fallback: Update interface statistics and calculate speeds
                UpdateInterfaceStats(elapsed);

                stats.TotalDownloadSpeed = _currentDownloadSpeed;
                stats.TotalUploadSpeed = _currentUploadSpeed;
                stats.TotalBytesReceived = _totalBytesReceived;
                stats.TotalBytesSent = _totalBytesSent;

                // Get process stats using connection-based estimation
                stats.ProcessStats = GetProcessStatsInternal(elapsed);
                stats.TotalConnections = stats.ProcessStats.Sum(p => p.ConnectionCount);
                stats.ActiveProcessCount = stats.ProcessStats.Count;
            }

            _lastUpdate = now;

            // Cache the results
            lock (_lock)
            {
                _cachedStats = stats;
                _lastCacheTime = now;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting network stats: {ex.Message}");
        }

        return stats;
    }

    /// <summary>
    /// Get process stats with caching to reduce CPU usage
    /// </summary>
    private List<ProcessNetworkStats> GetProcessStatsInternal(double elapsed)
    {
        var now = DateTime.Now;

        // Return cached process stats if available
        lock (_lock)
        {
            if (_cachedProcessStats != null && (now - _lastProcessCacheTime).TotalSeconds < PROCESS_CACHE_DURATION)
            {
                return _cachedProcessStats;
            }
        }

        var result = new List<ProcessNetworkStats>();

        try
        {
            // Get TCP connections with process info
            var tcpConnections = GetExtendedTcpTable();
            var udpEndpoints = GetExtendedUdpTable();

            // Group connections by process
            var processConnections = new Dictionary<int, int>(); // pid -> connection count
            var processEstablished = new Dictionary<int, int>(); // pid -> established count

            foreach (var conn in tcpConnections)
            {
                if (!processConnections.ContainsKey(conn.ProcessId))
                {
                    processConnections[conn.ProcessId] = 0;
                    processEstablished[conn.ProcessId] = 0;
                }
                processConnections[conn.ProcessId]++;
                if (conn.State == TcpState.Established)
                    processEstablished[conn.ProcessId]++;
            }

            foreach (var endpoint in udpEndpoints)
            {
                if (!processConnections.ContainsKey(endpoint.ProcessId))
                {
                    processConnections[endpoint.ProcessId] = 0;
                    processEstablished[endpoint.ProcessId] = 0;
                }
                processConnections[endpoint.ProcessId]++;
            }

            // Calculate total established connections for bandwidth distribution
            var totalEstablished = processEstablished.Values.Sum();
            if (totalEstablished < 1) totalEstablished = 1;

            // Get process names (only once, cached)
            foreach (var kvp in processConnections)
            {
                var pid = kvp.Key;
                var connectionCount = kvp.Value;
                var established = processEstablished.GetValueOrDefault(pid, 0);

                try
                {
                    string processName;
                    lock (_lock)
                    {
                        if (_processTrackers.TryGetValue(pid, out var existingTracker))
                        {
                            processName = existingTracker.ProcessName;
                        }
                        else
                        {
                            using var process = Process.GetProcessById(pid);
                            processName = process.ProcessName;
                        }
                    }

                    // Distribute bandwidth based on established connections
                    var share = (double)Math.Max(1, established) / totalEstablished;
                    var downloadSpeed = _currentDownloadSpeed * share;
                    var uploadSpeed = _currentUploadSpeed * share;

                    // Track totals per process
                    lock (_lock)
                    {
                        if (!_processTrackers.ContainsKey(pid))
                        {
                            _processTrackers[pid] = new ProcessBandwidthTracker(pid, processName);
                        }
                        var tracker = _processTrackers[pid];
                        tracker.Update(downloadSpeed, uploadSpeed, elapsed);

                        result.Add(new ProcessNetworkStats
                        {
                            ProcessId = pid,
                            ProcessName = processName,
                            DownloadSpeed = downloadSpeed,
                            UploadSpeed = uploadSpeed,
                            ConnectionCount = connectionCount,
                            TotalDownloaded = tracker.TotalDownloaded,
                            TotalUploaded = tracker.TotalUploaded
                        });
                    }
                }
                catch
                {
                    // Process may have exited
                }
            }

            // Clean up old trackers
            lock (_lock)
            {
                var activeProcessIds = processConnections.Keys.ToHashSet();
                var toRemove = _processTrackers.Keys.Where(k => !activeProcessIds.Contains(k)).ToList();
                foreach (var pid in toRemove)
                {
                    _processTrackers.Remove(pid);
                }

                _cachedProcessStats = result;
                _lastProcessCacheTime = now;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting process stats: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Get stats for a specific process by ID
    /// </summary>
    public ProcessNetworkStats? GetProcessStats(int processId)
    {
        // If PacketEngine is active, get directly from it for most accurate data
        if (IsPacketEngineActive)
        {
            var engineStats = PacketEngine.Instance.GetProcessStats(processId);
            if (engineStats != null)
            {
                return new ProcessNetworkStats
                {
                    ProcessId = engineStats.ProcessId,
                    ProcessName = engineStats.ProcessName,
                    DownloadSpeed = engineStats.DownloadSpeed,
                    UploadSpeed = engineStats.UploadSpeed,
                    TotalDownloaded = engineStats.TotalBytesReceived,
                    TotalUploaded = engineStats.TotalBytesSent,
                    ConnectionCount = 1
                };
            }
        }

        var stats = GetCurrentStats();
        return stats.ProcessStats.FirstOrDefault(p => p.ProcessId == processId);
    }

    private void UpdateInterfaceStats(double elapsed)
    {
        long totalReceived = 0;
        long totalSent = 0;

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                          && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var ni in interfaces)
            {
                if (_selectedInterfaceId != null && ni.Id != _selectedInterfaceId)
                    continue;

                try
                {
                    var ipStats = ni.GetIPStatistics();
                    totalReceived += ipStats.BytesReceived;
                    totalSent += ipStats.BytesSent;

                    if (_interfaceStats.TryGetValue(ni.Id, out var prevStats))
                    {
                        var deltaReceived = ipStats.BytesReceived - prevStats.LastBytesReceived;
                        var deltaSent = ipStats.BytesSent - prevStats.LastBytesSent;

                        prevStats.DownloadSpeed = elapsed > 0 ? (long)(deltaReceived / elapsed) : 0;
                        prevStats.UploadSpeed = elapsed > 0 ? (long)(deltaSent / elapsed) : 0;
                        prevStats.LastBytesReceived = ipStats.BytesReceived;
                        prevStats.LastBytesSent = ipStats.BytesSent;
                        prevStats.LastUpdate = DateTime.Now;
                    }
                    else
                    {
                        _interfaceStats[ni.Id] = new InterfaceStats
                        {
                            Name = ni.Name,
                            LastBytesReceived = ipStats.BytesReceived,
                            LastBytesSent = ipStats.BytesSent,
                            LastUpdate = DateTime.Now
                        };
                    }
                }
                catch { }
            }

            // Calculate speeds (bytes per second) based on delta
            if (elapsed > 0 && _previousBytesReceived > 0)
            {
                _currentDownloadSpeed = (long)((totalReceived - _previousBytesReceived) / elapsed);
                _currentUploadSpeed = (long)((totalSent - _previousBytesSent) / elapsed);

                // Clamp to non-negative
                if (_currentDownloadSpeed < 0) _currentDownloadSpeed = 0;
                if (_currentUploadSpeed < 0) _currentUploadSpeed = 0;
            }

            _previousBytesReceived = totalReceived;
            _previousBytesSent = totalSent;
            _totalBytesReceived = totalReceived;
            _totalBytesSent = totalSent;
        }
        catch { }
    }

    #endregion

    /// <summary>
    /// Get all process network info for dashboard
    /// </summary>
    public List<ProcessNetworkInfo> GetAllProcessStats()
    {
        var result = new List<ProcessNetworkInfo>();
        var stats = GetCurrentStats();

        foreach (var ps in stats.ProcessStats)
        {
            result.Add(new ProcessNetworkInfo
            {
                ProcessId = ps.ProcessId,
                ProcessName = ps.ProcessName,
                DownloadSpeed = (long)ps.DownloadSpeed,
                UploadSpeed = (long)ps.UploadSpeed,
                TotalDownloaded = ps.TotalDownloaded,
                TotalUploaded = ps.TotalUploaded,
                Connections = new List<ConnectionInfo>()
            });
        }

        return result;
    }

    /// <summary>
    /// Get total bandwidth (download speed, upload speed) in bytes per second
    /// </summary>
    public (long download, long upload) GetTotalBandwidth()
    {
        return (_currentDownloadSpeed, _currentUploadSpeed);
    }

    /// <summary>
    /// Get total bytes transferred (received, sent)
    /// </summary>
    public (long received, long sent) GetTotalBytes()
    {
        return (_totalBytesReceived, _totalBytesSent);
    }

    /// <summary>
    /// Get per-interface bandwidth statistics (with caching for performance)
    /// </summary>
    public Dictionary<string, (long download, long upload, string name)> GetInterfaceBandwidth()
    {
        var now = DateTime.Now;

        // Return cached result if available
        lock (_lock)
        {
            if (_cachedInterfaceBandwidth != null && (now - _lastInterfaceCacheTime).TotalSeconds < INTERFACE_CACHE_DURATION)
            {
                return _cachedInterfaceBandwidth;
            }
        }

        var result = new Dictionary<string, (long, long, string)>();

        foreach (var kvp in _interfaceStats)
        {
            result[kvp.Key] = (kvp.Value.DownloadSpeed, kvp.Value.UploadSpeed, kvp.Value.Name);
        }

        // Cache the result
        lock (_lock)
        {
            _cachedInterfaceBandwidth = result;
            _lastInterfaceCacheTime = now;
        }

        return result;
    }

    #region Native Methods for TCP/UDP Tables

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, TcpTableClass tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, UdpTableClass tableClass, uint reserved);

    private enum TcpTableClass
    {
        TCP_TABLE_OWNER_PID_ALL = 5
    }

    private enum UdpTableClass
    {
        UDP_TABLE_OWNER_PID = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public int owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public uint localPort;
        public int owningPid;
    }

    private List<TcpConnectionInfo> GetExtendedTcpTable()
    {
        var connections = new List<TcpConnectionInfo>();

        int bufferSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, 2, TcpTableClass.TCP_TABLE_OWNER_PID_ALL, 0);

        IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);

        try
        {
            uint result = GetExtendedTcpTable(tcpTablePtr, ref bufferSize, true, 2, TcpTableClass.TCP_TABLE_OWNER_PID_ALL, 0);

            if (result == 0)
            {
                int numEntries = Marshal.ReadInt32(tcpTablePtr);
                IntPtr rowPtr = tcpTablePtr + 4;

                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);

                    connections.Add(new TcpConnectionInfo
                    {
                        ProcessId = row.owningPid,
                        State = (TcpState)row.state
                    });

                    rowPtr += Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(tcpTablePtr);
        }

        return connections;
    }

    private List<UdpEndpointInfo> GetExtendedUdpTable()
    {
        var endpoints = new List<UdpEndpointInfo>();

        int bufferSize = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref bufferSize, true, 2, UdpTableClass.UDP_TABLE_OWNER_PID, 0);

        IntPtr udpTablePtr = Marshal.AllocHGlobal(bufferSize);

        try
        {
            uint result = GetExtendedUdpTable(udpTablePtr, ref bufferSize, true, 2, UdpTableClass.UDP_TABLE_OWNER_PID, 0);

            if (result == 0)
            {
                int numEntries = Marshal.ReadInt32(udpTablePtr);
                IntPtr rowPtr = udpTablePtr + 4;

                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);

                    endpoints.Add(new UdpEndpointInfo
                    {
                        ProcessId = row.owningPid
                    });

                    rowPtr += Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(udpTablePtr);
        }

        return endpoints;
    }

    #endregion
}

#region Helper Classes

/// <summary>
/// Tracks bandwidth totals for individual processes
/// </summary>
internal class ProcessBandwidthTracker
{
    public int ProcessId { get; }
    public string ProcessName { get; }
    public long TotalDownloaded { get; private set; }
    public long TotalUploaded { get; private set; }

    public ProcessBandwidthTracker(int processId, string processName)
    {
        ProcessId = processId;
        ProcessName = processName;
    }

    public void Update(double downloadSpeed, double uploadSpeed, double elapsed)
    {
        // Accumulate bytes transferred
        TotalDownloaded += (long)(downloadSpeed * elapsed);
        TotalUploaded += (long)(uploadSpeed * elapsed);
    }
}

/// <summary>
/// Stats for individual network interface
/// </summary>
internal class InterfaceStats
{
    public string Name { get; set; } = "";
    public long LastBytesReceived { get; set; }
    public long LastBytesSent { get; set; }
    public DateTime LastUpdate { get; set; }
    public long DownloadSpeed { get; set; }
    public long UploadSpeed { get; set; }
}

#endregion

#region Data Models

public class NetworkStats
{
    public double TotalDownloadSpeed { get; set; }
    public double TotalUploadSpeed { get; set; }
    public int ActiveProcessCount { get; set; }
    public int TotalConnections { get; set; }
    public long TotalBytesReceived { get; set; }
    public long TotalBytesSent { get; set; }
    public List<ProcessNetworkStats> ProcessStats { get; set; } = new();
}

public class ProcessNetworkStats
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public double DownloadSpeed { get; set; }
    public double UploadSpeed { get; set; }
    public int ConnectionCount { get; set; }
    public long TotalDownloaded { get; set; }
    public long TotalUploaded { get; set; }
}

public class TcpConnectionInfo
{
    public int ProcessId { get; set; }
    public TcpState State { get; set; }
}

public class UdpEndpointInfo
{
    public int ProcessId { get; set; }
}

public enum TcpState
{
    Closed = 1,
    Listen = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12
}

/// <summary>
/// Network interface information
/// </summary>
public class NetworkInterfaceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "";
    public long Speed { get; set; }
    public bool IsActive { get; set; }
    public string MacAddress { get; set; } = "";
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
}

/// <summary>
/// Process network info for dashboard display
/// </summary>
public class ProcessNetworkInfo
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public long DownloadSpeed { get; set; }
    public long UploadSpeed { get; set; }
    public long TotalDownloaded { get; set; }
    public long TotalUploaded { get; set; }
    public List<ConnectionInfo> Connections { get; set; } = new();
}

// ConnectionInfo class is defined in ConnectionMonitor.cs

#endregion

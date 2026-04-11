using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using WindivertDotnet;

namespace NetX.Core.Network;

/// <summary>
/// High-performance packet capture and bandwidth control engine using WinDivert.
/// Captures ALL network packets at kernel level for accurate measurement and real throttling.
/// </summary>
public class PacketEngine : IDisposable
{
    private static PacketEngine? _instance;
    private static readonly object _lock = new();

    public static PacketEngine Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new PacketEngine();
                }
            }
            return _instance;
        }
    }

    private WinDivert? _divert;
    private Thread? _captureThread;
    private volatile bool _isRunning;
    private readonly ConcurrentDictionary<int, ProcessTrafficStats> _processStats = new();
    private readonly ConcurrentDictionary<ushort, int> _portToProcessMap = new();
    private readonly ConcurrentDictionary<string, BandwidthThrottle> _throttleRules = new();

    // Aggregate stats
    private long _totalBytesReceived;
    private long _totalBytesSent;
    private double _currentDownloadSpeed;
    private double _currentUploadSpeed;
    private DateTime _lastSpeedCalc = DateTime.Now;
    private long _lastBytesReceived;
    private long _lastBytesSent;

    // Port-to-process mapping refresh
    private DateTime _lastPortMapRefresh = DateTime.MinValue;
    private readonly TimeSpan _portMapRefreshInterval = TimeSpan.FromSeconds(2);

    public event Action<PacketInfo>? OnPacketCaptured;
    public event Action<string>? OnError;

    public bool IsRunning => _isRunning;
    public bool IsDriverLoaded { get; private set; }

    private PacketEngine()
    {
        // Check if WinDivert driver can be loaded
        IsDriverLoaded = CheckDriverAvailable();
    }

    private bool CheckDriverAvailable()
    {
        try
        {
            // Try to create a test filter to check if driver is available
            var testFilter = Filter.True;
            using var testDivert = new WinDivert(testFilter, WinDivertLayer.Network);
            testDivert.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOutbound(WinDivertAddress addr)
    {
        return (addr.Flags & WinDivertAddressFlag.Outbound) != 0;
    }

    /// <summary>
    /// Start capturing all network packets
    /// </summary>
    public bool Start()
    {
        if (_isRunning) return true;

        try
        {
            // Open WinDivert to capture ALL network packets (IP or IPv6)
            var filter = Filter.True;
            _divert = new WinDivert(filter, WinDivertLayer.Network);

            _isRunning = true;

            // Start capture thread
            _captureThread = new Thread(CaptureLoop)
            {
                Name = "PacketEngine-Capture",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _captureThread.Start();

            // Start speed calculation timer
            Task.Run(SpeedCalculationLoop);

            Debug.WriteLine("PacketEngine started successfully with WinDivert");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start PacketEngine: {ex.Message}");
            OnError?.Invoke($"Failed to start packet capture: {ex.Message}. Run as Administrator.");
            _isRunning = false;
            return false;
        }
    }

    /// <summary>
    /// Stop packet capture
    /// </summary>
    public void Stop()
    {
        _isRunning = false;

        try
        {
            _divert?.Dispose();
            _divert = null;
        }
        catch { }

        _captureThread?.Join(1000);
        _captureThread = null;

        Debug.WriteLine("PacketEngine stopped");
    }

    private void CaptureLoop()
    {
        using var packet = new WinDivertPacket();
        using var addr = new WinDivertAddress();

        while (_isRunning && _divert != null)
        {
            try
            {
                // Receive packet synchronously (blocking)
                _divert.Recv(packet, addr);

                if (packet.Length == 0) continue;

                // Process packet
                ProcessPacket(packet, addr);

                // Determine packet direction
                bool isOutbound = IsOutbound(addr);

                // Check throttling - first check global throttle, then per-process
                BandwidthThrottle? throttle = null;

                // Check global throttle first
                if (_throttleRules.TryGetValue("global", out var globalThrottle))
                {
                    throttle = globalThrottle;
                }
                else
                {
                    // Check per-process throttle
                    var throttleKey = GetThrottleKey(packet, addr);
                    if (throttleKey != null && _throttleRules.TryGetValue(throttleKey, out var processThrottle))
                    {
                        throttle = processThrottle;
                    }
                }

                if (throttle != null)
                {
                    // Apply throttling using token bucket algorithm
                    int delayMs = throttle.ConsumeAndGetDelay((uint)packet.Length, isOutbound);
                    if (delayMs > 0)
                    {
                        Thread.Sleep(delayMs);
                    }

                    // Check if should drop (for blocked direction)
                    if (throttle.ShouldDrop(isOutbound))
                    {
                        continue; // Don't reinject = drop packet
                    }
                }

                // Re-inject the packet
                _divert.Send(packet, addr);
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    Debug.WriteLine($"Packet capture error: {ex.Message}");
                }
            }
        }
    }

    private unsafe void ProcessPacket(WinDivertPacket packet, WinDivertAddress addr)
    {
        try
        {
            var parseResult = packet.GetParseResult();

            ushort srcPort = 0, dstPort = 0;
            byte protocol = 0;

            // Get ports from TCP or UDP headers
            if (parseResult.TcpHeader != null)
            {
                srcPort = parseResult.TcpHeader->SrcPort;
                dstPort = parseResult.TcpHeader->DstPort;
                protocol = 6; // TCP
            }
            else if (parseResult.UdpHeader != null)
            {
                srcPort = parseResult.UdpHeader->SrcPort;
                dstPort = parseResult.UdpHeader->DstPort;
                protocol = 17; // UDP
            }

            // Find process by port
            int processId = 0;
            bool outbound = IsOutbound(addr);
            ushort localPort = outbound ? srcPort : dstPort;

            if (localPort != 0)
            {
                RefreshPortToProcessMapIfNeeded();
                _portToProcessMap.TryGetValue(localPort, out processId);
            }

            // Update stats
            var packetLen = packet.Length;

            if (processId != 0)
            {
                var stats = _processStats.GetOrAdd(processId, pid => new ProcessTrafficStats { ProcessId = pid });

                if (outbound)
                {
                    Interlocked.Add(ref stats._bytesSent, packetLen);
                    Interlocked.Add(ref _totalBytesSent, packetLen);
                }
                else
                {
                    Interlocked.Add(ref stats._bytesReceived, packetLen);
                    Interlocked.Add(ref _totalBytesReceived, packetLen);
                }

                stats.LastActivity = DateTime.Now;
            }
            else
            {
                // Unknown process, still count total
                if (outbound)
                {
                    Interlocked.Add(ref _totalBytesSent, packetLen);
                }
                else
                {
                    Interlocked.Add(ref _totalBytesReceived, packetLen);
                }
            }

            // Fire event for interested listeners
            OnPacketCaptured?.Invoke(new PacketInfo
            {
                ProcessId = processId,
                Length = packetLen,
                IsOutbound = outbound,
                Protocol = protocol,
                LocalPort = localPort,
                RemotePort = outbound ? dstPort : srcPort,
                Timestamp = DateTime.Now
            });
        }
        catch { }
    }

    private unsafe string? GetThrottleKey(WinDivertPacket packet, WinDivertAddress addr)
    {
        try
        {
            var parseResult = packet.GetParseResult();

            ushort srcPort = 0, dstPort = 0;

            if (parseResult.TcpHeader != null)
            {
                srcPort = parseResult.TcpHeader->SrcPort;
                dstPort = parseResult.TcpHeader->DstPort;
            }
            else if (parseResult.UdpHeader != null)
            {
                srcPort = parseResult.UdpHeader->SrcPort;
                dstPort = parseResult.UdpHeader->DstPort;
            }
            else
            {
                return null;
            }

            bool outbound = IsOutbound(addr);
            ushort localPort = outbound ? srcPort : dstPort;

            RefreshPortToProcessMapIfNeeded();

            if (_portToProcessMap.TryGetValue(localPort, out int processId))
            {
                return $"process:{processId}";
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private void RefreshPortToProcessMapIfNeeded()
    {
        if ((DateTime.Now - _lastPortMapRefresh) < _portMapRefreshInterval)
            return;

        _lastPortMapRefresh = DateTime.Now;

        try
        {
            RefreshPortToProcessMap();
        }
        catch { }
    }

    private void RefreshPortToProcessMap()
    {
        _portToProcessMap.Clear();

        try
        {
            // Get TCP connections
            var tcpTable = GetTcpConnections();
            foreach (var conn in tcpTable)
            {
                _portToProcessMap[conn.LocalPort] = conn.ProcessId;
            }

            // Get UDP endpoints
            var udpTable = GetUdpEndpoints();
            foreach (var ep in udpTable)
            {
                _portToProcessMap[ep.LocalPort] = ep.ProcessId;
            }
        }
        catch { }
    }

    private async void SpeedCalculationLoop()
    {
        while (_isRunning)
        {
            try
            {
                await Task.Delay(1000);

                var now = DateTime.Now;
                var elapsed = (now - _lastSpeedCalc).TotalSeconds;
                if (elapsed <= 0) continue;

                // Calculate total speeds
                var bytesReceivedDelta = _totalBytesReceived - _lastBytesReceived;
                var bytesSentDelta = _totalBytesSent - _lastBytesSent;

                _currentDownloadSpeed = bytesReceivedDelta / elapsed;
                _currentUploadSpeed = bytesSentDelta / elapsed;

                _lastBytesReceived = _totalBytesReceived;
                _lastBytesSent = _totalBytesSent;
                _lastSpeedCalc = now;

                // Calculate per-process speeds
                foreach (var stats in _processStats.Values)
                {
                    stats.CalculateSpeed(elapsed);
                }

                // Clean up dead processes
                CleanupDeadProcesses();
            }
            catch { }
        }
    }

    private void CleanupDeadProcesses()
    {
        var cutoff = DateTime.Now.AddSeconds(-30);
        var toRemove = _processStats.Where(kv =>
            kv.Value.LastActivity < cutoff || !IsProcessAlive(kv.Key)).ToList();

        foreach (var kv in toRemove)
        {
            _processStats.TryRemove(kv.Key, out _);
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            var proc = Process.GetProcessById(processId);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    #region Public API

    /// <summary>
    /// Get current network stats
    /// </summary>
    public NetworkStatsSnapshot GetStats()
    {
        return new NetworkStatsSnapshot
        {
            TotalDownloadSpeed = _currentDownloadSpeed,
            TotalUploadSpeed = _currentUploadSpeed,
            TotalBytesReceived = _totalBytesReceived,
            TotalBytesSent = _totalBytesSent,
            ActiveProcessCount = _processStats.Count(kv => kv.Value.IsActive),
            ProcessStats = _processStats.Values
                .Where(s => s.IsActive)
                .Select(s => s.ToSnapshot())
                .ToList()
        };
    }

    /// <summary>
    /// Get stats for a specific process
    /// </summary>
    public ProcessStatsSnapshot? GetProcessStats(int processId)
    {
        if (_processStats.TryGetValue(processId, out var stats))
        {
            return stats.ToSnapshot();
        }
        return null;
    }

    /// <summary>
    /// Get stats for a process by name
    /// </summary>
    public ProcessStatsSnapshot? GetProcessStats(string processName)
    {
        var cleanName = processName.Replace(".exe", "").ToLowerInvariant();

        foreach (var kv in _processStats)
        {
            try
            {
                var proc = Process.GetProcessById(kv.Key);
                if (proc.ProcessName.ToLowerInvariant() == cleanName)
                {
                    return kv.Value.ToSnapshot();
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Set bandwidth throttle for a process
    /// </summary>
    public void SetThrottle(int processId, long downloadBytesPerSec, long uploadBytesPerSec)
    {
        var key = $"process:{processId}";
        _throttleRules[key] = new BandwidthThrottle
        {
            ProcessId = processId,
            DownloadLimitBps = downloadBytesPerSec,
            UploadLimitBps = uploadBytesPerSec
        };

        Debug.WriteLine($"Throttle set for PID {processId}: {downloadBytesPerSec / 1024} KB/s down, {uploadBytesPerSec / 1024} KB/s up");
    }

    /// <summary>
    /// Set bandwidth throttle for a process by name
    /// </summary>
    public void SetThrottle(string processName, long downloadBytesPerSec, long uploadBytesPerSec)
    {
        var cleanName = processName.Replace(".exe", "");
        var processes = Process.GetProcessesByName(cleanName);

        foreach (var proc in processes)
        {
            SetThrottle(proc.Id, downloadBytesPerSec, uploadBytesPerSec);
        }
    }

    /// <summary>
    /// Remove throttle for a process
    /// </summary>
    public void RemoveThrottle(int processId)
    {
        var key = $"process:{processId}";
        _throttleRules.TryRemove(key, out _);
    }

    /// <summary>
    /// Block a process completely
    /// </summary>
    public void BlockProcess(int processId)
    {
        SetThrottle(processId, 0, 0);
    }

    /// <summary>
    /// Set global bandwidth throttle for all traffic (0 = no limit)
    /// </summary>
    public void SetGlobalThrottle(long downloadBytesPerSec, long uploadBytesPerSec)
    {
        if (downloadBytesPerSec == 0 && uploadBytesPerSec == 0)
        {
            // Remove global throttle
            _throttleRules.TryRemove("global", out _);
            Debug.WriteLine("Global throttle removed");
        }
        else
        {
            _throttleRules["global"] = new BandwidthThrottle
            {
                ProcessId = 0, // 0 = global
                DownloadLimitBps = downloadBytesPerSec,
                UploadLimitBps = uploadBytesPerSec
            };
            Debug.WriteLine($"Global throttle set: {downloadBytesPerSec / 1024} KB/s down, {uploadBytesPerSec / 1024} KB/s up");
        }
    }

    /// <summary>
    /// Remove global throttle
    /// </summary>
    public void RemoveGlobalThrottle()
    {
        _throttleRules.TryRemove("global", out _);
    }

    #endregion

    #region TCP/UDP Connection Helpers

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public int OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint LocalAddr;
        public uint LocalPort;
        public int OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, int reserved);

    private List<(ushort LocalPort, ushort RemotePort, int ProcessId)> GetTcpConnections()
    {
        var result = new List<(ushort, ushort, int)>();

        int bufferSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, 2, 5, 0);

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (GetExtendedTcpTable(buffer, ref bufferSize, false, 2, 5, 0) == 0)
            {
                int count = Marshal.ReadInt32(buffer);
                var rowPtr = buffer + 4;

                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    ushort localPort = (ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort);
                    ushort remotePort = (ushort)IPAddress.NetworkToHostOrder((short)row.RemotePort);

                    result.Add((localPort, remotePort, row.OwningPid));
                    rowPtr += Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private List<(ushort LocalPort, int ProcessId)> GetUdpEndpoints()
    {
        var result = new List<(ushort, int)>();

        int bufferSize = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref bufferSize, false, 2, 2, 0);

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (GetExtendedUdpTable(buffer, ref bufferSize, false, 2, 2, 0) == 0)
            {
                int count = Marshal.ReadInt32(buffer);
                var rowPtr = buffer + 4;

                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                    ushort localPort = (ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort);

                    result.Add((localPort, row.OwningPid));
                    rowPtr += Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    #endregion

    public void Dispose()
    {
        Stop();
        _instance = null;
    }
}

/// <summary>
/// Traffic stats for a single process
/// </summary>
public class ProcessTrafficStats
{
    public int ProcessId { get; set; }
    public long _bytesReceived;
    public long _bytesSent;
    public double DownloadSpeed { get; private set; }
    public double UploadSpeed { get; private set; }
    public DateTime LastActivity { get; set; } = DateTime.Now;

    private long _lastBytesReceived;
    private long _lastBytesSent;

    public bool IsActive => (DateTime.Now - LastActivity).TotalSeconds < 10;

    public void CalculateSpeed(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0) return;

        var receivedDelta = _bytesReceived - _lastBytesReceived;
        var sentDelta = _bytesSent - _lastBytesSent;

        DownloadSpeed = receivedDelta / elapsedSeconds;
        UploadSpeed = sentDelta / elapsedSeconds;

        _lastBytesReceived = _bytesReceived;
        _lastBytesSent = _bytesSent;
    }

    public ProcessStatsSnapshot ToSnapshot()
    {
        string processName = "";
        try
        {
            processName = Process.GetProcessById(ProcessId).ProcessName;
        }
        catch { }

        return new ProcessStatsSnapshot
        {
            ProcessId = ProcessId,
            ProcessName = processName,
            DownloadSpeed = DownloadSpeed,
            UploadSpeed = UploadSpeed,
            TotalBytesReceived = _bytesReceived,
            TotalBytesSent = _bytesSent,
            LastActivity = LastActivity
        };
    }
}

/// <summary>
/// Bandwidth throttle configuration using Token Bucket Algorithm for accurate rate limiting.
/// Separate buckets for download and upload with smooth packet pacing.
/// </summary>
public class BandwidthThrottle
{
    public int ProcessId { get; set; }
    public long DownloadLimitBps { get; set; }  // 0 = no limit, >0 = limit in bytes/sec
    public long UploadLimitBps { get; set; }    // 0 = no limit, >0 = limit in bytes/sec

    // Token bucket state for download
    private double _downloadTokens;
    private long _downloadLastRefill;
    private readonly object _downloadLock = new();

    // Token bucket state for upload
    private double _uploadTokens;
    private long _uploadLastRefill;
    private readonly object _uploadLock = new();

    // Bucket size = 1 second worth of tokens (allows small bursts)
    private const double BUCKET_SIZE_MULTIPLIER = 1.0;
    // Minimum tokens to allow (prevents starvation for small packets)
    private const double MIN_TOKENS = 1500; // ~1 MTU

    public BandwidthThrottle()
    {
        var now = Stopwatch.GetTimestamp();
        _downloadLastRefill = now;
        _uploadLastRefill = now;
        _downloadTokens = 0;
        _uploadTokens = 0;
    }

    /// <summary>
    /// Try to consume tokens for a packet. Returns delay in ms if should wait, 0 if can proceed.
    /// Uses Token Bucket algorithm for accurate rate limiting.
    /// 0 = no limit, >0 = limit in bytes/sec (1 B/s = effectively blocked)
    /// </summary>
    public int ConsumeAndGetDelay(uint packetSize, bool isOutbound)
    {
        if (isOutbound)
        {
            if (UploadLimitBps <= 0) return 0; // No limit
            return ConsumeTokens(ref _uploadTokens, ref _uploadLastRefill, _uploadLock,
                                  UploadLimitBps, packetSize);
        }
        else
        {
            if (DownloadLimitBps <= 0) return 0; // No limit
            return ConsumeTokens(ref _downloadTokens, ref _downloadLastRefill, _downloadLock,
                                  DownloadLimitBps, packetSize);
        }
    }

    private int ConsumeTokens(ref double tokens, ref long lastRefill, object lockObj,
                               long limitBps, uint packetSize)
    {
        lock (lockObj)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedTicks = now - lastRefill;
            var elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;

            // Refill tokens based on elapsed time
            var newTokens = elapsedSeconds * limitBps;
            var maxTokens = Math.Max(MIN_TOKENS, limitBps * BUCKET_SIZE_MULTIPLIER);
            tokens = Math.Min(maxTokens, tokens + newTokens);
            lastRefill = now;

            // Try to consume tokens for this packet
            if (tokens >= packetSize)
            {
                tokens -= packetSize;
                return 0; // No delay needed
            }

            // Not enough tokens - calculate delay needed
            double tokensNeeded = packetSize - tokens;
            double delaySeconds = tokensNeeded / limitBps;
            int delayMs = (int)(delaySeconds * 1000);

            // Consume whatever tokens we have (packet will be delayed)
            tokens = 0;

            // Cap delay to prevent extreme waits (max 500ms)
            return Math.Min(500, Math.Max(1, delayMs));
        }
    }

    /// <summary>
    /// Check if packet should be delayed based on direction (legacy compatibility)
    /// </summary>
    public bool ShouldDelay(uint packetSize, bool isOutbound)
    {
        // Always return false - we handle delay in ConsumeAndGetDelay
        // This is kept for API compatibility
        return ConsumeAndGetDelay(packetSize, isOutbound) > 0;
    }

    /// <summary>
    /// Get delay in milliseconds based on direction (legacy compatibility)
    /// Note: Call ConsumeAndGetDelay instead for accurate results
    /// </summary>
    public int GetDelayMs(uint packetSize, bool isOutbound)
    {
        long limit = isOutbound ? UploadLimitBps : DownloadLimitBps;
        if (limit <= 0) return 0;

        // Calculate delay based on packet size and limit
        double delaySeconds = (double)packetSize / limit;
        int delayMs = (int)(delaySeconds * 1000);

        // Cap delay to prevent extreme waits
        return Math.Min(500, Math.Max(1, delayMs));
    }

    /// <summary>
    /// Check if packet should be dropped (not used - blocking done via extreme throttling)
    /// </summary>
    public bool ShouldDrop(bool isOutbound)
    {
        return false;
    }
}

/// <summary>
/// Captured packet info
/// </summary>
public struct PacketInfo
{
    public int ProcessId;
    public int Length;
    public bool IsOutbound;
    public byte Protocol;
    public ushort LocalPort;
    public ushort RemotePort;
    public DateTime Timestamp;
}

/// <summary>
/// Snapshot of network stats
/// </summary>
public class NetworkStatsSnapshot
{
    public double TotalDownloadSpeed { get; set; }
    public double TotalUploadSpeed { get; set; }
    public long TotalBytesReceived { get; set; }
    public long TotalBytesSent { get; set; }
    public int ActiveProcessCount { get; set; }
    public List<ProcessStatsSnapshot> ProcessStats { get; set; } = new();
}

/// <summary>
/// Snapshot of process stats
/// </summary>
public class ProcessStatsSnapshot
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public double DownloadSpeed { get; set; }
    public double UploadSpeed { get; set; }
    public long TotalBytesReceived { get; set; }
    public long TotalBytesSent { get; set; }
    public DateTime LastActivity { get; set; }
}

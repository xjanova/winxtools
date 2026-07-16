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
    private Thread[] _captureThreads = [];
    private Thread[] _reinjectThreads = [];
    private volatile bool _isRunning;
    private readonly ConcurrentDictionary<int, ProcessTrafficStats> _processStats = new();
    private readonly ConcurrentDictionary<ushort, int> _portToProcessMap = new();
    private readonly ConcurrentDictionary<string, BandwidthThrottle> _throttleRules = new();

    // Packet queue for multi-thread throttling (capture workers → reinject workers)
    private readonly ConcurrentQueue<QueuedPacket> _throttledQueue = new();
    private int _queueCount;
    private const int MAX_QUEUE_PACKETS = 16384;

    // Aggregate stats
    private long _totalBytesReceived;
    private long _totalBytesSent;
    private double _currentDownloadSpeed;
    private double _currentUploadSpeed;
    private DateTime _lastSpeedCalc = DateTime.Now;
    private long _lastBytesReceived;
    private long _lastBytesSent;

    // Port-to-process mapping refresh (with thundering-herd prevention)
    private DateTime _lastPortMapRefresh = DateTime.MinValue;
    private readonly TimeSpan _portMapRefreshInterval = TimeSpan.FromSeconds(2);
    private int _portMapRefreshing; // 0 = idle, 1 = refreshing (atomic flag)

    public event Action<PacketInfo>? OnPacketCaptured;
    public event Action<string>? OnError;

    public bool IsRunning => _isRunning;
    public bool IsDriverLoaded { get; private set; }

    /// <summary>
    /// Number of capture worker threads (scales with CPU cores)
    /// </summary>
    public int CaptureWorkerCount { get; private set; }

    /// <summary>
    /// Number of reinject worker threads (scales with CPU cores)
    /// </summary>
    public int ReinjectWorkerCount { get; private set; }

    /// <summary>
    /// Current throttle queue depth (for monitoring)
    /// </summary>
    public int QueueDepth => _queueCount;

    /// <summary>
    /// True if any process/global throttle rule is active. Used so the packet
    /// monitor can stop the engine on close without breaking bandwidth limits.
    /// </summary>
    public bool HasThrottleRules => !_throttleRules.IsEmpty;

    private PacketEngine()
    {
        // Check if WinDivert driver can be loaded
        IsDriverLoaded = CheckDriverAvailable();
    }

    private bool CheckDriverAvailable()
    {
        try
        {
            // Use Reflect layer to verify driver availability without intercepting any network traffic
            using var testDivert = new WinDivert(Filter.True, WinDivertLayer.Reflect);
            return true; // Driver loaded successfully
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
    /// <summary>
    /// Calculate optimal capture thread count based on CPU cores.
    /// Scales from 1 thread (2 cores) up to 8 threads (16+ cores).
    /// </summary>
    private static int CalcCaptureWorkers()
    {
        int cores = Environment.ProcessorCount;
        return cores switch
        {
            <= 2 => 1,
            <= 4 => 2,
            <= 8 => Math.Max(2, cores / 2),
            <= 16 => Math.Max(4, cores / 2),
            _ => Math.Min(cores / 2, 16)    // 16 workers max
        };
    }

    /// <summary>
    /// Calculate optimal reinject thread count.
    /// Fewer needed because they spend most time sleeping (pacing).
    /// </summary>
    private static int CalcReinjectWorkers()
    {
        int cores = Environment.ProcessorCount;
        return cores switch
        {
            <= 4 => 1,
            <= 8 => 2,
            <= 16 => Math.Max(2, cores / 4),
            _ => Math.Min(cores / 4, 8)     // 8 workers max
        };
    }

    public bool Start()
    {
        if (_isRunning) return true;

        try
        {
            // Open WinDivert to capture ALL network packets (IP or IPv6)
            var filter = Filter.True;
            _divert = new WinDivert(filter, WinDivertLayer.Network);

            // Increase WinDivert internal buffer for multi-threaded burst handling
            try
            {
                _divert.QueueLength = 16384;
                _divert.QueueTime = TimeSpan.FromMilliseconds(4000);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not set WinDivert queue params: {ex.Message}");
            }

            _isRunning = true;

            // Calculate worker counts based on CPU cores
            CaptureWorkerCount = CalcCaptureWorkers();
            ReinjectWorkerCount = CalcReinjectWorkers();

            // Start capture worker pool
            // Each worker independently calls WinDivert.Recv() — the driver distributes
            // packets across all waiting threads automatically (kernel-level load balancing)
            _captureThreads = new Thread[CaptureWorkerCount];
            for (int i = 0; i < CaptureWorkerCount; i++)
            {
                int workerId = i;
                _captureThreads[i] = new Thread(() => CaptureLoop(workerId))
                {
                    Name = $"PacketEngine-Capture-{i}",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _captureThreads[i].Start();
            }

            // Start reinject worker pool
            // Multiple reinject workers drain the throttled queue in parallel
            _reinjectThreads = new Thread[ReinjectWorkerCount];
            for (int i = 0; i < ReinjectWorkerCount; i++)
            {
                int workerId = i;
                _reinjectThreads[i] = new Thread(() => ReinjectLoop(workerId))
                {
                    Name = $"PacketEngine-Reinject-{i}",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _reinjectThreads[i].Start();
            }

            // Start speed calculation in background
            _ = Task.Run(SpeedCalculationLoop);

            Debug.WriteLine($"PacketEngine started: {CaptureWorkerCount} capture + {ReinjectWorkerCount} reinject workers ({Environment.ProcessorCount} CPU cores)");
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
        catch (Exception ex)
        {
            Debug.WriteLine($"Error disposing WinDivert: {ex.Message}");
        }

        // Join all capture workers
        foreach (var t in _captureThreads)
        {
            try { if (t.IsAlive) t.Join(2000); } catch { }
        }
        _captureThreads = [];

        // Join all reinject workers
        foreach (var t in _reinjectThreads)
        {
            try { if (t.IsAlive) t.Join(2000); } catch { }
        }
        _reinjectThreads = [];

        // Drain and dispose queued packets
        while (_throttledQueue.TryDequeue(out var queued))
        {
            queued.Dispose();
        }
        _queueCount = 0;

        Debug.WriteLine("PacketEngine stopped");
    }

    /// <summary>
    /// Capture worker: receives packets, processes stats, and classifies.
    /// Multiple instances run in parallel — WinDivert distributes packets across all workers.
    /// NEVER sleeps — packets that need throttling are queued for reinject workers.
    /// </summary>
    private void CaptureLoop(int workerId)
    {
        using var packet = new WinDivertPacket();
        using var addr = new WinDivertAddress();

        while (_isRunning && _divert != null)
        {
            try
            {
                // Receive packet synchronously (blocking on WinDivert, NOT on our logic)
                _divert.Recv(packet, addr);

                if (packet.Length == 0) continue;

                // Process stats (always, regardless of throttle)
                ProcessPacket(packet, addr);

                // Determine packet direction
                bool isOutbound = IsOutbound(addr);

                // Check throttling - global first, then per-process
                BandwidthThrottle? throttle = null;

                if (_throttleRules.TryGetValue("global", out var globalThrottle))
                {
                    throttle = globalThrottle;
                }
                else
                {
                    var throttleKey = GetThrottleKey(packet, addr);
                    if (throttleKey != null && _throttleRules.TryGetValue(throttleKey, out var processThrottle))
                    {
                        throttle = processThrottle;
                    }
                }

                // No throttle active → reinject immediately (fast path, zero latency)
                if (throttle == null)
                {
                    _divert.Send(packet, addr);
                    continue;
                }

                // Check if direction is BLOCKED → drop immediately (don't waste queue space)
                if (throttle.ShouldDrop(isOutbound))
                {
                    continue; // Packet dropped
                }

                // Throttle active → try Token Bucket
                int delayMs = throttle.ConsumeAndGetDelay((uint)packet.Length, isOutbound);

                if (delayMs == 0)
                {
                    // Enough tokens → pass through immediately (no queue overhead)
                    _divert.Send(packet, addr);
                    continue;
                }

                if (delayMs < 0)
                {
                    // Blocked by token bucket → drop
                    continue;
                }

                // Needs delay → clone packet to queue for reinject thread
                // This way the capture thread NEVER blocks
                if (_queueCount < MAX_QUEUE_PACKETS)
                {
                    var queued = new QueuedPacket(packet, addr, isOutbound, delayMs);
                    _throttledQueue.Enqueue(queued);
                    Interlocked.Increment(ref _queueCount);
                }
                // else: queue full → drop packet (natural backpressure)
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

    /// <summary>
    /// Reinject worker: dequeues throttled packets and paces them using Token Bucket delays.
    /// Multiple instances drain the queue in parallel for higher throughput.
    /// Only reinject workers sleep — capture workers run at full speed.
    /// </summary>
    private void ReinjectLoop(int workerId)
    {
        while (_isRunning)
        {
            try
            {
                if (_throttledQueue.TryDequeue(out var queued))
                {
                    Interlocked.Decrement(ref _queueCount);

                    using (queued)
                    {
                        // Apply the delay calculated by capture thread's Token Bucket
                        if (queued.DelayMs > 0)
                        {
                            Thread.Sleep(queued.DelayMs);
                        }

                        // Reinject the packet (WinDivert Send is thread-safe)
                        if (_isRunning && _divert != null)
                        {
                            _divert.Send(queued.Packet, queued.Address);
                        }
                    }
                }
                else
                {
                    // No packets waiting → yield to avoid busy-spinning
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    Debug.WriteLine($"Reinject error: {ex.Message}");
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
                var stats = _processStats.GetOrAdd(processId, pid =>
                {
                    string name = "";
                    try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { }
                    return new ProcessTrafficStats { ProcessId = pid, ProcessName = name };
                });

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

            // Fire event for interested listeners. IP/flag extraction (which
            // allocates) only runs when something is actually subscribed — the
            // throttle-only path stays allocation-free.
            var handler = OnPacketCaptured;
            if (handler != null)
            {
                string? srcAddr = null, dstAddr = null, tcpFlags = null;
                try
                {
                    if (parseResult.IPV4Header != null)
                    {
                        srcAddr = parseResult.IPV4Header->SrcAddr.ToString();
                        dstAddr = parseResult.IPV4Header->DstAddr.ToString();
                    }
                    else if (parseResult.IPV6Header != null)
                    {
                        srcAddr = parseResult.IPV6Header->SrcAddr.ToString();
                        dstAddr = parseResult.IPV6Header->DstAddr.ToString();
                    }

                    if (parseResult.TcpHeader != null)
                    {
                        var t = parseResult.TcpHeader;
                        var flags = new List<string>(6);
                        if (t->Syn) flags.Add("SYN");
                        if (t->Ack) flags.Add("ACK");
                        if (t->Psh) flags.Add("PSH");
                        if (t->Fin) flags.Add("FIN");
                        if (t->Rst) flags.Add("RST");
                        if (t->Urg) flags.Add("URG");
                        tcpFlags = string.Join(",", flags);
                    }
                }
                catch { }

                handler(new PacketInfo
                {
                    ProcessId = processId,
                    Length = packetLen,
                    IsOutbound = outbound,
                    Protocol = protocol,
                    LocalPort = localPort,
                    RemotePort = outbound ? dstPort : srcPort,
                    SourceAddress = srcAddr,
                    DestAddress = dstAddr,
                    TcpFlags = tcpFlags,
                    Timestamp = DateTime.Now
                });
            }
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

        // Prevent thundering herd: only one worker refreshes at a time
        if (Interlocked.CompareExchange(ref _portMapRefreshing, 1, 0) != 0)
            return; // Another worker is already refreshing

        try
        {
            _lastPortMapRefresh = DateTime.Now;
            RefreshPortToProcessMap();
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _portMapRefreshing, 0);
        }
    }

    private void RefreshPortToProcessMap()
    {
        try
        {
            // Build new map first, then swap atomically — never leave the map empty
            var newMap = new ConcurrentDictionary<ushort, int>();

            // Get TCP connections
            var tcpTable = GetTcpConnections();
            foreach (var conn in tcpTable)
            {
                newMap[conn.LocalPort] = conn.ProcessId;
            }

            // Get UDP endpoints
            var udpTable = GetUdpEndpoints();
            foreach (var ep in udpTable)
            {
                newMap[ep.LocalPort] = ep.ProcessId;
            }

            // Merge into existing map (don't clear — keep stale entries until overwritten)
            // This prevents a gap where BitTorrent DHT connections lose their process mapping
            foreach (var kvp in newMap)
            {
                _portToProcessMap[kvp.Key] = kvp.Value;
            }

            // Remove entries whose port is no longer active (clean up stale mappings)
            var activeLocalPorts = new HashSet<ushort>(newMap.Keys);
            foreach (var existingPort in _portToProcessMap.Keys.ToArray())
            {
                if (!activeLocalPorts.Contains(existingPort))
                {
                    _portToProcessMap.TryRemove(existingPort, out _);
                }
            }
        }
        catch { }
    }

    private async Task SpeedCalculationLoop()
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
            using var proc = Process.GetProcessById(processId);
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
            if (kv.Value.ProcessName.ToLowerInvariant() == cleanName)
            {
                return kv.Value.ToSnapshot();
            }
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
        SetThrottle(processId, 1, 1); // 1 B/s ≤ BLOCK_THRESHOLD = drops all packets
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

    /// <summary>
    /// Holds a cloned packet + address for the reinject queue.
    /// Capture thread creates these; reinject thread consumes and disposes them.
    /// </summary>
    private sealed class QueuedPacket : IDisposable
    {
        public WinDivertPacket Packet { get; }
        public WinDivertAddress Address { get; }
        public bool IsOutbound { get; }
        public int DelayMs { get; }

        public QueuedPacket(WinDivertPacket srcPacket, WinDivertAddress srcAddr, bool isOutbound, int delayMs)
        {
            // Clone packet data + address so the capture thread can reuse its buffers
            Packet = srcPacket.Clone();
            Address = srcAddr.Clone();
            IsOutbound = isOutbound;
            DelayMs = Math.Min(delayMs, 500); // Cap delay per packet
        }

        public void Dispose()
        {
            Packet?.Dispose();
            Address?.Dispose();
        }
    }
}

/// <summary>
/// Traffic stats for a single process
/// </summary>
public class ProcessTrafficStats
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";  // Cached at creation time
    internal long _bytesReceived;
    internal long _bytesSent;
    public double DownloadSpeed { get; private set; }
    public double UploadSpeed { get; private set; }

    private long _lastActivityTicks = DateTime.Now.Ticks;

    public DateTime LastActivity
    {
        get => new DateTime(Interlocked.Read(ref _lastActivityTicks));
        set => Interlocked.Exchange(ref _lastActivityTicks, value.Ticks);
    }

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
        return new ProcessStatsSnapshot
        {
            ProcessId = ProcessId,
            ProcessName = ProcessName,
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

    // Threshold: any limit at or below this value = BLOCK (drop packets)
    private const long BLOCK_THRESHOLD_BPS = 10;

    /// <summary>
    /// Try to consume tokens for a packet. Returns:
    ///   -1 = packet should be DROPPED (blocked direction)
    ///    0 = proceed immediately (no delay)
    ///   >0 = delay in ms before reinjecting
    /// Uses Token Bucket algorithm for accurate rate limiting.
    /// </summary>
    public int ConsumeAndGetDelay(uint packetSize, bool isOutbound)
    {
        if (isOutbound)
        {
            if (UploadLimitBps <= 0) return 0; // No limit
            if (UploadLimitBps <= BLOCK_THRESHOLD_BPS) return -1; // BLOCKED → drop
            return ConsumeTokens(ref _uploadTokens, ref _uploadLastRefill, _uploadLock,
                                  UploadLimitBps, packetSize);
        }
        else
        {
            if (DownloadLimitBps <= 0) return 0; // No limit
            if (DownloadLimitBps <= BLOCK_THRESHOLD_BPS) return -1; // BLOCKED → drop
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

            // Cap delay to prevent extreme waits (max 500ms per packet)
            return Math.Min(500, Math.Max(1, delayMs));
        }
    }

    /// <summary>
    /// Check if packet should be delayed based on direction (legacy compatibility)
    /// </summary>
    public bool ShouldDelay(uint packetSize, bool isOutbound)
    {
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
        if (limit <= BLOCK_THRESHOLD_BPS) return -1; // Blocked

        // Calculate delay based on packet size and limit
        double delaySeconds = (double)packetSize / limit;
        int delayMs = (int)(delaySeconds * 1000);

        // Cap delay to prevent extreme waits
        return Math.Min(500, Math.Max(1, delayMs));
    }

    /// <summary>
    /// Check if packet should be dropped (blocked direction).
    /// A direction is blocked when its limit is > 0 but ≤ BLOCK_THRESHOLD_BPS.
    /// </summary>
    public bool ShouldDrop(bool isOutbound)
    {
        long limit = isOutbound ? UploadLimitBps : DownloadLimitBps;
        return limit > 0 && limit <= BLOCK_THRESHOLD_BPS;
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
    public string? SourceAddress;  // real packet source IP (null for non-IP)
    public string? DestAddress;    // real packet destination IP
    public string? TcpFlags;       // real TCP flags, e.g. "SYN,ACK" (null for non-TCP)
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

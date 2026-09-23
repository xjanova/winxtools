using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WindivertDotnet;

namespace NetX.Core.Network;

/// <summary>
/// Kernel packet engine built on the signed WinDivert driver.
///
/// Monitoring only  → the network handle runs in SNIFF mode: the kernel hands us
///                    copies, so this process never delays traffic (no extra ping).
/// Any limit active → the handle switches to DIVERT mode: every packet passes the
///                    shapers (GCRA virtual clock, see <see cref="ShaperBucket"/>)
///                    and is re-injected at its departure time by a pacer thread
///                    driven by a high-resolution waitable timer.
///
/// Packets are attributed to processes by local port. Ports are learned the
/// moment a socket binds/connects (WinDivert SOCKET layer, IPv4 and IPv6), with
/// the TCP/UDP owner tables as a periodic safety net. Loopback traffic is never
/// captured, so local IPC is unaffected by limits.
/// </summary>
public sealed class PacketEngine : IDisposable
{
    private static PacketEngine? _instance;
    private static readonly object _instanceLock = new();

    public static PacketEngine Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_instanceLock)
                {
                    _instance ??= new PacketEngine();
                }
            }
            return _instance;
        }
    }

    private const string NetworkFilter = "!loopback";
    private const byte ProtoTcp = 6;
    private const byte ProtoUdp = 17;

    private readonly object _lifecycle = new();
    private volatile bool _running;
    private volatile bool _draining;
    private volatile WinDivert? _divert;
    private volatile bool _divertMode;
    private Thread[] _captureThreads = [];
    private Pacer? _pacer;

    // Port → owning PID (index = local port). Plain int writes are atomic, so the
    // capture threads read these without locks.
    private readonly int[] _tcpOwner = new int[65536];
    private readonly int[] _udpOwner = new int[65536];
    private readonly long[] _tcpSeen = new long[65536];
    private readonly long[] _udpSeen = new long[65536];
    private WinDivert? _socketHandle;
    private Thread? _socketThread;
    private Thread? _tableThread;
    private readonly AutoResetEvent _tableSignal = new(false);
    private long _lastRefreshRequest;
    private volatile Dictionary<int, int> _connectionCounts = new();
    private readonly ConcurrentDictionary<int, AppIdentity> _identities = new();

    // Shaping state
    private readonly ConcurrentDictionary<string, ShaperPair> _appShapers = new(StringComparer.Ordinal);
    private volatile ShaperPair? _globalShaper;
    private static readonly long SendNowTicks = Stopwatch.Frequency / 4000; // 0.25 ms

    // Stats
    private readonly ConcurrentDictionary<int, ProcessTrafficStats> _processStats = new();
    private long _totalBytesReceived;
    private long _totalBytesSent;
    private long _droppedPackets;
    private double _currentDownloadSpeed;
    private double _currentUploadSpeed;
    private Thread? _speedThread;

    public event Action<PacketInfo>? OnPacketCaptured;
    public event Action<string>? OnError;

    public bool IsRunning => _running;
    public bool IsDriverLoaded { get; private set; }

    /// <summary>True while packets are diverted through the shapers.</summary>
    public bool IsShaping => _running && _divertMode;

    /// <summary>Last start/switch failure, shown to the user instead of failing silently.</summary>
    public string? LastError { get; private set; }

    public int CaptureWorkerCount { get; private set; }

    /// <summary>Packets currently waiting in the pacer.</summary>
    public int QueueDepth => _pacer?.Count ?? 0;

    /// <summary>Packets dropped by limits/blocks since the engine started.</summary>
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);

    /// <summary>True if any app or whole-PC limit is set.</summary>
    public bool HasLimits => _globalShaper != null || !_appShapers.IsEmpty;

    /// <summary>Kept for callers written before app/global limits existed.</summary>
    public bool HasThrottleRules => HasLimits;

    private PacketEngine()
    {
        IsDriverLoaded = CheckDriverAvailable();
    }

    private bool CheckDriverAvailable()
    {
        try
        {
            // The Reflect layer loads the driver without intercepting any traffic.
            using var probe = new WinDivert(Filter.True, WinDivertLayer.Reflect);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    #region Lifecycle

    private static int CalcCaptureWorkers()
    {
        int cores = Environment.ProcessorCount;
        return cores switch
        {
            <= 2 => 1,
            <= 4 => 2,
            <= 8 => Math.Max(2, cores / 2),
            _ => Math.Min(cores / 2, 8)
        };
    }

    /// <summary>
    /// Starts capturing. Sniff mode if no limits are set, divert mode otherwise.
    /// </summary>
    public bool Start()
    {
        lock (_lifecycle)
        {
            if (_running) return true;

            if (!IsDriverLoaded)
            {
                IsDriverLoaded = CheckDriverAvailable();
                if (!IsDriverLoaded)
                {
                    OnError?.Invoke($"WinDivert driver unavailable: {LastError}");
                    return false;
                }
            }

            _running = true;
            StartAttribution();

            if (!OpenNetwork(HasLimits) && !(HasLimits && OpenNetwork(false)))
            {
                _running = false;
                StopAttribution();
                return false;
            }

            _speedThread = new Thread(SpeedLoop) { Name = "PacketEngine-Speed", IsBackground = true };
            _speedThread.Start();

            Debug.WriteLine($"PacketEngine started ({(_divertMode ? "divert" : "sniff")}, {CaptureWorkerCount} workers)");
            return true;
        }
    }

    /// <summary>
    /// Stops capturing. Packets waiting in the pacer are sent first so nothing
    /// in flight is lost.
    /// </summary>
    public void Stop()
    {
        lock (_lifecycle)
        {
            if (!_running) return;
            CloseNetwork();
            _running = false;
            StopAttribution();
            _speedThread?.Join(1500);
            _speedThread = null;
            Debug.WriteLine("PacketEngine stopped");
        }
    }

    private bool OpenNetwork(bool divert)
    {
        try
        {
            var flags = divert ? WinDivertFlag.None : WinDivertFlag.Sniff | WinDivertFlag.RecvOnly;
            var handle = new WinDivert(NetworkFilter, WinDivertLayer.Network, 0, flags);

            try
            {
                handle.QueueLength = 16384;
                handle.QueueTime = TimeSpan.FromMilliseconds(2000);
                handle.QueueSize = 16 * 1024 * 1024;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not tune WinDivert queue: {ex.Message}");
            }

            if (divert) _pacer ??= new Pacer(this);

            _divertMode = divert;
            _draining = false;
            _divert = handle;

            CaptureWorkerCount = CalcCaptureWorkers();
            _captureThreads = new Thread[CaptureWorkerCount];
            for (int i = 0; i < _captureThreads.Length; i++)
            {
                _captureThreads[i] = new Thread(() => CaptureLoop(handle, divert))
                {
                    Name = $"PacketEngine-Capture-{i}",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _captureThreads[i].Start();
            }

            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Debug.WriteLine($"PacketEngine open failed ({(divert ? "divert" : "sniff")}): {ex.Message}");
            OnError?.Invoke($"Packet engine failed to start: {ex.Message}. Run WinXTools as Administrator.");
            return false;
        }
    }

    private void CloseNetwork()
    {
        var handle = _divert;
        if (handle == null) return;

        _draining = true;
        if (_divertMode)
        {
            // Stop new packets from queueing, let the workers drain what the
            // driver already holds (they re-inject it), then flush the pacer.
            try { handle.Shutdown(WinDivertShutdown.Recv); } catch { }
            JoinCaptureThreads();
            _pacer?.Stop(flush: true);
            _pacer?.Dispose();
            _pacer = null;
        }
        else
        {
            try { handle.Dispose(); } catch { }
            JoinCaptureThreads();
        }

        _divert = null;
        try { handle.Dispose(); } catch { }
        _draining = false;
    }

    private void JoinCaptureThreads()
    {
        // One shared deadline: a worker stuck in Recv must not multiply the wait.
        long deadline = Environment.TickCount64 + 3000;
        foreach (var t in _captureThreads)
        {
            try
            {
                int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
                if (t.IsAlive) t.Join(remaining);
            }
            catch { }
        }
        _captureThreads = [];
    }

    /// <summary>
    /// Switches between sniff and divert mode to match whether limits exist.
    /// </summary>
    private void EnsureMode()
    {
        lock (_lifecycle)
        {
            if (!_running) return;
            bool want = HasLimits;
            if (_divert != null && want == _divertMode) return;

            CloseNetwork();
            if (!OpenNetwork(want) && want)
            {
                // Could not divert: keep monitoring at least, and say why.
                OpenNetwork(false);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_instanceLock)
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
        }
    }

    #endregion

    #region Capture + shaping

    private void CaptureLoop(WinDivert handle, bool divertMode)
    {
        using var packet = new WinDivertPacket();
        using var addr = new WinDivertAddress();
        int consecutiveErrors = 0;

        while (true)
        {
            try
            {
                handle.Recv(packet, addr);
                consecutiveErrors = 0;
            }
            catch
            {
                // Shutdown/close ends the loop; anything else is retried.
                if (!_running || _draining || !ReferenceEquals(handle, _divert)) break;
                if (++consecutiveErrors > 50) Thread.Sleep(20);
                continue;
            }

            if (packet.Length == 0) continue;

            try
            {
                HandlePacket(handle, packet, addr, divertMode);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Packet handling error: {ex.Message}");
                // Never swallow a diverted packet because of our own bug.
                if (divertMode)
                {
                    try { handle.Send(packet, addr); } catch { }
                }
            }
        }
    }

    private unsafe void HandlePacket(WinDivert handle, WinDivertPacket packet, WinDivertAddress addr, bool divertMode)
    {
        int length = packet.Length;
        bool outbound = (addr.Flags & WinDivertAddressFlag.Outbound) != 0;

        var parse = packet.GetParseResult();
        ushort srcPort = 0, dstPort = 0;
        byte protocol = 0;
        if (parse.TcpHeader != null)
        {
            srcPort = parse.TcpHeader->SrcPort;
            dstPort = parse.TcpHeader->DstPort;
            protocol = ProtoTcp;
        }
        else if (parse.UdpHeader != null)
        {
            srcPort = parse.UdpHeader->SrcPort;
            dstPort = parse.UdpHeader->DstPort;
            protocol = ProtoUdp;
        }

        ushort localPort = outbound ? srcPort : dstPort;
        int pid = protocol switch
        {
            ProtoTcp => _tcpOwner[localPort],
            ProtoUdp => _udpOwner[localPort],
            _ => 0
        };
        if (pid == 0 && protocol != 0) RequestTableRefresh();

        var handler = OnPacketCaptured;
        if (handler != null)
            RaisePacketCaptured(handler, parse, pid, length, outbound, protocol, localPort, outbound ? dstPort : srcPort);

        if (!divertMode)
        {
            Account(pid, length, outbound);
            return;
        }

        ShaperPair? app = null;
        if (pid != 0 && !_appShapers.IsEmpty)
        {
            var identity = GetIdentity(pid);
            if (identity != null) _appShapers.TryGetValue(identity.Key, out app);
        }
        var global = _globalShaper;

        if (app == null && global == null)
        {
            handle.Send(packet, addr);
            Account(pid, length, outbound);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long departure = now;

        if (app != null)
        {
            departure = app.For(outbound).Schedule(length, now, departure);
            if (departure < 0) { Interlocked.Increment(ref _droppedPackets); return; }
        }
        if (global != null)
        {
            departure = global.For(outbound).Schedule(length, now, departure);
            if (departure < 0) { Interlocked.Increment(ref _droppedPackets); return; }
        }

        if (departure - now <= SendNowTicks)
        {
            handle.Send(packet, addr);
            Account(pid, length, outbound);
            return;
        }

        var pacer = _pacer;
        var queued = new QueuedPacket(handle, packet, addr, departure, pid, length, outbound);
        if (pacer == null || !pacer.Enqueue(queued))
        {
            queued.Dispose();
            Interlocked.Increment(ref _droppedPackets);
        }
    }

    private unsafe void RaisePacketCaptured(Action<PacketInfo> handler, WinDivertParseResult parse, int pid,
        int length, bool outbound, byte protocol, ushort localPort, ushort remotePort)
    {
        string? srcAddr = null, dstAddr = null, tcpFlags = null;
        try
        {
            if (parse.IPV4Header != null)
            {
                srcAddr = parse.IPV4Header->SrcAddr.ToString();
                dstAddr = parse.IPV4Header->DstAddr.ToString();
            }
            else if (parse.IPV6Header != null)
            {
                srcAddr = parse.IPV6Header->SrcAddr.ToString();
                dstAddr = parse.IPV6Header->DstAddr.ToString();
            }

            if (parse.TcpHeader != null)
            {
                var t = parse.TcpHeader;
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

        try
        {
            handler(new PacketInfo
            {
                ProcessId = pid,
                Length = length,
                IsOutbound = outbound,
                Protocol = protocol,
                LocalPort = localPort,
                RemotePort = remotePort,
                SourceAddress = srcAddr,
                DestAddress = dstAddr,
                TcpFlags = tcpFlags,
                Timestamp = DateTime.Now
            });
        }
        catch { }
    }

    /// <summary>Re-injects a packet the pacer held back. Called on the pacer thread.</summary>
    private void Deliver(QueuedPacket queued)
    {
        try
        {
            queued.Handle.Send(queued.Packet, queued.Address);
            Account(queued.ProcessId, queued.Length, queued.Outbound);
        }
        catch
        {
            Interlocked.Increment(ref _droppedPackets);
        }
        finally
        {
            queued.Dispose();
        }
    }

    private void Account(int pid, int length, bool outbound)
    {
        if (outbound) Interlocked.Add(ref _totalBytesSent, length);
        else Interlocked.Add(ref _totalBytesReceived, length);

        if (pid == 0) return;

        var stats = _processStats.GetOrAdd(pid, static (id, engine) =>
            new ProcessTrafficStats
            {
                ProcessId = id,
                ProcessName = engine.GetIdentity(id)?.Name ?? ""
            }, this);

        if (outbound) Interlocked.Add(ref stats._bytesSent, length);
        else Interlocked.Add(ref stats._bytesReceived, length);
        stats.Touch();
    }

    #endregion

    #region Limits API

    /// <summary>
    /// Lower-case process name without ".exe" — the key every limit uses, so
    /// all processes of one app share a single limit.
    /// </summary>
    public static string NormalizeAppKey(string processName)
    {
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// Limits one app (every process with that name). Values are bytes/second,
    /// <see cref="RateLimit.Unlimited"/> or <see cref="RateLimit.Blocked"/>.
    /// </summary>
    public void SetAppLimit(string processName, long downloadBps, long uploadBps)
    {
        var key = NormalizeAppKey(processName);
        if (key.Length == 0) return;

        if (downloadBps < 0 && uploadBps < 0)
        {
            RemoveAppLimit(processName);
            return;
        }

        _appShapers.AddOrUpdate(key,
            _ => new ShaperPair(downloadBps, uploadBps),
            (_, existing) => { existing.Update(downloadBps, uploadBps); return existing; });
        EnsureMode();
    }

    public void RemoveAppLimit(string processName)
    {
        if (_appShapers.TryRemove(NormalizeAppKey(processName), out _))
            EnsureMode();
    }

    public void ClearAppLimits()
    {
        _appShapers.Clear();
        EnsureMode();
    }

    /// <summary>Limits all traffic of this PC (every app together).</summary>
    public void SetGlobalLimit(long downloadBps, long uploadBps)
    {
        if (downloadBps < 0 && uploadBps < 0)
        {
            RemoveGlobalLimit();
            return;
        }

        var current = _globalShaper;
        if (current != null) current.Update(downloadBps, uploadBps);
        else _globalShaper = new ShaperPair(downloadBps, uploadBps);
        EnsureMode();
    }

    public void RemoveGlobalLimit()
    {
        if (_globalShaper == null) return;
        _globalShaper = null;
        EnsureMode();
    }

    #endregion

    #region Process attribution

    private void StartAttribution()
    {
        try
        {
            // Socket events arrive before the first packet of a connection, so
            // new downloads are attributed (and limited) from the first byte.
            _socketHandle = new WinDivert(NetworkFilter, WinDivertLayer.Socket, 0,
                WinDivertFlag.Sniff | WinDivertFlag.RecvOnly);
            var handle = _socketHandle;
            _socketThread = new Thread(() => SocketLoop(handle))
            {
                Name = "PacketEngine-Sockets",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _socketThread.Start();
        }
        catch (Exception ex)
        {
            // Older drivers: fall back to the owner tables only.
            _socketHandle = null;
            Debug.WriteLine($"Socket layer unavailable, using owner tables only: {ex.Message}");
        }

        _tableThread = new Thread(TableLoop) { Name = "PacketEngine-Ports", IsBackground = true };
        _tableThread.Start();
    }

    private void StopAttribution()
    {
        try { _socketHandle?.Dispose(); } catch { }
        _socketHandle = null;
        _tableSignal.Set();
        try { _socketThread?.Join(1500); } catch { }
        try { _tableThread?.Join(1500); } catch { }
        _socketThread = null;
        _tableThread = null;
    }

    private unsafe void SocketLoop(WinDivert handle)
    {
        using var packet = new WinDivertPacket(64);
        using var addr = new WinDivertAddress();

        while (_running)
        {
            try
            {
                handle.Recv(packet, addr);
            }
            catch
            {
                if (!_running || !ReferenceEquals(handle, _socketHandle)) break;
                Thread.Sleep(20);
                continue;
            }

            var ev = addr.Event;
            if (ev != WinDivertEvent.SocketBind && ev != WinDivertEvent.SocketConnect &&
                ev != WinDivertEvent.SocketListen && ev != WinDivertEvent.SocketAccept)
                continue;

            var socket = addr.Socket;
            int pid = (int)socket->ProcessId;
            ushort port = socket->LocalPort;
            int protocol = (int)socket->Protocol;
            if (pid <= 0 || port == 0) continue;

            long now = Stopwatch.GetTimestamp();
            if (protocol == ProtoTcp)
            {
                _tcpOwner[port] = pid;
                _tcpSeen[port] = now;
            }
            else if (protocol == ProtoUdp)
            {
                _udpOwner[port] = pid;
                _udpSeen[port] = now;
            }
            else continue;

            GetIdentity(pid); // warm the cache off the packet path
        }
    }

    private void RequestTableRefresh()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastRefreshRequest);
        if (now - last < 200) return;
        if (Interlocked.CompareExchange(ref _lastRefreshRequest, now, last) == last)
            _tableSignal.Set();
    }

    private void TableLoop()
    {
        while (_running)
        {
            try
            {
                RefreshOwnerTables();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Port table refresh failed: {ex.Message}");
            }

            _tableSignal.WaitOne(5000);
            if (_running) Thread.Sleep(50); // coalesce bursts of misses
        }
    }

    // Scratch for RefreshOwnerTables (only the table thread uses them).
    private readonly bool[] _tcpInTable = new bool[65536];
    private readonly bool[] _udpInTable = new bool[65536];

    private void RefreshOwnerTables()
    {
        long now = Stopwatch.GetTimestamp();
        var tcpNow = _tcpInTable;
        var udpNow = _udpInTable;
        Array.Clear(tcpNow);
        Array.Clear(udpNow);
        var counts = new Dictionary<int, int>();

        foreach (var (port, pid, listening) in OwnerTables.ReadTcp())
        {
            tcpNow[port] = true;
            _tcpOwner[port] = pid;
            _tcpSeen[port] = now;
            if (!listening) counts[pid] = counts.GetValueOrDefault(pid) + 1;
        }

        foreach (var (port, pid) in OwnerTables.ReadUdp())
        {
            udpNow[port] = true;
            _udpOwner[port] = pid;
            _udpSeen[port] = now;
            counts[pid] = counts.GetValueOrDefault(pid) + 1;
        }

        // Forget ports that vanished from the tables and weren't re-announced by
        // a socket event recently (the socket layer may be ahead of the tables).
        long staleBefore = now - Stopwatch.Frequency * 10;
        for (int port = 1; port < 65536; port++)
        {
            if (!tcpNow[port] && _tcpOwner[port] != 0 && _tcpSeen[port] < staleBefore) _tcpOwner[port] = 0;
            if (!udpNow[port] && _udpOwner[port] != 0 && _udpSeen[port] < staleBefore) _udpOwner[port] = 0;
        }

        _connectionCounts = counts;

        // Drop identities of processes that exited, and of PIDs Windows has
        // since handed to a different program (creation time changed).
        foreach (var (pid, identity) in _identities)
        {
            if (ProcessInfo.GetCreationTime(pid) != identity.CreationTime)
                _identities.TryRemove(pid, out _);
        }
    }

    /// <summary>Name/path of the process behind a PID (cached).</summary>
    public AppIdentity? GetIdentity(int pid)
    {
        if (pid <= 0) return null;
        if (_identities.TryGetValue(pid, out var cached)) return cached;

        var resolved = ProcessInfo.Resolve(pid);
        if (resolved == null) return null;
        return _identities.GetOrAdd(pid, resolved);
    }

    /// <summary>
    /// Full path of a running app's executable, learned from live traffic or
    /// by asking Windows. Used to create firewall rules for blocked apps.
    /// </summary>
    public string? FindExecutablePath(string processName)
    {
        var key = NormalizeAppKey(processName);
        foreach (var identity in _identities.Values)
        {
            if (identity.Key == key && !string.IsNullOrEmpty(identity.Path)) return identity.Path;
        }
        return ProcessInfo.FindPathByName(processName);
    }

    #endregion

    #region Stats

    private void SpeedLoop()
    {
        long lastTicks = Stopwatch.GetTimestamp();
        long lastReceived = Interlocked.Read(ref _totalBytesReceived);
        long lastSent = Interlocked.Read(ref _totalBytesSent);

        while (_running)
        {
            Thread.Sleep(1000);
            try
            {
                long nowTicks = Stopwatch.GetTimestamp();
                double elapsed = (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
                if (elapsed <= 0) continue;

                long received = Interlocked.Read(ref _totalBytesReceived);
                long sent = Interlocked.Read(ref _totalBytesSent);
                _currentDownloadSpeed = (received - lastReceived) / elapsed;
                _currentUploadSpeed = (sent - lastSent) / elapsed;
                lastReceived = received;
                lastSent = sent;
                lastTicks = nowTicks;

                foreach (var stats in _processStats.Values)
                    stats.CalculateSpeed(elapsed);

                // Forget processes that have been silent for 30 s.
                foreach (var (pid, stats) in _processStats)
                {
                    if (stats.IdleSeconds > 30) _processStats.TryRemove(pid, out _);
                }
            }
            catch { }
        }
    }

    public NetworkStatsSnapshot GetStats()
    {
        var counts = _connectionCounts;
        var active = _processStats.Values.Where(s => s.IsActive).ToList();

        // A process can start sending before its name could be read; fill it in now.
        foreach (var stats in active.Where(s => s.ProcessName.Length == 0))
            stats.ProcessName = GetIdentity(stats.ProcessId)?.Name ?? "";

        return new NetworkStatsSnapshot
        {
            TotalDownloadSpeed = _currentDownloadSpeed,
            TotalUploadSpeed = _currentUploadSpeed,
            TotalBytesReceived = Interlocked.Read(ref _totalBytesReceived),
            TotalBytesSent = Interlocked.Read(ref _totalBytesSent),
            ActiveProcessCount = active.Count,
            TotalConnections = counts.Values.Sum(),
            ProcessStats = active.Select(s =>
            {
                var snapshot = s.ToSnapshot();
                snapshot.ConnectionCount = counts.GetValueOrDefault(s.ProcessId);
                return snapshot;
            }).ToList()
        };
    }

    public ProcessStatsSnapshot? GetProcessStats(int processId)
    {
        if (!_processStats.TryGetValue(processId, out var stats)) return null;
        var snapshot = stats.ToSnapshot();
        snapshot.ConnectionCount = _connectionCounts.GetValueOrDefault(processId);
        return snapshot;
    }

    public ProcessStatsSnapshot? GetProcessStats(string processName)
    {
        var key = NormalizeAppKey(processName);
        foreach (var stats in _processStats.Values)
        {
            if (NormalizeAppKey(stats.ProcessName) == key)
                return stats.ToSnapshot();
        }
        return null;
    }

    /// <summary>
    /// Current combined speed of every process of one app, in bytes/second.
    /// </summary>
    public (double Download, double Upload) GetAppSpeed(string processName)
    {
        var key = NormalizeAppKey(processName);
        double down = 0, up = 0;
        foreach (var stats in _processStats.Values)
        {
            if (NormalizeAppKey(stats.ProcessName) != key) continue;
            down += stats.DownloadSpeed;
            up += stats.UploadSpeed;
        }
        return (down, up);
    }

    #endregion

    #region Pacer

    /// <summary>
    /// Holds delayed packets in a min-heap ordered by departure time and sends
    /// each one when it is due. Sleeps on a high-resolution waitable timer
    /// (≈0.5 ms precision) instead of Thread.Sleep (15.6 ms steps), so the
    /// delivered rate matches the configured one at every speed.
    /// </summary>
    private sealed class Pacer : IDisposable
    {
        private const int MaxQueuedPackets = 20000;
        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        private const uint TIMER_ALL_ACCESS = 0x1F0003;

        private readonly PacketEngine _owner;
        private readonly PriorityQueue<QueuedPacket, long> _heap = new();
        private readonly object _lock = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly SafeWaitHandle? _timer;
        private readonly WaitHandle[] _waitSet;
        private readonly bool _raisedTimerResolution;
        private readonly Thread _thread;
        private volatile bool _running = true;
        private long _earliest = long.MaxValue;

        public Pacer(PacketEngine owner)
        {
            _owner = owner;

            var timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (timer.IsInvalid)
            {
                // Pre-1803 Windows: normal timer + 1 ms system timer resolution.
                timer.Dispose();
                timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
                _raisedTimerResolution = timeBeginPeriod(1) == 0;
            }

            if (!timer.IsInvalid)
            {
                _timer = timer;
                _waitSet = [_wake, new TimerWaitHandle(timer)];
            }
            else
            {
                timer.Dispose();
                _waitSet = [_wake];
            }

            _thread = new Thread(Loop) { Name = "PacketEngine-Pacer", IsBackground = true, Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        public int Count
        {
            get { lock (_lock) return _heap.Count; }
        }

        public bool Enqueue(QueuedPacket packet)
        {
            bool wake;
            lock (_lock)
            {
                if (!_running || _heap.Count >= MaxQueuedPackets) return false;
                _heap.Enqueue(packet, packet.DueTicks);
                wake = packet.DueTicks < _earliest;
                if (wake) _earliest = packet.DueTicks;
            }
            if (wake) _wake.Set();
            return true;
        }

        private void Loop()
        {
            var due = new List<QueuedPacket>(256);

            while (_running)
            {
                long now = Stopwatch.GetTimestamp();
                long next;

                lock (_lock)
                {
                    while (_heap.TryPeek(out var packet, out var dueTicks) && dueTicks <= now + SendNowTicks)
                    {
                        _heap.Dequeue();
                        due.Add(packet);
                    }
                    next = _heap.TryPeek(out _, out var nextTicks) ? nextTicks : long.MaxValue;
                    _earliest = next;
                }

                foreach (var packet in due) _owner.Deliver(packet);
                due.Clear();

                if (next == long.MaxValue)
                {
                    _wake.WaitOne(250);
                    continue;
                }

                long wait = next - Stopwatch.GetTimestamp();
                if (wait > SendNowTicks) WaitTicks(wait);
            }
        }

        private void WaitTicks(long ticks)
        {
            if (_timer != null)
            {
                long dueTime = -Math.Max(1, ticks * 10_000_000 / Stopwatch.Frequency); // relative, 100 ns units
                if (SetWaitableTimer(_timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    WaitHandle.WaitAny(_waitSet);
                    return;
                }
            }
            _wake.WaitOne((int)Math.Max(1, ticks * 1000 / Stopwatch.Frequency));
        }

        /// <summary>Stops the thread; queued packets are sent now or discarded.</summary>
        public void Stop(bool flush)
        {
            lock (_lock) _running = false;
            _wake.Set();
            _thread.Join(2000);

            var rest = new List<QueuedPacket>();
            lock (_lock)
            {
                while (_heap.TryDequeue(out var packet, out _)) rest.Add(packet);
                _earliest = long.MaxValue;
            }

            foreach (var packet in rest)
            {
                if (flush) _owner.Deliver(packet);
                else packet.Dispose();
            }
        }

        public void Dispose()
        {
            if (_running) Stop(flush: false);
            if (_waitSet.Length > 1) _waitSet[1].Dispose();
            else _timer?.Dispose();
            if (_raisedTimerResolution) timeEndPeriod(1);
            _wake.Dispose();
        }

        private sealed class TimerWaitHandle : WaitHandle
        {
            public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(SafeWaitHandle hTimer, ref long pDueTime, int lPeriod,
            IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool fResume);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);
    }

    /// <summary>
    /// A diverted packet waiting for its departure time. Owns native copies of
    /// the packet and address so the capture thread can reuse its buffers.
    /// </summary>
    private sealed class QueuedPacket : IDisposable
    {
        public WinDivert Handle { get; }
        public WinDivertPacket Packet { get; }
        public WinDivertAddress Address { get; }
        public long DueTicks { get; }
        public int ProcessId { get; }
        public int Length { get; }
        public bool Outbound { get; }

        public QueuedPacket(WinDivert handle, WinDivertPacket packet, WinDivertAddress addr,
            long dueTicks, int processId, int length, bool outbound)
        {
            Handle = handle;
            Packet = packet.Clone();
            Address = addr.Clone();
            DueTicks = dueTicks;
            ProcessId = processId;
            Length = length;
            Outbound = outbound;
        }

        public void Dispose()
        {
            Packet.Dispose();
            Address.Dispose();
        }
    }

    #endregion
}

/// <summary>Who is behind a PID: display name, limit key, and executable path.</summary>
public sealed record AppIdentity(string Name, string Key, string? Path, long CreationTime);

/// <summary>Process lookups that work for elevated and protected processes.</summary>
internal static class ProcessInfo
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public static AppIdentity? Resolve(int pid)
    {
        if (pid == 4) return new AppIdentity("System", "system", null, 0);

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var path = QueryImagePath(handle);
                GetProcessTimes(handle, out long created, out _, out _, out _);
                if (!string.IsNullOrEmpty(path))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    return new AppIdentity(name, PacketEngine.NormalizeAppKey(name), path, created);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName;
            return new AppIdentity(name, PacketEngine.NormalizeAppKey(name), null, GetCreationTime(pid));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Process creation time (FILETIME), or -1 if it no longer exists.</summary>
    public static long GetCreationTime(int pid)
    {
        if (pid == 4) return 0;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return -1;
        try
        {
            return GetProcessTimes(handle, out long created, out _, out _, out _) ? created : -1;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string? FindPathByName(string processName)
    {
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        Process[] processes;
        try { processes = Process.GetProcessesByName(name); } catch { return null; }

        try
        {
            foreach (var process in processes)
            {
                var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    var path = QueryImagePath(handle);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return null;
    }

    private static string? QueryImagePath(IntPtr handle)
    {
        var buffer = new StringBuilder(1024);
        int size = buffer.Capacity;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime,
        out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>
/// Reads the TCP/UDP owner tables for IPv4 and IPv6 (port + owning PID).
/// </summary>
internal static class OwnerTables
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const int MIB_TCP_STATE_LISTEN = 2;

    public static List<(ushort Port, int Pid, bool Listening)> ReadTcp()
    {
        var rows = new List<(ushort, int, bool)>();
        // IPv4 row: state, localAddr, localPort, remoteAddr, remotePort, pid (24 bytes)
        ReadTable(isTcp: true, AF_INET, rowSize: 24, (row) =>
            rows.Add((Port(row, 8), Marshal.ReadInt32(row, 20), Marshal.ReadInt32(row, 0) == MIB_TCP_STATE_LISTEN)));
        // IPv6 row: localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid (56 bytes)
        ReadTable(isTcp: true, AF_INET6, rowSize: 56, (row) =>
            rows.Add((Port(row, 20), Marshal.ReadInt32(row, 52), Marshal.ReadInt32(row, 48) == MIB_TCP_STATE_LISTEN)));
        return rows;
    }

    public static List<(ushort Port, int Pid)> ReadUdp()
    {
        var rows = new List<(ushort, int)>();
        // IPv4 row: localAddr, localPort, pid (12 bytes)
        ReadTable(isTcp: false, AF_INET, rowSize: 12, (row) => rows.Add((Port(row, 4), Marshal.ReadInt32(row, 8))));
        // IPv6 row: localAddr[16], scope, localPort, pid (28 bytes)
        ReadTable(isTcp: false, AF_INET6, rowSize: 28, (row) => rows.Add((Port(row, 20), Marshal.ReadInt32(row, 24))));
        return rows;
    }

    private static ushort Port(IntPtr row, int offset)
    {
        // Stored in network byte order in the low 16 bits of a DWORD.
        int raw = Marshal.ReadInt32(row, offset);
        return (ushort)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
    }

    private static void ReadTable(bool isTcp, int family, int rowSize, Action<IntPtr> onRow)
    {
        int size = 0;
        int tableClass = isTcp ? TCP_TABLE_OWNER_PID_ALL : UDP_TABLE_OWNER_PID;
        Call(isTcp, IntPtr.Zero, ref size, family, tableClass);
        if (size <= 0) return;

        // The table can grow between the two calls; retry with the new size.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            size += 4096;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint result = Call(isTcp, buffer, ref size, family, tableClass);
                if (result == 122) continue; // ERROR_INSUFFICIENT_BUFFER
                if (result != 0) return;

                int count = Marshal.ReadInt32(buffer);
                var row = buffer + 4;
                for (int i = 0; i < count; i++, row += rowSize)
                    onRow(row);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static uint Call(bool isTcp, IntPtr buffer, ref int size, int family, int tableClass) =>
        isTcp
            ? GetExtendedTcpTable(buffer, ref size, false, family, tableClass, 0)
            : GetExtendedUdpTable(buffer, ref size, false, family, tableClass, 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, int reserved);
}

/// <summary>
/// Traffic counters for one process. Bytes are counted when a packet is
/// actually delivered, so a limited app shows its real (limited) speed.
/// </summary>
public class ProcessTrafficStats
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    internal long _bytesReceived;
    internal long _bytesSent;
    public double DownloadSpeed { get; private set; }
    public double UploadSpeed { get; private set; }

    private long _lastActivityTick = Environment.TickCount64;
    private long _lastBytesReceived;
    private long _lastBytesSent;

    internal void Touch() => Volatile.Write(ref _lastActivityTick, Environment.TickCount64);

    public double IdleSeconds => (Environment.TickCount64 - Volatile.Read(ref _lastActivityTick)) / 1000.0;

    public DateTime LastActivity => DateTime.Now.AddSeconds(-IdleSeconds);

    public bool IsActive => IdleSeconds < 10;

    public void CalculateSpeed(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0) return;

        long received = Interlocked.Read(ref _bytesReceived);
        long sent = Interlocked.Read(ref _bytesSent);

        DownloadSpeed = (received - _lastBytesReceived) / elapsedSeconds;
        UploadSpeed = (sent - _lastBytesSent) / elapsedSeconds;

        _lastBytesReceived = received;
        _lastBytesSent = sent;
    }

    public ProcessStatsSnapshot ToSnapshot() => new()
    {
        ProcessId = ProcessId,
        ProcessName = ProcessName,
        DownloadSpeed = DownloadSpeed,
        UploadSpeed = UploadSpeed,
        TotalBytesReceived = Interlocked.Read(ref _bytesReceived),
        TotalBytesSent = Interlocked.Read(ref _bytesSent),
        LastActivity = LastActivity
    };
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
    public int TotalConnections { get; set; }
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
    public int ConnectionCount { get; set; }
    public DateTime LastActivity { get; set; }
}

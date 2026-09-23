using System.Diagnostics;

namespace NetX.Core.Network;

/// <summary>
/// Special values for a direction's rate, shared by the engine, the limiter
/// and the UI. Any positive value is a real rate in bytes per second.
/// </summary>
public static class RateLimit
{
    /// <summary>No limit in this direction.</summary>
    public const long Unlimited = -1;

    /// <summary>Every packet in this direction is dropped.</summary>
    public const long Blocked = 0;

    public static bool IsLimited(long bytesPerSecond) => bytesPerSecond >= 0;
}

/// <summary>
/// One direction of a traffic shaper, implemented with the Generic Cell Rate
/// Algorithm (a virtual clock): every packet gets a departure time, so the
/// long-run rate can never exceed <see cref="RateBps"/> no matter how many
/// threads feed it or how coarse the OS timer is. A small burst tolerance lets
/// short bursts through untouched, and packets that would wait longer than
/// the queue budget are dropped so TCP backs off instead of queueing for
/// seconds (bufferbloat).
/// </summary>
public sealed class ShaperBucket
{
    // Largest Ethernet frame we expect; used to size bursts at very low rates.
    private const int MaxPacketBytes = 1514;

    // Packets this small (pure TCP ACK/SYN/FIN, DNS queries) are charged to the
    // bucket but never delayed, so browsing stays responsive under a tight cap.
    public const int PriorityPacketBytes = 100;

    private readonly object _lock = new();
    private long _rateBps = RateLimit.Unlimited;
    private double _ticksPerByte;
    private long _burstTicks;
    private long _maxDelayTicks;
    private long _tat; // theoretical arrival time of the next byte, Stopwatch ticks

    public ShaperBucket(long rateBps) => SetRate(rateBps);

    public long RateBps => Volatile.Read(ref _rateBps);

    /// <summary>
    /// Changes the rate without losing the schedule of packets already queued.
    /// </summary>
    public void SetRate(long rateBps)
    {
        lock (_lock)
        {
            if (rateBps > 0)
            {
                double freq = Stopwatch.Frequency;
                _ticksPerByte = freq / rateBps;

                // Burst: 25 ms of traffic, but at least two full packets so very
                // low caps don't stall on the first large packet.
                _burstTicks = Math.Max((long)(freq * 0.025), (long)(2 * MaxPacketBytes * _ticksPerByte));

                // Queue budget: 250 ms, stretched for low rates so a few packets
                // always fit; capped at 3 s.
                _maxDelayTicks = Math.Min((long)(freq * 3),
                    Math.Max((long)(freq * 0.25), (long)(4 * MaxPacketBytes * _ticksPerByte)));
            }
            Volatile.Write(ref _rateBps, rateBps < 0 ? RateLimit.Unlimited : rateBps);
        }
    }

    /// <summary>
    /// Schedules a packet that reaches this shaper at <paramref name="arrival"/>
    /// (which is <paramref name="now"/> unless an earlier shaper already delayed
    /// it). Returns the departure timestamp, or -1 when the packet must be
    /// dropped (direction blocked, or queue budget exceeded).
    /// </summary>
    public long Schedule(int size, long now, long arrival)
    {
        long rate = Volatile.Read(ref _rateBps);
        if (rate < 0) return arrival;
        if (rate == 0) return -1;

        lock (_lock)
        {
            long tat = _tat < arrival ? arrival : _tat;
            long cost = (long)(size * _ticksPerByte);

            if (size <= PriorityPacketBytes)
            {
                // Charge it (keeps the long-run rate honest) but send right away.
                _tat = tat + cost;
                return arrival;
            }

            long departure = tat - _burstTicks;
            if (departure < arrival) departure = arrival;

            if (departure - now > _maxDelayTicks)
                return -1;

            _tat = tat + cost;
            return departure;
        }
    }
}

/// <summary>
/// Download + upload shaper for one app (all of its processes share it) or
/// for the whole PC.
/// </summary>
public sealed class ShaperPair
{
    public ShaperBucket Download { get; }
    public ShaperBucket Upload { get; }

    public ShaperPair(long downloadBps, long uploadBps)
    {
        Download = new ShaperBucket(downloadBps);
        Upload = new ShaperBucket(uploadBps);
    }

    public ShaperBucket For(bool outbound) => outbound ? Upload : Download;

    public void Update(long downloadBps, long uploadBps)
    {
        Download.SetRate(downloadBps);
        Upload.SetRate(uploadBps);
    }

    public bool IsNoOp => Download.RateBps < 0 && Upload.RateBps < 0;
}

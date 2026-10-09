using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetX.Core.System.Storage;

/// <summary>One physical read or write: where on the disk (bytes from its start), how big, when (Stopwatch ticks).</summary>
public readonly record struct DiskIoSample(long Offset, int Bytes, bool IsWrite, long Timestamp);

/// <summary>
/// Sees every physical disk read and write through Windows' own event tracing
/// (the kernel "Disk I/O" events Resource Monitor uses) — where on the disk and
/// how much — to draw the real head movement in the 3D view. It runs only while
/// someone is looking: the Defrag page acquires it when it becomes visible and
/// releases it when it is hidden or closed, and nothing keeps it running in the
/// background. One private trace session (Windows 8 and later, administrator only).
/// </summary>
public sealed unsafe class DiskActivityMonitor
{
    public static DiskActivityMonitor Instance { get; } = new();

    private const string SessionName = "WinXTools Disk Activity";
    private static readonly Guid SessionGuid = new("6b3b8a4e-7f2c-4b8e-9a51-2f0d6c1e9b37");
    private static readonly Guid DiskIoGuid = new("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c");

    private const int PropertiesSize = 120 + 1024 + 1024;
    private const int LogFileSize = 448;
    private const ulong InvalidTrace = ulong.MaxValue;
    private const int RingSize = 512;

    private readonly object _gate = new();
    private readonly Dictionary<int, DiskRing> _rings = new();
    private int _users;
    private ulong _session;
    private ulong _trace = InvalidTrace;
    private IntPtr _loggerName;
    private Thread? _thread;

    private DiskActivityMonitor()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ForceStop();
    }

    public bool IsRunning => _thread != null;

    /// <summary>Windows' error code from the last failed start (0 = none).</summary>
    public int LastError { get; private set; }

    /// <summary>Starts watching, or joins the session already running. False when Windows refused.</summary>
    public bool Acquire()
    {
        lock (_gate)
        {
            if (_thread != null)
            {
                _users++;
                return true;
            }
            if (!StartSession()) return false;
            _users = 1;
            return true;
        }
    }

    /// <summary>The last user releasing it stops the trace session at once.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_users <= 0) return;
            if (--_users == 0) StopSession();
        }
    }

    /// <summary>Moves the I/Os seen on a disk since the last call into <paramref name="into"/> (oldest first).</summary>
    public int Drain(int disk, List<DiskIoSample> into)
    {
        DiskRing? ring;
        lock (_rings) _rings.TryGetValue(disk, out ring);
        return ring?.Drain(into) ?? 0;
    }

    #region Session

    private bool StartSession()
    {
        StopLeftover();

        var props = NewProperties();
        try
        {
            *(Guid*)(props + 24) = SessionGuid;             // Wnode.Guid
            *(uint*)(props + 40) = 1;                       // Wnode.ClientContext: QueryPerformanceCounter clock
            *(uint*)(props + 44) = 0x00020000;              // Wnode.Flags: WNODE_FLAG_TRACED_GUID
            *(uint*)(props + 48) = 64;                      // BufferSize (KB)
            *(uint*)(props + 52) = 4;                       // MinimumBuffers
            *(uint*)(props + 56) = 16;                      // MaximumBuffers
            *(uint*)(props + 64) = 0x00000100 | 0x02000000; // LogFileMode: real time | system logger (Windows 8+)
            *(uint*)(props + 68) = 1;                       // FlushTimer: deliver at least every second
            *(uint*)(props + 72) = 0x00000100;              // EnableFlags: EVENT_TRACE_FLAG_DISK_IO

            uint error = StartTraceW(out _session, SessionName, props);
            if (error != 0)
            {
                LastError = (int)error;
                Debug.WriteLine($"[DiskActivity] StartTrace failed: {error}");
                return false;
            }
        }
        finally
        {
            NativeMemory.Free(props);
        }

        var logfile = (byte*)NativeMemory.AllocZeroed(LogFileSize);
        try
        {
            _loggerName = Marshal.StringToHGlobalUni(SessionName);
            *(IntPtr*)(logfile + 8) = _loggerName;                     // LoggerName
            *(uint*)(logfile + 28) = 0x00000100 | 0x10000000;           // ProcessTraceMode: real time | EVENT_RECORD
            *(IntPtr*)(logfile + 424) = (IntPtr)(delegate* unmanaged<EventRecord*, void>)&OnEvent; // EventRecordCallback
            _trace = OpenTraceW(logfile);
        }
        finally
        {
            NativeMemory.Free(logfile);
        }

        if (_trace == InvalidTrace)
        {
            LastError = Marshal.GetLastWin32Error();
            StopTrace();
            FreeLoggerName();
            return false;
        }

        ulong trace = _trace;
        _thread = new Thread(() =>
        {
            uint result = ProcessTrace(new[] { trace }, 1, IntPtr.Zero, IntPtr.Zero);
            Debug.WriteLine($"[DiskActivity] ProcessTrace ended: {result}");
        })
        { IsBackground = true, Name = "WinXTools disk activity" };
        _thread.Start();
        LastError = 0;
        return true;
    }

    private void StopSession()
    {
        StopTrace();
        if (_trace != InvalidTrace)
        {
            CloseTrace(_trace);
            _trace = InvalidTrace;
        }
        _thread?.Join(3000);
        _thread = null;
        FreeLoggerName();
        lock (_rings) _rings.Clear();
    }

    private void StopTrace()
    {
        var props = NewProperties();
        try { ControlTraceW(_session, _session == 0 ? SessionName : null, props, 1 /* EVENT_TRACE_CONTROL_STOP */); }
        finally { NativeMemory.Free(props); }
        _session = 0;
    }

    private void FreeLoggerName()
    {
        if (_loggerName == IntPtr.Zero) return;
        Marshal.FreeHGlobal(_loggerName);
        _loggerName = IntPtr.Zero;
    }

    /// <summary>A session left behind by a WinXTools that crashed keeps running in Windows: stop it first.</summary>
    private static void StopLeftover()
    {
        var props = NewProperties();
        try { ControlTraceW(0, SessionName, props, 1); }
        finally { NativeMemory.Free(props); }
    }

    private void ForceStop()
    {
        lock (_gate)
        {
            if (_thread == null) return;
            _users = 0;
            StopSession();
        }
    }

    private static byte* NewProperties()
    {
        var props = (byte*)NativeMemory.AllocZeroed(PropertiesSize);
        *(uint*)props = PropertiesSize;        // Wnode.BufferSize
        *(uint*)(props + 116) = 120;           // LoggerNameOffset
        *(uint*)(props + 112) = 120 + 1024;    // LogFileNameOffset (empty: real time only)
        return props;
    }

    #endregion

    #region Events (trace thread)

    [StructLayout(LayoutKind.Explicit, Size = 112)]
    private struct EventRecord
    {
        [FieldOffset(4)] public ushort Flags;
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(45)] public byte Opcode;
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public IntPtr UserData;
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(EventRecord* record)
    {
        try
        {
            Instance.Handle(record);
        }
        catch
        {
            // Never let an exception cross back into Windows.
        }
    }

    private void Handle(EventRecord* record)
    {
        // DiskIo read (10) / write (11): DiskNumber @0, TransferSize @8, ByteOffset @16.
        if (record->ProviderId != DiskIoGuid || record->Opcode is not (10 or 11) || record->UserDataLength < 24) return;
        var data = (byte*)record->UserData;
        if (data == null) return;
        int disk = *(int*)data;
        RingFor(disk).Add(new DiskIoSample(*(long*)(data + 16), *(int*)(data + 8), record->Opcode == 11, Stopwatch.GetTimestamp()));
    }

    private DiskRing RingFor(int disk)
    {
        lock (_rings)
        {
            if (!_rings.TryGetValue(disk, out var ring)) _rings[disk] = ring = new DiskRing();
            return ring;
        }
    }

    #endregion

    /// <summary>The last few hundred I/Os of one disk (the page drains it a few times a second).</summary>
    private sealed class DiskRing
    {
        private readonly DiskIoSample[] _items = new DiskIoSample[RingSize];
        private int _start, _count;

        public void Add(DiskIoSample sample)
        {
            lock (this)
            {
                _items[(_start + _count) % RingSize] = sample;
                if (_count < RingSize) _count++;
                else _start = (_start + 1) % RingSize;
            }
        }

        public int Drain(List<DiskIoSample> into)
        {
            lock (this)
            {
                for (int i = 0; i < _count; i++) into.Add(_items[(_start + i) % RingSize]);
                int n = _count;
                _start = _count = 0;
                return n;
            }
        }
    }

    #region Native

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, byte* properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string? sessionName, byte* properties, uint controlCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(byte* logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);

    #endregion
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetX.Core.System.Storage;

public enum SafetyIssue
{
    /// <summary>The file system has errors (dirty bit set): chkdsk must run first.</summary>
    VolumeDirty,
    /// <summary>The drive itself reports it is failing: back up, don't move data around.</summary>
    DiskFailing,
    /// <summary>The drive reports a health warning.</summary>
    DiskWarning,
    /// <summary>Running on battery (laptop, or a UPS after a power cut).</summary>
    OnBattery,
    /// <summary>The drive caches writes but can't be told to empty that cache.</summary>
    NoCacheFlush,
    /// <summary>Windows was told not to flush this drive's cache ("turn off write-cache buffer flushing").</summary>
    FlushingTurnedOff,
    /// <summary>Too little free space to move files safely.</summary>
    LowFreeSpace,
    /// <summary>WinXTools' own file placement works on NTFS only.</summary>
    NotNtfs
}

public sealed record SafetyFinding(SafetyIssue Issue, bool Blocks);

/// <summary>What was checked before an operation that moves data on a drive.</summary>
public sealed class SafetyReport
{
    public List<SafetyFinding> Findings { get; } = new();
    public bool CanStart => !Findings.Any(f => f.Blocks);
    public WriteCacheInfo? Cache { get; init; }

    /// <summary>USB or another drive that can be unplugged: the user is told not to while data moves.</summary>
    public bool IsRemovable { get; init; }
}

/// <summary>The drive's write cache as Windows sees it (STORAGE_WRITE_CACHE_PROPERTY).</summary>
public sealed record WriteCacheInfo(bool? CacheEnabled, bool FlushSupported, bool PowerProtectedByUser);

public enum StopReason
{
    User,
    /// <summary>The power cable was pulled (or the UPS took over).</summary>
    PowerUnplugged,
    /// <summary>Someone asked Windows to remove the drive (Safely Remove / Eject).</summary>
    DriveRemoval,
    /// <summary>The drive disappeared (unplugged without asking).</summary>
    DriveGone,
    /// <summary>Automatic work only: the user came back to the PC.</summary>
    UserActive
}

/// <summary>
/// Safety checks before data is moved on a drive, and the helpers that keep it
/// safe while it moves. Moving itself is always done by the file system
/// (FSCTL_MOVE_FILE / Windows' defragmenter): it copies a piece to free space
/// first and switches the file over only once the copy is complete, recorded in
/// the NTFS journal — so a crash or power cut leaves each file either in its old
/// place or in its new one, never half-written.
/// </summary>
public static unsafe class DriveSafety
{
    /// <summary>
    /// <paramref name="ownMoves"/>: WinXTools moves the files itself (stricter:
    /// NTFS, free space, and a drive whose cache can be flushed). Windows'
    /// defragmenter makes those checks itself.
    /// </summary>
    public static SafetyReport Check(DriveVolume volume, bool ownMoves)
    {
        var cache = volume.DiskNumber is int n ? QueryWriteCache(n) : null;
        bool removable = volume.IsRemovable || volume.Disk?.Bus is DriveBus.Usb or DriveBus.SdCard;
        var report = new SafetyReport { Cache = cache, IsRemovable = removable };
        var findings = report.Findings;

        if (IsVolumeDirty(volume.Letter) == true) findings.Add(new(SafetyIssue.VolumeDirty, true));
        if (volume.Disk?.Health == DiskHealth.Unhealthy) findings.Add(new(SafetyIssue.DiskFailing, true));
        else if (volume.Disk?.Health == DiskHealth.Warning) findings.Add(new(SafetyIssue.DiskWarning, false));

        var power = PowerStatus();
        if (power.HasBattery && !power.OnAc) findings.Add(new(SafetyIssue.OnBattery, true));

        // A drive that keeps written data in a cache it can't be told to empty may
        // lose the last writes when unplugged: fine for Windows' own optimizer to
        // decide, never for WinXTools' own moves on a removable drive.
        if (cache is { CacheEnabled: true, FlushSupported: false })
            findings.Add(new(SafetyIssue.NoCacheFlush, ownMoves && removable));
        if (cache is { PowerProtectedByUser: true } && removable)
            findings.Add(new(SafetyIssue.FlushingTurnedOff, ownMoves));

        if (ownMoves)
        {
            if (!volume.IsNtfs) findings.Add(new(SafetyIssue.NotNtfs, true));
            if (volume.TotalBytes > 0 && volume.FreeBytes < volume.TotalBytes * 0.03)
                findings.Add(new(SafetyIssue.LowFreeSpace, true));
        }
        return report;
    }

    /// <summary>True when the file system has marked itself as having errors; null when it can't be read (not administrator).</summary>
    public static bool? IsVolumeDirty(char letter)
    {
        using var volume = StorageNative.OpenVolumeForRead(letter);
        if (volume.IsInvalid) return null;
        uint flags = 0;
        if (!StorageNative.DeviceIoControl(volume, StorageNative.FSCTL_IS_VOLUME_DIRTY, null, 0, &flags, 4, out int returned, IntPtr.Zero) ||
            returned < 4)
            return null;
        return (flags & 1) != 0; // VOLUME_IS_DIRTY
    }

    public static WriteCacheInfo? QueryWriteCache(int diskNumber)
    {
        using var disk = StorageNative.OpenDeviceForQuery($@"\\.\PhysicalDrive{diskNumber}");
        if (disk.IsInvalid) return null;
        // STORAGE_WRITE_CACHE_PROPERTY: WriteCacheEnabled @12 (1 off, 2 on), FlushCacheSupported @24,
        // UserDefinedPowerProtection @25.
        var data = StorageNative.QueryStorageProperty(disk, StorageNative.StorageDeviceWriteCacheProperty, 32);
        if (data == null || data.Length < 26) return null;
        int enabled = BitConverter.ToInt32(data, 12);
        return new WriteCacheInfo(enabled switch { 1 => false, 2 => true, _ => null }, data[24] != 0, data[25] != 0);
    }

    public static (bool OnAc, bool HasBattery, int? BatteryPercent) PowerStatus()
    {
        if (!GetSystemPowerStatus(out var status)) return (true, false, null);
        bool hasBattery = status.BatteryFlag != 128 && status.BatteryFlag != 255;
        return (status.ACLineStatus != 0, hasBattery, status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null);
    }

    /// <summary>
    /// Writes everything Windows still holds for the volume to the drive and
    /// asks the drive to empty its own cache. Needs administrator rights.
    /// </summary>
    public static bool FlushVolume(char letter)
    {
        using var volume = StorageNative.CreateFileW($@"\\.\{char.ToUpperInvariant(letter)}:",
            StorageNative.GENERIC_READ | StorageNative.GENERIC_WRITE,
            StorageNative.FILE_SHARE_READ | StorageNative.FILE_SHARE_WRITE, IntPtr.Zero, StorageNative.OPEN_EXISTING, 0, IntPtr.Zero);
        return !volume.IsInvalid && StorageNative.FlushFileBuffers(volume);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

/// <summary>
/// Keeps an operation that moves data on a drive safe while it runs: the PC
/// doesn't go to sleep, and the work stops by itself — between two atomic steps
/// — when the power cable is pulled (laptop on battery, UPS after a power cut)
/// or when someone asks Windows to remove the drive (Safely Remove / Eject), so
/// the drive is let go of cleanly instead of being pulled mid-write. On a
/// removable drive it also flushes the drive's cache every few seconds.
/// </summary>
public sealed unsafe class SafeOperationGuard : IDisposable
{
    private readonly char _letter;
    private readonly CancellationTokenSource _cts;
    private readonly Timer _powerTimer;
    private readonly Timer? _flushTimer;
    private readonly ManualResetEventSlim _released = new(false);
    private IntPtr _powerRequest;
    private IntPtr _notify;
    private GCHandle _self;
    private SafeFileHandle? _watchHandle;
    private int _stopped, _disposed;
    private StopReason? _reason;

    /// <param name="what">Shown in "powercfg /requests" while it runs.</param>
    /// <param name="holdsHandles">False when Windows' own defragmenter does the work (it releases the drive itself).</param>
    public SafeOperationGuard(char letter, string what, bool flushPeriodically, bool holdsHandles, CancellationToken outer = default)
    {
        _letter = char.ToUpperInvariant(letter);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        if (!holdsHandles) _released.Set();
        KeepAwake(what);
        WatchRemoval();
        _powerTimer = new Timer(_ => CheckPower(), null, 0, 2000);
        if (flushPeriodically)
            _flushTimer = new Timer(_ => { if (_stopped == 0) DriveSafety.FlushVolume(_letter); }, null, 5000, 5000);
    }

    public CancellationToken Token => _cts.Token;

    /// <summary>Why it stopped (null while running or after a normal finish).</summary>
    public StopReason? Reason => _reason;

    /// <summary>Raised once, on whatever thread noticed it (power timer, Windows' device notification…).</summary>
    public event Action<StopReason>? StopRequested;

    public void Stop(StopReason reason)
    {
        if (Interlocked.CompareExchange(ref _stopped, 1, 0) != 0) return;
        _reason = reason;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { StopRequested?.Invoke(reason); }
        catch (Exception ex) { Debug.WriteLine($"[SafeOperationGuard] Stop handler failed: {ex.Message}"); }
    }

    /// <summary>The operation calls this once it has closed every handle it had on the drive.</summary>
    public void MarkReleased()
    {
        try { _released.Set(); } catch (ObjectDisposedException) { }
    }

    private void CheckPower()
    {
        var power = DriveSafety.PowerStatus();
        if (power.HasBattery && !power.OnAc) Stop(StopReason.PowerUnplugged);
    }

    #region Keep the PC awake

    private void KeepAwake(string what)
    {
        var text = Marshal.StringToHGlobalUni(what);
        try
        {
            var context = new REASON_CONTEXT { Version = 0, Flags = 1 /* POWER_REQUEST_CONTEXT_SIMPLE_STRING */, SimpleReasonString = text };
            var request = PowerCreateRequest(ref context);
            if (request == IntPtr.Zero || request == new IntPtr(-1)) return;
            if (PowerSetRequest(request, PowerRequestSystemRequired)) _powerRequest = request;
            else CloseHandle(request);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    #endregion

    #region Drive removal (CM_Register_Notification on a handle to the volume)

    private void WatchRemoval()
    {
        var handle = StorageNative.OpenDeviceForQuery($@"\\.\{_letter}:");
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return;
        }
        _watchHandle = handle;
        _self = GCHandle.Alloc(this);

        // CM_NOTIFY_FILTER: cbSize, Flags, FilterType (1 = device handle), Reserved, u.DeviceHandle.hTarget @16; 416 bytes.
        var filter = stackalloc byte[416];
        new Span<byte>(filter, 416).Clear();
        *(uint*)filter = 416;
        *(uint*)(filter + 8) = 1;
        *(IntPtr*)(filter + 16) = handle.DangerousGetHandle();

        uint result = CM_Register_Notification(filter, GCHandle.ToIntPtr(_self), &OnDeviceEvent, out _notify);
        if (result != 0)
        {
            Debug.WriteLine($"[SafeOperationGuard] Removal notifications unavailable (CONFIGRET {result}).");
            _notify = IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly]
    private static uint OnDeviceEvent(IntPtr notify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not SafeOperationGuard guard) return 0;
            switch (action)
            {
                case 2: // CM_NOTIFY_ACTION_DEVICEQUERYREMOVE: stop, close everything, then let Windows go ahead.
                    guard.Stop(StopReason.DriveRemoval);
                    guard._released.Wait(TimeSpan.FromSeconds(8));
                    guard.CloseWatchHandle();
                    break;
                case 4: // DEVICEREMOVEPENDING
                case 5: // DEVICEREMOVECOMPLETE: it is gone (pulled out without asking, or removal went through).
                    guard.Stop(StopReason.DriveGone);
                    guard.CloseWatchHandle();
                    break;
            }
        }
        catch
        {
            // Never let an exception cross back into Windows.
        }
        return 0; // ERROR_SUCCESS: never veto a removal
    }

    private void CloseWatchHandle() => Interlocked.Exchange(ref _watchHandle, null)?.Dispose();

    #endregion

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _powerTimer.Dispose();
        _flushTimer?.Dispose();

        // Waits for a callback in progress; must not run on the callback's own thread (it never does).
        if (_notify != IntPtr.Zero)
        {
            CM_Unregister_Notification(_notify);
            _notify = IntPtr.Zero;
        }
        if (_self.IsAllocated) _self.Free();
        CloseWatchHandle();

        if (_powerRequest != IntPtr.Zero)
        {
            PowerClearRequest(_powerRequest, PowerRequestSystemRequired);
            CloseHandle(_powerRequest);
            _powerRequest = IntPtr.Zero;
        }
        _cts.Dispose();
        _released.Dispose();
    }

    #region Native

    private const int PowerRequestSystemRequired = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        private IntPtr _detailed1; // the union's larger (detailed) member
        private IntPtr _detailed2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr request, int type);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Register_Notification(void* filter, IntPtr context,
        delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, uint, uint> callback, out IntPtr notify);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Unregister_Notification(IntPtr notify);

    #endregion
}

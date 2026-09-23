using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace NetX.Core.Optimization;

/// <summary>
/// Native plumbing for the RAM page:
///  * memory-list counters (NtQuerySystemInformation / SystemMemoryListInformation,
///    readable without any privilege),
///  * a handle-free process snapshot (SystemProcessInformation: private working
///    set, commit, session, CPU time, creation time),
///  * the memory-list commands, page combining and the system file cache flush,
///    run only inside <see cref="PrivilegedScope"/>,
///  * per-process working-set trim and a PID-reuse-safe terminate.
/// Nothing here changes system state unless its name says so.
/// </summary>
internal static unsafe class MemoryNative
{
    #region Status codes

    internal const int StatusSuccess = 0;
    internal const int StatusNotImplemented = unchecked((int)0xC0000002);
    internal const int StatusInvalidInfoClass = unchecked((int)0xC0000003);
    internal const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    internal const int StatusAccessDenied = unchecked((int)0xC0000022);
    internal const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    internal const int StatusPrivilegeNotHeld = unchecked((int)0xC0000061);
    internal const int StatusNotSupported = unchecked((int)0xC00000BB);

    internal const int ErrorAccessDenied = 5;
    internal const int ErrorNotSupported = 50;
    internal const int ErrorInvalidParameter = 87;
    internal const int ErrorCallNotImplemented = 120;
    internal const int ErrorPrivilegeNotHeld = 1314;

    #endregion

    #region Memory lists

    private const int SystemProcessInformationClass = 5;
    private const int SystemMemoryListInformationClass = 0x50;
    private const int SystemCombinePhysicalMemoryInformationClass = 0x82;

    // SYSTEM_MEMORY_LIST_COMMAND
    internal const int MemoryEmptyWorkingSets = 2;
    internal const int MemoryFlushModifiedList = 3;
    internal const int MemoryPurgeStandbyList = 4;
    internal const int MemoryPurgeLowPriorityStandbyList = 5;

    // SYSTEM_MEMORY_LIST_INFORMATION is 22 ULONG_PTRs: zero, free, modified,
    // modified-no-write, bad, standby by priority [8], repurposed by priority [8],
    // modified-to-pagefile.
    private const int MemoryListFieldCount = 22;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, void* buffer, uint length);

    /// <summary>Page counts of the physical memory lists (4 KB pages).</summary>
    internal readonly struct MemoryLists
    {
        public MemoryLists(ulong zero, ulong free, ulong modified, ulong modifiedNoWrite, ulong[] standby)
        {
            ZeroPages = zero;
            FreePages = free;
            ModifiedPages = modified;
            ModifiedNoWritePages = modifiedNoWrite;
            StandbyByPriority = standby;
        }

        public ulong ZeroPages { get; }
        public ulong FreePages { get; }
        public ulong ModifiedPages { get; }
        public ulong ModifiedNoWritePages { get; }
        public ulong[] StandbyByPriority { get; }

        public ulong StandbyPages
        {
            get
            {
                ulong total = 0;
                foreach (var pages in StandbyByPriority)
                    total += pages;
                return total;
            }
        }

        public ulong LowPriorityStandbyPages => StandbyByPriority.Length > 0 ? StandbyByPriority[0] : 0;
    }

    /// <summary>
    /// Reads the memory lists. Works without elevation. Returns the NTSTATUS
    /// (0 = success); on failure <paramref name="lists"/> is empty.
    /// </summary>
    internal static int QueryMemoryLists(out MemoryLists lists)
    {
        lists = default;
        nuint* fields = stackalloc nuint[MemoryListFieldCount];
        int status = NtQuerySystemInformation(SystemMemoryListInformationClass, fields,
            (uint)(MemoryListFieldCount * IntPtr.Size), out _);
        if (status != StatusSuccess)
            return status;

        var standby = new ulong[8];
        for (int i = 0; i < 8; i++)
            standby[i] = fields[5 + i];

        lists = new MemoryLists(fields[0], fields[1], fields[2], fields[3], standby);
        return StatusSuccess;
    }

    /// <summary>Runs one SYSTEM_MEMORY_LIST_COMMAND. Call only inside <see cref="PrivilegedScope.Run"/>.</summary>
    internal static int SetMemoryListCommand(int command)
    {
        int value = command;
        return NtSetSystemInformation(SystemMemoryListInformationClass, &value, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_COMBINE_INFORMATION_EX
    {
        public nint Handle;
        public nuint PagesCombined;
        public uint Flags;
    }

    /// <summary>Asks Windows to combine identical pages (Windows 10+). Call only inside <see cref="PrivilegedScope.Run"/>.</summary>
    internal static int CombineMemoryLists(out long pagesCombined)
    {
        var info = new MEMORY_COMBINE_INFORMATION_EX();
        int status = NtSetSystemInformation(SystemCombinePhysicalMemoryInformationClass, &info,
            (uint)sizeof(MEMORY_COMBINE_INFORMATION_EX));
        pagesCombined = status == StatusSuccess ? (long)info.PagesCombined : 0;
        return status;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemFileCacheSize(nuint minimumFileCacheSize, nuint maximumFileCacheSize, uint flags);

    /// <summary>
    /// Empties the system file cache working set ((SIZE_T)-1 for both sizes is the
    /// documented "flush" request; flags 0 sets no persistent limit). The pages
    /// move to the standby/modified lists, nothing is lost. Returns the Win32 error
    /// (0 = success). Call only inside <see cref="PrivilegedScope.Run"/>.
    /// </summary>
    internal static int FlushSystemFileCache() =>
        SetSystemFileCacheSize(nuint.MaxValue, nuint.MaxValue, 0) ? 0 : Marshal.GetLastWin32Error();

    #endregion

    #region Global memory status

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    internal static bool TryGetMemoryStatus(out MEMORYSTATUSEX status)
    {
        status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        return GlobalMemoryStatusEx(ref status);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    /// <summary>Installed RAM in bytes (from the firmware tables), or 0 when unknown.</summary>
    internal static ulong GetInstalledMemoryBytes()
    {
        try
        {
            return GetPhysicallyInstalledSystemMemory(out var kb) ? kb * 1024 : 0;
        }
        catch
        {
            return 0;
        }
    }

    #endregion

    #region Process snapshot

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    // Documented prefix of SYSTEM_PROCESS_INFORMATION (winternl.h names most of
    // these fields Reserved; the layout is the one Task Manager and .NET's own
    // Process class parse). Sequential layout gives the right offsets on x86 and x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public nuint UniqueProcessKey;
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }

    /// <summary>One process from a snapshot. Name has no ".exe", like Process.ProcessName.</summary>
    internal sealed record ProcessEntry(
        int Pid,
        string Name,
        int SessionId,
        long CreateTime,
        long CpuTime,
        long WorkingSetBytes,
        long PrivateWorkingSetBytes,
        long PrivateBytes);

    private const int InitialSnapshotBytes = 512 * 1024;
    private const int MaxSnapshotBytes = 64 * 1024 * 1024;

    // Size that worked last time (plus headroom), so a snapshot is normally ONE
    // kernel call instead of a too-small call followed by a retry.
    private static int _snapshotBytesHint = InitialSnapshotBytes;

    /// <summary>
    /// Handle-free snapshot of every process (one system call, no OpenProcess, so
    /// protected and other users' processes are included). Null when it fails.
    /// </summary>
    internal static List<ProcessEntry>? SnapshotProcesses()
    {
        int size = Math.Clamp(Volatile.Read(ref _snapshotBytesHint), InitialSnapshotBytes, MaxSnapshotBytes);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                fixed (byte* start = buffer)
                {
                    int status = NtQuerySystemInformation(SystemProcessInformationClass, start, (uint)buffer.Length, out uint needed);
                    if (status == StatusInfoLengthMismatch || status == StatusBufferTooSmall)
                    {
                        // Processes can start between the two calls — leave headroom.
                        long next = Math.Max((long)needed, buffer.Length) + 128 * 1024;
                        if (next > MaxSnapshotBytes)
                            return null;
                        size = (int)next;
                        continue;
                    }

                    if (status != StatusSuccess)
                        return null;

                    int valid = needed > 0 && needed <= (uint)buffer.Length ? (int)needed : buffer.Length;
                    Volatile.Write(ref _snapshotBytesHint, (int)Math.Min((long)valid + 128 * 1024, MaxSnapshotBytes));
                    return ParseProcesses(start, valid);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return null;
    }

    private static List<ProcessEntry> ParseProcesses(byte* start, int length)
    {
        var list = new List<ProcessEntry>(512);
        int entrySize = sizeof(SYSTEM_PROCESS_INFORMATION);
        long offset = 0;

        while (offset >= 0 && offset + entrySize <= length)
        {
            var spi = (SYSTEM_PROCESS_INFORMATION*)(start + offset);
            int pid = (int)spi->UniqueProcessId;

            list.Add(new ProcessEntry(
                pid,
                ReadImageName(spi->ImageName, start, length, pid),
                (int)spi->SessionId,
                spi->CreateTime,
                spi->UserTime + spi->KernelTime,
                (long)spi->WorkingSetSize,
                spi->WorkingSetPrivateSize,
                (long)spi->PagefileUsage));

            if (spi->NextEntryOffset == 0)
                break;
            offset += spi->NextEntryOffset;
        }

        return list;
    }

    private static string ReadImageName(UNICODE_STRING name, byte* start, int length, int pid)
    {
        if (pid == 0)
            return "Idle";

        var text = (byte*)name.Buffer;
        // The kernel copies the names into our buffer; never read outside it.
        if (name.Length == 0 || text == null || text < start || text + name.Length > start + length)
            return pid == 4 ? "System" : "";

        var image = new string((char*)text, 0, name.Length / 2);
        return image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image;
    }

    #endregion

    #region Per-process actions

    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SYNCHRONIZE = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creationTime, out long exitTime,
        out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "K32EmptyWorkingSet")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(SafeProcessHandle process);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, out PROCESS_MEMORY_COUNTERS counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    private const uint WAIT_OBJECT_0 = 0;

    internal enum NativeOutcome
    {
        Done,
        AccessDenied,
        /// <summary>The process exited, or its PID now belongs to a different process.</summary>
        Gone,
        Failed,
        /// <summary>Terminate was accepted but the process had not exited within the wait.</summary>
        StillRunning
    }

    private static NativeOutcome MapOpenError(int error) => error switch
    {
        ErrorAccessDenied => NativeOutcome.AccessDenied,
        ErrorInvalidParameter => NativeOutcome.Gone, // no process with that PID any more
        _ => NativeOutcome.Failed
    };

    /// <summary>
    /// True when the handle still refers to the process instance created at
    /// <paramref name="expectedCreateTime"/> (0 = don't check). Guards against
    /// acting on a PID that was reused after the snapshot was taken.
    /// </summary>
    private static bool IsSameInstance(SafeProcessHandle handle, long expectedCreateTime) =>
        expectedCreateTime == 0 ||
        (GetProcessTimes(handle, out var created, out _, out _, out _) && created == expectedCreateTime);

    private static long GetWorkingSetBytes(SafeProcessHandle handle) =>
        GetProcessMemoryInfo(handle, out var counters, (uint)sizeof(PROCESS_MEMORY_COUNTERS))
            ? (long)counters.WorkingSetSize
            : -1;

    /// <summary>
    /// Empties one process's working set (pages move to the standby/modified
    /// lists and are soft-faulted back when used). Needs only PROCESS_SET_QUOTA.
    /// </summary>
    internal static NativeOutcome TrimWorkingSet(int pid, long expectedCreateTime, out long beforeBytes, out long afterBytes)
    {
        beforeBytes = afterBytes = -1;
        using var handle = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid)
            return MapOpenError(Marshal.GetLastWin32Error());
        if (!IsSameInstance(handle, expectedCreateTime))
            return NativeOutcome.Gone;

        beforeBytes = GetWorkingSetBytes(handle);
        if (!EmptyWorkingSet(handle))
            return Marshal.GetLastWin32Error() == ErrorAccessDenied ? NativeOutcome.AccessDenied : NativeOutcome.Failed;

        afterBytes = GetWorkingSetBytes(handle);
        return NativeOutcome.Done;
    }

    /// <summary>
    /// Ends exactly the process instance that was seen (PID + creation time),
    /// never a later process that reused the PID. Only that process — never its tree.
    /// </summary>
    internal static NativeOutcome TerminateVerified(int pid, long expectedCreateTime, int waitMilliseconds)
    {
        using var handle = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid);
        if (handle.IsInvalid)
            return MapOpenError(Marshal.GetLastWin32Error());
        if (!IsSameInstance(handle, expectedCreateTime))
            return NativeOutcome.Gone;

        if (!TerminateProcess(handle, 1))
        {
            int error = Marshal.GetLastWin32Error();
            // Access denied is also what an already-exiting process returns.
            if (WaitForSingleObject(handle, 0) == WAIT_OBJECT_0)
                return NativeOutcome.Gone;
            return error == ErrorAccessDenied ? NativeOutcome.AccessDenied : NativeOutcome.Failed;
        }

        if (waitMilliseconds <= 0)
            return NativeOutcome.Done;
        return WaitForSingleObject(handle, (uint)waitMilliseconds) == WAIT_OBJECT_0
            ? NativeOutcome.Done
            : NativeOutcome.StillRunning;
    }

    #endregion

    #region Privileges

    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_IMPERSONATE = 0x0004;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;
    private const int SecurityImpersonation = 2;
    private const int TokenImpersonation = 2;

    internal const string SeProfileSingleProcessPrivilege = "SeProfileSingleProcessPrivilege";
    internal const string SeIncreaseQuotaPrivilege = "SeIncreaseQuotaPrivilege";

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    /// <summary>
    /// A private impersonation copy of WinXTools' token with the memory-management
    /// privileges enabled. The privileges are only in effect on the calling thread
    /// while <see cref="Run"/> executes — the process token itself is never changed,
    /// so nothing else in the app runs with them.
    /// </summary>
    internal sealed class PrivilegedScope : IDisposable
    {
        private readonly SafeAccessTokenHandle? _token;
        private readonly HashSet<string> _enabled;

        private PrivilegedScope(SafeAccessTokenHandle? token, HashSet<string> enabled)
        {
            _token = token;
            _enabled = enabled;
        }

        /// <summary>True when <paramref name="privilege"/> is enabled on the private token.</summary>
        public bool Has(string privilege) => _token != null && _enabled.Contains(privilege);

        /// <summary>SeProfileSingleProcessPrivilege: memory-list commands and page combining.</summary>
        public bool HasProfileSingleProcess => Has(SeProfileSingleProcessPrivilege);

        /// <summary>SeIncreaseQuotaPrivilege: system file cache flush.</summary>
        public bool HasIncreaseQuota => Has(SeIncreaseQuotaPrivilege);

        /// <summary>The two privileges the memory operations need.</summary>
        public static PrivilegedScope Create() => Create(SeProfileSingleProcessPrivilege, SeIncreaseQuotaPrivilege);

        internal static PrivilegedScope Create(params string[] privileges)
        {
            var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr processToken = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY, out processToken))
                    return new PrivilegedScope(null, enabled);

                if (!DuplicateTokenEx(processToken, TOKEN_IMPERSONATE | TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES,
                        IntPtr.Zero, SecurityImpersonation, TokenImpersonation, out var duplicate))
                    return new PrivilegedScope(null, enabled);

                var token = new SafeAccessTokenHandle(duplicate);
                // One at a time: AdjustTokenPrivileges can't say WHICH one of a
                // batch was missing.
                foreach (var privilege in privileges)
                {
                    if (Enable(duplicate, privilege))
                        enabled.Add(privilege);
                }

                return new PrivilegedScope(token, enabled);
            }
            catch
            {
                return new PrivilegedScope(null, new HashSet<string>());
            }
            finally
            {
                if (processToken != IntPtr.Zero)
                    CloseHandle(processToken);
            }
        }

        private static bool Enable(IntPtr token, string privilege)
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid))
                return false;

            var state = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            // Success with ERROR_NOT_ALL_ASSIGNED (1300) means the token doesn't hold it.
            return Marshal.GetLastWin32Error() == 0;
        }

        /// <summary>Runs <paramref name="action"/> on this thread with the privileged token.</summary>
        public int Run(Func<int> action) =>
            _token == null || _token.IsInvalid
                ? StatusPrivilegeNotHeld
                : WindowsIdentity.RunImpersonated(_token, action);

        public void Dispose() => _token?.Dispose();
    }

    #endregion
}

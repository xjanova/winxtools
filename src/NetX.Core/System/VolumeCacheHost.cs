using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NetX.Core.System;

/// <summary>
/// Runs Windows' own Disk Cleanup handlers (the ones behind "Clean up system
/// files": Windows Update cleanup, upgrade leftovers, old drivers…) the way
/// cleanmgr.exe does — Initialize → GetSpaceUsed → Purge — so sizes are exact
/// and protected system files are removed by the component that owns them.
/// Only Microsoft handlers from System32 are loaded.
/// </summary>
public static class VolumeCacheHost
{
    private const string HandlersKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
    private const int S_OK = 0, S_FALSE = 1;
    private const int E_ABORT = unchecked((int)0x80004004);

    public sealed class Outcome
    {
        public long Bytes { get; set; }
        public int HandlersRun { get; set; }
        public int HandlersFailed { get; set; }
        public bool TimedOut { get; set; }
    }

    /// <summary>Space the handlers would free on <paramref name="volumeRoot"/> (e.g. "C:\").</summary>
    public static Outcome Measure(IReadOnlyList<string> handlers, string volumeRoot, TimeSpan timeout, CancellationToken token) =>
        RunOnSta(t => Run(handlers, volumeRoot, purge: false, t), timeout, token);

    /// <summary>Lets the handlers delete what they found. Returns the space they reported freeing.</summary>
    public static Outcome Purge(IReadOnlyList<string> handlers, string volumeRoot, TimeSpan timeout, CancellationToken token) =>
        RunOnSta(t => Run(handlers, volumeRoot, purge: true, t), timeout, token);

    private static Outcome RunOnSta(Func<CancellationToken, Outcome> work, TimeSpan timeout, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Outcome? result = null;
        Exception? failure = null;
        // Handlers are apartment-threaded COM objects: give them their own STA thread.
        var thread = new Thread(() =>
        {
            try { result = work(stop.Token); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "Disk Cleanup handlers" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var deadline = DateTime.UtcNow + timeout;
        while (!thread.Join(250))
        {
            if (token.IsCancellationRequested) break;
            if (DateTime.UtcNow > deadline)
            {
                // The callback tells the handler to abort at its next progress report.
                stop.Cancel();
                thread.Join(TimeSpan.FromSeconds(20));
                return new Outcome { TimedOut = true, Bytes = result?.Bytes ?? 0 };
            }
        }
        if (token.IsCancellationRequested)
        {
            thread.Join(TimeSpan.FromSeconds(20));
            token.ThrowIfCancellationRequested();
        }
        if (failure is not null and not OperationCanceledException) Debug.WriteLine($"[VolumeCacheHost] {failure.Message}");
        return result ?? new Outcome { HandlersFailed = failure == null ? 0 : 1 };
    }

    private static Outcome Run(IReadOnlyList<string> handlers, string volumeRoot, bool purge, CancellationToken token)
    {
        var outcome = new Outcome();
        var callback = new Callback(token);
        using var root = Registry.LocalMachine.OpenSubKey(HandlersKey, writable: false);
        if (root == null) return outcome;

        foreach (var name in handlers)
        {
            token.ThrowIfCancellationRequested();
            using var key = root.OpenSubKey(name, writable: false);
            if (key?.GetValue(null) is not string clsidText || !Guid.TryParse(clsidText, out var clsid)) continue;
            if (!IsSystemHandler(clsid)) continue;

            object? instance = null;
            try
            {
                var type = Type.GetTypeFromCLSID(clsid, throwOnError: false);
                if (type == null) continue;
                instance = Activator.CreateInstance(type);
                if (instance is not IEmptyVolumeCache cache) continue;

                uint flags = 0;
                int hr;
                IntPtr display = IntPtr.Zero, description = IntPtr.Zero, button = IntPtr.Zero;
                var hkey = key.Handle.DangerousGetHandle();
                if (instance is IEmptyVolumeCache2 cache2)
                    hr = cache2.InitializeEx(hkey, volumeRoot, name, out display, out description, out button, ref flags);
                else
                    hr = cache.Initialize(hkey, volumeRoot, out display, out description, ref flags);
                Marshal.FreeCoTaskMem(display);
                Marshal.FreeCoTaskMem(description);
                Marshal.FreeCoTaskMem(button);

                if (hr == S_FALSE) { outcome.HandlersRun++; continue; } // nothing to clean on this volume
                if (hr < 0) { outcome.HandlersFailed++; continue; }

                hr = cache.GetSpaceUsed(out long used, callback);
                if (hr == E_ABORT) token.ThrowIfCancellationRequested();
                if (hr < 0) { outcome.HandlersFailed++; TryDeactivate(cache); continue; }

                if (purge && used > 0)
                {
                    callback.Freed = 0;
                    hr = cache.Purge(-1, callback); // -1 = everything the handler found
                    if (hr == E_ABORT) token.ThrowIfCancellationRequested();
                    if (hr < 0) outcome.HandlersFailed++;
                    else outcome.Bytes += callback.Freed > 0 ? Math.Min(callback.Freed, used) : used;
                }
                else if (!purge)
                {
                    outcome.Bytes += Math.Max(0, used);
                }
                outcome.HandlersRun++;
                TryDeactivate(cache);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeCacheHost] {name}: {ex.Message}");
                outcome.HandlersFailed++;
            }
            finally
            {
                if (instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            }
        }
        return outcome;
    }

    private static void TryDeactivate(IEmptyVolumeCache cache)
    {
        try { cache.Deactivate(out _); } catch { }
    }

    /// <summary>The handler's DLL must be in System32 (no third-party code runs in WinXTools).</summary>
    private static bool IsSystemHandler(Guid clsid)
    {
        try
        {
            using var server = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid:B}\InprocServer32");
            var path = Environment.ExpandEnvironmentVariables(server?.GetValue(null) as string ?? "");
            if (path.Length == 0) return false;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            return dir.Equals(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class Callback(CancellationToken token) : IEmptyVolumeCacheCallBack
    {
        public long Freed;

        public int ScanProgress(long spaceUsed, uint flags, string? status) =>
            token.IsCancellationRequested ? E_ABORT : S_OK;

        public int PurgeProgress(long spaceFreed, long spaceToFree, uint flags, string? status)
        {
            Freed = Math.Max(Freed, spaceFreed);
            return token.IsCancellationRequested ? E_ABORT : S_OK;
        }
    }

    // emptyvc.h (Windows SDK). Method order must match the vtables exactly.

    [ComImport, Guid("8FCE5227-04DA-11d1-A004-00805F8ABE06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEmptyVolumeCache
    {
        [PreserveSig] int Initialize(IntPtr hkRegKey, [MarshalAs(UnmanagedType.LPWStr)] string pcwszVolume,
            out IntPtr ppwszDisplayName, out IntPtr ppwszDescription, ref uint pdwFlags);
        [PreserveSig] int GetSpaceUsed(out long pdwlSpaceUsed, IEmptyVolumeCacheCallBack? picb);
        [PreserveSig] int Purge(long dwlSpaceToFree, IEmptyVolumeCacheCallBack? picb);
        [PreserveSig] int ShowProperties(IntPtr hwnd);
        [PreserveSig] int Deactivate(out uint pdwFlags);
    }

    [ComImport, Guid("02b7e3ba-4db3-11d2-b2d9-00c04f8eec8c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEmptyVolumeCache2
    {
        // IEmptyVolumeCache methods, then InitializeEx.
        [PreserveSig] int Initialize(IntPtr hkRegKey, [MarshalAs(UnmanagedType.LPWStr)] string pcwszVolume,
            out IntPtr ppwszDisplayName, out IntPtr ppwszDescription, ref uint pdwFlags);
        [PreserveSig] int GetSpaceUsed(out long pdwlSpaceUsed, IEmptyVolumeCacheCallBack? picb);
        [PreserveSig] int Purge(long dwlSpaceToFree, IEmptyVolumeCacheCallBack? picb);
        [PreserveSig] int ShowProperties(IntPtr hwnd);
        [PreserveSig] int Deactivate(out uint pdwFlags);
        [PreserveSig] int InitializeEx(IntPtr hkRegKey, [MarshalAs(UnmanagedType.LPWStr)] string pcwszVolume,
            [MarshalAs(UnmanagedType.LPWStr)] string pcwszKeyName, out IntPtr ppwszDisplayName, out IntPtr ppwszDescription,
            out IntPtr ppwszBtnText, ref uint pdwFlags);
    }

    [ComImport, Guid("6E793361-73C6-11D0-8469-00AA00442901"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEmptyVolumeCacheCallBack
    {
        [PreserveSig] int ScanProgress(long dwlSpaceUsed, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string? pcwszStatus);
        [PreserveSig] int PurgeProgress(long dwlSpaceFreed, long dwlSpaceToFree, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string? pcwszStatus);
    }
}

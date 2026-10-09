using System.ComponentModel;
using System.IO;

namespace NetX.App.Helpers;

/// <summary>Turns the errors users actually hit into a sentence in their language instead of a raw exception message.</summary>
public static class ErrorText
{
    public static string From(Exception ex)
    {
        var inner = ex.GetBaseException();
        int code = inner switch
        {
            Win32Exception w => w.NativeErrorCode,
            UnauthorizedAccessException => 5,
            IOException io => io.HResult & 0xFFFF,
            _ => 0
        };
        return code switch
        {
            112 or 39 => Loc.T("Err_DiskFull", "The drive is full."),
            5 => Loc.T("Err_AccessDenied", "Windows refused access (administrator rights are needed, or it is protected)."),
            32 or 33 => Loc.T("Err_InUse", "Another program is using it right now."),
            21 => Loc.T("Err_NotReady", "The drive isn't ready (unplugged or asleep)."),
            23 or 1117 => Loc.T("Err_DeviceIo", "The drive reported a read/write error — it may be failing. Back up important files."),
            _ => inner.Message
        };
    }
}

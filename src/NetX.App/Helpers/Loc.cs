using System.Windows;
using NetX.Core.Network;

namespace NetX.App.Helpers;

/// <summary>
/// Looks up strings from the active language dictionary (Languages/*.xaml)
/// for text built in code-behind, so Thai users don't get English messages.
/// </summary>
public static class Loc
{
    /// <summary>True while the Thai language dictionary is active.</summary>
    public static bool IsThai =>
        Application.Current?.Resources.MergedDictionaries
            .Any(d => d.Source?.OriginalString.Contains("th-TH", StringComparison.OrdinalIgnoreCase) == true) == true;

    public static string T(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as string ?? fallback;

    public static string F(string key, string fallback, params object[] args)
    {
        try { return string.Format(T(key, fallback), args); }
        catch (FormatException) { return string.Format(fallback, args); }
    }
}

/// <summary>Formats speeds and limits consistently across the app.</summary>
public static class SpeedFormat
{
    /// <summary>Measured speed in bytes/second, e.g. "1.25 MB/s".</summary>
    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_073_741_824) return $"{bytesPerSecond / 1_073_741_824:F2} GB/s";
        if (bytesPerSecond >= 1_048_576) return $"{bytesPerSecond / 1_048_576:F2} MB/s";
        if (bytesPerSecond >= 1024) return $"{bytesPerSecond / 1024:F1} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    /// <summary>A limit in bytes/second (or Unlimited/Blocked), e.g. "512 KB/s".</summary>
    public static string Limit(long bytesPerSecond)
    {
        if (bytesPerSecond < 0) return Loc.T("BW_Unlimited", "Unlimited");
        if (bytesPerSecond == RateLimit.Blocked) return Loc.T("BW_Blocked", "Blocked");
        if (bytesPerSecond >= 1_048_576)
        {
            double mb = bytesPerSecond / 1_048_576.0;
            return mb >= 10 ? $"{mb:F0} MB/s" : $"{mb:0.#} MB/s";
        }
        return $"{bytesPerSecond / 1024.0:0.#} KB/s";
    }

    /// <summary>A limit given in kilobits/second (ISP style), e.g. "10 Mbps".</summary>
    public static string Kbps(int kbps)
    {
        if (kbps < 0) return Loc.T("BW_Unlimited", "Unlimited");
        if (kbps == 0) return Loc.T("BW_Blocked", "Blocked");
        if (kbps >= 1_000_000) return $"{kbps / 1_000_000.0:0.#} Gbps";
        if (kbps >= 1000) return $"{kbps / 1000.0:0.#} Mbps";
        return $"{kbps} Kbps";
    }
}

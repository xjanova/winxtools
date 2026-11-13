using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using NetX.Core.System.Tweaks;

namespace NetX.Core.System.Storage;

/// <summary>Fragmentation figures from Windows' own defragmenter (Win32_DefragAnalysis).</summary>
public sealed class DefragStats
{
    public bool DefragRecommended { get; init; }
    public int TotalPercentFragmentation { get; init; }
    public int FilePercentFragmentation { get; init; }
    public int FreeSpacePercentFragmentation { get; init; }
    public long TotalFiles { get; init; }
    public long TotalFragmentedFiles { get; init; }
    public long TotalExcessFragments { get; init; }
    public double AverageFragmentsPerFile { get; init; }
    public long FragmentedFolders { get; init; }
    public long TotalMftFragments { get; init; }
    public long VolumeSize { get; init; }
    public long UsedSpace { get; init; }
    public long FreeSpace { get; init; }
    public long LargestFreeExtent { get; init; }
    public DateTime TakenAt { get; init; } = DateTime.Now;
}

public enum DefragOperation
{
    /// <summary>"/O": defragment an HDD, TRIM an SSD — what Windows' Optimize Drives does.</summary>
    Optimize,
    /// <summary>"/D": traditional defragmentation.</summary>
    Defragment,
    /// <summary>"/X": pack free space together (fewer gaps for new files).</summary>
    ConsolidateFreeSpace,
    /// <summary>"/L": tell the SSD which blocks are free (TRIM).</summary>
    Retrim
}

public enum DefragOutcome
{
    Completed,
    Cancelled,
    Failed,
    StartFailed
}

public sealed class DefragRunResult
{
    public DefragOutcome Outcome { get; init; }
    public int ExitCode { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>Last meaningful line Windows printed (already in the user's language).</summary>
    public string Message { get; init; } = "";
    public string Output { get; init; } = "";
}

/// <summary>One progress line from defrag.exe ("Defragmentation: 45% complete…", localized).</summary>
public sealed record DefragProgress(string Text, int? Percent);

/// <summary>
/// Drives Windows' own defragmenter, the same engine as "Optimize Drives", so
/// files are moved safely by the file system (every move is atomic; stopping
/// half-way leaves nothing broken). WinXTools only starts it, reads its
/// progress and asks it for its fragmentation figures. Needs administrator rights.
/// </summary>
public static partial class DefragEngine
{
    private const string ServiceName = "defragsvc";

    public static string OperationSwitch(DefragOperation operation) => operation switch
    {
        DefragOperation.Defragment => "/D",
        DefragOperation.ConsolidateFreeSpace => "/X",
        DefragOperation.Retrim => "/L",
        _ => "/O"
    };

    #region Analysis (WMI)

    /// <summary>
    /// Asks Windows to analyze the volume (Win32_Volume.DefragAnalysis). Takes
    /// seconds on an SSD and up to a few minutes on a large, busy hard disk.
    /// </summary>
    /// <returns>The figures, or null with Windows' result code in <c>code</c>.</returns>
    public static async Task<(DefragStats? Stats, int Code)> AnalyzeAsync(char letter, CancellationToken token = default)
    {
        var work = Task.Run(() => Analyze(letter));
        return await work.WaitAsync(token).ConfigureAwait(false);
    }

    private static (DefragStats? Stats, int Code) Analyze(char letter)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2",
                $"SELECT DeviceID, DriveLetter FROM Win32_Volume WHERE DriveLetter = '{char.ToUpperInvariant(letter)}:'");
            using var results = searcher.Get();
            foreach (ManagementObject volume in results)
            {
                using (volume)
                {
                    using var output = volume.InvokeMethod("DefragAnalysis", null, null);
                    int code = Convert.ToInt32(output?["ReturnValue"] ?? 11);
                    if (code != 0) return (null, code);

                    bool recommended = output?["DefragRecommended"] is bool b && b;
                    if (output?["DefragAnalysis"] is not ManagementBaseObject analysis) return (null, 11);
                    using (analysis)
                    {
                        return (new DefragStats
                        {
                            DefragRecommended = recommended,
                            TotalPercentFragmentation = (int)Read(analysis, "TotalPercentFragmentation"),
                            FilePercentFragmentation = (int)Read(analysis, "FilePercentFragmentation"),
                            FreeSpacePercentFragmentation = (int)Read(analysis, "FreeSpacePercentFragmentation"),
                            TotalFiles = Read(analysis, "TotalFiles"),
                            TotalFragmentedFiles = Read(analysis, "TotalFragmentedFiles"),
                            TotalExcessFragments = Read(analysis, "TotalExcessFragments"),
                            AverageFragmentsPerFile = ReadDouble(analysis, "AverageFragmentsPerFile"),
                            FragmentedFolders = Read(analysis, "FragmentedFolders"),
                            TotalMftFragments = Read(analysis, "TotalMFTFragments"),
                            VolumeSize = Read(analysis, "VolumeSize"),
                            UsedSpace = Read(analysis, "UsedSpace"),
                            FreeSpace = Read(analysis, "FreeSpace"),
                            LargestFreeExtent = Read(analysis, "LargestFreeSpaceExtent")
                        }, 0);
                    }
                }
            }
            return (null, 2); // volume not found = not supported
        }
        catch (UnauthorizedAccessException)
        {
            return (null, 1);
        }
        catch (ManagementException ex)
        {
            Debug.WriteLine($"[DefragEngine] Analysis failed: {ex.Message}");
            return (null, ex.ErrorCode == ManagementStatus.AccessDenied ? 1 : 11);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefragEngine] Analysis failed: {ex.Message}");
            return (null, 11);
        }
    }

    private static long Read(ManagementBaseObject obj, string name)
    {
        try { return obj[name] is { } v ? Convert.ToInt64(v) : 0; }
        catch { return 0; }
    }

    private static double ReadDouble(ManagementBaseObject obj, string name)
    {
        try { return obj[name] is { } v ? Convert.ToDouble(v) : 0; }
        catch { return 0; }
    }

    #endregion

    #region Run (defrag.exe)

    /// <summary>
    /// Runs one optimization with Windows' defragmenter and reports its
    /// progress lines. Cancelling stops the engine (it finishes the file it is
    /// moving, nothing is left half-moved).
    /// </summary>
    public static async Task<DefragRunResult> RunAsync(char letter, DefragOperation operation,
        IProgress<DefragProgress>? progress, CancellationToken token)
    {
        var live = new ConsoleOutputBuffer();
        var started = DateTime.UtcNow;
        // /U = print progress, /V = verbose statistics, /H = normal priority (the default is idle priority).
        string args = $"{char.ToUpperInvariant(letter)}: {OperationSwitch(operation)} /U /V /H";
        var run = CommandRunner.RunAsync(CommandRunner.SystemTool("defrag.exe"), args, TimeSpan.FromHours(24), token, live);

        int lastVersion = -1;
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(400, CancellationToken.None)).ConfigureAwait(false);
            int version = live.Version;
            if (version == lastVersion) continue;
            lastVersion = version;
            if (ReadProgress(live.GetText()) is { } now) progress?.Report(now);
        }

        var result = await run.ConfigureAwait(false);
        var duration = DateTime.UtcNow - started;
        var output = result.Output;

        if (result.StartFailed)
            return new DefragRunResult { Outcome = DefragOutcome.StartFailed, Duration = duration };

        if (result.Cancelled || token.IsCancellationRequested)
        {
            // defrag.exe was ended; make sure the engine behind it stops too.
            await Task.Run(StopEngine).ConfigureAwait(false);
            return new DefragRunResult { Outcome = DefragOutcome.Cancelled, Duration = duration, Output = output, ExitCode = result.ExitCode };
        }

        // defrag.exe exits with 0 whatever happened: a refusal or a failure is only
        // visible as a line of its output that ends in Windows' error code,
        // "… (0x89000024)". Judging by the exit code alone called every failure "done".
        if (FindError(output) is { } error)
        {
            return new DefragRunResult
            {
                // Someone else stopped it (Windows' own maintenance, Optimize Drives' Stop button).
                Outcome = error.Code == ErrorCancelledByUser ? DefragOutcome.Cancelled : DefragOutcome.Failed,
                ExitCode = error.Code,
                Duration = duration,
                Output = output,
                Message = error.Message
            };
        }

        return new DefragRunResult
        {
            Outcome = result.ExitCode == 0 ? DefragOutcome.Completed : DefragOutcome.Failed,
            ExitCode = result.ExitCode,
            Duration = duration,
            Output = output,
            Message = LastMeaningfulLine(output) ?? ""
        };
    }

    /// <summary>"The user cancelled the operation." (0x89000006)</summary>
    private const int ErrorCancelledByUser = unchecked((int)0x89000006);

    /// <summary>
    /// The error defrag.exe printed, if any: the first line that ends in a failure
    /// code, such as "The given volume path is invalid. (0x89000001)". The text is
    /// Windows' own, in the user's language.
    /// </summary>
    public static (string Message, int Code)? FindError(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var match = ErrorCodeRegex().Match(line);
            if (!match.Success || IsStatisticsRow(line)) continue;
            uint code = Convert.ToUInt32(match.Groups[1].Value, 16);
            if ((code & 0x80000000) == 0) continue; // not a failure code
            return (line, unchecked((int)code));
        }
        return null;
    }

    /// <summary>
    /// What to show while it runs: the newest progress line ("Defragmentation:
    /// 45% complete…"), or, before the first one, the newest line that says
    /// something ("Invoking defragmentation on Data (F:)…"). The rows of the
    /// reports defrag.exe prints with /V ("Total fragmented space = 3%") also
    /// contain a percentage, but they are figures about the drive, not progress.
    /// </summary>
    public static DefragProgress? ReadProgress(string output)
    {
        string? latest = null;
        for (int end = output.Length; end > 0;)
        {
            int start = output.LastIndexOf('\n', end - 1);
            var line = output[(start + 1)..end].Trim();
            end = start < 0 ? 0 : start;
            if (line.Length == 0 || IsStatisticsRow(line)) continue;
            if (ParsePercent(line) is int percent) return new DefragProgress(line, percent);
            if (!line.EndsWith(':')) latest ??= line; // not a report heading
        }
        return latest == null ? null : new DefragProgress(latest, null);
    }

    private static bool IsStatisticsRow(string line) => line.Contains('=');

    /// <summary>Stops the Optimize Drives service (and with it any running optimization).</summary>
    public static bool StopEngine()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            service.Refresh();
            if (service.Status is ServiceControllerStatus.Stopped) return true;
            if (service.Status is not ServiceControllerStatus.StopPending)
                service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefragEngine] Could not stop {ServiceName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The last "NN%" in a progress line, or null.</summary>
    public static int? ParsePercent(string line)
    {
        var matches = PercentRegex().Matches(line);
        if (matches.Count == 0) return null;
        return int.TryParse(matches[^1].Groups[1].Value, out int value) ? Math.Clamp(value, 0, 100) : null;
    }

    /// <summary>The last line that isn't a statistics row (those contain " = ").</summary>
    private static string? LastMeaningfulLine(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (IsStatisticsRow(lines[i]) || lines[i].EndsWith(':')) continue;
            return lines[i];
        }
        return lines.LastOrDefault();
    }

    [GeneratedRegex(@"(\d{1,3})\s*%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"\(0x([0-9A-Fa-f]{8})\)\.?$")]
    private static partial Regex ErrorCodeRegex();

    #endregion
}

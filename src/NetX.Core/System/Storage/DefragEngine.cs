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
            var line = LastLine(live.GetText());
            if (line != null) progress?.Report(new DefragProgress(line, ParsePercent(line)));
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

        return new DefragRunResult
        {
            Outcome = result.ExitCode == 0 ? DefragOutcome.Completed : DefragOutcome.Failed,
            ExitCode = result.ExitCode,
            Duration = duration,
            Output = output,
            Message = LastMeaningfulLine(output) ?? ""
        };
    }

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

    private static string? LastLine(string text)
    {
        for (int end = text.Length; end > 0;)
        {
            int start = text.LastIndexOf('\n', end - 1);
            var line = text[(start + 1)..end].Trim();
            if (line.Length > 0) return line;
            end = start < 0 ? 0 : start;
        }
        return null;
    }

    /// <summary>The last line that isn't a statistics row (those contain " = ").</summary>
    private static string? LastMeaningfulLine(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Contains(" = ") || lines[i].EndsWith(':')) continue;
            return lines[i];
        }
        return lines.LastOrDefault();
    }

    [GeneratedRegex(@"(\d{1,3})\s*%")]
    private static partial Regex PercentRegex();

    #endregion
}

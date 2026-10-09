using System.Diagnostics;
using NetX.Core.Optimization;
using NetX.Core.System.Background;
using NetX.Core.System.Storage;
using NetX.Core.System.Tweaks;

namespace NetX.Core.System;

public enum OptimizeStep { Junk, Memory, Background, Ssd, Dns }

public enum StepState { Pending, Running, Done, Skipped, Failed }

/// <summary>Progress of one step; <paramref name="Fraction"/> is the share of the whole run done so far.</summary>
public sealed record StepUpdate(OptimizeStep Step, StepState State, double Fraction, string? Note = null);

public sealed class FullOptimizeResult
{
    public long JunkBytesFreed { get; set; }
    public int JunkFiles { get; set; }
    public int JunkSkipped { get; set; }

    /// <summary>Programs whose caches were in use (closing them lets the rest go next time).</summary>
    public List<string> JunkAppsRunning { get; } = new();

    public OptimizeResult? Memory { get; set; }
    public List<string> Calmed { get; } = new();

    /// <summary>Calming busy background programs is Smart CPU's work, so it needs Pro.</summary>
    public bool CalmNeedsPro { get; set; }
    public List<char> Trimmed { get; } = new();
    public List<char> TrimFailed { get; } = new();
    public bool DnsFlushed { get; set; }

    /// <summary>The state after the run, for suggestions (start-ups, Chrome, commit).</summary>
    public BackgroundReport? Background { get; set; }
    public MemoryAssessment? MemoryAfter { get; set; }
    public List<DriveVolume> HardDisks { get; } = new();

    public Dictionary<OptimizeStep, StepState> States { get; } = new();
    public bool Cancelled { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// "1-Click Full Optimize": everything that is safe to do without asking item by
/// item, in one go — the cleaner's default categories (never the Recycle Bin or
/// anything marked for review), RAM (write pending changes, merge identical
/// pages, release the least valued cache — programs are never trimmed),
/// Efficiency mode for background programs that keep the CPU busy (nothing is
/// closed), TRIM on internal SSDs, and a fresh DNS cache. Things that need the
/// user's choice — turning start-ups off, defragmenting a hard disk, closing
/// programs — come back as suggestions instead.
/// </summary>
public static class FullOptimizer
{
    private const double CalmCpuPercent = 3.0;

    private static readonly (OptimizeStep Step, double Weight)[] Plan =
    {
        (OptimizeStep.Junk, 0.55), (OptimizeStep.Memory, 0.12), (OptimizeStep.Background, 0.12), (OptimizeStep.Ssd, 0.18), (OptimizeStep.Dns, 0.03)
    };

    public static async Task<FullOptimizeResult> RunAsync(IProgress<StepUpdate>? progress, CancellationToken token)
    {
        var result = new FullOptimizeResult();
        var clock = Stopwatch.StartNew();
        double done = 0;
        bool elevated = Autostart.IsElevated();
        foreach (var (step, _) in Plan) result.States[step] = StepState.Pending;

        void Report(OptimizeStep step, StepState state, double within = 0, string? note = null)
        {
            result.States[step] = state;
            double weight = Plan.First(p => p.Step == step).Weight;
            progress?.Report(new StepUpdate(step, state, Math.Clamp(done + weight * within, 0, 1), note));
        }

        try
        {
            // 1. Junk files: the categories the cleaner ticks by default.
            Report(OptimizeStep.Junk, StepState.Running);
            var targets = SystemCleaner.Categories.Where(c => c.DefaultOn).Select(c => c.Target).ToList();
            for (int i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var target = targets[i];
                Report(OptimizeStep.Junk, StepState.Running, i / (double)targets.Count, target.ToString());
                var cleaned = await Task.Run(() => SystemCleaner.Clean(target, token), token).ConfigureAwait(false);
                result.JunkBytesFreed += Math.Max(0, cleaned.BytesFreed);
                result.JunkFiles += Math.Max(0, cleaned.FilesDeleted);
                result.JunkSkipped += cleaned.FilesSkipped;
                if (cleaned.NoteCode == "AppRunning" && !string.IsNullOrEmpty(cleaned.NoteArg))
                    result.JunkAppsRunning.AddRange(cleaned.NoteArg.Split(", ").Where(a => !result.JunkAppsRunning.Contains(a)));
            }
            Report(OptimizeStep.Junk, StepState.Done, 1);
            done += Plan[0].Weight;

            // 2. Memory (needs administrator rights).
            token.ThrowIfCancellationRequested();
            if (MemoryCleaner.IsElevated)
            {
                Report(OptimizeStep.Memory, StepState.Running);
                result.Memory = await RamOptimizer.Instance.RunAsync(
                    MemoryCleanItems.FlushModifiedList | MemoryCleanItems.PurgeLowPriorityStandby | MemoryCleanItems.CombinePages).ConfigureAwait(false);
                Report(OptimizeStep.Memory, result.Memory.AnyOperationSucceeded ? StepState.Done : StepState.Failed, 1);
            }
            else
            {
                Report(OptimizeStep.Memory, StepState.Skipped, 1, "admin");
            }
            done += Plan[1].Weight;

            // 3. Background programs that keep the CPU busy: Efficiency mode, nothing closed (Pro).
            //    The scan itself still runs without Pro: the suggestions come from it.
            token.ThrowIfCancellationRequested();
            Report(OptimizeStep.Background, StepState.Running);
            var report = await Task.Run(() => BackgroundScanner.Scan(), token).ConfigureAwait(false);
            result.Background = report;
            result.CalmNeedsPro = !TrialService.Instance.HasProAccess;
            foreach (var app in result.CalmNeedsPro ? Enumerable.Empty<BackgroundApp>() : report.Apps)
            {
                if (app.InEfficiencyMode || app.HelperOfOpenApp || app.CpuLastMinute < CalmCpuPercent) continue;
                if (app.Class is not (ProgramClass.App or ProgramClass.Updater or ProgramClass.Suspicious)) continue;
                if (app.Name.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)) continue;
                if (app.Processes.Count(p => Efficiency.Set(p, on: true)) > 0) result.Calmed.Add(app.DisplayName);
            }
            if (result.CalmNeedsPro) Report(OptimizeStep.Background, StepState.Skipped, 1, "pro");
            else Report(OptimizeStep.Background, StepState.Done, 1);
            done += Plan[2].Weight;

            // 4. TRIM on internal SSDs (Windows' own optimizer; needs administrator rights).
            token.ThrowIfCancellationRequested();
            var volumes = await Task.Run(DriveInventory.GetVolumes, token).ConfigureAwait(false);
            result.HardDisks.AddRange(volumes.Where(v => v.Disk?.IsHdd == true && v.IsNtfs));
            var ssds = volumes.Where(v => v.Disk?.IsSsd == true && v.IsNtfs && !v.IsRemovable && v.Disk.Bus is not (DriveBus.Usb or DriveBus.SdCard)).ToList();
            bool busy = DefragSession.Current is { IsRunning: true } || SmartPlacementSession.Current is { IsRunning: true };
            if (!elevated || busy || ssds.Count == 0)
            {
                Report(OptimizeStep.Ssd, StepState.Skipped, 1, !elevated ? "admin" : busy ? "busy" : "none");
            }
            else
            {
                for (int i = 0; i < ssds.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    Report(OptimizeStep.Ssd, StepState.Running, i / (double)ssds.Count, $"{ssds[i].Letter}:");
                    var run = await DefragEngine.RunAsync(ssds[i].Letter, DefragOperation.Retrim, null, token).ConfigureAwait(false);
                    (run.Outcome == DefragOutcome.Completed ? result.Trimmed : result.TrimFailed).Add(ssds[i].Letter);
                }
                Report(OptimizeStep.Ssd, result.Trimmed.Count > 0 ? StepState.Done : StepState.Failed, 1);
            }
            done += Plan[3].Weight;

            // 5. DNS cache.
            token.ThrowIfCancellationRequested();
            Report(OptimizeStep.Dns, StepState.Running);
            var dns = await CommandRunner.RunAsync(CommandRunner.SystemTool("ipconfig.exe"), "/flushdns", TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            result.DnsFlushed = dns.ExitCode == 0;
            Report(OptimizeStep.Dns, result.DnsFlushed ? StepState.Done : StepState.Failed, 1);
            done = 1;

            result.MemoryAfter = await Task.Run(() => RamOptimizer.Instance.Assess(), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            foreach (var step in result.States.Where(s => s.Value is StepState.Pending or StepState.Running).Select(s => s.Key).ToList())
                result.States[step] = StepState.Skipped;
        }
        result.Duration = clock.Elapsed;
        return result;
    }
}

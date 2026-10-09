using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NetX.App.Helpers;
using NetX.App.Views;
using NetX.Core.Data;
using NetX.Core.Optimization;
using NetX.Core.System;
using NetX.Core.System.Background;

namespace NetX.App.Controls;

/// <summary>
/// The Dashboard's "1-Click Full Optimize": one big button that runs every safe
/// optimization (<see cref="FullOptimizer"/>) with a live ring and step list,
/// then shows what came back and what is worth a look. One run for the whole
/// app: it keeps going if the user opens another page, and the card picks it
/// up again on the way back. Asks once, the first time, because it deletes files.
/// </summary>
public partial class OneClickOptimizeCard : UserControl
{
    private const string ConfirmedKey = "OneClick.Confirmed";
    private const double RingDiameter = 150, RingThickness = 7;

    private static Task<FullOptimizeResult>? _running;
    private static CancellationTokenSource? _cts;
    private static FullOptimizeResult? _last;
    private static readonly Dictionary<OptimizeStep, (StepState State, string? Note)> StepStates = new();
    private static double _fraction;
    private static event Action? Changed;

    private readonly ObservableCollection<StepRow> _rows = new();
    private bool _loaded;

    public OneClickOptimizeCard()
    {
        InitializeComponent();
        foreach (var step in Enum.GetValues<OptimizeStep>()) _rows.Add(new StepRow(step));
        StepsList.ItemsSource = _rows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static bool IsRunning => _running is { IsCompleted: false };

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        Changed += OnChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        Changed -= OnChanged;
        GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void OnChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_loaded) Refresh();
    });

    #region Run

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning)
        {
            // The button is a Stop button while it runs: the current step finishes, the rest is skipped.
            _cts?.Cancel();
            GoText.SetResourceReference(TextBlock.TextProperty, "OneClick_Stopping");
            GoButton.IsEnabled = false;
            return;
        }
        if (!Confirmed()) return;

        _last = null;
        StepStates.Clear();
        _fraction = 0;
        _cts = new CancellationTokenSource();
        var progress = new Progress<StepUpdate>(update =>
        {
            StepStates[update.Step] = (update.State, update.Note);
            _fraction = update.Fraction;
            Changed?.Invoke();
        });
        _running = RunAndKeep(progress, _cts.Token);
        Changed?.Invoke();
    }

    private static async Task<FullOptimizeResult> RunAndKeep(IProgress<StepUpdate> progress, CancellationToken token)
    {
        try
        {
            var result = await FullOptimizer.RunAsync(progress, token);
            _last = result;
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OneClick] {ex}");
            _last = new FullOptimizeResult { Cancelled = true };
            return _last;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Changed?.Invoke();
        }
    }

    /// <summary>The first time only: it deletes files, so the user hears what it does before it does it.</summary>
    private static bool Confirmed()
    {
        try
        {
            if (DatabaseService.Instance.GetSetting(ConfirmedKey) == "1") return true;
        }
        catch
        {
        }
        var answer = MessageBox.Show(Loc.T("OneClick_Confirm",
                "1-Click Full Optimize will:\n\n• delete junk files in the cleaner's safe categories (temp files, caches, old CapCut versions, crash reports… — never your own files or the Recycle Bin)\n• free RAM without slowing your programs\n• put background programs that keep the CPU busy into Efficiency mode (nothing is closed) — Pro\n• run TRIM on your SSDs\n• refresh the DNS cache\n\nRun it now? From now on the button starts right away."),
            Loc.T("OneClick_Title", "1-Click Full Optimize"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return false;
        try { DatabaseService.Instance.SetSetting(ConfirmedKey, "1"); } catch { }
        return true;
    }

    #endregion

    #region Showing it

    private void Refresh()
    {
        bool running = IsRunning;
        var last = running ? null : _last;

        foreach (var row in _rows)
        {
            var (state, note) = StepStates.TryGetValue(row.Step, out var s) ? s : (StepState.Pending, null);
            row.Update(state, last != null ? ResultNote(row.Step, last) : RunningNote(row.Step, state, note));
        }

        // Ring: how far the run is; full and green when done.
        double fraction = running ? _fraction : last != null ? 1 : 0;
        double circumference = Math.PI * (RingDiameter - RingThickness);
        double filled = fraction * circumference;
        ProgressRing.StrokeDashArray = new DoubleCollection { filled / RingThickness, (circumference - filled) / RingThickness + 1 };
        ProgressRing.Visibility = fraction > 0.005 ? Visibility.Visible : Visibility.Collapsed; // a round cap would draw a dot at 0

        GoButton.IsEnabled = !running || _cts is { IsCancellationRequested: false };
        GoIcon.Data = (Geometry)FindResource(running ? "StopSquareIcon" : "BoltIcon");
        GoText.SetResourceReference(TextBlock.TextProperty,
            running ? (_cts?.IsCancellationRequested == true ? "OneClick_Stopping" : "OneClick_Stop") : last != null ? "OneClick_Again" : "OneClick_Go");
        Spinner.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        Animate(running);

        if (last == null)
        {
            SummaryText.Visibility = Visibility.Collapsed;
            SuggestionsList.ItemsSource = null;
            Subtitle.SetResourceReference(TextBlock.TextProperty, running ? "OneClick_Working" : "OneClick_Subtitle");
            return;
        }

        Subtitle.Text = Loc.F("OneClick_DoneIn", "Done in {0:0} s.", last.Duration.TotalSeconds);
        SummaryText.Text = Summary(last);
        SummaryText.Visibility = Visibility.Visible;
        SuggestionsList.ItemsSource = Suggestions(last);
    }

    private void Animate(bool running)
    {
        if (running)
        {
            if (SpinnerRotate.HasAnimatedProperties) return;
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });
            Pulse(1.0, 1.16, 0.7);
        }
        else
        {
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            Pulse(1.0, 1.08, 1.8);
        }
    }

    private void Pulse(double from, double to, double seconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static string? RunningNote(OptimizeStep step, StepState state, string? note) => state switch
    {
        StepState.Running when step == OptimizeStep.Ssd && note != null => Loc.F("OneClick_TrimmingDrive", "TRIM {0}", note),
        StepState.Running => Loc.T("OneClick_StepRunning", "working…"),
        StepState.Skipped when note == "admin" => Loc.T("OneClick_NeedsAdmin", "needs administrator"),
        StepState.Skipped when note == "pro" => Loc.T("OneClick_ProOnly", "Pro feature (Smart CPU)"),
        StepState.Skipped => Loc.T("OneClick_StepSkipped", "skipped"),
        StepState.Done => Loc.T("OneClick_StepDone", "done"),
        StepState.Failed => Loc.T("OneClick_StepFailed", "didn't work"),
        _ => StepRow.Describe(step)
    };

    private static string ResultNote(OptimizeStep step, FullOptimizeResult r)
    {
        var state = r.States.GetValueOrDefault(step);
        if (state == StepState.Skipped)
            return step == OptimizeStep.Background && r.CalmNeedsPro
                ? Loc.T("OneClick_ProOnly", "Pro feature (Smart CPU)")
                : step is OptimizeStep.Memory or OptimizeStep.Ssd && !MemoryCleaner.IsElevated
                ? Loc.T("OneClick_NeedsAdmin", "needs administrator")
                : step == OptimizeStep.Ssd && r.Trimmed.Count == 0 && r.TrimFailed.Count == 0 && !r.Cancelled
                    ? Loc.T("OneClick_NoSsd", "no internal SSD (or another drive job is running)")
                    : Loc.T("OneClick_StepSkipped", "skipped");
        return step switch
        {
            OptimizeStep.Junk => Loc.F("OneClick_JunkNote", "{0} freed ({1:N0} files)", CleanerView.FormatSize(r.JunkBytesFreed), r.JunkFiles),
            OptimizeStep.Memory when r.Memory != null => RamOptimizerView.SummaryText(r.Memory),
            OptimizeStep.Background => r.Calmed.Count > 0
                ? Loc.F("OneClick_CalmedNote", "{0:N0} calmed: {1}", r.Calmed.Count, string.Join(", ", r.Calmed.Take(3)))
                : Loc.T("OneClick_NothingBusy", "nothing was hogging the CPU"),
            OptimizeStep.Ssd => r.Trimmed.Count > 0
                ? Loc.F("OneClick_TrimNote", "{0} done", string.Join(" ", r.Trimmed.Select(l => $"{l}:")))
                : Loc.T("OneClick_StepFailed", "didn't work"),
            OptimizeStep.Dns => r.DnsFlushed ? Loc.T("OneClick_DnsNote", "refreshed") : Loc.T("OneClick_StepFailed", "didn't work"),
            _ => ""
        };
    }

    private static string Summary(FullOptimizeResult r)
    {
        var parts = new List<string> { Loc.F("OneClick_SumJunk", "Freed {0}", CleanerView.FormatSize(r.JunkBytesFreed)) };
        if (r.Memory is { AnyOperationSucceeded: true } m)
        {
            long merged = m.Operations.Where(o => o.Operation == MemoryOperation.CombinePages && o.Succeeded).Sum(o => o.AmountMB);
            parts.Add(merged > 0 ? Loc.F("OneClick_SumMerged", "RAM {0} merged", RamOptimizerView.FormatMB(merged))
                                 : Loc.F("OneClick_SumRam", "RAM +{0}", RamOptimizerView.FormatMB(Math.Max(0, m.MemoryFreedMB))));
        }
        if (r.Calmed.Count > 0) parts.Add(Loc.F("OneClick_SumCalmed", "{0:N0} background programs calmed", r.Calmed.Count));
        if (r.Trimmed.Count > 0) parts.Add(Loc.F("OneClick_SumTrim", "SSD TRIM {0}", string.Join(" ", r.Trimmed.Select(l => $"{l}:"))));
        var text = string.Join(" · ", parts);
        return r.Cancelled ? Loc.F("OneClick_Stopped", "Stopped — {0}", text) : text;
    }

    private static List<SuggestionRow> Suggestions(FullOptimizeResult r)
    {
        var list = new List<SuggestionRow>();
        var main = Application.Current.MainWindow as MainWindow;
        bool admin = MemoryCleaner.IsElevated;

        if (r.Background is { } report)
        {
            int startups = report.Autostart.Count(e => e.ExecutablePath != null &&
                new AutostartRow(e, ProgramCatalog.Describe(e.ExecutablePath), null, admin).IsRecommended);
            if (startups > 0)
                list.Add(new SuggestionRow(Loc.F("OneClick_SugStartup", "{0:N0} programs start with Windows without needing to.", startups),
                    Loc.T("OneClick_Review", "Review"), () => main?.NavigateTo("SmartCpu")));
            if (report.Chrome is { HasWindow: false })
                list.Add(new SuggestionRow(Loc.T("OneClick_SugChrome", "Chrome keeps running in the background after you close it."),
                    Loc.T("OneClick_Fix", "Fix"), () => main?.NavigateTo("SmartCpu")));
        }
        if (r.MemoryAfter is { Pressure: MemoryPressure.CommitHigh } memory)
            list.Add(new SuggestionRow(Loc.F("OneClick_SugCommit", "Windows is close to running out of memory ({0}% committed) — closing a program helps: {1}.",
                    memory.CommitPercent, string.Join(", ", memory.TopApps.Take(3).Select(a => a.DisplayName))),
                Loc.T("OneClick_See", "See"), () => main?.NavigateTo("RamOptimizer")));
        if (r.HardDisks.Count > 0)
            list.Add(new SuggestionRow(Loc.F("OneClick_SugHdd", "{0} is a hard disk: arranging its program files or defragmenting can speed it up (takes a while, so it isn't part of 1-Click).",
                    string.Join(", ", r.HardDisks.Select(v => $"{v.Letter}:"))),
                Loc.T("OneClick_Open", "Open"), () => main?.NavigateTo("Defrag")));
        if (r.JunkAppsRunning.Count > 0)
            list.Add(new SuggestionRow(Loc.F("OneClick_SugAppsOpen", "Close {0} to clean their caches as well.", string.Join(", ", r.JunkAppsRunning.Take(4))),
                Loc.T("OneClick_Open", "Open"), () => main?.NavigateTo("Cleaner")));
        if (!admin)
            list.Add(new SuggestionRow(Loc.T("OneClick_SugAdmin", "Run WinXTools as administrator to also free RAM and TRIM your SSDs."),
                Loc.T("OneClick_Ok", "OK"), null));
        return list;
    }

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SuggestionRow row }) row.Action?.Invoke();
    }

    #endregion
}

public sealed class StepRow : INotifyPropertyChanged
{
    public StepRow(OptimizeStep step)
    {
        Step = step;
        Name = step switch
        {
            OptimizeStep.Junk => Loc.T("OneClick_StepJunk", "Junk files"),
            OptimizeStep.Memory => Loc.T("OneClick_StepMemory", "RAM"),
            OptimizeStep.Background => Loc.T("OneClick_StepBackground", "Background CPU"),
            OptimizeStep.Ssd => Loc.T("OneClick_StepSsd", "SSD TRIM"),
            _ => Loc.T("OneClick_StepDns", "DNS cache")
        };
        Update(StepState.Pending, Describe(step));
    }

    public OptimizeStep Step { get; }
    public string Name { get; }
    public string Glyph { get; private set; } = "○";
    public Brush GlyphBrush { get; private set; } = Brushes.Gray;
    public string? Note { get; private set; }

    public static string Describe(OptimizeStep step) => step switch
    {
        OptimizeStep.Junk => Loc.T("OneClick_DescJunk", "temp, caches, old versions"),
        OptimizeStep.Memory => Loc.T("OneClick_DescMemory", "without slowing programs"),
        OptimizeStep.Background => Loc.T("OneClick_DescBackground", "Efficiency mode, nothing closed"),
        OptimizeStep.Ssd => Loc.T("OneClick_DescSsd", "internal SSDs"),
        _ => Loc.T("OneClick_DescDns", "fresh name lookups")
    };

    public void Update(StepState state, string? note)
    {
        (Glyph, var key) = state switch
        {
            StepState.Running => ("◐", "AccentPrimaryBrush"),
            StepState.Done => ("✓", "SuccessBrush"),
            StepState.Skipped => ("–", "TextTertiaryBrush"),
            StepState.Failed => ("✗", "DangerBrush"),
            _ => ("○", "TextTertiaryBrush")
        };
        GlyphBrush = Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
        Note = note;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlyphBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Note)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record SuggestionRow(string Text, string ActionText, Action? Action);

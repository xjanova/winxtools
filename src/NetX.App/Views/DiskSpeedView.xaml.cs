using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using NetX.App.Helpers;
using NetX.Core.Helpers;
using NetX.Core.System.Storage;
using SkiaSharp;

namespace NetX.App.Views;

public partial class DiskSpeedView : Page
{
    private static char? _lastLetter;

    /// <summary>A test started from this page (one per drive); several can run at once on different disks.</summary>
    private sealed class LiveRun
    {
        public required SpeedDriveItem Item { get; init; }
        public BenchRun Run { get; set; } = new();
        public ObservableCollection<ObservablePoint> Surface { get; } = new();
        public bool Running { get; set; }
        public bool WithSurface { get; set; }
        public double Fraction { get; set; }
        public string Status { get; set; } = "";
        public double CurrentMBps { get; set; }
    }

    private readonly ObservableCollection<SpeedDriveItem> _drives = new();
    private readonly Dictionary<char, LiveRun> _live = new();
    private readonly ObservableCollection<ObservablePoint> _shownSurface = new();
    private SpeedDriveItem? _focused;
    private DriveExpectation? _expect;
    private ModelStats? _crowd;
    private BenchRun? _shown, _compare;
    private CancellationTokenSource? _cts;
    private bool _isActive;
    private int _focusVersion;

    // Skia draws chart text itself; it needs a font that has Thai letters.
    private static readonly SKTypeface? ChartFace = SKFontManager.Default.MatchCharacter('ก') ?? SKTypeface.Default;

    private static readonly SKColor ThisColor = SKColor.Parse("#00c8ff");
    private static readonly SKColor CompareColor = SKColor.Parse("#b46cff");
    private static readonly SKColor RatedColor = SKColor.Parse("#f59e0b");
    private static readonly SKColor CrowdColor = SKColor.Parse("#10b981");

    private bool AnyRunning => _live.Values.Any(l => l.Running);

    public DiskSpeedView()
    {
        InitializeComponent();
        DriveList.ItemsSource = _drives;
        ShareBox.IsChecked = DiskStatsClient.ShareEnabled;
        Loaded += OnLoaded;
        Unloaded += (s, e) =>
        {
            _isActive = false;
            _cts?.Cancel(); // leaving the page stops the tests; their files are deleted on close
        };
    }

    #region Lifetime and drive selection

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isActive = true;
        SetupCharts();
        List<DriveVolume> volumes;
        try { volumes = await Task.Run(DriveInventory.GetVolumes); }
        catch { volumes = new List<DriveVolume>(); }
        if (!_isActive) return;

        _drives.Clear();
        var history = BenchHistory.Load();
        foreach (var v in volumes)
        {
            var item = new SpeedDriveItem(v);
            var latest = history.FirstOrDefault(r => SameDrive(r, v));
            if (latest != null && BenchScore.Compute(latest) is { } score) item.SetScore(score.Total);
            _drives.Add(item);
        }
        var pick = _drives.FirstOrDefault(d => d.Volume.Letter == _lastLetter)
                   ?? _drives.FirstOrDefault(d => d.Volume.IsSystem) ?? _drives.FirstOrDefault();
        if (pick == null)
        {
            LiveText.Text = Loc.T("Defrag_NoDrives", "No drives found.");
            StartButton.IsEnabled = false;
            return;
        }
        pick.IsChecked = true;
        pick.IsSelected = true;
        if (_focused != pick) Focus(pick);
    }

    private void DriveCard_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: SpeedDriveItem item } && item != _focused) Focus(item);
    }

    /// <summary>Shows one drive's details (its running test, or its latest saved one).</summary>
    private void Focus(SpeedDriveItem item)
    {
        _focused = item;
        _lastLetter = item.Volume.Letter;
        _expect = DriveReference.Expect(item.Volume.Disk);
        _crowd = null;
        ShowExpectation();
        _ = LoadCrowdAsync(item, ++_focusVersion);

        var history = HistoryFor(item.Volume);
        if (_live.TryGetValue(item.Volume.Letter, out var live))
        {
            _shown = live.Run;
            _compare ??= history.FirstOrDefault(r => r.Id != live.Run.Id);
        }
        else
        {
            _shown = history.FirstOrDefault();
            _compare = history.Skip(1).FirstOrDefault();
        }
        if (_compare != null && !SameDrive(_compare, item.Volume)) _compare = history.FirstOrDefault(r => r.Id != _shown?.Id);
        FillCompareBox(history);
        ShowHistory(history);
        ShowRun(_shown, live);
        UpdateControls();
    }

    private static bool SameDrive(BenchRun run, DriveVolume volume) =>
        run.Letter == volume.Letter && run.DiskModel == (volume.Disk?.Model ?? "");

    private static List<BenchRun> HistoryFor(DriveVolume volume) =>
        BenchHistory.Load().Where(r => SameDrive(r, volume)).OrderByDescending(r => r.Time).ToList();

    private async Task LoadCrowdAsync(SpeedDriveItem item, int version)
    {
        CrowdText.Text = Loc.T("Speed_CrowdLoading", "Asking xman studio…");
        var stats = await DiskStatsClient.GetStatsAsync(item.Volume.Disk);
        if (!_isActive || version != _focusVersion) return;
        _crowd = stats;
        ShowCrowd();
        UpdateCharts();
    }

    #endregion

    #region Running tests (several drives at once)

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (AnyRunning) return;
        var chosen = _drives.Where(d => d.IsChecked).ToList();
        if (chosen.Count == 0 && _focused != null) chosen.Add(_focused);
        if (chosen.Count == 0) return;

        long wanted = (long)int.Parse((string)((ComboBoxItem)SizeBox.SelectedItem).Tag) << 20;
        var options = new BenchOptions
        {
            FileBytes = wanted,
            Duration = TimeSpan.FromSeconds(int.Parse((string)((ComboBoxItem)DurationBox.SelectedItem).Tag)),
            IncludeWrites = WritesBox.IsChecked == true,
            IncludeSurface = SurfaceBox.IsChecked == true && AdminHelper.IsRunAsAdmin(),
            Label = (string)((ComboBoxItem)LabelBox.SelectedItem).Tag
        };

        // Drives that can't be tested now are reported, the rest go ahead.
        var problems = new List<string>();
        var ready = new List<SpeedDriveItem>();
        foreach (var item in chosen)
        {
            var v = item.Volume;
            if (DefragSession.Current is { IsRunning: true } d && d.Letter == v.Letter)
                problems.Add(Loc.F("Speed_SkipDefrag", "{0}: being defragmented", v.DisplayName));
            else if (DiskBenchmark.FitFileSize(v, wanted) == null)
                problems.Add(Loc.F("Speed_SkipSpace", "{0}: not enough free space (needs 320 MB)", v.DisplayName));
            else
                ready.Add(item);
        }
        if (problems.Count > 0)
            MessageBox.Show(Loc.T("Speed_Skipped", "These drives are skipped:") + "\n\n" + string.Join("\n", problems),
                Loc.T("Speed_Title", "Disk speed test"), MessageBoxButton.OK, MessageBoxImage.Information);
        if (ready.Count == 0) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        foreach (var item in ready)
        {
            _live[item.Volume.Letter] = new LiveRun
            {
                Item = item,
                Run = new BenchRun { Letter = item.Volume.Letter, Label = options.Label, DiskModel = item.Volume.Disk?.Model ?? "" },
                Running = true,
                WithSurface = options.IncludeSurface && item.Volume.DiskNumber != null,
                Status = Loc.T("Speed_Queued", "Waiting for the same disk…")
            };
            item.StartProgress(Loc.T("Speed_Queued", "Waiting for the same disk…"));
        }
        if (_focused != null && _live.ContainsKey(_focused.Volume.Letter)) Focus(_focused);
        UpdateControls();

        // Volumes on the same physical disk share it, so they run one after another;
        // different disks run in parallel.
        var groups = ready.GroupBy(i => i.Volume.DiskNumber ?? -1 - i.Volume.Letter).ToList();
        await Task.WhenAll(groups.Select(g => RunGroupAsync(g.ToList(), options, token)));

        if (!_isActive) return;
        UpdateControls();
        if (_focused != null) Focus(_focused);
    }

    private async Task RunGroupAsync(List<SpeedDriveItem> items, BenchOptions options, CancellationToken token)
    {
        List<SurfacePoint>? diskSurface = null;
        double? diskAccess = null;
        foreach (var item in items)
        {
            var live = _live[item.Volume.Letter];
            if (token.IsCancellationRequested)
            {
                FinishLive(live, Loc.T("Speed_Cancelled", "Stopped."));
                continue;
            }

            // The whole-disk curve belongs to the disk: read it once per disk.
            var mine = new BenchOptions
            {
                FileBytes = options.FileBytes,
                Duration = options.Duration,
                IncludeWrites = options.IncludeWrites,
                IncludeSurface = options.IncludeSurface && diskSurface == null,
                Label = options.Label
            };
            var progress = new Progress<BenchProgress>(p => OnProgress(live, p));
            try
            {
                var run = await DiskBenchmark.RunAsync(item.Volume, mine, progress, token);
                if (diskSurface == null && run.Surface.Count > 0)
                {
                    diskSurface = run.Surface;
                    diskAccess = run.AccessTimeMs;
                }
                else if (diskSurface != null && run.Surface.Count == 0)
                {
                    run.Surface = diskSurface;
                    run.AccessTimeMs = diskAccess;
                }

                BenchHistory.Add(run);
                live.Run = run;
                var score = BenchScore.Compute(run);
                if (score != null) item.SetScore(score.Total);
                FinishLive(live, Loc.F("Speed_Done", "Finished — {0} MB test file, {1} s per test.", run.FileMB, run.SecondsPerTest));
                _ = ShareAsync(run, item.Volume.Disk);
            }
            catch (OperationCanceledException)
            {
                FinishLive(live, Loc.T("Speed_Cancelled", "Stopped. The test file was deleted; this partial result is not saved."));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DiskSpeedView] {item.Volume.Letter}: {ex}");
                FinishLive(live, Loc.F("Speed_Failed", "The test stopped: {0}", ErrorText.From(ex)));
            }
        }
    }

    private async Task ShareAsync(BenchRun run, PhysicalDisk? disk)
    {
        if (!DiskStatsClient.ShareEnabled) return;
        bool sent = await DiskStatsClient.UploadAsync(run, disk);
        if (sent && _isActive && _focused?.Volume.Disk?.Model == disk?.Model && _focused != null)
            _ = LoadCrowdAsync(_focused, ++_focusVersion);
    }

    private void FinishLive(LiveRun live, string status)
    {
        live.Running = false;
        live.Status = status;
        live.Fraction = 1;
        live.Item.StopProgress();
        if (!_isActive) return;
        if (live.Item == _focused)
        {
            LiveText.Text = status;
            LiveSpeed.Text = "";
            RunProgress.Visibility = Visibility.Collapsed;
        }
        UpdateControls();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopButton.IsEnabled = false;
    }

    private void OnProgress(LiveRun live, BenchProgress p)
    {
        if (!_isActive) return;
        live.Fraction = p.Fraction;
        live.CurrentMBps = p.CurrentMBps;
        live.Status = p.Stage switch
        {
            BenchStage.Preparing => Loc.F("Speed_Preparing", "Writing the test file… {0:0}%", p.Fraction * 100),
            BenchStage.Testing => Loc.F("Speed_Testing", "Testing {0}…", PatternTitle(p.PatternId)),
            BenchStage.Surface => Loc.F("Speed_Surface", "Reading the disk surface… {0:0}%", p.Fraction * 100),
            BenchStage.AccessTime => Loc.T("Speed_Access", "Measuring random access time…"),
            _ => live.Status
        };

        // Overall progress on the card: the file tests are about 70% of the work when the
        // whole-disk curve is read too, and nearly all of it otherwise.
        double testsShare = live.WithSurface ? 0.65 : 0.95;
        double overall = p.Stage switch
        {
            BenchStage.Preparing => p.Fraction * 0.05,
            BenchStage.Testing => 0.05 + p.Fraction * testsShare,
            BenchStage.Surface => 0.70 + p.Fraction * 0.25,
            BenchStage.AccessTime => 0.95 + p.Fraction * 0.05,
            _ => 1
        };
        live.Item.UpdateProgress(overall, $"{live.Status} {p.CurrentMBps:N0} MB/s");

        if (p.Finished != null)
        {
            live.Run.Results.RemoveAll(r => r.PatternId == p.Finished.PatternId);
            live.Run.Results.Add(p.Finished);
        }
        if (p.Point != null)
        {
            live.Run.Surface.Add(p.Point);
            live.Surface.Add(new ObservablePoint(p.Point.Position * 100, p.Point.MBps));
        }

        if (live.Item != _focused) return;
        LiveText.Text = live.Status;
        LiveSpeed.Text = p.CurrentMBps > 0 ? $"{p.CurrentMBps:N1} MB/s" : "";
        RunProgress.Visibility = Visibility.Visible;
        RunProgress.Value = overall;
        if (p.Finished != null)
        {
            FillCell(p.Finished);
            ShowScore(live.Run);
            UpdateBars();
        }
        if (p.Point != null) _shownSurface.Add(new ObservablePoint(p.Point.Position * 100, p.Point.MBps));
    }

    private void UpdateControls()
    {
        bool running = AnyRunning;
        StartButton.IsEnabled = !running;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = running && _cts is { IsCancellationRequested: false };
        foreach (var d in _drives) d.IsEnabled = !running;
        SizeBox.IsEnabled = DurationBox.IsEnabled = WritesBox.IsEnabled = LabelBox.IsEnabled = !running;
        CompareBox.IsEnabled = !running;
        SurfaceBox.IsEnabled = !running && AdminHelper.IsRunAsAdmin();
        if (!AdminHelper.IsRunAsAdmin()) SurfaceBox.IsChecked = false;
    }

    private void ShareBox_Click(object sender, RoutedEventArgs e) => DiskStatsClient.ShareEnabled = ShareBox.IsChecked == true;

    #endregion

    #region Results

    private void ClearResults()
    {
        foreach (var (value, detail) in Cells())
        {
            value.Text = "—";
            detail.Text = "";
        }
        ExtraText.Text = "";
        VerdictBadge.Visibility = Visibility.Collapsed;
        FindingList.ItemsSource = null;
        ScoreValue.Text = "—";
        ScoreTier.Text = "";
        ScoreParts.Text = "";
        ScoreDelta.Text = "";
    }

    private IEnumerable<(TextBlock Value, TextBlock Detail)> Cells() =>
    [
        (SeqQ8R, SeqQ8RDetail), (SeqQ8W, SeqQ8WDetail), (SeqQ1R, SeqQ1RDetail), (SeqQ1W, SeqQ1WDetail),
        (RndQ32R, RndQ32RDetail), (RndQ32W, RndQ32WDetail), (RndQ1R, RndQ1RDetail), (RndQ1W, RndQ1WDetail)
    ];

    private (TextBlock Value, TextBlock Detail)? CellFor(string patternId) => patternId switch
    {
        "SEQ1M Q8T1" => (SeqQ8R, SeqQ8RDetail),
        "SEQ1M Q8T1 W" => (SeqQ8W, SeqQ8WDetail),
        "SEQ1M Q1T1" => (SeqQ1R, SeqQ1RDetail),
        "SEQ1M Q1T1 W" => (SeqQ1W, SeqQ1WDetail),
        "RND4K Q32T1" => (RndQ32R, RndQ32RDetail),
        "RND4K Q32T1 W" => (RndQ32W, RndQ32WDetail),
        "RND4K Q1T1" => (RndQ1R, RndQ1RDetail),
        "RND4K Q1T1 W" => (RndQ1W, RndQ1WDetail),
        _ => null
    };

    private void FillCell(BenchResult r)
    {
        if (CellFor(r.PatternId) is not var (value, detail)) return;
        value.Text = r.MBps >= 100 ? $"{r.MBps:N0}" : $"{r.MBps:N2}";
        detail.Text = r.BlockBytes <= 4096
            ? $"{r.Iops:N0} IOPS · {FormatLatency(r.AvgLatencyMs)}"
            : $"MB/s · {FormatLatency(r.AvgLatencyMs)}";
    }

    private static string FormatLatency(double ms) => ms >= 1 ? $"{ms:0.0} ms" : $"{ms * 1000:0} µs";

    private void ShowRun(BenchRun? run, LiveRun? live)
    {
        ClearResults();
        _shownSurface.Clear();
        if (run == null)
        {
            LiveText.Text = Loc.T("Speed_Hint", "Pick a drive and press Start.");
            LiveSpeed.Text = "";
            RunProgress.Visibility = Visibility.Collapsed;
            UpdateCharts();
            return;
        }
        foreach (var r in run.Results) FillCell(r);
        foreach (var p in run.Surface) _shownSurface.Add(new ObservablePoint(p.Position * 100, p.MBps));
        ShowScore(run);

        if (live is { Running: true })
        {
            LiveText.Text = live.Status;
            RunProgress.Visibility = Visibility.Visible;
            RunProgress.Value = live.Fraction;
        }
        else
        {
            LiveText.Text = live?.Status ?? Loc.F("Speed_RunInfo", "Tested {0:g} · {1}", run.Time, LabelText(run.Label));
            LiveSpeed.Text = "";
            RunProgress.Visibility = Visibility.Collapsed;
        }

        var extra = new List<string>();
        if (run.AccessTimeMs is double access) extra.Add(Loc.F("Speed_AccessTime", "Random access time: {0}", FormatLatency(access)));
        if (run.Surface.Count > 0)
            extra.Add(Loc.F("Speed_SurfaceStats", "Surface: min {0:N0} · avg {1:N0} · max {2:N0} MB/s",
                run.Surface.Min(p => p.MBps), run.Surface.Average(p => p.MBps), run.Surface.Max(p => p.MBps)));
        ExtraText.Text = string.Join("   ·   ", extra);

        if (live is not { Running: true }) ShowVerdict(run);
        UpdateCharts();
    }

    private void ShowScore(BenchRun run)
    {
        var score = BenchScore.Compute(run);
        if (score == null)
        {
            ScoreValue.Text = "—";
            ScoreTier.Text = Loc.T("Speed_ScorePending", "The score appears when the read tests are done.");
            ScoreParts.Text = "";
            ScoreDelta.Text = "";
            return;
        }
        ScoreValue.Text = score.Total.ToString("N0");
        ScoreTier.Text = Loc.T($"Speed_Tier_{score.TierKey}", score.TierKey);
        ScoreParts.Text = score.Write is int w
            ? Loc.F("Speed_ScoreParts", "Read {0:N0} · Write {1:N0}", score.Read, w)
            : Loc.F("Speed_ScoreReadOnly", "Read {0:N0} (write tests skipped — total counts reading twice)", score.Read);

        ScoreDelta.Text = "";
        if (_compare != null && _compare.Id != run.Id && BenchScore.Compute(_compare) is { } before && before.Total > 0)
        {
            double change = (score.Total - before.Total) * 100.0 / before.Total;
            ScoreDelta.Text = Loc.F("Speed_ScoreDelta", "{0:+0;-0;0}% vs {1} ({2:N0})", change, LabelText(_compare.Label), before.Total);
            ScoreDelta.Foreground = (Brush)FindResource(change >= 3 ? "SuccessBrush" : change <= -3 ? "DangerBrush" : "TextSecondaryBrush");
        }
        if (_crowd?.Score is { } crowd)
        {
            var place = score.Total >= crowd.P75 ? Loc.T("Speed_CrowdAbove", "above most PCs with this model")
                : score.Total >= crowd.P25 ? Loc.T("Speed_CrowdNormal", "normal for this model")
                : Loc.T("Speed_CrowdBelow", "below most PCs with this model");
            ScoreDelta.Text += (ScoreDelta.Text.Length > 0 ? "\n" : "") +
                               Loc.F("Speed_ScoreVsCrowd", "{0} (users' median {1:N0})", place, crowd.Median);
        }
    }

    private void ShowExpectation()
    {
        var spec = _expect?.Spec;
        var disk = _focused?.Volume.Disk;
        SourceRun.Text = "";
        SourceLink.Tag = null;
        if (spec != null)
        {
            ExpectedModel.Text = spec.Name.StartsWith(spec.Brand, StringComparison.OrdinalIgnoreCase) || spec.Name.Contains(spec.Brand.Split('/')[0], StringComparison.OrdinalIgnoreCase) ? spec.Name : $"{spec.Brand} {spec.Name}";
            var parts = new List<string>();
            if (spec.IsHdd)
            {
                if (spec.Rpm is int rpm) parts.Add($"{rpm:N0} rpm");
                if (spec.SustainedMBs is double sus) parts.Add(Loc.F("Speed_RatedSustained", "up to {0:N0} MB/s sustained", sus));
                if (spec.SustainedIdMBs is double id) parts.Add(Loc.F("Speed_RatedInner", "{0:N0} MB/s at the inner tracks", id));
            }
            else
            {
                if (spec.SeqReadMBs is double r) parts.Add(Loc.F("Speed_RatedRead", "read up to {0:N0} MB/s", r));
                if (spec.SeqWriteMBs is double w) parts.Add(Loc.F("Speed_RatedWrite", "write up to {0:N0} MB/s", w));
                if (spec.RndReadIops is double ri) parts.Add(Loc.F("Speed_RatedIops", "random read {0:N0} IOPS", ri) + (spec.RndQueueDepth is { } qd ? $" ({qd})" : ""));
            }
            if (!string.IsNullOrWhiteSpace(spec.Interface)) parts.Add(spec.Interface!);
            ExpectedFigures.Text = string.Join(" · ", parts);
            // The maker publishes no speed for this model: compare with its kind.
            if (_expect?.RatedRead == null && _expect?.TypicalRead is { } typical)
                ExpectedFigures.Text += (parts.Count > 0 ? "\n" : "") +
                    Loc.F("Speed_TypicalRange", "Drives of this kind are rated {0:N0}–{1:N0} MB/s read (official ratings of {2} models).",
                        typical.Low, typical.High, typical.Models);
            SourceRun.Text = Loc.F("Speed_Source", "Source: {0}", spec.Source);
            SourceLink.Tag = spec.SourceUrl;
        }
        else
        {
            ExpectedModel.Text = string.IsNullOrWhiteSpace(disk?.Model)
                ? Loc.T("Disk_UnknownModel", "Unknown drive")
                : Loc.F("Speed_ModelNotListed", "{0} — not in WinXTools' list of rated drives", disk!.Model);
            ExpectedFigures.Text = _expect?.TypicalRead is { } range
                ? Loc.F("Speed_TypicalRange", "Drives of this kind are rated {0:N0}–{1:N0} MB/s read (official ratings of {2} models).",
                    range.Low, range.High, range.Models)
                : Loc.T("Speed_NoReference", "No official rating to compare with for this drive.");
        }
        if (_expect?.Link is { } link)
            ExpectedFigures.Text += "\n" + Loc.F("Speed_LinkCeiling", "Connection limit ({0}): {1:N0} MB/s", link.Name, link.MaxMBs);
    }

    private void ShowCrowd()
    {
        var c = _crowd;
        if (c == null)
        {
            CrowdText.Text = Loc.T("Speed_CrowdOffline", "Couldn't reach xman studio — showing the maker's figures only.");
        }
        else if (!c.HasStats)
        {
            CrowdText.Text = Loc.F("Speed_CrowdFew", "Not enough tests of this model yet ({0:N0} PCs). Your results help build the normal range.", c.Devices);
        }
        else
        {
            var lines = new List<string>
            {
                Loc.F("Speed_CrowdScore", "{0:N0} PCs tested this model · score {1:N0} (normal {2:N0}–{3:N0})",
                    c.Devices, c.Score!.Median, c.Score.P25, c.Score.P75)
            };
            if (c.SeqQ8Read is { } sr) lines.Add(Loc.F("Speed_CrowdSeq", "Sequential read: {0:N0} MB/s (normal {1:N0}–{2:N0})", sr.Median, sr.P25, sr.P75));
            if (c.Rnd4KQ1Read is { } rr) lines.Add(Loc.F("Speed_CrowdRnd", "Random 4K read Q1: {0:N1} MB/s (normal {1:N1}–{2:N1})", rr.Median, rr.P25, rr.P75));
            if (c.AccessMs is { } am) lines.Add(Loc.F("Speed_CrowdAccess", "Access time: {0:N1} ms (normal {1:N1}–{2:N1})", am.Median, am.P25, am.P75));
            CrowdText.Text = string.Join("\n", lines);
        }
        if (_shown != null) ShowScore(_shown);
    }

    private void ShowVerdict(BenchRun run)
    {
        if (_expect == null || run.Results.Count == 0)
        {
            VerdictBadge.Visibility = Visibility.Collapsed;
            return;
        }
        var findings = DriveReference.Judge(run, _expect, _focused?.Volume.Disk);
        var lines = new List<string>();
        foreach (var f in findings)
        {
            switch (f.Code)
            {
                case "Normal":
                    SetVerdict(Loc.F("Speed_VerdictNormal", "Normal — {0:0}% of the rated {1:N0} MB/s", f.Value, f.Value2), "SuccessBrush");
                    break;
                case "BelowSpec":
                    SetVerdict(Loc.F("Speed_VerdictBelow", "A little below spec — {0:0}% of the rated {1:N0} MB/s", f.Value, f.Value2), "WarningBrush");
                    break;
                case "FarBelowSpec":
                    SetVerdict(Loc.F("Speed_VerdictFarBelow", "Well below spec — only {0:0}% of the rated {1:N0} MB/s", f.Value, f.Value2), "DangerBrush");
                    break;
                case "WithinTypical":
                    SetVerdict(Loc.T("Speed_VerdictTypical", "In the normal range for this kind of drive"), "SuccessBrush");
                    break;
                case "BelowTypical":
                    SetVerdict(Loc.F("Speed_VerdictBelowTypical", "Slower than drives of this kind are rated ({0:N0}–{1:N0} MB/s)", f.Value, f.Value2), "WarningBrush");
                    break;
                case "NoRating":
                    SetVerdict(Loc.T("Speed_VerdictNoRating", "Measured — no official rating to compare with"), "SteelLightBrush");
                    break;
                case "Sata2Suspected":
                    lines.Add(Loc.T("Speed_HintSata2", "• About 270 MB/s is the limit of a SATA 3 Gb/s port or an old cable."));
                    break;
                case "Usb2Suspected":
                    lines.Add(Loc.T("Speed_HintUsb2", "• About 40 MB/s is the limit of USB 2.0. Use a blue (USB 3) port and the drive's own cable."));
                    break;
                case "PcieGen3Suspected":
                    lines.Add(Loc.F("Speed_HintPcieGen3", "• Reads stop at about {0:N0} MB/s although this model is rated {1:N0} MB/s: that is the limit of a PCIe 3.0 x4 slot.", f.Value, f.Value2));
                    break;
                case "SsdNearlyFull":
                    lines.Add(Loc.F("Speed_HintFull", "• Only {0:0.#}% of this SSD is free ({1:0.#} GB). A nearly full SSD writes much slower.", f.Value, f.Value2));
                    break;
                case "PcieLimited":
                    lines.Add(Loc.T("Speed_HintPcie", "• Much slower than its rating: check the M.2 slot and the SSD's temperature."));
                    break;
                case "SurfaceDips":
                    lines.Add(Loc.F("Speed_HintDips", "• The curve has {0:N0} sudden dips — back up important files and check the disk's health.", f.Value));
                    break;
                case "SsdSlowSpots":
                    lines.Add(Loc.F("Speed_HintSsdSlow", "• {0:N0} places read at less than half speed.", f.Value));
                    break;
                case "SlowAccess":
                    lines.Add(Loc.F("Speed_HintAccess", "• Random access takes {0:0.0} ms — slower than a healthy hard disk.", f.Value));
                    break;
            }
        }
        if (findings.Any(f => f.Code is "BelowSpec" or "FarBelowSpec") && lines.Count == 0)
            lines.Add(Loc.T("Speed_HintGeneric", "• Other programs using the drive, a nearly full SSD, power saving or heat can lower the numbers."));
        FindingList.ItemsSource = lines;
    }

    private void SetVerdict(string text, string brushKey)
    {
        VerdictText.Text = text;
        VerdictBadge.Background = (Brush)FindResource(brushKey);
        VerdictBadge.Visibility = Visibility.Visible;
    }

    private void SourceLink_Click(object sender, RoutedEventArgs e)
    {
        if (SourceLink.Tag is not string url || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        // Opened in the user's normal browser through Explorer (not as administrator).
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false }); }
        catch (Exception ex) { Debug.WriteLine($"[DiskSpeedView] Link: {ex.Message}"); }
    }

    private static string PatternTitle(string? id) => id switch
    {
        null => "",
        _ when id.EndsWith(" W") => id[..^2] + " " + Loc.T("Speed_Write", "Write"),
        _ => id + " " + Loc.T("Speed_Read", "Read")
    };

    private static string LabelText(string label) => label switch
    {
        "before" => Loc.T("Speed_LabelBefore", "Before cleaning / defrag"),
        "after" => Loc.T("Speed_LabelAfter", "After cleaning / defrag"),
        _ => Loc.T("Speed_LabelGeneral", "General test")
    };

    #endregion

    #region History and comparison

    private sealed class RunEntry
    {
        public BenchRun? Run { get; init; }
        public string Text { get; init; } = "";
    }

    private void FillCompareBox(List<BenchRun> history)
    {
        var entries = new List<RunEntry> { new() { Run = null, Text = Loc.T("Speed_CompareNone", "(nothing)") } };
        entries.AddRange(history.Where(r => r.Id != _shown?.Id).Select(r => new RunEntry { Run = r, Text = Describe(r) }));
        CompareBox.ItemsSource = entries;
        CompareBox.SelectedItem = entries.FirstOrDefault(x => x.Run != null && x.Run.Id == _compare?.Id) ?? entries[0];
    }

    private void ShowHistory(List<BenchRun> history)
    {
        HistoryList.ItemsSource = history.Select(r => new RunEntry { Run = r, Text = Describe(r) }).ToList();
        HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Describe(BenchRun r)
    {
        var score = BenchScore.Compute(r);
        var seq = r.Find(DiskBenchmark.Seq1MQ8.Id)?.MBps;
        var rnd = r.Find(DiskBenchmark.Rnd4KQ1.Id)?.MBps;
        var scoreText = score == null ? "" : Loc.F("Speed_HistoryScore", "score {0:N0} · ", score.Total);
        return $"{r.Time:g} · {LabelText(r.Label)} · {scoreText}SEQ {seq:N0} · RND4K Q1 {rnd:N2} MB/s";
    }

    private void CompareBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isActive || AnyRunning) return;
        _compare = (CompareBox.SelectedItem as RunEntry)?.Run;
        if (_shown != null) ShowScore(_shown);
        UpdateCharts();
    }

    private void DeleteRun_Click(object sender, RoutedEventArgs e)
    {
        if (AnyRunning || sender is not Button { DataContext: RunEntry { Run: { } run } }) return;
        if (MessageBox.Show(Loc.F("Speed_ConfirmDelete", "Delete the test from {0:g}?", run.Time), Loc.T("Speed_Title", "Disk speed test"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        BenchHistory.Remove(run.Id);
        _live.Remove(run.Letter);
        if (_compare?.Id == run.Id) _compare = null;
        if (_focused != null) Focus(_focused);
    }

    #endregion

    #region Charts

    private static SolidColorPaint TextPaint(string hex) => new(SKColor.Parse(hex)) { SKTypeface = ChartFace };

    private void SetupCharts()
    {
        foreach (var chart in new[] { SeqChart, RndChart, SurfaceChart })
        {
            chart.LegendPosition = LegendPosition.Bottom;
            chart.LegendTextPaint = TextPaint("#94a3b8");
            chart.LegendTextSize = 11;
            chart.TooltipTextPaint = TextPaint("#0f172a");
            chart.AnimationsSpeed = TimeSpan.FromMilliseconds(250);
        }
        SurfaceChart.XAxes = new[]
        {
            new Axis
            {
                MinLimit = 0, MaxLimit = 100, Labeler = v => $"{v:0}%", LabelsPaint = TextPaint("#6b8eab"), TextSize = 11,
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#1a4dc9ff")),
                Name = Loc.T("Speed_AxisPosition", "Position on the disk"), NamePaint = TextPaint("#6b8eab"), NameTextSize = 11
            }
        };
        SurfaceChart.YAxes = new[] { ValueAxis() };
    }

    private static Axis ValueAxis() => new()
    {
        MinLimit = 0,
        Labeler = v => $"{v:N0}",
        LabelsPaint = TextPaint("#6b8eab"),
        TextSize = 11,
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#1a4dc9ff"))
    };

    private void UpdateCharts()
    {
        UpdateBars();
        UpdateSurface();
    }

    private void UpdateBars()
    {
        string read = Loc.T("Speed_Read", "Read"), write = Loc.T("Speed_Write", "Write");
        string[] seqIds = ["SEQ1M Q8T1", "SEQ1M Q8T1 W", "SEQ1M Q1T1", "SEQ1M Q1T1 W"];
        string[] rndIds = ["RND4K Q32T1", "RND4K Q32T1 W", "RND4K Q1T1", "RND4K Q1T1 W"];
        string[] seqLabels = [$"Q8 {read}", $"Q8 {write}", $"Q1 {read}", $"Q1 {write}"];
        string[] rndLabels = [$"Q32 {read}", $"Q32 {write}", $"Q1 {read}", $"Q1 {write}"];

        var spec = _expect?.Spec;
        double?[] seqRated = [_expect?.RatedRead, _expect?.RatedWrite, null, null];
        double?[] rndRated =
        [
            spec?.RndReadIops is double ri ? ri * 4096 / 1e6 : null,
            spec?.RndWriteIops is double wi ? wi * 4096 / 1e6 : null,
            null, null
        ];
        if (spec?.IsHdd == true) seqRated = [spec.SustainedMBs, spec.SustainedMBs, null, null];
        double?[] seqCrowd = [_crowd?.SeqQ8Read?.Median, _crowd?.SeqQ8Write?.Median, _crowd?.SeqQ1Read?.Median, null];
        double?[] rndCrowd = [_crowd?.Rnd4KQ32Read?.Median, null, _crowd?.Rnd4KQ1Read?.Median, _crowd?.Rnd4KQ1Write?.Median];

        SeqChart.Series = Bars(seqIds, seqRated, seqCrowd);
        SeqChart.XAxes = new[] { CategoryAxis(seqLabels) };
        SeqChart.YAxes = new[] { ValueAxis() };
        RndChart.Series = Bars(rndIds, rndRated, rndCrowd);
        RndChart.XAxes = new[] { CategoryAxis(rndLabels) };
        RndChart.YAxes = new[] { ValueAxis() };
    }

    private ISeries[] Bars(string[] ids, double?[] rated, double?[] crowd)
    {
        var list = new List<ISeries>
        {
            new ColumnSeries<double?>
            {
                Name = _shown?.Label is { Length: > 0 } l ? LabelText(l) : Loc.T("Speed_SeriesThis", "This test"),
                Values = ids.Select(id => _shown?.Find(id)?.MBps).ToArray(),
                Fill = new SolidColorPaint(ThisColor),
                MaxBarWidth = 22
            }
        };
        if (_compare != null && _compare.Id != _shown?.Id)
            list.Add(new ColumnSeries<double?>
            {
                Name = Loc.F("Speed_SeriesCompare", "{0} ({1:d})", LabelText(_compare.Label), _compare.Time),
                Values = ids.Select(id => _compare.Find(id)?.MBps).ToArray(),
                Fill = new SolidColorPaint(CompareColor),
                MaxBarWidth = 22
            });
        if (crowd.Any(c => c != null))
            list.Add(new ColumnSeries<double?>
            {
                Name = Loc.T("Speed_SeriesCrowd", "Users' median (same model)"),
                Values = crowd,
                Fill = new SolidColorPaint(CrowdColor.WithAlpha(0xc0)),
                MaxBarWidth = 22
            });
        if (rated.Any(r => r != null))
            list.Add(new ColumnSeries<double?>
            {
                Name = Loc.T("Speed_SeriesRated", "Rated (data sheet)"),
                Values = rated,
                Fill = new SolidColorPaint(RatedColor.WithAlpha(0xb0)),
                MaxBarWidth = 22
            });
        return list.ToArray();
    }

    private static Axis CategoryAxis(string[] labels) => new()
    {
        Labels = labels,
        LabelsPaint = TextPaint("#94a3b8"),
        TextSize = 11,
        SeparatorsPaint = null
    };

    private void UpdateSurface()
    {
        var series = new List<ISeries>
        {
            new LineSeries<ObservablePoint>
            {
                Name = Loc.T("Speed_SeriesThis", "This test"),
                Values = _shownSurface,
                GeometrySize = 0,
                LineSmoothness = 0.2,
                Stroke = new SolidColorPaint(ThisColor, 2),
                Fill = new SolidColorPaint(ThisColor.WithAlpha(0x22))
            }
        };
        if (_compare?.Surface.Count > 0 && _compare.Id != _shown?.Id)
            series.Add(new LineSeries<ObservablePoint>
            {
                Name = Loc.F("Speed_SeriesCompare", "{0} ({1:d})", LabelText(_compare.Label), _compare.Time),
                Values = _compare.Surface.Select(p => new ObservablePoint(p.Position * 100, p.MBps)).ToArray(),
                GeometrySize = 0,
                LineSmoothness = 0.2,
                Stroke = new SolidColorPaint(CompareColor, 2),
                Fill = null
            });
        if (_crowd?.SurfaceMedian is { Length: > 1 } median)
            series.Add(new LineSeries<ObservablePoint>
            {
                Name = Loc.T("Speed_SeriesCrowd", "Users' median (same model)"),
                Values = median.Select((v, i) => new ObservablePoint((i + 0.5) * 100.0 / median.Length, v)).ToArray(),
                GeometrySize = 0,
                LineSmoothness = 0.4,
                Stroke = new SolidColorPaint(CrowdColor, 2) { PathEffect = new DashEffect([3f, 3f]) },
                Fill = null
            });

        SurfaceNote.Text = "";
        if (_expect is { CurveStart: double start, CurveEndLow: double lo, CurveEndHigh: double hi })
        {
            // Transfer rate is proportional to the track radius; with the same areal
            // density the radius falls like √(1 − f·(1 − k²)) from the outer edge inwards.
            ObservablePoint[] Curve(double end) => Enumerable.Range(0, 21).Select(i =>
            {
                double f = i / 20.0, k = end / start;
                return new ObservablePoint(f * 100, start * Math.Sqrt(1 - f * (1 - k * k)));
            }).ToArray();
            var dash = new DashEffect([7f, 5f]);
            series.Add(new LineSeries<ObservablePoint>
            {
                Name = Loc.T("Speed_SeriesExpected", "Expected for this model"),
                Values = Curve(hi), GeometrySize = 0, Fill = null, LineSmoothness = 0.4,
                Stroke = new SolidColorPaint(RatedColor, 1.6f) { PathEffect = dash }
            });
            if (Math.Abs(hi - lo) > 1)
                series.Add(new LineSeries<ObservablePoint>
                {
                    Name = Loc.T("Speed_SeriesExpectedLow", "Expected (lower edge)"),
                    Values = Curve(lo), GeometrySize = 0, Fill = null, LineSmoothness = 0.4,
                    Stroke = new SolidColorPaint(RatedColor.WithAlpha(0x90), 1.2f) { PathEffect = dash }
                });
            SurfaceNote.Text = _expect.CurveEndFromDataSheet
                ? Loc.T("Speed_CurveNoteSheet", "Dashed: the maker's outer- and inner-track ratings.")
                : Loc.T("Speed_CurveNoteTypical", "Dashed: starts at the maker's rated maximum; inner tracks normally run at about 45–60% of it.");
        }
        else if (_expect?.RatedRead is double rated && _expect.Spec?.IsHdd != true)
        {
            series.Add(new LineSeries<ObservablePoint>
            {
                Name = Loc.T("Speed_SeriesRated", "Rated (data sheet)"),
                Values = new[] { new ObservablePoint(0, rated), new ObservablePoint(100, rated) },
                GeometrySize = 0, Fill = null,
                Stroke = new SolidColorPaint(RatedColor, 1.6f) { PathEffect = new DashEffect([7f, 5f]) }
            });
            SurfaceNote.Text = Loc.T("Speed_CurveNoteSsd", "An SSD should read at about the same speed everywhere (a flat line).");
        }
        SurfaceChart.Series = series.ToArray();
    }

    #endregion
}

/// <summary>A drive card on the speed test page: focus (details), include-in-test tick, live progress and score.</summary>
public sealed class SpeedDriveItem : INotifyPropertyChanged
{
    public SpeedDriveItem(DriveVolume volume)
    {
        Volume = volume;
        var card = new DefragDriveItem(volume);
        LetterText = card.LetterText;
        Title = card.Title;
        Model = card.Model;
        MediaText = card.MediaText;
        BadgeBrush = card.BadgeBrush;
    }

    public DriveVolume Volume { get; }
    public string LetterText { get; }
    public string Title { get; }
    public string Model { get; }
    public string MediaText { get; }
    public Brush BadgeBrush { get; }

    private bool _isSelected, _isChecked, _isEnabled = true, _running;
    private double _progress;
    private string _liveText = "", _scoreText = "";

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value, nameof(IsSelected)); }
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value, nameof(IsChecked)); }
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value, nameof(IsEnabled)); }
    public double Progress { get => _progress; private set => Set(ref _progress, value, nameof(Progress)); }
    public string LiveText { get => _liveText; private set => Set(ref _liveText, value, nameof(LiveText)); }
    public string ScoreText { get => _scoreText; private set { Set(ref _scoreText, value, nameof(ScoreText)); OnChanged(nameof(ScoreVisibility)); } }
    public Visibility ScoreVisibility => _scoreText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ProgressVisibility => _running ? Visibility.Visible : Visibility.Collapsed;

    public void StartProgress(string text)
    {
        _running = true;
        Progress = 0;
        LiveText = text;
        OnChanged(nameof(ProgressVisibility));
    }

    public void UpdateProgress(double fraction, string text)
    {
        Progress = Math.Clamp(fraction, 0, 1);
        LiveText = text;
    }

    public void StopProgress()
    {
        _running = false;
        OnChanged(nameof(ProgressVisibility));
    }

    public void SetScore(int total) => ScoreText = Loc.F("Speed_CardScore", "Score {0:N0}", total);

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

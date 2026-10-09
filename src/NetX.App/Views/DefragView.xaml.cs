using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetX.App.Controls;
using NetX.App.Helpers;
using NetX.Core.Helpers;
using NetX.Core.System.Storage;

namespace NetX.App.Views;

public partial class DefragView : Page
{
    private const int PlatterBlocks = 16384;
    private const int BoardBlocks = 8192;
    private static readonly TimeSpan LiveRefresh = TimeSpan.FromSeconds(1.5);

    /// <summary>What we learned about each drive during this run of WinXTools (survives page changes).</summary>
    private sealed class DriveMemory
    {
        public DefragStats? Before, After;
        public double? BeforeSpeed, AfterSpeed;
        public List<string> TestFiles = new();
        public LayoutScan? Layout;
        public bool AwaitingAfter;
        public HotFileReport? Smart;
        public List<SmartProgramRow>? SmartRows;
        public DateTime SmartReadUtc;
    }

    private static readonly Dictionary<char, DriveMemory> Memory = new();
    private static DefragView? _attached;

    private readonly ObservableCollection<DefragDriveItem> _drives = new();
    private readonly DispatcherTimer _ioTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _liveTimer = new() { Interval = LiveRefresh };
    private readonly DispatcherTimer _ioLiveTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly List<DiskIoSample> _ioSamples = new();
    private readonly List<(double Fraction, bool Write)> _ioPoints = new();
    private SmartPlacementSession? _smart;
    private Window? _window;
    private bool _monitorHeld;
    private int _smartUiPending;
    private int _lastSmartFile = -1;
    private DefragDriveItem? _selected;
    private VolumeMap? _map;
    private float[]? _fragLayer, _metaLayer, _unmovLayer;
    private DiskIoCounter? _io;
    private DiskSceneLayout _sceneLayout = DiskSceneLayout.Platter;
    private double _span0, _span1 = 1;
    private CancellationTokenSource? _cts;
    private DefragSession? _session;
    private bool _isActive, _busy, _mapInFlight, _isAdmin;
    private long _movedBytes;
    private int _loadVersion;

    public DefragView()
    {
        InitializeComponent();
        DriveList.ItemsSource = _drives;
        _ioTimer.Tick += (s, e) => UpdateIo();
        _liveTimer.Tick += async (s, e) => await RefreshLiveMapAsync();
        _ioLiveTimer.Tick += (s, e) => DrainLiveIo();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (s, e) => UpdateMonitor();
    }

    /// <summary>Anything (from this page or not) is moving data on a drive right now.</summary>
    private static bool AnyRunning =>
        DefragSession.Current is { IsRunning: true } || SmartPlacementSession.Current is { IsRunning: true };

    #region Page lifetime

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isActive = true;
        _attached = this;
        _isAdmin = AdminHelper.IsRunAsAdmin();
        StatusText.Text = Loc.T("Defrag_Loading", "Reading your drives…");
        HudPhase.Text = "";
        UpdateButtons();

        List<DriveVolume> volumes;
        try
        {
            volumes = await Task.Run(DriveInventory.GetVolumes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefragView] Drives: {ex.Message}");
            volumes = new List<DriveVolume>();
        }
        if (!_isActive) return;

        _drives.Clear();
        foreach (var v in volumes) _drives.Add(new DefragDriveItem(v));

        var running = DefragSession.Current;
        var smart = SmartPlacementSession.Current;
        char? busyLetter = smart is { IsRunning: true } ? smart.Letter : running?.Letter;
        var pick = busyLetter != null
            ? _drives.FirstOrDefault(d => d.Volume.Letter == busyLetter)
            : _drives.FirstOrDefault(d => d.Volume.Disk?.IsHdd == true) ?? _drives.FirstOrDefault(d => d.Volume.IsSystem) ?? _drives.FirstOrDefault();

        if (pick == null)
        {
            StatusText.Text = Loc.T("Defrag_NoDrives", "No drives that can be optimized were found.");
            return;
        }

        pick.IsSelected = true; // raises Checked → SelectDrive
        if (_selected != pick) SelectDrive(pick);

        if (smart is { IsRunning: true } && smart.Letter == _selected?.Volume.Letter) AttachSmart(smart);
        else if (running != null) Attach(running);
        if (!_isAdmin) ShowCenter(Loc.T("Defrag_NeedsAdmin", "WinXTools must run as administrator to read and optimize drives."));
        _ioTimer.Start();

        _window = Window.GetWindow(this);
        if (_window != null) _window.StateChanged += OnWindowStateChanged;
        UpdateMonitor();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateMonitor();

    /// <summary>
    /// The disk trace (real head movement in the 3D view) runs only while this
    /// page is on screen: hidden, minimized or closed, it stops at once.
    /// </summary>
    private void UpdateMonitor()
    {
        bool want = _isActive && _isAdmin && IsVisible && _window?.WindowState != WindowState.Minimized;
        if (want && !_monitorHeld)
        {
            _monitorHeld = DiskActivityMonitor.Instance.Acquire();
            if (_monitorHeld) _ioLiveTimer.Start();
        }
        else if (!want && _monitorHeld)
        {
            _ioLiveTimer.Stop();
            _monitorHeld = false;
            DiskActivityMonitor.Instance.Release();
        }
    }

    /// <summary>Real reads and writes of the selected drive since the last tick: blocks glow, the head goes there.</summary>
    private void DrainLiveIo()
    {
        if (_selected?.Volume is not { DiskNumber: int disk, TotalBytes: > 0 } v) return;
        _ioSamples.Clear();
        if (DiskActivityMonitor.Instance.Drain(disk, _ioSamples) == 0) return;
        _ioPoints.Clear();
        foreach (var sample in _ioSamples)
        {
            double f = (sample.Offset - v.DiskOffset) / (double)v.TotalBytes;
            if (f >= 0 && f < 1) _ioPoints.Add((f, sample.IsWrite));
        }
        if (_ioPoints.Count == 0) return;
        Scene.ShowIo(_ioPoints);
        if (_map != null)
            FlatMap.Flash(_ioPoints.Skip(Math.Max(0, _ioPoints.Count - 24)).Select(p => (int)(p.Fraction * _map.BlockCount)));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isActive = false;
        if (_attached == this) _attached = null;
        _ioTimer.Stop();
        _liveTimer.Stop();
        _cts?.Cancel();
        Detach();
        DetachSmart();
        if (_window != null) _window.StateChanged -= OnWindowStateChanged;
        UpdateMonitor();
        _io?.Dispose();
        _io = null;
        // A running optimization keeps going; the page re-attaches when opened again.
    }

    #endregion

    #region Drive selection

    private void DriveCard_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: DefragDriveItem item } && item != _selected) SelectDrive(item);
    }

    private void SelectDrive(DefragDriveItem item)
    {
        if (_busy) return;
        _selected = item;
        _map = null;
        _fragLayer = _metaLayer = _unmovLayer = null;
        _movedBytes = 0;
        int version = ++_loadVersion;

        var v = item.Volume;
        var disk = v.Disk;
        bool ssd = disk?.IsSsd == true;
        double span0 = 0, span1 = 1;
        if (disk is { SizeBytes: > 0 })
        {
            span0 = v.DiskOffset / (double)disk.SizeBytes;
            span1 = (v.DiskOffset + v.TotalBytes) / (double)disk.SizeBytes;
        }
        _sceneLayout = ssd ? DiskSceneLayout.Board : DiskSceneLayout.Platter;
        _span0 = span0;
        _span1 = span1;
        Scene.Configure(_sceneLayout, span0, span1, ssd ? BoardBlocks : PlatterBlocks);
        FlatMap.Clear();

        HudDrive.Text = $"{v.DisplayName}";
        HudMedia.Text = $"{item.Model} · {item.MediaText}";
        HudPercent.Text = "";
        HudPhase.Text = "";
        HudMoved.Text = "—";
        HudElapsed.Text = "—";
        ConsolidateOption.IsEnabled = !ssd;
        OptimizeButton.Content = ssd ? Loc.T("Defrag_Trim", "Run TRIM") : Loc.T("Defrag_Optimize", "Defragment");

        _io?.Dispose();
        _io = v.DiskNumber is int n ? new DiskIoCounter(n) : null;

        ShowMemory();
        StatusText.Text = DescribeDrive(item);
        HideCenter();
        _ = LoadMapAsync(version);
        _ = LoadSmartAsync(version);
    }

    private async Task LoadMapAsync(int version)
    {
        if (_selected == null || !_isAdmin) return;
        var item = _selected;
        int blocks = item.Volume.Disk?.IsSsd == true ? BoardBlocks : PlatterBlocks;
        try
        {
            var map = await Task.Run(() => VolumeMapReader.ReadBitmap(item.Volume.Letter, blocks));
            if (!_isActive || version != _loadVersion) return;
            _map = map;
            var mem = MemoryFor(item.Volume.Letter);
            ApplyLayout(mem.Layout?.Fragmented.Length == map.BlockCount ? mem.Layout : null);
            PushLayers();
            MapCaption.Text = Loc.F("Defrag_MapCaption", "Each cell ≈ {0}", CleanerView.FormatSize(map.BytesPerBlock));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefragView] Map: {ex.Message}");
            if (_isActive && version == _loadVersion)
                ShowCenter(Loc.T("Defrag_MapUnavailable", "Windows didn't allow reading this drive's map (it may be locked, encrypted or not NTFS/FAT)."));
        }
    }

    private static DriveMemory MemoryFor(char letter)
    {
        lock (Memory)
        {
            if (!Memory.TryGetValue(letter, out var mem)) Memory[letter] = mem = new DriveMemory();
            return mem;
        }
    }

    #endregion

    #region Analyze

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected == null || !_isAdmin) return;
        await AnalyzeAsync(isAfterRun: false);
    }

    /// <summary>Windows' analysis + our map of where the fragments are. Returns false if it failed.</summary>
    private async Task<bool> AnalyzeAsync(bool isAfterRun)
    {
        var item = _selected!;
        char letter = item.Volume.Letter;
        var mem = MemoryFor(letter);
        _busy = true;
        UpdateButtons();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Scene.Busy = true;
        HideCenter();
        StatusText.Text = Loc.T("Defrag_Analyzing", "Analyzing — Windows is reading the file table…");
        HudPhase.Text = StatusText.Text;
        HudPercent.Text = "";

        double mftFraction = 0.02;
        try
        {
            var mft = await Task.Run(() => NtfsInfo.Read(letter), token);
            if (mft is { TotalClusters: > 0 } m) mftFraction = m.MftStartLcn / (double)m.TotalClusters;
            Scene.SeekToFraction(mftFraction);

            int blocks = item.Volume.Disk?.IsSsd == true ? BoardBlocks : PlatterBlocks;
            var map = await Task.Run(() => VolumeMapReader.ReadBitmap(letter, blocks, token), token);
            if (!_isActive) return false;
            _map = map;
            PushLayers();

            var (stats, code) = await DefragEngine.AnalyzeAsync(letter, token);
            if (!_isActive) return false;
            if (stats == null)
            {
                StatusText.Text = AnalysisError(code);
                return false;
            }

            LayoutScan? layout = null;
            if (item.Volume.IsNtfs)
            {
                var random = new Random();
                var progress = new Progress<long>(files =>
                {
                    if (!_isActive) return;
                    HudPhase.Text = Loc.F("Defrag_ScanningFiles", "Mapping fragments… {0:N0} files", files);
                    Scene.SeekToFraction(mftFraction + random.NextDouble() * 0.004);
                });
                layout = await Task.Run(() => VolumeMapReader.ScanLayout(letter, map, progress, token), token);
                if (!_isActive) return false;
            }

            if (isAfterRun)
            {
                mem.After = stats;
            }
            else
            {
                mem.Before = stats;
                mem.After = null;
                mem.BeforeSpeed = mem.AfterSpeed = null;
            }
            mem.Layout = layout;
            if (layout != null && !isAfterRun)
                mem.TestFiles = layout.Worst.Where(f => f.Bytes >= (1 << 20)).Take(25).Select(f => f.Path).ToList();

            ApplyLayout(layout);
            PushLayers();
            item.SetFragmentation(stats.TotalPercentFragmentation);
            ShowMemory();
            StatusText.Text = Loc.F("Defrag_AnalysisDone", "Analysis done — {0}% fragmented, {1:N0} of {2:N0} files in pieces.",
                stats.TotalPercentFragmentation, stats.TotalFragmentedFiles, stats.TotalFiles);
            ShowAdvice(item, stats);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (_isActive) StatusText.Text = Loc.T("Defrag_Cancelled", "Cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefragView] Analyze: {ex}");
            if (_isActive) StatusText.Text = Loc.F("Defrag_AnalyzeFailed", "Analysis failed: {0}", ErrorText.From(ex));
            return false;
        }
        finally
        {
            if (_isActive)
            {
                _busy = false;
                Scene.Busy = _session?.IsRunning == true;
                Scene.SeekToFraction(null);
                HudPhase.Text = "";
                UpdateButtons();
            }
        }
    }

    private static string AnalysisError(int code) => code switch
    {
        1 => Loc.T("Defrag_ErrAccess", "Windows refused: WinXTools needs administrator rights."),
        2 => Loc.T("Defrag_ErrNotSupported", "Windows can't analyze this drive (file system not supported)."),
        3 => Loc.T("Defrag_ErrDirty", "The drive has file system errors. Run \"chkdsk /f\" first, then try again."),
        4 => Loc.T("Defrag_ErrSpace", "Not enough free space for Windows to analyze/defragment this drive."),
        8 => Loc.T("Defrag_ErrRunning", "Windows is already optimizing a drive. Try again when it has finished."),
        _ => Loc.F("Defrag_ErrCode", "Windows' defragmenter reported error {0}.", code)
    };

    private void ShowAdvice(DefragDriveItem item, DefragStats stats)
    {
        string text;
        if (item.Volume.Disk?.IsSsd == true)
            text = Loc.T("Defrag_AdviceSsd", "This is an SSD: defragmenting only wears it. Use \"Run TRIM\" — it keeps write speed up.");
        else if (stats.DefragRecommended || stats.TotalPercentFragmentation >= 10)
            text = Loc.T("Defrag_AdviceDo", "Defragmenting is recommended for this hard disk.");
        else if (stats.FreeSpacePercentFragmentation >= 50)
            text = Loc.F("Defrag_AdviceFreeSpace",
                "Files are fine, but the free space is split into many small gaps ({0}% — the largest is only {1}), so new files get split. Tick \"Also pack free space together\" and press Defragment.",
                stats.FreeSpacePercentFragmentation, CleanerView.FormatSize(stats.LargestFreeExtent));
        else
            text = Loc.T("Defrag_AdviceSkip", "This drive is in good shape — defragmenting now won't make a noticeable difference.");
        if (DriveReference.IsSmr(item.Volume.Disk) == true)
            text += "\n\n" + Loc.T("Defrag_AdviceSmr",
                "This drive records in overlapping (SMR) tracks: it slows down sharply while writing a lot, so a full defragmentation can take many hours. Windows doesn't take that into account — prefer \"Arrange program files\", which moves only what programs load.");
        AdviceText.Text = text;
        AdviceText.Visibility = Visibility.Visible;
    }

    #endregion

    #region Optimize

    private async void Optimize_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected == null || !_isAdmin) return;
        var item = _selected;
        var v = item.Volume;
        bool ssd = v.Disk?.IsSsd == true;

        var ops = new List<DefragOperation>();
        if (ssd)
        {
            ops.Add(DefragOperation.Retrim);
        }
        else
        {
            ops.Add(DefragOperation.Optimize);
            if (ConsolidateOption.IsChecked == true) ops.Add(DefragOperation.ConsolidateFreeSpace);
        }

        bool removable = IsRemovable(v);
        SafetyReport safety;
        _busy = true;
        UpdateButtons();
        try
        {
            safety = await Task.Run(() => DriveSafety.Check(v, ownMoves: false));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefragView] Safety check: {ex.Message}");
            safety = new SafetyReport { IsRemovable = removable };
        }
        finally
        {
            _busy = false;
            if (_isActive) UpdateButtons();
        }
        if (!_isActive || !ShowBlockers(safety, v)) return;

        string question = ssd
            ? Loc.F("Defrag_ConfirmTrim", "Run TRIM on {0}?\n\nTRIM tells the SSD which blocks are free. It takes seconds to a few minutes and is completely safe.", v.DisplayName)
            : v.Disk?.Media == DriveMedia.Unknown
                ? Loc.F("Defrag_ConfirmUnknown", "Optimize {0}?\n\nWindows can't tell whether this drive is a hard disk or an SSD; its optimizer measures the drive and does the right thing for it. You can keep using the PC and stop at any time.", v.DisplayName)
                : Loc.F("Defrag_ConfirmHdd", "Defragment {0}?\n\nThis can take from a few minutes to several hours on a full hard disk. You can keep using the PC, and you can stop at any time — files already moved stay safe.", v.DisplayName);
        if (!ssd) question += SafetyNotes(safety);
        if (MessageBox.Show(question, Loc.T("Defrag_Title", "Defragment"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var mem = MemoryFor(v.Letter);

        // "Before" figures: analyze first when this drive hasn't been analyzed in this session.
        if (!ssd && (mem.Before == null || mem.After != null))
        {
            if (!await AnalyzeAsync(isAfterRun: false) || !_isActive) return;
        }

        if (!ssd && MeasureOption.IsChecked == true && mem.TestFiles.Count > 0 && mem.BeforeSpeed == null)
        {
            _busy = true;
            UpdateButtons();
            StatusText.Text = Loc.T("Defrag_MeasuringBefore", "Measuring how fast the most fragmented files read now…");
            try
            {
                var files = mem.TestFiles;
                mem.BeforeSpeed = await Task.Run(() => FileReadSpeedTest.Measure(files, 1536L << 20, TimeSpan.FromSeconds(25), CancellationToken.None));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DefragView] Speed before: {ex.Message}");
            }
            finally
            {
                _busy = false;
            }
            if (!_isActive) return;
            ShowMemory();
        }

        mem.AwaitingAfter = true;
        var session = DefragSession.Start(v.Letter, ops, removable: removable && !ssd);
        if (session == null)
        {
            mem.AwaitingAfter = false;
            MessageBox.Show(Loc.T("Defrag_AlreadyRunning", "Another drive is being optimized. Wait for it to finish or stop it first."),
                Loc.T("Defrag_Title", "Defragment"), MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateButtons();
            return;
        }
        session.Completed += OnSessionCompletedAnywhere;
        _movedBytes = 0;
        Attach(session);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_smart is { IsRunning: true })
        {
            _smart.Stop();
            StopButton.IsEnabled = false;
            StatusText.Text = Loc.T("Defrag_SmartStopping", "Stopping — finishing the piece being moved (a moment at most)…");
            return;
        }
        if (_session is not { IsRunning: true }) return;
        _session.Cancel();
        StopButton.IsEnabled = false;
        StatusText.Text = Loc.T("Defrag_Stopping", "Stopping — Windows finishes the file it is moving, then stops…");
    }

    private void Attach(DefragSession session)
    {
        Detach();
        _session = session;
        session.ProgressChanged += OnSessionProgress;
        session.Completed += OnSessionCompleted;

        Scene.Busy = true;
        RunProgress.Visibility = Visibility.Visible;
        RunProgress.IsIndeterminate = session.LastProgress?.Percent == null;
        UnplugBanner.Visibility = session.Removable ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = Loc.T("Defrag_Running", "Optimizing — you can keep using the PC.");
        if (session.LastProgress is { } p) ShowProgress(session, p);
        _liveTimer.Start();
        UpdateButtons();

        if (!session.IsRunning && session.Result is { } done) _ = HandleCompletedAsync(session, done);
    }

    private void Detach()
    {
        if (_session == null) return;
        _session.ProgressChanged -= OnSessionProgress;
        _session.Completed -= OnSessionCompleted;
        _session = null;
    }

    private void OnSessionProgress(DefragSession session, DefragProgress progress) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_isActive && session == _session) ShowProgress(session, progress);
        });

    private void ShowProgress(DefragSession session, DefragProgress progress)
    {
        string step = session.Operations.Count > 1
            ? Loc.F("Defrag_Step", "Step {0} of {1} · ", session.Step + 1, session.Operations.Count)
            : "";
        HudPhase.Text = step + progress.Text;
        if (progress.Percent is int pct)
        {
            HudPercent.Text = $"{pct}%";
            RunProgress.IsIndeterminate = false;
            RunProgress.Value = (session.Step * 100.0 + pct) / session.Operations.Count;
        }
    }

    private void OnSessionCompleted(DefragSession session, DefragRunResult result) =>
        Dispatcher.BeginInvoke(async () =>
        {
            if (_isActive && session == _session) await HandleCompletedAsync(session, result);
        });

    /// <summary>When nobody is looking at the page, a toast tells the user the run ended.</summary>
    private static void OnSessionCompletedAnywhere(DefragSession session, DefragRunResult result)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_attached != null) return;
            var text = result.Outcome == DefragOutcome.Completed
                ? Loc.F("Defrag_ToastDone", "Drive {0}: optimization finished.", session.Letter)
                : Loc.F("Defrag_ToastStopped", "Drive {0}: optimization stopped.", session.Letter);
            (Application.Current.MainWindow as MainWindow)?.ShowToast(text);
        });
    }

    private async Task HandleCompletedAsync(DefragSession session, DefragRunResult result)
    {
        _liveTimer.Stop();
        Detach();
        Scene.Busy = false;
        Scene.SetSweep(null);
        RunProgress.Visibility = Visibility.Collapsed;
        UnplugBanner.Visibility = Visibility.Collapsed;
        HudPercent.Text = result.Outcome == DefragOutcome.Completed ? "100%" : HudPercent.Text;
        UpdateButtons();

        var mem = MemoryFor(session.Letter);
        mem.AwaitingAfter = false;
        string summary = result.Outcome switch
        {
            DefragOutcome.Completed => Loc.F("Defrag_Done", "Done in {0}.", FormatDuration(result.Duration)),
            DefragOutcome.Cancelled when session.StopReason is { } reason => StopText(reason),
            DefragOutcome.Cancelled => Loc.T("Defrag_StoppedSafe", "Stopped. Files that were already moved stay in their new place — nothing is damaged."),
            DefragOutcome.StartFailed => Loc.T("Defrag_StartFailed", "Couldn't start Windows' defragmenter (defrag.exe)."),
            _ => Loc.F("Defrag_Failed", "Windows' defragmenter stopped with an error: {0}", string.IsNullOrWhiteSpace(result.Message) ? $"0x{result.ExitCode:X8}" : result.Message)
        };
        StatusText.Text = summary;
        HudPhase.Text = summary;

        if (_selected?.Volume.Letter != session.Letter || result.Outcome is DefragOutcome.StartFailed) return;
        if (_selected.Volume.Disk?.IsSsd == true)
        {
            await LoadMapAsync(_loadVersion);
            return;
        }

        // "After" figures for the comparison.
        if (await AnalyzeAsync(isAfterRun: true) && _isActive && mem.BeforeSpeed != null && mem.TestFiles.Count > 0)
        {
            _busy = true;
            UpdateButtons();
            StatusText.Text = Loc.T("Defrag_MeasuringAfter", "Measuring the same files again…");
            try
            {
                var files = mem.TestFiles;
                mem.AfterSpeed = await Task.Run(() => FileReadSpeedTest.Measure(files, 1536L << 20, TimeSpan.FromSeconds(25), CancellationToken.None));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DefragView] Speed after: {ex.Message}");
            }
            finally
            {
                _busy = false;
            }
            if (!_isActive) return;
            UpdateButtons();
            StatusText.Text = summary;
        }
        ShowMemory();
    }

    #endregion

    #region Smart placement (programs' start-up files together)

    private async Task LoadSmartAsync(int version)
    {
        var item = _selected;
        if (item == null) return;
        var v = item.Volume;
        bool eligible = v.Disk?.IsHdd == true && v.IsNtfs;
        SmartCard.Visibility = eligible ? Visibility.Visible : Visibility.Collapsed;
        if (!eligible) return;

        SmartAutoOption.IsChecked = SmartDefragService.Instance.AutoWhenIdle;
        ShowLastAuto();
        if (!_isAdmin)
        {
            SmartSummary.Text = Loc.T("Defrag_SmartNeedsAdmin", "Run WinXTools as administrator to see which programs start from this drive.");
            SmartPrograms.ItemsSource = null;
            UpdateButtons();
            return;
        }

        var mem = MemoryFor(v.Letter);
        if (mem.Smart == null || DateTime.UtcNow - mem.SmartReadUtc > TimeSpan.FromMinutes(5))
        {
            SmartSummary.Text = Loc.T("Defrag_SmartReading", "Reading what Windows recorded about your programs…");
            SmartPrograms.ItemsSource = null;
            try
            {
                char letter = v.Letter;
                var (report, names) = await Task.Run(() =>
                {
                    var r = HotFiles.Read(letter);
                    var n = r.Programs.Take(5).Select(p => ProgramName(letter, p)).ToList();
                    return (r, n);
                });
                mem.Smart = report;
                mem.SmartReadUtc = DateTime.UtcNow;
                mem.SmartRows = report.Programs.Take(5).Select((p, i) => new SmartProgramRow(names[i],
                    p.LaunchesThisWeek > 0
                        ? Loc.F("Defrag_SmartWeek", "×{0} this week", p.LaunchesThisWeek)
                        : Loc.F("Defrag_SmartLast", "last {0}", p.LastLaunchUtc.ToLocalTime().ToString("d MMM")))).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DefragView] Prefetch: {ex.Message}");
                mem.Smart = null;
            }
            if (!_isActive || version != _loadVersion) return;
        }
        ShowSmart(mem);
    }

    /// <summary>The program's own name ("Grand Theft Auto V") when its .exe is on this drive, else "Gta5".</summary>
    private static string ProgramName(char letter, LaunchedProgram program)
    {
        if (program.ExecutablePath != null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo($"{letter}:{program.ExecutablePath}").FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description) && description.Length <= 60) return description;
            }
            catch
            {
            }
        }
        var bare = System.IO.Path.GetFileNameWithoutExtension(program.Name);
        return bare.Length == 0 ? program.Name : char.ToUpperInvariant(bare[0]) + bare[1..].ToLowerInvariant();
    }

    private void ShowSmart(DriveMemory mem)
    {
        var report = mem.Smart;
        if (report == null || !report.PrefetchReadable)
        {
            SmartSummary.Text = Loc.T("Defrag_SmartNoPrefetch",
                "Windows' records of program start-ups (Prefetch) can't be read on this PC — Prefetch may be turned off — so there's nothing to arrange by.");
            SmartPrograms.ItemsSource = null;
        }
        else if (report.Programs.Count == 0)
        {
            SmartSummary.Text = Loc.T("Defrag_SmartNone", "Windows hasn't recorded any program starting from this drive in the last 60 days.");
            SmartPrograms.ItemsSource = null;
        }
        else
        {
            SmartSummary.Text = Loc.F("Defrag_SmartSummary",
                "Windows recorded {0:N0} programs that start from this drive ({1:N0} files). Arranging puts each program's start-up files together, in the order it loads them, near the fast outer edge of the disk.",
                report.Programs.Count, report.Files.Count);
            SmartPrograms.ItemsSource = mem.SmartRows;
        }
        UpdateButtons();
    }

    private void ShowLastAuto()
    {
        if (SmartDefragService.Instance.LastAutomatic is { } last)
        {
            SmartLastAuto.Text = Loc.F("Defrag_SmartLastAuto", "Last automatic run: {0} on {1}: — {2}",
                last.EndedLocal.ToString("HH:mm"), last.Letter, ResultLine(last.Result));
            SmartLastAuto.Visibility = Visibility.Visible;
        }
        else
        {
            SmartLastAuto.Visibility = Visibility.Collapsed;
        }
    }

    private void SmartAuto_Click(object sender, RoutedEventArgs e)
    {
        SmartDefragService.Instance.AutoWhenIdle = SmartAutoOption.IsChecked == true;
        ShowLastAuto();
    }

    private async void SmartArrange_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected == null || !_isAdmin || AnyRunning) return;
        var v = _selected.Volume;
        var report = MemoryFor(v.Letter).Smart;
        if (report is not { Programs.Count: > 0 }) return;

        SafetyReport safety;
        _busy = true;
        UpdateButtons();
        try
        {
            safety = await Task.Run(() => DriveSafety.Check(v, ownMoves: true));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefragView] Safety check: {ex.Message}");
            return;
        }
        finally
        {
            _busy = false;
            if (_isActive) UpdateButtons();
        }
        if (!_isActive || !ShowBlockers(safety, v)) return;

        var question = Loc.F("Defrag_SmartConfirm",
            "Arrange the start-up files of {0:N0} programs on {1}?\n\nWinXTools puts each program's files together, in the order it loads them, so they load with short head movements instead of long jumps. The file system does every move itself, so a file is never half-moved, and you can stop at any time.",
            report.Programs.Count, v.DisplayName) + SafetyNotes(safety);
        if (MessageBox.Show(question, Loc.T("Defrag_SmartTitle", "Smart: programs you use"), MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var session = SmartPlacementSession.Start(v, MeasureOption.IsChecked == true, automatic: false);
        if (session == null)
        {
            MessageBox.Show(Loc.T("Defrag_AlreadyRunning", "Another drive is being optimized. Wait for it to finish or stop it first."),
                Loc.T("Defrag_Title", "Defragment"), MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateButtons();
            return;
        }
        session.Completed += OnSmartCompletedAnywhere;
        _movedBytes = 0;
        AttachSmart(session);
    }

    private void AttachSmart(SmartPlacementSession session)
    {
        DetachSmart();
        _smart = session;
        _lastSmartFile = -1;
        session.Changed += OnSmartChanged;
        session.Completed += OnSmartCompleted;

        Scene.Busy = true;
        RunProgress.Visibility = Visibility.Visible;
        RunProgress.IsIndeterminate = true;
        UnplugBanner.Visibility = session.Removable ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = session.Automatic
            ? Loc.T("Defrag_SmartRunningAuto", "Arranging program files while the PC is idle — it stops as soon as you use the PC.")
            : Loc.T("Defrag_SmartRunning", "Arranging program files — you can keep using the PC.");
        ShowSmartProgress(session);
        _liveTimer.Start();
        UpdateButtons();

        if (!session.IsRunning && session.Result is { } done) _ = HandleSmartCompletedAsync(session, done);
    }

    private void DetachSmart()
    {
        if (_smart == null) return;
        _smart.Changed -= OnSmartChanged;
        _smart.Completed -= OnSmartCompleted;
        _smart = null;
    }

    private void OnSmartChanged(SmartPlacementSession session)
    {
        // Small files move hundreds a second: one UI update at a time is plenty.
        if (Interlocked.Exchange(ref _smartUiPending, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Volatile.Write(ref _smartUiPending, 0);
            if (_isActive && session == _smart) ShowSmartProgress(session);
        });
    }

    private void ShowSmartProgress(SmartPlacementSession session)
    {
        var p = session.LastProgress;
        HudPhase.Text = session.Phase switch
        {
            PlacementPhase.Planning => Loc.T("Defrag_SmartPlanning", "Reading what Windows recorded and planning…"),
            PlacementPhase.MeasuringBefore => Loc.T("Defrag_SmartMeasuringBefore", "Measuring how fast these files load now…"),
            PlacementPhase.Moving when p?.Current is { } path => Loc.F("Defrag_SmartMoving", "Moving {0:N0} of {1:N0}: {2}",
                p.Done + 1, p.Total, System.IO.Path.GetFileName(path)),
            PlacementPhase.Moving => Loc.T("Defrag_SmartFlushing", "Writing everything through to the drive…"),
            PlacementPhase.MeasuringAfter => Loc.T("Defrag_SmartMeasuringAfter", "Measuring the same files again…"),
            _ => HudPhase.Text
        };

        if (p is { BytesTotal: > 0 })
        {
            int percent = (int)Math.Clamp(p.BytesMoved * 100 / p.BytesTotal, 0, 100);
            HudPercent.Text = $"{percent}%";
            RunProgress.IsIndeterminate = false;
            RunProgress.Value = percent;
            HudMoved.Text = CleanerView.FormatSize(p.BytesMoved);
            if (p.Done != _lastSmartFile && p.Current != null && _map != null)
            {
                _lastSmartFile = p.Done;
                int from = (int)Math.Clamp(p.FromFraction * _map.BlockCount, 0, _map.BlockCount - 1);
                int to = (int)Math.Clamp(p.ToFraction * _map.BlockCount, 0, _map.BlockCount - 1);
                Scene.Pulse(new[] { from }, new[] { to });
                FlatMap.Flash(new[] { from, to });
            }
        }
    }

    private void OnSmartCompleted(SmartPlacementSession session, PlacementResult result) =>
        Dispatcher.BeginInvoke(async () =>
        {
            if (_isActive && session == _smart) await HandleSmartCompletedAsync(session, result);
        });

    /// <summary>When nobody is looking at the page, a toast tells the user the run ended.</summary>
    private static void OnSmartCompletedAnywhere(SmartPlacementSession session, PlacementResult result)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_attached != null) return;
            (Application.Current.MainWindow as MainWindow)?.ShowToast(
                Loc.F("Defrag_SmartToast", "Drive {0}: {1}", session.Letter, ResultLine(result)));
        });
    }

    private async Task HandleSmartCompletedAsync(SmartPlacementSession session, PlacementResult result)
    {
        _liveTimer.Stop();
        DetachSmart();
        Scene.Busy = false;
        RunProgress.Visibility = Visibility.Collapsed;
        UnplugBanner.Visibility = Visibility.Collapsed;
        if (result.Outcome == PlacementOutcome.Completed) HudPercent.Text = "100%";

        string text = ResultLine(result);
        if (session.SpeedBefore is double before && session.SpeedAfter is double after && before > 0)
            text += "\n" + (after / before >= 1.05
                ? Loc.F("Defrag_SmartGain", "They now load {0:0.0}× as fast ({1:0} → {2:0} MB/s, read straight from the disk).", after / before, before, after)
                : Loc.F("Defrag_SmartNoGain", "Their load speed barely changed ({0:0} → {1:0} MB/s) — they weren't slowing this drive down much.", before, after));
        StatusText.Text = text;
        HudPhase.Text = ResultLine(result);
        ShowLastAuto();
        UpdateButtons();

        if (_selected?.Volume.Letter == session.Letter) await LoadMapAsync(_loadVersion);
    }

    private static string ResultLine(PlacementResult result)
    {
        string text = result.Outcome switch
        {
            PlacementOutcome.Completed => Loc.F("Defrag_SmartDone", "Arranged {0:N0} files ({1}) in {2}.",
                result.Moved, CleanerView.FormatSize(result.BytesMoved), FormatDuration(result.Duration)),
            PlacementOutcome.NothingToDo => Loc.T("Defrag_SmartNothing",
                "Nothing to move — these programs' files are already in one piece near the front of the drive."),
            PlacementOutcome.Stopped => StopText(result.StopReason ?? StopReason.User),
            _ => Loc.F("Defrag_SmartFailed", "Couldn't arrange the files (Windows error {0}).", result.LastError)
        };
        if (result.Outcome == PlacementOutcome.Completed && result.Failed > 0)
            text += " " + Loc.F("Defrag_SmartSomeSkipped", "{0:N0} files couldn't be moved (in use or changed meanwhile) and were left as they were.", result.Failed);
        return text;
    }

    private static string StopText(StopReason reason) => reason switch
    {
        StopReason.PowerUnplugged => Loc.T("Defrag_StopPower",
            "Stopped because the PC switched to battery power. Every file is whole — plug in and start again to continue."),
        StopReason.DriveRemoval => Loc.T("Defrag_StopRemoval",
            "Stopped because Windows was asked to remove the drive. Every file is whole, and the drive can be removed now."),
        StopReason.DriveGone => Loc.T("Defrag_StopGone",
            "The drive was disconnected while it was working. The file system keeps every file whole; when you plug it in again and Windows offers to check it, let it."),
        StopReason.UserActive => Loc.T("Defrag_StopUserActive",
            "Paused because you started using the PC — it carries on another time the PC is idle."),
        _ => Loc.T("Defrag_StopUser", "Stopped. Every file is whole — the ones already moved are in their new place.")
    };

    #endregion

    #region Safety before moving data

    private static bool IsRemovable(DriveVolume v) => v.IsRemovable || v.Disk?.Bus is DriveBus.Usb or DriveBus.SdCard;

    /// <summary>Shows what stands in the way; false when the operation must not start.</summary>
    private static bool ShowBlockers(SafetyReport safety, DriveVolume v)
    {
        if (safety.CanStart) return true;
        var reasons = safety.Findings.Where(f => f.Blocks).Select(f => "• " + IssueText(f, v));
        MessageBox.Show(Loc.F("Defrag_Blocked", "WinXTools won't start on {0} right now:\n\n{1}", v.DisplayName, string.Join("\n", reasons)),
            Loc.T("Defrag_Title", "Defragment"), MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>Warnings that don't block, and the "don't unplug" rule for USB drives, for the confirmation text.</summary>
    private static string SafetyNotes(SafetyReport safety)
    {
        var notes = safety.Findings.Where(f => !f.Blocks).Select(f => IssueText(f, null)).ToList();
        if (safety.IsRemovable)
            notes.Add(Loc.T("Defrag_UnplugNote",
                "This is a removable (USB) drive: don't unplug it until it has finished. To take it out early, press Stop first — or use Safely Remove: WinXTools stops and lets go of the drive by itself."));
        return notes.Count == 0 ? "" : "\n\n" + string.Join("\n\n", notes.Select(n => "⚠ " + n));
    }

    private static string IssueText(SafetyFinding finding, DriveVolume? v) => finding.Issue switch
    {
        SafetyIssue.VolumeDirty => Loc.F("Defrag_IssueDirty",
            "The drive has file system errors. Run \"chkdsk {0}: /f\" first — moving files on a damaged file system could make it worse.", v?.Letter ?? '?'),
        SafetyIssue.DiskFailing => Loc.T("Defrag_IssueFailing",
            "The drive reports that it is failing. Copy your files somewhere else first — moving data around now puts extra strain on it."),
        SafetyIssue.DiskWarning => Loc.T("Defrag_IssueWarning", "The drive reports a health warning. Back up your important files first."),
        SafetyIssue.OnBattery => Loc.T("Defrag_IssueBattery",
            "The PC is running on battery. Plug it in first — if the battery ran out half-way, the drive would lose power suddenly."),
        SafetyIssue.NoCacheFlush => finding.Blocks
            ? Loc.T("Defrag_IssueNoFlushBlock",
                "This drive keeps written data in a cache that can't be emptied on request, so unplugging it or a power cut could lose the last writes. WinXTools won't move files on it.")
            : Loc.T("Defrag_IssueNoFlush",
                "This drive keeps written data in a cache that can't be emptied on request: keep it connected and powered until it has finished."),
        SafetyIssue.FlushingTurnedOff => Loc.T("Defrag_IssueFlushOff",
            "Windows is set not to flush this drive's write cache (Device Manager → the drive → Policies). Turn that off first, or keep the drive connected and powered until it has finished."),
        SafetyIssue.LowFreeSpace => Loc.T("Defrag_IssueSpace", "Less than 3% of the drive is free — free up some space first."),
        SafetyIssue.NotNtfs => Loc.T("Defrag_IssueNtfs", "Arranging files works on NTFS drives only."),
        _ => finding.Issue.ToString()
    };

    #endregion

    #region Live map while optimizing

    private async Task RefreshLiveMapAsync()
    {
        if (_mapInFlight || !_isActive || _selected == null || !(_session is { IsRunning: true } || _smart is { IsRunning: true })) return;
        _mapInFlight = true;
        try
        {
            var previous = _map;
            char letter = _selected.Volume.Letter;
            // Ask for the same block count as the first read: the same volume then folds into identical blocks.
            int blocks = _selected.Volume.Disk?.IsSsd == true ? BoardBlocks : PlatterBlocks;
            var current = await Task.Run(() => VolumeMapReader.ReadBitmap(letter, blocks));
            if (!_isActive) return;

            if (previous != null && previous.BlockCount == current.BlockCount)
            {
                var sources = new List<(int Block, float Delta)>();
                var targets = new List<(int Block, float Delta)>();
                var touched = new List<int>();
                for (int b = 0; b < current.BlockCount; b++)
                {
                    float delta = current.Used[b] - previous.Used[b];
                    if (delta < -0.002f) sources.Add((b, -delta));
                    else if (delta > 0.002f) targets.Add((b, delta));
                    else if (current.Signature[b] != previous.Signature[b]) touched.Add(b);
                }
                double grown = targets.Sum(t => (double)t.Delta) * current.BytesPerBlock;
                _movedBytes += (long)grown;

                var src = sources.OrderByDescending(s => s.Delta).Select(s => s.Block).Concat(touched).Take(28).ToList();
                var dst = targets.OrderByDescending(t => t.Delta).Select(t => t.Block).Concat(touched).Take(28).ToList();
                if (src.Count > 0 || dst.Count > 0)
                {
                    Scene.Pulse(src, dst);
                    FlatMap.Flash(src.Concat(dst));
                }

                // Space that became free can't hold fragmented data any more.
                if (_fragLayer != null)
                    for (int b = 0; b < current.BlockCount; b++)
                        _fragLayer[b] = Math.Min(_fragLayer[b], current.Used[b]);
            }

            _map = current;
            PushLayers();
            HudMoved.Text = CleanerView.FormatSize(_movedBytes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefragView] Live map: {ex.Message}");
        }
        finally
        {
            _mapInFlight = false;
        }
    }

    private void UpdateIo()
    {
        if (_io != null && _io.Sample(out var read, out var write))
        {
            HudRead.Text = SpeedFormat.Speed(read);
            HudWrite.Text = SpeedFormat.Speed(write);
        }
        if (_session is { IsRunning: true } s)
            HudElapsed.Text = FormatDuration(DateTime.UtcNow - s.StartedUtc);
        else if (_smart is { IsRunning: true } p)
            HudElapsed.Text = FormatDuration(DateTime.UtcNow - p.StartedUtc);
        else if (_smart == null && SmartPlacementSession.Current is { IsRunning: true } auto && auto.Letter == _selected?.Volume.Letter && !_busy)
            AttachSmart(auto); // started by the idle mode while this page is open
    }

    #endregion

    #region Drawing helpers

    private void ApplyLayout(LayoutScan? layout)
    {
        if (layout == null || _map == null || layout.Fragmented.Length != _map.BlockCount)
        {
            _fragLayer = _metaLayer = _unmovLayer = null;
            WorstCard.Visibility = Visibility.Collapsed;
            return;
        }
        _fragLayer = (float[])layout.Fragmented.Clone();
        _metaLayer = layout.Metadata;
        _unmovLayer = layout.Unmovable;

        WorstList.ItemsSource = layout.Worst.Take(12).Select(f => new
        {
            f.Path,
            Name = System.IO.Path.GetFileName(f.Path) is { Length: > 0 } n ? n : f.Path,
            PiecesText = Loc.F("Defrag_Pieces", "{0:N0}×", f.Fragments)
        }).ToList();
        WorstCard.Visibility = layout.Worst.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PushLayers()
    {
        if (_map == null) return;
        // The file system rounds the block size, so the map can have a few blocks
        // fewer than asked for; the 3D model must be built for exactly that many.
        if (Scene.BlockCount != _map.BlockCount) Scene.Configure(_sceneLayout, _span0, _span1, _map.BlockCount);
        Scene.SetLayers(_map.Used, _fragLayer, _metaLayer, _unmovLayer);
        FlatMap.SetLayers(_map.Used, _fragLayer, _metaLayer, _unmovLayer);
    }

    private void ShowMemory()
    {
        if (_selected == null) return;
        var mem = MemoryFor(_selected.Volume.Letter);
        Fill(mem.Before, BeforeFrag, BeforeFiles, BeforePieces, BeforeFree, BeforeLargest);
        Fill(mem.After, AfterFrag, AfterFiles, AfterPieces, AfterFree, AfterLargest);
        BeforeSpeed.Text = mem.BeforeSpeed is double b ? $"{b:0.#} MB/s" : "—";
        AfterSpeed.Text = mem.AfterSpeed is double a ? $"{a:0.#} MB/s" : "—";

        if (mem is { BeforeSpeed: double before, AfterSpeed: double after } && before > 0)
        {
            double ratio = after / before;
            GainText.Text = ratio >= 1.05
                ? Loc.F("Defrag_Gain", "Fragmented files now read {0:0.0}× as fast ({1:0} → {2:0} MB/s).", ratio, before, after)
                : Loc.F("Defrag_NoGain", "Read speed of those files barely changed ({0:0} → {1:0} MB/s) — they weren't slowing this drive down much.", before, after);
            GainText.Foreground = (Brush)FindResource(ratio >= 1.05 ? "SuccessBrush" : "TextSecondaryBrush");
            GainText.Visibility = Visibility.Visible;
        }
        else if (mem is { Before: { } bs, After: { } af })
        {
            GainText.Text = Loc.F("Defrag_FragGain", "Fragmentation {0}% → {1}%.", bs.TotalPercentFragmentation, af.TotalPercentFragmentation);
            GainText.Foreground = (Brush)FindResource("SuccessBrush");
            GainText.Visibility = Visibility.Visible;
        }
        else
        {
            GainText.Visibility = Visibility.Collapsed;
        }
        AdviceText.Visibility = mem.Before == null ? Visibility.Collapsed : AdviceText.Visibility;
    }

    private static void Fill(DefragStats? s, TextBlock frag, TextBlock files, TextBlock pieces, TextBlock free, TextBlock largest)
    {
        frag.Text = s == null ? "—" : $"{s.TotalPercentFragmentation}%";
        files.Text = s == null ? "—" : s.TotalFragmentedFiles.ToString("N0");
        pieces.Text = s == null ? "—" : s.TotalExcessFragments.ToString("N0");
        free.Text = s == null ? "—" : $"{s.FreeSpacePercentFragmentation}%";
        largest.Text = s == null || s.LargestFreeExtent <= 0 ? "—" : CleanerView.FormatSize(s.LargestFreeExtent);
    }

    private void UpdateButtons()
    {
        bool running = _session is { IsRunning: true } || _smart is { IsRunning: true } || AnyRunning;
        bool canAct = _isAdmin && !_busy && !running && _selected != null;
        AnalyzeButton.IsEnabled = canAct;
        OptimizeButton.IsEnabled = canAct;
        bool ours = _session is { IsRunning: true } || _smart is { IsRunning: true };
        StopButton.Visibility = ours ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = _session is { IsRunning: true, CancelRequested: false } || _smart is { IsRunning: true, StopRequested: false };
        MeasureOption.IsEnabled = canAct;
        ConsolidateOption.IsEnabled = canAct && _selected?.Volume.Disk?.IsSsd != true;
        SmartButton.IsEnabled = canAct && _selected != null && MemoryFor(_selected.Volume.Letter).Smart is { Programs.Count: > 0 };
        SmartAutoOption.IsEnabled = _isAdmin;
        foreach (var d in _drives) d.IsEnabled = !_busy && !running;
    }

    private string DescribeDrive(DefragDriveItem item)
    {
        var disk = item.Volume.Disk;
        if (disk?.IsSsd == true)
            return Loc.T("Defrag_DescSsd", "SSD — no moving parts, so fragmentation doesn't slow it down. WinXTools runs TRIM instead of defragmenting.");
        if (disk?.IsHdd == true)
            return Loc.T("Defrag_DescHdd", "Hard disk with a spinning platter — files in many pieces make the head jump around. Press Analyze to see how fragmented it is.");
        return Loc.T("Defrag_DescUnknown", "Windows doesn't say whether this is a hard disk or an SSD (common with USB drives). Its optimizer will measure the drive and do the right thing.");
    }

    private void ShowCenter(string text)
    {
        CenterMessage.Text = text;
        CenterMessage.Visibility = Visibility.Visible;
    }

    private void HideCenter() => CenterMessage.Visibility = Visibility.Collapsed;

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    #endregion
}

/// <summary>One program in the smart card.</summary>
public sealed record SmartProgramRow(string Name, string Detail);

/// <summary>A drive card on the Defrag page.</summary>
public sealed class DefragDriveItem : INotifyPropertyChanged
{
    public DefragDriveItem(DriveVolume volume)
    {
        Volume = volume;
        LetterText = $"{volume.Letter}:";
        Title = string.IsNullOrWhiteSpace(volume.Label) ? Loc.T("Disk_LocalDisk", "Local Disk") : volume.Label;
        Model = string.IsNullOrWhiteSpace(volume.Disk?.Model) ? Loc.T("Disk_UnknownModel", "Unknown drive") : volume.Disk!.Model;
        MediaText = DiskText.Media(volume.Disk);
        BadgeBrush = DiskText.MediaBrush(volume.Disk);
        double used = volume.TotalBytes > 0 ? volume.UsedBytes / (double)volume.TotalBytes : 0;
        UsedBarWidth = Math.Max(2, 186 * used);
        UsageText = Loc.F("Disk_UsedOf", "{0} free of {1}", CleanerView.FormatSize(volume.FreeBytes), CleanerView.FormatSize(volume.TotalBytes));
    }

    public DriveVolume Volume { get; }
    public string LetterText { get; }
    public string Title { get; }
    public string Model { get; }
    public string MediaText { get; }
    public Brush BadgeBrush { get; }
    public double UsedBarWidth { get; }
    public string UsageText { get; }

    public string FragText { get; private set; } = "";
    public Brush FragBrush { get; private set; } = Brushes.Gray;
    public Visibility FragVisibility => FragText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public void SetFragmentation(int percent)
    {
        FragText = Loc.F("Defrag_CardFrag", "Fragmented: {0}%", percent);
        FragBrush = (Application.Current.TryFindResource(percent >= 10 ? "WarningBrush" : "SuccessBrush") as Brush) ?? Brushes.Gray;
        OnPropertyChanged(nameof(FragText));
        OnPropertyChanged(nameof(FragBrush));
        OnPropertyChanged(nameof(FragVisibility));
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
    }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(nameof(IsEnabled)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Shared wording for drive types (Defrag, Speed test and Disk space pages).</summary>
public static class DiskText
{
    public static string Media(PhysicalDisk? disk)
    {
        if (disk == null) return Loc.T("Disk_KindUnknown", "Unknown type");
        string bus = disk.Bus switch
        {
            DriveBus.Nvme => "NVMe",
            DriveBus.Sata or DriveBus.Raid when disk.IsSsd => "SATA",
            DriveBus.Usb => "USB",
            DriveBus.SdCard => "SD",
            _ => ""
        };
        string smr = DriveReference.IsSmr(disk) == true ? " · SMR" : "";
        return disk.Media switch
        {
            DriveMedia.Hdd => (disk.Rpm is > 0
                ? Loc.F("Disk_KindHddRpm", "Hard disk · {0:N0} rpm", disk.Rpm)
                : (disk.Bus == DriveBus.Usb ? Loc.T("Disk_KindUsbHdd", "USB hard disk") : Loc.T("Disk_KindHdd", "Hard disk (HDD)"))) + smr,
            DriveMedia.Ssd => bus.Length > 0 ? $"{bus} SSD" : "SSD",
            _ => disk.Bus == DriveBus.Usb ? Loc.T("Disk_KindUsb", "USB drive") : Loc.T("Disk_KindUnknown", "Unknown type")
        };
    }

    public static Brush MediaBrush(PhysicalDisk? disk)
    {
        var key = disk?.Media switch
        {
            DriveMedia.Hdd => "WarningBrush",
            DriveMedia.Ssd => "AccentPrimaryBrush",
            _ => "SteelLightBrush"
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
}

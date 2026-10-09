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

/// <summary>
/// Bad-block scan: reads a whole disk (or thousands of points over it) straight
/// from the hardware, read-only, and paints every stretch on the 3D platter as
/// it goes — healthy, slow, very slow, unreadable — while the head follows the
/// scan. Unreadable sectors are pinned down to 4 KB and to the file that uses
/// them; "Mark bad sectors" then runs chkdsk /r so Windows never uses them again.
/// The scan outlives the page (it can take hours); closing WinXTools stops it.
/// </summary>
public partial class DiskHealthView : Page
{
    private const int PlatterBlocks = 16384;
    private const int BoardBlocks = 8192;

    private readonly ObservableCollection<HealthDiskItem> _disks = new();
    private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private HealthDiskItem? _selected;
    private SurfaceHealthScan? _scan;
    private bool _isActive, _isAdmin, _repairing;
    private int _shownErrors;

    public DiskHealthView()
    {
        InitializeComponent();
        DiskList.ItemsSource = _disks;
        _hudTimer.Tick += (s, e) => UpdateFromScan();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    #region Lifetime and disks

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isActive = true;
        _isAdmin = AdminHelper.IsRunAsAdmin();
        StatusText.Text = Loc.T("Health_Loading", "Reading your disks…");

        List<DriveVolume> volumes;
        try
        {
            volumes = await Task.Run(DriveInventory.GetVolumes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiskHealthView] {ex.Message}");
            volumes = new List<DriveVolume>();
        }
        if (!_isActive) return;

        _disks.Clear();
        foreach (var group in volumes.Where(v => v.DiskNumber != null && v.Disk != null).GroupBy(v => v.DiskNumber!.Value).OrderBy(g => g.Key))
            _disks.Add(new HealthDiskItem(group.First().Disk!, group.ToList()));

        var running = SurfaceHealthScan.Current;
        var pick = (running != null ? _disks.FirstOrDefault(d => d.Disk.Number == running.State.DiskNumber) : null)
                   ?? _disks.FirstOrDefault(d => d.Disk.IsHdd) ?? _disks.FirstOrDefault();
        if (pick == null)
        {
            StatusText.Text = Loc.T("Health_NoDisks", "No disks were found.");
            UpdateButtons();
            return;
        }
        pick.IsSelected = true;
        if (_selected != pick) SelectDisk(pick);
        if (running != null && running.State.DiskNumber == pick.Disk.Number) Attach(running);
        if (!_isAdmin) ShowCenter(Loc.T("Health_NeedsAdmin", "WinXTools must run as administrator to read a disk directly."));
        UpdateButtons();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isActive = false;
        _hudTimer.Stop();
        Detach();
    }

    private void DiskCard_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: HealthDiskItem item } && item != _selected) SelectDisk(item);
    }

    private void SelectDisk(HealthDiskItem item)
    {
        if (_scan is { IsRunning: true }) return;
        _selected = item;
        var disk = item.Disk;
        bool ssd = disk.IsSsd;
        Scene.Configure(ssd ? DiskSceneLayout.Board : DiskSceneLayout.Platter, 0, 1, ssd ? BoardBlocks : PlatterBlocks);
        var blank = new byte[Scene.BlockCount];
        Scene.SetHealth(blank);
        FlatMap.SetHealth(blank);
        HudDisk.Text = $"{item.LettersText}  {disk.Model}";
        HudMedia.Text = $"{item.MediaText} · {CleanerView.FormatSize(disk.SizeBytes)}";
        HudPercent.Text = "";
        HudPhase.Text = "";
        HudSpeed.Text = HudLatency.Text = HudElapsed.Text = HudRemaining.Text = "—";
        CountGood.Text = CountSlow.Text = CountVerySlow.Text = CountError.Text = "—";
        MedianText.Text = "";
        BadCard.Visibility = Visibility.Collapsed;
        AlertBanner.Visibility = Visibility.Collapsed;
        AdviceText.Visibility = Visibility.Collapsed;
        MapCaption.Text = Loc.F("Health_MapCaption", "Each cell ≈ {0}", CleanerView.FormatSize(disk.SizeBytes / Math.Max(1, Scene.BlockCount)));
        StatusText.Text = Loc.T("Health_Ready",
            "Quick scan reads 4,096 points spread over the disk (about a minute). Full scan reads every sector (hours on a large hard disk). Both only read — nothing is written.");
        HideCenter();
        _ = LoadReliabilityAsync(item);
        UpdateButtons();
    }

    private async Task LoadReliabilityAsync(HealthDiskItem item)
    {
        ReliabilityText.Text = Loc.T("Health_Reading", "Asking the drive…");
        var counters = await Task.Run(() => DiskReliability.Read(item.Disk.Number));
        if (!_isActive || _selected != item) return;
        if (counters == null)
        {
            ReliabilityText.Text = _isAdmin
                ? Loc.T("Health_NoCounters", "This drive doesn't share its own error counters with Windows.")
                : Loc.T("Health_CountersAdmin", "Run WinXTools as administrator to read the drive's own counters.");
            return;
        }
        var lines = new List<string>();
        if (counters.Temperature is int t and > 0)
            lines.Add(Loc.F("Health_Temp", "Temperature: {0} °C", t) + (counters.TemperatureMax is int tm and > 0 ? Loc.F("Health_TempMax", " (highest {0} °C)", tm) : ""));
        if (counters.PowerOnHours is long h and > 0) lines.Add(Loc.F("Health_Hours", "Powered on: {0:N0} hours (≈ {1:0.#} years)", h, h / 8766.0));
        if (counters.Wear is int w and > 0) lines.Add(Loc.F("Health_Wear", "Wear: {0}% of its rated life used", w));
        if (counters.ReadErrorsUncorrected is long ru) lines.Add(Loc.F("Health_ReadErrors", "Read errors it couldn't correct: {0:N0}", ru));
        if (counters.WriteErrorsUncorrected is long wu) lines.Add(Loc.F("Health_WriteErrors", "Write errors it couldn't correct: {0:N0}", wu));
        if (counters.StartStopCycles is long sc and > 0) lines.Add(Loc.F("Health_Cycles", "Start/stop cycles: {0:N0}", sc));
        ReliabilityText.Text = lines.Count > 0 ? string.Join("\n", lines) : Loc.T("Health_NoCounters", "This drive doesn't share its own error counters with Windows.");
        ReliabilityText.Foreground = (Brush)FindResource((counters.ReadErrorsUncorrected ?? 0) > 0 ? "WarningBrush" : "TextSecondaryBrush");
    }

    #endregion

    #region Scan

    private void Quick_Click(object sender, RoutedEventArgs e) => StartScan(full: false);

    private void Full_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var disk = _selected.Disk;
        double mbps = disk.IsSsd ? (disk.Bus == NetX.Core.System.Storage.DriveBus.Nvme ? 2000 : 450) : disk.Bus == NetX.Core.System.Storage.DriveBus.Usb ? 100 : 130;
        var estimate = TimeSpan.FromSeconds(disk.SizeBytes / 1e6 / mbps);
        if (MessageBox.Show(Loc.F("Health_ConfirmFull",
                    "Read every sector of {0} ({1})?\n\nIt takes about {2}. It only reads — nothing on the disk is changed — and you can keep using the PC, pause or stop at any time.",
                    _selected.Model, CleanerView.FormatSize(disk.SizeBytes), FormatDuration(estimate)),
                Loc.T("Health_Title", "Bad block scan"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        StartScan(full: true);
    }

    private void StartScan(bool full)
    {
        if (_selected == null || !_isAdmin || _scan is { IsRunning: true } || _repairing) return;
        var scan = SurfaceHealthScan.Start(_selected.Disk.Number, _selected.Disk.SizeBytes, full, Scene.BlockCount);
        if (scan == null)
        {
            MessageBox.Show(Loc.T("Health_AlreadyRunning", "A scan is already running."), Loc.T("Health_Title", "Bad block scan"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        scan.Completed += OnCompletedAnywhere;
        _shownErrors = 0;
        Attach(scan);
    }

    private void Attach(SurfaceHealthScan scan)
    {
        Detach();
        _scan = scan;
        scan.Completed += OnCompleted;
        Scene.SetHealth(scan.State.Map);
        FlatMap.SetHealth(scan.State.Map);
        Scene.Busy = true;
        RunProgress.Visibility = Visibility.Visible;
        StatusText.Text = scan.State.Full
            ? Loc.T("Health_RunningFull", "Reading every sector — read-only. You can keep using the PC.")
            : Loc.T("Health_RunningQuick", "Reading 4,096 points spread over the disk — read-only.");
        HideCenter();
        _hudTimer.Start();
        UpdateButtons();
        if (!scan.IsRunning) ShowFinished(scan);
    }

    private void Detach()
    {
        if (_scan == null) return;
        _scan.Completed -= OnCompleted;
        _scan = null;
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is not { IsRunning: true } scan) return;
        scan.Pause(!scan.IsPaused);
        UpdateButtons();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _scan?.Stop();
        StopButton.IsEnabled = false;
    }

    /// <summary>Four times a second: the scan fills its map in place; the view repaints and follows the head.</summary>
    private void UpdateFromScan()
    {
        if (_scan is not { } scan || !_isActive) return;
        var s = scan.State;
        Scene.InvalidateColors();
        FlatMap.SetHealth(s.Map);
        if (scan.IsRunning)
        {
            double head = s.Verifying ? s.VerifyPosition : s.Position;
            Scene.SeekToFraction(head);
            int block = (int)Math.Min(s.Blocks - 1, head * s.Blocks);
            Scene.Flash(block, 0.9f);
        }

        double percent = s.Verifying && s.VerifyTotal > 0
            ? s.VerifyDone * 100.0 / s.VerifyTotal
            : Math.Clamp(s.Position * 100, 0, 100);
        HudPercent.Text = $"{percent:0.0}%";
        RunProgress.Value = percent;
        HudSpeed.Text = s.Mbps > 0 && !s.Verifying ? $"{s.Mbps:0} MB/s" : "—";
        HudLatency.Text = s.LastMs > 0 ? $"{s.LastMs:0.0} ms" : "—";
        HudElapsed.Text = FormatDuration(s.Elapsed);
        HudRemaining.Text = !scan.IsRunning ? "—"
            : s.Verifying ? (s.VerifyDone > 0 ? FormatDuration(TimeSpan.FromSeconds(Math.Max(0, (s.Elapsed - s.VerifyStarted).TotalSeconds) / s.VerifyDone * (s.VerifyTotal - s.VerifyDone))) : "—")
            : s.Position > 0.002 ? FormatDuration(TimeSpan.FromSeconds(s.Elapsed.TotalSeconds / s.Position * (1 - s.Position))) : "—";
        HudPhase.Text = scan.IsPaused ? Loc.T("Health_Paused", "Paused")
            : s.Verifying ? Loc.F("Health_PhaseVerify", "Re-checking slow areas: {0:N0} of {1:N0}", s.VerifyDone, s.VerifyTotal)
            : s.Full ? Loc.T("Health_PhaseFull", "Full scan") : Loc.T("Health_PhaseQuick", "Quick scan");
        if (s.Verifying && scan.IsRunning)
            StatusText.Text = Loc.T("Health_VerifyStatus",
                "Reading the slow areas once more: one that reads normally now was only interrupted by another program; one that is still slow is really weak.");

        CountGood.Text = s.ReadsGood.ToString("N0");
        CountSlow.Text = s.ReadsSlow.ToString("N0");
        CountVerySlow.Text = s.ReadsVerySlow.ToString("N0");
        CountError.Text = s.ReadsError.ToString("N0");
        if (s.MedianMs > 0)
            MedianText.Text = Loc.F("Health_Median", "Normal read here: {0:0.0} ms per MB · slow > {1:0} ms · very slow > {2:0} ms", s.MedianMs, s.SlowMs, s.VerySlowMs);

        int errors;
        lock (s.BadSectors) errors = s.BadSectors.Count;
        if (errors > _shownErrors)
        {
            _shownErrors = errors;
            AlertText.Text = Loc.F("Health_AlertBad", "Unreadable sectors found: {0:N0} so far", errors);
            AlertBanner.Visibility = Visibility.Visible;
            ShowBadList(s);
        }
    }

    private void OnCompleted(SurfaceHealthScan scan) => Dispatcher.BeginInvoke(() =>
    {
        if (_isActive && scan == _scan) ShowFinished(scan);
    });

    private static void OnCompletedAnywhere(SurfaceHealthScan scan) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        var s = scan.State;
        if (s.Cancelled) return; // stopped on purpose: nothing to announce
        if (Application.Current.MainWindow is MainWindow main && main.Content is not null)
        {
            main.ShowToast(s.FatalError != 0 || s.Disconnected
                ? Loc.F("Health_ToastFailed", "Disk {0}: the scan couldn't finish.", s.DiskNumber)
                : s.ReadsError > 0
                    ? Loc.F("Health_ToastBad", "Disk {0}: scan finished — {1:N0} unreadable areas found.", s.DiskNumber, s.ReadsError)
                    : Loc.F("Health_ToastDone", "Disk {0}: scan finished — no unreadable sectors.", s.DiskNumber));
        }
    });

    private void ShowFinished(SurfaceHealthScan scan)
    {
        _hudTimer.Stop();
        UpdateFromScan();
        var s = scan.State;
        Scene.Busy = false;
        Scene.SeekToFraction(null);
        RunProgress.Visibility = Visibility.Collapsed;
        UpdateButtons();

        if (s.FatalError != 0)
        {
            StatusText.Text = Loc.F("Health_Failed", "The disk couldn't be read (Windows error {0}).", s.FatalError);
            return;
        }
        if (s.Disconnected)
        {
            StatusText.Text = Loc.T("Health_Disconnected", "The disk disappeared during the scan (unplugged or it stopped answering).");
            return;
        }

        long total = s.ReadsGood + s.ReadsSlow + s.ReadsVerySlow + s.ReadsError;
        StatusText.Text = (s.Cancelled ? Loc.T("Health_Stopped", "Stopped.") + " " : "") +
                          Loc.F("Health_Summary", "{0:N0} areas read in {1}: {2:N0} healthy, {3:N0} slow, {4:N0} very slow, {5:N0} unreadable.",
                              total, FormatDuration(s.Elapsed), s.ReadsGood, s.ReadsSlow, s.ReadsVerySlow, s.ReadsError) +
                          (s.Recovered > 0
                              ? "\n" + Loc.F("Health_Recovered", "{0:N0} areas that were slow the first time read normally on a second try (something else was using the disk then) — they count as healthy.", s.Recovered)
                              : "");

        string advice = s.ReadsError > 0
            ? Loc.T("Health_AdviceBad", "This disk has sectors it can no longer read. Copy your important files elsewhere now — then mark the bad sectors so Windows stops using them.")
            : s.ReadsVerySlow > 0
                ? Loc.T("Health_AdviceVerySlow", "Some areas only read after many retries: the surface is weakening. Back up your important files and scan again in a few weeks.")
                : s.ReadsSlow > total / 50
                    ? Loc.T("Health_AdviceSlow", "A few areas read slowly — common on older disks or while other programs use it. Nothing unreadable.")
                    : Loc.T("Health_AdviceGood", "The surface reads cleanly — no bad sectors.");
        AdviceText.Text = advice;
        AdviceText.Foreground = (Brush)FindResource(s.ReadsError > 0 ? "DangerBrush" : s.ReadsVerySlow > 0 ? "WarningBrush" : "SuccessBrush");
        AdviceText.Visibility = Visibility.Visible;
        ShowBadList(s);
    }

    private void ShowBadList(HealthScanState s)
    {
        List<BadSector> bad;
        lock (s.BadSectors) bad = s.BadSectors.ToList();
        if (bad.Count == 0)
        {
            BadCard.Visibility = Visibility.Collapsed;
            return;
        }
        BadCard.Visibility = Visibility.Visible;
        BadList.ItemsSource = bad.Take(40).Select(b => new BadRow(b)).ToList();
        BadMore.Text = bad.Count > 40 ? Loc.F("Health_BadMore", "…and {0:N0} more", bad.Count - 40) : "";
        BadMore.Visibility = bad.Count > 40 ? Visibility.Visible : Visibility.Collapsed;
        MarkButton.IsEnabled = s.Finished && bad.Any(b => b.Volume != null) && !_repairing;
    }

    #endregion

    #region Mark bad sectors (chkdsk /r)

    private async void Mark_Click(object sender, RoutedEventArgs e)
    {
        if (_scan?.State is not { Finished: true } s || _repairing) return;
        List<char> letters;
        lock (s.BadSectors) letters = s.BadSectors.Where(b => b.Volume != null).Select(b => b.Volume!.Value).Distinct().OrderBy(l => l).ToList();
        if (letters.Count == 0) return;

        char system = char.ToUpperInvariant(Environment.GetFolderPath(Environment.SpecialFolder.Windows)[0]);
        var question = Loc.F("Health_ConfirmMark",
            "Mark the bad sectors on {0} so Windows never uses them again?\n\nWindows' own check (chkdsk /r) reads every cluster of the drive, moves whatever can still be read to healthy space and records the bad ones in the file system's bad-cluster list. It takes a long time (hours on a large hard disk).\n\n• A data drive is locked while it runs: programs with files open on it lose them — save and close them first.\n• The Windows drive is checked at the next restart.\n\nBack up your important files first if you can.",
            string.Join(", ", letters.Select(l => $"{l}:")));
        if (MessageBox.Show(question, Loc.T("Health_Title", "Bad block scan"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _repairing = true;
        UpdateButtons();
        MarkButton.IsEnabled = false;
        var results = new List<string>();
        try
        {
            foreach (var letter in letters)
            {
                bool isSystem = letter == system;
                StatusText.Text = isSystem
                    ? Loc.F("Health_Scheduling", "Scheduling the check of {0}: for the next restart…", letter)
                    : Loc.F("Health_Marking", "Checking {0}: and marking bad sectors — this takes a while…", letter);
                var progress = new Progress<(string Line, int? Percent)>(p =>
                {
                    if (!_isActive) return;
                    HudPhase.Text = p.Line;
                    if (p.Percent is int pct) HudPercent.Text = $"{pct}%";
                });
                var outcome = await BadClusterRepair.RunAsync(letter, isSystem, progress, CancellationToken.None);
                results.Add(outcome.Scheduled
                    ? Loc.F("Health_MarkScheduled", "{0}: checked at the next restart.", letter)
                    : outcome.Completed
                        ? Loc.F("Health_MarkDone", "{0}: done — bad sectors are now marked and won't be used.", letter)
                        : Loc.F("Health_MarkFailed", "{0}: chkdsk ended with code {1} — {2}", letter, outcome.ExitCode, outcome.LastLine));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiskHealthView] chkdsk: {ex}");
            results.Add(Loc.F("Health_MarkError", "Couldn't run chkdsk: {0}", ErrorText.From(ex)));
        }
        finally
        {
            _repairing = false;
            if (_isActive)
            {
                StatusText.Text = string.Join("\n", results);
                UpdateButtons();
            }
        }
    }

    #endregion

    #region Helpers

    private void UpdateButtons()
    {
        bool running = _scan is { IsRunning: true };
        bool canStart = _isAdmin && _selected != null && !running && !_repairing && SurfaceHealthScan.Current is not { IsRunning: true };
        QuickButton.IsEnabled = canStart;
        FullButton.IsEnabled = canStart;
        PauseButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = running;
        PauseButton.SetResourceReference(ContentProperty, _scan?.IsPaused == true ? "Health_Resume" : "Health_Pause");
        foreach (var d in _disks) d.IsEnabled = !running && !_repairing;
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

/// <summary>A physical disk card on the bad-block page.</summary>
public sealed class HealthDiskItem : INotifyPropertyChanged
{
    public HealthDiskItem(PhysicalDisk disk, List<DriveVolume> volumes)
    {
        Disk = disk;
        LettersText = string.Join(" ", volumes.Select(v => $"{v.Letter}:"));
        Model = string.IsNullOrWhiteSpace(disk.Model) ? Loc.T("Disk_UnknownModel", "Unknown drive") : disk.Model;
        MediaText = DiskText.Media(disk);
        BadgeBrush = DiskText.MediaBrush(disk);
        SizeText = CleanerView.FormatSize(disk.SizeBytes);
        (HealthText, var key) = disk.Health switch
        {
            DiskHealth.Healthy => (Loc.T("Health_WinHealthy", "Windows: healthy"), "SuccessBrush"),
            DiskHealth.Warning => (Loc.T("Health_WinWarning", "Windows: warning"), "WarningBrush"),
            DiskHealth.Unhealthy => (Loc.T("Health_WinUnhealthy", "Windows: failing"), "DangerBrush"),
            _ => (Loc.T("Health_WinUnknown", "Windows: health unknown"), "TextTertiaryBrush")
        };
        HealthBrush = Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public PhysicalDisk Disk { get; }
    public string LettersText { get; }
    public string Model { get; }
    public string MediaText { get; }
    public Brush BadgeBrush { get; }
    public string SizeText { get; }
    public string HealthText { get; }
    public Brush HealthBrush { get; }

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

/// <summary>One unreadable sector in the list.</summary>
public sealed class BadRow
{
    public BadRow(BadSector sector)
    {
        Where = Loc.F("Health_BadWhere", "LBA {0:N0} · {1:0.000} GB into the disk", sector.DiskOffset / 512, sector.DiskOffset / 1e9);
        File = sector.Volume is char v
            ? sector.File switch
            {
                null => Loc.F("Health_BadUnknownFile", "{0}: (file not identified)", v),
                "(free space)" => Loc.F("Health_BadFree", "{0}: free space — no file affected", v),
                var f => $"{v}:{f}"
            }
            : Loc.T("Health_BadOutside", "outside any drive letter (partition table / recovery area)");
    }

    public string Where { get; }
    public string File { get; }
}

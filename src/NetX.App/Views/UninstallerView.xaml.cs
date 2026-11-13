using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetX.Core.System;

namespace NetX.App.Views;

public partial class UninstallerView : Page
{
    private List<InstalledProgram> _allPrograms = new();
    private bool _isBusy;
    private bool _isLoading;
    private bool _loadedOnce;
    private string? _loadError;

    // Windows apps (Microsoft Store / MSIX) — read through PowerShell, separately from the registry list
    private List<StoreAppRow> _storeRows = new();
    private StoreAppListResult? _storeResult;       // last successful read; null until there is one
    private bool _storeLoading;
    private string? _storeError;                    // quiet note when Windows apps could not be read
    private int _storeGeneration;                   // a newer read makes an older one's answer stale
    private CancellationTokenSource? _storeCts;

    public UninstallerView()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    /// <summary>Set by another page (Smart CPU's "Uninstall…") to open this list already filtered.</summary>
    public static string? PendingSearch { get; set; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded fires again whenever the page is re-attached; one initial read is enough
        if (_loadedOnce)
        {
            // Show what arrived while the page was away, and read Windows apps again if that was cut off
            SetBusy(_isBusy);
            ApplyFilters();
            if (!_storeLoading && _storeResult == null) _ = ReloadStoreAppsAsync();
            return;
        }
        _loadedOnce = true;
        if (PendingSearch is { Length: > 0 } search)
        {
            PendingSearch = null;
            SearchBox.Text = search;
        }

        // On its own: a slow or hanging PowerShell must not hold up the program list
        _ = ReloadStoreAppsAsync();
        _ = RefreshKeepAwayButtonAsync();

        SetBusy(true);
        try
        {
            await ReloadProgramsAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        // Stops the Windows-apps read and the size walk; nothing touches the page after this
        _storeCts?.Cancel();
    }

    #region Program list

    /// <summary>Re-reads the registry off the UI thread. Returns false if that failed (the list then says why).</summary>
    private async Task<bool> ReloadProgramsAsync()
    {
        _isLoading = true;
        ApplyFilters();

        try
        {
            var entries = await Task.Run(UninstallHelper.GetInstalledPrograms);
            _allPrograms = entries
                .Select(entry => new InstalledProgram(entry))
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _loadError = null;
            return true;
        }
        catch (Exception ex)
        {
            _allPrograms = new List<InstalledProgram>();
            _loadError = UninstallText.F("Uninstall_LoadFailed",
                "Couldn't read the list of installed programs.\n{0}", ex.Message);
            return false;
        }
        finally
        {
            _isLoading = false;
            ApplyFilters();
        }
    }

    // Order of the items in the "Show" box (SourceFilter)
    private enum SourceKind { Everything, Desktop, Store, Preinstalled, SafeToRemove }

    private void ApplyFilters()
    {
        // Not while the page is detached: Page_Loaded renders again when it comes back
        if (!IsInitialized || !IsLoaded) return;

        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var search = SearchBox.Text.Trim();
        var showSystem = ShowSystemApps.IsChecked == true;
        var source = (SourceKind)Math.Max(0, SourceFilter.SelectedIndex);

        var visible = new List<UninstallRow>();

        // Hidden system components only on request — the default list matches Windows' Apps list
        if (source is SourceKind.Everything or SourceKind.Desktop)
            visible.AddRange(_allPrograms.Where(p => showSystem || !p.IsSystemComponent));

        // Windows apps: only the ones that may be removed are ever in _storeRows
        if (source != SourceKind.Desktop)
        {
            visible.AddRange(_storeRows.Where(r => source switch
            {
                SourceKind.Preinstalled => r.App.ComesWithWindows,
                SourceKind.SafeToRemove => r.App.Verdict == StoreAppVerdict.SafeToRemove,
                _ => true,
            }));
        }

        var filtered = (search.Length == 0 ? visible : visible.Where(row => row.Matches(search)))
            .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // A tick on a row that is no longer shown would remove something the user can't see
        var shown = filtered.OfType<StoreAppRow>().ToHashSet();
        foreach (var row in _storeRows)
        {
            if (row.IsSelected && !shown.Contains(row)) row.IsSelected = false;
        }

        ProgramsList.ItemsSource = filtered;

        // While refreshing, the current list stays visible (no flash); an empty list shows the state text below
        CountText.Text = _isLoading
            ? (filtered.Count > 0 ? UninstallText.T("Uninstall_Loading", "Loading installed programs…") : "")
            : filtered.Count == visible.Count
                ? UninstallText.F("Uninstall_Count", "{0:N0} programs", visible.Count)
                : UninstallText.F("Uninstall_CountFiltered", "Showing {0:N0} of {1:N0} programs", filtered.Count, visible.Count);

        var storeOnly = source is not (SourceKind.Everything or SourceKind.Desktop);

        // Windows apps may fill the list while the registry could not be read — that must still be said
        if (!storeOnly && !_isLoading && _loadError != null && filtered.Count > 0)
            CountText.Text = _loadError.ReplaceLineEndings(" ");

        // An empty list of Windows apps says why: still reading, or could not be read
        string? state = filtered.Count > 0 ? null
                      : storeOnly && _storeLoading ? UninstallText.T("Uninstall_StoreLoading", "Reading Windows apps…")
                      : storeOnly && _storeError != null ? _storeError
                      : !storeOnly && _isLoading ? UninstallText.T("Uninstall_Loading", "Loading installed programs…")
                      : (storeOnly ? null : _loadError) ?? (visible.Count == 0
                          ? UninstallText.T("Uninstall_Empty", "No installed programs were found.")
                          : UninstallText.T("Uninstall_NoMatch", "No programs match your search."));

        ListStateText.Text = state ?? "";
        ListStateText.Visibility = state == null ? Visibility.Collapsed : Visibility.Visible;

        StoreNoteText.Text = StoreNote();
        UpdateSelectionUi();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void ShowSystemApps_Changed(object sender, RoutedEventArgs e)
    {
        ApplyFilters();
    }

    private void SourceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        _ = ReloadStoreAppsAsync();
        SetBusy(true);
        try
        {
            await ReloadProgramsAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        SetBusy(true);
        try
        {
            // Real re-enumeration of HKLM (64/32-bit) + HKCU; a failure is shown in the list
            if (!await ReloadProgramsAsync()) return;
            if (!IsLoaded) return;

            var listed = _allPrograms.Where(p => !p.IsSystemComponent).ToList();
            var perUser = listed.Count(p => p.IsPerUser);
            var hidden = _allPrograms.Count - listed.Count;

            var text = UninstallText.F("Uninstall_ScanResult",
                "Found {0:N0} installed programs:\n• {1:N0} installed for all users\n• {2:N0} installed for your account only\n\nHidden system components: {3:N0} — turn on \"Show System Apps\" to list them.",
                listed.Count, listed.Count - perUser, perUser, hidden);

            // Windows apps are read on their own (PowerShell); report what that read found, never wait for it here
            if (StoreNote() is { Length: > 0 } storeNote) text += "\n\n" + storeNote;
            if (_storeResult is { } store &&
                store.Protected.Any(a => a.PackageName.StartsWith("Microsoft.MicrosoftEdge", StringComparison.OrdinalIgnoreCase)))
            {
                text += "\n\n" + StoreAppCatalog.EdgeNote.Get(UninstallText.IsThai);
            }

            ShowMessage(text,
                UninstallText.T("Uninstall_ScanTitle", "Scan Complete"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        if (!IsLoaded) return;   // re-applied by Page_Loaded when the page comes back

        RefreshButton.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy;
        ProgramsList.IsEnabled = !busy;
        SourceFilter.IsEnabled = !busy;
        SelectSafeButton.IsEnabled = !busy;
        UndoKeepAwayButton.IsEnabled = !busy;
        UpdateSelectionUi();
    }

    #endregion

    #region Windows apps list

    /// <summary>
    /// Reads Microsoft Store / MSIX apps through PowerShell. Runs beside the registry list and never
    /// holds it up: a failure or a 60-second hang ends as a quiet note, and the page stays usable.
    /// </summary>
    private async Task ReloadStoreAppsAsync()
    {
        var previous = _storeCts;
        var cts = _storeCts = new CancellationTokenSource();
        previous?.Cancel();
        var generation = ++_storeGeneration;

        _storeLoading = true;
        ApplyFilters();

        var result = await StoreAppService.ListAsync(cts.Token);   // never throws
        if (generation != _storeGeneration) return;                // a newer read has taken over

        _storeLoading = false;
        if (result.Succeeded)
        {
            // Keep the ticks of apps that are still there
            var ticked = _storeRows.Where(r => r.IsSelected).Select(r => r.App.FamilyName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _storeResult = result;
            _storeError = null;
            _storeRows = result.Apps.Select(app => new StoreAppRow(app)).ToList();
            foreach (var row in _storeRows)
            {
                row.IsSelected = ticked.Contains(row.App.FamilyName);
                row.PropertyChanged += StoreRow_PropertyChanged;
            }
        }
        else if (result.Error != StoreAppListError.Cancelled)
        {
            // The page was not left: say why, quietly. What was listed before may be out of date, so it goes.
            _storeResult = null;
            _storeRows = new List<StoreAppRow>();
            _storeError = result.Error == StoreAppListError.TimedOut
                ? UninstallText.T("Uninstall_StoreTimedOut",
                    "Windows apps couldn't be read: Windows PowerShell did not answer within 60 seconds. Desktop programs are listed as usual — click Refresh to try again.")
                : UninstallText.F("Uninstall_StoreFailed",
                    "Windows apps couldn't be read ({0}). Desktop programs are listed as usual — click Refresh to try again.",
                    result.Error == StoreAppListError.BadOutput
                        ? UninstallText.T("Uninstall_StoreBadAnswer", "Windows PowerShell gave an unexpected answer")
                        : result.ErrorDetail.Length > 0
                            ? result.ErrorDetail
                            : UninstallText.T("Uninstall_StoreNoPowerShell", "Windows PowerShell could not be started"));
        }

        if (!IsLoaded) return;   // left meanwhile: Page_Loaded renders (and re-reads) when it comes back
        ApplyFilters();

        if (result.Succeeded) await MeasureStoreSizesAsync(generation, cts.Token);
    }

    /// <summary>Fills in sizes after the list is on screen — walking package folders never delays the list.</summary>
    private async Task MeasureStoreSizesAsync(int generation, CancellationToken ct)
    {
        foreach (var row in _storeRows.ToList())
        {
            long? bytes;
            try
            {
                bytes = await Task.Run(() => StoreAppService.MeasureSize(row.App, ct), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                continue;
            }

            if (generation != _storeGeneration || !IsLoaded) return;
            row.SetSize(bytes);
        }
    }

    /// <summary>The line beside the "Show" box: reading, what was found, or why it could not be read.</summary>
    private string StoreNote()
    {
        if (_storeLoading) return UninstallText.T("Uninstall_StoreLoading", "Reading Windows apps…");
        if (_storeError != null) return _storeError;
        if (_storeResult is not { } store) return "";

        var note = UninstallText.F("Uninstall_StoreSummary",
            "{0:N0} Windows apps · {1:N0} safe to remove · {2:N0} system packages are protected and not listed",
            _storeRows.Count, _storeRows.Count(r => r.App.Verdict == StoreAppVerdict.SafeToRemove), store.Protected.Count);
        if (!store.AllUsers)
            note += " · " + UninstallText.T("Uninstall_StoreOnlyThisAccount", "only your account could be read");
        return note;
    }

    private void StoreRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UninstallRow.IsSelected)) UpdateSelectionUi();
    }

    private void UpdateSelectionUi()
    {
        if (!IsLoaded) return;

        var count = _storeRows.Count(r => r.IsSelected);
        RemoveSelectedButton.Content = UninstallText.F("Uninstall_RemoveSelected", "Remove selected ({0})", count);
        RemoveSelectedButton.IsEnabled = count > 0 && !_isBusy;
    }

    /// <summary>
    /// Ticks Microsoft's own extras that are safe to remove — never a third-party app Windows
    /// promotes (the user may have installed that one on purpose). A second click unticks them.
    /// </summary>
    private void SelectSafe_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        // Show exactly what gets ticked
        SourceFilter.SelectedIndex = (int)SourceKind.SafeToRemove;
        SearchBox.Text = "";

        var safe = _storeRows.Where(r => r.App.Verdict == StoreAppVerdict.SafeToRemove && r.App.Catalog?.Promoted != true).ToList();
        var tick = safe.Any(r => !r.IsSelected);
        foreach (var row in safe) row.IsSelected = tick;
    }

    private void StoreRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StoreAppRow row)
            _ = RemoveStoreAppsAsync([row]);
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        _ = RemoveStoreAppsAsync(_storeRows.Where(r => r.IsSelected).ToList());
    }

    #endregion

    #region Windows apps removal

    /// <summary>
    /// One confirmation for the whole batch, then each app is removed for every account together
    /// with its provisioned copy. What is reported is what Windows says afterwards.
    /// </summary>
    private async Task RemoveStoreAppsAsync(List<StoreAppRow> rows)
    {
        // _isBusy is set before the first await, so a double-click finds the page busy
        if (_isBusy || rows.Count == 0) return;

        SetBusy(true);
        var started = false;
        try
        {
            var thai = UninstallText.IsThai;
            var includeCopilot = rows.Any(r => r.App.Catalog?.IsCopilot == true);

            // Read-only look at the registry: is there anything "keep it from coming back" would change?
            var plan = await Task.Run(() => StoreAppKeepAway.Apply(includeCopilot, dryRun: true));
            if (!IsLoaded) return;

            var confirm = new StoreRemoveConfirmModel(rows, thai, includeCopilot,
                keepAwayAvailable: plan.Changes.Any(c => c.Status == KeepAwayValueStatus.Planned));
            if (!ShowStoreConfirm(confirm)) return;

            started = true;
            var busy = new BusyDialogModel
            {
                Title = UninstallText.T("Uninstall_StoreWorkTitle", "Removing Windows apps"),
                Message = UninstallText.F("Uninstall_StoreWorkMsg", "App {0} of {1}:\n{2}", 1, rows.Count, rows[0].Name),
                CancelText = UninstallText.T("Uninstall_StoreStop", "Stop after this app"),
                CanCancel = true,
                ShowProgress = true,
            };
            busy.CancelRequested += () => busy.Message =
                UninstallText.T("Uninstall_StoreStopping", "Stopping after the app that is being removed…");
            var progress = new Progress<int>(index =>
            {
                if (!busy.CanCancel) return;   // "stopping…" stays on screen
                busy.Message = UninstallText.F("Uninstall_StoreWorkMsg", "App {0} of {1}:\n{2}", index + 1, rows.Count, rows[index].Name);
                busy.Progress = index * 100.0 / rows.Count;
            });

            var apps = rows.Select(r => r.App).ToList();
            var results = await RunWithBusyDialogAsync(busy, ct => Task.Run(() => RemoveBatchAsync(apps, progress, ct)));

            // Only when the user ticked it, and only if the batch was not stopped before it began
            KeepAwayResult? keepAway = null;
            if (confirm.KeepAway && results.Any(r => r.Status != StoreAppRemovalStatus.Cancelled))
                keepAway = await Task.Run(() => StoreAppKeepAway.Apply(includeCopilot, dryRun: false));

            // Rows Windows no longer reports leave the list at once; the full re-read follows below
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in results)
            {
                if (result.Status is StoreAppRemovalStatus.Removed or StoreAppRemovalStatus.AlreadyGone)
                    gone.Add(result.PackageName + "_" + result.PublisherId);
            }
            _storeRows.RemoveAll(r => gone.Contains(r.App.FamilyName));

            if (!IsLoaded) return;
            ApplyFilters();

            var allGood = results.All(r => r.Status is (StoreAppRemovalStatus.Removed or StoreAppRemovalStatus.AlreadyGone) && !r.AppDataLeft) &&
                          (keepAway == null || keepAway.Success);
            ShowMessage(BuildStoreSummary(rows, results, keepAway),
                UninstallText.T("Uninstall_StoreDoneTitle", "Removal Finished"),
                MessageBoxButton.OK, allGood ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            if (IsLoaded) ShowError(ex);
        }
        finally
        {
            SetBusy(false);
            if (started && IsLoaded)
            {
                _ = ReloadStoreAppsAsync();
                _ = RefreshKeepAwayButtonAsync();
            }
            else if (started)
            {
                _storeResult = null;   // left meanwhile: Page_Loaded reads again when the page comes back
            }
        }
    }

    /// <summary>
    /// One app after the other. Stopping takes effect between apps — a removal that has started is
    /// left to finish — and the apps not reached come back as Cancelled.
    /// </summary>
    private static async Task<List<StoreAppRemovalResult>> RemoveBatchAsync(
        List<StoreApp> apps, IProgress<int> progress, CancellationToken ct)
    {
        var results = new List<StoreAppRemovalResult>();
        for (var i = 0; i < apps.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                results.Add(new StoreAppRemovalResult
                {
                    PackageName = apps[i].PackageName,
                    PublisherId = apps[i].PublisherId,
                    Status = StoreAppRemovalStatus.Cancelled,
                });
                continue;
            }

            progress.Report(i);
            results.Add(await StoreAppService.RemoveAsync(apps[i].PackageName, apps[i].PublisherId, dryRun: false, ct));
        }
        return results;
    }

    private static string BuildStoreSummary(List<StoreAppRow> rows, List<StoreAppRemovalResult> results, KeepAwayResult? keepAway)
    {
        string NameOf(StoreAppRemovalResult result) =>
            rows.FirstOrDefault(r => r.App.PackageName.Equals(result.PackageName, StringComparison.OrdinalIgnoreCase) &&
                                     r.App.PublisherId.Equals(result.PublisherId, StringComparison.OrdinalIgnoreCase))?.Name
            ?? result.PackageName;

        var removed = results.Where(r => r.Status == StoreAppRemovalStatus.Removed).ToList();
        var alreadyGone = results.Where(r => r.Status == StoreAppRemovalStatus.AlreadyGone).ToList();
        var stopped = results.Count(r => r.Status == StoreAppRemovalStatus.Cancelled);
        var failed = results.Where(r => r.Status is not (StoreAppRemovalStatus.Removed or StoreAppRemovalStatus.AlreadyGone
            or StoreAppRemovalStatus.Cancelled)).ToList();

        var text = new StringBuilder();
        text.AppendLine(UninstallText.F("Uninstall_StoreResRemoved", "Removed completely: {0:N0}", removed.Count));
        foreach (var result in removed)
        {
            // Only possible when WinXTools is not elevated or DISM failed: say what was not looked at
            var notes = new List<string>();
            if (!result.AllUsersChecked) notes.Add(UninstallText.T("Uninstall_StoreNoteAccounts", "other accounts could not be checked"));
            if (!result.ProvisionedChecked) notes.Add(UninstallText.T("Uninstall_StoreNoteProvisioned", "the copy for new accounts could not be checked"));
            text.AppendLine($"  • {NameOf(result)}{(notes.Count > 0 ? " — " + string.Join("; ", notes) : "")}");
        }

        if (alreadyGone.Count > 0)
        {
            text.AppendLine(UninstallText.F("Uninstall_StoreResGone", "Already removed before WinXTools got to it: {0:N0}", alreadyGone.Count));
            foreach (var result in alreadyGone) text.AppendLine($"  • {NameOf(result)}");
        }

        if (failed.Count > 0)
        {
            text.AppendLine(UninstallText.F("Uninstall_StoreResNotRemoved", "Not removed: {0:N0}", failed.Count));
            foreach (var result in failed) text.AppendLine($"  • {NameOf(result)} — {StoreFailureText(result)}");
        }

        if (stopped > 0)
            text.AppendLine(UninstallText.F("Uninstall_StoreResStopped", "Not processed because you stopped: {0:N0}", stopped));

        var dataLeft = results.Where(r => r.AppDataLeft).ToList();
        if (dataLeft.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(UninstallText.T("Uninstall_StoreDataLeft", "App data still on disk (Windows did not delete it):"));
            foreach (var result in dataLeft) text.AppendLine($"  • {result.AppDataPath}");
        }

        if (keepAway != null)
        {
            text.AppendLine();
            var changed = keepAway.Changes.Count(c => c.Status == KeepAwayValueStatus.Applied);
            if (!keepAway.Success)
            {
                var notChanged = keepAway.Changes.Where(c => c.Status == KeepAwayValueStatus.Failed)
                    .Select(c => $@"  • {c.Location}\{c.ValueName}");
                text.AppendLine(UninstallText.F("Uninstall_KeepAwayFailed",
                    "Some of the settings that keep apps from coming back could not be changed:\n{0}",
                    string.Join("\n", notChanged) + (keepAway.Error.Length > 0 ? "\n" + keepAway.Error : "")));
            }
            else if (changed > 0)
            {
                text.AppendLine(UninstallText.F("Uninstall_KeepAwayApplied",
                    "Windows is now set not to bring apps back ({0:N0} settings changed). To undo, click \"Allow Windows suggestions again\" on this page.",
                    changed));
            }
            else
            {
                text.AppendLine(UninstallText.T("Uninstall_KeepAwayAlready", "Windows was already set not to bring apps back."));
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string StoreFailureText(StoreAppRemovalResult result) => result.Status switch
    {
        StoreAppRemovalStatus.WindowsRefused => UninstallText.F("Uninstall_StoreWhyWindows",
            "Windows does not allow removing this one ({0})", result.ErrorCode),
        StoreAppRemovalStatus.InUse => UninstallText.F("Uninstall_StoreWhyInUse",
            "it is running or being updated — close it and try again ({0})", result.ErrorCode),
        StoreAppRemovalStatus.PartlyRemoved => result.StagedOnly
            ? UninstallText.T("Uninstall_StoreWhyStaged", "no account has it any more, but Windows still keeps its files")
            : result.StillInstalled.Count > 0
                ? UninstallText.T("Uninstall_StoreWhyOtherAccount", "removed for your account, but another account still has it")
                : UninstallText.T("Uninstall_StoreWhyProvisioned", "removed, but the copy Windows keeps for new accounts is still there"),
        StoreAppRemovalStatus.StillInstalled => result.ErrorCode.Length > 0
            ? UninstallText.F("Uninstall_StoreWhyStill", "still installed (Windows error {0})", result.ErrorCode)
            : UninstallText.T("Uninstall_StoreWhyStillNoCode", "still installed"),
        StoreAppRemovalStatus.Refused => UninstallText.T("Uninstall_StoreWhyProtected",
            "WinXTools does not remove this one — Windows needs it"),
        StoreAppRemovalStatus.NotStarted => UninstallText.T("Uninstall_StoreWhyNoAnswer",
            "Windows did not answer, so nothing was removed — try again"),
        _ => UninstallText.T("Uninstall_StoreWhyUnknown",
            "the result could not be checked — click Refresh to see whether it is still installed"),
    };

    /// <summary>Shows the undo button only while WinXTools has recorded settings to put back.</summary>
    private async Task RefreshKeepAwayButtonAsync()
    {
        var hasUndo = await Task.Run(() => StoreAppKeepAway.HasUndo);
        if (!IsLoaded) return;
        UndoKeepAwayButton.Visibility = hasUndo ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UndoKeepAway_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        if (ShowMessage(
                UninstallText.T("Uninstall_KeepAwayUndoConfirm",
                    "Put the Windows settings back as they were before WinXTools changed them?\n\nWindows may suggest and install apps by itself again. This does not reinstall the apps you removed."),
                UninstallText.T("Uninstall_KeepAwayUndoTitle", "Allow Windows Suggestions"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        try
        {
            var result = await Task.Run(() => StoreAppKeepAway.Undo(dryRun: false));
            if (!IsLoaded) return;

            var restored = result.Changes.Count(c => c.Status == KeepAwayValueStatus.Restored);
            if (result.Success)
            {
                ShowMessage(
                    UninstallText.F("Uninstall_KeepAwayUndone", "The Windows settings are back as they were ({0:N0} values restored).", restored),
                    UninstallText.T("Uninstall_KeepAwayUndoTitle", "Allow Windows Suggestions"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var notRestored = result.Changes.Where(c => c.Status == KeepAwayValueStatus.Failed)
                    .Select(c => $@"  • {c.Location}\{c.ValueName}");
                ShowMessage(
                    UninstallText.F("Uninstall_KeepAwayUndoFailed", "Some settings could not be put back:\n{0}",
                        string.Join("\n", notRestored) + (result.Error.Length > 0 ? "\n" + result.Error : "")),
                    UninstallText.T("Uninstall_KeepAwayUndoTitle", "Allow Windows Suggestions"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            if (IsLoaded) ShowError(ex);
        }
        finally
        {
            SetBusy(false);
            _ = RefreshKeepAwayButtonAsync();
        }
    }

    #endregion

    #region Uninstall

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || (sender as FrameworkElement)?.DataContext is not InstalledProgram program) return;
        var entry = program.Entry;

        if (entry.NoRemove)
        {
            ShowMessage(
                UninstallText.F("Uninstall_NoRemove",
                    "The publisher of {0} has turned off uninstalling it from the apps list, so WinXTools won't remove it.",
                    entry.DisplayName),
                UninstallText.T("Uninstall_CannotTitle", "Can't Uninstall"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        var uninstallerRan = false;
        try
        {
            var command = await PrepareCommandAsync(entry);
            if (command == null) return;

            var confirmed = entry.IsSystemComponent
                ? ShowMessage(
                    UninstallText.F("Uninstall_ConfirmSystem",
                        "{0} is a hidden system component. Other programs may need it, and removing it can break them.\n\nUninstall it anyway?",
                        entry.DisplayName),
                    UninstallText.T("Uninstall_ConfirmTitle", "Confirm Uninstall"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : ShowMessage(
                    UninstallText.F("Uninstall_Confirm",
                        "Uninstall {0}?\n\nThe program's own uninstaller will open — follow its steps to finish. WinXTools waits for it, then checks that the program is really gone.",
                        entry.DisplayName),
                    UninstallText.T("Uninstall_ConfirmTitle", "Confirm Uninstall"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirmed != MessageBoxResult.Yes) return;

            var run = await RunUninstallerAsync(entry, command);
            if (run == null) return;
            uninstallerRan = true;

            if (run.StillRegistered)
            {
                ShowMessage(StillInstalledText(entry, run, deepClean: false),
                    UninstallText.T("Uninstall_NotRemovedTitle", "Still Installed"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                ShowMessage(UninstallText.F("Uninstall_Done", "{0} was uninstalled.", entry.DisplayName),
                    UninstallText.T("Uninstall_DoneTitle", "Uninstall Complete"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            if (uninstallerRan) await ReloadProgramsAsync();
            SetBusy(false);
        }
    }

    private async void DeepClean_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || (sender as FrameworkElement)?.DataContext is not InstalledProgram program) return;
        var entry = program.Entry;
        var name = entry.DisplayName;

        // Safety: Don't allow deep clean of system components
        if (entry.IsSystemComponent)
        {
            ShowMessage(
                UninstallText.F("Uninstall_DeepSystemBlocked",
                    "Deep Clean isn't available for {0}.\n\nIt is marked as a Windows system component — removing its files could make Windows or other programs unstable.",
                    name),
                UninstallText.T("Uninstall_ProtectedTitle", "Protected Program"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (entry.NoRemove)
        {
            ShowMessage(
                UninstallText.F("Uninstall_NoRemove",
                    "The publisher of {0} has turned off uninstalling it from the apps list, so WinXTools won't remove it.",
                    name),
                UninstallText.T("Uninstall_CannotTitle", "Can't Uninstall"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        var uninstallerRan = false;
        try
        {
            var command = await PrepareCommandAsync(entry);
            if (command == null) return;

            // Safety: extra warning for system/driver vendors
            if (IsProtectedPublisher(entry.Publisher) &&
                ShowMessage(
                    UninstallText.F("Uninstall_PublisherWarning",
                        "{0} is published by {1}, which usually makes system or driver software.\n\nDeep cleaning it could cause problems with your hardware or Windows. Are you sure?",
                        name, entry.Publisher),
                    UninstallText.T("Uninstall_PublisherWarningTitle", "Protected Publisher Warning"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            if (ShowMessage(
                    UninstallText.F("Uninstall_DeepConfirm",
                        "Deep Clean {0}?\n\n" +
                        "1. Run the program's own uninstaller and wait for it to finish.\n" +
                        "2. Check that the program is really gone. If it is still installed, stop without touching any files.\n" +
                        "3. Look for leftover folders that match this program exactly: its install folder, and folders named after it in Program Files, ProgramData and AppData.\n" +
                        "4. Show you everything found, with sizes — you choose what to remove. Chosen folders go to the Recycle Bin, so you can restore them.",
                        name),
                    UninstallText.T("Uninstall_DeepTitle", "Deep Clean"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            // 1-2. Real uninstall first; files are only considered once the program is really gone
            var run = await RunUninstallerAsync(entry, command);
            if (run == null) return;
            uninstallerRan = true;

            if (run.StillRegistered)
            {
                ShowMessage(StillInstalledText(entry, run, deepClean: true),
                    UninstallText.T("Uninstall_NotRemovedTitle", "Still Installed"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 3. Exact-match leftover search, off the UI thread
            List<LeftoverFolder> leftovers;
            try
            {
                var scan = new BusyDialogModel
                {
                    Title = UninstallText.F("Uninstall_ScanLeftoversTitle", "Looking for leftovers of {0}", name),
                    Message = UninstallText.T("Uninstall_ScanLeftoversMsg",
                        "Checking its install folder, Program Files, ProgramData and AppData…"),
                    CancelText = UninstallText.T("Common_Cancel", "Cancel"),
                    CanCancel = true,
                };
                leftovers = await RunWithBusyDialogAsync(scan,
                    ct => Task.Run(() => UninstallHelper.FindLeftovers(entry, ct), ct));
            }
            catch (OperationCanceledException)
            {
                ShowMessage(
                    UninstallText.F("Uninstall_ScanCancelled",
                        "{0} was uninstalled. The leftover search was cancelled — nothing was removed.", name),
                    UninstallText.T("Uninstall_DeepTitle", "Deep Clean"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (leftovers.Count == 0)
            {
                ShowMessage(
                    UninstallText.F("Uninstall_NoLeftovers",
                        "{0} was uninstalled and no leftover folders were found.", name),
                    UninstallText.T("Uninstall_DeepDoneTitle", "Deep Clean Finished"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 4. The user decides — everything found is listed, nothing is pre-selected
            var picker = new LeftoverPickerModel(
                UninstallText.F("Uninstall_LeftoversTitle", "Leftovers of {0}", name),
                UninstallText.F("Uninstall_LeftoversMsg",
                    "{0} was uninstalled, but these folders were left behind. Tick the ones you want to remove — they are moved to the Recycle Bin, so you can restore them if needed. Check the size first: a large folder may contain your own data.",
                    name),
                leftovers.Select(folder => new LeftoverItem(folder, KindText(folder.Kind))));
            if (!ShowLeftoverPicker(picker)) return;

            var chosen = picker.Items.Where(i => i.IsSelected).Select(i => i.Folder).ToList();

            // 5. Recycle Bin only — one folder at a time, each re-checked right before it moves
            var move = new BusyDialogModel
            {
                Title = UninstallText.T("Uninstall_MovingTitle", "Moving leftovers to the Recycle Bin"),
                Message = UninstallText.F("Uninstall_MovingMsg", "Folder {0} of {1}:\n{2}", 1, chosen.Count, chosen[0].Path),
                CancelText = UninstallText.T("Common_Cancel", "Cancel"),
                CanCancel = true,
                ShowProgress = true,
            };
            var progress = new Progress<(int Index, int Total, string Path)>(p =>
            {
                move.Message = UninstallText.F("Uninstall_MovingMsg", "Folder {0} of {1}:\n{2}", p.Index + 1, p.Total, p.Path);
                move.Progress = p.Index * 100.0 / p.Total;
            });
            var results = await RunWithBusyDialogAsync(move,
                ct => UninstallHelper.MoveToRecycleBinAsync(entry, chosen, progress, ct));

            var allGood = results.Count == chosen.Count &&
                          results.All(r => r.Status is RecycleStatus.Moved or RecycleStatus.AlreadyGone);
            ShowMessage(BuildCleanSummary(chosen.Count, results),
                UninstallText.T("Uninstall_DeepDoneTitle", "Deep Clean Finished"),
                MessageBoxButton.OK, allGood ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            if (uninstallerRan) await ReloadProgramsAsync();
            SetBusy(false);
        }
    }

    /// <summary>What happened after running a program's own uninstaller.</summary>
    private sealed record UninstallRun(bool StillRegistered, int? ExitCode, bool IsMsi);

    /// <summary>Resolves the uninstall command off the UI thread; tells the user when there is none.</summary>
    private async Task<UninstallCommand?> PrepareCommandAsync(UninstallEntry entry)
    {
        var (command, problem, missingPath) = await Task.Run(() =>
        {
            var found = UninstallHelper.GetUninstallCommand(entry, out var why, out var path);
            return (found, why, path);
        });
        if (command != null) return command;

        var text = problem == UninstallCommandProblem.UninstallerMissing
            ? UninstallText.F("Uninstall_UninstallerMissing",
                "The uninstaller for {0} is missing:\n{1}\n\nThe program may already be partly removed. Reinstall it and then uninstall, or use Windows Settings › Apps.",
                entry.DisplayName, missingPath)
            : UninstallText.F("Uninstall_NoCommand",
                "{0} doesn't register an uninstaller with Windows, so it can't be removed from here.\n\nTry the program's own setup file, or Windows Settings › Apps.",
                entry.DisplayName);
        ShowMessage(text, UninstallText.T("Uninstall_CannotTitle", "Can't Uninstall"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    /// <summary>
    /// Runs the program's own uninstaller (vendor UI), waits for it and everything it started
    /// without blocking the UI, then re-checks the registry. Null if it could not be started
    /// (the user has already been told why).
    /// </summary>
    private async Task<UninstallRun?> RunUninstallerAsync(UninstallEntry entry, UninstallCommand command)
    {
        UninstallerProcess process;
        try
        {
            // Per-user (HKCU) entries run with the user's normal rights, never with ours
            process = await Task.Run(() => UninstallHelper.Launch(command, asDesktopUser: entry.IsPerUser));
        }
        catch (UninstallLaunchException ex)
        {
            ShowMessage(LaunchErrorText(entry, ex),
                UninstallText.T("Uninstall_CannotTitle", "Can't Uninstall"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        using (process)
        {
            var runningText = UninstallText.T("Uninstall_WaitMsg",
                "The uninstaller is open — follow its steps. WinXTools continues automatically when it finishes.");
            var busy = new BusyDialogModel
            {
                Title = UninstallText.F("Uninstall_WaitTitle", "Uninstalling {0}", entry.DisplayName),
                Message = runningText,
                CancelText = UninstallText.T("Uninstall_StopWaiting", "Stop waiting"),
                CanCancel = true,
            };
            var stage = new Progress<UninstallWaitStage>(s => busy.Message =
                s == UninstallWaitStage.ClosedButStillRegistered
                    ? UninstallText.F("Uninstall_WaitClosed",
                        "The uninstaller has closed, but {0} is still installed.\n\nIf it is still finishing in the background, WinXTools continues automatically. If you cancelled it, click \"Stop waiting\".",
                        entry.DisplayName)
                    : runningText);

            // "Stop waiting" only stops watching — the uninstaller itself is never killed
            await RunWithBusyDialogAsync(busy,
                ct => UninstallHelper.WaitForUninstallerAsync(process, entry, command.IsMsi, stage, ct));

            var stillRegistered = await Task.Run(() => UninstallHelper.IsStillRegistered(entry));
            return new UninstallRun(stillRegistered, process.ExitCode, command.IsMsi);
        }
    }

    private static string StillInstalledText(UninstallEntry entry, UninstallRun run, bool deepClean)
    {
        var text = deepClean
            ? UninstallText.F("Uninstall_DeepStopped",
                "{0} is still installed, so Deep Clean stopped without touching any files.\n\nThe uninstaller may have been cancelled, failed, or needs a restart to finish.",
                entry.DisplayName)
            : UninstallText.F("Uninstall_StillInstalled",
                "{0} is still installed.\n\nThe uninstaller may have been cancelled, failed, or needs a restart to finish. If it asked for a restart, restart Windows and click Refresh to check again.",
                entry.DisplayName);

        // msiexec 1618: another installation is already in progress
        if (run.IsMsi && run.ExitCode == 1618)
        {
            text += "\n\n" + UninstallText.T("Uninstall_MsiBusy",
                "Windows Installer is busy with another installation. Wait for it to finish, then try again.");
        }

        return text;
    }

    private static string LaunchErrorText(UninstallEntry entry, UninstallLaunchException ex) => ex.Error switch
    {
        UninstallLaunchError.ElevationRequired => UninstallText.F("Uninstall_PerUserNeedsAdmin",
            "{0} is installed for your account only, but its uninstaller asks for administrator rights.\n\nFor your safety, WinXTools runs per-user uninstallers with normal user rights only. Please uninstall it from Windows Settings › Apps.",
            entry.DisplayName),
        UninstallLaunchError.DesktopUserUnavailable => UninstallText.F("Uninstall_NoDesktopUser",
            "Couldn't start the uninstaller for {0} with your normal user rights (Windows Explorer or the Secondary Logon service isn't available).\n\nPlease uninstall it from Windows Settings › Apps.",
            entry.DisplayName),
        _ => UninstallText.F("Uninstall_LaunchFailed",
            "Couldn't start the uninstaller for {0}.\n\n{1}",
            entry.DisplayName, new Win32Exception(ex.Win32Code).Message),
    };

    private static string BuildCleanSummary(int chosenCount, List<RecycleResult> results)
    {
        var moved = results.Where(r => r.Status == RecycleStatus.Moved).ToList();
        var alreadyGone = results.Count(r => r.Status == RecycleStatus.AlreadyGone);
        var failed = results.Where(r => r.Status is not (RecycleStatus.Moved or RecycleStatus.AlreadyGone)).ToList();
        var notReached = chosenCount - results.Count;

        var text = new StringBuilder();
        text.AppendLine(UninstallText.F("Uninstall_ResultMoved", "Moved to the Recycle Bin: {0} folder(s) ({1})",
            moved.Count, UninstallText.FormatBytes(moved.Sum(r => r.Folder.Bytes))));

        if (alreadyGone > 0)
            text.AppendLine(UninstallText.F("Uninstall_ResultGone", "Already removed by the uninstaller: {0} folder(s)", alreadyGone));

        if (failed.Count > 0)
        {
            text.AppendLine(UninstallText.F("Uninstall_ResultFailed", "Not moved: {0} folder(s)", failed.Count));
            foreach (var result in failed)
                text.AppendLine($"  • {result.Folder.Path} — {FailureText(result)}");
        }

        if (notReached > 0)
            text.AppendLine(UninstallText.F("Uninstall_ResultStopped", "Not processed because you stopped: {0} folder(s)", notReached));

        if (moved.Count > 0)
        {
            text.AppendLine();
            text.Append(UninstallText.T("Uninstall_ResultRestoreHint", "Moved folders can be restored from the Recycle Bin."));
        }

        return text.ToString().TrimEnd();
    }

    private static string FailureText(RecycleResult result) => result.Status switch
    {
        RecycleStatus.InUseOrDenied => UninstallText.T("Uninstall_FailInUse", "some files are in use or access was denied"),
        RecycleStatus.Cancelled => UninstallText.T("Uninstall_FailCancelled", "cancelled in the Windows prompt — the folder was kept"),
        RecycleStatus.Changed => UninstallText.T("Uninstall_FailChanged", "skipped for safety — the folder changed after the scan"),
        _ => UninstallText.F("Uninstall_FailCode", "Windows error 0x{0:X}", result.ErrorCode),
    };

    private static string KindText(LeftoverKind kind) => kind switch
    {
        LeftoverKind.InstallFolder => UninstallText.T("Uninstall_KindInstall", "Install folder"),
        LeftoverKind.ProgramFiles => "Program Files",
        LeftoverKind.ProgramData => "ProgramData",
        LeftoverKind.RoamingAppData => @"AppData\Roaming",
        LeftoverKind.LocalAppData => @"AppData\Local",
        _ => "",
    };

    // Safety: Check if publisher is a protected system publisher
    private static bool IsProtectedPublisher(string publisher)
    {
        var protectedPublishers = new[] {
            "microsoft", "windows", "intel", "nvidia", "amd", "realtek",
            "synaptics", "dell", "hp", "lenovo", "asus", "acer"
        };

        var lowerPublisher = publisher.ToLowerInvariant();
        return protectedPublishers.Any(p => lowerPublisher.Contains(p));
    }

    #endregion

    #region Dialogs

    private MessageBoxResult ShowMessage(string text, string title, MessageBoxButton buttons, MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var owner = Window.GetWindow(this);
        return owner != null
            ? MessageBox.Show(owner, text, title, buttons, icon, defaultResult)
            : MessageBox.Show(text, title, buttons, icon, defaultResult);
    }

    private void ShowError(Exception ex)
    {
        ShowMessage(
            UninstallText.F("Uninstall_Error", "The operation stopped because of an unexpected error:\n{0}", ex.Message),
            UninstallText.T("Common_Error", "Error"),
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// Runs <paramref name="work"/> while a modal "please wait" dialog is shown. The dialog keeps
    /// the rest of the app (navigation included) out of reach until the work ends, but the UI keeps
    /// painting. Cancel / Esc / Alt+F4 signal the token; the dialog closes when the work ends.
    /// </summary>
    private async Task<TResult> RunWithBusyDialogAsync<TResult>(BusyDialogModel model,
        Func<CancellationToken, Task<TResult>> work)
    {
        using var cts = new CancellationTokenSource();
        Action cancel = cts.Cancel;
        model.CancelRequested += cancel;
        try
        {
            var task = work(cts.Token);

            // Only create the window when it will really be shown: every constructed Window joins
            // Application.Windows, and one that is never closed would keep the app from exiting.
            if (!task.IsCompleted)
            {
                var dialog = CreateDialogWindow(model, "UninstallBusyTemplate", 480);
                dialog.Closing += (_, e) =>
                {
                    if (model.WorkFinished) return;
                    e.Cancel = true;
                    model.RequestCancel();
                };

                _ = task.ContinueWith(_ => dialog.Dispatcher.InvokeAsync(() =>
                {
                    model.WorkFinished = true;
                    dialog.Close();
                }), TaskScheduler.Default);

                dialog.ShowDialog();
            }

            return await task;
        }
        finally
        {
            model.CancelRequested -= cancel;
        }
    }

    /// <summary>True when the user picked at least one folder and chose to move it.</summary>
    private bool ShowLeftoverPicker(LeftoverPickerModel model)
    {
        var dialog = CreateDialogWindow(model, "UninstallLeftoverTemplate", 700);
        return dialog.ShowDialog() == true && model.HasSelection;
    }

    private Window CreateDialogWindow(object model, string templateKey, double width)
    {
        var owner = Window.GetWindow(this);
        var dialog = new Window
        {
            Title = "WinXTools",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Height,
            Width = width,
            DataContext = model,
            Content = model,
            ContentTemplate = (DataTemplate)FindResource(templateKey),
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (owner != null) dialog.Owner = owner;
        return dialog;
    }

    // Buttons of the dialog templates (Page.Resources) — the hosting window is found from the sender
    private void BusyCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BusyDialogModel model) model.RequestCancel();
    }

    private void LeftoverRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject element && Window.GetWindow(element) is { } dialog &&
            dialog.DataContext is LeftoverPickerModel { HasSelection: true })
        {
            dialog.DialogResult = true;
        }
    }

    private void LeftoverKeep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject element && Window.GetWindow(element) is { } dialog)
            dialog.DialogResult = false;
    }

    /// <summary>True only when the user clicked the red Remove button (Enter and Esc both cancel).</summary>
    private bool ShowStoreConfirm(StoreRemoveConfirmModel model)
    {
        var dialog = CreateDialogWindow(model, "UninstallStoreConfirmTemplate", 640);
        return dialog.ShowDialog() == true;
    }

    private void StoreConfirmRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject element && Window.GetWindow(element) is { } dialog &&
            dialog.DataContext is StoreRemoveConfirmModel)
        {
            dialog.DialogResult = true;
        }
    }

    private void StoreConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject element && Window.GetWindow(element) is { } dialog)
            dialog.DialogResult = false;
    }

    #endregion
}

/// <summary>What every row of the list shows — a desktop program or a Windows app.</summary>
public abstract class UninstallRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _size = "—";

    public abstract string Name { get; }
    public abstract string Publisher { get; }
    /// <summary>Publisher · version · per-user / system component.</summary>
    public string Details { get; protected set; } = "";
    public string InstallDate { get; protected set; } = "—";

    public string Size
    {
        get => _size;
        protected set
        {
            if (_size == value) return;
            _size = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Size)));
        }
    }

    /// <summary>A Microsoft Store / MSIX app: tick box, badges and one Remove button instead of Uninstall / Deep Clean.</summary>
    public virtual bool IsStoreApp => false;
    /// <summary>"Windows app" or "Comes with Windows".</summary>
    public string Badge { get; protected set; } = "";
    /// <summary>"Safe to remove" / "Your call" / "Hardware app", or empty for an ordinary app.</summary>
    public string VerdictText { get; protected set; } = "";
    public bool HasVerdict => VerdictText.Length > 0;
    /// <summary>The verdict as the XAML triggers know it (StoreAppVerdict name).</summary>
    public string VerdictKind { get; protected set; } = "";
    /// <summary>One sentence about the app.</summary>
    public string Description { get; protected set; } = "";
    /// <summary>Tooltip: the sentence plus what the user loses; null when there is nothing to say.</summary>
    public string? Hint { get; protected set; }

    /// <summary>Ticked for removing several Windows apps at once.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public virtual bool Matches(string search) =>
        Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        Publisher.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A Windows app (Microsoft Store / MSIX) in the list. Only apps that may be removed get a row.</summary>
public sealed class StoreAppRow : UninstallRow
{
    public StoreAppRow(StoreApp app)
    {
        App = app;
        var thai = UninstallText.IsThai;

        var details = new List<string>();
        if (app.CatalogName.Length > 0) details.Add(app.CatalogName);
        if (app.Publisher.Length > 0) details.Add(app.Publisher);
        if (app.Version.Length > 0) details.Add(app.Version);
        if (!app.InstalledForCurrentUser) details.Add(UninstallText.T("Uninstall_StoreNotMine", "Not installed for your account"));
        details.Add(app.PackageName);
        Details = string.Join("  ·  ", details);

        Badge = app.ComesWithWindows
            ? UninstallText.T("Uninstall_BadgePreinstalled", "Comes with Windows")
            : UninstallText.T("Uninstall_BadgeStore", "Windows app");
        VerdictText = VerdictLabel(app.Verdict);
        VerdictKind = app.Verdict.ToString();

        Description = app.Description?.Get(thai) ?? "";
        var warning = app.Warning?.Get(thai) ?? "";
        var hint = string.Join("\n\n", new[] { Description, warning }.Where(t => t.Length > 0));
        Hint = hint.Length > 0 ? hint : null;

        if (app.InstalledOn is { } date)
            InstallDate = date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    public StoreApp App { get; }
    public override string Name => App.DisplayName;
    public override string Publisher => App.Publisher;
    public override bool IsStoreApp => true;

    /// <summary>The size arrives after the list is shown; unreadable stays "—".</summary>
    public void SetSize(long? bytes) => Size = bytes is > 0 ? UninstallText.FormatBytes(bytes.Value) : "—";

    public override bool Matches(string search) =>
        base.Matches(search) ||
        App.PackageName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        App.CatalogName.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    public static string VerdictLabel(StoreAppVerdict verdict) => verdict switch
    {
        StoreAppVerdict.SafeToRemove => UninstallText.T("Uninstall_VerdictSafe", "Safe to remove"),
        StoreAppVerdict.YourCall => UninstallText.T("Uninstall_VerdictYourCall", "Your call"),
        StoreAppVerdict.Hardware => UninstallText.T("Uninstall_VerdictHardware", "Hardware app"),
        _ => "",
    };
}

/// <summary>State of the Windows-app removal confirmation: the names, what to know about each, and the optional tick.</summary>
public sealed class StoreRemoveConfirmModel
{
    public StoreRemoveConfirmModel(IReadOnlyList<StoreAppRow> rows, bool thai, bool includeCopilot, bool keepAwayAvailable)
    {
        Title = rows.Count == 1
            ? UninstallText.F("Uninstall_StoreConfirmTitleOne", "Remove {0}?", rows[0].Name)
            : UninstallText.F("Uninstall_StoreConfirmTitleMany", "Remove {0:N0} Windows apps?", rows.Count);
        Message = UninstallText.T("Uninstall_StoreConfirmMsg",
            "Everything listed below is removed for every account on this PC, together with the copy Windows keeps for new accounts, so it does not come back for a new user or after a feature update. Windows deletes each app's own data with it.");
        RemoveText = rows.Count == 1
            ? UninstallText.T("Uninstall_StoreRemove", "Remove")
            : UninstallText.F("Uninstall_StoreConfirmRemoveMany", "Remove all {0:N0}", rows.Count);

        Items = rows.Select(row => new StoreRemoveItem(row, thai)).ToList();

        KeepAwayAvailable = keepAwayAvailable;
        KeepAwayDetails = UninstallText.T("Uninstall_KeepAwayDetails",
            "Turns off Windows' consumer features, silently installed apps and app suggestions. The current settings are saved first; \"Allow Windows suggestions again\" on this page puts them back.");
        if (includeCopilot)
            KeepAwayDetails += " " + UninstallText.T("Uninstall_KeepAwayDetailsCopilot", "Windows Copilot is turned off as well.");
        KeepAwayValues = string.Join("\n", StoreAppKeepAway.Describe(includeCopilot));
    }

    public string Title { get; }
    public string Message { get; }
    public string RemoveText { get; }
    public List<StoreRemoveItem> Items { get; }

    /// <summary>False when Windows is already set that way — the tick box is then not shown.</summary>
    public bool KeepAwayAvailable { get; }
    public string KeepAwayDetails { get; }
    /// <summary>The exact registry values, as the tick box's tooltip.</summary>
    public string KeepAwayValues { get; }
    /// <summary>Never pre-ticked: the user opts in.</summary>
    public bool KeepAway { get; set; }
}

/// <summary>One app in the removal confirmation.</summary>
public sealed class StoreRemoveItem
{
    public StoreRemoveItem(StoreAppRow row, bool thai)
    {
        Name = row.App.CatalogName.Length > 0 ? $"{row.Name} — {row.App.CatalogName}" : row.Name;

        var notes = new List<string>();
        if (row.VerdictText.Length > 0) notes.Add(row.VerdictText);
        notes.Add(row.App.ReinstallFromStore
            ? UninstallText.T("Uninstall_StoreReinstallYes", "Can be installed again from the Microsoft Store")
            : UninstallText.T("Uninstall_StoreReinstallNo", "May not be available to install again"));
        Note = string.Join("  ·  ", notes);

        Warning = row.App.Warning?.Get(thai) ?? "";
    }

    public string Name { get; }
    /// <summary>Verdict · whether the Microsoft Store has it.</summary>
    public string Note { get; }
    /// <summary>What the user loses ("your call" and hardware apps always have one).</summary>
    public string Warning { get; }
    public bool HasWarning => Warning.Length > 0;
}

/// <summary>A desktop program in the list (from the registry Uninstall keys).</summary>
public class InstalledProgram : UninstallRow
{
    public InstalledProgram(UninstallEntry entry)
    {
        Entry = entry;

        var details = new List<string>();
        if (entry.Publisher.Length > 0) details.Add(entry.Publisher);
        if (entry.DisplayVersion.Length > 0) details.Add(entry.DisplayVersion);
        if (entry.IsPerUser) details.Add(UninstallText.T("Uninstall_PerUser", "Current user only"));
        if (entry.IsSystemComponent) details.Add(UninstallText.T("Uninstall_SystemComponent", "System component"));
        Details = string.Join("  ·  ", details);

        Size = FormatSize(entry.EstimatedSizeKB);
        InstallDate = FormatDate(entry.InstallDate);
    }

    public UninstallEntry Entry { get; }
    public override string Name => Entry.DisplayName;
    public override string Publisher => Entry.Publisher;
    public bool IsSystemComponent => Entry.IsSystemComponent;
    public bool IsPerUser => Entry.IsPerUser;

    private static string FormatSize(long sizeKB)
    {
        if (sizeKB <= 0) return "—";
        if (sizeKB >= 1_048_576) return $"{sizeKB / 1_048_576.0:F1} GB";
        if (sizeKB >= 1024) return $"{sizeKB / 1024.0:F1} MB";
        return $"{sizeKB} KB";
    }

    private static string FormatDate(string raw)
    {
        // Normally yyyyMMdd; a few installers write other formats
        string[] formats = ["yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd", "M/d/yyyy"];
        return DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : "—";
    }
}

/// <summary>State of the modal "please wait" dialog.</summary>
public sealed class BusyDialogModel : INotifyPropertyChanged
{
    private string _message = "";
    private bool _canCancel;
    private double _progress;

    public string Title { get; init; } = "";
    public string CancelText { get; init; } = "";
    public bool HasCancel => CancelText.Length > 0;
    public bool ShowProgress { get; init; }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    public bool CanCancel
    {
        get => _canCancel;
        set => SetField(ref _canCancel, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    /// <summary>Set by the host right before it closes the dialog itself.</summary>
    internal bool WorkFinished { get; set; }

    public event Action? CancelRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Asks the running work to stop (only once).</summary>
    public void RequestCancel()
    {
        if (!CanCancel) return;
        CanCancel = false;
        CancelRequested?.Invoke();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>State of the leftover picker dialog.</summary>
public sealed class LeftoverPickerModel : INotifyPropertyChanged
{
    public LeftoverPickerModel(string title, string message, IEnumerable<LeftoverItem> items)
    {
        Title = title;
        Message = message;
        Items = new ObservableCollection<LeftoverItem>(items);
        foreach (var item in Items)
            item.PropertyChanged += (_, _) => OnSelectionChanged();
    }

    public string Title { get; }
    public string Message { get; }
    public ObservableCollection<LeftoverItem> Items { get; }

    public bool HasSelection => Items.Any(i => i.IsSelected);

    /// <summary>Select-all checkbox: true / false, or null when only some are selected.</summary>
    public bool? AllSelected
    {
        get
        {
            var selected = Items.Count(i => i.IsSelected);
            return selected == 0 ? false : selected == Items.Count ? true : null;
        }
        set
        {
            var select = value == true;
            foreach (var item in Items) item.IsSelected = select;
        }
    }

    public string SelectionSummary
    {
        get
        {
            var selected = Items.Where(i => i.IsSelected).ToList();
            return UninstallText.F("Uninstall_LeftoverSelection", "Selected {0} of {1} folders · {2}",
                selected.Count, Items.Count, UninstallText.FormatBytes(selected.Sum(i => i.Folder.Bytes)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnSelectionChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelection)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AllSelected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionSummary)));
    }
}

/// <summary>One leftover folder in the picker; unticked until the user chooses it.</summary>
public sealed class LeftoverItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public LeftoverItem(LeftoverFolder folder, string kindText)
    {
        Folder = folder;
        KindText = kindText;
        SizeText = UninstallText.FormatBytes(folder.Bytes);
        FilesText = UninstallText.F("Uninstall_FileCount", "{0:N0} files", folder.FileCount);
    }

    public LeftoverFolder Folder { get; }
    public string Path => Folder.Path;
    public string KindText { get; }
    public string SizeText { get; }
    public string FilesText { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Strings from the active language dictionary (Languages/*.xaml), with English fallbacks.</summary>
internal static class UninstallText
{
    /// <summary>True while the Thai dictionary is active — picks the language of the catalog texts kept in code.</summary>
    public static bool IsThai => Helpers.Loc.IsThai;

    public static string T(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as string ?? fallback;

    public static string F(string key, string fallback, params object[] args)
    {
        try
        {
            return string.Format(T(key, fallback), args);
        }
        catch (FormatException)
        {
            return string.Format(fallback, args);
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }
}

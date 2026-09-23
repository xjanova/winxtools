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

    public UninstallerView()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded fires again whenever the page is re-attached; one initial read is enough
        if (_loadedOnce) return;
        _loadedOnce = true;

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

    private void ApplyFilters()
    {
        if (!IsInitialized) return;

        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var search = SearchBox.Text.Trim();
        var showSystem = ShowSystemApps.IsChecked == true;

        // Hidden system components only on request — the default list matches Windows' Apps list
        var visible = _allPrograms.Where(p => showSystem || !p.IsSystemComponent).ToList();
        var filtered = search.Length == 0
            ? visible
            : visible.Where(p => p.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                                 p.Publisher.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                     .ToList();

        ProgramsList.ItemsSource = filtered;

        // While refreshing, the current list stays visible (no flash); an empty list shows the state text below
        CountText.Text = _isLoading
            ? (filtered.Count > 0 ? UninstallText.T("Uninstall_Loading", "Loading installed programs…") : "")
            : filtered.Count == visible.Count
                ? UninstallText.F("Uninstall_Count", "{0:N0} programs", visible.Count)
                : UninstallText.F("Uninstall_CountFiltered", "Showing {0:N0} of {1:N0} programs", filtered.Count, visible.Count);

        string? state = filtered.Count > 0 ? null
                      : _isLoading ? UninstallText.T("Uninstall_Loading", "Loading installed programs…")
                      : _loadError ?? (visible.Count == 0
                          ? UninstallText.T("Uninstall_Empty", "No installed programs were found.")
                          : UninstallText.T("Uninstall_NoMatch", "No programs match your search."));

        ListStateText.Text = state ?? "";
        ListStateText.Visibility = state == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void ShowSystemApps_Changed(object sender, RoutedEventArgs e)
    {
        ApplyFilters();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

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

            var listed = _allPrograms.Where(p => !p.IsSystemComponent).ToList();
            var perUser = listed.Count(p => p.IsPerUser);
            var hidden = _allPrograms.Count - listed.Count;

            ShowMessage(
                UninstallText.F("Uninstall_ScanResult",
                    "Found {0:N0} installed programs:\n• {1:N0} installed for all users\n• {2:N0} installed for your account only\n\nHidden system components: {3:N0} — turn on \"Show System Apps\" to list them.",
                    listed.Count, listed.Count - perUser, perUser, hidden),
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
        RefreshButton.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy;
        ProgramsList.IsEnabled = !busy;
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

    #endregion
}

/// <summary>A row in the program list.</summary>
public class InstalledProgram
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
    public string Name => Entry.DisplayName;
    public string Publisher => Entry.Publisher;
    /// <summary>Publisher · version · per-user / system component.</summary>
    public string Details { get; }
    public string Size { get; }
    public string InstallDate { get; }
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

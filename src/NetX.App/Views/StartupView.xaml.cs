using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetX.App.Dialogs;
using NetX.App.Helpers;
using NetX.Core.Helpers;
using NetX.Core.System.Background;
using NetX.Core.System.Tweaks;

namespace NetX.App.Views;

/// <summary>
/// Startup Optimize: everything that starts with Windows — programs, Store apps,
/// services, scheduled tasks, the browsers' own start-up settings and the things
/// Windows sets up by itself — each with what it is and whether it is worth
/// keeping. One switch per item, "switch off what nothing needs", and the game
/// profile: a special start-up that leaves only what playing needs (used after a
/// restart, undone as a whole).
/// </summary>
public partial class StartupView : Page
{
    private List<StartupItem> _items = new();
    private bool _isActive, _busy, _isAdmin;

    public StartupView()
    {
        InitializeComponent();
        Loaded += async (s, e) =>
        {
            _isActive = true;
            _isAdmin = AdminHelper.IsRunAsAdmin();
            await ScanAsync();
        };
        Unloaded += (s, e) => _isActive = false;
    }

    #region Scan and show

    private async Task ScanAsync()
    {
        SetBusy(true);
        StatusText.Text = Loc.T("Startup_Scanning", "Looking at what starts with Windows…");
        try
        {
            var items = await Task.Run(StartupAdvisor.Scan);
            if (!_isActive) return;
            _items = items;
            Render();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupView] Scan: {ex}");
            if (_isActive) StatusText.Text = Loc.F("Startup_ScanFailed", "Couldn't read the start-up list: {0}", ErrorText.From(ex));
        }
        finally
        {
            if (_isActive) SetBusy(false);
        }
    }

    private void Render()
    {
        bool thai = Loc.IsThai;
        int filter = FilterBox?.SelectedIndex ?? 0;
        IEnumerable<StartupItem> shown = filter switch
        {
            1 => _items.Where(i => i.Verdict is StartupVerdict.TurnOff or StartupVerdict.Alert),
            2 => _items.Where(i => i.Verdict == StartupVerdict.YourChoice),
            3 => _items.Where(i => i.Verdict == StartupVerdict.Keep),
            4 => _items.Where(i => i.Change != null),
            _ => _items
        };
        ItemList.ItemsSource = shown.Select(i => new StartupRow(i, thai)).ToList();

        // Alerts
        var alerts = _items.Where(i => i.Verdict == StartupVerdict.Alert && i.Enabled).ToList();
        AlertBanner.Visibility = alerts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AlertText.Text = string.Join("\n\n", alerts.Select(a => $"{a.Name}\n{a.Why.Get(thai)}"));

        // Game profile
        var gameChanges = SmartCpuService.Instance.ProfileChanges(StartupAdvisor.GameProfile);
        bool gameOn = gameChanges.Count > 0;
        var plan = StartupAdvisor.GamePlan(_items, LightestOption.IsChecked == true);
        GameText.Text = gameOn
            ? Loc.F("Startup_GameOn", "On — {0:N0} things are switched off for it. Leaving the profile puts every one of them back.", gameChanges.Count)
            : Loc.F("Startup_GameOff",
                "A special start-up for playing: {0:N0} things that are of no use in a game don't start at all, so the memory stays free. It is used after a restart, and one button puts everything back.",
                plan.Count);
        GameButton.Content = gameOn ? Loc.T("Startup_GameLeave", "Back to normal start-up") : Loc.T("Startup_GameSetUp", "Set up the game profile…");
        LightestOption.IsEnabled = !gameOn && !_busy;

        // Recommended
        var recommended = StartupAdvisor.RecommendedPlan(_items);
        RecommendedText.Text = recommended.Count > 0
            ? Loc.F("Startup_RecommendedSome", "{0:N0} things start with Windows that nothing you use depends on: updaters, usage-data collectors, entries left behind.", recommended.Count)
            : Loc.T("Startup_RecommendedNone", "Nothing left that is plainly unneeded. The rest is your choice, item by item.");
        RecommendedButton.IsEnabled = !_busy && recommended.Count > 0;

        // Summary
        int on = _items.Count(i => i.Enabled), ours = _items.Count(i => i.Change != null);
        SummaryText.Text = Loc.F("Startup_Summary", "{0:N0} things start with Windows. WinXTools has switched off {1:N0}.", on, ours);
        UndoAllButton.IsEnabled = !_busy && SmartCpuService.Instance.Changes.Count > 0;

        StatusText.Text = _isAdmin
            ? Loc.F("Startup_Status", "{0:N0} listed. A switch here is the same one Task Manager, Services and Task Scheduler use — nothing is deleted.", _items.Count)
            : Loc.T("Startup_NeedsAdmin", "Run WinXTools as administrator to see scheduled tasks and to switch services.");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        GameButton.IsEnabled = !busy;
        ItemList.IsEnabled = !busy;
        if (busy)
        {
            RecommendedButton.IsEnabled = false;
            UndoAllButton.IsEnabled = false;
            LightestOption.IsEnabled = false;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) await ScanAsync();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isActive && !_busy) Render();
    }

    #endregion

    #region One switch

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: StartupRow row } box) return;
        bool on = box.IsChecked == true;
        if (_busy)
        {
            box.IsChecked = row.IsOn;
            return;
        }

        var item = row.Item;
        // Something may depend on it: say so once before switching it off.
        if (!on && item.Verdict == StartupVerdict.Keep &&
            MessageBox.Show(Loc.F("Startup_ConfirmKeep", "{0}\n\n{1}\n\nSwitch it off anyway? You can switch it back on here at any time.", item.Name, item.Why.Get(Loc.IsThai)),
                Loc.T("Startup_Title", "Startup Optimize"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            box.IsChecked = row.IsOn;
            return;
        }

        SetBusy(true);
        string? error = null;
        try
        {
            error = await Task.Run(() => StartupAdvisor.SetEnabled(item, on));
        }
        catch (Exception ex)
        {
            error = ErrorText.From(ex);
        }
        if (!_isActive) return;
        if (error != null)
            MessageBox.Show(Loc.F("Startup_SwitchFailed", "Windows didn't allow changing \"{0}\":\n\n{1}", item.Name, error),
                Loc.T("Startup_Title", "Startup Optimize"), MessageBoxButton.OK, MessageBoxImage.Warning);
        await ScanAsync(); // shows what is really in force now
    }

    #endregion

    #region Recommended, game profile, undo

    private async void Recommended_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var plan = StartupAdvisor.RecommendedPlan(_items);
        if (plan.Count == 0) return;
        var dialog = new StartupPlanDialog(
            Loc.T("Startup_RecommendedTitle", "Switch off what nothing needs"),
            Loc.T("Startup_RecommendedIntro", "These start with Windows although nothing you use depends on them. Untick what you want to keep. Every one can be switched back on from the list."),
            Loc.T("Startup_PlanApply", "Switch off the ticked ones"),
            plan.Select(i => (i, true)), Loc.IsThai)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true || dialog.Chosen.Count == 0) return;
        await ApplyAsync(dialog.Chosen, StartupAdvisor.RecommendedProfile, askRestart: false);
    }

    private async void Game_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string title = Loc.T("Startup_GameTitle", "Game profile");

        if (StartupAdvisor.GameProfileActive)
        {
            int count = SmartCpuService.Instance.ProfileChanges(StartupAdvisor.GameProfile).Count;
            if (MessageBox.Show(Loc.F("Startup_GameLeaveConfirm", "Leave the game profile?\n\nThe {0:N0} things it switched off start with Windows again, exactly as before.", count),
                    title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            SetBusy(true);
            List<string> errors;
            try
            {
                errors = await Task.Run(StartupAdvisor.LeaveGameProfile);
            }
            catch (Exception ex)
            {
                errors = new List<string> { ErrorText.From(ex) };
            }
            if (!_isActive) return;
            await ScanAsync();
            if (errors.Count > 0)
                MessageBox.Show(Loc.F("Startup_SomeNotRestored", "These couldn't be put back:\n\n{0}", string.Join("\n", errors.Take(12))), title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            AskRestart(Loc.T("Startup_LeaveRestart", "Back to the normal start-up. Programs that were switched off start again at the next restart.\n\nRestart now?"), title);
            return;
        }

        bool lightest = LightestOption.IsChecked == true;
        // Everything the profile could take; the device extras are ticked only in the lightest setting.
        var candidates = StartupAdvisor.GamePlan(_items, lightest: true);
        if (candidates.Count == 0)
        {
            MessageBox.Show(Loc.T("Startup_GameNothing", "Nothing to switch off: only what playing needs starts with Windows already."), title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new StartupPlanDialog(title,
            Loc.T("Startup_GameIntro",
                "The game profile switches these off so they don't start with Windows: nothing here is needed to play. Graphics, sound, input devices, anti-cheat, Xbox services and security software stay on. Untick what you want to keep; device extras (lights, makers' dashboards) are ticked only in the lightest setting.\n\nIt is used after a restart. \"Back to normal start-up\" puts every one of them back."),
            Loc.T("Startup_GameApply", "Set up the game profile"),
            candidates.Select(i => (i, i.Game == GameRole.Off || lightest)), Loc.IsThai)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true || dialog.Chosen.Count == 0) return;
        await ApplyAsync(dialog.Chosen, StartupAdvisor.GameProfile, askRestart: true);
    }

    private async Task ApplyAsync(IReadOnlyList<StartupItem> plan, string profile, bool askRestart)
    {
        string title = Loc.T("Startup_Title", "Startup Optimize");
        SetBusy(true);
        StatusText.Text = Loc.T("Startup_Applying", "Switching off…");
        (int Done, List<string> Errors) result;
        try
        {
            result = await Task.Run(() => StartupAdvisor.Apply(plan, profile));
        }
        catch (Exception ex)
        {
            result = (0, new List<string> { ErrorText.From(ex) });
        }
        if (!_isActive) return;
        await ScanAsync();

        string message = Loc.F("Startup_Applied", "Switched off {0:N0} of {1:N0}.", result.Done, plan.Count);
        if (result.Errors.Count > 0)
            message += "\n\n" + Loc.F("Startup_AppliedErrors", "Windows didn't allow these:\n{0}", string.Join("\n", result.Errors.Take(10)));

        if (!askRestart || result.Done == 0)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            return;
        }
        if (AskRestart(message + "\n\n" + Loc.T("Startup_GameRestart", "The game profile is used from the next restart. Restart now?"), title)) return;

        // Not restarting yet: the programs that are open now can be closed to get their memory back at once.
        var stillOpen = plan.Where(i => i.ProcessCount > 0 && i.Entry?.Kind is AutostartKind.RunKey or AutostartKind.StartupFolder).ToList();
        if (stillOpen.Count == 0) return;
        if (MessageBox.Show(Loc.F("Startup_CloseNow", "{0:N0} of these programs are still open:\n\n{1}\n\nClose them now to free their memory? Programs with an open window are asked to close (save your work first); the ones running in the background are ended.",
                    stillOpen.Count, string.Join("\n", stillOpen.Take(12).Select(i => "• " + i.Name))),
                title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        SetBusy(true);
        try
        {
            await Task.Run(() => StartupAdvisor.CloseRunning(stillOpen));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupView] Close: {ex.Message}");
        }
        if (_isActive) await ScanAsync();
    }

    /// <summary>Asks, and restarts Windows on Yes. True when a restart was started.</summary>
    private static bool AskRestart(string question, string title)
    {
        if (MessageBox.Show(question, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        try
        {
            Process.Start(new ProcessStartInfo(CommandRunner.SystemTool("shutdown.exe"), "/r /t 5") { UseShellExecute = false, CreateNoWindow = true });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Startup_RestartFailed", "Windows didn't start the restart: {0}", ErrorText.From(ex)), title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async void UndoAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        int count = SmartCpuService.Instance.Changes.Count;
        if (count == 0) return;
        string title = Loc.T("Startup_Title", "Startup Optimize");
        if (MessageBox.Show(Loc.F("Startup_UndoAllConfirm", "Put back all {0:N0} things WinXTools switched off (here, in Smart CPU and by the game profile)?", count),
                title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        SetBusy(true);
        List<string> errors;
        try
        {
            errors = await Task.Run(() => SmartCpuService.Instance.UndoAll());
        }
        catch (Exception ex)
        {
            errors = new List<string> { ErrorText.From(ex) };
        }
        if (!_isActive) return;
        await ScanAsync();
        if (errors.Count > 0)
            MessageBox.Show(Loc.F("Startup_SomeNotRestored", "These couldn't be put back:\n\n{0}", string.Join("\n", errors.Take(12))), title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    #endregion
}

/// <summary>One line of the start-up list.</summary>
public sealed class StartupRow
{
    public StartupRow(StartupItem item, bool thai)
    {
        Item = item;
        Name = item.Name;
        What = item.What.Get(thai);
        Why = item.Why.Get(thai);
        IsOn = item.Enabled;
        CanSwitch = !item.Locked;
        LockTip = item.Locked ? Loc.T("Startup_Locked", "WinXTools doesn't switch this one.") : null;

        GroupText = Loc.T($"Startup_Group_{item.Group}", item.Group.ToString());
        VerdictText = Loc.T($"Startup_Verdict_{item.Verdict}", item.Verdict.ToString());
        (VerdictBrush, VerdictForeground, WhyBrush) = item.Verdict switch
        {
            StartupVerdict.Alert => (Brush("DangerBrush"), (Brush)Brushes.White, Brush("DangerBrush")),
            StartupVerdict.TurnOff => (Brush("WarningBrush"), (Brush)Brushes.Black, Brush("WarningBrush")),
            StartupVerdict.Keep => (Brush("SuccessBrush"), (Brush)Brushes.Black, Brush("TextSecondaryBrush")),
            _ => (Brush("SteelLightBrush"), (Brush)Brushes.Black, Brush("TextSecondaryBrush"))
        };

        GameText = item.Game switch
        {
            GameRole.Needed => Loc.T("Startup_GameKeeps", "stays on in the game profile"),
            GameRole.Extra => Loc.T("Startup_GameExtra", "off in the lightest game profile"),
            _ => ""
        };
        GameTagVisibility = GameText.Length > 0 && !item.Locked ? Visibility.Visible : Visibility.Collapsed;

        ChangeText = item.Change == null ? ""
            : item.Change.Profile == StartupAdvisor.GameProfile ? Loc.T("Startup_ChangeGame", "switched off by the game profile")
            : Loc.T("Startup_ChangeOurs", "switched off by WinXTools");
        ChangeVisibility = item.Change != null ? Visibility.Visible : Visibility.Collapsed;

        var parts = new List<string> { Loc.T($"Startup_Kind_{(item.Setting != null ? "Browser" : item.Entry!.Kind.ToString())}", item.Setting != null ? "Browser setting" : item.Entry!.Kind.ToString()) };
        if (!string.IsNullOrWhiteSpace(item.Publisher)) parts.Add(item.Publisher!);
        if (item.ProcessCount > 0) parts.Add(Loc.F("Startup_RunningNow", "running now, {0}", CleanerView.FormatSize(item.MemoryBytes)));
        Detail = string.Join("  ·  ", parts);
    }

    private static Brush Brush(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public StartupItem Item { get; }
    public string Name { get; }
    public string What { get; }
    public string Why { get; }
    public string Detail { get; }
    public bool IsOn { get; }
    public bool CanSwitch { get; }
    public string? LockTip { get; }
    public string GroupText { get; }
    public string VerdictText { get; }
    public Brush VerdictBrush { get; }
    public Brush VerdictForeground { get; }
    public Brush WhyBrush { get; }
    public string GameText { get; }
    public Visibility GameTagVisibility { get; }
    public string ChangeText { get; }
    public Visibility ChangeVisibility { get; }
}

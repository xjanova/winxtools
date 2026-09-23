using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NetX.App.Helpers;
using NetX.Core.Helpers;
using NetX.Core.Data;
using NetX.Core.Network;
using NetX.Core.Optimization;
using NetX.Core.System;

namespace NetX.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One failing button must not take the whole app (and the packet
        // engine that paces the user's traffic) down with it.
        DispatcherUnhandledException += (s, args) =>
        {
            Debug.WriteLine($"Unhandled UI exception: {args.Exception}");
            args.Handled = true;
            MessageBox.Show(
                (TryFindResource("App_UnexpectedError") as string ?? "Something went wrong, but WinXTools is still running:")
                    + "\n\n" + args.Exception.GetBaseException().Message,
                "WinXTools", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Debug.WriteLine($"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        // A previous run that died while a free proxy was applied would leave
        // every browser pointing at it; put the user's own settings back, and
        // make sure every way of exiting restores them too.
        ProxyService.RestoreIfLeftOver();
        SessionEnding += (s, args) => ProxyService.DisconnectOnExit();
        AppDomain.CurrentDomain.ProcessExit += (s, args) => ProxyService.DisconnectOnExit();
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.IsTerminating) ProxyService.DisconnectOnExit();
        };

        // Enable hardware acceleration and optimize rendering
        OptimizeRendering();

        // Set process priority to High for best performance
        SetHighPriority();

        // Initialize database on first run
        InitializeDatabase();

        // Resume the saved Auto-Kill / Smart-Kill watchdog and automatic RAM
        // optimization (they used to start only when their page was opened).
        _ = ProcessKiller.Instance;
        _ = RamOptimizer.Instance;

        // Check and set language
        InitializeLanguage();

        // Automation rules are evaluated in the background from startup
        // (before, nothing ever started the engine, so no rule ever fired).
        NetX.App.Views.RulesView.StartAutomation();

        // Apply saved bandwidth limits on startup
        ApplySavedBandwidthLimits();

        // The machine id reads the firmware table once; do it off the UI thread.
        MachineIdentity.Warm();

        // A trial that is already running shows as running from the first frame (the Pro pages
        // must not flash locked while the license server is asked).
        TrialService.Instance.RestoreLocalState();

        // Saved license first (instant, works offline), then the license server and the trial.
        _ = InitializeLicenseAsync();

        // Check for updates in background (non-blocking)
        _ = CheckForUpdatesOnStartupAsync();

        // WinXTools often runs for days (Start with Windows): look again every few hours.
        _backgroundChecks = new System.Threading.Timer(_ => _ = RunBackgroundChecksAsync(), null, BackgroundCheckInterval, BackgroundCheckInterval);
    }

    private static readonly TimeSpan BackgroundCheckInterval = TimeSpan.FromHours(6);
    private System.Threading.Timer? _backgroundChecks;
    private int _backgroundCheckRunning;

    private async Task InitializeLicenseAsync()
    {
        try
        {
            // register-device, then validate the saved key (or restore this PC's license)
            await XmanLicenseService.Instance.InitializeAsync();

            // The 48-hour trial is only looked at when there is no paid license.
            await TrialService.Instance.InitializeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"License init failed: {ex.Message}");
        }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            // Delay to not slow down app launch
            await Task.Delay(TimeSpan.FromSeconds(8));

            var updateInfo = await AutoUpdateService.Instance.CheckForUpdatesAsync();
            if (updateInfo?.IsUpdateAvailable != true) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow is not NetX.App.MainWindow window || AutoUpdateService.Instance.IsInstalling) return;

                var answer = MessageBox.Show(window,
                    Loc.F("Update_StartupPrompt",
                        "WinXTools {1} is available (you have {0}).\n\nUpdate now? WinXTools downloads it, checks its signature, closes, updates itself and opens again. Your settings and license are kept.",
                        updateInfo.CurrentVersion, updateInfo.LatestVersion),
                    Loc.T("Settings_Updates", "Updates"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information,
                    MessageBoxResult.Yes);

                if (answer == MessageBoxResult.Yes)
                    window.NavigateToSettings(updateInfo);
                else
                    window.ShowToast(Loc.F("Update_Later", "WinXTools {0} is ready to install — click here, or use Settings → Updates.", updateInfo.LatestVersion),
                        () => window.NavigateToSettings(updateInfo));
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Startup update check failed: {ex.Message}");
        }
    }

    // Periodic: confirm the license with the server and look for a new release. A found update
    // is offered with a clickable notice, never a dialog popping up over whatever the user does.
    private async Task RunBackgroundChecksAsync()
    {
        if (Interlocked.Exchange(ref _backgroundCheckRunning, 1) == 1) return;
        try
        {
            await XmanLicenseService.Instance.RefreshAsync();

            if (AutoUpdateService.Instance.IsInstalling) return;
            var updateInfo = await AutoUpdateService.Instance.CheckForUpdatesAsync();
            if (updateInfo?.IsUpdateAvailable != true) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow is NetX.App.MainWindow window && !AutoUpdateService.Instance.IsInstalling)
                {
                    window.ShowToast(Loc.F("Update_Later", "WinXTools {0} is ready to install — click here, or use Settings → Updates.", updateInfo.LatestVersion),
                        () => window.NavigateToSettings(updateInfo));
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Background checks failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _backgroundCheckRunning, 0);
        }
    }

    private void OptimizeRendering()
    {
        try
        {
            // Use hardware rendering if available (Tier 2 = full hardware acceleration)
            var renderingTier = RenderCapability.Tier >> 16;
            if (renderingTier >= 2)
            {
                // Hardware acceleration is available and enabled by default
                // Disable software rendering fallback for better performance
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
            }

            // Set global rendering options for performance
            Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(Timeline),
                new FrameworkPropertyMetadata { DefaultValue = 30 }); // Lower frame rate to reduce CPU usage
        }
        catch
        {
            // Ignore rendering optimization failures
        }
    }

    private void SetHighPriority()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.High;
        }
        catch
        {
            // Ignore if cannot set priority
        }
    }

    private void InitializeDatabase()
    {
        try
        {
            DatabaseService.Instance.Initialize();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to initialize database: {ex.Message}",
                "NetX - Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void InitializeLanguage()
    {
        // The language picked in Settings ("th-TH" / "en-US") wins; "auto" or
        // nothing saved follows the Windows display language.
        string? saved = null;
        try { saved = DatabaseService.Instance.GetSetting("Language"); } catch { }

        bool thai = saved switch
        {
            "th-TH" => true,
            "en-US" => false,
            _ => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "th"
        };
        string langFile = thai ? "Languages/th-TH.xaml" : "Languages/en-US.xaml";

        try
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri(langFile, UriKind.Relative)
            };

            // Remove old language dictionary and add new one
            var oldDict = Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source?.OriginalString.Contains("Languages/") == true);

            if (oldDict != null)
            {
                Resources.MergedDictionaries.Remove(oldDict);
            }

            Resources.MergedDictionaries.Add(dict);
        }
        catch
        {
            // Keep default language if failed
        }
    }

    public static void ChangeLanguage(string cultureCode)
    {
        string langFile = cultureCode == "th-TH" ? "Languages/th-TH.xaml" : "Languages/en-US.xaml";

        var dict = new ResourceDictionary
        {
            Source = new Uri(langFile, UriKind.Relative)
        };

        var oldDict = Current.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.Contains("Languages/") == true);

        if (oldDict != null)
        {
            Current.Resources.MergedDictionaries.Remove(oldDict);
        }

        Current.Resources.MergedDictionaries.Add(dict);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _backgroundChecks?.Dispose();
        ProxyService.DisconnectOnExit();
        base.OnExit(e);
    }

    public static bool IsRunAsAdmin()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Re-applies every saved bandwidth rule (per-app limits/blocks and the
    /// whole-PC limit) in the background. The limiter first removes firewall
    /// rules a previous session may have left behind, then re-creates them.
    /// </summary>
    private void ApplySavedBandwidthLimits()
    {
        _ = BandwidthLimiter.Instance.InitializeAsync().ContinueWith(t =>
            Debug.WriteLine($"Bandwidth init failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}

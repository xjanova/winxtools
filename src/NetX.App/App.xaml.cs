using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NetX.Core.Helpers;
using NetX.Core.Data;
using NetX.Core.Network;
using NetX.Core.System;

namespace NetX.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Enable hardware acceleration and optimize rendering
        OptimizeRendering();

        // Set process priority to High for best performance
        SetHighPriority();

        // Initialize database on first run
        InitializeDatabase();

        // Check and set language
        InitializeLanguage();

        // Apply saved bandwidth limits on startup
        ApplySavedBandwidthLimits();

        // Register device and validate license in background
        _ = InitializeLicenseAsync();

        // Check for updates in background (non-blocking)
        _ = CheckForUpdatesOnStartupAsync();
    }

    private async Task InitializeLicenseAsync()
    {
        try
        {
            // Register device with xman studio
            await XmanLicenseService.Instance.RegisterDeviceAsync();

            // Validate saved license key if exists
            var savedKey = DatabaseService.Instance.GetSetting("LicenseKey");
            if (!string.IsNullOrEmpty(savedKey))
            {
                await XmanLicenseService.Instance.ValidateAsync(savedKey);
            }

            // Initialize 48-hour trial system (after license check)
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
            await Task.Delay(5000);

            var updateInfo = await AutoUpdateService.Instance.CheckForUpdatesAsync();
            if (updateInfo?.IsUpdateAvailable == true)
            {
                Dispatcher.Invoke(() =>
                {
                    var result = MessageBox.Show(
                        $"A new version of WinXTools is available!\n\n" +
                        $"Current: v{updateInfo.CurrentVersion}\n" +
                        $"New: v{updateInfo.LatestVersion}\n\n" +
                        $"Go to Settings to update.",
                        "Update Available",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Startup update check failed: {ex.Message}");
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
        // Get system language
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        var langCode = culture.TwoLetterISOLanguageName;

        // Set Thai if system is Thai, otherwise English
        string langFile = langCode == "th" ? "Languages/th-TH.xaml" : "Languages/en-US.xaml";

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

    public static bool IsRunAsAdmin()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Apply saved bandwidth limits on application startup
    /// </summary>
    private void ApplySavedBandwidthLimits()
    {
        try
        {
            // Find active network interface
            var activeInterface = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                          && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                          && IsPhysicalAdapter(ni))
                .Select(ni => {
                    try
                    {
                        var stats = ni.GetIPStatistics();
                        return (ni, total: stats.BytesReceived + stats.BytesSent);
                    }
                    catch { return (ni, total: 0L); }
                })
                .OrderByDescending(x => x.total)
                .FirstOrDefault().ni;

            if (activeInterface == null) return;

            // Check if there are saved limits for this interface
            var savedRule = BandwidthLimiter.Instance.GetLimit($"interface:{activeInterface.Id}");
            if (savedRule == null) return;

            // Only apply if not both unlimited
            if (savedRule.DownloadLimitKBps < 0 && savedRule.UploadLimitKBps < 0) return;

            // Values are stored as Kbps
            var dlKbps = savedRule.DownloadLimitKBps;
            var ulKbps = savedRule.UploadLimitKBps;
            Debug.WriteLine($"Applying saved bandwidth limits: DL={dlKbps} Kbps, UL={ulKbps} Kbps");

            // Start PacketEngine and apply throttle
            var packetEngine = PacketEngine.Instance;
            if (packetEngine.IsDriverLoaded)
            {
                packetEngine.Start();

                // Convert Kbps to bytes per second (1 Kbps = 125 bytes/sec)
                // 0 = blocked (use 1 byte/sec for extreme throttle)
                long dlBytesPerSec = dlKbps > 0 ? dlKbps * 125 : (dlKbps == 0 ? 1 : 0);
                long ulBytesPerSec = ulKbps > 0 ? ulKbps * 125 : (ulKbps == 0 ? 1 : 0);

                if (dlBytesPerSec > 0 || ulBytesPerSec > 0)
                {
                    packetEngine.SetGlobalThrottle(dlBytesPerSec, ulBytesPerSec);
                    Debug.WriteLine($"PacketEngine throttle applied on startup: {dlBytesPerSec} B/s down, {ulBytesPerSec} B/s up");
                }
            }

            BandwidthLimiter.Instance.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error applying saved bandwidth limits: {ex.Message}");
        }
    }

    private static bool IsPhysicalAdapter(NetworkInterface ni)
    {
        var desc = ni.Description.ToLowerInvariant();
        var name = ni.Name.ToLowerInvariant();

        if (desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("virtualbox") ||
            desc.Contains("hyper-v") || desc.Contains("vpn") || desc.Contains("tap-") ||
            desc.Contains("tunnel") || desc.Contains("pseudo") || desc.Contains("miniport") ||
            desc.Contains("wan") || desc.Contains("teredo") || desc.Contains("isatap") ||
            desc.Contains("6to4") || desc.Contains("bluetooth") ||
            name.Contains("vethernet") || name.Contains("docker") || name.Contains("wsl"))
        {
            return false;
        }

        return ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
               ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
               ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet;
    }
}

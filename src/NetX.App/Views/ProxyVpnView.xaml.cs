using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetX.Core.Network;

namespace NetX.App.Views;

/// <summary>
/// Free proxy page. A public HTTP proxy set as the Windows proxy - not a VPN, and the UI says so.
/// Everything shown is read back from <see cref="ProxyService"/>; nothing is assumed.
/// </summary>
public partial class ProxyVpnView : Page
{
    private static readonly Color ConnectedColor = Color.FromRgb(0x22, 0xc5, 0x5e);    // Green
    private static readonly Color WarningColor = Color.FromRgb(0xf5, 0x9e, 0x0b);      // Amber
    private static readonly Color DisconnectedColor = Color.FromRgb(0xef, 0x44, 0x44); // Red

    private readonly ProxyService _proxyService;
    private CancellationTokenSource _pageCts = new();   // cancelled when the page is left
    private CancellationTokenSource? _operationCts;     // connect / refresh (overlay Cancel button)
    private bool _isBusy;
    private bool _isActive;
    private int _progressHighWater;
    private ProxyRestoreResult _leftOver = ProxyRestoreResult.NothingToRestore;
    private string _selectedRegion = "All";
    private List<CountryDisplayItem> _allCountries = new();

    public ProxyVpnView()
    {
        InitializeComponent();
        _proxyService = ProxyService.Instance;

        LoadCountries();

        // The service is a singleton and the Frame journal can show this same instance again,
        // so subscribe on Loaded and unsubscribe on Unloaded (never in the constructor).
        Loaded += ProxyVpnView_Loaded;
        Unloaded += ProxyVpnView_Unloaded;
    }

    private void ProxyVpnView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isActive) return;
        _isActive = true;

        if (_pageCts.IsCancellationRequested)
        {
            _pageCts = new CancellationTokenSource();
        }

        _proxyService.OnStateChanged -= OnProxyStateChanged;
        _proxyService.OnStateChanged += OnProxyStateChanged;

        // A WinXTools session that died while connected may have left its proxy behind.
        _leftOver = ProxyService.RestoreIfLeftOver();

        RefreshCountTexts();
        RenderStatus();
    }

    private void ProxyVpnView_Unloaded(object sender, RoutedEventArgs e)
    {
        _isActive = false;
        _proxyService.OnStateChanged -= OnProxyStateChanged;

        // Leaving the page stops any test or refresh, so the proxy never changes behind the user's back.
        _operationCts?.Cancel();
        _pageCts.Cancel();
    }

    #region Country list

    private void LoadCountries()
    {
        _allCountries = ProxyService.Countries.Values
            .OrderBy(c => c.Region)
            .ThenBy(c => c.Name)
            .Select(c => new CountryDisplayItem
            {
                Code = c.Code,
                Name = c.Name,
                Flag = c.Flag,
                Region = c.Region,
                ProxyCountText = GetCachedCountText(c.Code)
            })
            .ToList();

        ApplyFilters();
    }

    private void RefreshCountTexts()
    {
        foreach (var country in _allCountries)
        {
            if (!country.IsLoading)
            {
                country.ProxyCountText = GetCachedCountText(country.Code);
            }
        }
    }

    private void UpdateCountText(string countryCode)
    {
        var country = _allCountries.FirstOrDefault(c => c.Code == countryCode);
        if (country != null && !country.IsLoading)
        {
            country.ProxyCountText = GetCachedCountText(countryCode);
        }
    }

    private string GetCachedCountText(string countryCode)
    {
        return _proxyService.HasCachedProxies(countryCode)
            ? GetCountText(_proxyService.GetCachedProxyCount(countryCode))
            : L("Proxy_CardClickToLoad", "Click card to load list");
    }

    private static string GetCountText(int count)
    {
        return count > 0
            ? F("Proxy_CardListed", "{0} proxies listed", count)
            : L("Proxy_CardNoneListed", "No proxies listed");
    }

    private static string GetCountText(ProxyFetchResult result)
    {
        return result.AllSourcesFailed && result.Proxies.Count == 0
            ? L("Proxy_CardLoadFailed", "Couldn't load list")
            : GetCountText(result.UsableCount);
    }

    private void ApplyFilters()
    {
        var searchText = SearchBox.Text?.ToLowerInvariant() ?? "";

        var filtered = _allCountries.Where(c =>
        {
            // Region filter
            if (_selectedRegion != "All")
            {
                // Map "America" to both North and South America
                if (_selectedRegion == "America")
                {
                    if (c.Region != "America") return false;
                }
                else if (_selectedRegion == "Other")
                {
                    if (c.Region != "Oceania" && c.Region != "Africa" && c.Region != "Middle East")
                        return false;
                }
                else if (c.Region != _selectedRegion)
                    return false;
            }

            // Search filter
            if (!string.IsNullOrEmpty(searchText))
            {
                if (!c.Name.ToLowerInvariant().Contains(searchText) &&
                    !c.Code.ToLowerInvariant().Contains(searchText))
                    return false;
            }

            return true;
        }).ToList();

        if (CountriesGrid != null)
            CountriesGrid.ItemsSource = filtered;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void RegionFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true } rb)
        {
            _selectedRegion = rb.Tag as string ?? "All";
            ApplyFilters();
        }
    }

    /// <summary>
    /// Card click only (re)loads that country's list - it never touches the system proxy.
    /// </summary>
    private async void Country_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { DataContext: CountryDisplayItem country } || country.IsLoading)
            return;

        var ct = _pageCts.Token;
        country.IsLoading = true;
        country.ProxyCountText = L("Proxy_CardLoading", "Loading list...");

        try
        {
            var result = await _proxyService.FetchProxiesAsync(country.Code, ct, forceRefresh: true);
            country.ProxyCountText = GetCountText(result);
        }
        catch (OperationCanceledException)
        {
            country.ProxyCountText = GetCachedCountText(country.Code);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Proxy list load failed for {country.Code}: {ex}");
            country.ProxyCountText = L("Proxy_CardLoadFailed", "Couldn't load list");
        }
        finally
        {
            country.IsLoading = false;
        }
    }

    #endregion

    #region Connect / Disconnect / Refresh

    private async void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string countryCode })
        {
            await ConnectToCountryAsync(countryCode);
        }
    }

    private async Task ConnectToCountryAsync(string countryCode)
    {
        // Guards double clicks / Enter on a focused button while the overlay is up.
        if (_isBusy) return;

        var countryName = GetCountryName(countryCode);
        var cts = BeginOperation();
        var ct = cts.Token;
        var progress = new Progress<ProxyProgress>(p => ReportProgress(p, countryName, ct));
        ShowLoading(true, F("Proxy_ProgressFetching", "Downloading the proxy list for {0}...", countryName));

        try
        {
            var result = await _proxyService.QuickConnectAsync(countryCode, progress, ct);
            var recheckedAlternative = false;

            // Working proxies exist, but none really exits in the chosen country: ask, don't assume.
            if (result.Status == ProxyConnectStatus.OnlyOtherCountry && result.Proxy is { } alternative && CanShowUi(ct))
            {
                ShowLoading(false);
                var question = F("Proxy_AskOtherCountry",
                    "No working proxy exits in {0}.\n\nThe best working proxy found exits in {1} (IP {2}, {3} ms).\n\nConnect to it anyway?",
                    countryName, GetCountryName(alternative.ActualCountry), alternative.ExternalIP ?? "-", alternative.Latency);
                if (!Ask(question)) return;

                ShowLoading(true, L("Proxy_ProgressRechecking", "Re-checking the proxy over HTTPS..."));
                result = await _proxyService.ConnectAsync(alternative.Proxy, progress, ct);
                recheckedAlternative = true;
            }

            if (CanShowUi(ct))
            {
                ShowConnectOutcome(result, countryName, recheckedAlternative);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user or by leaving the page - nothing was applied.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Proxy connect failed: {ex}");
            if (CanShowUi(ct))
            {
                ShowMessage(L("Proxy_ErrUnexpected", "Something went wrong while connecting. Please try again."));
            }
        }
        finally
        {
            EndOperation(cts);
            ShowLoading(false);
            UpdateCountText(countryCode);
            RenderStatus();
        }
    }

    private void ShowConnectOutcome(ProxyConnectResult result, string countryName, bool recheckedAlternative)
    {
        var message = result.Status switch
        {
            // The status card shows the real exit country, IP and latency - no dialog needed.
            ProxyConnectStatus.Connected => null,
            ProxyConnectStatus.NoWorkingProxy when recheckedAlternative =>
                L("Proxy_ErrProxyStopped", "That proxy stopped responding, so nothing was changed. Please try again."),
            ProxyConnectStatus.NoWorkingProxy =>
                F("Proxy_ErrNoWorking",
                  "No working proxy was found for {0} ({1} tested). Free proxies go offline often — try again in a few minutes, press Refresh, or pick another country.",
                  countryName, result.TestedCount),
            ProxyConnectStatus.NoProxiesListed =>
                F("Proxy_ErrNoneListed", "No proxies are listed for {0} right now. Try another country.", countryName),
            ProxyConnectStatus.ListUnavailable =>
                L("Proxy_ErrListUnavailable", "Couldn't download the proxy lists. Check your internet connection and try again."),
            ProxyConnectStatus.OwnedByOtherInstance =>
                L("Proxy_ErrOtherInstance", "Another WinXTools window is already using a proxy. Disconnect it in that window first."),
            ProxyConnectStatus.BackupFailed =>
                L("Proxy_ErrBackupFailed", "Couldn't save a backup of your current proxy settings, so nothing was changed."),
            ProxyConnectStatus.ApplyFailed =>
                L("Proxy_ErrApplyFailed", "Windows didn't accept the proxy setting — it may be locked by your organization. You are not connected."),
            _ => null // OnlyOtherCountry declined, AppClosing
        };

        if (message != null)
        {
            ShowMessage(message);
        }
    }

    private async void DisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        _isBusy = true;
        DisconnectBtn.IsEnabled = false;
        try
        {
            // Registry/WinINET writes are fast, but keep them off the UI thread anyway.
            var result = await Task.Run(() => _proxyService.Disconnect());
            if (result != ProxyRestoreResult.Failed)
            {
                _leftOver = ProxyRestoreResult.NothingToRestore;
            }
            if (!_isActive) return;

            switch (result)
            {
                case ProxyRestoreResult.ChangedElsewhere:
                    ShowMessage(L("Proxy_InfoChangedElsewhere",
                        "Your Windows proxy settings were changed outside WinXTools while connected, so WinXTools left them as they are."),
                        MessageBoxImage.Information);
                    break;
                case ProxyRestoreResult.Failed:
                    ShowMessage(L("Proxy_ErrRestoreFailed",
                        "Couldn't restore your original proxy settings. Check Windows Settings > Network & internet > Proxy. WinXTools will try again the next time it starts."));
                    break;
                case ProxyRestoreResult.InUseByOtherInstance:
                    ShowMessage(L("Proxy_ErrOtherInstance",
                        "Another WinXTools window is already using a proxy. Disconnect it in that window first."));
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Proxy disconnect failed: {ex}");
            if (_isActive)
            {
                ShowMessage(L("Proxy_ErrRestoreFailed",
                    "Couldn't restore your original proxy settings. Check Windows Settings > Network & internet > Proxy. WinXTools will try again the next time it starts."));
            }
        }
        finally
        {
            _isBusy = false;
            DisconnectBtn.IsEnabled = true;
            RenderStatus();
        }
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var cts = BeginOperation();
        var ct = cts.Token;
        var total = _allCountries.Count;
        var done = 0;
        ShowLoading(true, F("Proxy_ProgressRefreshing", "Refreshing proxy lists... {0}/{1}", done, total));

        // Each card updates as soon as its country finishes; unfinished ones keep their old count.
        var progress = new Progress<(string Code, ProxyFetchResult Result)>(r =>
        {
            var country = _allCountries.FirstOrDefault(c => c.Code == r.Code);
            if (country != null && !country.IsLoading)
            {
                country.ProxyCountText = GetCountText(r.Result);
            }

            done++;
            if (CanShowUi(ct))
            {
                LoadingText.Text = F("Proxy_ProgressRefreshing", "Refreshing proxy lists... {0}/{1}", done, total);
            }
        });

        try
        {
            var refreshed = await _proxyService.RefreshAllProxiesAsync(_allCountries.Select(c => c.Code).ToList(), progress, ct);
            if (refreshed == 0 && CanShowUi(ct))
            {
                ShowMessage(L("Proxy_ErrListUnavailable", "Couldn't download the proxy lists. Check your internet connection and try again."));
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled: countries that finished keep their new list.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Proxy list refresh failed: {ex}");
            if (CanShowUi(ct))
            {
                ShowMessage(L("Proxy_ErrListUnavailable", "Couldn't download the proxy lists. Check your internet connection and try again."));
            }
        }
        finally
        {
            EndOperation(cts);
            ShowLoading(false);
        }
    }

    private void CancelOperationBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCts is { IsCancellationRequested: false } cts)
        {
            cts.Cancel();
            LoadingText.Text = L("Proxy_Cancelling", "Cancelling...");
        }
    }

    private CancellationTokenSource BeginOperation()
    {
        _isBusy = true;
        _progressHighWater = 0;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token);
        _operationCts = cts;
        return cts;
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_operationCts, cts))
        {
            _operationCts = null;
        }
        cts.Dispose();
        _isBusy = false;
    }

    private bool CanShowUi(CancellationToken ct) => _isActive && !ct.IsCancellationRequested;

    private void ReportProgress(ProxyProgress progress, string countryName, CancellationToken ct)
    {
        if (!CanShowUi(ct)) return;

        switch (progress.Stage)
        {
            case ProxyProgressStage.FetchingList:
                _progressHighWater = 0;
                LoadingText.Text = F("Proxy_ProgressFetching", "Downloading the proxy list for {0}...", countryName);
                break;

            case ProxyProgressStage.Testing:
                // Reports from parallel tests can arrive slightly out of order.
                if (progress.Done == 0) _progressHighWater = 0;
                if (progress.Done < _progressHighWater) return;
                _progressHighWater = progress.Done;
                LoadingText.Text = F("Proxy_ProgressTesting", "Testing proxies for {0} over HTTPS... {1}/{2}",
                    countryName, progress.Done, progress.Total);
                break;

            case ProxyProgressStage.Applying:
                LoadingText.Text = L("Proxy_ProgressApplying", "Applying the Windows proxy setting...");
                break;
        }
    }

    #endregion

    #region Status

    private void OnProxyStateChanged()
    {
        // Raised on any thread. InvokeAsync never blocks the service (Invoke could deadlock on exit).
        Dispatcher.InvokeAsync(() =>
        {
            if (_isActive) RenderStatus();
        });
    }

    private void RenderStatus()
    {
        var status = _proxyService.Status;

        if (status.IsConnected && status.Proxy != null)
        {
            var picked = status.Proxy.CountryCode;
            var exit = status.ExitCountry;
            var ip = status.ExitIp ?? status.Proxy.Host;

            if (!status.IsHealthy)
            {
                SetStatus(WarningColor,
                    L("Proxy_StatusUnhealthy", "Proxy not responding"),
                    L("Proxy_StatusUnhealthyDetail", "Websites may not load. Disconnect, or pick another country."),
                    showDisconnect: true);
            }
            else if (!string.Equals(exit, picked, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(WarningColor,
                    F("Proxy_StatusExitMismatch", "Connected — exits in {0}", GetCountryLabel(exit)),
                    F("Proxy_StatusExitMismatchDetail", "You picked {0}, but this proxy really exits in {1}. IP {2} · {3} ms",
                        GetCountryName(picked), GetCountryName(exit), ip, status.LatencyMs),
                    showDisconnect: true);
            }
            else
            {
                SetStatus(ConnectedColor,
                    F("Proxy_StatusConnected", "Connected via proxy: {0}", GetCountryLabel(picked)),
                    F("Proxy_StatusConnectedDetail", "Exit IP {0} · {1} ms · HTTPS checked", ip, status.LatencyMs),
                    showDisconnect: true);
            }
            return;
        }

        // A restore that failed (this session, or a crashed earlier one) must keep Disconnect reachable.
        var leftOver = status.RestorePending ? ProxyRestoreResult.Failed : _leftOver;

        switch (leftOver)
        {
            case ProxyRestoreResult.Failed:
                SetStatus(WarningColor,
                    L("Proxy_StatusLeftOver", "Windows is still using a WinXTools proxy"),
                    L("Proxy_StatusLeftOverDetail", "Press Disconnect to restore your original proxy settings."),
                    showDisconnect: true);
                break;

            case ProxyRestoreResult.InUseByOtherInstance:
                SetStatus(WarningColor,
                    L("Proxy_StatusOtherInstance", "Another WinXTools window is using a proxy"),
                    L("Proxy_StatusOtherInstanceDetail", "Disconnect it in that window first."),
                    showDisconnect: false);
                break;

            default:
                SetStatus(DisconnectedColor,
                    L("Proxy_StatusNotConnected", "Not connected"),
                    leftOver == ProxyRestoreResult.Restored
                        ? L("Proxy_StatusRestoredLeftOver", "WinXTools was closed while connected last time — your original proxy settings have been restored.")
                        : L("Proxy_StatusPickCountry", "Pick a country and press Connect."),
                    showDisconnect: false);
                break;
        }
    }

    private void SetStatus(Color color, string text, string detail, bool showDisconnect)
    {
        StatusIndicator.Fill = new SolidColorBrush(color);
        StatusIndicator.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 10,
            ShadowDepth = 0,
            Color = color,
            Opacity = 0.5
        };
        StatusText.Text = text;
        StatusDetail.Text = detail;
        DisconnectBtn.Visibility = showDisconnect ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowLoading(bool show, string? text = null)
    {
        LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (text != null)
        {
            LoadingText.Text = text;
        }
    }

    private void ShowMessage(string message, MessageBoxImage icon = MessageBoxImage.Warning)
    {
        var title = L("Proxy_DialogTitle", "Free Proxy");
        var owner = Window.GetWindow(this);
        if (owner != null)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, icon);
    }

    private bool Ask(string question)
    {
        var title = L("Proxy_DialogTitle", "Free Proxy");
        var owner = Window.GetWindow(this);
        var answer = owner != null
            ? MessageBox.Show(owner, question, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(question, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes;
    }

    #endregion

    #region Localization helpers

    private static string GetCountryName(string? countryCode)
    {
        if (string.IsNullOrEmpty(countryCode))
            return L("Proxy_CountryUnknown", "an unknown country");

        return ProxyService.Countries.TryGetValue(countryCode, out var country) ? country.Name : countryCode;
    }

    private static string GetCountryLabel(string? countryCode)
    {
        return countryCode != null && ProxyService.Countries.TryGetValue(countryCode, out var country)
            ? $"{country.Flag} {country.Name}"
            : GetCountryName(countryCode);
    }

    private static string L(string key, string fallback)
    {
        return Application.Current?.TryFindResource(key) as string ?? fallback;
    }

    /// <summary>Localized format string; falls back to English if a translation's placeholders are broken.</summary>
    private static string F(string key, string fallback, params object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, L(key, fallback), args);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.CurrentCulture, fallback, args);
        }
    }

    #endregion
}

public class CountryDisplayItem : INotifyPropertyChanged
{
    private string _proxyCountText = "";

    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Flag { get; set; } = "";
    public string Region { get; set; } = "";
    public bool IsLoading { get; set; }

    // Notifies so a card updates in place (no ItemsSource reset / flicker).
    public string ProxyCountText
    {
        get => _proxyCountText;
        set
        {
            if (_proxyCountText == value) return;
            _proxyCountText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProxyCountText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

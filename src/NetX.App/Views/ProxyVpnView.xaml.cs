using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetX.Core.Network;

namespace NetX.App.Views;

public partial class ProxyVpnView : Page
{
    private readonly ProxyService _proxyService;
    private CancellationTokenSource? _cts;
    private string _selectedRegion = "All";
    private List<CountryDisplayItem> _allCountries = new();

    public ProxyVpnView()
    {
        InitializeComponent();
        _proxyService = ProxyService.Instance;

        // Subscribe to events
        _proxyService.OnProxyConnected += OnProxyConnected;
        _proxyService.OnProxyDisconnected += OnProxyDisconnected;
        _proxyService.OnStatusChanged += OnStatusChanged;
        _proxyService.OnError += OnError;

        LoadCountries();
        UpdateConnectionStatus();
    }

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
                ProxyCountText = GetCachedProxyCountText(c.Code)
            })
            .ToList();

        ApplyFilters();
    }

    private string GetCachedProxyCountText(string countryCode)
    {
        var count = _proxyService.GetCachedProxyCount(countryCode);
        if (count > 0)
        {
            return $"{count} proxies available";
        }
        return "Click to load";
    }

    private async Task FetchProxyCountsAsync(bool forceRefresh = false)
    {
        foreach (var country in _allCountries)
        {
            try
            {
                country.ProxyCountText = "Loading...";
                Dispatcher.Invoke(() => ApplyFilters());

                List<ProxyInfo> proxies;
                if (forceRefresh)
                {
                    proxies = await _proxyService.RefreshProxiesAsync(country.Code);
                }
                else
                {
                    proxies = await _proxyService.FetchProxiesAsync(country.Code);
                }

                country.ProxyCountText = proxies.Count > 0
                    ? $"{proxies.Count} proxies available"
                    : "No proxies found";

                // Update UI
                Dispatcher.Invoke(() => ApplyFilters());
            }
            catch
            {
                country.ProxyCountText = "Check connection";
            }
        }
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
        if (sender is RadioButton rb)
        {
            _selectedRegion = rb.Tag?.ToString() ?? "All";
            if (rb.IsChecked == true && rb.Content?.ToString() == "All")
                _selectedRegion = "All";

            ApplyFilters();
        }
    }

    private async void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string countryCode)
        {
            await ConnectToCountry(countryCode);
        }
    }

    private async void Country_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string countryCode)
        {
            await ConnectToCountry(countryCode);
        }
    }

    private async Task ConnectToCountry(string countryCode)
    {
        if (_proxyService.IsConnected)
        {
            _proxyService.Disconnect();
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        ShowLoading(true, $"Connecting to {ProxyService.Countries.GetValueOrDefault(countryCode)?.Name ?? countryCode}...");

        try
        {
            var success = await _proxyService.QuickConnectAsync(countryCode, _cts.Token);
            if (!success)
            {
                ShowError("Failed to connect. Please try another country.");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by user
        }
        catch (Exception ex)
        {
            ShowError($"Connection error: {ex.Message}");
        }
        finally
        {
            ShowLoading(false);
        }
    }

    private void DisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _proxyService.Disconnect();
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        ShowLoading(true, "Refreshing proxy list...");

        // Clear cache and force re-fetch
        _proxyService.ClearCache();
        _allCountries.ForEach(c => c.ProxyCountText = "Loading...");
        ApplyFilters();

        await FetchProxyCountsAsync(forceRefresh: true);

        ShowLoading(false);
    }

    private void OnProxyConnected(ProxyInfo proxy)
    {
        Dispatcher.Invoke(() =>
        {
            var country = ProxyService.Countries.GetValueOrDefault(proxy.CountryCode);
            UpdateConnectionStatus(true, country?.Flag ?? "", country?.Name ?? proxy.CountryCode,
                $"IP: {proxy.ExternalIP ?? proxy.Host} | Latency: {proxy.Latency}ms");
        });
    }

    private void OnProxyDisconnected()
    {
        Dispatcher.Invoke(() => UpdateConnectionStatus(false));
    }

    private void OnStatusChanged(string status)
    {
        Dispatcher.Invoke(() =>
        {
            LoadingText.Text = status;
        });
    }

    private void OnError(string error)
    {
        Dispatcher.Invoke(() =>
        {
            ShowLoading(false);
            ShowError(error);
        });
    }

    private void UpdateConnectionStatus(bool connected = false, string flag = "", string country = "", string detail = "")
    {
        if (connected)
        {
            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)); // Green
            StatusIndicator.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 0,
                Color = Color.FromRgb(0x22, 0xc5, 0x5e),
                Opacity = 0.5
            };
            StatusText.Text = $"{flag} Connected to {country}";
            StatusDetail.Text = detail;
            DisconnectBtn.Visibility = Visibility.Visible;
        }
        else
        {
            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)); // Red
            StatusIndicator.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 0,
                Color = Color.FromRgb(0xef, 0x44, 0x44),
                Opacity = 0.5
            };
            StatusText.Text = "Disconnected";
            StatusDetail.Text = "Select a country to connect";
            DisconnectBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowLoading(bool show, string text = "Connecting...")
    {
        LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Text = text;
    }

    private void ShowError(string message)
    {
        MessageBox.Show(message, "Connection Error", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

public class CountryDisplayItem
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Flag { get; set; } = "";
    public string Region { get; set; } = "";
    public string ProxyCountText { get; set; } = "";
}

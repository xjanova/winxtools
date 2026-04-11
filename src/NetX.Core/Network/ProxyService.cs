using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NetX.Core.Network;

/// <summary>
/// Service for managing proxy connections and fetching free proxies
/// </summary>
public class ProxyService : IDisposable
{
    private static ProxyService? _instance;
    public static ProxyService Instance => _instance ??= new ProxyService();

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, List<ProxyInfo>> _proxyCache = new();
    private readonly string _cacheFilePath;
    private ProxyInfo? _currentProxy;
    private bool _isConnected;
    private DateTime _lastCacheLoad = DateTime.MinValue;

    public event Action<ProxyInfo>? OnProxyConnected;
    public event Action? OnProxyDisconnected;
    public event Action<string>? OnError;
    public event Action<string>? OnStatusChanged;

    // Country data with flag emojis
    public static readonly Dictionary<string, CountryInfo> Countries = new()
    {
        { "US", new CountryInfo("US", "United States", "🇺🇸", "America") },
        { "GB", new CountryInfo("GB", "United Kingdom", "🇬🇧", "Europe") },
        { "DE", new CountryInfo("DE", "Germany", "🇩🇪", "Europe") },
        { "FR", new CountryInfo("FR", "France", "🇫🇷", "Europe") },
        { "NL", new CountryInfo("NL", "Netherlands", "🇳🇱", "Europe") },
        { "JP", new CountryInfo("JP", "Japan", "🇯🇵", "Asia") },
        { "SG", new CountryInfo("SG", "Singapore", "🇸🇬", "Asia") },
        { "KR", new CountryInfo("KR", "South Korea", "🇰🇷", "Asia") },
        { "HK", new CountryInfo("HK", "Hong Kong", "🇭🇰", "Asia") },
        { "TW", new CountryInfo("TW", "Taiwan", "🇹🇼", "Asia") },
        { "IN", new CountryInfo("IN", "India", "🇮🇳", "Asia") },
        { "TH", new CountryInfo("TH", "Thailand", "🇹🇭", "Asia") },
        { "VN", new CountryInfo("VN", "Vietnam", "🇻🇳", "Asia") },
        { "ID", new CountryInfo("ID", "Indonesia", "🇮🇩", "Asia") },
        { "MY", new CountryInfo("MY", "Malaysia", "🇲🇾", "Asia") },
        { "PH", new CountryInfo("PH", "Philippines", "🇵🇭", "Asia") },
        { "AU", new CountryInfo("AU", "Australia", "🇦🇺", "Oceania") },
        { "CA", new CountryInfo("CA", "Canada", "🇨🇦", "America") },
        { "BR", new CountryInfo("BR", "Brazil", "🇧🇷", "America") },
        { "MX", new CountryInfo("MX", "Mexico", "🇲🇽", "America") },
        { "AR", new CountryInfo("AR", "Argentina", "🇦🇷", "America") },
        { "RU", new CountryInfo("RU", "Russia", "🇷🇺", "Europe") },
        { "UA", new CountryInfo("UA", "Ukraine", "🇺🇦", "Europe") },
        { "PL", new CountryInfo("PL", "Poland", "🇵🇱", "Europe") },
        { "IT", new CountryInfo("IT", "Italy", "🇮🇹", "Europe") },
        { "ES", new CountryInfo("ES", "Spain", "🇪🇸", "Europe") },
        { "SE", new CountryInfo("SE", "Sweden", "🇸🇪", "Europe") },
        { "CH", new CountryInfo("CH", "Switzerland", "🇨🇭", "Europe") },
        { "AT", new CountryInfo("AT", "Austria", "🇦🇹", "Europe") },
        { "BE", new CountryInfo("BE", "Belgium", "🇧🇪", "Europe") },
        { "CZ", new CountryInfo("CZ", "Czech Republic", "🇨🇿", "Europe") },
        { "RO", new CountryInfo("RO", "Romania", "🇷🇴", "Europe") },
        { "TR", new CountryInfo("TR", "Turkey", "🇹🇷", "Europe") },
        { "ZA", new CountryInfo("ZA", "South Africa", "🇿🇦", "Africa") },
        { "EG", new CountryInfo("EG", "Egypt", "🇪🇬", "Africa") },
        { "AE", new CountryInfo("AE", "UAE", "🇦🇪", "Middle East") },
        { "IL", new CountryInfo("IL", "Israel", "🇮🇱", "Middle East") },
    };

    public ProxyService()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        // Setup cache file path
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheDir = Path.Combine(appData, "NetX", "Cache");
        Directory.CreateDirectory(cacheDir);
        _cacheFilePath = Path.Combine(cacheDir, "proxy_cache.json");

        // Load cached proxies from disk
        LoadCacheFromDisk();
    }

    public bool IsConnected => _isConnected;
    public ProxyInfo? CurrentProxy => _currentProxy;

    /// <summary>
    /// Get proxies for a specific country - returns cached if available, fetches only if forced
    /// </summary>
    public async Task<List<ProxyInfo>> FetchProxiesAsync(string countryCode, CancellationToken ct = default, bool forceRefresh = false)
    {
        // Return cached proxies if available and not forcing refresh
        if (!forceRefresh && _proxyCache.TryGetValue(countryCode, out var cachedProxies) && cachedProxies.Count > 0)
        {
            OnStatusChanged?.Invoke($"Loaded {cachedProxies.Count} cached proxies for {countryCode}");
            return cachedProxies;
        }

        var proxies = new List<ProxyInfo>();
        OnStatusChanged?.Invoke($"Fetching proxies for {countryCode}...");

        try
        {
            // Fetch from multiple free proxy sources
            var tasks = new[]
            {
                FetchFromProxyScrape(countryCode, ct),
                FetchFromGeoNode(countryCode, ct),
                FetchFromProxyList(countryCode, ct),
            };

            var results = await Task.WhenAll(tasks);
            foreach (var result in results)
            {
                proxies.AddRange(result);
            }

            // Remove duplicates
            proxies = proxies
                .GroupBy(p => $"{p.Host}:{p.Port}")
                .Select(g => g.First())
                .ToList();

            // Mark fetch time
            foreach (var proxy in proxies)
            {
                proxy.LastChecked = DateTime.Now;
            }

            // Cache the results in memory
            _proxyCache[countryCode] = proxies;

            // Save to disk
            SaveCacheToDisk();

            OnStatusChanged?.Invoke($"Found {proxies.Count} proxies for {countryCode}");
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Failed to fetch proxies: {ex.Message}");

            // Return cached if fetch failed
            if (_proxyCache.TryGetValue(countryCode, out var fallbackProxies))
            {
                return fallbackProxies;
            }
        }

        return proxies;
    }

    /// <summary>
    /// Force refresh proxies for a country (clears cache and re-fetches)
    /// </summary>
    public async Task<List<ProxyInfo>> RefreshProxiesAsync(string countryCode, CancellationToken ct = default)
    {
        // Remove from cache
        _proxyCache.TryRemove(countryCode, out _);

        // Fetch fresh
        return await FetchProxiesAsync(countryCode, ct, forceRefresh: true);
    }

    /// <summary>
    /// Refresh all cached proxies
    /// </summary>
    public async Task RefreshAllProxiesAsync(CancellationToken ct = default)
    {
        var countryCodes = _proxyCache.Keys.ToList();
        _proxyCache.Clear();

        foreach (var code in countryCodes)
        {
            ct.ThrowIfCancellationRequested();
            await FetchProxiesAsync(code, ct, forceRefresh: true);
        }

        SaveCacheToDisk();
    }

    private async Task<List<ProxyInfo>> FetchFromProxyScrape(string countryCode, CancellationToken ct)
    {
        var proxies = new List<ProxyInfo>();
        try
        {
            var url = $"https://api.proxyscrape.com/v2/?request=displayproxies&protocol=http&timeout=10000&country={countryCode}&ssl=all&anonymity=all";
            var response = await _httpClient.GetStringAsync(url, ct);

            foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out var port))
                {
                    proxies.Add(new ProxyInfo
                    {
                        Host = parts[0],
                        Port = port,
                        CountryCode = countryCode,
                        Type = ProxyType.HTTP,
                        Source = "ProxyScrape"
                    });
                }
            }
        }
        catch { }
        return proxies;
    }

    private async Task<List<ProxyInfo>> FetchFromGeoNode(string countryCode, CancellationToken ct)
    {
        var proxies = new List<ProxyInfo>();
        try
        {
            var url = $"https://proxylist.geonode.com/api/proxy-list?limit=50&page=1&sort_by=lastChecked&sort_type=desc&country={countryCode}";
            var response = await _httpClient.GetStringAsync(url, ct);

            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    var host = item.GetProperty("ip").GetString();
                    var port = item.GetProperty("port").GetString();
                    var protocols = item.GetProperty("protocols").EnumerateArray()
                        .Select(p => p.GetString()).ToList();

                    if (!string.IsNullOrEmpty(host) && int.TryParse(port, out var portNum))
                    {
                        proxies.Add(new ProxyInfo
                        {
                            Host = host,
                            Port = portNum,
                            CountryCode = countryCode,
                            Type = protocols.Contains("socks5") ? ProxyType.SOCKS5 :
                                   protocols.Contains("socks4") ? ProxyType.SOCKS4 : ProxyType.HTTP,
                            Source = "GeoNode"
                        });
                    }
                }
            }
        }
        catch { }
        return proxies;
    }

    private async Task<List<ProxyInfo>> FetchFromProxyList(string countryCode, CancellationToken ct)
    {
        var proxies = new List<ProxyInfo>();
        try
        {
            // Free proxy list API
            var url = $"https://www.proxy-list.download/api/v1/get?type=http&anon=elite&country={countryCode}";
            var response = await _httpClient.GetStringAsync(url, ct);

            foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out var port))
                {
                    proxies.Add(new ProxyInfo
                    {
                        Host = parts[0],
                        Port = port,
                        CountryCode = countryCode,
                        Type = ProxyType.HTTP,
                        Source = "ProxyList"
                    });
                }
            }
        }
        catch { }
        return proxies;
    }

    /// <summary>
    /// Test if a proxy is working and measure its latency
    /// </summary>
    public async Task<ProxyTestResult> TestProxyAsync(ProxyInfo proxy, CancellationToken ct = default)
    {
        var result = new ProxyTestResult { Proxy = proxy };
        var sw = Stopwatch.StartNew();

        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxy.Host, proxy.Port),
                UseProxy = true
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var response = await client.GetStringAsync("http://ip-api.com/json/", ct);

            sw.Stop();
            result.Latency = (int)sw.ElapsedMilliseconds;
            result.IsWorking = true;

            // Parse response to get actual location
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("countryCode", out var cc))
            {
                result.ActualCountry = cc.GetString() ?? proxy.CountryCode;
            }
            if (doc.RootElement.TryGetProperty("query", out var ip))
            {
                result.ExternalIP = ip.GetString() ?? "";
            }
        }
        catch
        {
            result.IsWorking = false;
            result.Latency = -1;
        }

        return result;
    }

    /// <summary>
    /// Connect to a proxy by setting system-wide proxy settings
    /// </summary>
    public async Task<bool> ConnectAsync(ProxyInfo proxy)
    {
        try
        {
            OnStatusChanged?.Invoke($"Testing proxy {proxy.Host}:{proxy.Port}...");

            // Test proxy first
            var testResult = await TestProxyAsync(proxy);
            if (!testResult.IsWorking)
            {
                OnError?.Invoke("Proxy is not responding");
                return false;
            }

            OnStatusChanged?.Invoke($"Connecting to {proxy.Host}:{proxy.Port}...");

            // Set system proxy
            SetSystemProxy(proxy.Host, proxy.Port, true);

            _currentProxy = proxy;
            _currentProxy.Latency = testResult.Latency;
            _currentProxy.ExternalIP = testResult.ExternalIP;
            _isConnected = true;

            OnStatusChanged?.Invoke($"Connected! External IP: {testResult.ExternalIP}");
            OnProxyConnected?.Invoke(proxy);

            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Connection failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Disconnect from current proxy
    /// </summary>
    public void Disconnect()
    {
        try
        {
            // Disable system proxy
            SetSystemProxy("", 0, false);

            _currentProxy = null;
            _isConnected = false;

            OnStatusChanged?.Invoke("Disconnected");
            OnProxyDisconnected?.Invoke();
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Disconnect failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Set Windows system proxy settings
    /// </summary>
    private void SetSystemProxy(string host, int port, bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);

            if (key != null)
            {
                if (enable)
                {
                    key.SetValue("ProxyServer", $"{host}:{port}");
                    key.SetValue("ProxyEnable", 1);
                }
                else
                {
                    key.SetValue("ProxyEnable", 0);
                }

                // Notify Windows of the change
                RefreshSystemProxy();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set system proxy: {ex.Message}");
            throw;
        }
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll")]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    private void RefreshSystemProxy()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    /// <summary>
    /// Get cached proxies for a country
    /// </summary>
    public List<ProxyInfo> GetCachedProxies(string countryCode)
    {
        return _proxyCache.TryGetValue(countryCode, out var proxies) ? proxies : new List<ProxyInfo>();
    }

    /// <summary>
    /// Check if we have cached proxies for a country
    /// </summary>
    public bool HasCachedProxies(string countryCode)
    {
        return _proxyCache.TryGetValue(countryCode, out var proxies) && proxies.Count > 0;
    }

    /// <summary>
    /// Get count of cached proxies for a country
    /// </summary>
    public int GetCachedProxyCount(string countryCode)
    {
        return _proxyCache.TryGetValue(countryCode, out var proxies) ? proxies.Count : 0;
    }

    #region Cache Persistence

    private void LoadCacheFromDisk()
    {
        try
        {
            if (!File.Exists(_cacheFilePath)) return;

            var json = File.ReadAllText(_cacheFilePath);
            var cache = JsonSerializer.Deserialize<ProxyCacheData>(json);

            if (cache?.Proxies != null)
            {
                foreach (var kvp in cache.Proxies)
                {
                    // Only load if cache is less than 24 hours old
                    var validProxies = kvp.Value
                        .Where(p => (DateTime.Now - p.LastChecked).TotalHours < 24)
                        .ToList();

                    if (validProxies.Count > 0)
                    {
                        _proxyCache[kvp.Key] = validProxies;
                    }
                }

                _lastCacheLoad = cache.LastUpdated;
                Debug.WriteLine($"Loaded proxy cache: {_proxyCache.Values.Sum(l => l.Count)} proxies from {_proxyCache.Count} countries");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load proxy cache: {ex.Message}");
        }
    }

    private void SaveCacheToDisk()
    {
        try
        {
            var cache = new ProxyCacheData
            {
                LastUpdated = DateTime.Now,
                Proxies = _proxyCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            };

            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_cacheFilePath, json);
            Debug.WriteLine($"Saved proxy cache: {_proxyCache.Values.Sum(l => l.Count)} proxies");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save proxy cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear all cached proxies
    /// </summary>
    public void ClearCache()
    {
        _proxyCache.Clear();
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                File.Delete(_cacheFilePath);
            }
        }
        catch { }
    }

    #endregion

    /// <summary>
    /// Find and connect to the best available proxy for a country
    /// </summary>
    public async Task<bool> QuickConnectAsync(string countryCode, CancellationToken ct = default)
    {
        OnStatusChanged?.Invoke($"Finding best proxy for {Countries.GetValueOrDefault(countryCode)?.Name ?? countryCode}...");

        var proxies = await FetchProxiesAsync(countryCode, ct);
        if (proxies.Count == 0)
        {
            OnError?.Invoke("No proxies found for this country");
            return false;
        }

        // Test proxies in parallel and find the fastest working one
        var testTasks = proxies.Take(10).Select(p => TestProxyAsync(p, ct)).ToList();
        var results = await Task.WhenAll(testTasks);

        var bestProxy = results
            .Where(r => r.IsWorking)
            .OrderBy(r => r.Latency)
            .FirstOrDefault();

        if (bestProxy == null)
        {
            OnError?.Invoke("No working proxies found");
            return false;
        }

        return await ConnectAsync(bestProxy.Proxy);
    }

    public void Dispose()
    {
        if (_isConnected)
        {
            Disconnect();
        }
        _httpClient.Dispose();
    }
}

public class ProxyInfo
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string CountryCode { get; set; } = "";
    public ProxyType Type { get; set; } = ProxyType.HTTP;
    public string Source { get; set; } = "";
    public int Latency { get; set; } = -1;
    public string? ExternalIP { get; set; }
    public DateTime LastChecked { get; set; }

    public string DisplayAddress => $"{Host}:{Port}";
    public string DisplayType => Type.ToString();
}

public class ProxyTestResult
{
    public ProxyInfo Proxy { get; set; } = null!;
    public bool IsWorking { get; set; }
    public int Latency { get; set; }
    public string? ActualCountry { get; set; }
    public string? ExternalIP { get; set; }
}

public class CountryInfo
{
    public string Code { get; set; }
    public string Name { get; set; }
    public string Flag { get; set; }
    public string Region { get; set; }

    public CountryInfo(string code, string name, string flag, string region)
    {
        Code = code;
        Name = name;
        Flag = flag;
        Region = region;
    }
}

public enum ProxyType
{
    HTTP,
    HTTPS,
    SOCKS4,
    SOCKS5
}

public class ProxyCacheData
{
    public DateTime LastUpdated { get; set; }
    public Dictionary<string, List<ProxyInfo>> Proxies { get; set; } = new();
}

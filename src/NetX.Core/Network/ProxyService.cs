using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace NetX.Core.Network;

/// <summary>
/// Free public proxies applied as the Windows (WinINET) system proxy.
///
/// This is NOT a VPN: WinXTools encrypts nothing, and only apps that honour the
/// Windows proxy setting (most browsers) are routed through the proxy.
///
/// Safety model:
///  - Before the first change, the user's complete WinINET config (connection flags
///    incl. auto-detect, proxy server, bypass list, PAC URL, plus the raw registry
///    values) is written to %LOCALAPPDATA%\NetX\proxy_backup.json. If that backup
///    cannot be written, nothing is changed.
///  - Disconnect / DisconnectOnExit / RestoreIfLeftOver put that config back and delete
///    the backup. A backup that outlives its process means WinXTools died while
///    connected; RestoreIfLeftOver (app startup) repairs it.
///  - Settings are only restored while Windows still points at a proxy WinXTools applied,
///    so a newer change made by the user or another app is never overwritten.
///  - A proxy is only applied after an HTTPS request through it succeeds (TLS certificate
///    validated, so an intercepting proxy fails the test).
/// </summary>
public class ProxyService : IDisposable
{
    private static ProxyService? _instance;
    public static ProxyService Instance => _instance ??= new ProxyService();

    // Testing / fetching limits
    private const int MaxParallelTests = 24;
    private const int MaxCandidatesPerRound = 160;
    private const int MaxParallelCountryFetches = 4;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TestConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ListFreshFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FailedProxyCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(60);
    private const int HealthFailuresBeforeWarning = 2;

    // HTTPS endpoints that echo the caller's public IP and country
    private const string TraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";
    private const string IpInfoUrl = "https://ipinfo.io/json";

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, List<ProxyInfo>> _proxyCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _recentFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly object _cacheFileLock = new();
    private readonly string _cacheFilePath;
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _healthCts;

    // Connection state. Guarded by _stateLock; when both are needed SystemProxyLock is taken first.
    private readonly object _stateLock = new();
    private ProxyInfo? _currentProxy;
    private string? _exitCountry;
    private string? _exitIp;
    private int _latency = -1;
    private bool _isHealthy = true;

    /// <summary>
    /// Raised on any thread when the connection state changes (connected, disconnected,
    /// proxy stopped answering). Read <see cref="Status"/> for the new state.
    /// </summary>
    public event Action? OnStateChanged;

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
        // Proxy lists are always fetched directly, never through the proxy we may have applied.
        var handler = new HttpClientHandler
        {
            UseProxy = false
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

    /// <summary>Snapshot of the current connection, safe to read from any thread.</summary>
    public ProxyConnectionStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return new ProxyConnectionStatus
                {
                    IsConnected = _currentProxy != null,
                    Proxy = _currentProxy,
                    ExitCountry = _exitCountry,
                    ExitIp = _exitIp,
                    LatencyMs = _latency,
                    IsHealthy = _isHealthy,
                    // Not connected, yet this process still holds a backup: a restore failed.
                    RestorePending = _currentProxy == null && Volatile.Read(ref _activeBackup) != null
                };
            }
        }
    }

    public bool IsConnected
    {
        get { lock (_stateLock) return _currentProxy != null; }
    }

    public ProxyInfo? CurrentProxy
    {
        get { lock (_stateLock) return _currentProxy; }
    }

    #region Proxy lists

    /// <summary>
    /// Proxy list for a country - returns the cached list unless <paramref name="forceRefresh"/> is set
    /// or nothing is cached. Lists are untested: a listed proxy is not necessarily a working one.
    /// </summary>
    public async Task<ProxyFetchResult> FetchProxiesAsync(string countryCode, CancellationToken ct = default, bool forceRefresh = false)
    {
        if (!forceRefresh && _proxyCache.TryGetValue(countryCode, out var cachedProxies) && cachedProxies.Count > 0)
        {
            return ProxyFetchResult.From(cachedProxies, allSourcesFailed: false);
        }

        var result = await DownloadCountryListAsync(countryCode, ct).ConfigureAwait(false);
        if (!result.AllSourcesFailed)
        {
            SaveCacheToDisk();
        }
        return result;
    }

    /// <summary>
    /// Re-download the lists of many countries, a few at a time, reporting each country as it
    /// finishes. Cancellable; countries finished before cancellation keep their new list.
    /// Returns how many countries were downloaded from at least one source.
    /// </summary>
    public async Task<int> RefreshAllProxiesAsync(IReadOnlyList<string> countryCodes,
        IProgress<(string Code, ProxyFetchResult Result)>? progress = null, CancellationToken ct = default)
    {
        int refreshed = 0;
        try
        {
            await Parallel.ForEachAsync(countryCodes,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelCountryFetches, CancellationToken = ct },
                async (code, token) =>
                {
                    var result = await DownloadCountryListAsync(code, token).ConfigureAwait(false);
                    if (!result.AllSourcesFailed)
                    {
                        Interlocked.Increment(ref refreshed);
                    }
                    progress?.Report((code, result));
                }).ConfigureAwait(false);
        }
        finally
        {
            // Persist whatever finished, even when the user cancelled half-way.
            SaveCacheToDisk();
        }

        return refreshed;
    }

    private async Task<ProxyFetchResult> DownloadCountryListAsync(string countryCode, CancellationToken ct)
    {
        // Fetch from multiple free proxy sources
        var results = await Task.WhenAll(
            FetchFromProxyScrape(countryCode, ct),
            FetchFromGeoNode(countryCode, ct),
            FetchFromProxyList(countryCode, ct)).ConfigureAwait(false);

        // A cancelled refresh must not replace a good cached list with a partial one.
        ct.ThrowIfCancellationRequested();

        if (results.All(r => r == null))
        {
            // Every source failed (offline, blocked, API down): keep what we had.
            return ProxyFetchResult.From(GetCachedProxies(countryCode), allSourcesFailed: true);
        }

        // Remove duplicates
        var proxies = results
            .Where(r => r != null)
            .SelectMany(r => r!)
            .GroupBy(p => p.DisplayAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Remember proxies that already worked so they are tried first next time.
        var knownGood = GetCachedProxies(countryCode)
            .Where(p => p.Latency > 0)
            .GroupBy(p => p.DisplayAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Latency, StringComparer.OrdinalIgnoreCase);

        var listedAt = DateTime.Now;
        foreach (var proxy in proxies)
        {
            // Time the list was downloaded - not a liveness check.
            proxy.LastChecked = listedAt;
            if (knownGood.TryGetValue(proxy.DisplayAddress, out var latency))
            {
                proxy.Latency = latency;
            }
        }

        _proxyCache[countryCode] = proxies;
        return ProxyFetchResult.From(proxies, allSourcesFailed: false);
    }

    // Each source returns null when it failed, an empty list when it answered with nothing.
    private async Task<List<ProxyInfo>?> FetchFromProxyScrape(string countryCode, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.proxyscrape.com/v2/?request=displayproxies&protocol=http&timeout=10000&country={Uri.EscapeDataString(countryCode)}&ssl=all&anonymity=all";
            var response = await _httpClient.GetStringAsync(url, ct).ConfigureAwait(false);
            return ParseHostPortLines(response, countryCode, "ProxyScrape");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ProxyScrape fetch failed for {countryCode}: {ex.Message}");
            return null;
        }
    }

    private async Task<List<ProxyInfo>?> FetchFromGeoNode(string countryCode, CancellationToken ct)
    {
        try
        {
            // HTTP(S) proxies only: SOCKS cannot be used as the Windows system proxy.
            var url = $"https://proxylist.geonode.com/api/proxy-list?limit=100&page=1&sort_by=lastChecked&sort_type=desc&protocols=http%2Chttps&country={Uri.EscapeDataString(countryCode)}";
            var response = await _httpClient.GetStringAsync(url, ct).ConfigureAwait(false);

            var proxies = new List<ProxyInfo>();
            using var doc = JsonDocument.Parse(response);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return proxies;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("ip", out var ipElement) || ipElement.ValueKind != JsonValueKind.String) continue;
                if (!item.TryGetProperty("port", out var portElement)) continue;

                var port = portElement.ValueKind switch
                {
                    JsonValueKind.Number when portElement.TryGetInt32(out var n) => n,
                    JsonValueKind.String when int.TryParse(portElement.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) => s,
                    _ => 0
                };

                if (item.TryGetProperty("protocols", out var protocols) && protocols.ValueKind == JsonValueKind.Array
                    && !protocols.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() is "http" or "https"))
                {
                    continue;
                }

                if (TryCreateProxy(ipElement.GetString() ?? "", port, countryCode, "GeoNode", out var proxy))
                {
                    proxies.Add(proxy);
                }
            }
            return proxies;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GeoNode fetch failed for {countryCode}: {ex.Message}");
            return null;
        }
    }

    private async Task<List<ProxyInfo>?> FetchFromProxyList(string countryCode, CancellationToken ct)
    {
        try
        {
            // Free proxy list API
            var url = $"https://www.proxy-list.download/api/v1/get?type=http&anon=elite&country={Uri.EscapeDataString(countryCode)}";
            var response = await _httpClient.GetStringAsync(url, ct).ConfigureAwait(false);
            return ParseHostPortLines(response, countryCode, "ProxyList");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ProxyList fetch failed for {countryCode}: {ex.Message}");
            return null;
        }
    }

    private static List<ProxyInfo> ParseHostPortLines(string text, string countryCode, string source)
    {
        var proxies = new List<ProxyInfo>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                && TryCreateProxy(parts[0], port, countryCode, source, out var proxy))
            {
                proxies.Add(proxy);
            }
        }
        return proxies;
    }

    /// <summary>
    /// List entries are untrusted input that can end up in the Windows proxy setting, so only a
    /// public IPv4 literal and a valid port are accepted (stored in canonical form).
    /// </summary>
    private static bool TryCreateProxy(string host, int port, string countryCode, string source, out ProxyInfo proxy)
    {
        proxy = null!;
        if (port is < 1 or > 65535) return false;
        if (!IPAddress.TryParse(host.Trim(), out var ip) || !IsPublicIPv4(ip)) return false;

        proxy = new ProxyInfo
        {
            Host = ip.ToString(),
            Port = port,
            CountryCode = countryCode,
            Type = ProxyType.HTTP,
            Source = source
        };
        return true;
    }

    private static bool IsPublicIPv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // carrier-grade NAT
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                 || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>
    /// True if the proxy can be set as the Windows system proxy: HTTP type, canonical public IPv4,
    /// valid port. Also guards entries loaded from the (user-writable) cache file.
    /// </summary>
    internal static bool IsUsable(ProxyInfo proxy) =>
        proxy.Type is ProxyType.HTTP or ProxyType.HTTPS
        && proxy.Port is >= 1 and <= 65535
        && IPAddress.TryParse(proxy.Host, out var ip)
        && IsPublicIPv4(ip)
        && string.Equals(ip.ToString(), proxy.Host, StringComparison.Ordinal);

    #endregion

    #region Testing

    /// <summary>
    /// Test a proxy the way a browser uses it for HTTPS: CONNECT tunnel + TLS with full certificate
    /// validation. Reports latency, exit IP and the exit country seen by the server.
    /// Only throws if <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<ProxyTestResult> TestProxyAsync(ProxyInfo proxy, CancellationToken ct = default)
    {
        var result = new ProxyTestResult { Proxy = proxy, Latency = -1 };
        if (!IsUsable(proxy)) return result;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TestTimeout);
        var sw = Stopwatch.StartNew();

        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(proxy.Host, proxy.Port),
                UseProxy = true,
                ConnectTimeout = TestConnectTimeout,
                AllowAutoRedirect = false,
                UseCookies = false
            };
            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan,
                MaxResponseContentBufferSize = 64 * 1024
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WinXTools-ProxyCheck/1.0");

            var (ip, country, reachedServer) = await QueryTraceAsync(client, timeout.Token).ConfigureAwait(false);
            if (ip == null && reachedServer)
            {
                // The tunnel works but the first endpoint misbehaved: ask a second one.
                (ip, country) = await QueryIpInfoAsync(client, timeout.Token).ConfigureAwait(false);
            }

            if (ip != null)
            {
                result.IsWorking = true;
                result.Latency = (int)sw.ElapsedMilliseconds;
                result.ExternalIP = ip;
                result.ActualCountry = country;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Dead, too slow, refused CONNECT, or failed TLS validation (intercepting proxy).
        }

        return result;
    }

    private static async Task<(string? Ip, string? Country, bool ReachedServer)> QueryTraceAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync(TraceUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return (null, null, true);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? ip = null, country = null;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("ip=", StringComparison.Ordinal)) ip = trimmed[3..];
            else if (trimmed.StartsWith("loc=", StringComparison.Ordinal)) country = trimmed[4..];
        }
        return (NormalizeIp(ip), NormalizeCountry(country), true);
    }

    private static async Task<(string? Ip, string? Country)> QueryIpInfoAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync(IpInfoUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return (null, null);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var ip = root.TryGetProperty("ip", out var ipElement) && ipElement.ValueKind == JsonValueKind.String
            ? ipElement.GetString() : null;
        var country = root.TryGetProperty("country", out var countryElement) && countryElement.ValueKind == JsonValueKind.String
            ? countryElement.GetString() : null;
        return (NormalizeIp(ip), NormalizeCountry(country));
    }

    private static string? NormalizeIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip.Trim(), out var parsed) ? parsed.ToString() : null;

    /// <summary>ISO 3166 alpha-2 code in upper case, or null when unknown ("XX", Tor "T1", garbage).</summary>
    private static string? NormalizeCountry(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim().ToUpperInvariant();
        return code.Length == 2 && char.IsAsciiLetterUpper(code[0]) && char.IsAsciiLetterUpper(code[1]) && code != "XX"
            ? code
            : null;
    }

    /// <summary>
    /// Order candidates: proxies that worked before first, recently failed ones last, sources
    /// interleaved so one long list cannot crowd out the others. Capped per round.
    /// </summary>
    private List<ProxyInfo> SelectCandidates(IEnumerable<ProxyInfo> listed, HashSet<string> alreadyTested)
    {
        var now = DateTime.UtcNow;
        bool FailedRecently(ProxyInfo p) =>
            _recentFailures.TryGetValue(p.DisplayAddress, out var at) && now - at < FailedProxyCooldown;

        var usable = listed
            .Where(p => IsUsable(p) && !alreadyTested.Contains(p.DisplayAddress))
            .GroupBy(p => p.DisplayAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var queues = usable.GroupBy(p => p.Source).Select(g => new Queue<ProxyInfo>(g)).ToList();
        var interleaved = new List<ProxyInfo>(usable.Count);
        while (queues.Any(q => q.Count > 0))
        {
            foreach (var queue in queues)
            {
                if (queue.Count > 0) interleaved.Add(queue.Dequeue());
            }
        }

        // OrderBy is stable, so the interleaving is kept inside each group.
        return interleaved
            .OrderBy(p => p.Latency > 0 ? 0 : FailedRecently(p) ? 2 : 1)
            .Take(MaxCandidatesPerRound)
            .ToList();
    }

    /// <summary>
    /// Test candidates in parallel (bounded) and stop as soon as one exits in the requested country.
    /// Working proxies that exit elsewhere are kept as a fallback the user can choose.
    /// </summary>
    private async Task<(ProxyTestResult? Match, ProxyTestResult? Alternative)> FindWorkingProxyAsync(
        List<ProxyInfo> candidates, string countryCode, IProgress<ProxyProgress>? progress, CancellationToken ct)
    {
        ProxyTestResult? match = null;
        var others = new ConcurrentBag<ProxyTestResult>();
        int done = 0;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        progress?.Report(new ProxyProgress(ProxyProgressStage.Testing, 0, candidates.Count));

        try
        {
            await Parallel.ForEachAsync(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelTests, CancellationToken = stop.Token },
                async (proxy, token) =>
                {
                    var result = await TestProxyAsync(proxy, token).ConfigureAwait(false);
                    progress?.Report(new ProxyProgress(ProxyProgressStage.Testing, Interlocked.Increment(ref done), candidates.Count));

                    if (!result.IsWorking)
                    {
                        proxy.Latency = -1;
                        _recentFailures[proxy.DisplayAddress] = DateTime.UtcNow;
                        return;
                    }

                    proxy.Latency = result.Latency;
                    _recentFailures.TryRemove(proxy.DisplayAddress, out _);

                    if (string.Equals(result.ActualCountry, countryCode, StringComparison.OrdinalIgnoreCase))
                    {
                        if (Interlocked.CompareExchange(ref match, result, null) == null)
                        {
                            stop.Cancel();
                        }
                    }
                    else
                    {
                        others.Add(result);
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Stopped early: a proxy in the requested country answered.
        }

        return (match, others.OrderBy(r => r.Latency).FirstOrDefault());
    }

    #endregion

    #region Connect / Disconnect

    /// <summary>
    /// Find a proxy that works over HTTPS and really exits in <paramref name="countryCode"/>, then set
    /// it as the Windows proxy. Uses the cached list when it is fresh, otherwise (or when nothing in
    /// the cache works) downloads a new one. Throws OperationCanceledException when cancelled -
    /// in that case the system proxy was not changed by this call.
    /// </summary>
    public async Task<ProxyConnectResult> QuickConnectAsync(string countryCode, IProgress<ProxyProgress>? progress = null, CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        var linked = BeginConnect(ct);

        try
        {
            if (Volatile.Read(ref _exiting)) return new ProxyConnectResult { Status = ProxyConnectStatus.AppClosing };
            if (IsSystemProxyOwnedByOtherInstance()) return new ProxyConnectResult { Status = ProxyConnectStatus.OwnedByOtherInstance };

            var token = linked.Token;
            var tested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ProxyTestResult? alternative = null;
            bool listUnavailable = false;

            for (int round = 0; round < 2; round++)
            {
                List<ProxyInfo> listed;
                bool freshList;

                if (round == 0 && IsListFresh(countryCode))
                {
                    listed = GetCachedProxies(countryCode);
                    freshList = false;
                }
                else
                {
                    progress?.Report(new ProxyProgress(ProxyProgressStage.FetchingList, 0, 0));
                    var fetched = await FetchProxiesAsync(countryCode, token, forceRefresh: true).ConfigureAwait(false);
                    listUnavailable = fetched.AllSourcesFailed;
                    listed = fetched.Proxies;
                    freshList = true;
                }

                var candidates = SelectCandidates(listed, tested);
                if (candidates.Count > 0)
                {
                    var (match, other) = await FindWorkingProxyAsync(candidates, countryCode, progress, token).ConfigureAwait(false);
                    foreach (var candidate in candidates) tested.Add(candidate.DisplayAddress);

                    if (match != null)
                    {
                        return ApplyTested(match, progress, token);
                    }
                    if (other != null && (alternative == null || other.Latency < alternative.Latency))
                    {
                        alternative = other;
                    }
                }

                // A cached list that yielded nothing gets one retry with a freshly downloaded list.
                if (freshList) break;
            }

            if (alternative != null)
            {
                return new ProxyConnectResult { Status = ProxyConnectStatus.OnlyOtherCountry, Proxy = alternative, TestedCount = tested.Count };
            }
            if (tested.Count > 0)
            {
                return new ProxyConnectResult { Status = ProxyConnectStatus.NoWorkingProxy, TestedCount = tested.Count };
            }
            return new ProxyConnectResult { Status = listUnavailable ? ProxyConnectStatus.ListUnavailable : ProxyConnectStatus.NoProxiesListed };
        }
        finally
        {
            EndConnect(linked);
        }
    }

    /// <summary>
    /// Re-test one specific proxy over HTTPS and apply it (used when the user accepts a proxy that
    /// exits in a different country). Throws OperationCanceledException when cancelled.
    /// </summary>
    public async Task<ProxyConnectResult> ConnectAsync(ProxyInfo proxy, IProgress<ProxyProgress>? progress = null, CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        var linked = BeginConnect(ct);

        try
        {
            if (Volatile.Read(ref _exiting)) return new ProxyConnectResult { Status = ProxyConnectStatus.AppClosing };

            // Proxies can die within seconds, and the user may have waited on a dialog.
            var result = await TestProxyAsync(proxy, linked.Token).ConfigureAwait(false);
            if (!result.IsWorking)
            {
                _recentFailures[proxy.DisplayAddress] = DateTime.UtcNow;
                return new ProxyConnectResult { Status = ProxyConnectStatus.NoWorkingProxy, TestedCount = 1 };
            }

            return ApplyTested(result, progress, linked.Token);
        }
        finally
        {
            EndConnect(linked);
        }
    }

    /// <summary>Caller holds <see cref="_connectGate"/>. On failure the gate is released here.</summary>
    private CancellationTokenSource BeginConnect(CancellationToken ct)
    {
        try
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Volatile.Write(ref _connectCts, linked);   // lets Disconnect / DisconnectOnExit abort it
            return linked;
        }
        catch
        {
            _connectGate.Release();
            throw;
        }
    }

    private void EndConnect(CancellationTokenSource linked)
    {
        Interlocked.CompareExchange(ref _connectCts, null, linked);
        linked.Dispose();
        _connectGate.Release();
    }

    private ProxyConnectResult ApplyTested(ProxyTestResult tested, IProgress<ProxyProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(new ProxyProgress(ProxyProgressStage.Applying, 0, 0));

        var status = ApplySystemProxy(tested, ct);
        if (status == ProxyConnectStatus.Connected)
        {
            tested.Proxy.Latency = tested.Latency;
            tested.Proxy.ExternalIP = tested.ExternalIP;
            StartHealthMonitor(tested.Proxy);
        }

        RaiseStateChanged();
        return new ProxyConnectResult { Status = status, Proxy = tested, TestedCount = 1 };
    }

    /// <summary>
    /// Put the user's original Windows proxy settings back (exactly as backed up) and delete the
    /// backup. Safe to call when not connected: it then only repairs a left-over from a crash.
    /// </summary>
    public ProxyRestoreResult Disconnect()
    {
        CancelInFlightConnect();

        ProxyRestoreResult result;
        lock (SystemProxyLock)
        {
            result = RestoreCore();
            if (result is not (ProxyRestoreResult.Failed or ProxyRestoreResult.InUseByOtherInstance))
            {
                ClearConnectionState();
            }
        }

        RaiseStateChanged();
        return result;
    }

    /// <summary>
    /// Call when WinXTools is closing (main window Closed, App exit, session ending, fatal crash).
    /// Synchronous, fast and never throws. Restores the user's original proxy settings if this
    /// process changed them, and guarantees no in-flight connect can apply a proxy afterwards.
    /// </summary>
    public static void DisconnectOnExit()
    {
        try
        {
            var service = Volatile.Read(ref _instance);
            service?.CancelInFlightConnect();
            service?.StopHealthMonitor();

            lock (SystemProxyLock)
            {
                _exiting = true;
                if (_activeBackup == null && !File.Exists(BackupFilePath)) return;

                var result = RestoreCore();
                if (result is not (ProxyRestoreResult.Failed or ProxyRestoreResult.InUseByOtherInstance))
                {
                    service?.ClearConnectionState();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Proxy restore on exit failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Call once at app startup. If a proxy backup exists whose owner process is gone, WinXTools died
    /// while connected: put the user's original proxy settings back. Synchronous, fast, never throws.
    /// </summary>
    public static ProxyRestoreResult RestoreIfLeftOver()
    {
        try
        {
            lock (SystemProxyLock)
            {
                var backup = _activeBackup ?? LoadBackupFile();
                if (backup == null) return ProxyRestoreResult.NothingToRestore;
                if (IsOwnedByThisProcess(backup)) return ProxyRestoreResult.InUseByThisSession;
                if (IsOwnerAlive(backup)) return ProxyRestoreResult.InUseByOtherInstance;

                return RestoreCore();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Proxy left-over restore failed: {ex.Message}");
            return ProxyRestoreResult.Failed;
        }
    }

    private void CancelInFlightConnect()
    {
        try
        {
            Volatile.Read(ref _connectCts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The connect just finished.
        }
    }

    private void ClearConnectionState()
    {
        lock (_stateLock)
        {
            _currentProxy = null;
            _exitCountry = null;
            _exitIp = null;
            _latency = -1;
            _isHealthy = true;
        }
        StopHealthMonitor();
    }

    private void RaiseStateChanged()
    {
        try
        {
            OnStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Proxy state subscriber failed: {ex.Message}");
        }
    }

    #endregion

    #region Health monitor

    /// <summary>
    /// While connected, re-test the proxy every minute. After two failures in a row the state is
    /// flagged unhealthy (the UI warns); nothing is switched automatically, so traffic never
    /// silently falls back to a direct connection.
    /// </summary>
    private void StartHealthMonitor(ProxyInfo proxy)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _healthCts, cts)?.Cancel();
        _ = Task.Run(() => HealthLoopAsync(proxy, cts.Token));
    }

    private void StopHealthMonitor()
    {
        Interlocked.Exchange(ref _healthCts, null)?.Cancel();
    }

    private async Task HealthLoopAsync(ProxyInfo proxy, CancellationToken ct)
    {
        int failures = 0;
        try
        {
            using var timer = new PeriodicTimer(HealthCheckInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var result = await TestProxyAsync(proxy, ct).ConfigureAwait(false);
                failures = result.IsWorking ? 0 : failures + 1;
                var healthy = failures < HealthFailuresBeforeWarning;

                bool changed;
                lock (_stateLock)
                {
                    // Switched or disconnected meanwhile
                    if (!ReferenceEquals(_currentProxy, proxy)) return;
                    changed = _isHealthy != healthy;
                    _isHealthy = healthy;
                }

                if (changed) RaiseStateChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnected / switched / exiting
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Proxy health check stopped: {ex.Message}");
        }
    }

    #endregion

    #region System proxy: backup, apply, restore (process-wide)

    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string ConnectionsKey = InternetSettingsKey + @"\Connections";
    private const string ConnectionBlobName = "DefaultConnectionSettings";
    private static readonly string[] LegacyValueNames = { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL", "AutoDetect" };

    private static readonly string BackupFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetX", "proxy_backup.json");

    // Serialises every read-modify-write of the Windows proxy settings and of the backup file.
    private static readonly object SystemProxyLock = new();
    private static ProxyBackup? _activeBackup;   // the backup owned by this process (mirrors the file)
    private static bool _exiting;                 // set by DisconnectOnExit: never apply a proxy after it
    private static readonly int CurrentPid = Environment.ProcessId;
    private static readonly DateTime CurrentStartUtc = GetProcessStartUtc();

    // Traffic to these never goes through the proxy: the LAN (router/NAS pages by name or IP) and loopback.
    private static readonly string[] LocalBypassEntries = BuildLocalBypassEntries();

    private static string[] BuildLocalBypassEntries()
    {
        var entries = new List<string> { "<local>", "localhost", "127.*", "10.*", "192.168.*", "169.254.*" };
        for (int i = 16; i <= 31; i++)
        {
            entries.Add($"172.{i}.*");
        }
        return entries.ToArray();
    }

    /// <summary>Local/LAN entries plus the user's own bypass list (e.g. corporate hosts), de-duplicated.</summary>
    private static string BuildBypassList(string? originalBypass)
    {
        var entries = new List<string>(LocalBypassEntries);
        if (!string.IsNullOrWhiteSpace(originalBypass))
        {
            foreach (var entry in originalBypass.Split(new[] { ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!entries.Contains(entry, StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add(entry);
                }
            }
        }
        return string.Join(";", entries);
    }

    /// <summary>
    /// Back up (first change only), then set the tested proxy as the Windows proxy and read it back.
    /// While connected, PAC and auto-detect are off so browsers really use the proxy.
    /// </summary>
    private ProxyConnectStatus ApplySystemProxy(ProxyTestResult tested, CancellationToken ct)
    {
        var server = tested.Proxy.DisplayAddress;
        if (!IsUsable(tested.Proxy)) return ProxyConnectStatus.ApplyFailed;

        lock (SystemProxyLock)
        {
            if (_exiting) return ProxyConnectStatus.AppClosing;
            ct.ThrowIfCancellationRequested();

            var backup = _activeBackup ?? LoadBackupFile();
            if (backup != null && !IsOwnedByThisProcess(backup))
            {
                if (IsOwnerAlive(backup)) return ProxyConnectStatus.OwnedByOtherInstance;

                // A crashed session left its proxy behind: give the user their settings back first,
                // so the new backup records their real config and not the stale proxy.
                if (RestoreCore() == ProxyRestoreResult.Failed) return ProxyConnectStatus.ApplyFailed;
                backup = null;
            }

            if (backup == null)
            {
                backup = CaptureCurrentSettings();
                if (backup == null) return ProxyConnectStatus.ApplyFailed;
            }

            if (!backup.AppliedServers.Contains(server, StringComparer.OrdinalIgnoreCase))
            {
                backup.AppliedServers.Add(server);
            }

            // Never touch the system proxy without a backup on disk that survives a crash.
            if (!SaveBackupFile(backup)) return ProxyConnectStatus.BackupFailed;
            _activeBackup = backup;

            var applied = WinInet.Set(WinInet.PROXY_TYPE_DIRECT | WinInet.PROXY_TYPE_PROXY,
                server, BuildBypassList(backup.ProxyBypass), null, includeAutoConfigUrl: false);
            WinInet.NotifySettingsChanged();

            if (applied)
            {
                // Group Policy can silently override the per-user setting - trust only what reads back.
                var (readOk, now) = ReadCurrentProxyServer();
                applied = readOk && string.Equals(now, server, StringComparison.OrdinalIgnoreCase);
            }

            if (!applied)
            {
                // Leave the machine exactly as the user had it.
                RestoreCore();
                ClearConnectionState();
                return ProxyConnectStatus.ApplyFailed;
            }

            lock (_stateLock)
            {
                _currentProxy = tested.Proxy;
                _exitCountry = tested.ActualCountry;
                _exitIp = tested.ExternalIP;
                _latency = tested.Latency;
                _isHealthy = true;
            }
            return ProxyConnectStatus.Connected;
        }
    }

    /// <summary>Caller must hold <see cref="SystemProxyLock"/>.</summary>
    private static ProxyRestoreResult RestoreCore()
    {
        var backup = _activeBackup ?? LoadBackupFile();
        if (backup == null) return ProxyRestoreResult.NothingToRestore;
        if (!IsOwnedByThisProcess(backup) && IsOwnerAlive(backup)) return ProxyRestoreResult.InUseByOtherInstance;

        var (readOk, currentServer) = ReadCurrentProxyServer();
        var stillOurs = currentServer != null
            && backup.AppliedServers.Any(s => string.Equals(s, currentServer, StringComparison.OrdinalIgnoreCase));

        if (readOk && !stillOurs)
        {
            // The user or another app changed the proxy after us: their newer setting wins.
            ForgetBackup();
            return backup.AppliedServers.Count == 0 ? ProxyRestoreResult.NothingToRestore : ProxyRestoreResult.ChangedElsewhere;
        }

        if (!WriteOriginalSettings(backup))
        {
            // Keep the backup so the next attempt (or next start) can still restore.
            return ProxyRestoreResult.Failed;
        }

        ForgetBackup();
        return ProxyRestoreResult.Restored;
    }

    private static bool WriteOriginalSettings(ProxyBackup backup)
    {
        var restored = backup.HasOptions
            && WinInet.Set(backup.Flags, backup.ProxyServer, backup.ProxyBypass, backup.AutoConfigUrl, includeAutoConfigUrl: true);

        if (!restored)
        {
            // Fallback: the raw registry values and connection blob captured before the change.
            restored = RestoreRawRegistry(backup);
        }

        WinInet.NotifySettingsChanged();
        return restored;
    }

    private static ProxyBackup? CaptureCurrentSettings()
    {
        // Without a readable current config there is nothing we could promise to restore.
        var options = WinInet.Query();
        if (options == null) return null;

        var backup = new ProxyBackup
        {
            CreatedUtc = DateTime.UtcNow,
            OwnerPid = CurrentPid,
            OwnerStartUtc = CurrentStartUtc,
            HasOptions = true,
            Flags = options.Flags,
            ProxyServer = options.ProxyServer,
            ProxyBypass = options.ProxyBypass,
            AutoConfigUrl = options.AutoConfigUrl
        };
        CaptureRawRegistry(backup);
        return backup;
    }

    private static (bool Ok, string? Server) ReadCurrentProxyServer()
    {
        var options = WinInet.Query();
        if (options != null) return (true, options.ProxyServer?.Trim());

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
            return (key != null, (key?.GetValue("ProxyServer") as string)?.Trim());
        }
        catch
        {
            return (false, null);
        }
    }

    private static bool IsSystemProxyOwnedByOtherInstance()
    {
        lock (SystemProxyLock)
        {
            var backup = _activeBackup ?? LoadBackupFile();
            return backup != null && !IsOwnedByThisProcess(backup) && IsOwnerAlive(backup);
        }
    }

    private static bool IsOwnedByThisProcess(ProxyBackup backup) =>
        backup.OwnerPid == CurrentPid && Math.Abs((backup.OwnerStartUtc - CurrentStartUtc).TotalSeconds) < 2;

    /// <summary>True if the WinXTools process that wrote the backup is still running (PID + start time).</summary>
    private static bool IsOwnerAlive(ProxyBackup backup)
    {
        if (IsOwnedByThisProcess(backup)) return true;
        if (backup.OwnerPid <= 0 || backup.OwnerPid == CurrentPid) return false;

        try
        {
            using var process = Process.GetProcessById(backup.OwnerPid);
            if (process.HasExited) return false;
            return Math.Abs((process.StartTime.ToUniversalTime() - backup.OwnerStartUtc).TotalSeconds) < 2;
        }
        catch
        {
            // Not running, or a reused PID we cannot inspect (not ours).
            return false;
        }
    }

    private static DateTime GetProcessStartUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static void CaptureRawRegistry(ProxyBackup backup)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false))
            {
                foreach (var name in LegacyValueNames)
                {
                    var saved = new ProxyRegistryValue { Name = name };
                    var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (key != null && value != null)
                    {
                        saved.Existed = true;
                        saved.Kind = key.GetValueKind(name).ToString();
                        saved.Value = value switch
                        {
                            int i => i.ToString(CultureInfo.InvariantCulture),
                            long l => l.ToString(CultureInfo.InvariantCulture),
                            byte[] bytes => Convert.ToBase64String(bytes),
                            _ => value.ToString()
                        };
                    }
                    backup.Registry.Add(saved);
                }
            }

            using var connections = Registry.CurrentUser.OpenSubKey(ConnectionsKey, writable: false);
            if (connections?.GetValue(ConnectionBlobName) is byte[] blob)
            {
                backup.ConnectionSettings = Convert.ToBase64String(blob);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Proxy registry snapshot failed: {ex.Message}");
        }
    }

    /// <summary>Fallback restore. Only ever writes the known proxy values - the backup file is user-writable.</summary>
    private static bool RestoreRawRegistry(ProxyBackup backup)
    {
        if (backup.Registry.Count == 0) return false;

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true))
            {
                foreach (var saved in backup.Registry)
                {
                    if (!LegacyValueNames.Contains(saved.Name, StringComparer.OrdinalIgnoreCase)) continue;

                    if (!saved.Existed)
                    {
                        key.DeleteValue(saved.Name, throwOnMissingValue: false);
                        continue;
                    }

                    if (!Enum.TryParse<RegistryValueKind>(saved.Kind, out var kind)) continue;
                    object? value = kind switch
                    {
                        RegistryValueKind.DWord when int.TryParse(saved.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
                        RegistryValueKind.QWord when long.TryParse(saved.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
                        RegistryValueKind.String or RegistryValueKind.ExpandString => saved.Value ?? "",
                        RegistryValueKind.Binary => TryFromBase64(saved.Value),
                        _ => null
                    };
                    if (value != null)
                    {
                        key.SetValue(saved.Name, value, kind);
                    }
                }
            }

            if (TryFromBase64(backup.ConnectionSettings) is { } blob)
            {
                using var connections = Registry.CurrentUser.CreateSubKey(ConnectionsKey, writable: true);
                connections.SetValue(ConnectionBlobName, blob, RegistryValueKind.Binary);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Proxy registry restore failed: {ex.Message}");
            return false;
        }
    }

    private static byte[]? TryFromBase64(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ProxyBackup? LoadBackupFile()
    {
        try
        {
            if (!File.Exists(BackupFilePath)) return null;
            return JsonSerializer.Deserialize<ProxyBackup>(File.ReadAllBytes(BackupFilePath));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read proxy backup: {ex.Message}");
            return null;
        }
    }

    /// <summary>Atomic, flushed write: a crash right after this still leaves a complete backup.</summary>
    private static bool SaveBackupFile(ProxyBackup backup)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BackupFilePath)!);
            var tempPath = BackupFilePath + ".tmp";
            var json = JsonSerializer.SerializeToUtf8Bytes(backup, new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, BackupFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save proxy backup: {ex.Message}");
            return false;
        }
    }

    private static void ForgetBackup()
    {
        _activeBackup = null;
        try
        {
            if (File.Exists(BackupFilePath))
            {
                File.Delete(BackupFilePath);
            }
        }
        catch (Exception ex)
        {
            // Harmless: a stale backup is discarded later because the proxy is no longer ours.
            Debug.WriteLine($"Failed to delete proxy backup: {ex.Message}");
        }
    }

    #endregion

    #region WinINET per-connection options

    /// <summary>
    /// The documented way to read/write the per-user proxy config (KB226473). Unlike raw registry
    /// writes this also updates the connection blob, which is where auto-detect lives.
    /// </summary>
    private static class WinInet
    {
        private const int INTERNET_OPTION_REFRESH = 37;
        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;
        private const int INTERNET_OPTION_PROXY_SETTINGS_CHANGED = 95;

        private const int INTERNET_PER_CONN_FLAGS = 1;
        private const int INTERNET_PER_CONN_PROXY_SERVER = 2;
        private const int INTERNET_PER_CONN_PROXY_BYPASS = 3;
        private const int INTERNET_PER_CONN_AUTOCONFIG_URL = 4;
        private const int INTERNET_PER_CONN_FLAGS_UI = 10;

        public const int PROXY_TYPE_DIRECT = 0x1;
        public const int PROXY_TYPE_PROXY = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        private struct PerConnOptionList
        {
            public int dwSize;
            public IntPtr pszConnection;   // NULL = the LAN settings
            public int dwOptionCount;
            public int dwOptionError;
            public IntPtr pOptions;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PerConnOption
        {
            public int dwOption;
            public PerConnValue Value;
        }

        // Native union { DWORD; LPWSTR; FILETIME } - the FILETIME member keeps it 8 bytes on x86 too.
        [StructLayout(LayoutKind.Explicit)]
        private struct PerConnValue
        {
            [FieldOffset(0)] public int dwValue;
            [FieldOffset(0)] public IntPtr pszValue;
            [FieldOffset(0)] public FileTimeValue ftValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTimeValue
        {
            public int Low;
            public int High;
        }

        [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "InternetSetOptionW")]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "InternetQueryOptionW")]
        private static extern bool InternetQueryOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, ref int lpdwBufferLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        public sealed class Options
        {
            public int Flags { get; init; }
            public string? ProxyServer { get; init; }
            public string? ProxyBypass { get; init; }
            public string? AutoConfigUrl { get; init; }
        }

        /// <summary>Current LAN proxy config, or null if WinINET could not be queried.</summary>
        public static Options? Query()
        {
            try
            {
                // Windows 7+: FLAGS_UI reports what the user configured; older builds only know FLAGS.
                return QueryCore(INTERNET_PER_CONN_FLAGS_UI) ?? QueryCore(INTERNET_PER_CONN_FLAGS);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinINET query failed: {ex.Message}");
                return null;
            }
        }

        private static Options? QueryCore(int flagsOption)
        {
            int[] ids = { flagsOption, INTERNET_PER_CONN_PROXY_SERVER, INTERNET_PER_CONN_PROXY_BYPASS, INTERNET_PER_CONN_AUTOCONFIG_URL };
            int optionSize = Marshal.SizeOf<PerConnOption>();
            int listSize = Marshal.SizeOf<PerConnOptionList>();
            IntPtr options = Marshal.AllocHGlobal(optionSize * ids.Length);
            IntPtr list = Marshal.AllocHGlobal(listSize);
            try
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    Marshal.StructureToPtr(new PerConnOption { dwOption = ids[i] }, options + i * optionSize, false);
                }
                Marshal.StructureToPtr(new PerConnOptionList
                {
                    dwSize = listSize,
                    dwOptionCount = ids.Length,
                    pOptions = options
                }, list, false);

                int size = listSize;
                if (!InternetQueryOption(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, list, ref size))
                {
                    return null;
                }

                var values = new PerConnOption[ids.Length];
                for (int i = 0; i < ids.Length; i++)
                {
                    values[i] = Marshal.PtrToStructure<PerConnOption>(options + i * optionSize);
                }

                return new Options
                {
                    Flags = values[0].Value.dwValue,
                    ProxyServer = TakeString(values[1].Value.pszValue),
                    ProxyBypass = TakeString(values[2].Value.pszValue),
                    AutoConfigUrl = TakeString(values[3].Value.pszValue)
                };
            }
            finally
            {
                Marshal.FreeHGlobal(options);
                Marshal.FreeHGlobal(list);
            }
        }

        // Strings returned by WinINET are GlobalAlloc'ed and owned by the caller.
        private static string? TakeString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                GlobalFree(ptr);
            }
        }

        /// <summary>
        /// Write the LAN proxy config (flags, server, bypass, and optionally the PAC URL - when left out
        /// the URL is kept and the flags decide whether it is used). Null strings clear the value.
        /// </summary>
        public static bool Set(int flags, string? proxyServer, string? proxyBypass, string? autoConfigUrl, bool includeAutoConfigUrl)
        {
            if (SetCore(flags, proxyServer, proxyBypass, autoConfigUrl, includeAutoConfigUrl)) return true;

            // Some builds reject NULL strings; an empty string clears the value just the same.
            return (proxyServer == null || proxyBypass == null || (includeAutoConfigUrl && autoConfigUrl == null))
                && SetCore(flags, proxyServer ?? "", proxyBypass ?? "", autoConfigUrl ?? "", includeAutoConfigUrl);
        }

        private static bool SetCore(int flags, string? proxyServer, string? proxyBypass, string? autoConfigUrl, bool includeAutoConfigUrl)
        {
            var ids = new List<int> { INTERNET_PER_CONN_FLAGS, INTERNET_PER_CONN_PROXY_SERVER, INTERNET_PER_CONN_PROXY_BYPASS };
            var strings = new List<string?> { null, proxyServer, proxyBypass };
            if (includeAutoConfigUrl)
            {
                ids.Add(INTERNET_PER_CONN_AUTOCONFIG_URL);
                strings.Add(autoConfigUrl);
            }

            int optionSize = Marshal.SizeOf<PerConnOption>();
            int listSize = Marshal.SizeOf<PerConnOptionList>();
            var allocated = new List<IntPtr>();
            IntPtr options = Marshal.AllocHGlobal(optionSize * ids.Count);
            IntPtr list = Marshal.AllocHGlobal(listSize);
            try
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    var option = new PerConnOption { dwOption = ids[i] };
                    if (i == 0)
                    {
                        option.Value.dwValue = flags;
                    }
                    else
                    {
                        var ptr = strings[i] == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(strings[i]);
                        if (ptr != IntPtr.Zero) allocated.Add(ptr);
                        option.Value.pszValue = ptr;
                    }
                    Marshal.StructureToPtr(option, options + i * optionSize, false);
                }
                Marshal.StructureToPtr(new PerConnOptionList
                {
                    dwSize = listSize,
                    dwOptionCount = ids.Count,
                    pOptions = options
                }, list, false);

                return InternetSetOption(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, list, listSize);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinINET set failed: {ex.Message}");
                return false;
            }
            finally
            {
                foreach (var ptr in allocated) Marshal.FreeHGlobal(ptr);
                Marshal.FreeHGlobal(options);
                Marshal.FreeHGlobal(list);
            }
        }

        /// <summary>Tell every WinINET client in the session (browsers included) to re-read the settings.</summary>
        public static void NotifySettingsChanged()
        {
            try
            {
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_PROXY_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinINET notify failed: {ex.Message}");
            }
        }
    }

    #endregion

    /// <summary>
    /// Get cached proxies for a country
    /// </summary>
    public List<ProxyInfo> GetCachedProxies(string countryCode)
    {
        return _proxyCache.TryGetValue(countryCode, out var proxies) ? proxies : new List<ProxyInfo>();
    }

    /// <summary>
    /// Check if we have a cached list for a country
    /// </summary>
    public bool HasCachedProxies(string countryCode)
    {
        return _proxyCache.TryGetValue(countryCode, out var proxies) && proxies.Count > 0;
    }

    /// <summary>
    /// Number of cached proxies for a country that could be used as the Windows proxy (untested)
    /// </summary>
    public int GetCachedProxyCount(string countryCode)
    {
        return _proxyCache.TryGetValue(countryCode, out var proxies) ? proxies.Count(IsUsable) : 0;
    }

    private bool IsListFresh(string countryCode)
    {
        return _proxyCache.TryGetValue(countryCode, out var proxies)
            && proxies.Count > 0
            && DateTime.Now - proxies.Max(p => p.LastChecked) < ListFreshFor;
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
        // Parallel refreshes finish at the same time; one writer at a time, atomic replace.
        lock (_cacheFileLock)
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

                var tempPath = _cacheFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _cacheFilePath, overwrite: true);
                Debug.WriteLine($"Saved proxy cache: {_proxyCache.Values.Sum(l => l.Count)} proxies");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save proxy cache: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Clear all cached proxies
    /// </summary>
    public void ClearCache()
    {
        _proxyCache.Clear();
        lock (_cacheFileLock)
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    File.Delete(_cacheFilePath);
                }
            }
            catch { }
        }
    }

    #endregion

    public void Dispose()
    {
        DisconnectOnExit();
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

    /// <summary>When the list containing this proxy was downloaded (not a liveness check).</summary>
    public DateTime LastChecked { get; set; }

    public string DisplayAddress => $"{Host}:{Port}";
    public string DisplayType => Type.ToString();
}

public class ProxyTestResult
{
    public ProxyInfo Proxy { get; set; } = null!;
    public bool IsWorking { get; set; }
    public int Latency { get; set; }

    /// <summary>Exit country reported by the HTTPS endpoint (ISO alpha-2), null when unknown.</summary>
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

/// <summary>Immutable snapshot of the proxy connection.</summary>
public sealed class ProxyConnectionStatus
{
    public bool IsConnected { get; init; }
    public ProxyInfo? Proxy { get; init; }

    /// <summary>Country the traffic really exits in (ISO alpha-2), null when unknown.</summary>
    public string? ExitCountry { get; init; }
    public string? ExitIp { get; init; }
    public int LatencyMs { get; init; }

    /// <summary>False after the proxy failed two health checks in a row.</summary>
    public bool IsHealthy { get; init; } = true;

    /// <summary>
    /// WinXTools changed the Windows proxy and could not put the original back yet
    /// (Disconnect should be offered so the user can retry).
    /// </summary>
    public bool RestorePending { get; init; }
}

public enum ProxyConnectStatus
{
    Connected,
    /// <summary>Working proxies were found, but none exits in the requested country (see Proxy).</summary>
    OnlyOtherCountry,
    NoWorkingProxy,
    NoProxiesListed,
    /// <summary>Every list source failed (offline / blocked).</summary>
    ListUnavailable,
    OwnedByOtherInstance,
    BackupFailed,
    /// <summary>Windows rejected the setting or it did not read back (e.g. Group Policy).</summary>
    ApplyFailed,
    AppClosing
}

public sealed class ProxyConnectResult
{
    public ProxyConnectStatus Status { get; init; }

    /// <summary>The applied proxy (Connected) or the best alternative (OnlyOtherCountry).</summary>
    public ProxyTestResult? Proxy { get; init; }
    public int TestedCount { get; init; }
}

public enum ProxyRestoreResult
{
    /// <summary>No backup - WinXTools has not changed the proxy.</summary>
    NothingToRestore,
    /// <summary>The user's original settings are back.</summary>
    Restored,
    /// <summary>The proxy was changed by the user/another app after us; left untouched, backup discarded.</summary>
    ChangedElsewhere,
    /// <summary>This running process is connected (not a left-over).</summary>
    InUseByThisSession,
    /// <summary>Another running WinXTools window owns the proxy.</summary>
    InUseByOtherInstance,
    /// <summary>Could not write the settings; the backup is kept so it can be retried.</summary>
    Failed
}

public enum ProxyProgressStage
{
    FetchingList,
    Testing,
    Applying
}

public readonly record struct ProxyProgress(ProxyProgressStage Stage, int Done, int Total);

public sealed class ProxyFetchResult
{
    public List<ProxyInfo> Proxies { get; init; } = new();

    /// <summary>Proxies usable as the Windows proxy (HTTP, public IPv4). Untested.</summary>
    public int UsableCount { get; init; }

    /// <summary>No source answered; <see cref="Proxies"/> is the previous cached list (maybe empty).</summary>
    public bool AllSourcesFailed { get; init; }

    internal static ProxyFetchResult From(List<ProxyInfo> proxies, bool allSourcesFailed) => new()
    {
        Proxies = proxies,
        UsableCount = proxies.Count(ProxyService.IsUsable),
        AllSourcesFailed = allSourcesFailed
    };
}

/// <summary>
/// The user's WinINET proxy config captured before WinXTools changed it
/// (%LOCALAPPDATA%\NetX\proxy_backup.json).
/// </summary>
public sealed class ProxyBackup
{
    public int Version { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }
    public int OwnerPid { get; set; }
    public DateTime OwnerStartUtc { get; set; }

    /// <summary>Every proxy server WinXTools set during this session (switching countries adds more).</summary>
    public List<string> AppliedServers { get; set; } = new();

    // Per-connection options (INTERNET_PER_CONN_*), restored through WinINET
    public bool HasOptions { get; set; }
    public int Flags { get; set; }
    public string? ProxyServer { get; set; }
    public string? ProxyBypass { get; set; }
    public string? AutoConfigUrl { get; set; }

    // Raw registry snapshot, used as a fallback restore
    public List<ProxyRegistryValue> Registry { get; set; } = new();
    public string? ConnectionSettings { get; set; }
}

public sealed class ProxyRegistryValue
{
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public string? Kind { get; set; }
    public string? Value { get; set; }
}

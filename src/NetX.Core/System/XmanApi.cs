using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetX.Core.System;

/// <summary>
/// The xman studio web API (https://xman4289.com) as WinXTools uses it: the same
/// /api/v1/product/{slug}/... endpoints the studio's other apps use for licenses,
/// trials and update checks.
/// </summary>
public static class XmanApi
{
    private const string HostName = "xman4289.com";
    public const string Host = "https://" + HostName;
    public const string ProductSlug = "winx-tools";
    public const string ProductApi = Host + "/api/v1/product/" + ProductSlug;
    public const string ProductPageUrl = Host + "/products/" + ProductSlug;

    // SPKI (SubjectPublicKeyInfo) SHA-256 pins. A local MITM (hosts-file redirect plus a
    // root CA the user was talked into installing) cannot present any of these keys.
    // Pinned above the leaf, which rotates every few months. Checked 2026-09-23:
    // leaf CN=xman4289.com <- WE1 <- GTS Root R4 (cross-signed by GlobalSign Root CA).
    private static readonly HashSet<string> PinnedSpkiSha256 = new(StringComparer.OrdinalIgnoreCase)
    {
        "908769E8D34477CC2CBA0632C88605B22D7294C0840F78596D247C645B1AFC0E", // WE1 (Google Trust Services)
        "9847E5653E5E9E847516E5CB818606AA7544A19BE67FD7366D506988E8D84347", // GTS Root R4
        "2BCEE858158CF5465FC9D76F0DFA312FEF25A4DCA8501DA9B46B67D1FBFA1B64"  // GlobalSign Root CA (R4 cross-sign)
    };

    /// <summary>
    /// An HttpClient for xman4289.com that only trusts the pinned certificate chain. Without
    /// <paramref name="followRedirects"/> a redirect comes back as the response, so the host it
    /// points to is never contacted.
    /// </summary>
    public static HttpClient CreateClient(string userAgent, TimeSpan timeout, bool followRedirects = true)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateServerCertificate,
            AllowAutoRedirect = followRedirects
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.Add("User-Agent", userAgent);
        // Laravel answers errors with JSON only when the caller asks for JSON.
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        return client;
    }

    /// <summary>True for an https:// address on xman4289.com itself (default port).</summary>
    public static bool IsXmanUrl(Uri? uri) =>
        uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase);

    private static bool ValidateServerCertificate(HttpRequestMessage request, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors != SslPolicyErrors.None || cert == null) return false;
        if (IsPinned(cert)) return true;
        if (chain?.ChainElements == null) return false;
        foreach (var element in chain.ChainElements)
        {
            if (IsPinned(element.Certificate)) return true;
        }
        return false;
    }

    private static bool IsPinned(X509Certificate2 cert) =>
        PinnedSpkiSha256.Contains(Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())));

    public static Task<ApiReply> PostAsync(HttpClient client, string path, object body) =>
        SendAsync(() => client.PostAsJsonAsync(ProductApi + path, body));

    public static Task<ApiReply> GetAsync(HttpClient client, string pathAndQuery) =>
        SendAsync(() => client.GetAsync(ProductApi + pathAndQuery));

    private static async Task<ApiReply> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            using var response = await send();
            JsonNode? json = null;
            try
            {
                var text = await response.Content.ReadAsStringAsync();
                if (text.Length <= 1024 * 1024) json = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // An HTML error page from a proxy or a 503: no answer from the app itself.
            }
            return new ApiReply(response.StatusCode, json as JsonObject);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Debug.WriteLine($"xman API unreachable: {ex.Message}");
            return ApiReply.Unreachable;
        }
    }
}

/// <summary>One answer from the xman API; <see cref="Json"/> is null when the body was not JSON.</summary>
public sealed class ApiReply(HttpStatusCode? status, JsonObject? json)
{
    public static readonly ApiReply Unreachable = new(null, null);

    /// <summary>Null when the request never got an HTTP answer (offline, DNS, TLS pin, timeout).</summary>
    public HttpStatusCode? Status { get; } = status;
    public JsonObject? Json { get; } = json;

    public bool Reached => Status != null;
    public bool Success => Json.Bool("success") == true;
    public string? ErrorCode => Json.Str("error_code") ?? Json.Str("code");
    public JsonObject? Data => Json?["data"] as JsonObject;

    /// <summary>
    /// A definite answer from the license app (a JSON body on a 2xx/4xx other than 429).
    /// Anything else — offline, 5xx, rate limit, an HTML page — says nothing about the license.
    /// </summary>
    public bool IsDefinitive =>
        Json != null && Status is { } s && (int)s < 500 && s != HttpStatusCode.TooManyRequests;

    public bool IsRateLimited => Status == HttpStatusCode.TooManyRequests;

    /// <summary>The server's own wording (Thai), for logs and as a last-resort message.</summary>
    public string? ServerMessage
    {
        get
        {
            var message = Json.Str("message") ?? Json.Str("error");
            if (!string.IsNullOrWhiteSpace(message)) return message;
            if (Json?["errors"] is JsonObject errors)
            {
                var all = errors.SelectMany(kv => kv.Value is JsonArray list
                    ? list.Select(v => v?.ToString())
                    : new[] { kv.Value?.ToString() });
                return string.Join("\n", all.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            return null;
        }
    }
}

/// <summary>Lenient readers: the server's JSON mixes bools, 0/1 and strings for the same idea.</summary>
public static class JsonRead
{
    public static string? Str(this JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var value) || value == null) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
            _ => null
        };
    }

    public static bool? Bool(this JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var value) || value == null) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetValue<double>() != 0,
            JsonValueKind.String => value.GetValue<string>() is "1" or "true" or "True",
            _ => null
        };
    }

    public static long? Long(this JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var value) || value == null) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.Number => (long)Math.Floor(value.GetValue<double>()),
            JsonValueKind.String when long.TryParse(value.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null
        };
    }

    public static DateTimeOffset? Date(this JsonNode? node, string name) =>
        DateTimeOffset.TryParse(node.Str(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    public static JsonObject? Obj(this JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value as JsonObject : null;
}

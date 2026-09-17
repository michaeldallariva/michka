using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Monitor.Services;

/// <summary>
/// Hub-side proxy for the on-disk Pi-hole widget. Pi-hole's web API doesn't send CORS headers, so the
/// kiosk browser can't call it directly — it asks the hub, which fetches server-side and returns a
/// normalised summary. The user's Pi-hole URL + password live in the browser's localStorage and ride
/// in on the query string.
///
/// Supports the current Pi-hole v6 REST API (POST /api/auth → SID, then GET /api/stats/summary) and
/// falls back to the legacy v5 PHP API (/admin/api.php?summaryRaw&amp;auth=&lt;token&gt;). v6 limits
/// concurrent sessions, so the SID is cached per base URL and only re-minted when it expires or a call
/// returns 401. Self-signed TLS is accepted (LAN appliance).
/// </summary>
public static class PiholeClient
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, Session> Sessions = new();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    private sealed record Session(string Sid, DateTime Expires);

    private static object Err(string code) => new { ok = false, error = code };

    /// <summary>Fetch + normalise a Pi-hole summary. Never throws; returns {ok:false,error} on failure.</summary>
    public static async Task<object> SummaryAsync(string baseUrl, string password, CancellationToken ct = default)
    {
        baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        password ??= "";
        if (baseUrl.Length == 0) return Err("no_url");
        if (!baseUrl.Contains("://")) baseUrl = "http://" + baseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var bu) || (bu.Scheme != "http" && bu.Scheme != "https"))
            return Err("bad_url");

        // Try the v6 REST API first.
        try
        {
            var v6 = await SummaryV6Async(baseUrl, password, ct);
            if (v6 is not null) return v6;
        }
        catch (AuthFailed) { return Err("auth"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Err("unreachable"); }
        catch (HttpRequestException) { /* maybe a v5 box — fall through */ }
        catch (Exception) { /* fall through to v5 */ }

        // Legacy v5 fallback.
        try
        {
            var v5 = await SummaryV5Async(baseUrl, password, ct);
            if (v5 is not null) return v5;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Err("unreachable"); }
        catch (Exception) { /* ignore */ }

        return Err("unreachable");
    }

    private sealed class AuthFailed : Exception { }

    // ---- v6 ----
    private static async Task<object?> SummaryV6Async(string baseUrl, string password, CancellationToken ct)
    {
        var sid = await GetSidV6Async(baseUrl, password, ct, force: false);
        var (status, body) = await GetV6Async(baseUrl, "/api/stats/summary", sid, ct);
        if (status == 401)   // SID expired/invalid — re-auth once and retry
        {
            sid = await GetSidV6Async(baseUrl, password, ct, force: true);
            (status, body) = await GetV6Async(baseUrl, "/api/stats/summary", sid, ct);
        }
        if (status == 404) return null;   // not a v6 box → let the caller try v5
        if (status is < 200 or >= 300) return Err(status == 401 ? "auth" : "http_" + status);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var q = root.GetProperty("queries");
        double total = NumProp(q, "total");
        double blocked = NumProp(q, "blocked");
        double pct = NumProp(q, "percent_blocked");
        double domains = root.TryGetProperty("gravity", out var g) ? NumProp(g, "domains_being_blocked") : 0;
        double clients = root.TryGetProperty("clients", out var c) ? NumProp(c, "active") : 0;

        return new
        {
            ok = true,
            version = 6,
            percentBlocked = pct,
            queriesToday = total,
            blockedToday = blocked,
            domainsOnList = domains,
            clientsActive = clients,
        };
    }

    private static async Task<string> GetSidV6Async(string baseUrl, string password, CancellationToken ct, bool force)
    {
        if (!force && Sessions.TryGetValue(baseUrl, out var s) && s.Expires > DateTime.UtcNow)
            return s.Sid;

        using var cts = LinkedTimeout(ct);
        var json = "{\"password\":" + JsonSerializer.Serialize(password) + "}";
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/auth")
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        using var resp = await Http.SendAsync(req, cts.Token);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) throw new HttpRequestException("not v6");
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("session", out var sess)) throw new AuthFailed();
        bool valid = sess.TryGetProperty("valid", out var v) && v.ValueKind == JsonValueKind.True;
        if (!valid || !sess.TryGetProperty("sid", out var sidEl) || sidEl.ValueKind != JsonValueKind.String)
            throw new AuthFailed();
        var sid = sidEl.GetString()!;
        double validity = sess.TryGetProperty("validity", out var vEl) && vEl.ValueKind == JsonValueKind.Number ? vEl.GetDouble() : 300;
        Sessions[baseUrl] = new Session(sid, DateTime.UtcNow.AddSeconds(Math.Max(30, validity - 60)));
        return sid;
    }

    private static async Task<(int status, string body)> GetV6Async(string baseUrl, string path, string sid, CancellationToken ct)
    {
        using var cts = LinkedTimeout(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        req.Headers.TryAddWithoutValidation("X-FTL-SID", sid);
        using var resp = await Http.SendAsync(req, cts.Token);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    // ---- v5 ----
    private static async Task<object?> SummaryV5Async(string baseUrl, string token, CancellationToken ct)
    {
        using var cts = LinkedTimeout(ct);
        var url = baseUrl + "/admin/api.php?summaryRaw";
        if (token.Length > 0) url += "&auth=" + Uri.EscapeDataString(token);
        using var resp = await Http.GetAsync(url, cts.Token);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart().StartsWith('[')) return null;   // [] = not Pi-hole / no data
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("dns_queries_today", out _)) return null;
        return new
        {
            ok = true,
            version = 5,
            percentBlocked = NumProp(r, "ads_percentage_today"),
            queriesToday = NumProp(r, "dns_queries_today"),
            blockedToday = NumProp(r, "ads_blocked_today"),
            domainsOnList = NumProp(r, "domains_being_blocked"),
            clientsActive = NumProp(r, "unique_clients"),
        };
    }

    // Pi-hole numbers come as either JSON numbers or comma-formatted strings ("12,345") depending on
    // version/endpoint — parse both.
    private static double NumProp(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return 0;
        if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString()?.Replace(",", ""),
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return 0;
    }

    private static CancellationTokenSource LinkedTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(7));
        return cts;
    }
}

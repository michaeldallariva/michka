using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Monitor.Services;

/// <summary>
/// Server-side reachability check for the on-disk Uptime widget. A browser can't ICMP-ping or do
/// cross-origin probes (CORS), so the kiosk asks the hub to check a user-supplied target and reports
/// up/down + latency. Three target forms:
///   - <c>http(s)://…</c>  → HTTP GET (a web service); any response &lt; 500 = up.
///   - <c>host:port</c>    → TCP connect to that port (connect OR refused = host alive).
///   - <c>host</c> / IP    → ICMP ping (what most people mean by "is it up"), falling back to a TCP
///                            connect on :80 when ICMP isn't permitted for the hub's user.
/// Self-signed TLS is accepted for the HTTP form (internal services).
/// </summary>
public static class UptimeChecker
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        var h = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("michka-uptime/1.0");
        return h;
    }

    public record Result(bool Ok, int Status, long Ms, string? Error);

    public static async Task<Result> CheckAsync(string target, CancellationToken ct = default)
    {
        target = (target ?? "").Trim();
        if (target.Length == 0) return new(false, 0, 0, "empty");

        // Explicit web URL → HTTP check.
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return await HttpCheckAsync(target, ct);

        // Bare host[:port] → ping (default) or TCP connect (if a port was given).
        var (host, port) = SplitHostPort(target);
        if (host.Length == 0) return new(false, 0, 0, "bad_url");

        if (port is int p) return await TcpCheckAsync(host, p, ct);

        // No port: ICMP ping first (matches "does it reply to pings"); fall back to TCP :80 if the hub's
        // user can't open an ICMP socket, so the check still works.
        var ping = await TryPingAsync(host);
        return ping ?? await TcpCheckAsync(host, 80, ct);
    }

    private static async Task<Result> HttpCheckAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return new(false, 0, 0, "bad_url");
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            int code = (int)resp.StatusCode;
            bool ok = code < 500;
            return new(ok, code, sw.ElapsedMilliseconds, ok ? null : "http_" + code);
        }
        catch (OperationCanceledException) { sw.Stop(); return new(false, 0, sw.ElapsedMilliseconds, "timeout"); }
        catch (HttpRequestException) { sw.Stop(); return new(false, 0, sw.ElapsedMilliseconds, "unreachable"); }
        catch (Exception) { sw.Stop(); return new(false, 0, sw.ElapsedMilliseconds, "error"); }
    }

    // Returns up + RTT on an ICMP reply; null when ICMP is unusable (no permission / DNS) or didn't
    // reply, so the caller can fall back to TCP.
    private static async Task<Result?> TryPingAsync(string host)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 3000);
            if (reply.Status == IPStatus.Success) return new(true, 0, reply.RoundtripTime, null);
            return null;
        }
        catch { return null; }   // ICMP socket not permitted, host won't resolve, etc.
    }

    private static async Task<Result> TcpCheckAsync(string host, int port, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return new(true, port, sw.ElapsedMilliseconds, null);          // connected → up
        }
        catch (SocketException se) when (se.SocketErrorCode == SocketError.ConnectionRefused)
        {
            sw.Stop();
            return new(true, port, sw.ElapsedMilliseconds, null);          // RST = host is alive
        }
        catch (OperationCanceledException) { sw.Stop(); return new(false, 0, sw.ElapsedMilliseconds, "timeout"); }
        catch (SocketException) { sw.Stop(); return new(false, 0, sw.ElapsedMilliseconds, "unreachable"); }
        catch (Exception) { sw.Stop(); return new(false, 0, sw.ElapsedMilliseconds, "error"); }
    }

    // Split "host:port" when the suffix is a valid port number; otherwise the whole string is the host.
    private static (string host, int? port) SplitHostPort(string s)
    {
        int i = s.LastIndexOf(':');
        if (i > 0 && i < s.Length - 1 && int.TryParse(s[(i + 1)..], out var p) && p is > 0 and <= 65535)
            return (s[..i], p);
        return (s, null);
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Monitor.Services;

/// <summary>
/// LAN discovery for a NexusM media server, used by the on-disk NexusM player widget.
///
/// A browser can't send UDP broadcasts, so the kiosk asks the hub to probe for it. NexusM's own
/// <c>DiscoveryService</c> listens on UDP <see cref="DiscoveryPort"/> for the literal probe
/// <c>"NexusM-Discovery"</c> and unicasts back <c>"NexusM-Server:http://&lt;ip&gt;:&lt;port&gt;"</c>.
/// We broadcast the probe, gather replies for a short window, and return the de-duped server URLs.
/// </summary>
public static class NexusmDiscovery
{
    private const int DiscoveryPort = 8180;            // must match NexusM's DiscoveryService
    private const string ProbeMsg = "NexusM-Discovery";
    private const string ReplyPrefix = "NexusM-Server:";

    /// <summary>Broadcasts a discovery probe and collects server URLs for up to <paramref name="timeoutMs"/>.</summary>
    public static async Task<List<string>> DiscoverAsync(int timeoutMs = 1200, CancellationToken ct = default)
    {
        var found = new List<string>();
        using var udp = new UdpClient();
        try
        {
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var probe = Encoding.UTF8.GetBytes(ProbeMsg);
            var dest = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
            await udp.SendAsync(probe, probe.Length, dest);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(cts.Token);
                    var msg = Encoding.UTF8.GetString(result.Buffer).Trim();
                    if (!msg.StartsWith(ReplyPrefix, StringComparison.Ordinal)) continue;
                    var url = ToHttp(msg[ReplyPrefix.Length..].Trim());
                    if (url.Length > 0 && !found.Contains(url)) found.Add(url);
                }
                catch (OperationCanceledException) { break; }     // window elapsed — normal
            }
        }
        catch (SocketException) { /* no network / broadcast blocked — return whatever we have */ }

        return found;
    }

    /// <summary>
    /// The kiosk player connects over plain HTTP only (a self-signed-HTTPS NexusM can't be reached from
    /// the browser anyway). NexusM advertises its HTTPS URL whenever HTTPS is enabled, but its plain
    /// HTTP server always also listens on <c>ServerPort</c> = <c>HttpsPort − 1</c> by NexusM's default
    /// convention (8182 / 8183). So rewrite any https reply to that http endpoint; http replies pass
    /// through unchanged. This is why discovery never surfaces an https address.
    /// </summary>
    private static string ToHttp(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        if (u.Scheme == Uri.UriSchemeHttps)
        {
            int httpPort = u.Port > 1 ? u.Port - 1 : 8182;   // NexusM default: HTTP = HTTPS − 1
            return $"http://{u.Host}:{httpPort}";
        }
        if (u.Scheme == Uri.UriSchemeHttp)
            return $"http://{u.Host}:{(u.IsDefaultPort ? 8182 : u.Port)}";
        return url;
    }
}

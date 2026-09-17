using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Monitor.Api;

/// <summary>
/// Lightweight request-auth helpers for the hub. There is no user login; instead two cheap,
/// complementary guards keep the open <c>0.0.0.0</c> listener from being driven by outsiders:
///
/// <list type="bullet">
/// <item><b>Shared token</b> — a secret generated into <c>michka.conf</c> and copied to each agent's
/// <c>michka_c.conf</c>. Agents present it on <c>/api/ingest</c> (header <c>X-Michka-Token</c> or
/// <c>Authorization: Bearer</c>), so a random host on the LAN can't spoof metrics.</item>
/// <item><b>Same-origin browser check</b> — the dashboard's own POSTs (settings / host delete /
/// system actions) are accepted when they are genuine same-origin requests from the served page
/// (Fetch-Metadata <c>Sec-Fetch-Site</c>, which page script cannot forge), <i>or</i> when they carry
/// the token. This lets the kiosk UI work without embedding the secret while still blocking
/// cross-site (CSRF) calls.</item>
/// </list>
///
/// The <see cref="HostAllowlist"/> + <see cref="JsonContentType"/> middlewares add DNS-rebinding and
/// simple-form-CSRF protection on top (a rebound attacker domain still shows in the Host header even
/// though the browser believes it is same-origin).
/// </summary>
public static class Security
{
    /// <summary>Header an agent (or automation) sends carrying the shared secret.</summary>
    public const string TokenHeader = "X-Michka-Token";

    /// <summary>True when the request presents the configured shared token (constant-time compare).</summary>
    public static bool HasValidToken(HttpContext ctx, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return false; // no secret configured ⇒ token path closed

        string? presented = ctx.Request.Headers[TokenHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(presented))
        {
            var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
            if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                presented = auth["Bearer ".Length..].Trim();
        }
        if (string.IsNullOrEmpty(presented)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>
    /// True for a request that is NOT a cross-site browser call, so the dashboard's own fetches pass
    /// while a malicious page's do not. Uses the Fetch-Metadata <c>Sec-Fetch-Site</c> header (set by
    /// the browser, unforgeable by script); falls back to an <c>Origin</c>-vs-<c>Host</c> match for
    /// the rare client that omits it. Non-browser callers (no Origin, no Sec-Fetch-*) are treated as
    /// trusted here — they are gated by the token and the Host allowlist instead.
    /// </summary>
    public static bool IsSameOriginRequest(HttpContext ctx)
    {
        var site = ctx.Request.Headers["Sec-Fetch-Site"].FirstOrDefault();
        if (!string.IsNullOrEmpty(site))
            return site is "same-origin" or "none";

        var origin = ctx.Request.Headers.Origin.FirstOrDefault();
        if (string.IsNullOrEmpty(origin)) return true; // direct navigation / non-browser client
        return Uri.TryCreate(origin, UriKind.Absolute, out var o)
            && string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Guard an agent-only endpoint (ingest): the shared token is mandatory. Returns an error
    /// result when unauthorized, or null to proceed.</summary>
    public static IResult? RequireToken(HttpContext ctx, string? token)
        => HasValidToken(ctx, token) ? null
            : Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>Guard a dashboard/admin endpoint (settings, host delete, system actions): a genuine
    /// same-origin page request <i>or</i> the shared token. Returns an error result when unauthorized,
    /// or null to proceed.</summary>
    public static IResult? RequireBrowserOrToken(HttpContext ctx, string? token)
        => (IsSameOriginRequest(ctx) || HasValidToken(ctx, token)) ? null
            : Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Generate a fresh URL-safe shared secret (~32 chars). Kept for any caller that wants a
    /// long token; new configs use <see cref="NewShortToken"/> for a human-copyable one.</summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // Token alphabet with look-alike characters removed (no 0/O, 1/I/l) so the short token is easy to
    // read off the screen and type into an agent without mistakes.
    private const string TokenAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>Generate a short, human-copyable shared token (6 chars by default, no look-alikes).
    /// Brute force is online-only, so this combined with the per-IP rate limiter on <c>/api/ingest</c>
    /// is plenty to keep a LAN appliance locked down (~3.4e10 combinations, throttled to a few guesses
    /// per minute).</summary>
    public static string NewShortToken(int len = 6)
    {
        Span<char> c = stackalloc char[len];
        for (int i = 0; i < len; i++) c[i] = TokenAlphabet[RandomNumberGenerator.GetInt32(TokenAlphabet.Length)];
        return new string(c);
    }

    /// <summary>Best-effort client IP (the rate-limit key). Direct LAN connections only, so the socket
    /// remote address is the right source; no proxy headers are trusted.</summary>
    public static string ClientIp(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Hash a UI PIN with PBKDF2-SHA256 + a random salt for storage in <c>michka.conf</c>. The
    /// PIN is only ever verified, never recovered, so a one-way salted hash is both safer and simpler
    /// than reversible encryption (which would need a key on the same box). Format:
    /// <c>pbkdf2$&lt;iters&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>.</summary>
    public static string HashPin(string pin)
    {
        const int iters = 200_000, saltLen = 16, keyLen = 32;
        Span<byte> salt = stackalloc byte[saltLen];
        RandomNumberGenerator.Fill(salt);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iters, HashAlgorithmName.SHA256, keyLen);
        return $"pbkdf2${iters}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>Verify a typed PIN against a stored <see cref="HashPin"/> value (constant-time compare).</summary>
    public static bool VerifyPin(string pin, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iters)) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[2]); expected = Convert.FromBase64String(parts[3]); }
        catch { return false; }
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iters, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(key, expected);
    }

    /// <summary>
    /// Reject requests whose <c>Host</c> header isn't one we expect, defeating DNS-rebinding (where a
    /// browser is tricked into treating an attacker domain as the hub's origin — the rebound domain
    /// still appears here). Loopback names, raw IP literals (immune to rebinding) and the machine name
    /// are always allowed; extra hostnames go in <c>michka.conf</c> <c>hostAllow</c>. A <c>"*"</c>
    /// entry disables the check.
    /// </summary>
    public static Func<HttpContext, RequestDelegate, Task> HostAllowlist(IReadOnlyList<string> extra)
    {
        var machine = Environment.MachineName;
        var allow = new HashSet<string>(extra, StringComparer.OrdinalIgnoreCase);
        bool disabled = allow.Contains("*");

        return async (ctx, next) =>
        {
            if (!disabled && !IsHostAllowed(ctx.Request.Host.Host, machine, allow))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = "host not allowed" });
                return;
            }
            await next(ctx);
        };
    }

    private static bool IsHostAllowed(string host, string machine, HashSet<string> allow)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (IPAddress.TryParse(host, out _)) return true;       // raw IP — can't be DNS-rebound
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.Equals(machine, StringComparison.OrdinalIgnoreCase)) return true;
        return allow.Contains(host);
    }

    /// <summary>
    /// Require <c>Content-Type: application/json</c> on POST/PUT/PATCH to <c>/api/*</c>. A cross-site
    /// <c>&lt;form&gt;</c> or <c>no-cors fetch</c> can only send simple content types, so this blunts
    /// the CSRF that the same-origin check might miss (and forces a CORS preflight, which the hub does
    /// not grant, for any scripted cross-origin attempt).
    /// </summary>
    public static async Task JsonContentType(HttpContext ctx, RequestDelegate next)
    {
        var m = ctx.Request.Method;
        bool mutating = HttpMethods.IsPost(m) || HttpMethods.IsPut(m) || HttpMethods.IsPatch(m);
        if (mutating && ctx.Request.Path.StartsWithSegments("/api"))
        {
            var contentType = ctx.Request.ContentType ?? "";
            if (!contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                await ctx.Response.WriteAsJsonAsync(new { error = "application/json required" });
                return;
            }
        }
        await next(ctx);
    }
}

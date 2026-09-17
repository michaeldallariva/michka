using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Monitor.Metrics;
using Monitor.Modes;
using Monitor.Realtime;
using Monitor.Services;
using Monitor.Storage;

namespace Monitor.Api;

public static class Endpoints
{
    /// <summary>A host considered "online" if seen within this window.</summary>
    private static readonly long OnlineWindowMs = 10_000;

    public static void MapApi(this WebApplication app)
    {
        // Agent / remote snapshot ingestion. When token auth is enabled (the default) the shared token
        // is mandatory so a random host on the network can't spoof metrics or poison the history/host
        // list; repeated wrong tokens from one source are rate-limited so the short token can't be
        // brute forced. The token requirement can be turned off in Settings for a fully trusted LAN.
        app.MapPost("/api/ingest", async (HttpContext ctx, LiveBroadcaster bc, HubRuntime rt, AuthState auth) =>
        {
            if (rt.Config.TokenRequired != "off")
            {
                var key = "ingest:" + Security.ClientIp(ctx);
                if (auth.IsLocked(key, out var retry))
                {
                    ctx.Response.Headers.RetryAfter = retry.ToString();
                    return Results.Json(new { error = "rate_limited" }, statusCode: StatusCodes.Status429TooManyRequests);
                }
                if (!Security.HasValidToken(ctx, rt.Config.Token))
                {
                    auth.RecordFailure(key);
                    return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
                }
                auth.RecordSuccess(key);
            }

            MetricSnapshot? snap;
            try
            {
                snap = await JsonSerializer.DeserializeAsync<MetricSnapshot>(ctx.Request.Body, MetricsJson.Options);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "invalid snapshot json" });
            }

            if (snap is null || string.IsNullOrWhiteSpace(snap.Host))
                return Results.BadRequest(new { error = "host is required" });

            bc.Publish(snap);
            return Results.Ok(new { ok = true });
        });

        // List of known hosts with online flag + last-seen.
        app.MapGet("/api/hosts", (LiveBroadcaster bc, MetricsStore store) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Union of hosts seen this session (live cache) and historical hosts (db).
            var lastSeen = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (host, ts) in store.KnownHosts()) lastSeen[host] = ts;
            foreach (var (host, snap) in bc.Latest)
                lastSeen[host] = Math.Max(lastSeen.GetValueOrDefault(host), snap.TsUnixMs);

            var hosts = lastSeen
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new
                {
                    name = kv.Key,
                    lastSeenMs = kv.Value,
                    online = now - kv.Value <= OnlineWindowMs,
                });

            return Results.Json(hosts, MetricsJson.Options);
        });

        // Remove a host: delete its history + cached snapshot + saved layout. Not a ban — if the
        // host pushes again it simply reappears.
        app.MapPost("/api/hosts/delete", async (HttpContext ctx, LiveBroadcaster bc, MetricsStore store, HubRuntime rt) =>
        {
            if (Security.RequireBrowserOrToken(ctx, rt.Config.Token) is { } deny) return deny;

            HostDelete? body;
            try { body = await JsonSerializer.DeserializeAsync<HostDelete>(ctx.Request.Body, MetricsJson.Options); }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid json" }); }
            var host = body?.Host;
            if (string.IsNullOrWhiteSpace(host)) return Results.BadRequest(new { error = "host is required" });

            int rows = store.DeleteHost(host);
            bc.Forget(host);
            if (rt.Config.Layouts.Remove(host))
            {
                try { rt.Config.Save(); } catch { /* best-effort: history is already gone */ }
            }
            return Results.Json(new { ok = true, deletedRows = rows }, MetricsJson.Options);
        });

        // History for chart hydration on host switch.
        app.MapGet("/api/history", (string host, long? sinceMs, MetricsStore store) =>
        {
            long since = sinceMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5 * 60_000; // default 5 min
            var points = store.QueryHistory(host, since);
            return Results.Json(points, MetricsJson.Options);
        });

        // Self-contained HTML report for one host (7-day overview charts). Opened in a new browser tab
        // by the dashboard's report button; the page inlines ECharts + data so it saves/views offline.
        app.MapGet("/api/report", (string host, HubRuntime rt, MetricsStore store) =>
        {
            if (string.IsNullOrWhiteSpace(host)) return Results.BadRequest(new { error = "host is required" });
            var html = ReportPage.Build(host, store, rt.Config.Lang ?? "en", rt.Config.DateFmt ?? "us");
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // Current settings for the Settings page.
        app.MapGet("/api/server-config", (HubRuntime rt) => Results.Json(new
        {
            port = rt.Port,
            hostName = rt.HostName,
            version = rt.Version,
            db = rt.DbPath,
            template = rt.Config.Template,
            tokenRequired = rt.Config.TokenRequired,
            pinEnabled = rt.Config.PinEnabled,
            platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            display = new
            {
                lang = rt.Config.Lang,
                tempUnit = rt.Config.TempUnit,
                cores = rt.Config.Cores,
                cycle = rt.Config.Cycle,
                cycleSec = rt.Config.CycleSec,
                dateFmt = rt.Config.DateFmt,
                screenSize = rt.Config.ScreenSize,
                ssMode = rt.Config.SsMode,
                ssTimeout = rt.Config.SsTimeout,
                kioskAuto = rt.Config.KioskAuto,
            },
            services = rt.Config.Services,
            servicesByHost = rt.Config.ServicesByHost,
            layout = rt.Config.Layout,
            layouts = rt.Config.Layouts,
            widgetsByHost = rt.Config.WidgetsByHost,
            widgetConfig = rt.Config.WidgetConfig,
        }, MetricsJson.Options));

        // ---- optional UI PIN gate (login / logout / status) ------------------------------------
        // These three are always reachable (even when the PIN gate is on, they are how you get past it).

        // Probe the auth state: whether a PIN is required and whether this browser is already in.
        app.MapGet("/api/auth", (HttpContext ctx, HubRuntime rt, AuthState auth) =>
        {
            bool on = rt.Config.PinEnabled == "on";
            bool authed = !on || auth.ValidSession(ctx.Request.Cookies["michka_sid"]);
            return Results.Json(new { pinEnabled = on, authenticated = authed });
        });

        // Submit the PIN from the keypad login page. Rate-limited per IP (shared limiter): a few wrong
        // PINs lock the source out for an escalating cool-down, so a 6-digit PIN can't be brute forced.
        app.MapPost("/api/login", async (HttpContext ctx, HubRuntime rt, AuthState auth) =>
        {
            if (rt.Config.PinEnabled != "on") return Results.Json(new { ok = true }); // nothing to log into
            var key = "pin:" + Security.ClientIp(ctx);
            if (auth.IsLocked(key, out var retry))
            {
                ctx.Response.Headers.RetryAfter = retry.ToString();
                return Results.Json(new { error = "rate_limited", retryAfter = retry }, statusCode: StatusCodes.Status429TooManyRequests);
            }
            LoginBody? body;
            try { body = await JsonSerializer.DeserializeAsync<LoginBody>(ctx.Request.Body, MetricsJson.Options); }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid json" }); }

            if (body?.Pin is not { Length: > 0 } pin || !Security.VerifyPin(pin, rt.Config.PinHash))
            {
                auth.RecordFailure(key);
                return Results.Json(new { error = "bad_pin" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            auth.RecordSuccess(key);
            IssueSession(ctx, auth);
            return Results.Json(new { ok = true });
        });

        // Lock the dashboard again (clear this browser's session).
        app.MapPost("/api/logout", (HttpContext ctx, AuthState auth) =>
        {
            auth.EndSession(ctx.Request.Cookies["michka_sid"]);
            ctx.Response.Cookies.Delete("michka_sid");
            return Results.Json(new { ok = true });
        });

        // Reveal the agent token for the Settings page so the user can copy it into agents. Guarded the
        // same way as the admin endpoints: a genuine same-origin browser request OR the token itself.
        // It is intentionally NOT returned by /api/server-config (which any GET can read); this is the
        // single, deliberately gated way to read the secret. (Once the optional UI PIN is enabled, the
        // PIN session is also required, since this endpoint is not on the PIN allow-list.)
        app.MapGet("/api/token", (HttpContext ctx, HubRuntime rt) =>
        {
            if (Security.RequireBrowserOrToken(ctx, rt.Config.Token) is { } deny) return deny;
            return Results.Json(new { token = rt.Config.Token, required = rt.Config.TokenRequired });
        });

        // Searchable list of a host's services/daemons (for the monitor-select page). With no host (or
        // the hub's own host) it uses the in-process inspector for freshness; for an agent host it
        // returns the catalog that host last pushed (empty/unavailable until the agent reports one).
        app.MapGet("/api/services", (string? host, IServiceInspector inspector, LiveBroadcaster bc, HubRuntime rt) =>
        {
            if (string.IsNullOrWhiteSpace(host) || host == rt.HostName)
                return Results.Json(new { host = rt.HostName, available = inspector.Available, services = inspector.List() }, MetricsJson.Options);

            var cat = bc.Catalog(host);
            return Results.Json(new { host, available = cat is { Count: > 0 }, services = cat ?? new() }, MetricsJson.Options);
        });

        // GPU temperature(s) for a host, from its latest cached snapshot — feeds the optional GPU-temp
        // widget. Empty list when the host reports no GPU sensor (Linux, or a Windows box without one).
        app.MapGet("/api/gpu", (string? host, LiveBroadcaster bc, HubRuntime rt) =>
        {
            var key = string.IsNullOrWhiteSpace(host) ? rt.HostName : host;
            var gpus = bc.Latest.TryGetValue(key, out var snap) ? snap.Gpus : new List<Monitor.Metrics.GpuInfo>();
            return Results.Json(new { host = key, available = gpus.Count > 0, gpus }, MetricsJson.Options);
        });

        // Docker containers for a host, from the list it last reported (cached + stripped from the
        // snapshot like the service catalog). Feeds the optional Docker widget. available=false when the
        // host has never reported Docker (no Docker, or not yet); an empty list = Docker, no containers.
        app.MapGet("/api/docker", (string? host, LiveBroadcaster bc, HubRuntime rt) =>
        {
            var key = string.IsNullOrWhiteSpace(host) ? rt.HostName : host;
            var list = bc.Containers(key);
            return Results.Json(new { host = key, available = list is not null, containers = list ?? new() }, MetricsJson.Options);
        });

        // Custom UI templates discovered on disk (for the Settings template selector).
        app.MapGet("/api/templates", (HubRuntime rt) =>
        {
            var list = TemplateStore.List(rt.TemplatesDir).Select(t => new
            {
                id = t.Id, name = t.Name, description = t.Description, author = t.Author,
                version = t.Version, hasScript = t.HasScript, hasCss = t.HasCss, hasPreview = t.HasPreview,
            });
            return Results.Json(list, MetricsJson.Options);
        });

        // Serve a file from a template folder, e.g. /templates/MyTheme/style.css. Path-traversal safe.
        app.MapGet("/templates/{id}/{**rest}", (string id, string rest, HubRuntime rt) =>
        {
            var path = TemplateStore.ResolveFile(rt.TemplatesDir, id, rest);
            if (path is null) return Results.NotFound();
            if (!new FileExtensionContentTypeProvider().TryGetContentType(path, out var ct))
                ct = "application/octet-stream";
            return Results.File(File.OpenRead(path), ct);
        });

        // Custom widgets discovered on disk (for the swipe-down Widgets page chooser).
        app.MapGet("/api/widgets", (HubRuntime rt) =>
        {
            var list = WidgetStore.List(rt.WidgetsDir).Select(w => new
            {
                id = w.Id, name = w.Name, description = w.Description, author = w.Author,
                version = w.Version, tag = w.Tag, hasCss = w.HasCss, hasIcon = w.HasIcon, hasPreview = w.HasPreview,
            });
            return Results.Json(list, MetricsJson.Options);
        });

        // Serve a file from a widget folder, e.g. /widgets/nexusm-music/widget.js. Path-traversal safe.
        app.MapGet("/widgets/{id}/{**rest}", (string id, string rest, HubRuntime rt) =>
        {
            var path = WidgetStore.ResolveFile(rt.WidgetsDir, id, rest);
            if (path is null) return Results.NotFound();
            if (!new FileExtensionContentTypeProvider().TryGetContentType(path, out var ct))
                ct = "application/octet-stream";
            return Results.File(File.OpenRead(path), ct);
        });

        // LAN discovery for the NexusM player widget. Browsers can't send UDP broadcasts, so the
        // kiosk asks the hub to probe; returns the NexusM server URL(s) advertised on the network.
        app.MapGet("/api/widget/nexusm/discover", async (CancellationToken ct) =>
        {
            var servers = await NexusmDiscovery.DiscoverAsync(ct: ct);
            return Results.Json(new { servers }, MetricsJson.Options);
        });

        // Reachability probe for the Uptime widget. Browsers can't do raw/cross-origin pings, so the
        // hub checks one user-supplied URL and reports up/down + latency.
        app.MapGet("/api/widget/uptime", async (string? url, CancellationToken ct) =>
        {
            var r = await UptimeChecker.CheckAsync(url ?? "", ct);
            return Results.Json(new { ok = r.Ok, status = r.Status, ms = r.Ms, error = r.Error }, MetricsJson.Options);
        });

        // Pi-hole summary proxy (Pi-hole's API isn't CORS-enabled, so the hub fetches it). The Pi-hole
        // base URL + password come from the browser's localStorage on the query string.
        app.MapGet("/api/widget/pihole", async (string? @base, string? key, CancellationToken ct) =>
        {
            var data = await PiholeClient.SummaryAsync(@base ?? "", key ?? "", ct);
            return Results.Json(data, MetricsJson.Options);
        });

        // Update settings (persisted to michka.conf). A port change restarts the hub + kiosk;
        // display changes just persist on the device.
        app.MapPost("/api/server-config", async (HttpContext ctx, HubRuntime rt, AuthState auth, ILoggerFactory lf) =>
        {
            if (Security.RequireBrowserOrToken(ctx, rt.Config.Token) is { } deny) return deny;

            ServerConfigUpdate? body;
            try { body = await JsonSerializer.DeserializeAsync<ServerConfigUpdate>(ctx.Request.Body, MetricsJson.Options); }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid json" }); }
            if (body is null) return Results.BadRequest(new { error = "empty body" });

            bool restart = false, changed = false;
            if (body.Port is int p)
            {
                if (p < 1 || p > 65535) return Results.BadRequest(new { error = "port must be 1..65535" });
                if (p != rt.Port) { rt.Config.Port = p; restart = true; changed = true; }
            }
            if (body.TokenRequired is { } tr) { rt.Config.TokenRequired = tr == "off" ? "off" : "on"; changed = true; }
            if (body.RegenToken == true) { rt.Config.Token = Security.NewShortToken(); changed = true; }
            // UI PIN: enabling requires a fresh 6-digit PIN (hashed); disabling clears it. Changing the
            // PIN while enabled re-hashes. Enabling/changing keeps the CURRENT browser logged in (issues
            // a session) so the admin never locks themselves out; disabling drops every session.
            if (body.PinEnabled is { } pe)
            {
                if (pe == "on")
                {
                    var pin = body.Pin ?? "";
                    if (pin.Length != 6 || !pin.All(char.IsDigit))
                        return Results.BadRequest(new { error = "pin must be 6 digits" });
                    rt.Config.PinHash = Security.HashPin(pin);
                    rt.Config.PinEnabled = "on";
                    auth.ClearSessions();           // invalidate any old sessions on a PIN change
                    IssueSession(ctx, auth);        // keep this browser in
                    changed = true;
                }
                else
                {
                    rt.Config.PinEnabled = "off";
                    rt.Config.PinHash = "";
                    auth.ClearSessions();
                    changed = true;
                }
            }
            if (body.Template is { } tpl) { rt.Config.Template = tpl; changed = true; }
            if (body.Lang is { } lng) { rt.Config.Lang = lng; changed = true; }
            if (body.TempUnit is { } tu) { rt.Config.TempUnit = tu; changed = true; }
            if (body.Cores is { } co) { rt.Config.Cores = co; changed = true; }
            if (body.Cycle is { } cy) { rt.Config.Cycle = cy; changed = true; }
            if (body.CycleSec is { } cs) { rt.Config.CycleSec = cs; changed = true; }
            if (body.DateFmt is { } dfm) { rt.Config.DateFmt = dfm; changed = true; }
            if (body.ScreenSize is { } scr) { rt.Config.ScreenSize = scr; changed = true; }
            if (body.SsMode is { } sm) { rt.Config.SsMode = sm; changed = true; }
            if (body.SsTimeout is { } st) { rt.Config.SsTimeout = st; changed = true; }
            if (body.KioskAuto is { } ka)
            {
                rt.Config.KioskAuto = ka; changed = true;
                SystemControl.SetKioskAutostart(ka == "on", lf.CreateLogger("config"));
            }
            if (body.Services is { } svc)
            {
                // With a host: that host's monitored set. Without: the legacy global list.
                if (body.ServicesHost is { Length: > 0 } sh) rt.Config.ServicesByHost[sh] = svc;
                else rt.Config.Services = svc;
                changed = true;
            }
            if (body.Layout is { } lay)
            {
                // With a host: store the per-host layout. Without: the global default/seed (legacy).
                if (body.LayoutHost is { Length: > 0 } lh) rt.Config.Layouts[lh] = lay;
                else rt.Config.Layout = lay;
                changed = true;
            }
            // Widgets-page placements for a host (the ordered widget type list).
            if (body.Widgets is { } wlist && body.WidgetsHost is { Length: > 0 } whost)
            {
                rt.Config.WidgetsByHost[whost] = wlist;
                changed = true;
            }
            // Per-widget-type config blobs — merged per type so an update to one widget doesn't clobber
            // others (and concurrent browsers don't stomp each other's unrelated widgets).
            if (body.WidgetConfig is { } wcfg)
            {
                foreach (var kv in wcfg) rt.Config.WidgetConfig[kv.Key] = kv.Value;
                changed = true;
            }

            if (changed)
            {
                try { rt.Config.Save(); }
                catch (Exception ex) { return Results.Problem($"could not save config: {ex.Message}"); }
            }
            if (restart) SystemControl.ScheduleConfigRestart(lf.CreateLogger("config"));

            return Results.Json(new { ok = true, restarting = restart }, MetricsJson.Options);
        });

        // Exit the on-screen UI and return tty1 to the Linux login prompt.
        app.MapPost("/api/system/exit-ui", (HttpContext ctx, HubRuntime rt, ILoggerFactory lf) =>
        {
            if (Security.RequireBrowserOrToken(ctx, rt.Config.Token) is { } deny) return deny;

            bool ok = SystemControl.ExitUiToLogin(lf.CreateLogger("system"));
            return ok
                ? Results.Json(new { ok = true })
                : Results.Json(new { ok = false, error = "only supported on the Linux kiosk" }, statusCode: 400);
        });

        // Screensaver "sleep" mode: power the kiosk panel off/on (best-effort, Linux/wlroots only).
        app.MapPost("/api/system/screen", async (HttpContext ctx, HubRuntime rt, ILoggerFactory lf) =>
        {
            if (Security.RequireBrowserOrToken(ctx, rt.Config.Token) is { } deny) return deny;

            ScreenPower? body;
            try { body = await JsonSerializer.DeserializeAsync<ScreenPower>(ctx.Request.Body, MetricsJson.Options); }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid json" }); }
            bool on = body?.On ?? true;
            bool ok = SystemControl.SetScreenPower(on, lf.CreateLogger("system"));
            return Results.Json(new { ok, on });
        });

        // Server-Sent Events live stream.
        app.MapGet("/api/stream", async (HttpContext ctx, LiveBroadcaster bc, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";
            ctx.Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering

            using var sub = bc.Subscribe();

            // Prime the new client with the latest snapshot for every known host.
            foreach (var snap in bc.Latest.Values)
                await WriteEvent(ctx, JsonSerializer.Serialize(snap, MetricsJson.Options), ct);

            try
            {
                await foreach (var json in sub.Reader.ReadAllAsync(ct))
                    await WriteEvent(ctx, json, ct);
            }
            catch (OperationCanceledException)
            {
                // client disconnected — normal
            }
        });
    }

    /// <summary>Open a PIN session and set its httpOnly, session-scoped cookie (no Max-Age, so it ends
    /// when the browser closes). SameSite=Strict; Secure left off since the hub is plain HTTP on the LAN.</summary>
    private static void IssueSession(HttpContext ctx, AuthState auth)
    {
        var sid = auth.NewSession();
        ctx.Response.Cookies.Append("michka_sid", sid, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = false,
            Path = "/",
        });
    }

    private static async Task WriteEvent(HttpContext ctx, string json, CancellationToken ct)
    {
        // SSE framing: "data: <payload>\n\n"
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await ctx.Response.Body.WriteAsync(bytes, ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    /// <summary>Partial settings update from the Settings page (only set fields are applied).</summary>
    private sealed class ServerConfigUpdate
    {
        public int? Port { get; set; }
        public string? TokenRequired { get; set; }
        public bool? RegenToken { get; set; }
        public string? PinEnabled { get; set; }
        public string? Pin { get; set; }
        public string? Template { get; set; }
        public string? Lang { get; set; }
        public string? TempUnit { get; set; }
        public string? Cores { get; set; }
        public string? Cycle { get; set; }
        public string? CycleSec { get; set; }
        public string? DateFmt { get; set; }
        public string? ScreenSize { get; set; }
        public string? SsMode { get; set; }
        public string? SsTimeout { get; set; }
        public string? KioskAuto { get; set; }
        public List<string>? Services { get; set; }
        public string? ServicesHost { get; set; }
        public List<LayoutBox>? Layout { get; set; }
        public string? LayoutHost { get; set; }
        public List<string>? Widgets { get; set; }
        public string? WidgetsHost { get; set; }
        public Dictionary<string, JsonElement>? WidgetConfig { get; set; }
    }

    /// <summary>Panel power request from the screensaver "sleep" mode.</summary>
    private sealed class ScreenPower
    {
        public bool On { get; set; } = true;
    }

    /// <summary>PIN submission from the keypad login page.</summary>
    private sealed class LoginBody
    {
        public string? Pin { get; set; }
    }

    /// <summary>Host-removal request from the dashboard (long-press a host tab).</summary>
    private sealed class HostDelete
    {
        public string? Host { get; set; }
    }
}

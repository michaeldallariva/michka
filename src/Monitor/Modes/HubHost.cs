using Microsoft.Extensions.FileProviders;
using Monitor.Api;
using Monitor.Cli;
using Monitor.Metrics;
using Monitor.Realtime;
using Monitor.Services;
using Monitor.Storage;

namespace Monitor.Modes;

/// <summary>
/// Hub mode: hosts the Kestrel web server + dashboard, collects local metrics, accepts agent
/// pushes, persists to SQLite, and streams live updates to browsers over SSE.
/// </summary>
public static class HubHost
{
    public static async Task<int> RunAsync(Options opt)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        // Quiet per-request logging — this runs 24/7 on a kiosk; keep only warnings+ from the framework.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

        // Settings live next to the db in michka.conf (created on first run). CLI --port overrides.
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(opt.DbPath)) ?? ".";

        // If this is a freshly-deployed binary, wipe the kiosk Chromium cache up front so the new
        // embedded UI assets can't be served stale from the browser's persistent profile. Done before
        // anything else (with a throwaway boot logger) so a following `systemctl restart michka-kiosk`
        // relaunches chromium against an already-empty cache. No-op on same-binary restarts / off Linux.
        using (var bootLog = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })))
            SystemControl.ClearKioskCacheOnUpgrade(dataDir, bootLog.CreateLogger("kiosk"));

        var configPath = Path.Combine(dataDir, "michka.conf");
        var config = ServerConfig.Load(configPath);
        int port = opt.PortSpecified ? opt.Port : (config.Port ?? 5000);

        // Custom UI templates live on disk (the embedded wwwroot can't be added to), so users can
        // drop template folders into <dataDir>/templates without rebuilding. Created on first run.
        var templatesDir = Path.Combine(dataDir, "templates");
        TemplateStore.EnsureDir(templatesDir);

        // Custom widgets are on-disk drop-ins too (same model as templates): users add widget folders
        // under <dataDir>/widgets without rebuilding. Created on first run.
        var widgetsDir = Path.Combine(dataDir, "widgets");
        WidgetStore.EnsureDir(widgetsDir);

        var runtime = new HubRuntime
        {
            Port = port,
            HostName = string.IsNullOrWhiteSpace(opt.Name) ? Environment.MachineName : opt.Name!,
            DbPath = opt.DbPath,
            Config = config,
            TemplatesDir = templatesDir,
            WidgetsDir = widgetsDir,
        };

        // Monitored services are tracked per-host.
        // Migrate the legacy list into this host's entry once, so existing setups keep their boxes.
        if (config.Services.Count > 0 && !config.ServicesByHost.ContainsKey(runtime.HostName))
        {
            config.ServicesByHost[runtime.HostName] = config.Services;
            try { config.Save(); } catch { /* best-effort migration */ }
        }

        // Listen on all interfaces so the rack screen and LAN clients can reach it.
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        builder.Services.AddSingleton(new MetricsStore(opt.DbPath));
        builder.Services.AddSingleton<Monitor.Api.AuthState>();
        builder.Services.AddSingleton<LiveBroadcaster>();
        builder.Services.AddSingleton(opt);
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton(ServiceInspectorFactory.Create());
        builder.Services.AddHostedService<HubBackgroundService>();

        var app = builder.Build();

        // Security guards (the listener is open on 0.0.0.0, with no user login): reject unexpected
        // Host headers (DNS-rebinding) and require JSON bodies on mutating /api calls (form/no-cors
        // CSRF). Per-endpoint token / same-origin checks live in the endpoint handlers.
        app.Use(Monitor.Api.Security.HostAllowlist(config.HostAllow));
        app.Use(Monitor.Api.Security.JsonContentType);

        // Optional UI PIN gate: when enabled, every browser request needs a valid PIN session, except
        // the login page + its API and agent ingest (which is token-authenticated, not PIN). An
        // unauthenticated browser navigation is redirected to /login; an unauthenticated API/XHR gets a
        // 401 so the SPA can redirect. Reads config.PinEnabled live, so toggling it takes effect at once.
        var auth = app.Services.GetRequiredService<Monitor.Api.AuthState>();
        app.Use(async (ctx, next) =>
        {
            if (config.PinEnabled != "on") { await next(ctx); return; }
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/api/login") || path.StartsWithSegments("/api/logout")
                || path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/api/ingest")
                || path.Equals("/login", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            { await next(ctx); return; }

            if (auth.ValidSession(ctx.Request.Cookies["michka_sid"])) { await next(ctx); return; }

            if (path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "pin_required" });
            }
            else
            {
                ctx.Response.Redirect("/login");
            }
        });

        // Serve the dashboard from resources embedded in the binary (true single-file deploy).
        var fileProvider = new ManifestEmbeddedFileProvider(typeof(HubHost).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });

        app.MapApi();
        // The PIN keypad login page (self-contained). Reachable even when the gate is on (it is how you
        // get past it); when the gate is off it simply bounces back to the dashboard.
        app.MapGet("/login", () => Results.Stream(
            fileProvider.GetFileInfo("login.html").CreateReadStream(), "text/html"));
        app.MapFallback(() => Results.Stream(
            fileProvider.GetFileInfo("index.html").CreateReadStream(), "text/html"));

        var log = app.Services.GetRequiredService<ILogger<object>>();
        log.LogInformation("michka hub listening on http://0.0.0.0:{Port}  (db: {Db})", port, opt.DbPath);
        log.LogInformation("agent push token (set 'token' in each agent's michka_c.conf): {Token}  [required: {Req}]",
            config.Token, config.TokenRequired);

        await app.RunAsync();
        return 0;
    }
}

/// <summary>Local metric sampling loop + periodic history pruning.</summary>
internal sealed class HubBackgroundService : BackgroundService
{
    private readonly Options _opt;
    private readonly LiveBroadcaster _broadcaster;
    private readonly MetricsStore _store;
    private readonly HubRuntime _runtime;
    private readonly IServiceInspector _services;
    private readonly ILogger<HubBackgroundService> _log;

    private static readonly long RetentionMs = 7L * 24 * 60 * 60 * 1000; // 7 days (host reports span up to a week)

    // Service status is polled less often than metrics (spawning systemctl every tick is wasteful).
    private List<Monitor.Services.ServiceState> _svcCache = new();
    private long _svcAtMs;
    private const long SvcPollMs = 2500;

    // Docker containers for the hub's own host, attached on the same ~10s cadence as the catalog.
    private readonly Monitor.Services.DockerInspector _docker = new();
    private long _dockerAtMs = -CatalogIntervalMs;

    public HubBackgroundService(Options opt, LiveBroadcaster broadcaster, MetricsStore store,
        HubRuntime runtime, IServiceInspector services, ILogger<HubBackgroundService> log)
    {
        _opt = opt;
        _broadcaster = broadcaster;
        _store = store;
        _runtime = runtime;
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var sampleTask = _opt.NoLocal ? Task.CompletedTask : SampleLoop(ct);
        var pruneTask = PruneLoop(ct);
        await Task.WhenAll(sampleTask, pruneTask);
    }

    // The local catalog rides along only every ~10s — same cadence as the agents (see AgentRunner).
    private const long CatalogIntervalMs = 10_000;
    private long _catalogAtMs = -CatalogIntervalMs;

    private async Task SampleLoop(CancellationToken ct)
    {
        var collector = MetricsCollectorFactory.Create(_opt.Name);
        var hostName = collector.HostName;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_opt.IntervalMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var snap = collector.Sample();
                snap.Services = SampleServices(hostName);

                long nowMs = Environment.TickCount64;
                if (_services.Available && nowMs - _catalogAtMs >= CatalogIntervalMs)
                {
                    try { snap.ServiceCatalog = _services.List().ToList(); _catalogAtMs = nowMs; }
                    catch (Exception ex) { _log.LogWarning(ex, "service catalog failed"); }
                }
                if (_docker.Available && nowMs - _dockerAtMs >= CatalogIntervalMs)
                {
                    try { snap.Containers = _docker.List().ToList(); _dockerAtMs = nowMs; }
                    catch (Exception ex) { _log.LogWarning(ex, "docker list failed"); }
                }

                _broadcaster.Publish(snap);
            }
            catch (Exception ex) { _log.LogWarning(ex, "local sample failed"); }
        }
    }

    /// <summary>Live status of this host's monitored services, refreshed at most every <see cref="SvcPollMs"/>.</summary>
    private List<Monitor.Services.ServiceState> SampleServices(string hostName)
    {
        var names = _runtime.Config.ServicesForHost(hostName);
        if (names.Count == 0) return new();

        long now = Environment.TickCount64;
        if (now - _svcAtMs >= SvcPollMs)
        {
            try { _svcCache = _services.Status(names).ToList(); }
            catch (Exception ex) { _log.LogWarning(ex, "service status failed"); }
            _svcAtMs = now;
        }
        return _svcCache;
    }

    private async Task PruneLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                int n = _store.Prune(RetentionMs);
                if (n > 0) _log.LogInformation("pruned {N} old samples", n);
            }
            catch (Exception ex) { _log.LogWarning(ex, "prune failed"); }
        }
    }
}

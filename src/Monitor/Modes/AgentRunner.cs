using System.Net.Http.Json;
using Monitor.Cli;
using Monitor.Metrics;
using Monitor.Services;
using Monitor.Storage;

namespace Monitor.Modes;

/// <summary>
/// Agent mode: sample local metrics and POST them to a hub on an interval. No web server.
/// Resilient to a hub that is temporarily unreachable (logs and retries next tick). Settings come
/// from <c>michka_c.conf</c> next to the binary, with any command-line flags overriding the file.
/// The core loop (<see cref="RunLoopAsync"/>) is shared by the console, the GUI, and the service.
/// </summary>
public static class AgentRunner
{
    /// <summary>The effective settings after merging <c>michka_c.conf</c> with command-line overrides.</summary>
    public readonly record struct Resolved(string? HubUrl, string? Name, int IntervalMs);

    /// <summary>CLI flags win over the conf file; the conf file fills in anything not passed.</summary>
    public static Resolved Resolve(Options opt, AgentConfig cfg)
    {
        var hub = !string.IsNullOrWhiteSpace(opt.Hub) ? opt.Hub
                : cfg.HasHub ? cfg.HubUrl
                : null;
        var name = !string.IsNullOrWhiteSpace(opt.Name) ? opt.Name
                 : !string.IsNullOrWhiteSpace(cfg.Name) ? cfg.Name
                 : null;
        var interval = opt.IntervalSpecified ? opt.IntervalMs : cfg.IntervalMs;
        if (interval < 200) interval = 200;
        return new Resolved(hub, name, interval);
    }

    /// <summary>Console entry point: resolve settings from conf + CLI, then run until Ctrl+C.</summary>
    public static async Task<int> RunAsync(Options opt)
    {
        var cfg = AgentConfig.Load();
        var r = Resolve(opt, cfg);
        if (string.IsNullOrWhiteSpace(r.HubUrl))
        {
            Console.Error.WriteLine($"no hub configured. Set one in {cfg.Path} (host/port), pass --hub <url>,");
            Console.Error.WriteLine("or run the Windows client with no arguments to open the settings GUI.");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try { await RunLoopAsync(r.HubUrl!, r.Name, r.IntervalMs, Console.WriteLine, cts.Token); }
        catch (OperationCanceledException) { /* Ctrl+C */ }

        Console.WriteLine("agent stopped");
        return 0;
    }

    /// <summary>
    /// The push loop: sample local metrics and POST them to <paramref name="hubUrl"/> every
    /// <paramref name="intervalMs"/>. Status/error lines go to <paramref name="log"/>. Runs until the
    /// token is cancelled. Shared by the console agent, the GUI's Start button, and the service.
    /// </summary>
    public static async Task RunLoopAsync(string hubUrl, string? name, int intervalMs, Action<string> log, CancellationToken ct)
    {
        var hub = hubUrl.TrimEnd('/');
        var ingestUrl = $"{hub}/api/ingest";
        var collector = MetricsCollectorFactory.Create(name);
        var services = ServiceInspectorFactory.Create();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        log($"Monitor agent '{collector.HostName}' -> {ingestUrl} every {intervalMs}ms");

        bool wasDown = false;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));

        // Prime the rate-based collectors so the first pushed sample is non-zero.
        collector.Sample();

        // The service catalog rides along only every ~10s (enumerating services is heavier than a
        // metrics sample, and the list barely changes); -CatalogIntervalMs forces it onto the first push.
        const long catalogIntervalMs = 10_000;
        long lastCatalogMs = -catalogIntervalMs;

        while (await timer.WaitForNextTickAsync(ct))
        {
            MetricSnapshot snap;
            try { snap = collector.Sample(); }
            catch (Exception ex) { log($"sample failed: {ex.Message}"); continue; }

            long nowMs = Environment.TickCount64;
            if (services.Available && nowMs - lastCatalogMs >= catalogIntervalMs)
            {
                try { snap.ServiceCatalog = services.List().ToList(); lastCatalogMs = nowMs; }
                catch (Exception ex) { log($"service catalog failed: {ex.Message}"); }
            }

            try
            {
                using var resp = await http.PostAsJsonAsync(ingestUrl, snap, MetricsJson.Options, ct);
                if (!resp.IsSuccessStatusCode)
                    log($"hub returned {(int)resp.StatusCode}");
                else if (wasDown)
                {
                    log("hub reachable again");
                    wasDown = false;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine shutdown — let the loop unwind
            }
            catch (Exception ex)
            {
                // Anything else (connection refused, DNS failure, or an HttpClient *timeout* — which
                // surfaces as a TaskCanceledException even though our token wasn't cancelled) just
                // means the hub is unreachable this tick. Keep looping so we reconnect on our own.
                if (!wasDown)
                {
                    log($"hub unreachable ({ex.Message}); will keep retrying");
                    wasDown = true;
                }
            }
        }
    }
}

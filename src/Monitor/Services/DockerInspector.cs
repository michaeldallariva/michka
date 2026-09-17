using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Monitor.Services;

/// <summary>
/// A Docker container and its live resource usage, shipped inside a <c>MetricSnapshot</c> for the
/// Docker widget. Usage fields come from <c>docker stats</c> (running containers only); the rest from
/// <c>docker ps -a</c>. Fields that can't be read stay at their defaults.
/// </summary>
public sealed class DockerContainer
{
    /// <summary>Short (12-char) container id.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";

    /// <summary>Lifecycle state: running | exited | created | paused | restarting | dead.</summary>
    public string State { get; set; } = "";

    /// <summary>Human status line, e.g. "Up 3 hours" / "Exited (0) 2 days ago".</summary>
    public string Status { get; set; } = "";

    /// <summary>Health, parsed from the status line: healthy | unhealthy | starting | "" (none).</summary>
    public string Health { get; set; } = "";

    /// <summary>Live CPU usage 0..100+ (a busy multi-core container can exceed 100). -1 = unknown.</summary>
    public double CpuPct { get; set; } = -1;

    public long MemUsedBytes { get; set; }
    public long MemLimitBytes { get; set; }

    /// <summary>Memory usage 0..100. -1 = unknown.</summary>
    public double MemPct { get; set; } = -1;

    public long NetRxBytes { get; set; }
    public long NetTxBytes { get; set; }
}

/// <summary>
/// Enumerates Docker containers on the host by shelling the <c>docker</c> CLI (the same approach the
/// service inspectors use for systemd/sc.exe). Works on any host with Docker on PATH and the daemon
/// reachable; reports <see cref="Available"/> = false otherwise. Cross-platform (Linux and Windows).
/// </summary>
public sealed class DockerInspector
{
    private bool? _available;

    /// <summary>True when the docker CLI + daemon answer. Probed once and cached.</summary>
    public bool Available => _available ??= Probe();

    private static bool Probe()
    {
        // `docker version` of the *server* only succeeds when the daemon is reachable.
        var v = Run(6000, "version", "--format", "{{.Server.Version}}");
        return !string.IsNullOrWhiteSpace(v);
    }

    /// <summary>The current container list with live usage. Empty list = Docker present, no containers.</summary>
    public IReadOnlyList<DockerContainer> List()
    {
        var ps = Run(6000, "ps", "-a", "--no-trunc", "--format", "{{json .}}");
        var byId = new Dictionary<string, DockerContainer>(StringComparer.Ordinal);
        var order = new List<DockerContainer>();

        foreach (var line in SplitLines(ps))
        {
            DockerPs? row;
            try { row = JsonSerializer.Deserialize<DockerPs>(line); }
            catch { continue; }
            if (row is null) continue;

            var id = Short(row.ID);
            var c = new DockerContainer
            {
                Id = id,
                Name = FirstName(row.Names),
                Image = row.Image ?? "",
                State = (row.State ?? "").ToLowerInvariant(),
                Status = row.Status ?? "",
                Health = ParseHealth(row.Status ?? ""),
            };
            byId[id] = c;
            order.Add(c);
        }

        // Merge live usage for running containers. `docker stats` only lists running ones.
        var stats = Run(6000, "stats", "--no-stream", "--format", "{{json .}}");
        foreach (var line in SplitLines(stats))
        {
            DockerStat? s;
            try { s = JsonSerializer.Deserialize<DockerStat>(line); }
            catch { continue; }
            if (s is null) continue;
            if (!byId.TryGetValue(Short(s.ID), out var c)) continue;

            c.CpuPct = ParsePct(s.CPUPerc);
            c.MemPct = ParsePct(s.MemPerc);
            var (mu, ml) = ParsePair(s.MemUsage);
            c.MemUsedBytes = mu; c.MemLimitBytes = ml;
            var (rx, tx) = ParsePair(s.NetIO);
            c.NetRxBytes = rx; c.NetTxBytes = tx;
        }

        return order;
    }

    /* ---------------- CLI plumbing ---------------- */

    // Run the docker CLI with a hard timeout (kills a wedged daemon call instead of blocking the
    // sampler). Returns stdout, or "" on any failure/timeout.
    private static string Run(int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return "";
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { /* ignore */ } return ""; }
            return stdout.GetAwaiter().GetResult();
        }
        catch { return ""; }
    }

    private static IEnumerable<string> SplitLines(string s) =>
        string.IsNullOrEmpty(s)
            ? Array.Empty<string>()
            : s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "" : (id.Length > 12 ? id[..12] : id);

    // ps "Names" can be a comma-separated list; take the first and drop any leading slash.
    private static string FirstName(string? names)
    {
        if (string.IsNullOrEmpty(names)) return "";
        var n = names.Split(',', 2)[0].Trim();
        return n.StartsWith('/') ? n[1..] : n;
    }

    private static string ParseHealth(string status)
    {
        if (status.Contains("(healthy)", StringComparison.OrdinalIgnoreCase)) return "healthy";
        if (status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase)) return "unhealthy";
        if (status.Contains("health: starting", StringComparison.OrdinalIgnoreCase)) return "starting";
        return "";
    }

    // "0.15%" -> 0.15 ; -1 on parse failure.
    private static double ParsePct(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return -1;
        s = s.Trim().TrimEnd('%').Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
    }

    // "12.3MiB / 1.952GiB" or "1.2kB / 3.4kB" -> (left bytes, right bytes).
    private static (long, long) ParsePair(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (0, 0);
        var parts = s.Split('/', 2);
        long a = ParseSize(parts[0]);
        long b = parts.Length > 1 ? ParseSize(parts[1]) : 0;
        return (a, b);
    }

    // Parse a docker size token like "12.3MiB", "1.952GiB", "1.2kB", "512B". Binary ("iB") = 1024-based,
    // decimal ("B") = 1000-based, matching docker's own formatting.
    private static long ParseSize(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;
        token = token.Trim();
        int i = 0;
        while (i < token.Length && (char.IsDigit(token[i]) || token[i] == '.' || token[i] == '-')) i++;
        if (i == 0) return 0;
        if (!double.TryParse(token[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return 0;

        var unit = token[i..].Trim().ToUpperInvariant();
        bool binary = unit.Contains('I');           // KiB/MiB/GiB/TiB
        char prefix = unit.Length > 0 ? unit[0] : 'B';
        double mult = prefix switch
        {
            'K' => binary ? 1024d : 1000d,
            'M' => binary ? 1024d * 1024 : 1_000_000d,
            'G' => binary ? 1024d * 1024 * 1024 : 1_000_000_000d,
            'T' => binary ? 1024d * 1024 * 1024 * 1024 : 1_000_000_000_000d,
            _ => 1d,                                  // plain "B"
        };
        return (long)(num * mult);
    }

    /* ---------------- docker JSON rows ---------------- */

    private sealed class DockerPs
    {
        public string? ID { get; set; }
        public string? Names { get; set; }
        public string? Image { get; set; }
        public string? State { get; set; }
        public string? Status { get; set; }
    }

    private sealed class DockerStat
    {
        public string? ID { get; set; }
        public string? CPUPerc { get; set; }
        public string? MemPerc { get; set; }
        public string? MemUsage { get; set; }
        public string? NetIO { get; set; }
    }
}

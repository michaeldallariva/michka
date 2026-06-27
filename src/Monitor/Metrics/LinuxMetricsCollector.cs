using System.Globalization;

namespace Monitor.Metrics;

/// <summary>
/// Linux metrics via the <c>/proc</c> and <c>/sys</c> pseudo-filesystems. CPU is computed from the
/// jiffies delta between consecutive <see cref="Sample"/> calls.
/// </summary>
public sealed class LinuxMetricsCollector : IMetricsCollector
{
    private readonly NetworkSampler _net = new();
    private readonly DiskSampler _disk = new();

    private long[]? _prevTotal;   // index 0 = aggregate, 1..N = per core
    private long[]? _prevIdle;

    private string? _cpuModel;    // parsed once from /proc/cpuinfo (static for the life of the box)
    private int _cpuCores;        // physical cores; 0 if undeterminable

    public string HostName { get; }

    public LinuxMetricsCollector(string? hostName)
    {
        HostName = string.IsNullOrWhiteSpace(hostName) ? System.Environment.MachineName : hostName!;
    }

    public MetricSnapshot Sample()
    {
        var snap = new MetricSnapshot
        {
            Host = HostName,
            Os = "linux",
            TsUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Cpu = ReadCpu(),
            Mem = ReadMem(),
            Net = _net.Sample(),
            Disks = _disk.Sample(),
            Temps = ReadTemps(),
            Load = ReadLoad(),
            UptimeSec = ReadUptime(),
        };
        return snap;
    }

    private CpuInfo ReadCpu()
    {
        var info = new CpuInfo();
        string[] lines;
        try { lines = File.ReadAllLines("/proc/stat"); }
        catch { return info; }

        // Collect "cpu" (aggregate) and "cpuN" lines in order: index 0 aggregate, then cores.
        var totals = new List<long>();
        var idles = new List<long>();

        foreach (var line in lines)
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal)) break; // cpu lines are first
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            // parts[0] = "cpu" or "cpuN"; the rest are jiffies counters.
            long sum = 0;
            for (int i = 1; i < parts.Length; i++)
            {
                if (long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                    sum += v;
            }
            // idle = field 4 (idle) + field 5 (iowait) when present.
            long idle = 0;
            if (long.TryParse(parts[4], out var idleJ)) idle += idleJ;
            if (parts.Length > 5 && long.TryParse(parts[5], out var iowait)) idle += iowait;

            totals.Add(sum);
            idles.Add(idle);
        }

        var curTotal = totals.ToArray();
        var curIdle = idles.ToArray();

        if (_prevTotal is { } pt && _prevIdle is { } pi && pt.Length == curTotal.Length)
        {
            var pct = new double[curTotal.Length];
            for (int i = 0; i < curTotal.Length; i++)
            {
                long dt = curTotal[i] - pt[i];
                long di = curIdle[i] - pi[i];
                pct[i] = dt > 0 ? Math.Clamp((dt - di) * 100.0 / dt, 0, 100) : 0;
            }
            info.TotalPct = pct.Length > 0 ? pct[0] : 0;
            info.PerCorePct = pct.Length > 1 ? pct[1..] : Array.Empty<double>();
        }

        _prevTotal = curTotal;
        _prevIdle = curIdle;

        EnsureCpuStatic();
        info.Model = _cpuModel ?? "";
        info.Cores = _cpuCores;
        info.Threads = Math.Max(0, totals.Count - 1); // cpuN lines (aggregate "cpu" excluded)
        return info;
    }

    /// <summary>Parse the static CPU identity from <c>/proc/cpuinfo</c> exactly once.</summary>
    private void EnsureCpuStatic()
    {
        if (_cpuModel != null) return;
        string model = "";
        int cpuCoresField = 0;
        var physicalCores = new HashSet<string>(StringComparer.Ordinal);
        string curPhys = "0", curCore = "";

        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                int colon = line.IndexOf(':');
                if (colon < 0)
                {
                    // Blank line separates per-processor blocks — record this block's (socket, core).
                    if (curCore.Length > 0) physicalCores.Add(curPhys + "/" + curCore);
                    curCore = ""; curPhys = "0";
                    continue;
                }
                var key = line[..colon].Trim();
                var val = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "model name": if (model.Length == 0) model = val; break;
                    case "physical id": curPhys = val; break;
                    case "core id": curCore = val; break;
                    case "cpu cores": int.TryParse(val, out cpuCoresField); break;
                }
            }
            if (curCore.Length > 0) physicalCores.Add(curPhys + "/" + curCore);
        }
        catch { /* best effort */ }

        _cpuModel = CpuInfo.CleanModel(model);
        _cpuCores = physicalCores.Count > 0 ? physicalCores.Count : cpuCoresField;
    }

    private static MemInfo ReadMem()
    {
        var info = new MemInfo();
        Dictionary<string, long> kv;
        try
        {
            kv = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon];
                var rest = line[(colon + 1)..].Trim();
                var num = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (num.Length > 0 && long.TryParse(num[0], out var kb))
                    kv[key] = kb * 1024; // values are kB
            }
        }
        catch { return info; }

        kv.TryGetValue("MemTotal", out var total);
        kv.TryGetValue("MemAvailable", out var avail);
        kv.TryGetValue("SwapTotal", out var swapTotal);
        kv.TryGetValue("SwapFree", out var swapFree);

        info.TotalBytes = total;
        info.UsedBytes = Math.Max(0, total - avail);
        info.Pct = total > 0 ? info.UsedBytes * 100.0 / total : 0;
        info.SwapTotalBytes = swapTotal;
        info.SwapUsedBytes = Math.Max(0, swapTotal - swapFree);
        return info;
    }

    private static List<TempInfo> ReadTemps()
    {
        var temps = new List<TempInfo>();
        const string root = "/sys/class/thermal";
        if (!Directory.Exists(root)) return temps;

        try
        {
            foreach (var zone in Directory.EnumerateDirectories(root, "thermal_zone*"))
            {
                var tempFile = Path.Combine(zone, "temp");
                if (!File.Exists(tempFile)) continue;
                if (!long.TryParse(File.ReadAllText(tempFile).Trim(), out var milli)) continue;

                string label = Path.GetFileName(zone);
                var typeFile = Path.Combine(zone, "type");
                if (File.Exists(typeFile))
                {
                    var t = File.ReadAllText(typeFile).Trim();
                    if (!string.IsNullOrEmpty(t)) label = t;
                }

                temps.Add(new TempInfo { Label = label, Celsius = milli / 1000.0 });
            }
        }
        catch { /* thermal zones not always readable */ }

        return temps;
    }

    private static LoadInfo? ReadLoad()
    {
        try
        {
            var parts = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var one)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var five)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fifteen))
            {
                return new LoadInfo { One = one, Five = five, Fifteen = fifteen };
            }
        }
        catch { }
        return null;
    }

    private static long ReadUptime()
    {
        try
        {
            var parts = File.ReadAllText("/proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
                return (long)sec;
        }
        catch { }
        return 0;
    }
}

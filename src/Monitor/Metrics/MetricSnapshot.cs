using System.Text.Json.Serialization;

namespace Monitor.Metrics;

/// <summary>
/// A single point-in-time snapshot of a host's hardware state. Serialized to JSON and shipped
/// between agents, the hub, SQLite, and the browser. Property names are lower camelCase on the
/// wire (see <see cref="MetricsJson"/>).
/// </summary>
public sealed class MetricSnapshot
{
    public string Host { get; set; } = "";
    public string Os { get; set; } = "";

    /// <summary>Unix time in milliseconds when this snapshot was taken.</summary>
    public long TsUnixMs { get; set; }

    public CpuInfo Cpu { get; set; } = new();
    public MemInfo Mem { get; set; } = new();
    public List<NetInfo> Net { get; set; } = new();
    public List<DiskInfo> Disks { get; set; } = new();
    public List<TempInfo> Temps { get; set; } = new();
    public LoadInfo? Load { get; set; }

    /// <summary>Disk-I/O + process/thread counts. Populated by the Windows collector (which reports no
    /// thermals, since the ACPI zone is a static firmware placeholder); null elsewhere. The dashboard's
    /// thermals box renders this instead of a temperature gauge when present.</summary>
    public SysInfo? Sys { get; set; }

    public long UptimeSec { get; set; }

    /// <summary>GPU(s) that expose a temperature sensor. On Windows this comes from the native WDDM
    /// D3DKMT adapter perf-data path — the same source Task Manager uses, with no vendor SDK (NVAPI/ADL)
    /// and no custom kernel driver. Empty when there's no GPU sensor (or on platforms without it).
    /// Feeds the optional per-host GPU-temperature widget.</summary>
    public List<GpuInfo> Gpus { get; set; } = new();

    /// <summary>Live status of this host's monitored services. The hub fills it for its own host;
    /// for agent hosts the hub derives it from <see cref="ServiceCatalog"/> ∩ the per-host monitored set.</summary>
    public List<Monitor.Services.ServiceState> Services { get; set; } = new();

    /// <summary>
    /// The host's full service/daemon catalog (name + basic state), so the picker can list the
    /// services of any host — not just the hub's. Agents attach it sparsely (every ~10s, not every
    /// tick) to keep pushes small; the hub caches the last non-empty one per host and strips it from
    /// the snapshot before persisting/broadcasting. Null on the ticks that don't carry it.
    /// </summary>
    public List<Monitor.Services.ServiceUnit>? ServiceCatalog { get; set; }
}

public sealed class CpuInfo
{
    /// <summary>Aggregate CPU utilization, 0..100.</summary>
    public double TotalPct { get; set; }

    /// <summary>Per-logical-core utilization, 0..100. May be empty on platforms without per-core data.</summary>
    public double[] PerCorePct { get; set; } = Array.Empty<double>();

    /// <summary>Marketing model string, e.g. "Intel(R) Core(TM) i5-1340P". Empty if unknown.</summary>
    public string Model { get; set; } = "";

    /// <summary>Physical core count. 0 if it could not be determined.</summary>
    public int Cores { get; set; }

    /// <summary>Logical processor count (threads).</summary>
    public int Threads { get; set; }

    /// <summary>
    /// Strip integrated-component suffixes like " with Radeon Graphics" from a CPU marketing string,
    /// leaving just the processor model. Returns the input trimmed if there is no such suffix.
    /// </summary>
    public static string CleanModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "";
        int i = model.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
        return (i >= 0 ? model[..i] : model).Trim();
    }
}

public sealed class MemInfo
{
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public double Pct { get; set; }
    public long SwapTotalBytes { get; set; }
    public long SwapUsedBytes { get; set; }
}

public sealed class NetInfo
{
    public string Name { get; set; } = "";
    public double RxBytesPerSec { get; set; }
    public double TxBytesPerSec { get; set; }
    public long RxTotalBytes { get; set; }
    public long TxTotalBytes { get; set; }
}

public sealed class DiskInfo
{
    public string Mount { get; set; } = "";
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public double Pct { get; set; }
}

public sealed class TempInfo
{
    public string Label { get; set; } = "";
    public double Celsius { get; set; }
}

public sealed class LoadInfo
{
    public double One { get; set; }
    public double Five { get; set; }
    public double Fifteen { get; set; }
}

/// <summary>A GPU and its live readings (temperature, utilisation, memory). All sourced natively on
/// Windows — temperature via D3DKMT adapter perf-data, utilisation + used memory via the PDH "GPU
/// Engine"/"GPU Adapter Memory" performance counters, and total memory via D3DKMT segment sizes.
/// Fields that couldn't be read stay 0 (the widget hides them).</summary>
public sealed class GpuInfo
{
    /// <summary>Adapter name, e.g. "AMD Radeon(TM) Graphics". May be empty if unavailable.</summary>
    public string Name { get; set; } = "";

    /// <summary>GPU temperature in °C. 0 = no sensor.</summary>
    public double TempC { get; set; }

    /// <summary>Live GPU utilisation 0..100 (the busiest engine type, mirroring Task Manager's
    /// headline figure). 0 when the perf counters aren't available yet.</summary>
    public double UtilPct { get; set; }

    /// <summary>GPU memory currently in use, bytes (dedicated + shared). 0 = unknown.</summary>
    public long MemUsedBytes { get; set; }

    /// <summary>Total GPU memory, bytes (dedicated VRAM + shared system). 0 = unknown.</summary>
    public long MemTotalBytes { get; set; }
}

/// <summary>Disk activity + process/thread counts — the Windows replacement for the (unreliable)
/// thermal box. <see cref="DiskBusyPct"/> is the gauge value; the byte rates and counts fill the body.</summary>
public sealed class SysInfo
{
    /// <summary>Aggregate disk active-time, 0..100 (the gauge value).</summary>
    public double DiskBusyPct { get; set; }
    public double DiskReadBps { get; set; }
    public double DiskWriteBps { get; set; }
    public int Processes { get; set; }
    public int Threads { get; set; }
}

/// <summary>
/// Shared JSON options + source-generation context. camelCase on the wire, ignore nulls.
/// </summary>
public static class MetricsJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

namespace Monitor.Metrics;

/// <summary>Selects the right <see cref="IMetricsCollector"/> for the current OS.</summary>
public static class MetricsCollectorFactory
{
    public static IMetricsCollector Create(string? hostName)
    {
        if (OperatingSystem.IsLinux())
            return new LinuxMetricsCollector(hostName);
        if (OperatingSystem.IsWindows())
            return new WindowsMetricsCollector(hostName);

        // macOS / other: fall back to whatever cross-platform data we can gather.
        return new GenericMetricsCollector(hostName);
    }
}

/// <summary>
/// Minimal fallback for unsupported OSes: network + disk only (no CPU/mem specifics), so the app
/// still runs and reports something instead of crashing.
/// </summary>
internal sealed class GenericMetricsCollector : IMetricsCollector
{
    private readonly NetworkSampler _net = new();
    private readonly DiskSampler _disk = new();

    public string HostName { get; }

    public GenericMetricsCollector(string? hostName)
        => HostName = string.IsNullOrWhiteSpace(hostName) ? System.Environment.MachineName : hostName!;

    public MetricSnapshot Sample() => new()
    {
        Host = HostName,
        Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        TsUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Net = _net.Sample(),
        Disks = _disk.Sample(),
        UptimeSec = (long)(System.Environment.TickCount64 / 1000),
    };
}

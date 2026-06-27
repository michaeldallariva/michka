namespace Monitor.Metrics;

/// <summary>
/// Collects a <see cref="MetricSnapshot"/> for the local machine. Implementations are stateful:
/// rate-based values (CPU %, network throughput) are computed from the delta against the previous
/// call, so the first sample after construction may report zero rates.
/// </summary>
public interface IMetricsCollector
{
    /// <summary>The reported host name (overridable from the CLI).</summary>
    string HostName { get; }

    /// <summary>Take a snapshot of the current local hardware state.</summary>
    MetricSnapshot Sample();
}

namespace Monitor.Services;

/// <summary>
/// Enumerates the host's services/daemons and reports the live status of a chosen subset. Linux uses
/// systemd (<c>systemctl</c>); Windows uses the service control manager (<c>sc.exe</c>). Both the hub's
/// local sampler and each agent run one: the catalog (<see cref="List"/>) ships in snapshots so the
/// picker can list any host's services, while <see cref="Status"/> drives the hub's own detailed boxes.
/// </summary>
public interface IServiceInspector
{
    /// <summary>True when this platform can enumerate services.</summary>
    bool Available { get; }

    /// <summary>All known services on the host (for the search/select page). Cached briefly.</summary>
    IReadOnlyList<ServiceUnit> List();

    /// <summary>Live status for the given unit names (for the monitored dashboard boxes).</summary>
    IReadOnlyList<ServiceState> Status(IReadOnlyCollection<string> names);
}

/// <summary>A service as shown in the searchable list.</summary>
public sealed class ServiceUnit
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Load { get; set; } = "";          // loaded | not-found | ...
    public string ActiveState { get; set; } = "";    // active | inactive | failed | ...
    public string SubState { get; set; } = "";       // running | dead | exited | ...
}

/// <summary>Live status of a monitored service, shipped inside a <c>MetricSnapshot</c>.</summary>
public sealed class ServiceState
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Active { get; set; }
    public string ActiveState { get; set; } = "";
    public string SubState { get; set; } = "";
    public long MemoryBytes { get; set; } = -1;      // -1 = unknown
    public int MainPid { get; set; }
}

/// <summary>Selects the right inspector for the current OS.</summary>
public static class ServiceInspectorFactory
{
    public static IServiceInspector Create()
    {
        if (OperatingSystem.IsLinux()) return new LinuxServiceInspector();
        if (OperatingSystem.IsWindows()) return new WindowsServiceInspector();
        return new NullServiceInspector();
    }
}

/// <summary>No-op inspector for unsupported platforms.</summary>
internal sealed class NullServiceInspector : IServiceInspector
{
    public bool Available => false;
    public IReadOnlyList<ServiceUnit> List() => Array.Empty<ServiceUnit>();
    public IReadOnlyList<ServiceState> Status(IReadOnlyCollection<string> names) => Array.Empty<ServiceState>();
}

// ProcRunner (shell a command, drain+timeout+kill) now lives in its own file, Services/ProcRunner.cs —
// shared by the hub, the agent, and the Windows GUI, not just the service inspectors.

using System.Runtime.Versioning;

namespace Monitor.Services;

/// <summary>
/// Windows service-control-manager inspector via <c>sc.exe</c>. Used by the Windows agent to report
/// its service catalog to the hub (verified enumerating ~283 services). Parsing <c>sc</c> text output
/// is best-effort and degrades gracefully (empty results) if the format differs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceInspector : IServiceInspector
{
    private readonly object _gate = new();
    private List<ServiceUnit>? _cache;
    private long _cacheAtMs;
    private const long CacheMs = 5000;

    public bool Available => OperatingSystem.IsWindows();

    public IReadOnlyList<ServiceUnit> List()
    {
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (_cache is not null && now - _cacheAtMs < CacheMs) return _cache;
        }

        var list = new List<ServiceUnit>();
        // `sc query` emits SERVICE_NAME / DISPLAY_NAME / STATE blocks separated by blank lines.
        string outp = ProcRunner.Capture("sc.exe", "query", "type=", "service", "state=", "all");
        ServiceUnit? cur = null;
        foreach (var raw in outp.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            {
                if (cur is not null) list.Add(cur);
                cur = new ServiceUnit { Name = After(line), Load = "loaded" };
            }
            else if (cur is not null && line.StartsWith("DISPLAY_NAME:", StringComparison.OrdinalIgnoreCase))
            {
                cur.Description = After(line);
            }
            else if (cur is not null && line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
            {
                (cur.ActiveState, cur.SubState) = MapState(line);
            }
        }
        if (cur is not null) list.Add(cur);
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        lock (_gate) { _cache = list; _cacheAtMs = now; }
        return list;
    }

    public IReadOnlyList<ServiceState> Status(IReadOnlyCollection<string> names)
    {
        var result = new List<ServiceState>(names.Count);
        foreach (var n in names)
        {
            string outp = ProcRunner.Capture("sc.exe", "query", n);
            string activeState = "unknown", sub = "";
            foreach (var raw in outp.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
                    (activeState, sub) = MapState(line);
            }
            result.Add(new ServiceState
            {
                Name = n,
                ActiveState = activeState,
                SubState = sub,
                Active = activeState == "active",
                MemoryBytes = -1,
            });
        }
        return result;
    }

    private static string After(string line)
    {
        int c = line.IndexOf(':');
        return c >= 0 ? line[(c + 1)..].Trim() : "";
    }

    // "STATE              : 4  RUNNING" -> (active|inactive, running|stopped|...)
    private static (string active, string sub) MapState(string line)
    {
        string tail = After(line);
        string word = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } p ? p[1] : tail;
        string sub = word.ToLowerInvariant();
        string active = word.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) ? "active" : "inactive";
        return (active, sub);
    }
}

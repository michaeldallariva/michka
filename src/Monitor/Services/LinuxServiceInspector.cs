using System.Text.Json;

namespace Monitor.Services;

/// <summary>
/// systemd-backed inspector. The full list comes from <c>systemctl list-units</c> (JSON), cached for
/// a few seconds; per-service status comes from a single <c>systemctl show</c> call.
/// </summary>
public sealed class LinuxServiceInspector : IServiceInspector
{
    private readonly object _gate = new();
    private List<ServiceUnit>? _cache;
    private long _cacheAtMs;
    private const long CacheMs = 5000;

    public bool Available => OperatingSystem.IsLinux();

    public IReadOnlyList<ServiceUnit> List()
    {
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (_cache is not null && now - _cacheAtMs < CacheMs) return _cache;
        }

        var units = LoadUnits();
        lock (_gate) { _cache = units; _cacheAtMs = now; }
        return units;
    }

    private static List<ServiceUnit> LoadUnits()
    {
        var list = new List<ServiceUnit>();
        // --all so stopped services are searchable too; JSON for robust parsing.
        string json = ProcRunner.Capture("systemctl",
            "list-units", "--type=service", "--all", "--no-pager", "--no-legend", "--output=json");
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string name = Str(el, "unit");
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new ServiceUnit
                {
                    Name = name,
                    Description = Str(el, "description"),
                    Load = Str(el, "load"),
                    ActiveState = Str(el, "active"),
                    SubState = Str(el, "sub"),
                });
            }
        }
        catch (JsonException) { /* unexpected format — return whatever we have */ }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public IReadOnlyList<ServiceState> Status(IReadOnlyCollection<string> names)
    {
        var result = new List<ServiceState>();
        if (names.Count == 0) return result;

        var args = new List<string> { "show", "--no-pager",
            "--property=Id,Description,LoadState,ActiveState,SubState,MainPID,MemoryCurrent" };
        args.AddRange(names);
        string outp = ProcRunner.Capture("systemctl", args.ToArray());
        if (string.IsNullOrWhiteSpace(outp)) return result;

        // `systemctl show` prints one Key=Value per line, with a blank line between units.
        var cur = new Dictionary<string, string>(StringComparer.Ordinal);
        void Flush()
        {
            if (cur.Count == 0) return;
            string id = cur.GetValueOrDefault("Id", "");
            if (!string.IsNullOrEmpty(id))
            {
                string active = cur.GetValueOrDefault("ActiveState", "");
                result.Add(new ServiceState
                {
                    Name = id,
                    Description = cur.GetValueOrDefault("Description", ""),
                    ActiveState = active,
                    SubState = cur.GetValueOrDefault("SubState", ""),
                    Active = active == "active",
                    MainPid = ParseInt(cur.GetValueOrDefault("MainPID", "0")),
                    MemoryBytes = ParseMem(cur.GetValueOrDefault("MemoryCurrent", "")),
                });
            }
            cur.Clear();
        }

        foreach (var raw in outp.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            cur[line[..eq]] = line[(eq + 1)..];
        }
        Flush();

        // Preserve the caller's order; include placeholders for units systemctl didn't report.
        var byName = result.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var ordered = new List<ServiceState>(names.Count);
        foreach (var n in names)
            ordered.Add(byName.TryGetValue(n, out var st) ? st
                : new ServiceState { Name = n, ActiveState = "unknown", SubState = "" });
        return ordered;
    }

    private static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static int ParseInt(string s) => int.TryParse(s, out var n) ? n : 0;

    private static long ParseMem(string s)
    {
        // [not set] / empty / ULONG_MAX all mean "unknown".
        if (!long.TryParse(s, out var n) || n < 0) return -1;
        if ((ulong)n == ulong.MaxValue) return -1;
        return n;
    }
}

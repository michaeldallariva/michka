using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Monitor.Metrics;
using Monitor.Services;
using Monitor.Storage;

namespace Monitor.Realtime;

/// <summary>
/// Central hub fan-out. Every incoming snapshot (local timer or remote agent) is persisted to
/// SQLite and pushed to all connected SSE subscribers. Also keeps the latest snapshot per host so
/// new browsers can be primed immediately.
/// </summary>
public sealed class LiveBroadcaster
{
    private readonly MetricsStore _store;
    private readonly HubRuntime _runtime;
    private readonly ConcurrentDictionary<string, MetricSnapshot> _latest = new();
    private readonly ConcurrentDictionary<string, List<ServiceUnit>> _catalogs = new();
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    public LiveBroadcaster(MetricsStore store, HubRuntime runtime)
    {
        _store = store;
        _runtime = runtime;
    }

    public IReadOnlyDictionary<string, MetricSnapshot> Latest => _latest;

    /// <summary>The last service catalog a host reported (for the per-host service picker). Null if
    /// the host hasn't pushed one yet.</summary>
    public List<ServiceUnit>? Catalog(string host) => _catalogs.GetValueOrDefault(host);

    /// <summary>Accept a snapshot: stamp host/ts if missing, persist, cache, broadcast.</summary>
    public void Publish(MetricSnapshot snap)
    {
        if (snap.TsUnixMs == 0) snap.TsUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (string.IsNullOrWhiteSpace(snap.Host)) snap.Host = "unknown";

        // The catalog rides in only occasionally; cache the latest non-empty one per host and strip
        // it from the snapshot so the bulky list never hits SQLite history or the SSE stream.
        if (snap.ServiceCatalog is { Count: > 0 } cat)
            _catalogs[snap.Host] = cat;
        snap.ServiceCatalog = null;

        // For agent hosts (no detailed status of their own), synthesise up/down service status for the
        // host's monitored set from its cached catalog, so service boxes/bubbles light up the same way
        // the hub's own host does. The hub fills its own Services with full detail before publishing.
        if (snap.Services.Count == 0)
            snap.Services = DeriveServices(snap.Host);

        var json = JsonSerializer.Serialize(snap, MetricsJson.Options);

        _latest[snap.Host] = snap;
        try { _store.Insert(snap, json); } catch { /* never let storage errors break the live feed */ }

        foreach (var ch in _subscribers.Values)
            ch.Writer.TryWrite(json); // bounded+drop: a slow client must not stall others
    }

    /// <summary>Build coarse (up/down only) service status for a host's monitored units from its
    /// cached catalog. Units not yet in the catalog report "unknown" until the next catalog push.</summary>
    private List<ServiceState> DeriveServices(string host)
    {
        var names = _runtime.Config.ServicesForHost(host);
        if (names.Count == 0) return new();

        var cat = _catalogs.GetValueOrDefault(host);
        var byName = cat?.ToDictionary(u => u.Name, StringComparer.Ordinal);
        var result = new List<ServiceState>(names.Count);
        foreach (var n in names)
        {
            if (byName is not null && byName.TryGetValue(n, out var u))
                result.Add(new ServiceState
                {
                    Name = n,
                    Description = u.Description,
                    ActiveState = u.ActiveState,
                    SubState = u.SubState,
                    Active = string.Equals(u.ActiveState, "active", StringComparison.OrdinalIgnoreCase),
                    MemoryBytes = -1,
                });
            else
                result.Add(new ServiceState { Name = n, ActiveState = "unknown", MemoryBytes = -1 });
        }
        return result;
    }

    /// <summary>Forget a host's cached latest snapshot + catalog (used when a host is deleted). The
    /// host can still return: a later push re-creates the entries.</summary>
    public bool Forget(string host)
    {
        _catalogs.TryRemove(host, out _);
        return _latest.TryRemove(host, out _);
    }

    /// <summary>Register an SSE subscriber. Dispose the returned token to unsubscribe.</summary>
    public Subscription Subscribe()
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _subscribers[id] = ch;
        return new Subscription(this, id, ch.Reader);
    }

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var ch))
            ch.Writer.TryComplete();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly LiveBroadcaster _owner;
        private readonly Guid _id;
        public ChannelReader<string> Reader { get; }

        internal Subscription(LiveBroadcaster owner, Guid id, ChannelReader<string> reader)
        {
            _owner = owner;
            _id = id;
            Reader = reader;
        }

        public void Dispose() => _owner.Unsubscribe(_id);
    }
}

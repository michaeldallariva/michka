using Microsoft.Data.Sqlite;
using Monitor.Metrics;

namespace Monitor.Storage;

public sealed record HistoryPoint(long Ts, double Cpu, double MemPct, long NetRx, long NetTx);

/// <summary>
/// SQLite-backed metric history. One row per received snapshot, with a few scalar columns pulled
/// out for fast charting plus the full JSON for completeness. Thread-safe via a single guarded
/// connection (WAL mode); write volume is light (~1 row/sec/host).
/// </summary>
public sealed class MetricsStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public MetricsStore(string dbPath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        _conn = new SqliteConnection(csb.ConnectionString);
        _conn.Open();

        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS samples (
                host    TEXT    NOT NULL,
                ts      INTEGER NOT NULL,
                cpu     REAL    NOT NULL,
                mem_pct REAL    NOT NULL,
                net_rx  INTEGER NOT NULL,
                net_tx  INTEGER NOT NULL,
                json    TEXT    NOT NULL
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS ix_samples_host_ts ON samples(host, ts);");
    }

    public void Insert(MetricSnapshot snap, string json)
    {
        long rx = 0, tx = 0;
        foreach (var n in snap.Net) { rx += (long)n.RxBytesPerSec; tx += (long)n.TxBytesPerSec; }

        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO samples (host, ts, cpu, mem_pct, net_rx, net_tx, json)
                VALUES ($host, $ts, $cpu, $mem, $rx, $tx, $json);
                """;
            cmd.Parameters.AddWithValue("$host", snap.Host);
            cmd.Parameters.AddWithValue("$ts", snap.TsUnixMs);
            cmd.Parameters.AddWithValue("$cpu", snap.Cpu.TotalPct);
            cmd.Parameters.AddWithValue("$mem", snap.Mem.Pct);
            cmd.Parameters.AddWithValue("$rx", rx);
            cmd.Parameters.AddWithValue("$tx", tx);
            cmd.Parameters.AddWithValue("$json", json);
            cmd.ExecuteNonQuery();
        }
    }

    public List<HistoryPoint> QueryHistory(string host, long sinceMs, int maxPoints = 600)
    {
        var result = new List<HistoryPoint>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT ts, cpu, mem_pct, net_rx, net_tx
                FROM samples
                WHERE host = $host AND ts >= $since
                ORDER BY ts ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue("$since", sinceMs);
            cmd.Parameters.AddWithValue("$limit", maxPoints);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new HistoryPoint(
                    r.GetInt64(0), r.GetDouble(1), r.GetDouble(2), r.GetInt64(3), r.GetInt64(4)));
            }
        }
        return result;
    }

    /// <summary>Distinct hosts seen, with the timestamp of their most recent sample.</summary>
    public Dictionary<string, long> KnownHosts()
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT host, MAX(ts) FROM samples GROUP BY host;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                map[r.GetString(0)] = r.GetInt64(1);
        }
        return map;
    }

    /// <summary>Delete all samples for a host. It reappears in the host list only if it pushes
    /// again (this is a removal, not a ban).</summary>
    public int DeleteHost(string host)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM samples WHERE host = $host;";
            cmd.Parameters.AddWithValue("$host", host);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Delete samples older than the given retention window.</summary>
    public int Prune(long retentionMs)
    {
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - retentionMs;
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM samples WHERE ts < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}

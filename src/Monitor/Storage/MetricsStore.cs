using Microsoft.Data.Sqlite;
using Monitor.Metrics;

namespace Monitor.Storage;

public sealed record HistoryPoint(long Ts, double Cpu, double MemPct, long NetRx, long NetTx);

/// <summary>One aggregated time bucket for the host report (avg + max per metric over the bucket).
/// MAX is exact per bucket, so genuine spikes survive the downsampling.</summary>
public sealed record ReportBucket(
    long Ts, double CpuAvg, double CpuMax, double MemAvg, double MemMax,
    long RxAvg, long RxMax, long TxAvg, long TxMax);

/// <summary>Whole-window roll-up for the report header (overall averages/peaks + coverage).</summary>
public sealed record ReportSummary(
    double CpuAvg, double CpuMax, double MemAvg, double MemMax,
    long RxMax, long TxMax, long FirstTs, long LastTs, long Count);

/// <summary>One time-bucketed disk-space point for the report (free + total bytes for a mount).</summary>
public sealed record DiskReportPoint(string Mount, long Ts, long FreeBytes, long TotalBytes);

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

        // Per-disk free/total space over time, for the report's "disk free space" trend. Written far
        // less often than metrics (disk usage drifts slowly) so it stays tiny: one row per mount per
        // ~5 min (see LiveBroadcaster), not per second.
        Exec("""
            CREATE TABLE IF NOT EXISTS disk_samples (
                host        TEXT    NOT NULL,
                ts          INTEGER NOT NULL,
                mount       TEXT    NOT NULL,
                total_bytes INTEGER NOT NULL,
                free_bytes  INTEGER NOT NULL
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS ix_disk_host_ts ON disk_samples(host, ts);");
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

    /// <summary>Record a host's per-mount free/total space at a point in time (throttled by the caller).</summary>
    public void InsertDisks(string host, long ts, IEnumerable<DiskInfo> disks)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO disk_samples (host, ts, mount, total_bytes, free_bytes)
                VALUES ($host, $ts, $mount, $total, $free);
                """;
            foreach (var d in disks)
            {
                if (d.TotalBytes <= 0 || string.IsNullOrEmpty(d.Mount)) continue;
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$host", host);
                cmd.Parameters.AddWithValue("$ts", ts);
                cmd.Parameters.AddWithValue("$mount", d.Mount);
                cmd.Parameters.AddWithValue("$total", d.TotalBytes);
                cmd.Parameters.AddWithValue("$free", Math.Max(0, d.TotalBytes - d.UsedBytes));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Per-mount free/total space over the window, downsampled into time buckets (avg per
    /// bucket — disk space drifts slowly so the average reads cleanly). Ordered by mount then time.</summary>
    public List<DiskReportPoint> QueryDiskReport(string host, long sinceMs, long bucketMs)
    {
        if (bucketMs < 1) bucketMs = 1;
        var result = new List<DiskReportPoint>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT mount, (ts / $bucket) * $bucket AS bts,
                       CAST(AVG(free_bytes) AS INTEGER), CAST(AVG(total_bytes) AS INTEGER)
                FROM disk_samples
                WHERE host = $host AND ts >= $since
                GROUP BY mount, bts
                ORDER BY mount ASC, bts ASC;
                """;
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue("$since", sinceMs);
            cmd.Parameters.AddWithValue("$bucket", bucketMs);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new DiskReportPoint(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
        }
        return result;
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

    /// <summary>Downsample a host's samples into fixed time buckets for charting a report. Each bucket
    /// carries the average AND the exact max of every metric, so peaks aren't smoothed away.</summary>
    public List<ReportBucket> QueryReport(string host, long sinceMs, long bucketMs)
    {
        if (bucketMs < 1) bucketMs = 1;
        var result = new List<ReportBucket>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT (ts / $bucket) * $bucket AS bts,
                       AVG(cpu), MAX(cpu), AVG(mem_pct), MAX(mem_pct),
                       CAST(AVG(net_rx) AS INTEGER), MAX(net_rx),
                       CAST(AVG(net_tx) AS INTEGER), MAX(net_tx)
                FROM samples
                WHERE host = $host AND ts >= $since
                GROUP BY bts
                ORDER BY bts ASC;
                """;
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue("$since", sinceMs);
            cmd.Parameters.AddWithValue("$bucket", bucketMs);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new ReportBucket(
                    r.GetInt64(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4),
                    r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8)));
            }
        }
        return result;
    }

    /// <summary>Overall averages/peaks + coverage for a host over the window (one cheap pass).</summary>
    public ReportSummary QueryReportSummary(string host, long sinceMs)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT AVG(cpu), MAX(cpu), AVG(mem_pct), MAX(mem_pct),
                       MAX(net_rx), MAX(net_tx), MIN(ts), MAX(ts), COUNT(*)
                FROM samples
                WHERE host = $host AND ts >= $since;
                """;
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue("$since", sinceMs);
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(8) || r.GetInt64(8) == 0)
                return new ReportSummary(0, 0, 0, 0, 0, 0, 0, 0, 0);
            double D(int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);
            long N(int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);
            return new ReportSummary(D(0), D(1), D(2), D(3), N(4), N(5), N(6), N(7), N(8));
        }
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
            int n;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM samples WHERE host = $host;";
                cmd.Parameters.AddWithValue("$host", host);
                n = cmd.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM disk_samples WHERE host = $host;";
                cmd.Parameters.AddWithValue("$host", host);
                cmd.ExecuteNonQuery();
            }
            return n;
        }
    }

    /// <summary>Delete samples (metric + disk) older than the given retention window.</summary>
    public int Prune(long retentionMs)
    {
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - retentionMs;
        lock (_lock)
        {
            int n;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM samples WHERE ts < $cutoff;";
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                n = cmd.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM disk_samples WHERE ts < $cutoff;";
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                cmd.ExecuteNonQuery();
            }
            return n;
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

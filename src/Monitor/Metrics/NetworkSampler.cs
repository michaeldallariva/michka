using System.Net.NetworkInformation;

namespace Monitor.Metrics;

/// <summary>
/// Cross-platform network throughput sampler. Uses <see cref="NetworkInterface"/> byte counters
/// and computes per-interface rates from the delta against the previous sample.
/// </summary>
public sealed class NetworkSampler
{
    private readonly record struct Counter(long Rx, long Tx, long Ticks);

    private readonly Dictionary<string, Counter> _previous = new();

    public List<NetInfo> Sample()
    {
        var now = DateTime.UtcNow.Ticks;
        var result = new List<NetInfo>();

        // On Windows the same physical NIC shows up many times as WFP/QoS filter pseudo-adapters,
        // all reporting identical byte counters. De-dupe by MAC so the aggregate isn't multiplied.
        var seenMacs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            var mac = nic.GetPhysicalAddress().ToString();
            if (!string.IsNullOrEmpty(mac) && !seenMacs.Add(mac)) continue;

            IPInterfaceStatistics stats;
            try { stats = nic.GetIPStatistics(); }
            catch { continue; }

            long rx = stats.BytesReceived;
            long tx = stats.BytesSent;

            double rxRate = 0, txRate = 0;
            if (_previous.TryGetValue(nic.Id, out var prev))
            {
                double seconds = (now - prev.Ticks) / (double)TimeSpan.TicksPerSecond;
                if (seconds > 0)
                {
                    // Guard against counter resets (interface bounce) producing negatives.
                    rxRate = Math.Max(0, rx - prev.Rx) / seconds;
                    txRate = Math.Max(0, tx - prev.Tx) / seconds;
                }
            }

            _previous[nic.Id] = new Counter(rx, tx, now);

            result.Add(new NetInfo
            {
                Name = nic.Name,
                RxBytesPerSec = rxRate,
                TxBytesPerSec = txRate,
                RxTotalBytes = rx,
                TxTotalBytes = tx,
            });
        }

        return result;
    }
}

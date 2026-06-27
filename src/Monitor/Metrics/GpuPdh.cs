using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Monitor.Metrics;

/// <summary>
/// Live GPU utilisation + used memory via the Windows <b>PDH</b> performance-counter API (pdh.dll) —
/// the same "GPU Engine" / "GPU Adapter Memory" counter sets Task Manager charts. This complements the
/// D3DKMT path in <see cref="WindowsMetricsCollector"/> (which gives temperature + total memory):
/// neither vendor SDK nor kernel driver, just a native API, so it stays out of the WINDOWS_GUI gate and
/// works in every Windows build. Best-effort: any failure disables it and leaves the readings at 0.
///
/// A single PDH query is opened once and re-collected on every sample (the collector already throttles
/// to ~2s). Results are keyed by adapter LUID so they can be matched back to the enumerated GPUs:
/// PDH instance names carry the LUID (e.g. "...luid_0x00000000_0x0000C3D4_phys_0_eng_0_engtype_3D").
/// Utilisation is reported per engine; we mirror Task Manager's headline by taking the busiest engine
/// *type* (summing the instances of each type, then taking the max). Memory is the dedicated + shared
/// usage summed across the adapter's segments.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GpuPdh
{
    private IntPtr _query;
    private IntPtr _util;      // \GPU Engine(*)\Utilization Percentage
    private IntPtr _dedicated; // \GPU Adapter Memory(*)\Dedicated Usage
    private IntPtr _shared;    // \GPU Adapter Memory(*)\Shared Usage
    private bool _opened;
    private bool _dead;        // a hard failure disables further attempts

    public readonly record struct Reading(double UtilPct, long MemUsedBytes);

    /// <summary>Collect the counters and return per-LUID readings. Empty on any failure (callers then
    /// just leave utilisation/used-memory at 0 — temperature + totals still come from D3DKMT).</summary>
    public Dictionary<string, Reading> Sample()
    {
        var result = new Dictionary<string, Reading>(StringComparer.OrdinalIgnoreCase);
        if (_dead) return result;
        try
        {
            if (!_opened) Open();
            if (_dead) return result;

            // One collect refreshes every added counter. The GPU counters are raw snapshots, so a single
            // collect yields valid data (the first call right after Open may read 0 for a beat — fine).
            if (PdhCollectQueryData(_query) != 0) return result;

            // Utilisation: sum per engine type, keep the busiest type per adapter (Task Manager's number).
            var byType = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in ReadArray(_util))
            {
                string? luid = ExtractLuid(name);
                if (luid is null) continue;
                string engtype = ExtractTag(name, "engtype_") ?? "?";
                if (!byType.TryGetValue(luid, out var types)) byType[luid] = types = new(StringComparer.OrdinalIgnoreCase);
                types[engtype] = types.GetValueOrDefault(engtype) + value;
            }

            // Memory: dedicated + shared usage summed across the adapter's segment instances.
            var usedByLuid = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            void AddMem(IntPtr counter)
            {
                foreach (var (name, value) in ReadArray(counter))
                {
                    string? luid = ExtractLuid(name);
                    if (luid is null) continue;
                    usedByLuid[luid] = usedByLuid.GetValueOrDefault(luid) + value;
                }
            }
            AddMem(_dedicated);
            AddMem(_shared);

            foreach (var luid in byType.Keys.Union(usedByLuid.Keys, StringComparer.OrdinalIgnoreCase))
            {
                double util = byType.TryGetValue(luid, out var types) && types.Count > 0 ? types.Values.Max() : 0;
                util = Math.Clamp(util, 0, 100);
                long used = usedByLuid.TryGetValue(luid, out var b) ? (long)b : 0;
                result[luid] = new Reading(Math.Round(util, 1), used);
            }
        }
        catch { _dead = true; }
        return result;
    }

    private void Open()
    {
        _opened = true;
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _dead = true; return; }
        // English counter paths so this is locale-independent. A missing set (older Windows) just leaves
        // that counter at IntPtr.Zero and ReadArray skips it.
        PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _util);
        PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _dedicated);
        PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Shared Usage", IntPtr.Zero, out _shared);
        if (_util == IntPtr.Zero && _dedicated == IntPtr.Zero) { _dead = true; return; }
        PdhCollectQueryData(_query); // prime
    }

    // Read a wildcard counter's formatted instances as (instanceName, value) pairs.
    private static IEnumerable<(string Name, double Value)> ReadArray(IntPtr counter)
    {
        if (counter == IntPtr.Zero) yield break;
        uint size = 0, count = 0;
        uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) yield break;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, buf) != 0) yield break;
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * itemSize);
                if (item.FmtValue.CStatus != 0) continue;               // skip instances with no valid data
                string name = item.szName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(item.szName) ?? "";
                yield return (name, item.FmtValue.doubleValue);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // Pull "luid_0xHIGH_0xLOW" out of a PDH GPU instance name (the stable per-adapter key).
    private static string? ExtractLuid(string instance)
    {
        int i = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        // luid_0x........_0x........  -> take through the second hex group.
        int u1 = instance.IndexOf('_', i + 5);                 // after "luid"
        if (u1 < 0) return null;
        int u2 = instance.IndexOf('_', u1 + 1);                // after the high part
        if (u2 < 0) return null;
        int u3 = instance.IndexOf('_', u2 + 1);                // after the low part (or end of string)
        string end = u3 < 0 ? instance.Substring(i) : instance.Substring(i, u3 - i);
        return end.ToLowerInvariant();
    }

    private static string? ExtractTag(string instance, string prefix)
    {
        int i = instance.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += prefix.Length;
        int end = instance.IndexOf('_', i);
        return end < 0 ? instance.Substring(i) : instance.Substring(i, end - i);
    }

    /// <summary>Build the LUID key the same way PDH formats it, from a D3DKMT adapter LUID.</summary>
    public static string LuidKey(int highPart, uint lowPart) =>
        $"luid_0x{(uint)highPart:x8}_0x{lowPart:x8}";

    // ---- PDH interop ----
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
        [FieldOffset(8)] public long largeValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;             // wchar* into the same buffer
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);
}

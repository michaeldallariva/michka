using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Monitor.Metrics;

/// <summary>
/// Windows metrics via Win32 (<c>GetSystemTimes</c>, <c>GlobalMemoryStatusEx</c>) plus WMI for a
/// load-average analog and disk-I/O / process counts. Reports aggregate CPU only (per-core is
/// deferred) and deliberately reports <b>no thermals</b>: the only user-mode source
/// (MSAcpi_ThermalZoneTemperature) is a static ~20 °C ACPI firmware placeholder on most hardware,
/// so the dashboard shows disk-I/O + uptime/process counts for Windows hosts instead. Network and
/// disk use the shared cross-platform samplers. Designed so the project keeps compiling on Linux;
/// the P/Invokes and WMI only run on Windows (the WMI code is gated behind WINDOWS_GUI, which is
/// exactly the build that carries the System.Management package).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMetricsCollector : IMetricsCollector
{
    private readonly NetworkSampler _net = new();
    private readonly DiskSampler _disk = new();

    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasPrev;

    private string? _cpuModel;   // read once from the registry
    private int _cpuCores;       // physical cores; 0 if undeterminable

    // The load analog + disk-I/O / process counts come from WMI, which is far heavier than a /proc
    // read, so they refresh on a throttle (~4s) rather than every push tick. The load average is an
    // EWMA, Linux-style.
    private const long SlowIntervalMs = 4000;
    private readonly System.Diagnostics.Stopwatch _slowSw = System.Diagnostics.Stopwatch.StartNew();
    private long _lastSlowMs = long.MinValue / 2;   // so the first Sample() always refreshes
    private SysInfo? _sys = null;   // populated under WINDOWS_GUI; stays null on the Linux build
    private double _load1, _load5, _load15;
    private bool _loadInit;

    // Page-file ("swap") size + in-use, from Win32_PageFileUsage on the same throttled WMI refresh.
    // -1 = not yet measured / WMI unavailable (e.g. the non-WINDOWS_GUI dev build), so ReadMem falls
    // back to the commit limit for the size and 0 used.
    private long _pageUsedBytes = -1;
    private long _pageTotalBytes = -1;

    // GPU temperature(s) via the native WDDM D3DKMT adapter perf-data path (gdi32 — no WMI, so it works
    // in every Windows build, not just WINDOWS_GUI). Throttled; GPU temp moves slowly.
    private const long GpuIntervalMs = 2000;
    private long _lastGpuMs = long.MinValue / 2;
    private List<GpuInfo> _gpus = new();
    // Utilisation + used-memory come from the PDH GPU performance counters (the D3DKMT perf-data only
    // carries temperature). One persistent query, collected on each GPU refresh; keyed by adapter LUID.
    private readonly GpuPdh _gpuPdh = new();

    public string HostName { get; }

    public WindowsMetricsCollector(string? hostName)
    {
        HostName = string.IsNullOrWhiteSpace(hostName) ? System.Environment.MachineName : hostName!;
    }

    public MetricSnapshot Sample()
    {
        var cpu = ReadCpu();
        RefreshSlow(cpu.TotalPct);
        RefreshGpu();
        return new MetricSnapshot
        {
            Host = HostName,
            Os = "windows",
            TsUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Cpu = cpu,
            Mem = ReadMem(),
            Net = _net.Sample(),
            Disks = _disk.Sample(),
            Gpus = _gpus,   // GPU temperature(s) via D3DKMT (native WDDM path)
            Sys = _sys,   // disk-I/O + process counts (no thermals on Windows — see class summary)
            Load = _loadInit
                ? new LoadInfo { One = Math.Round(_load1, 2), Five = Math.Round(_load5, 2), Fifteen = Math.Round(_load15, 2) }
                : null,
            UptimeSec = (long)(System.Environment.TickCount64 / 1000),
        };
    }

    /// <summary>
    /// Refresh the throttled WMI-derived metrics (disk-I/O + process counts + load average). Runs at
    /// most every <see cref="SlowIntervalMs"/>; cheap to call every tick otherwise. No-op on
    /// non-Windows builds.
    /// </summary>
    private void RefreshSlow(double cpuPct)
    {
#if WINDOWS_GUI
        long now = _slowSw.ElapsedMilliseconds;
        if (now - _lastSlowMs < SlowIntervalMs) return;
        long prev = _lastSlowMs;
        _lastSlowMs = now;

        _sys = ReadSys(out double? queue);
        ReadPageFile();

        if (queue is double q)
        {
            // Linux load average ≈ running + runnable threads. Approximate "running" by the number
            // of CPU-busy logical processors, and add the OS processor queue (threads waiting for a
            // core). Feed that instantaneous figure through the same 1/5/15-minute EWMA decays Linux
            // uses, so the numbers read like a familiar load average rather than a raw queue depth.
            double sample = q + cpuPct / 100.0 * Math.Max(1, Environment.ProcessorCount);
            if (!_loadInit)
            {
                _load1 = _load5 = _load15 = sample;
                _loadInit = true;
            }
            else
            {
                double dt = (now - prev) / 1000.0;
                _load1 = Ewma(_load1, sample, dt, 60);
                _load5 = Ewma(_load5, sample, dt, 300);
                _load15 = Ewma(_load15, sample, dt, 900);
            }
        }
#endif
    }

#if WINDOWS_GUI
    private static double Ewma(double prev, double sample, double dtSec, double periodSec)
    {
        if (dtSec <= 0) return prev;
        double a = Math.Exp(-dtSec / periodSec);
        return prev * a + sample * (1 - a);
    }

    /// <summary>
    /// Read the throttled system metrics from the formatted perf-counter WMI classes: disk active-time
    /// + read/write byte rates (<c>PerfDisk_PhysicalDisk</c>, the <c>_Total</c> instance) and the
    /// process/thread counts plus processor queue length (<c>PerfOS_System</c>). The queue length —
    /// the closest Windows analog to the runnable-task count behind a load average — is returned via
    /// <paramref name="queue"/> so the caller can fold it into the EWMA load. Best-effort; any WMI
    /// failure just leaves the corresponding fields at zero / null.
    /// </summary>
    private static SysInfo ReadSys(out double? queue)
    {
        queue = null;
        var sys = new SysInfo();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessorQueueLength, Processes, Threads FROM Win32_PerfFormattedData_PerfOS_System");
            foreach (var mo in searcher.Get())
            {
                using var obj = (System.Management.ManagementObject)mo;
                if (obj["ProcessorQueueLength"] is { } pq) queue = Convert.ToDouble(pq);
                if (obj["Processes"] is { } pr) sys.Processes = Convert.ToInt32(pr);
                if (obj["Threads"] is { } th) sys.Threads = Convert.ToInt32(th);
                break;   // single _Total-style row
            }
        }
        catch { /* perf class unavailable */ }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT PercentDiskTime, DiskReadBytesPersec, DiskWriteBytesPersec " +
                "FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'");
            foreach (var mo in searcher.Get())
            {
                using var obj = (System.Management.ManagementObject)mo;
                if (obj["PercentDiskTime"] is { } dt) sys.DiskBusyPct = Math.Clamp(Convert.ToDouble(dt), 0, 100);
                if (obj["DiskReadBytesPersec"] is { } rb) sys.DiskReadBps = Convert.ToDouble(rb);
                if (obj["DiskWriteBytesPersec"] is { } wb) sys.DiskWriteBps = Convert.ToDouble(wb);
                break;
            }
        }
        catch { /* perf class unavailable */ }

        return sys;
    }

    /// <summary>
    /// Read the real page-file ("swap") size + in-use from <c>Win32_PageFileUsage</c> (summing across
    /// pagefiles; both fields are in MB). This is the accurate, Task-Manager-equivalent source, unlike
    /// the old commit-charge math which over-counted committed-but-unpaged virtual memory and could
    /// exceed the page-file size. Best-effort: a WMI failure leaves the previous values untouched.
    /// </summary>
    private void ReadPageFile()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage");
            long totalMb = 0, usedMb = 0;
            foreach (var mo in searcher.Get())
            {
                using var obj = (System.Management.ManagementObject)mo;
                if (obj["AllocatedBaseSize"] is { } ab) totalMb += Convert.ToInt64(ab);
                if (obj["CurrentUsage"] is { } cu) usedMb += Convert.ToInt64(cu);
            }
            _pageTotalBytes = totalMb * 1024L * 1024L;
            _pageUsedBytes = usedMb * 1024L * 1024L;
        }
        catch { /* WMI unavailable — keep prior values */ }
    }
#endif

    /// <summary>Refresh the GPU temperature list on a throttle (GPU temp moves slowly). Not gated by
    /// WINDOWS_GUI — D3DKMT is in gdi32 with no extra package, so it works in every Windows build.</summary>
    private void RefreshGpu()
    {
        long now = _slowSw.ElapsedMilliseconds;
        if (now - _lastGpuMs < GpuIntervalMs) return;
        _lastGpuMs = now;

        var raw = ReadGpu();             // name + temperature + total memory + LUID, via D3DKMT
        var pdh = _gpuPdh.Sample();      // utilisation + used memory, via PDH, keyed by LUID
        foreach (var (gpu, luid) in raw)
        {
            GpuPdh.Reading? r = null;
            if (pdh.TryGetValue(luid, out var byLuid))
                r = byLuid;                                 // exact LUID match (multi-GPU correct)
            else if (raw.Count == 1 && pdh.Count > 0)
                // Single enumerated GPU but the temp adapter's LUID doesn't line up with the perf-counter
                // LUID (Windows sometimes tracks usage under a sibling adapter handle, and idle render
                // adapters also surface as zero-usage LUIDs). Take the busiest perf-counter adapter.
                r = pdh.Values.OrderByDescending(x => x.MemUsedBytes).ThenByDescending(x => x.UtilPct).First();
            if (r is { } reading) { gpu.UtilPct = reading.UtilPct; gpu.MemUsedBytes = reading.MemUsedBytes; }
        }
        _gpus = raw.Select(x => x.Gpu).ToList();
    }

    /// <summary>
    /// Read GPU temperature(s) the way Task Manager does — the WDDM kernel-mode driver's perf data via
    /// <c>D3DKMTQueryAdapterInfo(KMTQAITYPE_ADAPTERPERFDATA)</c>. No vendor SDK (NVAPI/ADL) and no custom
    /// kernel driver: it reads through whatever display driver is installed (AMD/NVIDIA/Intel). Adapters
    /// without a temperature sensor report 0 and are skipped. Best-effort: any failure yields an empty list.
    /// </summary>
    private static List<(GpuInfo Gpu, string Luid)> ReadGpu()
    {
        var list = new List<(GpuInfo Gpu, string Luid)>();
        try
        {
            // First call with a null buffer returns the adapter count.
            var en = new D3DKMT_ENUMADAPTERS2 { NumAdapters = 0, pAdapters = IntPtr.Zero };
            if (D3DKMTEnumAdapters2(ref en) != 0 || en.NumAdapters == 0)
                en.NumAdapters = 16;   // fall back to a sane max if the count probe didn't fill it

            int count = (int)en.NumAdapters;
            int infoSize = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            IntPtr buf = Marshal.AllocHGlobal(infoSize * count);
            try
            {
                en.pAdapters = buf;
                if (D3DKMTEnumAdapters2(ref en) != 0) return list;

                for (int i = 0; i < (int)en.NumAdapters; i++)
                {
                    var ai = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buf + i * infoSize);
                    try
                    {
                        double tempC = QueryAdapterTempC(ai.hAdapter);
                        if (tempC > 0)
                        {
                            string luid = GpuPdh.LuidKey(ai.AdapterLuid.HighPart, ai.AdapterLuid.LowPart);
                            list.Add((new GpuInfo
                            {
                                Name = QueryAdapterName(ai.hAdapter),
                                TempC = Math.Round(tempC, 1),
                                MemTotalBytes = QueryAdapterTotalMem(ai.hAdapter),
                            }, luid));
                        }
                    }
                    finally
                    {
                        var close = new D3DKMT_CLOSEADAPTER { hAdapter = ai.hAdapter };
                        D3DKMTCloseAdapter(ref close);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* D3DKMT unavailable */ }

        // The same physical GPU's sensor can surface on more than one adapter handle (e.g. a render-only
        // handle with no registry name). Prefer named entries, and keep just one per distinct name.
        var named = list.Where(g => !string.IsNullOrEmpty(g.Gpu.Name)).ToList();
        var src = named.Count > 0 ? named : list;
        var seen = new HashSet<string>();
        var result = new List<(GpuInfo Gpu, string Luid)>();
        foreach (var g in src)
            if (seen.Add(g.Gpu.Name)) result.Add(g);
        return result;
    }

    // The numeric value of KMTQAITYPE_ADAPTERPERFDATA varies between Windows builds (62 on this Win11,
    // 64 in some headers), so try the known candidates and take the first that yields a plausible temp.
    private static readonly int[] PerfDataQueryTypes = { 62, 64, 63 };

    // Query one adapter's perf data; Temperature is in deci-Celsius (540 => 54.0 °C). 0 = no sensor.
    private static double QueryAdapterTempC(uint hAdapter)
    {
        int size = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            foreach (int type in PerfDataQueryTypes)
            {
                Marshal.StructureToPtr(new D3DKMT_ADAPTER_PERFDATA(), p, false);   // zero-init
                var q = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = hAdapter, Type = type,
                    pPrivateDriverData = p, PrivateDriverDataSize = (uint)size,
                };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) continue;
                double c = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(p).Temperature / 10.0;
                if (c is > 0 and < 150) return c;   // sane GPU temp; ignore zero-filled wrong query types
            }
            return 0;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // Query one adapter's friendly name (registry info AdapterString). Empty on failure.
    private static string QueryAdapterName(uint hAdapter)
    {
        int size = Marshal.SizeOf<D3DKMT_ADAPTERREGISTRYINFO>();
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new D3DKMT_ADAPTERREGISTRYINFO(), p, false);
            var q = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = hAdapter, Type = KMTQAITYPE_ADAPTERREGISTRYINFO,
                pPrivateDriverData = p, PrivateDriverDataSize = (uint)size,
            };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return "";
            var reg = Marshal.PtrToStructure<D3DKMT_ADAPTERREGISTRYINFO>(p);
            return (reg.AdapterString ?? "").Trim();
        }
        catch { return ""; }
        finally { Marshal.FreeHGlobal(p); }
    }

    // Total GPU memory (bytes) = dedicated VRAM + shared system memory, via D3DKMT segment sizes. The
    // matching "used" figure comes from the PDH counters (see GpuPdh). 0 on failure.
    private static long QueryAdapterTotalMem(uint hAdapter)
    {
        int size = Marshal.SizeOf<D3DKMT_SEGMENTSIZEINFO>();
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new D3DKMT_SEGMENTSIZEINFO(), p, false);
            var q = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = hAdapter, Type = KMTQAITYPE_GETSEGMENTSIZE,
                pPrivateDriverData = p, PrivateDriverDataSize = (uint)size,
            };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return 0;
            var seg = Marshal.PtrToStructure<D3DKMT_SEGMENTSIZEINFO>(p);
            ulong total = seg.DedicatedVideoMemorySize + seg.DedicatedSystemMemorySize + seg.SharedSystemMemorySize;
            return total > long.MaxValue ? 0 : (long)total;
        }
        catch { return 0; }
        finally { Marshal.FreeHGlobal(p); }
    }

    private CpuInfo ReadCpu()
    {
        var info = new CpuInfo();
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            return info;

        ulong idle = ToULong(idleFt);
        ulong kernel = ToULong(kernelFt); // kernel time includes idle
        ulong user = ToULong(userFt);

        if (_hasPrev)
        {
            ulong dIdle = idle - _prevIdle;
            ulong dKernel = kernel - _prevKernel;
            ulong dUser = user - _prevUser;
            ulong total = dKernel + dUser;
            if (total > 0)
            {
                ulong active = total - dIdle;
                info.TotalPct = Math.Clamp(active * 100.0 / total, 0, 100);
            }
        }

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;
        _hasPrev = true;

        EnsureCpuStatic();
        info.Model = _cpuModel ?? "";
        info.Cores = _cpuCores;
        info.Threads = Environment.ProcessorCount;
        return info;
    }

    /// <summary>Read the static CPU identity (model + physical core count) exactly once.</summary>
    private void EnsureCpuStatic()
    {
        if (_cpuModel != null) return;
        string model = "";
        try
        {
            // HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0 : ProcessorNameString
            var hklm = new IntPtr(unchecked((int)0x80000002));
            const uint RRF_RT_REG_SZ = 0x00000002;
            var sb = new System.Text.StringBuilder(256);
            uint sizeBytes = (uint)(sb.Capacity * 2);
            int rc = RegGetValue(hklm, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString", RRF_RT_REG_SZ, out _, sb, ref sizeBytes);
            if (rc == 0) model = sb.ToString().Trim();
        }
        catch { /* best effort */ }
        _cpuModel = CpuInfo.CleanModel(model);
        _cpuCores = CountPhysicalCores();
    }

    // Count RelationProcessorCore entries from GetLogicalProcessorInformation = physical cores.
    private static int CountPhysicalCores()
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformation(IntPtr.Zero, ref len);
            if (len == 0) return 0;
            int size = Marshal.SizeOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>();
            int count = (int)(len / size);
            var buffer = new SYSTEM_LOGICAL_PROCESSOR_INFORMATION[count];
            if (!GetLogicalProcessorInformation(buffer, ref len)) return 0;
            int cores = 0;
            foreach (var b in buffer)
                if (b.Relationship == 0 /* RelationProcessorCore */) cores++;
            return cores;
        }
        catch { return 0; }
    }

    private MemInfo ReadMem()
    {
        var info = new MemInfo();
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            return info;

        info.TotalBytes = (long)status.ullTotalPhys;
        info.UsedBytes = (long)(status.ullTotalPhys - status.ullAvailPhys);
        info.Pct = status.ullTotalPhys > 0 ? info.UsedBytes * 100.0 / status.ullTotalPhys : 0;

        // "Swap" on Windows = the OS page file. Real size + in-use come from Win32_PageFileUsage
        // (WMI, throttled, populated by RefreshSlow on WINDOWS_GUI builds). When that's unavailable,
        // fall back to the commit limit for the size and 0 used — never the old commit-charge
        // heuristic, which counted committed-but-unpaged virtual memory and so could (wrongly) exceed
        // the page-file size.
        long pageTotalFallback = Math.Max(0, (long)status.ullTotalPageFile - (long)status.ullTotalPhys);
        info.SwapTotalBytes = _pageTotalBytes >= 0 ? _pageTotalBytes : pageTotalFallback;
        info.SwapUsedBytes = _pageUsedBytes >= 0 ? _pageUsedBytes : 0;
        if (info.SwapTotalBytes > 0)
            info.SwapUsedBytes = Math.Min(info.SwapUsedBytes, info.SwapTotalBytes);   // never exceed total
        return info;
    }

    private static ulong ToULong(FILETIME ft) => ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // Relationship field is first 4 bytes after the mask; the trailing union is 16 bytes on x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION
    {
        public UIntPtr ProcessorMask;
        public int Relationship;
        public ulong UnionPart0;
        public ulong UnionPart1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformation(
        [Out] SYSTEM_LOGICAL_PROCESSOR_INFORMATION[]? buffer, ref uint returnedLength);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetLogicalProcessorInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnedLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegGetValueW")]
    private static extern int RegGetValue(
        IntPtr hkey, string lpSubKey, string lpValue, uint dwFlags,
        out uint pdwType, System.Text.StringBuilder pvData, ref uint pcbData);

    // ---- D3DKMT (WDDM) GPU perf-data interop (gdi32). Same path Task Manager uses for GPU temp. ----
    private const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
    private const int KMTQAITYPE_GETSEGMENTSIZE = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;   // deci-Celsius
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_SEGMENTSIZEINFO
    {
        public ulong DedicatedVideoMemorySize;
        public ulong DedicatedSystemMemorySize;
        public ulong SharedSystemMemorySize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct D3DKMT_ADAPTERREGISTRYINFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string BiosString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DacType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ChipType;
    }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER p);
}

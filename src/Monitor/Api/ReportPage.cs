using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Monitor.Storage;

namespace Monitor.Api;

/// <summary>
/// Builds a self-contained HTML report for one host: a 7-day overview with CPU / memory / network
/// charts (downsampled into time buckets that keep the per-bucket MAX so spikes survive) plus a
/// summary header. The page inlines ECharts and its data, so it renders offline and can be saved
/// locally with one click (the in-page Download button blobs its own markup). Opened via
/// GET /api/report?host=NAME in a new browser tab.
/// </summary>
public static class ReportPage
{
    private static readonly ManifestEmbeddedFileProvider Files = new(typeof(ReportPage).Assembly, "wwwroot");
    private static string? _echarts;

    // ECharts is large (~1 MB); read the embedded copy once and reuse it across reports.
    private static string Echarts() => _echarts ??= ReadEmbedded("js/echarts.min.js");

    private static string ReadEmbedded(string path)
    {
        var fi = Files.GetFileInfo(path);
        if (!fi.Exists) return "";
        using var s = fi.CreateReadStream();
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // Server-side i18n: the report text routes through the same lang/<code>.json files the UI uses,
    // English as the fallback, so report strings translate alongside the rest of the app later.
    private static Dictionary<string, string> Lang(string code)
    {
        var dict = LoadLang("en");
        if (!string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
            foreach (var kv in LoadLang(code)) dict[kv.Key] = kv.Value;
        return dict;
    }

    private static Dictionary<string, string> LoadLang(string code)
    {
        try
        {
            var fi = Files.GetFileInfo($"lang/{code}.json");
            if (!fi.Exists) return new();
            using var s = fi.CreateReadStream();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        catch { return new(); }
    }

    public static string Build(string host, MetricsStore store, string langCode, string dateFmt)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long rangeMs = 7L * 24 * 60 * 60 * 1000;
        long since = now - rangeMs;
        long bucketMs = Math.Max(60_000, rangeMs / 400); // ~25-min buckets across the week

        var buckets = store.QueryReport(host, since, bucketMs);
        var sum = store.QueryReportSummary(host, since);
        // Disk free-space trend: one series per mount (free bytes over time).
        var diskSeries = store.QueryDiskReport(host, since, bucketMs)
            .GroupBy(p => p.Mount)
            .Select(g => new { mount = g.Key, points = g.Select(p => new object[] { p.Ts, p.FreeBytes }).ToArray() })
            .ToList();
        var L = Lang(langCode);
        string T(string k) => L.TryGetValue(k, out var v) ? v : k;
        // Honour the dashboard's date/time format setting (us = MM/DD/YYYY 12h, intl = DD/MM/YYYY 24h).
        string Time(long ms) => FmtTime(ms, dateFmt);

        var cpuPeakB = buckets.Count > 0 ? buckets.Aggregate((a, b) => b.CpuMax > a.CpuMax ? b : a) : null;
        var memPeakB = buckets.Count > 0 ? buckets.Aggregate((a, b) => b.MemMax > a.MemMax ? b : a) : null;

        // Data + labels handed to the inline render script. Default JsonSerializer escaping makes '<'
        // safe to embed inside <script> even if a host name contains markup.
        var payload = new
        {
            fileName = $"michka-report-{Sanitize(host)}-{DateTimeOffset.Now:yyyyMMdd}.html",
            buckets = buckets.Select(b => new object[]
            {
                b.Ts, R1(b.CpuAvg), R1(b.CpuMax), R1(b.MemAvg), R1(b.MemMax),
                b.RxAvg, b.RxMax, b.TxAvg, b.TxMax,
            }),
            disks = diskSeries,
            t = new
            {
                avg = T("report.avgLabel"),
                max = T("report.maxLabel"),
                rx = T("report.inLabel"),
                tx = T("report.outLabel"),
            },
        };
        string dataJson = JsonSerializer.Serialize(payload);

        var sb = new StringBuilder(Echarts().Length + 16_000);
        sb.Append("<!DOCTYPE html><html lang=\"").Append(Esc(langCode)).Append("\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(Esc(T("report.docTitle"))).Append(" · ").Append(Esc(host)).Append("</title>")
          .Append("<style>").Append(Css()).Append("</style></head><body>");

        // ---- toolbar ----
        // Back closes this report tab (it was opened with window.open from the dashboard); if the
        // browser blocks the self-close, it falls back to navigating to the dashboard.
        sb.Append("<div class=\"bar\"><div class=\"ttl\">")
          .Append("<button class=\"back\" title=\"").Append(Esc(T("report.back")))
          .Append("\" aria-label=\"").Append(Esc(T("report.back")))
          .Append("\" onclick=\"window.close();setTimeout(function(){location.href='/';},150);\">")
          .Append("<svg viewBox=\"0 0 24 24\" width=\"16\" height=\"16\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><polyline points=\"15 18 9 12 15 6\"/></svg>")
          .Append("<span>").Append(Esc(T("report.back"))).Append("</span></button>")
          .Append("<b>").Append(Esc(T("report.heading")))
          .Append("</b><span class=\"host\">").Append(Esc(host)).Append("</span></div>")
          .Append("<div class=\"acts\">")
          .Append("<button id=\"dl\">").Append(Esc(T("report.download"))).Append("</button>")
          .Append("<button id=\"pr\">").Append(Esc(T("report.print"))).Append("</button>")
          .Append("</div></div>");

        // ---- meta line ----
        string range = sum.Count > 0
            ? $"{Time(sum.FirstTs)} {T("report.to")} {Time(sum.LastTs)}"
            : T("report.na");
        sb.Append("<div class=\"meta\">")
          .Append(Meta(T("report.host"), Esc(host)))
          .Append(Meta(T("report.generated"), Esc(Time(now))))
          .Append(Meta(T("report.range"), Esc(T("report.last7days"))))
          .Append(Meta(T("report.dataRange"), Esc(range)))
          .Append(Meta(T("report.samples"), sum.Count.ToString("N0", CultureInfo.InvariantCulture)))
          .Append("</div>");

        if (sum.Count == 0)
        {
            sb.Append("<div class=\"nodata\">").Append(Esc(T("report.noData"))).Append("</div>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        // ---- summary cards (big value on top, label beneath; uniform, no side accents) ----
        sb.Append("<div class=\"cards\">")
          .Append(Card(T("report.peakCpu"), Pct(sum.CpuMax), cpuPeakB != null ? T("report.at").Replace("{time}", Time(cpuPeakB.Ts)) : ""))
          .Append(Card(T("report.avgCpu"), Pct(sum.CpuAvg), ""))
          .Append(Card(T("report.peakMem"), Pct(sum.MemMax), memPeakB != null ? T("report.at").Replace("{time}", Time(memPeakB.Ts)) : ""))
          .Append(Card(T("report.avgMem"), Pct(sum.MemAvg), ""))
          .Append(Card(T("report.peakIn"), FmtRate(sum.RxMax), ""))
          .Append(Card(T("report.peakOut"), FmtRate(sum.TxMax), ""))
          .Append("</div>");

        // ---- charts ----
        sb.Append(Section(T("report.cpuUsage"), "cpuChart"))
          .Append(Section(T("report.memUsage"), "memChart"))
          .Append(Section(T("report.netThroughput"), "netChart"));
        if (diskSeries.Count > 0)
            sb.Append(Section(T("report.diskFree"), "diskChart"));

        // ---- inline echarts + data + render ----
        sb.Append("<script>").Append(Echarts()).Append("</script>");
        sb.Append("<script>window.__REPORT__=").Append(dataJson).Append(";</script>");
        sb.Append("<script>").Append(RenderJs()).Append("</script>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string Meta(string k, string v) =>
        $"<div class=\"mi\"><span class=\"mk\">{Esc(k)}</span><span class=\"mv\">{v}</span></div>";

    private static string Card(string label, string value, string sub) =>
        $"<div class=\"card\"><div class=\"cv\">{Esc(value)}</div><div class=\"cl\">{Esc(label)}</div>" +
        (string.IsNullOrEmpty(sub) ? "" : $"<div class=\"cs\">{Esc(sub)}</div>") + "</div>";

    private static string Section(string title, string chartId) =>
        $"<div class=\"sec\"><h2>{Esc(title)}</h2><div class=\"chart\" id=\"{chartId}\"></div></div>";

    // ---------------- formatting helpers ----------------
    private static double R1(double v) => Math.Round(v, 1);
    private static string Pct(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string FmtBytes(double b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0; b = Math.Abs(b);
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        string n = (b >= 100 || i == 0) ? b.ToString("0", CultureInfo.InvariantCulture) : b.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{n} {u[i]}";
    }
    private static string FmtRate(double b) => FmtBytes(b) + "/s";
    // Match the dashboard's dateFmt setting: "intl" = DD/MM/YYYY 24-hour, "us" (default) = MM/DD/YYYY 12-hour.
    private static string FmtTime(long ms, string dateFmt)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        string fmt = string.Equals(dateFmt, "intl", StringComparison.OrdinalIgnoreCase)
            ? "dd/MM/yyyy HH:mm"
            : "MM/dd/yyyy hh:mm tt";
        return dt.ToString(fmt, CultureInfo.InvariantCulture);
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString();
    }

    private static string Esc(string? s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ---------------- static page assets ----------------
    private static string Css() => """
        :root{--bg:#eceff3;--panel:#ffffff;--edge:#dee2e8;--text:#1b1e24;--muted:#667085;}
        *{box-sizing:border-box;}
        body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 "Segoe UI",system-ui,-apple-system,Roboto,sans-serif;}
        .bar{display:flex;align-items:center;justify-content:space-between;padding:16px 22px;border-bottom:1px solid var(--edge);position:sticky;top:0;background:var(--panel);z-index:2;}
        .ttl{display:flex;align-items:center;}
        .ttl b{font-size:18px;letter-spacing:.3px;}
        .ttl .host{margin-left:12px;color:var(--muted);}
        .back{display:inline-flex;align-items:center;gap:6px;background:transparent;color:var(--text);border:1px solid var(--edge);border-radius:9px;padding:6px 12px;font-size:13px;cursor:pointer;margin-right:16px;}
        .back:hover{background:#eef1f5;}
        .acts button{background:var(--panel);color:var(--text);border:1px solid var(--edge);border-radius:9px;padding:8px 16px;font-size:13px;cursor:pointer;margin-left:8px;}
        .acts button:hover{background:#eef1f5;}
        .meta{display:flex;flex-wrap:wrap;align-items:baseline;gap:10px 26px;padding:14px 22px;color:var(--muted);border-bottom:1px solid var(--edge);background:var(--panel);}
        .mi{display:flex;align-items:baseline;gap:8px;}
        .mk{text-transform:uppercase;font-size:11px;letter-spacing:1px;}
        .mv{color:var(--text);}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:14px;padding:18px 22px;}
        .card{background:var(--panel);border:1px solid var(--edge);border-radius:12px;padding:18px 16px;text-align:center;box-shadow:0 1px 2px rgba(16,24,40,.04);}
        .cv{font-size:32px;font-weight:700;line-height:1.1;font-variant-numeric:tabular-nums;}
        .cl{font-size:11px;text-transform:uppercase;letter-spacing:1px;color:var(--muted);margin-top:7px;}
        .cs{font-size:11px;color:var(--muted);margin-top:4px;}
        .sec{padding:6px 22px 4px;}
        .sec h2{font-size:13px;font-weight:600;text-transform:uppercase;letter-spacing:1.5px;color:var(--muted);margin:18px 0 6px;}
        .chart{width:100%;height:220px;}
        .nodata{padding:60px 22px;text-align:center;color:var(--muted);font-size:15px;}
        @media print{.bar{position:static;}.acts{display:none;}body{background:#fff;}}
        """;

    private static string RenderJs() => """
        (function(){
          var R = window.__REPORT__, t = R.t;
          function fmtBytes(b){ if(b==null||isNaN(b))return "-"; var u=["B","KB","MB","GB","TB","PB"],i=0; b=Math.abs(b); while(b>=1024&&i<u.length-1){b/=1024;i++;} return (b>=100||i===0?b.toFixed(0):b.toFixed(1))+" "+u[i]; }
          function fmtRate(b){ return fmtBytes(b)+"/s"; }
          var dl=document.getElementById("dl");
          if(dl) dl.addEventListener("click",function(){
            var html="<!DOCTYPE html>\n"+document.documentElement.outerHTML;
            var blob=new Blob([html],{type:"text/html"}), a=document.createElement("a");
            a.href=URL.createObjectURL(blob); a.download=R.fileName;
            document.body.appendChild(a); a.click(); a.remove();
            setTimeout(function(){ URL.revokeObjectURL(a.href); },1000);
          });
          var pr=document.getElementById("pr");
          if(pr) pr.addEventListener("click",function(){ window.print(); });
          if(!R.buckets || !R.buckets.length) return;

          var ts = R.buckets.map(function(b){return b[0];});
          function xy(col){ return R.buckets.map(function(b){return [b[0], b[col]];}); }
          var axisC = "#667085", splitC = "#e7eaef", lineC = "#cbd1d8";
          function base(){
            return {
              animation:false,
              // Left gutter fits the widest y-axis label across all charts: byte/rate labels like
              // "93.1 GB" or "2.1 MB/s" are wider than "%" and clip at 52 on the kiosk's font (they
              // happened to just fit on the dev box's font). 70 keeps all four charts aligned too.
              grid:{left:70,right:18,top:14,bottom:26},
              tooltip:{trigger:"axis", backgroundColor:"#ffffff", borderColor:"#dee2e8", textStyle:{color:"#1b1e24"}},
              legend:{right:8, top:0, textStyle:{color:axisC}, itemWidth:14, itemHeight:8},
              xAxis:{type:"time", axisLine:{lineStyle:{color:lineC}}, axisLabel:{color:axisC}, splitLine:{show:false}},
            };
          }
          var charts=[];
          function mk(id,opt){ var c=echarts.init(document.getElementById(id)); c.setOption(opt); charts.push(c); }

          // CPU + Memory: percent axis, max area + avg line.
          function pctChart(id, avgCol, maxCol, areaColor, lineColor){
            var o=base();
            o.yAxis={type:"value",min:0,max:100,axisLabel:{formatter:"{value}%",color:axisC},splitLine:{lineStyle:{color:splitC}}};
            o.tooltip.valueFormatter=function(v){ return (Math.round(v*10)/10)+"%"; };
            o.series=[
              {name:t.max,type:"line",showSymbol:false,data:xy(maxCol),lineStyle:{width:1,color:lineColor},areaStyle:{color:areaColor}},
              {name:t.avg,type:"line",showSymbol:false,data:xy(avgCol),lineStyle:{width:1.6,color:lineColor}},
            ];
            mk(id,o);
          }
          pctChart("cpuChart",1,2,"rgba(34,184,95,.14)","#22b85f");
          pctChart("memChart",3,4,"rgba(59,125,240,.12)","#3b7df0");

          // Network: byte-rate axis, peak in (cyan) + peak out (orange).
          var n=base();
          n.yAxis={type:"value",min:0,axisLabel:{formatter:function(v){return fmtBytes(v)+"/s";},color:axisC},splitLine:{lineStyle:{color:splitC}}};
          n.tooltip.valueFormatter=function(v){ return fmtRate(v); };
          n.series=[
            {name:t.rx,type:"line",showSymbol:false,data:xy(6),lineStyle:{width:1.4,color:"#10b0bd"},areaStyle:{color:"rgba(16,176,189,.12)"}},
            {name:t.tx,type:"line",showSymbol:false,data:xy(8),lineStyle:{width:1.4,color:"#ef8a1f"},areaStyle:{color:"rgba(239,138,31,.12)"}},
          ];
          mk("netChart",n);

          // Disk free space: one line per mount (free bytes over time), only when there is disk history.
          var diskEl = document.getElementById("diskChart");
          if (diskEl && R.disks && R.disks.length) {
            var palette = ["#3b7df0","#22b85f","#10b0bd","#ef8a1f","#a06bff","#e0457e"];
            var dk = base();
            dk.yAxis = {type:"value", min:0, axisLabel:{formatter:function(v){return fmtBytes(v);}, color:axisC}, splitLine:{lineStyle:{color:splitC}}};
            dk.tooltip.valueFormatter = function(v){ return fmtBytes(v); };
            dk.legend.data = R.disks.map(function(x){ return x.mount; });
            dk.series = R.disks.map(function(x,i){
              return {name:x.mount, type:"line", showSymbol:false, data:x.points, lineStyle:{width:1.6, color:palette[i%palette.length]}};
            });
            mk("diskChart", dk);
          }

          window.addEventListener("resize",function(){ charts.forEach(function(c){ c.resize(); }); });
        })();
        """;
}

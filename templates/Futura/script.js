/* ============================================================================
   Futura template — companion script (injected after app.js when this template
   is active). It turns the default CPU + Network boxes into the modern column-
   stream design:

     - injects a headline number + stat chips + an ECharts column chart into each
       box (the matching default gauge/line is hidden by style.css),
     - drives them from the app's live data, read straight from app.js's globals
       (state / pctColor / fmtRate / COLORS / echarts), the same way the default
       big clock taps app.js helpers — so no core (app.js) changes are needed.

   It runs a ~1s loop that re-injects after the board re-renders (host switch, box
   add/remove) and tears itself down when the template is switched away (detected
   via app.js's `settings.template`). Everything is idempotent + self-cleaning so
   re-applying never stacks duplicates, timers, or chart instances.
   ============================================================================ */
(function () {
  "use strict";

  // Tear down a previous instance before building a new one (template re-applied / live re-inject).
  if (window.__futuraDesign) { try { window.__futuraDesign.stop(); } catch (e) { /* ignore */ } }

  var D = window.__futuraDesign = { timer: null, charts: [] };
  document.body.classList.add("futura-design");

  var CAP = 30;   // recent samples shown as columns (chunky + readable in the fixed-width box)

  // ---- echarts option builders (flat solid colours, no gradients) ----
  function cpuOpt() {
    return {
      grid: { left: 3, right: 3, top: 14, bottom: 3, containLabel: false },
      tooltip: { show: false },
      xAxis: { type: "category", show: false, data: [] },
      yAxis: {
        type: "value", min: 0, max: 100, axisLabel: { show: false }, axisLine: { show: false },
        axisTick: { show: false }, splitLine: { lineStyle: { color: "rgba(255,255,255,0.05)" } },
      },
      animationDuration: 300, animationDurationUpdate: 300,
      series: [{ type: "bar", barCategoryGap: "26%", data: [], itemStyle: { borderRadius: [3, 3, 0, 0] } }],
    };
  }
  function netOpt() {
    return {
      grid: { left: 3, right: 3, top: 14, bottom: 3, containLabel: false },
      tooltip: { show: false },
      xAxis: { type: "category", show: false, data: [] },
      yAxis: { type: "value", show: false, min: 0 },
      animationDuration: 300, animationDurationUpdate: 300,
      series: [
        { name: "rx", type: "bar", stack: "net", barCategoryGap: "26%", data: [], itemStyle: { color: COLORS.cyan } },
        { name: "tx", type: "bar", stack: "net", data: [], itemStyle: { color: COLORS.orange, borderRadius: [3, 3, 0, 0] } },
      ],
    };
  }
  // A "nice" ceiling just above the window's peak so the bars fill the box while staying contained.
  function niceCeil(p) { return p <= 25 ? 30 : p <= 45 ? 50 : p <= 70 ? 75 : 100; }
  // Dashed "avg" reference line, re-applied with the live average each tick. No text label — the
  // average is already shown big as the hero readout, and the tiny on-chart number was unreadable.
  function avgLine(v) {
    return {
      silent: true, symbol: "none",
      lineStyle: { color: "rgba(255,255,255,0.5)", type: "dashed", width: 1 },
      label: { show: false },
      data: [{ yAxis: Math.round(v) }],
    };
  }

  // ---- chart lifecycle (survives board re-renders) ----
  function disposeDetached() {
    D.charts = D.charts.filter(function (c) {
      var el = c.getDom && c.getDom();
      if (!el || !el.isConnected) { try { c.dispose(); } catch (e) { /* ignore */ } return false; }
      return true;
    });
  }
  function chartFor(el, optFn) {
    if (!el) return null;
    var c = echarts.getInstanceByDom(el);
    if (!c) { c = echarts.init(el, null, { renderer: "canvas" }); c.setOption(optFn()); D.charts.push(c); }
    return c;
  }

  // ---- inject the new UI into a box once ----
  function ensureCpu(box) {
    var f = box.querySelector(".f-cpu");
    if (!f) {
      f = document.createElement("div"); f.className = "f-cpu"; f.setAttribute("data-futura", "");
      f.innerHTML =
        '<div class="f-hero"><div class="f-big" data-val>--%</div><div class="f-sub">avg load</div></div>' +
        '<div class="f-chips">' +
          '<div class="f-chip"><span class="f-k">PEAK</span><span class="f-v orange" data-peak>--</span></div>' +
          '<div class="f-chip"><span class="f-k">LOW</span><span class="f-v green" data-low>--</span></div>' +
        '</div>' +
        '<div class="f-chart" data-chart></div>';
      box.appendChild(f);
    }
    return f;
  }
  function ensureNet(box) {
    var f = box.querySelector(".f-net");
    if (!f) {
      f = document.createElement("div"); f.className = "f-net"; f.setAttribute("data-futura", "");
      f.innerHTML =
        '<div class="f-hero"><div class="f-big" data-val>--</div><div class="f-sub">total throughput</div></div>' +
        '<div class="f-chips">' +
          '<div class="f-chip"><span class="f-k">RX</span><span class="f-v cyan" data-rx>--</span></div>' +
          '<div class="f-chip"><span class="f-k">TX</span><span class="f-v orange" data-tx>--</span></div>' +
        '</div>' +
        '<div class="f-chart" data-chart></div>';
      box.appendChild(f);
    }
    return f;
  }

  function stop() {
    if (D.timer) { clearInterval(D.timer); D.timer = null; }
    D.charts.forEach(function (c) { try { c.dispose(); } catch (e) { /* ignore */ } });
    D.charts = [];
    document.querySelectorAll("[data-futura]").forEach(function (e) { e.remove(); });
    document.body.classList.remove("futura-design");
    if (window.__futuraDesign === D) window.__futuraDesign = null;
  }
  D.stop = stop;

  function tick() {
    // Switched away from this template → clean up and stop.
    if (typeof settings !== "undefined" && settings.template !== "Futura") { stop(); return; }
    disposeDetached();

    var host = (typeof state !== "undefined") ? state.selected : null;
    if (!host) return;
    var snap = state.latest.get(host), s = state.series.get(host);
    if (!snap || !s) return;

    // CPU box → column stream.
    var cbox = document.querySelector(".box-cpu");
    if (cbox) {
      var f = ensureCpu(cbox);
      var cpu = (snap.cpu && snap.cpu.totalPct) || 0;
      var win = s.cpu.slice(-CAP);
      var peak = win.length ? Math.max.apply(null, win) : 0;
      var low = win.length ? Math.min.apply(null, win) : 0;
      var avg = win.length ? win.reduce(function (a, b) { return a + b; }, 0) / win.length : 0;
      var vEl = f.querySelector("[data-val]");
      vEl.textContent = Math.round(cpu) + "%"; vEl.style.color = pctColor(cpu);
      f.querySelector("[data-peak]").textContent = Math.round(peak) + "%";
      f.querySelector("[data-low]").textContent = Math.round(low) + "%";
      var c = chartFor(f.querySelector("[data-chart]"), cpuOpt);
      if (c) {
        c.resize();
        c.setOption({
          xAxis: { data: win.map(function (_, i) { return i; }) },
          yAxis: { max: niceCeil(peak) },
          series: [{
            data: win.map(function (v) { return { value: Math.round(v), itemStyle: { color: pctColor(v), borderRadius: [3, 3, 0, 0] } }; }),
            markLine: avgLine(avg),
          }],
        });
      }
    }

    // Network box → stacked column stream.
    var nbox = document.querySelector(".box-net");
    if (nbox) {
      var fn = ensureNet(nbox);
      var rx = s.rx[s.rx.length - 1] || 0, tx = s.tx[s.tx.length - 1] || 0;
      fn.querySelector("[data-val]").textContent = fmtRate(rx + tx);
      fn.querySelector("[data-rx]").textContent = fmtRate(rx);
      fn.querySelector("[data-tx]").textContent = fmtRate(tx);
      var rxw = s.rx.slice(-CAP), txw = s.tx.slice(-CAP);
      var cn = chartFor(fn.querySelector("[data-chart]"), netOpt);
      if (cn) {
        cn.resize();
        cn.setOption({ xAxis: { data: rxw.map(function (_, i) { return i; }) }, series: [{ data: rxw }, { data: txw }] });
      }
    }
  }

  tick();
  D.timer = setInterval(tick, 1000);
})();

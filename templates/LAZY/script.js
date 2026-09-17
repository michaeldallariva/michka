/* ============================================================================
   LAZY template - companion script (injected after app.js when this template is
   active). It replaces the freeform board of draggable boxes with ONE fixed
   overview panel that lists CPU / memory / GPU / VRAM / disks / network as flat
   horizontal 0-100% usage bars.

   Like the other template overlays it reads app.js's live globals by bare name
   (state / COLORS / pctColor / fmtBytes / fmtRate / clamp / settings / t) - no
   core (app.js) changes are needed - and runs a ~1s loop that re-injects after
   the board re-renders (host switch, etc.) and tears itself down when the
   template is switched away. Everything is idempotent + self-cleaning so
   re-applying never stacks duplicate panels or timers.

   GPU readings aren't in the snapshot globals, so they're polled from the hub's
   /api/gpu endpoint (the same source the GPU widget uses), throttled to ~2s.
   ============================================================================ */
(function () {
  "use strict";

  // Tear down a previous instance before building a new one (re-applied / live re-inject).
  if (window.__lazyDesign) { try { window.__lazyDesign.stop(); } catch (e) { /* ignore */ } }

  var D = window.__lazyDesign = { timer: null, gpuTimer: null, gpu: null, gpuHost: null };
  document.body.classList.add("lazy-design");

  // t() with an inline fallback, so strings missing from the lang files still read well.
  function tr(key, def) {
    try { var v = (typeof t === "function") ? t(key) : null; return (v && v !== key) ? v : def; }
    catch (e) { return def; }
  }

  // ---- the one overview panel (injected into #board, survives board re-renders) ----
  function ensurePanel() {
    var board = document.getElementById("board");
    if (!board) return null;
    var panel = board.querySelector(".lz-panel");
    if (!panel) {
      panel = document.createElement("div");
      panel.className = "lz-panel";
      panel.setAttribute("data-lazy", "");
      panel.innerHTML =
        '<div class="card-head"><h2>' + esc(tr("box.overview", "Overview")) + '</h2></div>' +
        '<div class="lz-rows" data-lz-rows></div>';
      board.appendChild(panel);
    }
    return panel;
  }

  // Build the row stack only when the set of metrics changes (disks/gpu appear or
  // go away); otherwise just update values in place - no flicker.
  function paint(panel, defs) {
    var rowsEl = panel.querySelector("[data-lz-rows]");
    var sig = defs.map(function (d) { return d.key; }).join(",");
    if (rowsEl.getAttribute("data-sig") !== sig) {
      rowsEl.setAttribute("data-sig", sig);
      rowsEl.innerHTML = "";
      defs.forEach(function (d) {
        var r = document.createElement("div");
        r.className = "lz-row"; r.setAttribute("data-k", d.key);
        r.innerHTML = '<div class="lz-label"></div><div class="lz-track"><div class="lz-fill"></div></div><div class="lz-val"></div>';
        rowsEl.appendChild(r);
      });
    }
    var rows = rowsEl.children;
    defs.forEach(function (d, i) {
      var r = rows[i]; if (!r) return;
      var lbl = r.querySelector(".lz-label");
      lbl.textContent = d.label; lbl.title = d.label;
      var fill = r.querySelector(".lz-fill");
      fill.style.width = clamp(d.pct, 0, 100) + "%";
      fill.style.background = d.color;
      r.querySelector(".lz-val").textContent = d.val;
    });
  }

  function pollGpu() {
    var host = (typeof state !== "undefined") ? state.selected : null;
    if (!host) { D.gpu = null; return; }
    fetch("/api/gpu?host=" + encodeURIComponent(host))
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (d) { if (state.selected === host) D.gpu = (d && d.gpus && d.gpus[0]) || null; })
      .catch(function () { /* hub busy / no sensor - leave last value */ });
  }

  function stop() {
    if (D.timer) { clearInterval(D.timer); D.timer = null; }
    if (D.gpuTimer) { clearInterval(D.gpuTimer); D.gpuTimer = null; }
    document.querySelectorAll("[data-lazy]").forEach(function (e) { e.remove(); });
    document.body.classList.remove("lazy-design");
    if (window.__lazyDesign === D) window.__lazyDesign = null;
  }
  D.stop = stop;

  function tick() {
    // Switched away from this template → clean up and stop.
    if (typeof settings !== "undefined" && settings.template !== "LAZY") { stop(); return; }

    var host = (typeof state !== "undefined") ? state.selected : null;
    if (!host) return;

    // Host changed → drop stale GPU reading and re-poll for the new host.
    if (D.gpuHost !== host) { D.gpuHost = host; D.gpu = null; pollGpu(); }

    var panel = ensurePanel();
    if (!panel) return;

    var snap = state.latest.get(host), s = state.series.get(host);
    if (!snap) return;

    var defs = [];

    // CPU + memory are always present.
    var cpu = (snap.cpu && snap.cpu.totalPct) || 0;
    defs.push({ key: "cpu", label: tr("box.cpu", "CPU"), pct: cpu, val: Math.round(cpu) + "%", color: COLORS.green });

    var mem = snap.mem || {};
    var memPct = mem.pct || 0;
    defs.push({ key: "mem", label: tr("box.mem", "Memory"), pct: memPct, val: Math.round(memPct) + "%", color: COLORS.blue });

    // GPU (polled) - utilisation + VRAM, each shown only when the host reports it.
    var g = D.gpu;
    if (g) {
      if (typeof g.utilPct === "number") {
        var u = clamp(g.utilPct, 0, 100);
        defs.push({ key: "gpu", label: "GPU", pct: u, val: Math.round(u) + "%", color: COLORS.purple });
      }
      if (g.memTotalBytes > 0) {
        var vp = (g.memUsedBytes || 0) / g.memTotalBytes * 100;
        defs.push({ key: "vram", label: "VRAM", pct: vp, val: Math.round(vp) + "%", color: COLORS.cyan });
      }
    }

    // Disks - one bar per mount (cap at 5 so the stack stays readable), red near-full.
    (snap.disks || []).slice(0, 5).forEach(function (d, idx) {
      var dp = d.pct || 0;
      defs.push({ key: "disk" + idx, label: d.mount, pct: dp, val: Math.round(dp) + "%", color: dp >= 85 ? COLORS.red : COLORS.yellow });
    });

    // Network - no natural 0-100%, so the bar fills relative to the session peak
    // throughput (in/out) and the value shows the live rate.
    if (s) {
      var rx = s.rx[s.rx.length - 1] || 0, tx = s.tx[s.tx.length - 1] || 0;
      var peak = 1;
      for (var i = 0; i < s.rx.length; i++) { if (s.rx[i] > peak) peak = s.rx[i]; }
      for (var j = 0; j < s.tx.length; j++) { if (s.tx[j] > peak) peak = s.tx[j]; }
      defs.push({ key: "rx", label: tr("net.rx", "Net Rx"), pct: rx / peak * 100, val: fmtRate(rx), color: COLORS.cyan });
      defs.push({ key: "tx", label: tr("net.tx", "Net Tx"), pct: tx / peak * 100, val: fmtRate(tx), color: COLORS.orange });
    }

    paint(panel, defs);
  }

  tick();
  D.timer = setInterval(tick, 1000);
  D.gpuTimer = setInterval(pollGpu, 2000);
})();

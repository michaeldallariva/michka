/* ============================================================================
   Marty template — companion script (injected after app.js when active).

   Turns each metric box into a "time-circuits" LED readout: NUMBERS ONLY, no
   gauges/charts. It injects a `.marty` block (small label + big 7-seg-style LED
   value, with a dim "ghost" copy behind for the unlit segments) into the CPU /
   Memory / Network / Disks / Temp boxes, and drives them from app.js's live data
   read straight from its globals (state / fmtRate / settings / toF) — the same
   approach the default clock + the Futura template use, so no core changes.

   style.css hides the default gauge/line/bar bodies and paints the brushed-metal
   panels + LED colours (and restyles the GPU drop-in widget, which keeps polling
   on its own). A ~1s loop re-injects after board re-renders and self-tears-down
   when the template is switched away (settings.template !== "Marty"). Idempotent.
   ============================================================================ */
(function () {
  "use strict";

  if (window.__martyDesign) { try { window.__martyDesign.stop(); } catch (e) { /* ignore */ } }
  var D = window.__martyDesign = { timer: null };
  document.body.classList.add("marty-design");

  // Build one "LABEL + LED value" readout.
  function ledRow(label) {
    var r = document.createElement("div");
    r.className = "marty-readout";
    r.innerHTML = '<div class="marty-label"></div><div class="marty-num"><span class="ghost"></span><span class="on"></span></div>';
    r.querySelector(".marty-label").textContent = label;
    return r;
  }
  function setLed(rowEl, val) {
    val = String(val);
    var on = rowEl.querySelector(".on");
    if (on.textContent !== val) {
      on.textContent = val;
      rowEl.querySelector(".ghost").textContent = val.replace(/[0-9]/g, "8");  // unlit segments
    }
  }
  // Secondary info lines under the big number (CPU model/cores, RAM used/total) so the readout fills
  // the box's leftover height. First line renders as the "lead" (brighter). Pass [] to clear it.
  function setInfoLines(m, lines) {
    var info = m.querySelector(".marty-info");
    if (!lines.length) { if (info) info.remove(); return; }
    if (!info) { info = document.createElement("div"); info.className = "marty-info"; m.appendChild(info); }
    while (info.children.length < lines.length) { var d = document.createElement("div"); d.className = "marty-iline"; info.appendChild(d); }
    while (info.children.length > lines.length) { info.removeChild(info.lastChild); }
    lines.forEach(function (txt, i) {
      var el = info.children[i];
      if (el.textContent !== txt) el.textContent = txt;
      el.classList.toggle("lead", i === 0);
    });
  }
  // Ensure a box has a `.marty` block with one readout per label; rebuild if the
  // label set changed (e.g. disks added/removed or a host with different sensors).
  function ensure(box, cls, labels) {
    var m = box.querySelector(".marty");
    var sig = cls + "|" + labels.join(",");
    if (m && m.dataset.sig === sig) return m;
    if (!m) { m = document.createElement("div"); m.setAttribute("data-marty", ""); box.appendChild(m); }
    m.className = "marty " + cls;
    m.dataset.sig = sig;
    m.innerHTML = "";
    labels.forEach(function (l) { m.appendChild(ledRow(l)); });
    return m;
  }

  function stop() {
    if (D.timer) { clearInterval(D.timer); D.timer = null; }
    document.querySelectorAll("[data-marty]").forEach(function (e) { e.remove(); });
    document.body.classList.remove("marty-design");
    if (window.__martyDesign === D) window.__martyDesign = null;
  }
  D.stop = stop;

  function tick() {
    if (typeof settings !== "undefined" && settings.template !== "Marty") { stop(); return; }
    var host = (typeof state !== "undefined") ? state.selected : null;
    if (!host) return;
    var snap = state.latest.get(host), s = state.series.get(host);
    if (!snap || !s) return;

    // CPU — load %, with model + core/thread count below.
    var cbox = document.querySelector(".box-cpu");
    if (cbox) {
      var ci = snap.cpu || {};
      var m = ensure(cbox, "cpu", ["LOAD"]);
      setLed(m.children[0], Math.round(ci.totalPct || 0) + "%");
      var cores = ci.cores || 0, threads = ci.threads || 0;
      var coreTxt = cores ? (threads > cores ? cores + " cores · " + threads + " threads" : cores + " cores")
        : (threads ? threads + " threads" : "");
      var lines = [];
      if (ci.model) lines.push(ci.model);
      if (coreTxt) lines.push(coreTxt);
      setInfoLines(m, lines);
    }

    // Memory — RAM used %, with used / total below.
    var mbox = document.querySelector(".box-mem");
    if (mbox) {
      var me = snap.mem || {};
      var mm = ensure(mbox, "mem", ["RAM"]);
      setLed(mm.children[0], Math.round(me.pct || 0) + "%");
      setInfoLines(mm, me.totalBytes ? [fmtBytes(me.usedBytes) + " / " + fmtBytes(me.totalBytes)] : []);
    }

    // Network — RX / TX rates.
    var nbox = document.querySelector(".box-net");
    if (nbox) {
      var nm = ensure(nbox, "net", ["RX", "TX"]);
      setLed(nm.children[0], fmtRate(s.rx[s.rx.length - 1] || 0));
      setLed(nm.children[1], fmtRate(s.tx[s.tx.length - 1] || 0));
    }

    // Disks — one row per mount (capped at 5), each showing used %. The row size + gap shrink as the
    // disk count rises so 2..5 disks always fit the box height (and never spill out the bottom).
    var dbox = document.querySelector(".box-storage");
    if (dbox) {
      var disks = (snap.disks || []).slice(0, 5);
      var labels = disks.length ? disks.map(function (d) { return d.mount; }) : ["DISK"];
      var dm = ensure(dbox, "disks", labels);
      var n = disks.length || 1;
      var fs = n <= 2 ? 40 : n === 3 ? 34 : n === 4 ? 28 : 24;   // px (scaled by --ui-scale in CSS)
      var gap = n <= 2 ? 14 : n === 3 ? 12 : n === 4 ? 9 : 7;
      dm.style.setProperty("--disk-fs", fs + "px");
      dm.style.setProperty("--disk-gap", gap + "px");
      if (disks.length) disks.forEach(function (d, i) { setLed(dm.children[i], Math.round(d.pct || 0) + "%"); });
      else setLed(dm.children[0], "--");
    }

    // Temp/Load — thermal °C + load (Linux) or process/thread counts (Windows `sys`).
    var tbox = document.querySelector(".box-temp");
    if (tbox) {
      if (snap.sys) {
        var sm = ensure(tbox, "temp", ["PROC", "THRD"]);
        setLed(sm.children[0], snap.sys.processes != null ? snap.sys.processes : "--");
        setLed(sm.children[1], snap.sys.threads != null ? snap.sys.threads : "--");
      } else {
        var temps = snap.temps || [];
        var maxC = temps.reduce(function (a, t) { return Math.max(a, t.celsius || 0); }, 0);
        if (temps.length) {
          var tm = ensure(tbox, "temp", ["TEMP", "LOAD"]);
          var f = (typeof settings !== "undefined" && settings.tempUnit === "F" && typeof toF === "function");
          setLed(tm.children[0], Math.round(f ? toF(maxC) : maxC) + "°");
          setLed(tm.children[1], snap.load ? snap.load.one.toFixed(2) : "--");
        } else {
          var lm = ensure(tbox, "temp", ["LOAD"]);
          setLed(lm.children[0], snap.load ? snap.load.one.toFixed(2) : "--");
        }
      }
    }
  }

  tick();
  D.timer = setInterval(tick, 1000);
})();

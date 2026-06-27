"use strict";

/* ---------------- helpers ---------------- */
const $ = (id) => document.getElementById(id);
const COLORS = {
  cyan: "#2ee6d6", blue: "#4f8cff", purple: "#a472ff",
  orange: "#ff9f43", red: "#ff5470", green: "#36e07a", yellow: "#ffd24a",
};
const SERIES_CAP = 60;

/* ---------------- settings (persisted server-side in michka.conf) ---------------- */
const SETTINGS_DEFAULTS = { lang: "en", tempUnit: "C", cores: "on", cycle: "off", cycleSec: "10", dateFmt: "us", screenSize: "1280x400", ssMode: "off", ssTimeout: "20", kioskAuto: "on", template: "" };
const settings = { ...SETTINGS_DEFAULTS };

/* ---------------- i18n ----------------
   UI strings live in /lang/<code>.json (embedded in the binary, served as static files). The active
   language is fetched and applied live — no hub restart. `t(key, vars)` resolves a key, substituting
   {placeholders}; static markup carries data-i18n / data-i18n-html / data-i18n-ph / data-i18n-aria
   attributes that applyTranslations() fills in. Missing keys fall back to the key itself. */
const LANGS = [
  { code: "en", name: "English" },
  { code: "fr", name: "Français" },
  { code: "de", name: "Deutsch" },
  { code: "es", name: "Español" },
  { code: "pt", name: "Português" },
  { code: "it", name: "Italiano" },
  { code: "ru", name: "Русский" },
  { code: "zh", name: "中文" },
];
let translations = {};
function t(key, vars) {
  let s = (translations && translations[key] != null) ? translations[key] : key;
  if (vars) for (const k in vars) s = s.replaceAll("{" + k + "}", vars[k]);
  return s;
}
function applyTranslations(root) {
  root = root || document;
  root.querySelectorAll("[data-i18n]").forEach((el) => { el.textContent = t(el.getAttribute("data-i18n")); });
  root.querySelectorAll("[data-i18n-html]").forEach((el) => { el.innerHTML = t(el.getAttribute("data-i18n-html")); });
  root.querySelectorAll("[data-i18n-ph]").forEach((el) => { el.setAttribute("placeholder", t(el.getAttribute("data-i18n-ph"))); });
  root.querySelectorAll("[data-i18n-aria]").forEach((el) => { el.setAttribute("aria-label", t(el.getAttribute("data-i18n-aria"))); });
}
async function loadLang(code) {
  code = code || "en";
  try {
    const r = await fetch(`/lang/${encodeURIComponent(code)}.json`);
    if (r.ok) translations = await r.json();
    else if (code !== "en") return loadLang("en");
  } catch { if (code !== "en") return loadLang("en"); }
  settings.lang = code;
  document.documentElement.setAttribute("lang", code);
  applyTranslations(document);
  // Re-render the parts whose text is built in JS rather than carried by data-i18n attributes.
  if ($("osk")) buildOsk($("osk").classList.contains("osk-numeric") ? "num" : "text");
  renderServiceList();
  renderLayout();
  if (vOpen === "svc") renderSvcStatus();
  else if (vOpen === "widgets") renderWidgets();
}
async function postConfig(patch) {
  try {
    const r = await fetch("/api/server-config", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch),
    });
    return r.ok ? await r.json() : null;
  } catch { return null; }
}
const toF = (c) => c * 9 / 5 + 32;

function fmtBytes(b) {
  if (b == null || isNaN(b)) return "--";
  const u = ["B", "KB", "MB", "GB", "TB", "PB"];
  let i = 0; b = Math.abs(b);
  while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  return `${b >= 100 || i === 0 ? b.toFixed(0) : b.toFixed(1)} ${u[i]}`;
}
const fmtRate = (b) => `${fmtBytes(b)}/s`;
function fmtUptime(s) {
  if (!s) return "--";
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
  if (d) return `${d}d ${h}h`;
  if (h) return `${h}h ${m}m`;
  return `${m}m`;
}
const pctColor = (v) => v >= 90 ? COLORS.red : v >= 70 ? COLORS.orange : v >= 40 ? COLORS.yellow : COLORS.green;
// Solid colours only — gradients are not used anywhere in the UI. Gauge arcs take a single
// flat colour (value-based gauges still vary that one colour by load/heat); chart area fills
// use a low-alpha flat tint of the line colour.
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
const uid = () => "b" + Math.random().toString(36).slice(2, 8);
const svcShort = (n) => String(n || "").replace(/\.service$/, "");

/* ---------------- state ---------------- */
const state = {
  hosts: new Map(),      // name -> { lastSeenMs, online }
  latest: new Map(),     // name -> snapshot
  series: new Map(),     // name -> { cpu:[], mem:[], rx:[], tx:[] }
  selected: null,
};

/* dashboard layout + service monitoring state */
let layout = [];           // current host's boxes: [{ id, type, service, x, y, w, h }]
let layoutsByHost = {};    // host name -> that host's boxes (each host page has its own layout)
let defaultBoxes = null;   // seed/default layout for hosts without their own (legacy global layout)
let monitoredByHost = {};  // host name -> that host's monitored unit names (per-host selection)
let monitored = [];        // the selected host's monitored unit names (a live ref into monitoredByHost)
let allServices = [];      // searchable catalog from /api/services, for the selected host
let svcAvailable = false;
let svcListHost = null;    // which host allServices currently describes
let svcPage = 0;
const SVC_PER_PAGE = 10;
let currentScreen = 1;     // 0 = services, 1 = dashboard, 2 = settings
let boxInteracting = false; // true while a box is "armed" (held) so swipes don't fire mid-drag
let vOpen = null;          // null | "svc" | "widgets": which vertical page overlays the dashboard
const BOX_TYPES = {
  cpu: { titleKey: "box.cpu", ratio: 5 },
  mem: { titleKey: "box.mem", ratio: 4 },
  net: { titleKey: "box.net", ratio: 6 },
  storage: { titleKey: "box.storage", ratio: 4 },
  temp: { titleKey: "box.temp", ratio: 5 },
};
// Floating box types place freely (free dropped x/y, may overlap) instead of tiling into the
// standard row: service boxes only. Widget boxes are now standard tiled boxes so they fit the
// board exactly like the metric boxes (same full height, same header/divider design). Everything
// except `service` is a "standard" tiled box.
const FLOATS = (type) => type === "service";

function ensureSeries(host) {
  // dr/dw = disk read/write bytes-per-sec trend (Windows hosts only; the Disk I/O box charts them).
  if (!state.series.has(host)) state.series.set(host, { cpu: [], mem: [], rx: [], tx: [], dr: [], dw: [] });
  return state.series.get(host);
}
function pushSeries(host, snap) {
  const s = ensureSeries(host);
  let rx = 0, tx = 0;
  (snap.net || []).forEach((n) => { rx += n.rxBytesPerSec || 0; tx += n.txBytesPerSec || 0; });
  s.cpu.push(snap.cpu?.totalPct || 0);
  s.mem.push(snap.mem?.pct || 0);
  s.rx.push(rx); s.tx.push(tx);
  s.dr.push(snap.sys?.diskReadBps || 0); s.dw.push(snap.sys?.diskWriteBps || 0);
  for (const k of ["cpu", "mem", "rx", "tx", "dr", "dw"]) while (s[k].length > SERIES_CAP) s[k].shift();
}

/* ---------------- charts ---------------- */
let cpuGauge, memGauge, tempGauge, netChart, ioChart;
const charts = [];

// Network-style dual-line traffic chart (in cyan / out orange) — shared by the Network box and the
// Windows Disk I/O box (read = cyan, write = orange) so both behave like the same scrolling lines.
function trafficLineOption() {
  return {
    grid: { left: 4, right: 4, top: 12, bottom: 4, containLabel: false },
    tooltip: { show: false },
    xAxis: { type: "category", show: false, boundaryGap: false, data: [] },
    yAxis: { type: "value", show: false, min: 0 },
    animationDurationUpdate: 300,
    series: [
      { name: "in", type: "line", smooth: true, symbol: "none", lineStyle: { color: COLORS.cyan, width: 1.6 },
        areaStyle: { color: "rgba(46,230,214,0.16)" }, data: [] },
      { name: "out", type: "line", smooth: true, symbol: "none", lineStyle: { color: COLORS.orange, width: 1.6 },
        areaStyle: { color: "rgba(255,159,67,0.16)" }, data: [] },
    ],
  };
}

function gaugeOption(value, color, formatter) {
  return {
    series: [{
      type: "gauge", radius: "94%", center: ["50%", "60%"],
      startAngle: 210, endAngle: -30, min: 0, max: 100,
      progress: { show: true, width: 9, roundCap: true, itemStyle: { color } },
      axisLine: { lineStyle: { width: 9, color: [[1, "rgba(255,255,255,0.08)"]] } },
      axisTick: { show: false }, splitLine: { show: false }, axisLabel: { show: false },
      pointer: { show: false }, anchor: { show: false },
      title: { show: false },
      detail: {
        valueAnimation: true, fontSize: 24, fontWeight: 700,
        offsetCenter: [0, "2%"], color: "#e6edf7", formatter,
      },
      data: [{ value: value }],
    }],
  };
}

// (Re)create echarts instances for whichever metric boxes are currently on the board.
function mountCharts() {
  charts.forEach((c) => c.dispose());
  charts.length = 0;
  cpuGauge = memGauge = tempGauge = netChart = ioChart = null;

  const mk = (id) => { const n = $(id); if (!n) return null; const c = echarts.init(n, null, { renderer: "canvas" }); charts.push(c); return c; };
  cpuGauge = mk("cpuGauge");
  memGauge = mk("memGauge");
  tempGauge = mk("tempGauge");
  netChart = mk("netChart");
  ioChart = mk("ioChart");

  if (cpuGauge) cpuGauge.setOption(gaugeOption(0, COLORS.green, "{value}%"));
  if (memGauge) memGauge.setOption(gaugeOption(0, COLORS.blue, "{value}%"));
  if (tempGauge) tempGauge.setOption(gaugeOption(0, COLORS.cyan, "{value}°"));
  if (netChart) netChart.setOption(trafficLineOption());
  if (ioChart) ioChart.setOption(trafficLineOption());
}

/* ---------------- rendering ---------------- */
// Every section is guarded: a box may have been removed from the board, so its elements/charts
// can be absent. Service boxes are matched by their unit name against snap.services.
function render() {
  const host = state.selected;
  if (!host) return;
  const snap = state.latest.get(host);
  if (!snap) return;
  const s = ensureSeries(host);

  // CPU
  if (cpuGauge && $("cpuVal")) {
    const ci = snap.cpu || {};
    const cpu = ci.totalPct || 0;
    $("cpuVal").textContent = `${cpu.toFixed(0)}%`;
    $("cpuVal").style.color = pctColor(cpu);
    cpuGauge.setOption({ series: [{ data: [{ value: +cpu.toFixed(0) }], progress: { itemStyle: { color: pctColor(cpu) } } }] });
    renderCores(ci.perCorePct || []);
    if ($("cpuModel")) {
      const model = ci.model || t("cpu.unknown");
      $("cpuModel").textContent = model;
      $("cpuModel").title = model;
    }
    if ($("cpuCores2")) {
      const cores = ci.cores || 0, threads = ci.threads || 0;
      let txt = "";
      if (cores > 0 && threads > cores) txt = t("cpu.coresThreads", { cores, threads });
      else if (cores > 0) txt = t("cpu.coresOnly", { cores });
      else if (threads > 0) txt = t("cpu.threadsOnly", { threads });
      $("cpuCores2").textContent = txt;
    }
  }

  // Memory
  if (memGauge && $("memVal")) {
    const mem = snap.mem || {};
    const memPct = mem.pct || 0;
    $("memVal").textContent = `${memPct.toFixed(0)}%`;
    $("memVal").style.color = pctColor(memPct);
    memGauge.setOption({ series: [{ data: [{ value: +memPct.toFixed(0) }] }] });
    $("ramFill").style.width = `${memPct}%`;
    $("ramText").textContent = `${fmtBytes(mem.usedBytes)} / ${fmtBytes(mem.totalBytes)}`;
    const swapPct = mem.swapTotalBytes ? (mem.swapUsedBytes / mem.swapTotalBytes) * 100 : 0;
    $("swapFill").style.width = `${swapPct}%`;
    $("swapText").textContent = mem.swapTotalBytes ? `${fmtBytes(mem.swapUsedBytes)} / ${fmtBytes(mem.swapTotalBytes)}` : t("mem.swapNone");
  }

  // Network
  if (netChart && $("netRx")) {
    const rx = s.rx[s.rx.length - 1] || 0, tx = s.tx[s.tx.length - 1] || 0;
    $("netRx").textContent = fmtRate(rx);
    $("netTx").textContent = fmtRate(tx);
    const xs = s.rx.map((_, i) => i);
    netChart.setOption({ xAxis: { data: xs }, series: [{ data: s.rx }, { data: s.tx }] });
  }

  // Disks
  if ($("diskList")) renderDisks(snap.disks || []);

  // Thermals + load (Linux) OR disk-I/O + system (Windows, which sends `sys` and no thermals).
  if (tempGauge && $("loadBox")) {
    const sys = snap.sys;
    if (sys) {
      // Windows box: disk read/write trend chart (like the network lines) + process/thread counts + uptime.
      $("tempTitle").textContent = t("box.sys");
      const ioEl = $("ioChart");
      const wasHidden = ioEl.style.display === "none";
      $("tempGauge").style.display = "none";
      ioEl.style.display = "";
      $("loadBox").style.display = "none";
      $("sysBox").style.display = "";
      if (ioChart) {
        if (wasHidden) ioChart.resize();   // chart was init'd hidden (0px) — re-measure once shown
        const xs = s.dr.map((_, i) => i);
        ioChart.setOption({ xAxis: { data: xs }, series: [{ data: s.dr }, { data: s.dw }] });
      }
      $("sysProcs").textContent = sys.processes ?? "--";
      $("sysThreads").textContent = sys.threads ?? "--";
      $("sysUptime").textContent = t("common.up", { v: fmtUptime(snap.uptimeSec) });
    } else {
      $("tempTitle").textContent = t("box.temp");
      $("tempGauge").style.display = "";
      $("ioChart").style.display = "none";
      $("loadBox").style.display = "";
      $("sysBox").style.display = "none";
      const temps = snap.temps || [];
      const maxC = temps.reduce((m, t) => Math.max(m, t.celsius || 0), 0);
      const hot = maxC >= 75;
      const shown = settings.tempUnit === "F" ? toF(maxC) : maxC;
      const unit = settings.tempUnit === "F" ? "°F" : "°";
      const tMax = settings.tempUnit === "F" ? 220 : 100;
      tempGauge.setOption({ series: [{
        max: tMax,
        detail: { formatter: `{value}${unit}` },
        data: [{ value: +shown.toFixed(0) }],
        progress: { itemStyle: { color: hot ? COLORS.red : COLORS.cyan } },
      }] });
      const load = snap.load;
      $("load1").textContent = load ? load.one.toFixed(2) : "--";
      $("load5").textContent = load ? load.five.toFixed(2) : "--";
      $("load15").textContent = load ? load.fifteen.toFixed(2) : "--";
      $("uptime").textContent = t("common.up", { v: fmtUptime(snap.uptimeSec) });
    }
  }

  // Service boxes
  const svc = new Map();
  (snap.services || []).forEach((x) => svc.set(x.name, x));
  document.querySelectorAll(".box-service").forEach((el) => {
    renderServiceBox(el, svc.get(el.dataset.service));
  });

  // Keep the swipe-up service-status page in sync with the live snapshot when it's showing.
  if (vOpen === "svc") renderSvcStatus();
}

function renderServiceBox(el, st) {
  const nameEl = el.querySelector("[data-svc-name]");
  const stateEl = el.querySelector("[data-svc-state]");
  const metaEl = el.querySelector("[data-svc-meta]");
  if (nameEl) nameEl.textContent = svcShort(el.dataset.service);
  if (!st) {
    stateEl.className = "svc-state unknown";
    stateEl.textContent = t("service.noData");
    metaEl.textContent = "";
    return;
  }
  const up = !!st.active;
  const unknown = st.activeState === "unknown";
  stateEl.className = "svc-state " + (up ? "up" : unknown ? "unknown" : "down");
  stateEl.textContent = `${st.activeState || "?"}${st.subState ? " · " + st.subState : ""}`;
  const parts = [];
  if (st.mainPid) parts.push("pid " + st.mainPid);
  if (typeof st.memoryBytes === "number" && st.memoryBytes >= 0) parts.push(fmtBytes(st.memoryBytes));
  metaEl.textContent = parts.join(" · ");
}

function renderCores(cores) {
  const box = $("cpuCores");
  if (!box) return;
  if (settings.cores !== "on" || !cores.length) {
    box.style.display = "none";
    if (cpuGauge) cpuGauge.resize();
    return;
  }
  box.style.display = "";
  if (box.childElementCount !== cores.length) {
    box.innerHTML = "";
    cores.forEach(() => {
      const row = document.createElement("div"); row.className = "core";
      row.innerHTML = `<div class="ctrack"><div class="cfill"></div></div><span class="clbl"></span>`;
      box.appendChild(row);
    });
  }
  const rows = box.children;
  cores.forEach((v, i) => {
    const r = rows[i]; if (!r) return;
    r.querySelector(".cfill").style.height = `${v}%`;
    r.querySelector(".clbl").textContent = `${v.toFixed(0)}`;
  });
}

function renderDisks(disks) {
  const box = $("diskList");
  // Cap the bubble at 5 bars — more than that won't fit the box height.
  disks = (disks || []).slice(0, 5);
  if (!disks.length) { box.innerHTML = `<div class="empty">${esc(t("disk.none"))}</div>`; return; }
  if (box.childElementCount !== disks.length) {
    box.innerHTML = "";
    disks.forEach(() => {
      const d = document.createElement("div"); d.className = "disk";
      d.innerHTML = `<div class="top"><span class="mnt"></span><span class="cap"></span></div><div class="track"><div class="fill"></div></div>`;
      box.appendChild(d);
    });
  }
  const rows = box.children;
  disks.forEach((d, i) => {
    const el = rows[i]; if (!el) return;
    el.querySelector(".mnt").textContent = d.mount;
    el.querySelector(".cap").textContent = `${fmtBytes(d.usedBytes)} / ${fmtBytes(d.totalBytes)} · ${(d.pct || 0).toFixed(0)}%`;
    const fill = el.querySelector(".fill");
    fill.style.width = `${d.pct || 0}%`;
    fill.classList.toggle("warn", (d.pct || 0) >= 85);
  });
}

/* ---------------- dashboard board: dynamic, draggable boxes ---------------- */
function boxInner(box) {
  switch (box.type) {
    case "cpu": return `
      <div class="card-head"><h2>${esc(t("box.cpu"))}</h2><span class="metric-big" id="cpuVal">--%</span></div>
      <div class="cpu-meta">
        <div class="cpu-model" id="cpuModel">--</div>
        <div class="cpu-cores" id="cpuCores2"></div>
      </div>
      <div class="card-body col gauge-card">
        <div class="gauge" id="cpuGauge"></div>
        <div class="cores" id="cpuCores"></div>
      </div>`;
    case "mem": return `
      <div class="card-head"><h2>${esc(t("box.mem"))}</h2><span class="metric-big" id="memVal">--%</span></div>
      <div class="card-body col gauge-card">
        <div class="gauge" id="memGauge"></div>
        <div class="sub">
          <div class="bar"><label>${esc(t("mem.ram"))}</label><div class="track"><div class="fill" id="ramFill"></div></div><span id="ramText">--</span></div>
          <div class="bar"><label>${esc(t("mem.swap"))}</label><div class="track"><div class="fill swap" id="swapFill"></div></div><span id="swapText">--</span></div>
        </div>
      </div>`;
    case "net": return `
      <div class="card-head">
        <h2>${esc(t("box.net"))}</h2>
        <span class="metric-pair">
          <span class="rx"><svg viewBox="0 0 24 24" width="11" height="11" fill="currentColor" aria-hidden="true"><path d="M12 16 6 8h12z"/></svg><span id="netRx">--</span></span>
          <span class="tx"><svg viewBox="0 0 24 24" width="11" height="11" fill="currentColor" aria-hidden="true"><path d="M12 8 6 16h12z"/></svg><span id="netTx">--</span></span>
        </span>
      </div>
      <div class="card-body"><div class="chart full" id="netChart"></div></div>`;
    case "storage": return `
      <div class="card-head"><h2>${esc(t("box.storage"))}</h2></div>
      <div class="card-body"><div class="disks" id="diskList"></div></div>`;
    case "temp": return `
      <div class="card-head">
        <h2 id="tempTitle">${esc(t("box.temp"))}</h2>
      </div>
      <div class="card-body col gauge-card">
        <div class="gauge" id="tempGauge"></div>
        <div class="chart full" id="ioChart" style="display:none"></div>
        <div class="loadbox" id="loadBox">
          <div class="loads">
            <div class="load"><span class="lv" id="load1">--</span><label>${esc(t("load.1m"))}</label></div>
            <div class="load"><span class="lv" id="load5">--</span><label>${esc(t("load.5m"))}</label></div>
            <div class="load"><span class="lv" id="load15">--</span><label>${esc(t("load.15m"))}</label></div>
          </div>
          <div class="uptime" id="uptime">${esc(t("common.up", { v: "--" }))}</div>
        </div>
        <div class="loadbox" id="sysBox" style="display:none">
          <div class="loads">
            <div class="load"><span class="lv" id="sysProcs">--</span><label>${esc(t("sys.procs"))}</label></div>
            <div class="load"><span class="lv" id="sysThreads">--</span><label>${esc(t("sys.threads"))}</label></div>
          </div>
          <div class="uptime" id="sysUptime">${esc(t("common.up", { v: "--" }))}</div>
        </div>
      </div>`;
    case "service": return `
      <div class="card-head"><h2>${esc(t("service.title"))}</h2></div>
      <div class="card-body col svc-box">
        <div class="svc-name" data-svc-name>${esc(svcShort(box.service))}</div>
        <div class="svc-state unknown" data-svc-state>—</div>
        <div class="svc-meta" data-svc-meta></div>
      </div>`;
    case "widget": return `
      <div class="card-head"><h2 data-widget-title>${esc(widgetName(box.widget))}</h2></div>
      <div class="card-body widget-body" data-widget-mount><div class="widget-soon">${esc(t("widget.loading"))}</div></div>`;
    default: return "";
  }
}

function makeBoxEl(box) {
  const el = document.createElement("section");
  el.className = "card box box-" + box.type;
  el.dataset.id = box.id;
  el.dataset.type = box.type;
  if (box.service) el.dataset.service = box.service;
  // Tag a widget box with its type so the widget's own CSS (`.widget-<type> .widget-body`) applies.
  if (box.widget) { el.dataset.widget = box.widget; el.classList.add("widget-" + box.widget); }
  el.style.left = box.x + "px";
  el.style.top = box.y + "px";
  el.style.width = box.w + "px";
  el.style.height = box.h + "px";
  el.innerHTML = boxInner(box);
  return el;
}

function defaultLayout() {
  const board = $("board");
  const W = board.clientWidth || 1260, H = board.clientHeight || 330;
  const order = ["cpu", "mem", "net", "storage", "temp"];
  const sum = order.reduce((a, t) => a + BOX_TYPES[t].ratio, 0);
  const gap = LAYOUT_GAP, availW = W - gap * (order.length - 1);
  let x = 0;
  return order.map((t) => {
    const w = availW * BOX_TYPES[t].ratio / sum;
    const box = { id: uid(), type: t, service: null, x: Math.round(x), y: 0, w: Math.round(w), h: H };
    x += w + gap;
    return box;
  });
}

// Uniform spacing kept between standard boxes at all times.
const LAYOUT_GAP = 10;

// Standard boxes (everything except `service`) live in a single uniform row and never overlap each
// other. Re-tile them left-to-right in their current horizontal order, keeping a constant LAYOUT_GAP
// between every box, so a moved/added box pushes its neighbours aside. Service boxes float freely
// (and may overlap anything), so they're left exactly where the user dropped them.
function reflowStandard() {
  const board = $("board");
  const H = board.clientHeight || 330;
  const std = layout.filter((b) => !FLOATS(b.type))
                    .sort((a, b) => (a.x + a.w / 2) - (b.x + b.w / 2));
  let x = 0;
  std.forEach((b) => {
    b.x = Math.round(x);
    b.y = 0;
    b.h = H;
    x += b.w + LAYOUT_GAP;
  });
}

// Push the model's x/y/height onto the live DOM elements; with the CSS left/top transition this
// glides pushed-aside boxes into place (used on drop, so no full re-render / chart rebuild needed).
function applyPositions() {
  const board = $("board");
  layout.forEach((b) => {
    const el = board.querySelector(`.box[data-id="${b.id}"]`);
    if (!el) return;
    el.style.left = b.x + "px";
    el.style.top = b.y + "px";
    el.style.height = b.h + "px";
  });
}

// Pin every box's height to the board. Standard boxes size their height from board.clientHeight,
// measured once at layout time; if the board later changes height (a template grows the top bar —
// e.g. Futura's clock — or the window resizes) those boxes keep their old, too-tall height and
// overrun the bottom of the screen. It's most visible on the Network box, whose chart fills to the
// bottom edge (gauge boxes have fixed-height content, so an oversized box there isn't noticed).
// Watch the board and, when its height actually changes, re-tile heights + resize charts *in place*
// (no full rebuild, so no chart teardown flicker) so boxes always fit the board exactly.
let boardObserver = null, boardLastH = 0;
function observeBoard() {
  const board = $("board");
  if (!board || boardObserver || typeof ResizeObserver === "undefined") return;
  boardLastH = board.clientHeight;
  boardObserver = new ResizeObserver(() => {
    const h = board.clientHeight;
    if (!h || h === boardLastH) return;                 // width-only changes don't affect box height
    boardLastH = h;
    if (!layout.length || board.querySelector(".box.dragging")) return;  // never yank a box mid-drag
    reflowStandard();                                   // recompute standard-box heights to the board
    applyPositions();                                   // push the new heights onto the DOM
    charts.forEach((c) => c.resize());
  });
  boardObserver.observe(board);
}

// Standard boxes auto-arrange into the uniform row; service boxes keep their stored free x/y.
function renderLayout() {
  const board = $("board");
  if (!board) return;
  unmountBoxWidgets();                 // stop any board-widget timers before tearing down the DOM
  if (!layout.length) layout = defaultLayout();
  reflowStandard();
  board.innerHTML = "";
  layout.forEach((box) => {
    const el = makeBoxEl(box);
    board.appendChild(el);
    wireBox(el, box);
  });
  mountCharts();
  render();
  mountBoxWidgets();                   // (async) load + mount any widget boxes into their containers
}

// ---- Widget boxes on the dashboard board (the same on-disk widgets as the Widgets page) ----
function widgetName(type) { return (widgetDefs[type] && widgetDefs[type].name) || type; }

const mountedBoxWidgets = new Map();   // box id -> { def, ctx } for widgets mounted on the board

function unmountBoxWidgets() {
  mountedBoxWidgets.forEach((m) => {
    try { if (typeof m.def.unmount === "function") m.def.unmount(m.ctx); } catch { /* best-effort */ }
  });
  mountedBoxWidgets.clear();
}

async function mountBoxWidgets() {
  const boxes = layout.filter((b) => b.type === "widget" && b.widget);
  if (!boxes.length) return;
  await ensureWidgetCatalog();
  for (const box of boxes) {
    const el = $("board")?.querySelector(`.box[data-id="${box.id}"]`);
    const mountEl = el?.querySelector("[data-widget-mount]");
    if (!mountEl) continue;
    const title = el.querySelector("[data-widget-title]");  // refresh the name now the catalog is loaded
    if (title) title.textContent = widgetName(box.widget);
    const ok = await ensureWidgetAssets(box.widget);
    if (!mountEl.isConnected) return;                        // layout changed while the module loaded
    const def = ok ? widgetRegistry[box.widget] : null;
    if (!def) { mountEl.innerHTML = `<div class="widget-soon">${esc(t("widget.unavailable"))}</div>`; continue; }
    const ctx = widgetCtx({ id: box.id, type: box.widget }, mountEl);
    try {
      const out = def.render(ctx);
      if (out instanceof Node) { mountEl.innerHTML = ""; mountEl.appendChild(out); }
      else mountEl.innerHTML = out || "";
      if (typeof def.mount === "function") def.mount(ctx);
      mountedBoxWidgets.set(box.id, { def, ctx });
    } catch { mountEl.innerHTML = `<div class="widget-soon">${esc(t("widget.unavailable"))}</div>`; }
  }
}

// Persist the current host's boxes under that host (so other host pages are untouched).
function saveLayout() {
  if (!state.selected) return;
  layoutsByHost[state.selected] = layout;
  postConfig({ layoutHost: state.selected, layout });
}

// Load the given host's boxes (its own saved layout, else a copy of the default seed) and render.
function applyHostLayout(name) {
  const clone = (boxes) => boxes.map((b) => ({ ...b }));
  const saved = layoutsByHost[name];
  if (Array.isArray(saved) && saved.length) layout = clone(saved);
  else if (Array.isArray(defaultBoxes) && defaultBoxes.length) layout = clone(defaultBoxes);
  else layout = defaultLayout();
  renderLayout();
}

function addBox(type, ref) {
  const board = $("board");
  const W = board.clientWidth || 1260, H = board.clientHeight || 330;
  const floating = FLOATS(type);
  const w = floating ? 210 : Math.round((W - 40) / 5);
  const h = floating ? Math.min(150, H) : H;
  const service = type === "service" ? (ref || null) : null;
  const widget = type === "widget" ? (ref || null) : null;
  if (floating) {
    // Service boxes float: drop the new one centred (it may overlap — that's allowed here).
    layout.push({
      id: uid(), type, service, widget,
      x: clamp(Math.round((W - w) / 2), 0, Math.max(0, W - w)),
      y: clamp(Math.round((H - h) / 2), 0, Math.max(0, H - h)),
      w, h,
    });
  } else {
    // A new standard box (incl. widget boxes) joins the row at the right end; renderLayout's reflow
    // re-tiles them all to uniform spacing and full board height.
    layout.push({ id: uid(), type, service, widget, x: Infinity, y: 0, w, h });
  }
  renderLayout();
  saveLayout();
}

function removeBox(box) {
  layout = layout.filter((b) => b.id !== box.id);
  renderLayout();
  saveLayout();
}

// Press-and-hold model so quick swipes still navigate between screens:
//  - For the first HOLD_MS the box does nothing; moving past a few px = a swipe (cancel, let nav run).
//  - After holding ~still for HOLD_MS the box "arms": now dragging moves it, and releasing it in
//    place (no drag) opens the context menu. While armed, the swipe handler is suppressed.
const BOX_HOLD_MS = 1200;
// How far the finger may drift before arming and still count as "holding still". Over a hold the
// fingertip's contact centroid wanders well past a few px on this panel; if that drift exceeds the
// threshold the hold silently aborts and you have to start over — which is what made arming feel
// like it took 5–6s (two failed attempts) instead of BOX_HOLD_MS. It must stay below a deliberate
// swipe (which moves fast and far, so it still cancels and lets screen navigation run). A quick
// swipe also self-cancels by lifting before BOX_HOLD_MS elapses.
const BOX_MOVE_CANCEL = 100;
function wireBox(el, box) {
  let startX = 0, startY = 0, origX = 0, origY = 0;
  let armed = false, movedSinceArm = false, holdTimer = null;

  const teardown = () => {
    clearTimeout(holdTimer); holdTimer = null;
    document.removeEventListener("pointermove", onMove);
    document.removeEventListener("pointerup", onUp);
    el.classList.remove("pressing", "armed", "dragging");
  };

  const onMove = (e) => {
    const dx = e.clientX - startX, dy = e.clientY - startY;
    if (!armed) {
      // Movement before the hold completes => the user is swiping/scrolling, not editing.
      if (Math.abs(dx) > BOX_MOVE_CANCEL || Math.abs(dy) > BOX_MOVE_CANCEL) { teardown(); }
      return;
    }
    movedSinceArm = true;
    el.classList.add("dragging");
    const board = $("board");
    // The box follows the finger (clamped to the board). On release a standard box re-tiles into the
    // uniform row (pushing neighbours aside); service boxes stay wherever they're dropped.
    const nx = clamp(origX + dx, 0, board.clientWidth - el.offsetWidth);
    const ny = clamp(origY + dy, 0, board.clientHeight - el.offsetHeight);
    el.style.left = nx + "px";
    el.style.top = ny + "px";
    box.x = Math.round(nx);
    box.y = Math.round(ny);
  };
  const onUp = (e) => {
    try { el.releasePointerCapture(e.pointerId); } catch { /* ignore */ }
    const wasArmed = armed, wasMoved = movedSinceArm;
    teardown();
    if (wasArmed && wasMoved) {
      // Standard box: re-tile the row so it pushes neighbours aside, gliding them into place.
      if (!FLOATS(box.type)) { reflowStandard(); applyPositions(); }
      saveLayout();                                             // persist the new arrangement
    } else if (wasArmed) {
      openBoxMenu(box, e.clientX, e.clientY);                   // held in place => menu
    }
    armed = false; movedSinceArm = false;
    // Let the screen-swipe handler ignore the trailing touchend from this gesture.
    setTimeout(() => { boxInteracting = false; }, 120);
  };

  el.addEventListener("pointerdown", (e) => {
    if (e.button && e.button !== 0) return;
    startX = e.clientX; startY = e.clientY;
    origX = el.offsetLeft; origY = el.offsetTop;
    armed = false; movedSinceArm = false;
    el.classList.add("pressing");
    document.addEventListener("pointermove", onMove);
    document.addEventListener("pointerup", onUp);
    holdTimer = setTimeout(() => {
      armed = true;
      boxInteracting = true;
      el.classList.remove("pressing");
      el.classList.add("armed");
      try { el.setPointerCapture(e.pointerId); } catch { /* ignore */ }
    }, BOX_HOLD_MS);
  });
}

/* ---- box context menu + add chooser ---- */
let menuBox = null, lastMenuX = 0, lastMenuY = 0;

function positionPopup(el, x, y) {
  el.style.left = "0px"; el.style.top = "0px"; // measure first
  const w = el.offsetWidth, h = el.offsetHeight;
  el.style.left = clamp(x, 8, window.innerWidth - w - 8) + "px";
  el.style.top = clamp(y, 8, window.innerHeight - h - 8) + "px";
}

function openBoxMenu(box, x, y) {
  menuBox = box; lastMenuX = x; lastMenuY = y;
  $("boxAdd").classList.add("hidden");
  $("popupScrim").classList.remove("hidden");
  const m = $("boxMenu");
  m.classList.remove("hidden");
  positionPopup(m, x, y);
}

async function openAddChooser() {
  const list = $("boxAddList");
  list.innerHTML = "";
  await ensureWidgetCatalog();
  const placedTypes = new Set(layout.filter((b) => !FLOATS(b.type)).map((b) => b.type));
  const placedSvc = new Set(layout.filter((b) => b.type === "service").map((b) => b.service));
  const placedWdg = new Set(layout.filter((b) => b.type === "widget").map((b) => b.widget));
  const items = [];
  Object.keys(BOX_TYPES).forEach((bt) => { if (!placedTypes.has(bt)) items.push({ type: bt, ref: null, label: t(BOX_TYPES[bt].titleKey), tag: t("box.tagMetric") }); });
  monitored.forEach((sv) => { if (!placedSvc.has(sv)) items.push({ type: "service", ref: sv, label: svcShort(sv), tag: t("box.tagService") }); });
  // Widgets are addable to the board too (floating boxes). Their own metadata tag (e.g. "GPU") labels them.
  Object.keys(widgetDefs).forEach((wt) => { if (!placedWdg.has(wt)) items.push({ type: "widget", ref: wt, label: widgetDefs[wt].name || wt, tag: widgetDefs[wt].tag || "widget" }); });

  if (!items.length) {
    list.innerHTML = `<div class="ch-empty">${esc(t("box.addEmpty"))}</div>`;
  } else {
    items.forEach((it) => {
      const b = document.createElement("button");
      b.className = "ch-item";
      b.innerHTML = `<span>${esc(it.label)}</span><span class="ch-tag">${esc(it.tag)}</span>`;
      b.onclick = () => { addBox(it.type, it.ref); closePopups(); };
      list.appendChild(b);
    });
  }
  $("boxMenu").classList.add("hidden");
  const a = $("boxAdd");
  a.classList.remove("hidden");
  positionPopup(a, lastMenuX, lastMenuY);
}

function closePopups() {
  $("boxMenu").classList.add("hidden");
  $("boxAdd").classList.add("hidden");
  $("widgetChooser").classList.add("hidden");
  $("hostMenu").classList.add("hidden");
  $("popupScrim").classList.add("hidden");
  menuBox = null;
  pendingHost = null;
}

function wireBoard() {
  $("boxMenu").querySelectorAll("button").forEach((b) => {
    b.onclick = () => {
      if (b.dataset.act === "remove") { if (menuBox) removeBox(menuBox); closePopups(); }
      else if (b.dataset.act === "add") openAddChooser();
    };
  });
  $("popupScrim").onclick = closePopups;
  observeBoard();   // keep box heights pinned to the board if it later resizes (e.g. a template grows the top bar)
}

/* ---------------- services page ---------------- */
// The monitored set is per-host (like layouts): each host page picks from its own service catalog.
function monitoredFor(host) {
  if (!host) return [];
  if (!Array.isArray(monitoredByHost[host])) monitoredByHost[host] = [];
  return monitoredByHost[host];
}

// Load the catalog for a host (defaults to the selected one). Agent hosts report it over the wire;
// the hub's own host is read in-process. Refreshed on host switch and when entering the page.
async function loadServices(host) {
  host = host || state.selected;
  svcListHost = host;
  const url = host ? `/api/services?host=${encodeURIComponent(host)}` : "/api/services";
  try {
    const r = await fetch(url);
    if (r.ok && svcListHost === host) {  // ignore a stale response if the host changed mid-flight
      const d = await r.json();
      svcAvailable = !!d.available;
      allServices = d.services || [];
    }
  } catch { /* ignore */ }
  renderServiceList();
}

function renderServiceList() {
  const list = $("svcList");
  if (!list) return;
  // Header host pill: name + green/grey online state of the selected host.
  const host = state.selected;
  const hostEl = $("svcPickerHost"); if (hostEl) hostEl.textContent = host || "--";
  const pill = $("svcPickerHostPill");
  if (pill) pill.classList.toggle("online", !!(host && state.hosts.get(host) && state.hosts.get(host).online));
  const q = ($("svcSearch").value || "").trim().toLowerCase();
  let items = allServices;
  if (q) items = items.filter((s) => s.name.toLowerCase().includes(q) || (s.description || "").toLowerCase().includes(q));
  const total = items.length;
  const pages = Math.max(1, Math.ceil(total / SVC_PER_PAGE));
  svcPage = clamp(svcPage, 0, pages - 1);
  const slice = items.slice(svcPage * SVC_PER_PAGE, svcPage * SVC_PER_PAGE + SVC_PER_PAGE);

  list.innerHTML = "";
  if (!svcAvailable) list.innerHTML = `<div class="svc-empty">${esc(t("services.unavailable"))}</div>`;
  else if (!total) list.innerHTML = `<div class="svc-empty">${esc(t("services.noMatch"))}</div>`;
  else slice.forEach((s) => list.appendChild(svcRow(s)));

  $("svcCount").textContent = svcAvailable ? t("services.count", { m: monitored.length, n: total }) : "";
  $("svcPage").textContent = `${svcPage + 1} / ${pages}`;
  $("svcPrev").disabled = svcPage <= 0;
  $("svcNext").disabled = svcPage >= pages - 1;
}

function svcRow(s) {
  const mon = monitored.includes(s.name);
  const row = document.createElement("div");
  row.className = "svc-row" + (mon ? " monitored" : "");
  const dotCls = s.activeState === "active" ? "active" : s.activeState === "failed" ? "failed" : "";
  row.innerHTML = `
    <span class="svc-row-dot ${dotCls}"></span>
    <div class="svc-row-main">
      <div class="svc-row-name">${esc(s.name)}</div>
      <div class="svc-row-desc">${esc(s.description || s.activeState || "")}</div>
    </div>
    <button class="svc-toggle ${mon ? "on" : ""}">${esc(mon ? t("services.monitoring") : t("services.monitor"))}</button>`;
  row.querySelector(".svc-toggle").onclick = () => toggleMonitor(s.name);
  return row;
}

function toggleMonitor(name) {
  const host = state.selected;
  monitored = monitoredFor(host);  // ensure we mutate the selected host's array
  const i = monitored.indexOf(name);
  if (i >= 0) {
    monitored.splice(i, 1);
    layout = layout.filter((b) => !(b.type === "service" && b.service === name));
    saveLayout();
    renderLayout();
  } else {
    monitored.push(name);
  }
  postConfig({ servicesHost: host, services: monitored });
  renderServiceList();
}

function wireServices() {
  $("svcSearch").addEventListener("input", () => { svcPage = 0; renderServiceList(); });
  $("svcPrev").onclick = () => { svcPage--; renderServiceList(); };
  $("svcNext").onclick = () => { svcPage++; renderServiceList(); };
  $("svcBack").onclick = () => goToScreen(1);

  // No physical keyboard on the kiosk — pop up our own when the search box is tapped.
  const openSvcOsk = () => showOsk("text", $("svcSearch"), () => { svcPage = 0; renderServiceList(); });
  $("svcSearch").addEventListener("focus", openSvcOsk);
  $("svcSearch").addEventListener("click", openSvcOsk);
}

/* ---- on-screen keyboard ----
   Two layouts share one panel: "text" (US QWERTY, letters + digits + space) for the services
   search, and "num" (a 0-9 keypad) for numeric fields like the HTTP port. It edits whatever input
   is passed to showOsk() and re-runs an optional onInput callback after each keystroke. A top-bar
   X always closes it (the kiosk has no physical keyboard, so there's no other way to dismiss it). */
const OSK_ROWS = ["1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm"];
const OSK_NUM_ROWS = ["123", "456", "789"];
let oskTarget = null;   // input element currently being edited
let oskOnInput = null;  // optional callback after each change
function oskKey(label, cls, onTap) {
  const b = document.createElement("button");
  b.className = "osk-key" + (cls ? " " + cls : "");
  b.innerHTML = label;
  // pointerdown (not click) so the key fires without stealing focus / waiting on touch delays.
  b.addEventListener("pointerdown", (e) => { e.preventDefault(); onTap(); });
  return b;
}
const OSK_BS_ICON = `<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 5H8L2 12l6 7h13a1 1 0 0 0 1-1V6a1 1 0 0 0-1-1z"/><line x1="18" y1="9" x2="12" y2="15"/><line x1="12" y1="9" x2="18" y2="15"/></svg>`;
function buildOskBar() {
  // Thin header strip whose only control is a right-aligned X that dismisses the keyboard.
  const bar = document.createElement("div");
  bar.className = "osk-bar";
  const close = document.createElement("button");
  close.className = "osk-close";
  close.setAttribute("aria-label", t("osk.close"));
  close.innerHTML = `<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>`;
  close.addEventListener("pointerdown", (e) => { e.preventDefault(); hideOsk(); });
  bar.appendChild(close);
  return bar;
}
function buildOsk(mode) {
  const osk = $("osk");
  osk.innerHTML = "";
  osk.classList.toggle("osk-numeric", mode === "num");
  osk.appendChild(buildOskBar());
  if (mode === "num") {
    OSK_NUM_ROWS.forEach((chars) => {
      const r = document.createElement("div");
      r.className = "osk-row";
      chars.split("").forEach((c) => r.appendChild(oskKey(c, "num", () => oskType(c))));
      osk.appendChild(r);
    });
    const bottom = document.createElement("div");
    bottom.className = "osk-row";
    bottom.appendChild(oskKey(OSK_BS_ICON, "num", oskBackspace));
    bottom.appendChild(oskKey("0", "num", () => oskType("0")));
    bottom.appendChild(oskKey(esc(t("osk.done")), "num done", hideOsk));
    osk.appendChild(bottom);
    return;
  }
  OSK_ROWS.forEach((chars, idx) => {
    const r = document.createElement("div");
    r.className = "osk-row";
    chars.split("").forEach((c) => r.appendChild(oskKey(c, "", () => oskType(c))));
    if (idx === OSK_ROWS.length - 1) {
      r.appendChild(oskKey(OSK_BS_ICON, "wide", oskBackspace));
    }
    osk.appendChild(r);
  });
  const bottom = document.createElement("div");
  bottom.className = "osk-row";
  bottom.appendChild(oskKey(esc(t("osk.clear")), "wide", () => { if (oskTarget) { oskTarget.value = ""; oskFire(); } }));
  bottom.appendChild(oskKey(esc(t("osk.space")), "space", () => oskType(" ")));
  bottom.appendChild(oskKey(esc(t("osk.done")), "wide done", hideOsk));
  osk.appendChild(bottom);
}
function oskFire() { if (oskOnInput) oskOnInput(); }
function oskType(c) {
  if (!oskTarget) return;
  oskTarget.value += c;
  oskFire();
}
function oskBackspace() {
  if (!oskTarget) return;
  oskTarget.value = oskTarget.value.slice(0, -1);
  oskFire();
}
function showOsk(mode, target, onInput) {
  oskTarget = target || null;
  oskOnInput = onInput || null;
  buildOsk(mode || "text");
  $("osk").classList.remove("hidden");
}
function hideOsk() {
  $("osk").classList.add("hidden");
  oskTarget = null; oskOnInput = null;
}

/* ---------------- host tabs ---------------- */
function renderTabs() {
  const nav = $("hostTabs");
  const names = [...state.hosts.keys()].sort((a, b) => a.localeCompare(b));
  nav.innerHTML = "";
  names.forEach((name) => {
    const h = state.hosts.get(name);
    const tab = document.createElement("button");
    tab.className = "tab" + (name === state.selected ? " active" : "");
    tab.innerHTML = `<span class="dot ${h.online ? "online" : ""}"></span><span>${esc(name)}</span>`;
    wireHostTab(tab, name);
    nav.appendChild(tab);
  });
}

// A tap selects the host; a press-and-hold opens the remove-host menu. Movement (e.g. scrolling the
// tab strip) cancels the hold so it never fires by accident.
const HOST_HOLD_MS = 600;
function wireHostTab(tab, name) {
  let holdTimer = null, held = false, sx = 0, sy = 0;
  const clear = () => { clearTimeout(holdTimer); holdTimer = null; };
  tab.addEventListener("pointerdown", (e) => {
    held = false; sx = e.clientX; sy = e.clientY;
    holdTimer = setTimeout(() => { held = true; openHostMenu(name, e.clientX, e.clientY); }, HOST_HOLD_MS);
  });
  tab.addEventListener("pointermove", (e) => {
    if (holdTimer && (Math.abs(e.clientX - sx) > 12 || Math.abs(e.clientY - sy) > 12)) clear();
  });
  tab.addEventListener("pointerup", clear);
  tab.addEventListener("pointercancel", clear);
  tab.addEventListener("click", (e) => {
    if (held) { e.preventDefault(); held = false; return; } // the hold opened the menu — swallow the click
    selectHost(name);
  });
}

let pendingHost = null;
function openHostMenu(name, x, y) {
  pendingHost = name;
  $("hostMenuTitle").textContent = (typeof t === "function" ? t("host.removeTitle") : "Remove host") + ` — ${name}`;
  $("boxMenu").classList.add("hidden");
  $("boxAdd").classList.add("hidden");
  $("widgetChooser").classList.add("hidden");
  $("popupScrim").classList.remove("hidden");
  const m = $("hostMenu");
  m.classList.remove("hidden");
  positionPopup(m, x, y);
}
function wireHostMenu() {
  $("hostMenu").querySelectorAll("button").forEach((b) => {
    b.onclick = () => {
      if (b.dataset.act === "remove" && pendingHost) deleteHost(pendingHost);
      else closePopups();
    };
  });
}

// Drop a host from the in-memory state (history/series/layout). Does not touch the server.
function forgetHostLocal(name) {
  state.hosts.delete(name);
  state.latest.delete(name);
  state.series.delete(name);
  delete layoutsByHost[name];
}

async function deleteHost(name) {
  closePopups();
  try {
    const r = await fetch("/api/hosts/delete", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ host: name }),
    });
    if (!r.ok) throw new Error("delete failed");
  } catch { showToast(typeof t === "function" ? t("host.removeError") : "Could not remove host", "err"); return; }

  forgetHostLocal(name);
  if (widgetsByHost[name]) { delete widgetsByHost[name]; saveWidgets(); }
  if (state.selected === name) {
    state.selected = null;
    const names = [...state.hosts.keys()].sort((a, b) => a.localeCompare(b));
    if (names.length) selectHost(names[0]);
    else { layout = []; renderLayout(); }
  }
  renderTabs();
}

async function selectHost(name) {
  state.selected = name;
  renderTabs();
  monitored = monitoredFor(name);  // swap to this host's monitored set
  loadServices(name);              // and its own service catalog (async; refreshes the picker)
  applyHostLayout(name);   // each host page shows its own boxes
  // Hydrate trend series from history if we don't have enough live points yet.
  const s = ensureSeries(name);
  if (s.cpu.length < 5) {
    try {
      const r = await fetch(`/api/history?host=${encodeURIComponent(name)}`);
      if (r.ok) {
        const pts = await r.json();
        s.cpu = pts.map((p) => p.cpu);
        s.mem = pts.map((p) => p.memPct);
        s.rx = pts.map((p) => p.netRx);
        s.tx = pts.map((p) => p.netTx);
        for (const k of ["cpu", "mem", "rx", "tx"]) while (s[k].length > SERIES_CAP) s[k].shift();
      }
    } catch { /* ignore */ }
  }
  render();
  // If a per-host vertical page is open, swap it to the newly-selected host.
  if (vOpen === "svc") renderSvcStatus();
  else if (vOpen === "widgets") renderWidgets();
}

function touchHost(name, ts) {
  const prev = state.hosts.get(name);
  state.hosts.set(name, { lastSeenMs: ts, online: true });
  if (!prev) renderTabs();
  if (!state.selected) selectHost(name);
}

/* ---------------- live stream ---------------- */
function connect() {
  const es = new EventSource("/api/stream");
  es.onopen = () => $("connDot").classList.add("on");
  es.onerror = () => $("connDot").classList.remove("on");
  es.onmessage = (e) => {
    let snap; try { snap = JSON.parse(e.data); } catch { return; }
    if (!snap.host) return;
    state.latest.set(snap.host, snap);
    pushSeries(snap.host, snap);
    touchHost(snap.host, snap.tsUnixMs || Date.now());
    if (snap.host === state.selected) render();
  };
}

async function refreshHosts() {
  try {
    const r = await fetch("/api/hosts");
    if (!r.ok) return;
    const hosts = await r.json();
    const known = new Set(hosts.map((h) => h.name));
    hosts.forEach((h) => state.hosts.set(h.name, { lastSeenMs: h.lastSeenMs, online: h.online }));
    // Forget hosts the server no longer reports (e.g. removed here or on another browser).
    [...state.hosts.keys()].forEach((name) => { if (!known.has(name)) forgetHostLocal(name); });
    // Mark stale hosts offline.
    const now = Date.now();
    state.hosts.forEach((h, name) => { if (now - h.lastSeenMs > 10000) h.online = false; });
    // If the selected host vanished, fall back to the first remaining one.
    if (state.selected && !state.hosts.has(state.selected)) state.selected = null;
    renderTabs();
    if (!state.selected && state.hosts.size) {
      selectHost([...state.hosts.keys()].sort((a, b) => a.localeCompare(b))[0]);
    }
  } catch { /* ignore */ }
}

/* Time/date formatting honouring the `dateFmt` setting: "us" = 12-hour clock +
   MM/DD/YYYY, "intl" = 24-hour clock + DD/MM/YYYY. Exposed globally so UI
   templates (e.g. Futura's big clock) can format consistently. */
function fmtClockTime(d, withSeconds) {
  const intl = settings.dateFmt === "intl";
  const opts = { hour: intl ? "2-digit" : "numeric", minute: "2-digit", hour12: !intl };
  if (withSeconds) opts.second = "2-digit";
  return d.toLocaleTimeString([], opts);
}
function fmtClockDate(d) {
  const dd = String(d.getDate()).padStart(2, "0");
  const mm = String(d.getMonth() + 1).padStart(2, "0");
  const yyyy = d.getFullYear();
  return settings.dateFmt === "intl" ? `${dd}/${mm}/${yyyy}` : `${mm}/${dd}/${yyyy}`;
}
function tickClock() { const el = $("clock"); if (el) el.textContent = fmtClockTime(new Date(), true); }
function startClock() {
  tickClock();
  setInterval(tickClock, 1000);
  // Re-render immediately when the format toggle flips (no reload needed).
  document.addEventListener("michka:settingschange", (e) => { if (e.detail && e.detail.key === "dateFmt") tickClock(); });
}

// Big top-left date+time clock — folded in from the former "Futura" template
// (now the default look). Builds its own element as the first item of the
// dashboard top bar (CSS hides the wordmark + the small status clock) and ticks
// once a second, honouring the global "Time & date format" setting. Idempotent so
// it never stacks duplicate elements/timers/listeners on a re-run.
function buildTopClock() {
  if (window.__topClockTimer) { clearInterval(window.__topClockTimer); window.__topClockTimer = null; }
  document.getElementById("futuraClock")?.remove();

  const topbar = document.querySelector(".screen-dash .topbar");
  if (!topbar) return;

  const box = document.createElement("div");
  box.id = "futuraClock";
  box.className = "futura-clock";
  box.innerHTML = '<div class="fc-date"></div><div class="fc-time"></div>';
  topbar.insertBefore(box, topbar.firstChild);

  const dateEl = box.querySelector(".fc-date");
  const timeEl = box.querySelector(".fc-time");
  const tick = () => { const now = new Date(); dateEl.textContent = fmtClockDate(now); timeEl.textContent = fmtClockTime(now, false); };
  tick();
  window.__topClockTimer = setInterval(tick, 1000);

  // Update the moment the format toggle flips (no reload); re-register cleanly.
  if (window.__topClockFmtHandler) document.removeEventListener("michka:settingschange", window.__topClockFmtHandler);
  window.__topClockFmtHandler = (e) => { if (!e.detail || e.detail.key === "dateFmt") tick(); };
  document.addEventListener("michka:settingschange", window.__topClockFmtHandler);
}

/* ---------------- settings UI + host auto-cycle ---------------- */
let cycleTimer = null;
function applyCycle() {
  if (cycleTimer) { clearInterval(cycleTimer); cycleTimer = null; }
  if (settings.cycle !== "on") return;
  const sec = Math.max(3, parseInt(settings.cycleSec, 10) || 10);
  cycleTimer = setInterval(() => {
    const names = [...state.hosts.keys()].sort((a, b) => a.localeCompare(b));
    if (names.length < 2) return;
    const i = names.indexOf(state.selected);
    selectHost(names[(i + 1) % names.length]);
  }, sec * 1000);
}

// Three screens: 0 = services, 1 = dashboard, 2 = settings. The dashboard is centred by default.
function goToScreen(idx) {
  currentScreen = clamp(idx, 0, 2);
  $("slider").style.transform = `translateX(${-currentScreen * 100}vw)`;
  const hash = currentScreen === 0 ? "#services" : currentScreen === 2 ? "#settings" : "#";
  try { history.replaceState(null, "", hash); } catch { /* ignore */ }
  if (currentScreen === 0) loadServices();  // refresh the selected host's catalog on entry
  hideOsk();  // never carry the keyboard across a screen change
  // charts/boxes may have changed size while off-screen — nudge them after the slide.
  setTimeout(() => charts.forEach((c) => c.resize()), 360);
}

function paintSegs() {
  document.querySelectorAll(".seg").forEach((seg) => {
    const key = seg.dataset.key;
    seg.querySelectorAll("button").forEach((b) => b.classList.toggle("on", b.dataset.v === String(settings[key])));
  });
}

function showToast(msg, kind) {
  const t = $("toast");
  t.textContent = msg;
  t.className = "toast" + (kind ? " " + kind : "");
  clearTimeout(t._h);
  t._h = setTimeout(() => t.classList.add("hidden"), 4000);
}

function wireSettings() {
  // Navigation: edge pull tabs / back buttons / swipe between the three screens
  $("pullTab").onclick = () => goToScreen(2);     // right edge  -> settings
  $("pullTabL").onclick = () => goToScreen(0);    // left edge   -> services
  $("pullTabB").onclick = () => openVertical("svc");      // bottom -> service status
  $("pullTabT").onclick = () => openVertical("widgets");  // top    -> widgets
  $("svcStatusBack").onclick = () => closeVertical();
  $("widgetsBack").onclick = () => closeVertical();
  $("backBtn").onclick = () => goToScreen(1);

  const app = $("app");
  let sx = 0, sy = 0, track = false;
  app.addEventListener("touchstart", (e) => {
    const t = e.touches[0]; sx = t.clientX; sy = t.clientY;
    // Don't hijack swipes that start on interactive bits or a popup. The board and the service list
    // are swipeable: the horizontal-vs-vertical check below keeps vertical list scrolling intact.
    track = !e.target.closest(".host-tabs, .seg, input, button, .popup, .osk");
  }, { passive: true });
  app.addEventListener("touchend", (e) => {
    if (!track || boxInteracting) return; // a held box owns the gesture — don't navigate
    const t = e.changedTouches[0], dx = t.clientX - sx, dy = t.clientY - sy;
    const adx = Math.abs(dx), ady = Math.abs(dy);
    if (ady > 70 && ady > adx * 1.4) {
      // Vertical swipe: open/close the service-status (up) and widgets (down) pages.
      if (dy < 0) handleSwipeUp(); else handleSwipeDown();
    } else if (adx > 70 && adx > ady * 1.4 && !vOpen) {
      goToScreen(currentScreen + (dx < 0 ? 1 : -1));
    }
  }, { passive: true });

  // Language dropdown: persist the choice and swap the active language live (no reload).
  const sel = $("langSelect");
  if (sel) {
    sel.innerHTML = "";
    LANGS.forEach((l) => {
      const o = document.createElement("option");
      o.value = l.code; o.textContent = l.name;
      sel.appendChild(o);
    });
    sel.value = settings.lang || "en";
    sel.onchange = () => { settings.lang = sel.value; postConfig({ lang: sel.value }); loadLang(sel.value); };
  }

  // Display toggles (persist to server, apply immediately)
  document.querySelectorAll(".seg").forEach((seg) => {
    const key = seg.dataset.key;
    seg.querySelectorAll("button").forEach((b) => {
      b.onclick = () => {
        settings[key] = b.dataset.v;
        paintSegs();
        postConfig({ [key]: b.dataset.v });
        if (key === "cycle" || key === "cycleSec") applyCycle();
        if (key === "ssMode") applyScreensaver();
        if (key === "screenSize") applyScreenSize();
        render();
        // Let live consumers (clocks, templates) react without a reload.
        document.dispatchEvent(new CustomEvent("michka:settingschange", { detail: { key } }));
      };
    });
  });

  // Server: port — no physical keyboard on the kiosk, so pop our numeric keypad on tap.
  const openPortOsk = () => showOsk("num", $("portInput"), null);
  $("portInput").addEventListener("focus", openPortOsk);
  $("portInput").addEventListener("click", openPortOsk);

  $("portApply").onclick = async () => {
    const p = parseInt($("portInput").value, 10);
    if (!(p >= 1 && p <= 65535)) { showToast(t("toast.portRange"), "err"); return; }
    showToast(t("toast.portApplying", { p }));
    const res = await postConfig({ port: p });
    if (res && res.restarting) showToast(t("toast.portRestarting", { p }));
    else if (res) showToast(t("toast.portUnchanged"));
    else showToast(t("toast.portError"), "err");
  };

  // Server: exit UI to login
  $("exitUiBtn").onclick = async () => {
    if (!confirm(t("confirm.exitUi"))) return;
    try {
      const r = await fetch("/api/system/exit-ui", { method: "POST" });
      if (!r.ok) showToast(t("toast.exitUnavailable"), "err");
    } catch { showToast(t("toast.exitFailed"), "err"); }
  };

  // Screensaver: timeout slider + preview button
  $("ssTimeout").addEventListener("input", () => {
    const idx = parseInt($("ssTimeout").value, 10) || 0;
    const min = SS_TIMEOUTS[idx] ?? 20;
    settings.ssTimeout = String(min);
    postConfig({ ssTimeout: String(min) });
    applyScreensaver();
  });
  $("ssPreview").onclick = () => ssActivate();

  // Any genuine user input resets the inactivity timer (and dismisses an active screensaver).
  ["pointerdown", "touchstart", "mousemove", "keydown", "wheel"].forEach((ev) =>
    document.addEventListener(ev, ssOnActivity, { passive: true }));
  window.addEventListener("resize", () => { if (ssActive && settings.ssMode === "eye") ssEnsureCanvas(); });

  paintSegs();
  ssSyncSlider();
  applyCycle();
  applyScreensaver();
}

async function loadServerConfig() {
  try {
    const r = await fetch("/api/server-config");
    if (!r.ok) return;
    const c = await r.json();
    if (c.display) Object.assign(settings, c.display);
    // Apply the persisted language (fetches /lang/<code>.json and re-translates the UI live).
    const langSel = $("langSelect");
    if (langSel) langSel.value = settings.lang || "en";
    loadLang(settings.lang || "en");
    if (c.port != null) $("portInput").value = c.port;
    $("srvHub").textContent = `${c.hostName} : ${c.port}`;
    $("srvPlatform").textContent = c.platform || "--";

    monitoredByHost = (c.servicesByHost && typeof c.servicesByHost === "object") ? c.servicesByHost : {};
    monitored = monitoredFor(state.selected);
    // Per-host layouts; the legacy global `layout` becomes the seed/default for hosts without one.
    layoutsByHost = (c.layouts && typeof c.layouts === "object") ? c.layouts : {};
    defaultBoxes = (Array.isArray(c.layout) && c.layout.length) ? c.layout : null;
    if (state.selected) applyHostLayout(state.selected);
    renderServiceList();

    applyTemplate(c.template || "");
    loadTemplateGrid();
    applyScreenSize();

    paintSegs();
    ssSyncSlider();
    applyCycle();
    applyScreensaver();
    if (state.selected) render();
  } catch { /* ignore */ }
}

/* ---------------- UI templates (drop-in custom skins) ----------------
   A template is a folder under the on-disk templates/ directory holding style.css / script.js /
   preview.png. Selecting one layers its CSS + JS over the built-in default; empty id = default. */
function applyTemplate(id) {
  const css = $("tplCss");
  if (css) css.href = id ? `/templates/${encodeURIComponent(id)}/style.css` : "";
  // Drop any previously-injected template script before adding the new one.
  document.querySelectorAll("script[data-tpl-script]").forEach((s) => s.remove());
  if (id) {
    const s = document.createElement("script");
    s.src = `/templates/${encodeURIComponent(id)}/script.js`;
    s.setAttribute("data-tpl-script", id);
    document.body.appendChild(s);
  }
  settings.template = id || "";
}

// Screen-size scaling (Settings → Display → Screen size). The UI is fluid and always fills the
// window; this just sets a body attribute that selects a CSS scale profile (`--ui-scale`) so each
// supported panel — notably the short 1424x280 — renders well. Re-fit the board afterwards so the
// gauges (radius is %-based) and charts pick up the new scale.
function applyScreenSize() {
  const size = settings.screenSize || "1280x400";
  document.body.dataset.screen = size;
  requestAnimationFrame(() => {
    try {
      if (typeof reflowStandard === "function" && Array.isArray(layout) && layout.length) {
        reflowStandard(); applyPositions();
      }
      if (charts && charts.forEach) charts.forEach((c) => { try { c.resize(); } catch { /* ignore */ } });
    } catch { /* best-effort refit */ }
  });
}

function markActiveTemplateCard(id) {
  document.querySelectorAll("#tplGrid .tpl-card").forEach((c) =>
    c.classList.toggle("active", (c.dataset.tplId || "") === (id || "")));
}

function selectTemplate(id) {
  applyTemplate(id);
  markActiveTemplateCard(id);
  postConfig({ template: id });
}

async function loadTemplateGrid() {
  const grid = $("tplGrid");
  if (!grid) return;
  // Keep the static Default card (empty id); rebuild the custom cards from the server each time.
  grid.querySelectorAll(".tpl-card").forEach((c) => { if (c.dataset.tplId) c.remove(); });
  const def = grid.querySelector('.tpl-card[data-tpl-id=""]');
  if (def) def.onclick = () => selectTemplate("");

  let templates = [];
  try { const r = await fetch("/api/templates"); if (r.ok) templates = await r.json(); } catch { /* none */ }
  templates.forEach((t) => {
    const card = document.createElement("div");
    card.className = "tpl-card";
    card.dataset.tplId = t.id;
    const preview = t.hasPreview
      ? `<img src="/templates/${encodeURIComponent(t.id)}/preview.png" alt="" />`
      : `<span>${esc(t.name)}</span>`;
    card.innerHTML = `<div class="tpl-card-preview">${preview}</div>
      <div class="tpl-card-body">
        <div class="tpl-card-name">${esc(t.name)}</div>
        <div class="tpl-card-desc">${esc(t.description || "")}</div>
        <div class="tpl-card-meta"><span>${esc(t.author || "")}</span><span>v${esc(t.version || "1.0")}</span></div>
      </div>`;
    card.onclick = () => selectTemplate(t.id);
    grid.appendChild(card);
  });
  markActiveTemplateCard(settings.template || "");
}

/* ---------------- screensaver ---------------- */
const SS_TIMEOUTS = [5, 20, 40, 60]; // minutes, indexed by the slider position
let ssActive = false, ssIdleTimer = null, ssRaf = 0, ssStartT = 0, ssActivatedAt = 0;
// When a widget asks to keep the screen awake (e.g. the NexusM player while a video plays), the
// idle timer keeps re-arming instead of activating the screensaver. Toggled via Michka.keepAwake().
let ssKeepAwake = false;

function ssSyncSlider() {
  const idx = SS_TIMEOUTS.indexOf(parseInt(settings.ssTimeout, 10));
  $("ssTimeout").value = idx >= 0 ? idx : 1; // default to 20m
}

// (Re)arm the idle timer from the current settings; dismiss an active saver if one is showing.
function applyScreensaver() {
  if (ssActive) ssDeactivate();
  if (ssIdleTimer) { clearTimeout(ssIdleTimer); ssIdleTimer = null; }
  if (settings.ssMode === "off") return;
  const min = parseInt(settings.ssTimeout, 10) || 20;
  ssIdleTimer = setTimeout(ssActivate, min * 60 * 1000);
}

function ssOnActivity() {
  // Ignore the very gesture/events that just triggered the saver so it doesn't flicker off.
  if (ssActive && performance.now() - ssActivatedAt < 600) return;
  applyScreensaver();
}

function ssActivate() {
  if (ssActive || settings.ssMode === "off") return;
  // A widget is holding the screen awake — re-arm and skip activation.
  if (ssKeepAwake) { applyScreensaver(); return; }
  ssActive = true;
  ssActivatedAt = performance.now();
  // The screensaver only fires when idle, so the cursor must not be left sitting on it; hide it now
  // (real mouse movement both dismisses the saver and re-shows the cursor via initCursorAutoHide).
  document.body.classList.add("cursor-hidden");
  if (ssIdleTimer) { clearTimeout(ssIdleTimer); ssIdleTimer = null; }
  const el = $("screensaver");
  el.className = "screensaver " + settings.ssMode;
  el.setAttribute("aria-hidden", "false");
  if (settings.ssMode === "eye") ssStartEye();
  else if (settings.ssMode === "michka") ssStartBear();
  else if (settings.ssMode === "sleep") ssScreenPower(false);
}

function ssDeactivate() {
  if (!ssActive) return;
  ssActive = false;
  ssStopEye();
  if (settings.ssMode === "sleep") ssScreenPower(true);
  const el = $("screensaver");
  el.className = "screensaver hidden";
  el.setAttribute("aria-hidden", "true");
}

async function ssScreenPower(on) {
  try {
    await fetch("/api/system/screen", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ on }),
    });
  } catch { /* best-effort; the black overlay covers the screen regardless */ }
}

/* --- Eye of Sauron: procedural fiery eye on a canvas --- */
let ssCtx = null, ssFlames = null, ssFilaments = null;

function ssEnsureCanvas() {
  const c = $("ssCanvas");
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  c.width = Math.floor(window.innerWidth * dpr);
  c.height = Math.floor(window.innerHeight * dpr);
  ssCtx = c.getContext("2d");
  ssCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
}

function ssBuildEye() {
  ssFlames = [];
  for (let i = 0; i < 130; i++) {
    const a = (i / 130) * Math.PI * 2;
    ssFlames.push({ a, len: 0.4 + Math.random() * 0.7, ph: Math.random() * Math.PI * 2, sp: 1.2 + Math.random() * 2.6 });
  }
  ssFilaments = [];
  for (let i = 0; i < 240; i++) {
    ssFilaments.push({
      a: Math.random() * Math.PI * 2,
      r0: 0.12 + Math.random() * 0.12,
      r1: 0.55 + Math.random() * 0.46,
      w: 0.5 + Math.random() * 1.6,
      ph: Math.random() * Math.PI * 2,
      sp: 1.5 + Math.random() * 4,
      hue: 20 + Math.random() * 28, // deep orange -> yellow
    });
  }
}

function ssStartEye() {
  ssEnsureCanvas();
  if (!ssFlames) ssBuildEye();
  ssStartT = performance.now();
  const loop = (now) => {
    if (!ssActive) return;
    ssDrawEye((now - ssStartT) / 1000);
    ssRaf = requestAnimationFrame(loop);
  };
  ssRaf = requestAnimationFrame(loop);
}

function ssStopEye() {
  if (ssRaf) cancelAnimationFrame(ssRaf);
  ssRaf = 0;
}

/* --- Michka the bear: bounces around the black overlay DVD-logo style (anti burn-in).
   Constant speed, reflects off all four edges (up/down/left/right), never rotates. --- */
function ssStartBear() {
  const bear = $("ssBear");
  let last = performance.now();
  let x = 0, y = 0, vx = 0, vy = 0, inited = false;
  const loop = (now) => {
    if (!ssActive) return;
    const W = window.innerWidth, H = window.innerHeight;
    const bw = bear.offsetWidth || W * 0.45, bh = bear.offsetHeight || H * 0.45;
    const maxX = Math.max(0, W - bw), maxY = Math.max(0, H - bh);
    if (!inited && bw && bh) {
      x = maxX / 2; y = maxY / 2;
      // Speed: a full horizontal traverse takes SS_SWEEP_S seconds (same cadence as before).
      const speed = Math.max(60, maxX) / SS_SWEEP_S; // px/s, constant
      const ang = (0.25 + Math.random() * 0.5) * (Math.PI / 2); // ~22deg..67deg → real up/down + left/right
      vx = speed * Math.cos(ang) * (Math.random() < 0.5 ? 1 : -1);
      vy = speed * Math.sin(ang) * (Math.random() < 0.5 ? 1 : -1);
      inited = true;
    }
    const dt = Math.min(0.05, (now - last) / 1000); last = now;
    x += vx * dt; y += vy * dt;
    if (x <= 0)        { x = 0;    vx = Math.abs(vx); }
    else if (x >= maxX) { x = maxX; vx = -Math.abs(vx); }
    if (y <= 0)        { y = 0;    vy = Math.abs(vy); }
    else if (y >= maxY) { y = maxY; vy = -Math.abs(vy); }
    bear.style.transform = `translate(${x}px, ${y}px)`;
    ssRaf = requestAnimationFrame(loop);
  };
  ssRaf = requestAnimationFrame(loop);
}

// Pointed "vesica" slit (cat-eye pupil), centred at origin.
function ssPupilPath(ctx, w, h) {
  ctx.beginPath();
  ctx.moveTo(0, -h);
  ctx.quadraticCurveTo(w, 0, 0, h);
  ctx.quadraticCurveTo(-w, 0, 0, -h);
  ctx.closePath();
}

// Full back-and-forth period: one crossing (edge to edge) takes SS_SWEEP_S seconds.
const SS_SWEEP_S = 10;
function ssDrawEye(t) {
  const ctx = ssCtx, W = window.innerWidth, H = window.innerHeight;
  const cy = H / 2, R = Math.min(W, H) * 0.45;
  // Drift the eye slowly left<->right so it never sits in one place (anti burn-in).
  const minX = R + 40, maxX = W - R - 40;
  const midX = (minX + maxX) / 2, ampX = Math.max(0, (maxX - minX) / 2);
  const cx = midX + ampX * Math.sin((Math.PI / SS_SWEEP_S) * t); // half-period = SS_SWEEP_S = one crossing

  // base + ambient fiery glow
  ctx.globalCompositeOperation = "source-over";
  ctx.fillStyle = "#000";
  ctx.fillRect(0, 0, W, H);
  let g = ctx.createRadialGradient(cx, cy, R * 0.2, cx, cy, R * 2.4);
  g.addColorStop(0, "rgba(140,28,6,0.55)");
  g.addColorStop(0.5, "rgba(70,10,3,0.35)");
  g.addColorStop(1, "rgba(0,0,0,0)");
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, W, H);

  // outer flames licking outward (additive)
  ctx.globalCompositeOperation = "lighter";
  ctx.lineCap = "round";
  for (const s of ssFlames) {
    const flick = 0.55 + 0.45 * Math.sin(t * s.sp + s.ph) + 0.18 * Math.sin(t * 7.7 + s.ph * 2);
    const r0 = R * 0.84;
    const r1 = R * (0.92 + s.len * 0.75 * Math.max(0.2, flick));
    const x0 = cx + Math.cos(s.a) * r0, y0 = cy + Math.sin(s.a) * r0;
    const x1 = cx + Math.cos(s.a) * r1, y1 = cy + Math.sin(s.a) * r1;
    const lg = ctx.createLinearGradient(x0, y0, x1, y1);
    lg.addColorStop(0, "rgba(255,190,60,0.5)");
    lg.addColorStop(0.5, "rgba(255,95,20,0.34)");
    lg.addColorStop(1, "rgba(120,12,0,0)");
    ctx.strokeStyle = lg;
    ctx.lineWidth = Math.max(1, R * 0.02 * (0.6 + flick * 0.7));
    ctx.beginPath();
    ctx.moveTo(x0, y0);
    ctx.lineTo(x1, y1);
    ctx.stroke();
  }

  // iris disk
  ctx.globalCompositeOperation = "source-over";
  g = ctx.createRadialGradient(cx, cy, R * 0.05, cx, cy, R);
  g.addColorStop(0, "#fff3c0");
  g.addColorStop(0.16, "#ffd24a");
  g.addColorStop(0.42, "#ff9f43");
  g.addColorStop(0.7, "#ff5470");
  g.addColorStop(0.9, "#b3160a");
  g.addColorStop(1, "#3a0702");
  ctx.save();
  ctx.beginPath();
  ctx.arc(cx, cy, R, 0, Math.PI * 2);
  ctx.clip();
  ctx.fillStyle = g;
  ctx.fillRect(cx - R, cy - R, R * 2, R * 2);

  // radial iris filaments / streaks (additive, clipped to the disk)
  ctx.globalCompositeOperation = "lighter";
  for (const f of ssFilaments) {
    const flick = 0.45 + 0.55 * Math.abs(Math.sin(t * f.sp * 0.5 + f.ph));
    const a = f.a + 0.025 * Math.sin(t * 0.3 + f.ph); // gentle shimmer
    const x0 = cx + Math.cos(a) * R * f.r0, y0 = cy + Math.sin(a) * R * f.r0;
    const x1 = cx + Math.cos(a) * R * f.r1, y1 = cy + Math.sin(a) * R * f.r1;
    ctx.strokeStyle = `hsla(${f.hue}, 100%, ${52 + flick * 22}%, ${0.1 + flick * 0.26})`;
    ctx.lineWidth = f.w;
    ctx.beginPath();
    ctx.moveTo(x0, y0);
    ctx.lineTo(x1, y1);
    ctx.stroke();
  }
  ctx.restore();

  // pupil — vertical cat-eye slit, breathing slightly
  ctx.globalCompositeOperation = "source-over";
  const pulse = 1 + 0.07 * Math.sin(t * 2.1);
  const pw = R * 0.12 * pulse, pH = R * 0.92;
  ctx.save();
  ctx.translate(cx, cy);
  ctx.shadowColor = "rgba(0,0,0,0.85)";
  ctx.shadowBlur = R * 0.1;
  ctx.fillStyle = "#050100";
  ssPupilPath(ctx, pw, pH);
  ctx.fill();
  ctx.restore();

  // bright glint near the top of the slit
  ctx.globalCompositeOperation = "lighter";
  const gx = cx, gy = cy - pH * 0.42;
  const gg = ctx.createRadialGradient(gx, gy, 0, gx, gy, R * 0.1);
  gg.addColorStop(0, "rgba(255,255,255,0.95)");
  gg.addColorStop(1, "rgba(255,255,255,0)");
  ctx.fillStyle = gg;
  ctx.beginPath();
  ctx.arc(gx, gy, R * 0.1, 0, Math.PI * 2);
  ctx.fill();
  ctx.globalCompositeOperation = "source-over";
}

/* ---------------- cursor auto-hide ----------------
   The kiosk has no mouse, so hide the pointer after a few idle seconds; any genuine mouse use
   (move / click / wheel) brings it back and restarts the timer. This must also work for desktop
   browsers driving the hub remotely — hence the listeners run in the CAPTURE phase on document, so
   full-screen overlays that stopPropagation on their own pointer events (e.g. the NexusM player,
   widget.js) can't swallow the reappearance and leave the cursor stuck hidden over them. */
const CURSOR_HIDE_MS = 3000;
function initCursorAutoHide() {
  let timer = null;
  let lastX = null, lastY = null;
  const hide = () => document.body.classList.add("cursor-hidden");
  const show = () => {
    document.body.classList.remove("cursor-hidden");
    clearTimeout(timer);
    timer = setTimeout(hide, CURSOR_HIDE_MS);
  };
  document.addEventListener("pointermove", (e) => {
    if (e.pointerType && e.pointerType !== "mouse") return; // ignore touch/pen-synthesised moves
    // The board re-renders live every second; when the DOM changes under a stationary cursor the
    // browser fires a "mouse" pointermove with unchanged coordinates. Treat only real movement
    // (coordinates actually changed) as activity, else the cursor would flicker back on its own.
    if (e.clientX === lastX && e.clientY === lastY) return;
    lastX = e.clientX; lastY = e.clientY;
    show();
  }, { capture: true, passive: true });
  // A click or scroll is "using the mouse" too — reveal the cursor so the user can aim/click.
  ["mousedown", "wheel"].forEach((ev) =>
    document.addEventListener(ev, show, { capture: true, passive: true }));
  timer = setTimeout(hide, CURSOR_HIDE_MS);
}

/* ---------------- vertical pages: Service status (swipe up) + Widgets (swipe down) ----------------
   Two full-screen overlays that slide in over the dashboard. The Service-status page shows live
   status bubbles for the *currently selected host*'s monitored services; the Widgets page is a
   per-host scaffold for small app-connector widgets (e.g. a NexusM music player), wired later.
   Opened by swiping up/down on the dashboard (or the top/bottom pull-tabs); dismissed by swiping
   the other way (or the back button). Only the dashboard can open them. */
function openVertical(which) {
  if (vOpen === which) return;
  closeVertical();
  vOpen = which;
  const el = which === "svc" ? $("vpageSvc") : $("vpageWidgets");
  el.classList.add("open");
  el.setAttribute("aria-hidden", "false");
  if (which === "svc") renderSvcStatus(); else renderWidgets();
}
function closeVertical() {
  if (!vOpen) return;
  if (vOpen === "widgets") unmountAllWidgets();   // stop widget timers/connections when leaving
  ["vpageSvc", "vpageWidgets"].forEach((id) => {
    const el = $(id);
    el.classList.remove("open");
    el.setAttribute("aria-hidden", "true");
  });
  vOpen = null;
}
// Swipe up: open the service page (from the dashboard) or close the widgets page.
function handleSwipeUp() {
  if (vOpen === "widgets") closeVertical();
  else if (!vOpen && currentScreen === 1) openVertical("svc");
}
// Swipe down: open the widgets page (from the dashboard) or close the service page.
function handleSwipeDown() {
  if (vOpen === "svc") closeVertical();
  else if (!vOpen && currentScreen === 1) openVertical("widgets");
}

/* ---- Service status page: live status bubbles for the selected host's monitored services ---- */
function renderSvcStatus() {
  const host = state.selected;
  const hEl = $("svcStatusHost"); if (hEl) hEl.textContent = host || "--";
  // Host pill border: green when the host is reachable, grey otherwise.
  const pill = $("svcStatusHostPill");
  if (pill) pill.classList.toggle("online", !!(host && state.hosts.get(host) && state.hosts.get(host).online));
  const grid = $("svcStatusGrid"); if (!grid) return;
  const snap = host ? state.latest.get(host) : null;
  const svcs = (snap && Array.isArray(snap.services)) ? snap.services : [];
  if (!svcs.length) {
    grid.innerHTML = `<div class="svc-empty">${t("svcStatus.empty", { host: esc(host || "—") })}</div>`;
    return;
  }
  // Rebuild the bubble shells only when the set changes; otherwise update fields in place (no flicker).
  if (grid.childElementCount !== svcs.length || grid.querySelector(".svc-empty")) {
    grid.innerHTML = "";
    svcs.forEach(() => {
      const c = document.createElement("div");
      c.className = "svc-bubble";
      c.innerHTML = `<div class="svc-bubble-head"><span class="svc-dot" data-d></span><span class="svc-bubble-name" data-n></span></div>
        <div class="svc-state" data-s></div><div class="svc-meta" data-m></div>`;
      grid.appendChild(c);
    });
  }
  svcs.forEach((st, i) => {
    const c = grid.children[i]; if (!c) return;
    const up = !!st.active, unknown = st.activeState === "unknown";
    c.querySelector("[data-d]").className = "svc-dot " + (up ? "up" : unknown ? "" : "down");
    c.querySelector("[data-n]").textContent = svcShort(st.name);
    const sEl = c.querySelector("[data-s]");
    sEl.className = "svc-state " + (up ? "up" : unknown ? "unknown" : "down");
    sEl.textContent = `${st.activeState || "?"}${st.subState ? " · " + st.subState : ""}`;
    const parts = [];
    if (st.mainPid) parts.push("pid " + st.mainPid);
    if (typeof st.memoryBytes === "number" && st.memoryBytes >= 0) parts.push(fmtBytes(st.memoryBytes));
    c.querySelector("[data-m]").textContent = parts.join(" · ");
  });
}

/* ---- Widgets page: per-host app-connector widgets (on-disk drop-ins) ----
   Widget *types* are discovered from the hub (GET /api/widgets) — each is a folder under the
   on-disk widgets/ dir shipping a widget.js that registers a renderer + lifecycle via the public
   Michka.widget() API. Their scripts/styles are lazy-loaded once on first use. Placed widgets
   persist per host in localStorage (no server round-trip yet); see WidgetStore on the hub. */

// Public widget API exposed to widget.js modules. A widget registers a renderer (required) plus
// optional mount/unmount lifecycle hooks; everything else (discovery, persistence, the card chrome)
// is handled here. Kept on window so injected widget scripts can reach it.
window.Michka = window.Michka || {};
const widgetRegistry = {};            // type -> { render, mount?, unmount? }
window.Michka.widget = function (type, def) {
  if (typeof type === "string" && def && typeof def.render === "function") widgetRegistry[type] = def;
};

// Kiosk helpers a rich widget can use (the kiosk has no physical keyboard and an idle screensaver):
//  - osk(mode, inputEl, onInput): pop the shared on-screen keyboard ("text" / "num") onto an <input>;
//    set inputmode="none" on the field so no native keyboard competes. Pass no args / hide() to dismiss.
//  - keepAwake(on): while true, the screensaver won't activate (re-arms instead) — call (true) while
//    media plays full-screen and (false) when done so a video isn't covered by the saver.
window.Michka.osk = function (mode, inputEl, onInput) { showOsk(mode, inputEl, onInput); };
window.Michka.hideOsk = function () { hideOsk(); };
window.Michka.keepAwake = function (on) {
  ssKeepAwake = !!on;
  if (ssKeepAwake) applyScreensaver();   // re-arm so the pending timer can't fire while held
};

let widgetDefs = {};                  // type -> server metadata { id, name, description, tag, hasCss, hasIcon }
let widgetCatalogLoaded = false;
const widgetAssetLoads = {};          // type -> Promise<bool> (load script/css once)
const mountedWidgets = new Map();     // instance id -> { def, ctx } currently mounted on the page

const WIDGETS_LS_KEY = "michka.widgetsByHost";
let widgetsByHost = {};
function loadWidgets() {
  try { widgetsByHost = JSON.parse(localStorage.getItem(WIDGETS_LS_KEY)) || {}; }
  catch { widgetsByHost = {}; }
}
function saveWidgets() {
  try { localStorage.setItem(WIDGETS_LS_KEY, JSON.stringify(widgetsByHost)); } catch { /* ignore */ }
}
function widgetsForHost(host) {
  if (!host) return [];
  if (!Array.isArray(widgetsByHost[host])) widgetsByHost[host] = [];
  return widgetsByHost[host];
}

// Fetch the available widget types from the hub (once; pass force to refresh).
async function ensureWidgetCatalog(force) {
  if (widgetCatalogLoaded && !force) return;
  try {
    const r = await fetch("/api/widgets");
    if (!r.ok) return;                 // failed fetch — don't latch, retry on the next call
    const list = await r.json();
    const defs = {};
    list.forEach((w) => { defs[w.id] = w; });
    widgetDefs = defs;
    widgetCatalogLoaded = true;        // only mark loaded once we actually have the catalog
  } catch { /* hub unreachable — leave whatever we had and retry next time */ }
}

// Lazy-load a widget type's CSS + JS exactly once. Resolves true if the module registered itself.
function ensureWidgetAssets(type) {
  if (widgetRegistry[type]) return Promise.resolve(true);
  if (widgetAssetLoads[type]) return widgetAssetLoads[type];
  const def = widgetDefs[type];
  if (!def) return Promise.resolve(false);
  if (def.hasCss && !document.querySelector(`link[data-widget-css="${type}"]`)) {
    const l = document.createElement("link");
    l.rel = "stylesheet";
    l.href = `/widgets/${encodeURIComponent(type)}/widget.css`;
    l.setAttribute("data-widget-css", type);
    document.head.appendChild(l);
  }
  widgetAssetLoads[type] = new Promise((resolve) => {
    const s = document.createElement("script");
    s.src = `/widgets/${encodeURIComponent(type)}/widget.js`;
    s.setAttribute("data-widget-script", type);
    s.onload = () => resolve(!!widgetRegistry[type]);
    s.onerror = () => resolve(false);
    document.body.appendChild(s);
  });
  return widgetAssetLoads[type];
}

// The context object handed to a widget's render/mount/unmount hooks.
function widgetCtx(w, el) {
  return {
    host: state.selected,
    widget: { id: w.id, type: w.type },
    el: el || null,
    meta: widgetDefs[w.type] || {},
    t, esc, fmtBytes,
    api: (path) => fetch(path).then((r) => (r.ok ? r.json() : Promise.reject(r.status))),
  };
}

// Stop + forget every widget currently mounted (host switch, page close, re-render).
function unmountAllWidgets() {
  mountedWidgets.forEach((m) => {
    try { if (typeof m.def.unmount === "function") m.def.unmount(m.ctx); } catch { /* widget cleanup is best-effort */ }
  });
  mountedWidgets.clear();
}

async function renderWidgets() {
  const host = state.selected;
  const hEl = $("widgetsHost"); if (hEl) hEl.textContent = host || "--";
  // Host pill border: green when the host is reachable, grey otherwise.
  const pill = $("widgetsHostPill");
  if (pill) pill.classList.toggle("online", !!(host && state.hosts.get(host) && state.hosts.get(host).online));
  const grid = $("widgetsGrid"); if (!grid) return;

  unmountAllWidgets();
  await ensureWidgetCatalog();
  grid.innerHTML = "";
  const items = host ? widgetsForHost(host) : [];

  // Empty state: a centred prompt with a single call-to-action.
  if (!items.length) {
    grid.classList.add("is-empty");
    const empty = document.createElement("div");
    empty.className = "widgets-empty";
    empty.innerHTML = `
      <div class="widgets-empty-icon"><svg viewBox="0 0 24 24" width="36" height="36" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><line x1="17.5" y1="14" x2="17.5" y2="21"/><line x1="14" y1="17.5" x2="21" y2="17.5"/></svg></div>
      <div class="widgets-empty-title">${esc(t("widget.empty.title"))}</div>
      <div class="widgets-empty-hint">${esc(t("widget.empty.hint"))}</div>
      <button class="widgets-empty-add" type="button">${esc(t("widget.add"))}</button>`;
    empty.querySelector(".widgets-empty-add").onclick = openWidgetChooser;
    grid.appendChild(empty);
    return;
  }

  // Populated: one card shell per widget, then a dashed "add" tile at the end. Bodies fill in
  // asynchronously once each widget module has loaded (mountWidget).
  grid.classList.remove("is-empty");
  items.forEach((w) => grid.appendChild(makeWidgetCard(w)));
  const add = document.createElement("button");
  add.className = "widget-add";
  add.type = "button";
  add.innerHTML = `<svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg><span>${esc(t("widget.add"))}</span>`;
  add.onclick = openWidgetChooser;
  grid.appendChild(add);

  items.forEach((w) => mountWidget(w));
}

// Build the card chrome (header + an empty body that the widget module renders into).
function makeWidgetCard(w) {
  const def = widgetDefs[w.type] || {};
  const name = def.name || w.type;
  const tag = def.tag || "";
  const card = document.createElement("div");
  card.className = "widget-card widget-" + w.type;
  card.dataset.widgetId = w.id;
  card.innerHTML = `
    <div class="widget-head">
      <span class="widget-name">${esc(name)}</span>
      ${tag ? `<span class="widget-tag">${esc(tag)}</span>` : ""}
      <button class="widget-x" type="button" aria-label="remove widget" title="${esc(t("widget.remove"))}">
        <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>
      </button>
    </div>
    <div class="widget-body" data-widget-body><div class="widget-soon">${esc(t("widget.loading"))}</div></div>`;
  card.querySelector(".widget-x").onclick = () => removeWidget(w.id);
  return card;
}

// Load the widget's module (if needed), render its body, and run its mount hook.
async function mountWidget(w) {
  const card = $("widgetsGrid")?.querySelector(`.widget-card[data-widget-id="${w.id}"]`);
  const body = card?.querySelector("[data-widget-body]");
  if (!body) return;
  const ok = await ensureWidgetAssets(w.type);
  if (!body.isConnected) return;     // host/page changed while the module was loading
  const def = ok ? widgetRegistry[w.type] : null;
  if (!def) { body.innerHTML = `<div class="widget-soon">${esc(t("widget.unavailable"))}</div>`; return; }
  const ctx = widgetCtx(w, body);
  try {
    const out = def.render(ctx);
    if (out instanceof Node) { body.innerHTML = ""; body.appendChild(out); }
    else body.innerHTML = out || "";
    if (typeof def.mount === "function") def.mount(ctx);
    mountedWidgets.set(w.id, { def, ctx });
  } catch (e) {
    body.innerHTML = `<div class="widget-soon">${esc(t("widget.unavailable"))}</div>`;
  }
}

function addWidget(type) {
  if (!state.selected) return;
  const list = widgetsForHost(state.selected);
  if (list.some((w) => w.type === type)) return;   // one of each type per host — no duplicates
  list.push({ id: uid(), type });
  saveWidgets();
  renderWidgets();
}
function removeWidget(id) {
  const host = state.selected; if (!host) return;
  const m = mountedWidgets.get(id);
  if (m) { try { if (typeof m.def.unmount === "function") m.def.unmount(m.ctx); } catch { /* ignore */ } mountedWidgets.delete(id); }
  widgetsByHost[host] = widgetsForHost(host).filter((w) => w.id !== id);
  saveWidgets();
  renderWidgets();
}
async function openWidgetChooser() {
  await ensureWidgetCatalog();
  const list = $("widgetChooserList");
  list.innerHTML = "";
  // Hide widget types already placed on this host so the same one can't be added twice.
  const placed = new Set(widgetsForHost(state.selected).map((w) => w.type));
  const types = Object.keys(widgetDefs).filter((id) => !placed.has(id));
  if (!types.length) {
    list.innerHTML = `<div class="ch-empty">${esc(t("widget.none"))}</div>`;
  } else {
    const phIcon = `<span class="ch-iconph"><svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/></svg></span>`;
    const plus = `<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>`;
    types.forEach((type) => {
      const w = widgetDefs[type];
      const b = document.createElement("button");
      b.className = "ch-item";
      const icon = w.hasIcon ? `<img class="ch-icon" src="/widgets/${encodeURIComponent(type)}/icon.svg" alt="" aria-hidden="true">` : phIcon;
      b.innerHTML = `${icon}<span class="ch-text"><span class="ch-name">${esc(w.name)}</span>${
        w.description ? `<small>${esc(w.description)}</small>` : ""
      }${w.tag ? `<span class="ch-tag">${esc(w.tag)}</span>` : ""}</span><span class="ch-add">${plus}${esc(t("widget.add"))}</span>`;
      b.onclick = () => { addWidget(type); closePopups(); };
      list.appendChild(b);
    });
  }
  const c = $("widgetChooser");
  c.querySelector(".store-close").onclick = closePopups;
  $("popupScrim").classList.remove("hidden");
  c.classList.remove("hidden");
}

/* ---------------- boot ---------------- */
window.addEventListener("DOMContentLoaded", async () => {
  await loadLang(settings.lang);  // English by default; applies translations + the initial board render
  startClock();
  buildTopClock();   // big top-left clock (default look, folded in from Futura)
  initCursorAutoHide();
  loadWidgets();
  ensureWidgetCatalog();   // discover on-disk widget types in the background
  wireSettings();
  wireBoard();
  wireHostMenu();
  wireServices();
  loadServerConfig();
  loadServices();
  if (location.hash === "#settings") goToScreen(2);
  else if (location.hash === "#services") goToScreen(0);
  else goToScreen(1);
  window.addEventListener("resize", () => charts.forEach((c) => c.resize()));
  refreshHosts();
  connect();
  setInterval(refreshHosts, 5000);
});

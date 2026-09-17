/* Alerting — rule-based alerts for your hosts.
 *
 * A small alert ENGINE runs in the browser (reading app.js's live `state`, which holds the latest
 * snapshot + online flag for EVERY reporting host, not just the selected one), evaluates user-defined
 * rules (CPU / memory / disk % thresholds, or a host going offline), and dispatches alerts to one or
 * more delivery channels.
 *
 * Channels (selectable in the settings gear):
 *   - popup    : an on-screen toast on the panel               — WIRED / working now.
 *   - ntfy     : push via an ntfy.sh topic                     — UI + payload stub only (see dispatchRemote).
 *   - telegram : a Telegram bot message                        — UI + payload stub only.
 *   - webhook  : a Discord / Slack incoming webhook            — UI + payload stub only.
 *   - generic  : a plain JSON POST to any URL                  — UI + payload stub only.
 * The four remote channels are designed and their payloads are built, but the actual network send is a
 * stub for now (browsers can't POST cross-origin to most of these; a hub-side proxy comes later, the
 * same pattern as the Pi-hole / Uptime widgets). Only the local pop-up actually fires today.
 *
 * Config is server-persisted via ctx.getConfig()/setConfig() (michka.conf widgetConfig.alerting), so the
 * same rules/channels apply in every browser. Strings use ctx.t("alerting.*") with English fallbacks.
 *
 * NOTE (v0.1 scope): the engine is tied to the widget being mounted at least once this page-session —
 * once started it keeps running across host switches (so alerts stay global), but a full page reload
 * needs the Widgets page (or a board widget box) to mount it again. A future version can hoist the
 * engine into app.js or run it hub-side so it's always on.
 */
(function () {
  "use strict";

  var EVAL_MS = 3000;                 // how often the engine re-checks every host against every rule
  var ctxRef = null;                  // last ctx (esc / t / config)
  var instances = [];                 // mounted cards to refresh
  var overlay = null;                 // settings takeover, or null
  var engineTimer = null;             // the evaluation loop (started once, kept running)
  var track = {};                     // "ruleId|host" -> { pendingSince, active } debounce state
  var recent = [];                    // recent fired alerts (newest first), for the card + popup history
  var activeCount = 0;                // number of currently-active (unrecovered) alerts

  // Config shape (server-persisted). Kept small + forward-compatible.
  var cfg = defaults();
  function defaults() {
    return {
      rules: [],                      // [{ id, metric, host, op, threshold, forSec, severity, enabled }]
      channels: {
        popup:    { enabled: true,  autoDismissSec: 12 },
        ntfy:     { enabled: false, server: "https://ntfy.sh", topic: "", priority: "default" },
        telegram: { enabled: false, token: "", chatId: "" },
        webhook:  { enabled: false, style: "discord", url: "" },   // Discord / Slack incoming webhook
        generic:  { enabled: false, url: "", method: "POST" },
      },
    };
  }

  /* ============================ metric registry (extensible) ============================ */
  // Each metric reads a numeric % from a host snapshot; `offline` is special (uses the host online flag).
  var METRICS = {
    cpu:     { unit: "%", get: function (snap) { return snap && snap.cpu ? (snap.cpu.totalPct || 0) : null; } },
    mem:     { unit: "%", get: function (snap) { return snap && snap.mem ? (snap.mem.pct || 0) : null; } },
    disk:    { unit: "%", get: function (snap) { return maxDiskPct(snap); } },
    offline: { unit: "",  boolean: true },
  };
  var METRIC_ORDER = ["cpu", "mem", "disk", "offline"];
  function maxDiskPct(snap) {
    if (!snap || !Array.isArray(snap.disks) || !snap.disks.length) return null;
    var m = 0; snap.disks.forEach(function (d) { if ((d.pct || 0) > m) m = d.pct || 0; });
    return m;
  }
  function metricLabel(id) {
    return tr("alerting.metric." + id, ({ cpu: "CPU load", mem: "Memory", disk: "Disk usage", offline: "Host offline" })[id] || id);
  }

  /* ============================ config load / save ============================ */
  function loadCfg() {
    try {
      var o = (ctxRef && ctxRef.getConfig) ? ctxRef.getConfig() : {};
      var d = defaults();
      if (o && Array.isArray(o.rules)) d.rules = o.rules.map(normalizeRule);
      if (o && o.channels) {
        Object.keys(d.channels).forEach(function (k) {
          if (o.channels[k]) d.channels[k] = Object.assign(d.channels[k], o.channels[k]);
        });
      }
      cfg = d;
    } catch (e) { cfg = defaults(); }
    return cfg;
  }
  function saveCfg() { try { if (ctxRef && ctxRef.setConfig) ctxRef.setConfig(cfg); } catch (e) { /* ignore */ } }
  function normalizeRule(r) {
    // Targeting: `hosts` is a list of client names to watch; an EMPTY list means "all hosts".
    // (Migrates the old single-host field.)
    var hosts = Array.isArray(r.hosts) ? r.hosts.filter(Boolean) : (r.host ? [r.host] : []);
    return {
      id: r.id || uid(),
      metric: METRICS[r.metric] ? r.metric : "cpu",
      hosts: hosts,
      op: r.op === "<" ? "<" : ">",
      threshold: (typeof r.threshold === "number") ? r.threshold : 90,
      forSec: (typeof r.forSec === "number") ? r.forSec : 60,
      severity: r.severity === "critical" ? "critical" : "warning",
      enabled: r.enabled !== false,
    };
  }

  /* ============================ helpers ============================ */
  function esc(s) { return ctxRef && ctxRef.esc ? ctxRef.esc(s) : String(s == null ? "" : s); }
  function tr(k, d) { var v = ctxRef && ctxRef.t ? ctxRef.t(k) : k; return (v && v !== k) ? v : (d || k); }
  function el(tag, cls, html) { var e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }
  function uid() { return "a" + Math.random().toString(36).slice(2, 9); }
  // app.js's live state (all hosts). Read by bare name with a guard so the widget also runs in a test harness.
  function liveState() { try { return (typeof state !== "undefined" && state) ? state : (window.state || null); } catch (e) { return window.state || null; } }
  function knownHosts(st) { try { return st && st.hosts ? Array.from(st.hosts.keys()) : []; } catch (e) { return []; } }
  function fmtVal(metric, val) {
    if (val == null || isNaN(val)) return "--";
    var m = METRICS[metric] || {};
    return Math.round(val) + (m.unit || "");
  }

  var GEAR = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>';
  var CLOSE = '<svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>';
  var BELL = '<svg viewBox="0 0 24 24" width="34" height="34" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/></svg>';
  var TRASH = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6M14 11v6"/></svg>';

  /* ============================ the engine ============================ */
  function startEngine() {
    if (engineTimer) return;           // already running — keep it global across host switches
    evaluate();
    engineTimer = setInterval(evaluate, EVAL_MS);
  }

  /* Ping cache for agentless "offline" targets — a host you typed that isn't running a michka agent.
   * The browser can't ICMP-ping, so the hub does it (GET /api/widget/uptime?url=host → {ok}, the same
   * probe the Uptime widget uses: bare host = ICMP ping w/ TCP:80 fallback, host:port = TCP, http(s):// =
   * HTTP GET). We refresh each such host every PING_TTL and read the cached up/down in ruleBreached. */
  var pingCache = {};                 // host -> { up: bool|null, ts, inflight, started }
  var PING_TTL = 15000;               // re-probe cadence per host
  function ensurePing(host) {
    var now = Date.now();
    var pc = pingCache[host] || (pingCache[host] = { up: null, ts: 0, inflight: false, started: 0 });
    if (pc.inflight && (now - pc.started) < 20000) return;   // a probe is in flight
    if (!pc.inflight && (now - pc.ts) < PING_TTL) return;    // still fresh
    var api = ctxRef && ctxRef.api;
    if (!api) return;
    pc.inflight = true; pc.started = now;
    api("/api/widget/uptime?url=" + encodeURIComponent(host)).then(function (r) {
      pc.up = !!(r && r.ok); pc.ts = Date.now(); pc.inflight = false;
    }, function () { pc.up = false; pc.ts = Date.now(); pc.inflight = false; });
  }

  function ruleBreached(rule, host, st) {
    if (rule.metric === "offline") {
      var h = st.hosts.get(host);
      // A reporting michka host: use its live online flag (the agent going quiet = offline).
      if (h) return h.online === false;
      // A host with NO michka agent (a name/IP you typed in): actually PING-test it via the hub, so
      // "is 192.168.0.x / my-nas up?" works without installing an agent. Unknown until the first probe
      // returns (so we don't false-alarm on load); once a probe says unreachable it breaches.
      ensurePing(host);
      var pc = pingCache[host];
      return !!(pc && pc.up === false);
    }
    var snap = st.latest.get(host);
    if (!snap) return false;           // no data yet — treat as not breaching
    var m = METRICS[rule.metric]; if (!m || !m.get) return false;
    var val = m.get(snap);
    if (val == null) return false;
    return rule.op === "<" ? (val < rule.threshold) : (val > rule.threshold);
  }

  function currentValue(rule, host, st) {
    if (rule.metric === "offline") return null;
    var snap = st.latest.get(host); if (!snap) return null;
    var m = METRICS[rule.metric]; return m && m.get ? m.get(snap) : null;
  }

  function evaluate() {
    var st = liveState(); if (!st || !st.hosts) return;
    var hosts = knownHosts(st);
    var live = {};                     // keys we touched this pass (to prune stale tracking)

    cfg.rules.forEach(function (rule) {
      if (!rule.enabled) return;
      // Empty target list = all currently-known hosts; otherwise the explicit client list.
      var targets = rule.hosts.length ? rule.hosts : hosts;
      targets.forEach(function (host) {
        // Metric rules need a snapshot, so skip clients that aren't reporting; offline rules can
        // fire for a named-but-absent client (see ruleBreached), so they aren't skipped.
        if (!st.hosts.has(host) && rule.metric !== "offline") return;
        var key = rule.id + "|" + host;
        live[key] = true;
        var t = track[key] || (track[key] = { pendingSince: 0, active: false });
        var breach = ruleBreached(rule, host, st);
        var now = Date.now();

        if (breach) {
          if (!t.pendingSince) t.pendingSince = now;
          if (!t.active && (now - t.pendingSince) >= rule.forSec * 1000) {
            t.active = true;
            fire(rule, host, "alert", currentValue(rule, host, st));
          }
        } else {
          if (t.active) fire(rule, host, "recovery", currentValue(rule, host, st));
          t.pendingSince = 0; t.active = false;
        }
      });
    });

    // Prune tracking for rules/hosts no longer evaluated (rule deleted, host removed).
    Object.keys(track).forEach(function (k) { if (!live[k]) delete track[k]; });
    recomputeActive();
    refreshCards();
  }

  function recomputeActive() {
    var n = 0; Object.keys(track).forEach(function (k) { if (track[k].active) n++; });
    activeCount = n;
  }

  /* ============================ dispatch ============================ */
  function fire(rule, host, kind, val) {
    var alert = {
      id: uid(),
      ts: Date.now(),
      kind: kind,                                  // "alert" | "recovery"
      severity: kind === "recovery" ? "recovery" : rule.severity,
      host: host,
      metric: rule.metric,
      value: val,
      title: kind === "recovery" ? tr("alerting.recovered", "Recovered") : (rule.severity === "critical" ? tr("alerting.critical", "Critical") : tr("alerting.warning", "Warning")),
      message: buildMessage(rule, host, val, kind),
    };
    recent.unshift(alert);
    if (recent.length > 40) recent.pop();

    // Local pop-up (the only channel wired today).
    if (cfg.channels.popup && cfg.channels.popup.enabled) showPopup(alert);
    // Remote channels — payloads are built; the send is stubbed (see dispatchRemote).
    ["ntfy", "telegram", "webhook", "generic"].forEach(function (ch) {
      if (cfg.channels[ch] && cfg.channels[ch].enabled) dispatchRemote(ch, alert);
    });
  }

  function buildMessage(rule, host, val, kind) {
    if (rule.metric === "offline") {
      return (kind === "recovery")
        ? tr("alerting.msg.online", "{host} is back online").replace("{host}", host)
        : tr("alerting.msg.offline", "{host} is offline").replace("{host}", host);
    }
    var mlabel = metricLabel(rule.metric);
    var unit = (METRICS[rule.metric] || {}).unit || "";
    var vtxt = fmtVal(rule.metric, val);
    if (kind === "recovery") {
      return tr("alerting.msg.recovered", "{host}: {metric} back to normal (now {value})")
        .replace("{host}", host).replace("{metric}", mlabel).replace("{value}", vtxt);
    }
    var opTxt = rule.op === "<" ? tr("alerting.msg.below", "below") : tr("alerting.msg.above", "above");
    return tr("alerting.msg.breach", "{host}: {metric} {op} {threshold}{unit} (now {value})")
      .replace("{host}", host).replace("{metric}", mlabel).replace("{op}", opTxt)
      .replace("{threshold}", rule.threshold).replace("{unit}", unit).replace("{value}", vtxt);
  }

  // Remote channels: build the outbound payload now so completing them later is just wiring the send.
  // For v0.1 the actual network call is intentionally NOT made (most of these reject cross-origin from
  // the browser; a hub proxy endpoint will carry them later). We log the built payload for debugging.
  function dispatchRemote(channel, alert) {
    var c = cfg.channels[channel] || {};
    var text = "[" + alert.title + "] " + alert.message;
    var payload = null;
    if (channel === "ntfy") {
      payload = { url: (c.server || "https://ntfy.sh") + "/" + encodeURIComponent(c.topic || ""), method: "POST", body: text, headers: { Title: "Michka " + alert.title, Priority: c.priority || "default" } };
    } else if (channel === "telegram") {
      payload = { url: "https://api.telegram.org/bot" + (c.token || "") + "/sendMessage", method: "POST", json: { chat_id: c.chatId || "", text: text } };
    } else if (channel === "webhook") {
      // Discord uses { content }, Slack uses { text }; send both keys so one URL type works either way.
      payload = { url: c.url || "", method: "POST", json: { content: text, text: text } };
    } else if (channel === "generic") {
      payload = { url: c.url || "", method: c.method || "POST", json: { title: alert.title, severity: alert.severity, host: alert.host, metric: alert.metric, value: alert.value, message: alert.message, ts: alert.ts } };
    }
    // TODO(v-next): route through a hub proxy (e.g. POST /api/widget/alert-send) instead of logging.
    try { console.debug("[alerting] would send via " + channel + ":", payload); } catch (e) { /* ignore */ }
  }

  /* ============================ pop-up stack ============================ */
  function popupHost() {
    var h = document.getElementById("michka-alert-stack");
    if (!h) {
      h = document.createElement("div");
      h.id = "michka-alert-stack";
      h.className = "al-stack";
      document.body.appendChild(h);
    }
    return h;
  }
  function showPopup(alert) {
    var host = popupHost();
    var card = el("div", "al-toast al-" + alert.severity);
    card.innerHTML =
      '<div class="al-toast-ic">' + BELL + '</div>' +
      '<div class="al-toast-main">' +
        '<div class="al-toast-title">' + esc(alert.title) + '</div>' +
        '<div class="al-toast-msg">' + esc(alert.message) + '</div>' +
      '</div>' +
      '<button class="al-toast-x" aria-label="' + esc(tr("alerting.dismiss", "Dismiss")) + '">' + CLOSE + '</button>';
    card.querySelector(".al-toast-x").addEventListener("click", function () { removeToast(card); });
    // Newest on top (nearest the top-right anchor); older toasts flow below and stay until dismissed.
    host.insertBefore(card, host.firstChild);
    // Enter animation.
    requestAnimationFrame(function () { card.classList.add("in"); });
    // Auto-dismiss: recoveries + warnings fade; criticals stay until dismissed.
    var secs = (cfg.channels.popup && cfg.channels.popup.autoDismissSec) || 12;
    if (alert.severity !== "critical") card._t = setTimeout(function () { removeToast(card); }, (alert.severity === "recovery" ? Math.min(secs, 6) : secs) * 1000);
    capStack(host);
  }
  // Keep the stack within what actually fits the screen so toasts can never pile off the bottom edge of
  // the short kiosk panel (there's no scroll — the container is click-through). Newest stays on top; when
  // over the fit, roll the OLDEST off, preferring non-criticals so a critical you must acknowledge is the
  // last thing dropped. (~2 fit on a 400px kiosk, ~5 on a tall desktop window; the widget card still shows
  // the live "N active" count regardless, so a rolled-off alert's state isn't lost.)
  function fitCount() {
    var vh = (window.innerHeight || 400) - 40;     // 20px top + 20px breathing room at the bottom
    return Math.max(1, Math.min(6, Math.round(vh / 175)));   // ~175px per toast incl. gap
  }
  function capStack(host) {
    var max = fitCount();
    // Live toasts only (skip ones already animating out), oldest last.
    var live = Array.prototype.slice.call(host.querySelectorAll(".al-toast:not(.out)"));
    while (live.length > max) {
      // Prefer to evict the oldest NON-critical; fall back to the oldest overall if all are critical.
      var victim = null;
      for (var i = live.length - 1; i >= 0; i--) { if (!live[i].classList.contains("al-critical")) { victim = live[i]; break; } }
      if (!victim) victim = live[live.length - 1];
      removeToast(victim);
      live.splice(live.indexOf(victim), 1);
    }
  }
  function removeToast(card) {
    if (card._t) clearTimeout(card._t);
    card.classList.remove("in"); card.classList.add("out");
    setTimeout(function () { if (card.parentNode) card.parentNode.removeChild(card); }, 260);
  }

  /* ============================ card rendering ============================ */
  function refreshCards() { instances.forEach(refreshInstance); }

  function refreshInstance(inst) {
    var root = inst.ctx.el; if (!root || !root.isConnected) return;
    root.innerHTML = "";

    var gear = el("button", "al-gear", GEAR);
    gear.title = tr("alerting.settings", "Alert settings");
    gear.setAttribute("aria-label", tr("alerting.settings", "Alert settings"));
    gear.addEventListener("click", function () { openSettings(); });

    // Empty state — no rules yet.
    if (!cfg.rules.length) {
      var setup = el("div", "al-setup");
      setup.appendChild(el("div", "al-setup-icon", BELL));
      setup.appendChild(el("div", "al-setup-title", esc(tr("alerting.empty", "No alert rules yet"))));
      setup.appendChild(el("div", "al-setup-hint", esc(tr("alerting.emptyHint", "Add a rule to be warned when a host is under stress or drops offline."))));
      var b = el("button", "al-btn", esc(tr("alerting.addRule", "Add a rule")));
      b.addEventListener("click", function () { openSettings("rules"); });
      setup.appendChild(b);
      root.appendChild(setup);
      root.appendChild(gear);
      return;
    }

    // Header: overall status.
    var head = el("div", "al-head");
    var ok = activeCount === 0;
    head.innerHTML =
      '<span class="al-status ' + (ok ? "ok" : "firing") + '">' +
        '<span class="al-status-dot"></span>' +
        '<span class="al-status-txt">' + esc(ok ? tr("alerting.allClear", "All clear") : tr("alerting.nActive", "{n} active").replace("{n}", activeCount)) + '</span>' +
      '</span>' +
      '<span class="al-rule-count">' + esc(tr("alerting.nRules", "{n} rules").replace("{n}", cfg.rules.length)) + '</span>';
    root.appendChild(head);

    // Live rule list.
    var list = el("div", "al-list");
    var st = liveState();
    cfg.rules.forEach(function (rule) {
      var row = el("div", "al-row" + (rule.enabled ? "" : " off"));
      var stat = ruleStatus(rule, st);              // "ok" | "pending" | "firing" | "idle"
      row.innerHTML =
        '<span class="al-dot ' + stat + '"></span>' +
        '<div class="al-main">' +
          '<div class="al-rule-label">' + esc(ruleLabel(rule)) + '</div>' +
          '<div class="al-rule-sub">' + esc(targetSummary(rule)) + '</div>' +
        '</div>' +
        '<span class="al-sev al-' + rule.severity + '">' + esc(rule.severity === "critical" ? tr("alerting.critical", "Critical") : tr("alerting.warning", "Warning")) + '</span>';
      list.appendChild(row);
    });
    root.appendChild(list);
    root.appendChild(gear);
  }

  // Aggregate a rule's live status across all hosts it targets.
  function ruleStatus(rule, st) {
    if (!rule.enabled) return "idle";
    if (!st || !st.hosts) return "ok";
    var hosts = rule.hosts.length ? rule.hosts : knownHosts(st);
    var anyFiring = false, anyPending = false;
    hosts.forEach(function (h) {
      var t = track[rule.id + "|" + h];
      if (t && t.active) anyFiring = true;
      else if (t && t.pendingSince) anyPending = true;
    });
    return anyFiring ? "firing" : (anyPending ? "pending" : "ok");
  }
  // Short description of which clients a rule watches, for the card row.
  function targetSummary(rule) {
    var n = rule.hosts ? rule.hosts.length : 0;
    if (!n) return tr("alerting.allHosts", "All hosts");
    if (n === 1) return rule.hosts[0];
    return tr("alerting.nClients", "{n} clients").replace("{n}", n);
  }
  function ruleLabel(rule) {
    if (rule.metric === "offline") return metricLabel("offline");
    var opTxt = rule.op === "<" ? tr("alerting.msg.below", "below") : tr("alerting.msg.above", "above");
    return metricLabel(rule.metric) + " " + opTxt + " " + rule.threshold + ((METRICS[rule.metric] || {}).unit || "") +
      " · " + tr("alerting.forShort", "{n}s").replace("{n}", rule.forSec);
  }

  /* ============================ settings overlay ============================ */
  var draftRules = [];
  var activeInput = null;                 // last-focused field, for the shared symbol pad
  var curTab = "rules";

  function openSettings(tab) {
    closeSettings();
    curTab = tab || "rules";
    draftRules = cfg.rules.map(function (r) { return Object.assign({}, r); });

    overlay = el("div", "al-overlay");
    ["pointerdown", "pointermove", "pointerup", "touchstart", "touchmove", "touchend", "wheel"]
      .forEach(function (ev) { overlay.addEventListener(ev, function (e) { e.stopPropagation(); }, { passive: true }); });

    var close = el("button", "al-ov-close", CLOSE);
    close.title = tr("alerting.close", "Close");
    close.setAttribute("aria-label", tr("alerting.close", "Close"));
    close.addEventListener("click", closeSettings);
    overlay.appendChild(close);

    var panel = el("div", "al-panel");
    panel.appendChild(el("div", "al-ov-title", esc(tr("alerting.title", "Alerting"))));

    // Tabs.
    var tabs = el("div", "al-tabs");
    var tRules = el("button", "al-tab" + (curTab === "rules" ? " on" : ""), esc(tr("alerting.tabRules", "Rules")));
    var tChan = el("button", "al-tab" + (curTab === "channels" ? " on" : ""), esc(tr("alerting.tabChannels", "Delivery")));
    tRules.addEventListener("click", function () { curTab = "rules"; renderTabs(panel); });
    tChan.addEventListener("click", function () { curTab = "channels"; renderTabs(panel); });
    tabs.appendChild(tRules); tabs.appendChild(tChan);
    panel.appendChild(tabs);

    // Body row: the tab content scrolls on the LEFT; the keypad + Save stay fixed on the RIGHT so they
    // never scroll off and the wide-but-short kiosk panel uses its width instead of stacking vertically.
    var bodyRow = el("div", "al-body-row");
    var bodyWrap = el("div", "al-tabbody");
    bodyRow.appendChild(bodyWrap);

    var side = el("div", "al-side");
    side.appendChild(buildPad());
    var save = el("button", "al-btn al-save", esc(tr("alerting.save", "Save")));
    save.addEventListener("click", commit);
    side.appendChild(save);
    bodyRow.appendChild(side);
    panel.appendChild(bodyRow);

    overlay.appendChild(panel);
    document.body.appendChild(overlay);
    renderTabs(panel);
  }

  function renderTabs(panel) {
    panel.querySelectorAll(".al-tab").forEach(function (b, i) { b.classList.toggle("on", (i === 0) === (curTab === "rules")); });
    var body = panel.querySelector(".al-tabbody"); body.innerHTML = "";
    if (curTab === "rules") body.appendChild(buildRulesTab());
    else body.appendChild(buildChannelsTab());
  }

  /* ---- Rules tab ---- */
  function buildRulesTab() {
    var wrap = el("div", "al-rules");
    wrap.appendChild(el("p", "al-hint", esc(tr("alerting.rulesHint", "Alert when a metric crosses a threshold for a sustained time, or when a host goes offline. Leave the host as All to watch every host."))));

    var listEl = el("div", "al-edit-list");
    wrap.appendChild(listEl);

    var add = el("button", "al-addrow", "+ " + esc(tr("alerting.addRule", "Add a rule")));
    add.addEventListener("click", function () {
      draftRules.push(normalizeRule({ metric: "cpu", op: ">", threshold: 90, forSec: 60, severity: "warning", enabled: true }));
      renderRuleRows(listEl);
    });
    wrap.appendChild(add);
    renderRuleRows(listEl);
    return wrap;
  }

  function renderRuleRows(listEl) {
    listEl.innerHTML = "";
    if (!draftRules.length) { listEl.appendChild(el("div", "al-none", esc(tr("alerting.noRules", "No rules yet — add one below.")))); return; }
    draftRules.forEach(function (r, idx) {
      var row = el("div", "al-edit-row");
      var metricOpts = METRIC_ORDER.map(function (m) { return '<option value="' + m + '"' + (r.metric === m ? " selected" : "") + '>' + esc(metricLabel(m)) + '</option>'; }).join("");
      var opOpts = '<option value=">"' + (r.op === ">" ? " selected" : "") + '>' + esc(tr("alerting.msg.above", "above")) + '</option>' +
                   '<option value="<"' + (r.op === "<" ? " selected" : "") + '>' + esc(tr("alerting.msg.below", "below")) + '</option>';
      var sevOpts = '<option value="warning"' + (r.severity === "warning" ? " selected" : "") + '>' + esc(tr("alerting.warning", "Warning")) + '</option>' +
                    '<option value="critical"' + (r.severity === "critical" ? " selected" : "") + '>' + esc(tr("alerting.critical", "Critical")) + '</option>';
      var isOffline = r.metric === "offline";

      var unit = (METRICS[r.metric] || {}).unit || "";
      // Every field: a label on top, then ONE control row (input/select + unit inline) so all fields are
      // the same height and their controls bottom-align across the row.
      row.innerHTML =
        '<div class="al-ef">' +
          '<label>' + esc(tr("alerting.when", "When")) + '</label>' +
          '<div class="al-inrow"><select class="al-sel" data-k="metric">' + metricOpts + '</select></div>' +
        '</div>' +
        '<div class="al-ef al-ef-cond"' + (isOffline ? ' style="display:none"' : "") + '>' +
          '<label>' + esc(tr("alerting.is", "is")) + '</label>' +
          '<div class="al-inrow">' +
            '<select class="al-sel" data-k="op">' + opOpts + '</select>' +
            '<input class="al-num" data-k="threshold" inputmode="none" value="' + esc(r.threshold) + '">' +
            '<span class="al-unit">' + esc(unit) + '</span>' +
          '</div>' +
        '</div>' +
        '<div class="al-ef">' +
          '<label>' + esc(tr("alerting.forT", "for")) + '</label>' +
          '<div class="al-inrow"><input class="al-num" data-k="forSec" inputmode="none" value="' + esc(r.forSec) + '"><span class="al-unit">s</span></div>' +
        '</div>' +
        '<div class="al-ef">' +
          '<label>' + esc(tr("alerting.level", "level")) + '</label>' +
          '<div class="al-inrow"><select class="al-sel" data-k="severity">' + sevOpts + '</select></div>' +
        '</div>' +
        '<button class="al-del" title="' + esc(tr("alerting.remove", "Remove")) + '" aria-label="' + esc(tr("alerting.remove", "Remove")) + '">' + TRASH + '</button>';

      // Wire selects.
      row.querySelectorAll("select.al-sel").forEach(function (sel) {
        sel.addEventListener("change", function () {
          var k = sel.getAttribute("data-k");
          r[k] = sel.value;
          if (k === "metric") renderRuleRows(listEl);   // re-render: offline hides the threshold, unit changes
        });
      });
      // Wire numeric fields (OSK numpad on the kiosk; plain typing in a browser).
      row.querySelectorAll("input.al-num").forEach(function (inp) {
        inp.addEventListener("input", function () { r[inp.getAttribute("data-k")] = parseFloat(inp.value) || 0; });
        wireNum(inp);
      });
      row.querySelector(".al-del").addEventListener("click", function () { draftRules.splice(idx, 1); renderRuleRows(listEl); });
      // Client targeting: a chip picker (All hosts / each known client / + add a client by name).
      row.appendChild(buildTargets(r, listEl));
      listEl.appendChild(row);
    });
  }

  // The per-rule client selector. Empty rule.hosts = all hosts; otherwise the chosen clients (which may
  // include a name that isn't reporting yet, e.g. to alert when an expected client is offline).
  function buildTargets(r, listEl) {
    var box = el("div", "al-targets");
    box.appendChild(el("span", "al-tlabel", esc(tr("alerting.clients", "Clients"))));

    var allChip = el("button", "al-chip" + (r.hosts.length ? "" : " on"), esc(tr("alerting.allHosts", "All hosts")));
    allChip.addEventListener("click", function () { r.hosts = []; renderRuleRows(listEl); });
    box.appendChild(allChip);

    // Known hosts, then any rule-listed client not currently reporting.
    var names = knownHosts(liveState()).slice().sort();
    r.hosts.forEach(function (h) { if (names.indexOf(h) < 0) names.push(h); });
    names.forEach(function (h) {
      var on = r.hosts.indexOf(h) >= 0;
      var chip = el("button", "al-chip" + (on ? " on" : ""), esc(h));
      chip.addEventListener("click", function () {
        var i = r.hosts.indexOf(h);
        if (i >= 0) r.hosts.splice(i, 1); else r.hosts.push(h);
        renderRuleRows(listEl);
      });
      box.appendChild(chip);
    });

    // Add a client by name (for a client not in the list, e.g. not yet connected).
    var add = el("button", "al-chip al-addc", "+ " + esc(tr("alerting.addClient", "Add client")));
    add.addEventListener("click", function () {
      var inp = el("input", "al-chip-in");
      inp.type = "text"; inp.inputMode = "none"; inp.spellcheck = false;
      inp.placeholder = tr("alerting.addClientPh", "Client name or IP");
      inp._rule = r;                 // so Save can flush a name still being typed (see flushPendingClients)
      var ok = el("button", "al-chip al-addok", "✓");
      var commitAdd = function () { var v = (inp.value || "").trim(); if (v && r.hosts.indexOf(v) < 0) r.hosts.push(v); renderRuleRows(listEl); };
      ok.addEventListener("click", commitAdd);
      inp.addEventListener("keydown", function (e) { if (e.key === "Enter") { e.preventDefault(); commitAdd(); } });
      box.replaceChild(inp, add); box.appendChild(ok);
      wireText(inp); inp.focus();
    });
    box.appendChild(add);
    return box;
  }

  /* ---- Channels tab ---- */
  function buildChannelsTab() {
    var wrap = el("div", "al-channels");
    wrap.appendChild(el("p", "al-hint", esc(tr("alerting.channelsHint", "Choose how alerts are delivered. The on-screen pop-up works now; the push channels are set up here and will send once enabled in a later build."))));

    // Local pop-up (working).
    wrap.appendChild(channelCard("popup", tr("alerting.ch.popup", "On-screen pop-up"), tr("alerting.ch.popupDesc", "Show a toast on this panel when an alert fires."), true, function (body, c) {
      var f = el("div", "al-cf");
      f.innerHTML = '<label>' + esc(tr("alerting.autoDismiss", "Auto-dismiss after")) + '</label><input class="al-num" data-k="autoDismissSec" inputmode="none" value="' + esc(c.autoDismissSec || 12) + '"><span class="al-unit">s</span>';
      var inp = f.querySelector("input");
      inp.addEventListener("input", function () { c.autoDismissSec = parseInt(inp.value, 10) || 12; });
      wireNum(inp);
      body.appendChild(f);
      var test = el("button", "al-mini", esc(tr("alerting.sendTest", "Send a test alert")));
      test.addEventListener("click", sendTest);
      body.appendChild(test);
    }));

    // ntfy.sh (stub).
    wrap.appendChild(channelCard("ntfy", tr("alerting.ch.ntfy", "ntfy.sh"), tr("alerting.ch.ntfyDesc", "Push to your phone via an ntfy topic. Free, no account."), false, function (body, c) {
      body.appendChild(textField(tr("alerting.ntfyServer", "Server"), c, "server", "https://ntfy.sh"));
      body.appendChild(textField(tr("alerting.ntfyTopic", "Topic"), c, "topic", "my-michka-alerts"));
    }));

    // Telegram (stub).
    wrap.appendChild(channelCard("telegram", tr("alerting.ch.telegram", "Telegram"), tr("alerting.ch.telegramDesc", "Message a Telegram chat from a bot you create with @BotFather."), false, function (body, c) {
      body.appendChild(textField(tr("alerting.tgToken", "Bot token"), c, "token", "123456:ABC-..."));
      body.appendChild(textField(tr("alerting.tgChat", "Chat ID"), c, "chatId", "123456789"));
    }));

    // Discord / Slack webhook (stub).
    wrap.appendChild(channelCard("webhook", tr("alerting.ch.webhook", "Discord / Slack"), tr("alerting.ch.webhookDesc", "Post to a Discord or Slack channel via an incoming webhook URL."), false, function (body, c) {
      var styleF = el("div", "al-cf");
      styleF.innerHTML = '<label>' + esc(tr("alerting.hookStyle", "Service")) + '</label><select class="al-sel" data-k="style"><option value="discord"' + (c.style === "discord" ? " selected" : "") + '>Discord</option><option value="slack"' + (c.style === "slack" ? " selected" : "") + '>Slack</option></select>';
      styleF.querySelector("select").addEventListener("change", function (e) { c.style = e.target.value; });
      body.appendChild(styleF);
      body.appendChild(textField(tr("alerting.hookUrl", "Webhook URL"), c, "url", "https://discord.com/api/webhooks/..."));
    }));

    // Generic webhook (stub).
    wrap.appendChild(channelCard("generic", tr("alerting.ch.generic", "Generic webhook"), tr("alerting.ch.genericDesc", "POST the alert as JSON to any URL of your own."), false, function (body, c) {
      body.appendChild(textField(tr("alerting.genUrl", "URL"), c, "url", "https://example.com/hook"));
    }));

    return wrap;
  }

  // A collapsible channel card with an enable toggle and (when enabled) its config fields.
  function channelCard(key, name, desc, working, fillBody) {
    var c = cfg.channels[key] || (cfg.channels[key] = {});
    var card = el("div", "al-ch");
    var head = el("div", "al-ch-head");
    head.innerHTML =
      '<div class="al-ch-name">' + esc(name) + (working ? ' <span class="al-badge live">' + esc(tr("alerting.badgeLive", "Working")) + '</span>' : ' <span class="al-badge soon">' + esc(tr("alerting.badgeSoon", "Preview")) + '</span>') + '</div>' +
      '<label class="al-switch"><input type="checkbox"' + (c.enabled ? " checked" : "") + '><span class="al-track"></span></label>';
    card.appendChild(head);
    card.appendChild(el("div", "al-ch-desc", esc(desc)));
    // Remote channels aren't wired to actually send yet (see dispatchRemote) — flag it in the card so the
    // user knows enabling one won't deliver. Deliberately not i18n'd yet (English-only, like the note text).
    if (!working) card.appendChild(el("div", "al-ch-note", "In Progress - Not operational yet"));
    var body = el("div", "al-ch-body");
    if (!c.enabled) body.style.display = "none";
    fillBody(body, c);
    card.appendChild(body);
    head.querySelector('input[type="checkbox"]').addEventListener("change", function (e) {
      c.enabled = e.target.checked; body.style.display = c.enabled ? "" : "none";
    });
    return card;
  }

  // Labelled text field bound to config key `k`, with OSK on the kiosk.
  function textField(label, obj, k, ph) {
    var f = el("div", "al-cf al-cf-col");
    f.innerHTML = '<label>' + esc(label) + '</label><input class="al-txt" type="text" inputmode="none" spellcheck="false" placeholder="' + esc(ph || "") + '" value="' + esc(obj[k] || "") + '">';
    var inp = f.querySelector("input");
    inp.addEventListener("input", function () { obj[k] = inp.value; });
    wireText(inp);
    return f;
  }

  function sendTest() {
    fire({ id: "test", metric: "cpu", host: "test-host", op: ">", threshold: 90, forSec: 0, severity: "warning" },
      (liveState() && liveState().selected) || "test-host", "alert", 97);
  }

  // A client name still being typed (input open, ✓ not yet clicked) would otherwise be lost when Save is
  // clicked — flush any open add-client inputs into their rule first.
  function flushPendingClients() {
    if (!overlay) return;
    overlay.querySelectorAll(".al-chip-in").forEach(function (inp) {
      var r = inp._rule, v = (inp.value || "").trim();
      if (r && v && r.hosts.indexOf(v) < 0) r.hosts.push(v);
    });
  }

  function commit() {
    flushPendingClients();
    cfg.rules = draftRules.map(normalizeRule);
    saveCfg();
    // Reset tracking so edited thresholds re-evaluate cleanly.
    track = {}; recomputeActive();
    closeSettings();
    refreshCards();
    startEngine();
  }

  /* ---- shared input helpers (kiosk OSK) ---- */
  function wireText(input) {
    input.addEventListener("focus", function () {
      activeInput = input;
      if (window.Michka && Michka.osk) Michka.osk("text", input, function () { input.dispatchEvent(new Event("input")); });
    });
  }
  // Numeric fields are driven by the always-visible side keypad (no OSK popup — it would compete with it).
  function wireNum(input) {
    input.addEventListener("focus", function () { activeInput = input; });
  }
  // Shared symbol/number pad (the letter OSK lacks digits and . : / -), acts on the last-focused field.
  function buildPad() {
    var pad = el("div", "al-pad");
    ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", ".", ":", "/", "-", "⌫", "✕"].forEach(function (k) {
      var b = el("button", "al-pad-key", k);
      b.addEventListener("click", function () {
        if (!activeInput) return;
        if (k === "⌫") activeInput.value = activeInput.value.slice(0, -1);
        else if (k === "✕") activeInput.value = "";
        else activeInput.value += k;
        activeInput.dispatchEvent(new Event("input"));
      });
      pad.appendChild(b);
    });
    return pad;
  }

  function closeSettings() {
    if (!overlay) return;
    if (window.Michka && Michka.hideOsk) Michka.hideOsk();
    activeInput = null;
    overlay.remove();
    overlay = null;
  }

  /* ============================ registration ============================ */
  Michka.widget("alerting", {
    render: function (ctx) { ctxRef = ctx; return '<div class="al-root"></div>'; },
    mount: function (ctx) {
      ctxRef = ctx;
      loadCfg();
      var inst = { ctx: ctx };
      instances.push(inst);
      refreshInstance(inst);
      startEngine();                 // starts once; kept running across host switches for global alerts
      ctx._alInst = inst;
    },
    unmount: function (ctx) {
      var inst = ctx._alInst;
      if (inst) { var i = instances.indexOf(inst); if (i >= 0) instances.splice(i, 1); }
      if (!instances.length) closeSettings();
      // NOTE: the engine + pop-ups keep running on purpose so alerts stay global after a host switch.
    },
  });
})();

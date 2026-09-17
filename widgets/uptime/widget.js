/* Uptime Monitor — up/down + response time for a list of websites/services on your network.
 *
 * The kiosk can't ping or do cross-origin probes (CORS), so each target is checked by the hub:
 * GET /api/widget/uptime?url=<target> returns { ok, status, ms, error }. The list of monitors lives in
 * the browser's localStorage ("michka.uptime") — it's network-wide, not tied to a michka host — and the
 * widget calls the hub once per target each refresh. Flat colours only, per the project's rules.
 *
 * Strings use ctx.t("uptime.*") with inline English fallbacks (tr(key, default)); lang-file keys can be
 * added later for parity, like the Docker widget.
 */
(function () {
  "use strict";

  var REFRESH_MS = 15000;            // re-check every 15s
  var ctxRef = null;                 // last ctx (esc/t/config)
  var instances = [];                // mounted cards
  var overlay = null;                // settings takeover, or null
  var cfg = { targets: [] };         // { targets: [ {id,label,url} ] } — loaded from the hub on mount

  /* ---------------- config (server-persisted via ctx, shared across browsers) ---------------- */
  function loadCfg() {
    var d = { targets: [] };
    try { var o = (ctxRef && ctxRef.getConfig) ? ctxRef.getConfig() : {}; if (o && Array.isArray(o.targets)) d.targets = o.targets; } catch (e) { /* defaults */ }
    cfg = d;
    return d;
  }
  function saveCfg() { try { if (ctxRef && ctxRef.setConfig) ctxRef.setConfig(cfg); } catch (e) { /* ignore */ } }
  function uid() { return "u" + Math.random().toString(36).slice(2, 9); }

  /* ---------------- helpers ---------------- */
  function esc(s) { return ctxRef && ctxRef.esc ? ctxRef.esc(s) : String(s == null ? "" : s); }
  function tr(k, d) { var v = ctxRef && ctxRef.t ? ctxRef.t(k) : k; return (v && v !== k) ? v : (d || k); }
  function el(tag, cls, html) { var e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }

  var GEAR = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>';
  var CLOSE = '<svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>';
  var PULSE = '<svg viewBox="0 0 24 24" width="40" height="40" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12h4l2.5-6 4 13 3-9 2 2h4.5"/></svg>';
  var TRASH = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6M14 11v6"/></svg>';

  /* ---------------- card rendering ---------------- */
  function refreshInstance(inst) {
    var root = inst.ctx.el;
    if (!root) return;
    root.innerHTML = "";

    var gear = el("button", "up-gear", GEAR);
    gear.title = tr("uptime.settings", "Edit monitors");
    gear.setAttribute("aria-label", tr("uptime.settings", "Edit monitors"));
    gear.addEventListener("click", function () { openSettings(); });

    if (!cfg.targets.length) {
      var setup = el("div", "up-setup");
      setup.appendChild(el("div", "up-setup-icon", PULSE));
      setup.appendChild(el("div", "up-setup-title", esc(tr("uptime.empty", "No monitors yet"))));
      setup.appendChild(el("div", "up-setup-hint", esc(tr("uptime.emptyHint",
        "Add a website or service to watch its status and response time."))));
      var b = el("button", "up-btn", esc(tr("uptime.add", "Add monitor")));
      b.addEventListener("click", function () { openSettings(); });
      setup.appendChild(b);
      root.appendChild(setup);
      root.appendChild(gear);
      return;
    }

    var head = el("div", "up-head");
    head.innerHTML = '<span class="up-count" data-count>--</span>' +
      '<span class="up-count-lbl">' + esc(tr("uptime.upTotal", "up / total")) + "</span>";
    var list = el("div", "up-list");
    cfg.targets.forEach(function (t) {
      var row = el("div", "up-row");
      row.dataset.id = t.id;
      row.innerHTML =
        '<span class="up-dot checking"></span>' +
        '<div class="up-main"><div class="up-label">' + esc(t.label || hostOf(t.url)) + "</div>" +
        '<div class="up-url">' + esc(t.url) + "</div></div>" +
        '<div class="up-stat" data-stat>' + esc(tr("uptime.checking", "checking…")) + "</div>";
      list.appendChild(row);
    });
    root.appendChild(head);
    root.appendChild(list);
    root.appendChild(gear);

    checkAll(inst);
  }

  function hostOf(url) {
    try { return new URL(/:\/\//.test(url) ? url : "http://" + url).host; } catch (e) { return url; }
  }

  function checkAll(inst) {
    var root = inst.ctx.el;
    var total = cfg.targets.length, done = 0, up = 0;
    if (!total) return;
    cfg.targets.forEach(function (t) {
      var finish = function (ok, label) {
        if (!root.isConnected) return;
        var row = root.querySelector('.up-row[data-id="' + t.id + '"]');
        if (row) {
          var dot = row.querySelector(".up-dot");
          var stat = row.querySelector("[data-stat]");
          dot.className = "up-dot " + (ok ? "ok" : "bad");
          stat.className = "up-stat " + (ok ? "ok" : "bad");
          stat.textContent = label;
        }
        if (ok) up++;
        if (++done === total) { var c = root.querySelector("[data-count]"); if (c) c.textContent = up + " / " + total; }
      };
      inst.ctx.api("/api/widget/uptime?url=" + encodeURIComponent(t.url))
        .then(function (r) {
          if (r && r.ok) finish(true, (r.ms || 0) + " ms");
          else finish(false, tr("uptime.down", "down"));
        })
        .catch(function () { finish(false, tr("uptime.down", "down")); });
    });
  }

  function refreshAll() { instances.forEach(refreshInstance); }

  /* ---------------- settings overlay ---------------- */
  // Working copy of the targets, edited live; committed to cfg on Save.
  var draft = [];
  var activeInput = null;   // last-focused field, for the shared symbol pad

  function openSettings() {
    closeSettings();
    draft = cfg.targets.map(function (t) { return { id: t.id, label: t.label || "", url: t.url || "" }; });
    if (!draft.length) draft.push({ id: uid(), label: "", url: "" });

    overlay = el("div", "up-overlay");
    ["pointerdown", "pointermove", "pointerup", "touchstart", "touchmove", "touchend", "wheel"]
      .forEach(function (ev) { overlay.addEventListener(ev, function (e) { e.stopPropagation(); }, { passive: true }); });

    var close = el("button", "up-ov-close", CLOSE);
    close.title = tr("uptime.close", "Close");
    close.setAttribute("aria-label", tr("uptime.close", "Close"));
    close.addEventListener("click", closeSettings);
    overlay.appendChild(close);

    var panel = el("div", "up-panel");
    panel.appendChild(el("div", "up-ov-title", esc(tr("uptime.title", "Monitors"))));
    panel.appendChild(el("p", "up-hint", esc(tr("uptime.editHint",
      "Add what to watch. An IP or hostname is pinged (e.g. 192.168.0.1); add a port to check a service (nas:5000); or use a full URL (https://site)."))));

    var editList = el("div", "up-edit-list");
    panel.appendChild(editList);

    var addBtn = el("button", "up-addrow", "+ " + esc(tr("uptime.add", "Add monitor")));
    addBtn.addEventListener("click", function () { draft.push({ id: uid(), label: "", url: "" }); renderRows(editList); });
    panel.appendChild(addBtn);

    var pad = buildPad();
    panel.appendChild(pad);

    var save = el("button", "up-btn up-save", esc(tr("uptime.save", "Save")));
    save.addEventListener("click", function () {
      cfg.targets = draft
        .map(function (d) { return { id: d.id, label: (d.label || "").trim(), url: (d.url || "").trim() }; })
        .filter(function (d) { return d.url.length > 0; });
      saveCfg();
      closeSettings();
      refreshAll();
    });
    panel.appendChild(save);

    overlay.appendChild(panel);
    document.body.appendChild(overlay);
    renderRows(editList);
  }

  function renderRows(editList) {
    editList.innerHTML = "";
    draft.forEach(function (d, idx) {
      var row = el("div", "up-edit-row");

      var labelIn = el("input", "up-input up-edit-label");
      labelIn.type = "text"; labelIn.inputMode = "none"; labelIn.spellcheck = false;
      labelIn.placeholder = tr("uptime.labelPh", "Name (optional)");
      labelIn.value = d.label || "";
      labelIn.addEventListener("input", function () { d.label = labelIn.value; });
      wireField(labelIn);

      var urlIn = el("input", "up-input up-edit-url");
      urlIn.type = "text"; urlIn.inputMode = "none"; urlIn.spellcheck = false;
      urlIn.placeholder = tr("uptime.urlPh", "Address e.g. 192.168.0.1:8080");
      urlIn.value = d.url || "";
      urlIn.addEventListener("input", function () { d.url = urlIn.value; });
      wireField(urlIn);

      var del = el("button", "up-del", TRASH);
      del.title = tr("uptime.remove", "Remove");
      del.setAttribute("aria-label", tr("uptime.remove", "Remove"));
      del.addEventListener("click", function () { draft.splice(idx, 1); if (!draft.length) draft.push({ id: uid(), label: "", url: "" }); renderRows(editList); });

      var fields = el("div", "up-edit-fields");
      fields.appendChild(labelIn);
      fields.appendChild(urlIn);
      row.appendChild(fields);
      row.appendChild(del);
      editList.appendChild(row);
    });
  }

  // Each field: tapping it tracks it for the symbol pad AND pops the letter keyboard (hostnames).
  function wireField(input) {
    input.addEventListener("focus", function () {
      activeInput = input;
      if (window.Michka && Michka.osk) Michka.osk("text", input, function () {
        input.dispatchEvent(new Event("input"));
      });
    });
  }

  // Shared symbol/number pad — the letter OSK has no digits or . : / - which addresses need. Acts on the
  // last-focused field.
  function buildPad() {
    var pad = el("div", "up-pad");
    ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", ".", ":", "/", "-", "⌫", "✕"].forEach(function (k) {
      var b = el("button", "up-pad-key", k);
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

  /* ---------------- registration ---------------- */
  Michka.widget("uptime", {
    render: function (ctx) { ctxRef = ctx; return '<div class="up-root"></div>'; },
    mount: function (ctx) {
      ctxRef = ctx;
      loadCfg();                       // pull this host's saved monitors now ctx (config) is available
      var inst = { ctx: ctx };
      instances.push(inst);
      refreshInstance(inst);
      inst._timer = setInterval(function () { checkAll(inst); }, REFRESH_MS);
      ctx._upInst = inst;
    },
    unmount: function (ctx) {
      var inst = ctx._upInst;
      if (inst) {
        if (inst._timer) clearInterval(inst._timer);
        var i = instances.indexOf(inst); if (i >= 0) instances.splice(i, 1);
      }
      if (!instances.length) closeSettings();
    }
  });
})();

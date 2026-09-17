/* Pi-hole — network-wide DNS ad-blocking stats.
 *
 * Pi-hole's web API isn't CORS-enabled, so the kiosk can't call it directly; the hub proxies it:
 * GET /api/widget/pihole?base=<url>&key=<password> returns a normalised summary (v6 REST API, with a
 * v5 fallback). The Pi-hole URL + password live in the browser's localStorage ("michka.pihole") — it's
 * a network-wide service, so the config is global (not per michka host) — and ride in on the query
 * string to the same-origin hub. Flat colours only, per the project's rules.
 *
 * Strings use ctx.t("pihole.*") with inline English fallbacks (tr(key, default)); lang-file keys can be
 * added later for parity, like the Docker widget.
 */
(function () {
  "use strict";

  var REFRESH_MS = 20000;            // Pi-hole stats move slowly; poll every 20s (also spares v6 sessions)
  var ctxRef = null;
  var instances = [];
  var overlay = null;
  var cfg = { base: "", key: "" };   // loaded from the hub on mount (ctx.getConfig)

  /* ---------------- config (server-persisted via ctx, shared across browsers) ---------------- */
  function loadCfg() {
    var d = { base: "", key: "" };
    try { var o = (ctxRef && ctxRef.getConfig) ? ctxRef.getConfig() : {}; if (o) { d.base = o.base || ""; d.key = o.key || ""; } } catch (e) { /* defaults */ }
    cfg = d;
    return d;
  }
  function saveCfg() { try { if (ctxRef && ctxRef.setConfig) ctxRef.setConfig(cfg); } catch (e) { /* ignore */ } }

  /* ---------------- helpers ---------------- */
  function esc(s) { return ctxRef && ctxRef.esc ? ctxRef.esc(s) : String(s == null ? "" : s); }
  function tr(k, d) { var v = ctxRef && ctxRef.t ? ctxRef.t(k) : k; return (v && v !== k) ? v : (d || k); }
  function el(tag, cls, html) { var e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }
  function num(n) { n = +n || 0; return n >= 1000 ? n.toLocaleString() : String(Math.round(n)); }
  function stripScheme(u) { return (u || "").trim().replace(/^https?:\/\//i, "").replace(/^\/+|\/+$/g, ""); }

  var GEAR = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>';
  var CLOSE = '<svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>';
  var SHIELD = '<svg viewBox="0 0 24 24" width="40" height="40" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2.5l7.5 3v5c0 4.6-3.1 8.4-7.5 10.5C7.6 18.9 4.5 15.1 4.5 10.5v-5z"/><circle cx="12" cy="11" r="3.2"/></svg>';

  /* ---------------- card rendering ---------------- */
  function refreshInstance(inst) {
    var root = inst.ctx.el;
    if (!root) return;
    root.innerHTML = "";

    var gear = el("button", "ph-gear", GEAR);
    gear.title = tr("pihole.settings", "Pi-hole settings");
    gear.setAttribute("aria-label", tr("pihole.settings", "Pi-hole settings"));
    gear.addEventListener("click", function () { openSettings(); });

    if (!cfg.base) {
      var setup = el("div", "ph-setup");
      setup.appendChild(el("div", "ph-setup-icon", SHIELD));
      setup.appendChild(el("div", "ph-setup-title", esc(tr("pihole.connectTitle", "Connect your Pi-hole"))));
      setup.appendChild(el("div", "ph-setup-hint", esc(tr("pihole.connectHint",
        "Enter your Pi-hole address and password to show blocking stats."))));
      var b = el("button", "ph-btn", esc(tr("pihole.connect", "Connect")));
      b.addEventListener("click", function () { openSettings(); });
      setup.appendChild(b);
      root.appendChild(setup);
      root.appendChild(gear);
      return;
    }

    var wrap = el("div", "ph");
    wrap.innerHTML =
      '<div class="ph-hero">' +
        '<div class="ph-pct" data-pct>--</div>' +
        '<div class="ph-pct-lbl">' + esc(tr("pihole.blocked", "queries blocked")) + "</div>" +
      "</div>" +
      '<div class="ph-bar"><div class="ph-bar-fill" data-bar></div></div>' +
      '<div class="ph-stats">' +
        '<div class="ph-stat"><span class="ph-k">' + esc(tr("pihole.queries", "Queries today")) + '</span><span class="ph-v" data-queries>--</span></div>' +
        '<div class="ph-stat"><span class="ph-k">' + esc(tr("pihole.blockedToday", "Blocked today")) + '</span><span class="ph-v" data-blk>--</span></div>' +
        '<div class="ph-stat"><span class="ph-k">' + esc(tr("pihole.domains", "On blocklist")) + '</span><span class="ph-v" data-dom>--</span></div>' +
        '<div class="ph-stat"><span class="ph-k">' + esc(tr("pihole.clients", "Active clients")) + '</span><span class="ph-v" data-cli>--</span></div>' +
      "</div>" +
      '<div class="ph-foot"><span class="ph-msg" data-msg></span><span class="ph-credit">Pi-hole</span></div>';
    root.appendChild(wrap);
    root.appendChild(gear);

    load(inst);
  }

  function load(inst) {
    var root = inst.ctx.el;
    if (!root || !cfg.base) return;
    var q = function (s) { return root.querySelector(s); };
    inst.ctx.api("/api/widget/pihole?base=" + encodeURIComponent(cfg.base) + "&key=" + encodeURIComponent(cfg.key || ""))
      .then(function (d) {
        if (!root.isConnected) return;
        if (!d || !d.ok) { showErr(root, d && d.error); return; }
        var pct = Math.round((d.percentBlocked || 0) * 10) / 10;
        if (q("[data-pct]")) q("[data-pct]").textContent = pct + "%";
        if (q("[data-bar]")) q("[data-bar]").style.width = Math.max(0, Math.min(100, d.percentBlocked || 0)) + "%";
        if (q("[data-queries]")) q("[data-queries]").textContent = num(d.queriesToday);
        if (q("[data-blk]")) q("[data-blk]").textContent = num(d.blockedToday);
        if (q("[data-dom]")) q("[data-dom]").textContent = num(d.domainsOnList);
        if (q("[data-cli]")) q("[data-cli]").textContent = d.clientsActive ? num(d.clientsActive) : "--";
        if (q("[data-msg]")) q("[data-msg]").textContent = "";
      })
      .catch(function () { if (root.isConnected) showErr(root, "unreachable"); });
  }

  function showErr(root, code) {
    var msg = root.querySelector("[data-msg]");
    if (!msg) return;
    if (code === "auth") msg.textContent = tr("pihole.errAuth", "Login failed — check the password");
    else if (code === "bad_url" || code === "no_url") msg.textContent = tr("pihole.errUrl", "Bad Pi-hole address");
    else msg.textContent = tr("pihole.errReach", "Can't reach Pi-hole");
  }

  function refreshAll() { instances.forEach(refreshInstance); }

  /* ---------------- settings overlay ---------------- */
  var activeInput = null;

  function openSettings() {
    closeSettings();
    overlay = el("div", "ph-overlay");
    ["pointerdown", "pointermove", "pointerup", "touchstart", "touchmove", "touchend", "wheel"]
      .forEach(function (ev) { overlay.addEventListener(ev, function (e) { e.stopPropagation(); }, { passive: true }); });

    var close = el("button", "ph-ov-close", CLOSE);
    close.title = tr("pihole.close", "Close");
    close.setAttribute("aria-label", tr("pihole.close", "Close"));
    close.addEventListener("click", closeSettings);
    overlay.appendChild(close);

    var panel = el("div", "ph-panel");
    panel.appendChild(el("div", "ph-ov-title", esc(tr("pihole.settings", "Pi-hole settings"))));
    panel.appendChild(el("p", "ph-hint", esc(tr("pihole.addrHint",
      "Your Pi-hole's address (e.g. 192.168.0.2 or http://pi.hole) and admin password."))));

    panel.appendChild(el("label", "ph-field-label", esc(tr("pihole.address", "Pi-hole address"))));
    var urlRow = el("div", "ph-url-row");
    var prefix = el("button", "ph-url-prefix", "http://");
    prefix.textContent = (cfg.base || "").toLowerCase().indexOf("https://") === 0 ? "https://" : "http://";
    prefix.title = tr("pihole.toggleScheme", "Toggle http/https");
    prefix.addEventListener("click", function () { prefix.textContent = prefix.textContent === "http://" ? "https://" : "http://"; });
    var urlIn = el("input", "ph-input ph-url-input");
    urlIn.type = "text"; urlIn.inputMode = "none"; urlIn.spellcheck = false;
    urlIn.placeholder = "192.168.0.2";
    urlIn.value = stripScheme(cfg.base);
    wireField(urlIn);
    urlRow.appendChild(prefix); urlRow.appendChild(urlIn);
    panel.appendChild(urlRow);

    panel.appendChild(el("label", "ph-field-label", esc(tr("pihole.password", "Password"))));
    var pwIn = el("input", "ph-input");
    pwIn.type = "password"; pwIn.inputMode = "none"; pwIn.spellcheck = false;
    pwIn.placeholder = tr("pihole.passwordPh", "Pi-hole password (or API token)");
    pwIn.value = cfg.key || "";
    wireField(pwIn);
    panel.appendChild(pwIn);

    var pad = buildPad();
    panel.appendChild(pad);

    var status = el("div", "ph-test-status");
    var btns = el("div", "ph-btn-row");
    var test = el("button", "ph-btn ph-btn-ghost", esc(tr("pihole.test", "Test")));
    test.addEventListener("click", function () {
      var base = stripScheme(urlIn.value);
      if (!base) { status.textContent = tr("pihole.errUrl", "Bad Pi-hole address"); return; }
      status.textContent = tr("pihole.testing", "Testing…");
      var url = "/api/widget/pihole?base=" + encodeURIComponent(prefix.textContent + base) + "&key=" + encodeURIComponent(pwIn.value || "");
      (ctxRef && ctxRef.api ? ctxRef.api(url) : fetch(url).then(function (r) { return r.json(); }))
        .then(function (d) {
          if (d && d.ok) status.textContent = tr("pihole.testOk", "Connected — ") + (Math.round((d.percentBlocked || 0) * 10) / 10) + "% " + tr("pihole.blockedShort", "blocked");
          else if (d && d.error === "auth") status.textContent = tr("pihole.errAuth", "Login failed — check the password");
          else status.textContent = tr("pihole.errReach", "Can't reach Pi-hole");
        })
        .catch(function () { status.textContent = tr("pihole.errReach", "Can't reach Pi-hole"); });
    });
    var save = el("button", "ph-btn", esc(tr("pihole.save", "Save")));
    save.addEventListener("click", function () {
      var base = stripScheme(urlIn.value);
      cfg.base = base ? prefix.textContent + base : "";
      cfg.key = pwIn.value || "";
      saveCfg();
      closeSettings();
      refreshAll();
    });
    btns.appendChild(test); btns.appendChild(save);
    panel.appendChild(btns);
    panel.appendChild(status);

    overlay.appendChild(panel);
    document.body.appendChild(overlay);
  }

  function wireField(input) {
    input.addEventListener("focus", function () {
      activeInput = input;
      if (window.Michka && Michka.osk) Michka.osk("text", input, function () { input.dispatchEvent(new Event("input")); });
    });
  }

  // Symbol/number pad — the letter OSK has no digits or . : / which addresses/passwords often need.
  function buildPad() {
    var pad = el("div", "ph-pad");
    ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", ".", ":", "/", "-", "_", "⌫", "✕"].forEach(function (k) {
      var b = el("button", "ph-pad-key", k);
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
  Michka.widget("pihole", {
    render: function (ctx) { ctxRef = ctx; return '<div class="ph-root"></div>'; },
    mount: function (ctx) {
      ctxRef = ctx;
      loadCfg();                       // pull this host's saved Pi-hole config now ctx is available
      var inst = { ctx: ctx };
      instances.push(inst);
      refreshInstance(inst);
      inst._timer = setInterval(function () { load(inst); }, REFRESH_MS);
      ctx._phInst = inst;
    },
    unmount: function (ctx) {
      var inst = ctx._phInst;
      if (inst) {
        if (inst._timer) clearInterval(inst._timer);
        var i = instances.indexOf(inst); if (i >= 0) instances.splice(i, 1);
      }
      if (!instances.length) closeSettings();
    }
  });
})();

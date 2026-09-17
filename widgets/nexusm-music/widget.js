/* NexusM Music — a full-screen music & music-video player for a NexusM media server.
 *
 * Unlike a normal widget, this one doesn't render its content inline on the host page. Its card is
 * just a launcher: tapping "Open player" opens a brand-new full-screen UI (appended to <body>) with
 * the cover/video on the left half and metadata + transport + a searchable browser on the right. A
 * semi-transparent close button tears it down and returns to the host page.
 *
 * Connection model: the kiosk talks DIRECTLY to NexusM (its CORS allows any
 * origin). The NexusM URL + a cached auth token live in localStorage ("michka.nexusm") — no hub
 * config. The one hub touch-point is GET /api/widget/nexusm/discover (same origin), which UDP-probes
 * the LAN for the server (browsers can't broadcast). Auth auto-detects: POST /api/auth/token with an
 * empty body returns an admin token when the server has no PIN; otherwise it 400/401s and we prompt
 * for username + PIN. Kiosk helpers used: window.Michka.osk() (on-screen keyboard) and
 * window.Michka.keepAwake() (suppress the screensaver while the player is open).
 */
(function () {
  "use strict";

  var cfg = { url: "", token: "", username: "admin" };   // loaded from the hub on mount (ctx.getConfig)
  var overlay = null;            // full-screen root, or null when closed
  var bodyEl = null;            // swappable content area under the close button
  var gearEl = null;            // settings gear (player only), lives on the overlay next to close
  var stageEl = null;            // left half (cover / video)
  var media = null;            // active <audio> or <video>
  var state = { tab: "music", q: "", page: 1, total: 0, items: [], idx: -1, kind: null };
  var lastWasShuffle = false;   // when true, auto-advance picks another random item on track end
  var ctxRef = null;            // last widget ctx (for esc/t helpers)

  /* ---------------- config (server-persisted via ctx, shared across browsers) ---------------- */
  function loadCfg() {
    try {
      var o = (ctxRef && ctxRef.getConfig) ? ctxRef.getConfig() : {};
      cfg = Object.assign({ url: "", token: "", username: "admin" }, o || {});
    } catch (e) { cfg = { url: "", token: "", username: "admin" }; }
    return cfg;
  }
  function saveCfg() { try { if (ctxRef && ctxRef.setConfig) ctxRef.setConfig(cfg); } catch (e) { /* ignore */ } }
  // NexusM on the LAN is plain HTTP, and the kiosk URL keypad has no letters, so the scheme is fixed
  // to http:// — these helpers strip any scheme/slashes to the bare host:port and rebuild the URL.
  function stripScheme(u) { return (u || "").trim().replace(/^https?:\/\//i, "").replace(/^\/+|\/+$/g, ""); }
  function buildUrl(hostPort) { var h = stripScheme(hostPort); return h ? "http://" + h : ""; }

  /* ---------------- helpers ---------------- */
  function esc(s) { return ctxRef && ctxRef.esc ? ctxRef.esc(s) : String(s == null ? "" : s); }
  function tr(k, d) { var v = ctxRef && ctxRef.t ? ctxRef.t(k) : k; return (v && v !== k) ? v : (d || k); }
  function el(tag, cls, html) { var e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }
  function fmtDur(sec) {
    sec = Math.max(0, Math.floor(+sec || 0));
    var m = Math.floor(sec / 60), s = sec % 60;
    return m + ":" + (s < 10 ? "0" : "") + s;
  }
  // fetch with a hard timeout (default 5s) so a dead/unreachable server fails fast instead of
  // hanging for minutes on the browser's default socket timeout.
  function fetchT(url, opts, ms) {
    opts = opts || {};
    var ctl = new AbortController();
    var to = setTimeout(function () { ctl.abort(); }, ms || 5000);
    opts.signal = ctl.signal;
    return fetch(url, opts).then(
      function (r) { clearTimeout(to); return r; },
      function (e) { clearTimeout(to); throw e; }
    );
  }
  // JSON fetch against NexusM; token via Authorization header. Throws {status} on HTTP error.
  function apiGet(path) {
    var h = {}; if (cfg.token) h.Authorization = "Bearer " + cfg.token;
    return fetchT(cfg.url + path, { headers: h }, 6000).then(function (r) {
      if (r.status === 401) { var e = new Error("unauthorized"); e.status = 401; throw e; }
      if (!r.ok) { var e2 = new Error("http " + r.status); e2.status = r.status; throw e2; }
      return r.json();
    });
  }
  // Media URL for <img>/<audio>/<video> — token must go in the query (elements can't set headers).
  function mediaUrl(path) {
    var u = cfg.url + path;
    if (cfg.token) u += (path.indexOf("?") >= 0 ? "&" : "?") + "token=" + encodeURIComponent(cfg.token);
    return u;
  }

  var SVG = {
    play: '<svg viewBox="0 0 24 24" width="34" height="34" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>',
    pause: '<svg viewBox="0 0 24 24" width="34" height="34" fill="currentColor"><path d="M6 5h4v14H6zm8 0h4v14h-4z"/></svg>',
    prev: '<svg viewBox="0 0 24 24" width="26" height="26" fill="currentColor"><path d="M6 6h2v12H6zm3.5 6 8.5 6V6z"/></svg>',
    next: '<svg viewBox="0 0 24 24" width="26" height="26" fill="currentColor"><path d="M16 6h2v12h-2zM6 6l8.5 6L6 18z"/></svg>',
    shuffle: '<svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M16 3h5v5"/><path d="M4 20 21 3"/><path d="M21 16v5h-5"/><path d="M15 15l6 6"/><path d="M4 4l5 5"/></svg>',
    note: '<svg viewBox="0 0 24 24" width="64" height="64" fill="currentColor" opacity="0.5"><path d="M12 3v10.55A4 4 0 1 0 14 17V7h4V3z"/></svg>',
    close: '<svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>',
    search: '<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>',
    gear: '<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>'
  };

  /* ---------------- overlay shell ---------------- */
  function openPlayer() {
    if (overlay) return;
    overlay = el("div", "nxm-overlay");
    // Keep the dashboard's swipe/drag handlers from reacting behind the player.
    ["pointerdown", "pointermove", "pointerup", "touchstart", "touchmove", "touchend", "wheel"]
      .forEach(function (ev) { overlay.addEventListener(ev, function (e) { e.stopPropagation(); }, { passive: true }); });

    var close = el("button", "nxm-close", SVG.close);
    close.title = tr("nxm.close", "Close");
    close.setAttribute("aria-label", tr("nxm.close", "Close"));
    close.addEventListener("click", closePlayer);
    overlay.appendChild(close);

    bodyEl = el("div", "nxm-body");
    overlay.appendChild(bodyEl);
    document.body.appendChild(overlay);

    if (window.Michka && Michka.keepAwake) Michka.keepAwake(true);   // don't let the saver cover us
    ensureConnected();
  }

  function closePlayer() {
    if (!overlay) return;
    stopMedia();
    if (window.Michka && Michka.hideOsk) Michka.hideOsk();
    if (window.Michka && Michka.keepAwake) Michka.keepAwake(false);
    overlay.remove();
    overlay = bodyEl = stageEl = gearEl = null;
  }

  // The settings gear lives on the overlay (beside the close button), so it must be torn down
  // explicitly when leaving the player for the connect/login/loading screens.
  function removeGear() { if (gearEl) { gearEl.remove(); gearEl = null; } }

  function stopMedia() {
    if (media) { try { media.pause(); media.removeAttribute("src"); media.load(); } catch (e) { /* ignore */ } media = null; }
  }

  // Decide what to show: setup, login, or the player.
  function ensureConnected() {
    if (!cfg.url) return showConnect();
    if (!cfg.token) return authProbe();
    showPlayer();
  }

  /* ---------------- connect panel (server URL) ----------------
     canCancel = true when reached from the player's settings gear (an existing, working connection to
     fall back to), so we offer a Cancel that returns to the player without changing anything. */
  function showConnect(msg, canCancel) {
    stopMedia();
    removeGear();
    bodyEl.innerHTML = "";
    // Two-column layout so everything fits the short panel (1280x400 / 1424x280): info + buttons on
    // the left, the URL keypad on the right. Nothing is pushed off the bottom of the screen.
    var card = el("div", "nxm-panel connect");
    card.appendChild(el("h2", "nxm-panel-title", esc(tr("nxm.connectTitle", "Connect to NexusM"))));

    var cols = el("div", "nxm-connect");
    var left = el("div", "nxm-connect-left");

    left.appendChild(el("p", "nxm-panel-hint", esc(tr("nxm.connectHint", "Enter your NexusM server address (IP:port), or auto-discover it."))));

    // URL field: a FIXED, undeletable "http://" prefix (NexusM is plain HTTP, and the keypad has no
    // letters) + the editable host:port, + a Clear button to wipe it and start a fresh address.
    var urlRow = el("div", "nxm-url-row");
    urlRow.appendChild(el("span", "nxm-url-prefix", "http://"));
    var input = el("input", "nxm-input nxm-url-input");
    input.type = "text"; input.inputMode = "none"; input.spellcheck = false;
    input.placeholder = "192.168.0.168:8182";
    input.value = stripScheme(cfg.url);
    urlRow.appendChild(input);
    var clr = el("button", "nxm-url-clear", "✕");
    clr.title = tr("nxm.clear", "Clear"); clr.setAttribute("aria-label", tr("nxm.clear", "Clear"));
    clr.addEventListener("click", function () { input.value = ""; input.focus(); });
    urlRow.appendChild(clr);
    left.appendChild(urlRow);

    var err = el("div", "nxm-err"); if (msg) err.textContent = msg;

    // Discovered-servers list: every result is a tappable row (so a multi-server LAN — e.g. a real
    // host plus a 127.0.0.1 loopback instance — lets you choose the right one).
    var results = el("div", "nxm-results");
    function showResults(list) {
      results.innerHTML = "";
      if (!list || !list.length) return;
      results.appendChild(el("div", "nxm-results-label",
        esc(list.length + " " + tr("nxm.found", "found") + " · " + tr("nxm.tapToUse", "tap to use"))));
      list.forEach(function (srv) {
        var b = el("button", "nxm-result", esc(srv));
        b.addEventListener("click", function () {
          input.value = stripScheme(srv); err.textContent = "";
          results.querySelectorAll(".nxm-result").forEach(function (x) { x.classList.remove("on"); });
          b.classList.add("on");
        });
        results.appendChild(b);
      });
    }

    var row = el("div", "nxm-panel-actions");
    var disc = el("button", "nxm-btn ghost", esc(tr("nxm.discover", "Auto-discover")));
    disc.addEventListener("click", function () {
      disc.disabled = true; disc.textContent = tr("nxm.searching", "Searching…");
      err.textContent = ""; showResults([]);
      fetchT("/api/widget/nexusm/discover", {}, 6000).then(function (r) { return r.json(); }).then(function (j) {
        var s = (j && j.servers) || [];
        disc.disabled = false; disc.textContent = tr("nxm.discover", "Auto-discover");
        if (!s.length) { err.textContent = tr("nxm.noServers", "No NexusM server found on the network."); return; }
        if (s.length === 1) { input.value = stripScheme(s[0]); }     // single hit: just fill it in
        showResults(s);                                              // always list so you can pick/confirm
      }).catch(function () {
        disc.disabled = false; disc.textContent = tr("nxm.discover", "Auto-discover");
        err.textContent = tr("nxm.discoverFail", "Discovery failed.");
      });
    });
    var go = el("button", "nxm-btn", esc(tr("nxm.connect", "Connect")));
    go.addEventListener("click", function () {
      var u = buildUrl(input.value);
      if (!u) { err.textContent = tr("nxm.needUrl", "Enter a server address."); return; }
      cfg.url = u; cfg.token = ""; saveCfg();
      if (window.Michka && Michka.hideOsk) Michka.hideOsk();
      authProbe();
    });
    row.appendChild(disc); row.appendChild(go);
    // Reached from the player's gear: let the user back out without re-pointing the server.
    if (canCancel && cfg.url && cfg.token) {
      var cancel = el("button", "nxm-btn ghost", esc(tr("nxm.cancel", "Cancel")));
      cancel.addEventListener("click", function () {
        if (window.Michka && Michka.hideOsk) Michka.hideOsk();
        showPlayer();
      });
      row.appendChild(cancel);
    }
    left.appendChild(row);
    left.appendChild(err);
    left.appendChild(results);

    cols.appendChild(left);
    cols.appendChild(buildUrlPad(input));   // keypad on the right (the kiosk keyboard has no "." / ":")
    card.appendChild(cols);
    bodyEl.appendChild(card);
  }

  // Large digit + "." ":" keypad that edits the host:port field (no "/" — the http:// prefix is fixed).
  function buildUrlPad(input) {
    var pad = el("div", "nxm-pad");
    var keys = ["1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "0", ":", "⌫", "✕"];
    keys.forEach(function (k) {
      var b = el("button", "nxm-pad-key", k);
      b.addEventListener("click", function () {
        if (k === "⌫") input.value = input.value.slice(0, -1);
        else if (k === "✕") input.value = "";
        else input.value += k;
      });
      pad.appendChild(b);
    });
    return pad;
  }

  // Numeric keypad for the PIN: digits + backspace/clear, capped at maxLen so it can't exceed 6.
  function buildNumPad(input, maxLen) {
    var pad = el("div", "nxm-pad nxm-pad-num");
    var keys = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "⌫", "0", "✕"];
    keys.forEach(function (k) {
      var b = el("button", "nxm-pad-key", k);
      b.addEventListener("click", function () {
        if (k === "⌫") input.value = input.value.slice(0, -1);
        else if (k === "✕") input.value = "";
        else if (!maxLen || input.value.length < maxLen) input.value += k;
      });
      pad.appendChild(b);
    });
    return pad;
  }

  /* ---------------- auth ---------------- */
  function authProbe() {
    removeGear();
    bodyEl.innerHTML = "";
    bodyEl.appendChild(el("div", "nxm-loading", esc(tr("nxm.connecting", "Connecting…"))));
    fetchT(cfg.url + "/api/auth/token", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: "{}"
    }, 5000).then(function (r) {
      if (r.ok) return r.json().then(function (j) { cfg.token = j.token || ""; saveCfg(); showPlayer(); });
      if (r.status === 400 || r.status === 401) { showLogin(); return; }
      showConnect(tr("nxm.serverErr", "The server returned an error.") + " (" + r.status + ")");
    }).catch(function (e) {
      // AbortError = our 5s timeout; anything else = network/DNS/refused. Either way, back to setup.
      showConnect(tr("nxm.noReach", "Couldn't reach the server at ") + cfg.url + ". " +
        tr("nxm.checkAddr", "Check the address and that NexusM is running."));
    });
  }

  function showLogin(msg) {
    removeGear();
    if (window.Michka && Michka.hideOsk) Michka.hideOsk();   // PIN uses an inline keypad, not the OSK
    bodyEl.innerHTML = "";
    // Two-column like the connect panel: fields on the left, an inline PIN keypad on the right, so the
    // PIN field stays visible (a bottom on-screen keyboard would cover it) and nothing is pushed off-screen.
    var card = el("div", "nxm-panel connect");
    card.appendChild(el("h2", "nxm-panel-title", esc(tr("nxm.loginTitle", "Sign in to NexusM"))));

    var cols = el("div", "nxm-connect");
    var left = el("div", "nxm-connect-left");
    left.appendChild(el("p", "nxm-panel-hint", esc(tr("nxm.loginHint", "This server requires a username and PIN."))));

    left.appendChild(el("label", "nxm-field-label", esc(tr("nxm.username", "Username"))));
    var user = el("input", "nxm-input"); user.type = "text"; user.inputMode = "none"; user.value = cfg.username || "admin";
    user.addEventListener("focus", function () { if (window.Michka && Michka.osk) Michka.osk("text", user); });
    left.appendChild(user);

    // PIN is masked (a real password field → dots, not the plain digits) and capped at 6 by the keypad.
    left.appendChild(el("label", "nxm-field-label", esc(tr("nxm.pin", "PIN")) + " (6)"));
    var pin = el("input", "nxm-input nxm-pin"); pin.type = "password"; pin.inputMode = "none";
    pin.maxLength = 6; pin.readOnly = true; pin.value = "";
    pin.setAttribute("autocomplete", "off"); pin.setAttribute("aria-label", tr("nxm.pin", "PIN"));
    left.appendChild(pin);

    var err = el("div", "nxm-err"); if (msg) err.textContent = msg;

    var row = el("div", "nxm-panel-actions");
    var go = el("button", "nxm-btn", esc(tr("nxm.signin", "Sign in")));
    go.addEventListener("click", function () {
      cfg.username = (user.value || "").trim() || "admin";
      go.disabled = true; err.textContent = "";
      fetchT(cfg.url + "/api/auth/token", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username: cfg.username, pin: pin.value })
      }, 5000).then(function (r) {
        go.disabled = false;
        if (r.ok) return r.json().then(function (j) {
          cfg.token = j.token || ""; saveCfg();
          if (window.Michka && Michka.hideOsk) Michka.hideOsk();
          showPlayer();
        });
        err.textContent = tr("nxm.badPin", "Wrong username or PIN.");
      }).catch(function () { go.disabled = false; err.textContent = tr("nxm.noReach", "Couldn't reach the server."); });
    });
    // Back: always available so you're never stuck on the PIN screen — returns to the connect screen
    // (server list / address) to pick a different server or fix the address.
    var back = el("button", "nxm-btn ghost", esc(tr("nxm.back", "Back")));
    back.addEventListener("click", function () {
      if (window.Michka && Michka.hideOsk) Michka.hideOsk();
      showConnect(null, !!(cfg.url && cfg.token));
    });
    row.appendChild(go); row.appendChild(back);
    left.appendChild(row);
    left.appendChild(err);

    cols.appendChild(left);
    cols.appendChild(buildNumPad(pin, 6));   // inline 6-digit keypad (no OSK to cover the field)
    card.appendChild(cols);
    bodyEl.appendChild(card);
  }

  // Any 401 mid-session: drop the token and re-auth.
  function onUnauthorized() { cfg.token = ""; saveCfg(); if (overlay) authProbe(); }

  /* ---------------- player ---------------- */
  function showPlayer() {
    bodyEl.innerHTML = "";
    var wrap = el("div", "nxm-player");

    // Left: media stage.
    stageEl = el("div", "nxm-stage");
    stageEl.innerHTML = '<div class="nxm-stage-empty">' + SVG.note + "</div>";
    wrap.appendChild(stageEl);

    // Right: now-playing + transport + browser.
    var side = el("div", "nxm-side");

    var now = el("div", "nxm-now");
    now.innerHTML =
      '<div class="nxm-now-title" data-title>' + esc(tr("nxm.nothing", "Nothing playing")) + "</div>" +
      '<div class="nxm-now-artist" data-artist></div>' +
      '<div class="nxm-now-album" data-album></div>';
    side.appendChild(now);

    // Settings gear: reopen the connect panel to change the server / re-run auto-discovery. It lives
    // on the overlay (top-right, just left of the close button) so it's always visible and never
    // collides with the metadata or the close button.
    removeGear();
    gearEl = el("button", "nxm-gear", SVG.gear);
    gearEl.title = tr("nxm.serverSettings", "Server settings");
    gearEl.setAttribute("aria-label", tr("nxm.serverSettings", "Server settings"));
    gearEl.addEventListener("click", function () { showConnect(null, true); });
    overlay.appendChild(gearEl);

    var trans = el("div", "nxm-transport");
    trans.innerHTML =
      '<button class="nxm-tbtn" data-prev>' + SVG.prev + "</button>" +
      '<button class="nxm-tbtn big" data-play>' + SVG.play + "</button>" +
      '<button class="nxm-tbtn" data-next>' + SVG.next + "</button>" +
      '<button class="nxm-tbtn" data-shuffle title="' + esc(tr("nxm.shuffle", "Shuffle")) +
        '" aria-label="' + esc(tr("nxm.shuffle", "Shuffle")) + '">' + SVG.shuffle + "</button>";
    side.appendChild(trans);

    var seekRow = el("div", "nxm-seek");
    seekRow.innerHTML =
      '<span class="nxm-time" data-cur>0:00</span>' +
      '<input class="nxm-range" type="range" min="0" max="1000" value="0" data-seek>' +
      '<span class="nxm-time" data-dur>0:00</span>';
    side.appendChild(seekRow);

    // Browser: tabs + search + list + pager.
    var browse = el("div", "nxm-browse");
    var tabs = el("div", "nxm-tabs");
    var tabMusic = el("button", "nxm-tab" + (state.tab === "music" ? " on" : ""), esc(tr("nxm.music", "Music")));
    var tabMv = el("button", "nxm-tab" + (state.tab === "mv" ? " on" : ""), esc(tr("nxm.videos", "Music Videos")));
    tabMusic.addEventListener("click", function () { setTab("music"); });
    tabMv.addEventListener("click", function () { setTab("mv"); });
    tabs.appendChild(tabMusic); tabs.appendChild(tabMv);
    browse.appendChild(tabs);

    var searchRow = el("div", "nxm-searchrow");
    var sIcon = el("span", "nxm-search-icon", SVG.search);
    var search = el("input", "nxm-input nxm-search");
    search.type = "text"; search.inputMode = "none"; search.placeholder = tr("nxm.searchPh", "Search…"); search.value = state.q;
    search.addEventListener("focus", function () {
      if (window.Michka && Michka.osk) Michka.osk("text", search, function () { onSearch(search.value); });
    });
    search.addEventListener("input", function () { onSearch(search.value); });
    searchRow.appendChild(sIcon); searchRow.appendChild(search);
    browse.appendChild(searchRow);

    var list = el("div", "nxm-list"); list.setAttribute("data-list", ""); browse.appendChild(list);

    var pager = el("div", "nxm-pager");
    pager.innerHTML =
      '<button class="nxm-pbtn" data-pprev>' + esc(tr("nxm.prev", "Prev")) + "</button>" +
      '<span class="nxm-pinfo" data-pinfo></span>' +
      '<button class="nxm-pbtn" data-pnext>' + esc(tr("nxm.next", "Next")) + "</button>";
    browse.appendChild(pager);
    side.appendChild(browse);

    wrap.appendChild(side);
    bodyEl.appendChild(wrap);

    // Wire transport.
    trans.querySelector("[data-play]").addEventListener("click", togglePlay);
    trans.querySelector("[data-prev]").addEventListener("click", function () { skip(-1); });
    trans.querySelector("[data-next]").addEventListener("click", function () { skip(1); });
    trans.querySelector("[data-shuffle]").addEventListener("click", shuffle);
    var seek = seekRow.querySelector("[data-seek]");
    seek.addEventListener("input", function () {
      if (media && media.duration) media.currentTime = (seek.value / 1000) * media.duration;
    });
    pager.querySelector("[data-pprev]").addEventListener("click", function () { if (state.page > 1) { state.page--; loadList(); } });
    pager.querySelector("[data-pnext]").addEventListener("click", function () { if (state.page * PAGE < state.total) { state.page++; loadList(); } });

    loadList();
  }

  var PAGE = 30;
  var searchT = null;
  function onSearch(q) {
    state.q = q; state.page = 1;
    clearTimeout(searchT);
    searchT = setTimeout(loadList, 250);
  }
  function setTab(tab) {
    if (state.tab === tab) return;
    state.tab = tab; state.q = ""; state.page = 1;
    var s = bodyEl.querySelector(".nxm-search"); if (s) s.value = "";
    bodyEl.querySelectorAll(".nxm-tab").forEach(function (b, i) { b.classList.toggle("on", (i === 0) === (tab === "music")); });
    loadList();
  }

  function loadList() {
    var listEl = bodyEl && bodyEl.querySelector("[data-list]");
    if (!listEl) return;
    listEl.innerHTML = '<div class="nxm-list-msg">' + esc(tr("nxm.loading", "Loading…")) + "</div>";
    var q = encodeURIComponent(state.q || "");
    var path = state.tab === "music"
      ? "/api/tracks?sort=title&page=" + state.page + "&limit=" + PAGE + (q ? "&search=" + q : "")
      : "/api/musicvideos?sort=title&page=" + state.page + "&limit=" + PAGE + (q ? "&search=" + q : "");
    var kind = state.tab;
    apiGet(path).then(function (j) {
      if (state.tab !== kind) return;                 // user switched tabs meanwhile
      state.items = (kind === "music" ? j.tracks : j.videos) || [];
      state.total = j.total || state.items.length;
      renderList();
    }).catch(function (e) {
      if (e && e.status === 401) { onUnauthorized(); return; }
      listEl.innerHTML = '<div class="nxm-list-msg">' + esc(tr("nxm.loadFail", "Couldn't load the library.")) + "</div>";
    });
  }

  function renderList() {
    var listEl = bodyEl.querySelector("[data-list]");
    var info = bodyEl.querySelector("[data-pinfo]");
    if (!listEl) return;
    listEl.innerHTML = "";
    if (!state.items.length) {
      listEl.innerHTML = '<div class="nxm-list-msg">' + esc(tr("nxm.empty", "Nothing found.")) + "</div>";
    } else {
      state.items.forEach(function (it, i) {
        var title = it.title || it.Title || it.fileName || "—";
        var artist = it.artist || it.Artist || "";
        var dur = it.duration || it.Duration || 0;
        var row = el("button", "nxm-row");
        row.innerHTML =
          '<span class="nxm-row-main"><span class="nxm-row-title">' + esc(title) + "</span>" +
          '<span class="nxm-row-artist">' + esc(artist) + "</span></span>" +
          '<span class="nxm-row-dur">' + (dur ? fmtDur(dur) : "") + "</span>";
        row.addEventListener("click", function () { playItem(i); });
        listEl.appendChild(row);
      });
    }
    if (info) {
      var pages = Math.max(1, Math.ceil(state.total / PAGE));
      info.textContent = tr("nxm.page", "Page") + " " + state.page + " / " + pages;
    }
  }

  /* ---------------- playback ---------------- */
  // Play a row from the current list (tracks prev/next + highlights the row).
  function playItem(i) {
    var it = state.items[i]; if (!it) return;
    state.idx = i; lastWasShuffle = false;
    playEntry(it, state.tab);
    highlightRow(i);
  }

  // Pick + play a random track (music view) or random music video (mv view) from the whole library.
  function shuffle() {
    var kind = state.tab;
    var ep = kind === "music" ? "/api/tracks/random" : "/api/musicvideos/random";
    apiGet(ep).then(function (it) {
      // The musicvideos/random endpoint returns only { id } — fetch the full record for metadata.
      if (kind === "mv" && it && (it.id || it.Id) && !(it.title || it.Title)) {
        return apiGet("/api/musicvideos/" + (it.id || it.Id)).then(function (full) { return full || it; });
      }
      return it;
    }).then(function (it) {
      if (!it) return;
      state.idx = -1; lastWasShuffle = true;   // not a list row; keep shuffling on track end
      highlightRow(-1);
      playEntry(it, kind);
    }).catch(function (e) { if (e && e.status === 401) onUnauthorized(); });
  }

  // Load + play a single track/video object into the stage. kind = "music" | "mv".
  function playEntry(it, kind) {
    state.kind = kind;
    var id = it.id || it.Id;
    var title = it.title || it.Title || it.fileName || "—";
    var artist = it.artist || it.Artist || "";
    var album = it.album || it.Album || "";
    var year = it.year || it.Year || "";

    bodyEl.querySelector("[data-title]").textContent = title;
    bodyEl.querySelector("[data-artist]").textContent = artist;
    bodyEl.querySelector("[data-album]").textContent = [album, year].filter(Boolean).join(" · ");

    stopMedia();
    stageEl.innerHTML = "";

    if (state.kind === "mv") {
      var v = document.createElement("video");
      v.className = "nxm-video"; v.autoplay = true; v.playsInline = true; v.src = mediaUrl("/api/stream-musicvideo/" + id);
      stageEl.appendChild(v);
      media = v;
    } else {
      var art = el("div", "nxm-art");
      var img = document.createElement("img");
      img.className = "nxm-cover"; img.alt = "";
      img.onerror = function () { art.classList.add("noart"); art.innerHTML = SVG.note; };
      img.src = mediaUrl("/api/cover/track/" + id);
      art.appendChild(img);
      stageEl.appendChild(art);
      var a = document.createElement("audio");
      a.autoplay = true; a.src = mediaUrl("/api/stream/" + id);
      stageEl.appendChild(a);
      media = a;
    }
    wireMedia();
  }

  function wireMedia() {
    if (!media) return;
    var playBtn = bodyEl.querySelector("[data-play]");
    var seek = bodyEl.querySelector("[data-seek]");
    var curEl = bodyEl.querySelector("[data-cur]");
    var durEl = bodyEl.querySelector("[data-dur]");
    media.addEventListener("play", function () { if (playBtn) playBtn.innerHTML = SVG.pause; });
    media.addEventListener("pause", function () { if (playBtn) playBtn.innerHTML = SVG.play; });
    media.addEventListener("loadedmetadata", function () { if (durEl) durEl.textContent = fmtDur(media.duration); });
    media.addEventListener("timeupdate", function () {
      if (!media.duration) return;
      if (seek) seek.value = Math.round((media.currentTime / media.duration) * 1000);
      if (curEl) curEl.textContent = fmtDur(media.currentTime);
    });
    media.addEventListener("ended", function () { if (lastWasShuffle) shuffle(); else skip(1); });
    media.addEventListener("error", function () {
      if (stageEl) stageEl.innerHTML = '<div class="nxm-stage-empty">' + esc(tr("nxm.playFail", "Can't play this item.")) + "</div>";
    });
  }

  function togglePlay() { if (!media) { if (state.items.length) playItem(0); return; } if (media.paused) media.play(); else media.pause(); }
  function skip(d) {
    if (!state.items.length) return;
    var n = state.idx + d;
    if (n < 0 || n >= state.items.length) return;     // clamp within the current page
    playItem(n);
  }
  function highlightRow(i) {
    var rows = bodyEl.querySelectorAll(".nxm-row");
    rows.forEach(function (r, j) { r.classList.toggle("playing", j === i); });
  }

  /* ---------------- widget registration ---------------- */
  Michka.widget("nexusm-music", {
    render: function (ctx) {
      ctxRef = ctx;
      loadCfg();                       // pull saved server URL/token now ctx (config) is available
      var sub = cfg.url ? (tr("nxm.connectedTo", "Connected") + ": " + ctx.esc(cfg.url)) : ctx.esc(tr("nxm.tapSetup", "Tap to set up your NexusM server"));
      return '<div class="nxm-launch">' +
        '<div class="nxm-launch-icon">' + SVG.note + "</div>" +
        '<div class="nxm-launch-sub">' + sub + "</div>" +
        '<div class="nxm-launch-url">' + ctx.esc(tr("nxm.getServer", "Get your NexusM Server from https://nexusm.org")) + "</div>" +
        '<button class="nxm-btn nxm-launch-btn" data-open>' + ctx.esc(tr("nxm.open", "Open player")) + "</button>" +
        "</div>";
    },
    mount: function (ctx) {
      ctxRef = ctx;
      loadCfg();
      var btn = ctx.el && ctx.el.querySelector("[data-open]");
      if (btn) btn.addEventListener("click", openPlayer);
    },
    unmount: function () { closePlayer(); }
  });
})();

/* Weather — local weather for a town you pick.
 *
 * Data comes from Open-Meteo (https://open-meteo.com), which is free, needs no API key, and licenses
 * its data under CC BY 4.0 (attribution only) — so the card shows a "Weather by Open-Meteo.com" credit.
 * Two endpoints are used, both same-provider and CORS-friendly so the kiosk browser calls them directly:
 *   - geocoding-api.open-meteo.com/v1/search  → resolve a town name to coordinates. It returns a LIST
 *     with country + admin1 (state/region), so when several places share a name (Paris FR vs Paris TX
 *     vs Paris ON) the user picks the right one.
 *   - api.open-meteo.com/v1/forecast          → current conditions + a 5-day forecast for those coords.
 *
 * Networking: direct from the browser by default. If the kiosk can't reach the internet, the user can
 * set a URL-prefix proxy (host:port) in the widget's settings — requests become "<proxy>/<full target
 * URL>" (the cors-anywhere convention). Both the chosen town (per host) and the proxy live in
 * localStorage ("michka.weather"); there is no hub round-trip.
 *
 * Units follow the dashboard's global temperature setting (michka.conf tempUnit): °C + km/h, or °F +
 * mph. The widget reads app.js's top-level `settings`/`toF` by bare name (widget.js loads as a classic
 * script after app.js, same as UI templates). All colours are flat per the project's data-display rules.
 */
(function () {
  "use strict";

  var LS_KEY = "michka.weather";
  var GEO = "https://geocoding-api.open-meteo.com/v1/search";
  var FC = "https://api.open-meteo.com/v1/forecast";
  var REFRESH_MS = 15 * 60 * 1000;            // weather changes slowly; poll every 15 min

  var cfg = { proxy: { enabled: false, base: "" }, byHost: {} };  // loaded from the hub on mount
  var ctxRef = null;                          // last ctx (for esc/t/config)
  var instances = [];                         // live mounted cards: { ctx, refresh }
  var overlay = null;                         // settings takeover, or null

  /* ---------------- config (server-persisted via ctx, shared across browsers; byHost keeps per-host towns) ---------------- */
  function loadCfg() {
    var d = { proxy: { enabled: false, base: "" }, byHost: {} };
    try {
      var o = (ctxRef && ctxRef.getConfig) ? ctxRef.getConfig() : {};
      if (o && o.proxy) d.proxy = { enabled: !!o.proxy.enabled, base: o.proxy.base || "" };
      if (o && o.byHost) d.byHost = o.byHost;
    } catch (e) { /* defaults */ }
    cfg = d;
    return d;
  }
  function saveCfg() { try { if (ctxRef && ctxRef.setConfig) ctxRef.setConfig(cfg); } catch (e) { /* ignore */ } }
  function locFor(host) { return cfg.byHost[host] || null; }

  /* ---------------- helpers ---------------- */
  function esc(s) { return ctxRef && ctxRef.esc ? ctxRef.esc(s) : String(s == null ? "" : s); }
  function tr(k, d) { var v = ctxRef && ctxRef.t ? ctxRef.t(k) : k; return (v && v !== k) ? v : (d || k); }
  function el(tag, cls, html) { var e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }
  function tempUnitF() { try { return typeof settings !== "undefined" && settings.tempUnit === "F"; } catch (e) { return false; } }
  function lang() { try { return (typeof settings !== "undefined" && settings.lang) || "en"; } catch (e) { return "en"; } }

  // fetch with a hard timeout so a dead network fails fast instead of hanging on the socket default.
  function fetchT(url, ms) {
    var ctl = new AbortController();
    var to = setTimeout(function () { ctl.abort(); }, ms || 7000);
    return fetch(url, { signal: ctl.signal }).then(
      function (r) { clearTimeout(to); return r; },
      function (e) { clearTimeout(to); throw e; }
    );
  }
  // Route through the user's proxy when enabled: "<proxyBase>/<full target URL>" (cors-anywhere style).
  function proxify(url) {
    if (cfg.proxy.enabled && cfg.proxy.base) return cfg.proxy.base.replace(/\/+$/, "") + "/" + url;
    return url;
  }
  function getJson(url, ms) {
    return fetchT(proxify(url), ms).then(function (r) {
      if (!r.ok) throw new Error("http " + r.status);
      return r.json();
    });
  }
  function stripScheme(u) { return (u || "").trim().replace(/^https?:\/\//i, "").replace(/^\/+|\/+$/g, ""); }

  /* ---------------- WMO weather codes ---------------- */
  // Map a WMO code to a label group + an icon kind. (https://open-meteo.com/en/docs → weather_code)
  function codeInfo(code) {
    var c = +code;
    if (c === 0) return { key: "weather.wmo.clear", def: "Clear sky", icon: "sun" };
    if (c === 1) return { key: "weather.wmo.mainlyClear", def: "Mainly clear", icon: "sun" };
    if (c === 2) return { key: "weather.wmo.partlyCloudy", def: "Partly cloudy", icon: "partly" };
    if (c === 3) return { key: "weather.wmo.overcast", def: "Overcast", icon: "cloud" };
    if (c === 45 || c === 48) return { key: "weather.wmo.fog", def: "Fog", icon: "fog" };
    if (c >= 51 && c <= 55) return { key: "weather.wmo.drizzle", def: "Drizzle", icon: "rain" };
    if (c === 56 || c === 57) return { key: "weather.wmo.freezingDrizzle", def: "Freezing drizzle", icon: "rain" };
    if (c >= 61 && c <= 65) return { key: "weather.wmo.rain", def: "Rain", icon: "rain" };
    if (c === 66 || c === 67) return { key: "weather.wmo.freezingRain", def: "Freezing rain", icon: "rain" };
    if (c >= 71 && c <= 75) return { key: "weather.wmo.snow", def: "Snow", icon: "snow" };
    if (c === 77) return { key: "weather.wmo.snowGrains", def: "Snow grains", icon: "snow" };
    if (c >= 80 && c <= 82) return { key: "weather.wmo.showers", def: "Rain showers", icon: "rain" };
    if (c === 85 || c === 86) return { key: "weather.wmo.snowShowers", def: "Snow showers", icon: "snow" };
    if (c === 95) return { key: "weather.wmo.thunder", def: "Thunderstorm", icon: "thunder" };
    if (c === 96 || c === 99) return { key: "weather.wmo.thunderHail", def: "Thunderstorm with hail", icon: "thunder" };
    return { key: "weather.wmo.overcast", def: "Overcast", icon: "cloud" };
  }
  function codeLabel(code) { var i = codeInfo(code); return tr(i.key, i.def); }

  // Inline SVG icons (flat, currentColor). isDay tints clear sky; everything else is theme-neutral.
  function icon(kind, sz) {
    var s = sz || 22;
    var open = '<svg viewBox="0 0 24 24" width="' + s + '" height="' + s + '" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">';
    var cloud = '<path d="M7 18h10a3.5 3.5 0 0 0 .3-6.99A4.5 4.5 0 0 0 8.6 9.4 3.5 3.5 0 0 0 7 18z"/>';
    var sun = '<circle cx="12" cy="12" r="4"/><line x1="12" y1="3" x2="12" y2="5"/><line x1="12" y1="19" x2="12" y2="21"/><line x1="3" y1="12" x2="5" y2="12"/><line x1="19" y1="12" x2="21" y2="12"/><line x1="5.6" y1="5.6" x2="7" y2="7"/><line x1="17" y1="17" x2="18.4" y2="18.4"/><line x1="18.4" y1="5.6" x2="17" y2="7"/><line x1="7" y1="17" x2="5.6" y2="18.4"/>';
    switch (kind) {
      case "sun": return '<span class="wx-i sun">' + open + sun + "</svg></span>";
      case "partly": return '<span class="wx-i sun">' + open + '<circle cx="8" cy="8" r="3"/><line x1="8" y1="2.5" x2="8" y2="3.6"/><line x1="2.5" y1="8" x2="3.6" y2="8"/><line x1="4.2" y1="4.2" x2="5" y2="5"/><path d="M9 18h8a3 3 0 0 0 .2-5.99A3.8 3.8 0 0 0 10 10.5 3 3 0 0 0 9 18z"/></svg></span>';
      case "cloud": return '<span class="wx-i">' + open + cloud + "</svg></span>";
      case "fog": return '<span class="wx-i">' + open + cloud.replace("18h10", "15h10").replace("M7 18", "M7 15") + '<line x1="5" y1="19" x2="15" y2="19"/><line x1="8" y1="22" x2="18" y2="22"/></svg></span>';
      case "rain": return '<span class="wx-i rain">' + open + '<path d="M7 15h10a3.5 3.5 0 0 0 .3-6.99A4.5 4.5 0 0 0 8.6 6.4 3.5 3.5 0 0 0 7 15z"/><line x1="8" y1="18" x2="7" y2="21"/><line x1="12" y1="18" x2="11" y2="21"/><line x1="16" y1="18" x2="15" y2="21"/></svg></span>';
      case "snow": return '<span class="wx-i snow">' + open + '<path d="M7 14h10a3.5 3.5 0 0 0 .3-6.99A4.5 4.5 0 0 0 8.6 5.4 3.5 3.5 0 0 0 7 14z"/><line x1="8" y1="18" x2="8" y2="18.5"/><line x1="12" y1="19" x2="12" y2="19.5"/><line x1="16" y1="18" x2="16" y2="18.5"/><line x1="10" y1="21" x2="10" y2="21.5"/><line x1="14" y1="21" x2="14" y2="21.5"/></svg></span>';
      case "thunder": return '<span class="wx-i thunder">' + open + '<path d="M7 14h10a3.5 3.5 0 0 0 .3-6.99A4.5 4.5 0 0 0 8.6 5.4 3.5 3.5 0 0 0 7 14z"/><path d="M12 15l-2 4h3l-2 4" stroke-width="1.6"/></svg></span>';
      default: return '<span class="wx-i">' + open + cloud + "</svg></span>";
    }
  }
  var GEAR = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>';
  var CLOSE = '<svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>';

  /* ---------------- card rendering ---------------- */
  // Compact one-line label for a chosen location: "Paris, FR" (town + country code only — the box is
  // narrow, so the admin1/region is dropped to keep the town + country always visible on one line).
  function locLabel(loc) {
    return [loc.name, loc.country_code].filter(Boolean).join(", ");
  }

  // Build the whole card body for one instance based on the current state.
  function refreshInstance(inst) {
    var host = inst.ctx.host;
    var loc = locFor(host);
    var root = inst.ctx.el;
    if (!root) return;
    root.innerHTML = "";

    var gear = el("button", "wx-gear", GEAR);
    gear.title = tr("weather.settings", "Weather settings");
    gear.setAttribute("aria-label", tr("weather.settings", "Weather settings"));
    gear.addEventListener("click", function () { openSettings(host, loc ? "loc" : "loc"); });

    if (!loc) {
      var setup = el("div", "wx-setup");
      setup.appendChild(el("div", "wx-setup-icon", icon("partly", 40)));
      setup.appendChild(el("div", "wx-setup-title", esc(tr("weather.choose", "Choose a town"))));
      setup.appendChild(el("div", "wx-setup-hint", esc(tr("weather.chooseHint", "Pick the town to show weather for"))));
      var b = el("button", "wx-btn", esc(tr("weather.choose", "Choose a town")));
      b.addEventListener("click", function () { openSettings(host, "loc"); });
      setup.appendChild(b);
      root.appendChild(setup);
      root.appendChild(gear);
      return;
    }

    var wrap = el("div", "wx");
    wrap.innerHTML =
      '<div class="wx-place">' + esc(locLabel(loc)) + "</div>" +
      '<div class="wx-cur">' +
        '<div class="wx-cur-icon" data-cicon></div>' +
        '<div class="wx-cur-main">' +
          '<div class="wx-temp" data-temp>--&deg;</div>' +
          '<div class="wx-cond" data-cond>' + esc(tr("weather.loading", "Loading…")) + "</div>" +
        "</div>" +
      "</div>" +
      '<div class="wx-meta">' +
        '<div class="wx-meta-i"><span class="wx-k">' + esc(tr("weather.feelsLike", "Feels like")) + '</span><span class="wx-v" data-feels>--</span></div>' +
        '<div class="wx-meta-i"><span class="wx-k">' + esc(tr("weather.humidity", "Humidity")) + '</span><span class="wx-v" data-hum>--</span></div>' +
      "</div>" +
      '<div class="wx-days" data-days></div>' +
      '<div class="wx-foot"><span class="wx-msg" data-msg></span>' +
        '<span class="wx-credit">' + esc(tr("weather.credit", "Weather by Open-Meteo.com")) + "</span></div>";
    root.appendChild(wrap);
    root.appendChild(gear);

    loadWeather(inst, loc);
  }

  function loadWeather(inst, loc) {
    var root = inst.ctx.el;
    var q = function (s) { return root.querySelector(s); };
    var fUnit = tempUnitF();
    var url = FC + "?latitude=" + encodeURIComponent(loc.latitude) + "&longitude=" + encodeURIComponent(loc.longitude) +
      "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,is_day" +
      "&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=3&timezone=auto" +
      "&temperature_unit=" + (fUnit ? "fahrenheit" : "celsius") +
      "&wind_speed_unit=" + (fUnit ? "mph" : "kmh");

    getJson(url, 8000).then(function (d) {
      if (!root.isConnected) return;
      var c = d.current || {};
      var deg = fUnit ? "°F" : "°C";
      if (q("[data-cicon]")) q("[data-cicon]").innerHTML = icon(codeInfo(c.weather_code).icon, 56);
      if (q("[data-temp]")) q("[data-temp]").innerHTML = Math.round(c.temperature_2m) + "°";
      if (q("[data-cond]")) q("[data-cond]").textContent = codeLabel(c.weather_code);
      if (q("[data-feels]")) q("[data-feels]").textContent = Math.round(c.apparent_temperature) + deg;
      if (q("[data-hum]")) q("[data-hum]").textContent = Math.round(c.relative_humidity_2m) + "%";
      if (q("[data-msg]")) q("[data-msg]").textContent = "";

      var days = q("[data-days]");
      if (days && d.daily && d.daily.time) {
        days.innerHTML = "";
        var dl = d.daily;
        for (var i = 0; i < dl.time.length; i++) {
          var cell = el("div", "wx-day");
          cell.innerHTML =
            '<div class="wx-day-name">' + esc(weekday(dl.time[i], i)) + "</div>" +
            '<div class="wx-day-icon">' + icon(codeInfo(dl.weather_code[i]).icon, 22) + "</div>" +
            '<div class="wx-day-temps"><span class="wx-hi">' + Math.round(dl.temperature_2m_max[i]) + "°</span>" +
            '<span class="wx-lo">' + Math.round(dl.temperature_2m_min[i]) + "°</span></div>";
          days.appendChild(cell);
        }
      }
    }).catch(function () {
      if (!root.isConnected) return;
      var msg = q("[data-msg]"); if (msg) msg.textContent = tr("weather.unavailable", "Weather unavailable");
    });
  }

  // Short localized weekday for a "YYYY-MM-DD" string; "Today" for index 0.
  function weekday(ymd, i) {
    if (i === 0) return tr("weather.today", "Today");
    var dt = new Date(ymd + "T00:00:00");
    try { return new Intl.DateTimeFormat(lang(), { weekday: "short" }).format(dt); }
    catch (e) { return dt.toLocaleDateString(undefined, { weekday: "short" }); }
  }

  function refreshAll() { instances.forEach(refreshInstance); }

  /* ---------------- settings overlay ---------------- */
  function openSettings(host, tab) {
    closeSettings();
    overlay = el("div", "wx-overlay");
    ["pointerdown", "pointermove", "pointerup", "touchstart", "touchmove", "touchend", "wheel"]
      .forEach(function (ev) { overlay.addEventListener(ev, function (e) { e.stopPropagation(); }, { passive: true }); });

    var close = el("button", "wx-ov-close", CLOSE);
    close.title = tr("weather.close", "Close");
    close.setAttribute("aria-label", tr("weather.close", "Close"));
    close.addEventListener("click", closeSettings);
    overlay.appendChild(close);

    var panel = el("div", "wx-panel");
    var tabs = el("div", "wx-tabs");
    var tLoc = el("button", "wx-tab", esc(tr("weather.location", "Location")));
    var tNet = el("button", "wx-tab", esc(tr("weather.network", "Network")));
    tabs.appendChild(tLoc); tabs.appendChild(tNet);
    panel.appendChild(tabs);

    var pane = el("div", "wx-pane");
    panel.appendChild(pane);
    overlay.appendChild(panel);
    document.body.appendChild(overlay);

    function select(which) {
      // Dismiss the on-screen keyboard when navigating away from the search field.
      if (window.Michka && Michka.hideOsk) Michka.hideOsk();
      tLoc.classList.toggle("on", which === "loc");
      tNet.classList.toggle("on", which === "net");
      pane.innerHTML = "";
      if (which === "loc") buildLocPane(pane, host); else buildNetPane(pane);
    }
    tLoc.addEventListener("click", function () { select("loc"); });
    tNet.addEventListener("click", function () { select("net"); });
    select(tab === "net" ? "net" : "loc");
  }

  function closeSettings() {
    if (!overlay) return;
    if (window.Michka && Michka.hideOsk) Michka.hideOsk();
    overlay.remove();
    overlay = null;
  }

  // Location pane: search field (text OSK) + a results list. Tapping a result saves it and closes.
  function buildLocPane(pane, host) {
    pane.appendChild(el("p", "wx-hint", esc(tr("weather.searchHint",
      "Search for a town, then pick the right one; several countries share names."))));

    var row = el("div", "wx-searchrow");
    var input = el("input", "wx-input");
    input.type = "text"; input.inputMode = "none"; input.spellcheck = false;
    input.placeholder = tr("weather.searchPh", "Town name…");
    var cur = locFor(host); if (cur) input.value = cur.name;
    var clr = el("button", "wx-clear", "✕");
    clr.title = tr("weather.clear", "Clear");
    clr.addEventListener("click", function () { input.value = ""; input.focus(); doSearch(""); });
    row.appendChild(input); row.appendChild(clr);
    pane.appendChild(row);

    var status = el("div", "wx-status");
    var results = el("div", "wx-results");
    pane.appendChild(status); pane.appendChild(results);

    var timer = null;
    function doSearch(qv) {
      clearTimeout(timer);
      results.innerHTML = "";
      qv = (qv || "").trim();
      if (qv.length < 2) { status.textContent = ""; return; }
      timer = setTimeout(function () {
        status.textContent = tr("weather.searching", "Searching…");
        getJson(GEO + "?name=" + encodeURIComponent(qv) + "&count=8&language=" + encodeURIComponent(lang()) + "&format=json", 7000)
          .then(function (d) {
            var list = (d && d.results) || [];
            status.textContent = list.length ? tr("weather.results", "Tap a town to use it") : tr("weather.noResults", "No towns found.");
            results.innerHTML = "";
            list.forEach(function (r) {
              var b = el("button", "wx-result");
              var sub = [r.admin1, r.country].filter(Boolean).join(", ");
              b.innerHTML = '<span class="wx-result-name">' + esc(r.name) + "</span>" +
                '<span class="wx-result-sub">' + esc(sub) + "</span>";
              b.addEventListener("click", function () {
                cfg.byHost[host] = {
                  name: r.name, admin1: r.admin1 || "", country: r.country || "",
                  country_code: r.country_code || "", latitude: r.latitude, longitude: r.longitude
                };
                saveCfg();
                closeSettings();
                refreshAll();
              });
              results.appendChild(b);
            });
          })
          .catch(function () { status.textContent = tr("weather.searchFail", "Search failed. Check the connection or proxy."); });
      }, 300);
    }

    input.addEventListener("input", function () { doSearch(input.value); });
    input.addEventListener("focus", function () {
      if (window.Michka && Michka.osk) Michka.osk("text", input, function () { doSearch(input.value); });
    });
  }

  // Network pane: enable a URL-prefix proxy + edit its host:port with a self-contained keypad.
  function buildNetPane(pane) {
    pane.appendChild(el("p", "wx-hint", esc(tr("weather.proxyHint",
      "By default the kiosk fetches weather directly. If it can't reach the internet, route requests through a URL-prefix proxy (e.g. cors-anywhere)."))));

    var toggleRow = el("div", "wx-toggle-row");
    toggleRow.appendChild(el("span", "wx-field-label", esc(tr("weather.proxyEnable", "Use proxy"))));
    var seg = el("div", "wx-seg");
    var offB = el("button", "wx-seg-b", esc(tr("weather.off", "Off")));
    var onB = el("button", "wx-seg-b", esc(tr("weather.on", "On")));
    seg.appendChild(offB); seg.appendChild(onB);
    toggleRow.appendChild(seg);
    pane.appendChild(toggleRow);

    pane.appendChild(el("label", "wx-field-label", esc(tr("weather.proxyAddr", "Proxy address (host:port)"))));

    var urlRow = el("div", "wx-url-row");
    var prefix = el("button", "wx-url-prefix", "http://");
    var scheme = (cfg.proxy.base || "").toLowerCase().indexOf("https://") === 0 ? "https://" : "http://";
    prefix.textContent = scheme;
    prefix.title = tr("weather.toggleScheme", "Toggle http/https");
    prefix.addEventListener("click", function () { prefix.textContent = prefix.textContent === "http://" ? "https://" : "http://"; });
    var input = el("input", "wx-input wx-url-input");
    input.type = "text"; input.inputMode = "none"; input.spellcheck = false;
    input.placeholder = "192.168.0.50:8080";
    input.value = stripScheme(cfg.proxy.base);
    urlRow.appendChild(prefix); urlRow.appendChild(input);
    pane.appendChild(urlRow);

    var save = el("button", "wx-btn", esc(tr("weather.save", "Save")));
    save.addEventListener("click", function () {
      var hp = stripScheme(input.value);
      cfg.proxy.base = hp ? prefix.textContent + hp : "";
      saveCfg();
      closeSettings();
      refreshAll();
    });
    pane.appendChild(save);

    // Wide keypad spanning the full width below Save (the kiosk text OSK has no "." / ":").
    pane.appendChild(buildPad(input));

    function setEnabled(on) {
      cfg.proxy.enabled = on;
      onB.classList.toggle("on", on);
      offB.classList.toggle("on", !on);
    }
    offB.addEventListener("click", function () { setEnabled(false); });
    onB.addEventListener("click", function () { setEnabled(true); });
    setEnabled(!!cfg.proxy.enabled);
  }

  // Wide host:port keypad — two rows of seven (the kiosk text OSK has no "." / ":").
  function buildPad(input) {
    var pad = el("div", "wx-pad");
    ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", ".", ":", "⌫", "✕"].forEach(function (k) {
      var b = el("button", "wx-pad-key", k);
      b.addEventListener("click", function () {
        if (k === "⌫") input.value = input.value.slice(0, -1);
        else if (k === "✕") input.value = "";
        else input.value += k;
      });
      pad.appendChild(b);
    });
    return pad;
  }

  // Re-fetch when the user flips °C/°F in Settings so the card updates at once instead of waiting for
  // the next 15-min poll. app.js fires "michka:settingschange" with detail.key from the .seg handler.
  if (!window.__wxSettingsHook) {
    window.__wxSettingsHook = true;
    document.addEventListener("michka:settingschange", function (e) {
      if (e.detail && e.detail.key && e.detail.key !== "tempUnit") return;
      instances.forEach(function (inst) { var loc = locFor(inst.ctx.host); if (loc) loadWeather(inst, loc); });
    });
  }

  /* ---------------- widget registration ---------------- */
  Michka.widget("weather", {
    render: function (ctx) {
      ctxRef = ctx;
      return '<div class="wx-root"></div>';
    },
    mount: function (ctx) {
      ctxRef = ctx;
      loadCfg();                       // pull saved towns/proxy now ctx (config) is available
      // ctx.el is the body; render() content is inside it. Use the body as our root.
      var inst = { ctx: ctx, refresh: null };
      inst.refresh = function () { refreshInstance(inst); };
      instances.push(inst);
      refreshInstance(inst);
      inst._timer = setInterval(function () {
        var loc = locFor(ctx.host); if (loc) loadWeather(inst, loc);
      }, REFRESH_MS);
      ctx._wxInst = inst;
    },
    unmount: function (ctx) {
      var inst = ctx._wxInst;
      if (inst) {
        if (inst._timer) clearInterval(inst._timer);
        var i = instances.indexOf(inst); if (i >= 0) instances.splice(i, 1);
      }
      if (!instances.length) closeSettings();
    }
  });
})();

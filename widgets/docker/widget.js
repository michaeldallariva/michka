/* Docker — live container monitor for the host this widget is placed on.
 *
 * Data path mirrors the GPU widget: the michka agent on the host runs `docker ps -a` + `docker stats`
 * and pushes the result; the hub serves it at GET /api/docker?host=<name>. So the kiosk never talks to
 * a Docker daemon directly and no Docker port has to be exposed. A host with no michka agent (or no
 * Docker) reports available:false, and the card tells the user to install the agent on that host.
 *
 * Shows one row per container: a status dot (green running / amber transitional / red unhealthy / grey
 * stopped), the name + image, and live CPU %, memory % and total network I/O. Flat colours only; reads
 * app.js's global `pctColor` (loaded before widget.js, like UI templates) to tint CPU by load. A
 * "restart container" action is planned for a later version.
 */
(function () {
  "use strict";

  function tr(ctx, k, d) { var v = ctx && ctx.t ? ctx.t(k) : k; return (v && v !== k) ? v : (d || k); }
  function el(tag, cls, html) { var e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }

  function fmtBytes(ctx, n) {
    if (ctx && ctx.fmtBytes) return ctx.fmtBytes(n || 0);
    n = n || 0; var u = ["B", "KB", "MB", "GB", "TB"], i = 0;
    while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
    return (i === 0 ? n : n.toFixed(1)) + u[i];
  }
  function cpuColor(pct) {
    try { if (typeof pctColor === "function") return pctColor(Math.min(100, pct)); } catch (e) { /* fall through */ }
    return "var(--text)";
  }

  // Dot colour class from container state + health.
  function dotClass(c) {
    if (c.state === "running") {
      if (c.health === "unhealthy") return "bad";
      if (c.health === "starting") return "warn";
      return "ok";
    }
    if (c.state === "restarting" || c.state === "paused") return "warn";
    return "off"; // exited | created | dead | unknown
  }

  function render() {
    return '<div class="dk">' +
      '<div class="dk-head"><span class="dk-count" data-count>--</span>' +
      '<span class="dk-count-lbl" data-countlbl></span></div>' +
      '<div class="dk-list" data-list></div>' +
      '<div class="dk-msg" data-msg></div></div>';
  }

  function paint(ctx, root, data) {
    var listEl = root.querySelector("[data-list]");
    var msgEl = root.querySelector("[data-msg]");
    var countEl = root.querySelector("[data-count]");
    var countLbl = root.querySelector("[data-countlbl]");

    if (!data || data.available === false) {
      listEl.innerHTML = ""; countEl.textContent = "--"; countLbl.textContent = "";
      // No agent reporting Docker for this host: tell the user how to enable it.
      msgEl.innerHTML = '<div class="dk-install-icon">' + INSTALL_ICON + "</div>" +
        '<div class="dk-install-title">' + ctx.esc(tr(ctx, "docker.noAgentTitle", "No Docker data for this host")) + "</div>" +
        '<div class="dk-install-hint">' + ctx.esc(tr(ctx, "docker.noAgentHint",
          "Install the michka agent on this Docker host to report its containers here.")) + "</div>";
      msgEl.classList.add("dk-install");
      return;
    }
    msgEl.classList.remove("dk-install");

    var list = data.containers || [];
    var running = list.filter(function (c) { return c.state === "running"; }).length;
    countEl.textContent = running + " / " + list.length;
    countLbl.textContent = tr(ctx, "docker.runningTotal", "running / total");

    if (!list.length) {
      listEl.innerHTML = ""; msgEl.textContent = tr(ctx, "docker.none", "No containers");
      return;
    }
    msgEl.textContent = "";

    list.sort(function (a, b) {
      var ar = a.state === "running" ? 0 : 1, br = b.state === "running" ? 0 : 1;
      return ar - br || (a.name || "").localeCompare(b.name || "");
    });

    listEl.innerHTML = "";
    list.forEach(function (c) {
      var row = el("div", "dk-row");
      row.title = c.status || c.state || "";
      var dot = el("span", "dk-dot " + dotClass(c));
      var main = el("div", "dk-main");
      main.appendChild(el("div", "dk-name", ctx.esc(c.name || c.id || "?")));
      main.appendChild(el("div", "dk-img", ctx.esc(c.image || "")));

      var stats = el("div", "dk-stats");
      if (c.state === "running") {
        var cpu = (typeof c.cpuPct === "number" && c.cpuPct >= 0) ? Math.round(c.cpuPct) + "%" : "--";
        var mem = (typeof c.memPct === "number" && c.memPct >= 0) ? Math.round(c.memPct) + "%" : "--";
        var cpuEl = el("div", "dk-stat", '<span class="dk-sk">' + tr(ctx, "docker.cpu", "CPU") + '</span><span class="dk-sv">' + cpu + "</span>");
        if (typeof c.cpuPct === "number" && c.cpuPct >= 0) cpuEl.querySelector(".dk-sv").style.color = cpuColor(c.cpuPct);
        var memEl = el("div", "dk-stat", '<span class="dk-sk">' + tr(ctx, "docker.mem", "MEM") + '</span><span class="dk-sv">' + mem + "</span>");
        memEl.querySelector(".dk-sv").title = fmtBytes(ctx, c.memUsedBytes) + " / " + fmtBytes(ctx, c.memLimitBytes);
        var netEl = el("div", "dk-stat dk-net", '<span class="dk-sk">' + tr(ctx, "docker.net", "NET") + '</span>' +
          '<span class="dk-sv">&#8595;' + fmtBytes(ctx, c.netRxBytes) + " &#8593;" + fmtBytes(ctx, c.netTxBytes) + "</span>");
        stats.appendChild(cpuEl); stats.appendChild(memEl); stats.appendChild(netEl);
      } else {
        stats.appendChild(el("div", "dk-state-pill", ctx.esc(c.state || "stopped")));
      }
      row.appendChild(dot); row.appendChild(main); row.appendChild(stats);
      listEl.appendChild(row);
    });
  }

  var INSTALL_ICON = '<svg viewBox="0 0 24 24" width="34" height="34" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="10.5" width="2.6" height="2.6"/><rect x="6.2" y="10.5" width="2.6" height="2.6"/><rect x="9.4" y="10.5" width="2.6" height="2.6"/><rect x="6.2" y="7.4" width="2.6" height="2.6"/><rect x="9.4" y="7.4" width="2.6" height="2.6"/><rect x="9.4" y="4.3" width="2.6" height="2.6"/><path d="M2 13.2c0 3.2 2.2 5.3 5.6 5.3 5 0 8.6-2.4 9.9-6 .9.5 2 .4 2.7-.5-1.1-.7-.8-2-.8-2"/></svg>';

  Michka.widget("docker", {
    render: function () { return render(); },
    mount: function (ctx) {
      var root = ctx.el;
      var poll = function () {
        ctx.api("/api/docker?host=" + encodeURIComponent(ctx.host))
          .then(function (d) { if (root.isConnected) paint(ctx, root, d); })
          .catch(function () {
            if (!root.isConnected) return;
            var msg = root.querySelector("[data-msg]"); if (msg) { msg.classList.remove("dk-install"); msg.textContent = tr(ctx, "docker.unavailable", "Docker unavailable"); }
          });
      };
      poll();
      ctx._timer = setInterval(poll, 3000);
    },
    unmount: function (ctx) { if (ctx._timer) { clearInterval(ctx._timer); ctx._timer = null; } },
  });
})();

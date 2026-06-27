/* GPU — the first functional michka widget.
 *
 * Shows the live GPU readings for the host this widget is placed on: temperature, utilisation, and
 * memory (used / total). The hub exposes them at GET /api/gpu?host=<name>, sourced from the host's
 * latest snapshot. On Windows the agent reads temperature + total memory via the native WDDM D3DKMT
 * path and utilisation + used memory via the PDH "GPU Engine" / "GPU Adapter Memory" performance
 * counters (the same sources Task Manager uses) — no vendor SDK and no custom kernel driver. Hosts
 * with no GPU sensor report an empty list, and the card says so. Readings that couldn't be read come
 * back as 0 and their row is hidden.
 *
 * Follows the widget contract: render() returns the body, mount() starts a poll, unmount() stops it.
 * Styling uses the shared theme tokens (widget.css) so active templates (e.g. Futura) re-skin it, and
 * the bars are flat solid colours (no gradients) per the project's data-display rules.
 */
Michka.widget("gpu-temp", {
  render(ctx) {
    return `
      <div class="gput">
        <div class="gput-temp"><span data-temp>--</span><span class="gput-deg">&deg;C</span></div>
        <div class="gput-name" data-name></div>
        <div class="gput-rows">
          <div class="gput-row" data-util-row hidden>
            <div class="gput-rowhead"><span class="gput-k">${ctx.t("gpu.utilisation")}</span><span class="gput-v" data-util>--</span></div>
            <div class="gput-bar"><div class="gput-fill util" data-util-fill></div></div>
          </div>
          <div class="gput-row" data-mem-row hidden>
            <div class="gput-rowhead"><span class="gput-k">${ctx.t("gpu.memory")}</span><span class="gput-v" data-mem>--</span></div>
            <div class="gput-bar"><div class="gput-fill mem" data-mem-fill></div></div>
          </div>
        </div>
        <div class="gput-msg" data-msg></div>
      </div>`;
  },

  mount(ctx) {
    const q = (s) => ctx.el.querySelector(s);
    const tempBig = q(".gput-temp"), tEl = q("[data-temp]"), nEl = q("[data-name]"), mEl = q("[data-msg]");
    const utilRow = q("[data-util-row]"), utilEl = q("[data-util]"), utilFill = q("[data-util-fill]");
    const memRow = q("[data-mem-row]"), memEl = q("[data-mem]"), memFill = q("[data-mem-fill]");

    const poll = async () => {
      try {
        const d = await ctx.api("/api/gpu?host=" + encodeURIComponent(ctx.host));
        const g = d && d.gpus && d.gpus[0];
        if (!g) {
          tEl.textContent = "--";
          nEl.textContent = "";
          mEl.textContent = ctx.t("gpu.noSensor");
          tempBig.classList.remove("warm", "hot");
          utilRow.hidden = true; memRow.hidden = true;
          return;
        }
        tEl.textContent = Math.round(g.tempC);
        nEl.textContent = g.name || "GPU";
        mEl.textContent = "";
        // Flat colour by heat (no gradients): warm >= 65 C, hot >= 80 C.
        tempBig.classList.toggle("hot", g.tempC >= 80);
        tempBig.classList.toggle("warm", g.tempC >= 65 && g.tempC < 80);

        // Utilisation (hidden when the counter isn't available — value 0 with no engine activity still
        // shows; we only hide when the field is missing entirely).
        if (typeof g.utilPct === "number") {
          utilRow.hidden = false;
          const u = Math.max(0, Math.min(100, g.utilPct));
          utilEl.textContent = Math.round(u) + "%";
          utilFill.style.width = u + "%";
        } else { utilRow.hidden = true; }

        // Memory used / total.
        if (g.memTotalBytes > 0) {
          memRow.hidden = false;
          memEl.textContent = ctx.fmtBytes(g.memUsedBytes || 0) + " / " + ctx.fmtBytes(g.memTotalBytes);
          memFill.style.width = Math.max(0, Math.min(100, 100 * (g.memUsedBytes || 0) / g.memTotalBytes)) + "%";
        } else { memRow.hidden = true; }
      } catch (e) {
        mEl.textContent = ctx.t("gpu.unavailable");
      }
    };

    poll();
    ctx._timer = setInterval(poll, 2000);
  },

  unmount(ctx) {
    if (ctx._timer) { clearInterval(ctx._timer); ctx._timer = null; }
  },
});

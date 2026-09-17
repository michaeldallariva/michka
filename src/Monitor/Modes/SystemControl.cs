using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Monitor.Modes;

/// <summary>
/// Privileged host actions the Settings page can trigger (Linux/systemd only). The hub may run as
/// root, but can also run as an unprivileged (systemd <c>DynamicUser</c>) identity: in that case the
/// actions that touch other units go through <c>systemctl</c> and are authorised by a polkit rule
/// for the hub's service user, and the ones that need the kiosk's own session/files (screen power,
/// kiosk cache wipe) are delegated to root helpers (the <c>michka-screen-*</c> units and
/// <c>kiosk.sh</c>) rather than performed in-process. See the deploy/ polkit rule + units.
/// </summary>
public static class SystemControl
{
    [DllImport("libc")] private static extern uint geteuid();

    /// <summary>True when the hub process is running as root (uid 0). Used to pick the in-process
    /// privileged path vs. delegating to a root helper unit when running unprivileged.</summary>
    private static bool IsRoot() => OperatingSystem.IsLinux() && geteuid() == 0;
    /// <summary>
    /// Apply a changed server config: reload the kiosk so chromium reconnects (possibly on a new
    /// port), then exit so systemd restarts the hub with the new settings. Runs after the HTTP
    /// response has been sent.
    /// </summary>
    public static void ScheduleConfigRestart(ILogger log)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(600);
            if (OperatingSystem.IsLinux())
                Run("systemctl", "restart michka-kiosk.service", log);
            log.LogInformation("restarting hub to apply server config");
            // Restart=always in the unit brings the hub back up with the new config.
            Environment.Exit(0);
        });
    }

    /// <summary>
    /// Drop the kiosk UI and return tty1 to the Linux console login. Starting getty@tty1 stops the
    /// kiosk via the unit's Conflicts= relationship.
    /// </summary>
    public static bool ExitUiToLogin(ILogger log)
    {
        if (!OperatingSystem.IsLinux()) return false;
        _ = Task.Run(async () =>
        {
            await Task.Delay(400); // let the HTTP response flush first
            Run("systemctl", "start getty@tty1.service", log);
        });
        return true;
    }

    /// <summary>
    /// Set whether the kiosk dashboard starts automatically at boot by enabling or disabling the
    /// <c>michka-kiosk.service</c> systemd unit. "Manual" (disable) leaves the unit installed but
    /// off at boot, so it can still be started by hand with <c>systemctl start michka-kiosk</c>.
    /// Persisting the preference is the caller's job; this only flips the boot behaviour. No-op
    /// off Linux. Does not start/stop the currently running session.
    /// </summary>
    public static bool SetKioskAutostart(bool auto, ILogger log)
    {
        if (!OperatingSystem.IsLinux()) return false;
        // enable/disable needs the systemd "manage-unit-files" privilege, which the unprivileged hub
        // can't get from the polkit rule (it grants "manage-units" = start/stop/restart, not unit-file
        // edits). So when not root, *start* a root helper oneshot (allowed by the rule) that does the
        // enable/disable as root. Root drives it directly.
        if (IsRoot())
            _ = Task.Run(() => Run("systemctl", log, auto ? "enable" : "disable", "michka-kiosk.service"));
        else
            _ = Task.Run(() => Run("systemctl", log, "start",
                auto ? "michka-kiosk-autostart-on.service" : "michka-kiosk-autostart-off.service"));
        log.LogInformation("kiosk autostart {State}", auto ? "enabled" : "disabled");
        return true;
    }

    /// <summary>
    /// Power the kiosk panel off/on for the screensaver "sleep" mode. Best-effort: uses
    /// <c>wlr-randr</c> inside the kiosk's Wayland session to toggle every output. The panel's touch
    /// digitizer is a separate USB HID, so taps still wake the browser even with the backlight off
    /// (the browser then calls this again with <c>on=true</c>). If the compositor doesn't support
    /// output power management this is a harmless no-op and the black overlay still covers the screen.
    /// </summary>
    public static bool SetScreenPower(bool on, ILogger log)
    {
        if (!OperatingSystem.IsLinux()) return false;

        // A non-root hub can't reach the kiosk's root-owned Wayland session (/run/user/0), so it asks
        // systemd to run a tiny root helper unit instead (authorised by the michka polkit rule). When
        // the hub itself is root (legacy/dev), drive wlr-randr directly so no helper unit is required.
        if (!IsRoot())
        {
            _ = Task.Run(() => Run("systemctl", log, "start", on ? "michka-screen-on.service" : "michka-screen-off.service"));
            return true;
        }

        string state = on ? "--on" : "--off";
        // Discover the Wayland socket the same way the screenshot helper does, then toggle outputs.
        string script =
            "sock=$(ls /run/user/0/ 2>/dev/null | grep -E '^wayland-[0-9]+$' | head -1); " +
            "export XDG_RUNTIME_DIR=/run/user/0 WAYLAND_DISPLAY=$sock; " +
            "for o in $(wlr-randr 2>/dev/null | grep -E '^[^[:space:]]' | awk '{print $1}'); do " +
            $"wlr-randr --output \"$o\" {state}; done";
        _ = Task.Run(() => Run("bash", log, "-c", script));
        return true;
    }

    /// <summary>
    /// When the hub binary has changed since the last run, wipe the kiosk Chromium HTTP cache so the
    /// freshly-deployed embedded UI assets (app.js/app.css/index.html/lang/*) can never be served
    /// stale from the browser's persistent profile. The kiosk profile lives at
    /// <c>&lt;dataDir&gt;/chrome-profile</c> on every install (the rack's <c>/root/michka</c> and the
    /// packaged <c>/var/lib/michka</c> both follow that convention), so the cache dirs are derived
    /// from the hub's own data directory. Keyed on the assembly's module version id — regenerated on
    /// every build — recorded in <c>&lt;dataDir&gt;/.kiosk-cache-stamp</c>, so ordinary same-binary
    /// restarts (config changes, reboots, crash-restarts) keep their cache warm and don't pay for a
    /// slow first paint; only a new binary triggers a wipe. Linux-only, best-effort: never throws.
    /// Runs synchronously and early so the deploy's following <c>systemctl restart michka-kiosk</c>
    /// relaunches chromium against an already-empty cache (replacing the manual
    /// <c>rm -rf chrome-profile/Default/Cache</c> step).
    /// </summary>
    public static void ClearKioskCacheOnUpgrade(string dataDir, ILogger log)
    {
        if (!OperatingSystem.IsLinux()) return;
        // The kiosk's Chromium profile is owned by the user the kiosk runs as (root on the rack). A
        // non-root hub can't delete it, so it leaves the wipe to kiosk.sh, which runs as that user and
        // does the same stamp-keyed clear before launching chromium. Skip here to avoid a half-done
        // clear that still updates the stamp (which would suppress the kiosk-side wipe).
        if (!IsRoot()) return;
        try
        {
            var stampPath = Path.Combine(dataDir, ".kiosk-cache-stamp");
            var current = typeof(SystemControl).Assembly.ManifestModule.ModuleVersionId.ToString("N");
            string? previous = null;
            try { if (File.Exists(stampPath)) previous = File.ReadAllText(stampPath).Trim(); }
            catch { /* unreadable stamp ⇒ treat as changed */ }
            if (previous == current) return; // same binary — nothing changed, keep the cache warm

            var profile = Path.Combine(dataDir, "chrome-profile", "Default");
            if (Directory.Exists(profile))
            {
                // The HTTP/code/GPU caches are what serve stale JS/CSS; leave cookies, localStorage,
                // and the rest of the profile untouched (the NexusM widget keeps its config there).
                foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                {
                    var dir = Path.Combine(profile, sub);
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                    catch (Exception ex) { log.LogWarning(ex, "kiosk cache clear failed for {Dir}", dir); }
                }
                log.LogInformation("cleared kiosk Chromium cache after hub binary change");
            }

            try { File.WriteAllText(stampPath, current); }
            catch (Exception ex) { log.LogWarning(ex, "kiosk cache stamp write failed"); }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "kiosk cache upgrade check failed");
        }
    }

    /// <summary>Run a command, passing each argument verbatim (no shell re-quoting).</summary>
    private static void Run(string file, ILogger log, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "command failed: {File} {Args}", file, string.Join(' ', args));
        }
    }

    private static void Run(string file, string args, ILogger log)
        => Run(file, log, args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

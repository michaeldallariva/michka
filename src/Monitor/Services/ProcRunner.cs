using System.Diagnostics;

namespace Monitor.Services;

/// <summary>
/// Shared helper for shelling out to an external process with a hard timeout. Every call site across
/// the hub, the agent, and the Windows GUI that shells a process (systemctl/sc.exe for service
/// enumeration, docker for container stats, systemctl/wlr-randr for kiosk control, sc.exe for the
/// Windows service install) goes through this one implementation instead of re-deriving the same
/// "redirect + drain + timeout + kill" boilerplate independently (which is how <c>Modes/SystemControl.cs</c>
/// ended up missing the kill-on-timeout step that the others already had).
///
/// NOT for a caller that needs <c>UseShellExecute = true</c> (e.g. a UAC-elevated launch) — Windows
/// doesn't allow combining that with output redirection, so that case can't go through here and has to
/// stay its own code path (see <c>Gui/AgentGui.cs</c>'s <c>RunSc</c>, whose elevated branch does its
/// own unbounded <c>Process.Start</c>/<c>WaitForExit</c> since it's gated on a human clicking the UAC
/// prompt, not something a short timeout should race against).
/// </summary>
internal static class ProcRunner
{
    public readonly record struct Result(bool TimedOut, int ExitCode, string Stdout, string Stderr)
    {
        public bool Ok => !TimedOut && ExitCode == 0;

        /// <summary>stdout + stderr concatenated — handy for CLI tools (like sc.exe) that aren't
        /// consistent about which stream error text lands on.</summary>
        public string Output => Stdout + Stderr;
    }

    /// <summary>Run a prepared <see cref="ProcessStartInfo"/> (forced into redirected,
    /// non-shell-execute mode), draining both stdout and stderr asynchronously before waiting so a
    /// child that fills a pipe buffer can't block <c>WaitForExit</c> forever, and killing it if it
    /// doesn't exit within <paramref name="timeoutMs"/>. Lets a <c>Process.Start</c> failure (e.g. the
    /// executable not found) throw, same as a bare <c>Process.Start</c> call would — callers that want
    /// "swallow failures, just give me an empty result" should use <see cref="Capture"/> instead.</summary>
    public static Result Run(ProcessStartInfo psi, int timeoutMs = 5000)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true; // no-op on Linux; suppresses the Windows console-flash otherwise seen here

        using var p = Process.Start(psi);
        if (p is null) return new Result(true, -1, "", "");
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { /* ignore */ } return new Result(true, -1, "", ""); }
        return new Result(false, p.ExitCode, so.GetAwaiter().GetResult(), se.GetAwaiter().GetResult());
    }

    /// <summary>Run <paramref name="file"/> with each argument passed verbatim (no shell re-quoting)
    /// and return its stdout, or "" on any failure/timeout (including <paramref name="file"/> not
    /// existing). The common case: a CLI tool whose output you want to parse and whose failures you
    /// want to treat as "no data" rather than an exception.</summary>
    public static string Capture(string file, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file);
            foreach (var a in args) psi.ArgumentList.Add(a);
            var r = Run(psi, timeoutMs);
            return r.TimedOut ? "" : r.Stdout;
        }
        catch { return ""; }
    }

    public static string Capture(string file, params string[] args) => Capture(file, 5000, args);
}

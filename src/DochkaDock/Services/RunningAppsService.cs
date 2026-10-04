using System.Diagnostics;
using System.Text;

namespace DochkaDock.Services;

/// <summary>A single matched top-level window: its handle plus title, used
/// to let the user pick a specific window when an app has more than one
/// open (e.g. three Firefox windows) rather than always focusing whichever
/// one EnumWindows happens to return first.</summary>
public readonly record struct RunningWindow(IntPtr Handle, string Title);

/// <summary>Matches dock items to their running process's windows, so the dock
/// can focus/restore an already-open app instead of always launching a new
/// instance. Enumerates top-level windows fresh on every call rather than
/// caching — a handful of pinned items polled every second or two is cheap,
/// and it avoids ever going stale.</summary>
public sealed class RunningAppsService
{
    public IReadOnlyList<IntPtr> FindWindows(string exePath) =>
        FindRunningWindows(exePath).Select(w => w.Handle).ToList();

    /// <summary>Same matching as FindWindows, but with each window's title
    /// too — only fetched here (not on every poll tick) since it's only
    /// needed when actually building a window-picker popup.</summary>
    public IReadOnlyList<RunningWindow> FindRunningWindows(string exePath)
    {
        var matches = new List<RunningWindow>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;

            var length = NativeMethods.GetWindowTextLength(hWnd);
            if (length == 0) return true;

            // The desktop's own shell window ("Program Manager") is owned by
            // explorer.exe too, so without this it shows up as a phantom,
            // unclosable "window" in File Explorer's picker/badge count
            // alongside any real open folder windows. Filtered by class
            // name ("Progman"), not title, since the title is what's
            // actually shown to the user and shouldn't be the thing silently
            // deciding whether a window counts.
            var classBuffer = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, classBuffer, classBuffer.Capacity);
            if (classBuffer.ToString() == "Progman") return true;

            if (TryGetExecutablePath(hWnd, out var windowExePath) &&
                string.Equals(windowExePath, exePath, StringComparison.OrdinalIgnoreCase))
            {
                var buffer = new StringBuilder(length + 1);
                NativeMethods.GetWindowText(hWnd, buffer, buffer.Capacity);
                matches.Add(new RunningWindow(hWnd, buffer.ToString()));
            }

            return true;
        }, IntPtr.Zero);

        return matches;
    }

    public bool IsRunning(string exePath) => FindWindows(exePath).Count > 0;

    public void FocusOrRestore(IntPtr hWnd)
    {
        if (NativeMethods.IsIconic(hWnd))
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

        NativeMethods.SetForegroundWindow(hWnd);
    }

    /// <summary>Cheap per-tick RAM read for a window already found by
    /// FindWindows — avoids a second full EnumWindows pass just to get this,
    /// unlike GetProcess below.</summary>
    public long GetWorkingSetBytes(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Used by End Task/Restart App — returns the live Process
    /// (caller must Dispose it) owning any matching window, or null if
    /// nothing's running.</summary>
    public Process? GetProcess(string exePath)
    {
        var windows = FindWindows(exePath);
        if (windows.Count == 0) return null;

        NativeMethods.GetWindowThreadProcessId(windows[0], out var pid);
        try
        {
            return Process.GetProcessById((int)pid);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>MainModule throws for protected/elevated processes the dock
    /// isn't allowed to inspect — treated as "no match" rather than crashing
    /// the poll loop.</summary>
    private static bool TryGetExecutablePath(IntPtr hWnd, out string? exePath)
    {
        exePath = null;
        try
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return false;

            using var process = Process.GetProcessById((int)pid);
            exePath = process.MainModule?.FileName;
            return exePath is not null;
        }
        catch
        {
            return false;
        }
    }
}

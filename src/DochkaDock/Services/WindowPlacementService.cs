using System.Runtime.InteropServices;
using DochkaDock.Models;

namespace DochkaDock.Services;

/// <summary>Captures and restores a window's position/size/monitor/maximized
/// state. Used by "Save Window Position" (capture) and by a fresh launch of
/// an item that has a saved placement (apply) — never applied to a window the
/// dock is merely focusing, so the dock never snaps a window you've since
/// moved back to an old spot.</summary>
public sealed class WindowPlacementService
{
    /// <summary>Reads the window's *restored* rect (rcNormalPosition), not its
    /// current maximized rect — that's the only one worth saving, since a
    /// maximized rect is just "the monitor's work area" and tells you nothing
    /// useful to restore to.</summary>
    public WindowPlacementData? Capture(IntPtr hWnd)
    {
        var placement = new NativeMethods.WINDOWPLACEMENT { length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
        if (!NativeMethods.GetWindowPlacement(hWnd, ref placement))
            return null;

        var rect = placement.rcNormalPosition;

        return new WindowPlacementData
        {
            MonitorDevice = GetMonitorDevice(hWnd),
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Right - rect.Left,
            Height = rect.Bottom - rect.Top,
            Maximized = placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED
        };
    }

    /// <summary>Moves/resizes the window to the saved rect, falling back to
    /// the primary monitor (and clamping into its work area) if the saved
    /// monitor is no longer connected, then maximizes if that was saved too.</summary>
    public void Apply(IntPtr hWnd, WindowPlacementData placement)
    {
        var targetMonitor = FindMonitorWorkArea(placement.MonitorDevice) ?? FindMonitorWorkArea(null);

        var left = placement.Left;
        var top = placement.Top;
        var width = placement.Width;
        var height = placement.Height;

        if (targetMonitor is { } work)
        {
            width = Math.Min(width, work.Right - work.Left);
            height = Math.Min(height, work.Bottom - work.Top);
            left = Math.Clamp(left, work.Left, work.Right - width);
            top = Math.Clamp(top, work.Top, work.Bottom - height);
        }

        NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, (int)left, (int)top, (int)width, (int)height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        if (placement.Maximized)
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOWMAXIMIZED);
    }

    private static string? GetMonitorDevice(IntPtr hWnd)
    {
        var monitor = NativeMethods.MonitorFromWindow(hWnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return null;

        var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        return NativeMethods.GetMonitorInfo(monitor, ref info) ? info.szDevice : null;
    }

    /// <summary>Null deviceName means "the primary monitor" (whichever one
    /// MonitorFromWindow(IntPtr.Zero, ...) resolves to isn't reliable, so
    /// this instead looks for the monitor whose work area starts at (0,0),
    /// which is always the primary one).</summary>
    private static NativeMethods.RECT? FindMonitorWorkArea(string? deviceName)
    {
        NativeMethods.RECT? found = null;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref info)) return true;

            var isMatch = deviceName is not null
                ? string.Equals(info.szDevice, deviceName, StringComparison.OrdinalIgnoreCase)
                : info.rcWork.Left == 0 && info.rcWork.Top == 0;

            if (isMatch)
            {
                found = info.rcWork;
                return false; // stop enumerating, we found it
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }
}

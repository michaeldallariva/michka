using System.Windows;
using System.Windows.Interop;

namespace DochkaDock.Services;

/// <summary>Tracks whether the dock's own session is currently being viewed over
/// Remote Desktop - live, for the lifetime of the process, not just once at
/// startup. A session that started on the physical console flips to remote for
/// as long as someone RDPs into that machine, then flips back once they
/// disconnect, so this has to be watched continuously (via
/// WTSRegisterSessionNotification/WM_WTSSESSION_CHANGE) rather than checked
/// once. Same hook-a-window-message pattern ClipboardMonitorService and
/// TrayIconManager already use for their own native notifications.</summary>
public sealed class RemoteSessionMonitorService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSourceHook _hook;

    public event Action<bool>? RemoteSessionChanged;

    public RemoteSessionMonitorService(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _hook = WndProc;
        HwndSource.FromHwnd(_hwnd)?.AddHook(_hook);
        NativeMethods.WTSRegisterSessionNotification(_hwnd, NativeMethods.NOTIFY_FOR_THIS_SESSION);
    }

    /// <summary>One-time check for the window's initial Show()/Hide() decision
    /// at launch, before the live notification hook above has anything to
    /// report yet.</summary>
    public static bool IsCurrentSessionRemote() => NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_WTSSESSION_CHANGE) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case NativeMethods.WTS_REMOTE_CONNECT:
                RemoteSessionChanged?.Invoke(true);
                break;
            case NativeMethods.WTS_REMOTE_DISCONNECT:
                RemoteSessionChanged?.Invoke(false);
                break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        NativeMethods.WTSUnRegisterSessionNotification(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.RemoveHook(_hook);
    }
}

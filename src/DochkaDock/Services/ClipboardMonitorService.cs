using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DochkaDock.Services;

/// <summary>One clipboard snapshot - either text or an image, never both.</summary>
public sealed class ClipboardSnapshot
{
    public string? Text { get; init; }
    public BitmapSource? Image { get; init; }
}

/// <summary>Notifies when the system clipboard changes, same hook-a-window-
/// message pattern TrayIconManager already uses for WM_TRAYICON (raw Win32
/// for the change notification WPF doesn't expose; the actual read uses
/// WPF's own System.Windows.Clipboard, no raw Win32 needed for that part).</summary>
public sealed class ClipboardMonitorService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSourceHook _hook;

    public event Action<ClipboardSnapshot>? ClipboardChanged;

    public ClipboardMonitorService(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _hook = WndProc;
        HwndSource.FromHwnd(_hwnd)?.AddHook(_hook);
        NativeMethods.AddClipboardFormatListener(_hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_CLIPBOARDUPDATE) return IntPtr.Zero;

        var snapshot = TryReadClipboard();
        if (snapshot is not null)
            ClipboardChanged?.Invoke(snapshot);

        return IntPtr.Zero;
    }

    /// <summary>Best-effort: the clipboard can be locked by another
    /// application at the exact moment of a change notification, or hold a
    /// format WPF's Clipboard class doesn't recognize - either way this
    /// returns null rather than throwing, so one bad read never crashes the
    /// dock or breaks the next legitimate one.</summary>
    private static ClipboardSnapshot? TryReadClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                return image is null ? null : new ClipboardSnapshot { Image = image };
            }

            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                return string.IsNullOrEmpty(text) ? null : new ClipboardSnapshot { Text = text };
            }
        }
        catch
        {
            // Best-effort only.
        }

        return null;
    }

    public void Dispose()
    {
        NativeMethods.RemoveClipboardFormatListener(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.RemoveHook(_hook);
    }
}

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using DochkaDock.Services;
using static DochkaDock.TrayNativeMethods;

namespace DochkaDock;

/// <summary>Owns the system tray icon and its menu: show/hide the dock,
/// add an app, change magnification, toggle autostart, or exit entirely.
/// Implemented with raw Win32 calls (Shell_NotifyIcon + a native popup
/// menu) instead of System.Windows.Forms.NotifyIcon, so the app doesn't
/// need to bundle all of WinForms just for a tray icon.</summary>
public sealed class TrayIconManager : IDisposable
{
    private const int WM_TRAYICON = 0x8001; // WM_APP + 1
    private const int TrayIconId = 1;        // must match uID in AddOrUpdateIcon
    private const int CMD_TOGGLE = 1001;
    private const int CMD_ADD_APP = 1002;
    private const int CMD_MAGNIFY_SUBTLE = 1003;
    private const int CMD_MAGNIFY_NORMAL = 1004;
    private const int CMD_MAGNIFY_LARGE = 1005;
    private const int CMD_AUTOSTART = 1006;
    private const int CMD_OPEN_CONFIG = 1007;
    private const int CMD_ABOUT = 1009;
    private const int CMD_EXIT = 1008;

    private readonly MainWindow _window;
    private readonly AutoStartService _autoStart = new();
    private readonly IntPtr _hwnd;
    private readonly HwndSourceHook _hook;
    private readonly IntPtr _icon;
    private bool _added;

    public TrayIconManager(MainWindow window)
    {
        _window = window;
        _hwnd = new WindowInteropHelper(window).Handle;

        _hook = WndProc;
        HwndSource.FromHwnd(_hwnd)?.AddHook(_hook);

        _icon = LoadTrayIcon();
        AddOrUpdateIcon();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAYICON) return IntPtr.Zero;

        // With NOTIFYICON_VERSION_4 the icon's own uID rides in lParam's high
        // word alongside the mouse/keyboard message in the low word. Any
        // other process on the same desktop session could PostMessage this
        // same WM_APP+1 value to this window to fake a tray click, so this
        // checks the callback actually claims to be about our one registered
        // icon (uID 1) before acting on it - the officially documented shape
        // of this message, not just an accept-anything handler.
        var iconId = (int)(((long)lParam >> 16) & 0xFFFF);
        if (iconId != TrayIconId) return IntPtr.Zero;

        var mouseMsg = (int)((long)lParam & 0xFFFF);
        if (mouseMsg == WM_LBUTTONUP)
        {
            _window.Dispatcher.Invoke(_window.ToggleVisibility);
            handled = true;
        }
        else if (mouseMsg == WM_RBUTTONUP || mouseMsg == WM_CONTEXTMENU)
        {
            _window.Dispatcher.Invoke(ShowMenu);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        GetCursorPos(out var cursor);

        var menu = CreatePopupMenu();
        try
        {
            var loc = LocalizationService.Instance;

            AppendMenu(menu, MF_STRING, (UIntPtr)CMD_TOGGLE, loc["Tray_ShowHideDock"]);
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)CMD_ADD_APP, loc["Options_AddApplication"]);

            var magnifyMenu = CreatePopupMenu();
            AppendMenu(magnifyMenu, MF_STRING, (UIntPtr)CMD_MAGNIFY_SUBTLE, loc["Options_MagnifySubtle"]);
            AppendMenu(magnifyMenu, MF_STRING, (UIntPtr)CMD_MAGNIFY_NORMAL, loc["Options_MagnifyNormal"]);
            AppendMenu(magnifyMenu, MF_STRING, (UIntPtr)CMD_MAGNIFY_LARGE, loc["Options_MagnifyLarge"]);
            AppendMenu(menu, MF_POPUP, (UIntPtr)magnifyMenu, loc["Options_IconMagnification"]);

            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);

            var autoStartFlags = MF_STRING | (_autoStart.IsEnabled() ? MF_CHECKED : 0);
            AppendMenu(menu, autoStartFlags, (UIntPtr)CMD_AUTOSTART, loc["Options_StartWithWindows"]);
            AppendMenu(menu, MF_STRING, (UIntPtr)CMD_OPEN_CONFIG, loc["Options_OpenConfigFolder"]);
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)CMD_ABOUT, loc["Options_AboutDochkaDock"]);
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)CMD_EXIT, loc["Options_Exit"]);

            // Required so the menu reliably dismisses on an outside click.
            SetForegroundWindow(_hwnd);
            var selected = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, cursor.X, cursor.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            HandleCommand(selected);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HandleCommand(int command)
    {
        switch (command)
        {
            case CMD_TOGGLE:
                _window.ToggleVisibility();
                break;
            case CMD_ADD_APP:
                _window.OpenAddAppPicker();
                break;
            case CMD_MAGNIFY_SUBTLE:
                _window.SetMagnifyScale(1.3);
                break;
            case CMD_MAGNIFY_NORMAL:
                _window.SetMagnifyScale(1.8);
                break;
            case CMD_MAGNIFY_LARGE:
                _window.SetMagnifyScale(2.3);
                break;
            case CMD_AUTOSTART:
                _autoStart.SetEnabled(!_autoStart.IsEnabled());
                break;
            case CMD_OPEN_CONFIG:
                try
                {
                    Process.Start(new ProcessStartInfo(_window.ConfigDirectory) { UseShellExecute = true });
                }
                catch
                {
                    // Best-effort only.
                }
                break;
            case CMD_ABOUT:
                new AboutWindow { ShowInTaskbar = true }.Show();
                break;
            case CMD_EXIT:
                Application.Current.Shutdown();
                break;
        }
    }

    private void AddOrUpdateIcon()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon,
            szTip = "DochkaDock"
        };

        Shell_NotifyIcon(NIM_ADD, ref data);
        _added = true;

        var version = new NOTIFYICONDATA { cbSize = data.cbSize, hWnd = _hwnd, uID = 1, uVersion = NOTIFYICON_VERSION_4 };
        Shell_NotifyIcon(NIM_SETVERSION, ref version);
    }

    private static IntPtr LoadTrayIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("DochkaDock.Assets.tray.ico");
        if (stream is null) return IntPtr.Zero;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var fileBytes = memory.ToArray();

        // .ico file layout: a 6-byte ICONDIR header followed by one 16-byte
        // ICONDIRENTRY per image. CreateIconFromResourceEx wants just the
        // single image's own bytes (what an ICONDIRENTRY points to), not the
        // whole file — passing the file as-is makes it silently fail.
        if (fileBytes.Length < 6 + 16) return IntPtr.Zero;
        var imageCount = BitConverter.ToUInt16(fileBytes, 4);
        if (imageCount < 1) return IntPtr.Zero;

        const int entryOffset = 6; // first image's ICONDIRENTRY
        var imageSize = BitConverter.ToInt32(fileBytes, entryOffset + 8);
        var imageOffset = BitConverter.ToInt32(fileBytes, entryOffset + 12);
        if (imageSize <= 0 || imageOffset < 0 || imageOffset + imageSize > fileBytes.Length)
            return IntPtr.Zero;

        var imageBytes = new byte[imageSize];
        Array.Copy(fileBytes, imageOffset, imageBytes, 0, imageSize);

        return CreateIconFromResourceEx(imageBytes, (uint)imageSize, true, 0x00030000, 32, 32, 0);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1 };
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }

        HwndSource.FromHwnd(_hwnd)?.RemoveHook(_hook);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }
}

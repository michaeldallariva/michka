using System.Runtime.InteropServices;

namespace DochkaDock;

/// <summary>Raw Win32 declarations for a system tray icon and its popup
/// menu. Used instead of System.Windows.Forms.NotifyIcon/ContextMenuStrip so
/// the app doesn't need to bundle all of WinForms (and, more importantly,
/// becomes eligible for trimming — the .NET SDK outright refuses to trim a
/// project that references Windows Forms).</summary>
internal static class TrayNativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    public const int NIM_ADD = 0x00;
    public const int NIM_MODIFY = 0x01;
    public const int NIM_DELETE = 0x02;
    public const int NIM_SETVERSION = 0x04;

    public const int NIF_MESSAGE = 0x01;
    public const int NIF_ICON = 0x02;
    public const int NIF_TIP = 0x04;

    public const int NOTIFYICON_VERSION_4 = 4;

    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_CONTEXTMENU = 0x007B;
    public const int WM_COMMAND = 0x0111;
    public const int WM_NULL = 0x0000;

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint MF_CHECKED = 0x00000008;
    public const uint MF_DISABLED = 0x00000002;
    public const uint MF_GRAYED = 0x00000001;
    public const uint MF_POPUP = 0x00000010;

    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    public static extern IntPtr CreateIconFromResourceEx(
        byte[] presbits, uint dwResSize, bool fIcon, uint dwVer,
        int cxDesired, int cyDesired, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);
}

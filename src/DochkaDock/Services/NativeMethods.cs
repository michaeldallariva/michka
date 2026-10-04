using System.Runtime.InteropServices;
using System.Text;

namespace DochkaDock.Services;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_SMALLICON = 0x000000001;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint SHGFI_PIDL = 0x000000008;
    public const uint SHGFI_SYSICONINDEX = 0x000004000;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    // Windows keeps the familiar 32x32 "large" icon list, but also a 256x256
    // "jumbo" one (introduced in Vista) addressed by the same system icon
    // index. SHGetFileInfo alone never hands back more than 32x32; this pair
    // (SHGetImageList + IImageList.GetIcon) is the standard route to the
    // higher-resolution bitmap that real Explorer-style UIs use for anything
    // that gets displayed above icon-tray size.
    public const int SHIL_JUMBO = 0x4;
    public const int ILD_TRANSPARENT = 0x00000001;
    public static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    // SHGetImageList has never been exported by name from shell32.dll, only
    // by ordinal, despite being documented in the Shlobj.h header — ordinal
    // 727 is the long-stable value for this across Windows versions.
    [DllImport("shell32.dll", EntryPoint = "#727")]
    public static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    // Only declared up to the one method we actually call (GetIcon); the
    // preceding methods must still be listed in their real vtable order so
    // GetIcon lands on the correct slot, even though we never invoke them.
    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    // Overload for SHGFI_PIDL: the "path" is really a pointer to an
    // ITEMIDLIST, which is how virtual shell items (Recycle Bin, Control
    // Panel, ...) are addressed — they have no real filesystem path, so the
    // string overload above can't resolve their icon reliably.
    [DllImport("shell32.dll", CharSet = CharSet.Auto, EntryPoint = "SHGetFileInfo")]
    public static extern IntPtr SHGetFileInfoByPidl(
        IntPtr pidl,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    public static readonly Guid FOLDERID_RecycleBinFolder = new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");

    [DllImport("shell32.dll")]
    public static extern int SHGetKnownFolderIDList(
        ref Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppidl);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // Also declared in the root-namespace TrayNativeMethods for its own,
    // narrower purpose (the tray icon itself) — duplicating the P/Invoke
    // signature here is harmless and avoids a Services -> root namespace
    // dependency just for one extern declaration, same reasoning as
    // SetForegroundWindow below. Used by IconService to build an HICON
    // straight from an embedded Workspace preset .ico's bytes.
    [DllImport("user32.dll")]
    public static extern IntPtr CreateIconFromResourceEx(
        byte[] presbits, uint dwResSize, bool fIcon, uint dwVer,
        int cxDesired, int cyDesired, uint flags);

    // ---- Remote Desktop awareness: the dock hides itself while its session ----
    // ---- is being viewed over RDP, and reappears once it's local again    ----

    // SM_REMOTESESSION: nonzero when the *current* session is right now being
    // serviced by a Terminal Services (Remote Desktop) connection. This is a
    // live, re-queryable state, not a one-time "was this process launched
    // under RDP" fact — a session that started on the physical console flips
    // to remote for as long as someone RDPs into that same machine, then
    // flips back once they disconnect. Used only for the one-time check at
    // startup; ongoing transitions are caught via WTSRegisterSessionNotification
    // below instead of polling this.
    public const int SM_REMOTESESSION = 0x1000;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    // WM_WTSSESSION_CHANGE + the two state transitions RemoteSessionMonitorService
    // cares about: WTS_REMOTE_CONNECT fires the instant this session starts being
    // displayed over RDP (whether that's a brand-new RDP-only session or the
    // physical console session getting taken over remotely); WTS_REMOTE_DISCONNECT
    // fires when it goes back to being local-only. (WTS_CONSOLE_CONNECT/DISCONNECT
    // and the logon/lock codes exist too but aren't "remote" transitions, so they're
    // not declared here.)
    public const int WM_WTSSESSION_CHANGE = 0x02B1;
    public const int WTS_REMOTE_CONNECT = 0x3;
    public const int WTS_REMOTE_DISCONNECT = 0x4;
    public const uint NOTIFY_FOR_THIS_SESSION = 0;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    // ---- Auto-hide: cursor position + "is the foreground window maximized" ----

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public const int SW_SHOWMAXIMIZED = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    // ---- Running-app awareness: enumerate top-level windows, match to a process ----

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // Also declared in the root-namespace TrayNativeMethods for its own,
    // narrower purpose (focusing the dock's own window) — duplicating the
    // P/Invoke signature here is harmless and avoids a Services -> root
    // namespace dependency just for one extern declaration.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---- Drop-onto-Recycle-Bin: send to Recycle Bin, not a permanent delete ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    public const uint FO_DELETE = 0x0003;
    public const ushort FOF_ALLOWUNDO = 0x0040;   // sends to Recycle Bin instead of a permanent delete
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_SILENT = 0x0004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    // ---- Window-position memory: capture/restore a window's rect + monitor ----

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    // ---- Clipboard shelf: notify-on-change, actual read done via WPF's own Clipboard class ----

    public const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    // ---- Recent Documents: the official per-app Jump List API ----

    public const int APPLICATION_USER_MODEL_ID_MAX_LENGTH = 130;

    [DllImport("kernel32.dll")]
    public static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint applicationUserModelIdLength, StringBuilder applicationUserModelId);

    public enum APPDOCLISTTYPE
    {
        ADLT_RECENT = 0,
        ADLT_FREQUENT = 1
    }

    public static readonly Guid CLSID_ApplicationDocumentLists = new("86bec222-30f2-47e0-9f25-60d11cd75c28");
    public static readonly Guid IID_IApplicationDocumentLists = new("3c594f9f-9f30-47a1-979a-c9e83d3d0a06");
    public static readonly Guid IID_IObjectArray = new("92ca9dcd-5622-4bba-a805-5e9f541bd8c9");
    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    public const uint CLSCTX_INPROC_SERVER = 0x1;

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [ComImport]
    [Guid("3c594f9f-9f30-47a1-979a-c9e83d3d0a06")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IApplicationDocumentLists
    {
        [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        [PreserveSig] int GetList(APPDOCLISTTYPE listtype, uint cItemsDesired, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport]
    [Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint cObjects);
        [PreserveSig] int GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    public const uint SIGDN_FILESYSPATH = 0x80058000;

    // Only declared up to GetDisplayName (the one method we call); the two
    // preceding methods must still be listed in their real vtable order,
    // same truncated-interface pattern IImageList above already uses.
    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
    }

    // ---- Media session matching: read a pinned item's own AppUserModelID ----
    //
    // A plain Win32 exe's media session usually reports a SourceAppUserModelId
    // related to its filename (the matching heuristic in MainWindow already
    // handles that). A UWP-packaged app (Windows 11's own "Media Player" among
    // them) reports a package identity instead (e.g.
    // "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" - "ZuneMusic" is
    // a legacy internal codename, confirmed via direct diagnostic against a
    // live session) that bears no resemblance to any exe filename. Windows
    // itself solves this by stamping the PKEY_AppUserModel_ID property
    // directly onto that app's Start Menu .lnk, readable via a plain
    // IPropertyStore on the shortcut path - no need to run the app first.

    public static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    public static PROPERTYKEY PKEY_AppUserModel_ID = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5
    };

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // Minimal layout: only the VT_LPWSTR case (vt == 31) is ever read here;
    // every other variant type is treated as "no AppUserModelID set" and
    // ignored, never interpreted as something else.
    [StructLayout(LayoutKind.Explicit)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    public const ushort VT_LPWSTR = 31;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetPropertyStoreFromParsingName(string pszPath, IntPtr pbc, uint flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }
}

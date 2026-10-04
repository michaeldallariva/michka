using System.Runtime.InteropServices;
using System.Text;

namespace DochkaDock.Services;

/// <summary>Resolves a .lnk shortcut to its target path via the shell's
/// IShellLink COM interface. Used purely for icon extraction: pulling the
/// icon straight off a .lnk always carries the little arrow "shortcut"
/// overlay badge, where resolving to the real target and extracting its
/// icon gives the plain, full icon instead — the same thing Explorer does
/// for Start Menu tiles.</summary>
internal static class ShellLinkResolver
{
    public static string? ResolveTarget(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(lnkPath, 0);

            var buffer = new StringBuilder(260);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            var target = buffer.ToString();

            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            // Unresolvable/corrupt shortcut — caller falls back to the .lnk
            // itself (overlay and all) rather than showing nothing.
            return null;
        }
    }

    /// <summary>A UWP-packaged app's real identity, found without needing it
    /// to already be running (unlike GetApplicationUserModelId in
    /// RunningAppsService/MediaSessionService, which needs a live process
    /// handle). Tries the official PKEY_AppUserModel_ID property first, then
    /// falls back to parsing "shell:AppsFolder\{AUMID}" out of the
    /// shortcut's own Arguments — the standard way Windows itself represents
    /// a UWP app shortcut (confirmed: this is exactly what dragging a Start
    /// Menu tile to the Desktop produces), and the more reliable path in
    /// practice: writing PKEY_AppUserModel_ID onto a freshly-created shell
    /// link via IPropertyStore.SetValue+Commit was tried and, even though it
    /// reported success, verifiably did not persist when read back (a
    /// follow-up PowerShell check against the saved file showed vt=0, no
    /// value) — so it can't be the only path. Null means neither found
    /// anything (true for most plain Win32 app shortcuts, which Windows
    /// derives an AppUserModelID for only once actually running).</summary>
    public static string? GetAppUserModelId(string path)
    {
        return GetAppUserModelIdFromPropertyStore(path) ?? GetAppUserModelIdFromShellAppsFolderArguments(path);
    }

    private static string? GetAppUserModelIdFromPropertyStore(string path)
    {
        try
        {
            var iid = NativeMethods.IID_IPropertyStore;
            if (NativeMethods.SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var storeObj) != 0)
                return null;

            var store = (NativeMethods.IPropertyStore)storeObj;
            try
            {
                var key = NativeMethods.PKEY_AppUserModel_ID;
                if (store.GetValue(ref key, out var value) != 0) return null;

                try
                {
                    return value.vt == NativeMethods.VT_LPWSTR && value.pwszVal != IntPtr.Zero
                        ? Marshal.PtrToStringUni(value.pwszVal)
                        : null;
                }
                finally
                {
                    NativeMethods.PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? GetAppUserModelIdFromShellAppsFolderArguments(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(path, 0);

            var buffer = new StringBuilder(2048);
            link.GetArguments(buffer, buffer.Capacity);
            var args = buffer.ToString();

            const string prefix = "shell:AppsFolder\\";
            var index = args.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;

            var aumid = args[(index + prefix.Length)..].Trim().Trim('"');
            return aumid.Length == 0 ? null : aumid;
        }
        catch
        {
            return null;
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(StringBuilder pszName, int cchMaxName);
        void SetDescription(string pszName);
        void GetWorkingDirectory(StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory(string pszDir);
        void GetArguments(StringBuilder pszArgs, int cchMaxPath);
        void SetArguments(string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation(string pszIconPath, int iIcon);
        void SetRelativePath(string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath(string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DochkaDock.Services;

/// <summary>Reads an app's own Jump List "recent documents" via Windows'
/// official IApplicationDocumentLists API. Only works for a currently
/// running process - the API needs a live AppUserModelID, which can only be
/// queried from an active process handle (GetApplicationUserModelId).
/// Parsing the proprietary .automaticDestinations-ms files to support
/// non-running apps too was deliberately ruled out as too fragile.</summary>
public sealed class RecentDocumentsService
{
    /// <summary>Never throws - any failure (no AppUserModelID, no Jump List
    /// data for this app, COM activation failure) just means an empty
    /// result, same "degrade to nothing shown" philosophy as IconService's
    /// blank-icon fallback.</summary>
    public IReadOnlyList<string> GetRecentDocuments(Process process, int maxItems)
    {
        try
        {
            var appId = GetApplicationUserModelId(process);
            if (appId is null) return Array.Empty<string>();

            var clsid = NativeMethods.CLSID_ApplicationDocumentLists;
            var docListIid = NativeMethods.IID_IApplicationDocumentLists;
            if (NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, NativeMethods.CLSCTX_INPROC_SERVER, ref docListIid, out var docListObj) != 0)
                return Array.Empty<string>();

            var docList = (NativeMethods.IApplicationDocumentLists)docListObj;
            try
            {
                if (docList.SetAppID(appId) != 0) return Array.Empty<string>();

                var arrayIid = NativeMethods.IID_IObjectArray;
                if (docList.GetList(NativeMethods.APPDOCLISTTYPE.ADLT_RECENT, (uint)maxItems, ref arrayIid, out var arrayObj) != 0)
                    return Array.Empty<string>();

                var array = (NativeMethods.IObjectArray)arrayObj;
                try
                {
                    return ReadPaths(array);
                }
                finally
                {
                    Marshal.ReleaseComObject(array);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(docList);
            }
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static List<string> ReadPaths(NativeMethods.IObjectArray array)
    {
        var results = new List<string>();
        if (array.GetCount(out var count) != 0) return results;

        for (uint i = 0; i < count; i++)
        {
            var itemIid = NativeMethods.IID_IShellItem;
            if (array.GetAt(i, ref itemIid, out var itemObj) != 0) continue;

            var item = (NativeMethods.IShellItem)itemObj;
            try
            {
                if (item.GetDisplayName(NativeMethods.SIGDN_FILESYSPATH, out var namePtr) != 0 || namePtr == IntPtr.Zero)
                    continue;

                var path = Marshal.PtrToStringUni(namePtr);
                NativeMethods.CoTaskMemFree(namePtr);
                if (!string.IsNullOrEmpty(path)) results.Add(path);
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }

        return results;
    }

    private static string? GetApplicationUserModelId(Process process)
    {
        try
        {
            var length = (uint)NativeMethods.APPLICATION_USER_MODEL_ID_MAX_LENGTH;
            var buffer = new StringBuilder((int)length);
            var result = NativeMethods.GetApplicationUserModelId(process.Handle, ref length, buffer);
            return result == 0 ? buffer.ToString() : null;
        }
        catch
        {
            return null;
        }
    }
}

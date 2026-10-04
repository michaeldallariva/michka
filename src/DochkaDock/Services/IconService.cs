using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DochkaDock.Models;

namespace DochkaDock.Services;

/// <summary>Extracts shell icons for dock items and caches them in memory.
/// Falls back to a generic icon on any failure so a missing/locked file
/// never stops the dock from rendering.</summary>
public sealed class IconService
{
    // Concurrent: the app picker extracts icons on a background thread while
    // the dock itself may be reading/writing this cache on the UI thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource GetIcon(DockItem item)
    {
        var cacheKey = CacheKey(item);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var icon = ExtractIcon(item) ?? GenericIcon();
        _cache[cacheKey] = icon;
        return icon;
    }

    public void InvalidateCache(DockItem item) =>
        _cache.TryRemove(CacheKey(item), out _);

    private static readonly string[] WorkspacePresetResourceNames =
        { "workspace1.ico", "workspace2.ico", "workspace3.ico", "workspace4.ico", "workspace5.ico" };

    /// <summary>One of the 5 bundled Workspace icon presets (1-5), embedded
    /// into the assembly rather than shipped as a loose file - so, unlike
    /// every other icon source here, there's no real path for SHGetFileInfo
    /// to read and the HICON has to be built directly from the resource's
    /// own bytes instead (see ExtractWorkspacePresetIcon).</summary>
    public ImageSource GetWorkspacePresetIcon(int presetId)
    {
        var cacheKey = $"WorkspacePreset|{presetId}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var icon = ExtractWorkspacePresetIcon(presetId) ?? GenericIcon();
        _cache[cacheKey] = icon;
        return icon;
    }

    /// <summary>Parses a WorkspaceIconPath value that refers to a bundled
    /// preset, in either shape: "preset:N" (current) or the full path to a
    /// loose workspaceN.ico (what older configs saved back when presets
    /// shipped as real files next to the exe - kept recognizable here so
    /// those workspaces don't silently lose their chosen icon after an
    /// update). Returns null for an app icon path, which isn't a preset at
    /// all.</summary>
    private static int? TryParsePresetId(string workspaceIconPath)
    {
        if (workspaceIconPath.StartsWith("preset:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(workspaceIconPath.AsSpan(7), out var id))
            return id;

        var match = System.Text.RegularExpressions.Regex.Match(
            Path.GetFileName(workspaceIconPath), @"^workspace(\d)\.ico$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static ImageSource? ExtractWorkspacePresetIcon(int presetId)
    {
        if (presetId < 1 || presetId > WorkspacePresetResourceNames.Length) return null;

        var resourceName = "DochkaDock.Assets.Workspaces." + WorkspacePresetResourceNames[presetId - 1];
        using var stream = typeof(IconService).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var icoBytes = memory.ToArray();

        // Same .ico layout as TrayIconManager.LoadTrayIcon (a 6-byte ICONDIR
        // header followed by one 16-byte ICONDIRENTRY per embedded image),
        // but picking the *largest* entry rather than always the first -
        // these presets ship 256/48/32/16px frames each, and the dock wants
        // the same crisp "jumbo" quality every other icon source here uses.
        if (icoBytes.Length < 6 + 16) return null;
        var imageCount = BitConverter.ToUInt16(icoBytes, 4);

        var bestOffset = -1;
        var bestSize = 0;
        var bestWidth = 0;
        var bestHeight = 0;

        for (var i = 0; i < imageCount; i++)
        {
            var entryOffset = 6 + i * 16;
            if (entryOffset + 16 > icoBytes.Length) break;

            var width = icoBytes[entryOffset] == 0 ? 256 : icoBytes[entryOffset];
            var height = icoBytes[entryOffset + 1] == 0 ? 256 : icoBytes[entryOffset + 1];
            var imageSize = BitConverter.ToInt32(icoBytes, entryOffset + 8);
            var imageOffset = BitConverter.ToInt32(icoBytes, entryOffset + 12);

            if (imageSize <= 0 || imageOffset < 0 || imageOffset + imageSize > icoBytes.Length) continue;
            if (width * height <= bestWidth * bestHeight) continue;

            bestOffset = imageOffset;
            bestSize = imageSize;
            bestWidth = width;
            bestHeight = height;
        }

        if (bestOffset < 0) return null;

        var imageBytes = new byte[bestSize];
        Array.Copy(icoBytes, bestOffset, imageBytes, 0, bestSize);

        var hIcon = NativeMethods.CreateIconFromResourceEx(imageBytes, (uint)bestSize, true, 0x00030000, bestWidth, bestHeight, 0);
        return hIcon == IntPtr.Zero ? null : BitmapFromHIcon(hIcon);
    }

    /// <summary>Shortcut/RecycleBin share a cache entry per Path, same as
    /// before. DropAction/Workspace don't have a meaningful Path (it's
    /// always empty for them), so Kind|Path alone would collide every drop
    /// action/workspace into the same entry — keyed by Id instead, which is
    /// always unique per item. Command is folded into the DropAction key too
    /// so editing it naturally picks up a fresh icon without an explicit
    /// InvalidateCache call.</summary>
    private static string CacheKey(DockItem item) => item.Kind switch
    {
        DockItemKind.DropAction => $"{item.Kind}|{item.Id}|{item.Command}",
        DockItemKind.Workspace => $"{item.Kind}|{item.Id}|{item.WorkspaceIconPath}",
        _ => $"{item.Kind}|{item.Path}"
    };

    /// <summary>For callers that just have a file/shortcut path and no
    /// DockItem (e.g. the installed-apps picker).</summary>
    public ImageSource GetIconForPath(string path)
    {
        var cacheKey = $"{DockItemKind.Shortcut}|{path}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var icon = ExtractFileIcon(path) ?? GenericIcon();
        _cache[cacheKey] = icon;
        return icon;
    }

    private static ImageSource? ExtractIcon(DockItem item) => item.Kind switch
    {
        DockItemKind.RecycleBin => ExtractRecycleBinIcon(),
        // A Drop Action's icon is just whatever its configured tool's own
        // icon is — no separate icon-picking UI needed.
        DockItemKind.DropAction => string.IsNullOrEmpty(item.Command) ? null : ExtractFileIcon(item.Command),
        // A Workspace defaults to borrowing its first app's icon, unless the
        // user explicitly picked one (an app's icon or a bundled preset) via
        // the Edit Workspace dialog.
        DockItemKind.Workspace => item.WorkspaceIconPath is { Length: > 0 } explicitIcon
            ? (TryParsePresetId(explicitIcon) is int presetId ? ExtractWorkspacePresetIcon(presetId) : ExtractFileIcon(explicitIcon))
            : item.WorkspaceAppPaths is { Count: > 0 } paths ? ExtractFileIcon(paths[0]) : null,
        _ => ExtractFileIcon(item.Path)
    };

    private static ImageSource? ExtractFileIcon(string path)
    {
        // Pulling the icon straight off a .lnk always carries the shortcut
        // arrow overlay; resolve to the real target first so the dock shows
        // the plain app icon, same as Explorer does for Start Menu tiles.
        var iconSourcePath = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? ShellLinkResolver.ResolveTarget(path) ?? path
            : path;

        var sysIconInfo = new NativeMethods.SHFILEINFO();
        var sysIconFlags = NativeMethods.SHGFI_SYSICONINDEX | NativeMethods.SHGFI_LARGEICON;
        var sysIconResult = NativeMethods.SHGetFileInfo(iconSourcePath, 0, ref sysIconInfo,
            (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), sysIconFlags);

        if (sysIconResult != IntPtr.Zero && ExtractJumboIcon(sysIconInfo.iIcon) is BitmapSource jumbo && !IsEffectivelyBlank(jumbo))
            return jumbo;

        var info = new NativeMethods.SHFILEINFO();
        var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;

        var result = NativeMethods.SHGetFileInfo(iconSourcePath, 0, ref info,
            (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        return BitmapFromHIcon(info.hIcon);
    }

    /// <summary>A handful of system tools (regedit.exe among them) have no
    /// real 256x256 icon resource — SHGetImageList/IImageList.GetIcon still
    /// report success and hand back a correctly-sized, non-null bitmap for
    /// their "jumbo" slot, just one where every pixel is fully transparent.
    /// A bitmap like that passes every earlier null-check yet renders as
    /// nothing at all, so it's caught here (by sampling actual pixel alpha)
    /// and treated as a miss, falling through to the legacy 32x32 icon
    /// instead, which doesn't have this gap.</summary>
    private static bool IsEffectivelyBlank(BitmapSource bitmap)
    {
        try
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var buffer = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(buffer, stride, 0);

            for (var i = 3; i < buffer.Length; i += 4)
            {
                if (buffer[i] > 10) return false; // found a non-transparent pixel
            }
            return true;
        }
        catch
        {
            return false; // can't tell — don't block a possibly-good icon
        }
    }

    private static ImageSource? ExtractRecycleBinIcon()
    {
        // The Recycle Bin is a virtual shell item with no real filesystem
        // path, so it's addressed by PIDL rather than a string path.
        var folderId = NativeMethods.FOLDERID_RecycleBinFolder;
        if (NativeMethods.SHGetKnownFolderIDList(ref folderId, 0, IntPtr.Zero, out var pidl) != 0 || pidl == IntPtr.Zero)
            return null;

        try
        {
            var sysIconInfo = new NativeMethods.SHFILEINFO();
            var sysIconFlags = NativeMethods.SHGFI_SYSICONINDEX | NativeMethods.SHGFI_LARGEICON | NativeMethods.SHGFI_PIDL;
            var sysIconResult = NativeMethods.SHGetFileInfoByPidl(pidl, 0, ref sysIconInfo,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), sysIconFlags);

            if (sysIconResult != IntPtr.Zero && ExtractJumboIcon(sysIconInfo.iIcon) is BitmapSource jumbo && !IsEffectivelyBlank(jumbo))
                return jumbo;

            var info = new NativeMethods.SHFILEINFO();
            var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON | NativeMethods.SHGFI_PIDL;

            var result = NativeMethods.SHGetFileInfoByPidl(pidl, 0, ref info,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;

            return BitmapFromHIcon(info.hIcon);
        }
        finally
        {
            NativeMethods.CoTaskMemFree(pidl);
        }
    }

    /// <summary>SHGFI_ICON only ever hands back a 32x32 bitmap — fine at the
    /// dock's resting size, but it's what was showing visible pixels once
    /// hover-magnify scaled it up past 2x. Windows separately keeps a
    /// 256x256 "jumbo" system image list addressed by the same per-file icon
    /// index, so this looks it up there instead and stays crisp at any
    /// magnify scale actually in use.</summary>
    private static ImageSource? ExtractJumboIcon(int sysIconIndex)
    {
        try
        {
            var iid = NativeMethods.IID_IImageList;
            if (NativeMethods.SHGetImageList(NativeMethods.SHIL_JUMBO, ref iid, out var imageList) != 0 || imageList == null)
                return null;

            try
            {
                if (imageList.GetIcon(sysIconIndex, NativeMethods.ILD_TRANSPARENT, out var hIcon) != 0 || hIcon == IntPtr.Zero)
                    return null;
                return BitmapFromHIcon(hIcon);
            }
            finally
            {
                Marshal.ReleaseComObject(imageList);
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? BitmapFromHIcon(IntPtr hIcon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private static ImageSource GenericIcon()
    {
        // Simple drawn placeholder (rounded square) — avoids bundling an
        // image asset just for the rare case a target icon can't resolve.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9F));
            dc.DrawRoundedRectangle(brush, null, new Rect(2, 2, 28, 28), 6, 6);
        }

        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

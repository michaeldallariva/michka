using System.IO;
using DochkaDock.Models;

namespace DochkaDock.Services;

/// <summary>Finds installed apps the same way the Start Menu does: by
/// walking the shortcut (.lnk) files under the all-users and per-user
/// "Start Menu\Programs" folders. Deliberately doesn't touch the registry's
/// Uninstall keys or enumerate AppX/UWP packages — those add real
/// complexity (and COM dependencies) for apps that, by and large, already
/// have a Start Menu shortcut too.</summary>
public sealed class InstalledAppsService
{
    public List<InstalledApp> GetInstalledApps()
    {
        var byName = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in GetProgramsFolders())
        {
            foreach (var shortcutPath in SafeEnumerateFiles(root, "*.lnk"))
            {
                var name = Path.GetFileNameWithoutExtension(shortcutPath);
                if (name.Length == 0) continue;
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;

                // Prefer the first occurrence (per-user shortcuts are scanned
                // first and should win over an all-users duplicate).
                byName.TryAdd(name, new InstalledApp(name, shortcutPath));
            }
        }

        return byName.Values
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> GetProgramsFolders()
    {
        var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        var allUsers = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        if (Directory.Exists(user)) yield return user;
        if (Directory.Exists(allUsers)) yield return allUsers;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern)
    {
        IEnumerable<string> files = Array.Empty<string>();
        try { files = Directory.EnumerateFiles(directory, pattern); }
        catch { /* locked/restricted folder — skip it, not the whole scan */ }
        foreach (var file in files) yield return file;

        IEnumerable<string> subdirs = Array.Empty<string>();
        try { subdirs = Directory.EnumerateDirectories(directory); }
        catch { }

        foreach (var subdir in subdirs)
        {
            // Skip reparse points/symlinks to avoid any chance of a cycle.
            var attrs = FileAttributes.None;
            try { attrs = File.GetAttributes(subdir); } catch { continue; }
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;

            foreach (var file in SafeEnumerateFiles(subdir, pattern))
                yield return file;
        }
    }
}

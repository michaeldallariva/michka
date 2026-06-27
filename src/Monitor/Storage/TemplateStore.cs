using System.Text.Json;

namespace Monitor.Storage;

/// <summary>
/// Discovery + safe file access for on-disk UI templates. Each template is a folder under the
/// templates directory (next to the db/config) holding an optional <c>template.json</c> (metadata)
/// plus <c>style.css</c> / <c>script.js</c> / <c>preview.png</c>. Users drop folders in here without
/// rebuilding the binary; the page layers the chosen template's CSS + JS over the built-in default.
/// Mirrors NexusM's <c>wwwroot/templates</c> convention.
/// </summary>
public static class TemplateStore
{
    /// <summary>Create the templates directory on first run and seed a README describing the format.</summary>
    public static void EnsureDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var readme = Path.Combine(dir, "README.txt");
            if (!File.Exists(readme)) File.WriteAllText(readme, ReadmeText);
        }
        catch { /* best effort — a missing templates dir just means "default only" */ }
    }

    /// <summary>Enumerate the templates found on disk, newest metadata wins. Never throws.</summary>
    public static List<TemplateInfo> List(string dir)
    {
        var list = new List<TemplateInfo>();
        if (!Directory.Exists(dir)) return list;

        foreach (var path in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(path);
            if (string.IsNullOrEmpty(id) || id.StartsWith('.')) continue;

            string name = id, description = "", author = "", version = "1.0";
            var metaPath = Path.Combine(path, "template.json");
            if (File.Exists(metaPath))
            {
                try
                {
                    var meta = JsonDocument.Parse(File.ReadAllText(metaPath)).RootElement;
                    if (meta.TryGetProperty("name", out var n)) name = n.GetString() ?? id;
                    if (meta.TryGetProperty("description", out var d)) description = d.GetString() ?? "";
                    if (meta.TryGetProperty("author", out var a)) author = a.GetString() ?? "";
                    if (meta.TryGetProperty("version", out var v)) version = v.GetString() ?? "1.0";
                }
                catch { /* malformed json — fall back to the folder name */ }
            }

            list.Add(new TemplateInfo(
                id, name, description, author, version,
                HasScript: File.Exists(Path.Combine(path, "script.js")),
                HasCss: File.Exists(Path.Combine(path, "style.css")),
                HasPreview: File.Exists(Path.Combine(path, "preview.png"))));
        }
        return list;
    }

    /// <summary>
    /// Resolve a request for <c>templates/{id}/{relative}</c> to an absolute file path, guarding
    /// against path traversal. Returns null if the path escapes the templates root or is missing.
    /// </summary>
    public static string? ResolveFile(string dir, string id, string relative)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(relative)) return null;

        var root = Path.GetFullPath(dir);
        var full = Path.GetFullPath(Path.Combine(root, id, relative));

        // Must stay strictly within the templates root (blocks "..", absolute paths, etc.).
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return File.Exists(full) ? full : null;
    }

    private const string ReadmeText =
        "michka UI templates\n" +
        "===================\n\n" +
        "Drop one folder per template in this directory. The folder name is the template id.\n" +
        "Select a template from the Settings page (Templates group). Empty = the built-in default.\n\n" +
        "Each template folder may contain:\n" +
        "  template.json  - metadata: { \"name\", \"description\", \"author\", \"version\" }\n" +
        "  style.css      - layered over the default stylesheet when this template is active\n" +
        "  script.js      - injected after the default app.js when this template is active\n" +
        "  preview.png    - thumbnail shown on the template card in Settings\n\n" +
        "Files are served at /templates/<folder>/<file>.\n";
}

/// <summary>Metadata for one on-disk UI template, returned by <c>GET /api/templates</c>.</summary>
public readonly record struct TemplateInfo(
    string Id, string Name, string Description, string Author, string Version,
    bool HasScript, bool HasCss, bool HasPreview);

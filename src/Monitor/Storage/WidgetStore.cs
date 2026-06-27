using System.Text.Json;

namespace Monitor.Storage;

/// <summary>
/// Discovery + safe file access for on-disk dashboard widgets (the swipe-down Widgets page's
/// app-connector cards). Each widget is a folder under the widgets directory (next to the db/config)
/// holding an optional <c>widget.json</c> (metadata) plus a <c>widget.js</c> module that registers
/// the widget's renderer/lifecycle with the page, an optional <c>widget.css</c>, and an optional
/// <c>icon.svg</c> / <c>preview.png</c>. Users drop widget folders in here without rebuilding the
/// binary — the same drop-in model as the UI templates (<see cref="TemplateStore"/>), so people can
/// develop and ship their own widget kinds. The folder name is the widget <c>type</c> id.
/// </summary>
public static class WidgetStore
{
    /// <summary>Create the widgets directory on first run and seed a README describing the format.</summary>
    public static void EnsureDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var readme = Path.Combine(dir, "README.txt");
            if (!File.Exists(readme)) File.WriteAllText(readme, ReadmeText);
        }
        catch { /* best effort — a missing widgets dir just means "no widgets available" */ }
    }

    /// <summary>Enumerate the widgets found on disk. Never throws.</summary>
    public static List<WidgetInfo> List(string dir)
    {
        var list = new List<WidgetInfo>();
        if (!Directory.Exists(dir)) return list;

        foreach (var path in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(path);
            if (string.IsNullOrEmpty(id) || id.StartsWith('.')) continue;

            // A widget must ship its renderer module — without widget.js there's nothing to mount.
            if (!File.Exists(Path.Combine(path, "widget.js"))) continue;

            string name = id, description = "", author = "", version = "1.0", tag = "";
            var metaPath = Path.Combine(path, "widget.json");
            if (File.Exists(metaPath))
            {
                try
                {
                    var meta = JsonDocument.Parse(File.ReadAllText(metaPath)).RootElement;
                    if (meta.TryGetProperty("name", out var n)) name = n.GetString() ?? id;
                    if (meta.TryGetProperty("description", out var d)) description = d.GetString() ?? "";
                    if (meta.TryGetProperty("author", out var a)) author = a.GetString() ?? "";
                    if (meta.TryGetProperty("version", out var v)) version = v.GetString() ?? "1.0";
                    if (meta.TryGetProperty("tag", out var g)) tag = g.GetString() ?? "";
                }
                catch { /* malformed json — fall back to the folder name */ }
            }

            list.Add(new WidgetInfo(
                id, name, description, author, version, tag,
                HasCss: File.Exists(Path.Combine(path, "widget.css")),
                HasIcon: File.Exists(Path.Combine(path, "icon.svg")),
                HasPreview: File.Exists(Path.Combine(path, "preview.png"))));
        }
        return list;
    }

    /// <summary>
    /// Resolve a request for <c>widgets/{id}/{relative}</c> to an absolute file path, guarding
    /// against path traversal. Returns null if the path escapes the widgets root or is missing.
    /// </summary>
    public static string? ResolveFile(string dir, string id, string relative)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(relative)) return null;

        var root = Path.GetFullPath(dir);
        var full = Path.GetFullPath(Path.Combine(root, id, relative));

        // Must stay strictly within the widgets root (blocks "..", absolute paths, etc.).
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return File.Exists(full) ? full : null;
    }

    private const string ReadmeText =
        "michka dashboard widgets\n" +
        "========================\n\n" +
        "Drop one folder per widget in this directory. The folder name is the widget type id.\n" +
        "Widgets appear in the swipe-down Widgets page chooser; selections are per host.\n\n" +
        "Each widget folder may contain:\n" +
        "  widget.json - metadata: { \"name\", \"description\", \"author\", \"version\", \"tag\" }\n" +
        "  widget.js   - REQUIRED. Registers the widget renderer/lifecycle (see the contract below)\n" +
        "  widget.css  - optional styles, injected once when the widget type is first used\n" +
        "  icon.svg    - optional icon shown on the chooser entry\n" +
        "  preview.png - optional thumbnail\n\n" +
        "Files are served at /widgets/<folder>/<file>.\n\n" +
        "widget.js contract\n" +
        "------------------\n" +
        "The script is injected once per page; it must register itself by id:\n\n" +
        "  Michka.widget(\"my-widget\", {\n" +
        "    // Build the card body. Return an HTML string or a DOM node. (required)\n" +
        "    render(ctx) { return `<div>Hello ${ctx.esc(ctx.host)}</div>`; },\n" +
        "    // Start timers / fetch data after the body is in the DOM. (optional)\n" +
        "    mount(ctx) { /* ctx.el is the .widget-body element */ },\n" +
        "    // Tear down timers when the card is removed or the page closes. (optional)\n" +
        "    unmount(ctx) { },\n" +
        "  });\n\n" +
        "ctx gives you: host (selected host name), widget (the instance { id, type }),\n" +
        "el (the body element, mount/unmount only), meta (the widget.json fields), and helpers\n" +
        "t (i18n), esc (HTML-escape), fmtBytes, and api(path) (fetch JSON from the hub, same origin).\n\n" +
        "Kiosk helpers (window.Michka)\n" +
        "-----------------------------\n" +
        "For rich widgets that take over the screen (e.g. a media player), the kiosk also exposes:\n" +
        "  Michka.osk(mode, inputEl, onInput) - pop the on-screen keyboard onto an <input>.\n" +
        "      mode is \"text\" (QWERTY) or \"num\" (0-9 keypad). Set inputEl.inputMode=\"none\" so the\n" +
        "      native keyboard doesn't compete; onInput() runs after each keystroke. Michka.hideOsk()\n" +
        "      dismisses it.\n" +
        "  Michka.keepAwake(on) - while true, the screensaver won't activate (so a playing video isn't\n" +
        "      covered). Call (true) while your full-screen UI is open and (false) when it closes.\n";
}

/// <summary>Metadata for one on-disk widget, returned by <c>GET /api/widgets</c>.</summary>
public readonly record struct WidgetInfo(
    string Id, string Name, string Description, string Author, string Version, string Tag,
    bool HasCss, bool HasIcon, bool HasPreview);

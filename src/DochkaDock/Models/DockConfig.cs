namespace DochkaDock.Models;

/// <summary>A remembered window position/size/monitor/maximized-state for a
/// Shortcut item, captured via "Save Window Position" and reapplied the next
/// time that item is freshly launched (never on a plain focus-click).</summary>
public sealed class WindowPlacementData
{
    public string? MonitorDevice { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Plain-data shape persisted to config.json. Kept separate from
/// <see cref="DockItem"/> so the UI model (icons, change notification) never
/// touches serialization.</summary>
public sealed class DockItemData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DockItemKind Kind { get; set; } = DockItemKind.Shortcut;
    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    // Shortcut only.
    public WindowPlacementData? SavedPlacement { get; set; }

    // DropAction only. AcceptExtensions is a comma-separated list like
    // ".jpg,.jpeg"; null/empty means "accept any file".
    public string? Command { get; set; }
    public string? ArgumentsTemplate { get; set; }
    public string? AcceptExtensions { get; set; }

    // Workspace only. WorkspaceIconPath is null for the default behavior
    // (icon borrowed from the first selected app); set it to override with
    // either an app's exe/lnk path or one of the bundled preset .ico paths.
    public List<string>? WorkspaceAppPaths { get; set; }
    public string? WorkspaceIconPath { get; set; }
}

public sealed class DockConfig
{
    public int SchemaVersion { get; set; } = 1;
    public double BaseIconSize { get; set; } = 48;
    public double MagnifyScale { get; set; } = 1.8;
    public bool AutoHideEnabled { get; set; } = false;
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "Default";
    public List<DockItemData> Items { get; set; } = new();
}

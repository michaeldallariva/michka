using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DochkaDock.Models;

public enum DockItemKind
{
    Shortcut,
    RecycleBin,
    DropAction,
    Workspace
}

/// <summary>
/// A single pinned item on the dock. Mutable at runtime (name, icon) but
/// identity is tracked by <see cref="Id"/> so drag-reorder and persistence
/// stay stable even if the target path is edited later.
/// </summary>
public sealed class DockItem : INotifyPropertyChanged
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DockItemKind Kind { get; init; } = DockItemKind.Shortcut;

    private string _path = string.Empty;
    public string Path
    {
        get => _path;
        set { _path = value; OnChanged(); }
    }

    private string _displayName = string.Empty;
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnChanged(); }
    }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnChanged(); }
    }

    // Runtime-only — never persisted to config.json (DockItemData has no
    // equivalent fields). ResolvedExecutablePath is the .lnk -> target
    // resolution, computed once when the item is loaded/added rather than
    // repeatedly by the running-apps poll. RunningWindowCount is refreshed by
    // that poll; 0 means not running.
    public string? ResolvedExecutablePath { get; set; }

    // The explicit AppUserModelID read off the pinned .lnk itself (see
    // ShellLinkResolver.GetAppUserModelId) — only ever set for apps whose
    // shortcut actually carries one (UWP-packaged apps always do; most
    // plain Win32 apps don't). Used for exact media-session matching instead
    // of the filename-heuristic fallback, which can't work for a UWP app's
    // package-identity-style SourceAppUserModelId.
    public string? AppUserModelId { get; set; }

    // Mirrors of DockItemData's per-kind fields (see there for which kind
    // each applies to). Not bound in any DataTemplate, so plain properties
    // like ResolvedExecutablePath rather than the OnChanged() pattern below.
    public WindowPlacementData? SavedPlacement { get; set; }
    public string? Command { get; set; }
    public string? ArgumentsTemplate { get; set; }
    public string? AcceptExtensions { get; set; }
    public List<string>? WorkspaceAppPaths { get; set; }
    public string? WorkspaceIconPath { get; set; }

    // Bindable so "Clear Saved Position" can show/hide itself via a
    // DataTrigger the same way RunningWindowCount-driven items already do.
    private bool _hasSavedPlacement;
    public bool HasSavedPlacement
    {
        get => _hasSavedPlacement;
        set { _hasSavedPlacement = value; OnChanged(); }
    }

    private int _runningWindowCount;
    public int RunningWindowCount
    {
        get => _runningWindowCount;
        set { _runningWindowCount = value; OnChanged(); }
    }

    // Working-set RAM for the running process, in MB; 0 when not running.
    // Refreshed on the same poll as RunningWindowCount rather than read
    // fresh each time a context menu opens, so it's simple binding, not an
    // imperative "find this control and set its text" dance.
    private long _runningMemoryMb;
    public long RunningMemoryMb
    {
        get => _runningMemoryMb;
        set { _runningMemoryMb = value; OnChanged(); }
    }

    // Recomputed by MainWindow.UpdateGroupDividers() on every add/remove/
    // reorder — true only for whichever item is currently the first
    // Workspace in _items, so the dock can draw a separator before it.
    private bool _showGroupDividerBefore;
    public bool ShowGroupDividerBefore
    {
        get => _showGroupDividerBefore;
        set { _showGroupDividerBefore = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

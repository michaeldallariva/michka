using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DochkaDock.Models;
using DochkaDock.Services;

namespace DochkaDock;

public partial class MainWindow : Window
{
    private const double MagnifyRadius = 110; // px, falloff distance either side of cursor
    private const double VerticalGap = 8;      // gap above the taskbar

    private readonly ConfigService _configService = new();
    private readonly IconService _iconService = new();
    private readonly AutoStartService _autoStartService = new();
    private readonly RunningAppsService _runningAppsService = new();
    private readonly WindowPlacementService _windowPlacementService = new();
    private readonly RecentDocumentsService _recentDocumentsService = new();
    private readonly MediaSessionService _mediaSessionService = new();
    private MediaSessionInfo? _currentMediaSession;
    private readonly DispatcherTimer _mediaSessionTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ObservableCollection<DockItem> _items = new();

    private DockConfig _config = new();

    // Drag state (reorder within the dock)
    private Point _dragStartPoint;
    private Border? _dragCandidate;
    private bool _dragInProgress;

    // Magnify: a continuous per-frame follow toward these targets (see
    // OnMagnifyRendering) rather than a WPF animation restarted on every
    // mouse-move event.
    private readonly Dictionary<Border, (double Scale, double Shift)> _magnifyTargets = new();
    private bool _magnifyRenderingHooked;
    private TimeSpan _lastRenderingTime;

    // Auto-hide
    private DispatcherTimer? _autoHideTimer;
    private bool _dockHidden;
    private DateTime? _awayFromDockSince;

    // Running-apps poll — coarser than auto-hide's since process/window state
    // changes far less often than cursor position.
    private readonly DispatcherTimer _runningAppsTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    // Shelf: session-only, never persisted. Public so the Popup in
    // MainWindow.xaml (whose DataContext is explicitly rebound to this
    // Window, since the Window's own DataContext is _items) can bind to them.
    private readonly ObservableCollection<ShelfFileItem> _shelfFiles = new();
    private readonly ObservableCollection<ClipboardHistoryItem> _clipboardHistory = new();
    private const int MaxClipboardHistory = 20;
    private ClipboardMonitorService? _clipboardMonitor;

    // Two independent reasons the dock must stay invisible, both resolving to
    // the same Hide()/Show() call — independent of auto-hide, which slides
    // the window off-screen via Top and never touches Visibility:
    //  1. This session itself is being viewed over Remote Desktop (someone
    //     RDP'd into this machine) — RemoteSessionMonitorService, live for
    //     the session's whole lifetime since a session can flip between
    //     local and remote.
    //  2. A Remote Desktop client is focused on this machine (this machine
    //     is the one doing the RDP'ing) — otherwise this machine's own
    //     always-on-top dock bleeds on top of whatever remote desktop is
    //     being viewed, especially once that RDP window goes full-screen.
    // Either reason alone must keep the dock hidden; only clearing both
    // should bring it back, hence two separate flags feeding one combiner
    // (UpdateRemoteDesktopVisibility) rather than each calling Hide()/Show()
    // directly and potentially undoing the other's Hide().
    private RemoteSessionMonitorService? _remoteSessionMonitor;
    private bool _hiddenForRemoteSession;
    private bool _hiddenForLocalRdpClientForeground;
    private readonly DispatcherTimer _rdpForegroundTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public ObservableCollection<ShelfFileItem> ShelfFiles => _shelfFiles;
    public ObservableCollection<ClipboardHistoryItem> ClipboardHistory => _clipboardHistory;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _items;
        LoadFromConfig();
        UpdateGroupDividers();
        UpdateAutoHide();

        // Subscribed after the initial load (which already got its own
        // UpdateGroupDividers() call above) so each item added during that
        // load doesn't trigger a redundant recompute of the whole list.
        _items.CollectionChanged += (_, _) => UpdateGroupDividers();

        _shelfFiles.CollectionChanged += (_, _) => UpdateShelfBadge();

        _runningAppsTimer.Tick += RunningAppsTimer_Tick;
        _runningAppsTimer.Start();

        _mediaSessionTimer.Tick += async (_, _) => _currentMediaSession = await _mediaSessionService.GetCurrentSessionAsync();
        _mediaSessionTimer.Start();

        // Always runs, unlike auto-hide's timer — this isn't an opt-in
        // feature, the dock must never bleed onto a Remote Desktop view
        // regardless of the auto-hide setting.
        _rdpForegroundTimer.Tick += RdpForegroundTimer_Tick;
        _rdpForegroundTimer.Start();
    }

    /// <summary>Marks whichever item is currently the first Workspace in
    /// _items so the dock can draw a separator before it, the same visual
    /// idea as the fixed divider before the gear icon — keeps workspaces
    /// visually distinct from individual pinned apps. Recomputed on every
    /// add/remove/reorder via the _items.CollectionChanged subscription
    /// above, since any of those can change which item is "first".</summary>
    private void UpdateGroupDividers()
    {
        var foundFirstWorkspace = false;
        foreach (var item in _items)
        {
            var isFirstWorkspace = item.Kind == DockItemKind.Workspace && !foundFirstWorkspace;
            item.ShowGroupDividerBefore = isFirstWorkspace;
            if (isFirstWorkspace) foundFirstWorkspace = true;
        }
    }

    private void RunningAppsTimer_Tick(object? sender, EventArgs e)
    {
        foreach (var item in _items)
        {
            if (item.Kind != DockItemKind.Shortcut || item.ResolvedExecutablePath is null)
                continue;

            var windows = _runningAppsService.FindWindows(item.ResolvedExecutablePath);
            item.RunningWindowCount = windows.Count;
            item.RunningMemoryMb = windows.Count > 0
                ? _runningAppsService.GetWorkingSetBytes(windows[0]) / (1024 * 1024)
                : 0;
        }
    }

    // ---- Loading / persistence ----------------------------------------

    private void LoadFromConfig()
    {
        _config = _configService.Load();
        _items.Clear();

        foreach (var data in _config.Items)
        {
            if (data.Kind == DockItemKind.Shortcut && !File.Exists(data.Path))
                continue; // skip items whose target has disappeared
            // Only reject a DropAction whose Command is a rooted path that no
            // longer exists — a bare command name (e.g. "magick") is meant to
            // resolve via PATH at run time and can't be validated up front.
            if (data.Kind == DockItemKind.DropAction && Path.IsPathRooted(data.Command) && !File.Exists(data.Command))
                continue;

            var item = new DockItem
            {
                Id = data.Id,
                Kind = data.Kind,
                Path = data.Path,
                DisplayName = data.DisplayName,
                SavedPlacement = data.SavedPlacement,
                Command = data.Command,
                ArgumentsTemplate = data.ArgumentsTemplate,
                AcceptExtensions = data.AcceptExtensions,
                WorkspaceAppPaths = data.WorkspaceAppPaths,
                WorkspaceIconPath = data.WorkspaceIconPath,
                HasSavedPlacement = data.SavedPlacement is not null
            };
            item.Icon = _iconService.GetIcon(item);
            item.ResolvedExecutablePath = ResolveExecutablePath(item);
            item.AppUserModelId = ResolveAppUserModelId(item);
            _items.Add(item);
        }
    }

    /// <summary>The .lnk -> target resolution, same one IconService uses
    /// internally for icon extraction, exposed here too so running-app
    /// matching compares against the real .exe rather than the shortcut.
    /// Computed once per item rather than on every poll tick.</summary>
    private static string? ResolveExecutablePath(DockItem item)
    {
        if (item.Kind != DockItemKind.Shortcut) return null;

        return item.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? ShellLinkResolver.ResolveTarget(item.Path) ?? item.Path
            : item.Path;
    }

    /// <summary>Read off the pinned path itself (the .lnk, if that's what
    /// was pinned), not the resolved target — a UWP app's AppUserModelID
    /// property lives on the shortcut; its "target" doesn't meaningfully
    /// resolve to a normal exe path the way a Win32 app's does.</summary>
    private static string? ResolveAppUserModelId(DockItem item) =>
        item.Kind == DockItemKind.Shortcut ? ShellLinkResolver.GetAppUserModelId(item.Path) : null;

    private void SaveConfig()
    {
        _config.Items = _items.Select(i => new DockItemData
        {
            Id = i.Id,
            Kind = i.Kind,
            Path = i.Path,
            DisplayName = i.DisplayName,
            SavedPlacement = i.SavedPlacement,
            Command = i.Command,
            ArgumentsTemplate = i.ArgumentsTemplate,
            AcceptExtensions = i.AcceptExtensions,
            WorkspaceAppPaths = i.WorkspaceAppPaths,
            WorkspaceIconPath = i.WorkspaceIconPath
        }).ToList();

        _configService.Save(_config);
    }

    // ---- Window positioning --------------------------------------------

    private void RootWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RepositionWindow();

        // Needs the window's native HWND, which doesn't exist until about
        // now - constructing it any earlier (e.g. in the constructor) would
        // find WindowInteropHelper's Handle still zero.
        _clipboardMonitor = new ClipboardMonitorService(this);
        _clipboardMonitor.ClipboardChanged += OnClipboardChanged;

        _remoteSessionMonitor = new RemoteSessionMonitorService(this);
        _remoteSessionMonitor.RemoteSessionChanged += OnRemoteSessionChanged;
        // Covers the case where the session is already remote by the time the
        // dock launches (e.g. autostart firing inside a session that only
        // ever exists as an RDP session) - the live hook above only reports
        // *transitions*, not the state at the moment it was registered.
        _hiddenForRemoteSession = RemoteSessionMonitorService.IsCurrentSessionRemote();
        UpdateRemoteDesktopVisibility();
    }

    private void OnRemoteSessionChanged(bool isRemote)
    {
        _hiddenForRemoteSession = isRemote;
        UpdateRemoteDesktopVisibility();
    }

    /// <summary>Polls at the same cadence regardless of auto-hide's setting
    /// (this isn't optional behavior) for whether the foreground window
    /// belongs to Windows' built-in Remote Desktop Connection client
    /// (mstsc.exe) — covers both windowed and full-screen RDP sessions.
    /// Doesn't recognize third-party or Microsoft Store Remote Desktop
    /// clients (different process names) — a real, narrower gap, not a
    /// design choice; mstsc.exe is what this was built and tested against.</summary>
    private void RdpForegroundTimer_Tick(object? sender, EventArgs e)
    {
        var isRdpForeground = ForegroundWindowBelongsToRdpClient();
        if (isRdpForeground == _hiddenForLocalRdpClientForeground) return;

        _hiddenForLocalRdpClientForeground = isRdpForeground;
        UpdateRemoteDesktopVisibility();
    }

    private static bool ForegroundWindowBelongsToRdpClient()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("mstsc", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Single combiner for both hide reasons above, so neither one's
    /// Show() can undo the other's still-active Hide().</summary>
    private void UpdateRemoteDesktopVisibility()
    {
        var shouldBeHidden = _hiddenForRemoteSession || _hiddenForLocalRdpClientForeground;
        if (shouldBeHidden && Visibility == Visibility.Visible)
            Hide();
        else if (!shouldBeHidden && Visibility != Visibility.Visible)
            Show();
    }

    private void OnClipboardChanged(ClipboardSnapshot snapshot)
    {
        _clipboardHistory.Insert(0, new ClipboardHistoryItem(snapshot.Text, snapshot.Image, DateTime.Now));
        while (_clipboardHistory.Count > MaxClipboardHistory)
            _clipboardHistory.RemoveAt(_clipboardHistory.Count - 1);
    }

    protected override void OnClosed(EventArgs e)
    {
        _clipboardMonitor?.Dispose();
        _remoteSessionMonitor?.Dispose();
        _rdpForegroundTimer.Stop();
        base.OnClosed(e);
    }

    private void RootWindow_SizeChanged(object sender, SizeChangedEventArgs e) => RepositionWindow();

    private void RepositionWindow()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
        // The pill sits flush against the window's bottom edge (no bottom
        // margin), with the window's extra height reserved above it purely
        // as unclipped headroom for icons to scale into on hover. So anchor
        // using the window's own height, not the pill's. Skip it while
        // auto-hidden — the window is deliberately slid off-screen then, and
        // a resize (e.g. adding an icon) shouldn't snap it back early.
        if (!_dockHidden)
            Top = RestingTop;
    }

    // Shared by RepositionWindow and the auto-hide slide so the "resting"
    // formula only lives in one place.
    private double RestingTop => SystemParameters.WorkArea.Bottom - VerticalGap - ActualHeight;

    // ---- Auto-hide -------------------------------------------------------

    private const double AutoHideRevealZonePx = 6;  // how close to the screen's bottom edge triggers a reveal
    private const double AutoHideGraceMs = 600;      // delay before hiding, so briefly glancing away doesn't flicker it
    private static readonly TimeSpan AutoHideSlideDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>Starts or stops the polling timer to match the current
    /// setting. A DispatcherTimer polling GetCursorPos every 150ms, rather
    /// than a global low-level mouse hook, because a hook runs in every
    /// process's message loop system-wide and has to stay fast to avoid
    /// visible input lag — overkill for "is the mouse near the bottom edge",
    /// which a cheap periodic check answers just as well.</summary>
    private void UpdateAutoHide()
    {
        if (_config.AutoHideEnabled)
        {
            if (_autoHideTimer is null)
            {
                _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _autoHideTimer.Tick += AutoHideTimer_Tick;
            }
            _autoHideTimer.Start();
        }
        else
        {
            _autoHideTimer?.Stop();
            _awayFromDockSince = null;
            if (_dockHidden) ExpandDock();
        }
    }

    /// <summary>"Smart" here means it only hides once there's actually a
    /// reason to reclaim the screen space — a maximized window in the
    /// foreground — rather than vanishing the instant the mouse drifts away
    /// while the user is just sitting on an empty desktop. It always reveals
    /// unconditionally on approach to the bottom edge, same as Windows'
    /// own auto-hide taskbar.</summary>
    private void AutoHideTimer_Tick(object? sender, EventArgs e)
    {
        if (Visibility != Visibility.Visible) return;

        NativeMethods.GetCursorPos(out var cursor);
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        var mouseNearBottomEdge = cursor.Y >= screenHeight - AutoHideRevealZonePx;

        if (_dockHidden)
        {
            if (mouseNearBottomEdge) ExpandDock();
            return;
        }

        var mouseOverDock = cursor.X >= Left && cursor.X <= Left + ActualWidth &&
                             cursor.Y >= Top && cursor.Y <= Top + ActualHeight;

        if (mouseOverDock || !ForegroundWindowIsMaximized())
        {
            _awayFromDockSince = null;
            return;
        }

        _awayFromDockSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _awayFromDockSince.Value).TotalMilliseconds >= AutoHideGraceMs)
            CollapseDock();
    }

    /// <summary>Only primary-monitor-aware, same limitation RepositionWindow
    /// already has: a window maximized on a different monitor is treated
    /// the same as one maximized on the dock's own screen.</summary>
    private static bool ForegroundWindowIsMaximized()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        var placement = new NativeMethods.WINDOWPLACEMENT { length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
        return NativeMethods.GetWindowPlacement(hwnd, ref placement) && placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED;
    }

    private void CollapseDock()
    {
        _dockHidden = true;
        AnimateTop(SystemParameters.PrimaryScreenHeight + 20); // fully below the monitor, past any taskbar overlap
    }

    private void ExpandDock()
    {
        _dockHidden = false;
        _awayFromDockSince = null;
        AnimateTop(RestingTop);
    }

    private void AnimateTop(double targetTop)
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        BeginAnimation(TopProperty, new DoubleAnimation(Top, targetTop, AutoHideSlideDuration) { EasingFunction = ease });
    }

    // ---- Magnify on hover ------------------------------------------------

    private void IconsPanel_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not StackPanel panel) return;
        var mouseX = e.GetPosition(panel).X;

        var items = GetBaseCenterPositions(panel)
            .Select(p =>
            {
                var distance = Math.Abs(p.Center - mouseX);
                var falloff = Math.Max(0, 1 - distance / MagnifyRadius);
                // Smooth easing instead of a linear ramp (cosine ease-out).
                falloff = 1 - Math.Cos(falloff * Math.PI / 2);
                var scale = 1 + (_config.MagnifyScale - 1) * falloff;
                var width = double.IsNaN(p.Border.Width) ? p.Border.ActualWidth : p.Border.Width;
                return (Border: p.Border, Center: p.Center, Scale: scale, ExtraWidth: (scale - 1) * width);
            })
            .ToList();

        // A growing icon expands symmetrically around its own center, so it
        // encroaches on both neighbors. Push every other icon outward by
        // half the extra width of each icon between it and that neighbor —
        // the same cumulative "make room" effect the real dock uses — so
        // magnified icons don't overlap their neighbors.
        foreach (var item in items)
        {
            var shift = 0.0;
            foreach (var other in items)
            {
                if (other.Border == item.Border) continue;
                if (other.Center < item.Center) shift += other.ExtraWidth / 2;
                else if (other.Center > item.Center) shift -= other.ExtraWidth / 2;
            }

            SetMagnifyTarget(item.Border, item.Scale, shift);
        }
    }

    private void IconsPanel_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not StackPanel panel) return;
        foreach (var child in GetIconBorders(panel))
            SetMagnifyTarget(child, 1.0, 0.0);
    }

    private void SetMagnifyTarget(Border border, double scale, double shift)
    {
        _magnifyTargets[border] = (scale, shift);
        if (!_magnifyRenderingHooked)
        {
            _magnifyRenderingHooked = true;
            _lastRenderingTime = TimeSpan.Zero;
            CompositionTarget.Rendering += OnMagnifyRendering;
        }
    }

    private static readonly TimeSpan MagnifySmoothingTimeConstant = TimeSpan.FromMilliseconds(60);

    /// <summary>Drives magnify scale/shift with a continuous per-frame
    /// exponential "follow" toward the target, instead of restarting a WPF
    /// DoubleAnimation on every MouseMove event. MouseMove doesn't fire at a
    /// steady rate, and retargeting a fresh animation clock on every tiny
    /// mouse delta discards velocity each time — the ease-out curve keeps
    /// restarting from zero, which is what actually produced the residual
    /// shakiness even after fixing the position feedback loop (see
    /// GetBaseCenterPositions). A single continuous recurrence relation,
    /// ticking on the render clock instead of the irregular mouse-move rate,
    /// has no restart to produce that stutter.</summary>
    private void OnMagnifyRendering(object? sender, EventArgs e)
    {
        var renderingTime = ((RenderingEventArgs)e).RenderingTime;
        var dt = _lastRenderingTime == TimeSpan.Zero ? TimeSpan.FromMilliseconds(16) : renderingTime - _lastRenderingTime;
        _lastRenderingTime = renderingTime;

        var alpha = 1 - Math.Exp(-dt.TotalSeconds / MagnifySmoothingTimeConstant.TotalSeconds);
        var allSettled = true;

        foreach (var (border, target) in _magnifyTargets)
        {
            var (currentScale, currentShift) = GetCurrentTransform(border);
            var scaleGap = target.Scale - currentScale;
            var shiftGap = target.Shift - currentShift;

            double newScale, newShift;
            if (Math.Abs(scaleGap) < 0.001 && Math.Abs(shiftGap) < 0.05)
            {
                newScale = target.Scale;
                newShift = target.Shift;
            }
            else
            {
                newScale = currentScale + scaleGap * alpha;
                newShift = currentShift + shiftGap * alpha;
                allSettled = false;
            }

            border.RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(newScale, newScale), new TranslateTransform(newShift, 0) }
            };
            SetItemZIndex(border, newScale > 1.02 ? 10 : 0);
        }

        if (allSettled)
        {
            CompositionTarget.Rendering -= OnMagnifyRendering;
            _magnifyRenderingHooked = false;
            _lastRenderingTime = TimeSpan.Zero;
        }
    }

    /// <summary>The ItemsControl wraps each DataTemplate instance in an
    /// implicit ContentPresenter, so the panel's direct Children are
    /// ContentPresenters, not the Border the template defines — this walks
    /// one level in to find it.</summary>
    /// <summary>The icon-level Border carries Tag="{Binding}" (used elsewhere
    /// for drag/click handling); the group-divider Border added alongside it
    /// does not. Matching on that, rather than "the first Border found", is
    /// what lets the divider live as a plain sibling Border in the same
    /// per-item StackPanel without being mistaken for the icon itself.</summary>
    private static IEnumerable<Border> GetIconBorders(Panel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is Border { Tag: DockItem } border) yield return border;
            else if (child is DependencyObject obj && FindBorder(obj) is Border nested) yield return nested;
        }
    }

    private static Border? FindBorder(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border { Tag: DockItem } border) return border;
            if (FindBorder(child) is Border nested) return nested;
        }
        return null;
    }

    /// <summary>A magnified icon needs to render above its unmagnified
    /// neighbors, but Panel.ZIndex only affects ordering among an element's
    /// own immediate siblings under its nearest panel ancestor. IconBorder's
    /// immediate parent is now the per-item wrapping StackPanel (just it and
    /// the group-divider, which never overlap it), not the ItemsControl's
    /// own panel where the actual neighbor-to-neighbor overlap happens — so
    /// this walks up to the ContentPresenter the ItemsControl generated
    /// (the real direct child of that panel) and sets ZIndex there instead.</summary>
    private static void SetItemZIndex(Border border, int zIndex)
    {
        DependencyObject? current = border;
        while (current is not null and not ContentPresenter)
            current = VisualTreeHelper.GetParent(current);

        if (current is UIElement presenter)
            Panel.SetZIndex(presenter, zIndex);
    }

    // Must match the group-divider Border's own footprint in MainWindow.xaml
    // (Width="1" Margin="6,0" = 6 + 1 + 6). GetBaseCenterPositions can't see
    // that Border directly (GetIconBorders deliberately skips it, see above),
    // so its width is added in here explicitly whenever the accumulation
    // reaches an item the divider sits in front of.
    private const double GroupDividerWidth = 13;

    /// <summary>Each icon's center, computed purely from layout (margin +
    /// width accumulation along the StackPanel), never from
    /// TranslatePoint/TransformToAncestor on the live visual. Reading a
    /// transformed position while computing the next transform is a
    /// feedback loop — last frame's magnify shift leaks into this frame's
    /// distance-to-cursor calculation — which is what made hover-magnify
    /// look shaky. Also reused by drag-reorder so drop-index detection and
    /// the FLIP slide below are based on the same untransformed geometry.</summary>
    private static List<(Border Border, double Center)> GetBaseCenterPositions(StackPanel panel)
    {
        var positions = new List<(Border Border, double Center)>();
        double x = 0;
        foreach (var border in GetIconBorders(panel))
        {
            if (border.Tag is DockItem { ShowGroupDividerBefore: true })
                x += GroupDividerWidth;

            var width = double.IsNaN(border.Width) ? border.ActualWidth : border.Width;
            x += border.Margin.Left;
            positions.Add((border, x + width / 2));
            x += width + border.Margin.Right;
        }
        return positions;
    }

    private static readonly TimeSpan ReorderSlideDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>WPF freezes Freezables (like a ScaleTransform) declared
    /// inline in a DataTemplate as a sharing optimization, so mutating or
    /// animating that instance throws. OnMagnifyRendering and SlideIntoPlace
    /// both read the current (possibly mid-animation) values first, then
    /// always replace RenderTransform with a fresh, code-owned (never
    /// frozen) group.</summary>
    private static (double Scale, double Shift) GetCurrentTransform(Border border) =>
        border.RenderTransform is TransformGroup g
                && g.Children.Count == 2
                && g.Children[0] is ScaleTransform s
                && g.Children[1] is TranslateTransform t
            ? (s.ScaleX, t.X)
            : (1.0, 0.0);

    /// <summary>FLIP (First-Last-Invert-Play), the same technique dnd-kit's
    /// sortable uses for reorder animations: called right after the
    /// ObservableCollection reorders and the panel has re-arranged. For each
    /// icon that actually moved, the transform is first set so it still sits
    /// exactly where it visually was a moment ago (no snap), then animated
    /// back down to its real position — so the icon slides into its new slot
    /// instead of teleporting there.</summary>
    private static void SlideIntoPlace(StackPanel panel, List<(Border Border, double Center)> oldPositions)
    {
        var oldCenterByBorder = oldPositions.ToDictionary(p => p.Border, p => p.Center);

        foreach (var (border, newCenter) in GetBaseCenterPositions(panel))
        {
            if (!oldCenterByBorder.TryGetValue(border, out var oldCenter)) continue;

            var delta = oldCenter - newCenter;
            if (Math.Abs(delta) < 0.5) continue;

            var (scale, shift) = GetCurrentTransform(border);

            var scaleTransform = new ScaleTransform(scale, scale);
            var translateTransform = new TranslateTransform(shift + delta, 0);
            border.RenderTransform = new TransformGroup
            {
                Children = { scaleTransform, translateTransform }
            };

            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var anim = new DoubleAnimation(shift + delta, shift, ReorderSlideDuration) { EasingFunction = ease };
            translateTransform.BeginAnimation(TranslateTransform.XProperty, anim);
        }
    }

    // ---- Click to launch / drag to reorder -------------------------------

    private void Icon_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragCandidate = sender as Border;
        _dragInProgress = false;
    }

    private void Icon_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed || _dragInProgress)
            return;

        var pos = e.GetPosition(null);
        var diff = _dragStartPoint - pos;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragInProgress = true;
        var border = _dragCandidate;
        if (border?.Tag is not DockItem item) return;

        var data = new DataObject("DockItemReorder", item);
        DragDrop.DoDragDrop(border, data, DragDropEffects.Move);

        _dragCandidate = null;
        _dragInProgress = false;
    }

    private void Icon_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _dragInProgress;
        var candidate = _dragCandidate;
        _dragCandidate = null;
        _dragInProgress = false;

        if (wasDragging) return;
        if (candidate != sender) return;
        if (candidate?.Tag is not DockItem item) return;

        switch (item.Kind)
        {
            case DockItemKind.Workspace:
                LaunchWorkspace(item);
                break;
            case DockItemKind.DropAction:
                RunDropActionViaFilePicker(item);
                break;
            default:
                LaunchOrFocusItem(item, candidate);
                break;
        }
    }

    /// <summary>Focuses/restores an already-running app's window instead of
    /// spawning a duplicate instance. With more than one window open, shows
    /// a small picker listing each window's title instead of guessing which
    /// one to focus — otherwise there's no way to reach, say, the second of
    /// three open Firefox windows from the dock at all.</summary>
    private void LaunchOrFocusItem(DockItem item, Border iconBorder)
    {
        if (item.RunningWindowCount > 0 && item.ResolvedExecutablePath is not null)
        {
            var windows = _runningAppsService.FindRunningWindows(item.ResolvedExecutablePath);
            if (windows.Count == 1)
            {
                _runningAppsService.FocusOrRestore(windows[0].Handle);
                return;
            }
            if (windows.Count > 1)
            {
                ShowWindowPicker(iconBorder, windows);
                return;
            }
        }

        LaunchItem(item);
    }

    /// <summary>A small flyout listing each open window's title, anchored
    /// above the clicked icon — built in code rather than static XAML since
    /// its row count is dynamic. Closes itself (StaysOpen=False) on an
    /// outside click same as the dock's other popup-style menus. The Popup
    /// is created before the loop (empty) so each row's click handler can
    /// capture and close it — its properties are only read later, when a row
    /// is actually clicked, so building it out-of-order is fine.</summary>
    private void ShowWindowPicker(Border iconBorder, IReadOnlyList<RunningWindow> windows)
    {
        var popup = new Popup
        {
            PlacementTarget = iconBorder,
            Placement = PlacementMode.Top,
            VerticalOffset = -8,
            AllowsTransparency = true,
            StaysOpen = false
        };

        var list = new StackPanel();
        foreach (var window in windows)
        {
            var row = new TextBlock
            {
                Text = window.Title,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                FontSize = 13,
                Padding = new Thickness(12, 7, 12, 7),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260,
                Cursor = Cursors.Hand
            };

            row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) =>
            {
                _runningAppsService.FocusOrRestore(window.Handle);
                popup.IsOpen = false;
            };

            list.Children.Add(row);
        }

        popup.Child = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x20, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            Child = list,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 16,
                ShadowDepth = 4,
                Opacity = 0.4
            }
        };

        popup.IsOpen = true;
    }

    // ---- Drag a file onto a specific icon: open it there, or recycle it ----

    /// <summary>Handled here (not at the dock root) so dropping squarely on
    /// an icon opens the file with that app instead of pinning it as a new
    /// dock item. Marking e.Handled stops the event bubbling up to
    /// DockPill_DragOver/_Drop, which otherwise treats any file drop as "add
    /// a new pinned app." A DockItemReorder drag (another dock icon passing
    /// over this one) is deliberately left unhandled so it bubbles up to the
    /// existing reorder logic unchanged.</summary>
    private void Icon_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("DockItemReorder")) return;
        if (sender is not Border { Tag: DockItem item } || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        // No sensible "drop a file on a workspace" action in v1 — leave
        // e.Handled false so it bubbles up to the dock root's own drop
        // target, which pins the file as a new app like dropping on empty
        // dock space does.
        if (item.Kind == DockItemKind.Workspace) return;

        if (item.Kind == DockItemKind.DropAction)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            e.Effects = files is not null && files.Any(f => AcceptsFile(item, f))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Icon_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("DockItemReorder")) return;
        if (sender is not Border { Tag: DockItem item }) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        if (item.Kind == DockItemKind.Workspace) return;

        e.Handled = true;

        if (item.Kind == DockItemKind.RecycleBin)
        {
            RecycleFiles(files);
            return;
        }

        if (item.Kind == DockItemKind.DropAction)
        {
            RunDropAction(item, files);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(item.Path) { UseShellExecute = true };
            foreach (var file in files) startInfo.ArgumentList.Add(file);
            Process.Start(startInfo);
        }
        catch
        {
            MessageBox.Show(string.Format(LocalizationService.Instance["Common_CouldntOpenFile"], item.DisplayName),
                LocalizationService.Instance["Common_AppTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool AcceptsFile(DockItem item, string filePath)
    {
        if (string.IsNullOrWhiteSpace(item.AcceptExtensions)) return true;

        var ext = Path.GetExtension(filePath);
        return item.AcceptExtensions
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(accepted => string.Equals(accepted, ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs the configured command once per accepted file — these
    /// are inherently per-file transforms ("resize this image"), unlike
    /// "open with" which can reasonably take several files in one launch.</summary>
    private static void RunDropAction(DockItem item, IEnumerable<string> files)
    {
        if (string.IsNullOrWhiteSpace(item.Command)) return;

        foreach (var file in files)
        {
            if (!AcceptsFile(item, file)) continue;

            try
            {
                Process.Start(new ProcessStartInfo(item.Command)
                {
                    Arguments = SubstitutePlaceholders(item.ArgumentsTemplate ?? string.Empty, file),
                    UseShellExecute = true
                });
            }
            catch
            {
                MessageBox.Show(string.Format(LocalizationService.Instance["Common_CouldntRunAction"], item.DisplayName, Path.GetFileName(file)),
                    LocalizationService.Instance["Common_AppTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private static readonly Regex PlaceholderPattern = new(@"\{file\}|\{dir\}|\{name\}|\{ext\}", RegexOptions.Compiled);

    /// <summary>Substitutes each placeholder with the matching path piece. A
    /// malicious/crafted filename (e.g. a downloaded file named to look like
    /// an extra command-line flag, or one containing spaces) could otherwise
    /// inject arguments into whatever tool the Drop Action runs. The
    /// substituted value is therefore quoted using the standard Win32
    /// argument-escaping rule (doubling trailing backslashes, escaping any
    /// embedded quote) unless the template already quotes that placeholder
    /// itself (the documented usage, see DropAction_ArgumentsHint) - in that
    /// case the template's own quotes are left alone and nothing is added,
    /// so existing user-configured templates keep behaving exactly as
    /// before.</summary>
    private static string SubstitutePlaceholders(string template, string filePath)
    {
        var file = filePath;
        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);

        return PlaceholderPattern.Replace(template, match =>
        {
            var value = match.Value switch
            {
                "{file}" => file,
                "{dir}" => dir,
                "{name}" => name,
                _ => ext
            };

            var start = match.Index;
            var end = match.Index + match.Length;
            var alreadyQuoted = start > 0 && template[start - 1] == '"'
                && end < template.Length && template[end] == '"';

            return alreadyQuoted ? value : QuoteArgument(value);
        });
    }

    /// <summary>Standard Win32 command-line argument quoting (the same rule
    /// CommandLineToArgvW expects): wraps the value in quotes, doubles any
    /// run of backslashes that's immediately followed by a quote (so it
    /// isn't read as escaping that quote), and escapes a literal embedded
    /// quote as \" - belt-and-suspenders, since Windows filenames can't
    /// actually contain a '"' themselves.</summary>
    private static string QuoteArgument(string value)
    {
        var sb = new StringBuilder("\"");
        var backslashes = 0;

        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
            }

            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Sends files to the Recycle Bin (recoverable) rather than
    /// permanently deleting them — SHFileOperation + FOF_ALLOWUNDO is the
    /// classic Win32 way to get that, same "soft delete" Explorer itself
    /// performs on a normal Delete.</summary>
    private static void RecycleFiles(IEnumerable<string> files)
    {
        var paths = string.Join('\0', files);
        if (paths.Length == 0) return;

        var fileOp = new NativeMethods.SHFILEOPSTRUCT
        {
            wFunc = NativeMethods.FO_DELETE,
            pFrom = paths + "\0",
            fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_SILENT
        };

        NativeMethods.SHFileOperation(ref fileOp);
    }

    // ---- Drag-drop target: reorder and add new apps ----------------------

    private void DockPill_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("DockItemReorder"))
        {
            e.Effects = DragDropEffects.Move;

            // Reorder live as the cursor moves, instead of only on drop, so
            // the icon visibly slides to where it will land.
            if (e.Data.GetData("DockItemReorder") is DockItem dragged)
            {
                var currentIndex = _items.IndexOf(dragged);
                if (currentIndex >= 0)
                {
                    var dropIndex = ComputeDropIndex(e.GetPosition(IconsPanelElement()));
                    if (dropIndex > currentIndex) dropIndex--;
                    dropIndex = Math.Clamp(dropIndex, 0, _items.Count - 1);
                    if (dropIndex != currentIndex)
                    {
                        var panel = IconsPanelElement();
                        var oldPositions = GetBaseCenterPositions(panel);
                        _items.Move(currentIndex, dropIndex);
                        panel.UpdateLayout();
                        SlideIntoPlace(panel, oldPositions);
                    }
                }
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // Copy, not Move — dropping a .exe/.lnk from Explorer pins a
            // reference to it, it never relocates the source file. Also
            // check the actual file types up front, not just at Drop, so an
            // unsupported file (e.g. a .txt) shows "not allowed" instead of
            // a "sure, drop here" cursor that silently does nothing.
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            e.Effects = files is not null && files.Any(IsSupportedTarget)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void DockPill_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("DockItemReorder"))
        {
            // Already moved to its final position live, during DragOver —
            // just persist it.
            SaveConfig();
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var insertAt = ComputeDropIndex(e.GetPosition(IconsPanelElement()));
                var reachedLimit = false;
                foreach (var file in files)
                {
                    if (!IsSupportedTarget(file)) continue;
                    if (AtMaxDockItems) { reachedLimit = true; break; }
                    var item = CreateItemFromPath(file);
                    _items.Insert(Math.Clamp(insertAt, 0, _items.Count), item);
                    insertAt++;
                }
                SaveConfig();
                if (reachedLimit) ShowMaxItemsReachedMessage();
            }
        }
    }

    private static bool IsSupportedTarget(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
    }

    private DockItem CreateItemFromPath(string path, string? displayName = null)
    {
        var item = new DockItem
        {
            Kind = DockItemKind.Shortcut,
            Path = path,
            DisplayName = displayName ?? Path.GetFileNameWithoutExtension(path)
        };
        item.Icon = _iconService.GetIcon(item);
        item.ResolvedExecutablePath = ResolveExecutablePath(item);
        item.AppUserModelId = ResolveAppUserModelId(item);
        return item;
    }

    private StackPanel IconsPanelElement()
    {
        // The named x:Name inside a DataTemplate isn't directly reachable;
        // walk the ItemsControl's visual tree for its single StackPanel host.
        return (StackPanel)VisualTreeHelperFindPanel(IconsControl);
    }

    private static DependencyObject VisualTreeHelperFindPanel(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is StackPanel) return child;
            var found = VisualTreeHelperFindPanel(child);
            if (found is StackPanel) return found;
        }
        return root;
    }

    private int ComputeDropIndex(Point posInPanel)
    {
        var positions = GetBaseCenterPositions(IconsPanelElement());
        for (int i = 0; i < positions.Count; i++)
        {
            if (posInPanel.X < positions[i].Center) return i;
        }
        return positions.Count;
    }

    // ---- Context menu actions --------------------------------------------

    private void OpenItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is DockItem item) LaunchItem(item);
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not DockItem item) return;
        try
        {
            if (item.Kind == DockItemKind.RecycleBin)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", item.Path) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
            }
        }
        catch
        {
            // Best-effort: ignore if Explorer can't be launched.
        }
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not DockItem item) return;
        _items.Remove(item);
        _iconService.InvalidateCache(item);
        SaveConfig();
    }

    private async void RestartApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not DockItem item) return;
        await EndTaskAsync(item);
        LaunchItem(item);
    }

    private async void EndTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not DockItem item) return;
        await EndTaskAsync(item);
    }

    /// <summary>Tries a graceful close first (CloseMainWindow), gives the app
    /// a few seconds to actually exit, then force-kills only if it's still
    /// running — never a blocking Thread.Sleep on the UI thread.</summary>
    private async Task EndTaskAsync(DockItem item)
    {
        if (item.ResolvedExecutablePath is null) return;

        using var process = _runningAppsService.GetProcess(item.ResolvedExecutablePath);
        if (process is null) return;

        try
        {
            process.CloseMainWindow();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { /* still running after the grace period */ }

            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: process may have already exited, or access denied.
        }
    }

    private void SaveWindowPosition_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not DockItem item || item.ResolvedExecutablePath is null) return;

        var windows = _runningAppsService.FindWindows(item.ResolvedExecutablePath);
        if (windows.Count == 0) return;

        var placement = _windowPlacementService.Capture(windows[0]);
        if (placement is null) return;

        item.SavedPlacement = placement;
        item.HasSavedPlacement = true;
        SaveConfig();
    }

    private void ClearWindowPosition_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not DockItem item) return;

        item.SavedPlacement = null;
        item.HasSavedPlacement = false;
        SaveConfig();
    }

    /// <summary>Fires every time a per-item context menu opens. Populates
    /// the Recent Files submenu with a fresh Jump List fetch (never cached -
    /// a stale list would be worse than a slightly slower open) for a
    /// running Shortcut item. x:Name inside a DataTemplate doesn't produce a
    /// field, so the submenu is located by its Name property among the
    /// ContextMenu's own Items instead.</summary>
    private void ItemContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not DockItem item) return;

        UpdateNowPlayingSection(menu, item);

        var recentFilesItem = FindMenuItem(menu, "RecentFilesMenuItem");
        if (recentFilesItem is null) return;

        recentFilesItem.ClearValue(VisibilityProperty); // let the RunningWindowCount style trigger decide first
        recentFilesItem.Items.Clear();

        if (item.Kind != DockItemKind.Shortcut || item.RunningWindowCount == 0 || item.ResolvedExecutablePath is null)
            return;

        using var process = _runningAppsService.GetProcess(item.ResolvedExecutablePath);
        if (process is null)
        {
            recentFilesItem.Visibility = Visibility.Collapsed;
            return;
        }

        var recentDocs = _recentDocumentsService.GetRecentDocuments(process, 10);
        if (recentDocs.Count == 0)
        {
            recentFilesItem.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var path in recentDocs)
        {
            var doc = new MenuItem { Header = Path.GetFileName(path), Tag = path };
            doc.Click += RecentDocument_Click;
            recentFilesItem.Items.Add(doc);
        }
    }

    private static MenuItem? FindMenuItem(ContextMenu menu, string name) =>
        menu.Items.OfType<MenuItem>().FirstOrDefault(mi => mi.Name == name);

    private void RecentDocument_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not string path) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort: the file may have been moved/deleted since it
            // appeared in the app's own Jump List.
        }
    }

    /// <summary>Best-effort: a GlobalSystemMediaTransportControlsSession's
    /// SourceAppUserModelId isn't always literally the exe filename (can be
    /// a package family name for UWP-style apps), so this checks a
    /// case-insensitive substring match in either direction rather than
    /// requiring an exact one. No match for anything this tick simply means
    /// no Now Playing section shows anywhere - not every media app is
    /// guaranteed to be caught.</summary>
    private bool MediaSessionMatches(DockItem item)
    {
        if (_currentMediaSession is null) return false;
        var sourceId = _currentMediaSession.SourceAppUserModelId;
        if (string.IsNullOrEmpty(sourceId)) return false;

        // Exact match first: a UWP-packaged app's shortcut carries its real
        // AppUserModelID (a package identity, e.g.
        // "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" for
        // Windows 11's own Media Player) which bears no resemblance to any
        // exe filename, so the heuristic below can never catch it.
        if (item.AppUserModelId is not null)
            return string.Equals(item.AppUserModelId, sourceId, StringComparison.OrdinalIgnoreCase);

        // Fallback for plain Win32 apps, whose shortcuts usually don't carry
        // an explicit AppUserModelID (Windows derives one at runtime
        // instead) but whose SourceAppUserModelId is often filename-like.
        if (item.ResolvedExecutablePath is null) return false;
        var exeName = Path.GetFileNameWithoutExtension(item.ResolvedExecutablePath);
        if (string.IsNullOrEmpty(exeName)) return false;

        return sourceId.Contains(exeName, StringComparison.OrdinalIgnoreCase) ||
               exeName.Contains(sourceId, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateNowPlayingSection(ContextMenu menu, DockItem item)
    {
        var header = FindMenuItem(menu, "NowPlayingHeaderMenuItem");
        var playPause = FindMenuItem(menu, "NowPlayingPlayPauseMenuItem");
        var previous = FindMenuItem(menu, "NowPlayingPreviousMenuItem");
        var next = FindMenuItem(menu, "NowPlayingNextMenuItem");
        var separator = menu.Items.OfType<Separator>().FirstOrDefault(s => s.Name == "NowPlayingSeparator");
        if (header is null || playPause is null || previous is null || next is null || separator is null) return;

        var matches = item.Kind == DockItemKind.Shortcut && MediaSessionMatches(item);
        var visibility = matches ? Visibility.Visible : Visibility.Collapsed;
        header.Visibility = visibility;
        playPause.Visibility = visibility;
        previous.Visibility = visibility;
        next.Visibility = visibility;
        separator.Visibility = visibility;

        if (!matches || _currentMediaSession is null) return;

        header.Header = string.Format(LocalizationService.Instance["DockItem_NowPlayingFormat"],
            _currentMediaSession.Title, _currentMediaSession.Artist);
        playPause.Header = LocalizationService.Instance[_currentMediaSession.IsPlaying ? "DockItem_MediaPause" : "DockItem_MediaPlay"];

        // The icon doubles as a play/pause toggle, so its glyph has to track
        // state the same way the header text does - found by walking the
        // Icon's own content (Viewbox -> Path) rather than a name lookup,
        // since x:Name inside this DataTemplate doesn't generate a field but
        // the MenuItem reference we already have gives direct object access.
        if (playPause.Icon is Viewbox { Child: System.Windows.Shapes.Path iconPath })
        {
            iconPath.Data = Geometry.Parse(_currentMediaSession.IsPlaying
                ? "M6,19h4V5H6V19z M14,5v14h4V5H14z"
                : "M8,5v14l11,-7z");
        }
    }

    private async void MediaPrevious_Click(object sender, RoutedEventArgs e) => await _mediaSessionService.PreviousAsync();

    private async void MediaPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMediaSession?.IsPlaying == true) await _mediaSessionService.PauseAsync();
        else await _mediaSessionService.PlayAsync();
    }

    private async void MediaNext_Click(object sender, RoutedEventArgs e) => await _mediaSessionService.NextAsync();

    // ---- Fixed shelf icon: file references + clipboard history, session-only ----

    private void ShelfIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        ShelfPopup.IsOpen = !ShelfPopup.IsOpen;

    private void ShelfIcon_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        // Any file type - the shelf is a generic staging area, not scoped
        // to apps/extensions the way Drop Actions deliberately are.
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void ShelfIcon_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;

        foreach (var file in files)
        {
            if (_shelfFiles.Any(f => string.Equals(f.Path, file, StringComparison.OrdinalIgnoreCase)))
                continue; // already on the shelf

            var icon = _iconService.GetIconForPath(file);
            _shelfFiles.Add(new ShelfFileItem(file, Path.GetFileName(file), icon));
        }
    }

    private void UpdateShelfBadge()
    {
        ShelfCountBadge.Visibility = _shelfFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfCountText.Text = _shelfFiles.Count.ToString();
    }

    private void RemoveShelfFile_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ShelfFileItem item)
            _shelfFiles.Remove(item);
    }

    /// <summary>Drags a shelf file back out to Explorer/another app. No
    /// minimum-distance threshold like the dock icons' own drag-reorder
    /// has — a shelf row has no competing click action to disambiguate
    /// from (removal is a separate, dedicated "x" button), so starting the
    /// drag on the first move is fine.</summary>
    private void ShelfFileRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if ((sender as FrameworkElement)?.Tag is not ShelfFileItem item) return;

        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DataFormats.FileDrop, new[] { item.Path }), DragDropEffects.Copy);
    }

    private void ClipboardEntry_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ClipboardHistoryItem item) return;

        try
        {
            if (item.Image is not null) Clipboard.SetImage(item.Image);
            else if (item.Text is not null) Clipboard.SetText(item.Text);
        }
        catch
        {
            // Best-effort: another app may be holding the clipboard open.
        }

        ShelfPopup.IsOpen = false;
    }

    // ---- Fixed options icon --------------------------------------------------

    private void OptionsIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu })
            menu.IsOpen = true;
    }

    private void OptionsMenu_Opened(object sender, RoutedEventArgs e)
    {
        AutoStartMenuItem.IsChecked = _autoStartService.IsEnabled();
        AutoHideMenuItem.IsChecked = _config.AutoHideEnabled;
        AddApplicationMenuItem.IsEnabled = !AtMaxDockItems;
        AddDropActionMenuItem.IsEnabled = !AtMaxDockItems;
        AddWorkspaceMenuItem.IsEnabled = !AtMaxDockItems && _items.Count(i => i.Kind == DockItemKind.Workspace) < MaxWorkspaces;

        foreach (var languageItem in LanguageMenuItems)
            languageItem.IsChecked = Equals(languageItem.Tag, _config.Language);

        foreach (var themeItem in ThemeMenuItems)
            themeItem.IsChecked = Equals(themeItem.Tag, _config.Theme);
    }

    private IEnumerable<MenuItem> LanguageMenuItems =>
        new[] { Lang_en, Lang_fr, Lang_de, Lang_nl, Lang_it, Lang_es, Lang_pt, Lang_ru, Lang_zh, Lang_zhHant, Lang_ja, Lang_ko };

    private void SetLanguage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not string code) return;

        _config.Language = code;
        LocalizationService.Instance.SetLanguage(code);
        SaveConfig();
    }

    private IEnumerable<MenuItem> ThemeMenuItems => new[] { Theme_Default, Theme_WaterGlass, Theme_DarkGlass };

    private void SetTheme_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not string themeName) return;

        var theme = ThemeService.Parse(themeName);
        _config.Theme = theme.ToString();
        ThemeService.Instance.SetTheme(theme);
        SaveConfig();
    }

    private void AddApplication_Click(object sender, RoutedEventArgs e) => OpenAddAppPicker();

    private void AddDropAction_Click(object sender, RoutedEventArgs e) => OpenDropActionEditor(null);

    private void EditDropAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is DockItem item) OpenDropActionEditor(item);
    }

    /// <summary>Shared by Add and Edit — existing is null for a new action,
    /// or the item being edited (pre-fills the form, mutates in place on
    /// save instead of creating a second item).</summary>
    private void OpenDropActionEditor(DockItem? existing)
    {
        var editor = new DropActionEditorWindow(existing, (name, command, argumentsTemplate, accept) =>
        {
            if (existing is not null)
            {
                existing.DisplayName = name;
                existing.Command = command;
                existing.ArgumentsTemplate = argumentsTemplate;
                existing.AcceptExtensions = accept;
                existing.Icon = _iconService.GetIcon(existing);
            }
            else if (AtMaxDockItems)
            {
                ShowMaxItemsReachedMessage();
            }
            else
            {
                var item = new DockItem
                {
                    Kind = DockItemKind.DropAction,
                    DisplayName = name,
                    Command = command,
                    ArgumentsTemplate = argumentsTemplate,
                    AcceptExtensions = accept
                };
                item.Icon = _iconService.GetIcon(item);
                _items.Add(item);
            }

            SaveConfig();
        })
        { ShowInTaskbar = true };

        editor.ShowDialog();
    }

    private const int MaxWorkspaces = 5;

    private void AddWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count(i => i.Kind == DockItemKind.Workspace) >= MaxWorkspaces)
        {
            MessageBox.Show(string.Format(LocalizationService.Instance["Workspace_MaxReachedMessage"], MaxWorkspaces),
                LocalizationService.Instance["Workspace_MaxReachedTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (AtMaxDockItems)
        {
            ShowMaxItemsReachedMessage();
            return;
        }

        OpenWorkspaceEditor(null);
    }

    private void LaunchWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is DockItem item) LaunchWorkspace(item);
    }

    private void EditWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is DockItem item) OpenWorkspaceEditor(item);
    }

    /// <summary>Shared by Add and Edit — existing is null for a new
    /// workspace, or the item being edited (mutates in place on save).</summary>
    private void OpenWorkspaceEditor(DockItem? existing)
    {
        var editor = new WorkspaceEditorWindow(_iconService, existing, (name, paths, iconPath) =>
        {
            if (existing is not null)
            {
                existing.DisplayName = name;
                existing.WorkspaceAppPaths = paths;
                existing.WorkspaceIconPath = iconPath;
                existing.Icon = _iconService.GetIcon(existing);
            }
            else
            {
                var item = new DockItem
                {
                    Kind = DockItemKind.Workspace,
                    DisplayName = name,
                    WorkspaceAppPaths = paths,
                    WorkspaceIconPath = iconPath
                };
                item.Icon = _iconService.GetIcon(item);
                _items.Add(item);
            }

            SaveConfig();
        })
        { ShowInTaskbar = true };

        editor.ShowDialog();
    }

    /// <summary>Launches whatever isn't already running and focuses whatever
    /// is — no per-workspace monitor layout in v1. An app that's also
    /// separately pinned to the dock launches through that same DockItem, so
    /// it picks up its own saved window position for free; one that isn't
    /// pinned just launches plainly (there's nowhere a saved position for it
    /// could live).</summary>
    private void LaunchWorkspace(DockItem workspace)
    {
        if (workspace.WorkspaceAppPaths is not { Count: > 0 } paths) return;

        foreach (var path in paths)
        {
            var resolvedPath = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                ? ShellLinkResolver.ResolveTarget(path) ?? path
                : path;

            var windows = _runningAppsService.FindWindows(resolvedPath);
            if (windows.Count > 0)
            {
                _runningAppsService.FocusOrRestore(windows[0]);
                continue;
            }

            var pinned = _items.FirstOrDefault(i =>
                i.Kind == DockItemKind.Shortcut &&
                string.Equals(i.ResolvedExecutablePath, resolvedPath, StringComparison.OrdinalIgnoreCase));

            LaunchItem(pinned ?? new DockItem
            {
                Kind = DockItemKind.Shortcut,
                Path = path,
                DisplayName = Path.GetFileNameWithoutExtension(path),
                ResolvedExecutablePath = resolvedPath
            });
        }
    }

    /// <summary>A DropAction icon has no single launch target to click on —
    /// this is the click-driven alternative to dragging a file onto it, so
    /// the action is reachable (and the extension filter discoverable)
    /// without needing to find a matching file in Explorer first.</summary>
    private void RunDropActionViaFilePicker(DockItem item)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose a file for \"{item.DisplayName}\"",
            Multiselect = true
        };

        if (!string.IsNullOrWhiteSpace(item.AcceptExtensions))
        {
            var patterns = string.Join(';', item.AcceptExtensions
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(ext => $"*{ext}"));
            dialog.Filter = $"Accepted files|{patterns}|All files (*.*)|*.*";
        }

        if (dialog.ShowDialog() == true)
            RunDropAction(item, dialog.FileNames);
    }

    private void AutoHide_Click(object sender, RoutedEventArgs e)
    {
        _config.AutoHideEnabled = !_config.AutoHideEnabled;
        SaveConfig();
        UpdateAutoHide();
    }

    private void MagnifySubtle_Click(object sender, RoutedEventArgs e) => SetMagnifyScale(1.3);

    private void MagnifyNormal_Click(object sender, RoutedEventArgs e) => SetMagnifyScale(1.8);

    private void MagnifyLarge_Click(object sender, RoutedEventArgs e) => SetMagnifyScale(2.3);

    private void AutoStart_Click(object sender, RoutedEventArgs e) =>
        _autoStartService.SetEnabled(!_autoStartService.IsEnabled());

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ConfigDirectory) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort only, same as the tray menu's equivalent action.
        }
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { ShowInTaskbar = true }.Show();

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ---- Windows Recovery Toolkit ------------------------------------------

    private void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        // The user's own per-session shell process — no elevation needed to
        // kill/relaunch it, unlike the network/audio actions below.
        try
        {
            foreach (var explorer in Process.GetProcessesByName("explorer"))
                explorer.Kill();

            Process.Start("explorer.exe");
        }
        catch
        {
            // Best-effort only — Explorer usually restarts itself anyway.
        }
    }

    private void FlushDns_Click(object sender, RoutedEventArgs e) => RunNetworkCommand("/flushdns", elevate: false);

    private void RenewIp_Click(object sender, RoutedEventArgs e)
    {
        RunNetworkCommand("/release", elevate: true);
        RunNetworkCommand("/renew", elevate: true);
    }

    private static void RunNetworkCommand(string ipconfigArgs, bool elevate)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ipconfig.exe", ipconfigArgs)
            {
                UseShellExecute = elevate, // only ShellExecute supports the "runas" verb
                CreateNoWindow = !elevate,
                Verb = elevate ? "runas" : string.Empty
            });
        }
        catch
        {
            // Best-effort: covers both a failure to launch and the user
            // declining the UAC prompt.
        }
    }

    private void RestartAudio_Click(object sender, RoutedEventArgs e)
    {
        // Stopping/starting a Windows service needs admin rights regardless
        // of API — elevate just this one helper command via "runas" rather
        // than requiring the whole dock to run elevated.
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c net stop audiosrv && net start audiosrv")
            {
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch
        {
            // Best-effort: covers both a failure to launch and the user
            // declining the UAC prompt.
        }
    }

    private void OpenDeviceManager_Click(object sender, RoutedEventArgs e) => OpenMmcSnapin("devmgmt.msc");

    private void OpenServices_Click(object sender, RoutedEventArgs e) => OpenMmcSnapin("services.msc");

    private static void OpenMmcSnapin(string snapin)
    {
        try
        {
            Process.Start(new ProcessStartInfo(snapin) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort only.
        }
    }

    // ---- Launching ---------------------------------------------------------

    private void LaunchItem(DockItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show(string.Format(LocalizationService.Instance["Common_CouldntLaunch"], item.DisplayName),
                LocalizationService.Instance["Common_AppTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (item.SavedPlacement is not null)
            _ = ApplySavedPlacementWhenWindowAppearsAsync(item);
    }

    /// <summary>A freshly launched process doesn't have a window yet, so this
    /// polls for one briefly rather than applying placement immediately. Only
    /// called right after Process.Start — never for a plain focus-click on an
    /// already-running window, which must never get snapped back to a saved
    /// spot the user may have since moved away from.</summary>
    private async Task ApplySavedPlacementWhenWindowAppearsAsync(DockItem item)
    {
        if (item.ResolvedExecutablePath is null || item.SavedPlacement is null) return;
        var placement = item.SavedPlacement;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!cts.IsCancellationRequested)
        {
            var windows = _runningAppsService.FindWindows(item.ResolvedExecutablePath);
            if (windows.Count > 0)
            {
                _windowPlacementService.Apply(windows[0], placement);
                return;
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ---- Public API for the tray icon ---------------------------------------

    public void ToggleVisibility() => Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;

    public void ShowDock()
    {
        Visibility = Visibility.Visible;
        RepositionWindow();
    }

    public void HideDock() => Visibility = Visibility.Hidden;

    /// <summary>Opens the picker: a searchable grid of installed apps (from
    /// the Start Menu) plus a "browse for a file" fallback for standalone
    /// tools — like putty.exe — that don't have a Start Menu shortcut.</summary>
    public void OpenAddAppPicker()
    {
        var picker = new AppPickerWindow(_iconService, _items.Select(i => i.Path), AddApplicationByPath)
        {
            ShowInTaskbar = true
        };
        picker.Show();
    }

    /// <summary>Adds an item by path if it isn't already pinned. Shared by
    /// the app picker (both its installed-apps grid and its browse button).</summary>
    public void AddApplicationByPath(string path, string displayName)
    {
        if (_items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;

        if (AtMaxDockItems)
        {
            ShowMaxItemsReachedMessage();
            return;
        }

        var item = CreateItemFromPath(path, displayName);
        _items.Add(item);
        SaveConfig();
    }

    // A dock that's grown wide enough to spill off-screen stops being
    // usable as a dock, so pinned items (of any kind - Shortcut, DropAction,
    // or Workspace) are capped overall, same spirit as MaxWorkspaces below
    // but across the whole dock rather than just that one kind.
    private const int MaxDockItems = 20;

    private bool AtMaxDockItems => _items.Count >= MaxDockItems;

    private void ShowMaxItemsReachedMessage() =>
        MessageBox.Show(string.Format(LocalizationService.Instance["Common_MaxItemsMessage"], MaxDockItems),
            LocalizationService.Instance["Common_MaxItemsTitle"], MessageBoxButton.OK, MessageBoxImage.Information);

    public void SetMagnifyScale(double scale)
    {
        _config.MagnifyScale = scale;
        SaveConfig();
    }

    public string ConfigDirectory => _configService.ConfigDirectory;
}

/// <summary>A file reference held on the shelf - never a copy, just a path.
/// Session-only, like the shelf itself: no INotifyPropertyChanged needed,
/// since nothing about an entry changes after it's added (removal is a
/// whole-item removal from the owning ObservableCollection, not a property
/// mutation).</summary>
public sealed class ShelfFileItem
{
    public string Path { get; }
    public string DisplayName { get; }
    public ImageSource Icon { get; }

    public ShelfFileItem(string path, string displayName, ImageSource icon)
    {
        Path = path;
        DisplayName = displayName;
        Icon = icon;
    }
}

/// <summary>One clipboard snapshot, captured by ClipboardMonitorService -
/// either Text or Image is set, never both.</summary>
public sealed class ClipboardHistoryItem
{
    public string? Text { get; }
    public System.Windows.Media.Imaging.BitmapSource? Image { get; }
    public DateTime CapturedAt { get; }

    public bool IsImage => Image is not null;

    public string Preview => IsImage
        ? string.Format(LocalizationService.Instance["Shelf_ImagePreviewFormat"], CapturedAt.ToString("t"))
        : Text ?? string.Empty;

    public ClipboardHistoryItem(string? text, System.Windows.Media.Imaging.BitmapSource? image, DateTime capturedAt)
    {
        Text = text;
        Image = image;
        CapturedAt = capturedAt;
    }
}

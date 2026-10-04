using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DochkaDock.Models;
using DochkaDock.Services;

namespace DochkaDock;

/// <summary>Create/edit dialog for a Workspace: a named set of apps that
/// launch (or focus, if already running) together from one dock icon. Same
/// searchable tile grid as AppPickerWindow, sourced the same way
/// (InstalledAppsService + IconService), but toggle-select instead of
/// fire-once-per-click, since a workspace needs to accumulate a whole set
/// before it's saved. Also lets the user override the auto-assigned icon
/// (first selected app) with either a specific app's icon or one of the
/// bundled preset images.</summary>
public partial class WorkspaceEditorWindow : Window
{
    private readonly IconService _iconService;
    private readonly Action<string, List<string>, string?> _onSave;
    private List<WorkspacePickerItem> _allApps = new();

    // null means "Automatic" (first selected app's icon, the pre-existing
    // default behavior); otherwise an app's path or "preset:N" for one of
    // the bundled presets.
    private string? _selectedIconPath;

    public WorkspaceEditorWindow(IconService iconService, DockItem? existing, Action<string, List<string>, string?> onSave)
    {
        InitializeComponent();
        _iconService = iconService;
        _onSave = onSave;
        _selectedIconPath = existing?.WorkspaceIconPath;

        var selectedPaths = new HashSet<string>(
            existing?.WorkspaceAppPaths ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        if (existing is not null)
            NameBox.Text = existing.DisplayName;

        LoadPresetSwatches();

        Loaded += async (_, _) =>
        {
            _allApps = await Task.Run(() =>
            {
                var apps = new InstalledAppsService().GetInstalledApps();
                var items = new List<WorkspacePickerItem>(apps.Count);
                foreach (var app in apps)
                {
                    var icon = iconService.GetIconForPath(app.Path);
                    items.Add(new WorkspacePickerItem(app.DisplayName, app.Path, icon, selectedPaths.Contains(app.Path)));
                }
                return items;
            });

            LoadingText.Visibility = Visibility.Collapsed;
            ApplyFilter();
            RefreshIconCombo();
            RefreshIconPreviewAndHighlights();
        };
    }

    private void LoadPresetSwatches()
    {
        var images = new[] { Preset1Image, Preset2Image, Preset3Image, Preset4Image, Preset5Image };
        for (var i = 0; i < images.Length; i++)
            images[i].Source = _iconService.GetWorkspacePresetIcon(i + 1);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var filtered = query.Length == 0
            ? _allApps
            : _allApps.Where(a => a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        AppsList.ItemsSource = filtered;

        var stillLoading = LoadingText.Visibility == Visibility.Visible;
        ResultsScroll.Visibility = !stillLoading && filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = !stillLoading && filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tile_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as Border)?.Tag is not WorkspacePickerItem item) return;
        item.IsSelected = !item.IsSelected;

        RefreshIconCombo();
        // An app being added/removed from the selection can change what
        // "Automatic" resolves to (it's always the first selected app).
        if (_selectedIconPath is null)
            RefreshIconPreviewAndHighlights();
    }

    private void AutoIconSwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _selectedIconPath = null;
        RefreshIconPreviewAndHighlights();
    }

    private void PresetSwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as Border)?.Tag is not string presetId) return;
        _selectedIconPath = $"preset:{presetId}";
        RefreshIconPreviewAndHighlights();
    }

    private void AppIconCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppIconCombo.SelectedItem is not WorkspacePickerItem item) return;
        _selectedIconPath = item.Path;
        RefreshIconPreviewAndHighlights();
    }

    /// <summary>The combo only offers apps currently ticked in the grid
    /// below, since picking an icon from an app that isn't even part of the
    /// workspace would be confusing.</summary>
    private void RefreshIconCombo()
    {
        var selectedApps = _allApps.Where(a => a.IsSelected).ToList();
        AppIconCombo.ItemsSource = selectedApps;

        if (_selectedIconPath is not null)
        {
            var match = selectedApps.FirstOrDefault(a => string.Equals(a.Path, _selectedIconPath, StringComparison.OrdinalIgnoreCase));
            AppIconCombo.SelectedItem = match;
        }
    }

    private void RefreshIconPreviewAndHighlights()
    {
        if (_selectedIconPath is { } selected && TryParsePresetId(selected) is int selectedPresetId)
        {
            IconPreviewImage.Source = _iconService.GetWorkspacePresetIcon(selectedPresetId);
        }
        else
        {
            var effectivePath = _selectedIconPath ?? _allApps.FirstOrDefault(a => a.IsSelected)?.Path;
            IconPreviewImage.Source = effectivePath is not null ? _iconService.GetIconForPath(effectivePath) : null;
        }

        var selectedBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0xCF, 0x6F));
        AutoIconSwatch.BorderBrush = _selectedIconPath is null ? selectedBrush : Brushes.Transparent;

        var presetSwatches = new[] { Preset1Swatch, Preset2Swatch, Preset3Swatch, Preset4Swatch, Preset5Swatch };
        for (var i = 0; i < presetSwatches.Length; i++)
        {
            presetSwatches[i].BorderBrush = _selectedIconPath is not null && TryParsePresetId(_selectedIconPath) == i + 1
                ? selectedBrush
                : Brushes.Transparent;
        }
    }

    /// <summary>Mirrors IconService's own parsing so the editor's preview and
    /// swatch highlight agree with what the dock will actually render -
    /// "preset:N" (current), or the full path to a loose workspaceN.ico
    /// (what an older config saved before presets were embedded).</summary>
    private static int? TryParsePresetId(string workspaceIconPath)
    {
        if (workspaceIconPath.StartsWith("preset:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(workspaceIconPath.AsSpan(7), out var id))
            return id;

        var match = System.Text.RegularExpressions.Regex.Match(
            Path.GetFileName(workspaceIconPath), @"^workspace(\d)\.ico$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var selected = _allApps.Where(a => a.IsSelected).Select(a => a.Path).ToList();

        if (name.Length == 0 || selected.Count == 0)
        {
            MessageBox.Show(LocalizationService.Instance["Workspace_ValidationMessage"],
                LocalizationService.Instance["Workspace_Title"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _onSave(name, selected, _selectedIconPath);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Picker-local view model: an installed app plus whether it's
/// currently part of the workspace being edited.</summary>
public sealed class WorkspacePickerItem : INotifyPropertyChanged
{
    public string DisplayName { get; }
    public string Path { get; }
    public ImageSource Icon { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public WorkspacePickerItem(string displayName, string path, ImageSource icon, bool isSelected)
    {
        DisplayName = displayName;
        Path = path;
        Icon = icon;
        _isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DochkaDock.Services;
using Microsoft.Win32;

namespace DochkaDock;

/// <summary>Lets the user add a dock item either by picking from a searchable
/// grid of installed apps (sourced from the Start Menu), or by browsing to a
/// specific executable — for standalone tools like putty.exe that don't
/// install a Start Menu shortcut at all.</summary>
public partial class AppPickerWindow : Window
{
    private readonly IconService _iconService;
    private readonly HashSet<string> _existingPaths;
    private readonly Action<string, string> _onAdd;
    private List<AppPickerItem> _allApps = new();

    public AppPickerWindow(IconService iconService, IEnumerable<string> existingPaths, Action<string, string> onAdd)
    {
        InitializeComponent();
        _iconService = iconService;
        _existingPaths = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
        _onAdd = onAdd;
        Loaded += AppPickerWindow_Loaded;
    }

    private async void AppPickerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var iconService = _iconService;
        var existingPaths = _existingPaths;

        _allApps = await Task.Run(() =>
        {
            var apps = new InstalledAppsService().GetInstalledApps();
            var items = new List<AppPickerItem>(apps.Count);
            foreach (var app in apps)
            {
                var icon = iconService.GetIconForPath(app.Path);
                items.Add(new AppPickerItem(app.DisplayName, app.Path, icon, existingPaths.Contains(app.Path)));
            }
            return items;
        });

        LoadingText.Visibility = Visibility.Collapsed;
        ApplyFilter();
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
        if ((sender as Border)?.Tag is not AppPickerItem item || item.IsAdded) return;

        _onAdd(item.Path, item.DisplayName);
        item.IsAdded = true;
        _existingPaths.Add(item.Path);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Instance["Picker_BrowseDialogTitle"],
            Filter = LocalizationService.Instance["Picker_BrowseDialogFilter"],
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
            _onAdd(dialog.FileName, name);
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Picker-local view model: an installed app plus whether it's
/// already pinned (greys the tile out and shows a checkmark).</summary>
public sealed class AppPickerItem : INotifyPropertyChanged
{
    public string DisplayName { get; }
    public string Path { get; }
    public ImageSource Icon { get; }

    private bool _isAdded;
    public bool IsAdded
    {
        get => _isAdded;
        set
        {
            if (_isAdded == value) return;
            _isAdded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdded)));
        }
    }

    public AppPickerItem(string displayName, string path, ImageSource icon, bool isAdded)
    {
        DisplayName = displayName;
        Path = path;
        Icon = icon;
        _isAdded = isAdded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

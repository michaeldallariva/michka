using System.ComponentModel;

namespace DochkaDock.Services;

/// <summary>The dock's visual theme. Default is the original flat-icon look;
/// WaterGlass/DarkGlass each wrap every icon in a translucent "glass tile"
/// (gradient fill + highlight + border stroke) while leaving layout,
/// behavior and the outer pill untouched - they differ only in the tile's
/// fill color (blue-tinted vs. near-black).</summary>
public enum DockTheme
{
    Default,
    WaterGlass,
    DarkGlass
}

/// <summary>Singleton holding the active theme, same shape as
/// <see cref="LocalizationService"/> (a static access point XAML bindings can
/// target, raising PropertyChanged so every {Binding Source={x:Static
/// services:ThemeService.Instance}, Path=IsWaterGlass} across the dock -
/// including per-item DataTemplates re-generated on add/remove/reorder -
/// re-evaluates immediately when the theme changes, with no per-element
/// invalidation code anywhere.</summary>
public sealed class ThemeService : INotifyPropertyChanged
{
    public static ThemeService Instance { get; } = new();

    private ThemeService() { }

    public DockTheme CurrentTheme { get; private set; } = DockTheme.Default;

    public bool IsWaterGlass => CurrentTheme == DockTheme.WaterGlass;
    public bool IsDarkGlass => CurrentTheme == DockTheme.DarkGlass;

    // Either glass theme insets the icon image inside its tile the same way
    // - exposed so XAML only needs one trigger for that shared behavior
    // instead of one per theme.
    public bool IsAnyGlass => IsWaterGlass || IsDarkGlass;

    public void SetTheme(DockTheme theme)
    {
        if (CurrentTheme == theme) return;

        CurrentTheme = theme;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTheme)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWaterGlass)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDarkGlass)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAnyGlass)));
    }

    /// <summary>Tolerant of an unknown/corrupt config value - falls back to
    /// Default rather than throwing, same philosophy as ConfigService.</summary>
    public static DockTheme Parse(string? value)
    {
        if (string.Equals(value, nameof(DockTheme.WaterGlass), StringComparison.OrdinalIgnoreCase))
            return DockTheme.WaterGlass;
        if (string.Equals(value, nameof(DockTheme.DarkGlass), StringComparison.OrdinalIgnoreCase))
            return DockTheme.DarkGlass;
        return DockTheme.Default;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

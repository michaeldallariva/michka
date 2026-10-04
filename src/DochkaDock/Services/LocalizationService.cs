using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace DochkaDock.Services;

/// <summary>Flat key -> string lookup for all UI text, loaded from
/// Languages/{code}.json files embedded directly into the assembly (not
/// shipped as loose files next to the exe - one less thing a user could
/// accidentally edit, delete, or that antivirus/SmartScreen flags as an
/// unsigned companion file). A singleton (not DI'd like the other services)
/// because XAML's {loc:Loc} markup extension needs a static access point to
/// bind against. Raising PropertyChanged("Item[]") - WPF's special
/// indexer-change notification - makes every {Binding [key]} across the
/// whole app, including any already-open secondary window, refresh the
/// instant the language changes: no per-window invalidation code anywhere.</summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    private const string DefaultLanguage = "en";

    // MSBuild's default embedded-resource naming: {RootNamespace}.{folder
    // with '/' turned into '.'}.{filename} - same convention already used
    // for the tray icon ("DochkaDock.Assets.tray.ico").
    private const string ResourcePrefix = "DochkaDock.Languages.";
    private static readonly Assembly ResourceAssembly = typeof(LocalizationService).Assembly;

    private Dictionary<string, string> _strings = new();
    private Dictionary<string, string> _fallbackStrings = new();

    public string CurrentLanguage { get; private set; } = DefaultLanguage;

    private LocalizationService()
    {
        _fallbackStrings = Load(DefaultLanguage) ?? new Dictionary<string, string>();
        _strings = _fallbackStrings;
    }

    /// <summary>Never shows a blank string: missing key in the selected
    /// language falls back to English, missing from English too falls back
    /// to the key itself.</summary>
    public string this[string key] =>
        _strings.TryGetValue(key, out var value) ? value
        : _fallbackStrings.TryGetValue(key, out var fallback) ? fallback
        : key;

    public void SetLanguage(string languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) languageCode = DefaultLanguage;

        var loaded = Load(languageCode);
        if (loaded is not null)
        {
            _strings = loaded;
            CurrentLanguage = languageCode;
        }
        else
        {
            // Missing/corrupt language file - never leave the UI with no
            // strings at all, fall back to English rather than throw.
            _strings = _fallbackStrings;
            CurrentLanguage = DefaultLanguage;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    private Dictionary<string, string>? Load(string languageCode)
    {
        try
        {
            using var stream = ResourceAssembly.GetManifestResourceStream(ResourcePrefix + $"{languageCode}.json");
            if (stream is null) return null;

            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        }
        catch
        {
            return null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

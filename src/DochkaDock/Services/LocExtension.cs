using System.Windows.Data;
using System.Windows.Markup;

namespace DochkaDock.Services;

/// <summary>XAML localization: {loc:Loc Key=Options_AddApplication} instead
/// of a hardcoded literal. Returns a Binding to LocalizationService's
/// indexer rather than a plain string, so every usage live-refreshes when
/// the language changes (see LocalizationService's PropertyChanged("Item[]")
/// notification) - the standard WPF indexer-binding localization pattern.</summary>
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    public override object? ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
            FallbackValue = Key
        };
        return binding.ProvideValue(serviceProvider);
    }
}

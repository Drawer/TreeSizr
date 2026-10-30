using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace TreeSizr;

/// <summary>
/// UI texts, from Resources/Strings.resx (English) and Strings.nl.resx (Dutch).
/// XAML uses <c>{local:Tr Key}</c>; code uses <see cref="T"/> and <see cref="F"/>.
/// Switching <see cref="Language"/> updates every XAML text at once through the indexer binding.
/// To add a language: add a Strings.xx.resx file and add the language to <see cref="Languages"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly ResourceManager Strings = new("TreeSizr.Resources.Strings", typeof(Loc).Assembly);

    public static Loc Instance { get; } = new();

    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("en", "English"),
        new("nl", "Nederlands"),
    ];

    private string _language = "en";
    private CultureInfo _culture = CultureInfo.GetCultureInfo("en");

    public string Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            _culture = CultureInfo.GetCultureInfo(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]")); // refreshes all {local:Tr} bindings
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The text for <paramref name="key"/> in the current language, or the key itself if it is missing.</summary>
    public string this[string key] => Strings.GetString(key, _culture) ?? key;

    public static string T(string key) => Instance[key];

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Instance[key], args);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;
}

public sealed record LanguageOption(string Code, string Name);

/// <summary>XAML shortcut for a translated text that follows language changes: <c>Text="{local:Tr Scan}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

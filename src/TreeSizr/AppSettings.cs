using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using TreeSizr.Scanning;

namespace TreeSizr;

public enum NameBarStyle { Off, Gradient, Solid }

/// <summary>
/// User settings, stored as JSON in %APPDATA%\TreeSizr\settings.json.
/// Every change is saved immediately and raises PropertyChanged, so the UI can bind to <see cref="Current"/> directly.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TreeSizr", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; } = Load();

    private string _language = "en";
    private AppTheme _theme = AppTheme.System;
    private NameBarStyle _nameBar = NameBarStyle.Gradient;
    private SizeMetric _sizeMetric = SizeMetric.OnDisk;
    private bool _showSizeInName;
    private bool _showShareColumn = true;
    private bool _showAllocatedColumn = true;
    private bool _useMftScan = true;
    private int _topFileCount = 50;
    private bool _loading;

    /// <summary>UI language code, see <see cref="Loc.Languages"/>.</summary>
    public string Language
    {
        get => _language;
        set
        {
            if (Set(ref _language, Loc.Languages.Any(l => l.Code == value) ? value : "en"))
                Loc.Instance.Language = _language;
        }
    }

    /// <summary>Light, dark, or follow the Windows app mode.</summary>
    public AppTheme Theme { get => _theme; set => Set(ref _theme, value); }

    /// <summary>The size bar drawn behind each folder name in the tree, like TreeSize does.</summary>
    public NameBarStyle NameBar { get => _nameBar; set => Set(ref _nameBar, value); }

    /// <summary>Whether the tree order, percentages, bars, treemap and largest files use the file size or the space on disk.</summary>
    public SizeMetric SizeMetric { get => _sizeMetric; set => Set(ref _sizeMetric, value); }

    /// <summary>Also show the size in front of each name, like TreeSize does.</summary>
    public bool ShowSizeInName { get => _showSizeInName; set => Set(ref _showSizeInName, value); }

    public bool ShowShareColumn { get => _showShareColumn; set => Set(ref _showShareColumn, value); }

    public bool ShowAllocatedColumn { get => _showAllocatedColumn; set => Set(ref _showAllocatedColumn, value); }

    /// <summary>Read the MFT directly when running as administrator on a local NTFS drive.</summary>
    public bool UseMftScan { get => _useMftScan; set => Set(ref _useMftScan, value); }

    /// <summary>How many files the "Largest files" list keeps. Applies to the next scan.</summary>
    public int TopFileCount { get => _topFileCount; set => Set(ref _topFileCount, Math.Clamp(value, 10, 1000)); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (!_loading) Save();
        return true;
    }

    private static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(FilePath))
            {
                var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (stored is not null)
                {
                    // Copy through the setters so values are validated; don't write the file back while doing so.
                    settings._loading = true;
                    settings.Language = stored.Language;
                    settings.Theme = stored.Theme;
                    settings.NameBar = stored.NameBar;
                    settings.SizeMetric = stored.SizeMetric;
                    settings.ShowSizeInName = stored.ShowSizeInName;
                    settings.ShowShareColumn = stored.ShowShareColumn;
                    settings.ShowAllocatedColumn = stored.ShowAllocatedColumn;
                    settings.UseMftScan = stored.UseMftScan;
                    settings.TopFileCount = stored.TopFileCount;
                    settings._loading = false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable or corrupt settings: start with the defaults.
            settings._loading = false;
        }
        Loc.Instance.Language = settings.Language;
        return settings;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var stored = new StoredSettings
            {
                Language = Language,
                Theme = Theme,
                NameBar = NameBar,
                SizeMetric = SizeMetric,
                ShowSizeInName = ShowSizeInName,
                ShowShareColumn = ShowShareColumn,
                ShowAllocatedColumn = ShowAllocatedColumn,
                UseMftScan = UseMftScan,
                TopFileCount = TopFileCount,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to save settings should never break the app.
        }
    }

    /// <summary>The file format. Kept separate so deserializing doesn't run the setters' side effects.</summary>
    private sealed class StoredSettings
    {
        public string Language { get; set; } = "en";
        public AppTheme Theme { get; set; } = AppTheme.System;
        public NameBarStyle NameBar { get; set; } = NameBarStyle.Gradient;
        public SizeMetric SizeMetric { get; set; } = SizeMetric.OnDisk;
        public bool ShowSizeInName { get; set; }
        public bool ShowShareColumn { get; set; } = true;
        public bool ShowAllocatedColumn { get; set; } = true;
        public bool UseMftScan { get; set; } = true;
        public int TopFileCount { get; set; } = 50;
    }
}

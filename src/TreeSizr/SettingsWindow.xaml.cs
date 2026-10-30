using System.Windows;

namespace TreeSizr;

/// <summary>Edits <see cref="AppSettings.Current"/> directly; every change applies and saves immediately.</summary>
public partial class SettingsWindow : Window
{
    private static readonly int[] TopFileCounts = [25, 50, 100, 250, 500, 1000];

    public SettingsWindow()
    {
        InitializeComponent();
        int current = AppSettings.Current.TopFileCount;
        TopFilesBox.ItemsSource = TopFileCounts.Contains(current) ? TopFileCounts : TopFileCounts.Append(current).Order().ToArray();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyTitleBar(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

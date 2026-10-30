using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TreeSizr.Controls;
using TreeSizr.Scanning;
using TreeSizr.ViewModels;

namespace TreeSizr;

/// <summary>
/// The main window. Scan state lives in <see cref="MainViewModel"/>; this class handles what needs the window:
/// dialogs, Explorer, context menus, and the treemap's mouse interaction.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "WPF windows are not disposable; the view model is disposed when the window closes.")]
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        // A path on the command line is used when restarting as administrator.
        var args = Environment.GetCommandLineArgs();
        _vm = new MainViewModel(args.Length > 1 ? args[1] : null);
        _vm.MessageRaised += (_, message) => MessageBox.Show(this, message.Text, "TreeSizr", MessageBoxButton.OK,
            message.IsError ? MessageBoxImage.Error : MessageBoxImage.Warning);
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = _vm;

        Loc.Instance.LanguageChanged += (_, _) => OnLanguageChanged();
        TreemapHover.Text = Loc.T("TreemapHint");
        Loaded += (_, _) => PathBox.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyTitleBar(this);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsScanning))
            Mouse.OverrideCursor = _vm.IsScanning ? Cursors.AppStarting : null;
    }

    /// <summary>XAML texts follow the language by themselves; texts set from code are refreshed here.</summary>
    private void OnLanguageChanged()
    {
        TreemapHover.Text = Loc.T("TreemapHint");
        Treemap.InvalidateVisual(); // the "(files)" labels are drawn, not bound
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => _vm.Dispose();

    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow { Owner = this }.ShowDialog();

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (Elevation.TryRestartElevated(_vm.Path.Trim()))
            Close();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("BrowseTitle") };
        if (Directory.Exists(_vm.Path))
            dialog.InitialDirectory = _vm.Path;
        if (dialog.ShowDialog(this) == true)
            _vm.Path = dialog.FolderName;
    }

    // ---- Folder tree -----------------------------------------------------------------------

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeItemViewModel item)
            _vm.TreemapRoot = item.Folder;
    }

    /// <summary>Select the row under the mouse before the context menu opens.</summary>
    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item is not null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextPath(sender, out bool isFile) is not { } path) return;
        if (isFile) Shell.SelectInExplorer(path);
        else Shell.OpenFolder(path);
    }

    private void CopyFolderPath_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextPath(sender, out _) is { } path) Shell.CopyToClipboard(path);
    }

    /// <summary>The path a context menu applies to: the selected tree row, or the treemap block under the mouse.</summary>
    private string? GetContextPath(object menuItem, out bool isFile)
    {
        isFile = false;
        var target = ((menuItem as MenuItem)?.Parent as ContextMenu)?.PlacementTarget;
        if (target == Treemap)
            return (Treemap.HoveredItem?.Node ?? Treemap.Root)?.FullPath;
        var item = Tree.SelectedItem as TreeItemViewModel;
        isFile = item?.Kind == TreeItemKind.File;
        return item?.FullPath;
    }

    // ---- Largest files ---------------------------------------------------------------------

    private void TopFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext is FileEntry file)
            Shell.SelectInExplorer(file.FullPath);
    }

    private void ShowFileInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (TopFilesList.SelectedItem is FileEntry file) Shell.SelectInExplorer(file.FullPath);
    }

    private void CopyFilePath_Click(object sender, RoutedEventArgs e)
    {
        if (TopFilesList.SelectedItem is FileEntry file) Shell.CopyToClipboard(file.FullPath);
    }

    // ---- Treemap ---------------------------------------------------------------------------

    private void Treemap_NodeActivated(object? sender, DirNode node) => _vm.TreemapRoot = node;

    private void Treemap_HoverChanged(object? sender, TreemapHit? hit)
    {
        if (hit is null) return;
        TreemapHover.Text = hit.IsFiles
            ? Loc.F("HoverLooseFiles", hit.Node.FullPath, Format.Bytes(hit.Node.OwnFilesSizeBy(Treemap.Metric)), hit.Node.OwnFileCount)
            : Loc.F("HoverFolder", hit.Node.FullPath, Format.Bytes(hit.Node.SizeBy(Treemap.Metric)), hit.Node.FileCount);
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}

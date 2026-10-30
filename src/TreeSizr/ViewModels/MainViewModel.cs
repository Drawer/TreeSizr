using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using TreeSizr.Scanning;

namespace TreeSizr.ViewModels;

/// <summary>A message the window should show in a dialog.</summary>
public sealed record UserMessage(string Text, bool IsError);

/// <summary>
/// State of the main window: what to scan, the running scan, and its results.
/// Things that need the window itself (dialogs, Explorer, the treemap's mouse handling) stay in MainWindow.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? _cts;
    private IDiskScanner? _scanner;
    private Stopwatch? _scanClock;

    /// <summary>Status texts are kept as a function so they can be rebuilt in a new language.</summary>
    private Func<string> _status = () => Loc.T("StatusReady");

    private string _path;
    private string? _selectedDrive;
    private bool _isScanning;
    private IReadOnlyList<TreeItemViewModel> _treeRoots = [];
    private ScanResult? _result;
    private DirNode? _treemapRoot;

    public MainViewModel(string? startPath)
    {
        Drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name).ToList();
        _path = startPath ?? (Drives.Count > 0 ? Drives[0] : "");
        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !IsScanning);
        CancelCommand = new RelayCommand(Cancel, () => IsScanning);
        TreemapUpCommand = new RelayCommand(
            () => { if (TreemapRoot?.Parent is { } parent) TreemapRoot = parent; },
            () => TreemapRoot?.Parent is not null);
        _progressTimer.Tick += (_, _) => UpdateProgress();
        AppSettings.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.SizeMetric)) OnSizeMetricChanged();
        };
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(WindowTitle));
        };
    }

    /// <summary>Raised when the user must be told something in a dialog.</summary>
    public event EventHandler<UserMessage>? MessageRaised;

    public IReadOnlyList<string> Drives { get; }

    public string? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (SetProperty(ref _selectedDrive, value) && value is not null)
                Path = value;
        }
    }

    public string Path
    {
        get => _path;
        set => SetProperty(ref _path, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetProperty(ref _isScanning, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIdle => !IsScanning;

    public string StatusText => _status();

    public string WindowTitle => Elevation.IsElevated ? $"TreeSizr ({Loc.T("Administrator")})" : "TreeSizr";

    /// <summary>Restarting as administrator is offered only when not elevated yet.</summary>
    public bool CanElevate => !Elevation.IsElevated;

    /// <summary>The tree's single root row, as a list for the TreeView.</summary>
    public IReadOnlyList<TreeItemViewModel> TreeRoots
    {
        get => _treeRoots;
        private set => SetProperty(ref _treeRoots, value);
    }

    /// <summary>The largest files of the last scan, by the size measure chosen in the settings.</summary>
    public IReadOnlyList<FileEntry> TopFiles => _result?.LargestFiles(AppSettings.Current.SizeMetric) ?? [];

    /// <summary>The folder the treemap shows.</summary>
    public DirNode? TreemapRoot
    {
        get => _treemapRoot;
        set
        {
            if (!SetProperty(ref _treemapRoot, value)) return;
            OnPropertyChanged(nameof(TreemapTitle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string TreemapTitle => TreemapRoot is { } root ? $"{root.FullPath}  ({Format.Bytes(root.SizeBy(AppSettings.Current.SizeMetric))})" : "";

    public ICommand ScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand TreemapUpCommand { get; }

    public void Dispose()
    {
        _progressTimer.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
    }

    public void Cancel()
    {
        if (_cts is null) return;
        _cts.Cancel();
        SetStatus(() => Loc.T("Cancelling"));
    }

    private async Task ScanAsync()
    {
        if (_cts is not null) return;

        string path = NormalizePath(Path);
        if (!Directory.Exists(path))
        {
            MessageRaised?.Invoke(this, new UserMessage(Loc.F("FolderNotFound", path), IsError: false));
            return;
        }
        Path = path;

        _cts = new CancellationTokenSource();
        var settings = AppSettings.Current;
        var mftReason = MftUnavailableReason.None;
        bool useMft = settings.UseMftScan && MftScanner.CanScan(path, out mftReason);
        // Why the fast scan was not used; a function so the status line can be rebuilt in another language.
        Func<string> whyNotMft = settings.UseMftScan ? () => ReasonText(mftReason) : () => Loc.T("MftDisabled");
        _scanClock = Stopwatch.StartNew();
        IsScanning = true;
        _progressTimer.Start();

        try
        {
            ScanResult result;
            try
            {
                _scanner = useMft ? new MftScanner(settings.TopFileCount) : new DiskScanner(settings.TopFileCount);
                result = await _scanner.ScanAsync(path, _cts.Token);
            }
            catch (Exception ex) when (useMft && ex is not OperationCanceledException)
            {
                // Unusual NTFS layouts can trip up the MFT reader; the normal scan always works.
                whyNotMft = () => Loc.F("MftFailed", ex.Message);
                _scanner = new DiskScanner(settings.TopFileCount);
                result = await _scanner.ScanAsync(path, _cts.Token);
            }

            ShowResult(result);
            SetStatus(() =>
                Loc.F("Done", Loc.T(result.Method == ScanMethod.Mft ? "MethodMft" : "MethodNormal"),
                    Format.Bytes(result.Root.Size), Format.Bytes(result.Root.AllocatedSize),
                    result.Root.FileCount, result.Elapsed.TotalSeconds) +
                (result.Errors > 0 ? Loc.F("SkippedFolders", result.Errors) : "") +
                (result.Method != ScanMethod.Mft && whyNotMft() is { Length: > 0 } reason ? Loc.F("FastScanNotUsed", reason) : ""));
        }
        catch (OperationCanceledException)
        {
            SetStatus(() => Loc.T("Cancelled"));
        }
        catch (Exception ex)
        {
            SetStatus(() => Loc.T("Failed"));
            MessageRaised?.Invoke(this, new UserMessage(ex.Message, IsError: true));
        }
        finally
        {
            _progressTimer.Stop();
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
            _scanner = null;
        }
    }

    private void ShowResult(ScanResult result)
    {
        _result = result;
        var root = new DirNodeViewModel(result.Root, 0) { IsExpanded = true, IsSelected = true };
        TreeRoots = [root];
        OnPropertyChanged(nameof(TopFiles));
        TreemapRoot = result.Root;
    }

    /// <summary>
    /// The tree is ordered by the size measure, so its rows are rebuilt; folders that were open stay open,
    /// and the selected row stays selected.
    /// </summary>
    private void OnSizeMetricChanged()
    {
        OnPropertyChanged(nameof(TopFiles));
        OnPropertyChanged(nameof(TreemapTitle));
        if (TreeRoots is not [DirNodeViewModel oldRoot]) return;

        var expanded = new HashSet<(TreeItemKind, string)>();
        (TreeItemKind, string)? selected = null;
        foreach (var row in Rows(oldRoot))
        {
            if (row.IsExpanded) expanded.Add((row.Kind, row.FullPath));
            if (row.IsSelected) selected = (row.Kind, row.FullPath);
        }

        var root = new DirNodeViewModel(oldRoot.Node, 0);
        Restore(root);
        TreeRoots = [root];

        void Restore(TreeItemViewModel row)
        {
            if (selected == (row.Kind, row.FullPath)) row.IsSelected = true;
            if (!expanded.Contains((row.Kind, row.FullPath))) return;
            row.IsExpanded = true; // folder rows load their children right away; file lists load in the background
            foreach (var child in row.Children.ToList())
                Restore(child);
        }
    }

    /// <summary>All rows that have been created so far, depth first.</summary>
    private static IEnumerable<TreeItemViewModel> Rows(TreeItemViewModel row)
    {
        var stack = new Stack<TreeItemViewModel>([row]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (var child in current.Children)
                stack.Push(child);
        }
    }

    private void UpdateProgress()
    {
        if (_scanner is null || _cts is { IsCancellationRequested: true }) return;
        var p = _scanner.Progress;
        double seconds = _scanClock?.Elapsed.TotalSeconds ?? 0;
        SetStatus(() => Loc.F("Progress", seconds, p.Directories, p.Files, Format.Bytes(p.Bytes), PhaseText(p)));
    }

    private static string PhaseText(ScanProgress p) => p.Phase switch
    {
        ScanPhase.ReadingMft => Loc.T("MftReading") + (p.PhasePercent is { } pct ? $" {pct:0} %" : ""),
        ScanPhase.BuildingTree => Loc.T("MftBuilding"),
        _ => p.CurrentPath,
    };

    private static string ReasonText(MftUnavailableReason reason) => reason switch
    {
        MftUnavailableReason.NotLocalDrive => Loc.T("MftNotLocal"),
        MftUnavailableReason.NotNtfs => Loc.T("MftNotNtfs"),
        MftUnavailableReason.NotElevated => Loc.T("MftNoAdmin"),
        _ => "",
    };

    private void SetStatus(Func<string> status)
    {
        _status = status;
        OnPropertyChanged(nameof(StatusText));
    }

    internal static string NormalizePath(string input)
    {
        string path = input.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':') path += "\\"; // "C:" means the current dir on C:, we want the root
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}

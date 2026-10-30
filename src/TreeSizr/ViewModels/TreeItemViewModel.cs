using System.Collections.ObjectModel;
using System.Globalization;
using TreeSizr.Scanning;

namespace TreeSizr.ViewModels;

public enum TreeItemKind { Folder, Files, File }

/// <summary>
/// One row in the folder tree: a folder, the "[N Files]" group of a folder's own files, or a single file.
/// Children are created lazily on first expand, so even a full-drive scan only materializes the rows
/// the user actually opens.
/// </summary>
public abstract class TreeItemViewModel : ObservableObject
{
    private static readonly TreeItemViewModel Placeholder = new PlaceholderItem();

    private readonly long _parentSize;
    private readonly long _parentAllocated;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _childrenLoaded;

    protected TreeItemViewModel(int depth, long parentSize, long parentAllocated, bool hasChildren)
    {
        Depth = depth;
        _parentSize = parentSize;
        _parentAllocated = parentAllocated;
        if (hasChildren)
            Children.Add(Placeholder);
        else
            _childrenLoaded = true;
    }

    public abstract TreeItemKind Kind { get; }
    public int Depth { get; }
    public ObservableCollection<TreeItemViewModel> Children { get; } = [];

    public abstract string Name { get; }
    public abstract string FullPath { get; }
    public abstract long Size { get; }
    public abstract long AllocatedSize { get; }

    /// <summary>The folder this row belongs to; the treemap shows it when the row is selected.</summary>
    public abstract DirNode Folder { get; }

    public virtual bool IsInaccessible => false;
    public virtual string ToolTip => FullPath;
    public virtual string FilesText => "";

    public string SizeText => Format.Bytes(Size);
    public string AllocatedText => Format.Bytes(AllocatedSize);

    /// <summary>The size the tree is ordered by: <see cref="Size"/> or <see cref="AllocatedSize"/>, per the settings.</summary>
    public long MeasuredSize => Metric == SizeMetric.OnDisk ? AllocatedSize : Size;
    public string MeasuredSizeText => Format.Bytes(MeasuredSize);

    /// <summary>Share of the parent row, 0–100, by the same measure.</summary>
    public double Percent
    {
        get
        {
            long parent = Metric == SizeMetric.OnDisk ? _parentAllocated : _parentSize;
            return parent > 0 ? 100.0 * MeasuredSize / parent : 100.0;
        }
    }
    public string PercentText => string.Format(CultureInfo.CurrentCulture, "{0:0.0} %", Percent);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value && !_childrenLoaded)
                LoadChildren();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>True when <see cref="CreateChildren"/> reads the disk; it then runs off the UI thread.</summary>
    protected virtual bool ChildrenComeFromDisk => false;

    protected virtual IEnumerable<TreeItemViewModel> CreateChildren() => [];

    private async void LoadChildren()
    {
        _childrenLoaded = true;
        // A folder can hold hundreds of thousands of files; don't freeze the window while listing them.
        var children = ChildrenComeFromDisk
            ? await Task.Run(() => CreateChildren().ToList())
            : CreateChildren().ToList();
        Children.Clear();
        foreach (var child in children)
            Children.Add(child);
    }

    /// <summary>Rows are rebuilt when this setting changes, so reading it here is enough.</summary>
    protected static SizeMetric Metric => AppSettings.Current.SizeMetric;

    protected static string Count(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Stands in for the children until they are loaded, so the row shows an expander.</summary>
    private sealed class PlaceholderItem() : TreeItemViewModel(0, 0, 0, false)
    {
        public override TreeItemKind Kind => TreeItemKind.File;
        public override string Name => "…";
        public override string FullPath => "";
        public override long Size => 0;
        public override long AllocatedSize => 0;
        public override DirNode Folder { get; } = new("", null);
    }
}

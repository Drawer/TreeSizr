using TreeSizr.Scanning;

namespace TreeSizr.ViewModels;

/// <summary>A folder row.</summary>
public sealed class DirNodeViewModel(DirNode node, int depth)
    : TreeItemViewModel(depth, node.Parent?.Size ?? 0, node.Parent?.AllocatedSize ?? 0, node.Children.Count > 0 || node.OwnFileCount > 0)
{
    public DirNode Node { get; } = node;

    public override TreeItemKind Kind => TreeItemKind.Folder;
    public override string Name => Node.Name;
    public override string FullPath => Node.FullPath;
    public override long Size => Node.Size;
    public override long AllocatedSize => Node.AllocatedSize;
    public override DirNode Folder => Node;
    public override bool IsInaccessible => Node.IsInaccessible;
    public override string ToolTip => Node.IsInaccessible ? Loc.F("NoAccessTip", Node.FullPath) : Node.FullPath;
    public override string FilesText => Count(Node.FileCount);

    /// <summary>A folder with only files lists them directly, without the "[N Files]" group, like TreeSize.</summary>
    private bool OnlyFiles => Node.Children.Count == 0;

    protected override bool ChildrenComeFromDisk => OnlyFiles;

    /// <summary>Subfolders plus the files group, all sorted by the chosen size measure.</summary>
    protected override IEnumerable<TreeItemViewModel> CreateChildren()
    {
        if (OnlyFiles)
            return FileItemViewModel.ListFiles(Node, Depth + 1, Node.Size, Node.AllocatedSize);
        return CreateFoldersAndGroup();
    }

    private IEnumerable<TreeItemViewModel> CreateFoldersAndGroup()
    {
        var files = Node.OwnFileCount > 0 ? new FilesGroupViewModel(Node, Depth + 1) : null;
        foreach (var child in Node.Children.OrderByDescending(c => c.SizeBy(Metric)))
        {
            if (files is not null && files.MeasuredSize >= child.SizeBy(Metric))
            {
                yield return files;
                files = null;
            }
            yield return new DirNodeViewModel(child, Depth + 1);
        }
        if (files is not null)
            yield return files;
    }
}

using TreeSizr.Scanning;

namespace TreeSizr.ViewModels;

/// <summary>
/// The "[N Files]" row that groups the files directly in a folder. The scan only keeps totals per folder,
/// so the individual files are listed from disk when the group is expanded.
/// </summary>
public sealed class FilesGroupViewModel(DirNode folder, int depth)
    : TreeItemViewModel(depth, folder.Size, folder.AllocatedSize, true)
{
    public override TreeItemKind Kind => TreeItemKind.Files;
    public override string Name => FilesText;
    public override string FullPath => folder.FullPath;
    public override long Size => folder.OwnFilesSize;
    public override long AllocatedSize => folder.OwnAllocatedSize;
    public override DirNode Folder => folder;
    public override string FilesText => Count(folder.OwnFileCount);

    protected override bool ChildrenComeFromDisk => true;

    protected override IEnumerable<TreeItemViewModel> CreateChildren() => FileItemViewModel.ListFiles(folder, Depth + 1, Size, AllocatedSize);
}

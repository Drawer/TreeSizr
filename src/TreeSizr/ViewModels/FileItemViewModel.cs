using TreeSizr.Scanning;

namespace TreeSizr.ViewModels;

/// <summary>A single file row.</summary>
public sealed class FileItemViewModel(DirNode folder, FolderFile file, int depth, long parentSize, long parentAllocated)
    : TreeItemViewModel(depth, parentSize, parentAllocated, false)
{
    public override TreeItemKind Kind => TreeItemKind.File;
    public override string Name => Path.GetFileName(FullPath);
    public override string FullPath => file.FullPath;
    public override long Size => file.Size;
    public override long AllocatedSize => file.AllocatedSize;
    public override DirNode Folder => folder;

    /// <summary>Rows for the files directly in <paramref name="folder"/>, largest first. Reads the disk.</summary>
    internal static IEnumerable<TreeItemViewModel> ListFiles(DirNode folder, int depth, long parentSize, long parentAllocated)
    {
        var metric = Metric;
        return DiskScanner.ListFiles(folder.FullPath)
            .OrderByDescending(f => f.SizeBy(metric))
            .Select(f => new FileItemViewModel(folder, f, depth, parentSize, parentAllocated));
    }
}

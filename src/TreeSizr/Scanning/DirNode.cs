namespace TreeSizr.Scanning;

/// <summary>
/// One scanned directory. Sizes and counts are totals including all subdirectories.
/// Children are sorted by size, largest first, once the scan has finished.
/// </summary>
public sealed class DirNode
{
    public DirNode(string fullPath, DirNode? parent)
    {
        FullPath = fullPath;
        Parent = parent;
    }

    public string FullPath { get; }
    public DirNode? Parent { get; }
    public string Name => Parent is null ? FullPath : Path.GetFileName(FullPath);

    public List<DirNode> Children { get; internal set; } = [];

    /// <summary>Total size of all files in this directory and below.</summary>
    public long Size { get; internal set; }

    /// <summary>
    /// Total space actually allocated on disk. Smaller than <see cref="Size"/> for cloud placeholders,
    /// sparse and compressed files; hard links are counted once when the MFT scanner is used.
    /// </summary>
    public long AllocatedSize { get; internal set; }

    /// <summary>Size of the files directly in this directory.</summary>
    public long OwnFilesSize { get; internal set; }

    public long OwnAllocatedSize { get; internal set; }

    /// <summary>Number of files in this directory and below.</summary>
    public long FileCount { get; internal set; }

    public int OwnFileCount { get; internal set; }

    /// <summary><see cref="Size"/> or <see cref="AllocatedSize"/>.</summary>
    public long SizeBy(SizeMetric metric) => metric == SizeMetric.OnDisk ? AllocatedSize : Size;

    /// <summary><see cref="OwnFilesSize"/> or <see cref="OwnAllocatedSize"/>.</summary>
    public long OwnFilesSizeBy(SizeMetric metric) => metric == SizeMetric.OnDisk ? OwnAllocatedSize : OwnFilesSize;

    /// <summary>True when the directory could not be read (access denied or I/O error).</summary>
    public bool IsInaccessible { get; internal set; }

    /// <summary>
    /// Fills in the totals from the Own* values for the whole tree and sorts children by size.
    /// Iterative, so very deep trees cannot overflow the stack.
    /// </summary>
    internal static void Aggregate(DirNode root)
    {
        var stack = new Stack<(DirNode Node, bool ChildrenDone)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (node, childrenDone) = stack.Pop();
            if (!childrenDone)
            {
                stack.Push((node, true));
                foreach (var child in node.Children)
                    stack.Push((child, false));
                continue;
            }

            long size = node.OwnFilesSize, allocated = node.OwnAllocatedSize, files = node.OwnFileCount;
            foreach (var child in node.Children)
            {
                size += child.Size;
                allocated += child.AllocatedSize;
                files += child.FileCount;
            }
            node.Size = size;
            node.AllocatedSize = allocated;
            node.FileCount = files;
            node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
    }
}

namespace TreeSizr.Scanning;

public sealed record FileEntry(int Rank, string FullPath, long Size, long AllocatedSize)
{
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
}

/// <summary>A file listed from a folder on demand (see <see cref="DiskScanner.ListFiles"/>).</summary>
public sealed record FolderFile(string FullPath, long Size, long AllocatedSize)
{
    public long SizeBy(SizeMetric metric) => metric == SizeMetric.OnDisk ? AllocatedSize : Size;
}

/// <summary>
/// Thread-safe collector that keeps the N largest files seen, by length or by space on disk.
/// <see cref="Threshold"/> can be read without locking so most files are rejected cheaply.
/// </summary>
internal sealed class TopFilesCollector(int capacity, SizeMetric metric)
{
    private readonly PriorityQueue<(string Path, long Size, long Allocated), long> _queue = new(capacity + 1);
    private readonly object _lock = new();
    private long _threshold = -1;

    /// <summary>Files must be strictly larger than this (by the collector's metric) to make the list.</summary>
    public long Threshold => Volatile.Read(ref _threshold);

    public long KeyOf(long size, long allocated) => metric == SizeMetric.OnDisk ? allocated : size;

    public void Offer(string path, long size, long allocated)
    {
        long key = KeyOf(size, allocated);
        if (key <= Threshold) return;
        lock (_lock)
        {
            if (_queue.Count < capacity)
            {
                _queue.Enqueue((path, size, allocated), key);
            }
            else if (_queue.TryPeek(out _, out var smallest) && key > smallest)
            {
                _queue.DequeueEnqueue((path, size, allocated), key);
            }

            if (_queue.Count == capacity && _queue.TryPeek(out _, out var min))
                Volatile.Write(ref _threshold, min);
        }
    }

    public List<FileEntry> ToSortedList()
    {
        lock (_lock)
        {
            return _queue.UnorderedItems
                .OrderByDescending(x => x.Priority)
                .Select((x, i) => new FileEntry(i + 1, x.Element.Path, x.Element.Size, x.Element.Allocated))
                .ToList();
        }
    }
}

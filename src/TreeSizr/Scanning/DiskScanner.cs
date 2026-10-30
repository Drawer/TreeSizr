using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace TreeSizr.Scanning;

/// <summary>
/// Scans a directory tree through the normal file system APIs. Works on any drive, without admin rights.
/// Directories go into a shared work stack that all worker threads pull from, so one huge, deep
/// directory is spread over all workers instead of being scanned by a single thread.
/// Unreadable directories are marked and skipped.
/// </summary>
public sealed class DiskScanner : IDiskScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false, // we catch the errors ourselves so we can count and mark them
        AttributesToSkip = 0,       // include hidden and system files
        ReturnSpecialDirectories = false,
    };

    /// <summary>Cloud placeholders (OneDrive "online only") whose content is not on this disk.</summary>
    private const FileAttributes CloudOnlyAttributes =
        FileAttributes.Offline | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

    /// <summary>Files with these attributes may use less disk space than their length; ask Windows for the real figure.</summary>
    private const FileAttributes ExactAllocationAttributes = FileAttributes.Compressed | FileAttributes.SparseFile;

    private readonly TopFilesCollector _largestBySize;
    private readonly TopFilesCollector _largestOnDisk;
    private readonly FileSystemEnumerable<Entry>.FindTransform _transform;
    private readonly int _workerCount;
    private long _clusterSize = 4096;

    private readonly ConcurrentStack<DirNode> _work = new();
    private SemaphoreSlim? _available;
    private long _pending;

    private long _directories, _files, _bytes, _errors;
    private string _currentPath = "";

    public DiskScanner(int topFileCount = 50, int? workerCount = null)
    {
        _largestBySize = new TopFilesCollector(topFileCount, SizeMetric.Size);
        _largestOnDisk = new TopFilesCollector(topFileCount, SizeMetric.OnDisk);
        _workerCount = workerCount ?? Math.Clamp(Environment.ProcessorCount * 2, 4, 32);

        // Only build a full path string for files that could make the top list or need an exact allocation lookup.
        _transform = (ref FileSystemEntry e) =>
        {
            if (e.IsDirectory)
                return new Entry(e.ToFullPath(), true, 0, 0, e.Attributes);

            long length = e.Length;
            string? path = null;
            long allocated;
            if ((e.Attributes & CloudOnlyAttributes) != 0)
            {
                // Asking Windows would open every placeholder, which is slow; they take (almost) no local space.
                allocated = 0;
            }
            else if ((e.Attributes & ExactAllocationAttributes) != 0)
            {
                path = e.ToFullPath();
                allocated = GetAllocatedSize(path, length);
            }
            else
            {
                allocated = (length + _clusterSize - 1) / _clusterSize * _clusterSize;
            }
            if (path is null && (length > _largestBySize.Threshold || allocated > _largestOnDisk.Threshold))
                path = e.ToFullPath();
            return new Entry(path, false, length, allocated, e.Attributes);
        };
    }

    public ScanProgress Progress => new(
        Interlocked.Read(ref _directories),
        Interlocked.Read(ref _files),
        Interlocked.Read(ref _bytes),
        Interlocked.Read(ref _errors),
        Volatile.Read(ref _currentPath));

    public Task<ScanResult> ScanAsync(string rootPath, CancellationToken ct) => Task.Run(() =>
    {
        var stopwatch = Stopwatch.StartNew();
        _clusterSize = GetClusterSize(rootPath);

        var root = new DirNode(rootPath, null);
        using var available = new SemaphoreSlim(0);
        _available = available;
        _pending = 1;
        _work.Push(root);
        _available.Release();

        // A failing worker cancels the others, otherwise they would wait forever for work.
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, _workerCount)
            .Select(_ => Task.Factory.StartNew(() => Worker(failed), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        try
        {
            Task.WaitAll(workers);
        }
        catch (AggregateException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (AggregateException ex)
        {
            throw ex.InnerExceptions.First(e => e is not OperationCanceledException);
        }

        DirNode.Aggregate(root);
        return new ScanResult(root, _largestBySize.ToSortedList(), _largestOnDisk.ToSortedList(), Interlocked.Read(ref _errors),
            stopwatch.Elapsed, ScanMethod.Normal);
    }, ct);

    private void Worker(CancellationTokenSource failed)
    {
        var ct = failed.Token;
        try
        {
            while (true)
            {
                _available!.Wait(ct);
                if (!_work.TryPop(out var node))
                    return; // woken up because all work is done

                ScanDirectory(node);

                if (Interlocked.Decrement(ref _pending) == 0)
                    _available.Release(_workerCount); // wake every worker so they can exit
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed.Cancel();
            throw;
        }
    }

    private void ScanDirectory(DirNode node)
    {
        Volatile.Write(ref _currentPath, node.FullPath);
        Interlocked.Increment(ref _directories);

        var subdirs = new List<DirNode>();
        long ownSize = 0, ownAllocated = 0;
        int ownCount = 0;

        try
        {
            foreach (var entry in new FileSystemEnumerable<Entry>(node.FullPath, _transform, Options))
            {
                if (entry.IsDirectory)
                {
                    if (!IsLink(entry))
                        subdirs.Add(new DirNode(entry.Path!, node));
                }
                else
                {
                    ownSize += entry.Length;
                    ownAllocated += entry.Allocated;
                    ownCount++;
                    if (entry.Path is not null)
                    {
                        _largestBySize.Offer(entry.Path, entry.Length, entry.Allocated);
                        _largestOnDisk.Offer(entry.Path, entry.Length, entry.Allocated);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.IsInaccessible = true;
            Interlocked.Increment(ref _errors);
        }

        Interlocked.Add(ref _files, ownCount);
        Interlocked.Add(ref _bytes, ownSize);

        node.OwnFilesSize = ownSize;
        node.OwnAllocatedSize = ownAllocated;
        node.OwnFileCount = ownCount;
        node.Children = subdirs;

        // Only this worker touches node.Children; the subdirectories are filled in by whichever worker pops them.
        foreach (var d in subdirs)
        {
            Interlocked.Increment(ref _pending);
            _work.Push(d);
            _available!.Release();
        }
    }

    /// <summary>
    /// Junctions and symbolic links are skipped to avoid loops and double counting.
    /// Other reparse points (such as OneDrive cloud placeholders) are scanned normally.
    /// </summary>
    private static bool IsLink(Entry entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try
        {
            return new DirectoryInfo(entry.Path!).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    /// <summary>
    /// The files directly in <paramref name="folder"/>, largest first, with the same disk space estimate the scan uses.
    /// Empty when the folder cannot be read.
    /// </summary>
    public static List<FolderFile> ListFiles(string folder)
    {
        long clusterSize = GetClusterSize(folder);
        List<FolderFile> files;
        try
        {
            files = new FileSystemEnumerable<FolderFile>(folder,
                (ref FileSystemEntry e) =>
                {
                    string path = e.ToFullPath();
                    return new FolderFile(path, e.Length, EstimateAllocatedSize(path, e.Length, e.Attributes, clusterSize));
                },
                ListOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            }.ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return [];
        }
        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        return files;
    }

    private static long EstimateAllocatedSize(string path, long length, FileAttributes attributes, long clusterSize)
    {
        if ((attributes & CloudOnlyAttributes) != 0) return 0;
        if ((attributes & ExactAllocationAttributes) != 0) return GetAllocatedSize(path, length);
        return (length + clusterSize - 1) / clusterSize * clusterSize;
    }

    private static long GetAllocatedSize(string path, long fallback)
    {
        uint low = GetCompressedFileSizeW(path, out uint high);
        if (low == uint.MaxValue && Marshal.GetLastPInvokeError() != 0)
            return fallback;
        return ((long)high << 32) | low;
    }

    private static long GetClusterSize(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is not null && GetDiskFreeSpaceW(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
            return (long)sectorsPerCluster * bytesPerSector;
        return 4096;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string rootPath, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);

    private readonly record struct Entry(string? Path, bool IsDirectory, long Length, long Allocated, FileAttributes Attributes);
}

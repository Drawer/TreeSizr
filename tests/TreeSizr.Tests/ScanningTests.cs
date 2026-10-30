using TreeSizr.Scanning;

namespace TreeSizr.Tests;

public class DirNodeTests
{
    [Fact]
    public void Aggregate_SumsSubtreesAndSortsBySize()
    {
        var root = new DirNode(@"C:\root", null) { OwnFilesSize = 10, OwnAllocatedSize = 16, OwnFileCount = 1 };
        var small = new DirNode(@"C:\root\small", root) { OwnFilesSize = 5, OwnAllocatedSize = 8, OwnFileCount = 2 };
        var big = new DirNode(@"C:\root\big", root) { OwnFilesSize = 100, OwnAllocatedSize = 104, OwnFileCount = 3 };
        var deep = new DirNode(@"C:\root\small\deep", small) { OwnFilesSize = 50, OwnAllocatedSize = 56, OwnFileCount = 4 };
        root.Children = [small, big];
        small.Children = [deep];

        DirNode.Aggregate(root);

        Assert.Equal(165, root.Size);
        Assert.Equal(184, root.AllocatedSize);
        Assert.Equal(10, root.FileCount);
        Assert.Equal(55, small.Size);
        Assert.Equal(new[] { big, small }, root.Children); // largest first
        Assert.Equal(184, root.SizeBy(SizeMetric.OnDisk));
        Assert.Equal(16, root.OwnFilesSizeBy(SizeMetric.OnDisk));
    }

    [Fact]
    public void Aggregate_HandlesVeryDeepTrees()
    {
        var root = new DirNode("root", null);
        var node = root;
        for (int i = 0; i < 100_000; i++) // would overflow the stack if Aggregate were recursive
        {
            var child = new DirNode($"d{i}", node) { OwnFilesSize = 1, OwnFileCount = 1 };
            node.Children = [child];
            node = child;
        }

        DirNode.Aggregate(root);

        Assert.Equal(100_000, root.Size);
    }
}

public class TopFilesCollectorTests
{
    [Fact]
    public void KeepsTheLargestFilesInOrder()
    {
        var collector = new TopFilesCollector(3, SizeMetric.Size);
        foreach (var size in new long[] { 5, 50, 1, 40, 30, 60, 2 })
            collector.Offer($"f{size}", size, size);

        var top = collector.ToSortedList();

        Assert.Equal(new long[] { 60, 50, 40 }, top.Select(f => f.Size));
        Assert.Equal(new[] { 1, 2, 3 }, top.Select(f => f.Rank));
        Assert.Equal(40, collector.Threshold);
    }

    [Fact]
    public void CanRankBySpaceOnDisk()
    {
        var collector = new TopFilesCollector(2, SizeMetric.OnDisk);
        collector.Offer("online-only.iso", size: 9000, allocated: 0); // OneDrive placeholder: big, but not on disk
        collector.Offer("vm.vhdx", size: 5000, allocated: 4000);
        collector.Offer("photo.jpg", size: 3000, allocated: 3072);

        var top = collector.ToSortedList();

        Assert.Equal(new[] { "vm.vhdx", "photo.jpg" }, top.Select(f => f.FullPath));
        Assert.Equal(5000, top[0].Size); // both sizes are kept
    }

    [Fact]
    public void IsThreadSafe()
    {
        var collector = new TopFilesCollector(10, SizeMetric.Size);
        Parallel.For(0, 100_000, i => collector.Offer($"f{i}", i, i));

        Assert.Equal(Enumerable.Range(99_990, 10).Reverse().Select(i => (long)i), collector.ToSortedList().Select(f => f.Size));
    }
}

/// <summary>Scans a real (temporary) folder tree with the normal scanner.</summary>
public sealed class DiskScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TreeSizrTests-" + Guid.NewGuid().ToString("N"));

    public DiskScannerTests()
    {
        Write("a.bin", 1000);
        Write(@"sub\b.bin", 5000);
        Write(@"sub\deeper\c.bin", 300);
        Write(@"sub\deeper\d.bin", 0);
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Scan_CountsSizesAndFiles()
    {
        var result = await new DiskScanner(topFileCount: 2).ScanAsync(_root, CancellationToken.None);

        Assert.Equal(6300, result.Root.Size);
        Assert.Equal(4, result.Root.FileCount);
        Assert.Equal(1, result.Root.OwnFileCount);
        Assert.Equal(new[] { "sub", "empty" }, result.Root.Children.Select(c => c.Name));
        Assert.Equal(5300, result.Root.Children[0].Size);
        Assert.True(result.Root.AllocatedSize >= result.Root.Size - 300); // whole clusters, empty file takes none
        Assert.Equal(new[] { "b.bin", "a.bin" }, result.LargestBySize.Select(f => f.Name));
        Assert.Equal(new[] { "b.bin", "a.bin" }, result.LargestOnDisk.Select(f => f.Name));
        Assert.Equal(0, result.Errors);
        Assert.Equal(ScanMethod.Normal, result.Method);
    }

    [Fact]
    public async Task Scan_CanBeCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DiskScanner().ScanAsync(_root, cts.Token));
    }

    [Fact]
    public void ListFiles_ReturnsFilesLargestFirst()
    {
        var files = DiskScanner.ListFiles(Path.Combine(_root, "sub", "deeper"));

        Assert.Equal(new[] { 300L, 0L }, files.Select(f => f.Size));
        Assert.EndsWith("c.bin", files[0].FullPath);
    }

    [Fact]
    public void ListFiles_ReturnsNothingForMissingFolder() =>
        Assert.Empty(DiskScanner.ListFiles(Path.Combine(_root, "does-not-exist")));

    private void Write(string relativePath, int size)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }
}

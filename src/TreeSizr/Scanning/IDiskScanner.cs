namespace TreeSizr.Scanning;

public enum ScanMethod { Normal, Mft }

/// <summary>What a scanner is doing; the normal scan only has <see cref="Scanning"/>.</summary>
public enum ScanPhase { Scanning, ReadingMft, BuildingTree }

/// <summary>Which size to sort and compare by: the file length, or the space allocated on disk.</summary>
public enum SizeMetric { Size, OnDisk }

/// <param name="PhasePercent">Progress of the current phase (0–100) when known.</param>
public readonly record struct ScanProgress(
    long Directories, long Files, long Bytes, long Errors, string CurrentPath, ScanPhase Phase = ScanPhase.Scanning,
    double? PhasePercent = null);

/// <param name="LargestBySize">The largest files by length, largest first.</param>
/// <param name="LargestOnDisk">The files that take the most disk space, largest first.</param>
public sealed record ScanResult(
    DirNode Root, IReadOnlyList<FileEntry> LargestBySize, IReadOnlyList<FileEntry> LargestOnDisk,
    long Errors, TimeSpan Elapsed, ScanMethod Method)
{
    public IReadOnlyList<FileEntry> LargestFiles(SizeMetric metric) =>
        metric == SizeMetric.OnDisk ? LargestOnDisk : LargestBySize;
}

public interface IDiskScanner
{
    /// <summary>A snapshot of the progress; safe to read from another thread while scanning.</summary>
    ScanProgress Progress { get; }

    /// <summary>Scans <paramref name="rootPath"/>. A scanner instance is meant for a single scan.</summary>
    Task<ScanResult> ScanAsync(string rootPath, CancellationToken ct);
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TreeSizr.Scanning;

/// <summary>Why <see cref="MftScanner.CanScan"/> said no.</summary>
public enum MftUnavailableReason { None, NotLocalDrive, NotNtfs, NotElevated }

/// <summary>
/// Scans an NTFS volume by reading its Master File Table directly instead of listing every directory.
/// One sequential read of the MFT gives every file's name, parent directory, size and allocated size,
/// which is typically 10–50× faster than a normal scan. Requires administrator rights and a local NTFS volume.
/// </summary>
/// <remarks>
/// Hard links: every link counts as a file with its full size in its own directory (like Explorer and the
/// normal scan), but the allocated size is counted only once, so <see cref="DirNode.AllocatedSize"/> of the
/// volume root matches the space actually in use.
/// </remarks>
public sealed class MftScanner : IDiskScanner
{
    private const int RootRecord = 5;          // the "." record of the volume root
    private const int FirstUserRecord = 16;    // records below this are NTFS metadata files ($MFT, $LogFile, …)

    private const uint AttrAttributeList = 0x20;
    private const uint AttrFileName = 0x30;
    private const uint AttrData = 0x80;
    private const uint AttrEnd = 0xFFFFFFFF;
    private const byte NamespaceDos = 2;
    private const uint FileNameFlagDirectory = 0x10000000;
    private const int ResidentHeaderSize = 0x18;     // attribute header up to the resident value offset
    private const int NonResidentHeaderSize = 0x40;  // attribute header up to and including the data size

    private readonly int _topFileCount;
    private long _recordsRead, _totalRecords, _directories, _files, _bytes;
    private volatile int _phase = (int)ScanPhase.ReadingMft;

    public MftScanner(int topFileCount = 50) => _topFileCount = topFileCount;

    public ScanProgress Progress
    {
        get
        {
            long total = Interlocked.Read(ref _totalRecords);
            long read = Interlocked.Read(ref _recordsRead);
            var phase = (ScanPhase)_phase;
            double? percent = phase == ScanPhase.ReadingMft && total > 0 ? 100.0 * read / total : null;
            return new(Interlocked.Read(ref _directories), Interlocked.Read(ref _files), Interlocked.Read(ref _bytes), 0, "",
                phase, percent);
        }
    }

    /// <summary>True when <paramref name="path"/> is on a local NTFS volume and we have the rights to read its MFT.</summary>
    public static bool CanScan(string path, out MftUnavailableReason reason)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null || root.Length != 3 || root[1] != ':')
            {
                reason = MftUnavailableReason.NotLocalDrive;
                return false;
            }
            var drive = new DriveInfo(root);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || drive.DriveFormat != "NTFS")
            {
                reason = MftUnavailableReason.NotNtfs;
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            reason = MftUnavailableReason.NotLocalDrive;
            return false;
        }

        if (!Elevation.IsElevated)
        {
            reason = MftUnavailableReason.NotElevated;
            return false;
        }
        reason = MftUnavailableReason.None;
        return true;
    }

    public Task<ScanResult> ScanAsync(string rootPath, CancellationToken ct) => Task.Run(() => Scan(rootPath, ct), ct);

    private ScanResult Scan(string rootPath, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        rootPath = Path.GetFullPath(rootPath);
        string volumeRoot = Path.GetPathRoot(rootPath)!;

        using var volume = OpenVolume(volumeRoot);
        var info = GetVolumeData(volume);
        int recordSize = (int)info.BytesPerFileRecordSegment;
        long clusterSize = info.BytesPerCluster;
        long recordCount = info.MftValidDataLength / recordSize;
        if (recordCount > int.MaxValue)
            throw new NotSupportedException("The MFT of this volume is too large.");
        Interlocked.Exchange(ref _totalRecords, recordCount);

        int scanRootRecord = rootPath.Length == volumeRoot.Length ? RootRecord : GetRecordNumber(rootPath);

        // The MFT is itself a file; record 0 tells us where its pieces are on disk.
        var mftExtents = ReadMftExtents(volume, info, recordSize);

        var mft = new MftData((int)recordCount);
        _phase = (int)ScanPhase.ReadingMft;
        ReadAllRecords(volume, mftExtents, clusterSize, recordSize, recordCount, mft, ct);

        _phase = (int)ScanPhase.BuildingTree;
        var builder = new TreeBuilder(mft, scanRootRecord, rootPath);
        var largestBySize = new PriorityQueue<int, long>(_topFileCount + 1);
        var largestOnDisk = new PriorityQueue<int, long>(_topFileCount + 1);
        var allocationCounted = new bool[recordCount];
        long fileCount = 0;

        foreach (var link in mft.FileLinks)
        {
            var dir = builder.Resolve(link.Parent);
            if (dir is null) continue;

            long size = mft.Size[link.Record];
            dir.OwnFilesSize += size;
            dir.OwnFileCount++;
            if (!allocationCounted[link.Record])
            {
                allocationCounted[link.Record] = true;
                dir.OwnAllocatedSize += mft.Allocated[link.Record];

                KeepLargest(largestBySize, link.Record, size);
                KeepLargest(largestOnDisk, link.Record, mft.Allocated[link.Record]);
            }
            if (++fileCount % 65536 == 0)
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Exchange(ref _files, fileCount);
            }
        }
        Interlocked.Exchange(ref _files, fileCount);
        Interlocked.Exchange(ref _directories, builder.DirectoryCount);

        DirNode.Aggregate(builder.Root);
        Interlocked.Exchange(ref _bytes, builder.Root.Size);

        // File names are not kept in memory during the scan; read these few records again.
        var paths = new Dictionary<int, string?>();
        List<FileEntry> ToEntries(PriorityQueue<int, long> queue)
        {
            var entries = new List<FileEntry>(queue.Count);
            foreach (var (record, _) in queue.UnorderedItems.OrderByDescending(x => x.Priority))
            {
                if (!paths.TryGetValue(record, out var path))
                    paths[record] = path = ReadFilePath(volume, mftExtents, clusterSize, recordSize, record, builder);
                if (path is not null)
                    entries.Add(new FileEntry(entries.Count + 1, path, mft.Size[record], mft.Allocated[record]));
            }
            return entries;
        }

        return new ScanResult(builder.Root, ToEntries(largestBySize), ToEntries(largestOnDisk), 0, stopwatch.Elapsed,
            ScanMethod.Mft);
    }

    private void KeepLargest(PriorityQueue<int, long> queue, int record, long key)
    {
        if (queue.Count < _topFileCount) queue.Enqueue(record, key);
        else if (queue.TryPeek(out _, out long smallest) && key > smallest) queue.DequeueEnqueue(record, key);
    }

    // ---- Reading the MFT -----------------------------------------------------------------------

    private void ReadAllRecords(SafeFileHandle volume, List<Extent> extents, long clusterSize, int recordSize,
        long recordCount, MftData mft, CancellationToken ct)
    {
        // Large sequential reads; double-buffered so parsing one chunk overlaps reading the next.
        const int chunkSize = 4 * 1024 * 1024;
        var chunks = new List<(long Offset, int Length)>();
        long recordsPlanned = 0;
        foreach (var extent in extents)
        {
            long offset = extent.Lcn * clusterSize;
            long remaining = extent.ClusterCount * clusterSize;
            while (remaining > 0 && recordsPlanned < recordCount)
            {
                int length = (int)Math.Min(chunkSize, remaining);
                length = (int)Math.Min(length, (recordCount - recordsPlanned) * recordSize);
                length = (int)((length + clusterSize - 1) / clusterSize * clusterSize); // volume reads must be aligned
                chunks.Add((offset, length));
                recordsPlanned += length / recordSize;
                offset += length;
                remaining -= length;
            }
        }

        var buffers = new[] { new byte[chunkSize], new byte[chunkSize] };
        long recordIndex = 0;
        Task<int>? pending = chunks.Count > 0 ? ReadAsync(0) : null;
        for (int c = 0; c < chunks.Count; c++)
        {
            ct.ThrowIfCancellationRequested();
            int read = pending!.GetAwaiter().GetResult();
            var buffer = buffers[c % 2];
            pending = c + 1 < chunks.Count ? ReadAsync(c + 1) : null;

            for (int off = 0; off + recordSize <= read && recordIndex < recordCount; off += recordSize, recordIndex++)
                ParseRecord(buffer.AsSpan(off, recordSize), (int)recordIndex, mft);

            Interlocked.Exchange(ref _recordsRead, recordIndex);
            Interlocked.Exchange(ref _files, mft.FileLinks.Count);
            Interlocked.Exchange(ref _directories, mft.DirectoryCount);
        }

        Task<int> ReadAsync(int index)
        {
            var (offset, length) = chunks[index];
            var buffer = buffers[index % 2];
            return Task.Run(() => ReadAt(volume, offset, buffer, length));
        }
    }

    /// <summary>
    /// Reads the names, parent and sizes from one MFT record. Records come straight from disk, so every
    /// offset is checked; a damaged record is skipped instead of failing the scan.
    /// </summary>
    internal static void ParseRecord(Span<byte> record, int index, MftData mft)
    {
        if (record.Length < 0x30 || !ApplyFixups(record)) return;

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        if ((flags & 0x01) == 0) return; // not in use

        // Extension records hold overflow attributes of a base record.
        long baseRef = (long)(BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]) & 0xFFFFFFFFFFFF);
        int owner = baseRef != 0 ? (int)baseRef : index;
        if (owner < FirstUserRecord && owner != RootRecord) return;
        if ((uint)owner >= (uint)mft.Size.Length) return; // base reference outside the MFT: damaged

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        int bytesInUse = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);

        while (offset + 16 <= bytesInUse)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == AttrEnd) break;
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length <= 0 || offset + length > bytesInUse) break;
            var attr = record.Slice(offset, length);
            offset += length;
            if (attr.Length < ResidentHeaderSize) continue;
            bool nonResident = attr[0x08] != 0;
            byte nameLength = attr[0x09];

            if (type == AttrFileName && !nonResident)
            {
                ParseFileName(attr, owner, mft);
            }
            else if (type == AttrData && nameLength == 0) // unnamed stream = the file's content
            {
                if (!nonResident)
                {
                    mft.Size[owner] = BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]);
                    // Resident data lives inside the MFT record and takes no clusters of its own.
                }
                else if (attr.Length >= NonResidentHeaderSize
                         && BinaryPrimitives.ReadInt64LittleEndian(attr[0x10..]) == 0) // first extent carries the sizes
                {
                    ushort attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x0C..]);
                    ushort runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x20..]);
                    long allocated = BinaryPrimitives.ReadInt64LittleEndian(attr[0x28..]);
                    long dataSize = BinaryPrimitives.ReadInt64LittleEndian(attr[0x30..]);
                    // Compressed and sparse files (including cloud placeholders) store the clusters really in use.
                    if ((attrFlags & 0x8001) != 0 && runsOffset >= 0x48 && attr.Length >= 0x48)
                        allocated = BinaryPrimitives.ReadInt64LittleEndian(attr[0x40..]);
                    mft.Size[owner] = dataSize;
                    mft.Allocated[owner] = allocated;
                }
            }
        }
    }

    private static void ParseFileName(Span<byte> attr, int owner, MftData mft)
    {
        int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
        if (valueOffset > attr.Length) return;
        var value = attr[valueOffset..];
        if (value.Length < 0x42) return;

        byte nameSpace = value[0x41];
        if (nameSpace == NamespaceDos) return; // 8.3 alias of a name we also see in full

        int parent = (int)(BinaryPrimitives.ReadUInt64LittleEndian(value) & 0xFFFFFFFFFFFF);
        uint fileFlags = BinaryPrimitives.ReadUInt32LittleEndian(value[0x38..]);
        int nameChars = value[0x40];
        if (value.Length < 0x42 + nameChars * 2) return;

        if ((fileFlags & FileNameFlagDirectory) != 0)
        {
            if (owner == RootRecord) return;
            var name = MemoryMarshal.Cast<byte, char>(value.Slice(0x42, nameChars * 2));
            mft.AddDirectory(owner, parent, new string(name));
        }
        else
        {
            mft.FileLinks.Add(new FileLink(owner, parent));
        }
    }

    /// <summary>
    /// Undoes the NTFS "update sequence" protection: the last two bytes of every 512-byte stride were
    /// replaced by a check value on disk; the originals are stored in the record header.
    /// </summary>
    internal static bool ApplyFixups(Span<byte> record)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x454C4946) return false; // "FILE"
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[0x06..]);
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length || (usaCount - 1) * 512 > record.Length) return false;

        ushort check = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * 512 - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[pos..]) != check) return false; // torn write
            record[pos] = record[usaOffset + i * 2];
            record[pos + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    /// <summary>
    /// Finds where the MFT itself is stored. Record 0 describes the start; on large or fragmented volumes
    /// the rest of its run list lives in extension records, listed in record 0's $ATTRIBUTE_LIST.
    /// </summary>
    private static List<Extent> ReadMftExtents(SafeFileHandle volume, NtfsVolumeData info, int recordSize)
    {
        long clusterSize = info.BytesPerCluster;
        int readLength = (int)Math.Max(clusterSize, recordSize);
        var buffer = new byte[readLength];
        ReadAt(volume, info.MftStartLcn * clusterSize, buffer, readLength);
        var record0 = buffer.AsSpan(0, recordSize);
        if (!ApplyFixups(record0))
            throw new InvalidDataException("The first MFT record is invalid.");

        var pieces = new List<(long LowestVcn, List<Extent> Extents)>();
        CollectDataPieces(record0, pieces);

        var extensionRecords = new SortedSet<int>();
        foreach (var (type, offset, length) in Attributes(record0))
        {
            if (type != AttrAttributeList) continue;
            var list = ReadAttributeValue(volume, record0.Slice(offset, length), clusterSize);
            for (int pos = 0; pos + 0x1A <= list.Length;)
            {
                uint entryType = BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(pos));
                int entryLength = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(pos + 4));
                if (entryLength == 0) break;
                int entryRecord = (int)(BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(pos + 0x10)) & 0xFFFFFFFFFFFF);
                if (entryType == AttrData && list[pos + 6] == 0 && entryRecord != 0)
                    extensionRecords.Add(entryRecord);
                pos += entryLength;
            }
        }

        // Extension records sit early in the MFT, which the pieces from record 0 already cover.
        foreach (int r in extensionRecords)
        {
            var record = ReadRecord(volume, Concatenate(pieces, allowGaps: true), clusterSize, recordSize, r)
                ?? throw new InvalidDataException($"MFT extension record {r} cannot be read.");
            CollectDataPieces(record, pieces);
        }

        var extents = Concatenate(pieces, allowGaps: false);
        if (extents.Sum(e => e.ClusterCount) * clusterSize < info.MftValidDataLength)
            throw new NotSupportedException("The location of the MFT could not be fully determined.");
        return extents;
    }

    /// <summary>Adds every unnamed, non-resident $DATA attribute in <paramref name="record"/> as a (starting VCN, extents) piece.</summary>
    private static void CollectDataPieces(ReadOnlySpan<byte> record, List<(long LowestVcn, List<Extent> Extents)> pieces)
    {
        foreach (var (type, offset, length) in Attributes(record))
        {
            var attr = record.Slice(offset, length);
            if (type != AttrData || attr.Length < NonResidentHeaderSize || attr[0x08] == 0 || attr[0x09] != 0) continue;
            long lowestVcn = BinaryPrimitives.ReadInt64LittleEndian(attr[0x10..]);
            if (pieces.Any(p => p.LowestVcn == lowestVcn)) continue;
            int runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x20..]);
            if (runsOffset >= attr.Length) continue;
            pieces.Add((lowestVcn, ParseDataRuns(attr[runsOffset..])));
        }
    }

    private static List<Extent> Concatenate(List<(long LowestVcn, List<Extent> Extents)> pieces, bool allowGaps)
    {
        var result = new List<Extent>();
        long vcn = 0;
        foreach (var piece in pieces.OrderBy(p => p.LowestVcn))
        {
            if (piece.LowestVcn != vcn)
            {
                if (allowGaps) break; // only the contiguous start is usable yet
                throw new NotSupportedException("The MFT fragments do not line up.");
            }
            result.AddRange(piece.Extents);
            vcn += piece.Extents.Sum(e => e.ClusterCount);
        }
        return result;
    }

    /// <summary>The value of an attribute, read from disk if it is non-resident.</summary>
    private static byte[] ReadAttributeValue(SafeFileHandle volume, ReadOnlySpan<byte> attr, long clusterSize)
    {
        if (attr.Length >= ResidentHeaderSize && attr[0x08] == 0)
        {
            long valueLength = BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]);
            int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
            if (valueOffset + valueLength > attr.Length)
                throw new InvalidDataException("An MFT attribute is damaged.");
            return attr.Slice(valueOffset, (int)valueLength).ToArray();
        }

        if (attr.Length < NonResidentHeaderSize)
            throw new InvalidDataException("An MFT attribute is damaged.");
        long dataSize = BinaryPrimitives.ReadInt64LittleEndian(attr[0x30..]);
        int runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x20..]);
        // Only used for the attribute list of the MFT itself, which is a few kilobytes at most.
        if (dataSize is < 0 or > 16 * 1024 * 1024 || runsOffset >= attr.Length)
            throw new InvalidDataException("An MFT attribute is damaged.");
        var result = new byte[dataSize];
        int written = 0;
        foreach (var extent in ParseDataRuns(attr[runsOffset..]))
        {
            var buffer = new byte[extent.ClusterCount * clusterSize];
            ReadAt(volume, extent.Lcn * clusterSize, buffer, buffer.Length);
            int n = (int)Math.Min(buffer.Length, dataSize - written);
            buffer.AsSpan(0, n).CopyTo(result.AsSpan(written));
            written += n;
            if (written >= dataSize) break;
        }
        return result;
    }

    /// <summary>The attributes in a record as (type, offset, length), stopping at the end marker.</summary>
    private static List<(uint Type, int Offset, int Length)> Attributes(ReadOnlySpan<byte> record)
    {
        var result = new List<(uint, int, int)>();
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        int bytesInUse = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
        while (offset + 16 <= bytesInUse)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == AttrEnd) break;
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length <= 0 || offset + length > bytesInUse) break;
            result.Add((type, offset, length));
            offset += length;
        }
        return result;
    }

    /// <summary>Reads MFT record <paramref name="record"/> from disk with fixups applied, or null if it is invalid or not mapped.</summary>
    private static byte[]? ReadRecord(SafeFileHandle volume, List<Extent> extents, long clusterSize, int recordSize, int record)
    {
        long byteInMft = (long)record * recordSize;
        long cluster = byteInMft / clusterSize;
        foreach (var extent in extents)
        {
            if (cluster >= extent.ClusterCount)
            {
                cluster -= extent.ClusterCount;
                continue;
            }
            int within = (int)(byteInMft % clusterSize);
            int readLength = (int)((within + recordSize + clusterSize - 1) / clusterSize * clusterSize);
            var buffer = new byte[readLength];
            ReadAt(volume, (extent.Lcn + cluster) * clusterSize, buffer, readLength);
            var result = buffer.AsSpan(within, recordSize).ToArray();
            return ApplyFixups(result) ? result : null;
        }
        return null;
    }

    /// <summary>
    /// Decodes an NTFS run list into absolute extents. Each run is a header byte (low nibble: size of the length,
    /// high nibble: size of the offset), the cluster count, and the signed offset relative to the previous run.
    /// Stops at the end marker or at a run that does not fit.
    /// </summary>
    internal static List<Extent> ParseDataRuns(ReadOnlySpan<byte> runs)
    {
        var result = new List<Extent>();
        long lcn = 0;
        int i = 0;
        while (i < runs.Length && runs[i] != 0)
        {
            int lengthBytes = runs[i] & 0x0F;
            int offsetBytes = runs[i] >> 4;
            i++;
            if (lengthBytes > 8 || offsetBytes > 8 || i + lengthBytes + offsetBytes > runs.Length)
                break; // damaged run list
            long count = 0;
            for (int b = 0; b < lengthBytes; b++) count |= (long)runs[i + b] << (8 * b);
            i += lengthBytes;

            if (offsetBytes == 0)
                continue; // sparse run: no clusters on disk (does not occur in the MFT itself)
            long delta = 0;
            for (int b = 0; b < offsetBytes; b++) delta |= (long)runs[i + b] << (8 * b);
            if ((runs[i + offsetBytes - 1] & 0x80) != 0) // sign-extend the relative offset
                delta |= -1L << (8 * offsetBytes);
            i += offsetBytes;

            lcn += delta;
            result.Add(new Extent(lcn, count));
        }
        return result;
    }

    /// <summary>Reads one record again (for the top-files list) and returns its full path, or null if it is not in the scanned tree.</summary>
    private static string? ReadFilePath(SafeFileHandle volume, List<Extent> extents, long clusterSize, int recordSize,
        int record, TreeBuilder builder)
    {
        var data = ReadRecord(volume, extents, clusterSize, recordSize, record);
        if (data is null) return null;
        // Names may also sit in extension records; those files are rare enough to just leave out of the list.
        foreach (var (type, offset, length) in Attributes(data))
        {
            var attr = data.AsSpan(offset, length);
            if (type != AttrFileName || attr.Length < ResidentHeaderSize || attr[0x08] != 0) continue;
            int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
            if (valueOffset > attr.Length) continue;
            var value = attr[valueOffset..];
            if (value.Length < 0x42 || value[0x41] == NamespaceDos || value.Length < 0x42 + value[0x40] * 2) continue;
            int parent = (int)(BinaryPrimitives.ReadUInt64LittleEndian(value) & 0xFFFFFFFFFFFF);
            var dir = builder.Resolve(parent);
            if (dir is not null)
                return Path.Combine(dir.FullPath, Encoding.Unicode.GetString(value.Slice(0x42, value[0x40] * 2)));
        }
        return null;
    }

    // ---- Building the directory tree -----------------------------------------------------------

    /// <summary>Creates <see cref="DirNode"/>s on demand for directories inside the scanned root.</summary>
    private sealed class TreeBuilder
    {
        private readonly MftData _mft;
        private readonly int _rootRecord;
        private readonly Dictionary<int, DirNode?> _nodes = [];

        public TreeBuilder(MftData mft, int rootRecord, string rootPath)
        {
            _mft = mft;
            _rootRecord = rootRecord;
            Root = new DirNode(rootPath, null);
            _nodes[rootRecord] = Root;
        }

        public DirNode Root { get; }
        public long DirectoryCount => _nodes.Count(n => n.Value is not null);

        /// <summary>The node for directory <paramref name="record"/>, or null if it lies outside the root (or is NTFS metadata).</summary>
        public DirNode? Resolve(int record)
        {
            if (_nodes.TryGetValue(record, out var known)) return known;

            // Walk up until we reach something we already know, then create nodes on the way back down.
            var chain = new Stack<int>();
            int current = record;
            DirNode? ancestor = null;
            while (true)
            {
                if (_nodes.TryGetValue(current, out ancestor)) break;
                if (current == RootRecord || !_mft.Directories.TryGetValue(current, out var dir) || chain.Count > 4096)
                {
                    ancestor = null; // outside the scanned root, or metadata ($Extend and friends)
                    break;
                }
                chain.Push(current);
                current = dir.Parent;
            }

            while (chain.Count > 0)
            {
                int r = chain.Pop();
                DirNode? node = null;
                if (ancestor is not null)
                {
                    node = new DirNode(Path.Combine(ancestor.FullPath, _mft.Directories[r].Name), ancestor);
                    ancestor.Children.Add(node);
                }
                _nodes[r] = node;
                ancestor = node;
            }
            _nodes.TryAdd(record, null); // excluded straight away (metadata such as $Extend), remember that too
            return _nodes[record];
        }
    }

    // ---- Data collected from the MFT ----------------------------------------------------------

    internal readonly record struct Extent(long Lcn, long ClusterCount);

    internal readonly record struct FileLink(int Record, int Parent);

    internal readonly record struct DirEntry(int Parent, string Name);

    internal sealed class MftData(int recordCount)
    {
        public readonly long[] Size = new long[recordCount];
        public readonly long[] Allocated = new long[recordCount];
        public readonly List<FileLink> FileLinks = new(recordCount);
        public readonly Dictionary<int, DirEntry> Directories = [];

        public int DirectoryCount => Directories.Count;

        public void AddDirectory(int record, int parent, string name) =>
            Directories.TryAdd(record, new DirEntry(parent, name)); // directories have one name; keep the first
    }

    // ---- Win32 ---------------------------------------------------------------------------------

    private static SafeFileHandle OpenVolume(string volumeRoot)
    {
        var handle = CreateFileW($@"\\.\{volumeRoot.TrimEnd('\\')}", GenericRead, FileShareReadWrite, IntPtr.Zero,
            OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new UnauthorizedAccessException($"Cannot open volume {volumeRoot} (error {Marshal.GetLastPInvokeError()}).");
        return handle;
    }

    private static NtfsVolumeData GetVolumeData(SafeFileHandle volume)
    {
        if (!DeviceIoControl(volume, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, out NtfsVolumeData data,
                Marshal.SizeOf<NtfsVolumeData>(), out _, IntPtr.Zero))
            throw new IOException($"Cannot read the NTFS volume data (error {Marshal.GetLastPInvokeError()}).");
        return data;
    }

    private static int GetRecordNumber(string directory)
    {
        using var handle = CreateFileW(directory, 0, FileShareReadWrite | 0x4 /* delete */, IntPtr.Zero, OpenExisting,
            0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS, needed for directories */, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            throw new IOException($"Cannot open {directory} (error {Marshal.GetLastPInvokeError()}).");
        return (int)((((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) & 0xFFFFFFFFFFFF);
    }

    private static int ReadAt(SafeFileHandle volume, long offset, byte[] buffer, int length)
    {
        var overlapped = new NativeOverlapped { OffsetLow = (int)offset, OffsetHigh = (int)(offset >> 32) };
        if (!ReadFile(volume, buffer, length, out int read, ref overlapped))
            throw new IOException($"Reading the volume failed at offset {offset} (error {Marshal.GetLastPInvokeError()}).");
        return read;
    }

    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint FsctlGetNtfsVolumeData = 0x00090064;

    [StructLayout(LayoutKind.Sequential)]
    private struct NtfsVolumeData
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] // FILETIMEs are two DWORDs, so no 8-byte alignment
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize,
        out NtfsVolumeData outBuffer, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle file, [Out] byte[] buffer, int toRead, out int read,
        ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
}

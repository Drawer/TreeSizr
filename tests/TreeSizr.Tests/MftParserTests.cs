using System.Buffers.Binary;
using System.Text;
using TreeSizr.Scanning;

namespace TreeSizr.Tests;

public class MftParserTests
{
    private const int RecordSize = 1024;

    [Fact]
    public void ParseDataRuns_DecodesRelativeOffsets()
    {
        // Run 1: 0x18 clusters at LCN 0x5634. Run 2: 0x10 clusters, 0x20 clusters further. Run 3: back by 0x100.
        byte[] runs = [0x21, 0x18, 0x34, 0x56, 0x11, 0x10, 0x20, 0x21, 0x08, 0x00, 0xFF, 0x00];

        var extents = MftScanner.ParseDataRuns(runs);

        Assert.Equal(new MftScanner.Extent[] { new(0x5634, 0x18), new(0x5654, 0x10), new(0x5554, 0x08) }, extents);
    }

    [Fact]
    public void ParseDataRuns_SkipsSparseRuns()
    {
        byte[] runs = [0x21, 0x04, 0x00, 0x01, 0x01, 0x08, 0x11, 0x02, 0x10, 0x00];

        var extents = MftScanner.ParseDataRuns(runs);

        Assert.Equal(new MftScanner.Extent[] { new(0x100, 4), new(0x110, 2) }, extents);
    }

    [Fact]
    public void ParseDataRuns_StopsAtTruncatedRun()
    {
        byte[] runs = [0x21, 0x18, 0x34, 0x56, 0x44, 0x01, 0x02]; // second run claims 8 bytes, only 2 left

        var extents = MftScanner.ParseDataRuns(runs);

        Assert.Equal(new MftScanner.Extent[] { new(0x5634, 0x18) }, extents);
    }

    [Fact]
    public void ApplyFixups_RestoresSectorEnds()
    {
        var record = new RecordBuilder().Build();
        Assert.Equal(0xBEEF, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510))); // check value on disk

        Assert.True(MftScanner.ApplyFixups(record));

        Assert.Equal(0x1111, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)));
        Assert.Equal(0x2222, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022)));
    }

    [Fact]
    public void ApplyFixups_RejectsTornWrite()
    {
        var record = new RecordBuilder().Build();
        record[1022] ^= 0xFF; // second sector was not fully written

        Assert.False(MftScanner.ApplyFixups(record));
    }

    [Fact]
    public void ParseRecord_ReadsFileNameParentAndSize()
    {
        var record = new RecordBuilder().FileName(parent: 42, "report.pdf", directory: false).ResidentData(123).Build();
        var mft = new MftScanner.MftData(100);

        MftScanner.ParseRecord(record, 77, mft);

        Assert.Equal(new[] { new MftScanner.FileLink(77, 42) }, mft.FileLinks);
        Assert.Equal(123, mft.Size[77]);
        Assert.Equal(0, mft.Allocated[77]); // resident data takes no clusters
    }

    [Fact]
    public void ParseRecord_ReadsDirectory()
    {
        var record = new RecordBuilder().FileName(parent: 5, "Users", directory: true).Build();
        var mft = new MftScanner.MftData(100);

        MftScanner.ParseRecord(record, 30, mft);

        Assert.Empty(mft.FileLinks);
        Assert.Equal(new MftScanner.DirEntry(5, "Users"), mft.Directories[30]);
    }

    [Fact]
    public void ParseRecord_SkipsDosAliases()
    {
        var record = new RecordBuilder().FileName(parent: 42, "REPORT~1.PDF", directory: false, nameSpace: 2).Build();
        var mft = new MftScanner.MftData(100);

        MftScanner.ParseRecord(record, 77, mft);

        Assert.Empty(mft.FileLinks);
    }

    [Fact]
    public void ParseRecord_IgnoresRecordsNotInUse()
    {
        var record = new RecordBuilder { InUse = false }.FileName(parent: 42, "old.txt", directory: false).Build();
        var mft = new MftScanner.MftData(100);

        MftScanner.ParseRecord(record, 77, mft);

        Assert.Empty(mft.FileLinks);
    }

    [Fact]
    public void ParseRecord_SurvivesDamagedRecords()
    {
        // Valid header and fixups, random attribute bytes: must never throw, whatever the content.
        var random = new Random(1234);
        var mft = new MftScanner.MftData(100);
        for (int i = 0; i < 20_000; i++)
        {
            var record = new RecordBuilder().Build();
            random.NextBytes(record.AsSpan(0x38, RecordSize - 0x38 - 2));
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x18), RecordSize); // parse the whole record
            if (i % 2 == 0) // also try plausible attribute types and lengths
            {
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x38), random.Next(2) == 0 ? 0x30u : 0x80u);
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x3C), (uint)random.Next(0, 0x100));
            }
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510), 0xBEEF);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(1022), 0xBEEF);

            MftScanner.ParseRecord(record, 50, mft);
        }
    }

    /// <summary>Builds a 1024-byte MFT FILE record as it is stored on disk, update sequence applied.</summary>
    private sealed class RecordBuilder
    {
        private const int FirstAttribute = 0x38;
        private readonly byte[] _record = new byte[RecordSize];
        private int _offset = FirstAttribute;

        public bool InUse { get; init; } = true;

        public RecordBuilder FileName(int parent, string name, bool directory, byte nameSpace = 1)
        {
            int valueLength = 0x42 + name.Length * 2;
            var value = new byte[valueLength];
            BinaryPrimitives.WriteUInt64LittleEndian(value, (uint)parent | (1UL << 48)); // sequence number in the top bits
            BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x38), directory ? 0x10000000u : 0x20u);
            value[0x40] = (byte)name.Length;
            value[0x41] = nameSpace;
            Encoding.Unicode.GetBytes(name).CopyTo(value, 0x42);
            return ResidentAttribute(0x30, value);
        }

        public RecordBuilder ResidentData(int size) => ResidentAttribute(0x80, new byte[size]);

        private RecordBuilder ResidentAttribute(uint type, byte[] value)
        {
            int length = (0x18 + value.Length + 7) / 8 * 8;
            var attr = _record.AsSpan(_offset, length);
            BinaryPrimitives.WriteUInt32LittleEndian(attr, type);
            BinaryPrimitives.WriteUInt32LittleEndian(attr[4..], (uint)length);
            attr[8] = 0; // resident
            BinaryPrimitives.WriteUInt32LittleEndian(attr[0x10..], (uint)value.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(attr[0x14..], 0x18);
            value.CopyTo(attr[0x18..]);
            _offset += length;
            return this;
        }

        public byte[] Build()
        {
            var r = _record.AsSpan();
            Encoding.ASCII.GetBytes("FILE").CopyTo(r);
            BinaryPrimitives.WriteUInt16LittleEndian(r[0x04..], 0x30); // update sequence array offset
            BinaryPrimitives.WriteUInt16LittleEndian(r[0x06..], 3);    // check value + one entry per 512-byte sector
            BinaryPrimitives.WriteUInt16LittleEndian(r[0x14..], FirstAttribute);
            BinaryPrimitives.WriteUInt16LittleEndian(r[0x16..], (ushort)(InUse ? 1 : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(r[_offset..], 0xFFFFFFFF); // end marker
            BinaryPrimitives.WriteUInt32LittleEndian(r[0x18..], (uint)(_offset + 8)); // bytes in use

            // Update sequence: the real sector ends go into the array, the check value onto the disk.
            BinaryPrimitives.WriteUInt16LittleEndian(r[0x30..], 0xBEEF);
            BinaryPrimitives.WriteUInt16LittleEndian(r[0x32..], 0x1111);
            BinaryPrimitives.WriteUInt16LittleEndian(r[0x34..], 0x2222);
            BinaryPrimitives.WriteUInt16LittleEndian(r[510..], 0xBEEF);
            BinaryPrimitives.WriteUInt16LittleEndian(r[1022..], 0xBEEF);
            return _record;
        }
    }
}

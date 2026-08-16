using System.Buffers.Binary;
using System.Text;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M494: reading Mimir's .hashdb / .lhdb hash tables.
///
/// <para>These build real files rather than lean on a downloaded fixture, including a genuine zstd
/// <b>seekable</b> arena — multiple independent frames plus the trailing seek table — so the path that
/// actually serves every lookup on a shipped table is the path under test. A test that only covered the raw
/// arena would pass while the format we ship against stayed unexercised.</para>
///
/// <para>The layout asserted here was measured against Mimir's shipped bintypes-2026-08-14.lhdb and then
/// confirmed end to end: reading that file with this reader reproduced all 3,711 names, and 20,000 entries
/// of the 2.29M-entry game table matched CommunityDragon's own text list exactly.</para>
/// </summary>
public sealed class HashDbFileTests
{
    /// <summary>Compress each chunk as its own zstd frame and append a seek table — the zstd Seekable
    /// Format, which is what Mimir writes.</summary>
    private static byte[] SeekableArena(IReadOnlyList<byte[]> chunks)
    {
        using var stream = new MemoryStream();
        var entries = new List<(int Compressed, int Decompressed)>();
        foreach (var chunk in chunks)
        {
            using var frame = new MemoryStream();
            using (var compressor = new ZstdSharp.CompressionStream(frame)) compressor.Write(chunk, 0, chunk.Length);
            var bytes = frame.ToArray();
            stream.Write(bytes, 0, bytes.Length);
            entries.Add((bytes.Length, chunk.Length));
        }

        // Skippable frame: [magic 0x184D2A5E][size][entries][footer]
        int tableBytes = entries.Count * 8 + 9;
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(head, 0x184D2A5E);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)tableBytes);
        stream.Write(head);

        Span<byte> entry = stackalloc byte[8];
        foreach (var (c, d) in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)c);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)d);
            stream.Write(entry);
        }

        Span<byte> footer = stackalloc byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(footer, (uint)entries.Count);
        footer[4] = 0;                                              // no per-frame checksums
        BinaryPrimitives.WriteUInt32LittleEndian(footer[5..], 0x8F92EAB1);
        stream.Write(footer);
        return stream.ToArray();
    }

    /// <summary>Write a .hashdb. Entries are sorted by key here, because the reader binary-searches and a
    /// generator that emitted them unsorted would be the bug, not the reader.</summary>
    private static string WriteDb(string path, IEnumerable<(ulong Key, string Value)> input,
        int keyWidth = 8, bool compressArena = true, int framesWanted = 3)
    {
        var entries = input.OrderBy(e => e.Key).ToList();

        // Arena: every string back to back, offsets recorded as we go.
        var arena = new MemoryStream();
        var offsets = new List<long>();
        var lengths = new List<int>();
        foreach (var (_, value) in entries)
        {
            var utf8 = Encoding.UTF8.GetBytes(value);
            offsets.Add(arena.Length);
            lengths.Add(utf8.Length);
            arena.Write(utf8, 0, utf8.Length);
        }
        var arenaBytes = arena.ToArray();

        byte[] stored;
        if (compressArena)
        {
            // Split into several frames so the multi-frame seek path is genuinely exercised.
            var chunks = new List<byte[]>();
            int per = Math.Max(1, arenaBytes.Length / Math.Max(1, framesWanted));
            for (int at = 0; at < arenaBytes.Length; at += per)
                chunks.Add(arenaBytes[at..Math.Min(arenaBytes.Length, at + per)]);
            if (chunks.Count == 0) chunks.Add(Array.Empty<byte>());
            stored = SeekableArena(chunks);
        }
        else stored = arenaBytes;

        const int offsetWidth = 4;
        long keysOffset = HashDbFile.HeaderSize;
        long offsetsOffset = keysOffset + (long)entries.Count * keyWidth;
        long lengthsOffset = offsetsOffset + (long)entries.Count * offsetWidth;
        long arenaOffset = lengthsOffset + (long)entries.Count * 2;

        using var file = new MemoryStream();
        var header = new byte[HashDbFile.HeaderSize];
        Encoding.ASCII.GetBytes("HASHDB\0\0").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), HashDbFile.SupportedVersion);
        header[10] = 1;                                             // hash kind
        header[11] = (byte)((compressArena ? 0x01 : 0x00) | 0x02);  // compressed | case-insensitive
        header[12] = (byte)keyWidth;
        header[13] = offsetWidth;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)entries.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), (ulong)keysOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), (ulong)offsetsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40), (ulong)arenaOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48), (ulong)arenaBytes.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(56), (ulong)stored.Length);
        file.Write(header);

        foreach (var (key, _) in entries)
        {
            Span<byte> buffer = stackalloc byte[8];
            if (keyWidth == 4) BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)key);
            else BinaryPrimitives.WriteUInt64LittleEndian(buffer, key);
            file.Write(buffer[..keyWidth]);
        }
        foreach (long offset in offsets)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)offset);
            file.Write(buffer);
        }
        foreach (int length in lengths)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)length);
            file.Write(buffer);
        }
        file.Write(stored);

        File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"reyengine_hashdb_{Guid.NewGuid():N}.hashdb");

    private static IEnumerable<(ulong, string)> Sample(int count) =>
        Enumerable.Range(0, count).Select(i =>
            ((ulong)(i * 2654435761u % 1000000007u), $"assets/characters/unit{i}/skins/base/unit{i}_tx_cm.dds"));

    [Fact]
    public void ReadsHeaderAndEveryEntryFromASeekableCompressedArena()
    {
        string path = TempPath();
        try
        {
            var wanted = Sample(500).ToDictionary(e => e.Item1, e => e.Item2);
            WriteDb(path, wanted.Select(kv => (kv.Key, kv.Value)));

            using var db = HashDbFile.Open(path);
            Assert.Equal(wanted.Count, db.EntryCount);
            Assert.Equal(8, db.KeyWidth);
            Assert.True(db.ArenaCompressed);
            Assert.True(db.CaseInsensitive);
            Assert.True(db.KeysAreSorted());

            foreach (var (key, value) in wanted)
            {
                Assert.True(db.TryGet(key, out var got), $"key {key:x} not found");
                Assert.Equal(value, got);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingKeyMissesInsteadOfReturningANeighbour()
    {
        // Binary search that returns the insertion point instead of "not found" is the classic failure
        // here, and it is silent: every unknown hash would resolve to some unrelated real path.
        string path = TempPath();
        try
        {
            WriteDb(path, new[] { (10ul, "ten"), (20ul, "twenty"), (30ul, "thirty") });
            using var db = HashDbFile.Open(path);

            Assert.True(db.TryGet(20, out var hit));
            Assert.Equal("twenty", hit);
            foreach (ulong absent in new ulong[] { 0, 9, 11, 25, 31, ulong.MaxValue })
                Assert.False(db.TryGet(absent, out _), $"{absent} should not resolve");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RawArenaIsReadWithoutDecompression()
    {
        string path = TempPath();
        try
        {
            WriteDb(path, Sample(50), compressArena: false);
            using var db = HashDbFile.Open(path);

            Assert.False(db.ArenaCompressed);
            foreach (var (key, value) in Sample(50))
            {
                Assert.True(db.TryGet(key, out var got));
                Assert.Equal(value, got);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FourByteKeyTablesUseTheNarrowKeyArray()
    {
        // bin tables (binentries, binfields, bintypes) ship key_width 4; game/lcu ship 8. Reading a 4-wide
        // table with an 8-wide stride would walk straight off the end of the keys array.
        string path = TempPath();
        try
        {
            var wanted = Enumerable.Range(1, 200).Select(i => ((ulong)(i * 7919), $"Type{i}")).ToList();
            WriteDb(path, wanted, keyWidth: 4);

            using var db = HashDbFile.Open(path);
            Assert.Equal(4, db.KeyWidth);
            foreach (var (key, value) in wanted)
            {
                Assert.True(db.TryGet(key, out var got));
                Assert.Equal(value, got);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StringSpanningTwoFramesIsStitchedBackTogether()
    {
        // A string that straddles a frame boundary is the case a single-frame fast path gets wrong, and it
        // yields a truncated or garbled path rather than an exception.
        string path = TempPath();
        try
        {
            var wanted = Enumerable.Range(0, 40)
                .Select(i => ((ulong)i, new string((char)('a' + i % 26), 900) + i))
                .ToList();
            WriteDb(path, wanted, framesWanted: 7);

            using var db = HashDbFile.Open(path);
            foreach (var (key, value) in wanted)
            {
                Assert.True(db.TryGet(key, out var got));
                Assert.Equal(value, got);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsForeignOrTruncatedFiles()
    {
        string path = TempPath();
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("PROP").Concat(new byte[200]).ToArray());
            Assert.Throws<InvalidDataException>(() => HashDbFile.Open(path));

            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("HASHDB\0\0"));
            Assert.Throws<InvalidDataException>(() => HashDbFile.Open(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EntriesEnumeratesInKeyOrder()
    {
        string path = TempPath();
        try
        {
            WriteDb(path, Sample(120));
            using var db = HashDbFile.Open(path);

            var seen = db.Entries().ToList();
            Assert.Equal(120, seen.Count);
            for (int i = 1; i < seen.Count; i++)
                Assert.True(seen[i - 1].Key < seen[i].Key, "entries must come out ascending");
        }
        finally { File.Delete(path); }
    }
}

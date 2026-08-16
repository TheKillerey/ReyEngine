using System.Buffers.Binary;
using System.Text;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// Writes .hashdb files for tests, including a genuine zstd <b>seekable</b> arena.
///
/// <para>ReyEngine ships no hashdb writer on purpose (M494: an unexercised writer is a liability), but the
/// tests need real files — leaning on a downloaded fixture would leave the format untested on any machine
/// that had not fetched one, and would not let a test construct the awkward cases deliberately.</para>
///
/// <para>The layout written here is the one measured off Mimir's shipped bintypes-2026-08-14.lhdb: 80-byte
/// header, sorted keys, offsets, then lengths at a fixed 2 bytes each regardless of offset width.</para>
/// </summary>
internal static class HashDbTestWriter
{
    /// <summary>Compress each chunk as its own zstd frame and append a seek table — the zstd Seekable
    /// Format, which is what Mimir writes.</summary>
    public static byte[] SeekableArena(IReadOnlyList<byte[]> chunks)
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
    public static string Write(string path, IEnumerable<(ulong Key, string Value)> input,
        int keyWidth = 8, bool compressArena = true, int framesWanted = 3)
    {
        var entries = input.OrderBy(e => e.Key).ToList();

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
}

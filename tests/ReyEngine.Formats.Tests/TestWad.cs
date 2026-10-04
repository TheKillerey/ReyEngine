using System.Buffers.Binary;
using System.IO.Compression;
using ZstdSharp;

namespace ReyEngine.Formats.Tests;

/// <summary>One chunk of an archive written by hand: its hash, how it is stored, the stored bytes, and the size it decodes to.</summary>
internal sealed record TestChunk(ulong Hash, byte Compression, byte[] Stored, uint Uncompressed, byte Frames = 0, uint StartFrame = 0)
{
    public const byte None = 0, GZip = 1, Satellite = 2, Zstd = 3, ZstdMulti = 4;

    public static TestChunk Stored_(ulong hash, byte[] data) => new(hash, None, data, (uint)data.Length);

    public static TestChunk Compressed(ulong hash, byte[] data) => new(hash, Zstd, ZstdBytes(data), (uint)data.Length);

    public static TestChunk GZipped(ulong hash, byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(data);
        return new TestChunk(hash, GZip, ms.ToArray(), (uint)data.Length);
    }

    /// <summary>A chunk of Zstd frames laid end to end, the way a texture is stored: its first frame starts at byte 0.</summary>
    public static TestChunk Multi(ulong hash, params byte[][] parts) =>
        new(hash, ZstdMulti, parts.SelectMany(ZstdBytes).ToArray(), (uint)parts.Sum(p => p.Length), (byte)parts.Length);

    public static TestChunk SatelliteOf(ulong hash, uint size) => new(hash, Satellite, Array.Empty<byte>(), size);

    public static byte[] ZstdBytes(byte[] data)
    {
        using var compressor = new Compressor(3);
        return compressor.Wrap(data).ToArray();
    }
}

/// <summary>Writes archives by hand, so that every way a chunk can be stored, and every way an archive can be wrong, is a few lines of a test.</summary>
internal static class TestWad
{
    /// <summary>A version <c>major.minor</c> archive of <paramref name="chunks"/>, with the table in the order given (a hash may be listed twice).</summary>
    public static void Write(string path, IReadOnlyList<TestChunk> chunks, byte major = 3, byte minor = 4)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, Build(chunks, major, minor));
    }

    public static byte[] Build(IReadOnlyList<TestChunk> chunks, byte major = 3, byte minor = 4)
    {
        int tocStart = major == 3 ? 272 : major == 2 ? 104 : 12;
        long data = tocStart + chunks.Count * 32L;
        using var ms = new MemoryStream();
        var header = new byte[tocStart];
        header[0] = (byte)'R';
        header[1] = (byte)'W';
        header[2] = major;
        header[3] = minor;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(tocStart - 4), chunks.Count);
        ms.Write(header);

        var offsets = new List<long>();
        foreach (var chunk in chunks)
        {
            offsets.Add(data);
            data += chunk.Stored.Length;
        }
        var row = new byte[32];
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            Array.Clear(row);
            BinaryPrimitives.WriteUInt64LittleEndian(row, c.Hash);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(12), (uint)c.Stored.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(16), c.Uncompressed);
            row[20] = (byte)(c.Frames << 4 | c.Compression);
            if (major == 3 && minor <= 3) BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(22), (ushort)c.StartFrame);
            else
            {
                row[21] = (byte)(c.StartFrame >> 16);
                row[22] = (byte)c.StartFrame;
                row[23] = (byte)(c.StartFrame >> 8);
            }
            ms.Write(row);
        }
        foreach (var chunk in chunks) ms.Write(chunk.Stored);
        return ms.ToArray();
    }

    /// <summary>A bin of one object whose strings are random text, so that Zstd cannot make much of it: its first block is large.</summary>
    public static byte[] BigBin(string objectPath, int kilobytes, int seed = 1)
    {
        var random = new Random(seed);
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var tags = new List<string>();
        for (int i = 0; i < kilobytes; i++)
            tags.Add(new string(Enumerable.Range(0, 1000).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
        return OverlayKit.Bin(OverlayKit.Obj(objectPath, tags.ToArray()));
    }
}

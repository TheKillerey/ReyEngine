using System.Buffers.Binary;
using ReyEngine.Core.Build;
using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M589: two paths holding the same bytes must share one data region.
///
/// <para>The packer stored one blob per entry, which on a real 3,600-file map mod produced <b>686 MB</b>
/// against <b>419 MB</b> for the author's own build of identical content — the entire difference was this.
/// Riot's shipped Map11.wad shares a region between 11,551 of its 28,858 entries.</para>
///
/// <para><c>isDuplicated</c> stays false because that is Riot's v3.4 convention, measured rather than
/// assumed: of those 11,551 shared entries <b>zero</b> carry the flag. (A v3.3 wad built by an older tool
/// flags all 1,191 of its own — the conventions disagree, and this writes v3.4.)</para>
/// </summary>
public sealed class WadPackDedupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rey-packdedup-" + Guid.NewGuid().ToString("n")[..8]);

    public WadPackDedupTests() => Directory.CreateDirectory(Path.Combine(_dir, "src"));
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Src => Path.Combine(_dir, "src");

    private void Write(string rel, byte[] bytes)
    {
        string p = Path.Combine(Src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    /// <summary>Compressible content, so the packer actually stores it Zstd rather than raw.</summary>
    private static byte[] Blob(byte seed, int size = 40_000)
    {
        var b = new byte[size];
        for (int i = 0; i < size; i++) b[i] = (byte)((i / 97) + seed);
        return b;
    }

    private (int Count, int DistinctOffsets, int Flagged) ReadToc(string wad)
    {
        using var fs = File.OpenRead(wad);
        var head = new byte[272];
        fs.ReadExactly(head);
        int count = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(268));
        var toc = new byte[count * 32];
        fs.ReadExactly(toc);

        var offsets = new HashSet<uint>();
        int flagged = 0;
        for (int i = 0; i < count; i++)
        {
            var e = toc.AsSpan(i * 32, 32);
            offsets.Add(BinaryPrimitives.ReadUInt32LittleEndian(e[8..]));
            if (e[21] != 0) flagged++;
        }
        return (count, offsets.Count, flagged);
    }

    [Fact]
    public void IdenticalFilesShareOneDataRegion()
    {
        var same = Blob(1);
        Write("data/a.bin", same);
        Write("data/b.bin", same);
        Write("data/c.bin", Blob(2));

        string wad = Path.Combine(_dir, "out.wad.client");
        var report = WadPackService.Pack(Src, wad);
        Assert.True(report.Success, string.Join("; ", report.Warnings));

        var (count, distinct, _) = ReadToc(wad);
        Assert.Equal(3, count);
        Assert.Equal(2, distinct);              // a and b share
        Assert.Equal(1, report.SharedChunks);
    }

    [Fact]
    public void TheSharedEntriesAreNotFlaggedAsDuplicated()
    {
        var same = Blob(3);
        Write("data/a.bin", same);
        Write("data/b.bin", same);

        string wad = Path.Combine(_dir, "out.wad.client");
        WadPackService.Pack(Src, wad);

        // 0 of Riot's 11,551 shared v3.4 entries set the flag.
        Assert.Equal(0, ReadToc(wad).Flagged);
    }

    [Fact]
    public void EveryFileStillReadsBackAsItself()
    {
        // The property that matters: dedup must not hand an entry someone else's bytes.
        var shared = Blob(4);
        var unique = Blob(5);
        Write("data/one.bin", shared);
        Write("data/two.bin", shared);
        Write("data/three.bin", unique);

        string wad = Path.Combine(_dir, "out.wad.client");
        Assert.True(WadPackService.Pack(Src, wad).Success);

        using var archive = WadArchive.Open(wad);
        foreach (var (rel, expected) in new[]
                 { ("data/one.bin", shared), ("data/two.bin", shared), ("data/three.bin", unique) })
        {
            ulong h = ReyEngine.Core.Hashing.HashAlgorithms.WadPath(rel);
            Assert.True(archive.TryGetEntry(h, out _), rel);
            Assert.Equal(expected, archive.Extract(h));
        }
    }

    [Fact]
    public void FilesThatMerelyShareALengthAreNotConflated()
    {
        // The dedup key hashes for lookup but compares byte for byte; same-size different-content must
        // stay separate, because conflating them would ship the wrong asset silently.
        var a = Blob(6);
        var b = Blob(7);
        Assert.Equal(a.Length, b.Length);
        Write("data/a.bin", a);
        Write("data/b.bin", b);

        string wad = Path.Combine(_dir, "out.wad.client");
        WadPackService.Pack(Src, wad);

        var (count, distinct, _) = ReadToc(wad);
        Assert.Equal(2, count);
        Assert.Equal(2, distinct);

        using var archive = WadArchive.Open(wad);
        Assert.Equal(a, archive.Extract(ReyEngine.Core.Hashing.HashAlgorithms.WadPath("data/a.bin")));
        Assert.Equal(b, archive.Extract(ReyEngine.Core.Hashing.HashAlgorithms.WadPath("data/b.bin")));
    }

    [Fact]
    public void APackWithNoDuplicatesIsUnchanged()
    {
        Write("data/a.bin", Blob(8));
        Write("data/b.bin", Blob(9));

        string wad = Path.Combine(_dir, "out.wad.client");
        var report = WadPackService.Pack(Src, wad);

        Assert.Equal(0, report.SharedChunks);
        var (count, distinct, _) = ReadToc(wad);
        Assert.Equal(2, count);
        Assert.Equal(2, distinct);
    }
}

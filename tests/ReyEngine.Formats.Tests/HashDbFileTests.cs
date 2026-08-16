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
    // The writer lives in HashDbTestWriter so the layering tests build identical files.
    private static string WriteDb(string path, IEnumerable<(ulong Key, string Value)> input,
        int keyWidth = 8, bool compressArena = true, int framesWanted = 3)
        => HashDbTestWriter.Write(path, input, keyWidth, compressArena, framesWanted);

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

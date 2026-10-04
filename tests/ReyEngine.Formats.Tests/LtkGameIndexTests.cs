using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using ReyEngine.Formats.LtkGameData;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M818: the game's object index - which archives there are and in what order, which holds each chunk first, which chunks are bins, and what they declare - against archives written
/// by hand. The index equals <c>ltk_game_index</c>'s over the installed game row for row (the harness in <c>.codex_tmp/M818</c> compares 393 archives, 808,646 chunks and 440,618
/// declarations); these tests keep each rule it rests on, and need no game.
/// </summary>
public sealed class LtkGameIndexTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Game => _temp.Combine("game");

    private string Final => Path.Combine(Game, "DATA", "FINAL");

    private string Archive(string name) => Path.Combine(Final, name.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string name, params TestChunk[] chunks) => TestWad.Write(Archive(name), chunks);

    private static byte[] BinOf(string path, params string[] tags) => Bin(Obj(path, tags));

    // ================================================================================================ the archives

    [Fact]
    public void Archives_are_every_wad_client_below_data_final_in_byte_order_of_the_relative_name()
    {
        // 'B' < 'C' < 'D' < 'M' < 'Z' < 'a' in bytes: a case-insensitive order would put a.wad.client first
        string[] names =
        {
            "a.wad.client", "B.wad.client", "Champions/Aatrox.wad.client", "Champions/Aatrox.en_US.wad.client",
            "Maps/Shipping/Map11.wad.client", "Z/x.WAD.CLIENT", "Dir.wad.client/inner.wad.client",
        };
        foreach (string name in names) Write(name);
        File.WriteAllText(Archive("notes.txt"), "not an archive");
        File.WriteAllText(Archive("m.wad.client.bak"), "not an archive either");
        Directory.CreateDirectory(Archive("Empty.wad.client"));   // a directory is not a file

        var archives = GameArchiveList.Enumerate(Game);

        Assert.Equal(
            new[]
            {
                "B.wad.client", "Champions/Aatrox.en_US.wad.client", "Champions/Aatrox.wad.client", "Dir.wad.client/inner.wad.client",
                "Maps/Shipping/Map11.wad.client", "Z/x.WAD.CLIENT", "a.wad.client",
            },
            archives.Select(a => a.Name).ToArray());
        Assert.Equal(Enumerable.Range(0, 7), archives.Select(a => a.Id));
        Assert.Equal("DATA/FINAL/Champions/Aatrox.wad.client", archives[2].WadPath);
        Assert.Equal("Aatrox.wad.client", archives[2].FileName);
        Assert.Equal(Path.GetFullPath(Archive("Champions/Aatrox.wad.client")), archives[2].Path);
    }

    [Fact]
    public void An_archive_name_is_compared_by_its_utf8_bytes()
    {
        // U+FF5E is three bytes starting 0xEF and U+1F600 four bytes starting 0xF0: the bytes put the emoji after, where UTF-16 code units put its surrogate (0xD83D) before
        Write("～.wad.client");
        Write("\U0001F600.wad.client");
        Write("z.wad.client");

        var names = GameArchiveList.Enumerate(Game).Select(a => a.Name).ToArray();

        Assert.Equal(new[] { "z.wad.client", "～.wad.client", "\U0001F600.wad.client" }, names);
    }

    [Fact]
    public void A_directory_that_is_not_an_installation_is_a_refusal_and_not_an_empty_index()
    {
        var error = Assert.Throws<GameIndexException>(() => GameArchiveList.Enumerate(_temp.Combine("nowhere")));
        Assert.Contains("no DATA/FINAL directory", error.Message);
    }

    [Fact]
    public void The_fingerprint_is_xxh3_over_each_archives_name_length_and_modification_time()
    {
        Write("A.wad.client", TestChunk.Compressed(1, BinOf("Test/Obj/A", "a")));
        Write("b.wad.client", TestChunk.Compressed(2, BinOf("Test/Obj/B", "b")));
        File.SetLastWriteTimeUtc(Archive("A.wad.client"), new DateTime(2026, 9, 24, 19, 49, 12, DateTimeKind.Utc).AddTicks(1234567 % 1000));
        File.SetLastWriteTimeUtc(Archive("b.wad.client"), new DateTime(2026, 9, 25, 1, 2, 3, DateTimeKind.Utc));
        var archives = GameArchiveList.Enumerate(Game);

        // what the crate computes: the name's bytes, the length as u64, the time in nanoseconds since the epoch as u128, little-endian, for each archive in order
        var hash = new XxHash3();
        foreach (var archive in archives)
        {
            hash.Append(Encoding.UTF8.GetBytes(archive.Name));
            hash.Append(BitConverter.GetBytes((ulong)new FileInfo(archive.Path).Length));
            ulong nanos = (ulong)((File.GetLastWriteTimeUtc(archive.Path).Ticks - DateTime.UnixEpoch.Ticks) * 100);
            hash.Append(BitConverter.GetBytes(nanos));
            hash.Append(new byte[8]);
        }

        Assert.Equal(hash.GetCurrentHashAsUInt64(), GameArchiveList.Fingerprint(archives));
    }

    [Fact]
    public void Any_change_to_an_archives_name_length_or_time_is_another_fingerprint()
    {
        Write("A.wad.client", TestChunk.Compressed(1, BinOf("Test/Obj/A", "a")));
        Write("B.wad.client", TestChunk.Compressed(2, BinOf("Test/Obj/B", "b")));
        ulong before = GameArchiveList.Fingerprint(GameArchiveList.Enumerate(Game));
        Assert.Equal(before, GameArchiveList.Fingerprint(GameArchiveList.Enumerate(Game)));

        var written = File.GetLastWriteTimeUtc(Archive("B.wad.client"));
        File.SetLastWriteTimeUtc(Archive("B.wad.client"), written.AddSeconds(1));
        ulong touched = GameArchiveList.Fingerprint(GameArchiveList.Enumerate(Game));
        Assert.NotEqual(before, touched);

        File.SetLastWriteTimeUtc(Archive("B.wad.client"), written);
        Assert.Equal(before, GameArchiveList.Fingerprint(GameArchiveList.Enumerate(Game)));

        using (var stream = new FileStream(Archive("B.wad.client"), FileMode.Append)) stream.WriteByte(0);
        File.SetLastWriteTimeUtc(Archive("B.wad.client"), written);
        Assert.NotEqual(before, GameArchiveList.Fingerprint(GameArchiveList.Enumerate(Game)));

        File.Move(Archive("B.wad.client"), Archive("C.wad.client"));
        Assert.NotEqual(touched, GameArchiveList.Fingerprint(GameArchiveList.Enumerate(Game)));
    }

    // ================================================================================================ which archive holds a chunk

    [Fact]
    public void A_chunk_is_held_by_every_archive_that_lists_it_and_first_by_the_lowest_id()
    {
        // 'a' sorts after 'B' in bytes, so B is archive 0 and a archive 1
        Write("a.wad.client", TestChunk.Compressed(0x20, BinOf("Test/Obj/A", "a")), TestChunk.Compressed(0x30, BinOf("Test/Obj/Only", "o")));
        Write("B.wad.client", TestChunk.Compressed(0x20, BinOf("Test/Obj/A", "a")), TestChunk.Compressed(0x10, BinOf("Test/Obj/B", "b")));

        var table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));

        Assert.Equal(new[] { 0, 1 }, table.Holders(0x20));
        Assert.Equal(0, table.FirstHolder(0x20));
        Assert.Equal(new[] { 0 }, table.Holders(0x10));
        Assert.Equal(new[] { 1 }, table.Holders(0x30));
        Assert.Empty(table.Holders(0x99));
        Assert.Equal(-1, table.FirstHolder(0x99));
        Assert.True(table.ContainsChunk(0x30));
        Assert.False(table.ContainsChunk(0x99));
        Assert.Equal(3, table.ChunkCount);
        Assert.Equal(new ulong[] { 0x10, 0x20, 0x30 }, Enumerable.Range(0, table.ChunkCount).Select(table.ChunkAt));
        Assert.Equal(new[] { 0, 1 }, table.HoldersAt(1));
    }

    [Fact]
    public void An_archive_that_does_not_mount_keeps_its_id_and_holds_no_chunk()
    {
        Write("A.wad.client", TestChunk.Compressed(0x20, BinOf("Test/Obj/A", "a")));
        File.WriteAllText(Archive("M.wad.client"), "this is not an archive");
        Write("Z.wad.client", TestChunk.Compressed(0x20, BinOf("Test/Obj/A", "a")), TestChunk.Compressed(0x40, BinOf("Test/Obj/Z", "z")));

        var table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));

        Assert.Equal(new[] { "A.wad.client", "M.wad.client", "Z.wad.client" }, table.Archives.Select(a => a.Name).ToArray());
        var skipped = Assert.Single(table.Skipped);
        Assert.Equal(1, skipped.Archive);
        Assert.Equal("M.wad.client", skipped.Name);
        Assert.Equal("cannot mount the archive: invalid header", skipped.Error);
        Assert.False(skipped.Transient);   // the bytes are not an archive, on every build: this is settled
        Assert.True(table.IsSettled);
        Assert.Equal(new[] { 0, 2 }, table.Holders(0x20));
        Assert.Equal(2, table.FirstHolder(0x40));
    }

    [Theory]
    [InlineData(4, 0, "cannot mount the archive: invalid version 4.0")]
    [InlineData(1, 0, "cannot mount the archive: invalid version 1.0")]   // a version 1 table fails at its first descriptor
    [InlineData(2, 3, "cannot mount the archive: invalid version 2.3")]
    public void An_archive_of_a_version_the_crate_does_not_read_is_skipped(int major, int minor, string error)
    {
        TestWad.Write(Archive("Old.wad.client"), new[] { TestChunk.Stored_(5, new byte[] { 1, 2, 3, 4 }) }, (byte)major, (byte)minor);

        var table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));

        Assert.Equal(error, Assert.Single(table.Skipped).Error);
        Assert.Equal(0, table.ChunkCount);
    }

    [Fact]
    public void An_empty_version_one_or_two_archive_mounts_and_holds_nothing()
    {
        TestWad.Write(Archive("Old.wad.client"), Array.Empty<TestChunk>(), 1, 0);
        TestWad.Write(Archive("Older.wad.client"), Array.Empty<TestChunk>(), 2, 0);

        var table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));

        Assert.Empty(table.Skipped);
        Assert.Equal(0, table.ChunkCount);
    }

    [Theory]
    [InlineData(0)]   // the table of contents is cut off
    [InlineData(1)]   // a compression the format does not name
    public void A_table_that_is_short_or_names_a_compression_the_format_lacks_is_skipped(int fault)
    {
        byte[] bytes = TestWad.Build(new[] { TestChunk.Stored_(5, new byte[] { 1, 2, 3, 4 }), TestChunk.Stored_(6, new byte[] { 1, 2, 3, 4 }) });
        if (fault == 0) bytes = bytes[..(272 + 40)];
        else bytes[272 + 20] = 9;   // the first descriptor's compression
        Directory.CreateDirectory(Final);
        File.WriteAllBytes(Archive("Bad.wad.client"), bytes);

        var table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));

        string error = Assert.Single(table.Skipped).Error;
        Assert.StartsWith("cannot mount the archive: ", error);
        Assert.Equal(0, table.ChunkCount);
    }

    [Fact]
    public void A_chunk_an_archive_lists_twice_is_one_chunk_with_one_holder()
    {
        byte[] first = BinOf("Test/Obj/First", "1"), second = BinOf("Test/Obj/Second", "2");
        Write("A.wad.client", TestChunk.Compressed(0x20, first), TestChunk.Compressed(0x20, second));

        var index = GameObjectIndexBuilder.Build(Game);

        Assert.Equal(1, index.Table.ChunkCount);
        Assert.Equal(new[] { 0 }, index.Table.Holders(0x20));
        // the crate reads the chunk by hash, which finds the LAST row the table lists
        var declaration = Assert.Single(index.DeclarationsIn(0x20));
        Assert.Equal(H("Test/Obj/Second"), declaration.Object);
    }

    // ================================================================================================ which chunks are bins

    [Fact]
    public void A_chunk_is_a_bin_when_it_opens_with_prop_or_ptch_whatever_it_is_called_or_how_it_is_stored()
    {
        byte[] whole = BinOf("Test/Obj/D1", "d");
        int half = whole.Length / 2;
        var chunks = new[]
        {
            TestChunk.Compressed(0x01, BinOf("Test/Obj/A1", "a")),                      // Zstd
            TestChunk.GZipped(0x02, BinOf("Test/Obj/B1", "b")),                         // GZip
            TestChunk.Stored_(0x03, BinOf("Test/Obj/C1", "c")),                         // not compressed
            TestChunk.Multi(0x04, whole[..half], whole[half..]),                        // Zstd frames laid end to end
            TestChunk.Compressed(0x05, Ptch(new[] { Obj("Test/Obj/P1", new[] { "p" }) })),
            TestChunk.Compressed(0x06, Encoding.ASCII.GetBytes("TEX\0 anything at all, not a bin, long enough to look like a texture")),
            TestChunk.Stored_(0x07, Encoding.ASCII.GetBytes("PRO")),                    // shorter than a magic
            TestChunk.Stored_(0x08, Array.Empty<byte>()),
            TestChunk.SatelliteOf(0x09, 100),                                           // holds no bytes
            new TestChunk(0x0A, TestChunk.Zstd, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 100),   // does not decompress
            TestChunk.Compressed(0x0B, "PROP"u8.ToArray().Concat(new byte[] { 0xff, 0xff, 0xff, 0xff, 1, 2, 3, 4 }).ToArray()),   // opens like a bin and is not one
            TestChunk.Compressed(0x0C, TestWad.BigBin("Test/Obj/Big", 90)),             // its first Zstd block does not fit the first read
            TestChunk.Stored_(0x0D, new byte[] { (byte)'P', (byte)'R', (byte)'O', (byte)'P', 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),   // a bin of no objects
        };
        Write("A.wad.client", chunks);

        var index = GameObjectIndexBuilder.Build(Game);

        Assert.Equal(
            new[] { "Test/Obj/A1", "Test/Obj/B1", "Test/Obj/C1", "Test/Obj/D1", "Test/Obj/P1", "Test/Obj/Big" }.Select(H).ToArray(),
            Enumerable.Range(0, index.DeclarationCount).Select(i => index.DeclarationAt(i).Object).ToArray());
        Assert.Equal(new ulong[] { 1, 2, 3, 4, 5, 0x0C }, Enumerable.Range(0, index.DeclarationCount).Select(i => index.DeclarationAt(i).Chunk).ToArray());
        Assert.Equal(13, index.Stats.Sniffed);
        Assert.Equal(8, index.Stats.Bins);          // 1-5, 0B, 0C, 0D open with a magic
        Assert.Equal(1, index.Stats.SkippedChunks);  // 0B does not decode
        Assert.Equal(6, index.Stats.Declarations);
        Assert.True(index.Stats.Bytes > 0);
    }

    [Fact]
    public void A_bin_that_does_not_decode_declares_nothing_even_where_its_table_could_be_swept()
    {
        // two objects, cut inside the first: the sweep reads the first object's header, hops by its size to past the end of the file, and fails at the second: the first is dropped
        byte[] two = Bin(Obj("Test/Obj/First", new[] { "x" }), Obj("Test/Obj/Second", new[] { "x" }));
        byte[] cut = two[..40];   // PROP, version, 0 dependencies, 2 objects, 2 classes and the first object's header make 32 bytes
        Write("A.wad.client", TestChunk.Compressed(0x01, cut), TestChunk.Compressed(0x02, BinOf("Test/Obj/Fine", "y")));

        var index = GameObjectIndexBuilder.Build(Game);

        Assert.Equal(new[] { H("Test/Obj/Fine") }, Enumerable.Range(0, index.DeclarationCount).Select(i => index.DeclarationAt(i).Object));
        Assert.Equal(2, index.Stats.Bins);
        Assert.Equal(1, index.Stats.SkippedChunks);
        Assert.True(index.IsSettled);   // a bin that does not decode is the bytes, and the same on every build
    }

    [Fact]
    public void A_ptch_declares_the_objects_it_holds_and_its_records_declare_nothing()
    {
        var patch = Obj("Test/Obj/FromPatch", new[] { "p" });
        Write("A.wad.client", TestChunk.Compressed(0x01, Ptch(new[] { patch }, H("Test/Obj/Deleted"))));

        var index = GameObjectIndexBuilder.Build(Game);

        var declaration = Assert.Single(Enumerable.Range(0, index.DeclarationCount).Select(index.DeclarationAt));
        Assert.Equal(new GameObjectDeclaration(H("Test/Obj/FromPatch"), TestClass, 0x01, 0), declaration);
        Assert.False(index.Declares(H("Test/Obj/Deleted")));
    }

    // ================================================================================================ what the chunks declare, and in what order

    [Fact]
    public void Declarations_are_stored_by_archive_then_chunk_hash_then_object_order_and_a_chunk_is_read_from_its_first_holder_only()
    {
        // archive 0 (B) holds chunk 0x20 (o3, o1) and 0x10 (o2); archive 1 (a) holds 0x05 (o1) and a copy of 0x20 it is not the first holder of
        Write("B.wad.client",
            TestChunk.Compressed(0x20, Bin(Obj("O/Three", new[] { "3" }), Obj("O/One", new[] { "1" }))),
            TestChunk.Compressed(0x10, Bin(Obj("O/Two", new[] { "2" }))));
        Write("a.wad.client",
            TestChunk.Compressed(0x05, Bin(Obj("O/One", new[] { "again" }))),
            TestChunk.Compressed(0x20, Bin(Obj("O/Intruder", new[] { "no" }))));

        var index = GameObjectIndexBuilder.Build(Game);

        var stored = Enumerable.Range(0, index.DeclarationCount).Select(index.DeclarationAt).ToArray();
        Assert.Equal(
            new[]
            {
                (H("O/Two"), 0x10UL, 0), (H("O/Three"), 0x20UL, 0), (H("O/One"), 0x20UL, 0), (H("O/One"), 0x05UL, 1),
            },
            stored.Select(d => (d.Object, d.Chunk, d.Archive)).ToArray());

        // by object, stably: the first declaration is the first in storage order, not the lowest chunk hash
        Assert.Equal(new ulong[] { 0x20, 0x05 }, index.Declarations(H("O/One")).Select(d => d.Chunk).ToArray());
        Assert.Equal(new GameObjectDeclaration(H("O/One"), TestClass, 0x20, 0), index.FirstDeclaration(H("O/One")));
        Assert.False(index.Declares(H("O/Intruder")));
        Assert.Null(index.FirstDeclaration(H("O/Intruder")));
        Assert.Empty(index.Declarations(H("O/Nobody")));
        Assert.Equal(new[] { H("O/Three"), H("O/One") }, index.DeclarationsIn(0x20).Select(d => d.Object).ToArray());
        Assert.Empty(index.DeclarationsIn(0x99));
        Assert.Equal(new[] { H("O/One"), H("O/Two"), H("O/Three") }.Order().ToArray(), index.Objects().ToArray());
        Assert.Equal(3, index.ObjectCount);
    }

    [Fact]
    public void The_index_answers_who_holds_a_chunk_and_where_the_archive_lives_as_the_table_does()
    {
        Write("B.wad.client", TestChunk.Compressed(0x20, BinOf("O/A", "a")));
        Write("a.wad.client", TestChunk.Compressed(0x20, BinOf("O/A", "a")), TestChunk.Compressed(0x30, BinOf("O/B", "b")));

        var index = GameObjectIndexBuilder.Build(Game);

        Assert.Equal(0, index.FirstHolder(0x20));
        Assert.Equal(new[] { 0, 1 }, index.Holders(0x20));
        Assert.Equal(1, index.FirstHolder(0x30));
        Assert.Equal(-1, index.FirstHolder(0x99));
        Assert.Equal("DATA/FINAL/a.wad.client", index.WadPath(1));
        Assert.Equal(index.Table.Archive(0), index.Table.Archives[0]);
    }

    [Fact]
    public void An_object_a_chunk_lists_twice_is_declared_twice()
    {
        Write("A.wad.client", TestChunk.Compressed(0x01, TwoRows(Obj("O/X", new[] { "first" }), Obj("O/X", new[] { "last" }))));

        var index = GameObjectIndexBuilder.Build(Game);

        Assert.Equal(2, index.Declarations(H("O/X")).Length);
        Assert.Equal(2, index.DeclarationsIn(0x01).Length);
    }

    [Fact]
    public void The_stats_say_what_the_build_did()
    {
        Write("A.wad.client", TestChunk.Compressed(0x01, BinOf("O/A", "a")), TestChunk.Compressed(0x02, Encoding.ASCII.GetBytes("TEX\0texture bytes here")));
        Write("B.wad.client", TestChunk.Compressed(0x03, BinOf("O/B", "b")));

        var index = GameObjectIndexBuilder.Build(Game, new GameObjectIndexOptions { Workers = 2 });

        Assert.Equal(2, index.Stats.Workers);
        Assert.Equal(3, index.Stats.Sniffed);
        Assert.Equal(2, index.Stats.Bins);
        Assert.Equal(2, index.Stats.Declarations);
        Assert.Equal(0, index.Stats.SkippedChunks);
        Assert.Equal(3, index.Table.ChunkCount);
        Assert.Equal(index.Table.Fingerprint, index.Fingerprint);
        Assert.Equal(Game, index.Table.GameDirectory);
    }

    [Fact]
    public void The_build_does_not_touch_the_game()
    {
        Write("A.wad.client", TestChunk.Compressed(0x01, BinOf("O/A", "a")));
        string before = Describe();

        GameObjectIndexBuilder.Build(Game);

        Assert.Equal(before, Describe());

        string Describe() => string.Join("\n", Directory.EnumerateFiles(Game, "*", SearchOption.AllDirectories)
            .Order().Select(f => $"{f} {new FileInfo(f).Length} {File.GetLastWriteTimeUtc(f).Ticks}"));
    }

    // ================================================================================================ cancellation and progress

    private sealed class SyncProgress : IProgress<GameIndexProgress>
    {
        public readonly List<GameIndexProgress> Seen = new();
        public Action<GameIndexProgress>? OnReport;

        public void Report(GameIndexProgress value)
        {
            lock (Seen) Seen.Add(value);
            OnReport?.Invoke(value);
        }
    }

    [Fact]
    public void A_build_names_each_stage_in_order_and_ends_with_every_archive_done()
    {
        Write("A.wad.client", TestChunk.Compressed(0x01, BinOf("O/A", "a")));
        Write("B.wad.client", TestChunk.Compressed(0x02, BinOf("O/B", "b")));
        var progress = new SyncProgress();

        GameObjectIndexBuilder.Build(Game, progress: progress);

        var stages = progress.Seen.Select(p => p.Stage).Distinct().ToArray();
        Assert.Equal(new[] { GameIndexStage.Enumerating, GameIndexStage.Mounting, GameIndexStage.ReadingBins }, stages);
        var last = progress.Seen.Last(p => p.Stage == GameIndexStage.ReadingBins);
        Assert.Equal(2, last.Done);
        Assert.Equal(2, last.Total);
        Assert.Equal(2, last.Bins);
        Assert.Equal(2, last.Declarations);
    }

    [Fact]
    public void A_cancelled_build_ends_with_the_tokens_exception()
    {
        for (int i = 0; i < 6; i++) Write($"A{i}.wad.client", TestChunk.Compressed((ulong)i + 1, BinOf($"O/A{i}", "a")));

        using var already = new CancellationTokenSource();
        already.Cancel();
        Assert.Throws<OperationCanceledException>(() => GameObjectIndexBuilder.Build(Game, cancellationToken: already.Token));

        // cancelled from inside the build, between the stages
        using var midway = new CancellationTokenSource();
        var progress = new SyncProgress { OnReport = p => { if (p.Stage == GameIndexStage.ReadingBins) midway.Cancel(); } };
        Assert.Throws<OperationCanceledException>(() => GameObjectIndexBuilder.Build(Game, new GameObjectIndexOptions { Workers = 1 }, progress, midway.Token));
    }
}

using System.Text;
using ReyEngine.Formats.LtkGameData;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M818: reading the game's bytes - a chunk of an archive, and the entry a reference names - as the overlay does it. A reference reads the FIRST chunk the index says declares the entry, once
/// per entry, and a failure is a statement and never an exception.
/// </summary>
public sealed class LtkGameEntryReaderTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Game => _temp.Combine("game");

    private string Archive(string name) => Path.Combine(Game, "DATA", "FINAL", name.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string name, params TestChunk[] chunks) => TestWad.Write(Archive(name), chunks);

    private GameObjectIndex Index() => GameObjectIndexBuilder.Build(Game);

    private static EntryName Name(string text) => EntryName.Create(text);

    private static string[] Tags(PropObject obj) => ((PropList)obj.Properties.ValueAt(obj.Properties.IndexOf(H("tags")))).Items.Select(i => ((PropString)i).Value).ToArray();

    // ================================================================================================ a chunk of an archive

    [Theory]
    [InlineData(4)]   // the 24-bit start frame
    [InlineData(3)]   // a u16 start frame after a duplicated flag
    [InlineData(0)]
    public void Every_way_an_archive_stores_a_chunk_reads_back_to_its_bytes(int minor)
    {
        byte[] text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("a chunk of text that compresses well, ", 200)));
        byte[] whole = Bin(Obj("O/Multi", new[] { "m" }));
        int half = whole.Length / 2;
        var chunks = new[]
        {
            TestChunk.Stored_(0x01, text),
            TestChunk.Compressed(0x02, text),
            TestChunk.GZipped(0x03, text),
            TestChunk.Multi(0x04, whole[..half], whole[half..]),
            TestChunk.Stored_(0x05, Array.Empty<byte>()),
        };
        TestWad.Write(Archive("A.wad.client"), chunks, 3, (byte)minor);
        var table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));
        var reader = new GameChunkReader(table);

        Assert.Equal(text, reader.Read(0, 0x01));
        Assert.Equal(text, reader.Read(0, 0x02));
        Assert.Equal(text, reader.Read(0, 0x03));
        Assert.Equal(whole, reader.Read(0, 0x04));
        Assert.Empty(reader.Read(0, 0x05));
    }

    [Fact]
    public void A_chunk_that_cannot_be_read_is_a_statement_that_says_why()
    {
        Write("A.wad.client",
            TestChunk.Compressed(0x01, Bin(Obj("O/A", new[] { "a" }))),
            TestChunk.SatelliteOf(0x02, 10),
            new TestChunk(0x03, TestChunk.Zstd, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 50),
            new TestChunk(0x04, TestChunk.Zstd, TestChunk.ZstdBytes(new byte[] { 1, 2, 3, 4 }), 400));   // decodes to fewer bytes than the table says
        var table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));
        var reader = new GameChunkReader(table);

        Assert.Contains("does not hold the chunk", Assert.Throws<GameChunkReadException>(() => reader.Read(0, 0x99)).Message);
        Assert.Contains("satellite", Assert.Throws<GameChunkReadException>(() => reader.Read(0, 0x02)).Message);
        Assert.StartsWith("failed to decompress chunk", Assert.Throws<GameChunkReadException>(() => reader.Read(0, 0x03)).Message);
        Assert.Contains("decompressed 4 bytes, expected 400", Assert.Throws<GameChunkReadException>(() => reader.Read(0, 0x04)).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(7, 0x01));
    }

    [Fact]
    public void An_archive_that_changed_since_the_index_was_built_is_a_stale_index_and_not_a_read_from_a_wrong_offset()
    {
        Write("A.wad.client", TestChunk.Compressed(0x01, Bin(Obj("O/A", new[] { "a" }))));
        var table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));
        var reader = new GameChunkReader(table);
        Assert.NotEmpty(reader.Read(0, 0x01));

        // another game's archive of the same name: another table, and bytes at other offsets
        Write("A.wad.client", TestChunk.Compressed(0x77, new byte[40]), TestChunk.Compressed(0x01, Bin(Obj("O/Other", new[] { "other" }))));

        var error = Assert.Throws<GameChunkReadException>(() => reader.Read(0, 0x01));
        Assert.Equal("DATA/FINAL/A.wad.client changed after the index was built, so the index is stale", error.Message);

        File.Delete(Archive("A.wad.client"));
        Assert.Equal("DATA/FINAL/A.wad.client is gone", Assert.Throws<GameChunkReadException>(() => reader.Read(0, 0x01)).Message);
    }

    [Fact]
    public void Reads_from_many_threads_across_more_archives_than_the_reader_keeps_mounted_are_all_right()
    {
        const int archives = 24;
        for (int i = 0; i < archives; i++) Write($"A{i:D2}.wad.client", TestChunk.Compressed(0x100 + (ulong)i, Bin(Obj($"O/A{i}", new[] { $"tag{i}" }))));
        var table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));
        var reader = new GameChunkReader(table);

        var failures = new List<string>();
        Parallel.For(0, 600, new ParallelOptions { MaxDegreeOfParallelism = 8 }, n =>
        {
            int id = n * 7 % archives;
            try { Assert.Equal(new[] { $"tag{id}" }, TagsOf(reader.Read(id, 0x100 + (ulong)id), $"O/A{id}")); }
            catch (Exception e) { lock (failures) failures.Add(e.Message); }
        });

        Assert.Empty(failures);
    }

    // ================================================================================================ the entry a reference names

    [Fact]
    public void An_entry_is_read_from_the_first_declaring_chunk_in_storage_order()
    {
        // 'Z' sorts before 'a' in bytes, so Z is archive 0 and its copy is the first declaration
        Write("a.wad.client", TestChunk.Compressed(0x10, Bin(Obj("O/X", new[] { "from_a" }))));
        Write("Z.wad.client", TestChunk.Compressed(0x90, Bin(Obj("O/X", new[] { "from_z" }))));
        var index = Index();
        var reader = new GameEntryReader(index, new GameChunkReader(index.Table));

        var read = reader.Read(Name("O/X"));

        Assert.Null(read.Error);
        Assert.Equal(new[] { "from_z" }, Tags(read.Object!));
        Assert.Equal(1, reader.ChunkReads);
    }

    [Fact]
    public void An_entry_nobody_declares_and_a_reader_with_no_index_both_answer_none()
    {
        Write("A.wad.client", TestChunk.Compressed(0x10, Bin(Obj("O/X", new[] { "x" }))));
        var index = Index();

        var present = new GameEntryReader(index, new GameChunkReader(index.Table));
        var absent = new GameEntryReader(null, new GameChunkReader(index.Table));

        Assert.Equal(GameDataEntryRead.None, present.Read(Name("O/Nobody")));
        Assert.Equal(GameDataEntryRead.None, absent.Read(Name("O/X")));
        Assert.Equal(0, present.ChunkReads + absent.ChunkReads);
    }

    [Fact]
    public void An_entry_spelled_two_ways_is_one_entry()
    {
        Write("A.wad.client", TestChunk.Compressed(0x10, Bin(Obj("O/X", new[] { "x" }))));
        var index = Index();
        var reader = new GameEntryReader(index, new GameChunkReader(index.Table));

        var byName = reader.Read(Name("O/X"));
        var byHash = reader.Read(Name("0x" + H("O/X").ToString("x8")));

        Assert.Same(byName.Object, byHash.Object);
        Assert.Equal(1, reader.ChunkReads);
    }

    private sealed class FlakyChunks : IGameChunkReader
    {
        private readonly IGameChunkReader _real;
        public int Calls;
        public int FailFirst;
        public long LastMaxBytes;

        public FlakyChunks(IGameChunkReader real) { _real = real; }

        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            LastMaxBytes = maxBytes;
            if (Calls <= FailFirst) throw new GameChunkReadException("the disk is busy");
            return _real.ReadChunk(archive, chunk, maxBytes, cancellationToken);
        }
    }

    [Fact]
    public void An_answer_is_kept_and_a_failure_is_not()
    {
        Write("A.wad.client", TestChunk.Compressed(0x10, Bin(Obj("O/X", new[] { "x" }))));
        var index = Index();
        var chunks = new FlakyChunks(new GameChunkReader(index.Table)) { FailFirst = 2 };
        var reader = new GameEntryReader(index, chunks);

        var first = reader.Read(Name("O/X"));
        var second = reader.Read(Name("O/X"));
        var third = reader.Read(Name("O/X"));
        var fourth = reader.Read(Name("O/X"));

        string wad = "DATA/FINAL/A.wad.client";
        Assert.Equal($"O/X: {0x10:x16} of {wad}: the disk is busy", first.Error);
        Assert.Equal(first.Error, second.Error);
        Assert.Null(third.Error);
        Assert.Same(third.Object, fourth.Object);
        Assert.Equal(3, chunks.Calls);   // two that failed and one that served, and the fourth read none
    }

    [Fact]
    public void A_first_holder_that_is_a_ptch_is_unreadable_with_the_codecs_statement()
    {
        Write("P.wad.client", TestChunk.Compressed(0x10, Ptch(new[] { Obj("O/P1", new[] { "p" }) })));
        var index = Index();
        var reader = new GameEntryReader(index, new GameChunkReader(index.Table));

        var read = reader.Read(Name("O/P1"));

        Assert.Null(read.Object);
        Assert.Equal($"O/P1: {0x10:x16} of DATA/FINAL/P.wad.client: Expected a PROP bin, found a PTCH bin", read.Error);
    }

    [Fact]
    public void A_chunk_that_does_not_hold_the_object_after_all_is_a_stale_index()
    {
        Write("A.wad.client", TestChunk.Compressed(0x10, Bin(Obj("O/X", new[] { "x" }))));
        var index = Index();
        var other = new FixedChunk(Bin(Obj("O/Y", new[] { "y" })));
        var reader = new GameEntryReader(index, other);

        var read = reader.Read(Name("O/X"));

        Assert.Equal($"O/X: {0x10:x16} of DATA/FINAL/A.wad.client: chunk does not hold the object, so the index is stale", read.Error);
    }

    private sealed class FixedChunk : IGameChunkReader
    {
        private readonly byte[] _bytes;

        public FixedChunk(byte[] bytes) { _bytes = bytes; }

        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken) => _bytes;
    }

    [Fact]
    public void A_chunk_that_does_not_decode_is_the_codecs_statement_and_never_an_exception()
    {
        Write("A.wad.client", TestChunk.Compressed(0x10, Bin(Obj("O/X", new[] { "x" }))));
        var index = Index();
        var reader = new GameEntryReader(index, new FixedChunk(new byte[] { 1, 2, 3 }));

        var read = reader.Read(Name("O/X"));

        Assert.Null(read.Object);
        Assert.StartsWith($"O/X: {0x10:x16} of DATA/FINAL/A.wad.client: ", read.Error);
    }

    [Fact]
    public void When_a_chunk_lists_one_object_twice_the_last_is_the_object()
    {
        Write("A.wad.client", TestChunk.Compressed(0x10, TwoRows(Obj("O/X", new[] { "first" }), Obj("O/X", new[] { "last" }))));
        var index = Index();
        var reader = new GameEntryReader(index, new GameChunkReader(index.Table));

        Assert.Equal(new[] { "last" }, Tags(reader.Read(Name("O/X")).Object!));
    }

    [Fact]
    public void The_entries_of_one_bin_cost_one_decompression()
    {
        Write("A.wad.client", TestChunk.Compressed(0x10, Bin(Obj("O/One", new[] { "1" }), Obj("O/Two", new[] { "2" }), Obj("O/Three", new[] { "3" }))));
        var index = Index();
        var chunks = new FlakyChunks(new GameChunkReader(index.Table));
        var reader = new GameEntryReader(index, chunks);

        foreach (string name in new[] { "O/One", "O/Two", "O/Three" }) Assert.Null(reader.Read(Name(name)).Error);

        Assert.Equal(1, chunks.Calls);
        Assert.Equal(1, reader.ChunkReads);
    }

    [Fact]
    public void A_cancelled_read_memoises_nothing()
    {
        Write("A.wad.client", TestChunk.Compressed(0x10, Bin(Obj("O/X", new[] { "x" }))));
        var index = Index();
        var chunks = new FlakyChunks(new GameChunkReader(index.Table));
        var reader = new GameEntryReader(index, chunks);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => reader.Read(Name("O/X"), cancel.Token));
        Assert.Equal(0, chunks.Calls);

        Assert.Null(reader.Read(Name("O/X")).Error);
    }

    [Fact]
    public void Threads_asking_for_one_entry_share_one_read_and_one_object()
    {
        Write("A.wad.client", TestChunk.Compressed(0x10, Bin(Obj("O/X", new[] { "x" }))));
        var index = Index();
        var chunks = new FlakyChunks(new GameChunkReader(index.Table));
        var reader = new GameEntryReader(index, chunks);

        var seen = new PropObject?[16];
        Parallel.For(0, seen.Length, new ParallelOptions { MaxDegreeOfParallelism = 16 }, n => seen[n] = reader.Read(Name("O/X")).Object);

        Assert.Equal(1, chunks.Calls);
        Assert.All(seen, o => Assert.Same(seen[0], o));
    }

    [Fact]
    public void The_reader_is_the_delegate_the_engine_takes()
    {
        Write("A.wad.client", TestChunk.Compressed(0x10, Bin(Obj("O/X", new[] { "x" }))));
        var index = Index();
        var reader = new GameEntryReader(index, new GameChunkReader(index.Table));

        ReadGameEntry read = reader.AsDelegate();

        Assert.Equal(new[] { "x" }, Tags(read(Name("O/X")).Object!));
    }
}

using System.Buffers.Binary;
using ReyEngine.Formats.LtkGameData;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M818 review fixes, reading the game: a chunk is sized by its table of contents before any of it is read, and the references of a package are budgeted. The archives here are written by hand, so that a
/// descriptor can claim what a corrupt or hostile table claims, and nothing needs the game.
/// </summary>
public sealed class LtkGameEntryReaderHardeningTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Game => _temp.Combine("game");

    private string Archive(string name) => Path.Combine(Game, "DATA", "FINAL", name.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string name, params TestChunk[] chunks) => TestWad.Write(Archive(name), chunks);

    private GameChunkTable Table() => GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));

    private GameObjectIndex Index() => GameObjectIndexBuilder.Build(Game);

    private static EntryName Name(string text) => EntryName.Create(text);

    /// <summary>Patches a u32 of the descriptor of chunk number <paramref name="chunk"/> of an archive written with <see cref="TestWad"/>: +8 is the offset, +12 the stored size, +16 the decoded size.</summary>
    private void Patch(string archive, int chunk, int field, uint value)
    {
        byte[] bytes = File.ReadAllBytes(Archive(archive));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(272 + chunk * 32 + field), value);
        File.WriteAllBytes(Archive(archive), bytes);
    }

    private static string Digits(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // ================================================================================================ sized before it is read

    [Fact]
    public void A_chunk_the_table_says_decodes_to_more_than_the_limit_is_refused_before_anything_is_allocated()
    {
        Write("A.wad.client", new TestChunk(0x01, TestChunk.Zstd, new byte[] { 1, 2, 3, 4 }, 3_000_000_000u), TestChunk.Stored_(0x02, new byte[400]));
        var reader = new GameChunkReader(Table());
        reader.Read(0, 0x02);   // mounts the table and warms the reader, so that what is measured is the refusal

        GameChunkReadException? error = null;
        long allocated = Allocated(() => error = Assert.Throws<GameChunkReadException>(() => reader.ReadChunk(0, 0x01, 64L << 20, CancellationToken.None)));

        Assert.Equal("chunk decodes to 3,000,000,000 bytes, more than the 67,108,864 the read allows", error!.Message);
        Assert.True(allocated < 1 << 20, $"{allocated:N0} bytes were allocated to refuse a chunk");
    }

    [Fact]
    public void A_descriptor_that_claims_more_stored_bytes_than_the_limit_or_than_the_file_holds_is_refused_before_anything_is_allocated()
    {
        Write("A.wad.client", TestChunk.Stored_(0x01, new byte[100]), TestChunk.Stored_(0x02, new byte[100]), TestChunk.Compressed(0x03, new byte[100]));
        Patch("A.wad.client", 0, 12, 200_000_000);   // stored in 200 MB: past the limit
        Patch("A.wad.client", 1, 12, 60_000_000);    // stored in 60 MB: inside the limit and far outside the file
        Patch("A.wad.client", 2, 8, 0xFFFFFFF0);     // an offset past the end of anything
        var reader = new GameChunkReader(Table());
        Assert.Contains("is stored in 200,000,000 bytes", Assert.Throws<GameChunkReadException>(() => reader.ReadChunk(0, 0x01, 64L << 20, CancellationToken.None)).Message);

        string outside = "";
        long allocated = Allocated(() => outside = Assert.Throws<GameChunkReadException>(() => reader.ReadChunk(0, 0x02, 64L << 20, CancellationToken.None)).Message);
        string offset = Assert.Throws<GameChunkReadException>(() => reader.ReadChunk(0, 0x03, 64L << 20, CancellationToken.None)).Message;

        Assert.StartsWith("chunk lies outside its archive: it ends at byte ", outside);
        Assert.StartsWith("chunk lies outside its archive: it ends at byte ", offset);
        Assert.True(allocated < 1 << 20, $"{allocated:N0} bytes were allocated to refuse a chunk");
    }

    [Fact]
    public void A_chunk_exactly_at_the_limit_reads_and_one_byte_over_does_not()
    {
        Write("A.wad.client", TestChunk.Stored_(0x01, Enumerable.Range(0, 400).Select(i => (byte)i).ToArray()), TestChunk.Compressed(0x02, new byte[4000]));
        var reader = new GameChunkReader(Table());

        Assert.Equal(400, reader.ReadChunk(0, 0x01, 400, CancellationToken.None).Length);
        Assert.Contains("more than the 399", Assert.Throws<GameChunkReadException>(() => reader.ReadChunk(0, 0x01, 399, CancellationToken.None)).Message);
        // a compressed chunk is held to what it DECODES to: it is small stored and large decoded, and it is the decoded bytes that become a tree
        Assert.Contains("decodes to 4,000 bytes, more than the 3,999", Assert.Throws<GameChunkReadException>(() => reader.ReadChunk(0, 0x02, 3999, CancellationToken.None)).Message);
        Assert.Equal(4000, reader.ReadChunk(0, 0x02, 4000, CancellationToken.None).Length);
    }

    [Fact]
    public void A_read_with_a_cancelled_token_ends_with_the_tokens_exception_and_a_read_leaves_the_archive_deletable()
    {
        Write("A.wad.client", TestChunk.Compressed(0x01, Bin(Obj("O/A", new[] { "a" }))));
        var reader = new GameChunkReader(Table());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => reader.ReadChunk(0, 0x01, 1 << 20, cancel.Token));
        Assert.NotEmpty(reader.Read(0, 0x01));

        // the read opened the file for its own length and closed it: nothing holds an archive the game's patcher may replace
        File.Delete(Archive("A.wad.client"));
        Assert.False(File.Exists(Archive("A.wad.client")));
    }

    // ================================================================================================ a reader's budget

    /// <summary>Bins of <paramref name="objectsEach"/> objects, stored as they are, so that what an archive stores a bin in and what it decodes to are one number, which the limits are exact about.</summary>
    private void WriteBins(int count, int objectsEach = 1)
    {
        var chunks = Enumerable.Range(0, count)
            .Select(i => TestChunk.Stored_(0x100 + (ulong)i, Bin(Enumerable.Range(0, objectsEach).Select(j => Obj($"O/B{i}_{j}", new[] { "t" })).ToArray())))
            .ToArray();
        Write("A.wad.client", chunks);
    }

    [Fact]
    public void A_reader_keeps_at_most_the_entries_it_is_allowed_and_a_refusal_is_not_remembered()
    {
        WriteBins(5);
        var index = Index();
        var reader = new GameEntryReader(index, new GameChunkReader(index.Table), new GameEntryLimits { MaxEntries = 3 });

        var kept = Enumerable.Range(0, 3).Select(i => reader.Read(Name($"O/B{i}_0"))).ToArray();
        var refused = reader.Read(Name("O/B3_0"));
        var refusedAgain = reader.Read(Name("O/B4_0"));

        Assert.All(kept, read => Assert.Null(read.Error));
        Assert.Null(refused.Object);
        Assert.Equal("O/B3_0: reference budget exceeded: more than the 3 distinct entries a reader keeps", refused.Error);
        Assert.StartsWith("O/B4_0: reference budget exceeded", refusedAgain.Error);
        Assert.Equal(2, reader.Stats.Refused);
        Assert.Equal(3, reader.Stats.Entries);
        // what was answered stays answered at the cap, and an entry nobody declares is none at any time and takes no slot
        Assert.Same(kept[0].Object, reader.Read(Name("O/B0_0")).Object);
        Assert.Equal(GameDataEntryRead.None, reader.Read(Name("O/Nobody")));
        Assert.Equal(3, reader.ChunkReads);
    }

    [Fact]
    public void A_reader_keeps_at_most_the_values_it_is_allowed_drops_the_object_that_would_pass_them_and_gives_the_room_back()
    {
        var wide = Obj("O/Wide", new[] { "w" });
        for (int i = 0; i < 40; i++) wide.Properties.Set(H("p" + i), new PropInt(PropKind.I32, (ulong)(uint)(1000 + i)));
        Write("A.wad.client",
            TestChunk.Compressed(0x01, Bin(Obj("O/Small1", new[] { "s" }))),
            TestChunk.Compressed(0x02, Bin(wide)),
            TestChunk.Compressed(0x03, Bin(Obj("O/Small2", new[] { "s" }))));
        var index = Index();
        var chunks = new GameChunkReader(index.Table);

        // what each object weighs, from a reader with room for them all
        var weigh = new GameEntryReader(index, chunks);
        weigh.Read(Name("O/Small1"));
        long small = weigh.Stats.RetainedValues;
        weigh.Read(Name("O/Wide"));
        long heavy = weigh.Stats.RetainedValues - small;
        Assert.True(heavy > 2 * small, $"the wide object weighs {heavy} and the small one {small}");

        // room for the small one and for another small one, and not for the small one and the wide one
        var reader = new GameEntryReader(index, chunks, new GameEntryLimits { MaxRetainedValues = small + heavy - 1 });
        Assert.Null(reader.Read(Name("O/Small1")).Error);
        var refused = reader.Read(Name("O/Wide"));

        Assert.Null(refused.Object);
        Assert.Contains("reference budget exceeded: the objects kept hold more than the ", refused.Error);
        Assert.Equal(small, reader.Stats.RetainedValues);   // what the dropped object would have held was given back
        Assert.Null(reader.Read(Name("O/Small2")).Error);   // and the room is there for what fits
        Assert.Equal(2 * small, reader.Stats.RetainedValues);
        Assert.Equal(1, reader.Stats.Refused);
    }

    [Fact]
    public void A_reader_reads_at_most_the_bytes_of_bins_it_is_allowed_and_asks_the_archive_for_no_bin_larger_than_what_is_left()
    {
        WriteBins(4, objectsEach: 2);
        long bin = Bin(Obj("O/B0_0", new[] { "t" }), Obj("O/B0_1", new[] { "t" })).Length;
        var index = Index();
        var spy = new RecordingChunks(new GameChunkReader(index.Table));
        var reader = new GameEntryReader(index, spy, new GameEntryLimits { MaxChunkBytes = bin + 10, MaxBinBytes = 10 * bin });

        Assert.Null(reader.Read(Name("O/B0_0")).Error);
        // the first bin was asked for with the whole of the budget, the second with what is left of it, which is too little for a bin: the archive says so before it reads one
        Assert.Equal(new[] { bin + 10 }, spy.Limits);
        var second = reader.Read(Name("O/B1_0"));
        Assert.Equal(new[] { bin + 10, 10 }, spy.Limits);
        Assert.Contains($"decodes to {Digits(bin)} bytes, more than the 10 the read allows", second.Error);
        // the other entry of the first bin is in the bin that is kept: it costs no bytes of the budget
        Assert.Null(reader.Read(Name("O/B0_1")).Error);
        Assert.Equal(2, spy.Limits.Count);
        Assert.Equal(1, reader.Stats.ChunkReads);
        Assert.Equal(bin, reader.Stats.ChunkBytes);
    }

    [Fact]
    public void A_reader_whose_bytes_are_spent_refuses_the_next_bin_and_says_the_budget_is_what_ended_it()
    {
        WriteBins(3);
        long bin = Bin(Obj("O/B0_0", new[] { "t" })).Length;
        var index = Index();
        var reader = new GameEntryReader(index, new GameChunkReader(index.Table), new GameEntryLimits { MaxChunkBytes = bin });

        Assert.Null(reader.Read(Name("O/B0_0")).Error);
        var refused = reader.Read(Name("O/B1_0"));

        Assert.Equal($"O/B1_0: reference budget exceeded: the bins read come to more than the {Digits(bin)} bytes a reader reads", refused.Error);
        Assert.Equal(1, reader.Stats.Refused);
    }

    [Fact]
    public void The_largest_bin_a_reader_reads_is_the_limit_it_gives_the_archive()
    {
        WriteBins(1);
        var index = Index();
        var spy = new RecordingChunks(new GameChunkReader(index.Table));

        new GameEntryReader(index, spy).Read(Name("O/B0_0"));
        new GameEntryReader(index, spy, new GameEntryLimits { MaxBinBytes = 12345 }).Read(Name("O/B0_0"));

        Assert.Equal(new[] { GameEntryLimits.DefaultMaxBinBytes, 12345L }, spy.Limits);
        Assert.Equal(64L << 20, GameEntryLimits.DefaultMaxBinBytes);
    }

    private sealed class RecordingChunks : IGameChunkReader
    {
        private readonly IGameChunkReader _inner;
        public readonly List<long> Limits = new();

        public RecordingChunks(IGameChunkReader inner) { _inner = inner; }

        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken)
        {
            Limits.Add(maxBytes);
            return _inner.ReadChunk(archive, chunk, maxBytes, cancellationToken);
        }
    }

    // ================================================================================================ the cost of the reads, in the engine's units

    [Fact]
    public void A_tally_is_charged_for_a_bin_read_and_for_the_object_decoded_and_for_nothing_that_is_already_known()
    {
        WriteBins(2, objectsEach: 2);
        var index = Index();
        var reader = new GameEntryReader(index, new GameChunkReader(index.Table));
        var tally = new GameEntryTally();

        Assert.Null(reader.Read(Name("O/B0_0"), CancellationToken.None, tally).Error);
        long fresh = tally.Units;
        Assert.True(fresh > 0);

        // the same entry again is answered from memory, and a second entry of the bin that is kept costs its own decode and not the bin
        Assert.Null(reader.Read(Name("O/B0_0"), CancellationToken.None, tally).Error);
        Assert.Equal(fresh, tally.Units);
        Assert.Null(reader.Read(Name("O/B0_1"), CancellationToken.None, tally).Error);
        long second = tally.Units - fresh;
        Assert.InRange(second, 1, fresh - 1);
        Assert.Equal(1, reader.ChunkReads);
    }

    [Fact]
    public void A_read_that_would_take_a_tally_past_its_limit_is_refused_and_one_that_is_known_is_not()
    {
        WriteBins(3);
        var index = Index();
        var reader = new GameEntryReader(index, new GameChunkReader(index.Table));
        var tally = new GameEntryTally(limit: 1);

        var first = reader.Read(Name("O/B0_0"), CancellationToken.None, tally);   // the first read is within the limit, and is what brings the tally to it
        var refused = reader.Read(Name("O/B1_0"), CancellationToken.None, tally);
        var known = reader.Read(Name("O/B0_0"), CancellationToken.None, tally);

        Assert.Null(first.Error);
        Assert.True(tally.Units >= 1);
        Assert.Equal("O/B1_0: reference budget exceeded: the applications to this chunk have read as much as the overlay allows one chunk", refused.Error);
        Assert.Null(known.Error);
        Assert.Same(first.Object, known.Object);
        // a refusal is nobody's answer: another application, with room, reads it
        Assert.Null(reader.Read(Name("O/B1_0")).Error);
    }

    [Fact]
    public void The_delegate_the_engine_takes_carries_the_tally()
    {
        WriteBins(1);
        var index = Index();
        var reader = new GameEntryReader(index, new GameChunkReader(index.Table));
        var tally = new GameEntryTally();

        ReadGameEntry read = reader.AsDelegate(CancellationToken.None, tally);

        Assert.Null(read(Name("O/B0_0")).Error);
        Assert.True(tally.Units > 0);
    }

    private sealed class ThrowingChunks : IGameChunkReader
    {
        private readonly Exception _error;

        public ThrowingChunks(Exception error) { _error = error; }

        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken) => throw _error;
    }

    [Fact]
    public void A_chunk_reader_that_throws_what_nobody_expected_is_a_failed_read_and_never_an_exception()
    {
        WriteBins(1);
        var index = Index();
        var reader = new GameEntryReader(index, new ThrowingChunks(new InvalidDataException("the archive is damaged")));

        var read = reader.Read(Name("O/B0_0"));

        Assert.Null(read.Object);
        Assert.Equal($"O/B0_0: {0x100:x16} of DATA/FINAL/A.wad.client: internal error (InvalidDataException): the archive is damaged", read.Error);
        // and it is a failure and not an answer: the next ask reads again
        Assert.Equal(read.Error, reader.Read(Name("O/B0_0")).Error);
        Assert.Equal(0, reader.Stats.Entries);
    }

    [Fact]
    public async Task A_caller_waiting_for_an_entry_another_caller_is_reading_ends_its_wait_with_its_own_token()
    {
        WriteBins(1);
        var index = Index();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var chunks = new BlockingChunks(new GameChunkReader(index.Table), entered, release);
        var reader = new GameEntryReader(index, chunks);
        var first = Task.Run(() => reader.Read(Name("O/B0_0")));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        using var cancel = new CancellationTokenSource();
        var second = Task.Run(() => reader.Read(Name("O/B0_0"), cancel.Token));
        await Task.Delay(100);
        cancel.Cancel();

        // a deadlock is a TimeoutException that fails the test, and not a test that never ends
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(10)));
        release.Set();
        var read = await first.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(read.Error);
        Assert.Equal(1, chunks.Calls);   // the one that read it kept it, and the one that gave up read nothing
    }

    private sealed class BlockingChunks : IGameChunkReader
    {
        private readonly IGameChunkReader _inner;
        private readonly ManualResetEventSlim _entered, _release;
        public int Calls;

        public BlockingChunks(IGameChunkReader inner, ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _inner = inner;
            _entered = entered;
            _release = release;
        }

        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 1)
            {
                _entered.Set();
                _release.Wait();
            }
            return _inner.ReadChunk(archive, chunk, maxBytes, cancellationToken);
        }
    }

    [Fact]
    public void Many_threads_asking_for_more_entries_than_the_budget_allows_keep_no_more_than_it_allows()
    {
        WriteBins(64);
        var index = Index();
        var reader = new GameEntryReader(index, new GameChunkReader(index.Table), new GameEntryLimits { MaxEntries = 20 });
        var failures = new List<string>();

        var workers = Enumerable.Range(0, 8).Select(w => new Thread(() =>
        {
            try { for (int i = 0; i < 64; i++) reader.Read(Name($"O/B{(i + w * 8) % 64}_0")); }
            catch (Exception e) { lock (failures) failures.Add(e.ToString()); }
        }) { IsBackground = true }).ToList();
        foreach (var worker in workers) worker.Start();
        foreach (var worker in workers) Assert.True(worker.Join(TimeSpan.FromSeconds(60)), "a worker did not finish: a deadlock");

        Assert.Empty(failures);
        Assert.Equal(20, reader.Stats.Entries);
        Assert.True(reader.Stats.Refused > 0);
    }
}

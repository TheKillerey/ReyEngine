using ReyEngine.Formats.LtkGameData;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M818: the index on disk. It is keyed by the fingerprint of the installation (name, length and modification time of every archive), served when that is what the game is now, rebuilt
/// when it is not, never trusted when it is damaged, and never written at all by a build that was cancelled. None of it needs the game.
/// </summary>
public sealed class LtkGameIndexCacheTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Game => _temp.Combine("game");

    private string Cache => _temp.Combine("cache", "index.idx");

    private string Archive(string name) => Path.Combine(Game, "DATA", "FINAL", name.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string name, params TestChunk[] chunks) => TestWad.Write(Archive(name), chunks);

    private void Install()
    {
        Write("A.wad.client", TestChunk.Compressed(0x20, Bin(Obj("O/One", new[] { "1" }), Obj("O/Two", new[] { "2" }))), TestChunk.Compressed(0x10, Bin(Obj("O/Three", new[] { "3" }))));
        Write("b.wad.client", TestChunk.Compressed(0x20, Bin(Obj("O/One", new[] { "1" }), Obj("O/Two", new[] { "2" }))), TestChunk.Compressed(0x30, Ptch(new[] { Obj("O/Four", new[] { "4" }) })));
        File.WriteAllText(Archive("M.wad.client"), "not an archive");
    }

    private static void AssertSame(GameObjectIndex expected, GameObjectIndex actual)
    {
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Table.Archives.Select(a => a.Name), actual.Table.Archives.Select(a => a.Name));
        Assert.Equal(expected.Table.Skipped, actual.Table.Skipped);
        Assert.Equal(expected.Table.ChunkCount, actual.Table.ChunkCount);
        for (int i = 0; i < expected.Table.ChunkCount; i++)
        {
            Assert.Equal(expected.Table.ChunkAt(i), actual.Table.ChunkAt(i));
            Assert.Equal(expected.Table.HoldersAt(i), actual.Table.HoldersAt(i));
        }
        Assert.Equal(expected.DeclarationCount, actual.DeclarationCount);
        for (int i = 0; i < expected.DeclarationCount; i++) Assert.Equal(expected.DeclarationAt(i), actual.DeclarationAt(i));
        Assert.Equal(expected.Objects().ToArray(), actual.Objects().ToArray());
        foreach (uint obj in expected.Objects()) Assert.Equal(expected.Declarations(obj), actual.Declarations(obj));
        // what the build counted, but not how long it took
        Assert.Equal(expected.Stats with { Elapsed = TimeSpan.Zero }, actual.Stats with { Elapsed = TimeSpan.Zero });
    }

    // ================================================================================================ the round trip

    [Fact]
    public void A_cache_serves_the_index_it_was_written_from_row_for_row()
    {
        Install();

        var built = GameObjectIndexCache.LoadOrBuild(Game, Cache);
        var served = GameObjectIndexCache.LoadOrBuild(Game, Cache);

        Assert.False(built.FromCache);
        Assert.Equal("no cache", built.CacheNote);
        Assert.True(built.CacheBytes > 0);
        Assert.True(File.Exists(Cache));
        Assert.True(served.FromCache);
        Assert.Null(served.CacheNote);
        Assert.Equal(built.CacheBytes, served.CacheBytes);
        AssertSame(built.Value, served.Value);
        // the archive that did not mount is kept, with its id
        Assert.Equal(1, Assert.Single(served.Value.Table.Skipped).Archive);
    }

    [Fact]
    public void The_cache_is_the_chunk_table_and_the_declarations_and_nothing_else()
    {
        Install();
        long size = GameObjectIndexCache.LoadOrBuild(Game, Cache).CacheBytes;

        // 3 chunks (0x10, 0x20, 0x30) with 4 holders, 4 declarations of 18 bytes and a header: far below a kilobyte of anything but the columns
        Assert.InRange(size, 200, 1200);
    }

    [Fact]
    public void Nothing_of_a_cache_is_trusted_for_another_state_of_the_game()
    {
        Install();
        var first = GameObjectIndexCache.LoadOrBuild(Game, Cache);

        // one more byte in one archive: another length, so another fingerprint, whatever the modification time is made to say
        var written = File.GetLastWriteTimeUtc(Archive("A.wad.client"));
        using (var stream = new FileStream(Archive("A.wad.client"), FileMode.Append)) stream.WriteByte(0);
        File.SetLastWriteTimeUtc(Archive("A.wad.client"), written);
        var second = GameObjectIndexCache.LoadOrBuild(Game, Cache);

        Assert.False(second.FromCache);
        Assert.StartsWith("stale: built for fingerprint ", second.CacheNote);
        Assert.NotEqual(first.Value.Fingerprint, second.Value.Fingerprint);
        // and the cache was replaced: the next call is served, with the new fingerprint
        var third = GameObjectIndexCache.LoadOrBuild(Game, Cache);
        Assert.True(third.FromCache);
        Assert.Equal(second.Value.Fingerprint, third.Value.Fingerprint);
    }

    [Fact]
    public void A_modification_time_alone_is_another_game()
    {
        Install();
        GameObjectIndexCache.LoadOrBuild(Game, Cache);

        File.SetLastWriteTimeUtc(Archive("b.wad.client"), File.GetLastWriteTimeUtc(Archive("b.wad.client")).AddSeconds(3));

        Assert.False(GameObjectIndexCache.LoadOrBuild(Game, Cache).FromCache);
    }

    [Fact]
    public void A_new_declaration_in_a_changed_game_is_in_the_rebuilt_index()
    {
        Install();
        var before = GameObjectIndexCache.LoadOrBuild(Game, Cache).Value;
        Assert.False(before.Declares(H("O/Brand")));

        Write("Z.wad.client", TestChunk.Compressed(0x99, Bin(Obj("O/Brand", new[] { "b" }))));
        var after = GameObjectIndexCache.LoadOrBuild(Game, Cache).Value;

        Assert.True(after.Declares(H("O/Brand")));
        Assert.Equal(4, after.Table.Archives.Count);
    }

    // ================================================================================================ a cache that is not good

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("table-checksum")]
    [InlineData("objects-checksum")]
    [InlineData("truncated")]
    [InlineData("empty")]
    [InlineData("garbage")]
    public void A_damaged_cache_is_an_absent_cache_and_is_replaced(string damage)
    {
        Install();
        var good = GameObjectIndexCache.LoadOrBuild(Game, Cache);
        byte[] bytes = File.ReadAllBytes(Cache);
        switch (damage)
        {
            case "magic": bytes[0] ^= 0xff; break;
            case "version": bytes[4] = 99; break;
            case "table-checksum": bytes[56 + 4] ^= 0x10; break;                 // a byte of the table section
            case "objects-checksum": bytes[^3] ^= 0x10; break;                   // a byte of the declarations
            case "truncated": bytes = bytes[..(bytes.Length / 2)]; break;
            case "empty": bytes = Array.Empty<byte>(); break;
            default: bytes = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray(); break;
        }
        File.WriteAllBytes(Cache, bytes);

        var rebuilt = GameObjectIndexCache.LoadOrBuild(Game, Cache);

        Assert.False(rebuilt.FromCache);
        Assert.NotNull(rebuilt.CacheNote);
        AssertSame(good.Value, rebuilt.Value);
        Assert.True(GameObjectIndexCache.LoadOrBuild(Game, Cache).FromCache);
    }

    [Fact]
    public void A_cache_that_cannot_be_written_is_a_note_and_not_a_failure()
    {
        Install();
        string blocked = _temp.Combine("blocked");
        File.WriteAllText(blocked, "a file where the cache folder would be");

        var result = GameObjectIndexCache.LoadOrBuild(Game, Path.Combine(blocked, "index.idx"));

        Assert.False(result.FromCache);
        Assert.Contains("the cache was not written", result.CacheNote);
        Assert.Equal(0, result.CacheBytes);
        Assert.True(result.Value.Declares(H("O/One")));
    }

    [Fact]
    public void A_write_leaves_no_partial_file_beside_the_cache()
    {
        Install();

        GameObjectIndexCache.LoadOrBuild(Game, Cache);

        Assert.Equal(new[] { "index.idx" }, Directory.GetFiles(Path.GetDirectoryName(Cache)!).Select(Path.GetFileName).ToArray());
    }

    // ================================================================================================ the table alone, and the declarations added to it

    [Fact]
    public void The_chunk_table_is_served_without_the_declarations_and_the_declarations_are_added_when_asked_for()
    {
        Install();

        var table = GameObjectIndexCache.LoadOrBuildTable(Game, Cache);
        Assert.False(table.FromCache);
        long tableOnly = table.CacheBytes;
        Assert.True(GameObjectIndexCache.LoadOrBuildTable(Game, Cache).FromCache);

        var archives = GameArchiveList.Enumerate(Game);
        Assert.False(GameObjectIndexCache.TryLoadObjects(Cache, table.Value, out var none, out string why));
        Assert.Null(none);
        Assert.Equal("no declarations in the cache", why);

        var objects = GameObjectIndexCache.LoadOrBuildObjects(table.Value, Cache);
        Assert.False(objects.FromCache);
        Assert.True(objects.CacheBytes > tableOnly);
        Assert.True(GameObjectIndexCache.LoadOrBuildObjects(table.Value, Cache).FromCache);
        Assert.True(GameObjectIndexCache.LoadOrBuildTable(Game, Cache).FromCache);
        Assert.Equal(table.Value.Archives.Select(a => a.Name), objects.Value.Table.Archives.Select(a => a.Name));
        Assert.Equal(archives.Count, objects.Value.Table.Archives.Count);
    }

    [Fact]
    public void A_table_cache_of_one_state_is_not_served_for_another()
    {
        Install();
        var table = GameObjectIndexCache.LoadOrBuildTable(Game, Cache);
        File.SetLastWriteTimeUtc(Archive("A.wad.client"), File.GetLastWriteTimeUtc(Archive("A.wad.client")).AddSeconds(1));

        var archives = GameArchiveList.Enumerate(Game);
        bool served = GameObjectIndexCache.TryLoadTable(Cache, Game, archives, GameArchiveList.Fingerprint(archives), out var again, out string why);

        Assert.False(served);
        Assert.Null(again);
        Assert.StartsWith("stale", why);
        Assert.NotEqual(table.Value.Fingerprint, GameArchiveList.Fingerprint(archives));
        // and declarations cannot be loaded for a table of another state either
        Assert.False(GameObjectIndexCache.TryLoadObjects(Cache, GameObjectIndexBuilder.BuildTable(Game, archives), out _, out string objectsWhy));
        Assert.StartsWith("stale", objectsWhy);
    }

    [Fact]
    public void Declarations_of_one_state_are_not_saved_beside_the_table_of_another()
    {
        Install();
        var oldIndex = GameObjectIndexBuilder.Build(Game);
        File.SetLastWriteTimeUtc(Archive("A.wad.client"), File.GetLastWriteTimeUtc(Archive("A.wad.client")).AddSeconds(1));
        var newTable = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));

        Assert.Throws<ArgumentException>(() => GameObjectIndexCache.Save(newTable, oldIndex, Cache));
    }

    // ================================================================================================ cancelled builds write nothing

    [Fact]
    public void A_build_cancelled_before_its_table_is_done_writes_no_cache()
    {
        for (int i = 0; i < 4; i++) Write($"A{i}.wad.client", TestChunk.Compressed((ulong)i + 1, Bin(Obj($"O/A{i}", new[] { "a" }))));
        using var cancel = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() =>
            GameObjectIndexCache.LoadOrBuild(Game, Cache, new GameObjectIndexOptions { Workers = 1 }, new CancelOn(cancel, GameIndexStage.Mounting), cancel.Token));

        Assert.False(File.Exists(Cache));
    }

    [Fact]
    public void A_build_cancelled_while_it_reads_bins_leaves_no_declarations_in_the_cache()
    {
        for (int i = 0; i < 4; i++) Write($"A{i}.wad.client", TestChunk.Compressed((ulong)i + 1, Bin(Obj($"O/A{i}", new[] { "a" }))));
        using var cancel = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() =>
            GameObjectIndexCache.LoadOrBuild(Game, Cache, new GameObjectIndexOptions { Workers = 1 }, new CancelOn(cancel, GameIndexStage.ReadingBins), cancel.Token));

        // the table was whole and is kept; the declarations were not, and are not there
        var table = GameObjectIndexCache.LoadOrBuildTable(Game, Cache);
        Assert.True(table.FromCache);
        Assert.False(GameObjectIndexCache.TryLoadObjects(Cache, table.Value, out _, out string why));
        Assert.Equal("no declarations in the cache", why);
    }

    private sealed class CancelOn : IProgress<GameIndexProgress>
    {
        private readonly CancellationTokenSource _source;
        private readonly GameIndexStage _stage;

        public CancelOn(CancellationTokenSource source, GameIndexStage stage) { _source = source; _stage = stage; }

        public void Report(GameIndexProgress value)
        {
            if (value.Stage == _stage) _source.Cancel();
        }
    }

    // ================================================================================================ off the calling thread

    [Fact]
    public async Task The_async_forms_build_and_serve_on_a_thread_of_their_own_and_a_cancelled_one_ends_as_cancelled()
    {
        Install();
        int caller = System.Environment.CurrentManagedThreadId;
        int worker = -1;
        var progress = new Progress(p => worker = System.Environment.CurrentManagedThreadId);

        var built = await GameObjectIndexCache.LoadOrBuildAsync(Game, Cache, progress: progress);
        var served = await GameObjectIndexCache.LoadOrBuildAsync(Game, Cache);
        var table = await GameObjectIndexCache.LoadOrBuildTableAsync(Game, Cache);

        Assert.False(built.FromCache);
        Assert.True(served.FromCache);
        Assert.True(table.FromCache);
        Assert.NotEqual(-1, worker);
        Assert.NotEqual(caller, worker);   // the build reported from a thread that was not the caller's

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameObjectIndexCache.LoadOrBuildAsync(Game, Cache, cancellationToken: cancel.Token));
    }

    private sealed class Progress : IProgress<GameIndexProgress>
    {
        private readonly Action<GameIndexProgress> _report;

        public Progress(Action<GameIndexProgress> report) { _report = report; }

        public void Report(GameIndexProgress value) => _report(value);
    }

    // ================================================================================================ where it lives

    [Fact]
    public void The_default_cache_is_a_file_of_ReyEngines_cache_folder_of_the_user_and_one_for_each_game_directory()
    {
        string local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        string one = GameObjectIndexCache.DefaultPath(@"C:\Riot Games\League of Legends\Game");

        Assert.StartsWith(Path.Combine(local, "ReyEngine", "cache") + Path.DirectorySeparatorChar, one);
        Assert.EndsWith(".idx", one);
        // the same directory spelled another way is the same file; another directory is another
        Assert.Equal(one, GameObjectIndexCache.DefaultPath(@"c:\riot games\league of legends\game\"));
        Assert.NotEqual(one, GameObjectIndexCache.DefaultPath(@"C:\Riot Games\League of Legends PBE\Game"));
        // never the settings
        Assert.DoesNotContain("settings", Path.GetFileName(one), StringComparison.OrdinalIgnoreCase);
    }
}

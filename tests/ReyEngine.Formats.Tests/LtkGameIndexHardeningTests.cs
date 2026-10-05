using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using ReyEngine.Formats.LtkGameData;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M818 review fixes, the index and its cache. What the cache keeps is keyed by the fingerprint of an installation (names, lengths, times), which does not change when a READ does: so a build in
/// which a file could not be opened must not be kept, and must say so; one failure must not end the build; the archives are the files and not the links; a cache is read only if it is sane. These
/// tests make the failures that real machines make (a file a scanner holds, a byte range another process has locked), on real files.
/// </summary>
public sealed class LtkGameIndexHardeningTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Game => _temp.Combine("game");

    private string Final => Path.Combine(Game, "DATA", "FINAL");

    private string Cache => _temp.Combine("cache", "index.idx");

    private string Archive(string name) => Path.Combine(Final, name.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string name, params TestChunk[] chunks) => TestWad.Write(Archive(name), chunks);

    private static byte[] BinOf(string path, params string[] tags) => Bin(Obj(path, tags));

    /// <summary>Two archives that mount: A (a bin of one object, and a bin of two that B also holds) and B.</summary>
    private void Install()
    {
        Write("A.wad.client", TestChunk.Compressed(0x20, Bin(Obj("O/Shared1", new[] { "1" }), Obj("O/Shared2", new[] { "2" }))), TestChunk.Compressed(0x10, BinOf("O/FromA", "a")));
        Write("b.wad.client", TestChunk.Compressed(0x20, Bin(Obj("O/Shared1", new[] { "1" }), Obj("O/Shared2", new[] { "2" }))), TestChunk.Compressed(0x30, BinOf("O/FromB", "b")));
    }

    /// <summary>Holds an archive so that no other handle can open it, as a virus scanner or the game's patcher may at the moment a build looks at it.</summary>
    private FileStream Hold(string name) => new(Archive(name), FileMode.Open, FileAccess.Read, FileShare.None);

    private sealed class SyncProgress : IProgress<GameIndexProgress>
    {
        public Action<GameIndexProgress>? OnReport;

        public void Report(GameIndexProgress value) => OnReport?.Invoke(value);
    }

    // ================================================================================================ a failure of the moment is not kept

    [Fact]
    public void An_archive_another_process_holds_is_skipped_for_a_reason_of_the_moment_and_the_table_is_not_settled()
    {
        Install();

        using (Hold("b.wad.client"))
        {
            var table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));

            var skipped = Assert.Single(table.Skipped);
            Assert.Equal(1, skipped.Archive);
            Assert.True(skipped.Transient);
            Assert.StartsWith("cannot open the archive: ", skipped.Error);
            Assert.False(table.IsSettled);
            Assert.StartsWith("DATA/FINAL/b.wad.client: cannot open the archive: ", Assert.Single(table.Unsettled));
            Assert.Empty(table.Holders(0x30));   // what only that archive holds is not in this build's table
        }

        var again = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));
        Assert.True(again.IsSettled);
        Assert.Empty(again.Skipped);
        Assert.Equal(new[] { 1 }, again.Holders(0x30));
    }

    [Fact]
    public void Bytes_that_are_not_an_archive_are_settled_and_a_bin_that_does_not_decode_is_settled()
    {
        Write("A.wad.client", TestChunk.Compressed(0x01, BinOf("O/Fine", "f")), TestChunk.Compressed(0x02, "PROP"u8.ToArray().Concat(new byte[] { 0xff, 0xff, 0xff, 0xff, 1, 2, 3, 4 }).ToArray()));
        File.WriteAllText(Archive("M.wad.client"), "this is not an archive");

        var index = GameObjectIndexBuilder.Build(Game);

        // both are the same on every build of this installation, so what the build made of them may be kept
        Assert.False(Assert.Single(index.Table.Skipped).Transient);
        Assert.Equal(1, index.Stats.SkippedChunks);
        Assert.Empty(index.Unsettled);
        Assert.True(index.IsSettled);
    }

    [Fact]
    public void An_archive_that_opens_for_the_table_and_not_for_its_bins_loses_its_chunks_to_the_build_and_the_index_is_unsettled()
    {
        Install();
        var table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));

        using (Hold("A.wad.client"))
        {
            var index = GameObjectIndexBuilder.BuildObjects(table);

            Assert.False(index.IsSettled);
            var note = Assert.Single(index.Unsettled);
            Assert.StartsWith("DATA/FINAL/A.wad.client: cannot open the archive: ", note);
            Assert.False(index.Declares(H("O/FromA")));
            Assert.True(index.Declares(H("O/FromB")));
            // the chunks A holds first (0x10 and 0x20) were not read, and the build counts them
            Assert.Equal(2, index.Stats.SkippedChunks);
        }

        var healed = GameObjectIndexBuilder.BuildObjects(table);
        Assert.True(healed.IsSettled);
        Assert.True(healed.Declares(H("O/FromA")));
    }

    [Fact]
    public void A_chunk_whose_bytes_another_process_has_locked_is_skipped_and_noted_and_the_rest_of_the_archive_indexes()
    {
        // a byte-range lock stops another handle's reads where the file system enforces it, which is Windows
        if (!OperatingSystem.IsWindows()) return;
        var first = TestChunk.Stored_(0x01, BinOf("O/First", "1"));
        var second = TestChunk.Stored_(0x02, BinOf("O/Second", "2"));
        var third = TestChunk.Stored_(0x03, BinOf("O/Third", "3"));
        Write("A.wad.client", first, second, third);
        long secondAt = 272 + 3 * 32 + first.Stored.Length;
        var table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));

        using (var holder = new FileStream(Archive("A.wad.client"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            holder.Lock(secondAt, second.Stored.Length);
            var index = GameObjectIndexBuilder.BuildObjects(table);
            holder.Unlock(secondAt, second.Stored.Length);

            Assert.False(index.IsSettled);
            var note = Assert.Single(index.Unsettled);
            Assert.StartsWith($"DATA/FINAL/A.wad.client: chunk {0x02:x16}: ", note);
            Assert.True(index.Declares(H("O/First")));
            Assert.False(index.Declares(H("O/Second")));
            Assert.True(index.Declares(H("O/Third")));   // one chunk does not end the archive, and one archive does not end the build
        }

        var healed = GameObjectIndexBuilder.BuildObjects(table);
        Assert.True(healed.IsSettled);
        Assert.True(healed.Declares(H("O/Second")));
    }

    [Fact]
    public void An_index_built_while_an_archive_is_held_is_returned_and_never_written_and_says_why()
    {
        Install();

        using (Hold("b.wad.client"))
        {
            var result = GameObjectIndexCache.LoadOrBuild(Game, Cache);

            Assert.False(result.FromCache);
            Assert.Equal(0, result.CacheBytes);
            Assert.False(File.Exists(Cache));
            Assert.Contains("a read failed for a reason of the moment", result.NotWritten);
            Assert.Contains("DATA/FINAL/b.wad.client: cannot open the archive", result.NotWritten);
            Assert.False(result.Value.IsSettled);
            Assert.True(result.Value.Declares(H("O/FromA")));
            Assert.False(result.Value.Declares(H("O/FromB")));
        }

        // nothing was kept, so the next session builds the whole installation, and keeps it
        var healed = GameObjectIndexCache.LoadOrBuild(Game, Cache);
        Assert.False(healed.FromCache);
        Assert.Null(healed.NotWritten);
        Assert.True(healed.Value.IsSettled);
        Assert.True(healed.Value.Declares(H("O/FromB")));
        Assert.True(GameObjectIndexCache.LoadOrBuild(Game, Cache).FromCache);
    }

    [Fact]
    public void A_table_built_while_an_archive_is_held_is_not_written_and_the_declarations_built_from_it_are_not_either()
    {
        Install();
        using var hold = Hold("b.wad.client");

        var table = GameObjectIndexCache.LoadOrBuildTable(Game, Cache);
        var objects = GameObjectIndexCache.LoadOrBuildObjects(table.Value, Cache);

        Assert.NotNull(table.NotWritten);
        Assert.NotNull(objects.NotWritten);
        Assert.False(File.Exists(Cache));
    }

    [Fact]
    public void An_unsettled_index_cannot_be_saved_by_a_direct_call_either()
    {
        Install();
        GameChunkTable table;
        using (Hold("b.wad.client")) table = GameObjectIndexBuilder.BuildTable(Game, GameArchiveList.Enumerate(Game));

        var error = Assert.Throws<ArgumentException>(() => GameObjectIndexCache.Save(table, null, Cache));

        Assert.StartsWith("the index is not settled: ", error.Message);
        Assert.False(File.Exists(Cache));
    }

    [Fact]
    public void An_installation_that_changed_while_the_index_was_built_is_returned_and_not_written()
    {
        Install();
        var progress = new SyncProgress
        {
            // an archive written while the bins are being read: what was built is the installation of a moment ago
            OnReport = p => { if (p.Stage == GameIndexStage.ReadingBins && p.Done == 1) File.SetLastWriteTimeUtc(Archive("A.wad.client"), File.GetLastWriteTimeUtc(Archive("A.wad.client")).AddSeconds(30)); },
        };

        var result = GameObjectIndexCache.LoadOrBuild(Game, Cache, new GameObjectIndexOptions { Workers = 1 }, progress);

        Assert.Contains("the installation changed while the index was being built", result.NotWritten);
        Assert.True(result.Value.Declares(H("O/FromA")));
        // the table was written before the bins were read, for the installation of that moment; the declarations were not
        Assert.False(GameObjectIndexCache.TryLoadObjects(Cache, result.Value.Table, out _, out string why));
        Assert.True(why is "no declarations in the cache" || why.StartsWith("stale"), why);
        // and the size says what is on disk: the table-only file, which is a file, and not zero
        Assert.True(result.CacheBytes > 0);
        Assert.Equal(new FileInfo(Cache).Length, result.CacheBytes);
    }

    [Fact]
    public void A_declarations_save_that_is_refused_reports_the_table_only_file_that_is_there()
    {
        Install();
        var table = GameObjectIndexCache.LoadOrBuildTable(Game, Cache);
        Assert.True(table.CacheBytes > 0);

        using (Hold("A.wad.client"))
        {
            // the table is settled and saved; the declarations are built while an archive cannot be opened, and so are not
            var objects = GameObjectIndexCache.LoadOrBuildObjects(table.Value, Cache);

            Assert.NotNull(objects.NotWritten);
            Assert.False(objects.Value.IsSettled);
            Assert.Equal(table.CacheBytes, objects.CacheBytes);
            Assert.Equal(new FileInfo(Cache).Length, objects.CacheBytes);
        }
    }

    [Fact]
    public void A_save_that_fails_reports_the_file_that_is_there_and_zero_where_there_is_none()
    {
        Install();
        var table = GameObjectIndexCache.LoadOrBuildTable(Game, Cache);
        long tableOnly = table.CacheBytes;
        Assert.True(tableOnly > 0);

        // the cache is held, so that the new one cannot be moved over it: the declarations are built and returned, and the file is the table's
        using (new FileStream(Cache, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var objects = GameObjectIndexCache.LoadOrBuildObjects(table.Value, Cache);

            Assert.Contains("the cache was not written", objects.CacheNote);
            Assert.True(objects.Value.Declares(H("O/FromA")));
            Assert.Equal(tableOnly, objects.CacheBytes);
        }

        // where there is no file there is no size
        string blocked = _temp.Combine("blocked");
        File.WriteAllText(blocked, "a file where the cache folder would be");
        Assert.Equal(0, GameObjectIndexCache.LoadOrBuildObjects(table.Value, Path.Combine(blocked, "index.idx")).CacheBytes);
    }

    // ================================================================================================ the archives are the files

    [Fact]
    public void An_installation_with_no_archive_is_a_refusal_and_not_an_empty_index()
    {
        Directory.CreateDirectory(Final);
        File.WriteAllText(Path.Combine(Final, "notes.txt"), "not an archive");

        var error = Assert.Throws<GameIndexException>(() => GameArchiveList.Enumerate(Game));
        Assert.Contains("holds no .wad.client archive", error.Message);
        Assert.Throws<GameIndexException>(() => GameObjectIndexCache.LoadOrBuild(Game, Cache));
        Assert.False(File.Exists(Cache));
        Assert.Throws<GameIndexException>(() => GameObjectIndexBuilder.BuildTable(Game, Array.Empty<GameArchive>()));
    }

    [Fact]
    public void A_junction_is_not_followed_and_the_archives_in_real_folders_are_listed()
    {
        // a junction needs no privilege on NTFS; where one cannot be made the rule has nothing to say
        if (!OperatingSystem.IsWindows()) return;
        Write("Real/Inner.wad.client", TestChunk.Compressed(0x01, BinOf("O/A", "a")));
        string outside = _temp.Combine("outside");
        TestWad.Write(Path.Combine(outside, "Hidden.wad.client"), new[] { TestChunk.Compressed(0x02, BinOf("O/B", "b")) });
        if (!TryJunction(Path.Combine(Final, "Linked"), outside)) return;

        var names = GameArchiveList.Enumerate(Game).Select(a => a.Name).ToArray();

        Assert.Equal(new[] { "Real/Inner.wad.client" }, names);
    }

    private static bool TryJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    // ================================================================================================ no handle outlives its read

    [Fact]
    public void A_build_leaves_every_archive_replaceable_and_deletable_during_its_progress_callbacks_and_after_it()
    {
        // the largest archive is read first and for longest (tens of thousands of bins, a third of a second), on a worker of its own; the small one finishes while it is still open, and reports from its own thread
        const int bins = 30_000;
        Write("A_big.wad.client", Enumerable.Range(0, bins).Select(i => TestChunk.Stored_(0x1000 + (ulong)i, BinOf($"O/Big{i}", "b"))).ToArray());
        Write("B_small.wad.client", TestChunk.Compressed(0x01, BinOf("O/Small", "s")));
        var archives = new[] { Archive("A_big.wad.client"), Archive("B_small.wad.client") };
        var gate = new object();
        int renames = 0;
        var progress = new SyncProgress
        {
            OnReport = p =>
            {
                if (p.Stage != GameIndexStage.ReadingBins) return;
                Thread.Sleep(20);   // by now the big archive is open on its own worker, with most of its chunks to go
                lock (gate)
                {
                    // once: how many reports a build makes depends on how its workers are scheduled, which a loaded
                    // machine changes, and only the first report is sure to come while the big archive is still open
                    if (renames > 0) return;
                    // a handle opened without FileShare.Delete would refuse this; the file goes away and comes back, as a patcher replacing it would
                    foreach (string archive in archives)
                    {
                        File.Move(archive, archive + ".away");
                        File.Move(archive + ".away", archive);
                        renames++;
                    }
                }
            },
        };

        var index = GameObjectIndexBuilder.Build(Game, new GameObjectIndexOptions { Workers = 2 }, progress);

        Assert.Equal(archives.Length, renames);
        // the read went on through the handle of the file that was renamed under it
        Assert.Equal(bins + 1, index.DeclarationCount);
        Assert.True(index.IsSettled);
        // and after the build nothing is open
        foreach (string archive in archives) File.Delete(archive);
        Assert.All(archives, archive => Assert.False(File.Exists(archive)));
    }

    [Fact]
    public void A_table_and_an_object_index_that_were_loaded_leave_the_archives_deletable()
    {
        Install();
        var load = GameObjectIndexCache.LoadOrBuild(Game, Cache);
        var served = GameObjectIndexCache.LoadOrBuild(Game, Cache);

        foreach (string name in new[] { "A.wad.client", "b.wad.client" }) File.Delete(Archive(name));

        Assert.False(File.Exists(Archive("A.wad.client")));
        Assert.True(served.FromCache);
        Assert.NotNull(load.Value);
    }

    // ================================================================================================ a cache is read only if it is sane

    private static byte[] Forge(byte[] cache, Func<byte[], byte[]>? table = null, Func<byte[], byte[]>? objects = null)
    {
        int tableLength = (int)BinaryPrimitives.ReadUInt64LittleEndian(cache.AsSpan(24));
        int objectsLength = (int)BinaryPrimitives.ReadUInt64LittleEndian(cache.AsSpan(40));
        byte[] t = cache.AsSpan(56, tableLength).ToArray();
        byte[] o = cache.AsSpan(56 + tableLength, objectsLength).ToArray();
        if (table is not null) t = table(t);
        if (objects is not null) o = objects(o);
        var header = cache.AsSpan(0, 56).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), (ulong)t.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), XxHash3.HashToUInt64(t));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40), (ulong)o.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48), XxHash3.HashToUInt64(o));
        return header.Concat(t).Concat(o).ToArray();
    }

    private static byte[] With(byte[] body, int at, int value)
    {
        var copy = (byte[])body.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(at), value);
        return copy;
    }

    private static byte[] WithShort(byte[] body, int at, ushort value)
    {
        var copy = (byte[])body.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(at), value);
        return copy;
    }

    /// <summary>The offsets of the columns of a table section of the installation <see cref="Install"/> makes: 2 archives, none skipped, 3 chunks, 4 holders.</summary>
    private const int TableChunkCount = 8, TableHashes = 12, TableHolderCounts = 12 + 3 * 8, TableHolderTotal = 12 + 3 * 8 + 3 * 2, TableHolders = TableHolderTotal + 4;

    /// <summary>The offsets of an object section: the stats are 36 bytes, then the count and the columns of the declarations.</summary>
    private const int ObjectsDeclared = 8, ObjectsCount = 36, ObjectsObjects = 40;

    [Fact]
    public void A_forged_file_with_a_valid_checksum_is_the_baseline_a_cache_that_loads()
    {
        Install();
        GameObjectIndexCache.LoadOrBuild(Game, Cache);
        byte[] forged = Forge(File.ReadAllBytes(Cache), t => t, o => o);
        File.WriteAllBytes(Cache, forged);

        Assert.True(GameObjectIndexCache.LoadOrBuild(Game, Cache).FromCache);
    }

    [Theory]
    [InlineData("archive count")]
    [InlineData("skipped count huge")]
    [InlineData("skipped count negative")]
    [InlineData("chunk count huge")]
    [InlineData("chunk count negative")]
    [InlineData("holder total too large")]
    [InlineData("holder total does not add up")]
    [InlineData("hashes out of order")]
    [InlineData("a holder that is not listed")]
    [InlineData("a chunk nobody holds")]
    [InlineData("bytes after the columns")]
    [InlineData("table cut short")]
    public void A_table_whose_counts_are_forged_under_a_valid_checksum_is_refused_and_replaced(string forgery)
    {
        Install();
        var built = GameObjectIndexCache.LoadOrBuild(Game, Cache);
        byte[] bytes = File.ReadAllBytes(Cache);
        Func<byte[], byte[]> table = forgery switch
        {
            "archive count" => t => With(t, 0, 3),
            "skipped count huge" => t => With(t, 4, int.MaxValue),
            "skipped count negative" => t => With(t, 4, -1),
            "chunk count huge" => t => With(t, TableChunkCount, int.MaxValue),
            "chunk count negative" => t => With(t, TableChunkCount, -5),
            "holder total too large" => t => With(t, TableHolderTotal, 1_000_000),
            "holder total does not add up" => t => With(t, TableHolderTotal, 3),
            "hashes out of order" => t =>
            {
                var copy = (byte[])t.Clone();
                copy.AsSpan(TableHashes, 8).CopyTo(copy.AsSpan(TableHashes + 8, 8));   // the second hash equals the first
                return copy;
            },
            "a holder that is not listed" => t => WithShort(t, TableHolders, 9),
            "a chunk nobody holds" => t => WithShort(t, TableHolderCounts, 0),
            "bytes after the columns" => t => t.Concat(new byte[] { 1 }).ToArray(),
            _ => t => t[..^3],
        };
        File.WriteAllBytes(Cache, Forge(bytes, table: table));
        var archives = GameArchiveList.Enumerate(Game);

        bool served = GameObjectIndexCache.TryLoadTable(Cache, Game, archives, built.Value.Fingerprint, out var loaded, out string why);

        Assert.False(served);
        Assert.Null(loaded);
        Assert.StartsWith("unreadable: ", why);
        var replaced = GameObjectIndexCache.LoadOrBuild(Game, Cache);
        Assert.False(replaced.FromCache);
        Assert.True(GameObjectIndexCache.LoadOrBuild(Game, Cache).FromCache);
    }

    [Theory]
    [InlineData("declaration count huge")]
    [InlineData("declaration count negative")]
    [InlineData("stats disagree with the count")]
    [InlineData("negative stats")]
    [InlineData("a declaring archive that is not listed")]
    [InlineData("a chunk its archive does not hold first")]
    [InlineData("a chunk nobody holds")]
    [InlineData("bytes after the columns")]
    [InlineData("columns cut short")]
    public void Declarations_whose_counts_are_forged_under_a_valid_checksum_are_refused_and_replaced(string forgery)
    {
        Install();
        var built = GameObjectIndexCache.LoadOrBuild(Game, Cache);
        int declarations = built.Value.DeclarationCount;
        Assert.Equal(4, declarations);   // O/FromA and the two objects of the shared bin, which A holds first, and O/FromB
        byte[] bytes = File.ReadAllBytes(Cache);
        int declaringAt = ObjectsObjects + declarations * (4 + 4 + 8);
        Func<byte[], byte[]> objects = forgery switch
        {
            "declaration count huge" => o => With(o, ObjectsCount, int.MaxValue),
            "declaration count negative" => o => With(o, ObjectsCount, -1),
            "stats disagree with the count" => o => With(o, ObjectsDeclared, declarations + 1),
            "negative stats" => o => With(o, 0, -4),
            "a declaring archive that is not listed" => o => WithShort(o, declaringAt, 9),
            "a chunk its archive does not hold first" => o => WithShort(o, declaringAt, 1),   // archive 1 holds 0x10 not at all
            "a chunk nobody holds" => o =>
            {
                var copy = (byte[])o.Clone();
                BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(ObjectsObjects + declarations * 8), 0xDEADBEEF);   // the first declaration's chunk is one no archive holds
                return copy;
            },
            "bytes after the columns" => o => o.Concat(new byte[] { 7 }).ToArray(),
            _ => o => o[..^5],
        };
        File.WriteAllBytes(Cache, Forge(bytes, objects: objects));

        bool served = GameObjectIndexCache.TryLoadObjects(Cache, built.Value.Table, out var loaded, out string why);

        Assert.False(served);
        Assert.Null(loaded);
        Assert.StartsWith("unreadable: ", why);
        var replaced = GameObjectIndexCache.LoadOrBuild(Game, Cache);
        Assert.False(replaced.FromCache);
        Assert.Equal(declarations, replaced.Value.DeclarationCount);
        Assert.True(GameObjectIndexCache.LoadOrBuild(Game, Cache).FromCache);
    }

    [Fact]
    public void A_file_larger_than_any_cache_is_not_read_into_memory_and_is_replaced_by_a_real_one()
    {
        Install();
        Directory.CreateDirectory(Path.GetDirectoryName(Cache)!);
        // the length is set and nothing is written: the file system makes the rest zeros when they are asked for, and they are not
        using (var big = new FileStream(Cache, FileMode.Create, FileAccess.Write)) big.SetLength(GameObjectIndexCache.MaxCacheBytes + 1);
        var archives = GameArchiveList.Enumerate(Game);

        bool served = GameObjectIndexCache.TryLoadTable(Cache, Game, archives, GameArchiveList.Fingerprint(archives), out _, out string why);

        Assert.False(served);
        Assert.StartsWith("unreadable: larger than the ", why);
        var replaced = GameObjectIndexCache.LoadOrBuild(Game, Cache);
        Assert.False(replaced.FromCache);
        Assert.True(new FileInfo(Cache).Length < 10_000);
        Assert.True(GameObjectIndexCache.LoadOrBuild(Game, Cache).FromCache);
    }

    [Fact]
    public void A_save_removes_the_temporary_files_an_earlier_save_died_leaving_and_only_its_own_and_only_old_ones()
    {
        Install();
        string directory = Path.GetDirectoryName(Cache)!;
        Directory.CreateDirectory(directory);
        string old = Path.Combine(directory, ".index.idx.4242.1.tmp");
        string fresh = Path.Combine(directory, ".index.idx.4243.2.tmp");
        string other = Path.Combine(directory, ".another.idx.4244.3.tmp");
        foreach (string file in new[] { old, fresh, other }) File.WriteAllText(file, "half a cache");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-1));

        GameObjectIndexCache.LoadOrBuild(Game, Cache);

        Assert.False(File.Exists(old));       // an orphan of this cache, long dead
        Assert.True(File.Exists(fresh));      // a save in another process may be writing it this moment
        Assert.True(File.Exists(other));      // another cache's
        Assert.True(File.Exists(Cache));
    }
}

using System.Diagnostics;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>Where a build is: <see cref="Stage"/> names the step, and the counts say how far the step has come.</summary>
/// <param name="Stage">The step.</param>
/// <param name="Done">Archives finished in this step.</param>
/// <param name="Total">Archives the step covers.</param>
/// <param name="Bins">Bins found so far (during <see cref="GameIndexStage.ReadingBins"/>).</param>
/// <param name="Declarations">Declarations read so far (during <see cref="GameIndexStage.ReadingBins"/>).</param>
public readonly record struct GameIndexProgress(GameIndexStage Stage, int Done, int Total, int Bins, int Declarations);

public enum GameIndexStage
{
    /// <summary>Listing the archives of <c>DATA/FINAL</c> and fingerprinting them.</summary>
    Enumerating,
    /// <summary>Reading the cache of an earlier build.</summary>
    LoadingCache,
    /// <summary>Reading each archive's table of contents, and working out which archive holds each chunk first.</summary>
    Mounting,
    /// <summary>Sniffing every chunk for a bin's magic, and reading the bins for the objects they declare.</summary>
    ReadingBins,
    /// <summary>Writing the cache.</summary>
    SavingCache,
}

/// <summary>How an index build runs.</summary>
public sealed class GameObjectIndexOptions
{
    /// <summary>Archives read at once. Zero is half the processor count, at most eight: the build is decompression, which a machine at work for its user should not give
    /// all its cores to.</summary>
    public int Workers { get; init; }
}

/// <summary>
/// M818: the index build, <c>GameIndex::build</c> and then <c>ObjectIndex::build</c> with no resolver: it lists the archives, mounts each, finds the first holder of every chunk
/// (<see cref="BuildTable"/>), and then reads every archive for the bins its own chunks hold (<see cref="BuildObjects"/>).
///
/// <para><b>Which chunks are bins.</b> A chunk is a bin when its first decoded bytes are <c>PROP</c> or <c>PTCH</c> (<c>LeagueFileKind::identify_from_bytes</c>: the two magics, tried
/// before every other and not reachable by any that is tried first). Every chunk is looked at, in ascending hash order within its first holder, since no name is given: a name
/// would be a guess about a chunk's content, and the overlay makes none. Only the first eight bytes are decoded to decide, and a chunk of any other kind costs that and nothing more.</para>
///
/// <para><b>What is read of a bin.</b> The whole bin is decompressed (a decoder cannot skip to the object table), and then only its header and its table of objects are walked: a
/// <c>PROP</c> by the size fields, hopping over every body (<see cref="PropMount.Declarations"/>), a <c>PTCH</c> as a whole, since it has no table to hop over
/// (<c>for_each_declaration</c>). No property value is decoded. A bin that does not read declares nothing, however many of its objects the sweep reached first.</para>
///
/// <para><b>Memory.</b> What is held is the chunk table, the declarations read, and the table of contents of the archives being read: no chunk outlives the loop that reads
/// it, so a build costs one bin per worker at a time, not the game.</para>
///
/// <para><b>Failure.</b> An archive that does not open or mount is skipped and keeps its id (<see cref="SkippedGameArchive"/>); the chunks of the others index. A bin that does not read is
/// skipped. The build ends with <see cref="OperationCanceledException"/> when its token is cancelled, having written nothing. Nothing else ends it: whatever one chunk or one archive
/// throws is that chunk's or that archive's, and the rest index.</para>
///
/// <para><b>Settled, or not.</b> A failure is one of two kinds, and the cache needs to know which, because it keys what it keeps by the installation's fingerprint (names, lengths, times),
/// which does not change when a read does. A failure that is a function of the BYTES (<c>GameWadException</c>: an archive or a chunk that is not what the crate reads;
/// <see cref="PropDecodeException"/>: a bin that does not decode) is the same on every build of that installation, and is settled: the index that results is the index of the installation. A
/// failure of the MOMENT (an <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> on a file a scanner or the game's patcher holds, and anything else the build did not
/// foresee) says nothing of the file: the next build may read it. Such an index is complete as far as it goes and is returned, with the reasons in
/// <see cref="GameObjectIndex.Unsettled"/> and <see cref="SkippedGameArchive.Transient"/>, and is never written to the cache.</para>
/// </summary>
public static class GameObjectIndexBuilder
{
    /// <summary>The bytes of a chunk a sniff decodes (<c>MAX_MAGIC_SIZE</c>).</summary>
    private const int MagicSize = 8;

    private static readonly byte[] PropMagic = "PROP"u8.ToArray();
    private static readonly byte[] PtchMagic = "PTCH"u8.ToArray();

    /// <summary>Builds the chunk table and the object index of the installation at <paramref name="gameDirectory"/>.</summary>
    /// <exception cref="GameIndexException">The installation has no <c>DATA/FINAL</c>, or its archives cannot be listed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static GameObjectIndex Build(string gameDirectory, GameObjectIndexOptions? options = null, IProgress<GameIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new GameIndexProgress(GameIndexStage.Enumerating, 0, 0, 0, 0));
        var archives = GameArchiveList.Enumerate(gameDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var table = BuildTable(gameDirectory, archives, options, progress, cancellationToken);
        return BuildObjects(table, options, progress, cancellationToken);
    }

    private static int WorkersOf(GameObjectIndexOptions? options, int jobs) =>
        Math.Clamp(options is { Workers: > 0 } ? options.Workers : Math.Clamp(System.Environment.ProcessorCount / 2, 1, 8), 1, Math.Max(1, jobs));

    // ================================================================================================ the chunk table

    /// <summary>Builds the chunk table of <paramref name="archives"/>, which <see cref="GameArchiveList.Enumerate"/> listed: each archive's table of contents, and no chunk.</summary>
    public static GameChunkTable BuildTable(string gameDirectory, IReadOnlyList<GameArchive> archives, GameObjectIndexOptions? options = null, IProgress<GameIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameDirectory);
        ArgumentNullException.ThrowIfNull(archives);
        if (archives.Count == 0) throw new GameIndexException($"{gameDirectory} has no archive to index");
        if (archives.Count > ushort.MaxValue) throw new GameIndexException($"{archives.Count} archives are more than the index numbers ({ushort.MaxValue})");
        ulong fingerprint = GameArchiveList.Fingerprint(archives);
        int workers = WorkersOf(options, archives.Count);

        var wads = new GameWad?[archives.Count];
        var errors = new (string? Error, bool Transient)[archives.Count];
        int mounted = 0;
        RunAcross(Enumerable.Range(0, archives.Count).OrderByDescending(i => archives[i].Length).ToArray(), workers, cancellationToken, id =>
        {
            var (wad, error, transient) = TryMount(archives[id]);
            wads[id] = wad;
            errors[id] = (error, transient);
            progress?.Report(new GameIndexProgress(GameIndexStage.Mounting, Interlocked.Increment(ref mounted), archives.Count, 0, 0));
        });
        cancellationToken.ThrowIfCancellationRequested();

        var skipped = new List<SkippedGameArchive>();
        for (int id = 0; id < archives.Count; id++)
            if (errors[id].Error is { } error) skipped.Add(new SkippedGameArchive(id, archives[id].Name, error, errors[id].Transient));

        var (hashes, start, holders) = MergeHolders(wads);
        return new GameChunkTable(gameDirectory, fingerprint, archives.ToArray(), skipped.ToArray(), hashes, start, holders);
    }

    /// <summary>
    /// The table of contents of an archive, or what a build says of one it cannot read (<c>ArchiveReadError</c>), and whether that is a failure of the MOMENT: a file that does not open (or any
    /// exception the mount did not foresee) says nothing of the file, where bytes that are not an archive the crate mounts are the same on every build.
    /// </summary>
    private static (GameWad? Wad, string? Error, bool Transient) TryMount(GameArchive archive)
    {
        try { return (GameWad.Mount(archive.Path), null, false); }
        catch (GameWadException e) { return (null, "cannot mount the archive: " + e.Message, false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, "cannot open the archive: " + e.Message, true); }
        catch (Exception e) when (e is not OperationCanceledException) { return (null, $"cannot read the archive ({e.GetType().Name}): {e.Message}", true); }
    }

    /// <summary>
    /// The chunk rows: every distinct hash over the archives that mounted, ascending, with the archives that hold it in id order. A chunk a table lists twice counts once for its
    /// archive. The rows are built from the tables of contents alone, and what they hold is eight bytes of hash and two of holder for each chunk.
    /// </summary>
    private static (ulong[] Hashes, int[] Start, ushort[] Holders) MergeHolders(GameWad?[] wads)
    {
        int total = 0;
        foreach (var wad in wads) total += wad?.Count ?? 0;
        var keys = new ulong[total];
        var owners = new ushort[total];
        int at = 0;
        for (int id = 0; id < wads.Length; id++)
        {
            if (wads[id] is not { } wad) continue;
            ulong? previous = null;
            foreach (var chunk in wad.Chunks)
            {
                if (chunk.PathHash == previous) continue;   // the table is in hash order: a repeated hash is the next row
                previous = chunk.PathHash;
                keys[at] = chunk.PathHash;
                owners[at++] = (ushort)id;
            }
        }

        Array.Sort(keys, owners, 0, at);
        // rows of one hash are in no order among themselves yet; the holders are in id order
        for (int i = 0; i < at;)
        {
            int end = i + 1;
            while (end < at && keys[end] == keys[i]) end++;
            if (end - i > 1) Array.Sort(owners, i, end - i);
            i = end;
        }

        int distinct = 0;
        for (int i = 0; i < at; i++)
            if (i == 0 || keys[i] != keys[i - 1]) distinct++;
        var hashes = new ulong[distinct];
        var start = new int[distinct + 1];
        int row = -1;
        for (int i = 0; i < at; i++)
        {
            if (i == 0 || keys[i] != keys[i - 1])
            {
                row++;
                hashes[row] = keys[i];
                start[row] = i;
            }
        }
        start[distinct] = at;
        return (hashes, start, owners.AsSpan(0, at).ToArray());
    }

    // ================================================================================================ the declarations

    /// <summary>The most reasons a build keeps, in all and from one archive: what a person is shown is a handful, and a count says how many more there were.</summary>
    private const int MaxNotes = 16, MaxNotesPerArchive = 4;

    /// <summary>What one archive's job read.</summary>
    private sealed class ArchiveRead
    {
        public readonly List<GameObjectDeclaration> Objects = new();
        public int Bins, Sniffed, SkippedChunks;
        public long Bytes;

        /// <summary>Reads that failed for a reason of the moment, and the first few of them worded (<see cref="GameObjectIndex.Unsettled"/>).</summary>
        public int Unsettled;
        public readonly List<string> Notes = new();

        public void Fail(string note)
        {
            Unsettled++;
            if (Notes.Count < MaxNotesPerArchive) Notes.Add(note);
        }
    }

    /// <summary>An exception worded for a person: the platform's own words for an I/O failure, and the type for what the build did not foresee.</summary>
    private static string Describe(Exception e) => e is IOException or UnauthorizedAccessException ? e.Message : $"{e.GetType().Name}: {e.Message}";

    /// <summary>Reads every archive of <paramref name="table"/> for the bins its own chunks hold, and builds the object index.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static GameObjectIndex BuildObjects(GameChunkTable table, GameObjectIndexOptions? options = null, IProgress<GameIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        var clock = Stopwatch.StartNew();
        var archives = table.Archives;
        var failed = table.Skipped.Select(s => s.Archive).ToHashSet();
        int[] jobs = Enumerable.Range(0, archives.Count).Where(i => !failed.Contains(i)).OrderByDescending(i => archives[i].Length).ToArray();
        int workers = WorkersOf(options, jobs.Length);

        var reads = new ArchiveRead?[archives.Count];
        int finished = 0, bins = 0, declared = 0, sniffed = 0;
        RunAcross(jobs, workers, cancellationToken, id =>
        {
            // the archive is mounted again, as the crate does: what the table kept of it is the hashes, not the rows
            var (wad, error, _) = TryMount(archives[id]);
            ArchiveRead? read = null;
            string? lost = error;
            if (wad is not null)
            {
                try { read = ReadArchive(id, wad, table, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { lost = "cannot be read: " + Describe(e); }
            }
            if (read is null)
            {
                // an archive that mounted when the table was built and does not now: the installation changed under the build or a file is held, and its chunks are lost to this build
                // for a reason of the moment, whatever the reason said
                read = new ArchiveRead { SkippedChunks = CountFirstHeldBy(table, id) };
                read.Fail($"{archives[id].WadPath}: {lost}");
            }
            reads[id] = read;
            Interlocked.Add(ref sniffed, read.Sniffed);
            int totalBins = Interlocked.Add(ref bins, read.Bins);
            int totalDeclared = Interlocked.Add(ref declared, read.Objects.Count);
            progress?.Report(new GameIndexProgress(GameIndexStage.ReadingBins, Interlocked.Increment(ref finished), jobs.Length, totalBins, totalDeclared));
        });
        cancellationToken.ThrowIfCancellationRequested();

        // storage order: archive id, then each archive's own order (chunk hash, then the chunk's object table)
        int count = reads.Sum(r => r?.Objects.Count ?? 0);
        var objects = new uint[count];
        var classes = new uint[count];
        var chunks = new ulong[count];
        var declaringArchive = new ushort[count];
        int at = 0;
        long bytes = 0;
        int skippedChunks = 0, unsettledReads = 0;
        var notes = new List<string>();
        for (int id = 0; id < reads.Length; id++)
        {
            if (reads[id] is not { } read) continue;
            bytes += read.Bytes;
            skippedChunks += read.SkippedChunks;
            unsettledReads += read.Unsettled;
            foreach (string note in read.Notes)
                if (notes.Count < MaxNotes) notes.Add(note);
            foreach (var declaration in read.Objects)
            {
                objects[at] = declaration.Object;
                classes[at] = declaration.Class;
                chunks[at] = declaration.Chunk;
                declaringArchive[at] = (ushort)id;
                at++;
            }
        }
        if (unsettledReads > notes.Count) notes.Add($"{unsettledReads.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} reads failed in all");

        var stats = new GameObjectIndexStats(sniffed, bins, count, skippedChunks, bytes, clock.Elapsed, workers);
        return new GameObjectIndex(table, stats, objects, classes, chunks, declaringArchive, notes);
    }

    private static int CountFirstHeldBy(GameChunkTable table, int archive)
    {
        int count = 0;
        for (int i = 0; i < table.ChunkCount; i++)
            if (table.FirstHolderAt(i) == archive) count++;
        return count;
    }

    /// <summary>
    /// One archive's share of the build: its own chunks (the ones it holds first) in ascending hash order, each sniffed and, where it is a bin, read.
    /// A chunk that will not read is skipped and counted; so is a bin that does not decode, with none of its objects kept. Nothing one chunk throws ends the archive: the bytes that are not
    /// what the crate reads are settled (a bin the codec refuses declares nothing, on every build), and a read that failed for a reason of the moment, or in a way the build did not foresee,
    /// is skipped and noted (<see cref="ArchiveRead.Unsettled"/>), so that the index which results is not kept.
    /// </summary>
    private static ArchiveRead ReadArchive(int id, GameWad wad, GameChunkTable table, CancellationToken cancellationToken)
    {
        var read = new ArchiveRead();
        var hashes = table.HashesColumn;
        string wadPath = table.Archive(id).WadPath;
        using var reader = wad.OpenReader();
        ulong? previous = null;
        foreach (var listed in wad.Chunks)
        {
            if (listed.PathHash == previous) continue;
            previous = listed.PathHash;

            int row = Array.BinarySearch(hashes, listed.PathHash);
            if (row < 0 || table.FirstHolderAt(row) != id) continue;   // another archive holds it first, and reads it
            cancellationToken.ThrowIfCancellationRequested();

            // the row of the table the crate reads: the last one the table lists for the hash
            wad.TryGet(listed.PathHash, out var chunk);
            read.Sniffed++;
            if (!SniffsAsBin(reader, chunk, read, wadPath)) continue;
            read.Bins++;

            byte[] bytes;
            try { bytes = reader.ReadChunk(chunk, GameWad.MaxChunkBytes, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (GameWadException) { read.SkippedChunks++; continue; }
            catch (Exception e)
            {
                read.SkippedChunks++;
                read.Fail($"{wadPath}: chunk {chunk.PathHash:x16}: {Describe(e)}");
                continue;
            }
            read.Bytes += bytes.Length;

            int before = read.Objects.Count;
            try
            {
                ulong chunkHash = chunk.PathHash;
                ForEachDeclaration(bytes, (obj, cls) => read.Objects.Add(new GameObjectDeclaration(obj, cls, chunkHash, id)));
            }
            catch (PropDecodeException)
            {
                read.Objects.RemoveRange(before, read.Objects.Count - before);
                read.SkippedChunks++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                read.Objects.RemoveRange(before, read.Objects.Count - before);
                read.SkippedChunks++;
                read.Fail($"{wadPath}: chunk {chunk.PathHash:x16}: {Describe(e)}");
            }
        }
        return read;
    }

    /// <summary>Whether the first bytes of the chunk are a bin's magic. A chunk that does not decode that far is not a bin the build could read either; one that could not be READ is not known to be
    /// anything, which the build notes (<see cref="ArchiveRead.Unsettled"/>) and does not count as a bin.</summary>
    private static bool SniffsAsBin(GameWadReader reader, in GameWadChunk chunk, ArchiveRead read, string wadPath)
    {
        // a chunk of fewer bytes than a magic has none, and a satellite chunk has no bytes at all: neither is read
        if (chunk.UncompressedSize < 4 || chunk.Compression == GameWadCompression.Satellite) return false;
        byte[] head;
        try { head = reader.ReadHead(chunk, MagicSize); }
        catch (GameWadException) { return false; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            read.Fail($"{wadPath}: chunk {chunk.PathHash:x16}: {Describe(e)}");
            return false;
        }
        return head.Length >= 4 && (head.AsSpan(0, 4).SequenceEqual(PropMagic) || head.AsSpan(0, 4).SequenceEqual(PtchMagic));
    }

    /// <summary>
    /// Visits <c>(object, class)</c> for every object one bin declares, in the order its table lists them (<c>for_each_declaration</c>). A PROP is swept through its table of
    /// objects without decoding a value; a PTCH is read whole, and its records declare nothing.
    /// </summary>
    /// <exception cref="PropDecodeException">The bytes are not a bin the codec reads.</exception>
    internal static void ForEachDeclaration(byte[] bytes, Action<uint, uint> visit)
    {
        if (bytes.AsSpan().StartsWith(PtchMagic))
        {
            var patch = PropCodec.ReadPtch(bytes);
            for (int i = 0; i < patch.Objects.Count; i++)
            {
                var obj = patch.Objects.ValueAt(i);
                visit(obj.PathHash, obj.ClassHash);
            }
            return;
        }
        foreach (var (path, cls) in PropCodec.Mount(bytes).Declarations()) visit(path, cls);
    }

    /// <summary>Runs <paramref name="work"/> over <paramref name="items"/>, in order, on <paramref name="workers"/> threads of their own at below normal priority; the first exception stops the rest and is rethrown.</summary>
    private static void RunAcross(int[] items, int workers, CancellationToken cancellationToken, Action<int> work)
    {
        if (items.Length == 0) return;
        int next = -1;
        Exception? failure = null;
        void Loop()
        {
            try
            {
                while (Volatile.Read(ref failure) is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int at = Interlocked.Increment(ref next);
                    if (at >= items.Length) return;
                    work(items[at]);
                }
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref failure, e, null);
            }
        }

        var threads = new Thread[Math.Min(workers, items.Length)];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "game-index-" + i };
            threads[i].Start();
        }
        foreach (var thread in threads) thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

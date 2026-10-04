namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// What one <see cref="GameEntryReader"/> may spend on the references of a package, in all, for its life: the references are the package's, and the bins they name are the game's, so a document
/// of a hundred thousand names spread over every bin of the game would otherwise cost the whole game's bins in time and a good part of its objects in memory. A read the budget refuses answers
/// <see cref="GameDataEntryRead.Failed"/> (<c>reference budget exceeded</c>), which is not remembered; the references that were answered stay answered.
///
/// <para>The defaults are measured, and generous for what is real. Crauzer's Winter Rift, the largest package measured, makes <c>11</c> distinct references, in <c>11</c> bins (6.7 MB decompressed in
/// all), and the objects it keeps hold <c>521</c> values; the seven packages of M815 make at most <c>3</c>, in at most <c>2</c> bins (6.7 MB), holding at most <c>530</c> values. The defaults are
/// three orders of magnitude above that where being generous costs nothing (entries, bytes read), and where it does they are set by what a machine may be asked to hold: a decoded tree takes about
/// <c>64</c> bytes a value, so <c>4,000,000</c> values is about a quarter of a gigabyte, which is Map22, the largest bin of the game (4.2 million values), decoded whole.</para>
/// </summary>
public sealed record GameEntryLimits
{
    /// <summary>The most distinct entries a reader decodes and keeps: 20,000, about eighteen hundred times the 11 of the largest package measured. The game declares 416,479 objects.</summary>
    public const int DefaultMaxEntries = 20_000;

    /// <summary>The most values (units of decode work, about one each) the decoded objects a reader keeps may hold together: about a quarter of a gigabyte of tree at the measured sixty-four bytes a value,
    /// and nearly eight thousand times the 530 of the largest package measured.</summary>
    public const long DefaultMaxRetainedValues = 4_000_000;

    /// <summary>The most decompressed bytes of bins a reader reads in all: 1 GiB, about a hundred and fifty times the 6.7 MB of the largest package measured, and thirty of the largest bin of the game. A bin is
    /// read once for all the entries it holds while it stays in the few kept.</summary>
    public const long DefaultMaxChunkBytes = 1L << 30;

    /// <summary>The largest bin a reference may read: 64 MiB, about twice the largest bin of the installed game (Map22, 33 MB).</summary>
    public const long DefaultMaxBinBytes = 64L << 20;

    public int MaxEntries { get; init; } = DefaultMaxEntries;

    public long MaxRetainedValues { get; init; } = DefaultMaxRetainedValues;

    public long MaxChunkBytes { get; init; } = DefaultMaxChunkBytes;

    public long MaxBinBytes { get; init; } = DefaultMaxBinBytes;

    public static GameEntryLimits Default { get; } = new();
}

/// <summary>What a reader has done so far: for a caller that wants to say what the references cost, and for tests.</summary>
/// <param name="Entries">Distinct entries decoded and kept.</param>
/// <param name="ChunkReads">Bins read from the game (an entry served from a kept bin is not one).</param>
/// <param name="ChunkBytes">Decompressed bytes of those bins.</param>
/// <param name="RetainedValues">Values of the decoded objects kept.</param>
/// <param name="Refused">Reads the budget refused.</param>
public readonly record struct GameEntryReaderStats(int Entries, int ChunkReads, long ChunkBytes, long RetainedValues, int Refused);

/// <summary>
/// The cost of the reads one application of the overlay asked for, in the units of work <see cref="GameDataLimits"/> counts: the engine's own meter cannot be reached from a reader (the
/// delegate it calls carries only a name), so the caller that runs the application gives its reads one of these, adds <see cref="Units"/> to the work of the chunk when the application ends, and
/// sets <see cref="Limit"/> to what the chunk may still spend so that a read which would go past it is refused before it is made.
/// </summary>
public sealed class GameEntryTally
{
    private long _units;

    /// <param name="limit">The units the reads may cost together before the next is refused; <see cref="long.MaxValue"/> for no limit.</param>
    public GameEntryTally(long limit = long.MaxValue) { Limit = limit; }

    /// <summary>The units a read may still bring the tally up to.</summary>
    public long Limit { get; }

    /// <summary>The units the reads cost: a unit for each 32 bytes of a bin decompressed, one for each eight objects of its table walked, and one for each value decoded.</summary>
    public long Units => Volatile.Read(ref _units);

    internal void Charge(long units) => Interlocked.Add(ref _units, units);
}

/// <summary>
/// M818: the game's copy of an entry a reference names, as the overlay reads it (<c>read_referenced_entry</c>): the entry is looked up in the object index, the FIRST chunk that declares it
/// is read from its first holder, and the object is decoded out of it. It is <see cref="ReadGameEntry"/> for <see cref="GameDataApplier.Apply"/>, once per distinct entry and before any edit.
///
/// <para><b>What it answers.</b></para>
/// <list type="bullet">
/// <item><description>No index (it did not load, or the caller has none), or no chunk declares the entry: <see cref="GameDataEntryRead.None"/>. The game lacks the entry as far as this reader
/// knows, and the key that names it reports so. Nothing is remembered for it: the index answers in a lookup.</description></item>
/// <item><description>A chunk that declares the entry and does not read, mount or decode, or that does not hold the object after all (a stale index): <see cref="GameDataEntryRead.Failed"/>,
/// worded <c>&lt;entry&gt;: &lt;chunk&gt; of &lt;wad&gt;: &lt;why&gt;</c>. That is the installation or a stale cache, and no property key can say so. A first holder that is a PTCH is one of
/// these: a PTCH holds patch records, and the streaming reader the crate reads objects with refuses one.</description></item>
/// <item><description>A read the reader's budget (<see cref="GameEntryLimits"/>) or the caller's (<see cref="GameEntryTally"/>) does not allow: <see cref="GameDataEntryRead.Failed"/>,
/// <c>&lt;entry&gt;: reference budget exceeded: &lt;what&gt;</c>.</description></item>
/// <item><description>Otherwise the object. When a chunk lists one object hash twice, the LAST of them is the object (the crate's table of contents keeps the last, as the eager reader's
/// map does).</description></item>
/// </list>
///
/// <para><b>Memoised, within a budget.</b> An answer of <c>Some</c> is kept for the life of the reader, however many keys, targets and threads ask; a failure is not, so a read the
/// installation could not serve is tried again by the next caller. The decoded object is shared by every application that references it and is only read: its ordered maps publish their index
/// safely (<see cref="OrderedMap{T}"/>), which is what lets applications run on several threads over one object. What is kept is bounded by <see cref="GameEntryLimits"/>.</para>
///
/// <para><b>Cost.</b> A reference into a large bin decompresses the whole bin, as the crate does. The last few decompressed bins are kept (under 96 MiB), so that the entries of one bin, which
/// is what a source skin's references are, cost one decompression. A bin that is bigger than the limit, or that would take the reader past its budget of bytes read, is refused
/// before it is read: the archive's table of contents says how large it is.</para>
/// </summary>
public sealed class GameEntryReader
{
    /// <summary>The decompressed bins kept, and the bytes they may hold together.</summary>
    private const int KeptBins = 2;
    private const long KeptBytes = 96L << 20;

    /// <summary>Bytes of a bin decompressed for each unit of work (a Zstd decompression runs at about a twentieth of the engine's decode, byte for unit).</summary>
    private const int BytesPerUnit = 32;

    private sealed class Memo
    {
        // held while the entry's bin is read, which can take a while on a busy disk: a caller that waits for it ends its wait with its own token
        public readonly SemaphoreSlim Gate = new(1, 1);
        public bool Done;
        public PropObject? Object;
    }

    private sealed record KeptBin(int Archive, ulong Chunk, byte[] Bytes, PropMount Mount);

    private readonly GameObjectIndex? _index;
    private readonly IGameChunkReader _chunks;
    private readonly GameEntryLimits _limits;
    // an entry's hash is the document's to choose, so the table that keys by it is salted (KeyHash): hashes chosen to share a bucket cannot chain it
    private readonly Dictionary<uint, Memo> _memo = KeyHash.Map<Memo>();
    private readonly object _memoGate = new();
    private readonly object _keptGate = new();
    private readonly List<KeptBin> _kept = new();
    private int _chunkReads;
    private long _chunkBytes;
    private long _retainedValues;
    private int _entries;
    private int _refused;

    /// <param name="index">The object index; null when it is unavailable, which makes every entry absent.</param>
    /// <param name="chunks">Reads the chunk of a declaration from its archive.</param>
    /// <param name="limits">What the reader may spend in all; <see cref="GameEntryLimits.Default"/> when null.</param>
    public GameEntryReader(GameObjectIndex? index, IGameChunkReader chunks, GameEntryLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        _index = index;
        _chunks = chunks;
        _limits = limits ?? GameEntryLimits.Default;
    }

    /// <summary>How many chunk reads the reader has made: one for each entry that was not memoised and not in a kept bin. For tests, and for a caller that wants to say what the references cost.</summary>
    public int ChunkReads => Volatile.Read(ref _chunkReads);

    /// <summary>What the reader has read and kept so far.</summary>
    public GameEntryReaderStats Stats => new(Volatile.Read(ref _entries), ChunkReads, Interlocked.Read(ref _chunkBytes), Interlocked.Read(ref _retainedValues), Volatile.Read(ref _refused));

    /// <summary>The reader as the delegate <see cref="GameDataApplier.Apply"/> takes. <paramref name="cancellationToken"/> ends a read in progress; <paramref name="tally"/>, where there is one,
    /// is charged what each read costs and refuses the reads that would take it past its limit.</summary>
    public ReadGameEntry AsDelegate(CancellationToken cancellationToken = default, GameEntryTally? tally = null) => name => Read(name, cancellationToken, tally);

    /// <summary>The game's copy of <paramref name="name"/>'s object.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; nothing is memoised.</exception>
    public GameDataEntryRead Read(EntryName name, CancellationToken cancellationToken = default) => Read(name, cancellationToken, null);

    /// <summary>The game's copy of <paramref name="name"/>'s object, with what the read costs charged to <paramref name="tally"/>.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; nothing is memoised.</exception>
    public GameDataEntryRead Read(EntryName name, CancellationToken cancellationToken, GameEntryTally? tally)
    {
        ArgumentNullException.ThrowIfNull(name);
        uint hash = name.ObjectHash;

        // an entry no chunk declares costs a lookup and keeps nothing: the answer is the index's, which does not change
        if (_index?.FirstDeclaration(hash) is not { } declaration) return GameDataEntryRead.None;

        Memo? memo;
        lock (_memoGate)
        {
            if (!_memo.TryGetValue(hash, out memo))
            {
                if (_memo.Count >= _limits.MaxEntries) return Refused(name, $"more than the {GameDataOverlayDiagnostics.Count(_limits.MaxEntries)} distinct entries a reader keeps");
                _memo[hash] = memo = new Memo();
            }
        }
        memo.Gate.Wait(cancellationToken);
        try
        {
            if (memo.Done) return GameDataEntryRead.Found(memo.Object!);
            cancellationToken.ThrowIfCancellationRequested();

            string wad = _index.Table.Archive(declaration.Archive).WadPath;
            string Failed(string why) => $"{name.Text}: {declaration.Chunk:x16} of {wad}: {why}";
            try
            {
                if (tally is not null && tally.Units >= tally.Limit) return Refused(name, "the applications to this chunk have read as much as the overlay allows one chunk");
                var (bytes, mount, fresh) = Mounted(declaration, cancellationToken);

                var meter = new WorkMeter(null, cancellationToken, bytes.Length);
                var found = mount.ReadObject(hash, meter);
                if (fresh) tally?.Charge(bytes.Length / BytesPerUnit);
                tally?.Charge(mount.ObjectCount / 8 + meter.Work);
                if (found is null) return GameDataEntryRead.Failed(Failed("chunk does not hold the object, so the index is stale"));

                // what is kept is bounded: an object that would take the reader past its budget of values is dropped, and the read is refused, so that a document naming the biggest objects of the
                // game cannot hold the game in memory
                if (Interlocked.Add(ref _retainedValues, meter.Work) > _limits.MaxRetainedValues)
                {
                    Interlocked.Add(ref _retainedValues, -meter.Work);
                    return Refused(name, $"the objects kept hold more than the {GameDataOverlayDiagnostics.Count(_limits.MaxRetainedValues)} values a reader keeps");
                }
                memo.Object = found;
                memo.Done = true;
                Interlocked.Increment(ref _entries);
                return GameDataEntryRead.Found(found);
            }
            catch (BudgetExceeded e)
            {
                return Refused(name, e.Message);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // a failure is a statement and never an exception: the chunk reader is the caller's, and what it throws that nobody expected is this entry's reference unreadable, not the application's
                return GameDataEntryRead.Failed(Failed(e is GameChunkReadException or PropDecodeException or GameDataException or IOException or UnauthorizedAccessException
                    ? e.Message
                    : $"internal error ({e.GetType().Name}): {e.Message}"));
            }
        }
        finally
        {
            memo.Gate.Release();
        }
    }

    private GameDataEntryRead Refused(EntryName name, string what)
    {
        Interlocked.Increment(ref _refused);
        return GameDataEntryRead.Failed($"{name.Text}: reference budget exceeded: {what}");
    }

    /// <summary>What a read of a bin finds out about the reader's budget before it reads: the bytes it has read already are the limit's.</summary>
    private sealed class BudgetExceeded : Exception
    {
        public BudgetExceeded(string what) : base(what) { }
    }

    /// <summary>The decompressed bin of a declaration with its header read, from the bins kept or from the archive.</summary>
    /// <exception cref="BudgetExceeded">The reader has read all the bytes of bins its budget allows.</exception>
    private (byte[] Bytes, PropMount Mount, bool Fresh) Mounted(GameObjectDeclaration declaration, CancellationToken cancellationToken)
    {
        lock (_keptGate)
        {
            for (int i = 0; i < _kept.Count; i++)
            {
                if (_kept[i].Archive != declaration.Archive || _kept[i].Chunk != declaration.Chunk) continue;
                var hit = _kept[i];
                _kept.RemoveAt(i);
                _kept.Insert(0, hit);
                return (hit.Bytes, hit.Mount, false);
            }
        }

        // the archive's table of contents says how large the bin is, and the read is refused on that before a byte is read: the limit is the one bin the reader allows or what is left of its bytes
        long left = _limits.MaxChunkBytes - Interlocked.Read(ref _chunkBytes);
        if (left <= 0) throw new BudgetExceeded($"the bins read come to more than the {GameDataOverlayDiagnostics.Count(_limits.MaxChunkBytes)} bytes a reader reads");
        byte[] bytes = _chunks.ReadChunk(declaration.Archive, declaration.Chunk, Math.Min(_limits.MaxBinBytes, left), cancellationToken);
        Interlocked.Increment(ref _chunkReads);
        Interlocked.Add(ref _chunkBytes, bytes.Length);
        var mount = PropCodec.Mount(bytes);
        lock (_keptGate)
        {
            _kept.Insert(0, new KeptBin(declaration.Archive, declaration.Chunk, bytes, mount));
            long held = 0;
            for (int i = 0; i < _kept.Count; i++)
            {
                held += _kept[i].Bytes.Length;
                if (i >= KeptBins || (i > 0 && held > KeptBytes)) { _kept.RemoveRange(i, _kept.Count - i); break; }
            }
        }
        return (bytes, mount, true);
    }
}

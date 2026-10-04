namespace ReyEngine.Formats.LtkGameData;

/// <summary>One object in one declaring chunk (<c>Declaration</c>): the object, the class the chunk declares it as, the chunk, and the archive that is the chunk's first holder.</summary>
public readonly record struct GameObjectDeclaration(uint Object, uint Class, ulong Chunk, int Archive);

/// <summary>Counts of one object index build (<c>ObjectStats</c>).</summary>
/// <param name="Sniffed">Chunks whose first bytes were decoded to look for a bin's magic.</param>
/// <param name="Bins">Chunks that opened with <c>PROP</c> or <c>PTCH</c> and were read.</param>
/// <param name="Declarations">Declarations stored.</param>
/// <param name="SkippedChunks">Bins that did not read: a chunk that would not decompress, or a bin that does not decode.</param>
/// <param name="Bytes">Decompressed bytes of bins read.</param>
/// <param name="Elapsed">Wall-clock time of the build that produced the index (an index loaded from its cache reports the time its build took).</param>
/// <param name="Workers">Threads the build ran on.</param>
public sealed record GameObjectIndexStats(int Sniffed, int Bins, int Declarations, int SkippedChunks, long Bytes, TimeSpan Elapsed, int Workers);

/// <summary>
/// M818: every bin object of an installed game, with every chunk that declares it - <c>ltk_game_index</c>'s <c>ObjectIndex</c>, built the way <c>ltk_overlay</c> builds it (no names:
/// every chunk is sniffed for a bin's magic), over the <see cref="GameChunkTable"/> of the same installation.
///
/// <para><b>What it answers.</b> Which chunks declare an object (<see cref="Declarations"/>, in storage order) and what a chunk declares (<see cref="DeclarationsIn"/>). A chunk is READ
/// from its first holder only, so every declaration names that archive. The first declaration of an object is the one a reference reads, and the chunks of an object come out in the
/// order an <c>entries</c> module edits them.</para>
///
/// <para><b>Storage order.</b> Declarations are stored by archive id, then by chunk hash ascending, then in the order the chunk's object table lists them; <see cref="Declarations"/>
/// is a stable sort of that by object.</para>
///
/// <para><b>Lifetime.</b> An index is immutable and safe to share across threads. <see cref="GameObjectIndexBuilder"/> builds one and <see cref="GameObjectIndexCache"/> keeps it on disk.</para>
/// </summary>
public sealed class GameObjectIndex
{
    // the declarations, in storage order, as columns; and the permutation that sorts them stably by object
    private readonly uint[] _objects;
    private readonly uint[] _classes;
    private readonly ulong[] _chunks;
    private readonly ushort[] _declaringArchive;
    private readonly int[] _byObject;

    // the declarations of each chunk are one run of storage order: where the run of a chunk begins and ends, sorted by chunk
    private readonly ulong[] _runChunk;
    private readonly int[] _runStart;
    private readonly int[] _runLength;

    internal GameObjectIndex(GameChunkTable table, GameObjectIndexStats stats, uint[] objects, uint[] classes, ulong[] chunks, ushort[] declaringArchive, IReadOnlyList<string>? unsettled = null)
    {
        Table = table;
        Stats = stats;
        Unsettled = unsettled ?? Array.Empty<string>();
        _objects = objects;
        _classes = classes;
        _chunks = chunks;
        _declaringArchive = declaringArchive;

        // sorting (object, position) pairs makes the sort stable: equal objects stay in storage order
        var keys = new ulong[objects.Length];
        var order = new int[objects.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = (ulong)objects[i] << 32 | (uint)i;
            order[i] = i;
        }
        Array.Sort(keys, order);
        _byObject = order;

        var runChunk = new List<ulong>();
        var runStart = new List<int>();
        var runLength = new List<int>();
        for (int i = 0; i < chunks.Length;)
        {
            int end = i + 1;
            while (end < chunks.Length && chunks[end] == chunks[i]) end++;
            runChunk.Add(chunks[i]);
            runStart.Add(i);
            runLength.Add(end - i);
            i = end;
        }
        var runKeys = runChunk.ToArray();
        var runOrder = Enumerable.Range(0, runKeys.Length).ToArray();
        Array.Sort(runKeys, runOrder);
        _runChunk = runKeys;
        _runStart = runOrder.Select(r => runStart[r]).ToArray();
        _runLength = runOrder.Select(r => runLength[r]).ToArray();
    }

    /// <summary>The chunk table of the same installation: which archives hold each chunk.</summary>
    public GameChunkTable Table { get; }

    public GameObjectIndexStats Stats { get; }

    /// <summary>The reads that failed for a reason of the moment while the index was built (an archive or a chunk that could not be opened or read, a scanner holding a file, anything the build did not
    /// foresee), worded for a person, the first few of them. They are not stored with the index: an index with any is returned to its caller and never written to the cache (<see cref="IsSettled"/>), so a
    /// loaded one has none. A bin that does not decode is not one of them: that is the bytes, and it is settled.</summary>
    public IReadOnlyList<string> Unsettled { get; }

    /// <summary>Whether the index and its table are a function of the installation alone, which is what the cache keys them by: false when a read failed for a reason of the moment.</summary>
    public bool IsSettled => Unsettled.Count == 0 && Table.IsSettled;

    /// <summary>The identity of the installation state this index was built from.</summary>
    public ulong Fingerprint => Table.Fingerprint;

    /// <summary>The id of the first archive in id order that holds <paramref name="chunk"/>, or -1 (<see cref="GameChunkTable.FirstHolder"/>).</summary>
    public int FirstHolder(ulong chunk) => Table.FirstHolder(chunk);

    /// <summary>Every archive holding <paramref name="chunk"/>, in id order (<see cref="GameChunkTable.Holders"/>).</summary>
    public int[] Holders(ulong chunk) => Table.Holders(chunk);

    /// <summary>The path of archive <paramref name="archive"/> below the game directory: <c>DATA/FINAL/Champions/Aatrox.wad.client</c> (<see cref="GameArchive.WadPath"/>).</summary>
    public string WadPath(int archive) => Table.Archive(archive).WadPath;

    /// <summary>The number of declarations stored.</summary>
    public int DeclarationCount => _objects.Length;

    /// <summary>The declaration at <paramref name="position"/> of storage order.</summary>
    public GameObjectDeclaration DeclarationAt(int position) => new(_objects[position], _classes[position], _chunks[position], _declaringArchive[position]);

    /// <summary>Every object some chunk declares, ascending, each once.</summary>
    public IEnumerable<uint> Objects()
    {
        for (int i = 0; i < _byObject.Length; i++)
            if (i == 0 || _objects[_byObject[i]] != _objects[_byObject[i - 1]]) yield return _objects[_byObject[i]];
    }

    /// <summary>The number of distinct objects any chunk declares.</summary>
    public int ObjectCount => Objects().Count();

    /// <summary>Whether any chunk declares <paramref name="objectHash"/>.</summary>
    public bool Declares(uint objectHash)
    {
        int at = LowerBound(objectHash);
        return at < _byObject.Length && _objects[_byObject[at]] == objectHash;
    }

    /// <summary>Every declaration of <paramref name="objectHash"/>, in storage order: the first is the one a reference reads.</summary>
    public GameObjectDeclaration[] Declarations(uint objectHash)
    {
        int start = LowerBound(objectHash), end = start;
        while (end < _byObject.Length && _objects[_byObject[end]] == objectHash) end++;
        var result = new GameObjectDeclaration[end - start];
        for (int i = 0; i < result.Length; i++) result[i] = DeclarationAt(_byObject[start + i]);
        return result;
    }

    /// <summary>The first declaration of <paramref name="objectHash"/> in storage order, or null for an object no chunk declares.</summary>
    public GameObjectDeclaration? FirstDeclaration(uint objectHash)
    {
        int at = LowerBound(objectHash);
        return at < _byObject.Length && _objects[_byObject[at]] == objectHash ? DeclarationAt(_byObject[at]) : null;
    }

    /// <summary>Every declaration in <paramref name="chunk"/>, in storage order.</summary>
    public GameObjectDeclaration[] DeclarationsIn(ulong chunk)
    {
        int at = Array.BinarySearch(_runChunk, chunk);
        if (at < 0) return Array.Empty<GameObjectDeclaration>();
        var result = new GameObjectDeclaration[_runLength[at]];
        for (int i = 0; i < result.Length; i++) result[i] = DeclarationAt(_runStart[at] + i);
        return result;
    }

    /// <summary>The first position of <see cref="_byObject"/> whose object is not less than <paramref name="objectHash"/>.</summary>
    private int LowerBound(uint objectHash)
    {
        int low = 0, high = _byObject.Length;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (_objects[_byObject[mid]] < objectHash) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    // ================================================================================================ what the cache stores

    internal uint[] ObjectsColumn => _objects;
    internal uint[] ClassesColumn => _classes;
    internal ulong[] ChunksColumn => _chunks;
    internal ushort[] DeclaringArchiveColumn => _declaringArchive;
}

namespace ReyEngine.Formats.LtkGameData;

/// <summary>An archive the index build could not open or mount (<c>SkippedArchive</c>). It keeps its id and its place in the list, and holds no chunks.</summary>
/// <param name="Archive">The archive's id.</param>
/// <param name="Name">The archive's name below <c>DATA/FINAL</c>.</param>
/// <param name="Error">Why it was skipped.</param>
/// <param name="Transient">True when the reason is one of the moment (the file could not be opened: a sharing violation, a scanner holding it, a permission) and says nothing of the file itself. A table with
/// such an archive is not kept (<see cref="GameObjectIndexCache"/>), since the same archive opens the next time. False when the bytes themselves are not an archive the crate mounts.</param>
public sealed record SkippedGameArchive(int Archive, string Name, string Error, bool Transient = false);

/// <summary>
/// M818: every chunk of an installed game and every archive that holds it - <c>ltk_game_index</c>'s <c>GameIndex</c>, the part of the index every target of a declaration needs
/// and that costs little to build: it reads the table of contents of each archive and no chunk.
///
/// <para><b>What it answers.</b> Which archives hold a chunk (<see cref="Holders"/>, in id order; <see cref="FirstHolder"/> is the first), and the WAD path of an archive id
/// (<see cref="GameArchive.WadPath"/>). The ids are the positions in <see cref="GameArchiveList.Enumerate"/>'s byte-ordered list. An archive that does not open or mount is
/// in <see cref="Skipped"/> and holds no chunk, so a chunk it shares with another is held by the other.</para>
///
/// <para><b>Lifetime.</b> A table is immutable and safe to share across threads. It is the product of one installation state, named by <see cref="Fingerprint"/>
/// (<see cref="GameArchiveList.Fingerprint"/>).</para>
/// </summary>
public sealed class GameChunkTable
{
    private readonly GameArchive[] _archives;
    private readonly SkippedGameArchive[] _skipped;

    // ascending chunk hashes, and the holders of each as a flat list with where each chunk's run begins
    private readonly ulong[] _hashes;
    private readonly int[] _holderStart;
    private readonly ushort[] _holders;

    internal GameChunkTable(string gameDirectory, ulong fingerprint, GameArchive[] archives, SkippedGameArchive[] skipped, ulong[] hashes, int[] holderStart, ushort[] holders)
    {
        GameDirectory = gameDirectory;
        Fingerprint = fingerprint;
        _archives = archives;
        _skipped = skipped;
        _hashes = hashes;
        _holderStart = holderStart;
        _holders = holders;
    }

    /// <summary>The game directory the table was built from (the one that holds <c>DATA/FINAL</c>).</summary>
    public string GameDirectory { get; }

    /// <summary>The identity of the installation state: <see cref="GameArchiveList.Fingerprint"/> over its archives.</summary>
    public ulong Fingerprint { get; }

    /// <summary>Every archive, in id order.</summary>
    public IReadOnlyList<GameArchive> Archives => _archives;

    /// <summary>The archives the build could not read, in id order.</summary>
    public IReadOnlyList<SkippedGameArchive> Skipped => _skipped;

    /// <summary>Whether the table is a function of the installation alone: true unless an archive was skipped for a reason of the moment (<see cref="SkippedGameArchive.Transient"/>). A table that is not
    /// settled is returned to the caller and never written to the cache.</summary>
    public bool IsSettled => !_skipped.Any(s => s.Transient);

    /// <summary>The reasons the table is not settled, one for each archive skipped for a reason of the moment, worded for a person: <c>DATA/FINAL/Map11.wad.client: cannot open the archive: ...</c>.</summary>
    public IReadOnlyList<string> Unsettled => _skipped.Where(s => s.Transient).Select(s => $"{GameArchiveList.ArchiveRoot}/{s.Name}: {s.Error}").ToList();

    /// <summary>The archive with <paramref name="id"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is not one of this table's.</exception>
    public GameArchive Archive(int id) =>
        (uint)id < (uint)_archives.Length ? _archives[id] : throw new ArgumentOutOfRangeException(nameof(id), id, $"archive id {id} is not from this index, which has {_archives.Length} archives");

    /// <summary>The number of distinct chunks.</summary>
    public int ChunkCount => _hashes.Length;

    /// <summary>The chunk hash at <paramref name="position"/> of the table, which is in ascending hash order.</summary>
    public ulong ChunkAt(int position) => _hashes[position];

    /// <summary>Whether any archive holds <paramref name="chunk"/>.</summary>
    public bool ContainsChunk(ulong chunk) => Array.BinarySearch(_hashes, chunk) >= 0;

    /// <summary>The id of the first archive in id order that holds <paramref name="chunk"/>, or -1 for a chunk no archive holds.</summary>
    public int FirstHolder(ulong chunk)
    {
        int at = Array.BinarySearch(_hashes, chunk);
        return at < 0 ? -1 : _holders[_holderStart[at]];
    }

    /// <summary>Every archive holding <paramref name="chunk"/>, in id order. Empty for a chunk no archive holds.</summary>
    public int[] Holders(ulong chunk)
    {
        int at = Array.BinarySearch(_hashes, chunk);
        return at < 0 ? Array.Empty<int>() : HoldersAt(at);
    }

    /// <summary>The holders of the chunk at <paramref name="position"/> of the table.</summary>
    public int[] HoldersAt(int position)
    {
        int start = _holderStart[position], end = _holderStart[position + 1];
        var result = new int[end - start];
        for (int i = 0; i < result.Length; i++) result[i] = _holders[start + i];
        return result;
    }

    /// <summary>The first holder of the chunk at <paramref name="position"/> of the table.</summary>
    internal int FirstHolderAt(int position) => _holders[_holderStart[position]];

    // ================================================================================================ what the cache stores

    internal ulong[] HashesColumn => _hashes;
    internal int[] HolderStartColumn => _holderStart;
    internal ushort[] HoldersColumn => _holders;
}

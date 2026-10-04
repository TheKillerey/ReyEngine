namespace ReyEngine.Formats.LtkGameData;

/// <summary>A game chunk that cannot be read. <see cref="Exception.Message"/> is the reader's own statement of why, written for a diagnostic.</summary>
public sealed class GameChunkReadException : Exception
{
    public GameChunkReadException(string message) : base(message) { }

    public GameChunkReadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Reads the bytes of a chunk of the installed game: what the overlay needs for a target that no mod file shadows, and for the entry a reference names.</summary>
public interface IGameChunkReader
{
    /// <summary>The decompressed bytes of <paramref name="chunk"/> in archive <paramref name="archive"/> (an id of the table the reader was made for).</summary>
    /// <param name="archive">The archive's id in the table the reader was made for.</param>
    /// <param name="chunk">The chunk's path hash.</param>
    /// <param name="maxBytes">The most the chunk may be stored in and may decode to. A chunk whose table of contents says more is refused before any of it is read or allocated.</param>
    /// <param name="cancellationToken">Ends a read in progress.</param>
    /// <exception cref="GameChunkReadException">The archive does not open, does not hold the chunk, changed since the index was built, holds a chunk larger than <paramref name="maxBytes"/> or one that
    /// lies outside the file, or holds bytes that do not decompress.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>
/// M818: <see cref="IGameChunkReader"/> over an installation's archives (<c>GameDir::read_chunk</c>): the archive is found by id in the <see cref="GameChunkTable"/>, mounted, and the chunk
/// decompressed. A mounted table of contents is kept for the archives read last, and the file is opened for the read alone and closed, so that no handle is held on a file the
/// game's patcher may replace.
///
/// <para>The archive must be the file the table was built from: its length and its modification time are compared with the table's on every read. An archive that was patched since is the
/// reason a chunk is not where the index says, and the reader says so rather than read from an offset the new file may not have. Reads may run on any thread.</para>
/// </summary>
public sealed class GameChunkReader : IGameChunkReader
{
    /// <summary>Tables of contents kept: a map's archive, the shared ones its bins live in, and a champion or two.</summary>
    private const int MountedKept = 16;

    private readonly GameChunkTable _table;
    private readonly object _gate = new();
    private readonly LinkedList<(int Archive, GameWad Wad)> _mounted = new();

    public GameChunkReader(GameChunkTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken)
    {
        var info = _table.Archive(archive);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var file = new FileInfo(info.Path);
            if (!file.Exists) throw new GameChunkReadException($"{info.WadPath} is gone");
            if (file.Length != info.Length || GameArchiveList.ModifiedNanos(file.LastWriteTimeUtc) != info.ModifiedNanos)
                throw new GameChunkReadException($"{info.WadPath} changed after the index was built, so the index is stale");

            var wad = Mount(info);
            if (!wad.TryGet(chunk, out var row)) throw new GameChunkReadException($"{info.WadPath} does not hold the chunk");
            using var reader = wad.OpenReader();
            return reader.ReadChunk(row, maxBytes, cancellationToken);
        }
        catch (GameWadException e) { throw new GameChunkReadException(e.Message, e); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new GameChunkReadException(e.Message, e); }
    }

    private GameWad Mount(GameArchive archive)
    {
        lock (_gate)
        {
            for (var node = _mounted.First; node is not null; node = node.Next)
            {
                if (node.Value.Archive != archive.Id) continue;
                _mounted.Remove(node);
                _mounted.AddFirst(node);
                return node.Value.Wad;
            }
        }
        // mounted outside the lock: reading a table takes a while, and two readers racing for one archive cost a duplicate read, not a wrong answer
        var wad = GameWad.Mount(archive.Path);
        lock (_gate)
        {
            _mounted.AddFirst((archive.Id, wad));
            while (_mounted.Count > MountedKept) _mounted.RemoveLast();
        }
        return wad;
    }
}

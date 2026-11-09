using System.Collections.Concurrent;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M891: ONE index over every game archive, read on demand - the way LTK Manager's Game Index works. Given a game path (<c>data/characters/kalista/spells/kalistaw.preload</c>) or its path
/// hash, it returns the bytes from whichever archive holds the chunk, without the caller naming or mounting a WAD.
///
/// <para><b>Built from.</b> <see cref="GameObjectIndexCache.LoadOrBuildTable"/>: the table of contents of every <c>*.wad.client</c> below <c>DATA/FINAL</c> (chunk hash to holders, no chunk read),
/// kept in a fingerprint-keyed cache file under the user's local application data - never in the game folder. The table is built lazily on first use, once per
/// instance, under a lock; <see cref="Shared"/> keeps one instance per game folder for the process. If an archive changes after the table was built (the patcher), the read says so and
/// <see cref="TryRead(ulong, out byte[]?, string[]?)"/> rebuilds the table once and retries.</para>
///
/// <para><b>Which holder wins.</b> A chunk several archives hold (a champion's file also shipped in a locale or Scripts archive) is read from, in this order:
/// (1) the first archive named in <c>preferredArchives</c> that holds it - the champion's own WAD, the open map's WAD, matched by file name ignoring case and a missing
/// <c>.wad.client</c>; (2) otherwise the FIRST holder in archive id order, which is LTK's rule (ids are the byte order of the name below <c>DATA/FINAL</c>,
/// <see cref="GameArchiveList"/>). The preference never makes a missing chunk resolve and never reads an archive that does not hold it.</para>
///
/// <para><b>Reads.</b> Delegated to <see cref="GameChunkReader"/>: a least-recently-used set of 16 mounted tables of contents, the archive file opened for the read alone with
/// <c>FileShare.ReadWrite | Delete</c> and closed, so no handle is held on a file the patcher may replace. Thread-safe. Nothing here writes to the game folder.</para>
/// </summary>
public sealed class GameFileIndex
{
    private static readonly ConcurrentDictionary<string, GameFileIndex> Shareds = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _gameDirectory;
    private readonly string? _cachePath;
    private readonly object _gate = new();
    private State? _state;

    private sealed record State(GameChunkTable Table, GameChunkReader Reader, GameFileIndexBuild Build);

    public GameFileIndex(string gameDirectory, string? cachePath = null)
    {
        ArgumentNullException.ThrowIfNull(gameDirectory);
        _gameDirectory = Path.GetFullPath(gameDirectory);
        _cachePath = cachePath;
    }

    /// <summary>The process-wide index of a game folder (the one that holds <c>DATA/FINAL</c>).</summary>
    public static GameFileIndex Shared(string gameDirectory) =>
        Shareds.GetOrAdd(Path.GetFullPath(gameDirectory).TrimEnd('\\', '/'), static key => new GameFileIndex(key));

    /// <summary>The game folder that holds <c>DATA/FINAL</c> for a <c>DATA/FINAL</c> folder or an archive path below it; null when none is found.</summary>
    public static string? GameDirectoryOf(string finalOrArchivePath)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(finalOrArchivePath));
        for (; dir is not null; dir = dir.Parent)
            if (dir.Name.Equals("FINAL", StringComparison.OrdinalIgnoreCase) && dir.Parent?.Name.Equals("DATA", StringComparison.OrdinalIgnoreCase) == true)
                return dir.Parent.Parent?.FullName;
        return null;
    }

    public string GameDirectory => _gameDirectory;

    /// <summary>How the table was built, or null before first use.</summary>
    public GameFileIndexBuild? Build => _state?.Build;

    /// <summary>The table, built on first use. Throws <see cref="GameIndexException"/> for a folder that is no installation.</summary>
    public GameChunkTable Table => Ensure().Table;

    /// <summary>Builds the table now (otherwise the first lookup does).</summary>
    public GameFileIndexBuild EnsureBuilt() => Ensure().Build;

    private State Ensure()
    {
        var s = _state;
        if (s is not null) return s;
        lock (_gate)
        {
            if (_state is not null) return _state;
            return _state = BuildState();
        }
    }

    private State BuildState()
    {
        long before = GC.GetTotalMemory(false);
        var load = GameObjectIndexCache.LoadOrBuildTable(_gameDirectory, _cachePath);
        var t = load.Value;
        long bytes = (long)t.HashesColumn.Length * 8 + (long)t.HolderStartColumn.Length * 4 + (long)t.HoldersColumn.Length * 2;
        var build = new GameFileIndexBuild(load.Elapsed, load.FromCache, t.Archives.Count, t.ChunkCount, bytes, GC.GetTotalMemory(false) - before, t.Skipped.Count);
        return new State(t, new GameChunkReader(t), build);
    }

    /// <summary>Forgets the table, so the next lookup lists the archives again (the cache file is reused when the installation did not change).</summary>
    public void Invalidate()
    {
        lock (_gate) _state = null;
    }

    public bool Contains(ulong pathHash) => Ensure().Table.ContainsChunk(pathHash);

    public bool Contains(string gamePath) => Contains(HashAlgorithms.WadPath(Normalize(gamePath)));

    /// <summary>The archive that <see cref="TryRead(ulong, out byte[]?, string[]?)"/> would read the chunk from (see the class remarks), or null when none holds it.</summary>
    public GameArchive? HolderOf(ulong pathHash, string[]? preferredArchives = null)
    {
        var t = Ensure().Table;
        int id = PickHolder(t, pathHash, preferredArchives);
        return id < 0 ? null : t.Archive(id);
    }

    /// <summary>A reader for callers that take a function: (game path, preferred archive file name) to bytes, null when no archive holds the file.</summary>
    public Func<string, string, byte[]?> AsReader() => (path, preferred) => TryRead(path, out var b, string.IsNullOrEmpty(preferred) ? null : new[] { preferred }) ? b : null;

    public bool TryRead(string gamePath, out byte[]? bytes, string[]? preferredArchives = null) =>
        TryRead(HashAlgorithms.WadPath(Normalize(gamePath)), out bytes, preferredArchives);

    /// <summary>The decompressed bytes of the chunk, or false when no archive holds it or the read fails (a stale index is rebuilt once). Never throws for a missing or unreadable chunk.</summary>
    public bool TryRead(ulong pathHash, out byte[]? bytes, string[]? preferredArchives = null)
    {
        bytes = null;
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var s = Ensure();
                int id = PickHolder(s.Table, pathHash, preferredArchives);
                if (id < 0) return false;
                try
                {
                    bytes = s.Reader.ReadChunk(id, pathHash, GameWad.MaxChunkBytes, CancellationToken.None);
                    return true;
                }
                catch (GameChunkReadException e) when (attempt == 0 && e.Message.Contains("changed after the index was built", StringComparison.Ordinal))
                {
                    lock (_gate) { if (ReferenceEquals(_state, s)) _state = null; }
                }
            }
        }
        catch (Exception e) when (e is GameChunkReadException or GameIndexException) { }
        return false;
    }

    /// <summary>The holder rule: the first of <paramref name="preferred"/> that holds the chunk, else the first holder in id order, else -1.</summary>
    public static int PickHolder(GameChunkTable table, ulong pathHash, string[]? preferred)
    {
        var holders = table.Holders(pathHash);
        if (holders.Length == 0) return -1;
        if (preferred is { Length: > 0 })
        {
            foreach (var want in preferred)
            {
                if (string.IsNullOrWhiteSpace(want)) continue;
                foreach (int id in holders)
                    if (NameMatches(table.Archive(id), want)) return id;
            }
        }
        return holders[0];
    }

    /// <summary>A preferred archive matches by file name (<c>Kalista.wad.client</c>, <c>Kalista</c>) or by name below <c>DATA/FINAL</c> (<c>Champions/Kalista.wad.client</c>), ignoring case; a full path is reduced to its file name.</summary>
    private static bool NameMatches(GameArchive a, string want)
    {
        want = want.Replace('\\', '/');
        if (want.Contains(':') || want.StartsWith("/", StringComparison.Ordinal)) want = want[(want.LastIndexOf('/') + 1)..];
        string withSuffix = want.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase) ? want : want + ".wad.client";
        return a.Name.Equals(withSuffix, StringComparison.OrdinalIgnoreCase) || a.FileName.Equals(withSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string gamePath) => gamePath.Replace('\\', '/').TrimStart('/');
}

/// <summary>What building a <see cref="GameFileIndex"/> cost.</summary>
/// <param name="Elapsed">Wall time of loading the cache file or reading every table of contents.</param>
/// <param name="FromCache">True when the fingerprint-keyed cache file was used.</param>
/// <param name="TableBytes">Size of the table's arrays in memory.</param>
/// <param name="ManagedDelta">Change of the managed heap across the build (approximate).</param>
public sealed record GameFileIndexBuild(TimeSpan Elapsed, bool FromCache, int Archives, int Chunks, long TableBytes, long ManagedDelta, int SkippedArchives);

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>What <see cref="GameObjectIndexCache"/> returns: the value, and how it came to be.</summary>
/// <param name="Value">The chunk table or the object index of the installation as it is now.</param>
/// <param name="FromCache">True when the cache of an earlier build matched the installation and served it.</param>
/// <param name="Elapsed">The time the call took: the load, or the enumeration and the build and the save.</param>
/// <param name="CacheBytes">The size of the cache file after the call, whatever it holds, or zero when there is no file. After a save that was refused or failed it is the file that was there: the table-only cache an
/// earlier step of the same call saved, for the declarations that were not.</param>
/// <param name="CacheNote">Why the cache did not serve (<c>no cache</c>, <c>stale: ...</c>, <c>unreadable: ...</c>), or why it could not be written; null when it served.</param>
public sealed record GameIndexLoad<T>(T Value, bool FromCache, TimeSpan Elapsed, long CacheBytes, string? CacheNote)
{
    /// <summary>Why a build's result was not written to the cache (a read failed for a reason of the moment, the installation changed while it was built, or the file could not be written), or null when it
    /// was written, or when the cache served it. The part of <see cref="CacheNote"/> that says so.</summary>
    public string? NotWritten => GameObjectIndexCache.WhyNotWritten(CacheNote);
}

/// <summary>
/// M818: the chunk table and the object index kept on disk, keyed by the fingerprint of the installation they were built from (<see cref="GameArchiveList.Fingerprint"/>): the name,
/// the length and the modification time of every archive. A game that was patched, repaired or touched in any archive has another fingerprint, and the cache is rebuilt; nothing
/// of an old cache is used for a new game.
///
/// <para><b>Where.</b> <c>%LocalAppData%\ReyEngine\cache\game-objects-&lt;hash of the game directory&gt;.idx</c>: one file for each installation the editor has been pointed at, never
/// a setting. Two installations do not share one, and one installation has one, whatever it was patched from.</para>
///
/// <para><b>What the file is.</b> A header (magic, format version, fingerprint, flags, and the length and XXH3-64 of each section) and two sections: the chunk table, which
/// every target of a declaration needs and a build reads from the archives' tables of contents alone, and the object declarations, which a reference or an <c>entries</c> module needs
/// and which cost a read of every bin. A table without declarations is a valid file (a mod of <c>target</c> modules never asks for them); the declarations are added to it when
/// first asked for. Columns are little-endian integers an array wide, so that a load is a copy. The archive list is not stored: the fingerprint says the archives the call
/// enumerated are the ones the file was built from, so they are used. A file whose magic, version, fingerprint, length or checksum is not right, or whose columns do not fit their own
/// counts, is an absent cache, never an error. It is written to a sibling file and moved over the old one, so a reader sees the old file or the new and a failed write leaves the old.</para>
///
/// <para><b>What is never kept.</b> The fingerprint names the installation by its files' names, lengths and times, and none of those changes when a read fails: so an index built while a scanner
/// held an archive, or the game's patcher was replacing one, would be served for that installation by every later session, with that archive's declarations gone. An index with a read that
/// failed for a reason of the moment (<see cref="GameObjectIndex.IsSettled"/>) is therefore returned to the caller and never written, with the reason as the load's
/// <see cref="GameIndexLoad{T}.CacheNote"/>; and an installation that changed while the build ran is not written either. A bin that does not decode is the bytes, and is kept. A file that
/// holds more than <see cref="MaxCacheBytes"/> is no cache, and nothing larger is written.</para>
/// </summary>
public static class GameObjectIndexCache
{
    /// <summary>Bumped on any change to the file's layout or to what an index holds (<c>CACHE_FORMAT_VERSION</c>).</summary>
    public const uint FormatVersion = 1;

    private static readonly byte[] Magic = "RGOX"u8.ToArray();
    private const int HeaderLength = 4 + 4 + 8 + 4 + 4 + 8 + 8 + 8 + 8;
    private const uint HasDeclarations = 1;

    /// <summary>The largest cache file read or written: 256 MiB, about fourteen times the cache of the installed game (17.8 MB for 808,646 chunks and 440,618 declarations). A file larger than that is
    /// not read into memory to find out what is wrong with it.</summary>
    public const long MaxCacheBytes = 256L << 20;

    /// <summary>How old a temporary file of an interrupted save must be before the next save removes it: far longer than a save takes, so that one in progress in another process is left alone.</summary>
    private static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(10);

    private const string NotWrittenMarker = "; the cache was not written: ";

    /// <summary>The cache file of the installation at <paramref name="gameDirectory"/>.</summary>
    public static string DefaultPath(string gameDirectory)
    {
        ArgumentNullException.ThrowIfNull(gameDirectory);
        string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
        string key = Path.GetFullPath(gameDirectory).TrimEnd('\\', '/').ToLowerInvariant();
        ulong hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(key));
        return Path.Combine(root, "ReyEngine", "cache", $"game-objects-{hash:x16}.idx");
    }

    // ================================================================================================ load or build

    /// <summary>
    /// The chunk table of the installation at <paramref name="gameDirectory"/>: the cache at <paramref name="cachePath"/> when it is the cache of this installation as it stands, and a fresh
    /// build, written to it, otherwise (<c>load_or_build</c>). A cache that cannot be written is a note, not a failure: the table is returned.
    /// </summary>
    /// <exception cref="GameIndexException">The installation has no <c>DATA/FINAL</c>, or its archives cannot be listed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; nothing was written.</exception>
    public static GameIndexLoad<GameChunkTable> LoadOrBuildTable(string gameDirectory, string? cachePath = null, GameObjectIndexOptions? options = null, IProgress<GameIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        cachePath ??= DefaultPath(gameDirectory);
        var (archives, fingerprint) = ListArchives(gameDirectory, progress, cancellationToken);
        return LoadOrBuildTable(gameDirectory, archives, fingerprint, cachePath, options, progress, cancellationToken, clock);
    }

    private static GameIndexLoad<GameChunkTable> LoadOrBuildTable(string gameDirectory, IReadOnlyList<GameArchive> archives, ulong fingerprint, string cachePath, GameObjectIndexOptions? options, IProgress<GameIndexProgress>? progress, CancellationToken cancellationToken, Stopwatch clock)
    {
        progress?.Report(new GameIndexProgress(GameIndexStage.LoadingCache, 0, 0, 0, 0));
        if (TryLoadTable(cachePath, gameDirectory, archives, fingerprint, out var cached, out string note))
            return new GameIndexLoad<GameChunkTable>(cached!, true, clock.Elapsed, SizeOf(cachePath), null);

        var built = GameObjectIndexBuilder.BuildTable(gameDirectory, archives, options, progress, cancellationToken);
        progress?.Report(new GameIndexProgress(GameIndexStage.SavingCache, 0, 0, 0, 0));
        long size = TrySave(built, null, cachePath, ref note);
        return new GameIndexLoad<GameChunkTable>(built, false, clock.Elapsed, size, note);
    }

    /// <summary>
    /// The object index of <paramref name="table"/>'s installation: the declarations in the cache at <paramref name="cachePath"/> when they are those of this installation as it stands, and a
    /// read of every bin, written to the cache beside the table, otherwise.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; nothing was written.</exception>
    public static GameIndexLoad<GameObjectIndex> LoadOrBuildObjects(GameChunkTable table, string? cachePath = null, GameObjectIndexOptions? options = null, IProgress<GameIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        var clock = Stopwatch.StartNew();
        cachePath ??= DefaultPath(table.GameDirectory);
        progress?.Report(new GameIndexProgress(GameIndexStage.LoadingCache, 0, 0, 0, 0));
        if (TryLoadObjects(cachePath, table, out var cached, out string note))
            return new GameIndexLoad<GameObjectIndex>(cached!, true, clock.Elapsed, SizeOf(cachePath), null);

        var built = GameObjectIndexBuilder.BuildObjects(table, options, progress, cancellationToken);
        progress?.Report(new GameIndexProgress(GameIndexStage.SavingCache, 0, 0, built.Stats.Bins, built.Stats.Declarations));
        long size = TrySave(table, built, cachePath, ref note);
        return new GameIndexLoad<GameObjectIndex>(built, false, clock.Elapsed, size, note);
    }

    /// <summary>The object index of the installation at <paramref name="gameDirectory"/>, with its chunk table: both from the cache when it serves, and built, written, otherwise.</summary>
    /// <exception cref="GameIndexException">The installation has no <c>DATA/FINAL</c>, or its archives cannot be listed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; nothing was written.</exception>
    public static GameIndexLoad<GameObjectIndex> LoadOrBuild(string gameDirectory, string? cachePath = null, GameObjectIndexOptions? options = null, IProgress<GameIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        cachePath ??= DefaultPath(gameDirectory);
        var (archives, fingerprint) = ListArchives(gameDirectory, progress, cancellationToken);
        var table = LoadOrBuildTable(gameDirectory, archives, fingerprint, cachePath, options, progress, cancellationToken, clock);
        var objects = LoadOrBuildObjects(table.Value, cachePath, options, progress, cancellationToken);

        // why the cache did not serve is the table's reason, where it had one; why nothing was written is said whichever of the two found it out
        string? note = table.CacheNote ?? objects.CacheNote;
        if (WhyNotWritten(note) is null && WhyNotWritten(objects.CacheNote) is { } unwritten) note += NotWrittenMarker + unwritten;
        return new GameIndexLoad<GameObjectIndex>(objects.Value, table.FromCache && objects.FromCache, clock.Elapsed, objects.CacheBytes, note);
    }

    /// <summary>The reason after <see cref="NotWrittenMarker"/> in a load's note, or null when the note does not say a cache was not written.</summary>
    internal static string? WhyNotWritten(string? note)
    {
        int at = note?.IndexOf(NotWrittenMarker, StringComparison.Ordinal) ?? -1;
        return at < 0 ? null : note![(at + NotWrittenMarker.Length)..];
    }

    /// <summary><see cref="LoadOrBuild"/> on a thread of its own, for a caller that must not block (the editor's UI thread): a build takes seconds on a game never indexed, and the calling thread is free
    /// throughout. The task ends with the token's <see cref="OperationCanceledException"/> when it is cancelled, and nothing was written.</summary>
    public static Task<GameIndexLoad<GameObjectIndex>> LoadOrBuildAsync(string gameDirectory, string? cachePath = null, GameObjectIndexOptions? options = null, IProgress<GameIndexProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() => LoadOrBuild(gameDirectory, cachePath, options, progress, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary><see cref="LoadOrBuildTable"/> on a thread of its own.</summary>
    public static Task<GameIndexLoad<GameChunkTable>> LoadOrBuildTableAsync(string gameDirectory, string? cachePath = null, GameObjectIndexOptions? options = null, IProgress<GameIndexProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() => LoadOrBuildTable(gameDirectory, cachePath, options, progress, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static (IReadOnlyList<GameArchive> Archives, ulong Fingerprint) ListArchives(string gameDirectory, IProgress<GameIndexProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new GameIndexProgress(GameIndexStage.Enumerating, 0, 0, 0, 0));
        var archives = GameArchiveList.Enumerate(gameDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        return (archives, GameArchiveList.Fingerprint(archives));
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Writes the cache; a write that fails is a note and no size. An index that is not settled, or of an installation that changed while it was built, is not written: it is the answer of this
    /// session and no other's.</summary>
    private static long TrySave(GameChunkTable table, GameObjectIndex? objects, string cachePath, ref string note)
    {
        string? refusal = Unsettled(table, objects) ?? ChangedSince(table);
        if (refusal is not null)
        {
            note += NotWrittenMarker + refusal;
            return SizeOf(cachePath);   // what is there is what an earlier step saved (a table-only cache is a cache), and the size is the file's, whatever this call wrote
        }
        try
        {
            return Save(table, objects, cachePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            note += NotWrittenMarker + e.Message;
            return SizeOf(cachePath);
        }
    }

    /// <summary>Why the table, and the declarations where there are any, are not the installation's alone: the reads that failed for a reason of the moment, as a sentence; null when none did.</summary>
    private static string? Unsettled(GameChunkTable table, GameObjectIndex? objects)
    {
        var reasons = table.Unsettled.Concat(objects?.Unsettled ?? Array.Empty<string>()).ToList();
        if (reasons.Count == 0) return null;
        string shown = string.Join("; ", reasons.Take(3)) + (reasons.Count > 3 ? "; ..." : "");
        return $"a read failed for a reason of the moment, so what was built may lack what the installation holds and the next session reads it again ({shown})";
    }

    /// <summary>Whether the installation is no longer the one <paramref name="table"/> was built from: an archive written, replaced, added or removed while the build ran. Null when it is the same.</summary>
    private static string? ChangedSince(GameChunkTable table)
    {
        try
        {
            return GameArchiveList.Fingerprint(GameArchiveList.Enumerate(table.GameDirectory)) == table.Fingerprint
                ? null
                : "the installation changed while the index was being built";
        }
        catch (GameIndexException e)
        {
            return "the installation could not be listed again after the build: " + e.Message;
        }
    }

    // ================================================================================================ reading

    /// <summary>The bytes of the file at <paramref name="path"/> and its flags, or why the file is no cache of the installation with <paramref name="fingerprint"/>.</summary>
    private static bool TryOpen(string path, ulong fingerprint, out byte[] file, out uint flags, out string note)
    {
        file = Array.Empty<byte>();
        flags = 0;
        if (!BitConverter.IsLittleEndian) { note = "the cache is little-endian and this machine is not"; return false; }
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { note = "no cache"; return false; }
            // a file larger than any cache this code writes is not read into memory to be told it is wrong
            if (info.Length > MaxCacheBytes) { note = $"unreadable: larger than the {MaxCacheBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes a cache holds"; return false; }
            file = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            note = "unreadable: " + e.Message;
            return false;
        }
        if (file.Length > MaxCacheBytes) { file = Array.Empty<byte>(); note = "unreadable: larger than a cache holds"; return false; }
        if (file.Length < HeaderLength || !file.AsSpan(0, 4).SequenceEqual(Magic)) { note = "unreadable: not an index"; return false; }
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        if (version != FormatVersion) { note = $"stale: format version {version}, expected {FormatVersion}"; return false; }
        ulong cached = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(8));
        if (cached != fingerprint) { note = $"stale: built for fingerprint {cached:x16}, the installation is {fingerprint:x16}"; return false; }
        flags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16));
        note = "";
        return true;
    }

    /// <summary>The checksummed section <paramref name="which"/> of the file (0: the table, 1: the declarations); empty, with the reason in <paramref name="note"/>, when the file does not hold it whole.</summary>
    private static ReadOnlySpan<byte> Section(byte[] file, int which, out string note)
    {
        ulong tableLength = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(24));
        int header = 24 + which * 16;
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(header));
        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(header + 8));
        ulong start = (ulong)HeaderLength + (which == 0 ? 0 : tableLength);
        if (start > (ulong)file.Length || length > (ulong)file.Length - start) { note = "unreadable: truncated"; return default; }
        var body = file.AsSpan((int)start, (int)length);
        if (XxHash3.HashToUInt64(body) != hash) { note = "unreadable: checksum"; return default; }
        note = "";
        return body;
    }

    /// <summary>Reads the chunk table from the cache at <paramref name="path"/> for the installation <paramref name="archives"/> lists. False, with the reason, for a file that is absent,
    /// unreadable, of another version, or of another installation state.</summary>
    public static bool TryLoadTable(string path, string gameDirectory, IReadOnlyList<GameArchive> archives, ulong fingerprint, out GameChunkTable? table, out string note)
    {
        table = null;
        if (!TryOpen(path, fingerprint, out var file, out _, out note)) return false;
        try
        {
            var body = Section(file, 0, out note);
            if (note.Length != 0) return false;
            table = ReadTable(body, gameDirectory, archives, fingerprint);
            note = "";
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            note = "unreadable: " + e.Message;
            return false;
        }
    }

    /// <summary>Reads the declarations from the cache at <paramref name="path"/>, which must be those of the installation <paramref name="table"/> describes and must hold them.</summary>
    public static bool TryLoadObjects(string path, GameChunkTable table, out GameObjectIndex? index, out string note)
    {
        index = null;
        if (!TryOpen(path, table.Fingerprint, out var file, out uint flags, out note)) return false;
        if ((flags & HasDeclarations) == 0) { note = "no declarations in the cache"; return false; }
        try
        {
            var body = Section(file, 1, out note);
            if (note.Length != 0) return false;
            index = ReadObjects(body, table);
            note = "";
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            note = "unreadable: " + e.Message;
            return false;
        }
    }

    private static GameChunkTable ReadTable(ReadOnlySpan<byte> body, string gameDirectory, IReadOnlyList<GameArchive> archives, ulong fingerprint)
    {
        var reader = new Reader(body);
        if (reader.Int() != archives.Count) throw new InvalidDataException("another number of archives");

        var skipped = new SkippedGameArchive[reader.Count(4)];
        for (int i = 0; i < skipped.Length; i++)
        {
            int id = reader.UShort();
            if (id >= archives.Count) throw new InvalidDataException("a skipped archive that is not listed");
            if (i > 0 && id <= skipped[i - 1].Archive) throw new InvalidDataException("skipped archives out of order");
            skipped[i] = new SkippedGameArchive(id, archives[id].Name, reader.String());
        }

        int chunkCount = reader.Count(8 + 2);
        var hashes = reader.Array<ulong>(chunkCount);
        var holderCounts = reader.Array<ushort>(chunkCount);
        int holderTotal = reader.Count(2);
        var holders = reader.Array<ushort>(holderTotal);
        var holderStart = new int[chunkCount + 1];
        long sum = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            if (holderCounts[i] == 0) throw new InvalidDataException("a chunk no archive holds");
            holderStart[i] = (int)sum;
            sum += holderCounts[i];
            if (i > 0 && hashes[i] <= hashes[i - 1]) throw new InvalidDataException("chunk hashes out of order");
        }
        if (sum != holderTotal) throw new InvalidDataException("holders do not add up");
        holderStart[chunkCount] = holderTotal;
        foreach (ushort holder in holders)
            if (holder >= archives.Count) throw new InvalidDataException("a holder that is not listed");
        if (reader.Remaining != 0) throw new InvalidDataException("bytes after the columns");

        return new GameChunkTable(gameDirectory, fingerprint, archives.ToArray(), skipped, hashes, holderStart, holders);
    }

    private static GameObjectIndex ReadObjects(ReadOnlySpan<byte> body, GameChunkTable table)
    {
        var reader = new Reader(body);
        var stats = new GameObjectIndexStats(reader.Int(), reader.Int(), reader.Int(), reader.Int(), reader.Long(), TimeSpan.FromTicks(reader.Long()), reader.Int());
        int count = reader.Count(4 + 4 + 8 + 2);
        // what the build counted must be what it stored: a count that disagrees with its own columns is a file that is not one this code wrote, whatever its checksum says
        if (stats.Sniffed < 0 || stats.Bins < 0 || stats.SkippedChunks < 0 || stats.Bytes < 0 || stats.Workers < 0 || stats.Declarations != count)
            throw new InvalidDataException("counts that do not match the declarations");
        var objects = reader.Array<uint>(count);
        var classes = reader.Array<uint>(count);
        var chunks = reader.Array<ulong>(count);
        var declaring = reader.Array<ushort>(count);
        if (reader.Remaining != 0) throw new InvalidDataException("bytes after the columns");
        // a chunk is read from its first holder only, so a declaration is of a chunk the table lists and the declaring archive holds first: one run of declarations is one bin, and a run costs one lookup
        for (int i = 0; i < count; i++)
        {
            if (declaring[i] >= table.Archives.Count) throw new InvalidDataException("a declaring archive that is not listed");
            if (i > 0 && chunks[i] == chunks[i - 1] && declaring[i] == declaring[i - 1]) continue;
            if (table.FirstHolder(chunks[i]) != declaring[i]) throw new InvalidDataException("a declaration of a chunk its archive does not hold first");
        }
        return new GameObjectIndex(table, stats, objects, classes, chunks, declaring);
    }

    // ================================================================================================ writing

    /// <summary>
    /// Writes the cache of <paramref name="table"/>, and of <paramref name="objects"/> when there are any, to <paramref name="path"/> through a sibling file, and answers its size. The temporary
    /// files an earlier save left behind by dying between its write and its move are removed first.
    /// </summary>
    /// <exception cref="ArgumentException">The declarations are of another installation state than the table, or a read failed for a reason of the moment while either was built
    /// (<see cref="GameObjectIndex.IsSettled"/>): that answer is not the installation's, and is not kept.</exception>
    /// <exception cref="IOException">The file could not be written, or would be larger than <see cref="MaxCacheBytes"/>.</exception>
    public static long Save(GameChunkTable table, GameObjectIndex? objects, string path)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (objects is not null && objects.Fingerprint != table.Fingerprint) throw new ArgumentException("the declarations are of another installation state than the table", nameof(objects));
        if (Unsettled(table, objects) is { } unsettled) throw new ArgumentException("the index is not settled: " + unsettled, nameof(table));

        var tableBody = new Writer();
        tableBody.Int(table.Archives.Count);
        tableBody.Int(table.Skipped.Count);
        foreach (var skip in table.Skipped)
        {
            tableBody.UShort((ushort)skip.Archive);
            tableBody.String(skip.Error);
        }
        int chunkCount = table.ChunkCount;
        tableBody.Int(chunkCount);
        tableBody.Array(table.HashesColumn);
        var counts = new ushort[chunkCount];
        var start = table.HolderStartColumn;
        for (int i = 0; i < chunkCount; i++) counts[i] = (ushort)(start[i + 1] - start[i]);
        tableBody.Array(counts);
        tableBody.Int(table.HoldersColumn.Length);
        tableBody.Array(table.HoldersColumn);
        byte[] tableBytes = tableBody.ToArray();

        byte[] objectBytes = Array.Empty<byte>();
        if (objects is not null)
        {
            var body = new Writer();
            var stats = objects.Stats;
            body.Int(stats.Sniffed); body.Int(stats.Bins); body.Int(stats.Declarations); body.Int(stats.SkippedChunks);
            body.Long(stats.Bytes); body.Long(stats.Elapsed.Ticks); body.Int(stats.Workers);
            body.Int(objects.DeclarationCount);
            body.Array(objects.ObjectsColumn);
            body.Array(objects.ClassesColumn);
            body.Array(objects.ChunksColumn);
            body.Array(objects.DeclaringArchiveColumn);
            objectBytes = body.ToArray();
        }

        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), table.Fingerprint);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), objects is null ? 0 : HasDeclarations);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), (ulong)tableBytes.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), XxHash3.HashToUInt64(tableBytes));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40), (ulong)objectBytes.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48), objects is null ? 0 : XxHash3.HashToUInt64(objectBytes));

        long total = HeaderLength + tableBytes.Length + objectBytes.Length;
        if (total > MaxCacheBytes) throw new IOException($"the cache would be {total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes, more than the {MaxCacheBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} a cache holds");

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            RemoveOrphans(directory, Path.GetFileName(path));
        }
        string temporary = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{System.Environment.ProcessId}.{DateTime.UtcNow.Ticks}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                stream.Write(header);
                stream.Write(tableBytes);
                stream.Write(objectBytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return total;
    }

    /// <summary>Removes the temporary files a save left when it died between writing and moving (<c>.name.pid.ticks.tmp</c>) that are older than <see cref="OrphanAge"/>. A file that cannot be removed
    /// is left: it is only space.</summary>
    private static void RemoveOrphans(string directory, string fileName)
    {
        try
        {
            foreach (string orphan in Directory.EnumerateFiles(directory, $".{fileName}.*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(orphan) < DateTime.UtcNow - OrphanAge) File.Delete(orphan);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A growing body of little-endian values.</summary>
    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public void Int(int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void Long(long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void UShort(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void String(string text)
        {
            var utf8 = Encoding.UTF8.GetBytes(text);
            if (utf8.Length > ushort.MaxValue) utf8 = utf8.AsSpan(0, ushort.MaxValue).ToArray();
            UShort((ushort)utf8.Length);
            _stream.Write(utf8);
        }

        public void Array<T>(T[] values) where T : unmanaged => _stream.Write(MemoryMarshal.AsBytes(values.AsSpan()));

        public byte[] ToArray() => _stream.ToArray();
    }

    /// <summary>A body read forward. Every count is checked against the bytes left before anything is allocated for it.</summary>
    private ref struct Reader
    {
        private ReadOnlySpan<byte> _rest;

        public Reader(ReadOnlySpan<byte> body) { _rest = body; }

        public readonly int Remaining => _rest.Length;

        private ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || length > _rest.Length) throw new InvalidDataException("the file ends inside a value");
            var taken = _rest[..length];
            _rest = _rest[length..];
            return taken;
        }

        public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public long Long() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public int UShort() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        /// <summary>A count of values that take at least <paramref name="width"/> bytes each, which the bytes left must be able to hold.</summary>
        public int Count(int width)
        {
            int count = Int();
            if (count < 0 || (long)count * width > _rest.Length) throw new InvalidDataException("a count the file cannot hold");
            return count;
        }

        public string String()
        {
            int length = UShort();
            return Encoding.UTF8.GetString(Take(length));
        }

        public T[] Array<T>(int count) where T : unmanaged
        {
            var bytes = Take(checked(count * System.Runtime.CompilerServices.Unsafe.SizeOf<T>()));
            var values = new T[count];
            bytes.CopyTo(MemoryMarshal.AsBytes(values.AsSpan()));
            return values;
        }
    }
}

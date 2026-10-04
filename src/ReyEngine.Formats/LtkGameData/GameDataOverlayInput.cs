using System.Text;
using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>The files of one layer the declarations name: the <c>.ptch</c> override files of its modules (<c>META/game_data/&lt;layer&gt;/&lt;path&gt;</c> in a package).</summary>
public interface IGameDataLayerFiles
{
    /// <summary>The bytes of the override file at the layer-relative path (forward slashes), or null when the layer holds none.</summary>
    /// <param name="path">The path as the document spells it. A provider may compare it without regard to case.</param>
    /// <param name="maxBytes">The most the overlay reads: a provider refuses a longer file before it reads it.</param>
    /// <exception cref="IOException">The file exists and cannot be read, or is longer than <paramref name="maxBytes"/>.</exception>
    byte[]? ReadOverrideFile(string path, long maxBytes);
}

/// <summary>The mod's own copies of chunks: what a target's base is when the mod ships the chunk itself (<c>read_wad_override_file</c> and <c>read_raw_override_file</c>).</summary>
public interface IGameDataModFiles
{
    /// <summary>The mod's file for <paramref name="chunk"/> in the WAD folders of <paramref name="layer"/>, or null when the layer holds none. A layer's WAD folders are one namespace:
    /// a chunk path hash names one file in them, whatever the WAD folder.</summary>
    /// <exception cref="IOException">The file exists and cannot be read, or is longer than <paramref name="maxBytes"/>.</exception>
    byte[]? ReadLayerFile(string layer, ulong chunk, long maxBytes);

    /// <summary>The mod's <c>RAW</c> file for <paramref name="chunk"/>, or null. Only the base layer holds a <c>RAW</c> folder, and what it holds wins over every layer's WAD folders.</summary>
    /// <exception cref="IOException">The file exists and cannot be read, or is longer than <paramref name="maxBytes"/>.</exception>
    byte[]? ReadRawFile(ulong chunk, long maxBytes);
}

/// <summary>A mod that ships no chunk of its own: every base is the game's.</summary>
public sealed class NoModFiles : IGameDataModFiles
{
    public static readonly NoModFiles Instance = new();

    private NoModFiles() { }

    public byte[]? ReadLayerFile(string layer, ulong chunk, long maxBytes) => null;

    public byte[]? ReadRawFile(ulong chunk, long maxBytes) => null;
}

/// <summary>
/// The installed game as the overlay needs it: where each chunk lives, what the chunks declare, and a chunk's bytes. <see cref="InstalledGame"/> is the implementation over a game directory;
/// the interface is what lets a caller serve the overlay from anywhere else, or fail it on purpose.
///
/// <para>A member that cannot answer throws, and the overlay turns the exception into the diagnostic the crate gives the same failure: an object index that does not load is
/// <see cref="GameDataOverlayDiagnosticKind.IndexUnavailable"/> and a chunk table that does not is a target skipped. Only <see cref="OperationCanceledException"/> is not a failure: it ends
/// the overlay's call. Every member may be called from any thread, and an answer is the same for the life of the overlay.</para>
/// </summary>
public interface IGameDataGame : IGameChunkReader
{
    /// <summary>Which archives hold each chunk. Cheap: it reads tables of contents, not chunks.</summary>
    GameChunkTable GetTable(CancellationToken cancellationToken);

    /// <summary>Which chunks declare each object. Costs a read of every bin the first time for a game state (<see cref="GameObjectIndexCache"/>).</summary>
    GameObjectIndex GetObjects(CancellationToken cancellationToken);

    /// <summary>Why what the game built of its index was not kept for the next session, once it has been asked for: a read failed for a reason of the moment, the installation changed while it was
    /// built, or the cache could not be written. Null when it was kept or was served from the cache, and for a game that keeps nothing.</summary>
    string? IndexNotSaved => null;
}

/// <summary>
/// M818: <see cref="IGameDataGame"/> over an installation. The chunk table and the object index are loaded from the cache, or built, the first time they are asked for: on the asking thread
/// (the call blocks it, so a caller that must not block asks from a thread of its own, or through <see cref="GetTableAsync"/> and <see cref="GetObjectsAsync"/>), once however many threads
/// ask, and cancellably. A failure to build is remembered for the life of the object, as the crate remembers it for the life of a build; a cancellation is not.
/// </summary>
public sealed class InstalledGame : IGameDataGame
{
    /// <summary>What the table's load produced, with the reader made for that table: one object, published whole and last, so that whoever sees the table sees the reader that reads from it.</summary>
    private sealed record Loaded(GameIndexLoad<GameChunkTable> Load, GameChunkReader Reader);

    private readonly string _gameDirectory;
    private readonly string _cachePath;
    private readonly GameObjectIndexOptions? _options;
    private readonly IProgress<GameIndexProgress>? _progress;
    // held across a whole build, which takes seconds, so a waiter's own token ends its wait (a Monitor would not): the lock is not reentrant, and nothing the build calls asks for it
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Exception? _tableFailure;
    private Exception? _objectsFailure;
    private Loaded? _table;
    private GameIndexLoad<GameObjectIndex>? _objects;

    public InstalledGame(string gameDirectory, string? cachePath = null, GameObjectIndexOptions? options = null, IProgress<GameIndexProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDirectory);
        _gameDirectory = gameDirectory;
        _cachePath = cachePath ?? GameObjectIndexCache.DefaultPath(gameDirectory);
        _options = options;
        _progress = progress;
    }

    /// <summary>The game directory (the one that holds <c>DATA/FINAL</c>).</summary>
    public string GameDirectory => _gameDirectory;

    /// <summary>How the chunk table came to be, once it has been asked for; null before.</summary>
    public GameIndexLoad<GameChunkTable>? TableLoad => Volatile.Read(ref _table)?.Load;

    /// <summary>How the object index came to be, once it has been asked for; null before.</summary>
    public GameIndexLoad<GameObjectIndex>? ObjectsLoad => Volatile.Read(ref _objects);

    /// <inheritdoc />
    public string? IndexNotSaved
    {
        get
        {
            var reasons = new[] { TableLoad?.NotWritten, ObjectsLoad?.NotWritten }.Where(r => r is not null).Distinct().ToList();
            return reasons.Count == 0 ? null : string.Join("; ", reasons);
        }
    }

    private Loaded Table(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _table) is { } done) return done;
        _gate.Wait(cancellationToken);
        try
        {
            if (_table is { } again) return again;
            if (_tableFailure is not null) throw new InvalidOperationException(_tableFailure.Message, _tableFailure);
            try
            {
                var load = GameObjectIndexCache.LoadOrBuildTable(_gameDirectory, _cachePath, _options, _progress, cancellationToken);
                var loaded = new Loaded(load, new GameChunkReader(load.Value));
                Volatile.Write(ref _table, loaded);
                return loaded;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                _tableFailure = e;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public GameChunkTable GetTable(CancellationToken cancellationToken) => Table(cancellationToken).Load.Value;

    public GameObjectIndex GetObjects(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _objects) is { } done) return done.Value;
        var table = Table(cancellationToken).Load.Value;
        _gate.Wait(cancellationToken);
        try
        {
            if (_objects is { } again) return again.Value;
            if (_objectsFailure is not null) throw new InvalidOperationException(_objectsFailure.Message, _objectsFailure);
            try
            {
                var load = GameObjectIndexCache.LoadOrBuildObjects(table, _cachePath, _options, _progress, cancellationToken);
                Volatile.Write(ref _objects, load);
                return load.Value;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                _objectsFailure = e;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc cref="IGameChunkReader.ReadChunk" />
    public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken) =>
        Table(cancellationToken).Reader.ReadChunk(archive, chunk, maxBytes, cancellationToken);

    /// <summary><see cref="GetTable"/> on a thread of its own, for a caller that must not block.</summary>
    public Task<GameChunkTable> GetTableAsync(CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() => GetTable(cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary><see cref="GetObjects"/> on a thread of its own, for a caller that must not block: the first call for a game state reads every bin of it.</summary>
    public Task<GameObjectIndex> GetObjectsAsync(CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() => GetObjects(cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}

/// <summary>
/// One layer of the mod as the overlay reads it: its name and priority, and the GameData document the layer declares (<c>Layers.&lt;name&gt;.GameData</c> of a package) as the text it was
/// written in, with the files its modules name.
/// </summary>
/// <param name="Name">The layer's name. <c>base</c>, spelled so, is the base layer.</param>
/// <param name="Priority">The layer's priority: a higher number is applied over a lower one.</param>
/// <param name="DocumentText">The document's text, or null when the layer declares none.</param>
/// <param name="Files">The override files the document names, or null when there are none.</param>
/// <param name="DocumentProblem">Why a document the layer does declare could not be given (it is too large, or its file could not be read); the layer is then rejected with this as the reason.</param>
public sealed record GameDataLayerInput(string Name, int Priority, string? DocumentText, IGameDataLayerFiles? Files = null, string? DocumentProblem = null)
{
    /// <summary>The layers of <paramref name="project"/> that declare GameData, and the others, as overlay inputs. A document larger than <paramref name="maxDocumentBytes"/>, one that would take the
    /// documents read so far past <paramref name="maxTotalBytes"/>, or a file that cannot be read is a <see cref="DocumentProblem"/>, which rejects that layer and leaves the rest. The documents are
    /// read in the order the overlay applies the layers in, so it is the layers applied last that the total refuses. The base layer is present whether or not the project lists it.</summary>
    /// <param name="project">A project that has been opened from a folder.</param>
    /// <param name="maxDocumentBytes">The largest document read: <see cref="GameDataOverlayOptions.MaxDocumentBytes"/> by default.</param>
    /// <param name="maxTotalBytes">The most the documents of all layers may hold together: <see cref="GameDataOverlayOptions.DefaultMaxTotalDocumentBytes"/> by default.</param>
    public static IReadOnlyList<GameDataLayerInput> FromProject(
        ReyProject project, long maxDocumentBytes = GameDataOverlayOptions.DefaultMaxDocumentBytes, long maxTotalBytes = GameDataOverlayOptions.DefaultMaxTotalDocumentBytes)
    {
        ArgumentNullException.ThrowIfNull(project);
        var layers = new List<GameDataLayerInput>();
        long remaining = maxTotalBytes;
        var ordered = project.Layers.ToList();
        ordered.Sort((a, b) => GameDataLayerOrder.Compare(a.Name, a.Priority, b.Name, b.Priority));
        foreach (var layer in ordered)
        {
            string? text = null, problem = null;
            IGameDataLayerFiles? files = null;
            if (project.RootPath is not null && LtkProjectStore.IsSafeKey(layer.DeclarationsKey))
            {
                string document = Path.Combine(LtkProjectStore.DirectoryOf(project.RootPath, layer.DeclarationsKey!), LtkProjectStore.DeclarationsFileName);
                try
                {
                    var info = new FileInfo(document);
                    if (info.Exists)
                    {
                        files = new StoredLayerFiles(LtkProjectStore.FilesDirectoryOf(project.RootPath, layer.DeclarationsKey!));
                        if (info.Length > maxDocumentBytes) problem = $"the document is {GameDataOverlayDiagnostics.Count(info.Length)} bytes, more than the {GameDataOverlayDiagnostics.Count(maxDocumentBytes)} the overlay reads";
                        else if (info.Length > remaining) problem = $"the documents together are more than the {GameDataOverlayDiagnostics.Count(maxTotalBytes)} bytes the overlay reads";
                        else
                        {
                            byte[] bytes = BoundedFile.Read(document, Math.Min(maxDocumentBytes, remaining));
                            remaining -= bytes.Length;
                            text = new UTF8Encoding(false).GetString(bytes);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    problem = "the document cannot be read: " + e.Message;
                }
            }
            layers.Add(new GameDataLayerInput(layer.Name, layer.Priority, text, files, problem));
        }
        if (!layers.Any(l => l.Name == ProjectLayer.BaseLayer)) layers.Add(new GameDataLayerInput(ProjectLayer.BaseLayer, 0, null));
        return layers;
    }

    /// <summary>The override files a project stores for a layer (<see cref="LtkProjectStore"/>): only the listed files can be read, so a path the document spells cannot reach outside the folder.</summary>
    private sealed class StoredLayerFiles : IGameDataLayerFiles
    {
        private readonly string _directory;
        // listed on the first ask, whichever thread makes it; a listing that fails is not kept, and the next ask lists again
        private readonly Lazy<Dictionary<string, string>> _byPath;

        public StoredLayerFiles(string directory)
        {
            _directory = directory;
            _byPath = new Lazy<Dictionary<string, string>>(List, LazyThreadSafetyMode.PublicationOnly);
        }

        public byte[]? ReadOverrideFile(string path, long maxBytes)
        {
            if (!_byPath.Value.TryGetValue(path.Replace('\\', '/').ToUpperInvariant(), out string? full)) return null;
            return BoundedFile.Read(full, maxBytes);
        }

        private Dictionary<string, string> List()
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(_directory)) return found;
            foreach (string file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
                found.TryAdd(Path.GetRelativePath(_directory, file).Replace('\\', '/').ToUpperInvariant(), file);
            return found;
        }
    }
}

/// <summary>How the overlay runs, and what it refuses to read.</summary>
public sealed class GameDataOverlayOptions
{
    /// <summary>16 MiB: more than twenty times the whole <c>info.json</c> of the largest package measured (Crauzer's Winter Rift, 714 KB, three layers).</summary>
    public const long DefaultMaxDocumentBytes = 16L << 20;

    /// <summary>The mod the layers belong to, as diagnostics name it (<c>mod_id</c>).</summary>
    public string ModId { get; init; } = "project";

    /// <summary>The layers in play. Null is all of them (a layer is active by default); the base layer is always active, whatever this holds. An inactive layer contributes no GameData and no files.</summary>
    public IReadOnlySet<string>? ActiveLayers { get; init; }

    /// <summary>Types the property edits and knows the classes: <see cref="LtkMetaSchema.At(uint?)"/> of the installed build, or <see cref="NoSchema.Instance"/>.</summary>
    public IGameDataSchema Schema { get; init; } = NoSchema.Instance;

    /// <summary>What one application may cost. The default is <see cref="GameDataLimits.Default"/>: for content that is not hostile, and generous for what is.</summary>
    public GameDataLimits Limits { get; init; } = GameDataLimits.Default;

    /// <summary>The largest GameData document of one layer, in UTF-8 bytes. A larger one rejects its layer.</summary>
    public long MaxDocumentBytes { get; init; } = DefaultMaxDocumentBytes;

    /// <summary>The largest override file read, in bytes: <see cref="GameDataLimits.DefaultMaxOverrideBytes"/>, as much as the largest bin of the installed game and five hundred times its largest PTCH.</summary>
    public long MaxOverrideFileBytes { get; init; } = GameDataLimits.DefaultMaxOverrideBytes;

    /// <summary>The most applications the modules of all layers may make: a target module makes one, an <c>entries</c> module one for each entry in each chunk that declares it. 250,000 is a thousand
    /// times what a mod of the installed game's bins needs (Crauzer's Winter Rift: 13 modules); a document that asks for more is a package that does not play fair, and the rest is dropped.</summary>
    public int MaxApplications { get; init; } = 250_000;

    /// <summary>The units of work (<see cref="GameDataLimits"/>) all the applications to ONE chunk may use together: each application re-decodes what the one before it left, so a package that names
    /// a large bin in thousands of modules would otherwise cost thousands of decodes. The default is four times <see cref="GameDataLimits.DefaultMaxWork"/>, about 170 decodes and writes of the largest
    /// bin of the installed game, and no real mod comes near it (Crauzer's Winter Rift applies at most 7 modules to a chunk). The applications past it are skipped, and the chunk says so.</summary>
    public long MaxWorkPerChunk { get; init; } = 4 * GameDataLimits.DefaultMaxWork;

    /// <summary>64 MiB: about twice the largest bin of the installed game (Map22.wad.client, 33 MB). The bytes of a base are decoded into a tree that takes up to forty times as much memory, so what a base
    /// may weigh is not what the output limit says (256 MiB): a package's own copy of a target that is 200 MiB of single-byte values would decode to gigabytes inside the work limit.</summary>
    public const long DefaultMaxBaseChunkBytes = 64L << 20;

    /// <summary>64 MiB: what the documents of all the layers of a project may hold together (<see cref="GameDataLayerInput.FromProject"/>), four of the largest document each layer may hold and about ninety
    /// times the largest package measured (Crauzer's Winter Rift, 714 KB).</summary>
    public const long DefaultMaxTotalDocumentBytes = 64L << 20;

    /// <summary>The largest chunk of a mod's own read as a base, in bytes: <see cref="DefaultMaxBaseChunkBytes"/>, whatever <see cref="Limits"/> allows an application to write. A provider is asked
    /// for no more, and what it returns is measured again.</summary>
    public long MaxModChunkBytes { get; init; } = DefaultMaxBaseChunkBytes;

    /// <summary>The largest chunk of the game read as a base, in bytes: <see cref="DefaultMaxBaseChunkBytes"/>. The archive's table of contents says how large a chunk is, and a target that names a
    /// large texture or sound is refused on that before a byte of it is read.</summary>
    public long MaxGameChunkBytes { get; init; } = DefaultMaxBaseChunkBytes;

    /// <summary>What the references of the package may spend in all (<see cref="GameEntryReader"/>).</summary>
    public GameEntryLimits References { get; init; } = GameEntryLimits.Default;

    /// <summary>The most bytes of results the overlay keeps for the next ask, 512 MiB: about seventeen times what Crauzer's Winter Rift produces. A result past it is returned and not kept, and computed
    /// again if it is asked for again.</summary>
    public long MaxCachedBytes { get; init; } = 512L << 20;
}

/// <summary>Reads a whole file without trusting its length: the bytes are taken as far as the limit and one more, so a file that grew past what was asked is refused, not read.</summary>
internal static class BoundedFile
{
    /// <exception cref="IOException">The file holds more than <paramref name="maxBytes"/> or cannot be read.</exception>
    public static byte[] Read(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        long length = stream.Length;
        if (length > maxBytes) throw TooLarge(length, maxBytes);
        var buffer = new byte[length];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        // a file that grew after its length was read holds more than it said
        if (total == buffer.Length && stream.ReadByte() >= 0) throw TooLarge(length + 1, maxBytes);
        if (total < buffer.Length) Array.Resize(ref buffer, total);
        return buffer;
    }

    private static IOException TooLarge(long length, long maxBytes) =>
        new($"the file is {GameDataOverlayDiagnostics.Count(length)} bytes, more than the {GameDataOverlayDiagnostics.Count(maxBytes)} the overlay reads");
}

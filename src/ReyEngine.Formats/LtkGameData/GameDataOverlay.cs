using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>Where a target's base bytes come from.</summary>
public enum GameDataBaseKind
{
    /// <summary>No base: it could not be found or read, and the target is skipped.</summary>
    None,
    /// <summary>The game's copy, from the chunk's first holder.</summary>
    Game,
    /// <summary>The mod's copy in the WAD folders of a layer (<see cref="GameDataChunkResult.BaseLayer"/>).</summary>
    ModLayer,
    /// <summary>The mod's <c>RAW</c> copy.</summary>
    ModRaw,
}

/// <summary>A layer as the plan found it: whether it plays, what its document gave, and the chunks its modules touch.</summary>
/// <param name="Name">The layer's name.</param>
/// <param name="Priority">The layer's priority.</param>
/// <param name="Active">Whether the layer is in play. An inactive layer contributes no GameData and no files.</param>
/// <param name="HasDocument">Whether the layer declares a document.</param>
/// <param name="Rejected">Whether the document was refused (a <see cref="GameDataOverlayDiagnosticKind.DeclarationsRejected"/> diagnostic says why).</param>
/// <param name="Modules">The modules the layer contributes.</param>
/// <param name="Targets">The chunks its modules touch, each once, in the order they are first touched: a <c>target</c> module's chunk, and every chunk an <c>entries</c> module's entries are declared in.</param>
public sealed record GameDataPlannedLayer(string Name, int Priority, bool Active, bool HasDocument, bool Rejected, int Modules, IReadOnlyList<ulong> Targets);

/// <summary>One chunk the declarations touch: how many applications it takes, from which layers, and which game archives hold it.</summary>
/// <param name="Chunk">The chunk's path hash.</param>
/// <param name="Applications">The number of applications: one for each target module naming it, and one for each entry of an <c>entries</c> module declared in it.</param>
/// <param name="Layers">The layers that touch it, each once, in apply order.</param>
/// <param name="Holders">The ids of the game archives that hold it, in id order (<see cref="GameChunkTable.Holders"/>); empty for a chunk the game does not have, or when the table is unavailable.</param>
/// <param name="HolderPaths">The paths of <paramref name="Holders"/> below the game directory (<c>DATA/FINAL/Champions/Aatrox.wad.client</c>): the WADs an overlay routes the chunk to.</param>
public sealed record GameDataTarget(ulong Chunk, int Applications, IReadOnlyList<string> Layers, IReadOnlyList<int> Holders, IReadOnlyList<string> HolderPaths);

/// <summary>
/// What the declarations of all layers will do, before a byte of a chunk is read: the layers in apply order with the chunks each touches, every chunk with the archives that hold it, and the
/// diagnostics that need no chunk (<see cref="GameDataOverlayDiagnosticKind.DeclarationsRejected"/> and everything <c>ltk_overlay</c> reports while it binds modules to chunks).
/// </summary>
public sealed class GameDataPlan
{
    internal GameDataPlan(
        IReadOnlyList<GameDataPlannedLayer> layers, IReadOnlyList<GameDataTarget> targets, IReadOnlyList<GameDataOverlayDiagnostic> diagnostics,
        bool usedObjectIndex, string? objectIndexError, string? tableError, SortedDictionary<ulong, List<GameDataOverlay.Application>> applications,
        IReadOnlyList<SkippedGameArchive> skippedArchives, IReadOnlyList<string> indexUnsettled, string? indexNotSaved)
    {
        Layers = layers;
        Targets = targets;
        Diagnostics = diagnostics;
        UsedObjectIndex = usedObjectIndex;
        ObjectIndexError = objectIndexError;
        TableError = tableError;
        Applications = applications;
        SkippedArchives = skippedArchives;
        IndexUnsettled = indexUnsettled;
        IndexNotSaved = indexNotSaved;
    }

    /// <summary>Every layer, in apply order: the base layer first, then by priority and by name as a person reads it.</summary>
    public IReadOnlyList<GameDataPlannedLayer> Layers { get; }

    /// <summary>The chunks to apply to, by ascending path hash: the order <c>ltk_overlay</c> applies them in.</summary>
    public IReadOnlyList<GameDataTarget> Targets { get; }

    /// <summary>The diagnostics of the plan, in the order the crate reports them: layers refused, then each module in order.</summary>
    public IReadOnlyList<GameDataOverlayDiagnostic> Diagnostics { get; }

    /// <summary>Whether a module needed the object index: a non-empty <c>entries</c> module, a reference, or an object creation. A <c>PTCH</c> target asks for it later, when it is applied.</summary>
    public bool UsedObjectIndex { get; }

    /// <summary>Why the object index was unavailable, where it was asked for and could not be had; null otherwise.</summary>
    public string? ObjectIndexError { get; }

    /// <summary>Why the chunk table (which archives hold each chunk) was unavailable, where a target needed it; null otherwise.</summary>
    public string? TableError { get; }

    /// <summary>The game archives the index could not read (<see cref="GameChunkTable.Skipped"/>), as far as the plan used the game's index: each holds no chunk, so a target that lives in one reads as absent.
    /// <see cref="SkippedGameArchive.Transient"/> says which are failures of the moment (a file held by a scanner or the patcher) and which are bytes that are no archive. Empty when the plan did not ask.</summary>
    public IReadOnlyList<SkippedGameArchive> SkippedArchives { get; }

    /// <summary>The reads that failed for a reason of the moment while the game's index was built (<see cref="GameObjectIndex.Unsettled"/>, <see cref="GameChunkTable.Unsettled"/>), worded for a person: what the
    /// index lacks because of them is not the installation's, and the index was not kept for the next session. Empty when the index is settled, or the plan did not ask.</summary>
    public IReadOnlyList<string> IndexUnsettled { get; }

    /// <summary>Why what the game built of its index was not kept for the next session (<see cref="IGameDataGame.IndexNotSaved"/>): the reads that failed, a change to the installation while it was built, or a
    /// cache that could not be written. Null when it was kept, was served from the cache, or the plan did not ask.</summary>
    public string? IndexNotSaved { get; }

    internal SortedDictionary<ulong, List<GameDataOverlay.Application>> Applications { get; }

    /// <summary>The target of <paramref name="chunk"/>, or null for a chunk no module touches.</summary>
    public GameDataTarget? Target(ulong chunk)
    {
        int low = 0, high = Targets.Count - 1;
        while (low <= high)
        {
            int mid = (low + high) >>> 1;
            int order = Targets[mid].Chunk.CompareTo(chunk);
            if (order == 0) return Targets[mid];
            if (order < 0) low = mid + 1;
            else high = mid - 1;
        }
        return null;
    }
}

/// <summary>What the overlay does to one chunk.</summary>
public sealed class GameDataChunkResult
{
    internal GameDataChunkResult(
        ulong chunk, GameDataBaseKind baseKind, string? baseLayer, int baseArchive, ulong baseFingerprint, ulong documentsFingerprint,
        bool applied, byte[]? bytes, IReadOnlyList<string> dependencies, IReadOnlyList<GameDataOverlayDiagnostic> diagnostics, int applications, bool cacheable)
    {
        Cacheable = cacheable;
        Chunk = chunk;
        BaseKind = baseKind;
        BaseLayer = baseLayer;
        BaseArchive = baseArchive;
        BaseFingerprint = baseFingerprint;
        DocumentsFingerprint = documentsFingerprint;
        Applied = applied;
        Bytes = bytes;
        Dependencies = dependencies;
        Diagnostics = diagnostics;
        Applications = applications;
    }

    /// <summary>Whether the result may be served again: not one that a read that failed produced, which the next call tries again.</summary>
    internal bool Cacheable { get; }

    public ulong Chunk { get; }

    /// <summary>Where the base the edits ran over came from.</summary>
    public GameDataBaseKind BaseKind { get; }

    /// <summary>The layer whose WAD folders held the base, for <see cref="GameDataBaseKind.ModLayer"/>.</summary>
    public string? BaseLayer { get; }

    /// <summary>The id of the game archive the base was read from, for <see cref="GameDataBaseKind.Game"/>; -1 otherwise.</summary>
    public int BaseArchive { get; }

    /// <summary>The identity of the base: a hash of its bytes for a mod's copy, and of the game state, the archive and the chunk for the game's, which needs no read to know.</summary>
    public ulong BaseFingerprint { get; }

    /// <summary>The identity of what was applied (<see cref="GameDataOverlay.DocumentsFingerprint"/>).</summary>
    public ulong DocumentsFingerprint { get; }

    /// <summary>Whether any application changed the bytes. When it did, <see cref="Bytes"/> is what the mod serves for the chunk; when it did not, the chunk is as the base has it and the
    /// caller reads the base as it always does.</summary>
    public bool Applied { get; }

    /// <summary>The bytes after every application, or null when nothing was applied.</summary>
    public byte[]? Bytes { get; }

    /// <summary>The dependency spellings of <see cref="Bytes"/> (<c>linked_bins</c>); empty when nothing was applied.</summary>
    public IReadOnlyList<string> Dependencies { get; }

    /// <summary>This chunk's diagnostics, in the order the crate reports them.</summary>
    public IReadOnlyList<GameDataOverlayDiagnostic> Diagnostics { get; }

    /// <summary>The applications made to the chunk.</summary>
    public int Applications { get; }
}

/// <summary>The overlay's whole result: the plan, every chunk in ascending order, and every diagnostic in the order <c>ltk_overlay</c> reports them.</summary>
public sealed record GameDataOverlayBuild(GameDataPlan Plan, IReadOnlyList<GameDataChunkResult> Chunks, IReadOnlyList<GameDataOverlayDiagnostic> Diagnostics);

/// <summary>The progress of <see cref="GameDataOverlay.BuildAll"/>.</summary>
public readonly record struct GameDataOverlayProgress(int Done, int Total, ulong Chunk);

/// <summary>
/// M818: the game-data stage of <c>ltk_overlay</c> (<c>builder/game_data.rs</c>, <c>builder/metadata.rs</c>), for ONE mod - a project - over the installed game: it binds the modules of every
/// layer to chunks, decides each chunk's base, and runs the modules over it in the crate's order with the crate's diagnostics. What it computes for a chunk is the bytes LTK Manager
/// writes into the overlay for it.
///
/// <para><b>The order.</b> Layers apply in <c>apply_order</c>: the base layer first, then by ascending priority, then by name as a person reads it (digit runs numeric). An inactive layer
/// contributes no document and no file. Inside a layer, modules apply in document order, and a module's edits in order. A chunk's applications are every module's, in that order, each over the
/// bytes the previous one left; a module whose edits all skipped leaves the bytes and is a <see cref="GameDataOverlayDiagnosticKind.NoEffect"/>, and one the engine refuses is a
/// <see cref="GameDataOverlayDiagnosticKind.TargetSkipped"/>. Chunks apply by ascending path hash, which is the order of the whole result's diagnostics.</para>
///
/// <para><b>The base.</b> The mod's own copy of the chunk when it ships one: <c>RAW</c> over every layer, otherwise the highest-priority active layer's (the WAD folders of a layer are one
/// namespace, a later layer's copy replaces an earlier one's, and the base layer is the earliest). Otherwise the game's copy, from the chunk's first holder. The copy a mod ships is the
/// base even where it is byte-identical to the game's.</para>
///
/// <para><b>Entries.</b> An <c>entries</c> module becomes one application for each entry in each chunk that declares it, in storage order (<see cref="GameObjectIndex.Declarations"/>): none is an
/// <see cref="GameDataOverlayDiagnosticKind.EntryUnresolved"/>, several an <see cref="GameDataOverlayDiagnosticKind.EntryFanOut"/> (informational) with every chunk edited. A <c>target</c> module that
/// creates an object another chunk declares reports an <see cref="GameDataOverlayDiagnosticKind.ObjectShadowsGame"/> (informational).</para>
///
/// <para><b>The object index.</b> It is loaded when a module needs it - a non-empty <c>entries</c> module, a reference, an object creation - and otherwise at the first <c>PTCH</c> target, never for a
/// mod that needs neither. When it is unavailable each <c>entries</c> module is an <see cref="GameDataOverlayDiagnosticKind.IndexUnavailable"/>, a <c>target</c> module with references loses them (they read
/// nothing), and a created object is made unchecked. A reference reads the game's copy of an entry from the FIRST chunk that declares it, once per entry for the life of the overlay
/// (<see cref="GameEntryReader"/>), and never sees the mod's own files or another module's edits.</para>
///
/// <para><b>Lazy and cached.</b> <see cref="Plan"/> binds the modules once. <see cref="Apply"/> computes one chunk, on first request and then from a cache keyed by the chunk, the identity of its
/// base and the identity of the declarations; a mod's own copy is read each time to know its identity, and the game's is not read at all on a hit. It may be called from any thread: one thread
/// computes a chunk while another that wants it waits and shares the answer. <see cref="BuildAll"/> is the eager form, and its diagnostics are in the order the crate gives them. All three take a
/// cancellation token: a cancelled call leaves nothing cached, and a call that WAITS (for the plan, for the game's index, for a chunk another thread is computing) ends its wait with its own token.
/// A result is kept only if no read failed for a reason of the moment while it was made (a file the provider could not read, a reference the game could not answer): the next call makes it
/// again. The results kept are bounded (<see cref="GameDataOverlayOptions.MaxCachedBytes"/>); one past the bound is returned and computed again if it is asked for again. The layers in play are
/// those the options named when the overlay was made.</para>
///
/// <para><b>Untrusted input.</b> The documents and override files of a package are the input of a stranger: a document larger than <see cref="GameDataOverlayOptions.MaxDocumentBytes"/>, an override
/// file larger than <see cref="GameDataOverlayOptions.MaxOverrideFileBytes"/> and more than <see cref="GameDataOverlayOptions.MaxApplications"/> applications (an entry no bin declares is one) are
/// refused, every application runs under <see cref="GameDataOverlayOptions.Limits"/> and the call's token, and a document nested deeper than <c>System.Text.Json</c> reads (64) and no deeper than
/// <c>serde_json</c> (127) is read. What an engine refuses of one chunk, a decode, a limit or an unreadable file, is that chunk's diagnostic and no other chunk is touched. A base is sized before it is
/// decoded, because a tree takes up to forty times its bytes: the mod's own copy of a target may weigh <see cref="GameDataOverlayOptions.MaxModChunkBytes"/> and the game's
/// <see cref="GameDataOverlayOptions.MaxGameChunkBytes"/> (the archive says how large a chunk is, so a target that names a texture is refused before it is read). The references of a package are
/// budgeted (<see cref="GameDataOverlayOptions.References"/>, and the work of the chunk they are made for). Whatever a provider, the parser or the game throws that this code did not foresee is a
/// diagnostic of that chunk or layer, never an exception of the call.</para>
///
/// <para><b>Where it differs from the crate</b> (nowhere a real bin reaches): an <c>entries</c> module with no entries and no other module needing the index makes the crate panic and makes
/// no application here; the object index and the chunk table are one object that may fail separately, and a table that is unavailable skips every target that reads the game; a limit that is
/// exceeded is a <see cref="GameDataOverlayDiagnosticKind.TargetSkipped"/> (or a <see cref="GameDataOverlayDiagnosticKind.LimitExceeded"/> for applications); the texts of reads that fail are this
/// code's, not Rust's. The crate's string overrides and WAD routing are not part of this stage.</para>
/// </summary>
public sealed class GameDataOverlay
{
    /// <summary>One module's edits bound to one chunk (<c>Application</c>).</summary>
    internal sealed class Application
    {
        public required string Layer { get; init; }

        /// <summary>The authored target or entry name, as diagnostics report it.</summary>
        public required string Target { get; init; }

        public required ulong Chunk { get; init; }

        public required IReadOnlyList<GameDataEdit> Edits { get; init; }

        public required GameDataOrigin Origin { get; init; }
    }

    /// <summary>A module of one active layer, in apply order.</summary>
    private sealed record Pending(string Layer, GameDataModule Module);

    private sealed class ChunkSlot
    {
        // held while a chunk is computed, which can include building the game's index: a caller that waits for it ends its wait with its own token, which a Monitor would not let it do
        public readonly SemaphoreSlim Gate = new(1, 1);
        public GameDataChunkResult? Result;
    }

    private readonly IReadOnlyList<GameDataLayerInput> _layers;
    private readonly List<GameDataLayerInput> _active;
    // the layers in play, as they were when the overlay was made: the plan, the choice of a base and the documents' fingerprint all read this one copy, whatever the caller does to the set it passed
    private readonly IReadOnlySet<string>? _activeLayers;
    private readonly IGameDataGame _game;
    private readonly IGameDataModFiles _modFiles;
    private readonly GameDataOverlayOptions _options;

    // The gates, and the order they are taken in: a slot's gate or the plan's, then the state's, then the game's own (InstalledGame's). A thread holds none of them twice (they are not reentrant) and waits
    // for none while it holds a later one, so they cannot deadlock; each is a semaphore, so that a waiter ends its wait by its own token.
    private readonly SemaphoreSlim _planGate = new(1, 1);
    private GameDataPlan? _plan;

    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private volatile bool _objectsResolved;
    private GameObjectIndex? _objects;
    private string? _objectsError;
    private volatile bool _tableResolved;
    private GameChunkTable? _table;
    private string? _tableError;
    private GameEntryReader? _entries;
    private readonly object _blockedGate = new();
    private HashSet<ulong>? _blocked;

    private readonly ConcurrentDictionary<ulong, ChunkSlot> _chunks = new(ChunkKeys.Comparer);
    private readonly ConcurrentDictionary<(string Layer, string Path), byte[]> _overrideBytes = new();
    private long _cachedBytes;
    private readonly Lazy<ulong> _documents;

    /// <param name="layers">The mod's layers, in any order: the overlay sorts them. A base layer is added when there is none.</param>
    /// <param name="game">The installed game.</param>
    /// <param name="modFiles">The mod's own copies of chunks; null for a mod that ships none.</param>
    /// <param name="options">How the overlay runs; null for the defaults.</param>
    public GameDataOverlay(IReadOnlyList<GameDataLayerInput> layers, IGameDataGame game, IGameDataModFiles? modFiles = null, GameDataOverlayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(game);
        _game = game;
        _modFiles = modFiles ?? NoModFiles.Instance;
        _options = options ?? new GameDataOverlayOptions();
        _activeLayers = _options.ActiveLayers is null ? null : new HashSet<string>(_options.ActiveLayers, StringComparer.Ordinal);

        var sorted = layers.ToList();
        if (!sorted.Any(l => l.Name == GameDataLayerOrder.BaseName)) sorted.Add(new GameDataLayerInput(GameDataLayerOrder.BaseName, 0, null));
        sorted.Sort(GameDataLayerOrder.Compare);
        // a name repeated keeps the first of its place in the order, as the crate's table does
        var seen = new HashSet<string>(StringComparer.Ordinal);
        sorted.RemoveAll(layer => !seen.Add(layer.Name));
        _layers = sorted;
        _active = sorted.Where(l => IsActive(l.Name)).ToList();
        _documents = new Lazy<ulong>(ComputeDocumentsFingerprint);
    }

    /// <summary>Whether a layer is in play: the base layer always, and any other when <see cref="GameDataOverlayOptions.ActiveLayers"/> was null or named it when the overlay was made.</summary>
    private bool IsActive(string layer) => layer == GameDataLayerOrder.BaseName || _activeLayers is null || _activeLayers.Contains(layer);

    /// <summary>The identity of what the overlay applies: the layers, their priorities and activity, and the text of their documents. Override files are read once for the life of the overlay and are not in it.</summary>
    public ulong DocumentsFingerprint => _documents.Value;

    private ulong ComputeDocumentsFingerprint()
    {
        var hash = new XxHash3();
        var number = new byte[8];
        void Text(string? text)
        {
            if (text is null) { hash.Append(new byte[] { 0 }); return; }
            var utf8 = Encoding.UTF8.GetBytes(text);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(number, utf8.Length + 1);
            hash.Append(number);
            hash.Append(utf8);
        }
        foreach (var layer in _layers)
        {
            Text(layer.Name);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(number, layer.Priority);
            hash.Append(number);
            hash.Append(new byte[] { IsActive(layer.Name) ? (byte)1 : (byte)0 });
            Text(layer.DocumentText);
            Text(layer.DocumentProblem);
        }
        Text(_options.ModId);
        return hash.GetCurrentHashAsUInt64();
    }

    // ================================================================================================ the plan

    /// <summary>Binds the modules of every active layer to chunks, once. The first call may load the object index (a build of a minute on a game never indexed) and the chunk table.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; the plan is made again by the next call.</exception>
    public GameDataPlan Plan(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _plan) is { } done) return done;
        _planGate.Wait(cancellationToken);
        try
        {
            if (_plan is null) Volatile.Write(ref _plan, BuildPlan(cancellationToken));
            return _plan;
        }
        finally
        {
            _planGate.Release();
        }
    }

    private GameDataPlan BuildPlan(CancellationToken cancellationToken)
    {
        var diagnostics = new List<GameDataOverlayDiagnostic>();
        var pending = new List<Pending>();
        var summaries = new List<(GameDataLayerInput Layer, bool Active, bool Has, bool Rejected, int Modules)>();

        foreach (var layer in _layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsActive(layer.Name))
            {
                summaries.Add((layer, false, layer.DocumentText is not null || layer.DocumentProblem is not null, false, 0));
                continue;
            }

            string? refusal = null;
            GameDataDocument? document = null;
            if (layer.DocumentProblem is { } problem) refusal = problem;
            else if (layer.DocumentText is { } text)
            {
                if (Encoding.UTF8.GetByteCount(text) > _options.MaxDocumentBytes)
                    refusal = $"the document is more than the {GameDataOverlayDiagnostics.Count(_options.MaxDocumentBytes)} bytes the overlay reads";
                else
                {
                    try
                    {
                        if (!GameDataDocument.TryParse(text, out document, out var error)) refusal = error!.Message;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        // the parser reads what a stranger wrote: whatever it was not ready for is this layer's refusal, and every other layer goes on
                        document = null;
                        refusal = Unforeseen(e);
                    }
                }
            }
            bool has = layer.DocumentText is not null || layer.DocumentProblem is not null;
            if (refusal is not null)
            {
                diagnostics.Add(new GameDataOverlayDiagnostic(
                    GameDataOverlayDiagnosticKind.DeclarationsRejected, _options.ModId, layer.Name, null, null, null, null, null, null, null,
                    $"Layer declarations refused: {refusal}; update the consumer for unsupported bindings"));
                summaries.Add((layer, true, has, true, 0));
                continue;
            }
            if (document is not null) pending.AddRange(document.Modules.Select(module => new Pending(layer.Name, module)));
            summaries.Add((layer, true, has, false, document?.Modules.Count ?? 0));
        }

        // a reference needs the index for the same reason an `entries` module does: it names an entry, and only the index says which chunk declares it;
        // a created object is checked against the entries the game declares
        bool needsIndex = pending.Any(p => p.Module.Entries is { Count: > 0 } || p.Module.References().Count > 0 || CreatedObjects(p.Module).Any());
        (GameObjectIndex? Index, string? Error) objects = needsIndex ? ResolveObjects(cancellationToken) : (null, null);

        var targets = new SortedDictionary<ulong, List<Application>>();
        var touched = new Dictionary<string, (List<ulong> Order, HashSet<ulong> Seen)>(StringComparer.Ordinal);
        int count = 0;
        bool capped = false;
        // an entry is one of the applications whether or not any chunk declares it: a document of a million names no game bin declares would otherwise be a million diagnostics
        bool Reserve(string layer, string target, GameDataOrigin origin)
        {
            if (count >= _options.MaxApplications)
            {
                capped = true;
                diagnostics.Add(new GameDataOverlayDiagnostic(
                    GameDataOverlayDiagnosticKind.LimitExceeded, _options.ModId, layer, target, null, origin, null, null, null, null,
                    $"The declarations ask for more than the {GameDataOverlayDiagnostics.Count(_options.MaxApplications)} applications the overlay makes; the rest are skipped"));
                return false;
            }
            count++;
            return true;
        }
        bool Add(string layer, Application application)
        {
            if (!Reserve(layer, application.Target, application.Origin)) return false;
            if (!targets.TryGetValue(application.Chunk, out var list)) targets[application.Chunk] = list = new List<Application>();
            list.Add(application);
            if (!touched.TryGetValue(layer, out var chunks)) touched[layer] = chunks = (new List<ulong>(), new HashSet<ulong>(ChunkKeys.Comparer));
            if (chunks.Seen.Add(application.Chunk)) chunks.Order.Add(application.Chunk);
            return true;
        }

        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (capped) break;
            var module = item.Module;
            if (module.IsTarget)
            {
                // a target module resolves without the index, but a reference inside one does not: the module still applies, only its reference keys skip
                if (objects.Error is not null && module.References().Count > 0)
                    diagnostics.Add(ModuleDiagnostic(item, GameDataOverlayDiagnosticKind.IndexUnavailable, null,
                        IndexUnavailableText(objects.Error, "references cannot resolve, so their keys are skipped")));

                ulong chunk = module.Target!.ChunkHash;
                if (objects.Index is { } index)
                {
                    foreach (var name in CreatedObjects(module))
                        if (Shadowed(item, name, chunk, index) is { } shadow) diagnostics.Add(shadow);
                }
                else if (objects.Error is not null && CreatedObjects(module).Any())
                    diagnostics.Add(ModuleDiagnostic(item, GameDataOverlayDiagnosticKind.IndexUnavailable, null,
                        IndexUnavailableText(objects.Error, "created objects are not checked against the game's entries")));

                Add(item.Layer, new Application { Layer = item.Layer, Target = module.Target.Text, Chunk = chunk, Edits = module.Edits!, Origin = module.Origin });
            }
            else if (objects.Index is { } index)
            {
                LowerEntries(item, index, diagnostics, Add, Reserve);
            }
            else if (objects.Error is not null)
            {
                diagnostics.Add(ModuleDiagnostic(item, GameDataOverlayDiagnosticKind.IndexUnavailable, null, IndexUnavailableText(objects.Error, "entries are skipped")));
            }
        }

        // where each target lives: the archives that hold it, which is what a caller routes the chunk by
        GameChunkTable? table = null;
        string? tableError = null;
        if (targets.Count > 0) (table, tableError) = ResolveTable(cancellationToken);
        var planned = new List<GameDataTarget>(targets.Count);
        foreach (var (chunk, applications) in targets)
        {
            var holders = table?.Holders(chunk) ?? Array.Empty<int>();
            planned.Add(new GameDataTarget(
                chunk, applications.Count, applications.Select(a => a.Layer).Distinct().ToList(),
                holders, holders.Select(h => table!.Archive(h).WadPath).ToList()));
        }

        var layers = summaries
            .Select(s => new GameDataPlannedLayer(s.Layer.Name, s.Layer.Priority, s.Active, s.Has, s.Rejected, s.Modules,
                touched.TryGetValue(s.Layer.Name, out var chunks) ? chunks.Order : new List<ulong>()))
            .ToList();

        // what a person is told of the game's index: the archives it could not read, which of those were a failure of the moment and what else it lacks for that reason, and why none of it was kept
        var known = table ?? objects.Index?.Table;
        var unsettled = new List<string>();
        if (known is not null) unsettled.AddRange(known.Unsettled);
        if (objects.Index is { } built) unsettled.AddRange(built.Unsettled);
        string? notSaved = null;
        if (known is not null)
        {
            try { notSaved = _game.IndexNotSaved; }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // what a game says of its index is the caller's code too: a getter that throws is a note on the plan, and neither ends this plan nor makes every later call throw
                unsettled.Add($"the game could not say whether its index was kept: {Unforeseen(e)}");
            }
        }
        return new GameDataPlan(layers, planned, diagnostics, needsIndex, objects.Error, tableError, targets, known?.Skipped ?? Array.Empty<SkippedGameArchive>(), unsettled, notSaved);
    }

    private static string IndexUnavailableText(string error, string consequence) => $"Object index is unavailable: {error}; {consequence}";

    /// <summary>The names a <c>target</c> module's edits create, clones and constructions, in edit order (<c>created_objects</c>).</summary>
    private static IEnumerable<EntryName> CreatedObjects(GameDataModule module) =>
        (module.Edits ?? new List<GameDataEdit>()).SelectMany(edit => edit.Objects.Where(o => o.Value is not RemoveObjectEdit).Select(o => o.Key));

    private GameDataOverlayDiagnostic ModuleDiagnostic(Pending item, GameDataOverlayDiagnosticKind kind, EntryName? target, string message) =>
        new(kind, _options.ModId, item.Layer, target?.Text, null, item.Module.Origin, null, null, null, null, message);

    /// <summary>The <c>ObjectShadowsGame</c> diagnostic of an object <paramref name="name"/> created in <paramref name="chunk"/>, or null when no other game bin declares it.
    /// A game bin that declares the name loads its own object under the same hash: which of the two the game reads is the game's load order, not the declaration's.</summary>
    private GameDataOverlayDiagnostic? Shadowed(Pending item, EntryName name, ulong chunk, GameObjectIndex index)
    {
        var others = Distinct(index.Declarations(name.ObjectHash).Where(d => d.Chunk != chunk));
        if (others.Count == 0) return null;
        string named = string.Join(", ", others.Select(o => $"{o.Chunk:x16} ({index.Table.Archive(o.Archive).Name})"));
        return ModuleDiagnostic(item, GameDataOverlayDiagnosticKind.ObjectShadowsGame, name, $"Game bins declare the created object: {named}; the object is created");
    }

    /// <summary>The distinct chunks of declarations in the order they first appear; a chunk named twice keeps the place of the first and the archive of the last (<c>IndexMap::collect</c>).</summary>
    private static List<(ulong Chunk, int Archive)> Distinct(IEnumerable<GameObjectDeclaration> declarations)
    {
        var chunks = new List<(ulong Chunk, int Archive)>();
        var at = new Dictionary<ulong, int>();
        foreach (var declaration in declarations)
        {
            if (at.TryGetValue(declaration.Chunk, out int position)) chunks[position] = (declaration.Chunk, declaration.Archive);
            else
            {
                at[declaration.Chunk] = chunks.Count;
                chunks.Add((declaration.Chunk, declaration.Archive));
            }
        }
        return chunks;
    }

    /// <summary>Lowers the entries of an <c>entries</c> module to one application per declaring chunk, in mapping order (<c>lower_entries</c>).</summary>
    private void LowerEntries(Pending item, GameObjectIndex index, List<GameDataOverlayDiagnostic> diagnostics, Func<string, Application, bool> add, Func<string, string, GameDataOrigin, bool> reserve)
    {
        foreach (var (name, edit) in item.Module.Entries!)
        {
            var chunks = Distinct(index.Declarations(name.ObjectHash));
            if (chunks.Count == 0)
            {
                // an entry no bin declares makes no application, and counts as one against the limit all the same
                if (!reserve(item.Layer, name.Text, item.Module.Origin)) return;
                diagnostics.Add(ModuleDiagnostic(item, GameDataOverlayDiagnosticKind.EntryUnresolved, name, "No game bin declares the entry; edits are skipped"));
            }
            else if (chunks.Count > 1)
            {
                string named = string.Join(", ", chunks.Select(c => $"{c.Chunk:x16} ({index.Table.Archive(c.Archive).Name})"));
                diagnostics.Add(ModuleDiagnostic(item, GameDataOverlayDiagnosticKind.EntryFanOut, name, $"Entry is declared in {chunks.Count} chunks, each edited: {named}"));
            }
            foreach (var (chunk, _) in chunks)
            {
                // the entry becomes a one-entry edit, which is the shape the engine takes; the links of an entries module ride along as links of the declaring chunk
                var application = new Application
                {
                    Layer = item.Layer,
                    Target = name.Text,
                    Chunk = chunk,
                    Edits = new[] { GameDataEdit.ForEntry(name, edit) },
                    Origin = item.Module.Origin,
                };
                if (!add(item.Layer, application)) return;
            }
        }
    }

    // ================================================================================================ the game

    /// <summary>The object index, loaded on the first ask and the same answer after it. A failure is the answer, as the crate keeps it for a build; a cancellation is not.</summary>
    private (GameObjectIndex? Index, string? Error) ResolveObjects(CancellationToken cancellationToken)
    {
        if (_objectsResolved) return (_objects, _objectsError);
        _stateGate.Wait(cancellationToken);
        try
        {
            if (!_objectsResolved)
            {
                try { _objects = _game.GetObjects(cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { _objectsError = e.Message; }
                Volatile.Write(ref _entries, new GameEntryReader(_objects, _game, _options.References));
                _objectsResolved = true;   // last: whoever sees it set sees the index, the error and the reader
            }
            return (_objects, _objectsError);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private (GameChunkTable? Table, string? Error) ResolveTable(CancellationToken cancellationToken)
    {
        if (_tableResolved) return (_table, _tableError);
        _stateGate.Wait(cancellationToken);
        try
        {
            if (!_tableResolved)
            {
                try { _table = _game.GetTable(cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { _tableError = e.Message; }
                _tableResolved = true;
            }
            return (_table, _tableError);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>The entry reader, with the index loaded for it where it has not been: what a <c>PTCH</c> target asks for, since it reads every object its edits name from the game.</summary>
    private GameEntryReader EntryReader(CancellationToken cancellationToken)
    {
        ResolveObjects(cancellationToken);
        return Volatile.Read(ref _entries)!;
    }

    /// <summary>What the references of the package have cost so far: the entries read and kept, the bins read, the values held, and the reads the budget refused. Null until a reference has been asked for.</summary>
    public GameEntryReaderStats? ReferenceStats => Volatile.Read(ref _entries)?.Stats;

    /// <summary>The chunk hashes of every archive's <c>.wad.SubChunkTOC</c>: a mod's override with one of these hashes is stripped by the crate, and a declaration naming one is skipped.</summary>
    private bool IsBlocked(ulong chunk, GameChunkTable? table)
    {
        if (table is null) return false;
        lock (_blockedGate)
        {
            _blocked ??= table.Archives
                .Select(a => GameDataApplier.AsciiLower(a.Name))
                .Where(name => name.EndsWith(".client", StringComparison.Ordinal))
                .Select(name => LtkHash.Xxh64Path(RustLowercase.Of($"{GameArchiveList.ArchiveRoot}/{name[..^".client".Length]}.subchunktoc")))
                .ToHashSet();
            return _blocked.Contains(chunk);
        }
    }

    // ================================================================================================ one chunk

    /// <summary>
    /// The result of applying every module that names <paramref name="chunk"/>, or null for a chunk no module names. The first call for a chunk computes it; later calls with the same base and the
    /// same declarations return the same result. May be called from any thread.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; nothing is cached for the chunk.</exception>
    public GameDataChunkResult? Apply(ulong chunk, CancellationToken cancellationToken = default)
    {
        var plan = Plan(cancellationToken);
        if (!plan.Applications.TryGetValue(chunk, out var applications)) return null;

        cancellationToken.ThrowIfCancellationRequested();
        var chosen = ChooseBase(chunk, cancellationToken);
        var slot = _chunks.GetOrAdd(chunk, static _ => new ChunkSlot());
        slot.Gate.Wait(cancellationToken);
        try
        {
            if (slot.Result is { } cached && cached.BaseFingerprint == chosen.Fingerprint && cached.BaseKind == chosen.Kind) return cached;
            var result = Compute(chunk, applications, chosen, cancellationToken);
            Keep(slot, result);
            return result;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>
    /// M823: the bytes the modules of <paramref name="chunk"/> run over - the mod's own copy when it ships one (<c>RAW</c> over every layer, then the highest-priority active layer's), else the game's from
    /// the chunk's first holder - which is the base <see cref="Apply"/> starts from, whether or not any module changed it. The bin as LTK finds it before any declaration touches it. Null when there is
    /// none to read, and <paramref name="problem"/> says why. The array is the caller's.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public byte[]? ReadBase(ulong chunk, out GameDataBaseKind kind, out string? problem, CancellationToken cancellationToken = default)
    {
        var chosen = ChooseBase(chunk, cancellationToken);
        kind = chosen.Kind;
        problem = chosen.Error;
        if (chosen.Kind == GameDataBaseKind.None) return null;
        if (chosen.Bytes is { } copy) return (byte[])copy.Clone();
        try
        {
            var bytes = _game.ReadChunk(chosen.Archive, chunk, _options.MaxGameChunkBytes, cancellationToken);
            if (bytes.Length <= _options.MaxGameChunkBytes) return bytes;
            problem = $"the chunk is {GameDataOverlayDiagnostics.Count(bytes.Length)} bytes, more than the {GameDataOverlayDiagnostics.Count(_options.MaxGameChunkBytes)} the overlay reads";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { problem = Unforeseen(e); }
        kind = GameDataBaseKind.None;
        return null;
    }

    /// <summary>Keeps <paramref name="result"/> for the next ask of its chunk, if it may be kept and the overlay's budget of bytes kept (<see cref="GameDataOverlayOptions.MaxCachedBytes"/>) has room for it. What
    /// the slot held before is let go either way: it was the result for another base. Called with the slot's gate held.</summary>
    private void Keep(ChunkSlot slot, GameDataChunkResult result)
    {
        long replaced = slot.Result?.Bytes?.Length ?? 0;
        slot.Result = null;
        if (replaced != 0) Interlocked.Add(ref _cachedBytes, -replaced);
        if (!result.Cacheable) return;

        long size = result.Bytes?.Length ?? 0;
        if (Interlocked.Add(ref _cachedBytes, size) > _options.MaxCachedBytes)
        {
            // there is no room: the result is the caller's, and is computed again if it is asked for again
            Interlocked.Add(ref _cachedBytes, -size);
            return;
        }
        slot.Result = result;
    }

    /// <summary>Applies to every target, ascending, and reports every diagnostic in the order <c>ltk_overlay</c> reports them: the plan's, then each chunk's.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; what was computed stays cached.</exception>
    public GameDataOverlayBuild BuildAll(IProgress<GameDataOverlayProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var plan = Plan(cancellationToken);
        var chunks = new List<GameDataChunkResult>(plan.Targets.Count);
        var diagnostics = new List<GameDataOverlayDiagnostic>(plan.Diagnostics);
        for (int i = 0; i < plan.Targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Apply(plan.Targets[i].Chunk, cancellationToken)!;
            chunks.Add(result);
            diagnostics.AddRange(result.Diagnostics);
            progress?.Report(new GameDataOverlayProgress(i + 1, plan.Targets.Count, result.Chunk));
        }
        return new GameDataOverlayBuild(plan, chunks, diagnostics);
    }

    private const string AbsentTarget = "target is absent from enabled content and the game index";

    /// <summary>Where a chunk's base is, and what identifies it. The bytes are those of a mod's copy, which had to be read to identify it; the game's are read when the result is computed.</summary>
    private sealed record ChosenBase(GameDataBaseKind Kind, string? Layer, int Archive, byte[]? Bytes, ulong Fingerprint, string? Error, GameChunkTable? Table);

    /// <summary>The mod's copy of the chunk (<c>RAW</c> over every layer, then the highest-priority active layer), else the game's from the first holder (<c>read_declaration_base</c>).</summary>
    private ChosenBase ChooseBase(ulong chunk, CancellationToken cancellationToken)
    {
        long cap = _options.MaxModChunkBytes;
        try
        {
            if (_modFiles.ReadRawFile(chunk, cap) is { } raw) return Mod(GameDataBaseKind.ModRaw, null, raw);
            for (int i = _active.Count - 1; i >= 0; i--)
                if (_modFiles.ReadLayerFile(_active[i].Name, chunk, cap) is { } bytes) return Mod(GameDataBaseKind.ModLayer, _active[i].Name, bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // the provider is the caller's code over the package's files: whatever it throws (a file locked, a file it did not expect, a folder it could not list) is this chunk's base not read, which
            // is tried again by the next call, and no other chunk is touched
            return new ChosenBase(GameDataBaseKind.None, null, -1, null, 0, Unforeseen(e), null);
        }

        var (table, error) = ResolveTable(cancellationToken);
        if (table is null) return new ChosenBase(GameDataBaseKind.None, null, -1, null, 0, $"the game index is unavailable: {error}", null);
        int holder = table.FirstHolder(chunk);
        if (holder < 0) return new ChosenBase(GameDataBaseKind.None, null, -1, null, 0, AbsentTarget, table);

        Span<byte> key = stackalloc byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(key, table.Fingerprint);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(key[8..], (ulong)holder);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(key[16..], chunk);
        return new ChosenBase(GameDataBaseKind.Game, null, holder, null, XxHash3.HashToUInt64(key), null, table);

        ChosenBase Mod(GameDataBaseKind kind, string? layer, byte[] bytes)
        {
            // the provider was asked for no more than the cap and is not trusted to have kept to it: what it returned is measured again
            if (bytes.Length > cap) throw new IOException($"the file is {GameDataOverlayDiagnostics.Count(bytes.Length)} bytes, more than the {GameDataOverlayDiagnostics.Count(cap)} the overlay reads");
            return new(kind, layer, -1, bytes, XxHash3.HashToUInt64(bytes) ^ (ulong)bytes.Length * 0x9E3779B97F4A7C15UL, null, ResolveTable(cancellationToken).Table);
        }
    }

    /// <summary>An exception worded for a diagnostic: the platform's own words for an I/O failure and the reader's own for a chunk it refused, and for whatever else a provider or the parser threw, which
    /// this code did not foresee, <c>internal error (Type): message</c>.</summary>
    private static string Unforeseen(Exception e) =>
        e is IOException or UnauthorizedAccessException or GameChunkReadException ? e.Message : $"internal error ({e.GetType().Name}): {e.Message}";

    /// <summary>Runs every application over the base and answers what became of the chunk (the loop over one target of <c>apply_game_data</c>).</summary>
    private GameDataChunkResult Compute(ulong chunk, List<Application> applications, ChosenBase chosen, CancellationToken cancellationToken)
    {
        var diagnostics = new List<GameDataOverlayDiagnostic>();
        GameDataChunkResult Skipped(string reason, bool cacheable)
        {
            // the base did not read, or the target is not one: every application of the chunk says so
            foreach (var application in applications) diagnostics.Add(ApplicationDiagnostic(application, GameDataOverlayDiagnosticKind.TargetSkipped, null, reason));
            return Finish(chunk, chosen, applied: false, null, Array.Empty<string>(), diagnostics, 0, cacheable);
        }

        // a chunk the game does not have is settled; one whose base could not be read is tried again by the next call
        if (chosen.Kind == GameDataBaseKind.None) return Skipped(chosen.Error!, chosen.Error == AbsentTarget);

        byte[] bytes;
        if (chosen.Bytes is { } copy) bytes = copy;
        else
        {
            // the table of contents says how large the chunk is, and the read is refused on that before any of it is read: a target that names a texture or a sound costs a lookup
            try { bytes = _game.ReadChunk(chosen.Archive, chunk, _options.MaxGameChunkBytes, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { return Skipped(Unforeseen(e), cacheable: false); }
            if (bytes.Length > _options.MaxGameChunkBytes)
                return Skipped($"the chunk is {GameDataOverlayDiagnostics.Count(bytes.Length)} bytes, more than the {GameDataOverlayDiagnostics.Count(_options.MaxGameChunkBytes)} the overlay reads", cacheable: false);
        }

        if (IsBlocked(chunk, chosen.Table)) return Skipped("target is a blocked game chunk", cacheable: true);

        // a PTCH target reads every object its edits name from the game, and only the index says which chunk declares one
        var entries = bytes.AsSpan().StartsWith("PTCH"u8) || _objectsResolved ? EntryReader(cancellationToken) : null;

        // a read that failed for a reason of the moment (a file the provider could not read, a reference the game could not answer) is this call's answer and no other's: the result is not kept
        bool unsettled = false;
        GameDataBytesRead ReadOverrideOf(GameDataLayerInput layer, OverridePath path)
        {
            var read = ReadOverride(layer, path, out bool providerFailed);
            if (providerFailed) unsettled = true;
            return read;
        }

        bool applied = false;
        var dependencies = (IReadOnlyList<string>)Array.Empty<string>();
        int ran = 0;
        long worked = 0;
        foreach (var application in applications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (worked > _options.MaxWorkPerChunk)
            {
                diagnostics.Add(ApplicationDiagnostic(application, GameDataOverlayDiagnosticKind.LimitExceeded, null,
                    $"The applications to this chunk used more than the {GameDataOverlayDiagnostics.Count(_options.MaxWorkPerChunk)} units of work the overlay allows one chunk; this one and the rest are skipped"));
                break;
            }
            ran++;

            // the engine's meter cannot be reached from the reader of references, so what the reads cost is tallied here, counts against the chunk's budget, and stops the reads once it is spent
            var tally = new GameEntryTally(Math.Max(0, _options.MaxWorkPerChunk - worked));
            ReadGameEntry readEntry = entries is null
                ? EntriesWithoutIndex(cancellationToken)
                : name =>
                {
                    var read = entries.Read(name, cancellationToken, tally);
                    if (read.Error is not null) unsettled = true;
                    return read;
                };
            try
            {
                var layer = _layers.First(l => l.Name == application.Layer);
                var result = GameDataApplier.Apply(
                    bytes, application.Edits, path => ReadOverrideOf(layer, path), readEntry,
                    _options.Schema, _options.Limits, cancellationToken);
                worked = Add(worked, result.WorkUsed);
                foreach (var diagnostic in result.Diagnostics) diagnostics.Add(Lower(application, diagnostic));
                // `Ok` says the base decoded, not that any edit landed: an application where every edit skipped leaves the base
                if (result.Changed)
                {
                    bytes = result.Bytes;
                    dependencies = result.Dependencies;
                    applied = true;
                }
                else diagnostics.Add(ApplicationDiagnostic(application, GameDataOverlayDiagnosticKind.NoEffect, null, "every edit was skipped, so the target is unchanged"));
            }
            catch (GameDataException e)
            {
                // the work an application that ran over a limit used is not known, and was at most the limit
                worked = Add(worked, e.Kind == GameDataErrorKind.LimitExceeded ? _options.Limits.MaxWork : 1);
                diagnostics.Add(ApplicationDiagnostic(application, GameDataOverlayDiagnosticKind.TargetSkipped, null, e.Message));
            }
            catch (PropDecodeException e)
            {
                worked = Add(worked, 1);
                diagnostics.Add(ApplicationDiagnostic(application, GameDataOverlayDiagnosticKind.TargetSkipped, null, e.Message));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // the engine reads what a stranger wrote: whatever it was not ready for ends this chunk's application, says what it was, and leaves every other chunk alone
                worked = Add(worked, 1);
                diagnostics.Add(ApplicationDiagnostic(application, GameDataOverlayDiagnosticKind.TargetSkipped, null, $"internal error ({e.GetType().Name}): {e.Message}"));
            }
            worked = Add(worked, tally.Units);
        }
        return Finish(chunk, chosen, applied, applied ? bytes : null, dependencies, diagnostics, ran, cacheable: !unsettled);
    }

    private static long Add(long total, long more) => total > long.MaxValue - more ? long.MaxValue : total + more;

    /// <summary>The reader a target answers its references with when no module asked for the index: a reference cannot occur there, and an entry is absent.</summary>
    private static ReadGameEntry EntriesWithoutIndex(CancellationToken cancellationToken) => _ =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GameDataEntryRead.None;
    };

    private GameDataChunkResult Finish(ulong chunk, ChosenBase chosen, bool applied, byte[]? bytes, IReadOnlyList<string> dependencies, List<GameDataOverlayDiagnostic> diagnostics, int applications, bool cacheable) =>
        new(chunk, chosen.Kind, chosen.Layer, chosen.Archive, chosen.Fingerprint, DocumentsFingerprint, applied, bytes, dependencies, diagnostics, applications, cacheable);

    /// <summary>The override file of a layer, read once for the life of the overlay (<c>ResourceCache::read</c>); a file that cannot be read is the apply's <c>OverrideUnreadable</c>, and is read again by the next application that names it.</summary>
    /// <param name="providerFailed">Set when the provider threw: a failure of the moment, which says nothing of the file, so that the chunk's result is not kept. A file the layer does not hold, or holds too large, is settled.</param>
    private GameDataBytesRead ReadOverride(GameDataLayerInput layer, OverridePath path, out bool providerFailed)
    {
        providerFailed = false;
        var key = (layer.Name, path.Text);
        if (_overrideBytes.TryGetValue(key, out var cached)) return GameDataBytesRead.Found(cached);
        try
        {
            var bytes = layer.Files?.ReadOverrideFile(path.Text, _options.MaxOverrideFileBytes);
            if (bytes is null) return GameDataBytesRead.Failed($"{path.Text}: input is missing");
            if (bytes.Length > _options.MaxOverrideFileBytes)
                return GameDataBytesRead.Failed($"{path.Text}: the file is {GameDataOverlayDiagnostics.Count(bytes.Length)} bytes, more than the {GameDataOverlayDiagnostics.Count(_options.MaxOverrideFileBytes)} the overlay reads");
            _overrideBytes.TryAdd(key, bytes);
            return GameDataBytesRead.Found(bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            providerFailed = true;
            return GameDataBytesRead.Failed($"{path.Text}: {Unforeseen(e)}");
        }
    }

    private GameDataOverlayDiagnostic ApplicationDiagnostic(Application application, GameDataOverlayDiagnosticKind kind, int? edit, string message) =>
        new(kind, _options.ModId, application.Layer, application.Target, application.Chunk, application.Origin, edit, null, null, null, message);

    /// <summary>The overlay diagnostic of one application diagnostic of <paramref name="application"/>; the message is the one <c>ltk_game_data</c> writes, so a category the overlay
    /// lowers to <c>Unknown</c> still reads as what it is (<c>Application::lower</c>).</summary>
    private GameDataOverlayDiagnostic Lower(Application application, GameDataDiagnostic diagnostic) =>
        new(GameDataOverlayDiagnostics.Lower(diagnostic.Kind), _options.ModId, application.Layer, application.Target, application.Chunk, application.Origin, diagnostic.Edit,
            diagnostic.Record, diagnostic.Property, diagnostic.Object, GameDataOverlayDiagnostics.Describe(diagnostic));
}

/// <summary>The comparer of every table keyed by a chunk hash that a document chose (a <c>target</c> is a path or the sixteen digits of a hash): a hash is its own hash code, so keys picked to
/// share a bucket chain it, and the code here is salted for the process, as <see cref="KeyHash"/> does for the 32-bit hashes of a file.</summary>
internal static class ChunkKeys
{
    public static readonly IEqualityComparer<ulong> Comparer = new Salted();

    private sealed class Salted : IEqualityComparer<ulong>
    {
        public bool Equals(ulong x, ulong y) => x == y;

        public int GetHashCode(ulong key) => HashCode.Combine(key);
    }
}

/// <summary>The order layers apply in (<c>ModProjectLayer::apply_order</c>): the base layer first, whatever priority it is given, then ascending priority, then the name as a person reads it.</summary>
internal static class GameDataLayerOrder
{
    public const string BaseName = "base";

    public static int Compare(GameDataLayerInput a, GameDataLayerInput b) => Compare(a.Name, a.Priority, b.Name, b.Priority);

    public static int Compare(string aName, int aPriority, string bName, int bPriority)
    {
        int order = (bName == BaseName).CompareTo(aName == BaseName);
        if (order != 0) return order;
        order = aPriority.CompareTo(bPriority);
        return order != 0 ? order : NaturalCompare(aName, bName);
    }

    /// <summary><c>natural_cmp</c>: runs of digits compare by the number they spell, padding zeros dropped, so <c>layer9</c> comes before <c>layer10</c>; everything else compares by UTF-8 byte; names
    /// that tie on every run fall back to plain byte order, which keeps this a total order.</summary>
    public static int NaturalCompare(string a, string b)
    {
        byte[] x = Encoding.UTF8.GetBytes(a), y = Encoding.UTF8.GetBytes(b);
        int order = NaturalBytes(x, y);
        return order != 0 ? order : Math.Sign(x.AsSpan().SequenceCompareTo(y));
    }

    private static int NaturalBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        while (true)
        {
            if (a.IsEmpty && b.IsEmpty) return 0;
            if (a.IsEmpty) return -1;
            if (b.IsEmpty) return 1;
            if (IsDigit(a[0]) && IsDigit(b[0]))
            {
                var (xStart, xEnd) = Number(a);
                var (yStart, yEnd) = Number(b);
                // longer means larger once the padding is gone, so the numbers never have to be parsed and a run of any length is safe
                int order = (xEnd - xStart).CompareTo(yEnd - yStart);
                if (order == 0) order = Math.Sign(a[xStart..xEnd].SequenceCompareTo(b[yStart..yEnd]));
                if (order != 0) return order;
                a = a[xEnd..];
                b = b[yEnd..];
                continue;
            }
            if (a[0] == b[0])
            {
                a = a[1..];
                b = b[1..];
                continue;
            }
            return a[0] < b[0] ? -1 : 1;
        }
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    /// <summary>The leading run of digits of <paramref name="name"/> without the zeros that pad it: where the value starts and where the run ends.</summary>
    private static (int Start, int End) Number(ReadOnlySpan<byte> name)
    {
        int end = 0;
        while (end < name.Length && IsDigit(name[end])) end++;
        int start = 0;
        while (start < end && name[start] == (byte)'0') start++;
        return (start, end);
    }
}

using System.Globalization;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>Where a <see cref="GameDataPreview"/> is: working, done, unable to, or stopped.</summary>
public enum GameDataPreviewState
{
    /// <summary>Reading the project's declarations, binding them to the game's chunks, applying them. Nothing is served yet.</summary>
    Pending,
    /// <summary>Every target is applied; the changed chunks are served.</summary>
    Ready,
    /// <summary>The preview could not be made (<see cref="GameDataPreview.Failure"/> says why). Nothing is served.</summary>
    Failed,
    /// <summary>Stopped before it finished, because the mounts it was made for were replaced. Nothing is served.</summary>
    Cancelled,
}

/// <summary>
/// M819: everything a preview runs on, built on the preview's worker thread (it reads files, and the game's table of contents): the layers with their documents, the game, the mod's own copies of chunks, and how the
/// overlay runs. <see cref="GameDataSetups.ForProject"/> makes the one the editor uses.
/// </summary>
public sealed class GameDataSetup
{
    public required IReadOnlyList<GameDataLayerInput> Layers { get; init; }
    public required IGameDataGame Game { get; init; }

    /// <summary>The mod's own copies of chunks; null for a mod that ships none.</summary>
    public IGameDataModFiles? ModFiles { get; init; }
    public GameDataOverlayOptions? Options { get; init; }

    /// <summary>The installed build, when the game said.</summary>
    public GameBuild? Build { get; init; }

    /// <summary>The newest build the class schema describes, or null when there is no schema.</summary>
    public uint? SchemaLatest { get; init; }

    /// <summary>Why the class schema types less than it could, worded for a person; null when it types everything it is asked about.</summary>
    public string? SchemaNote { get; init; }

    /// <summary>Which class schema the setup reads, worded for a person - "LTK Manager's cache (newest build it describes: 8217343)" - or null when there is none.</summary>
    public string? SchemaSource { get; init; }

    /// <summary>
    /// The options of a second application with ReyEngine's own class schema, when LTK Manager's cache is the schema in use, does not describe the installed build, and ReyEngine's newer copy does; else null. The preview
    /// applies the declarations a second time with it to say how many more edits LTK Manager will apply once it has updated its schema.
    /// </summary>
    public GameDataOverlayOptions? NewerSchemaOptions { get; init; }

    /// <summary>The newest build ReyEngine's own class schema describes, for <see cref="NewerSchemaOptions"/>.</summary>
    public uint? NewerSchemaLatest { get; init; }

    /// <summary>The identity of the class schema the options type with (<see cref="LtkMetaSchema.Identity"/>); null when there is none. What a cache of the preview's work is keyed by.</summary>
    public string? SchemaIdentity { get; init; }

    /// <summary>The identity of the class schema of <see cref="NewerSchemaOptions"/>.</summary>
    public string? NewerSchemaIdentity { get; init; }
}

/// <summary>M819: the setups of the editor.</summary>
public static class GameDataSetups
{
    /// <summary>
    /// The setup for a project: its stored GameData over the installed game, with the class schema LTK Manager installs with today (its own cache, <paramref name="managerSchemaPath"/>) when that can be read, and ReyEngine's copy
    /// (<paramref name="schemaPath"/>) when not - read at the installed build (<see cref="LtkMetaSchema.At(GameBuild?)"/>) the way the manager reads it, with its fallback: a build the schema does not describe types nothing from
    /// the schema, and that is what the manager installs.
    /// </summary>
    /// <param name="project">A project opened from a folder.</param>
    /// <param name="gameDirectory">The folder that holds <c>DATA/FINAL</c>; null when there is none (see <paramref name="gameProblem"/>): the declarations are still read - what they name is known, and a write to it is refused - and every
    /// target whose base is the game's is skipped because the game cannot be read.</param>
    /// <param name="gameProblem">Why there is no game folder, worded for a person.</param>
    /// <param name="modFiles">The project's own copies of chunks (see <see cref="MountModFiles"/>).</param>
    /// <param name="schemaPath">ReyEngine's copy of LTK's meta schema database (<c>data/meta/meta.db.json</c>); used when LTK Manager's own cache (<paramref name="managerSchemaPath"/>) is not there or does not parse. Null or missing leaves the
    /// edits typed by the bins alone, and says so.</param>
    /// <param name="cachePath">Where the game's object index is kept; null for the user's cache folder.</param>
    /// <param name="indexProgress">Told how the game's index is coming along, from the thread that builds it.</param>
    /// <param name="gameFactory">Makes the game; null for <see cref="InstalledGame"/>. A seam for tests.</param>
    /// <param name="buildOverride">The build the schema is read at instead of the installed one: a build the database describes, to see what a mod does where LTK Manager's schema is current. A seam for tests and probes; null reads the installed build.</param>
    /// <param name="managerSchemaPath">LTK Manager's own cache of the schema (<see cref="DefaultManagerSchemaPath"/>): what it installs with TODAY, so what the preview shows when it can be read. Null leaves the manager's cache out (a test
    /// that must not read the machine's files). LTK Manager 1.21 prefers the snapshot embedded in it when the cache does not cover the game's build; its snapshot (build 8175716) does not cover the installed build (8230722) either, so
    /// the cache is what it applies with today. ReyEngine has no snapshot: if the cache does not describe the installed build it stays, undescribed, as the manager's does.</param>
    /// <exception cref="InvalidDataException">A stored document is not JSON: LTK Manager could not read a mod whose <c>info.json</c> held it either.</exception>
    public static GameDataSetup ForProject(ReyProject project, string? gameDirectory, IGameDataModFiles? modFiles, string? schemaPath,
        string? cachePath = null, IProgress<GameIndexProgress>? indexProgress = null, Func<string, IGameDataGame>? gameFactory = null, GameBuild? buildOverride = null, string? gameProblem = null,
        string? managerSchemaPath = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        // a document that is not JSON is a mod LTK Manager cannot read at all (its info.json is one file): there is nothing to preview, and the error says which one. A document that is JSON and
        // is refused by the engine is one layer refused, as the manager installs it, and the overlay says so itself.
        LtkProjectStore.ReadLayers(project);
        var layers = GameDataLayerInput.FromProject(project);

        var build = buildOverride ?? (gameDirectory is null ? null : GameBuild.ReadInstalled(gameDirectory));

        // the class schema: the one LTK Manager installs with TODAY - its own cache - when that can be read, ReyEngine's copy when not. (LTK Manager 1.21 prefers its embedded snapshot to a cache that does not cover the build;
        // the snapshot, 8175716, does not cover the installed build either, so the cache is what it uses today. The day a snapshot covers the installed build, this preference is the thing to revisit.)
        var meta = LoadSchema(managerSchemaPath, out string? managerProblem);
        bool fromManager = meta is not null;
        string? ownProblem = null;
        LtkMetaSchema? newer = null;
        if (meta is null) meta = LoadSchema(schemaPath, out ownProblem);
        else if (build is { } wanted && !meta.Describes(wanted.Content) && schemaPath is not null && !SamePath(schemaPath, managerSchemaPath)
                 && LoadSchema(schemaPath, out _) is { } own && own.Describes(wanted.Content))
            newer = own;   // the manager's cache is behind ReyEngine's copy for this build: what it will apply once it has caught up is worth saying

        IGameDataSchema schema = NoSchema.Instance;
        uint? latest = null;
        string? note = null, source = null;
        if (meta is null)
        {
            string? problem = managerProblem ?? ownProblem;
            note = problem is null
                ? "ReyEngine has no class schema (data/meta/meta.db.json is missing; Tools > Hashes & Names > Sync Meta Classes downloads it), so properties a bin does not already hold cannot be typed and their edits are skipped."
                : $"the class schema cannot be read ({problem}), so properties a bin does not already hold cannot be typed and their edits are skipped.";
        }
        else
        {
            schema = meta.At(build);
            latest = meta.Latest;
            source = fromManager
                ? $"LTK Manager's cache (newest build it describes: {meta.Latest})"
                : $"ReyEngine's copy, data/meta/meta.db.json (newest build it describes: {meta.Latest})"
                  + (managerSchemaPath is null ? "" : managerProblem is null ? "; LTK Manager's own cache was not found" : $"; LTK Manager's cache could not be read ({managerProblem})");
            if (build is { } installed && !meta.Describes(installed.Content))
                note = $"the game build ({installed}) is newer than the class schema (newest build it describes: {meta.Latest}), so edits that need the schema to type a property are skipped; "
                     + (fromManager ? "LTK Manager skips these too until its own class schema is updated." : "LTK Manager skips these too if its own class schema is as old as this one.");
            else if (build is null)
                note = gameDirectory is null
                    ? "the game cannot be read, so its build is not known and the class schema cannot be read at it: edits that need it to type a property are skipped."
                    : "the installed game did not say which build it is (content-metadata.json), so the class schema cannot be read at it and edits that need it to type a property are skipped.";
        }

        IGameDataGame game = gameDirectory is null ? new UnavailableGame(gameProblem ?? "no League game folder is configured")
            : gameFactory is not null ? gameFactory(gameDirectory) : new InstalledGame(gameDirectory, cachePath, null, indexProgress);
        return new GameDataSetup
        {
            Layers = layers,
            Game = game,
            ModFiles = modFiles,
            Options = new GameDataOverlayOptions { Schema = schema },
            Build = build,
            SchemaLatest = latest,
            SchemaNote = note,
            SchemaSource = source,
            NewerSchemaOptions = newer is null ? null : new GameDataOverlayOptions { Schema = newer.At(build) },
            NewerSchemaLatest = newer?.Latest,
            SchemaIdentity = meta?.Identity,
            NewerSchemaIdentity = newer?.Identity,
        };
    }

    /// <summary>Where LTK Manager keeps the class schema it installs with: <c>%LocalAppData%\LeagueToolkit\meta\meta-schema.json</c>. Null where the platform has no such folder.</summary>
    public static string? DefaultManagerSchemaPath
    {
        get
        {
            string local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "LeagueToolkit", "meta", "meta-schema.json");
        }
    }

    /// <summary>The schema at <paramref name="path"/>, or null when there is none to read (no path, no file) or it cannot be (<paramref name="problem"/> says why).</summary>
    private static LtkMetaSchema? LoadSchema(string? path, out string? problem)
    {
        problem = null;
        if (path is null || !File.Exists(path)) return null;
        try { return LtkMetaSchema.Load(path); }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            problem = e.Message;
            return null;
        }
    }

    private static bool SamePath(string a, string? b)
    {
        if (b is null) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

/// <summary>A game that cannot be read, for a project whose game folder is missing: every question it is asked is answered with the reason, so the overlay reports what it reports of any game it cannot read (a chunk table that is
/// unavailable, and every target whose base is the game's skipped) and the preview still knows what the declarations name.</summary>
public sealed class UnavailableGame : IGameDataGame
{
    private readonly string _reason;

    public UnavailableGame(string reason) { _reason = reason; }

    public GameChunkTable GetTable(CancellationToken cancellationToken) => throw new GameIndexException(_reason);

    public GameObjectIndex GetObjects(CancellationToken cancellationToken) => throw new GameIndexException(_reason);

    public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken) => throw new GameIndexException(_reason);
}

/// <summary>One bin the declarations name, and what became of it.</summary>
/// <param name="Chunk">The chunk's path hash.</param>
/// <param name="Name">Its path, when a module named it by path.</param>
/// <param name="Holders">The game archives that hold it, as paths below the game folder; empty for a chunk the game does not have.</param>
/// <param name="Layers">The layers whose modules name it, in apply order.</param>
/// <param name="Applications">How many applications were made to it.</param>
/// <param name="Applied">Whether any of them changed it.</param>
/// <param name="BaseKind">Where the base the edits ran over came from.</param>
/// <param name="BaseLayer">The layer whose WAD folders held the base, for a mod's own copy.</param>
/// <param name="Size">The size of the bytes served, or 0 for a bin that was not changed.</param>
/// <param name="Diagnostics">What the overlay said about it, in the order it said it.</param>
public sealed record GameDataBinReport(
    ulong Chunk, string? Name, IReadOnlyList<string> Holders, IReadOnlyList<string> Layers, int Applications, bool Applied,
    GameDataBaseKind BaseKind, string? BaseLayer, long Size, IReadOnlyList<GameDataOverlayDiagnostic> Diagnostics);

/// <summary>What a finished preview came to, in the words the editor shows.</summary>
/// <param name="Layers">The layers of the project that declare something.</param>
/// <param name="Modules">The modules of every active layer.</param>
/// <param name="Targets">The chunks they name.</param>
/// <param name="ChangedBins">The chunks an application changed: the ones served.</param>
/// <param name="EditsSkipped">The edits the engine skipped: property edits, object creations and removals, link edits and override records.</param>
/// <param name="RejectedLayers">The layers whose documents were refused whole.</param>
/// <param name="Headline">One line: <c>GameData: 13 modules changed 11 bins; 1,641 edits skipped</c>.</param>
/// <param name="Notes">What a person should know and need not act on: why the schema types less than it could, that an entry was declared in several bins, that the index was not kept.</param>
/// <param name="Warnings">What may make the result wrong: archives the index could not read, reads that failed for a reason of the moment. Each says what it affects.</param>
/// <param name="IndexUnsettled">Whether the game's index lacks something for a reason of the moment (a WAD was locked or unreadable while it was built): what it says is absent may not be, until the mounts are rebuilt.</param>
public sealed record GameDataSummary(
    int Layers, int Modules, int Targets, int ChangedBins, int EditsSkipped, int RejectedLayers, string Headline,
    IReadOnlyList<string> Notes, IReadOnlyList<string> Warnings, bool IndexUnsettled)
{
    /// <summary>The kinds that count as an edit the engine skipped.</summary>
    internal static bool IsSkippedEdit(GameDataOverlayDiagnosticKind kind) =>
        kind is GameDataOverlayDiagnosticKind.PropertyEditSkipped or GameDataOverlayDiagnosticKind.ObjectSkipped or GameDataOverlayDiagnosticKind.LinkRemovalUnmatched
            or GameDataOverlayDiagnosticKind.OverrideRecordSkipped or GameDataOverlayDiagnosticKind.LinkUnsupported;
}

/// <summary>
/// M819: what the previews of ONE project remember from one to the next, so that the rebuild of the mounts that follows every write the editor makes (the file watcher fires a moment after it) does not start from nothing.
///
/// <para><b>The chunks the declarations name.</b> A new preview cannot say which bins its declarations name until it has bound them to the game - 0.1 s with the game's index cached, 13 s the first time - and until then a
/// write guard must refuse every bin. A rebuild after the editor's own save changes neither the documents nor the game, though: the new preview inherits what the last one that was Ready named
/// (<see cref="InheritNamed"/>) and answers at once, so the bins that are no targets keep saving and the targets stay refused. It is taken only when the documents (<see cref="GameDataPreview.FingerprintOf"/>) and the game
/// (its folder and build) are exactly those it was learned from - and what it holds only grows while they are: a preview that was degraded (the index unsettled, the table or the object index unreadable) names fewer chunks than a
/// whole one did, and must not make the next one forget them (<see cref="RememberNamed"/>).</para>
///
/// <para>The same chunks are what the editor keeps refusing when a preview that was working stops (it failed, or could not be started): an editor that was opened while it worked may still hold the bytes it made
/// (<see cref="LastNamed"/>).</para>
///
/// <para><b>The count of edits a newer class schema would apply.</b> It takes a second application of every declaration, and depends on nothing the editor's own saves change: it is worked out once for the same documents,
/// bases, build and schemas.</para>
///
/// <para>Every member may be called from any thread.</para>
/// </summary>
public sealed class GameDataPreviewMemory
{
    private const int MaxRemembered = 64;
    private readonly object _gate = new();
    private (ulong Documents, string Game, IReadOnlySet<ulong> Chunks)? _named;
    private readonly Dictionary<string, int> _moreEdits = new(StringComparer.Ordinal);

    /// <summary>Whether a preview that was Ready has named chunks, so that <see cref="InheritNamed"/> can answer.</summary>
    public bool HasNamed
    {
        get { lock (_gate) return _named is not null; }
    }

    /// <summary>The chunks the declarations of the last preview that was Ready named, or null when none was.</summary>
    public IReadOnlySet<ulong>? LastNamed
    {
        get { lock (_gate) return _named?.Chunks; }
    }

    /// <summary>
    /// Remembers what a preview that is Ready named. For the same documents over the same game it ADDS to what was remembered instead of replacing it: a preview made while the index was unsettled, or while the table or the
    /// object index could not be read, binds fewer entries and so names fewer chunks than a whole one did, and the rebuild after it would inherit the smaller set and let a target be written. Over-naming is the safe direction - it
    /// keeps refusing a bin that a whole preview would have named, until the documents or the game change - and never lets a target through. Other documents, or another game (a Riot patch), start again: what the old ones named
    /// says nothing of them.
    /// </summary>
    /// <param name="documents">The identity of its documents (<see cref="GameDataPreview.FingerprintOf"/>).</param>
    /// <param name="game">The game it was made over, as the caller keys it (folder and build).</param>
    /// <param name="chunks">The chunks its declarations name (<see cref="GameDataPreview.NamedChunks"/>). Not changed.</param>
    public void RememberNamed(ulong documents, string game, IReadOnlySet<ulong> chunks)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(chunks);
        lock (_gate)
        {
            if (_named is { } known && known.Documents == documents && string.Equals(known.Game, game, StringComparison.Ordinal))
            {
                if (chunks.IsSubsetOf(known.Chunks)) return;   // nothing new: what was remembered stands
                var all = new HashSet<ulong>(known.Chunks);
                all.UnionWith(chunks);
                _named = (documents, game, all);
            }
            else _named = (documents, game, chunks);
        }
    }

    /// <summary>The chunks the last preview that was Ready named, when it was made from exactly these documents over exactly this game; else null.</summary>
    public IReadOnlySet<ulong>? InheritNamed(ulong documents, string game)
    {
        lock (_gate)
            return _named is { } known && known.Documents == documents && string.Equals(known.Game, game, StringComparison.Ordinal) ? known.Chunks : null;
    }

    internal bool TryGetMoreEdits(string key, out int count)
    {
        lock (_gate) return _moreEdits.TryGetValue(key, out count);
    }

    internal void RememberMoreEdits(string key, int count)
    {
        lock (_gate)
        {
            if (_moreEdits.Count >= MaxRemembered) _moreEdits.Clear();   // bounded: a session that goes through many states keeps the latest
            _moreEdits[key] = count;
        }
    }
}

/// <summary>
/// M819: a project's LTK GameData over the installed game, prepared in the background and served read-only - what the editor shows of an imported mod, as LTK Manager installs it.
///
/// <para><b>One preview per set of mounts.</b> The editor makes one when the mounts of a project that stores GameData are built, starts it (<see cref="Start"/>), and cancels it (<see cref="Cancel"/>) when the mounts are
/// replaced; a new one re-fingerprints the game, which is how a Riot patch while the editor is open is picked up. It is an <see cref="IAssetOverlay"/>: attached to the mounts it serves the changed chunks, and names the
/// chunks its declarations touch (the write guard).</para>
///
/// <para><b>What the guard may ask while it works.</b> The chunks the <c>target</c> modules name are known as soon as the worker starts, before the class schema is loaded and the game is made (the <c>declarations</c> of the
/// constructor); the chunks a module that edits entries names are known the moment the plan exists, before a chunk is applied (<see cref="TargetsKnown"/>). Between the two, a caller that guards a write must treat any bin as a target.</para>
///
/// <para><b>Nothing here waits on the UI thread, and nothing the UI thread does waits on this.</b> The work - reading the documents, the game's table of contents and object index (4 to 13 seconds the first time for a
/// game state, 0.1 s from the cache), binding the modules, applying every target - runs on one worker thread. Until it is done the preview serves nothing and says <see cref="GameDataPreviewState.Pending"/>: a read answers what
/// the mounts hold, which is NOT final, and a caller that must show the result waits for <see cref="Settled"/> without blocking a thread. When it settles the editor refreshes what it showed.</para>
///
/// <para><b>Failures are states, not exceptions.</b> A document that is not JSON, or anything this code did not foresee: the preview is <see cref="GameDataPreviewState.Failed"/> with the reason, serves nothing, and the
/// project stays usable. A game that cannot be read (its folder is missing, its archives do not open) is not a failure of the preview: it is Ready, serves what the mod's own copies make of the declarations - and nothing of
/// the game's bins - and its <see cref="Summary"/> warns of it. An index that is UNSETTLED (a WAD was locked or unreadable while it was built) is kept by the game for the life of this preview, so what it says is absent may not be: <see cref="Summary"/> says so, and a new
/// preview (the editor rebuilds its mounts) is the retry.</para>
/// </summary>
public sealed class GameDataPreview : IAssetOverlay, IDisposable
{
    private sealed record Prepared(
        GameDataSetup Setup, GameDataOverlay Overlay, GameDataPlan Plan, GameDataOverlayBuild Build, GameDataSummary Summary,
        IReadOnlyList<GameDataBinReport> Bins, IReadOnlyList<AssetOverlayEntry> Entries, IReadOnlyList<MapGameContainer> Containers);

    /// <summary>What the declarations name without the game's help: the chunks of the <c>target</c> modules, and the paths they were given by.</summary>
    private sealed record Declared(HashSet<ulong> Chunks, Dictionary<ulong, string> Names);

    private readonly Func<CancellationToken, GameDataSetup> _setup;
    private readonly Func<IReadOnlyList<GameDataLayerInput>>? _declarations;
    private readonly IProgress<string>? _status;
    private readonly GameDataPreviewMemory? _memory;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    private int _state;
    private volatile string? _failure;
    private volatile Declared? _declared;
    private volatile GameDataPlan? _plan;
    private volatile Prepared? _prepared;
    // what the guard may answer with until the plan exists: the chunks the same documents named over the same game when they were last previewed (see GameDataPreviewMemory)
    private volatile IReadOnlySet<ulong>? _inherited;

    /// <param name="setup">Makes what the preview runs on. Called once, on the worker thread; an exception it throws is the preview's <see cref="Failure"/>.</param>
    /// <param name="status">Told what the worker is doing, in words, from the worker's thread: the editor passes a progress sink made on the UI thread.</param>
    /// <param name="declarations">Reads the project's layers and their documents, and nothing else: cheap, and called FIRST on the worker, before <paramref name="setup"/> loads the class schema and makes the game, so that the chunks the
    /// <c>target</c> modules name are known (<see cref="IsTarget"/>) as soon as the worker starts. Null reads them from the setup's layers, after it is made.</param>
    /// <param name="memory">What the project's earlier previews remember (<see cref="GameDataPreviewMemory"/>): the preview keeps the count of edits a newer class schema would apply there. Null remembers nothing.</param>
    /// <param name="inheritedTargets">The chunks the same documents named over the same game when they were last previewed (<see cref="GameDataPreviewMemory.InheritNamed"/>), or null. Until the plan exists the guard answers with
    /// them (<see cref="IsTarget"/>, <see cref="TargetsKnown"/>); the plan replaces them. The caller has checked that the documents and the game are those the chunks were learned from.</param>
    public GameDataPreview(Func<CancellationToken, GameDataSetup> setup, IProgress<string>? status = null, Func<IReadOnlyList<GameDataLayerInput>>? declarations = null,
        GameDataPreviewMemory? memory = null, IReadOnlySet<ulong>? inheritedTargets = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setup = setup;
        _status = status;
        _declarations = declarations;
        _memory = memory;
        _inherited = inheritedTargets;
    }

    /// <inheritdoc />
    public string Name => "LTK GameData";

    public GameDataPreviewState State => (GameDataPreviewState)Volatile.Read(ref _state);

    /// <summary>Whether the preview is still working (nothing is served yet).</summary>
    public bool IsPending => State == GameDataPreviewState.Pending;

    /// <summary>
    /// Whether <see cref="IsTarget"/> can say, for every chunk, if the declarations name it. The <c>target</c> modules name theirs at once, but a module that edits ENTRIES names its chunks only once the game's index has been read and
    /// the declarations bound to it (<see cref="GameDataOverlay.Plan"/>): until then a chunk <see cref="IsTarget"/> answers false for may be one. False while the preview is pending and has not planned - unless it inherited
    /// the chunks the same documents named over the same game (<c>inheritedTargets</c>), which answer from the first moment; true after (the plan is published the moment it exists, before every chunk is applied), and for a
    /// preview that is no longer pending.
    /// </summary>
    public bool TargetsKnown => State != GameDataPreviewState.Pending || _plan is not null || _inherited is not null;

    /// <summary>Why the preview could not be made, for <see cref="GameDataPreviewState.Failed"/>; null otherwise.</summary>
    public string? Failure => _failure;

    /// <summary>Completes when the preview is no longer pending, in whatever state it ended. It never faults and never needs a thread to wait on.</summary>
    public Task Settled => _settled.Task;

    /// <summary>What the preview came to, once it is <see cref="GameDataPreviewState.Ready"/>.</summary>
    public GameDataSummary? Summary => _prepared?.Summary;

    /// <summary>The bins the declarations name and what became of each, once Ready: the changed ones first, then by name.</summary>
    public IReadOnlyList<GameDataBinReport> Bins => _prepared?.Bins ?? Array.Empty<GameDataBinReport>();

    /// <summary>The diagnostics that belong to no chunk (a layer refused, an index that was unavailable), once Ready.</summary>
    public IReadOnlyList<GameDataOverlayDiagnostic> GeneralDiagnostics =>
        _prepared is { } p ? p.Plan.Diagnostics.Where(d => d.Chunk is null).ToList() : Array.Empty<GameDataOverlayDiagnostic>();

    /// <summary>The map the game loads for each shipping map bin the declarations change - the container its <c>Default</c> skin names - once Ready. Empty when no such bin changed, or its skin names none.</summary>
    public IReadOnlyList<MapGameContainer> Containers => _prepared?.Containers ?? Array.Empty<MapGameContainer>();

    /// <summary>The overlay, once the declarations were bound; null before and for a preview that failed first.</summary>
    public GameDataOverlay? Overlay => _prepared?.Overlay;

    /// <summary>The identity of what the preview applies: the layers, their priorities and the text of their documents (<see cref="GameDataOverlay.DocumentsFingerprint"/>). Null until Ready.</summary>
    public ulong? DocumentsFingerprint => _prepared?.Overlay.DocumentsFingerprint;

    /// <summary>The setup the preview runs on, once it has been made.</summary>
    public GameDataSetup? Setup => _prepared?.Setup;

    /// <summary>Starts the work on a thread of its own, once. Returns <see cref="Settled"/>.</summary>
    public Task Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Settled;
    }

    /// <summary>Stops the work. A preview that has not settled settles as <see cref="GameDataPreviewState.Cancelled"/> and serves nothing; one that has is left as it is - Ready, with every result in hand - so that a reader that still holds the mounts it was attached to reads what they always showed.</summary>
    public void Cancel() => _cancel.Cancel();

    public void Dispose() => Cancel();

    // ============================================================================================ the worker

    private void Report(string text) => _status?.Report(text);

    private void Run()
    {
        var token = _cancel.Token;
        try
        {
            Report("LTK GameData: reading the project's declarations...");
            // what the declarations name by themselves is known first of all - before the class schema is loaded and the game is made: it is what the write guard needs while the rest is still being done
            if (_declarations is not null)
            {
                try { _declared = ReadDeclared(_declarations()); }
                catch (Exception e) when (e is not OperationCanceledException) { /* the setup reads the same files and says what is wrong with them */ }
            }
            var setup = _setup(token);
            token.ThrowIfCancellationRequested();

            var declared = ReadDeclared(setup.Layers);
            _declared = declared;

            var overlay = new GameDataOverlay(setup.Layers, setup.Game, setup.ModFiles, setup.Options);
            Report("LTK GameData: binding the declarations to the game (the first time for a game state, the index reads every WAD)...");
            var plan = overlay.Plan(token);
            _plan = plan;   // from this moment every chunk the declarations name, entries included, is known to IsTarget
            _inherited = null;   // and what was inherited has done its work: the plan is the answer now
            Report($"LTK GameData: applying {plan.Targets.Count:N0} chunk(s)...");
            var build = overlay.BuildAll(null, token);
            token.ThrowIfCancellationRequested();

            int? moreEdits = CountEditsOfNewerSchema(setup, overlay, build, token);
            _prepared = Summarise(setup, overlay, plan, build, declared, moreEdits);
            Interlocked.Exchange(ref _state, (int)GameDataPreviewState.Ready);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _state, (int)GameDataPreviewState.Cancelled);
        }
        catch (Exception e)
        {
            // a document that is not JSON, or anything this code did not foresee (a game that cannot be read is not one: the overlay reports it as a table that is unavailable): the project is usable, and the preview says why it is not
            _failure = e is InvalidDataException or IOException or UnauthorizedAccessException or GameIndexException or ArgumentException ? e.Message : $"internal error ({e.GetType().Name}): {e.Message}";
            Interlocked.Exchange(ref _state, (int)GameDataPreviewState.Failed);
        }
        finally
        {
            _settled.TrySetResult();
        }
    }

    private static Declared ReadDeclared(IReadOnlyList<GameDataLayerInput> layers)
    {
        var chunks = new HashSet<ulong>();
        var names = new Dictionary<ulong, string>();
        foreach (var layer in layers)
        {
            if (layer.DocumentText is not { } text) continue;
            try
            {
                if (!GameDataDocument.TryParse(text, out var document, out _) || document is null) continue;
                foreach (var module in document.Modules)
                {
                    if (module.Target is not { } target) continue;
                    ulong chunk = target.ChunkHash;
                    chunks.Add(chunk);
                    if (!target.IsHash) names.TryAdd(chunk, target.Text);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // a document the engine cannot read is that layer's refusal, which the overlay reports; it names nothing here
            }
        }
        return new Declared(chunks, names);
    }

    /// <summary>
    /// How many edits LTK Manager will apply that it skips today, once its class schema is as new as ReyEngine's: the declarations applied a second time with ReyEngine's copy, and the skipped edits counted again. Null when
    /// there is no newer schema to compare with, or the comparison could not be made (it is a courtesy: the preview does not depend on it).
    /// </summary>
    private int? CountEditsOfNewerSchema(GameDataSetup setup, GameDataOverlay overlay, GameDataOverlayBuild build, CancellationToken token)
    {
        if (setup.NewerSchemaOptions is not { } options) return null;
        // a second application of every declaration, and the rebuild after each of the editor's own saves would pay for it again: the count is kept for the same documents, bases, build and schemas
        string? key = MoreEditsKey(setup, overlay, build);
        if (key is not null && _memory is not null && _memory.TryGetMoreEdits(key, out int known)) return known;
        try
        {
            Report("LTK GameData: comparing with ReyEngine's newer class schema...");
            var other = new GameDataOverlay(setup.Layers, setup.Game, setup.ModFiles, options).BuildAll(null, token);
            int now = build.Diagnostics.Count(d => GameDataSummary.IsSkippedEdit(d.Kind));
            int then = other.Diagnostics.Count(d => GameDataSummary.IsSkippedEdit(d.Kind));
            if (key is not null) _memory?.RememberMoreEdits(key, now - then);
            return now - then;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// What <see cref="CountEditsOfNewerSchema"/> depends on, as one string: the documents, the base each target was applied to (the game's state is in the ones the game holds), the installed build and both schemas. Null when a
    /// schema has no identity: nothing is kept then.
    /// </summary>
    private static string? MoreEditsKey(GameDataSetup setup, GameDataOverlay overlay, GameDataOverlayBuild build)
    {
        if (setup.SchemaIdentity is not { } schema || setup.NewerSchemaIdentity is not { } newer) return null;
        var bases = new System.IO.Hashing.XxHash64();
        var number = new byte[8];
        foreach (var result in build.Chunks)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(number, result.Chunk);
            bases.Append(number);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(number, result.BaseFingerprint);
            bases.Append(number);
        }
        return $"{overlay.DocumentsFingerprint:x16}|{bases.GetCurrentHashAsUInt64():x16}|{setup.Build}|{schema}|{newer}";
    }

    private Prepared Summarise(GameDataSetup setup, GameDataOverlay overlay, GameDataPlan plan, GameDataOverlayBuild build, Declared declared, int? moreEdits)
    {
        ulong documents = overlay.DocumentsFingerprint;
        var bins = new List<GameDataBinReport>(build.Chunks.Count);
        var entries = new List<AssetOverlayEntry>();
        foreach (var result in build.Chunks)
        {
            var target = plan.Target(result.Chunk);
            declared.Names.TryGetValue(result.Chunk, out string? name);
            long size = result.Applied ? result.Bytes!.Length : 0;
            bins.Add(new GameDataBinReport(
                result.Chunk, name, target?.HolderPaths ?? Array.Empty<string>(), target?.Layers ?? Array.Empty<string>(), result.Applications, result.Applied,
                result.BaseKind, result.BaseLayer, size, result.Diagnostics));
            // the name a module spelled is listed only when it is a plain relative path (the browser makes file names of it); and the identity follows the BYTES, not only what they were made from - a class schema that changes
            // is a change nothing else in the identity sees
            if (result.Applied)
                entries.Add(new AssetOverlayEntry(result.Chunk, AssetPathSafety.IsSafeRelativePath(name) ? name : null, size,
                    $"gamedata:{documents:x16}:{result.BaseFingerprint:x16}:{size}:{System.IO.Hashing.XxHash64.HashToUInt64(result.Bytes!):x16}"));
        }
        bins.Sort((a, b) =>
        {
            int order = b.Applied.CompareTo(a.Applied);
            return order != 0 ? order : string.Compare(a.Name ?? $"~{a.Chunk:x16}", b.Name ?? $"~{b.Chunk:x16}", StringComparison.OrdinalIgnoreCase);
        });

        // the map the game loads for each shipping map bin the GameData changes: its Default skin's container (what the editor opens a map by)
        var containers = new List<MapGameContainer>();
        foreach (var result in build.Chunks)
        {
            if (!result.Applied || !declared.Names.TryGetValue(result.Chunk, out string? path) || !IsShippingMapBin(path)) continue;
            try
            {
                if (MapGameContainer.Resolve(result.Bytes!) is { } container && !containers.Contains(container)) containers.Add(container);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // a bin the reader was not ready for names no container; the rest of the preview stands
            }
        }

        var summary = BuildSummary(setup, plan, build, entries.Count, moreEdits);
        return new Prepared(setup, overlay, plan, build, summary, bins, entries, containers);
    }

    /// <summary>Whether a path is a shipping map's own bin: <c>data/maps/shipping/map11/map11.bin</c>.</summary>
    public static bool IsShippingMapBin(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5
            && parts[0].Equals("data", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("maps", StringComparison.OrdinalIgnoreCase) && parts[2].Equals("shipping", StringComparison.OrdinalIgnoreCase)
            && parts[3].StartsWith("map", StringComparison.OrdinalIgnoreCase) && parts[3].Length > 3 && parts[3][3..].All(char.IsAsciiDigit)
            && parts[4].Equals(parts[3] + ".bin", StringComparison.OrdinalIgnoreCase);
    }

    private static GameDataSummary BuildSummary(GameDataSetup setup, GameDataPlan plan, GameDataOverlayBuild build, int changed, int? moreEdits = null)
    {
        int modules = plan.Layers.Sum(l => l.Modules);
        int skipped = build.Diagnostics.Count(d => GameDataSummary.IsSkippedEdit(d.Kind));
        int rejected = plan.Layers.Count(l => l.Rejected);
        var notes = new List<string>();
        var warnings = new List<string>();

        string headline = $"GameData: {Count(modules, "module")} changed {Count(changed, "bin")}; {Count(skipped, "edit")} skipped"
                          + (rejected > 0 ? $"; {Count(rejected, "layer")} refused" : "");

        if (setup.SchemaSource is { } schemaSource) notes.Add("Class schema: " + schemaSource + ".");
        if (setup.SchemaNote is { } schema) notes.Add("Edits that need the class schema: " + schema);
        if (moreEdits is > 0)
            notes.Add($"LTK Manager will apply {Count(moreEdits.Value, "more edit")} once it updates its class schema: ReyEngine's own copy (newest build it describes: {setup.NewerSchemaLatest}) describes this game build and the manager's cache does not.");
        int fallback = build.Diagnostics.Count(d => d.Kind == GameDataOverlayDiagnosticKind.SchemaFallback);
        if (fallback > 0)
            notes.Add($"{Count(fallback, "property", "properties")} typed by the schema's fallback (the bin does not hold them and the schema does not describe this build): their edits apply where that type fits and are skipped where it does not.");
        int fanOut = build.Diagnostics.Count(d => d.Kind == GameDataOverlayDiagnosticKind.EntryFanOut);
        if (fanOut > 0) notes.Add($"{Count(fanOut, "entry", "entries")} declared in several game bins, each edited (as LTK installs them).");
        if (plan.IndexNotSaved is { } notSaved)
            notes.Add($"The game's index was not kept for the next session ({notSaved}); the next time the project opens it is read again.");

        bool unsettled = plan.IndexUnsettled.Count > 0;
        if (unsettled)
            warnings.Add("The game's index is incomplete for a reason of the moment (" + string.Join("; ", plan.IndexUnsettled)
                         + "). A target reported absent, and an entry no bin declares, may be wrong: that is not a fact about the game. Retry rebuilds the mounts and reads the game again.");
        var unreadable = plan.SkippedArchives.Where(a => !a.Transient).ToList();
        if (unreadable.Count > 0)
            warnings.Add($"{Count(unreadable.Count, "game archive")} could not be read and hold no chunk for the index ({string.Join(", ", unreadable.Take(3).Select(a => a.Name))}"
                         + (unreadable.Count > 3 ? ", ..." : "") + "): a target that lives in one is reported absent.");
        var transient = plan.SkippedArchives.Where(a => a.Transient).ToList();
        if (transient.Count > 0)
            warnings.Add($"{Count(transient.Count, "game archive")} could not be read just now ({string.Join(", ", transient.Take(3).Select(a => a.Name))}"
                         + (transient.Count > 3 ? ", ..." : "") + "), so the index lacks them until the mounts are rebuilt.");
        if (plan.TableError is { } tableError)
            warnings.Add($"The game cannot be read ({tableError}): a bin whose base is the game's could not be applied, so it shows as the project and the game hold it.");
        if (plan.ObjectIndexError is { } indexError) warnings.Add($"The game's object index is unavailable ({indexError}): entries are skipped and references cannot resolve.");

        return new GameDataSummary(plan.Layers.Count(l => l.HasDocument), modules, plan.Targets.Count, changed, skipped, rejected, headline, notes, warnings, unsettled);
    }

    private static string Count(int n, string one, string? many = null) =>
        n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many ?? one + "s");

    // ============================================================================================ the overlay

    /// <inheritdoc />
    public IReadOnlyList<AssetOverlayEntry> Entries =>
        State == GameDataPreviewState.Ready && _prepared is { } p ? p.Entries : Array.Empty<AssetOverlayEntry>();

    /// <inheritdoc />
    public bool TryRead(ulong pathHash, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (State != GameDataPreviewState.Ready || _prepared is not { } p) return false;
        // every target was applied when the preview became ready, so this is the overlay's cached answer (a result too large to keep is made again). Cancel() stops WORK: a preview that is Ready has none left, and
        // the service it was attached to may still be read by a job that began before the mounts were rebuilt - which sees what that service always showed
        var result = p.Overlay.Apply(pathHash, CancellationToken.None);
        if (result is not { Applied: true, Bytes: { } served }) return false;
        bytes = (byte[])served.Clone();   // the overlay keeps its own: a caller may do what it likes with the one it is given, as with every other mount's
        return true;
    }

    /// <inheritdoc />
    public bool IsTarget(ulong pathHash) =>
        _declared?.Chunks.Contains(pathHash) == true || _plan?.Target(pathHash) is not null || _inherited?.Contains(pathHash) == true;

    /// <summary>
    /// Every chunk the declarations name, as far as the preview knows now: the ones the <c>target</c> modules name, the plan's targets, and - before the plan exists - the ones it inherited. What a preview that is Ready hands
    /// to <see cref="GameDataPreviewMemory.RememberNamed"/>.
    /// </summary>
    public IReadOnlySet<ulong> NamedChunks()
    {
        var named = new HashSet<ulong>();
        if (_declared is { } declared) named.UnionWith(declared.Chunks);
        if (_inherited is { } inherited) named.UnionWith(inherited);
        if (_plan is { } plan)
            foreach (var target in plan.Targets) named.Add(target.Chunk);
        return named;
    }

    /// <summary>
    /// The identity a preview made of these layers gives its documents (<see cref="DocumentsFingerprint"/>: the layers, their priorities and the text of their documents), without making one. What the editor compares
    /// with the documents of the last preview to see if anything it names can have changed.
    /// </summary>
    public static ulong FingerprintOf(IReadOnlyList<GameDataLayerInput> layers) =>
        new GameDataOverlay(layers, new UnavailableGame("only the documents are asked for")).DocumentsFingerprint;

    /// <summary>The chunks the declarations name by themselves (the <c>target</c> modules of every layer), known as soon as the worker has read the documents - before the class schema is loaded and the game's index is read - or null until then.</summary>
    public IReadOnlyCollection<ulong>? DeclaredTargets => _declared?.Chunks;
}

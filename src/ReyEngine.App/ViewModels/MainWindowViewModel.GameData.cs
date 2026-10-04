using System.Text;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Documents;
using ReyEngine.Core;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M819: a project's imported LTK GameData, shown the way LTK Manager installs it - read-only.
///
/// <para><b>What it is.</b> A project that stores GameData (<see cref="LtkProjectStore.HasGameData"/>) gets a <see cref="GameDataPreview"/> whenever its mounts are built: the stored documents applied over the INSTALLED
/// game, in the background, and attached to the mounts as an overlay (<see cref="AssetMountService.SetOverlay"/>). Every read of a chunk the declarations change then answers the result - the viewport, the material and
/// particle editors, the Character window, the Content Browser - and a chunk only the game has is listed and readable. A project that stores none gets nothing: no overlay, no work, and its mounts, tree and reads are what they
/// were (a test pins it).</para>
///
/// <para><b>Readiness.</b> The work takes 0.1 s with the game's index cached and 4 to 13 s the first time for a game state, so it never runs on the UI thread and nothing on the UI thread waits for it. Until it is done the overlay
/// serves NOTHING: a synchronous read of a target answers what the mounts hold - the project's copy, else the game's - which is not final, and the status line says the preview is being prepared. A map that is opened meanwhile waits
/// for it asynchronously (<see cref="WaitForGameDataAsync"/>) instead of loading the unchanged bins, and so does the automatic patch update (it chooses its rows by what the GameData names); everything else is refreshed when the work
/// settles (<see cref="ApplyGameDataSettled"/>), if what is served is not what it was already built from - or if a chunk it serves was READ before the result was published (<see cref="NoteGameDataReadWhilePending"/>: while it was
/// preparing, and in the moment between its being ready and the editor applying it): whoever read it has the unchanged bytes even when the result is the same as the one before. The open map is reloaded only when nothing would be
/// lost by it (<see cref="HasUnsavedMapWork"/>).</para>
///
/// <para><b>Failure never blocks a project.</b> A document that is not JSON, or anything unforeseen: the preview is Failed, the log says why, the overlay is detached and the project opens without the preview. A game folder that
/// is missing or cannot be read is not a failure: the preview is Ready, serves what the mod's own copies make of the declarations (nothing of the game's bins), still names what the declarations target - so the write guard holds -
/// and warns of it.</para>
///
/// <para><b>Read-only until editing GameData targets is supported.</b> Saving the overlaid bytes of a chunk would make LTK apply the modules to their own output (<c>+list</c> duplicates, clone <c>ObjectExists</c>, <c>-list</c> <c>RemovalUnmatched</c>). Every path
/// that writes a bin back into the project or the override store refuses a chunk the declarations name (<see cref="RefusesGameDataWrite"/>), with one message. Until the preview has bound its declarations to the game it cannot say which
/// bins a module that edits ENTRIES names (the <c>target</c> modules are known at once), so until then ANY bin is refused, with another message (<see cref="GameDataPendingRefusal"/>) - unless the same documents over the same game
/// were previewed before, whose answer the new preview inherits at once (<see cref="GameDataPreviewMemory"/>: the rebuild that follows each of the editor's own saves must not refuse the next one). A flow that can wait for the
/// preview does (<see cref="SettleGameDataForWriteAsync"/>, with a limit) instead of failing; and a flow that stages files and writes a bin last asks first (<see cref="PlacementWriteRefusal"/>), so that a refusal at the end does not
/// leave the project half done. When a preview that worked stops (it failed, or could not be started) the bins it named stay refused, with the reason: an editor that was opened while it worked may still hold the bytes it made.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The one sentence that says why a chunk cannot be written back.</summary>
    public const string GameDataEditRefusal = "changed by the mod's GameData; editing it comes with the next update";

    /// <summary>What a bin is told while the preview has not yet worked out which bins the declarations name: any bin of a project that stores GameData is refused until it has.</summary>
    public const string GameDataPendingRefusal = "the GameData preview is still being prepared; try again in a moment";

    /// <summary>What a write that waited for the preview is told when the project it was asked for is gone by the time the preview is ready.</summary>
    public const string GameDataProjectChangedRefusal = "Nothing was written: the project changed while the GameData preview was being prepared.";

    private GameDataPreview? _gameData;
    private Task _gameDataTask = Task.CompletedTask;
    private int _mapLoadsWaitingForGameData;

    /// <summary>What the project's previews remember from one to the next (the chunks the declarations named, the count of edits a newer class schema would apply); null for a project that stores no GameData, and a new one for each project.</summary>
    private GameDataPreviewMemory? _gameDataMemory;

    /// <summary>Why a project that stores GameData has no working preview - "failed (...)" or "could not be started (...)" - or null while it has one. The bins the last preview that worked named stay refused while this is set.</summary>
    private string? _gameDataUnavailable;

    /// <summary>The game the current preview is made over, as <see cref="GameDataPreviewMemory"/> keys it: the folder and its build.</summary>
    private string _gameDataGameKey = "";

    /// <summary>How many writes <see cref="RefusesGameDataWrite"/> has refused: the auto-save compares it before and after a tick, so that it does not say it saved what was refused.</summary>
    private int _gameDataRefusals;

    /// <summary>How long a save waits for a preview that is still working before it gives up and is refused as pending. A cold index takes 4 to 13 s on the machines measured. Internal for the tests.</summary>
    internal TimeSpan GameDataWriteWait = TimeSpan.FromSeconds(30);

    /// <summary>What the open map, the tree and the editors were last made from: the preview's documents and everything it serves, or null when nothing was served. A preview that settles on the same key
    /// changes nothing the user sees, so nothing is reloaded.</summary>
    private string? _gameDataShownKey;

    /// <summary>What the console was last told of the preview (its summary and warnings, or its failure). A rebuild of the mounts after any file change makes a new preview of the same declarations: it says nothing again.</summary>
    private string? _gameDataLoggedSignature;

    /// <summary>The mapgeos the game loads for the shipping maps the GameData routes (the container of the overlaid bin's Default skin), by path hash: listed among the maps even where only the Riot reference holds them.</summary>
    private HashSet<ulong> _gameDataContainerMaps = new();

    /// <summary>The chunks read while a preview was still preparing, across rebuilds of the mounts: they were answered from the mounts, not from the overlay, so whoever kept the bytes has to be refreshed once the preview is ready
    /// - even when what it serves is what the editor was already showing (see <see cref="ApplyGameDataSettled"/>). Written from any thread that reads.</summary>
    private HashSet<ulong> _gameDataReads = new();
    private readonly object _gameDataReadsGate = new();

    /// <summary>The preview of this project's GameData, or null for a project that stores none. Internal for the tests and probes.</summary>
    internal GameDataPreview? GameData => _gameData;

    /// <summary>Completes when the preview of the current mounts has settled AND the editor has applied it (see <see cref="ApplyGameDataSettled"/>). Internal for the tests and probes.</summary>
    internal Task GameDataApplied => _gameDataTask;

    /// <summary>Test seam: makes the game a preview reads. Null is <see cref="InstalledGame"/> over the project's game folder.</summary>
    internal Func<string, IGameDataGame>? GameDataGameFactory = null;   // set only by tests and probes (reflection): an explicit null keeps CS0649 quiet

    /// <summary>Test and probe seam: where the game's object index is kept. Null is the user's cache folder.</summary>
    internal string? GameDataIndexCachePath = null;

    /// <summary>Test and probe seam: ReyEngine's copy of LTK's meta schema database. Null is <see cref="ReyPaths.MetaDbFile"/>. Used when LTK Manager's own cache (<see cref="GameDataManagerSchemaPath"/>) cannot be read.</summary>
    internal string? GameDataSchemaPath = null;

    /// <summary>Test and probe seam: LTK Manager's own cache of the schema, which the preview prefers - it is what the manager installs with today. Null is the manager's location
    /// (<see cref="GameDataSetups.DefaultManagerSchemaPath"/>); a path that does not exist leaves the manager's cache out.</summary>
    internal string? GameDataManagerSchemaPath = null;

    /// <summary>Test and probe seam: the build the schema is read at instead of the installed one - a build the database describes. Null reads the installed build.</summary>
    internal GameBuild? GameDataBuildOverride = null;

    /// <summary>Test seam: what loads the open map again when a preview changes what it is made from. Null is <see cref="LoadMapGeoAsync"/>. A test that only asks WHETHER the map is reloaded must not load one: a load that fails awaits
    /// the editor's dispatcher, which a test does not run, and leaves work queued on it for whatever runs the dispatcher next.</summary>
    internal Func<WadAssetEntry, Task>? GameDataMapReload = null;

    // ============================================================================================ the lifecycle

    /// <summary>The mounts are about to be replaced: the work of the preview made for the old ones stops, and it serves nothing from now on. Called first in <see cref="BuildMounts"/>.</summary>
    private void CancelGameData()
    {
        var old = _gameData;
        _gameData = null;
        // the service is about to be replaced, and its preview will never be published: a reader that still holds it (a map thumbnail in flight) reads what it always showed, and the editor has nothing to undo for it
        if (_mounts is { } retiring) retiring.ReadWhileOverlayPending = null;
        old?.Cancel();
    }

    /// <summary>
    /// The mounts were built (<see cref="BuildMounts"/>): give a project that stores GameData its preview and start it. A project that stores none is left exactly as it is.
    /// </summary>
    /// <param name="layerMounts">The mounts each layer holds, in the order they win in: where a target's base is read from.</param>
    /// <param name="rawMounts">The mounts of the <c>RAW</c> folder.</param>
    private void AttachGameData(Dictionary<string, List<IAssetMount>> layerMounts, List<IAssetMount> rawMounts)
    {
        var mounts = _mounts;
        var project = Project;
        _gameDataUnavailable = null;   // a preview is about to be made: what stopped the last one is not this one's
        if (mounts is null || !LtkProjectStore.HasGameData(project))
        {
            _gameDataMemory = null;   // a project that stores no GameData has no result of it for an editor to hold
            return;
        }

        var modFiles = new MountModFiles(
            layerMounts.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<IAssetMount>)pair.Value, StringComparer.Ordinal), rawMounts);
        TryFindGameFolder(project, out string? gameDirectory, out string? gameProblem);
        string? cachePath = GameDataIndexCachePath, schemaPath = GameDataSchemaPath ?? ReyPaths.MetaDbFile;
        string? managerSchemaPath = GameDataManagerSchemaPath ?? GameDataSetups.DefaultManagerSchemaPath;
        var factory = GameDataGameFactory;
        var buildOverride = GameDataBuildOverride;
        // the worker reads the layers while the user may be editing them in Project Settings: it reads a copy taken here
        var snapshot = new ReyProject
        {
            Name = project.Name, RootPath = project.RootPath, GameDirectory = project.GameDirectory,
            Layers = project.Layers.Select(l => new ProjectLayer { Name = l.Name, Priority = l.Priority, DeclarationsKey = l.DeclarationsKey, Folders = l.Folders.ToList() }).ToList(),
        };

        // The chunks the last preview that was Ready named are inherited when the documents and the game are the same ones: the rebuild after each of the editor's own saves must not refuse every bin until the game has been read
        // again. The documents are read here for it (a few small files; the worker reads them itself when there is nothing to inherit), so that the answer is there from the first moment.
        var memory = _gameDataMemory ??= new GameDataPreviewMemory();
        string gameKey = _gameDataGameKey = GameDataGameKeyOf(gameDirectory, buildOverride);
        IReadOnlyList<GameDataLayerInput>? layers = null;
        IReadOnlySet<ulong>? inherited = null;
        if (memory.HasNamed)
        {
            layers = GameDataLayerInput.FromProject(snapshot);
            inherited = memory.InheritNamed(GameDataPreview.FingerprintOf(layers), gameKey);
        }

        // both sinks are made HERE, on the UI thread, so their callbacks come back to it
        GameDataPreview? preview = null;
        var textSink = new Progress<string>(text =>
        {
            if (preview is not null && ReferenceEquals(_gameData, preview) && preview.IsPending) Status = text;
        });
        long lastTick = 0;
        GameIndexStage lastStage = (GameIndexStage)(-1);
        var indexSink = new Progress<GameIndexProgress>(p =>
        {
            // an index build reports once per archive: the status line shows it a few times a second, and every change of stage
            long now = Environment.TickCount64;
            if (p.Stage == lastStage && now - lastTick < 150) return;
            lastStage = p.Stage;
            lastTick = now;
            if (preview is not null && ReferenceEquals(_gameData, preview) && preview.IsPending) Status = DescribeIndexProgress(p);
        });
        preview = new GameDataPreview(_ =>
            GameDataSetups.ForProject(snapshot, gameDirectory, modFiles, schemaPath, cachePath, indexSink, factory, buildOverride, gameProblem, managerSchemaPath), textSink,
            declarations: () => layers ?? GameDataLayerInput.FromProject(snapshot), memory, inherited);

        _gameData = preview;
        mounts.SetOverlay(preview, _resolver);
        mounts.ReadWhileOverlayPending = NoteGameDataReadWhilePending;
        Status = $"LTK GameData: preparing the preview of '{project.Name}'...";
        _gameDataTask = RunGameDataAsync(preview, mounts);
    }

    /// <summary>The game a preview is made over, as <see cref="GameDataPreviewMemory"/> keys it: its folder and its build, which a Riot patch while the editor is open changes.</summary>
    private static string GameDataGameKeyOf(string? gameDirectory, GameBuild? buildOverride) =>
        gameDirectory is null ? "" : gameDirectory + "|" + (buildOverride ?? GameBuild.ReadInstalled(gameDirectory))?.ToString();

    /// <summary>The preview could not even be started (<see cref="AttachGameData"/> threw): the project opens without it, and says so - and the bins the last preview that worked named stay refused, with the reason.</summary>
    private void NoteGameDataStartFailed(Exception ex)
    {
        _gameDataUnavailable = $"could not be started ({ex.Message})";
        _log.Error("GameData", $"The LTK GameData preview could not be started: {ex.Message} The project is open without it.");
    }

    /// <summary>The game folder a preview reads, as <see cref="GameReferenceLibrary.Inspect"/> normalises it, or why there is none.</summary>
    private static bool TryFindGameFolder(ReyProject project, out string? gameDirectory, out string? problem)
    {
        var status = GameReferenceLibrary.Inspect(project.GameDirectory);
        gameDirectory = status.IsValid ? status.GameDirectory : null;
        problem = status.IsValid ? null : status.Message + (status.Message.EndsWith('.') ? " " : ". ") + "Project > Set Game Folder... selects it.";
        return status.IsValid;
    }

    private static string DescribeIndexProgress(GameIndexProgress p) => p.Stage switch
    {
        GameIndexStage.Enumerating => "LTK GameData: listing the game's WADs...",
        GameIndexStage.LoadingCache => "LTK GameData: loading the game's index...",
        GameIndexStage.Mounting => $"LTK GameData: reading the game's WADs ({p.Done:N0}/{p.Total:N0})...",
        GameIndexStage.ReadingBins => $"LTK GameData: indexing the game's bins ({p.Done:N0}/{p.Total:N0} WADs, {p.Bins:N0} bins) - the first time for this game build only...",
        GameIndexStage.SavingCache => "LTK GameData: keeping the game's index for the next session...",
        _ => "LTK GameData: preparing...",
    };

    private void NoteGameDataReadWhilePending(ulong hash)
    {
        lock (_gameDataReadsGate)
            if (_gameDataReads.Count < 500_000) _gameDataReads.Add(hash);   // bounded: a scan of a whole game would otherwise keep every hash it met
    }

    /// <summary>The reads noted since the last time, and none after: what a settled preview compares with what it serves.</summary>
    private HashSet<ulong> TakeGameDataReads()
    {
        lock (_gameDataReadsGate)
        {
            var taken = _gameDataReads;
            _gameDataReads = new HashSet<ulong>();
            return taken;
        }
    }

    private async Task RunGameDataAsync(GameDataPreview preview, AssetMountService mounts)
    {
        // The continuation after the await comes back to the UI thread through the dispatcher. It can come late - after the project was replaced, or while the editor shuts down - and neither is a problem: a project that is gone fails
        // the identity check below and the continuation does nothing, and a dispatcher that has shut down accepts a post and drops it without throwing (checked against Avalonia 12.1.2 with a scratch probe).
        // Whatever happens here is the preview's own trouble, logged: this task is awaited by the readers that wait for the preview (a map that is opening), and a fault would be theirs
        try
        {
            await preview.Start();   // settled, never faulted; the continuation is the UI thread's
            if (!ReferenceEquals(_gameData, preview) || !ReferenceEquals(_mounts, mounts)) return;   // the mounts were replaced meanwhile: the preview made for them is the editor's business
            ApplyGameDataSettled(preview, mounts);
        }
        catch (Exception ex)
        {
            // and what says so must not throw in turn: this runs wherever the continuation lands - the UI thread, or, when the editor is being torn down around it, a thread nothing guards
            try
            {
                _log.Error("GameData", $"The LTK GameData preview of '{Project.Name}' could not be applied to the editor: {ex.Message} The project stays open.");
                if (ReferenceEquals(_gameData, preview)) Status = "LTK GameData: not applied - see the console.";
            }
            catch (Exception) { /* nobody is left to tell */ }
        }
    }

    /// <summary>
    /// The preview settled (UI thread): say what it came to, merge what it serves into the index, and refresh what was built from the unchanged bins - the tree, the caches that go stale, the open map and the open editors.
    /// Nothing is refreshed when what is served is what the editor is already showing (a rebuild of the mounts after a file changed makes a new preview of the same declarations).
    /// </summary>
    internal void ApplyGameDataSettled(GameDataPreview preview, AssetMountService mounts)
    {
        switch (preview.State)
        {
            case GameDataPreviewState.Failed:
                mounts.SetOverlay(null);   // nothing is served: the project has no overlay
                _gameDataUnavailable = $"failed ({preview.Failure})";   // and an editor that was opened while an earlier preview worked may still hold what it made: the bins it named stay refused
                TakeGameDataReads();
                if (_gameDataLoggedSignature != "failed:" + preview.Failure)
                {
                    _gameDataLoggedSignature = "failed:" + preview.Failure;
                    _log.Error("GameData", $"The LTK GameData of '{Project.Name}' cannot be previewed: {preview.Failure} The project is open without it; its bins are shown as the project and the game hold them.");
                }
                Status = "LTK GameData: not applied - see the console.";
                NotifyMapThumbnailAvailability();   // the tiles that were not described while it was pending are asked again
                return;
            case GameDataPreviewState.Cancelled:
                return;
        }

        var summary = preview.Summary!;
        mounts.RefreshOverlay();
        _gameDataUnavailable = null;
        _gameDataMemory?.RememberNamed(GameDataPreview.FingerprintOf(preview.Setup!.Layers), _gameDataGameKey, preview.NamedChunks());   // what the next preview of these documents over this game inherits
        string? key = GameDataKeyOf(preview);
        // a chunk the preview serves was read while it was still preparing: what read it has the unchanged bytes, whatever the key says
        var reads = TakeGameDataReads();
        bool readBeforeReady = reads.Count > 0 && preview.Entries.Any(e => reads.Contains(e.PathHash));
        string signature = (key ?? "") + "\n" + summary.Headline + "\n" + string.Join("\n", summary.Warnings);
        if (signature != _gameDataLoggedSignature)
        {
            _gameDataLoggedSignature = signature;
            LogGameDataSummary(preview, summary);
        }
        Status = summary.Headline + (summary.IndexUnsettled ? " (the game's index is incomplete - see the console)" : summary.Warnings.Count > 0 ? " (see the console)" : "");

        _gameDataContainerMaps = preview.Containers.Select(c => HashAlgorithms.WadPath(c.GeometryPath)).ToHashSet();

        // the tree built with the mounts listed none of the overlay: it was not ready then
        if (preview.Entries.Count > 0) BuildProjectTree();
        NotifyMapThumbnailAvailability();   // a tile is not described while the preview is pending (no picture beats a stale one): those are asked again, whatever the preview came to

        if (key == _gameDataShownKey && !readBeforeReady) return;
        _gameDataShownKey = key;
        InvalidateGameDataState();
        RefreshForGameData(preview);
    }

    /// <summary>Another project (or no project) is being opened: what the editor was showing of the last one's GameData, and said of it, is forgotten.</summary>
    private void ForgetGameDataShown()
    {
        _gameDataMemory = null;
        _gameDataUnavailable = null;
        _gameDataShownKey = null;
        _gameDataLoggedSignature = null;
        _gameDataContainerMaps = new();
        TakeGameDataReads();
    }

    private void LogGameDataSummary(GameDataPreview preview, GameDataSummary summary)
    {
        bool trouble = summary.RejectedLayers > 0 || summary.IndexUnsettled || summary.Warnings.Count > 0;
        if (trouble) _log.Warn("GameData", summary.Headline + ".");
        else _log.Success("GameData", summary.Headline + ".");
        foreach (string note in summary.Notes) _log.Info("GameData", note);
        foreach (string warning in summary.Warnings) _log.Warn("GameData", warning);
        foreach (var bin in preview.Bins.Where(b => b.Applied))
            _log.Info("GameData", $"{GameDataBinName(bin)} - changed by {bin.Layers.Count} layer(s), {bin.Applications} application(s), {bin.Size:N0} bytes"
                + (bin.Diagnostics.Count > 0 ? $", {bin.Diagnostics.Count:N0} diagnostic(s)" : ""));
        if (preview.Bins.Any(b => b.Diagnostics.Count > 0) || preview.GeneralDiagnostics.Count > 0)
            _log.Info("GameData", "Tools > Mod Health > LTK GameData Diagnostics lists them per bin.");
    }

    internal string GameDataBinName(GameDataBinReport bin) =>
        bin.Name ?? (_resolver.TryGetPath(bin.Chunk, out var path) ? path : $"0x{bin.Chunk:x16}");

    /// <summary>What a preview serves, as one string: the documents it applies and the identity of every chunk it changes. Null when it serves nothing.</summary>
    internal static string? GameDataKeyOf(GameDataPreview preview)
    {
        var entries = preview.Entries;
        if (preview.State != GameDataPreviewState.Ready || entries.Count == 0) return null;
        var sb = new StringBuilder().Append(preview.DocumentsFingerprint?.ToString("x16"));
        foreach (var entry in entries.OrderBy(e => e.PathHash)) sb.Append('|').Append(entry.Identity);
        return sb.ToString();
    }

    /// <summary>The caches built from bins that may be overlaid go stale when what is served changes: the map's state data, the snapshots of the maps the tabs left, the material names, the props' lightgrid and clip tables.</summary>
    private void InvalidateGameDataState()
    {
        InvalidateMapState();
        foreach (var doc in Documents)
        {
            if (doc.Kind != DocumentKind.Map) continue;
            if (doc.Scene is MapScene { HasUnsavedWork: true })
            {
                // the snapshot of a tab that was left holds the edits made in it: dropping it would drop them
                _log.Warn("GameData", $"The map tab {doc.Title} holds unsaved edits, so it keeps the map it was loaded with; reload it to see the mod's GameData in it.");
                continue;
            }
            doc.Scene = null;
        }
        InvalidateMapMaterialNames();
        _propLightGrid = (null, null);
        _propClipTables.Clear();
    }

    /// <summary>
    /// Whether loading the open map again would throw away work nobody has saved: mesh moves, deleted or added pieces, face edits, grows and reshapes (<see cref="HasPendingMapGeoWork"/>), placement edits of particles,
    /// sounds, props and probes, and painted textures that are not written yet - or a stroke still going. One list, so that the reloads that follow a preview agree on it.
    /// </summary>
    private bool HasUnsavedMapWork =>
        HasPendingMapGeoWork || MapContent.HasPlacementEdits || HasParticleMoves
        || HasUnsavedPaint || _paintStrokeActive || (_paintSession?.PaintedTextures.Count ?? 0) > 0;

    /// <summary>Show what the preview serves where something was built from the unchanged bins: the open map is loaded again - unless it holds edits nobody has saved, which a reload would throw away - and the editors open on a
    /// chunk it changed are loaded again or, when they hold edits, say they are stale.</summary>
    private void RefreshForGameData(GameDataPreview preview)
    {
        if (_gameData is not { } current || !ReferenceEquals(current, preview) || _mounts is null) return;
        var served = preview.Entries.Select(e => e.PathHash).ToHashSet();
        if (served.Count == 0) return;

        if (_currentMapEntry is { } map && _mapLoadsWaitingForGameData == 0)
        {
            if (HasUnsavedMapWork)
                _log.Warn("GameData", $"The open map {map.DisplayName} has unsaved edits, so it was not reloaded; reload it to see the mod's GameData in it.");
            else if (TryResolveEntry(map.PathHash, out var reloaded))
            {
                _log.Info("GameData", $"Reloading {map.DisplayName} with the mod's GameData...");
                _ = (GameDataMapReload ?? LoadMapGeoAsync)(reloaded);
            }
        }

        if (MaterialEditor.BinEntry is { } material && served.Contains(material.PathHash))
        {
            if (MaterialEditor.IsDirty) _log.Warn("GameData", $"The Materials tab holds unsaved edits to {material.DisplayName}, which the mod's GameData changes; it shows the bin as it was loaded.");
            else if (TryResolveEntry(material.PathHash, out var entry)) _ = LoadMaterialBinAsync(entry, alsoRawBin: false);
        }
        if (MeshPreview.MaterialEditor.BinEntry is { } skin && served.Contains(skin.PathHash) && !MeshPreview.MaterialEditor.IsDirty
            && TryResolveEntry(skin.PathHash, out var skinEntry))
            _ = LoadMaterialBinAsync(skinEntry, alsoRawBin: false);
        if (ParticleEditor.Entry is { } particle && served.Contains(particle.PathHash))
            _log.Warn("GameData", $"The Particle Editor holds {particle.DisplayName}, which the mod's GameData changes; reopen it to see the result.");
        if (MapBinEditor.Entry is { } mapBin && served.Contains(mapBin.PathHash))
            _log.Warn("GameData", $"The Map Bin Editor holds {mapBin.DisplayName}, which the mod's GameData changes; reopen it to see the result.");
        if (BinEditor.Entry is { } bin && served.Contains(bin.PathHash))
        {
            if (BinEditor.IsDirty) _log.Warn("GameData", $"The bin editor holds unsaved edits to {bin.DisplayName}, which the mod's GameData changes; it shows the bin as it was loaded.");
            else if (TryResolveEntry(bin.PathHash, out var binEntry)) _ = LoadBinAsync(binEntry);
        }
    }

    /// <summary>
    /// A map is about to be read: if the preview is still working, wait for it - without blocking a thread - so that the map loads from the bins as the game would. The map load (and the automatic patch update, see
    /// <see cref="WaitForGameDataToSettleAsync"/>) are the readers that wait; every other reader answers at once with what the mounts hold, and is refreshed when the preview settles.
    /// </summary>
    private Task WaitForGameDataAsync() => WaitForGameDataToSettleAsync("the map opens");

    /// <summary>
    /// A map is about to be loaded while the preview is still working: waits for it (<see cref="WaitForGameDataAsync"/>) and then looks again at what was asked for - the project may have been switched, or the mounts rebuilt, while
    /// it waited. Returns the entry to load: the same one, or the one the rebuilt mounts hold now; or null when the load is not to go on (the project is another, or the rebuilt mounts no longer hold the map).
    /// </summary>
    private async Task<WadAssetEntry?> EntryAfterGameDataAsync(WadAssetEntry entry)
    {
        var projectAtRequest = Project;
        var mountsAtRequest = _mounts;
        try { await WaitForGameDataAsync(); }
        catch (Exception ex) { _log.Warn("GameData", $"The wait for the GameData preview ended badly ({ex.Message}); {entry.DisplayName} loads from what the mounts hold."); }
        if (!ContentLoaded || !ReferenceEquals(Project, projectAtRequest) || (mountsAtRequest is not null && _mounts is null))
        {
            _log.Info("GameData", $"{entry.DisplayName} was not opened: the project it was asked for changed while the GameData preview was being prepared.");
            return null;
        }
        if (ReferenceEquals(_mounts, mountsAtRequest)) return entry;

        // rebuilt meanwhile (a file changed): the entry is looked up in the mounts that are there now, and the load is dropped if they no longer hold it
        if (TryResolveEntry(entry.PathHash, out var current)) return current;
        _log.Info("GameData", $"{entry.DisplayName} was not opened: the rebuilt project no longer holds it.");
        return null;
    }

    /// <summary>The wait of <see cref="WaitForGameDataAsync"/> for any caller that must not decide before the preview has settled (the automatic patch update chooses which bins to leave alone by what the GameData names).</summary>
    /// <param name="whatWaits">What is waiting, for the status line: "the map opens".</param>
    /// <param name="limit">The longest it waits, in all; null for as long as it takes. A caller that is refused afterwards anyway (a save) gives up.</param>
    /// <param name="holdsMapReload">Whether the wait keeps the open map from being reloaded when the preview settles: true for a map that is loading itself (it reads the settled bins), false for a save (the map is reloaded as ever).</param>
    private async Task WaitForGameDataToSettleAsync(string whatWaits, TimeSpan? limit = null, bool holdsMapReload = true)
    {
        long deadline = limit is { } wait ? Environment.TickCount64 + (long)wait.TotalMilliseconds : 0;
        for (int round = 0; round < 4 && _gameData is { IsPending: true }; round++)
        {
            Status = $"LTK GameData: {whatWaits} when the preview is ready...";
            if (holdsMapReload) Interlocked.Increment(ref _mapLoadsWaitingForGameData);
            try
            {
                if (limit is null) await _gameDataTask;
                else
                {
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0) return;
                    try { await _gameDataTask.WaitAsync(TimeSpan.FromMilliseconds(left)); }
                    catch (TimeoutException) { return; }
                }
            }
            finally { if (holdsMapReload) Interlocked.Decrement(ref _mapLoadsWaitingForGameData); }
        }
    }

    // ============================================================================================ the map the game loads

    /// <summary>
    /// A map was opened: when the mod's GameData routes the map's Default skin to a container, say whether this is the map the game loads for it. The editor shows the mapgeo it is asked to open - and the materials beside it,
    /// which are the container's own bin, overlaid - so a mapgeo other than the container's is not wrong, only not the one the game would load first; the Content Browser lists the container's mapgeo among the maps.
    /// </summary>
    private void LogGameDataContainerHint(WadAssetEntry entry)
    {
        if (_gameData is not { State: GameDataPreviewState.Ready } preview) return;
        string folder = entry.Path.Contains('/') ? entry.Path[..entry.Path.LastIndexOf('/')] : "";
        foreach (var container in preview.Containers)
        {
            string containerFolder = container.GeometryPath[..container.GeometryPath.LastIndexOf('/')];
            if (!string.Equals(folder, containerFolder, StringComparison.OrdinalIgnoreCase)) continue;
            if (container.IsGeometry(entry.Path))
                _log.Info("GameData", $"{entry.DisplayName} is the map the game loads for this map's {container.SkinName} skin ({container.ContainerLink}), as the mod's GameData routes it.");
            else
                _log.Info("GameData", $"With the mod's GameData the game loads {container.ContainerLink} for this map's {container.SkinName} skin, not {entry.DisplayName}: "
                    + $"open {container.GeometryPath} to see what the game loads.");
            return;
        }
    }

    // ============================================================================================ the tree

    /// <summary>The entry the browser shows for an asset a mount holds: what the editor READS for that chunk. A chunk the GameData serves is read-only and says LTK GameData, whichever mount holds a copy of it; every
    /// other asset - and every asset of a project with no overlay, without a lookup - is its own entry.</summary>
    private WadAssetEntry EntryOf(MountedAsset held)
    {
        if (_mounts is { Overlay: not null } mounts && mounts.IsOverlaid(held.PathHash) && mounts.TryGet(held.PathHash, out var served))
            return served.ToEntry();
        return held.ToEntry();
    }

    /// <summary>The group of bins only the game has and the GameData changes (the ones no project mount holds), or null when there are none: the tree's third root, listed read-only beside the project's and the Riot references'.</summary>
    private AssetTreeNode? GameDataTreeGroup()
    {
        if (_mounts is not { Overlay: not null } mounts) return null;
        var entries = mounts.Assets
            .Where(a => a.Source is OverlayMount && a.AllSources.All(s => s is OverlayMount))
            .Select(a => a.ToEntry())
            .ToList();
        return entries.Count == 0 ? null : AssetTree.Build(entries, "LTK GameData");
    }

    // ============================================================================================ the read-only guard

    /// <summary>
    /// Whether a chunk must not be written back (true: refused, and said). A chunk the project's GameData names - changed by it or not - is the mod's declarations applied over the game's copy: saving what the editor
    /// read would give LTK its own output to apply the declarations to again, so editing it is not supported yet. A project with no GameData refuses nothing.
    /// </summary>
    internal bool RefusesGameDataWrite(WadAssetEntry? entry)
    {
        if (entry is null || GameDataWriteRefusal(entry.PathHash, entry.DisplayName, IsBinEntry(entry)) is not { } why) return false;
        _gameDataRefusals++;
        _log.Warn("GameData", why + " Nothing was written.");
        Status = why;
        return true;
    }

    /// <summary>
    /// <see cref="RefusesGameDataWrite"/> for a flow that can wait: a bin refused only because the preview is still working is waited for (<see cref="SettleGameDataForWriteAsync"/>) and then asked again. A write the project
    /// changed under while it waited is refused.
    /// </summary>
    private async Task<bool> RefusesGameDataWriteAsync(WadAssetEntry? entry)
    {
        if (entry is not null && !await SettleGameDataForWriteAsync(entry.PathHash, IsBinEntry(entry)))
        {
            _gameDataRefusals++;
            return true;
        }
        return RefusesGameDataWrite(entry);
    }

    /// <summary>
    /// A write is about to be decided: if the only thing against it is that the preview has not yet said which bins the declarations name (<see cref="GameDataWritePending"/>), waits for it - without blocking a thread, and for
    /// at most <see cref="GameDataWriteWait"/> - so that the editor's own save is not refused for the half second after the previous one. Returns false when the project was changed while it waited: what the caller holds belongs
    /// to another project, and nothing may be written for it.
    /// </summary>
    private async ValueTask<bool> SettleGameDataForWriteAsync(ulong hash, bool isBin)
    {
        if (!GameDataWritePending(hash, isBin)) return true;
        var project = Project;
        await WaitForGameDataToSettleAsync("the bin is written", GameDataWriteWait, holdsMapReload: false);
        if (ContentLoaded && ReferenceEquals(Project, project)) return true;
        _log.Warn("GameData", GameDataProjectChangedRefusal);
        Status = GameDataProjectChangedRefusal;
        return false;
    }

    /// <summary><see cref="GuardEditable"/> for a flow that can wait for the preview (see <see cref="SettleGameDataForWriteAsync"/>).</summary>
    private async ValueTask<bool> GuardEditableAsync(WadAssetEntry? entry)
    {
        if (entry is not null && !await SettleGameDataForWriteAsync(entry.PathHash, IsBinEntry(entry)))
        {
            _gameDataRefusals++;
            return false;
        }
        return GuardEditable(entry);
    }

    /// <summary><see cref="ThrowIfGameDataTarget"/> for a flow that can wait for the preview (see <see cref="SettleGameDataForWriteAsync"/>): the check a flow makes before it stages anything.</summary>
    /// <exception cref="InvalidOperationException">The chunk is one the project's GameData changes, or the project was changed while the preview was being waited for.</exception>
    private async ValueTask ThrowIfGameDataTargetAsync(WadAssetEntry entry)
    {
        if (!await SettleGameDataForWriteAsync(entry.PathHash, IsBinEntry(entry)))
            throw new InvalidOperationException(GameDataProjectChangedRefusal);
        ThrowIfGameDataTarget(entry);
    }

    /// <summary><see cref="PlacementWriteRefusal"/> for a flow that can wait for the preview (see <see cref="SettleGameDataForWriteAsync"/>).</summary>
    private async Task<string?> PlacementWriteRefusalAsync(WadAssetEntry mapEntry)
    {
        // one wait is the wait for both bins: the preview is the same
        if (TryResolveMaterialsBin(mapEntry.Path, out var materials) && !await SettleGameDataForWriteAsync(materials.PathHash, isBin: true)) return GameDataProjectChangedRefusal;
        if (MapBinPathFor(mapEntry.Path) is { } mapBinPath && TryResolveEntry(HashAlgorithms.WadPath(mapBinPath), out var mapBin)
            && !await SettleGameDataForWriteAsync(mapBin.PathHash, isBin: true)) return GameDataProjectChangedRefusal;
        return PlacementWriteRefusal(mapEntry);
    }

    /// <summary>
    /// Why a write of this chunk is refused, or null when it is not. A chunk the declarations name is refused for good (<see cref="GameDataEditRefusal"/>) - and so is one the last preview that worked named, while there is no
    /// preview because it failed or could not be started: an editor that was opened while it worked may still hold the bytes it made. Until the preview has bound its declarations to the game, though, the chunks a module that
    /// edits ENTRIES names are not known - so until it has, any BIN of a project that stores GameData is refused, for a moment (<see cref="GameDataPendingRefusal"/>): an editor still holding the bytes of the service before this
    /// one, or an autosave, must not write what the declarations would then be applied to a second time. (A preview of the same documents over the same game as the last one that was Ready knows at once: see <see cref="GameDataPreviewMemory"/>.)
    /// </summary>
    private string? GameDataWriteRefusal(ulong hash, string name, bool isBin)
    {
        if (_mounts is not { } mounts) return null;
        if (mounts.IsOverlayTarget(hash)) return $"'{name}' is {GameDataEditRefusal}.";
        if (WasNamedByStoppedPreview(hash, mounts))
            return $"'{name}' is {GameDataEditRefusal}, and its preview {_gameDataUnavailable}: an editor that was opened while it worked may still hold the bytes it made, so nothing is written until it works again.";
        if (isBin && !mounts.OverlayTargetsKnown) return $"'{name}': {GameDataPendingRefusal}.";
        return null;
    }

    /// <summary>Whether the refusal of this bin is only that the preview cannot yet say if the declarations name it (<see cref="GameDataPendingRefusal"/>): the one refusal that time takes away.</summary>
    private bool GameDataWritePending(ulong hash, bool isBin) =>
        isBin && _mounts is { } mounts && !mounts.IsOverlayTarget(hash) && !mounts.OverlayTargetsKnown;

    /// <summary>Whether the declarations name this chunk: the preview says, and when there is none because it stopped working, the last one that worked did (<see cref="WasNamedByStoppedPreview"/>).</summary>
    private bool IsGameDataTarget(ulong hash) =>
        _mounts is { } mounts && (mounts.IsOverlayTarget(hash) || WasNamedByStoppedPreview(hash, mounts));

    private bool WasNamedByStoppedPreview(ulong hash, AssetMountService mounts) =>
        mounts.Overlay is null && _gameDataUnavailable is not null && _gameDataMemory?.LastNamed?.Contains(hash) == true;

    private static bool IsBinEntry(WadAssetEntry entry) =>
        entry.Type == AssetType.Bin || entry.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>The low write paths (a project file, the folder a copy goes in) refuse by throwing: they have no way to say no, their callers already guard and log, and one that did not must fail rather than write.</summary>
    /// <exception cref="InvalidOperationException">The chunk is one the project's GameData changes, or a bin while the preview cannot yet say which are.</exception>
    private void ThrowIfGameDataTarget(WadAssetEntry entry)
    {
        if (GameDataWriteRefusal(entry.PathHash, entry.DisplayName, IsBinEntry(entry)) is { } why)
            throw new InvalidOperationException(why);
    }

    /// <summary>
    /// Why a placement (a prop, a character, a particle) cannot be written, or null: the bins it ends in - the map's materials.bin, which holds the placement, and the map's own bin, which lists the characters - are checked
    /// BEFORE anything is staged, because a flow that writes its assets first and meets the refusal at the end leaves the project half done.
    /// </summary>
    private string? PlacementWriteRefusal(WadAssetEntry mapEntry)
    {
        if (TryResolveMaterialsBin(mapEntry.Path, out var materials) && GameDataWriteRefusal(materials.PathHash, materials.DisplayName, true) is { } first)
            return first;
        if (MapBinPathFor(mapEntry.Path) is { } mapBinPath && TryResolveEntry(HashAlgorithms.WadPath(mapBinPath), out var mapBin)
            && GameDataWriteRefusal(mapBin.PathHash, mapBin.DisplayName, true) is { } second)
            return second;
        return null;
    }

    /// <summary>
    /// The file a copy of <paramref name="entry"/> is written to in <paramref name="folder"/>: its own name when that is a plain file name, the hash form when it is not, and never outside the folder. The name of an asset can
    /// come from a package or its hashtables, and <c>..\..\y.cmd</c> hashes as well as a real one: it would write where the user did not choose.
    /// </summary>
    internal static string SafeFileIn(string folder, WadAssetEntry entry)
    {
        string name = Path.GetFileName(entry.DisplayName);   // a backslash is a separator here: the part before the last one goes
        if (!AssetPathSafety.IsSafeFileName(name))
        {
            string extension = Path.GetExtension(name);
            name = $"0x{entry.PathHash:x16}" + (extension.Length is > 1 and <= 9 && AssetPathSafety.IsSafeFileName("a" + extension) ? extension : ".bin");
        }
        if (!AssetPathSafety.TryCombineUnder(folder, name, out string full))
            throw new InvalidDataException($"'{entry.DisplayName}' is not a name a file can be written under in {folder}.");
        return full;
    }

    /// <summary>The bytes of a chunk as the project and the game hold them, never as the GameData changes them: what a rebase reads (<see cref="AssetMountService.ReadRaw"/>). With no overlay it is <see cref="ReadAsset"/>.</summary>
    private byte[] ReadAssetRaw(ulong hash) =>
        _mounts is { } mounts ? mounts.ReadRaw(hash) ?? throw new FileNotFoundException($"0x{hash:x16} not in any mount.") : ReadAsset(hash);

    /// <summary>The patch update leaves a bin the GameData changes out of its rebase - its result could not be saved - and says so.</summary>
    private bool SkipsGameDataBin(WadAssetEntry entry)
    {
        if (!IsGameDataTarget(entry.PathHash)) return false;
        _log.Info("PatchUpdate", $"'{entry.Path}' is {GameDataEditRefusal}, so it is not rebased onto the new patch here.");
        return true;
    }

    // ============================================================================================ the diagnostics

    /// <summary>Tools > Mod Health > LTK GameData Diagnostics: what the overlay said, per bin, in the Bin Issues window.</summary>
    [RelayCommand]
    private void OpenGameDataDiagnostics()
    {
        if (_gameData is not { } preview)
        { _log.Info("GameData", "This project stores no LTK GameData (an imported layered .fantome does)."); return; }
        if (preview.IsPending)
        { _log.Info("GameData", "The GameData preview is still being prepared; try again in a moment."); Status = "LTK GameData: still preparing..."; return; }
        if (preview.State == GameDataPreviewState.Failed)
        { _log.Warn("GameData", $"The GameData preview could not be made: {preview.Failure}"); return; }
        if (preview.State != GameDataPreviewState.Ready) return;

        var vm = GameDataDiagnosticsView.Build(preview, bin => GameDataBinName(bin), RetryGameData);
        ShowBinIssuesWindow(vm);
    }

    /// <summary>Rebuilds the mounts, which makes a new preview over a new <see cref="InstalledGame"/> (it re-reads the game): the retry of an index that was unsettled.</summary>
    private void RetryGameData()
    {
        if (!ProjectMode) return;
        _log.Info("GameData", "Retrying: the mounts are rebuilt and the game is read again.");
        _gameDataShownKey = null;
        _gameDataLoggedSignature = null;
        BuildMounts();
        BuildProjectTree();
    }
}

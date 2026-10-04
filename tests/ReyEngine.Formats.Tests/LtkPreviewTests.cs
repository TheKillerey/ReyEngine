using System.IO.Compression;
using System.Text;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.LtkGameData;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M819: the GameData preview on its own - what it serves and when, what it says it is doing, what it does when it cannot, and what it makes of the game's index. Synthetic games and mods; the real
/// package over the real game is the probes' (see the M819 commit).
/// </summary>
public sealed class LtkPreviewTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private int _installs;

    public void Dispose() => _temp.Dispose();

    private const string A = "data/t/a.bin", X = "data/t/x.bin";

    private static SyntheticGame Everything() => new SyntheticGame()
        .Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
        .Add("X.wad.client", X, Bin(Obj("Test/Obj/X", new[] { "x" })));

    private static string AddTag(string path, string obj, string tag) => Doc(Target(path, $"{{\"{obj}\":{{\"+tags\":[\"{tag}\"]}}}}"));

    private InstalledGame Install(SyntheticGame game)
    {
        int n = _installs++;
        return new InstalledGame(game.Write(_temp.Combine("game" + n)), _temp.Combine("cache" + n, "index.idx"));
    }

    private static GameDataPreview Preview(IGameDataGame game, SyntheticMod mod, IProgress<string>? status = null) =>
        new(_ => new GameDataSetup { Layers = mod.ToLayers(), Game = game, ModFiles = mod.ToFiles() }, status);

    /// <summary>The game with a gate in front of its table: a preview over it is pending until the test opens the gate (or cancels, or ends: disposing it opens the gate, so that no worker is left waiting at it).</summary>
    private sealed class GatedGame : IGameDataGame, IDisposable
    {
        private readonly IGameDataGame _inner;
        public readonly ManualResetEventSlim Gate = new(false);
        public readonly ManualResetEventSlim Reached = new(false);

        public GatedGame(IGameDataGame inner) { _inner = inner; }

        public void Dispose() => Gate.Set();

        public GameChunkTable GetTable(CancellationToken cancellationToken)
        {
            Reached.Set();
            Gate.Wait(cancellationToken);
            return _inner.GetTable(cancellationToken);
        }

        public GameObjectIndex GetObjects(CancellationToken cancellationToken) => _inner.GetObjects(cancellationToken);

        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken) => _inner.ReadChunk(archive, chunk, maxBytes, cancellationToken);

        public string? IndexNotSaved => _inner.IndexNotSaved;
    }

    /// <summary>
    /// The game with a gate in front of its table and another in front of the first chunk it is asked to read: a preview over it is pending before it has planned, then planned but with nothing applied yet.
    /// </summary>
    private sealed class StagedGame : IGameDataGame, IDisposable
    {
        private readonly IGameDataGame _inner;
        public readonly ManualResetEventSlim TableGate = new(false);
        public readonly ManualResetEventSlim ReadGate = new(false);
        public readonly ManualResetEventSlim ReadReached = new(false);

        public StagedGame(IGameDataGame inner) { _inner = inner; }

        /// <summary>Opens both gates: a worker waiting at either goes on (the test has ended).</summary>
        public void Dispose()
        {
            TableGate.Set();
            ReadGate.Set();
        }

        public GameChunkTable GetTable(CancellationToken cancellationToken)
        {
            TableGate.Wait(cancellationToken);
            return _inner.GetTable(cancellationToken);
        }

        public GameObjectIndex GetObjects(CancellationToken cancellationToken) => _inner.GetObjects(cancellationToken);

        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken)
        {
            ReadReached.Set();
            ReadGate.Wait(cancellationToken);
            return _inner.ReadChunk(archive, chunk, maxBytes, cancellationToken);
        }

        public string? IndexNotSaved => _inner.IndexNotSaved;
    }

    // ================================================================================================ what it serves, and when

    [Fact]
    public async Task A_preview_serves_nothing_until_it_is_ready_and_then_what_the_overlay_makes_of_each_changed_chunk()
    {
        var mod = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/A", "mod"));
        var game = Install(Everything());
        var preview = Preview(game, mod);

        Assert.Equal(GameDataPreviewState.Pending, preview.State);
        Assert.True(preview.IsPending);
        Assert.Empty(preview.Entries);
        Assert.False(preview.TryRead(ChunkOf(A), out _));
        Assert.Null(preview.Summary);

        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        Assert.False(preview.IsPending);
        var entry = Assert.Single(preview.Entries);
        Assert.Equal(ChunkOf(A), entry.PathHash);
        Assert.Equal(A, entry.Path);                                          // the module named it by path
        Assert.True(preview.TryRead(ChunkOf(A), out var served));
        Assert.Equal(entry.Size, served.Length);
        Assert.Equal(new[] { "g1", "mod" }, TagsOf(served, "Test/Obj/A"));
        // and it is exactly what the overlay itself makes of the chunk
        var alone = new GameDataOverlay(mod.ToLayers(), game, mod.ToFiles()).Apply(ChunkOf(A))!;     // over the same game: its fingerprint is part of the identity
        Assert.Equal(alone.Bytes, served);
        // the identity follows the BYTES too: a class schema that changes is a change nothing else in it would see
        Assert.Equal($"gamedata:{preview.DocumentsFingerprint:x16}:{alone.BaseFingerprint:x16}:{served.Length}:{System.IO.Hashing.XxHash64.HashToUInt64(served):x16}", entry.Identity);
        // a chunk no module names is not served and is no target
        Assert.False(preview.TryRead(ChunkOf(X), out _));
        Assert.False(preview.IsTarget(ChunkOf(X)));
    }

    [Fact]
    public async Task A_chunk_the_declarations_name_and_change_nothing_in_is_a_target_and_is_not_served()
    {
        var mod = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/NotThere", "mod"));   // an object the bin does not hold: the edit is skipped
        var preview = Preview(Install(Everything()), mod);

        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        Assert.Empty(preview.Entries);
        Assert.False(preview.TryRead(ChunkOf(A), out _));
        Assert.True(preview.IsTarget(ChunkOf(A)));                            // the guard refuses what the declarations name, changed or not
        var bin = Assert.Single(preview.Bins);
        Assert.False(bin.Applied);
        Assert.Contains(bin.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.NoEffect);
        Assert.Equal("GameData: 1 module changed 0 bins; 1 edit skipped", preview.Summary!.Headline);
    }

    [Fact]
    public async Task What_the_declarations_name_by_themselves_is_known_before_the_game_is_read_which_is_what_the_write_guard_needs()
    {
        var mod = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/A", "mod"));
        using var gated = new GatedGame(Install(Everything()));
        var preview = Preview(gated, mod);

        var settled = preview.Start();
        Assert.True(gated.Reached.Wait(TimeSpan.FromSeconds(60)));              // the worker is in front of the gate: the game's table has not been read

        Assert.Equal(GameDataPreviewState.Pending, preview.State);
        Assert.True(preview.IsTarget(ChunkOf(A)));
        Assert.Contains(ChunkOf(A), preview.DeclaredTargets!);
        Assert.False(preview.IsTarget(ChunkOf(X)));
        Assert.False(preview.TryRead(ChunkOf(A), out _));                      // nothing is served while it is pending
        Assert.False(settled.IsCompleted);

        gated.Gate.Set();
        await settled.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(GameDataPreviewState.Ready, preview.State);
    }

    [Fact]
    public async Task A_preview_cancelled_while_it_is_pending_settles_as_cancelled_and_serves_nothing()
    {
        var mod = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/A", "mod"));
        using var gated = new GatedGame(Install(Everything()));
        var preview = Preview(gated, mod);
        var settled = preview.Start();
        Assert.True(gated.Reached.Wait(TimeSpan.FromSeconds(60)));

        preview.Cancel();
        await settled.WaitAsync(TimeSpan.FromSeconds(60));                                                         // it ends its wait by its own token: nothing opens the gate

        Assert.Equal(GameDataPreviewState.Cancelled, preview.State);
        Assert.Empty(preview.Entries);
        Assert.False(preview.TryRead(ChunkOf(A), out _));
        Assert.Null(preview.Failure);
    }

    [Fact]
    public async Task A_preview_that_is_ready_when_the_mounts_are_replaced_keeps_what_it_served_for_a_reader_that_still_holds_them()
    {
        var mod = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/A", "mod"));
        var preview = Preview(Install(Everything()), mod);
        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        preview.Cancel();                                                      // BuildMounts replaces the preview: a thumbnail job on the old service goes on reading it

        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        Assert.True(preview.TryRead(ChunkOf(A), out var bytes));
        Assert.Equal(new[] { "g1", "mod" }, TagsOf(bytes, "Test/Obj/A"));
    }

    [Fact]
    public async Task The_status_says_what_the_worker_is_doing_and_Start_is_one_start()
    {
        var said = new List<string>();
        var mod = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/A", "mod"));
        var preview = Preview(Install(Everything()), mod, new Progress<string>(text => { lock (said) said.Add(text); }));   // posted from more than one thread: the list is only ever touched under its lock

        var first = preview.Start();
        var second = preview.Start();
        await first.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Same(first, second);
        await Task.Delay(100);                                                // Progress<T> posts to the pool here
        lock (said)
        {
            Assert.Contains(said, s => s.Contains("reading the project's declarations"));
            Assert.Contains(said, s => s.Contains("applying 1 chunk(s)"));
        }
    }

    // ================================================================================================ what it does when it cannot

    [Fact]
    public async Task A_setup_that_cannot_be_made_is_a_failed_preview_with_the_reason_and_the_project_is_not_the_worse_for_it()
    {
        var preview = new GameDataPreview(_ => throw new InvalidDataException("The GameData document of layer 'events' is not valid JSON"));

        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(GameDataPreviewState.Failed, preview.State);
        Assert.Equal("The GameData document of layer 'events' is not valid JSON", preview.Failure);
        Assert.Empty(preview.Entries);
        Assert.False(preview.TryRead(ChunkOf(A), out _));
        Assert.False(preview.IsTarget(ChunkOf(A)));
        Assert.Null(preview.Summary);
    }

    [Fact]
    public async Task A_setup_that_fails_to_read_a_file_says_its_words_and_whatever_nobody_foresaw_says_what_it_was()
    {
        var noGame = new GameDataPreview(_ => throw new IOException("No League DATA/FINAL installation was found under: Z:\\nowhere"));
        var odd = new GameDataPreview(_ => throw new InvalidOperationException("surprise"));

        await noGame.Start().WaitAsync(TimeSpan.FromSeconds(60));
        await odd.Start().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("No League DATA/FINAL installation was found under: Z:\\nowhere", noGame.Failure);
        Assert.Equal("internal error (InvalidOperationException): surprise", odd.Failure);
        Assert.Equal(GameDataPreviewState.Failed, odd.State);
    }

    [Fact]
    public async Task A_game_that_cannot_be_indexed_fails_the_preview_without_an_exception_to_anyone()
    {
        string noGameDirectory = _temp.Combine("empty");
        Directory.CreateDirectory(noGameDirectory);
        var mod = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/A", "mod"));
        var preview = Preview(new InstalledGame(noGameDirectory, _temp.Combine("c", "i.idx")), mod);

        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        // the chunk table is unavailable, so the target is skipped: the preview is ready, serves nothing, and the plan says why
        Assert.NotEqual(GameDataPreviewState.Pending, preview.State);
        Assert.Empty(preview.Entries);
        Assert.True(preview.IsTarget(ChunkOf(A)));
    }

    // ================================================================================================ the setup of a project

    private string Package(string name, string info, params (string Entry, byte[] Bytes)[] more)
    {
        string path = _temp.Combine(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string entry, byte[] bytes) { using var s = zip.CreateEntry(entry).Open(); s.Write(bytes); }
        Put("META/info.json", Encoding.UTF8.GetBytes(info));
        foreach (var (entry, bytes) in more) Put(entry, bytes);
        return path;
    }

    private ReyProject Import(string package)
    {
        string projects = _temp.Combine("projects");
        Directory.CreateDirectory(projects);
        return ReyProjectService.OpenFolder(FantomeImporter.Import(package, projects, null, new HashDatabase()).RootPath);
    }

    private static string Info(string gameData) =>
        "{\"Name\":\"t\",\"Author\":\"a\",\"Version\":\"1.0.0\",\"Description\":\"d\",\"Layers\":{\"base\":{\"Name\":\"base\",\"Priority\":0,\"GameData\":" + gameData + "}}}";

    private string GameWithBuild(string version, SyntheticGame game)
    {
        string directory = game.Write(_temp.Combine("game-b" + _installs++));
        File.WriteAllText(Path.Combine(directory, "content-metadata.json"), "{\"version\":\"" + version + "\"}");
        return directory;
    }

    private string Schema(uint latest)
    {
        string path = _temp.Combine("meta-" + latest + ".json");
        File.WriteAllText(path, "{\"formatVersion\":1,\"latest\":" + latest + ",\"classes\":{}}");
        return path;
    }

    [Fact]
    public void A_stored_document_that_is_not_JSON_is_a_mod_LTK_Manager_could_not_read_either_and_the_setup_says_which_layer()
    {
        var project = Import(Package("p.fantome", Info(AddTag(A, "Test/Obj/A", "mod"))));
        File.WriteAllText(Path.Combine(LtkProjectStore.DirectoryOf(project.RootPath!, "base"), LtkProjectStore.DeclarationsFileName), "{ this is not json");

        var ex = Assert.Throws<InvalidDataException>(() => GameDataSetups.ForProject(project, _temp.Path, null, null));

        Assert.Contains("The GameData document of layer 'base'", ex.Message);
    }

    [Fact]
    public async Task A_broken_document_makes_a_failed_preview_through_the_real_setup_and_a_valid_one_a_ready_preview()
    {
        var project = Import(Package("p.fantome", Info(AddTag(A, "Test/Obj/A", "mod"))));
        string directory = GameWithBuild("16.19.50+branch.x", Everything());

        var good = new GameDataPreview(_ => GameDataSetups.ForProject(project, directory, null, Schema(100), _temp.Combine("c-good", "i.idx")));
        await good.Start().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(GameDataPreviewState.Ready, good.State);
        Assert.Single(good.Entries);

        File.WriteAllText(Path.Combine(LtkProjectStore.DirectoryOf(project.RootPath!, "base"), LtkProjectStore.DeclarationsFileName), "{ not json");
        var broken = new GameDataPreview(_ => GameDataSetups.ForProject(project, directory, null, Schema(100), _temp.Combine("c-bad", "i.idx")));
        await broken.Start().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(GameDataPreviewState.Failed, broken.State);
        Assert.Contains("is not valid JSON", broken.Failure);
        Assert.Empty(broken.Entries);
    }

    [Fact]
    public async Task A_project_with_no_game_folder_is_a_ready_preview_that_serves_no_game_bin_names_its_targets_and_says_why()
    {
        // the game folder is missing (the editor passes null and the reason): the declarations are still read, so what they name is known and the write guard can hold
        var project = Import(Package("p.fantome", Info(AddTag(A, "Test/Obj/A", "mod"))));
        const string reason = "The configured folder does not exist. Project > Set Game Folder... selects it.";

        var preview = new GameDataPreview(_ => GameDataSetups.ForProject(project, null, null, Schema(100), _temp.Combine("c-none", "i.idx"), gameProblem: reason));
        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        Assert.Null(preview.Failure);
        Assert.Empty(preview.Entries);
        Assert.True(preview.IsTarget(ChunkOf(A)));
        Assert.Contains(ChunkOf(A), preview.DeclaredTargets!);
        var summary = preview.Summary!;
        Assert.Equal(0, summary.ChangedBins);
        var warning = Assert.Single(summary.Warnings);
        Assert.Contains("The game cannot be read (" + reason + ")", warning);
        Assert.Contains("a bin whose base is the game's could not be applied", warning);
        Assert.Equal(2, summary.Notes.Count);
        Assert.StartsWith("Class schema: ReyEngine's copy, data/meta/meta.db.json (newest build it describes: 100)", summary.Notes[0]);   // which schema it was said, with no manager's cache named
        Assert.Contains("its build is not known", summary.Notes[1]);                       // the schema cannot be read at a build nobody could tell
        var bin = Assert.Single(preview.Bins);
        Assert.False(bin.Applied);
        Assert.Contains(bin.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.TargetSkipped && d.Message.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_with_no_game_folder_still_applies_the_declarations_to_a_bin_the_mod_ships_itself()
    {
        var mod = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/A", "mod"));
        mod.WadFiles.Add(("base", "WAD/Own.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "own" }))));

        var preview = new GameDataPreview(_ => new GameDataSetup { Layers = mod.ToLayers(), Game = new UnavailableGame("no game"), ModFiles = mod.ToFiles() });
        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        Assert.True(preview.TryRead(ChunkOf(A), out var served));
        Assert.Equal(new[] { "own", "mod" }, TagsOf(served, "Test/Obj/A"));              // the mod's copy is the base: no game was needed
    }

    [Fact]
    public void The_schema_note_says_why_when_the_build_is_newer_than_the_schema_and_is_absent_when_the_schema_describes_it()
    {
        var project = Import(Package("p.fantome", Info(AddTag(A, "Test/Obj/A", "mod"))));

        var newer = GameDataSetups.ForProject(project, GameWithBuild("16.19.200+branch.x", Everything()), null, Schema(100), _temp.Combine("c1", "i"));
        var described = GameDataSetups.ForProject(project, GameWithBuild("16.19.50+branch.x", Everything()), null, Schema(100), _temp.Combine("c2", "i"));
        var missing = GameDataSetups.ForProject(project, GameWithBuild("16.19.50+branch.x", Everything()), null, _temp.Combine("no-such-schema.json"), _temp.Combine("c3", "i"));
        var seamed = GameDataSetups.ForProject(project, GameWithBuild("16.19.200+branch.x", Everything()), null, Schema(100), _temp.Combine("c4", "i"), buildOverride: new GameBuild(16, 19, 50));

        Assert.Contains("the game build (16.19.200) is newer than the class schema (newest build it describes: 100)", newer.SchemaNote);
        Assert.Contains("LTK Manager skips these too", newer.SchemaNote);
        Assert.Equal(100u, newer.SchemaLatest);
        Assert.Null(described.SchemaNote);
        Assert.Contains("data/meta/meta.db.json is missing", missing.SchemaNote);
        Assert.Null(missing.SchemaLatest);
        Assert.Null(seamed.SchemaNote);                                        // the seam reads the schema at a build it describes
        Assert.Equal(new GameBuild(16, 19, 200), newer.Build);
    }

    // ================================================================================================ what it makes of the game's index

    [Fact]
    public async Task A_summary_counts_the_modules_the_bins_and_the_skipped_edits_in_the_words_the_editor_shows()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(
            Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"mod\"]}}"),
            Target(X, "{\"Test/Obj/X\":{\"+tags\":[\"mod\"],\"nope.value\":1}}")));
        var preview = Preview(Install(Everything()), mod);

        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        var summary = preview.Summary!;
        Assert.Equal(2, summary.Modules);
        Assert.Equal(2, summary.Targets);
        Assert.Equal(2, summary.ChangedBins);
        Assert.StartsWith("GameData: 2 modules changed 2 bins; ", summary.Headline);
        Assert.Empty(summary.Warnings);
        Assert.False(summary.IndexUnsettled);
        Assert.Equal(new[] { ChunkOf(A), ChunkOf(X) }.Order(), preview.Entries.Select(e => e.PathHash).Order());
        Assert.All(preview.Bins, b => Assert.Equal(new[] { "base" }, b.Layers));
    }

    [Fact]
    public async Task An_index_that_could_not_be_read_for_a_reason_of_the_moment_is_a_warning_that_says_what_it_affects_and_how_to_retry()
    {
        var game = new SyntheticGame()
            .Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
            .Add("B.wad.client", X, Bin(Obj("Test/Obj/X", new[] { "x" })));
        string directory = game.Write(_temp.Combine("game-held"));
        var mod = new SyntheticMod().Layer("base", 0, Doc(Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]},\"Test/Obj/X\":{\"+tags\":[\"e\"]}")));

        GameDataPreview preview;
        using (new FileStream(Path.Combine(directory, "DATA", "FINAL", "B.wad.client"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            preview = Preview(new InstalledGame(directory, _temp.Combine("cache-held", "index.idx")), mod);
            await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));
        }

        var summary = preview.Summary!;
        Assert.True(summary.IndexUnsettled);
        var warning = Assert.Single(summary.Warnings, w => w.StartsWith("The game's index is incomplete", StringComparison.Ordinal));
        Assert.Contains("B.wad.client", warning);
        Assert.Contains("not a fact about the game", warning);
        Assert.Contains("Retry", warning);
        Assert.Contains(summary.Warnings, w => w.Contains("could not be read just now"));
        Assert.Contains(summary.Notes, n => n.Contains("was not kept for the next session"));
        // what only B declares reads as absent this time, and the diagnostics carry the same words the crate gives it
        Assert.Contains(preview.GeneralDiagnostics.Concat(preview.Bins.SelectMany(b => b.Diagnostics)), d => d.Kind == GameDataOverlayDiagnosticKind.EntryUnresolved && d.Target == "Test/Obj/X");
        Assert.Equal("GameData: 1 module changed 1 bin; 0 edits skipped", summary.Headline);
    }

    [Fact]
    public async Task A_settled_index_has_no_warnings_and_the_summary_of_a_layer_the_engine_refuses_says_so()
    {
        var mod = new SyntheticMod()
            .Layer("base", 0, AddTag(A, "Test/Obj/A", "mod"))
            .Layer("bad", 1, "{\"version\":2,\"modules\":[]}");                  // a document ltk_game_data refuses whole
        var preview = Preview(Install(Everything()), mod);

        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        var summary = preview.Summary!;
        Assert.Equal(1, summary.RejectedLayers);
        Assert.Equal("GameData: 1 module changed 1 bin; 0 edits skipped; 1 layer refused", summary.Headline);
        Assert.Contains(preview.GeneralDiagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.DeclarationsRejected && d.Layer == "bad");
        Assert.Empty(summary.Warnings);
    }

    // ================================================================================================ a shipping map bin routes the map

    [Fact]
    public async Task A_changed_shipping_map_bin_names_the_container_its_Default_skin_loads()
    {
        const string ship = "data/maps/shipping/map11/map11.bin";
        var skin = new ReyEngine.Formats.LtkGameData.PropObject(H("Maps/Shipping/Map11/MapSkins/Default"), H("MapSkin"));
        skin.Properties.Set(H("name"), new PropString("Default"));
        skin.Properties.Set(H("mMapContainerLink"), new PropString("Maps/MapGeometry/Map11/Base_SRX"));
        var game = new SyntheticGame().Add("Map11.wad.client", ship, Bin(skin));
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(ship,
            "{\"Maps/Shipping/Map11/MapSkins/Default\":{\"mMapContainerLink\":\"Maps/MapGeometry/Map11/Milkshake_SRS\"}}")));
        var preview = Preview(Install(game), mod);

        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        var container = Assert.Single(preview.Containers);
        Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS", container.ContainerLink);
        Assert.Equal("data/Maps/MapGeometry/Map11/Milkshake_SRS.mapgeo", container.GeometryPath);
    }

    // ================================================================================================ what the guard is told while it works

    [Fact]
    public async Task What_the_target_modules_name_is_known_before_the_setup_is_made_when_the_declarations_are_read_first()
    {
        var mod = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/A", "mod"));
        var game = Install(Everything());
        var inSetup = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        // the setup is where the class schema is loaded and the game is made: slow, and not needed to know what a target module names
        var preview = new GameDataPreview(
            _ => { inSetup.Set(); release.Wait(TimeSpan.FromSeconds(60)); return new GameDataSetup { Layers = mod.ToLayers(), Game = game, ModFiles = mod.ToFiles() }; },
            declarations: () => mod.ToLayers());
        var settled = preview.Start();
        try
        {
            Assert.True(inSetup.Wait(TimeSpan.FromSeconds(60)));

            Assert.Equal(GameDataPreviewState.Pending, preview.State);
            Assert.True(preview.IsTarget(ChunkOf(A)));
            Assert.False(preview.IsTarget(ChunkOf(X)));
            Assert.False(preview.TargetsKnown);                                  // but what a module that edits entries would add is not
        }
        finally
        {
            release.Set();                                                       // whatever became of the assertions, the worker is not left waiting
        }
        await settled.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        Assert.True(preview.TargetsKnown);
    }

    [Fact]
    public async Task A_chunk_only_an_entries_module_names_is_known_the_moment_the_plan_exists_and_not_before()
    {
        // an ENTRIES module names Test/Obj/A; no target module names the bin it lives in, so only the plan can say that data/t/a.bin is a target
        var mod = new SyntheticMod().Layer("base", 0, Doc(Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]}")));
        using var staged = new StagedGame(Install(Everything()));
        var preview = Preview(staged, mod);
        var settled = preview.Start();
        await Task.Delay(200);                                                   // the worker has read the documents and is waiting for the game's table

        Assert.Equal(GameDataPreviewState.Pending, preview.State);
        Assert.False(preview.TargetsKnown);
        Assert.False(preview.IsTarget(ChunkOf(A)));                              // the window: it is a target, and nothing yet says so

        staged.TableGate.Set();
        Assert.True(staged.ReadReached.Wait(TimeSpan.FromSeconds(60)));          // planned, and the first chunk is being read

        Assert.Equal(GameDataPreviewState.Pending, preview.State);
        Assert.True(preview.TargetsKnown);
        Assert.True(preview.IsTarget(ChunkOf(A)));                               // published the moment the plan existed, not when every chunk was applied
        Assert.False(preview.IsTarget(ChunkOf(X)));
        Assert.Empty(preview.Entries);

        staged.ReadGate.Set();
        await settled.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        Assert.True(preview.IsTarget(ChunkOf(A)));
    }

    [Fact]
    public async Task What_a_read_returns_is_the_callers_own_copy_and_not_the_one_the_overlay_keeps()
    {
        var mod = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/A", "mod"));
        var preview = Preview(Install(Everything()), mod);
        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(preview.TryRead(ChunkOf(A), out var first));
        byte[] original = (byte[])first.Clone();
        Array.Clear(first);                                                      // a caller does what it likes with what it is given

        Assert.True(preview.TryRead(ChunkOf(A), out var second));
        Assert.NotSame(first, second);
        Assert.Equal(original, second);
    }

    [Fact]
    public async Task A_target_spelled_with_a_path_that_can_leave_a_folder_is_served_but_listed_by_its_hash_only()
    {
        // the module's spelling hashes like any other; the mod ships a bin under that hash, so the chunk is served
        const string hostile = "data/x/..\\..\\y.cmd";
        var mod = new SyntheticMod().Layer("base", 0, Doc(
            Target(hostile.Replace("\\", "\\\\"), "{\"Test/Obj/H\":{\"+tags\":[\"mod\"]}}"),
            Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"mod\"]}}")));
        mod.WadFiles.Add(("base", "WAD/H.wad.client", $"{ChunkOf(hostile):x16}.bin", Bin(Obj("Test/Obj/H", new[] { "own" }))));
        var preview = Preview(Install(Everything()), mod);

        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(2, preview.Entries.Count);
        var odd = Assert.Single(preview.Entries, e => e.PathHash == ChunkOf(hostile));
        Assert.Null(odd.Path);                                                   // the spelling is not passed on to the browser
        Assert.Equal(A, Assert.Single(preview.Entries, e => e.PathHash == ChunkOf(A)).Path);   // a plain one is
        Assert.Equal(hostile, Assert.Single(preview.Bins, b => b.Chunk == ChunkOf(hostile)).Name);   // and what the module said stays where its author reads it
    }

    // ================================================================================================ which class schema

    private string SchemaFile(string name, uint latest, bool describesNewField)
    {
        string classes = describesNewField
            ? $"\"0x{TestClass:x8}\":{{\"name\":\"TestClass\",\"properties\":{{\"0x{H("mNew"):x8}\":{{\"name\":\"mNew\",\"revisions\":[{{\"from\":1,\"type\":[\"F32\",\"0x0\",\"0x0\",\"0x0\"]}}]}}}}}}"
            : "";
        string path = _temp.Combine(name);
        File.WriteAllText(path, $"{{\"formatVersion\":1,\"latest\":{latest},\"classes\":{{{classes}}}}}");
        return path;
    }

    /// <summary>A project whose one module adds a property the bin does not hold: only a class schema that describes the game build can type it.</summary>
    private async Task<(GameDataPreview Preview, GameDataSetup Setup)> PreviewOfNewField(string? managerSchema, string? ownSchema, string build = "16.19.50+branch.x")
    {
        var project = Import(Package("p-" + _installs++ + ".fantome", Info(Doc(Target(A, "{\"Test/Obj/A\":{\"mNew\":5}}")))));
        string directory = GameWithBuild(build, Everything());
        GameDataSetup? setup = null;
        var preview = new GameDataPreview(_ => setup = GameDataSetups.ForProject(project, directory, null, ownSchema, _temp.Combine("c-schema-" + _installs++, "i.idx"), managerSchemaPath: managerSchema));
        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        return (preview, setup!);
    }

    [Fact]
    public async Task LTK_Managers_own_cache_is_the_schema_when_it_can_be_read_even_where_ReyEngines_copy_is_newer_and_the_preview_says_what_that_costs()
    {
        // the manager's cache stops at build 40 and the game is at 50: undescribed, as the manager's own install is today. ReyEngine's copy describes it.
        var (preview, setup) = await PreviewOfNewField(SchemaFile("manager-old.json", 40, describesNewField: true), SchemaFile("own-new.json", 100, describesNewField: true));

        var summary = preview.Summary!;
        Assert.Equal("LTK Manager's cache (newest build it describes: 40)", setup.SchemaSource);
        Assert.Equal(1, summary.EditsSkipped);                                   // what the manager skips today
        Assert.Equal("Class schema: LTK Manager's cache (newest build it describes: 40).", summary.Notes[0]);
        Assert.Contains(summary.Notes, n => n.StartsWith("Edits that need the class schema: the game build (16.19.50) is newer than the class schema (newest build it describes: 40)", StringComparison.Ordinal)
                                            && n.Contains("LTK Manager skips these too until its own class schema is updated"));
        var more = Assert.Single(summary.Notes, n => n.StartsWith("LTK Manager will apply", StringComparison.Ordinal));
        Assert.StartsWith("LTK Manager will apply 1 more edit once it updates its class schema: ReyEngine's own copy (newest build it describes: 100)", more);
    }

    [Fact]
    public async Task A_manager_cache_that_describes_the_build_is_used_and_nothing_is_said_of_a_newer_copy()
    {
        var (preview, setup) = await PreviewOfNewField(SchemaFile("manager-new.json", 100, describesNewField: true), SchemaFile("own-old.json", 40, describesNewField: false));

        var summary = preview.Summary!;
        Assert.Equal("LTK Manager's cache (newest build it describes: 100)", setup.SchemaSource);
        Assert.Equal(0, summary.EditsSkipped);                                   // typed by the manager's own schema
        Assert.Equal(1, summary.ChangedBins);
        Assert.DoesNotContain(summary.Notes, n => n.StartsWith("LTK Manager will apply", StringComparison.Ordinal));
        Assert.DoesNotContain(summary.Notes, n => n.StartsWith("Edits that need the class schema", StringComparison.Ordinal));
        Assert.Null(setup.NewerSchemaOptions);
    }

    [Fact]
    public async Task ReyEngines_copy_is_used_when_the_managers_cache_is_not_there_or_does_not_parse_and_the_note_says_so()
    {
        string own = SchemaFile("own.json", 100, describesNewField: true);
        File.WriteAllText(_temp.Combine("manager-broken.json"), "{ this is not a database");

        var (absent, absentSetup) = await PreviewOfNewField(_temp.Combine("no-such-manager.json"), own);
        var (broken, brokenSetup) = await PreviewOfNewField(_temp.Combine("manager-broken.json"), own);

        Assert.Equal("ReyEngine's copy, data/meta/meta.db.json (newest build it describes: 100); LTK Manager's own cache was not found", absentSetup.SchemaSource);
        Assert.StartsWith("ReyEngine's copy, data/meta/meta.db.json (newest build it describes: 100); LTK Manager's cache could not be read (", brokenSetup.SchemaSource);
        foreach (var preview in new[] { absent, broken })
        {
            Assert.Equal(0, preview.Summary!.EditsSkipped);                      // ReyEngine's copy describes the build
            Assert.StartsWith("Class schema: ReyEngine's copy", preview.Summary.Notes[0]);
        }
    }

    [Fact]
    public async Task With_no_schema_at_all_nothing_names_a_source_and_the_note_says_why_edits_are_skipped()
    {
        var (preview, setup) = await PreviewOfNewField(_temp.Combine("no-manager.json"), _temp.Combine("no-own.json"));

        Assert.Null(setup.SchemaSource);
        Assert.Equal(1, preview.Summary!.EditsSkipped);
        Assert.Contains(preview.Summary.Notes, n => n.Contains("ReyEngine has no class schema", StringComparison.Ordinal));
        Assert.DoesNotContain(preview.Summary.Notes, n => n.StartsWith("Class schema:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_setup_that_is_given_no_manager_cache_does_not_look_for_one()
    {
        // the Formats tests above and below pass no manager path: they must not read the machine's LeagueToolkit folder
        var project = Import(Package("p-none.fantome", Info(AddTag(A, "Test/Obj/A", "mod"))));
        var setup = GameDataSetups.ForProject(project, GameWithBuild("16.19.50+branch.x", Everything()), null, SchemaFile("own-only.json", 100, describesNewField: false), _temp.Combine("c-none", "i.idx"));

        Assert.StartsWith("ReyEngine's copy, data/meta/meta.db.json (newest build it describes: 100)", setup.SchemaSource);
        Assert.DoesNotContain("manager", setup.SchemaSource!, StringComparison.OrdinalIgnoreCase);
        await Task.CompletedTask;
    }

    // ================================================================================================ what a preview remembers from the one before

    /// <summary>The game, counting the chunks it is asked to read.</summary>
    private sealed class CountingGame : IGameDataGame
    {
        private readonly IGameDataGame _inner;
        private int _reads;

        public CountingGame(IGameDataGame inner) { _inner = inner; }

        public int Reads => Volatile.Read(ref _reads);

        public GameChunkTable GetTable(CancellationToken cancellationToken) => _inner.GetTable(cancellationToken);

        public GameObjectIndex GetObjects(CancellationToken cancellationToken) => _inner.GetObjects(cancellationToken);

        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return _inner.ReadChunk(archive, chunk, maxBytes, cancellationToken);
        }

        public string? IndexNotSaved => _inner.IndexNotSaved;
    }

    [Fact]
    public async Task A_preview_that_inherited_the_chunks_the_same_declarations_named_answers_the_guard_before_the_game_is_read()
    {
        // an ENTRIES module names Test/Obj/A: only the plan can say that data/t/a.bin is a target - which is the window the preview above shows, unless it inherited
        var mod = new SyntheticMod().Layer("base", 0, Doc(Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]}")));
        using var staged = new StagedGame(Install(Everything()));
        var preview = new GameDataPreview(_ => new GameDataSetup { Layers = mod.ToLayers(), Game = staged, ModFiles = mod.ToFiles() }, inheritedTargets: new HashSet<ulong> { ChunkOf(A) });
        var settled = preview.Start();
        await Task.Delay(200);                                                   // the worker has read the documents and is waiting for the game's table

        Assert.Equal(GameDataPreviewState.Pending, preview.State);
        Assert.True(preview.TargetsKnown);
        Assert.True(preview.IsTarget(ChunkOf(A)));
        Assert.False(preview.IsTarget(ChunkOf(X)));                              // and the others are known not to be: a bin that is no target can be saved at once

        staged.TableGate.Set();
        Assert.True(staged.ReadReached.Wait(TimeSpan.FromSeconds(60)));          // planned
        Assert.True(preview.IsTarget(ChunkOf(A)));                               // now from the plan itself
        staged.ReadGate.Set();
        await settled.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        Assert.True(preview.IsTarget(ChunkOf(A)));
        Assert.False(preview.IsTarget(ChunkOf(X)));
        Assert.Contains(ChunkOf(A), preview.NamedChunks());
    }

    [Fact]
    public async Task What_a_preview_inherited_is_dropped_when_the_plan_exists_so_a_chunk_the_declarations_no_longer_name_is_no_target()
    {
        var mod = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/A", "mod"));
        var game = Install(Everything());
        var preview = new GameDataPreview(_ => new GameDataSetup { Layers = mod.ToLayers(), Game = game, ModFiles = mod.ToFiles() }, inheritedTargets: new HashSet<ulong> { ChunkOf(X) });
        Assert.True(preview.IsTarget(ChunkOf(X)));                               // before anything ran, the inherited names are the answer
        Assert.True(preview.TargetsKnown);

        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(preview.IsTarget(ChunkOf(X)));                              // the declarations of this preview do not name it
        Assert.True(preview.IsTarget(ChunkOf(A)));
        Assert.Equal(new[] { ChunkOf(A) }, preview.NamedChunks().ToArray());
    }

    [Fact]
    public void The_memory_hands_on_the_chunks_only_for_the_same_documents_over_the_same_game()
    {
        var memory = new GameDataPreviewMemory();
        Assert.False(memory.HasNamed);
        Assert.Null(memory.LastNamed);
        Assert.Null(memory.InheritNamed(1, "game|16.19.1"));

        var chunks = new HashSet<ulong> { 1, 2 };
        memory.RememberNamed(7, "game|16.19.1", chunks);

        Assert.True(memory.HasNamed);
        Assert.Same(chunks, memory.InheritNamed(7, "game|16.19.1"));
        Assert.Null(memory.InheritNamed(8, "game|16.19.1"));                     // other documents
        Assert.Null(memory.InheritNamed(7, "game|16.19.2"));                     // a game that was patched
        Assert.Same(chunks, memory.LastNamed);                                   // what an editor keeps refusing when the preview stops, whatever it was made from

        var later = new HashSet<ulong> { 3 };
        memory.RememberNamed(9, "other", later);
        Assert.Null(memory.InheritNamed(7, "game|16.19.1"));
        Assert.Same(later, memory.LastNamed);
    }

    [Fact]
    public async Task The_fingerprint_of_layers_is_the_documents_fingerprint_a_preview_of_them_gives()
    {
        var mod = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/A", "mod"));
        var preview = Preview(Install(Everything()), mod);
        await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(preview.DocumentsFingerprint, GameDataPreview.FingerprintOf(mod.ToLayers()));
        var other = new SyntheticMod().Layer("base", 0, AddTag(A, "Test/Obj/A", "other"));
        Assert.NotEqual(preview.DocumentsFingerprint, GameDataPreview.FingerprintOf(other.ToLayers()));
    }

    [Fact]
    public async Task The_count_of_edits_a_newer_schema_would_apply_is_worked_out_once_for_the_same_documents_game_and_schemas()
    {
        var project = Import(Package("p-memo.fantome", Info(Doc(Target(A, "{\"Test/Obj/A\":{\"mNew\":5}}")))));
        string directory = GameWithBuild("16.19.50+branch.x", Everything());
        string own = SchemaFile("memo-own.json", 100, describesNewField: true);
        var memory = new GameDataPreviewMemory();

        async Task<(GameDataPreview Preview, GameDataSetup Setup, int Reads)> Run(string managerSchema)
        {
            CountingGame? counted = null;
            GameDataSetup? setup = null;
            var preview = new GameDataPreview(
                _ => setup = GameDataSetups.ForProject(project, directory, null, own, _temp.Combine("c-memo-" + _installs++, "i.idx"),
                    gameFactory: dir => counted = new CountingGame(new InstalledGame(dir, _temp.Combine("c-memo-i-" + _installs++, "i.idx"))), managerSchemaPath: managerSchema),
                memory: memory);
            await preview.Start().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(GameDataPreviewState.Ready, preview.State);
            return (preview, setup!, counted!.Reads);
        }

        string manager = SchemaFile("memo-manager.json", 40, describesNewField: true);
        var first = await Run(manager);
        var second = await Run(manager);                                         // the rebuild that follows one of the editor's own saves
        var third = await Run(SchemaFile("memo-manager-2.json", 41, describesNewField: true));   // another cache: another schema, another question

        string more = Assert.Single(first.Preview.Summary!.Notes, n => n.StartsWith("LTK Manager will apply", StringComparison.Ordinal));
        Assert.Equal(more, Assert.Single(second.Preview.Summary!.Notes, n => n.StartsWith("LTK Manager will apply", StringComparison.Ordinal)));   // the same words
        Assert.True(second.Reads < first.Reads, $"the second application ran again: {first.Reads} reads, then {second.Reads}");                      // and not a second application of the declarations
        Assert.Equal(first.Reads, third.Reads);
        Assert.NotNull(first.Setup.SchemaIdentity);
        Assert.NotNull(first.Setup.NewerSchemaIdentity);
        Assert.Equal(first.Setup.SchemaIdentity, second.Setup.SchemaIdentity);
        Assert.NotEqual(first.Setup.SchemaIdentity, third.Setup.SchemaIdentity);
    }

    // ================================================================================================ the schema file

    [Fact]
    public void A_schema_file_larger_than_the_reader_takes_is_refused_before_it_is_read()
    {
        string path = _temp.Combine("huge-schema.json");
        using (var stream = File.Create(path)) stream.SetLength(LtkMetaSchema.MaxFileBytes + 1);

        var ex = Assert.Throws<IOException>(() => LtkMetaSchema.Load(path));

        Assert.Contains("schema reader", ex.Message);
        Assert.Contains("more than the", ex.Message);
    }

    [Fact]
    public void A_schema_file_its_owner_holds_open_for_writing_can_still_be_read_and_the_identity_follows_the_bytes()
    {
        string path = Schema(100);
        string sameBytes = _temp.Combine("same-bytes.json");
        File.Copy(path, sameBytes);
        string otherBytes = Schema(101);
        using (var owner = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))   // LTK Manager, updating its cache
        {
            var schema = LtkMetaSchema.Load(path);                                // a reader that did not allow writers would fail with a sharing violation here

            Assert.Equal(100u, schema.Latest);
            Assert.Equal(LtkMetaSchema.Load(sameBytes).Identity, schema.Identity);   // the same bytes, the same identity
            Assert.NotEqual(LtkMetaSchema.Load(otherBytes).Identity, schema.Identity);
        }
        // and a handle that may delete the file - what renaming a new file over it needs - is not in the way either
        string copy = _temp.Combine("schema-copy.json");
        File.Copy(path, copy);
        using (var deleting = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose))
            Assert.Equal(100u, LtkMetaSchema.Load(copy).Latest);
    }
}

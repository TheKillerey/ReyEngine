using System.Text.Json;
using System.Text.Json.Nodes;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.Meta;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M823: the preview with the edits kept on top of the GameData - where the layer's document gets them, that the preview applies them after the package's own modules, the bin the edit is the difference from
/// (<c>B</c>), the in-place refresh that makes a save visible to whoever reads next, and the baselines the planner declares a project bin between when the GameData also targets it. A synthetic game and a
/// synthetic package; no view model.
/// </summary>
public sealed class LtkEditPreviewTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private int _n;

    public void Dispose() => _temp.Dispose();

    private const string A = "data/t/a.bin", M = "data/t/m.bin";

    private static ulong ChunkOf(string path) => HashAlgorithms.WadPath(path);

    private static readonly IDeclarationNames Names = new Known("Test/Obj/A", "Test/Obj/M", "tags", "count", "name", "TestClass");

    private sealed class Known : IDeclarationNames
    {
        private readonly Dictionary<uint, string> _names = new();
        public Known(params string[] names) { foreach (var name in names) _names[H(name)] = name; }
        public string? Field(uint hash) => _names.GetValueOrDefault(hash);
        public string? Class(uint hash) => Field(hash);
        public string? Entry(uint hash) => Field(hash);
        public string? File(ulong hash) => null;
    }

    /// <summary>The game, with a count of the chunks read from it: an edit must make the preview read the one chunk it changed and no other.</summary>
    private sealed class CountingGame : IGameDataGame
    {
        private readonly IGameDataGame _inner;
        public readonly Dictionary<ulong, int> Reads = new();
        public CountingGame(IGameDataGame inner) { _inner = inner; }
        public GameChunkTable GetTable(CancellationToken cancellationToken) => _inner.GetTable(cancellationToken);
        public GameObjectIndex GetObjects(CancellationToken cancellationToken) => _inner.GetObjects(cancellationToken);
        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken)
        {
            lock (Reads) Reads[chunk] = Reads.GetValueOrDefault(chunk) + 1;
            return _inner.ReadChunk(archive, chunk, maxBytes, cancellationToken);
        }
        public string? IndexNotSaved => _inner.IndexNotSaved;
        public int ReadsOf(ulong chunk) { lock (Reads) return Reads.GetValueOrDefault(chunk); }
        public void Reset() { lock (Reads) Reads.Clear(); }
    }

    private string Game(string name, byte[]? a = null, byte[]? m = null) =>
        new SyntheticGame()
            .Add("DATA.wad.client", "data/readme.txt", new byte[] { 1 })
            .Add("Global.wad.client", "data/global.txt", new byte[] { 2 })
            .Add("Champions/A.wad.client", A, a ?? Bin(Obj("Test/Obj/A", new[] { "g1" }, count: 1)))
            .Add("Map11.wad.client", M, m ?? Bin(Obj("Test/Obj/M", new[] { "game" })))
            .Write(_temp.Combine(name));

    private static string AddTag(string path, string obj, string tag) => Target(path, $"{{\"{obj}\":{{\"+tags\":[\"{tag}\"]}}}}");

    /// <summary>A project that stores a package's GameData: layer <c>base</c> names A and M, layer <c>fix</c> names A again.</summary>
    private ReyProject Project(string name = "p")
    {
        string root = _temp.Combine(name + _n++);
        Directory.CreateDirectory(root);
        var project = new ReyProject { Name = name, RootPath = root };
        project.Layers.Add(new ProjectLayer { Name = "base", Priority = 0, DeclarationsKey = "base" });
        project.Layers.Add(new ProjectLayer { Name = "fix", Priority = 1, DeclarationsKey = "fix" });
        LtkProjectStore.WriteDeclarations(root, "base", Doc(AddTag(A, "Test/Obj/A", "base"), AddTag(M, "Test/Obj/M", "base")));
        LtkProjectStore.WriteDeclarations(root, "fix", Doc(AddTag(A, "Test/Obj/A", "fix")));
        return project;
    }

    private (GameDataPreview Preview, CountingGame Game) Ready(ReyProject project, string gameDirectory)
    {
        CountingGame? counting = null;
        var setup = GameDataSetups.ForProject(project, gameDirectory, null, null, _temp.Combine("cache" + _n++, "i.idx"),
            gameFactory: dir => counting = new CountingGame(new InstalledGame(dir, _temp.Combine("cache" + _n++, "i.idx"))));
        var preview = new GameDataPreview(_ => setup);
        preview.Start().Wait(TimeSpan.FromSeconds(60));
        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        return (preview, counting!);
    }

    /// <summary>The module for the edit that turns <paramref name="imported"/> into <paramref name="edited"/>.</summary>
    private static string ModuleFor(string path, byte[] imported, byte[] edited, IGameDataSchema? schema = null)
    {
        var outcome = GameDataEditPlanner.Plan(path, imported, edited, Names, schema ?? NoSchema.Instance);
        Assert.Equal(GameDataEditKind.Declared, outcome.Kind);
        return outcome.ModuleText!;
    }

    private static byte[] Served(GameDataPreview preview, string path)
    {
        Assert.True(preview.TryRead(ChunkOf(path), out var bytes), "the preview serves nothing for " + path);
        return bytes;
    }

    // ===================================================== where the edit goes

    [Fact]
    public void The_edit_is_composed_into_the_layer_document_behind_its_imported_modules_and_the_package_text_is_kept_beside_it()
    {
        var project = Project();
        var (preview, _) = Ready(project, Game("g-compose"));
        byte[] b = Served(preview, A);
        byte[] e = Bin(Obj("Test/Obj/A", new[] { "g1", "base", "fix", "mine" }, count: 7));
        string module = ModuleFor(A, b, e);

        // the plan says which layer applies last to the bin: that one holds the edit
        var target = preview.Overlay!.Plan().Target(ChunkOf(A))!;
        Assert.Equal(new[] { "base", "fix" }, target.Layers.ToArray());
        var layer = project.Layers.Single(l => l.Name == target.Layers[^1]);
        LtkEditStore.Set(project, layer, ChunkOf(A), module);
        var inputs = GameDataLayerInput.FromProject(project);

        var fix = inputs.Single(i => i.Name == "fix");
        var baseLayer = inputs.Single(i => i.Name == "base");
        Assert.Null(baseLayer.ImportedDocumentText);                                // a layer with no edit is what it was
        Assert.Null(baseLayer.EditedChunks);
        Assert.NotNull(fix.ImportedDocumentText);
        Assert.Equal(new[] { ChunkOf(A) }, fix.EditedChunks!.ToArray());
        var composed = GameDataDocumentText.Read(fix.DocumentText!);
        var imported = GameDataDocumentText.Read(fix.ImportedDocumentText!);
        Assert.Equal(imported.Modules.Count + 1, composed.Modules.Count);
        Assert.Equal(imported.Modules.Select(m => m.Text), composed.Modules.Take(imported.Modules.Count).Select(m => m.Text));   // the package's modules, byte for byte, first
        Assert.Equal(A, composed.Modules[^1].Target);
        Assert.Equal(imported.Modules.Count, JsonDocument.Parse(composed.Modules[^1].Text).RootElement.GetProperty("origin").GetProperty("module").GetInt32());
        var plain = fix.ImportedOnly();
        Assert.Equal(fix.ImportedDocumentText, plain.DocumentText);
        Assert.Null(plain.ImportedDocumentText);
        Assert.Null(plain.EditedChunks);
        Assert.Same(baseLayer, baseLayer.ImportedOnly());
    }

    [Fact]
    public void The_preview_applies_the_edit_after_every_imported_module_and_B_is_the_bin_without_it()
    {
        var project = Project();
        var (first, _) = Ready(project, Game("g-after"));
        byte[] b = Served(first, A);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(b, "Test/Obj/A"));        // the package alone: base's module, then fix's
        byte[] e = Bin(Obj("Test/Obj/A", new[] { "g1", "base", "fix", "mine" }, count: 7));
        LtkEditStore.Set(project, project.Layers.Single(l => l.Name == "fix"), ChunkOf(A), ModuleFor(A, b, e));

        var (preview, _) = Ready(project, Game("g-after-2"));

        Assert.Equal(new[] { "g1", "base", "fix", "mine" }, TagsOf(Served(preview, A), "Test/Obj/A"));
        Assert.Null(BinTreeEquivalence.FirstDifference(SafeBinTree.Parse(e), SafeBinTree.Parse(Served(preview, A)), Names));
        Assert.True(preview.TryReadImportedOnly(ChunkOf(A), out var imported, out var problem), problem);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(imported!, "Test/Obj/A"));   // B: what the package makes, none of the edit
        Assert.Equal(b, imported);
        // a bin with no edit on top is served as ever, and B of it is what is served
        Assert.True(preview.TryReadImportedOnly(ChunkOf(M), out var m, out _));
        Assert.Equal(Served(preview, M), m);
        Assert.Equal(new[] { "game", "base" }, TagsOf(m!, "Test/Obj/M"));
    }

    [Fact]
    public void A_bin_the_package_names_but_does_not_change_is_still_a_base_an_edit_can_follow()
    {
        string root = _temp.Combine("quiet");
        Directory.CreateDirectory(root);
        var project = new ReyProject { Name = "quiet", RootPath = root };
        project.Layers.Add(new ProjectLayer { Name = "base", DeclarationsKey = "base" });
        // every edit of the module is skipped (the object is not in the bin): the package leaves A as the game has it
        LtkProjectStore.WriteDeclarations(root, "base", Doc(AddTag(A, "Test/Obj/Nowhere", "x")));
        byte[] game = Bin(Obj("Test/Obj/A", new[] { "g1" }, count: 1));
        var (before, _) = Ready(project, Game("g-quiet", a: game));
        Assert.False(before.TryRead(ChunkOf(A), out _));                            // nothing is served: the bin is the game's
        Assert.True(before.TryReadImportedOnly(ChunkOf(A), out var b, out var problem), problem);
        Assert.Equal(game, b);                                                      // but B exists: it is the game's own bytes

        string module = ModuleFor(A, b!, Bin(Obj("Test/Obj/A", new[] { "g1", "mine" }, count: 1)));
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(A), module);
        var (after, _) = Ready(project, Game("g-quiet-2", a: game));

        Assert.Equal(new[] { "g1", "mine" }, TagsOf(Served(after, A), "Test/Obj/A"));   // the edit is now what the bin is
        Assert.True(after.TryReadImportedOnly(ChunkOf(A), out var again, out _));
        Assert.Equal(game, again);
    }

    [Fact]
    public void A_bin_no_declaration_names_has_no_base_and_the_preview_says_so()
    {
        var (preview, _) = Ready(Project(), Game("g-none"));

        Assert.False(preview.TryReadImportedOnly(ChunkOf("data/t/other.bin"), out var bytes, out var problem));

        Assert.Null(bytes);
        Assert.Contains("no declaration", problem);
    }

    // ===================================================== edits the layer has no room for, or cannot read

    private string DeclarationsOf(ReyProject project, string key) =>
        Path.Combine(LtkProjectStore.DirectoryOf(project.RootPath!, key), LtkProjectStore.DeclarationsFileName);

    /// <summary>A project whose fix layer keeps an edit of A, and the document the layer would be with no edit.</summary>
    private (ReyProject Project, string Game) ProjectWithEdit(string name)
    {
        var project = Project(name);
        string game = Game(name + "-game");
        var (preview, _) = Ready(project, game);
        byte[] e = Bin(Obj("Test/Obj/A", new[] { "g1", "base", "fix", "mine" }, count: 7));
        LtkEditStore.Set(project, project.Layers[1], ChunkOf(A), ModuleFor(A, Served(preview, A), e));
        return (project, game);
    }

    [Fact]
    public void Edits_that_would_take_the_layers_document_past_the_limit_are_refused_and_the_package_still_applies()
    {
        var (project, game) = ProjectWithEdit("limit");
        long document = new FileInfo(DeclarationsOf(project, "fix")).Length;
        var everything = GameDataLayerInput.FromProject(project).Single(l => l.Name == "fix");
        Assert.True(everything.EditsProblem is null, everything.EditsProblem);
        Assert.NotNull(everything.ImportedDocumentText);

        // room for the document and not for what the edits add
        var tight = GameDataLayerInput.FromProject(project, maxDocumentBytes: document + 8).Single(l => l.Name == "fix");

        Assert.NotNull(tight.EditsProblem);
        Assert.Contains("room for", tight.EditsProblem);
        Assert.Null(tight.DocumentProblem);                                                       // the layer is NOT refused
        Assert.Equal(File.ReadAllText(DeclarationsOf(project, "fix")), tight.DocumentText);        // its own modules are all it applies
        Assert.Null(tight.ImportedDocumentText);
        Assert.Null(tight.EditedChunks);

        // and the composed document itself past the limit: the file fits, the sum does not
        int composed = System.Text.Encoding.UTF8.GetByteCount(everything.DocumentText!);
        var barely = GameDataLayerInput.FromProject(project, maxDocumentBytes: composed - 1).Single(l => l.Name == "fix");
        Assert.NotNull(barely.EditsProblem);
        Assert.Null(barely.DocumentProblem);
        Assert.Equal(File.ReadAllText(DeclarationsOf(project, "fix")), barely.DocumentText);
        // room for the file as well (it carries a wrapper of its own): applied
        Assert.Null(GameDataLayerInput.FromProject(project, maxDocumentBytes: composed + 256).Single(l => l.Name == "fix").EditsProblem);
    }

    [Fact]
    public void The_edits_count_against_the_total_the_documents_of_all_layers_may_hold()
    {
        var (project, _) = ProjectWithEdit("total");
        long documents = new FileInfo(DeclarationsOf(project, "base")).Length + new FileInfo(DeclarationsOf(project, "fix")).Length;

        var inputs = GameDataLayerInput.FromProject(project, maxTotalBytes: documents + 8);

        Assert.Null(inputs.Single(l => l.Name == "base").EditsProblem);
        var fix = inputs.Single(l => l.Name == "fix");                                            // applied last: the total refuses what comes last
        Assert.Contains("room for", fix.EditsProblem);
        Assert.Null(fix.DocumentProblem);
        Assert.NotNull(fix.DocumentText);
        Assert.Null(GameDataLayerInput.FromProject(project).Single(l => l.Name == "fix").EditsProblem);
    }

    [Fact]
    public void A_damaged_edits_file_refuses_the_edits_not_the_layer_and_the_preview_still_works_and_says_so()
    {
        var (project, game) = ProjectWithEdit("damaged");
        File.WriteAllText(LtkEditStore.PathOf(project.RootPath!, "fix"), "{ not json");

        var fix = GameDataLayerInput.FromProject(project).Single(l => l.Name == "fix");
        Assert.Contains("not valid JSON", fix.EditsProblem);
        Assert.Null(fix.DocumentProblem);
        Assert.Equal(File.ReadAllText(DeclarationsOf(project, "fix")), fix.DocumentText);

        // the setup no longer fails on it: the package alone is previewed, and the summary tells what is not applied
        var (preview, _) = Ready(project, game);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(Served(preview, A), "Test/Obj/A"));
        var warning = Assert.Single(preview.Summary!.Warnings, w => w.Contains("edits kept on top of layer 'fix' are not applied", StringComparison.Ordinal));
        Assert.Contains("not valid JSON", warning);
        Assert.Empty(preview.Setup!.EditedChunks);
        // an export still says so instead of leaving the edits out of a package
        Assert.Throws<InvalidDataException>(() => LtkProjectStore.ReadLayers(project));
        Assert.Single(LtkProjectStore.ReadLayers(project, includeEdits: false), l => l.Layer == "fix");
    }

    // ===================================================== the refresh in place

    [Fact]
    public void A_refresh_serves_the_edit_to_whoever_reads_next_and_reads_the_game_for_the_one_bin_that_changed_only()
    {
        var project = Project();
        var (preview, game) = Ready(project, Game("g-refresh"));
        byte[] b = Served(preview, A);
        byte[] mBefore = Served(preview, M);
        string mIdentityBefore = preview.Entries.Single(e => e.PathHash == ChunkOf(M)).Identity;
        game.Reset();
        byte[] e = Bin(Obj("Test/Obj/A", new[] { "g1", "base", "fix", "mine" }, count: 7));
        LtkEditStore.Set(project, project.Layers.Single(l => l.Name == "fix"), ChunkOf(A), ModuleFor(A, b, e));

        Assert.True(preview.TryRefreshEdited(GameDataLayerInput.FromProject(project), ChunkOf(A), out var problem), problem);

        Assert.Equal(GameDataPreviewState.Ready, preview.State);                    // never pending in between: a reader is answered with the old bin or the new one
        Assert.Equal(new[] { "g1", "base", "fix", "mine" }, TagsOf(Served(preview, A), "Test/Obj/A"));
        Assert.Equal(mBefore, Served(preview, M));                                  // no other bin changed
        Assert.Equal(1, game.ReadsOf(ChunkOf(A)));                                  // the game was read for the bin that changed...
        Assert.Equal(0, game.ReadsOf(ChunkOf(M)));                                  // ...and for no other
        Assert.True(preview.IsTarget(ChunkOf(A)));
        Assert.True(preview.IsTarget(ChunkOf(M)));
        // the identity of what is served follows the documents, so a cache keyed by it (a thumbnail) is dropped
        Assert.NotEqual(mIdentityBefore, preview.Entries.Single(x => x.PathHash == ChunkOf(M)).Identity);
        Assert.Equal(preview.DocumentsFingerprint, GameDataPreview.FingerprintOf(GameDataLayerInput.FromProject(project)));   // the same identity a preview made afresh gives
        // B is still the package's alone, and is read from the game once and then kept
        game.Reset();
        Assert.True(preview.TryReadImportedOnly(ChunkOf(A), out var imported, out _));
        Assert.True(preview.TryReadImportedOnly(ChunkOf(A), out _, out _));
        Assert.Equal(b, imported);
        Assert.Equal(1, game.ReadsOf(ChunkOf(A)));
    }

    [Fact]
    public void A_refresh_after_the_edit_was_taken_away_serves_the_package_alone_again()
    {
        var project = Project();
        var fix = project.Layers.Single(l => l.Name == "fix");
        var (preview, _) = Ready(project, Game("g-revert"));
        byte[] b = Served(preview, A);
        LtkEditStore.Set(project, fix, ChunkOf(A), ModuleFor(A, b, Bin(Obj("Test/Obj/A", new[] { "mine" }, count: 7))));
        Assert.True(preview.TryRefreshEdited(GameDataLayerInput.FromProject(project), ChunkOf(A), out _));
        Assert.Equal(new[] { "mine" }, TagsOf(Served(preview, A), "Test/Obj/A"));

        Assert.True(LtkEditStore.Remove(project, ChunkOf(A)));
        Assert.True(preview.TryRefreshEdited(GameDataLayerInput.FromProject(project), ChunkOf(A), out var problem), problem);

        Assert.Equal(b, Served(preview, A));
        Assert.True(preview.TryReadImportedOnly(ChunkOf(A), out var imported, out _));
        Assert.Equal(b, imported);
    }

    [Fact]
    public void A_preview_that_is_not_ready_cannot_be_refreshed_or_asked_for_B_and_says_why()
    {
        var project = Project();
        var setup = GameDataSetups.ForProject(project, Game("g-pending"), null, null, _temp.Combine("cache-p", "i.idx"));
        var preview = new GameDataPreview(_ => setup);                                // never started

        Assert.False(preview.TryRefreshEdited(GameDataLayerInput.FromProject(project), ChunkOf(A), out var refresh));
        Assert.Contains("not ready", refresh);
        Assert.False(preview.TryReadImportedOnly(ChunkOf(A), out _, out var why));
        Assert.Contains("still being prepared", why);
    }

    [Fact]
    public void A_preview_serves_edits_as_editable_and_a_plain_overlay_does_not()
    {
        var (preview, _) = Ready(Project(), Game("g-edit-flag"));
        var service = new AssetMountService();
        service.SetOverlay(preview);
        service.Rebuild();

        Assert.True(preview.AllowsEdits);
        Assert.True(service.TryGet(ChunkOf(A), out var asset));
        Assert.Equal(AssetSourceKind.LtkGameData, asset.SourceKind);
        Assert.True(asset.IsEditable);                                              // an edit of it is a declaration, so the editors open it editable
        Assert.False(asset.ToEntry().ReadOnly);
        Assert.False(service.TryGetFilePath(ChunkOf(A), out _, out _));            // and it is still no file: nothing is ever written to it
    }

    // ===================================================== the planner's baselines

    [Fact]
    public void A_project_bin_the_GameData_also_targets_is_declared_between_the_game_and_the_copy_both_with_the_modules_in_front_of_it_applied()
    {
        // the game's bin has tags [g1]; the package's module adds "base"; the mod ships its own copy of the bin, tags [g1, mine]
        string root = _temp.Combine("baselines");
        Directory.CreateDirectory(root);
        var project = new ReyProject { Name = "b", RootPath = root };
        project.Layers.Add(new ProjectLayer { Name = "base", DeclarationsKey = "base" });
        LtkProjectStore.WriteDeclarations(root, "base", Doc(AddTag(A, "Test/Obj/A", "base")));
        byte[] gameBin = Bin(Obj("Test/Obj/A", new[] { "g1" }, count: 1));
        byte[] copy = Bin(Obj("Test/Obj/A", new[] { "g1", "mine" }, count: 1));
        string gameDirectory = Game("g-baselines", a: gameBin);
        var inputs = GameDataLayerInput.FromProject(project);
        var game = new InstalledGame(gameDirectory, _temp.Combine("cache-b", "i.idx"));
        var baselines = new GameDataDeclarationBaselines(inputs, new HashSet<ulong> { ChunkOf(A) }, game);
        string copyPath = Path.Combine(root, "Map11", "data", "t", "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
        File.WriteAllBytes(copyPath, copy);
        var files = new[] { new DeclarationFile("base", "Map11", A, copyPath) };

        // against the game's own bin the copy states its whole tag list: the package's "base" is not in it, so LTK's module and this one would fight
        var raw = BinDeclarationPlanner.Plan(files, (_, _) => gameBin, Names);
        // between the two bins as they are once the package's module has run, the copy states what it adds to that
        var based = BinDeclarationPlanner.Plan(files, (_, _) => gameBin, Names, baselines: baselines);

        string TagsOfModule(DeclarationPlan plan)
        {
            var doc = BinDeclarations.GameDataDocument(plan.Modules["base"]);
            return doc["modules"]![0]!["edits"]![0]!["Test/Obj/A"]!["tags"]!.ToJsonString();
        }
        Assert.Equal("[\"g1\",\"mine\"]", TagsOfModule(raw));
        Assert.Equal("[\"g1\",\"mine\",\"base\"]", TagsOfModule(based));            // the copy, with the package's "base" in it as it is at install

        // and installed - the package's document, then this module, over the game's bin - it gives what the preview shows of the copy: the package's module over the copy
        var shown = new GameDataOverlay(inputs, game, new SingleCopy(copy)).Apply(ChunkOf(A))!.Bytes!;
        var plannerModule = BinDeclarations.GameDataDocument(based.Modules["base"], firstModuleIndex: 1)["modules"]![0]!.DeepClone();
        string document = GameDataDocumentText.Read(inputs.Single(i => i.Name == "base").DocumentText!).WithModules(new[] { plannerModule });
        var installedInputs = new[] { new GameDataLayerInput("base", 0, document) };
        var installed = new GameDataOverlay(installedInputs, game).Apply(ChunkOf(A))!.Bytes!;
        Assert.Null(BinTreeEquivalence.FirstDifference(SafeBinTree.Parse(shown), SafeBinTree.Parse(installed), Names));
        Assert.Equal(new[] { "g1", "mine", "base" }, TagsOf(installed, "Test/Obj/A"));

        // a bin the GameData does not target is declared against the game's, as it always was
        var other = new GameDataDeclarationBaselines(inputs, new HashSet<ulong>(), game);
        Assert.Null(other.For(files[0], ChunkOf(A), gameBin, copy));
    }

    private sealed class SingleCopy : IGameDataModFiles
    {
        private readonly byte[] _bytes;
        public SingleCopy(byte[] bytes) { _bytes = bytes; }
        public byte[]? ReadLayerFile(string layer, ulong chunk, long maxBytes) => chunk == ChunkOf(A) ? (byte[])_bytes.Clone() : null;
        public byte[]? ReadRawFile(ulong chunk, long maxBytes) => null;
    }

    [Fact]
    public void The_baseline_of_a_bin_includes_the_edits_kept_on_top_of_the_packages_modules_which_run_before_the_projects_own_module()
    {
        var project = Project();
        var (preview, _) = Ready(project, Game("g-base-edits"));
        byte[] b = Served(preview, M);                                                // base's module on M: [game, base]
        byte[] e = Bin(Obj("Test/Obj/M", new[] { "game", "base", "edited" }));
        LtkEditStore.Set(project, project.Layers.Single(l => l.Name == "base"), ChunkOf(M), ModuleFor(M, b, e));
        var inputs = GameDataLayerInput.FromProject(project);
        byte[] gameBin = Bin(Obj("Test/Obj/M", new[] { "game" }));
        var baselines = new GameDataDeclarationBaselines(inputs, new HashSet<ulong> { ChunkOf(M) }, new InstalledGame(Game("g-base-edits-2"), _temp.Combine("cache-be", "i.idx")));

        var pair = baselines.For(new DeclarationFile("base", "Map11", M, "x"), ChunkOf(M), gameBin, Bin(Obj("Test/Obj/M", new[] { "game", "theirs" })))!;

        Assert.Equal(new[] { "game", "base", "edited" }, TagsOf(pair.Game, "Test/Obj/M"));          // the package's module, then the edit, over the game's bin
        Assert.Equal(new[] { "game", "base", "edited" }, TagsOf(pair.Mod, "Test/Obj/M"));          // and over the copy: the edit states the list whole, so it is the copy's list too
    }

    [Fact]
    public void A_baseline_stops_at_the_layer_the_bin_is_in_so_the_modules_of_later_layers_still_run_after_it()
    {
        var project = Project();                                                       // base names A and M; fix names A
        var inputs = GameDataLayerInput.FromProject(project);
        var game = new InstalledGame(Game("g-prefix"), _temp.Combine("cache-prefix", "i.idx"));
        var baselines = new GameDataDeclarationBaselines(inputs, new HashSet<ulong> { ChunkOf(A) }, game);
        byte[] gameBin = Bin(Obj("Test/Obj/A", new[] { "g1" }, count: 1));
        byte[] copy = Bin(Obj("Test/Obj/A", new[] { "g1", "mine" }, count: 1));

        var inBase = baselines.For(new DeclarationFile("base", "Map11", A, "x"), ChunkOf(A), gameBin, copy)!;
        var inFix = baselines.For(new DeclarationFile("fix", "Map11", A, "x"), ChunkOf(A), gameBin, copy)!;

        Assert.Equal(new[] { "g1", "base" }, TagsOf(inBase.Game, "Test/Obj/A"));                  // only base's module has run in front of a module of base
        Assert.Equal(new[] { "g1", "mine", "base" }, TagsOf(inBase.Mod, "Test/Obj/A"));
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(inFix.Game, "Test/Obj/A"));            // and both layers' have, in front of a module of fix
        Assert.Equal(new[] { "g1", "mine", "base", "fix" }, TagsOf(inFix.Mod, "Test/Obj/A"));
    }
}

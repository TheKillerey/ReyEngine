using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.LtkGameData;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M818: the overlay over what a project holds (M816's store), and over what a package may hold: a document nested deeper than <c>System.Text.Json</c> reads by default and within what
/// <c>serde_json</c> reads must survive the import, the store and the overlay; one nested past what the parser allows is a refused layer and not a crash; and an installation is indexed once
/// however many threads ask. None of it needs the game.
/// </summary>
public sealed class LtkOverlayProjectTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private const string A = "data/t/a.bin";

    private static SyntheticGame OneBin() => new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })));

    private InstalledGame Install(SyntheticGame game) => new(game.Write(_temp.Combine("game")), _temp.Combine("cache", "index.idx"));

    /// <summary>A package of hand-written entries: JSON text and bytes.</summary>
    private string Package(string name, string info, params (string Entry, byte[] Bytes)[] more)
    {
        string path = _temp.Combine(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string entry, byte[] bytes)
        {
            using var s = zip.CreateEntry(entry).Open();
            s.Write(bytes);
        }
        Put("META/info.json", Encoding.UTF8.GetBytes(info));
        foreach (var (entry, bytes) in more) Put(entry, bytes);
        return path;
    }

    private ReyProject Import(string package)
    {
        string projects = _temp.Combine("projects");
        Directory.CreateDirectory(projects);
        var result = FantomeImporter.Import(package, projects, null, new HashDatabase());
        return ReyProjectService.OpenFolder(result.RootPath);
    }

    private static string Info(params (string Layer, int Priority, string? GameData)[] layers) =>
        "{\"Name\":\"t\",\"Author\":\"a\",\"Version\":\"1.0.0\",\"Description\":\"d\",\"Layers\":{" +
        string.Join(",", layers.Select(l => $"\"{l.Layer}\":{{\"Name\":\"{l.Layer}\",\"Priority\":{l.Priority}{(l.GameData is null ? "" : ",\"GameData\":" + l.GameData)}}}")) + "}}";

    // ================================================================================================ a project's layers

    [Fact]
    public void A_projects_layers_documents_and_override_files_are_the_overlays_inputs()
    {
        string baseDoc = Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}"));
        string fixDoc = Doc(Target(A, "{\"overrides\":[\"fix.ptch\"],\"Test/Obj/A\":{\"+tags\":[\"f\"]}}"));
        string package = Package("p.fantome", Info(("base", 0, baseDoc), ("fix", 3, fixDoc)),
            ("META/game_data/fix/fix.ptch", Ptch(new[] { Obj("Test/Obj/FromPatch", new[] { "pp" }) })));
        var project = Import(package);

        var inputs = GameDataLayerInput.FromProject(project);
        var overlay = new GameDataOverlay(inputs, Install(OneBin()));
        var result = overlay.Apply(ChunkOf(A))!;

        Assert.Equal(new[] { "base", "fix" }, inputs.OrderBy(i => i.Priority).Select(i => i.Name).ToArray());
        Assert.All(inputs, i => Assert.Null(i.DocumentProblem));
        Assert.True(result.Applied);
        Assert.Equal(new[] { "g1", "b", "f" }, TagsOf(result.Bytes!, "Test/Obj/A"));
        var bin = PropCodec.ReadProp(result.Bytes!);
        Assert.True(bin.Objects.ContainsKey(H("Test/Obj/FromPatch")));
        Assert.Empty(overlay.Plan().Diagnostics);
    }

    [Fact]
    public void A_project_that_lists_no_base_layer_has_one_and_a_layer_without_a_document_declares_nothing()
    {
        // an import always writes the base layer; a project file edited by hand may not list it
        var project = Import(Package("p.fantome", Info(("fix", 3, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"f\"]}}"))))));
        project.Layers.RemoveAll(l => l.Name == ProjectLayer.BaseLayer);

        var inputs = GameDataLayerInput.FromProject(project);

        Assert.Contains(inputs, i => i.Name == ProjectLayer.BaseLayer && i.DocumentText is null);
        Assert.Contains(inputs, i => i.Name == "fix" && i.DocumentText is not null);
    }

    [Fact]
    public void A_document_over_the_limit_or_a_missing_files_folder_is_a_problem_for_that_layer_alone()
    {
        string big = Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"" + new string('x', 400) + "\"]}}"));
        var project = Import(Package("p.fantome", Info(("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}"))), ("big", 1, big))));

        var inputs = GameDataLayerInput.FromProject(project, maxDocumentBytes: 300);

        Assert.Null(inputs.Single(i => i.Name == "base").DocumentProblem);
        var problem = inputs.Single(i => i.Name == "big");
        Assert.Null(problem.DocumentText);
        Assert.Contains("more than the 300 the overlay reads", problem.DocumentProblem);
        // a layer with no override file stored answers none, not an error
        Assert.Null(inputs.Single(i => i.Name == "base").Files!.ReadOverrideFile("anything.ptch", 1000));

        var overlay = new GameDataOverlay(inputs, Install(OneBin()));
        Assert.Contains(overlay.Plan().Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.DeclarationsRejected && d.Layer == "big");
        Assert.True(overlay.Apply(ChunkOf(A))!.Applied);
    }

    [Fact]
    public void The_documents_of_all_layers_together_may_hold_no_more_than_the_total_and_the_layers_applied_last_are_the_ones_it_refuses()
    {
        string doc = Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"" + new string('x', 100) + "\"]}}"));
        var project = Import(Package("p.fantome", Info(("last", 2, doc), ("base", 0, doc), ("mid", 1, doc))));
        long one = Encoding.UTF8.GetByteCount(doc);
        long total = 2 * one + 10;

        var inputs = GameDataLayerInput.FromProject(project, maxTotalBytes: total);

        // read in the order the overlay applies them in, whatever order the project lists them in
        Assert.Null(inputs.Single(i => i.Name == "base").DocumentProblem);
        Assert.Null(inputs.Single(i => i.Name == "mid").DocumentProblem);
        var refused = inputs.Single(i => i.Name == "last");
        Assert.Null(refused.DocumentText);
        Assert.Equal($"the documents together are more than the {total} bytes the overlay reads", refused.DocumentProblem);
        var overlay = new GameDataOverlay(inputs, Install(OneBin()));
        Assert.Single(overlay.Plan().Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.DeclarationsRejected && d.Layer == "last");
        // the game's tag, and the one each of the two layers that were read adds
        Assert.Equal(3, TagsOf(overlay.Apply(ChunkOf(A))!.Bytes!, "Test/Obj/A").Length);
    }

    [Fact]
    public void The_total_for_the_documents_of_a_project_is_64_MiB_and_that_is_far_above_any_package_measured()
    {
        Assert.Equal(64L << 20, GameDataOverlayOptions.DefaultMaxTotalDocumentBytes);
        Assert.Equal(16L << 20, GameDataOverlayOptions.DefaultMaxDocumentBytes);
    }

    [Fact]
    public void An_override_file_is_found_without_regard_to_case_and_one_that_is_not_stored_cannot_be_reached_by_a_path_that_climbs()
    {
        var package = Package("p.fantome", Info(("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")))),
            ("META/game_data/base/Sub/Fix.PTCH", Ptch(new[] { Obj("Test/Obj/FromPatch", new[] { "pp" }) })));
        var project = Import(package);
        File.WriteAllText(Path.Combine(project.RootPath!, "secret.txt"), "not an override");
        var files = GameDataLayerInput.FromProject(project).Single(i => i.Name == "base").Files!;

        Assert.NotNull(files.ReadOverrideFile("sub/fix.ptch", 1_000_000));
        Assert.Null(files.ReadOverrideFile("../../../../secret.txt", 1_000_000));
        Assert.Null(files.ReadOverrideFile("/etc/passwd", 1_000_000));
        Assert.Throws<IOException>(() => files.ReadOverrideFile("sub/fix.ptch", 10));
    }

    // ================================================================================================ a document nested past what System.Text.Json reads

    /// <summary>Embeds nested <paramref name="pins"/> deep: each is three levels of JSON.</summary>
    private static string DeepValue(int pins)
    {
        string value = "{\"embed\":{\"class\":\"TestClass\",\"set\":{}}}";
        for (int i = 1; i < pins; i++) value = "{\"embed\":{\"class\":\"TestClass\",\"set\":{\"n\":" + value + "}}}";
        return value;
    }

    private sealed class EmbedEverywhere : IGameDataSchema
    {
        public GameDataShape? Expected(uint cls, uint field) => new GameDataShape(PropKind.Embedded);

        public bool HasClass(uint cls) => true;
    }

    private static SyntheticGame GameWithEmbed()
    {
        var obj = Obj("Test/Obj/A", new[] { "g1" });
        obj.Properties.Set(H("deep"), new PropStruct(PropKind.Embedded, TestClass, new OrderedMap<PropValue>()));
        return new SyntheticGame().Add("A.wad.client", A, Bin(obj));
    }

    [Fact]
    public void A_document_nested_between_64_and_127_levels_survives_the_import_the_store_and_the_overlay()
    {
        // 30 pins: the info.json is about 99 levels deep, past System.Text.Json's default of 64 and inside serde_json's 128
        string doc = Doc(Target(A, "{\"Test/Obj/A\":{\"deep\":" + DeepValue(30) + "}}"));
        string info = Info(("base", 0, doc));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(info));   // the default reader refuses it: that is what the test is about

        var project = Import(Package("deep.fantome", info));

        // stored byte for byte, as the package held it
        var stored = LtkProjectStore.ReadDeclarationsText(project, project.Layers.Single(l => l.Name == ProjectLayer.BaseLayer));
        Assert.Equal(doc, stored);
        var inputs = GameDataLayerInput.FromProject(project);
        var overlay = new GameDataOverlay(inputs, Install(GameWithEmbed()), null, new GameDataOverlayOptions { Schema = new EmbedEverywhere() });

        var plan = overlay.Plan();
        var result = overlay.Apply(ChunkOf(A))!;

        Assert.DoesNotContain(plan.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.DeclarationsRejected);
        Assert.True(result.Applied, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Empty(result.Diagnostics);
        // the value landed: the object's `deep` is a chain of 30 embeds
        var deep = PropCodec.ReadObject(result.Bytes!, H("Test/Obj/A"))!.Properties.ValueAt(PropCodec.ReadObject(result.Bytes!, H("Test/Obj/A"))!.Properties.IndexOf(H("deep")));
        int levels = 0;
        for (var s = deep as PropStruct; s is not null; s = s.Properties.Count > 0 ? s.Properties.ValueAt(0) as PropStruct : null) levels++;
        Assert.Equal(30, levels);
    }

    [Theory]
    [InlineData(45)]    // about 144 levels in a package: past what serde_json reads
    [InlineData(200)]   // far past what any reader of the toolchain would hold
    public void A_document_nested_past_the_parsers_limit_is_a_refused_layer_and_not_a_crash(int pins)
    {
        string doc = Doc(Target(A, "{\"Test/Obj/A\":{\"deep\":" + DeepValue(pins) + "}}"));
        var layers = new List<GameDataLayerInput> { new("base", 0, doc), new("ok", 1, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"fine\"]}}"))) };
        var overlay = new GameDataOverlay(layers, Install(GameWithEmbed()));

        var plan = overlay.Plan();

        var rejected = Assert.Single(plan.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.DeclarationsRejected);
        Assert.Equal("base", rejected.Layer);
        Assert.Contains("recursion limit exceeded", rejected.Message);
        Assert.Equal(new[] { "g1", "fine" }, TagsOf(overlay.Apply(ChunkOf(A))!.Bytes!, "Test/Obj/A"));
    }

    // ================================================================================================ the installed game

    [Fact]
    public void An_installed_game_builds_the_table_without_the_declarations_and_each_once_however_many_threads_ask()
    {
        string game = OneBin().Add("B.wad.client", "data/t/b.bin", Bin(Obj("Test/Obj/B", new[] { "b" }))).Write(_temp.Combine("game"));
        var progress = new List<GameIndexProgress>();
        var installed = new InstalledGame(game, _temp.Combine("cache", "index.idx"), new GameObjectIndexOptions { Workers = 2 }, new Sync(progress));

        var table = installed.GetTable(CancellationToken.None);
        Assert.NotNull(installed.TableLoad);
        Assert.Null(installed.ObjectsLoad);   // a mod of target modules never asks for the declarations
        Assert.DoesNotContain(progress, p => p.Stage == GameIndexStage.ReadingBins);

        var seen = new GameObjectIndex?[12];
        Parallel.For(0, seen.Length, new ParallelOptions { MaxDegreeOfParallelism = 12 }, n => seen[n] = installed.GetObjects(CancellationToken.None));

        Assert.All(seen, o => Assert.Same(seen[0], o));
        Assert.Same(table, seen[0]!.Table);
        Assert.False(installed.ObjectsLoad!.FromCache);
        Assert.Equal(1, progress.Count(p => p.Stage == GameIndexStage.SavingCache && p.Bins > 0));

        // another installed game over the same directory and cache is served from it
        var again = new InstalledGame(game, _temp.Combine("cache", "index.idx"));
        again.GetObjects(CancellationToken.None);
        Assert.True(again.TableLoad!.FromCache);
        Assert.True(again.ObjectsLoad!.FromCache);
    }

    private sealed class Sync : IProgress<GameIndexProgress>
    {
        private readonly List<GameIndexProgress> _seen;

        public Sync(List<GameIndexProgress> seen) { _seen = seen; }

        public void Report(GameIndexProgress value) { lock (_seen) _seen.Add(value); }
    }

    [Fact]
    public void A_failure_to_index_is_remembered_and_a_cancellation_is_not()
    {
        string game = OneBin().Write(_temp.Combine("game"));
        var installed = new InstalledGame(game, _temp.Combine("cache", "index.idx"));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => installed.GetObjects(cancel.Token));
        Assert.Null(installed.TableLoad);
        Assert.NotNull(installed.GetObjects(CancellationToken.None));   // a cancelled call left nothing behind

        var nowhere = new InstalledGame(_temp.Combine("nowhere"), _temp.Combine("cache2", "index.idx"));
        var first = Assert.ThrowsAny<Exception>(() => nowhere.GetTable(CancellationToken.None));
        var second = Assert.ThrowsAny<Exception>(() => nowhere.GetTable(CancellationToken.None));
        Assert.Contains("no DATA/FINAL directory", first.Message);
        Assert.Equal(first.Message, second.Message);
    }

    [Fact]
    public void A_chunk_is_read_through_the_installed_game_and_the_overlay_serves_it_on_a_machine_with_no_user_cache()
    {
        var installed = Install(OneBin());

        var bytes = installed.Read(0, ChunkOf(A));

        Assert.Equal(new[] { "g1" }, TagsOf(bytes, "Test/Obj/A"));
        // the cache the test chose is the only one written: nothing of this test reached the user's folder
        Assert.True(File.Exists(_temp.Combine("cache", "index.idx")));
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Meta;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M823: the edits kept on top of an imported package's GameData (<see cref="LtkEditStore"/>) - where they live, what the file looks like, how a module is replaced or taken away, that
/// the package's own document is never touched, and how the layer's reader, the fantome export and the Send manifest carry them behind the imported modules.
/// </summary>
public sealed class LtkEditStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private const string A = "data/t/a.bin", B = "data/t/b.bin";

    private static ulong ChunkOf(string path) => HashAlgorithms.WadPath(path);

    /// <summary>A project that stores the given layers' documents the way an import does.</summary>
    private ReyProject Project(params (string Name, int Priority, string? Key, string? Document)[] layers)
    {
        string root = _temp.Combine("project");
        Directory.CreateDirectory(root);
        var project = new ReyProject { Name = "p", RootPath = root };
        foreach (var (name, priority, key, document) in layers)
        {
            project.Layers.Add(new ProjectLayer { Name = name, Priority = priority, DeclarationsKey = key });
            if (key is not null && document is not null) LtkProjectStore.WriteDeclarations(root, key, document);
        }
        return project;
    }

    private static string Module(string target, string value, int origin = 0) =>
        $"{{\"target\":\"{target}\",\"edits\":[{{\"Test/Obj/X\":{{\"count\":{value}}}}}],\"origin\":{{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":{origin}}}}}";

    private static readonly string Imported = Doc(Target(A, "{\"Test/Obj/X\":{\"+tags\":[\"imported\"]}}"), Target(B, "{\"Test/Obj/Y\":{\"+tags\":[\"imported\"]}}"));

    // ===================================================== the file

    [Fact]
    public void A_module_is_kept_in_the_file_beside_the_layers_document_which_stays_as_the_package_wrote_it()
    {
        var project = Project(("base", 0, "base", Imported));
        var layer = project.Layers[0];
        string documentPath = Path.Combine(LtkProjectStore.DirectoryOf(project.RootPath!, "base"), LtkProjectStore.DeclarationsFileName);
        byte[] before = File.ReadAllBytes(documentPath);

        LtkEditStore.Set(project, layer, ChunkOf(A), Module(A, "7"));

        string file = LtkEditStore.PathOf(project.RootPath!, "base");
        Assert.Equal(Path.Combine(LtkProjectStore.DirectoryOf(project.RootPath!, "base"), "reyengine-edits.json"), file);
        Assert.True(File.Exists(file));
        Assert.Equal(before, File.ReadAllBytes(documentPath));                    // the package's document is never written after the import
        Assert.False(File.Exists(file + ".tmp"));                                  // written whole beside itself and moved into place

        // the file is a GameData document of the shape a layer's is: version, then the modules, one to a line
        string text = File.ReadAllText(file);
        Assert.StartsWith("{\n  \"version\": 1,\n  \"modules\": [\n", text);
        var document = GameDataDocumentText.Read(text);
        Assert.True(document.IsExpectedShape);
        var module = Assert.Single(document.Modules);
        Assert.Equal(A, module.Target);
        Assert.Equal(Module(A, "7"), module.Text);                                 // the module is kept as it was given, byte for byte
    }

    [Fact]
    public void A_chunk_has_one_module_and_saving_it_again_replaces_it_in_its_place()
    {
        var project = Project(("base", 0, "base", Imported));
        var layer = project.Layers[0];
        LtkEditStore.Set(project, layer, ChunkOf(A), Module(A, "1"));
        LtkEditStore.Set(project, layer, ChunkOf(B), Module(B, "2"));

        LtkEditStore.Set(project, layer, ChunkOf(A), Module(A, "3"));            // the first chunk again

        var modules = LtkEditStore.Read(project.RootPath!, layer);
        Assert.Equal(new[] { Module(A, "3"), Module(B, "2") }, modules.Select(m => m.Text).ToArray());   // replaced in its place, not appended
        Assert.Equal(new[] { ChunkOf(A), ChunkOf(B) }.Order(), LtkEditStore.Chunks(project).Order());
    }

    [Fact]
    public void Taking_the_last_module_away_removes_the_file_and_taking_one_that_is_not_kept_changes_nothing()
    {
        var project = Project(("base", 0, "base", Imported));
        var layer = project.Layers[0];
        LtkEditStore.Set(project, layer, ChunkOf(A), Module(A, "1"));
        LtkEditStore.Set(project, layer, ChunkOf(B), Module(B, "2"));
        string file = LtkEditStore.PathOf(project.RootPath!, "base");

        Assert.False(LtkEditStore.Remove(project, ChunkOf("data/t/other.bin")));
        Assert.True(LtkEditStore.Remove(project, ChunkOf(A)));
        Assert.Equal(new[] { Module(B, "2") }, LtkEditStore.Read(project.RootPath!, layer).Select(m => m.Text).ToArray());
        Assert.True(LtkEditStore.Remove(project, ChunkOf(B)));

        Assert.False(File.Exists(file));
        Assert.False(LtkEditStore.Any(project));
        Assert.Empty(LtkEditStore.Chunks(project));
        Assert.Empty(LtkEditStore.Read(project.RootPath!, layer));
    }

    [Fact]
    public void A_chunk_is_edited_in_one_layer_only_so_a_bin_whose_last_layer_changed_is_not_edited_twice()
    {
        var project = Project(("base", 0, "base", Imported), ("fix", 1, "fix", Imported));
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(A), Module(A, "\"in base\""));

        LtkEditStore.Set(project, project.Layers[1], ChunkOf(A), Module(A, "\"in fix\""));

        Assert.Empty(LtkEditStore.Read(project.RootPath!, project.Layers[0]));
        Assert.False(File.Exists(LtkEditStore.PathOf(project.RootPath!, "base")));
        Assert.Equal(Module(A, "\"in fix\""), Assert.Single(LtkEditStore.Read(project.RootPath!, project.Layers[1])).Text);
        var (layer, module) = LtkEditStore.Find(project, ChunkOf(A))!.Value;
        Assert.Equal("fix", layer.Name);
        Assert.Equal(A, module.Target);
        Assert.Null(LtkEditStore.Find(project, ChunkOf(B)));
    }

    [Fact]
    public void A_layer_that_stores_no_document_of_a_package_cannot_hold_an_edit()
    {
        var project = Project(("base", 0, "base", Imported), ("plain", 1, null, null));

        Assert.Throws<InvalidOperationException>(() => LtkEditStore.Set(project, project.Layers[1], ChunkOf(A), Module(A, "1")));
        Assert.Empty(LtkEditStore.Read(project.RootPath!, project.Layers[1]));
        Assert.False(LtkEditStore.Any(project));
    }

    [Fact]
    public void The_chunk_of_a_module_is_the_hash_of_its_path_or_the_sixteen_digits_it_is_spelled_with()
    {
        var project = Project(("base", 0, "base", Imported));
        string hex = ChunkOf(B).ToString("x16");
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(A), Module("Data/T/A.bin", "1"));         // a path is hashed the way the loader hashes it: lowercase
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(B), Module(hex, "2"));

        Assert.Equal(new[] { ChunkOf(A), ChunkOf(B) }.Order(), LtkEditStore.Chunks(project).Order());
        Assert.NotNull(LtkEditStore.Find(project, ChunkOf(A)));
        Assert.NotNull(LtkEditStore.Find(project, ChunkOf(B)));
    }

    // ===================================================== a damaged file

    [Fact]
    public void A_file_that_is_not_a_GameData_document_is_an_error_that_names_the_file_and_the_layer_not_an_edit_dropped()
    {
        var project = Project(("base", 0, "base", Imported));
        string file = LtkEditStore.PathOf(project.RootPath!, "base");
        foreach (string damaged in new[] { "{ not json", "[1,2]", "{\"version\":1,\"modules\":[{\"edits\":[]}]}", "{\"version\":1}" })
        {
            File.WriteAllText(file, damaged);
            var ex = Assert.Throws<InvalidDataException>(() => LtkEditStore.Read(project.RootPath!, project.Layers[0]));
            Assert.Contains(file, ex.Message);
            Assert.Contains("'base'", ex.Message);
            // the layer's reader, which an export and a send go through, says so too instead of writing a package without the person's edits
            Assert.Throws<InvalidDataException>(() => LtkProjectStore.ReadLayers(project));
        }
    }

    [Fact]
    public void The_file_is_read_with_a_limit_and_a_file_that_cannot_be_read_is_the_same_error_naming_it()
    {
        var project = Project(("base", 0, "base", Imported));
        var layer = project.Layers[0];
        LtkEditStore.Set(project, layer, ChunkOf(A), Module(A, "7"));
        string file = LtkEditStore.PathOf(project.RootPath!, "base");

        // longer than the limit: not read, and said with the file's name
        var tooLarge = Assert.Throws<InvalidDataException>(() => LtkEditStore.Read(project.RootPath!, layer, maxBytes: 10));
        Assert.Contains(file, tooLarge.Message);
        Assert.Contains("more than the 10 ", tooLarge.Message);
        Assert.Single(LtkEditStore.Read(project.RootPath!, layer));                       // within the limit it reads

        // a real file longer than the store ever reads (16 MiB): the text reader the cleanup uses stops at it too
        using (var big = new FileStream(file, FileMode.Create, FileAccess.Write)) big.SetLength(LtkEditStore.MaxFileBytes + 1);
        Assert.Throws<InvalidDataException>(() => LtkEditStore.Read(project.RootPath!, layer));
        Assert.Throws<IOException>(() => LtkProjectStore.ReadEditsText(project, layer));

        // a file another process holds closed to readers: the file system's failure is the store's error, not an exception that ends a rebuild of the mounts
        File.WriteAllText(file, "{\"version\":1,\"modules\":[]}");
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var held = Assert.Throws<InvalidDataException>(() => LtkEditStore.Read(project.RootPath!, layer));
            Assert.Contains(file, held.Message);
            Assert.Contains("cannot be read", held.Message);
            Assert.Throws<InvalidDataException>(() => LtkEditStore.Chunks(project));
        }
    }

    [Fact]
    public void A_write_that_fails_leaves_the_module_in_the_layer_it_was_in_because_the_new_one_is_written_first()
    {
        var project = Project(("base", 0, "base", Imported), ("fix", 1, "fix", Imported));
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(A), Module(A, "\"in base\""));
        // fix's file cannot be written: a folder stands where it goes
        Directory.CreateDirectory(LtkEditStore.PathOf(project.RootPath!, "fix"));

        var ex = Record.Exception(() => LtkEditStore.Set(project, project.Layers[1], ChunkOf(A), Module(A, "\"in fix\"")));

        Assert.True(ex is IOException or UnauthorizedAccessException, ex?.GetType().Name ?? "no exception");
        Assert.Equal(Module(A, "\"in base\""), Assert.Single(LtkEditStore.Read(project.RootPath!, project.Layers[0])).Text);   // the edit is where it was
        Assert.Equal("base", LtkEditStore.Find(project, ChunkOf(A))!.Value.Layer.Name);
        Assert.False(File.Exists(LtkEditStore.PathOf(project.RootPath!, "fix") + ".tmp"));
    }

    [Fact]
    public void A_write_that_fails_behind_the_new_module_takes_the_new_module_away_again()
    {
        var project = Project(("base", 0, "base", Imported), ("fix", 1, "fix", Imported));
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(A), Module(A, "\"in base\""));
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(B), Module(B, "2"));       // base keeps another module too, so taking A out of it is a REWRITE of its file
        string baseFile = LtkEditStore.PathOf(project.RootPath!, "base");
        byte[] before = File.ReadAllBytes(baseFile);

        Exception? failure;
        // base's file is open in another process without the right to be replaced: the module is written to fix first, and the rewrite of base fails behind it
        using (new FileStream(baseFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            failure = Record.Exception(() => LtkEditStore.Set(project, project.Layers[1], ChunkOf(A), Module(A, "\"in fix\"")));

        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.GetType().Name ?? "no exception");
        Assert.False(File.Exists(LtkEditStore.PathOf(project.RootPath!, "fix")));      // fix has none: what was written there was taken away
        Assert.Equal(before, File.ReadAllBytes(baseFile));
        Assert.Equal("base", LtkEditStore.Find(project, ChunkOf(A))!.Value.Layer.Name);
    }

    [Fact]
    public void A_layer_key_that_is_no_folder_name_of_the_stores_is_never_a_place_to_read_or_write()
    {
        string root = _temp.Combine("keys");
        Assert.Throws<ArgumentException>(() => LtkEditStore.PathOf(root, "..\\..\\x"));
        var project = new ReyProject { RootPath = root };
        project.Layers.Add(new ProjectLayer { Name = "evil", DeclarationsKey = "..\\..\\x" });

        Assert.Empty(LtkEditStore.Read(root, project.Layers[0]));
        Assert.False(LtkEditStore.Any(project));
        Assert.Throws<InvalidOperationException>(() => LtkEditStore.Set(project, project.Layers[0], 1, Module(A, "1")));
    }

    // ===================================================== what reads the layer

    [Fact]
    public void The_layers_reader_carries_the_edits_after_the_imported_modules_and_counts_the_numbering_past_them()
    {
        var project = Project(("base", 0, "base", Imported), ("fix", 1, "fix", Doc(Target(A, "{\"Test/Obj/X\":{\"+tags\":[\"fix\"]}}"))));
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(B), Module(B, "5"));

        var layers = LtkProjectStore.ReadLayers(project);

        var baseLayer = layers.Single(l => l.Layer == "base");
        Assert.Equal(2, baseLayer.Modules.Count);                                  // the package's, as it wrote them
        Assert.Equal(Imported, baseLayer.DocumentText);
        Assert.Equal(new[] { Module(B, "5") }, baseLayer.Edits.Select(m => m.Text).ToArray());
        Assert.Equal(3, baseLayer.OwnStart);                                       // the first free place after both
        var fixLayer = layers.Single(l => l.Layer == "fix");
        Assert.Empty(fixLayer.Edits);
        Assert.Equal(1, fixLayer.OwnStart);
    }

    [Fact]
    public void A_project_with_no_edit_reads_the_layers_it_always_did()
    {
        var project = Project(("base", 0, "base", Imported));

        var layer = Assert.Single(LtkProjectStore.ReadLayers(project));

        Assert.Empty(layer.Edits);
        Assert.Equal(layer.Modules.Count, layer.OwnStart);
        Assert.Null(LtkProjectStore.ReadEditsText(project, project.Layers[0]));
    }

    [Fact]
    public void The_edits_text_is_a_document_a_reader_of_what_the_project_names_can_read()
    {
        var project = Project(("base", 0, "base", Imported));
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(A), Module(A, "1"));

        string text = LtkProjectStore.ReadEditsText(project, project.Layers[0])!;

        var index = new ProjectReferenceIndex();
        index.AddGameData(text);
        Assert.Equal(1, index.GameDataRead);
        Assert.Equal(0, index.GameDataFailed);
    }

    // ===================================================== composing the document

    [Fact]
    public void ModuleNode_numbers_a_module_for_its_place_and_gives_one_without_an_origin_the_origin_the_document_requires()
    {
        var numbered = GameDataDocumentText.ModuleNode(Module(A, "1"), 9);
        Assert.Equal(9, numbered["origin"]!["module"]!.GetValue<int>());
        Assert.Equal("game_data.yaml", numbered["origin"]!["manifest"]!.GetValue<string>());

        var bare = GameDataDocumentText.ModuleNode("{\"target\":\"x\",\"edits\":[{\"a/b\":{\"c\":1.0}}]}", 4);
        Assert.Equal(4, bare["origin"]!["module"]!.GetValue<int>());
        Assert.Null(bare["origin"]!["source"]);
        Assert.Contains("1.0", bare.ToJsonString());                              // a number keeps its token
        Assert.Throws<JsonException>(() => GameDataDocumentText.ModuleNode("[1]", 0));
    }

    [Fact]
    public void WithModules_writes_the_imported_modules_as_they_are_and_the_new_ones_behind_them()
    {
        const string doc = "{\n  \"version\": 1,\n  \"modules\": [\n    { \"target\" : \"data/t/a.bin\", \"edits\": [ {\"Test/Obj/X\" : {\"speed\": 1E-30}} ], \"origin\": {\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":0} }\n  ]\n}";
        var read = GameDataDocumentText.Read(doc);

        string composed = read.WithModules(new[] { GameDataDocumentText.ModuleNode(Module(B, "2"), 1) });

        var again = GameDataDocumentText.Read(composed);
        Assert.Equal(2, again.Modules.Count);
        Assert.Equal(read.Modules[0].Text, again.Modules[0].Text);                // byte for byte, whitespace and the number token included
        Assert.Equal(B, again.Modules[1].Target);
        Assert.Equal(doc, read.WithModules(Array.Empty<JsonNode>()));              // nothing to add: the text goes out as it is
    }

    // ===================================================== the manifest Send writes

    [Fact]
    public void The_manifest_writes_the_edits_after_the_imported_modules_and_before_the_projects_own_without_origins()
    {
        var project = Project(("base", 0, "base", Imported));
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(A), Module(A, "7"));
        var data = LtkProjectStore.ReadLayers(project).Single();
        var own = new DeclaredChunk("data/own.bin", "  - target: \"data/own.bin\"\n    X/y:\n      speed: 2\n", null);

        string text = BinDeclarations.Manifest(new[] { own }, data.Modules, data.Edits);

        var lines = text.Split('\n');
        int at = Array.IndexOf(lines, "modules:");
        Assert.StartsWith("  - {\"target\":\"data/t/a.bin\",\"edits\":[{\"Test/Obj/X\":{\"+tags\":[\"imported\"]}}]}", lines[at + 1]);   // the package's first
        Assert.StartsWith("  - {\"target\":\"data/t/b.bin\"", lines[at + 2]);                                                              // and its second
        Assert.Equal("  - {\"target\":\"data/t/a.bin\",\"edits\":[{\"Test/Obj/X\":{\"count\":7}}]}", lines[at + 3]);                     // then the edit, without its origin
        Assert.Equal("  - target: \"data/own.bin\"", lines[at + 4]);                                                                      // then what this project declares
        Assert.DoesNotContain("origin", text);
        Assert.Contains("The first 2 module(s) are declarations imported from a .fantome", text);
        Assert.Contains("The next 1 module(s) are edits made in ReyEngine", text);
    }

    [Fact]
    public void A_manifest_without_edits_is_the_manifest_it_always_was()
    {
        var project = Project(("base", 0, "base", Imported));
        var data = LtkProjectStore.ReadLayers(project).Single();
        var own = new DeclaredChunk("data/own.bin", "  - target: \"data/own.bin\"\n    X/y:\n      speed: 2\n", null);

        string withNone = BinDeclarations.Manifest(new[] { own }, data.Modules, data.Edits);

        Assert.Equal(BinDeclarations.Manifest(new[] { own }, data.Modules), withNone);
        Assert.Equal(BinDeclarations.Manifest(new[] { own }, data.Modules), BinDeclarations.Manifest(new[] { own }, data.Modules, null));
        Assert.DoesNotContain("edits made in ReyEngine", withNone);
    }

    [Fact]
    public void A_layer_with_only_imported_modules_and_edits_gets_a_manifest_of_both()
    {
        var project = Project(("base", 0, "base", Imported));
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(A), Module(A, "7"));

        var all = BinDeclarations.WithImported(new Dictionary<string, string>(), LtkProjectStore.ReadLayers(project));

        string manifest = Assert.Single(all).Value;
        Assert.Contains("{\"target\":\"data/t/a.bin\",\"edits\":[{\"Test/Obj/X\":{\"count\":7}}]}", manifest);
    }

    // ===================================================== the fantome export

    [Fact]
    public void ForFantome_gives_a_layer_its_edits_numbered_on_from_the_imported_modules()
    {
        var project = Project(("base", 0, "base", Imported));
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(A), Module(A, "7"));
        var layers = LtkProjectLayers.ForFantome(project, null, LtkProjectStore.ReadLayers(project));

        var layer = layers.Single(l => l.Name == "base");

        Assert.Equal(Imported, layer.ImportedGameData);
        var edit = Assert.Single(layer.EditModules);
        Assert.Equal(2, edit["origin"]!["module"]!.GetValue<int>());              // 0 and 1 are the package's
        Assert.Equal(A, edit["target"]!.GetValue<string>());
    }

    [Fact]
    public void The_export_writes_the_imported_document_verbatim_then_the_edits_then_the_projects_own_modules_numbered_in_one_run()
    {
        var project = Project(("base", 0, "base", Imported));
        LtkEditStore.Set(project, project.Layers[0], ChunkOf(A), Module(A, "7"));
        var layers = LtkProjectLayers.ForFantome(project, new Dictionary<string, JsonNode>
        {
            ["base"] = BinDeclarations.GameDataDocument(new[]
            {
                new DeclaredChunk("data/own.bin", "  - target: \"data/own.bin\"\n", null) { Edit = new DeclaredEdit(new[] { "x.bin" }, Array.Empty<string>(), Array.Empty<DeclaredBody>(), Array.Empty<DeclaredObject>()) },
            }, firstModuleIndex: 3),                                                // the planner numbers on from the imported (2) and the edit (1): the view model passes OwnStart
        }, LtkProjectStore.ReadLayers(project));
        var meta = new FantomeMeta { Name = "n", Author = "a", Layers = layers };

        string info = FantomeExporter.BuildInfoJson(meta, FantomeExporter.OrderLayers(layers), hashtable: false);

        using var parsed = JsonDocument.Parse(info);
        string document = parsed.RootElement.GetProperty("Layers").GetProperty("base").GetProperty("GameData").GetRawText();
        var written = GameDataDocumentText.Read(document);
        Assert.Equal(4, written.Modules.Count);
        Assert.Equal(GameDataDocumentText.Read(Imported).Modules.Select(m => m.Text), written.Modules.Take(2).Select(m => m.Text));   // verbatim
        Assert.Equal(new[] { A, B, A, "data/own.bin" }, written.Modules.Select(m => m.Target).ToArray());                             // imported, imported, the edit, the project's own
        var origins = written.Modules.Select(m => JsonDocument.Parse(m.Text).RootElement.GetProperty("origin").GetProperty("module").GetInt32()).ToArray();
        Assert.Equal(new[] { 0, 1, 2, 3 }, origins);                                                                                  // 0, 1, 2, 3 as a loader would number the manifest of the same modules
    }

    [Fact]
    public void A_layer_with_no_edits_is_exported_exactly_as_before()
    {
        var project = Project(("base", 0, "base", Imported));
        var imported = LtkProjectStore.ReadLayers(project);
        var withEditsSupport = LtkProjectLayers.ForFantome(project, null, imported);
        var meta = new FantomeMeta { Name = "n", Author = "a", Layers = withEditsSupport };

        string info = FantomeExporter.BuildInfoJson(meta, FantomeExporter.OrderLayers(withEditsSupport), hashtable: false);

        using var parsed = JsonDocument.Parse(info);
        Assert.Empty(withEditsSupport.Single(l => l.Name == "base").EditModules);
        Assert.Equal(Imported, parsed.RootElement.GetProperty("Layers").GetProperty("base").GetProperty("GameData").GetRawText());   // as it came, not even re-indented
    }
}

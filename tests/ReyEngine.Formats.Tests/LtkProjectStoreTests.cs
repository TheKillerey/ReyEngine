using System.Text;
using System.Text.Json.Nodes;
using ReyEngine.Core.Build;
using ReyEngine.Core.Cleanup;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816: the part of a project that came from an LTK-layered .fantome and belongs to no WAD folder - GameData documents, override
/// files, hashtables, the package's text - kept as files under <c>.reyengine/ltk</c> (<see cref="LtkProjectStore"/>), and the
/// read API M817 and M818 build on.
///
/// <para>What matters here is what the location has to survive: the project being saved and loaded, packing (which must not put a
/// GameData document into a WAD), the mounts (which must not show it as a game asset), and Cleanup Project (which must never list
/// it as unused).</para>
/// </summary>
public sealed class LtkProjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m816-store-" + Guid.NewGuid().ToString("N"));

    public LtkProjectStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly UTF8Encoding Utf8 = new(false);

    private const string Doc = "{\"version\":1,\"modules\":[{\"target\":\"data/a.bin\",\"edits\":[{\"A/b\":{\"speed\":1.0}}]},{\"name\":\"n\",\"entries\":{\"A/c\":{\"x\":2}}}]}";

    // ===================================================== keys

    [Theory]
    [InlineData("base", "base")]
    [InlineData("snowdown-baron", "snowdown-baron")]
    [InlineData("Fix_2", "Fix_2")]
    [InlineData("my layer", "my_layer")]
    [InlineData("a/b", "a_b")]
    [InlineData("..", "__")]
    [InlineData("caf\u00e9", "caf_")]
    [InlineData("", "layer")]
    [InlineData("nul", "nul_")]
    [InlineData("COM3", "COM3_")]
    [InlineData("com", "com")]
    public void AKeyIsAFolderNameMadeFromTheLayerName(string layer, string key) =>
        Assert.Equal(key, LtkProjectStore.KeyFor(layer, new HashSet<string>()));

    [Fact]
    public void AKeyIsUniqueAmongTheOnesTakenWithoutRegardToCase()
    {
        var taken = new HashSet<string>();
        Assert.Equal("my_layer", LtkProjectStore.KeyFor("my layer", taken));
        Assert.Equal("my_layer-2", LtkProjectStore.KeyFor("my_layer", taken));                        // the same key again
        Assert.Equal("MY_LAYER-3", LtkProjectStore.KeyFor("MY_LAYER", taken));
        Assert.Equal(3, taken.Count);
    }

    // ===================================================== documents and files

    [Fact]
    public void ADocumentIsStoredByteForByteAndReadBackWithItsModules()
    {
        string text = "\uFEFF{ \"version\" : 1 ,\r\n\t\"modules\" : [ ] }\r\n";           // not even valid for LTK; a copy must not "improve" it
        LtkProjectStore.WriteDeclarations(_root, "base", text);
        Assert.Equal(Utf8.GetBytes(text), File.ReadAllBytes(Path.Combine(_root, ".reyengine", "ltk", "game_data", "base", "declarations.json")));

        var project = new ReyProject { RootPath = _root };
        project.Layers.Add(new ProjectLayer { Name = "base", DeclarationsKey = "base" });
        // the BOM is not JSON: reading it is the caller's to refuse, never to hide - the stored bytes are what they were
        var ex = Assert.Throws<InvalidDataException>(() => LtkProjectStore.ReadLayer(project, "base"));
        Assert.Contains("layer 'base'", ex.Message);
    }

    [Fact]
    public void ModulesAreFoundWithTheirNamesAndTargets()
    {
        LtkProjectStore.WriteDeclarations(_root, "fix", Doc);
        var project = new ReyProject { RootPath = _root };
        project.Layers.Add(new ProjectLayer { Name = "fix", Priority = 3, DeclarationsKey = "fix" });

        var data = LtkProjectStore.ReadLayer(project, "FIX")!;
        Assert.Equal("fix", data.Layer);
        Assert.Equal(Doc, data.DocumentText);
        Assert.Equal(2, data.Modules.Count);
        Assert.Equal(new string?[] { "data/a.bin", null }, data.Modules.Select(m => m.Target));
        Assert.Equal(new string?[] { null, "n" }, data.Modules.Select(m => m.Name));
        Assert.Equal("{\"target\":\"data/a.bin\",\"edits\":[{\"A/b\":{\"speed\":1.0}}]}", data.Modules[0].Text);
        Assert.True(data.Modules[0].IsTarget);
        Assert.False(data.Modules[1].IsTarget);
        Assert.NotNull(data.ParseDocument());
    }

    [Fact]
    public void OverrideFilesKeepTheirLayerRelativePathsAndBytes()
    {
        LtkProjectStore.WriteDeclarations(_root, "fix", Doc);
        var bytes = new byte[] { 0, 1, 2, 255, 13, 10 };
        Assert.True(LtkProjectStore.WriteOverrideFile(_root, "fix", "data/maps/x.ptch", bytes));
        Assert.True(LtkProjectStore.WriteOverrideFile(_root, "fix", "y.ptch", new byte[] { 7 }));
        var project = new ReyProject { RootPath = _root };
        project.Layers.Add(new ProjectLayer { Name = "fix", DeclarationsKey = "fix" });

        var data = LtkProjectStore.ReadLayer(project, "fix")!;

        Assert.Equal(new[] { "data/maps/x.ptch", "y.ptch" }, data.Files.Select(f => f.Path));
        Assert.Equal(new long[] { 6, 1 }, data.Files.Select(f => f.Length));
        Assert.Equal(bytes, data.ReadFile("data/maps/x.ptch"));
        Assert.Equal(bytes, data.ReadFile("DATA\\MAPS\\X.PTCH"));
        Assert.Null(data.ReadFile("missing.ptch"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/abs.ptch")]
    [InlineData("a//b.ptch")]
    [InlineData("a/./b.ptch")]
    [InlineData("../b.ptch")]
    [InlineData("a/../../b.ptch")]
    [InlineData("C:/b.ptch")]
    [InlineData("C:b.ptch")]
    [InlineData("a\\b.ptch")]
    [InlineData("a/b?.ptch")]
    [InlineData("a/b*.ptch")]
    public void APathThatCouldLeaveTheLayerIsRefusedAndNotWritten(string path)
    {
        Assert.NotNull(LtkProjectStore.PathProblem(path));
        Assert.False(LtkProjectStore.WriteOverrideFile(_root, "fix", path, new byte[] { 1 }));
        Assert.False(Directory.Exists(Path.Combine(_root, ".reyengine", "ltk", "game_data", "fix", "files")) &&
            Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".reyengine", "ltk", "game_data", "fix", "files")).Any());
        Assert.False(File.Exists(Path.Combine(_root, "b.ptch")));
    }

    [Theory]
    [InlineData("x.ptch")]
    [InlineData("data/maps/x.ptch")]
    [InlineData("a b/c d.ptch")]
    [InlineData("data/con_/x.ptch")]
    [InlineData("data/console.ptch")]
    [InlineData("data/a.b.c.ptch")]
    public void OrdinaryPathsAreFine(string path) => Assert.Null(LtkProjectStore.PathProblem(path));

    // review, round 3: a segment Windows cannot make as it is named threw when the file was written, after the whole WAD had been unpacked
    [Theory]
    [InlineData("data/trailing./x.ptch", "ends with a dot or a space")]
    [InlineData("data/trailing /x.ptch", "ends with a dot or a space")]
    [InlineData("data/.../x.ptch", "ends with a dot or a space")]
    [InlineData("data/x.ptch.", "ends with a dot or a space")]
    [InlineData("data/nul/x.ptch", "Windows device name")]
    [InlineData("data/CON/x.ptch", "Windows device name")]
    [InlineData("data/com1.txt", "Windows device name")]
    [InlineData("lpt9.x/y.ptch", "Windows device name")]
    [InlineData("aux .txt", "Windows device name")]
    public void ASegmentWindowsCannotMakeAsItIsNamedIsRefusedAndNotWritten(string path, string why)
    {
        var problem = LtkProjectStore.PathProblem(path);

        Assert.StartsWith("the path has a segment that ", problem);
        Assert.Contains(why, problem);
        Assert.False(LtkProjectStore.WriteOverrideFile(_root, "fix", path, new byte[] { 1 }));
        Assert.False(Directory.Exists(Path.Combine(_root, ".reyengine")));                               // nothing was made on the way
    }

    [Fact]
    public void ASegmentOfMoreThan255CharactersIsRefusedAndOneOf255IsNot()
    {
        Assert.Null(LtkProjectStore.PathProblem(new string('x', 255) + "/y.ptch"));
        Assert.Contains("longer than 255 characters", LtkProjectStore.PathProblem(new string('x', 256) + "/y.ptch"));
        Assert.Contains("longer than 255 characters", LtkProjectStore.PathProblem("data/" + new string('y', 300)));
    }

    // review, round 3: the hashtables a package declares are kept under the store's own names
    [Theory]
    [InlineData("META/hashes/con.txt")]
    [InlineData("META/hashes/nul")]
    [InlineData("META/hashes/names.")]
    [InlineData("META/hashes/names ")]
    [InlineData("META/hashes/COM3.hashes.txt")]
    public void ATableWhoseNameWindowsCannotMakeIsKeptUnderASafeNameWithItsSourceInTheManifest(string source)
    {
        var table = (new FantomeInfoHashtable(source, "game", "xxh64", 64), System.Text.Encoding.UTF8.GetBytes("assets/a.tex\n"));

        var problems = LtkProjectStore.WriteHashtables(_root, new[] { table });

        Assert.Empty(problems);
        var stored = Assert.Single(LtkProjectStore.ReadHashtables(_root));
        Assert.Equal(source, stored.Source);                                                              // where the package had it
        Assert.Null(FantomeLayers.SegmentProblem(stored.File["hashes/".Length..], 100));                  // and where the project keeps it
        Assert.Equal(new[] { "assets/a.tex" }, LtkProjectStore.ReadTableNames(_root, stored));
    }

    [Fact]
    public void AnOverlongTableNameIsKeptUnderASafeNameToo()
    {
        var table = (new FantomeInfoHashtable("META/hashes/" + new string('t', 300) + ".txt", "game", "xxh64", 64), System.Text.Encoding.UTF8.GetBytes("assets/a.tex\n"));

        Assert.Empty(LtkProjectStore.WriteHashtables(_root, new[] { table }));

        var stored = Assert.Single(LtkProjectStore.ReadHashtables(_root));
        Assert.True(stored.File.Length < 120);
        Assert.Equal(1, LtkProjectStore.LoadHashtables(_root, new HashDatabase()));
    }

    [Fact]
    public void ATableTheFileSystemRefusesIsAProblemNotAnExceptionAndTheOthersAreKept()
    {
        // a folder where the first table's file must go: the write fails, and says so - it does not end the import
        Directory.CreateDirectory(Path.Combine(LtkProjectStore.RootOf(_root), "hashes", "table.txt"));
        var first = (new FantomeInfoHashtable("META/hashes/con", "game", "xxh64", 64), System.Text.Encoding.UTF8.GetBytes("assets/a.tex\n"));
        var second = (new FantomeInfoHashtable("META/hashes/fine.txt", "game", "xxh64", 64), System.Text.Encoding.UTF8.GetBytes("assets/b.tex\n"));

        var problems = LtkProjectStore.WriteHashtables(_root, new[] { first, second });

        var problem = Assert.Single(problems);
        Assert.StartsWith("The hashtable META/hashes/con could not be kept (", problem);
        Assert.EndsWith("so its names are not used when the project opens.", problem);
        var stored = Assert.Single(LtkProjectStore.ReadHashtables(_root));
        Assert.Equal("META/hashes/fine.txt", stored.Source);
    }

    [Fact]
    public void NothingToStoreIsNoProblem()
    {
        Assert.Empty(LtkProjectStore.WriteHashtables(_root, Array.Empty<(FantomeInfoHashtable, byte[])>()));
        Assert.False(Directory.Exists(Path.Combine(_root, ".reyengine")));
    }

    [Fact]
    public void LayersAreReadInTheOrderALoaderAppliesThem()
    {
        var project = new ReyProject { RootPath = _root };
        foreach (var (name, priority) in new[] { ("layer10", 2), ("zeta", 1), ("base", 0), ("layer9", 2), ("alpha", 1) })
        {
            LtkProjectStore.WriteDeclarations(_root, name, Doc);
            project.Layers.Add(new ProjectLayer { Name = name, Priority = priority, DeclarationsKey = name });
        }
        Assert.Equal(new[] { "base", "alpha", "zeta", "layer9", "layer10" }, LtkProjectStore.ReadLayers(project).Select(d => d.Layer));
    }

    [Fact]
    public void ALayerWithoutADocumentOrWithAKeyThatIsNotAFolderIsNotRead()
    {
        LtkProjectStore.WriteDeclarations(_root, "ok", Doc);
        var project = new ReyProject { RootPath = _root };
        project.Layers.Add(new ProjectLayer { Name = "ok", DeclarationsKey = "ok" });
        project.Layers.Add(new ProjectLayer { Name = "none" });                                             // imported nothing
        project.Layers.Add(new ProjectLayer { Name = "gone", DeclarationsKey = "gone" });                   // its folder was deleted
        project.Layers.Add(new ProjectLayer { Name = "up", DeclarationsKey = "../../escape" });             // project.json is a file a person can edit
        project.Layers.Add(new ProjectLayer { Name = "abs", DeclarationsKey = "C:\\Windows" });

        Assert.Equal(new[] { "ok" }, LtkProjectStore.ReadLayers(project).Select(d => d.Layer));
        Assert.Null(LtkProjectStore.ReadLayer(project, "missing"));
        Assert.Null(LtkProjectStore.ReadLayer(new ReyProject(), "ok"));                                      // a project with no folder reads nothing
    }

    [Fact]
    public void RenamingALayerKeepsItsDeclarationsBecauseTheKeyIsNotTheName()
    {
        LtkProjectStore.WriteDeclarations(_root, "snowdown", Doc);
        var project = new ReyProject { RootPath = _root };
        var layer = new ProjectLayer { Name = "snowdown", Priority = 1, DeclarationsKey = "snowdown" };
        project.Layers.Add(layer);

        layer.Name = "winter";                                                                              // Project Settings renames it

        var data = LtkProjectStore.ReadLayer(project, "winter")!;
        Assert.Equal("winter", data.Layer);
        Assert.Equal("snowdown", data.Key);
        Assert.Equal(Doc, data.DocumentText);
    }

    // ===================================================== hashtables

    [Fact]
    public void HashtablesAreKeptWithWhatTheyDeclareAndTeachTheDatabaseOnlyWhatItCanUse()
    {
        var game = new FantomeInfoHashtable("META/hashes/game.harvested.hashes.txt", "game", "xxh64", 64);
        var bins = new FantomeInfoHashtable("META/hashes/bin.hashes.txt", "binentries", "fnv1a_32", 32);
        var narrow = new FantomeInfoHashtable("META/hashes/narrow.txt", "game", "xxh64", 32);                // truncated keys: not a path hash
        var unknown = new FantomeInfoHashtable("META/hashes/other.txt", "mystery", "blake3", 64);
        LtkProjectStore.WriteHashtables(_root, new (FantomeInfoHashtable, byte[])[]
        {
            (game, Utf8.GetBytes("assets/a.tex\r\nData/Maps/B.bin\n\nbad\\name\n")),
            (bins, Utf8.GetBytes("Maps/Foo/Bar\n")),
            (narrow, Utf8.GetBytes("assets/narrow.tex\n")),
            (unknown, Utf8.GetBytes("assets/unknown.tex\n")),
        });

        var tables = LtkProjectStore.ReadHashtables(_root);
        Assert.Equal(new[] { true, true, false, false }, tables.Select(t => t.IsUsable));
        Assert.Equal(new[] { "META/hashes/game.harvested.hashes.txt", "META/hashes/bin.hashes.txt", "META/hashes/narrow.txt", "META/hashes/other.txt" }, tables.Select(t => t.Source));
        Assert.Equal(new[] { "assets/a.tex", "Data/Maps/B.bin" }, LtkProjectStore.ReadTableNames(_root, tables[0]));   // CRLF tolerated, a bad line left out

        var db = new HashDatabase();
        Assert.Equal(3, LtkProjectStore.LoadHashtables(_root, db));
        Assert.True(db.TryGetPath(HashAlgorithms.WadPath("assets/a.tex"), out var a)); Assert.Equal("assets/a.tex", a);
        Assert.True(db.TryGetPath(HashAlgorithms.WadPath("data/maps/b.bin"), out var b)); Assert.Equal("Data/Maps/B.bin", b);   // the author's spelling
        Assert.True(db.TryGetBinName(HashAlgorithms.Fnv1a("Maps/Foo/Bar"), out var bin)); Assert.Equal("Maps/Foo/Bar", bin);
        Assert.False(db.TryGetPath(HashAlgorithms.WadPath("assets/narrow.tex"), out _));
        Assert.False(db.TryGetPath(HashAlgorithms.WadPath("assets/unknown.tex"), out _));
    }

    [Fact]
    public void TheTableFilesAreTheBytesThePackageHeld()
    {
        var table = new FantomeInfoHashtable("META/hashes/game.harvested.hashes.txt", "game", "xxh64", 64);
        var bytes = Utf8.GetBytes("a\nb\n");
        LtkProjectStore.WriteHashtables(_root, new (FantomeInfoHashtable, byte[])[] { (table, bytes) });
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_root, ".reyengine", "ltk", "hashes", "game.harvested.hashes.txt")));
    }

    [Fact]
    public void TwoTablesOfOneFileNameAreBothKept()
    {
        LtkProjectStore.WriteHashtables(_root, new (FantomeInfoHashtable, byte[])[]
        {
            (new FantomeInfoHashtable("META/hashes/a/names.txt", "game", "xxh64", 64), Utf8.GetBytes("one\n")),
            (new FantomeInfoHashtable("META/hashes/b/names.txt", "game", "xxh64", 64), Utf8.GetBytes("two\n")),
        });
        var db = new HashDatabase();
        Assert.Equal(2, LtkProjectStore.LoadHashtables(_root, db));
        Assert.Equal(2, db.WadCount);
    }

    [Fact]
    public void ABrokenManifestOrAMissingTableIsNoNamesNotAProjectThatWillNotOpen()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".reyengine", "ltk"));
        File.WriteAllText(Path.Combine(_root, ".reyengine", "ltk", "hashtables.json"), "{ not json");
        Assert.Empty(LtkProjectStore.ReadHashtables(_root));
        Assert.Equal(0, LtkProjectStore.LoadHashtables(_root, new HashDatabase()));

        File.WriteAllText(Path.Combine(_root, ".reyengine", "ltk", "hashtables.json"),
            "[{\"File\":\"hashes/gone.txt\",\"Category\":\"game\",\"Algorithm\":\"xxh64\",\"Bits\":64},{\"File\":\"../../escape.txt\",\"Category\":\"game\",\"Algorithm\":\"xxh64\",\"Bits\":64}]");
        Assert.Equal(0, LtkProjectStore.LoadHashtables(_root, new HashDatabase()));
        Assert.Equal(0, LtkProjectStore.LoadHashtables(Path.Combine(_root, "nowhere"), new HashDatabase()));
    }

    // ===================================================== what it has to survive

    [Fact]
    public void TheLayersSurviveSavingAndLoadingTheProjectFile()
    {
        var p = new ReyProject { Name = "P", RootPath = _root };
        p.Layers.Add(new ProjectLayer
        {
            Name = "words",
            Priority = 4,
            DisplayName = "Words",
            DeclarationsKey = "words",
            Folders = { "layers/words/Map11" },
            StringOverrides = JsonNode.Parse("{\"en_us\":{\"b\":\"2\",\"a\":\"1\"},\"default\":{\"z\":\"9\"}}")!.AsObject(),
        });
        p.ModLicense = new ProjectLicense { Name = "MIT", AsObject = true };
        p.ModTags.AddRange(new[] { "map-skin", "sfx" });
        p.ModChampions.Add("Ahri");
        p.ModMaps.Add("summoners-rift");
        p.ImportedGenerator = "ltk_mod_project 0.16.2";

        string file = Path.Combine(_root, ".reyengine", "project.json");
        ReyProjectService.Save(p, file);
        var q = ReyProjectService.OpenFolder(_root);

        var layer = Assert.Single(q.Layers);
        Assert.Equal(("words", 4, "Words", "words"), (layer.Name, layer.Priority, layer.DisplayName, layer.DeclarationsKey));
        Assert.Equal(new[] { "layers/words/Map11" }, layer.Folders);
        Assert.Equal(new[] { "en_us", "default" }, layer.StringOverrides!.Select(x => x.Key));
        Assert.Equal(new[] { "b", "a" }, layer.StringOverrides["en_us"]!.AsObject().Select(x => x.Key));
        Assert.Equal("MIT", q.ModLicense!.Name);
        Assert.True(q.ModLicense.AsObject);
        Assert.Equal(new[] { "map-skin", "sfx" }, q.ModTags);
        Assert.Equal(new[] { "Ahri" }, q.ModChampions);
        Assert.Equal(new[] { "summoners-rift" }, q.ModMaps);
        Assert.Equal("ltk_mod_project 0.16.2", q.ImportedGenerator);
    }

    [Fact]
    public void AProjectFileWrittenBeforeM816StillOpensAndHasNothingOfIt()
    {
        string file = Path.Combine(_root, ".reyengine", "project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"Name\":\"Old\",\"ProjectFolders\":[\"Map11\"],\"Layers\":[{\"Name\":\"fix\",\"Priority\":10,\"Description\":\"d\",\"Folders\":[\"Ahri\"]}]}");

        var p = ReyProjectService.OpenFolder(_root);

        var layer = Assert.Single(p.Layers);
        Assert.Equal(("fix", 10, "d"), (layer.Name, layer.Priority, layer.Description));
        Assert.Null(layer.DisplayName);
        Assert.Null(layer.StringOverrides);
        Assert.Null(layer.DeclarationsKey);
        Assert.Null(p.ModLicense);
        Assert.Empty(p.ModTags);
        Assert.Equal("fix", p.LayerOfFolder("mods/Ahri"));
        Assert.Empty(LtkProjectStore.ReadLayers(p));
    }

    [Fact]
    public void TheStoreIsNotPackedIntoAWadNorMountedAsGameContent()
    {
        // a project whose own folder is the WAD folder: the store sits inside it, and must stay out of everything built from it
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllBytes(Path.Combine(_root, "assets", "x.dds"), new byte[] { 1 });
        LtkProjectStore.WriteDeclarations(_root, "base", Doc);
        LtkProjectStore.WriteOverrideFile(_root, "base", "data/x.ptch", new byte[] { 1, 2 });
        LtkProjectStore.WriteMetaFile(_root, "README.md", Utf8.GetBytes("hi"));

        var packed = WadPackService.EnumerateChunkFiles(_root).Select(f => Path.GetRelativePath(_root, f.path).Replace('\\', '/')).ToList();
        Assert.Equal(new[] { "assets/x.dds" }, packed);

        using var mount = new ReyEngine.Core.Assets.FolderMount(_root, null);
        Assert.Equal(1, mount.Enumerate().Count());
    }

    [Fact]
    public void CleanupProjectNeverListsTheStore()
    {
        string folder = Path.Combine(_root, "Map11");
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        File.WriteAllBytes(Path.Combine(folder, "assets", "x.dds"), new byte[] { 1 });
        LtkProjectStore.WriteDeclarations(_root, "base", Doc);
        LtkProjectStore.WriteOverrideFile(_root, "base", "data/x.ptch", new byte[] { 1, 2 });
        LtkProjectStore.WriteHashtables(_root, new (FantomeInfoHashtable, byte[])[]
            { (new FantomeInfoHashtable("META/hashes/t.txt", "game", "xxh64", 64), Utf8.GetBytes("assets/x.dds\n")) });

        // the scanner walks the WAD folders; with the project root itself offered as one, the store is still skipped
        foreach (var root in new[] { folder, _root })
        {
            var report = CleanupScanner.Scan(new CleanupScanOptions
            {
                ProjectRoot = _root,
                Folders = new[] { ("project", root) },
                References = new EmptyReferences(),
                ScanUnused = true,
                ScanRiotIdentical = false,
            });
            Assert.DoesNotContain(report.Candidates, c => c.AbsPath.Contains(".reyengine", StringComparison.OrdinalIgnoreCase));
        }
        Assert.True(File.Exists(Path.Combine(_root, ".reyengine", "ltk", "game_data", "base", "declarations.json")));
    }

    private sealed class EmptyReferences : IReferenceIndex
    {
        public bool IsReferenced(string relPath, ulong hash, out string by) { by = ""; return false; }
    }
}

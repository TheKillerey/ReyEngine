using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Build;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using static ReyEngine.Formats.Tests.LayeredFantomeSupport;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816 review (HIGH): a layer name is a package's text, and it ends up a folder name. Send to LTK Manager deletes
/// <c>&lt;mod&gt;/content/&lt;layer&gt;/</c> recursively and writes below it, and <c>Path.Combine</c> takes <c>..\..</c> as the grandparent and a
/// rooted <c>C:\Temp\x</c> as the whole path. These pin both ends of the fix: an import never puts such a name in the project (the layer
/// is kept under a safe name and the Notes say so), and every writer refuses one anyway and proves the folder it writes in lies below
/// <c>&lt;mod&gt;/content</c> before it deletes, creates or writes anything.
///
/// <para>The rooted paths the writers are tried with point into this test's own temporary folder, so a regression would damage a
/// folder that is going to be deleted anyway and not <c>C:\Temp</c>; the UNC name is only ever resolved, never opened.</para>
///
/// <para>Round 3: a send refuses only a name that is unsafe as a PATH (<see cref="FantomeLayers.PathProblem"/>) - "Particle Fix", which the M744
/// dialog allowed and a send has always written to <c>content/Particle Fix/</c>, still sends - and the strict rule of the .fantome layout
/// (<see cref="FantomeLayers.NameProblem"/>) stays with the export and the import's renaming.</para>
/// </summary>
public sealed class LayerNameSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m816-names-" + Guid.NewGuid().ToString("N"));

    private string Projects => Path.Combine(_root, "projects");
    private string Scratch => Path.Combine(_root, "scratch");
    private string Workshop => Path.Combine(_root, "workshop");

    public LayerNameSafetyTests()
    {
        Directory.CreateDirectory(Projects);
        Directory.CreateDirectory(Workshop);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ===================================================== the name rule and the folder it names

    [Theory]
    [InlineData("..\\..", "layer")]
    [InlineData("..", "layer")]
    [InlineData("C:\\Temp\\x", "C_Temp_x")]
    [InlineData("\\\\server\\share", "server_share")]
    [InlineData("a/b", "a_b")]
    [InlineData("my layer", "my_layer")]
    [InlineData("  spaced   out  ", "spaced_out")]
    [InlineData("caf\u00e9", "caf")]
    [InlineData("con", "con_")]
    [InlineData("NUL", "NUL_")]
    [InlineData("com1", "com1_")]
    [InlineData("base ", "base_")]
    [InlineData("", "layer")]
    [InlineData("a--b__c", "a--b__c")]
    public void ASafeNameIsWhatIsLeftOfTheNameAndIsAlwaysUsable(string original, string expected)
    {
        string safe = FantomeLayers.SafeName(original, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(expected, safe);
        Assert.Null(FantomeLayers.NameProblem(safe));
    }

    [Fact]
    public void ASafeNameIsNeverOneAnotherLayerAlreadyHoldsAndTheSameInputGivesTheSameNames()
    {
        string[] hostile = { "..\\..", "..", "...", "a/b", "A_B", "a b" };
        List<string> Run()
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base", "layer-3" };
            return hostile.Select(h => FantomeLayers.SafeName(h, taken)).ToList();
        }

        var first = Run();

        Assert.Equal(first, Run());                                                         // deterministic
        Assert.Equal(first.Count, first.Select(n => n.ToLowerInvariant()).Distinct().Count());   // unique, without regard to case
        Assert.Equal(new[] { "layer", "layer-2", "layer-4", "a_b", "A_B-2", "a_b-3" }, first);   // and numbered in the order they came ("a_b-2" is "A_B-2" already)
        Assert.DoesNotContain("base", first);
        Assert.All(first, n => Assert.Null(FantomeLayers.NameProblem(n)));
    }

    [Fact]
    public void ASafeNameIsCappedSoItCanBeAFolder()
    {
        string safe = FantomeLayers.SafeName(new string('x', 5000), new HashSet<string>());
        Assert.Equal(64, safe.Length);
        Assert.Null(FantomeLayers.NameProblem(safe));
    }

    // The names a destination folder cannot take - the rule of a PATH (FantomeLayers.PathProblem), looser than the .fantome layout's
    // (NameProblem): a layer called "Particle Fix" is a perfectly good folder, and LTK Manager reads it from content/Particle Fix/.
    public static IEnumerable<object[]> UnsafeFolderNames => new[]
    {
        "..\\..", "..", ".", "...", "C:\\Temp\\x", "\\\\server\\share", "/etc", "a/b", "a\\b", "x:y",
        "con", "CON", "com3", "COM0", "lpt9", "nul", "aux", "prn", "con.txt", "NUL.tar.gz", "com1.x", "aux .txt",
        "", "  ", "x ", "x.", "trailing. ", "a*b", "a?b", "a<b", "a>b", "a|b", "a\"b", "tab\there", "line\nbreak", "bell\a",
        "BASE", "Base", "bAsE",
        new string('x', 101),
    }.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(UnsafeFolderNames))]
    public void ALayerDirectoryIsNeverResolvedForAnUnsafeName(string layer)
    {
        string content = Path.Combine(Workshop, "mod", "content");

        Assert.NotNull(FantomeLayers.PathProblem(layer));
        Assert.Throws<InvalidOperationException>(() => FantomeLayers.LayerDirectory(content, layer));
        Assert.False(Directory.Exists(content));                                             // resolving a folder creates nothing
        Assert.False(Directory.Exists(Path.Combine(Workshop, "mod")));
    }

    public static IEnumerable<object[]> SafeFolderNames => new[]
    {
        "winter", "Fix_2", "base", "Particle Fix", "layer 2", "caf\u00e9", "a.b", "x y z", "con_", "console", "com10", "aux1", "lpt", "Sk\u00edn", "\U0001F4A5",
        " leading", "UPPER", "mixed-Case_1", new string('x', 100),
    }.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(SafeFolderNames))]
    public void ALayerDirectoryIsOneFolderStrictlyBelowTheRoot(string layer)
    {
        string content = Path.Combine(Workshop, "mod", "content");

        Assert.Null(FantomeLayers.PathProblem(layer));
        string dir = FantomeLayers.LayerDirectory(content, layer);

        Assert.Equal(Path.GetFullPath(Path.Combine(content, layer)), dir);
        Assert.True(FantomeLayers.IsStrictlyBelow(dir, content));
        Assert.False(Directory.Exists(content));                                             // and nothing was created
    }

    [Theory]
    [InlineData("Particle Fix")]
    [InlineData("caf\u00e9")]
    [InlineData("layer 2")]
    public void APathSafeNameThatTheFantomeLayoutRefusesIsAFolderToASendOnly(string name)
    {
        Assert.Null(FantomeLayers.PathProblem(name));                                        // a send and a build can use it
        Assert.NotNull(FantomeLayers.NameProblem(name));                                     // an export cannot
        var project = new ReyProject { Name = "P", RootPath = Path.Combine(_root, "p") };
        project.Layers.Add(new ProjectLayer { Name = name, Priority = 3 });

        Assert.Equal(new[] { "base", name }, LtkProjectLayers.Of(project).Select(l => l.Name));              // the send's table has it ...
        var export = Assert.Throws<InvalidOperationException>(() => LtkProjectLayers.ForFantome(project));  // ... the export's refuses it, as it always did
        Assert.Contains("Project Settings", export.Message);
    }

    [Fact]
    public void PathProblemSaysWhichRuleAnUnsafeNameBroke()
    {
        Assert.Contains("a character a file name cannot hold", FantomeLayers.PathProblem("a/b"));
        Assert.Contains("a character a file name cannot hold", FantomeLayers.PathProblem("x:y"));
        Assert.Contains("a character a file name cannot hold", FantomeLayers.PathProblem("tab\there"));
        Assert.Contains("a dot name", FantomeLayers.PathProblem(".."));
        Assert.Contains("ends with a dot or a space", FantomeLayers.PathProblem("x."));
        Assert.Contains("ends with a dot or a space", FantomeLayers.PathProblem("x "));
        Assert.Contains("Windows device name", FantomeLayers.PathProblem("con.txt"));
        Assert.Contains("longer than 100 characters", FantomeLayers.PathProblem(new string('x', 101)));
        Assert.Contains("another casing of the base layer", FantomeLayers.PathProblem("BASE"));
        Assert.Contains("needs a name", FantomeLayers.PathProblem(""));
        Assert.Null(FantomeLayers.PathProblem("base"));                                      // exactly base is the base layer's folder
        Assert.True(FantomeLayers.PathProblem(new string('x', 5000))!.Length < 200, "the message does not repeat a long name");
    }

    [Fact]
    public void AControlCharacterInANameIsNotPutInAMessage()
    {
        string message = FantomeLayers.PathProblem("a\nb\tc\u0007")!;
        Assert.DoesNotContain('\n', message);
        Assert.DoesNotContain('\t', message);
        Assert.DoesNotContain('\u0007', message);
    }

    [Theory]
    [InlineData("C:\\a\\content\\x", "C:\\a\\content", true)]
    [InlineData("C:\\a\\content\\x\\y", "C:\\a\\content\\", true)]
    [InlineData("C:\\a\\CONTENT\\x", "C:\\a\\content", true)]                                // the file system ignores case
    [InlineData("C:\\a\\content", "C:\\a\\content", false)]                                  // the root is not below itself
    [InlineData("C:\\a\\content\\", "C:\\a\\content", false)]
    [InlineData("C:\\a\\content-evil\\x", "C:\\a\\content", false)]                          // a sibling that shares a prefix
    [InlineData("C:\\a\\content\\..\\x", "C:\\a\\content", false)]
    [InlineData("C:\\a", "C:\\a\\content", false)]
    [InlineData("D:\\a\\content\\x", "C:\\a\\content", false)]
    public void StrictlyBelowComparesResolvedPaths(string path, string root, bool expected) =>
        Assert.Equal(expected, FantomeLayers.IsStrictlyBelow(path, root));

    [Fact]
    public void ALayerTableIsRefusedForAnyLayerNameThatIsNotAFolder()
    {
        foreach (string name in new[] { "..\\..", "..", "C:\\Temp\\x", "\\\\server\\share", "con", "a/b", "BASE", "Base" })
        {
            var project = new ReyProject { Name = "P", RootPath = Path.Combine(_root, "p") };
            project.Layers.Add(new ProjectLayer { Name = name, Priority = 1 });

            var send = Assert.Throws<InvalidOperationException>(() => LtkProjectLayers.Of(project));
            var export = Assert.Throws<InvalidOperationException>(() => LtkProjectLayers.ForFantome(project));

            Assert.Contains("Project Settings", send.Message);                              // with a way out
            Assert.Contains("Project Settings", export.Message);
        }

        // exactly "base" is the base layer a project may re-declare, and an unnamed layer is left out, as they always were
        var fine = new ReyProject { Name = "P" };
        fine.Layers.Add(new ProjectLayer { Name = "base", Priority = 0 });
        fine.Layers.Add(new ProjectLayer { Name = "  ", Priority = 1 });
        fine.Layers.Add(new ProjectLayer { Name = "winter", Priority = 2 });
        Assert.Equal(new[] { "base", "winter" }, LtkProjectLayers.Of(fine).Select(l => l.Name));
    }

    // ===================================================== the store key

    [Theory]
    [InlineData("..\\..")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:\\x")]
    [InlineData("con")]
    [InlineData("")]
    [InlineData("caf\u00e9")]
    public void AStoreKeyThatIsNotAFolderNameKeyForCouldHaveMadeIsRefused(string key)
    {
        Assert.False(LtkProjectStore.IsSafeKey(key));
        Assert.Throws<ArgumentException>(() => LtkProjectStore.DirectoryOf(Path.Combine(_root, "p"), key));
        Assert.Throws<ArgumentException>(() => LtkProjectStore.WriteDeclarations(Path.Combine(_root, "p"), key, "{}"));
        Assert.False(Directory.Exists(Path.Combine(_root, "p")));                            // and nothing was created on the way
    }

    [Fact]
    public void EveryKeyKeyForMakesIsASafeKeyWhateverTheLayerIsCalled()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "..\\..", "..", "C:\\Temp\\x", "\\\\server\\share", "con", "a/b", "base", "", "x" })
        {
            string key = LtkProjectStore.KeyFor(name, taken);
            Assert.True(LtkProjectStore.IsSafeKey(key), key);
        }
        Assert.Equal(taken.Count, taken.Select(t => t.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void AKeyInProjectJsonThatPointsOutOfTheStoreReadsNothing()
    {
        string project = Path.Combine(_root, "edited");
        string store = LtkProjectStore.RootOf(project);
        Directory.CreateDirectory(store);
        // a document where the key ".." would lead: <store>/game_data/../declarations.json
        File.WriteAllText(Path.Combine(store, "declarations.json"), "{\"version\":1,\"modules\":[{\"target\":\"data/a.bin\",\"edits\":[]}]}");
        var p = new ReyProject { Name = "E", RootPath = project };
        p.Layers.Add(new ProjectLayer { Name = "winter", DeclarationsKey = ".." });

        Assert.Null(LtkProjectStore.ReadLayer(p, "winter"));
        Assert.Empty(LtkProjectStore.ReadLayers(p));
        Assert.Null(LtkProjectStore.ReadDeclarationsText(p, p.Layers[0]));
    }

    // ===================================================== an import never puts such a name in the project

    /// <summary>A rooted path - a package can spell any absolute path as a layer name - that points into THIS test's own temporary folder, so a
    /// regression that used it would damage a folder this test deletes anyway and not <c>C:\Temp</c> (review, round 3). (<c>\\server\share</c>
    /// below can write nowhere: it is never opened.)</summary>
    private string Rooted => Path.Combine(_root, "rooted", "x");

    private string[] Hostile => new[] { "..\\..", "..", Rooted, "\\\\server\\share", "con", "a/b", "base" };

    /// <summary>What an import makes of <see cref="Rooted"/>: the name's usable characters, cut to 64.</summary>
    private string RootedSafe => FantomeLayers.SafeName(Rooted, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base", "layer", "layer-2" });

    /// <summary>A name as a Note quotes it: the first 80 characters.</summary>
    private static string Quoted(string name) => name.Length <= 80 ? name : name[..80] + "...";

    private const string OneModule =
        "{\"version\":1,\"modules\":[{\"target\":\"data/maps/a.bin\",\"edits\":[{\"Maps/A\":{\"speed\":2}}],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":0}}]}";

    /// <summary>A package whose layer table names the hostile spellings (with a GameData document each), and whose directories include
    /// one called CON; the layer called CON has an override file.</summary>
    private string HostilePackage()
    {
        var layers = new JsonObject();
        int priority = 1;
        foreach (string name in Hostile)
            layers[name] = new JsonObject
            {
                ["GameData"] = JsonNode.Parse(OneModule),
                ["Name"] = name,
                ["DisplayName"] = "Display of " + name,
                ["Priority"] = name == "base" ? 0 : priority++,
            };
        var info = new JsonObject
        {
            ["Name"] = "Hostile", ["Author"] = "T", ["Description"] = "layers with awkward names", ["Layers"] = layers,
            ["Hashtables"] = JsonNode.Parse(TableManifest),
        };

        string wads = Path.Combine(_root, "wads");
        var baseWad = PackWad(Scratch, Path.Combine(wads, "base.wad.client"), ("data/maps/a.bin", new byte[] { 1, 2, 3 }));
        var conWad = PackWad(Scratch, Path.Combine(wads, "con.wad.client"), ("assets/c.tex", new byte[] { 9 }));
        return WriteFantome(Path.Combine(_root, "hostile.fantome"),
            Entry("META/info.json", info.ToJsonString()),
            Table("data/maps/a.bin", "assets/c.tex"),
            ("WAD/Map11.wad.client", baseWad),
            ("WAD_con/Ahri.wad.client", conWad),
            ("META/game_data/con/data/x.ptch", new byte[] { 5, 6, 7 }));
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).ToDictionary(
            p => Path.GetRelativePath(dir, p),
            p => Directory.Exists(p) ? "<dir>" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

    private (FantomeImportResult Import, ReyProject Project) ImportHostile()
    {
        var import = FantomeImporter.Import(HostilePackage(), Projects, null, new HashDatabase());
        return (import, ReyProjectService.OpenFolder(import.RootPath));
    }

    [Fact]
    public void AnImportKeepsEveryLayerUnderANameThatIsSafeAndSaysWhatItDid()
    {
        // things a hostile name could reach: a sibling folder with a canary in it, and the folder the rooted name points at
        Directory.CreateDirectory(Path.Combine(_root, "outside"));
        File.WriteAllText(Path.Combine(_root, "outside", "canary.txt"), "alive");
        Directory.CreateDirectory(Rooted);
        File.WriteAllText(Path.Combine(Rooted, "precious.txt"), "precious");
        string package = HostilePackage();
        var before = Snapshot(_root);

        var import = FantomeImporter.Import(package, Projects, null, new HashDatabase());
        var project = ReyProjectService.OpenFolder(import.RootPath);

        // the layers: the same count, the same priorities and display names, every name usable as a folder
        Assert.Equal(new[] { "base", "layer", "layer-2", RootedSafe, "server_share", "con_", "a_b" }, project.Layers.Select(l => l.Name));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, project.Layers.Select(l => l.Priority));
        Assert.Equal("Display of ..\\..", project.Layers.Single(l => l.Name == "layer").DisplayName);
        Assert.All(project.Layers.Where(l => l.Name != "base"), l => Assert.Null(FantomeLayers.NameProblem(l.Name)));
        Assert.All(import.Layers, l => Assert.True(FantomeLayers.IsBase(l.Name) || FantomeLayers.NameProblem(l.Name) is null));
        Assert.InRange(RootedSafe.Length, 1, 64);                                                  // a long path is cut, and stays a name

        // each rename is in the Notes, with the name as the package spelled it
        foreach (var (original, safe) in new[] { ("..\\..", "layer"), ("..", "layer-2"), (Rooted, RootedSafe), ("\\\\server\\share", "server_share"), ("con", "con_"), ("a/b", "a_b") })
            Assert.Contains(import.Notes, n => n.StartsWith($"The package names a layer '{Quoted(original)}'", StringComparison.Ordinal) && n.Contains($"imported as '{safe}'", StringComparison.Ordinal));
        Assert.Equal(6, import.Notes.Count);

        // the declarations are all there, under keys that are folder names, and the override file followed its renamed layer
        var stored = LtkProjectStore.ReadLayers(project);
        Assert.Equal(7, stored.Count);
        Assert.All(stored, d => { Assert.True(LtkProjectStore.IsSafeKey(d.Key)); Assert.Single(d.Modules); });
        Assert.Equal(new[] { "data/x.ptch" }, stored.Single(d => d.Layer == "con_").Files.Select(f => f.Path));
        Assert.Equal(1, import.OverrideFiles);
        Assert.Equal(7, import.Layers.Count(l => l.GameDataModules == 1));              // the declarations of every layer, base included

        // the WAD of the directory called CON is the renamed layer's
        Assert.Equal(new[] { "Map11", "layers/con_/Ahri" }, project.ProjectFolders);
        Assert.Equal("con_", project.LayerOfFolder("layers/con_/Ahri"));
        Assert.True(File.Exists(Path.Combine(import.RootPath, "layers", "con_", "Ahri", "assets", "c.tex")));

        // nothing was written but the project - not above it, not beside it, not where the rooted name points. The whole tree of this
        // test, with the hash of every file, is compared with what it was: whatever the import added or changed must be below the
        // project folder, and nothing that was there may be gone or different. (The system temp folder above this test's cannot be
        // listed reliably - other tests share it - so this covers what a name two levels up from the project could reach.)
        var after = Snapshot(_root);
        string projectRel = Path.GetRelativePath(_root, import.RootPath);
        var changed = after.Where(kv => !before.TryGetValue(kv.Key, out var was) || was != kv.Value).Select(kv => kv.Key).ToList();
        Assert.NotEmpty(changed);
        Assert.All(changed, rel => Assert.True(rel == projectRel || rel.StartsWith(projectRel + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"{rel} was written by the import and is not in the project"));
        foreach (var (rel, hash) in before) Assert.True(after.TryGetValue(rel, out var now) && now == hash, $"{rel} was changed or removed by the import");
        Assert.Equal("precious", File.ReadAllText(Path.Combine(Rooted, "precious.txt")));
        Assert.Equal("alive", File.ReadAllText(Path.Combine(_root, "outside", "canary.txt")));
    }

    [Fact]
    public void ALayerNameTooLongToBeAFolderIsRenamedAndTheNoteIsNotAsLongAsTheName()
    {
        string name = new string('x', 5000);                                                        // made of letters, and 5,000 of them
        string package = WriteFantome(Path.Combine(_root, "long.fantome"),
            Entry("META/info.json", "{\"Layers\":{\"" + name + "\":{\"Name\":\"" + name + "\",\"Priority\":1,\"GameData\":" + OneModule + "}}}"));

        var import = FantomeImporter.Import(package, Projects, null, new HashDatabase());

        Assert.Equal(new string('x', 64), Assert.Single(import.Layers).Name);
        var note = Assert.Single(import.Notes);
        Assert.Contains("at most 100 characters", note);
        Assert.True(note.Length < 700, $"the note is {note.Length} characters");
        Assert.Single(LtkProjectStore.ReadLayers(ReyProjectService.OpenFolder(import.RootPath)));
    }

    [Fact]
    public void ARenamedLayerKeepsItsNameThroughSavingAndReopening()
    {
        var (_, project) = ImportHostile();
        ReyProjectService.Save(project, project.ProjectFilePath!);

        var again = ReyProjectService.OpenFolder(project.RootPath!);

        Assert.Equal(project.Layers.Select(l => l.Name), again.Layers.Select(l => l.Name));
        Assert.Equal(7, LtkProjectStore.ReadLayers(again).Count);
    }

    [Fact]
    public void ADirectoryLayerNeverGetsANameADeclaredLayerHolds()
    {
        // WAD_a_b/ is a layer of its own; the declared "a/b" must not become it (and so must not pick up its WAD)
        byte[] wad = PackWad(Scratch, Path.Combine(_root, "wads", "ab.wad.client"), ("assets/ab.tex", new byte[] { 1 }));
        string package = WriteFantome(Path.Combine(_root, "collide.fantome"),
            Entry("META/info.json", "{\"Layers\":{\"a/b\":{\"Name\":\"a/b\",\"Priority\":1,\"GameData\":" + OneModule + "},\"a_b\":{\"Name\":\"a_b\",\"Priority\":2}}}"),
            ("WAD_a_b/Ahri.wad.client", wad));

        var import = FantomeImporter.Import(package, Projects, null, new HashDatabase());
        var project = ReyProjectService.OpenFolder(import.RootPath);

        Assert.Equal(new[] { "a_b", "a_b-2" }, project.Layers.Select(l => l.Name).Order(StringComparer.Ordinal));
        // the declared "a_b" is the one that holds the WAD; the renamed "a/b" holds the GameData and no WAD
        Assert.Equal(new[] { "layers/a_b/Ahri" }, project.Layers.Single(l => l.Name == "a_b").Folders);
        Assert.Empty(project.Layers.Single(l => l.Name == "a_b-2").Folders);
        Assert.Single(LtkProjectStore.ReadLayer(project, "a_b-2")!.Modules);
    }

    [Fact]
    public void AnExportOfTheImportedPackageWritesTheSafeNamesAndTheContentUnchanged()
    {
        string source = HostilePackage();
        var import = FantomeImporter.Import(source, Projects, null, new HashDatabase());
        var project = ReyProjectService.OpenFolder(import.RootPath);
        project.OutputDirectory = Path.Combine(_root, "build");
        var vm = new MainWindowViewModel { Project = project };

        string output = Export(vm, Path.Combine(_root, "out.fantome"));

        using var src = ZipFile.OpenRead(source);
        using var re = ZipFile.OpenRead(output);
        Assert.Equal(new[] { "base", "layer", "layer-2", RootedSafe, "server_share", "con_", "a_b" }, LayerKeys(re));
        Assert.True(Has(re, "WAD_con_/Ahri.wad.client"));
        Assert.True(Has(re, "META/game_data/con_/data/x.ptch"));
        Assert.DoesNotContain(re.Entries, e => e.FullName.Contains("..", StringComparison.Ordinal) || e.FullName.Contains('\\') || e.FullName.StartsWith("WAD_con/", StringComparison.Ordinal));
        // a renamed layer's declarations are the package's text, whatever the layer is now called
        foreach (var (original, safe) in new[] { ("..\\..", "layer"), (Rooted, RootedSafe), ("con", "con_"), ("a/b", "a_b") })
            Assert.Equal(GameData(src, original), GameData(re, safe));
    }

    // ===================================================== a send writes below <mod>/content and nowhere else

    /// <summary>A workshop holding other things a send must not touch, and the mod this send is an UPDATE of.</summary>
    private (string Canary, string InMod, string OtherMod, string Foreign) PlantCanaries(string slug)
    {
        string mod = Path.Combine(Workshop, slug);
        Directory.CreateDirectory(Path.Combine(mod, "content", "survivor"));
        File.WriteAllText(Path.Combine(mod, "mod.config.json"), "{\"name\":\"" + slug + "\"}");
        string inMod = Path.Combine(mod, "canary-in-mod.txt");
        string foreign = Path.Combine(mod, "content", "survivor", "foreign.txt");     // a layer the send does not own is left alone
        string canary = Path.Combine(Workshop, "canary.txt");
        string otherMod = Path.Combine(Workshop, "other-mod", "mod.config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(otherMod)!);
        File.WriteAllText(inMod, "mod");
        File.WriteAllText(foreign, "foreign");
        File.WriteAllText(canary, "workshop");
        File.WriteAllText(otherMod, "{\"name\":\"other-mod\"}");
        return (canary, inMod, otherMod, foreign);
    }

    [Fact]
    public async Task SendingTheImportedPackageWritesOnlyBelowContentAndLeavesEveryCanaryAlone()
    {
        var (import, project) = ImportHostile();
        var (canary, inMod, otherMod, foreign) = PlantCanaries("hostile");
        var before = Snapshot(Workshop);
        var vm = new MainWindowViewModel { Project = project };
        var log = new List<LogEntry>();
        ((Logger)typeof(MainWindowViewModel).GetField("_log", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!).Logged += e => { lock (log) log.Add(e); };

        await (Task)typeof(MainWindowViewModel).GetMethod("SendProjectToWorkshop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(vm, new object[] { Workshop })!;

        lock (log) Assert.DoesNotContain(log, l => l.Level == LogLevel.Error);
        Assert.Equal("workshop", File.ReadAllText(canary));
        Assert.Equal("mod", File.ReadAllText(inMod));
        Assert.Equal("{\"name\":\"other-mod\"}", File.ReadAllText(otherMod));
        Assert.Equal("foreign", File.ReadAllText(foreign));

        // every entry that is new or different lies in the mod folder; below content/ except the config and the README a send writes beside it
        string mod = Path.GetFullPath(Path.Combine(Workshop, "hostile"));
        var after = Snapshot(Workshop);
        foreach (var (rel, hash) in after)
        {
            if (before.TryGetValue(rel, out var was) && was == hash) continue;
            string full = Path.GetFullPath(Path.Combine(Workshop, rel));
            Assert.True(FantomeLayers.IsStrictlyBelow(full, mod), $"{rel} was written outside the mod");
            bool inContent = FantomeLayers.IsStrictlyBelow(full, Path.Combine(mod, "content"));
            bool beside = Path.GetDirectoryName(full) == mod && Path.GetFileName(full) is "mod.config.json" or "README.md";
            Assert.True(inContent || beside || full == Path.Combine(mod, "content"), $"{rel} is neither below content/ nor the config");
        }
        // and the layer folders a send made are the safe names, with the declarations in each
        var layerFolders = Directory.EnumerateDirectories(Path.Combine(mod, "content")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { RootedSafe, "a_b", "base", "con_", "layer", "layer-2", "server_share", "survivor" }.Order(StringComparer.Ordinal), layerFolders);
        Assert.True(File.Exists(Path.Combine(mod, "content", "con_", "game_data.yaml")));
        Assert.True(File.Exists(Path.Combine(mod, "content", "con_", "data", "x.ptch")));
        Assert.True(File.Exists(Path.Combine(mod, "content", "con_", "Ahri.wad.client", "assets", "c.tex")));
    }

    /// <summary>The ways a layer name reaches the writer of a send, each carrying <paramref name="layer"/>: by the files of a layer, by the
    /// declarations, by the override files, by the default layer, and by the layers the config declares.</summary>
    private static IEnumerable<(string Way, LtkSendOptions Options, (string Layer, string WadFolder, string RelPath, string AbsPath)[] Files)> WaysIn(string workshop, string layer, string anyFile)
    {
        var plain = new LtkSendOptions(workshop, "victim-mod", "V", "1", "d", "A");
        var one = new (string Layer, string WadFolder, string RelPath, string AbsPath)[] { ("base", "Map11", "a.txt", anyFile) };
        yield return ("files", plain, new (string Layer, string WadFolder, string RelPath, string AbsPath)[] { (layer, "Map11", "a.txt", anyFile) });
        yield return ("declarations", plain with { GameData = new Dictionary<string, string> { [layer] = "version: 1\nmodules: []\n" } }, one);
        yield return ("override files", plain with { LayerFiles = new (string Layer, string RelPath, string AbsPath)[] { (layer, "x.ptch", anyFile) } }, one);
        yield return ("default layer", plain with { Layer = layer }, new (string Layer, string WadFolder, string RelPath, string AbsPath)[] { ("", "Map11", "a.txt", anyFile) });
        yield return ("declared layers", plain with { Layers = new[] { new LtkLayer("base", 0, ""), new LtkLayer(layer, 1, "") } }, one);
    }

    [Fact]
    public void ABypassedHostileNameStillCannotMakeASendDeleteOrWriteOutsideContent()
    {
        string victim = Path.Combine(_root, "victim");                                       // a rooted name, aimed at a folder that is ours to lose
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "precious.txt"), "precious");
        string anyFile = Path.Combine(_root, "any.txt");
        File.WriteAllText(anyFile, "x");
        string[] names =
        {
            "..\\..", "..", ".", victim, "con", "a/b", "a\\b", "..\\..\\victim", "COM1", "x:y",
            "con.txt", "NUL.", "x.", "x ", "a|b", "a*b", "tab\there", "BASE", "Base", new string('x', 101),
        };
        int refused = 0;

        foreach (string name in names)
            foreach (var (way, options, files) in WaysIn(Workshop, name, anyFile))
            {
                // the mod is an UPDATE of an existing one, so content/ exists and so do the things beside it
                var (canary, inMod, otherMod, foreign) = PlantCanaries("victim-mod");
                var before = Snapshot(_root);

                var ex = Record.Exception(() => LtkWorkshopExporter.Send(options, files));

                Assert.True(ex is InvalidOperationException, $"{way} with the name '{name}' was not refused ({ex?.GetType().Name ?? "no exception"})");
                refused++;
                Assert.Equal("precious", File.ReadAllText(Path.Combine(victim, "precious.txt")));
                Assert.Equal("workshop", File.ReadAllText(canary));
                Assert.Equal("mod", File.ReadAllText(inMod));
                Assert.Equal("foreign", File.ReadAllText(foreign));
                Assert.Equal("{\"name\":\"other-mod\"}", File.ReadAllText(otherMod));
                // nothing changed anywhere, not even inside the mod's content/: the name was refused before the first delete, create or write
                Assert.Equal(before, Snapshot(_root));
                Directory.Delete(Path.Combine(Workshop, "victim-mod"), recursive: true);
                Directory.Delete(Path.Combine(Workshop, "other-mod"), recursive: true);
                File.Delete(canary);
            }
        Assert.Equal(names.Length * 5, refused);
    }

    [Fact]
    public void ABlankLayerNameOnAFileIsTheDefaultLayerAsItAlwaysWas()
    {
        string anyFile = Path.Combine(_root, "any.txt");
        File.WriteAllText(anyFile, "x");

        LtkWorkshopExporter.Send(new LtkSendOptions(Workshop, "blank", "B", "1", "d", "A"), new[] { ("  ", "Map11", "a.txt", anyFile), ("", "Map11", "b.txt", anyFile) });

        Assert.True(File.Exists(Path.Combine(Workshop, "blank", "content", "base", "Map11.wad.client", "a.txt")));
        Assert.True(File.Exists(Path.Combine(Workshop, "blank", "content", "base", "Map11.wad.client", "b.txt")));
    }

    [Fact]
    public void ARefusedNameLeavesNoHalfMadeModBehind()
    {
        string anyFile = Path.Combine(_root, "any.txt");
        File.WriteAllText(anyFile, "x");
        var options = new LtkSendOptions(Workshop, "brand-new", "N", "1", "d", "A");

        Assert.Throws<InvalidOperationException>(() => LtkWorkshopExporter.Send(options, new[] { ("..\\..", "Map11", "a.txt", anyFile) }));

        Assert.False(Directory.Exists(Path.Combine(Workshop, "brand-new")));                // not even the mod folder: names are proven first
        Assert.Empty(Directory.EnumerateFileSystemEntries(Workshop));
    }

    [Fact]
    public void ASendOfPlainNamesIsWhatItWas()
    {
        string anyFile = Path.Combine(_root, "any.txt");
        File.WriteAllText(anyFile, "x");
        var options = new LtkSendOptions(Workshop, "plain", "P", "1", "d", "A")
        {
            Layers = new[] { new LtkLayer("base", 0, "Base layer of the mod"), new LtkLayer("particle-fix", 10, "fix") },
            GameData = new Dictionary<string, string> { ["particle-fix"] = "version: 1\nmodules: []\n" },
        };

        var result = LtkWorkshopExporter.Send(options, new[] { ("base", "Map11", "data/a.txt", anyFile), ("particle-fix", "Ahri", "b.txt", anyFile) });

        string content = Path.Combine(Workshop, "plain", "content");
        Assert.True(result.Created);
        Assert.True(File.Exists(Path.Combine(content, "base", "Map11.wad.client", "data", "a.txt")));
        Assert.True(File.Exists(Path.Combine(content, "particle-fix", "Ahri.wad.client", "b.txt")));
        Assert.True(File.Exists(Path.Combine(content, "particle-fix", "game_data.yaml")));
        Assert.Equal(new[] { "base", "particle-fix" }, Directory.EnumerateDirectories(content).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnotherCasingOfBaseIsNotASecondSpellingOfItsFolder()
    {
        string anyFile = Path.Combine(_root, "any.txt");
        File.WriteAllText(anyFile, "x");

        var ex = Assert.Throws<InvalidOperationException>(() => LtkWorkshopExporter.Send(new LtkSendOptions(Workshop, "casing", "C", "1", "d", "A"),
            new[] { ("base", "Map11", "a.txt", anyFile), ("BASE", "Lux", "c.txt", anyFile) }));

        Assert.Contains("another casing of the base layer", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Workshop));
    }

    // ===================================================== review, round 3: a send writes the names it always wrote

    private static List<LogEntry> Capture(MainWindowViewModel vm)
    {
        var lines = new List<LogEntry>();
        ((Logger)typeof(MainWindowViewModel).GetField("_log", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!).Logged += e => { lock (lines) lines.Add(e); };
        return lines;
    }

    /// <summary>The M744 dialog let a layer be called "Particle Fix", a send wrote it to <c>content/Particle Fix/</c>, and LTK Manager does not
    /// validate a layer name it reads. A send refuses only a name that is unsafe as a PATH - not one the .fantome layout cannot carry.</summary>
    [Fact]
    public void ALayerCalledParticleFixIsSentToItsOwnFolderAsItAlwaysWas()
    {
        string anyFile = Path.Combine(_root, "any.txt");
        File.WriteAllText(anyFile, "x");
        var options = new LtkSendOptions(Workshop, "legacy", "L", "1", "d", "A")
        {
            Layers = new[] { new LtkLayer("base", 0, "Base layer of the mod"), new LtkLayer("Particle Fix", 10, "Disables the jade sprites") },
            GameData = new Dictionary<string, string> { ["Particle Fix"] = "version: 1\nmodules: []\n" },
            LayerFiles = new (string Layer, string RelPath, string AbsPath)[] { ("Particle Fix", "data/x.ptch", anyFile) },
        };

        var result = LtkWorkshopExporter.Send(options, new[] { ("base", "Map11", "a.txt", anyFile), ("Particle Fix", "Ahri", "b.txt", anyFile) });

        string content = Path.Combine(Workshop, "legacy", "content");
        Assert.True(File.Exists(Path.Combine(content, "Particle Fix", "Ahri.wad.client", "b.txt")));
        Assert.True(File.Exists(Path.Combine(content, "Particle Fix", "game_data.yaml")));
        Assert.True(File.Exists(Path.Combine(content, "Particle Fix", "data", "x.ptch")));
        Assert.Equal(new[] { "Particle Fix", "base" }, Directory.EnumerateDirectories(content).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var layers = JsonNode.Parse(File.ReadAllText(Path.Combine(result.ModFolder, "mod.config.json")))!["layers"]!.AsArray();
        Assert.Contains(layers, l => (string?)l!["name"] == "Particle Fix");
    }

    [Fact]
    public async Task AProjectWithALayerCalledParticleFixStillSendsAndStillCannotBeExportedAsAFantome()
    {
        string root = Path.Combine(_root, "legacy-project");
        foreach (string folder in new[] { "Map11", "Ahri" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder, "assets"));
            File.WriteAllText(Path.Combine(root, folder, "assets", "a.dds"), folder);
        }
        var project = new ReyProject { Name = "Legacy", RootPath = root, OutputDirectory = Path.Combine(_root, "build"), ProjectFolders = { "Map11", "Ahri" } };
        project.Layers.Add(new ProjectLayer { Name = "Particle Fix", Priority = 10, Description = "d", Folders = { "Ahri" } });   // as the M744 dialog could write it
        var vm = new MainWindowViewModel { Project = project };
        var log = Capture(vm);

        await (Task)typeof(MainWindowViewModel).GetMethod("SendProjectToWorkshop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(vm, new object[] { Workshop })!;

        lock (log) Assert.DoesNotContain(log, l => l.Level == LogLevel.Error);
        Assert.Equal("Ahri", File.ReadAllText(Path.Combine(Workshop, "legacy", "content", "Particle Fix", "Ahri.wad.client", "assets", "a.dds")));
        Assert.Equal("Map11", File.ReadAllText(Path.Combine(Workshop, "legacy", "content", "base", "Map11.wad.client", "assets", "a.dds")));

        // and the .fantome export, whose layout cannot carry the name, refuses it as before
        var ex = Assert.Throws<InvalidOperationException>(() => Export(vm, Path.Combine(_root, "legacy.fantome")));
        Assert.Contains("Project Settings", ex.Message);
        Assert.Contains("ASCII letters, digits", ex.Message);
        Assert.False(File.Exists(Path.Combine(_root, "legacy.fantome")));
    }

    [Fact]
    public void ALayerCalledParticleFixGetsAFolderInTheBuildOutputToo()
    {
        // Build Package writes a whole-claimed layer WAD below Build/<layer>/; the folder rule is the path's, so the name needs no .fantome rule
        string root = Path.Combine(_root, "legacy-build");
        Directory.CreateDirectory(Path.Combine(root, "layers", "Particle Fix", "Ahri", "assets"));
        File.WriteAllText(Path.Combine(root, "layers", "Particle Fix", "Ahri", "assets", "a.dds"), "x");
        var project = new ReyProject { Name = "B", RootPath = root, OutputDirectory = Path.Combine(_root, "Build"), ProjectFolders = { "layers/Particle Fix/Ahri" } };
        project.Layers.Add(new ProjectLayer { Name = "Particle Fix", Priority = 1, Folders = { "layers/Particle Fix/Ahri" } });
        var vm = new MainWindowViewModel { Project = project };

        var method = typeof(MainWindowViewModel).GetMethod("BuildProjectCore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(vm, new object?[] { project.OutputDirectory, null, false });

        Assert.True(File.Exists(Path.Combine(_root, "Build", "Particle Fix", "Ahri.wad.client")));
    }
}

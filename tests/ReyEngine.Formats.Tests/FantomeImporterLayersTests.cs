using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816: Import .fantome reads LTK's layered layout. M814 made Export .fantome write it (<c>WAD_&lt;layer&gt;/</c> for a layer
/// other than base, <c>Layers.&lt;name&gt;.GameData</c> for game bins shipped as declarations) and the import then said, in a
/// warning, what it left behind. These pin what the import brings in now - layers with their priorities and display
/// names, the WADs of every layer in folders of their own, each layer's GameData document and override files byte for byte,
/// the metadata - and that a package of the base layer alone still imports as it always did, with nothing to report.
///
/// <para>The archives are written by <see cref="FantomeExporter"/> itself, so the layout under test is the export's, and by
/// hand where a shape the exporter never writes has to be tolerated.</para>
/// </summary>
public sealed class FantomeImporterLayersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-import-layers-" + Guid.NewGuid().ToString("N"));

    private string Projects => Path.Combine(_root, "projects");

    public FantomeImporterLayersTests() => Directory.CreateDirectory(Projects);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>A real packed WAD holding the given files, as the project would pack them.</summary>
    private string PackedWad(string name, params (string Rel, string Text)[] files)
    {
        string folder = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in files)
        {
            string path = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        string wad = Path.Combine(_root, "wads", name);
        Directory.CreateDirectory(Path.GetDirectoryName(wad)!);
        Assert.True(WadPackService.Pack(folder, wad).Success);
        return wad;
    }

    private static JsonNode Doc(params string[] targets) => JsonNode.Parse(
        "{\"version\":1,\"modules\":[" + string.Join(",", targets.Select((t, i) =>
            $"{{\"target\":\"{t}\",\"edits\":[{{\"A/b\":{{\"speed\":2}}}}],\"origin\":{{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":{i}}}}}")) + "]}")!;

    /// <summary>The export of a layered mod: Map11 in base (with one declared bin), Ahri in layer "fix" (with two).</summary>
    private string LayeredExport()
    {
        string map = PackedWad("Map11.wad.client", ("data/maps/new.bin", "new"), ("assets/x.tex", "xx"));
        string ahri = PackedWad("Ahri.wad.client", ("assets/y.dds", "yy"));
        string archive = Path.Combine(_root, "layered.fantome");
        FantomeExporter.Export(
            new FantomeMeta
            {
                Name = "Layered", Author = "T",
                Layers = new[]
                {
                    new FantomeLayer("base", 0, GameData: Doc("data/maps/a.bin")),
                    new FantomeLayer("fix", 10, "Particle fix", Doc("data/characters/ahri/skins/skin0.bin", "data/characters/ahri/skins/skin1.bin")),
                },
            },
            new[]
            {
                new FantomeWad(map, FantomeLayers.Base, new[] { "data/maps/new.bin", "assets/x.tex" }),
                new FantomeWad(ahri, "fix", new[] { "assets/y.dds" }),
            }, null, archive);
        return archive;
    }

    private FantomeImportResult Import(string archive) => FantomeImporter.Import(archive, Projects, null, new HashDatabase());

    private static ReyProject Open(FantomeImportResult r) => ReyProjectService.OpenFolder(r.RootPath);

    /// <summary>A hand-written package: entry names to their text.</summary>
    private string Archive(string name, params (string Entry, string Text)[] entries)
    {
        string path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries)
        {
            var e = zip.CreateEntry(entry);
            if (entry.EndsWith('/')) continue;                     // a directory entry has no content
            using var s = e.Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
        return path;
    }

    private static (string, int, int, int)[] Shape(FantomeImportResult r) =>
        r.Layers.Select(l => (l.Name, l.Wads, l.GameDataModules, l.OverrideFiles)).ToArray();

    // ===================================================== a layered export

    [Fact]
    public void ALayeredExportImportsEveryLayerWithItsWadsAndDeclarations()
    {
        var result = Import(LayeredExport());

        // the base layer's WAD/ folder, as ever - and the other layer's WAD_fix/ folder, which the import used to leave behind
        Assert.Equal(2, result.Wads);
        Assert.Equal(0, result.FailedChunks);
        Assert.True(Directory.Exists(Path.Combine(result.RootPath, "Map11")));
        Assert.True(Directory.Exists(Path.Combine(result.RootPath, "layers", "fix", "Ahri")));
        Assert.True(File.Exists(Path.Combine(result.RootPath, "layers", "fix", "Ahri", "assets", "y.dds")));

        Assert.Equal(1, result.LayerWads);
        Assert.Equal(3, result.GameDataModules);
        Assert.Equal(new[] { ("base", 1, 1, 0), ("fix", 1, 2, 0) }, Shape(result));

        // the harvested hashtable names every chunk of both WADs, and the import used it before it unpacked
        Assert.Equal(3, result.TableNames);
        Assert.Equal(3, result.ChunksNamedByTables);
        Assert.True(File.Exists(Path.Combine(result.RootPath, "Map11", "data", "maps", "new.bin")));
        Assert.Empty(Directory.GetFiles(Path.Combine(result.RootPath, "layers", "fix", "Ahri"), "????????????????.*"));
    }

    [Fact]
    public void TheProjectHoldsTheLayersWithTheirPrioritiesNamesAndFolders()
    {
        var project = Open(Import(LayeredExport()));

        Assert.Equal(new[] { "base", "fix" }, project.Layers.Select(l => l.Name));
        Assert.Equal(new[] { 0, 10 }, project.Layers.Select(l => l.Priority));
        Assert.Null(project.Layers[0].DisplayName);
        Assert.Equal("Particle fix", project.Layers[1].DisplayName);
        Assert.Equal(new[] { "Map11", "layers/fix/Ahri" }, project.ProjectFolders);

        // the folder a layer claims whole rides that layer; the base folder rides base
        Assert.Equal(new[] { "layers/fix/Ahri" }, project.Layers[1].Folders);
        Assert.Equal("fix", project.LayerOfFolder("layers/fix/Ahri"));
        Assert.Equal("base", project.LayerOfFolder("Map11"));
    }

    [Fact]
    public void EachLayersGameDataIsStoredAsTheTextThePackageHeldAndItsModulesAreFound()
    {
        var project = Open(Import(LayeredExport()));

        var fix = LtkProjectStore.ReadLayer(project, "fix")!;
        Assert.Equal(2, fix.Modules.Count);
        Assert.Equal(new[] { "data/characters/ahri/skins/skin0.bin", "data/characters/ahri/skins/skin1.bin" }, fix.Modules.Select(m => m.Target));
        Assert.Equal(Doc("data/characters/ahri/skins/skin0.bin", "data/characters/ahri/skins/skin1.bin").ToJsonString(), JsonNode.Parse(fix.DocumentText)!.ToJsonString());

        // and it is the span of info.json that held it, not a rendering of it
        using var zip = ZipFile.OpenRead(Path.Combine(_root, "layered.fantome"));
        Assert.Equal(GameDataTextIn(zip, "fix"), fix.DocumentText);
        Assert.Equal(GameDataTextIn(zip, "base"), LtkProjectStore.ReadLayer(project, "base")!.DocumentText);
    }

    [Fact]
    public void NothingIsLeftBehindSoThereIsNoWarning()
    {
        var result = Import(LayeredExport());
        Assert.Empty(result.Notes);
        Assert.Null(result.NotImportedWarning);
    }

    [Fact]
    public void ALayerWithWadsAndNoDeclarationsIsALayerWithFolders()
    {
        string archive = Path.Combine(_root, "wads-only.fantome");
        FantomeExporter.Export(
            new FantomeMeta { Name = "W", Author = "T", Layers = new[] { new FantomeLayer("fx", 5) } },
            new[] { new FantomeWad(PackedWad("Ahri.wad.client", ("assets/y.dds", "yy")), "fx") }, null, archive);

        var result = Import(archive);

        Assert.Equal(1, result.Wads);                                  // base held nothing
        Assert.Equal(new[] { ("fx", 1, 0, 0) }, Shape(result));
        var project = Open(result);
        Assert.Equal(new[] { "layers/fx/Ahri" }, project.ProjectFolders);
        Assert.Null(project.Layers.Single().DeclarationsKey);          // it declared nothing, so nothing is stored for it
        Assert.Null(LtkProjectStore.ReadLayer(project, "fx"));
        Assert.Null(result.NotImportedWarning);
    }

    [Fact]
    public void AnExportWithOnlyDeclarationsImportsNoWadAndKeepsTheModules()
    {
        string archive = Path.Combine(_root, "declared-only.fantome");
        FantomeExporter.Export(
            new FantomeMeta { Name = "D", Author = "T", Layers = new[] { new FantomeLayer("base", 0, GameData: Doc("data/a.bin", "data/b.bin")) } },
            Array.Empty<FantomeWad>(), null, archive);

        var result = Import(archive);

        Assert.Equal(0, result.Wads);
        Assert.Equal(0, result.LayerWads);
        Assert.Equal(2, result.GameDataModules);
        Assert.Empty(Open(result).ProjectFolders);
        Assert.Null(result.NotImportedWarning);
    }

    // ===================================================== a package with nothing layered

    [Fact]
    public void ABaseOnlyPackageHasNoLayersAndNoWarning()
    {
        string archive = Path.Combine(_root, "plain.fantome");
        FantomeExporter.Export(
            new FantomeMeta { Name = "Plain", Author = "T" },
            new[] { new FantomeWad(PackedWad("Map11.wad.client", ("data/maps/new.bin", "new"))) }, null, archive);

        var result = Import(archive);

        Assert.Equal(1, result.Wads);
        Assert.Empty(result.Layers);                                   // base declared nothing: the project is as an old import's
        Assert.Empty(Open(result).Layers);
        Assert.Equal(0, result.LayerWads);
        Assert.Equal(0, result.GameDataModules);
        Assert.Null(result.NotImportedWarning);
    }

    [Fact]
    public void ALegacyPackageWithoutALayersTableImportsAsItAlwaysDid()
    {
        var result = Import(Archive("legacy.fantome",
            ("META/info.json", "{\"Name\":\"Legacy\",\"Author\":\"A\",\"Version\":\"1.0.0\",\"Description\":\"d\",\"Heart\":\"h\",\"Home\":\"o\"}"),
            ("RAW/assets/safe.txt", "inside")));

        Assert.Equal(1, result.RawFiles);
        Assert.Empty(result.Layers);
        Assert.Null(result.NotImportedWarning);

        var project = Open(result);
        Assert.Equal("Legacy", project.ModName);
        Assert.Equal("A", project.ModAuthor);
        Assert.Equal("1.0.0", project.ModVersion);
        Assert.Equal("d", project.ModDescription);
        Assert.Equal("h", project.ModHeart);
        Assert.Equal("o", project.ModHome);
        Assert.Empty(project.Layers);
        Assert.Null(project.ModLicense);
        Assert.Empty(project.ModTags);
        Assert.False(Directory.Exists(LtkProjectStore.RootOf(result.RootPath)), "nothing layered, so nothing is stored");
    }

    [Fact]
    public void AnEmptyDeclarationDocumentIsKeptAndAnEmptyLayerIsStillALayer()
    {
        var result = Import(Archive("empty-layers.fantome",
            ("META/info.json",
                "{\"Name\":\"E\",\"Layers\":{\"base\":{\"Name\":\"base\",\"Priority\":0,\"GameData\":{\"version\":1,\"modules\":[]}},"
                + "\"fix\":{\"Name\":\"fix\",\"Priority\":1}}}"),
            ("RAW/assets/safe.txt", "inside")));

        Assert.Equal(new[] { ("base", 0, 0, 0), ("fix", 0, 0, 0) }, Shape(result));
        Assert.Null(result.NotImportedWarning);
        var project = Open(result);
        Assert.Equal("{\"version\":1,\"modules\":[]}", LtkProjectStore.ReadLayer(project, "base")!.DocumentText);
    }

    // ===================================================== shapes the exporter never writes

    /// <summary>A WAD is counted once however many entries it has: a raw folder is many files and one WAD, a directory
    /// placeholder is no WAD at all.</summary>
    [Fact]
    public void ARawFolderWadIsOneWadAndADirectoryEntryIsNone()
    {
        var result = Import(Archive("raw-layer.fantome",
            ("WAD_fx/", ""),
            ("WAD_fx/Ahri.wad.client/", ""),
            ("WAD_fx/Ahri.wad.client/assets/a.dds", "a"),
            ("WAD_fx/Ahri.wad.client/assets/b.dds", "b"),
            ("WAD_fx/Ahri.wad.client/data/c.bin", "c"),
            ("WAD_empty/", "")));

        Assert.Equal(1, result.Wads);
        Assert.Equal(3, result.ExtractedFiles);
        Assert.Equal(new[] { ("fx", 1, 0, 0) }, Shape(result));          // fx was never declared: it is a layer at priority 0
        Assert.Equal(0, Shape(result)[0].Item3);
        Assert.Equal(0, result.Layers[0].Priority);
        Assert.True(File.Exists(Path.Combine(result.RootPath, "layers", "fx", "Ahri", "data", "c.bin")));
    }

    [Fact]
    public void AWadDirectoryOfAnUndeclaredLayerDeclaresItAtPriorityZero()
    {
        var project = Open(Import(Archive("undeclared.fantome",
            ("META/info.json", "{\"Name\":\"U\",\"Layers\":{\"low\":{\"Name\":\"low\",\"Priority\":-4}}}"),
            ("WAD_zeta/Lux.wad.client/data/x.bin", "x"))));

        Assert.Equal(new[] { "low", "zeta" }, project.Layers.Select(l => l.Name));
        Assert.Equal(new[] { -4, 0 }, project.Layers.Select(l => l.Priority));
    }

    [Fact]
    public void LayerNamesAreComparedWithoutRegardToCaseAndTheTablesSpellingWins()
    {
        var result = Import(Archive("case.fantome",
            ("META/info.json", "{\"Layers\":{\"Fix\":{\"Name\":\"Fix\",\"Priority\":1,\"GameData\":{\"version\":1,\"modules\":[{}]}}}}"),
            ("WAD_fix/Ahri.wad.client/data/x.bin", "x")));

        Assert.Equal(new[] { ("Fix", 1, 1, 0) }, Shape(result));
        var project = Open(result);
        Assert.Equal(new[] { "layers/Fix/Ahri" }, project.ProjectFolders);
        Assert.Equal("Fix", project.LayerOfFolder("layers/fix/ahri"));
    }

    [Fact]
    public void LayersAreListedBaseFirstThenByPriorityAndNameAsAPersonReadsIt()
    {
        var result = Import(Archive("order.fantome",
            ("META/info.json",
                "{\"Layers\":{\"layer10\":{\"Name\":\"layer10\",\"Priority\":2},\"zeta\":{\"Name\":\"zeta\",\"Priority\":1},"
                + "\"base\":{\"Name\":\"base\",\"Priority\":7,\"DisplayName\":\"Base\"},\"layer9\":{\"Name\":\"layer9\",\"Priority\":2},"
                + "\"alpha\":{\"Name\":\"alpha\",\"Priority\":1}}}")));

        Assert.Equal(new[] { "base", "alpha", "zeta", "layer9", "layer10" }, result.Layers.Select(l => l.Name).ToArray());
        Assert.Equal(0, result.Layers[0].Priority);                    // league-mod reads a base layer at 0 whatever the file says
        Assert.Equal("Base", result.Layers[0].DisplayName);
        Assert.Equal(new[] { "base", "alpha", "zeta", "layer9", "layer10" }, Open(result).Layers.Select(l => l.Name).ToArray());
    }

    /// <summary>Review: a layer name a folder cannot carry is not put in the project as it is - it is a path in a send, in the build output
    /// and in an export. The layer is kept under a safe name and the Notes say so (<see cref="LayerNameSafetyTests"/> goes through the
    /// hostile spellings; this is the plain one).</summary>
    [Fact]
    public void ALayerWhoseNameAFantomeCannotCarryIsKeptUnderASafeNameAndSaidSo()
    {
        var result = Import(Archive("badname.fantome",
            ("META/info.json", "{\"Layers\":{\"my layer\":{\"Name\":\"my layer\",\"Priority\":1,\"DisplayName\":\"Mine\",\"GameData\":{\"version\":1,\"modules\":[{}]}}}}")));

        var layer = Assert.Single(result.Layers);
        Assert.Equal("my_layer", layer.Name);
        Assert.Equal((1, 1, "Mine"), (layer.Priority, layer.GameDataModules, layer.DisplayName));              // the content is the package's, whatever the name
        Assert.Contains("The package names a layer 'my layer'", result.NotImportedWarning);
        Assert.Contains("it was imported as 'my_layer'", result.NotImportedWarning);
        Assert.Null(FantomeLayers.NameProblem(Assert.Single(Open(result).Layers).Name));
        Assert.Equal("my_layer", Assert.Single(Open(result).Layers).Name);
    }

    /// <summary>Review, round 3: LTK's string overrides are <c>locale -> field -> text</c> and ltk_fantome refuses every other shape. An import
    /// reads that shape and nothing else: whatever else the file holds is a Note and the layer's other content is imported. (A table read as it
    /// was stopped at 64 levels with an exception nothing caught; one of 63 imported fine and then threw when the project was saved, because
    /// project.json and mod.config.json add levels of their own.)</summary>
    [Theory]
    [InlineData("{\"en_us\":{\"title\":{\"nested\":\"object\"}}}")]                  // a table as a field
    [InlineData("{\"en_us\":[\"a\",\"b\"]}")]                                         // a list as a locale
    [InlineData("{\"en_us\":{\"count\":3}}")]                                         // a number as text
    [InlineData("{\"en_us\":{\"gone\":null}}")]
    [InlineData("DEEP63")]
    [InlineData("DEEP70")]
    public void StringOverridesOfAnotherShapeAreANoteAndTheRestOfThePackageStillImportsAndTheProjectSaves(string overrides)
    {
        if (overrides.StartsWith("DEEP"))
        {
            int levels = int.Parse(overrides[4..]);
            overrides = string.Concat(Enumerable.Repeat("{\"a\":", levels)) + "1" + string.Concat(Enumerable.Repeat("}", levels));
        }
        var result = Import(Archive("shape.fantome",
            ("META/info.json", "{\"Name\":\"Shape\",\"Layers\":{\"words\":{\"Name\":\"words\",\"Priority\":1,\"DisplayName\":\"Words\",\"StringOverrides\":" + overrides
                + ",\"GameData\":{\"version\":1,\"modules\":[{}]}}}}"),
            ("WAD_words/Ahri.wad.client/data/x.bin", "x")));

        var layer = Assert.Single(result.Layers);
        Assert.Equal(("words", 1, 1, 1, "Words"), (layer.Name, layer.Priority, layer.Wads, layer.GameDataModules, layer.DisplayName));
        var note = Assert.Single(result.Notes);
        Assert.StartsWith("Layer 'words' has StringOverrides that are not locale -> field -> text as LTK reads them (", note);
        Assert.EndsWith("so they were not read.", note);

        // the project is made, saves and opens again - the failure of a 63-level table was in the SAVE
        var project = Open(result);
        Assert.Null(Assert.Single(project.Layers).StringOverrides);
        ReyProjectService.Save(project, project.ProjectFilePath!);
        var again = ReyProjectService.OpenFolder(result.RootPath);
        Assert.Equal("words", Assert.Single(again.Layers).Name);
        Assert.Equal(1, LtkProjectStore.ReadLayers(again).Single().Modules.Count);
    }

    // ===================================================== review, round 3: a name Windows cannot create must not end an import that has unpacked every WAD

    [Fact]
    public void OverrideFilesWindowsCannotMakeAsTheyAreNamedAreNotesAndTheImportGoesOn()
    {
        string longSegment = new string('x', 300);
        string doc = "{\"version\":1,\"modules\":[{\"target\":\"data/a.bin\",\"edits\":[{\"overrides\":[\"data/ok.ptch\",\"" + longSegment + "/z.ptch\","
            + "\"data/nul/q.ptch\",\"data/trail./w.ptch\",\"data/.../v.ptch\",\"data/CON.txt\"]}],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":0}}]}";
        var result = Import(Archive("segments.fantome",
            ("META/info.json", "{\"Name\":\"Seg\",\"Layers\":{\"events\":{\"Name\":\"events\",\"Priority\":1,\"GameData\":" + doc + "}}}"),
            ("META/game_data/events/data/ok.ptch", "ok"),
            ("META/game_data/events/" + longSegment + "/z.ptch", "too long"),
            ("META/game_data/events/data/nul/q.ptch", "a device"),
            ("META/game_data/events/data/trail./w.ptch", "a trailing dot"),
            ("META/game_data/events/data/.../v.ptch", "only dots"),
            ("META/game_data/events/data/CON.txt", "a device with an extension")));

        // the one file Windows can make is kept; each of the others is a Note naming the file and why
        Assert.Equal(1, result.OverrideFiles);
        Assert.Equal(5, result.Notes.Count(n => n.Contains("was not carried: the path has a segment that ")));
        Assert.Contains(result.Notes, n => n.Contains("data/nul/q.ptch was not carried") && n.Contains("Windows device name"));
        Assert.Contains(result.Notes, n => n.Contains("data/CON.txt was not carried") && n.Contains("Windows device name"));
        Assert.Contains(result.Notes, n => n.Contains("data/trail./w.ptch was not carried") && n.Contains("ends with a dot or a space"));
        Assert.Contains(result.Notes, n => n.Contains("data/.../v.ptch was not carried") && n.Contains("ends with a dot or a space"));
        Assert.Contains(result.Notes, n => n.Contains("was not carried") && n.Contains("longer than 255 characters"));
        // a file the package holds and this import could not keep is not also "missing from the package"
        Assert.DoesNotContain(result.Notes, n => n.Contains("which the package does not hold"));

        // and the import finished: the layer, its declarations and the one file are in the project
        var project = Open(result);
        var data = LtkProjectStore.ReadLayer(project, "events")!;
        Assert.Equal(new[] { "data/ok.ptch" }, data.Files.Select(f => f.Path));
        Assert.Single(data.Modules);
    }

    [Fact]
    public void ADamagedThumbnailEntryIsANoteAndTheImportGoesOn()
    {
        // META/image.png deflated, then the first byte of its data made a reserved block type: reading it throws InvalidDataException
        string path = Archive("thumb.fantome",
            ("META/info.json", "{\"Name\":\"Thumb\",\"Layers\":{\"events\":{\"Name\":\"events\",\"Priority\":1,\"GameData\":{\"version\":1,\"modules\":[{}]}}}}"));
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        using (var s = zip.CreateEntry("META/image.png", CompressionLevel.Optimal).Open())
            s.Write(new byte[2000].Select(_ => (byte)'A').ToArray());
        byte[] bytes = File.ReadAllBytes(path);
        int local = Array.FindIndex(Enumerable.Range(0, bytes.Length - 4).ToArray(), i => BitConverter.ToUInt32(bytes, i) == 0x04034b50 && Encoding.ASCII.GetString(bytes, i + 30, 14) == "META/image.png");
        Assert.True(local >= 0);
        bytes[local + 30 + 14 + BitConverter.ToUInt16(bytes, local + 28)] = 0xFF;
        File.WriteAllBytes(path, bytes);

        var result = Import(path);                                               // used to throw out of ExtractToFile, after everything was unpacked

        var note = Assert.Single(result.Notes);
        Assert.StartsWith("The package's META/image.png could not be kept (", note);
        Assert.EndsWith("so the project has no thumbnail.", note);
        Assert.Null(Open(result).ThumbnailPath);
        Assert.Equal(1, Assert.Single(result.Layers).GameDataModules);           // and the rest came in
    }

    [Fact]
    public void HashtablesWhoseFilesWindowsCannotMakeAreKeptUnderASafeNameAndStillNameTheChunks()
    {
        string wad = PackedWad("Map11.wad.client", ("assets/a.tex", "a"), ("assets/b.tex", "b"), ("assets/c.tex", "c"), ("assets/d.tex", "d"));
        string[] tables = { "META/hashes/con.txt", "META/hashes/nul", "META/hashes/names.", "META/hashes/" + new string('t', 300) + ".txt" };
        string[] names = { "assets/a.tex", "assets/b.tex", "assets/c.tex", "assets/d.tex" };
        string manifest = "[" + string.Join(",", tables.Select(t => "{\"Path\":\"" + t + "\",\"Category\":\"game\",\"Algorithm\":\"xxh64\",\"Bits\":64}")) + "]";
        var entries = new List<(string, byte[])>
        {
            LayeredFantomeSupport.Entry("META/info.json", "{\"Name\":\"Tables\",\"Hashtables\":" + manifest + "}"),
            ("WAD/Map11.wad.client", File.ReadAllBytes(wad)),
        };
        for (int i = 0; i < tables.Length; i++) entries.Add(LayeredFantomeSupport.Entry(tables[i], names[i] + "\n"));
        string archive = LayeredFantomeSupport.WriteFantome(Path.Combine(_root, "tables.fantome"), entries.ToArray());

        var result = Import(archive);                                           // used to throw from the store, after the WAD was unpacked

        Assert.Empty(result.Notes);
        Assert.Equal(4, result.TableNames);
        Assert.Equal(4, result.ChunksNamedByTables);                            // the tables did their work: all four chunks have their paths
        foreach (string name in names) Assert.True(File.Exists(Path.Combine(result.RootPath, "Map11", name.Replace('/', Path.DirectorySeparatorChar))), name);
        var stored = LtkProjectStore.ReadHashtables(result.RootPath);
        Assert.Equal(tables.Order(), stored.Select(t => t.Source).Order());      // each is still known by the path the package gave it
        Assert.All(stored, t => Assert.Null(FantomeLayers.SegmentProblem(t.File["hashes/".Length..], 100)));
        Assert.Equal(4, LtkProjectStore.LoadHashtables(result.RootPath, new HashDatabase()));
    }

    [Fact]
    public void StringOverridesInTheShapeLTKReadsAreKeptAndSurviveTheSave()
    {
        var result = Import(Archive("strings.fantome",
            ("META/info.json", "{\"Name\":\"Strings\",\"Layers\":{\"words\":{\"Name\":\"words\",\"Priority\":1,\"StringOverrides\":{\"en_us\":{\"b\":\"2\",\"a\":\"1\"},\"default\":{}}}}}")));

        Assert.Empty(result.Notes);
        var project = Open(result);
        ReyProjectService.Save(project, project.ProjectFilePath!);
        var so = ReyProjectService.OpenFolder(result.RootPath).Layers.Single().StringOverrides!;
        Assert.Equal(new[] { "en_us", "default" }, so.Select(x => x.Key));
        Assert.Equal(new[] { "b", "a" }, so["en_us"]!.AsObject().Select(x => x.Key));
    }

    [Fact]
    public void TwoLayersOfOneNameIgnoringCaseKeepTheFirstAndSayWhy()
    {
        var result = Import(Archive("dup.fantome",
            ("META/info.json", "{\"Layers\":{\"fix\":{\"Name\":\"fix\",\"Priority\":1},\"Fix\":{\"Name\":\"Fix\",\"Priority\":2}}}")));

        Assert.Equal(new[] { "fix" }, result.Layers.Select(l => l.Name));
        Assert.Contains("repeats the name 'Fix'", result.NotImportedWarning);
    }

    [Theory]
    [InlineData("{\"Layers\":[1,2,3]}", "Layers is not a table")]
    [InlineData("{\"Layers\":{\"fx\":5}}", "Layer 'fx' is not an object")]
    [InlineData("{\"Layers\":{\"fx\":{\"GameData\":\"nope\"}}}", "GameData that is not an object")]
    [InlineData("{\"Layers\":{\"fx\":{\"Priority\":\"high\"}}}", "Priority that is not a whole number")]
    public void AMalformedLayersTableIsToleratedAndTheDirectoriesStillCount(string info, string said)
    {
        var result = Import(Archive("odd.fantome",
            ("META/info.json", info),
            ("WAD_fx/Ahri.wad.client/data/x.bin", "x")));

        Assert.Equal(1, result.Wads);                                  // the WAD is there whatever the table says
        Assert.Equal(0, result.GameDataModules);
        Assert.Contains(said, result.NotImportedWarning);
        Assert.True(Directory.Exists(Path.Combine(result.RootPath, "layers", "fx", "Ahri")));
    }

    [Theory]
    [InlineData("{\"Layers\":null}")]
    [InlineData("[1,2,3]")]                                                        // not even an object
    [InlineData("{ this is not json")]
    public void AnInfoJsonThatIsNotATableOfLayersFallsBackToTheDirectories(string info)
    {
        var result = Import(Archive("odd2.fantome",
            ("META/info.json", info),
            ("WAD_fx/Ahri.wad.client/data/x.bin", "x")));

        Assert.Equal(1, result.Wads);
        Assert.Equal("odd2", result.ProjectName);                      // named after the file, as ever
        Assert.Equal(new[] { "fx" }, result.Layers.Select(l => l.Name));
    }

    [Fact]
    public void AMalformedInfoJsonIsSaidSoInsteadOfBeingDroppedSilently()
    {
        var result = Import(Archive("garbled.fantome", ("META/info.json", "{ this is not json"), ("RAW/a.txt", "a")));

        Assert.Equal("garbled", result.ProjectName);
        Assert.Contains("META/info.json is not a JSON object", result.NotImportedWarning);
    }

    [Fact]
    public void InfoJsonIsFoundWhateverItsCasingAndAByteOrderMarkIsStripped()
    {
        string path = Path.Combine(_root, "bom.fantome");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("meta/INFO.JSON");
            using var s = entry.Open();
            s.Write(new byte[] { 0xEF, 0xBB, 0xBF });
            s.Write(Encoding.UTF8.GetBytes("\n {\"Name\":\"Bom\",\"Author\":\"A\",\"Layers\":{\"base\":{\"Name\":\"base\",\"Priority\":0,\"DisplayName\":\"Base\"}}}\n"));
        }

        var result = Import(path);

        Assert.Equal("Bom", result.ProjectName);
        Assert.Equal("Base", Assert.Single(result.Layers).DisplayName);
    }

    [Fact]
    public void KeysThisImportDoesNotCarryAreSaidSoAndNothingElseIs()
    {
        var result = Import(Archive("extra.fantome",
            ("META/info.json", "{\"Name\":\"X\",\"Extension\":{\"a\":1},\"Layers\":{\"fx\":{\"Name\":\"fx\",\"Priority\":1,\"Mystery\":true}}}"),
            ("RAW/a.txt", "a")));

        Assert.Equal(2, result.Notes.Count);
        Assert.Contains("META/info.json has key(s) ReyEngine does not carry (Extension)", result.NotImportedWarning);
        Assert.Contains("Layer 'fx' has key(s) ReyEngine does not carry (Mystery)", result.NotImportedWarning);
        Assert.EndsWith("The .fantome itself is untouched.", result.NotImportedWarning);
    }

    // ===================================================== metadata

    [Fact]
    public void LicenseTagsChampionsMapsAndGeneratorComeIn()
    {
        var project = Open(Import(Archive("meta.fantome",
            ("META/info.json",
                "{\"Name\":\"M\",\"Author\":\"A\",\"Description\":\"d\",\"License\":{\"Name\":\"MIT\",\"Url\":\"https://x\"},"
                + "\"Tags\":[\"map-skin\",\"sfx\"],\"Champions\":[\"Ahri\"],\"Maps\":[\"summoners-rift\"],\"Generator\":\"ltk_mod_project 0.16.2\"}"))));

        Assert.Equal("MIT", project.ModLicense!.Name);
        Assert.Equal("https://x", project.ModLicense.Url);
        Assert.True(project.ModLicense.AsObject);
        Assert.Equal(new[] { "map-skin", "sfx" }, project.ModTags);
        Assert.Equal(new[] { "Ahri" }, project.ModChampions);
        Assert.Equal(new[] { "summoners-rift" }, project.ModMaps);
        Assert.Equal("ltk_mod_project 0.16.2", project.ImportedGenerator);
    }

    [Fact]
    public void ALicenseGivenAsAStringStaysAStringAndOneGivenAsAnObjectWithoutALinkStaysAnObject()
    {
        var text = Open(Import(Archive("l1.fantome", ("META/info.json", "{\"Name\":\"L\",\"License\":\"MIT\"}"))));
        Assert.Equal("MIT", text.ModLicense!.Name);
        Assert.False(text.ModLicense.AsObject);
        Assert.Null(text.ModLicense.Url);

        var obj = Open(Import(Archive("l2.fantome", ("META/info.json", "{\"Name\":\"L\",\"License\":{\"Name\":\"Custom terms\"}}"))));
        Assert.Equal("Custom terms", obj.ModLicense!.Name);
        Assert.True(obj.ModLicense.AsObject);
        Assert.Null(obj.ModLicense.Url);
    }

    [Fact]
    public void TheReadmeAndLicenseTextAreKept()
    {
        var result = Import(Archive("text.fantome",
            ("META/info.json", "{\"Name\":\"T\"}"), ("META/README.md", "# Hello"), ("META/LICENSE", "terms")));

        var files = LtkProjectStore.ReadMetaFiles(result.RootPath);
        Assert.Equal(new[] { "META/LICENSE", "META/README.md" }, files.Select(f => f.EntryName).ToArray());
        Assert.Equal("# Hello", File.ReadAllText(files.Single(f => f.EntryName == "META/README.md").FullPath));
    }

    [Fact]
    public void StringOverridesComeInWithTheirKeyOrder()
    {
        var project = Open(Import(Archive("so.fantome",
            ("META/info.json",
                "{\"Name\":\"S\",\"Layers\":{\"fx\":{\"Name\":\"fx\",\"Priority\":1,\"StringOverrides\":{\"en_us\":{\"b\":\"2\",\"a\":\"1\"},\"default\":{\"z\":\"9\"}}}}}"))));

        var overrides = project.Layers.Single().StringOverrides!;
        Assert.Equal(new[] { "en_us", "default" }, overrides.Select(p => p.Key));
        Assert.Equal(new[] { "b", "a" }, overrides["en_us"]!.AsObject().Select(p => p.Key));
    }

    // ===================================================== the source is left alone

    /// <summary>The import does not touch what it reads: the source package is read-only to it.</summary>
    [Fact]
    public void TheSourcePackageIsLeftAlone()
    {
        string archive = LayeredExport();
        byte[] before = File.ReadAllBytes(archive);
        DateTime stamp = File.GetLastWriteTimeUtc(archive);

        Import(archive);

        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(archive));
    }

    [Fact]
    public void TheProgressSaysWhatCameInAndStaysQuietForAPlainPackage()
    {
        var lines = new List<string>();
        FantomeImporter.Import(LayeredExport(), Projects, null, new HashDatabase(), new Collect(lines));
        Assert.Contains(lines, l => l == "Layers: 2 - 3 GameData module(s), 1 layer WAD(s), 0 override file(s)");
        Assert.DoesNotContain(lines, l => l.StartsWith("Not everything", StringComparison.Ordinal));

        lines.Clear();
        FantomeImporter.Import(Archive("quiet.fantome", ("RAW/a.txt", "a")), Projects, null, new HashDatabase(), new Collect(lines));
        Assert.DoesNotContain(lines, l => l.StartsWith("Layers:", StringComparison.Ordinal));
    }

    private sealed class Collect : IProgress<string>
    {
        private readonly List<string> _lines;
        public Collect(List<string> lines) => _lines = lines;
        public void Report(string value) => _lines.Add(value);
    }

    // ===================================================== reading the text back out of an archive, independently

    /// <summary>The text of <c>Layers.&lt;layer&gt;.GameData</c> exactly as info.json spells it, found with a forward-only reader
    /// and byte offsets - not with the code under test.</summary>
    internal static string GameDataTextIn(ZipArchive zip, string layer)
    {
        using var s = zip.GetEntry("META/info.json")!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return GameDataTextIn(ms.ToArray(), layer)!;
    }

    internal static string? GameDataTextIn(byte[] info, string layer)
    {
        var reader = new Utf8JsonReader(info, new JsonReaderOptions { MaxDepth = 256 });
        int depth = 0;
        string? inLayer = null;
        bool inLayers = false;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    string name = reader.GetString()!;
                    if (depth == 1 && name == "Layers") inLayers = true;
                    else if (depth == 2 && inLayers) inLayer = name;
                    else if (depth == 3 && inLayers && name == "GameData" && string.Equals(inLayer, layer, StringComparison.Ordinal))
                    {
                        reader.Read();
                        int start = (int)reader.TokenStartIndex;
                        reader.Skip();
                        return Encoding.UTF8.GetString(info, start, (int)reader.BytesConsumed - start);
                    }
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    depth++;
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    depth--;
                    if (depth == 1) inLayers = false;
                    break;
            }
        }
        return null;
    }
}

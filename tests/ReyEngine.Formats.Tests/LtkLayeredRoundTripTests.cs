using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using static ReyEngine.Formats.Tests.LayeredFantomeSupport;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816: a package written by LTK's tools is imported into a project, and Export .fantome writes it back. These run the real
/// import and the real export path of the view model over synthetic packages and check what must come back: each layer's GameData
/// text byte for byte, the WADs chunk for chunk, the override files, the string overrides, the license in the shape it had.
///
/// <para>The packages are written by hand, so their JSON is spelled the way a person or another tool spells it - compact,
/// tab-indented, CRLF-separated, with number tokens like <c>1E-30</c> and escapes like <c>é</c> - which is what a
/// verbatim copy has to survive. Crauzer's Winter Rift is checked in <see cref="CrauzerWinterRiftTests"/>.</para>
/// </summary>
public sealed class LtkLayeredRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m816-" + Guid.NewGuid().ToString("N"));

    private string Projects => Path.Combine(_root, "projects");
    private string Scratch => Path.Combine(_root, "scratch");

    public LtkLayeredRoundTripTests() => Directory.CreateDirectory(Projects);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Imports a package into a project, opens it as the app does, and exports it with the real export path.</summary>
    private (FantomeImportResult Import, ReyProject Project, string Out) RoundTrip(string source, bool declare = false)
    {
        var import = FantomeImporter.Import(source, Projects, null, new HashDatabase());
        var project = ReyProjectService.OpenFolder(import.RootPath);
        project.OutputDirectory = Path.Combine(_root, "build-" + Guid.NewGuid().ToString("N"));
        project.ShipBinEditsAsDeclarations = declare;
        var vm = new MainWindowViewModel { Project = project };
        string output = Export(vm, Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N") + ".fantome"));
        return (import, project, output);
    }

    // ===================================================== the same WAD in two layers

    private string TwoMaps()
    {
        string wads = Path.Combine(_root, "wads");
        var baseWad = PackWad(Scratch, Path.Combine(wads, "base.wad.client"),
            ("data/maps/a.bin", new byte[] { 1, 2, 3 }), ("assets/shared.tex", new byte[] { 9, 9, 9 }), ("assets/base-only.tex", new byte[] { 4 }));
        var winterWad = PackWad(Scratch, Path.Combine(wads, "winter.wad.client"),
            ("assets/shared.tex", new byte[] { 7, 7 }), ("assets/snow.tex", new byte[] { 5, 5, 5 }));
        string info = """
            {
              "Name": "Two Maps",
              "Author": "Tester",
              "Version": "1.2.3",
              "Description": "the same WAD name in two layers",
              "Layers": {
                "winter": { "Name": "winter", "DisplayName": "Winter", "Priority": 5 },
                "base": { "Name": "base", "Priority": 0 }
              },
              "Hashtables": [{"Path":"META/hashes/game.harvested.hashes.txt","Category":"game","Algorithm":"xxh64","Bits":64}],
              "Generator": "by hand"
            }
            """;
        return WriteFantome(Path.Combine(_root, "two-maps.fantome"),
            Entry("META/info.json", info),
            Table("data/maps/a.bin", "assets/shared.tex", "assets/base-only.tex", "assets/snow.tex"),
            ("WAD/Map11.wad.client", baseWad),
            ("WAD_winter/Map11.wad.client", winterWad));
    }

    [Fact]
    public void TheSameWadNameInTwoLayersIsTwoFoldersAndTwoWads()
    {
        var (import, project, _) = RoundTrip(TwoMaps());

        Assert.Equal(2, import.Wads);
        Assert.Equal(new[] { "Map11", "layers/winter/Map11" }, project.ProjectFolders);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Combine(import.RootPath, "Map11", "assets", "shared.tex")));
        Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(Path.Combine(import.RootPath, "layers", "winter", "Map11", "assets", "shared.tex")));

        // two folders with one leaf: the whole entry tells them apart, and the leaf still names the WAD
        Assert.Equal("base", project.LayerOfFolder("Map11"));
        Assert.Equal("winter", project.LayerOfFolder("layers/winter/Map11"));
        Assert.Equal("Map11", project.LeafOfFolder("layers/winter/Map11"));
        Assert.True(project.IsClaimedWhole("layers/winter/Map11"));
        Assert.False(project.IsClaimedWhole("Map11"));
    }

    [Fact]
    public void TheSameWadNameInTwoLayersLeavesAsWadMap11AndWadWinterMap11WithTheirOwnChunks()
    {
        string source = TwoMaps();
        var (_, _, output) = RoundTrip(source);

        using var src = ZipFile.OpenRead(source);
        using var re = ZipFile.OpenRead(output);
        Assert.True(Has(re, "WAD/Map11.wad.client"));
        Assert.True(Has(re, "WAD_winter/Map11.wad.client"));
        Assert.Equal(2, re.Entries.Count(e => e.FullName.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase)));

        foreach (var wad in new[] { "WAD/Map11.wad.client", "WAD_winter/Map11.wad.client" })
        {
            var want = Chunks(src, wad, Scratch);
            var got = Chunks(re, wad, Scratch);
            Assert.Equal(want.Keys.Order(), got.Keys.Order());                                    // the same chunks, compared by hash: pack order may differ
            foreach (var (hash, bytes) in want) Assert.Equal(bytes, got[hash]);                    // with the same bytes
        }
        // the one path both WADs hold keeps each WAD's own content
        Assert.Equal(new byte[] { 9, 9, 9 }, Chunks(re, "WAD/Map11.wad.client", Scratch)[Hash("assets/shared.tex")]);
        Assert.Equal(new byte[] { 7, 7 }, Chunks(re, "WAD_winter/Map11.wad.client", Scratch)[Hash("assets/shared.tex")]);

        // the layers keep their priorities and display names; the table names every packed path once
        using var info = JsonDocument.Parse(Bytes(re, "META/info.json"));
        var layers = info.RootElement.GetProperty("Layers");
        Assert.Equal(new[] { "base", "winter" }, LayerKeys(re));
        Assert.Equal(5, layers.GetProperty("winter").GetProperty("Priority").GetInt32());
        Assert.Equal("Winter", layers.GetProperty("winter").GetProperty("DisplayName").GetString());
        Assert.Equal("assets/base-only.tex\nassets/shared.tex\nassets/snow.tex\ndata/maps/a.bin\n", Text(re, FantomeHashtables.HarvestedPath));
    }

    [Fact]
    public void TheSameWadNameInTwoLayersSendsToTwoLayerFolders()
    {
        var import = FantomeImporter.Import(TwoMaps(), Projects, null, new HashDatabase());
        var project = ReyProjectService.OpenFolder(import.RootPath);

        // the files Send to LTK Manager gathers: the layer of each folder, and the WAD folder (the leaf) it ships under
        var files = new List<(string Layer, string WadFolder, string RelPath, string AbsPath)>();
        foreach (var entry in project.ProjectFolders)
        {
            string abs = project.ResolveProjectPath(entry);
            foreach (var (_, path) in WadPackService.EnumerateChunkFiles(abs))
                files.Add((project.LayerOfFolder(entry), project.LeafOfFolder(entry), Path.GetRelativePath(abs, path).Replace('\\', '/'), path));
        }
        var workshop = Path.Combine(_root, "workshop");
        Directory.CreateDirectory(workshop);
        var options = new LtkSendOptions(workshop, "two-maps", "Two Maps", "1.2.3", "d", "Tester") { Layers = LtkProjectLayers.Of(project) };
        LtkWorkshopExporter.Send(options, files);

        string mod = Path.Combine(workshop, "two-maps", "content");
        Assert.True(File.Exists(Path.Combine(mod, "base", "Map11.wad.client", "assets", "shared.tex")));
        Assert.True(File.Exists(Path.Combine(mod, "winter", "Map11.wad.client", "assets", "shared.tex")));
        Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(Path.Combine(mod, "winter", "Map11.wad.client", "assets", "shared.tex")));
    }

    // ===================================================== GameData, verbatim

    private const string Compact =
        "{\"version\":1,\"modules\":[{\"name\":\"caf\\u00e9 \\/ \\\"quoted\\\"\",\"target\":\"data/maps/a.bin\",\"edits\":[{\"Maps/A\":{\"speed\":1E-30,\"z\":-0.0,\"one\":1.0,\"big\":123456789012345678901234567890}}],"
        + "\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":0}}]}";

    private const string TabbedWithCrLf =
        "{\r\n\t\"version\": 1,\r\n\t\"modules\": [\r\n\t\t{\r\n\t\t\t\"target\": \"data/events/e.bin\",\r\n\t\t\t\"edits\": [ { \"Events/E\": { \"on\": true, \"list\": [1,2 ,3] } } ],\r\n"
        + "\t\t\t\"origin\": { \"manifest\": \"game_data.yaml\", \"source\": null, \"module\": 0 }\r\n\t\t},\r\n\t\t{ \"name\": \"second\", \"entries\": { \"Events/F\": { \"speed\": 2.50 } },\r\n\t\t  \"origin\": {\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":1} }\r\n\t]\r\n}";

    private string Verbatim()
    {
        string wad = Path.Combine(_root, "wads", "v.wad.client");
        var bytes = PackWad(Scratch, wad, ("data/maps/a.bin", new byte[] { 1, 2, 3 }), ("assets/x.tex", new byte[] { 4 }));
        string info = "{\"Name\":\"Verbatim\",\"Author\":\"T\",\"Description\":\"d\",\"Layers\":{"
            + "\"base\":{\"GameData\":" + Compact + ",\"Name\":\"base\",\"Priority\":0},"
            + "\"events\":{\"GameData\":" + TabbedWithCrLf + ",\"Name\":\"events\",\"DisplayName\":\"Events\",\"Priority\":3},"
            + "\"empty\":{\"GameData\":{\"version\":1,\"modules\":[]},\"Name\":\"empty\",\"Priority\":4}"
            + "},\"Hashtables\":" + TableManifest + "}";
        return WriteFantome(Path.Combine(_root, "verbatim.fantome"),
            Entry("META/info.json", info), Table("data/maps/a.bin", "assets/x.tex"), ("WAD/Map11.wad.client", bytes));
    }

    [Fact]
    public void EachLayersGameDataIsStoredAndWrittenBackByteForByte()
    {
        string source = Verbatim();
        var (import, project, output) = RoundTrip(source);

        using var src = ZipFile.OpenRead(source);
        using var re = ZipFile.OpenRead(output);
        foreach (var layer in new[] { "base", "events", "empty" })
        {
            string want = GameData(src, layer)!;
            // stored in the project as the text the package held ...
            Assert.Equal(want, LtkProjectStore.ReadLayer(project, layer)!.DocumentText);
            // ... and written back into the new info.json as the same text
            Assert.Equal(want, GameData(re, layer));
        }
        Assert.Equal(Compact, GameData(re, "base"));
        Assert.Equal(TabbedWithCrLf, GameData(re, "events"));

        Assert.Equal(new[] { 1, 2, 0 }, new[] { "base", "events", "empty" }.Select(l => import.Layers.Single(x => x.Name == l).GameDataModules).ToArray());
        Assert.Equal(new Dictionary<string, int> { ["base"] = 0, ["events"] = 3, ["empty"] = 4 }, project.Layers.ToDictionary(l => l.Name, l => l.Priority));
        Assert.Equal("Events", project.Layers.Single(l => l.Name == "events").DisplayName);
        Assert.Equal(LtkProjectLayers.BaseDescription, project.Layers.Single(l => l.Name == "base").Description);     // a send declares base with its usual text
        Assert.Equal("", project.Layers.Single(l => l.Name == "events").Description);
    }

    [Fact]
    public void ModuleNamesAndOriginsOfImportedModulesAreKept()
    {
        var (_, _, output) = RoundTrip(Verbatim());
        using var re = ZipFile.OpenRead(output);
        using var doc = JsonDocument.Parse(GameData(re, "base")!);
        var module = doc.RootElement.GetProperty("modules")[0];
        Assert.Equal("café / \"quoted\"", module.GetProperty("name").GetString());          // a name in a package whose export would drop it
        Assert.Equal(0, module.GetProperty("origin").GetProperty("module").GetInt32());
        Assert.Equal("game_data.yaml", module.GetProperty("origin").GetProperty("manifest").GetString());
        Assert.Contains("1E-30", GameData(re, "base"));
        Assert.Contains("-0.0", GameData(re, "base"));
        Assert.Contains("123456789012345678901234567890", GameData(re, "base"));
    }

    [Fact]
    public void AGameDataOnlyLayerNeedsNoWadAndTheExportIsNotEmpty()
    {
        string source = WriteFantome(Path.Combine(_root, "events-only.fantome"),
            Entry("META/info.json", "{\"Name\":\"Events\",\"Author\":\"T\",\"Description\":\"d\",\"Layers\":{\"events\":{\"GameData\":" + TabbedWithCrLf + ",\"Name\":\"events\",\"Priority\":1}}}"));
        var (import, project, output) = RoundTrip(source);

        Assert.Equal(0, import.Wads);
        Assert.Empty(project.ProjectFolders);
        using var re = ZipFile.OpenRead(output);
        Assert.Equal(TabbedWithCrLf, GameData(re, "events"));
        Assert.Equal(new[] { "base", "events" }, LayerKeys(re));
        Assert.DoesNotContain(re.Entries, e => e.FullName.StartsWith("WAD", StringComparison.OrdinalIgnoreCase));
    }

    // ===================================================== override files

    [Fact]
    public void OverrideFilesAreStoredAndWrittenBackByteForByte()
    {
        var ptch = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7 + 3)).ToArray();
        var other = new byte[] { 0, 255, 128, 13, 10, 13, 10, 0 };
        string source = WriteFantome(Path.Combine(_root, "ptch.fantome"),
            Entry("META/info.json", "{\"Name\":\"P\",\"Author\":\"T\",\"Description\":\"d\",\"Layers\":{\"events\":{\"GameData\":" + TabbedWithCrLf + ",\"Name\":\"events\",\"Priority\":1}}}"),
            ("META/game_data/events/data/x.ptch", ptch),
            ("META/game_data/Events/y.ptch", other),                    // the layer's folder in another casing
            Entry("META/game_data/ghost/z.ptch", "a layer nobody declared"));
        var (import, project, output) = RoundTrip(source);

        Assert.Equal(2, import.OverrideFiles);
        var data = LtkProjectStore.ReadLayer(project, "events")!;
        Assert.Equal(new[] { "data/x.ptch", "y.ptch" }, data.Files.Select(f => f.Path));
        Assert.Equal(ptch, data.ReadFile("data/x.ptch"));
        Assert.Equal(other, data.ReadFile("Y.PTCH"));
        Assert.Contains("override file META/game_data/ghost/z.ptch belongs to layer 'ghost', which the package does not declare", import.NotImportedWarning);

        using var re = ZipFile.OpenRead(output);
        Assert.Equal(ptch, Bytes(re, "META/game_data/events/data/x.ptch"));
        Assert.Equal(other, Bytes(re, "META/game_data/events/y.ptch"));
        Assert.DoesNotContain(re.Entries, e => e.FullName.Contains("ghost", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AModuleThatNamesAnOverrideFileThePackageDoesNotHoldIsSaidSo()
    {
        const string doc = "{\"version\":1,\"modules\":[{\"target\":\"data/a.bin\",\"edits\":[{\"overrides\":[\"data/there.ptch\",\"data/gone.ptch\"]}],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":0}}]}";
        string source = WriteFantome(Path.Combine(_root, "missing.fantome"),
            Entry("META/info.json", "{\"Name\":\"M\",\"Layers\":{\"events\":{\"GameData\":" + doc + ",\"Name\":\"events\",\"Priority\":1}}}"),
            ("META/game_data/events/DATA/THERE.ptch", new byte[] { 1 }));              // held, under another casing

        var import = FantomeImporter.Import(source, Projects, null, new HashDatabase());

        var note = Assert.Single(import.Notes);
        Assert.Equal("Layer 'events' declares the override file 'data/gone.ptch', which the package does not hold, so LTK Manager would refuse the layer.", note);
        Assert.Equal(1, import.OverrideFiles);
    }

    [Theory]
    [InlineData("META/game_data/events/../../escape.ptch")]
    [InlineData("META/game_data/events//x.ptch")]
    [InlineData("META/game_data/events/C:x.ptch")]
    public void AnOverrideFileThatLeavesItsLayerIsNotCarriedAndNothingIsWrittenOutside(string entry)
    {
        string source = WriteFantome(Path.Combine(_root, "escape.fantome"),
            Entry("META/info.json", "{\"Name\":\"P\",\"Layers\":{\"events\":{\"GameData\":" + Compact + ",\"Name\":\"events\",\"Priority\":1}}}"),
            Entry(entry, "x"));
        var import = FantomeImporter.Import(source, Projects, null, new HashDatabase());

        Assert.Equal(0, import.OverrideFiles);
        Assert.Contains("was not carried", import.NotImportedWarning);
        Assert.False(File.Exists(Path.Combine(Projects, "escape.ptch")));
        Assert.False(File.Exists(Path.Combine(import.RootPath, "escape.ptch")));
    }

    // ===================================================== string overrides and the license

    [Fact]
    public void StringOverridesKeepTheirKeyOrderThroughTheRoundTrip()
    {
        const string overrides = "{\"en_us\":{\"zz\":\"2\",\"aa\":\"1 \\u2013 caf\\u00e9\"},\"default\":{\"k\":\"v\"}}";
        string source = WriteFantome(Path.Combine(_root, "so.fantome"),
            Entry("META/info.json", "{\"Name\":\"S\",\"Author\":\"T\",\"Description\":\"d\",\"Layers\":{\"words\":{\"Name\":\"words\",\"Priority\":2,\"StringOverrides\":" + overrides + "}}}"));
        var (_, _, output) = RoundTrip(source);

        using var re = ZipFile.OpenRead(output);
        using var info = JsonDocument.Parse(Bytes(re, "META/info.json"));
        var words = info.RootElement.GetProperty("Layers").GetProperty("words");
        Assert.Equal(new[] { "Name", "Priority", "StringOverrides" }, words.EnumerateObject().Select(p => p.Name));     // FantomeLayerInfo's order
        var so = words.GetProperty("StringOverrides");
        Assert.Equal(new[] { "en_us", "default" }, so.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "zz", "aa" }, so.GetProperty("en_us").EnumerateObject().Select(p => p.Name));
        Assert.Equal("1 – café", so.GetProperty("en_us").GetProperty("aa").GetString());
    }

    [Fact]
    public void ALicenseGivenAsAStringExportsAsAStringAndAnObjectAsAnObject()
    {
        string asString = WriteFantome(Path.Combine(_root, "l1.fantome"),
            Entry("META/info.json", "{\"Name\":\"L\",\"Author\":\"T\",\"Description\":\"d\",\"License\":\"MIT\",\"Tags\":[\"map-skin\"],\"Champions\":[\"Ahri\",\"Lux\"],\"Maps\":[\"summoners-rift\"]}"),
            Entry("RAW/a.dds", "a"));
        var (_, _, outString) = RoundTrip(asString);
        using (var re = ZipFile.OpenRead(outString))
        using (var info = JsonDocument.Parse(Bytes(re, "META/info.json")))
        {
            Assert.Equal(JsonValueKind.String, info.RootElement.GetProperty("License").ValueKind);
            Assert.Equal("MIT", info.RootElement.GetProperty("License").GetString());
            Assert.Equal(new[] { "map-skin" }, info.RootElement.GetProperty("Tags").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(new[] { "Ahri", "Lux" }, info.RootElement.GetProperty("Champions").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(new[] { "summoners-rift" }, info.RootElement.GetProperty("Maps").EnumerateArray().Select(e => e.GetString()));
        }

        string asObject = WriteFantome(Path.Combine(_root, "l2.fantome"),
            Entry("META/info.json", "{\"Name\":\"L2\",\"Author\":\"T\",\"Description\":\"d\",\"License\":{\"Name\":\"MIT for the mod files\"}}"),
            Entry("RAW/a.dds", "a"));
        var (_, _, outObject) = RoundTrip(asObject);
        using (var re = ZipFile.OpenRead(outObject))
        using (var info = JsonDocument.Parse(Bytes(re, "META/info.json")))
        {
            var license = info.RootElement.GetProperty("License");
            Assert.Equal(JsonValueKind.Object, license.ValueKind);
            Assert.Equal(new[] { "Name" }, license.EnumerateObject().Select(p => p.Name));
            Assert.Equal("MIT for the mod files", license.GetProperty("Name").GetString());
            Assert.False(info.RootElement.TryGetProperty("Tags", out _));              // a key with nothing to say is not written
        }
    }

    [Fact]
    public void ALicenseWithALinkKeepsTheLink()
    {
        string source = WriteFantome(Path.Combine(_root, "l3.fantome"),
            Entry("META/info.json", "{\"Name\":\"L3\",\"License\":{\"Name\":\"X\",\"Url\":\"https://example.com/x\"}}"), Entry("RAW/a.dds", "a"));
        var (_, _, output) = RoundTrip(source);
        using var re = ZipFile.OpenRead(output);
        using var info = JsonDocument.Parse(Bytes(re, "META/info.json"));
        Assert.Equal("https://example.com/x", info.RootElement.GetProperty("License").GetProperty("Url").GetString());
    }

    [Fact]
    public void TheReadmeAndLicenseTextRoundTrip()
    {
        string source = WriteFantome(Path.Combine(_root, "text.fantome"),
            Entry("META/info.json", "{\"Name\":\"T\",\"Author\":\"A\",\"Description\":\"d\"}"),
            Entry("META/README.md", "# Title\r\n\r\nSome – text"), Entry("META/LICENSE", "Terms"), Entry("RAW/a.dds", "a"));
        var (_, _, output) = RoundTrip(source);
        using var re = ZipFile.OpenRead(output);
        Assert.Equal("# Title\r\n\r\nSome – text", Text(re, "META/README.md"));
        Assert.Equal("Terms", Text(re, "META/LICENSE"));
    }

    // ===================================================== an old cslol package

    [Fact]
    public void AnOldCslolFantomeImportsAndExportsAsItDid()
    {
        string source = WriteFantome(Path.Combine(_root, "cslol.fantome"),
            Entry("META/info.json", "{\"Name\":\"Old Mod\",\"Author\":\"Someone\",\"Version\":\"1.0\",\"Description\":\"cslol layout\",\"Heart\":\"https://h\",\"Home\":\"https://o\"}"),
            Entry("WAD/Ahri.wad.client/data/characters/ahri/skins/skin0.bin", "bin"),
            Entry("WAD/Ahri.wad.client/assets/a.dds", "dds"),
            Entry("RAW/extra/b.dds", "raw"));
        var (import, project, output) = RoundTrip(source);

        Assert.Equal(1, import.Wads);
        Assert.Equal(1, import.RawFiles);
        Assert.Empty(import.Layers);
        Assert.Null(import.NotImportedWarning);
        Assert.Equal(new[] { "Ahri", "RAW" }, project.ProjectFolders);
        Assert.Empty(project.Layers);
        Assert.False(Directory.Exists(LtkProjectStore.RootOf(import.RootPath)));

        using var re = ZipFile.OpenRead(output);
        Assert.Equal(new[] { "base" }, LayerKeys(re));
        Assert.True(Has(re, "WAD/Ahri.wad.client"));
        Assert.DoesNotContain(re.Entries, e => e.FullName.StartsWith("WAD_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(re.Entries, e => e.FullName.StartsWith("META/game_data", StringComparison.OrdinalIgnoreCase));
        using var info = JsonDocument.Parse(Bytes(re, "META/info.json"));
        Assert.Equal(new[] { "Name", "Author", "Version", "Description", "Heart", "Home", "Layers", "Hashtables", "Generator" },
            info.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.False(info.RootElement.GetProperty("Layers").GetProperty("base").TryGetProperty("GameData", out _));
    }

    /// <summary>The project folders of a project that predates M816, exported with the layer a leaf name claims: the layer comes from the
    /// leaf, and the staged WAD is named for the whole entry with its slashes made underscores - which is what the package then
    /// stores, as it did before. M816 changes neither for a folder no layer claims whole.</summary>
    [Fact]
    public void ALegacyNestedFolderKeepsTodaysLayerAndTodaysPackageName()
    {
        string project = Path.Combine(_root, "legacy");
        Put(Path.Combine(project, "Map11"), "assets/m.dds", new byte[] { 1 });
        Put(Path.Combine(project, "mods", "Ahri"), "assets/a.dds", new byte[] { 2 });
        Put(Path.Combine(project, "Lux"), "assets/l.dds", new byte[] { 3 });
        var p = new ReyProject
        {
            Name = "Legacy", RootPath = project, OutputDirectory = Path.Combine(_root, "legacy-build"),
            ProjectFolders = { "Map11", "mods/Ahri", "Lux" },
        };
        p.Layers.Add(new ProjectLayer { Name = "fix", Priority = 10, Folders = { "Ahri", "Lux" } });          // the leaf names, as the dialog wrote them
        var vm = new MainWindowViewModel { Project = p };

        string output = Export(vm, Path.Combine(_root, "legacy.fantome"));

        using var re = ZipFile.OpenRead(output);
        Assert.Equal(new[] { "WAD/Map11.wad.client", "WAD_fix/Lux.wad.client", "WAD_fix/mods_Ahri.wad.client" },
            re.Entries.Where(e => e.FullName.EndsWith(".wad.client")).Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "base", "fix" }, LayerKeys(re));
    }

    // ===================================================== imported modules first, ReyEngine's own after them

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static byte[] Bin(params BinTreeProperty[] props)
    {
        using var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(H("Maps/Test/Thing"), H("Thing"), props) }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static byte[] Speed(float v) => Bin(new BinTreeF32(H("speed"), v));

    private static void Put(string folder, string rel, byte[] bytes)
    {
        string path = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>A project that holds one game bin changed against the game's (so it is declared when the setting is on) and an
    /// imported GameData document for the base layer, stored as an import stores it.</summary>
    private (ReyProject Project, string ImportedText) ProjectWithImportedAndOwn(string importedText)
    {
        string project = Path.Combine(_root, "owned");
        string riot = Path.Combine(_root, "riot");
        Put(riot, "data/maps/a.bin", Speed(1f));
        string refWad = Path.Combine(_root, "RiotMap11.wad.client");
        Assert.True(WadPackService.Pack(riot, refWad).Success);
        Put(Path.Combine(project, "Map11"), "data/maps/a.bin", Speed(2f));       // changed: declared when the setting is on
        Put(Path.Combine(project, "Map11"), "assets/x.tex", new byte[] { 1, 2, 3 });

        var p = new ReyProject
        {
            Name = "Owned",
            RootPath = project,
            OutputDirectory = Path.Combine(_root, "owned-build"),
            ShipBinEditsAsDeclarations = true,
            ProjectFolders = { "Map11" },
            ReferenceWads = { refWad },
        };
        p.Layers.Add(new ProjectLayer { Name = "base", DeclarationsKey = "base" });
        LtkProjectStore.WriteDeclarations(project, "base", importedText);
        return (p, importedText);
    }

    [Fact]
    public void ImportedModulesAreWrittenFirstAndVerbatimThenReyEnginesOwnWithOriginsCountingOn()
    {
        var (project, imported) = ProjectWithImportedAndOwn(TabbedWithCrLf);
        var vm = new MainWindowViewModel { Project = project };
        string output = Export(vm, Path.Combine(_root, "owned.fantome"));

        using var re = ZipFile.OpenRead(output);
        string text = GameData(re, "base")!;
        using var doc = JsonDocument.Parse(text);
        var modules = doc.RootElement.GetProperty("modules");
        Assert.Equal(3, modules.GetArrayLength());

        // the imported modules, as their own text - the spelling of the package, tabs and CRLF inside the module included
        var parsed = GameDataDocumentText.Read(imported);
        Assert.Equal(2, parsed.Modules.Count);
        Assert.Contains(parsed.Modules[0].Text, text);
        Assert.Contains(parsed.Modules[1].Text, text);
        int second = text.IndexOf(parsed.Modules[1].Text, StringComparison.Ordinal);
        Assert.True(text.IndexOf(parsed.Modules[0].Text, StringComparison.Ordinal) < second);
        Assert.Equal(new[] { 0, 1, 2 }, modules.EnumerateArray().Select(m => m.GetProperty("origin").GetProperty("module").GetInt32()));

        // then the module this project declares, numbered after them
        var own = modules[2];
        Assert.Equal("data/maps/a.bin", own.GetProperty("target").GetString());
        Assert.Equal(new[] { "target", "edits", "origin" }, own.EnumerateObject().Select(p => p.Name));
        Assert.True(text.LastIndexOf("data/maps/a.bin", StringComparison.Ordinal) > second + parsed.Modules[1].Text.Length, "the own module comes after the imported ones");
    }

    [Fact]
    public void WithNothingOfItsOwnToDeclareTheImportedDocumentIsWrittenAsItCame()
    {
        var (project, imported) = ProjectWithImportedAndOwn(Compact);
        File.Delete(Path.Combine(project.RootPath!, "Map11", "data", "maps", "a.bin"));
        Put(Path.Combine(project.RootPath!, "Map11"), "data/maps/a.bin", Speed(1f));      // now identical to the game's: nothing to declare
        var vm = new MainWindowViewModel { Project = project };
        string output = Export(vm, Path.Combine(_root, "same.fantome"));

        using var re = ZipFile.OpenRead(output);
        Assert.Equal(imported, GameData(re, "base"));
    }

    [Fact]
    public void ImportedDeclarationsShipWhenTheSettingIsOff()
    {
        var (project, imported) = ProjectWithImportedAndOwn(Compact);
        project.ShipBinEditsAsDeclarations = false;                                       // the project's own bins ship whole
        var vm = new MainWindowViewModel { Project = project };
        string output = Export(vm, Path.Combine(_root, "off.fantome"));

        using var re = ZipFile.OpenRead(output);
        Assert.Equal(imported, GameData(re, "base"));
        Assert.True(Has(re, "WAD/Map11.wad.client"));
        Assert.Contains(Hash("data/maps/a.bin"), Chunks(re, "WAD/Map11.wad.client", Scratch).Keys);
    }

    [Fact]
    public void ALayerOfStringOverridesAloneIsAPackage()
    {
        string project = Path.Combine(_root, "words");
        Directory.CreateDirectory(project);
        var p = new ReyProject { Name = "Words", RootPath = project, OutputDirectory = Path.Combine(_root, "words-build") };
        p.Layers.Add(new ProjectLayer { Name = "words", Priority = 1, StringOverrides = JsonNode.Parse("{\"default\":{\"a\":\"b\"}}")!.AsObject() });
        var vm = new MainWindowViewModel { Project = p };

        string output = Export(vm, Path.Combine(_root, "words.fantome"));

        using var re = ZipFile.OpenRead(output);
        Assert.Equal(new[] { "base", "words" }, LayerKeys(re));
        using var info = JsonDocument.Parse(Bytes(re, "META/info.json"));
        Assert.Equal("b", info.RootElement.GetProperty("Layers").GetProperty("words").GetProperty("StringOverrides").GetProperty("default").GetProperty("a").GetString());
    }

    /// <summary>Patch Update rebases a project's bins against a new Riot patch and, when the project asks for it, rebuilds the package
    /// (<c>BuildUpdatedProjectArtifactsAsync</c>, which builds its metadata itself). A declaration is applied by LTK Manager over whatever
    /// the installed game then holds, so the imported GameData is exactly what must not be touched by the update: the rebuild writes it
    /// back as it was, and the files that hold it are as they were.</summary>
    [Fact]
    public async Task TheRebuildAfterAPatchUpdateWritesTheImportedLayersAndLeavesTheStoreAsItWas()
    {
        string source = Verbatim();
        var import = FantomeImporter.Import(source, Projects, null, new HashDatabase());
        var project = ReyProjectService.OpenFolder(import.RootPath);
        project.OutputDirectory = Path.Combine(_root, "rebuild");
        project.RiotPatchVersion = "16.20";
        project.ModLicense = new ProjectLicense { Name = "MIT", AsObject = false };
        string store = LtkProjectStore.RootOf(import.RootPath);
        var before = Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(store, f), f => File.ReadAllBytes(f));
        var vm = new MainWindowViewModel { Project = project };

        var rebuild = typeof(MainWindowViewModel).GetMethod("BuildUpdatedProjectArtifactsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        bool ok = await (Task<bool>)rebuild.Invoke(vm, new object[] { project })!;

        Assert.True(ok);
        string rebuilt = Directory.EnumerateFiles(project.OutputDirectory!, "*.fantome").Single();
        using var src = ZipFile.OpenRead(source);
        using var re = ZipFile.OpenRead(rebuilt);
        foreach (var layer in new[] { "base", "events", "empty" })
            Assert.Equal(GameData(src, layer), GameData(re, layer));
        using var info = JsonDocument.Parse(Bytes(re, "META/info.json"));
        Assert.Equal("MIT", info.RootElement.GetProperty("License").GetString());                     // the metadata is the project's, whoever builds it

        var after = Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(store, f), f => File.ReadAllBytes(f));
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
    }

    // ===================================================== review: every chunk of the package comes back, whatever its type

    /// <summary>A package whose WADs hold chunks of types the game's own WADs do not carry (<c>.json</c>, <c>.gmesh</c>, <c>.txt</c>,
    /// <c>.lua</c>) beside ones it does.</summary>
    private string WithUnusualTypes()
    {
        string wads = Path.Combine(_root, "wads");
        var baseWad = PackWad(Scratch, Path.Combine(wads, "odd-base.wad.client"),
            ("data/settings.json", Encoding.UTF8.GetBytes("{\"a\":1}")), ("assets/mesh.gmesh", new byte[] { 1, 2, 3, 4 }),
            ("notes/readme.txt", Encoding.UTF8.GetBytes("hello")), ("assets/a.tex", new byte[] { 9 }));
        var winterWad = PackWad(Scratch, Path.Combine(wads, "odd-winter.wad.client"),
            ("scripts/s.lua", Encoding.UTF8.GetBytes("return 1")), ("assets/b.tex", new byte[] { 8, 8 }));
        return WriteFantome(Path.Combine(_root, "odd-types.fantome"),
            Entry("META/info.json", "{\"Name\":\"Odd\",\"Author\":\"T\",\"Description\":\"d\",\"Layers\":{\"winter\":{\"Name\":\"winter\",\"Priority\":2}},\"Hashtables\":" + TableManifest + "}"),
            Table("data/settings.json", "assets/mesh.gmesh", "notes/readme.txt", "assets/a.tex", "scripts/s.lua", "assets/b.tex"),
            ("WAD/Map11.wad.client", baseWad), ("WAD_winter/Map11.wad.client", winterWad));
    }

    [Fact]
    public void ChunksOfTypesTheGameDoesNotCarryComeBackInTheReExport()
    {
        string source = WithUnusualTypes();
        var (import, project, output) = RoundTrip(source);

        // the project says: pack what the package held (the default packs only the types the game's WADs carry)
        Assert.False(project.PackKnownTypesOnly);
        Assert.True(new ReyProject().PackKnownTypesOnly);                                 // a project made any other way keeps the default
        Assert.Equal(6, import.ExtractedFiles);

        using var src = ZipFile.OpenRead(source);
        using var re = ZipFile.OpenRead(output);
        foreach (var wad in new[] { "WAD/Map11.wad.client", "WAD_winter/Map11.wad.client" })
        {
            var want = Chunks(src, wad, Scratch);
            var got = Chunks(re, wad, Scratch);
            Assert.Equal(want.Keys.Order(), got.Keys.Order());                            // the same chunks, none dropped
            foreach (var (hash, bytes) in want) Assert.Equal(bytes, got[hash]);
        }
        Assert.Contains(Hash("data/settings.json"), Chunks(re, "WAD/Map11.wad.client", Scratch).Keys);
        Assert.Contains(Hash("assets/mesh.gmesh"), Chunks(re, "WAD/Map11.wad.client", Scratch).Keys);
        Assert.Contains(Hash("notes/readme.txt"), Chunks(re, "WAD/Map11.wad.client", Scratch).Keys);
        Assert.Contains(Hash("scripts/s.lua"), Chunks(re, "WAD_winter/Map11.wad.client", Scratch).Keys);
    }

    [Fact]
    public void WithTheSettingOnTheseChunksAreWhatWasDroppedAndSendNeverDroppedThem()
    {
        // the control: the same project with the default setting loses them in an export, which is what an import used to hand over -
        // and a send, which does not filter, still carried them, so the two disagreed
        string source = WithUnusualTypes();
        var import = FantomeImporter.Import(source, Projects, null, new HashDatabase());
        var project = ReyProjectService.OpenFolder(import.RootPath);
        project.OutputDirectory = Path.Combine(_root, "control-build");
        project.PackKnownTypesOnly = true;
        var vm = new MainWindowViewModel { Project = project };

        string output = Export(vm, Path.Combine(_root, "control.fantome"));

        using var re = ZipFile.OpenRead(output);
        var kept = Chunks(re, "WAD/Map11.wad.client", Scratch).Keys;
        Assert.DoesNotContain(Hash("data/settings.json"), kept);
        Assert.DoesNotContain(Hash("assets/mesh.gmesh"), kept);
        Assert.DoesNotContain(Hash("notes/readme.txt"), kept);
        Assert.Contains(Hash("assets/a.tex"), kept);

        var sent = project.ProjectFolders.SelectMany(f => WadPackService.EnumerateChunkFiles(project.ResolveProjectPath(f)).Select(c => c.hash)).ToHashSet();
        Assert.Contains(Hash("data/settings.json"), sent);
        Assert.Contains(Hash("scripts/s.lua"), sent);
    }

    [Fact]
    public void AnOldCslolPackageKeepsItsUnusualChunksToo()
    {
        string source = WriteFantome(Path.Combine(_root, "cslol-odd.fantome"),
            Entry("META/info.json", "{\"Name\":\"Old\",\"Author\":\"Someone\"}"),
            Entry("WAD/Ahri.wad.client/data/settings.json", "{}"),
            Entry("WAD/Ahri.wad.client/assets/a.dds", "dds"),
            Entry("RAW/notes.txt", "raw"));
        var (_, project, output) = RoundTrip(source);

        Assert.False(project.PackKnownTypesOnly);
        using var re = ZipFile.OpenRead(output);
        Assert.Contains(Hash("data/settings.json"), Chunks(re, "WAD/Ahri.wad.client", Scratch).Keys);
        Assert.Contains(Hash("notes.txt"), Chunks(re, "WAD/RAW.wad.client", Scratch).Keys);      // RAW/ is a WAD of its own in the export
    }

    // ===================================================== the project survives being saved and reopened

    [Fact]
    public void TheImportedLayersSurviveSavingAndReopeningTheProject()
    {
        var import = FantomeImporter.Import(Verbatim(), Projects, null, new HashDatabase());
        var first = ReyProjectService.OpenFolder(import.RootPath);
        ReyProjectService.Save(first, first.ProjectFilePath!);
        var second = ReyProjectService.OpenFolder(import.RootPath);

        Assert.Equal(first.Layers.Select(l => (l.Name, l.Priority, l.DisplayName, l.DeclarationsKey)),
            second.Layers.Select(l => (l.Name, l.Priority, l.DisplayName, l.DeclarationsKey)));
        Assert.Equal(new[] { "base", "events", "empty" }.Order(), LtkProjectStore.ReadLayers(second).Select(d => d.Layer).Order());
        Assert.Equal(Compact, LtkProjectStore.ReadLayer(second, "base")!.DocumentText);
    }
}

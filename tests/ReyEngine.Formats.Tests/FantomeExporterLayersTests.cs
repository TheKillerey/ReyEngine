using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReyEngine.Core.Build;
using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M814: Export .fantome writes LTK's layered layout. The rules come from league-mod @219d84a
/// (<c>ltk_fantome</c> 0.15.1, <c>ltk_mod_project</c> 0.16.2): <c>WAD/</c> for the base layer, <c>WAD_&lt;layer&gt;/</c>
/// for the others, a <c>Layers</c> table keyed by name, a harvested hashtable listed in <c>Hashtables</c>, and a
/// <c>Generator</c>. Also read the strong way, outside the suite: <c>ltk_fantome</c>'s own reader parses the
/// export, with the layers and the hashtable written here (see the M814 commit).
/// </summary>
public sealed class FantomeExporterLayersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rey-fantome-tests-" + Guid.NewGuid().ToString("N"));

    public FantomeExporterLayersTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Out => Path.Combine(_dir, "out", "mod.fantome");

    private string Wad(string name, int size = 96)
    {
        string path = Path.Combine(_dir, "wads", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size];
        new Random(name.Length).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static FantomeMeta Meta(params FantomeLayer[] layers) => new()
    {
        Name = "My Mod",
        Author = "Someone",
        Version = "1.2.3",
        Description = "A mod's description",
        Heart = "https://example.com/heart",
        Home = "https://example.com/home",
        Generator = "ReyEngine 0.5.1",
        Layers = layers,
    };

    private static JsonDocument Info(string fantome)
    {
        using var zip = ZipFile.OpenRead(fantome);
        return JsonDocument.Parse(Bytes(zip, "META/info.json"));
    }

    private static byte[] Bytes(ZipArchive zip, string name)
    {
        using var s = (zip.GetEntry(name) ?? throw new InvalidOperationException($"no entry {name}")).Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string[] Names(ZipArchive zip) => zip.Entries.Select(e => e.FullName).ToArray();

    private static string[] Keys(JsonElement obj) => obj.EnumerateObject().Select(p => p.Name).ToArray();

    // ===================================================== META/info.json

    [Fact]
    public void InfoJsonKeepsTheLegacyFieldsAndAddsLayersHashtablesAndGenerator()
    {
        FantomeExporter.Export(Meta(), new[] { new FantomeWad(Wad("Map11.wad.client"), "base", new[] { "data/a.bin" }) }, null, Out);

        using var info = Info(Out);
        Assert.Equal(new[] { "Name", "Author", "Version", "Description", "Heart", "Home", "Layers", "Hashtables", "Generator" },
            Keys(info.RootElement));
        Assert.Equal("My Mod", info.RootElement.GetProperty("Name").GetString());
        Assert.Equal("Someone", info.RootElement.GetProperty("Author").GetString());
        Assert.Equal("1.2.3", info.RootElement.GetProperty("Version").GetString());
        Assert.Equal("A mod's description", info.RootElement.GetProperty("Description").GetString());
        Assert.Equal("https://example.com/heart", info.RootElement.GetProperty("Heart").GetString());
        Assert.Equal("ReyEngine 0.5.1", info.RootElement.GetProperty("Generator").GetString());
    }

    [Fact]
    public void HeartAndHomeAreLeftOutWhenBlank()
    {
        var meta = Meta();
        meta.Heart = " ";
        meta.Home = null;
        FantomeExporter.Export(meta, Array.Empty<FantomeWad>(), null, Out);
        using var info = Info(Out);
        Assert.Equal(new[] { "Name", "Author", "Version", "Description", "Layers", "Generator" }, Keys(info.RootElement));
    }

    [Fact]
    public void ANoLayerProjectIsOneBaseLayer()
    {
        FantomeExporter.Export(Meta(), new[] { new FantomeWad(Wad("Map11.wad.client")) }, null, Out);
        using var info = Info(Out);
        var layers = info.RootElement.GetProperty("Layers");
        Assert.Equal(new[] { "base" }, Keys(layers));
        Assert.Equal(new[] { "Name", "Priority" }, Keys(layers.GetProperty("base")));
        Assert.Equal("base", layers.GetProperty("base").GetProperty("Name").GetString());
        Assert.Equal(0, layers.GetProperty("base").GetProperty("Priority").GetInt32());
    }

    [Fact]
    public void LayersListTheBaseLayerFirstThenByPriorityAndName()
    {
        var meta = Meta(
            new FantomeLayer("zeta", 5), new FantomeLayer("alpha", 5), new FantomeLayer("base", 0),
            new FantomeLayer("low", -3, "Low priority"));
        FantomeExporter.Export(meta, Array.Empty<FantomeWad>(), null, Out);

        using var info = Info(Out);
        var layers = info.RootElement.GetProperty("Layers");
        Assert.Equal(new[] { "base", "low", "alpha", "zeta" }, Keys(layers));
        foreach (var layer in layers.EnumerateObject())
            Assert.Equal(layer.Name, layer.Value.GetProperty("Name").GetString());   // key == Name
        Assert.Equal(new[] { "Name", "DisplayName", "Priority" }, Keys(layers.GetProperty("low")));
        Assert.Equal("Low priority", layers.GetProperty("low").GetProperty("DisplayName").GetString());
        Assert.Equal(-3, layers.GetProperty("low").GetProperty("Priority").GetInt32());
        Assert.Equal(5, layers.GetProperty("zeta").GetProperty("Priority").GetInt32());
    }

    /// <summary>league-mod's <c>apply_order</c> reads names as a person does: layer9 before layer10, where plain
    /// ordering puts layer10 second. The table is written in the order a reader applies it.</summary>
    [Fact]
    public void LayersOfOnePriorityAreInNaturalNameOrder()
    {
        var meta = Meta(new FantomeLayer("layer10", 1), new FantomeLayer("layer9", 1), new FantomeLayer("Layer2", 1),
            new FantomeLayer("layer09", 1), new FantomeLayer("layer1", 1));
        FantomeExporter.Export(meta, Array.Empty<FantomeWad>(), null, Out);
        using var info = Info(Out);
        // 09 and 9 spell one number, so they tie on every run and fall back to plain ordinal order; "L" precedes "l"
        Assert.Equal(new[] { "base", "Layer2", "layer1", "layer09", "layer9", "layer10" }, Keys(info.RootElement.GetProperty("Layers")));
    }

    [Theory]
    [InlineData("layer9", "layer10", -1)]
    [InlineData("layer10", "layer9", 1)]
    [InlineData("a", "a", 0)]
    [InlineData("a", "b", -1)]
    [InlineData("A", "a", -1)]
    [InlineData("x", "x1", -1)]
    [InlineData("a2b", "a10b", -1)]
    [InlineData("a007", "a7", -1)]     // the numbers tie (padding is ignored), so plain order decides: '0' < '7'
    public void NaturalCompareIsLeagueModsNaturalCmp(string a, string b, int expected) =>
        Assert.Equal(expected, FantomeLayers.NaturalCompare(a, b));

    [Fact]
    public void TheBaseLayerIsAlwaysWrittenAtPriorityZero()
    {
        // league-mod corrects a base layer of any other priority on import (normalize_table), and the packer refuses one
        FantomeExporter.Export(Meta(new FantomeLayer("base", 5)), Array.Empty<FantomeWad>(), null, Out);
        using var info = Info(Out);
        Assert.Equal(0, info.RootElement.GetProperty("Layers").GetProperty("base").GetProperty("Priority").GetInt32());
    }

    [Fact]
    public void TheBaseLayerIsAddedWhenTheTableLacksItAndAnyCasingOfItIsNormalised()
    {
        FantomeExporter.Export(Meta(new FantomeLayer("fix", 10)), Array.Empty<FantomeWad>(), null, Out);
        using (var info = Info(Out))
            Assert.Equal(new[] { "base", "fix" }, Keys(info.RootElement.GetProperty("Layers")));

        FantomeExporter.Export(Meta(new FantomeLayer("BASE", 0)), Array.Empty<FantomeWad>(), null, Out);
        using (var info = Info(Out))
            Assert.Equal(new[] { "base" }, Keys(info.RootElement.GetProperty("Layers")));
    }

    [Fact]
    public void AGameDataDocumentRidesItsLayerAndKeepsItsNumberTokens()
    {
        var doc = JsonNode.Parse("""{"version":1,"modules":[{"target":"data/a.bin","edits":[{"A/b":{"speed":1E-30,"z":-0.0}}],"origin":{"manifest":"game_data.yaml","source":null,"module":0}}]}""")!;
        var layer = new FantomeLayer("base", 0, GameData: doc);

        FantomeExporter.Export(Meta(layer), Array.Empty<FantomeWad>(), null, Out);
        FantomeExporter.Export(Meta(layer), Array.Empty<FantomeWad>(), null, Out);   // the same node twice: it is cloned, not adopted

        using var zip = ZipFile.OpenRead(Out);
        string text = Encoding.UTF8.GetString(Bytes(zip, "META/info.json"));
        Assert.Contains("\"speed\": 1E-30", text);
        Assert.Contains("\"z\": -0.0", text);
        using var info = JsonDocument.Parse(text);
        var baseLayer = info.RootElement.GetProperty("Layers").GetProperty("base");
        Assert.Equal(new[] { "GameData", "Name", "Priority" }, Keys(baseLayer));   // FantomeLayerInfo's field order
        Assert.Equal(1, baseLayer.GetProperty("GameData").GetProperty("version").GetInt32());
        Assert.Equal(new[] { "target", "edits", "origin" }, Keys(baseLayer.GetProperty("GameData").GetProperty("modules")[0]));
        Assert.Equal(JsonValueKind.Null,
            baseLayer.GetProperty("GameData").GetProperty("modules")[0].GetProperty("origin").GetProperty("source").ValueKind);
    }

    [Fact]
    public void InfoJsonIsPrettyPrintedUtf8WithLfAndNoByteOrderMark()
    {
        var meta = Meta();
        meta.Description = "caf\u00e9 \u2013 it's \"quoted\"";
        FantomeExporter.Export(meta, Array.Empty<FantomeWad>(), null, Out);

        using var zip = ZipFile.OpenRead(Out);
        var bytes = Bytes(zip, "META/info.json");
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
        string text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain('\r', text);
        Assert.StartsWith("{\n  \"Name\": \"My Mod\",\n  \"Author\": \"Someone\",\n", text);
        Assert.Contains("caf\u00e9 \u2013 it's \\\"quoted\\\"", text);   // not \u00E9: readable, and still valid JSON
        Assert.Equal(meta.Description, JsonDocument.Parse(text).RootElement.GetProperty("Description").GetString());
    }

    // ===================================================== the WAD directories

    [Fact]
    public void TheBaseLayerRidesWADAndEveryOtherLayerItsOwnDirectory()
    {
        var meta = Meta(new FantomeLayer("particle-fix", 10), new FantomeLayer("Fix_2", 20));
        var wads = new[]
        {
            new FantomeWad(Wad("Map11.wad.client"), "base"),
            new FantomeWad(Wad("Ahri.wad.client"), "particle-fix"),
            new FantomeWad(Wad("Ahri.wad.client"), "Fix_2"),   // the same file name in another layer is another entry
        };
        FantomeExporter.Export(meta, wads, null, Out);

        using var zip = ZipFile.OpenRead(Out);
        var names = Names(zip);
        Assert.Contains("WAD/Map11.wad.client", names);
        Assert.Contains("WAD_particle-fix/Ahri.wad.client", names);
        Assert.Contains("WAD_Fix_2/Ahri.wad.client", names);
        Assert.DoesNotContain("WAD/Ahri.wad.client", names);
        Assert.Equal(3, names.Count(n => n.StartsWith("WAD", StringComparison.Ordinal)));
    }

    [Fact]
    public void APackedWadIsStoredNotDeflated()
    {
        FantomeExporter.Export(Meta(), new[] { new FantomeWad(Wad("Map11.wad.client", 4096), "base") }, null, Out);
        using var zip = ZipFile.OpenRead(Out);
        var entry = zip.GetEntry("WAD/Map11.wad.client")!;
        Assert.Equal(4096, entry.Length);
        Assert.Equal(entry.Length, entry.CompressedLength);
    }

    [Fact]
    public void ALayerOfDeclarationsAloneNeedsNoWadDirectory()
    {
        var doc = JsonNode.Parse("""{"version":1,"modules":[]}""")!;
        FantomeExporter.Export(Meta(new FantomeLayer("base", 0, GameData: doc)), Array.Empty<FantomeWad>(), null, Out);
        using var zip = ZipFile.OpenRead(Out);
        Assert.DoesNotContain(Names(zip), n => n.StartsWith("WAD", StringComparison.Ordinal));
        Assert.Contains("META/info.json", Names(zip));
    }

    [Fact]
    public void TheMetadataComesBeforeTheWads()
    {
        // entries whose bytes never move sit ahead of the WADs a later repair grows (ltk_fantome writes it so)
        FantomeExporter.Export(Meta(), new[] { new FantomeWad(Wad("Map11.wad.client"), "base", new[] { "data/a.bin" }) }, new byte[] { 1, 2, 3 }, Out);
        using var zip = ZipFile.OpenRead(Out);
        var names = Names(zip).ToList();
        int firstWad = names.FindIndex(n => n.StartsWith("WAD", StringComparison.Ordinal));
        Assert.True(names.IndexOf("META/info.json") < firstWad);
        Assert.True(names.IndexOf("META/image.png") < firstWad);
        Assert.True(names.IndexOf(FantomeHashtables.HarvestedPath) < firstWad);
    }

    [Fact]
    public void TheThumbnailAndTheLegacyDetailsFileAreStillThere()
    {
        FantomeExporter.Export(Meta(), Array.Empty<FantomeWad>(), new byte[] { 9, 8, 7 }, Out);
        using var zip = ZipFile.OpenRead(Out);
        Assert.Equal(new byte[] { 9, 8, 7 }, Bytes(zip, "META/image.png"));
        // M17's cslol layer config, untouched: ltk_fantome places no META/details.json, so it ignores it
        using var details = JsonDocument.Parse(Bytes(zip, "META/details.json"));
        Assert.Equal(new[] { "Priority", "override_", "InnerPath", "Random", "Layers", "layerss" }, Keys(details.RootElement));
        Assert.Equal("WAD", details.RootElement.GetProperty("Layers")[0].GetProperty("folder_name").GetString());
    }

    // ===================================================== the harvested hashtable

    [Fact]
    public void TheHarvestedTableIsSortedUniqueAsciiAndWithoutNamelessChunks()
    {
        var names = FantomeHashtables.Harvest(new[]
        {
            "data/a.bin", "Data/B.bin", "data/a.bin",                    // case is kept; a duplicate is one name
            "0123456789abcdef.bin", "assets/x/0123456789ABCDEF.tex",    // a stem of sixteen hex digits is a nameless chunk
            "assets/caf\u00e9.tex", "back\\slash.tex", "ctl\u0001.tex", "",   // outside the table grammar
            "assets/ok.TEX", "assets/0123456789abcde.tex",                 // fifteen digits is a name
        });
        Assert.Equal(new[] { "Data/B.bin", "assets/0123456789abcde.tex", "assets/ok.TEX", "data/a.bin" }, names);
    }

    [Fact]
    public void TheTableFileIsOneNamePerLineEndedByLfWithNoBom()
    {
        var wads = new[]
        {
            new FantomeWad(Wad("Map11.wad.client"), "base", new[] { "data/b.bin", "assets/a.tex" }),
            new FantomeWad(Wad("Ahri.wad.client"), "fix", new[] { "data/b.bin", "data/ahri.bin" }),   // merged across WADs and layers
        };
        FantomeExporter.Export(Meta(new FantomeLayer("fix", 1)), wads, null, Out);

        using var zip = ZipFile.OpenRead(Out);
        var bytes = Bytes(zip, FantomeHashtables.HarvestedPath);
        Assert.Equal("assets/a.tex\ndata/ahri.bin\ndata/b.bin\n", Encoding.UTF8.GetString(bytes));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        using var info = Info(Out);
        var table = Assert.Single(info.RootElement.GetProperty("Hashtables").EnumerateArray());
        Assert.Equal(new[] { "Path", "Category", "Algorithm", "Bits" }, Keys(table));
        Assert.Equal(FantomeHashtables.HarvestedPath, table.GetProperty("Path").GetString());
        Assert.Equal("game", table.GetProperty("Category").GetString());
        Assert.Equal("xxh64", table.GetProperty("Algorithm").GetString());
        Assert.Equal(64, table.GetProperty("Bits").GetInt32());
    }

    [Fact]
    public void NoNamesMeansNoTableAndNoManifestEntry()
    {
        FantomeExporter.Export(Meta(), new[] { new FantomeWad(Wad("Map11.wad.client"), "base", new[] { "0123456789abcdef.bin" }) }, null, Out);
        using var zip = ZipFile.OpenRead(Out);
        Assert.DoesNotContain(Names(zip), n => n.StartsWith("META/hashes/", StringComparison.Ordinal));
        using var info = Info(Out);
        Assert.False(info.RootElement.TryGetProperty("Hashtables", out _));
    }

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("0123456789abcdef.bin", true)]
    [InlineData("0123456789ABCDEF.tex", true)]
    [InlineData("assets/0123456789abcdef.tex", true)]       // ltk_wad reads the file stem wherever it sits
    [InlineData("0123456789abcdeg.bin", false)]
    [InlineData("0123456789abcde.bin", false)]
    [InlineData("0123456789abcdef0.bin", false)]
    [InlineData("assets/thing.bin", false)]
    [InlineData("0123456789abcdef/thing.bin", false)]
    public void ANamelessChunkIsAFileStemOfSixteenHexDigits(string path, bool hex) =>
        Assert.Equal(hex, FantomeHashtables.IsHexChunkPath(path));

    // ===================================================== what is refused, before the file is touched

    [Theory]
    [InlineData("my layer")]
    [InlineData("caf\u00e9")]
    [InlineData("a/b")]
    [InlineData("a.b")]
    [InlineData("")]
    [InlineData("base")]
    [InlineData("Base")]
    public void ALayerNameTheFormatCannotCarryIsRefusedAndNothingIsWritten(string name)
    {
        // "base" in any casing is the base layer, never another layer; the rest cannot name WAD_<layer>/
        var layers = name.Equals("base", StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<FantomeLayer>()
            : new[] { new FantomeLayer(name, 1) };
        if (layers.Length == 0)
        {
            Assert.NotNull(FantomeLayers.NameProblem(name));
            return;
        }
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FantomeExporter.Export(Meta(layers), Array.Empty<FantomeWad>(), null, Out));
        Assert.False(File.Exists(Out));
        Assert.Contains("layer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWadInALayerTheModDoesNotDeclareIsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FantomeExporter.Export(Meta(), new[] { new FantomeWad(Wad("Ahri.wad.client"), "ghost") }, null, Out));
        Assert.Contains("ghost", ex.Message);
        Assert.False(File.Exists(Out));
    }

    [Fact]
    public void TwoWadsOfOneNameInOneLayerAreRefused()
    {
        string a = Wad("Map11.wad.client");
        string b = Path.Combine(_dir, "other", "Map11.wad.client");
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.Copy(a, b);
        Assert.Throws<InvalidOperationException>(() =>
            FantomeExporter.Export(Meta(), new[] { new FantomeWad(a, "base"), new FantomeWad(b, "base") }, null, Out));
        Assert.False(File.Exists(Out));
    }

    [Fact]
    public void TwoLayersOfOneNameIgnoringCaseAreRefused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FantomeExporter.Export(Meta(new FantomeLayer("Fix", 1), new FantomeLayer("fix", 2)), Array.Empty<FantomeWad>(), null, Out));
    }

    [Theory]
    [InlineData("particle-fix", null)]
    [InlineData("Fix_2", null)]
    [InlineData("a", null)]
    [InlineData("x1-y_2", null)]
    [InlineData("", "needs a name")]
    [InlineData("  ", "needs a name")]
    [InlineData("base", "base layer")]
    [InlineData("BASE", "base layer")]
    [InlineData("my layer", "ASCII letters, digits")]
    [InlineData("caf\u00e9", "ASCII letters, digits")]
    // review: a layer is a folder, and a name from a package can be a path
    [InlineData("..", "ASCII letters, digits")]
    [InlineData("..\\..", "ASCII letters, digits")]
    [InlineData("C:\\Temp\\x", "ASCII letters, digits")]
    [InlineData("\\\\server\\share", "ASCII letters, digits")]
    [InlineData("a/b", "ASCII letters, digits")]
    [InlineData("con", "Windows device name")]
    [InlineData("CON", "Windows device name")]
    [InlineData("Nul", "Windows device name")]
    [InlineData("aux", "Windows device name")]
    [InlineData("prn", "Windows device name")]
    [InlineData("com1", "Windows device name")]
    [InlineData("COM0", "Windows device name")]
    [InlineData("LPT9", "Windows device name")]
    [InlineData("console", null)]
    [InlineData("com10", null)]
    [InlineData("aux1", null)]
    [InlineData("lpt", null)]
    [InlineData("con_", null)]
    public void LayerNameProblems(string name, string? problem)
    {
        var found = FantomeLayers.NameProblem(name);
        if (problem is null) Assert.Null(found);
        else Assert.Contains(problem, found);
    }

    [Fact]
    public void ALayerNameIsAtMostAHundredCharactersAFolderNameThatMustStillFitInAPath()
    {
        Assert.Null(FantomeLayers.NameProblem(new string('a', FantomeLayers.MaxLayerNameLength)));
        var problem = FantomeLayers.NameProblem(new string('a', FantomeLayers.MaxLayerNameLength + 1));
        Assert.Contains("at most 100 characters", problem);
        Assert.True(problem!.Length < 200, "the message does not repeat the name");
    }

    // ===================================================== the layer table a project declares

    private static ReyProject Project(params ProjectLayer[] layers)
    {
        var p = new ReyProject { Name = "P", RootPath = @"C:\P" };
        foreach (var l in layers) p.Layers.Add(l);
        return p;
    }

    [Fact]
    public void AProjectWithNoLayersDeclaresBaseAlone()
    {
        var table = LtkProjectLayers.ForFantome(Project());
        var only = Assert.Single(table);
        Assert.Equal("base", only.Name);
        Assert.Equal(0, only.Priority);
    }

    /// <summary>The fantome table is Send to LTK Manager's table: the same layers at the same priorities.</summary>
    [Fact]
    public void TheTableIsTheOneSendToLtkManagerDeclares()
    {
        var project = Project(
            new ProjectLayer { Name = "particle-fix", Priority = 10, Folders = { "Ahri" } },
            new ProjectLayer { Name = "extras", Priority = 5 },
            new ProjectLayer { Name = "empty", Priority = 7 });   // a layer with no folders is still declared, as a send declares it

        var send = LtkProjectLayers.Of(project).OrderBy(l => l.Name == "base" ? 0 : 1).ThenBy(l => l.Priority).ToList();
        var table = LtkProjectLayers.ForFantome(project);

        Assert.Equal(send.Select(l => (l.Name, l.Priority)), table.Select(l => (l.Name, l.Priority)));
        Assert.Equal(new[] { "base", "extras", "empty", "particle-fix" }, table.Select(l => l.Name).ToArray());
    }

    [Fact]
    public void ALayerIsAssignedByTheSameRuleAsASend()
    {
        var project = Project(new ProjectLayer { Name = "fix", Priority = 1, Folders = { "Ahri" } });
        Assert.Equal("fix", project.LayerOf("Ahri"));
        Assert.Equal("fix", project.LayerOf("ahri"));      // a folder name compares without regard to case
        Assert.Equal("base", project.LayerOf("Map11"));    // and a folder no layer names is base
    }

    [Fact]
    public void DeclarationsAreAttachedToTheirLayerByNameWithoutRegardToCase()
    {
        var doc = JsonNode.Parse("""{"version":1,"modules":[]}""")!;
        var table = LtkProjectLayers.ForFantome(
            Project(new ProjectLayer { Name = "Fix", Priority = 1 }),
            new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase) { ["fix"] = doc });
        Assert.Null(table.Single(l => l.Name == "base").GameData);
        Assert.Same(doc, table.Single(l => l.Name == "Fix").GameData);
    }

    [Fact]
    public void DeclarationsForALayerTheProjectLacksAreRefused()
    {
        var doc = JsonNode.Parse("""{"version":1,"modules":[]}""")!;
        Assert.Throws<InvalidOperationException>(() => LtkProjectLayers.ForFantome(Project(),
            new Dictionary<string, JsonNode> { ["ghost"] = doc }));
    }

    [Theory]
    [InlineData("my layer")]
    [InlineData("BASE")]
    [InlineData("caf\u00e9")]
    public void AProjectLayerTheFormatCannotCarryStopsTheExportWithAWayOut(string name)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LtkProjectLayers.ForFantome(Project(new ProjectLayer { Name = name })));
        Assert.Contains("Project Settings", ex.Message);
    }

    [Fact]
    public void AnUnnamedLayerIsLeftOutAsASendLeavesItOut()
    {
        var table = LtkProjectLayers.ForFantome(Project(new ProjectLayer { Name = "  ", Priority = 3 }));
        Assert.Equal("base", Assert.Single(table).Name);
    }

    // ===================================================== re-declaring base (M814 review)

    /// <summary><see cref="LtkProjectLayers.Of"/> lets a project re-declare its base layer - to give it a priority or a
    /// description - and a send accepts that (<c>LtkWorkshopExporterTests.Redeclaring_base_replaces_it_rather_than_duplicating_it</c>).
    /// The export agrees: exactly "base" is the base layer, not an error.</summary>
    [Fact]
    public void AProjectThatRedeclaresBaseExactlyExportsAsASendDoes()
    {
        var project = Project(new ProjectLayer { Name = "base", Priority = 5, Description = "The map itself", Folders = { "Map11" } });

        var table = LtkProjectLayers.ForFantome(project);

        var only = Assert.Single(table);
        Assert.Equal("base", only.Name);
        Assert.Equal(0, only.Priority);                 // base applies first whatever it was given (ModProjectLayer::normalize_table)
        Assert.Equal("base", project.LayerOf("Map11"));
        Assert.Equal(LtkProjectLayers.Of(project).Select(l => l.Name), table.Select(l => l.Name));   // the table a send declares
    }

    [Fact]
    public void ARedeclaredBaseAndAnotherLayerAreTabledBaseFirst()
    {
        var project = Project(
            new ProjectLayer { Name = "fix", Priority = 1 },
            new ProjectLayer { Name = "base", Priority = 99 });

        var table = LtkProjectLayers.ForFantome(project);

        Assert.Equal(new[] { "base", "fix" }, table.Select(l => l.Name).ToArray());
        Assert.Equal(new[] { 0, 1 }, table.Select(l => l.Priority).ToArray());
    }

    [Theory]
    [InlineData("BASE")]
    [InlineData("Base")]
    [InlineData("bAsE")]
    public void AnotherCasingOfBaseIsStillRefusedWithAWayOut(string name)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LtkProjectLayers.ForFantome(Project(new ProjectLayer { Name = name })));
        Assert.Contains("base layer", ex.Message);
        Assert.Contains("Project Settings", ex.Message);
    }

    [Fact]
    public void BaseAndAnotherCasingOfItAreRefusedForTheOtherCasing()
    {
        Assert.Throws<InvalidOperationException>(() => LtkProjectLayers.ForFantome(Project(
            new ProjectLayer { Name = "base", Priority = 1 },
            new ProjectLayer { Name = "BASE", Priority = 2 })));
    }

    /// <summary>The same exact-"base" table reaches the archive: one base layer, written under the name the format knows.</summary>
    [Fact]
    public void ARedeclaredBaseReachesTheArchiveAsTheOneBaseLayerWithPriorityZero()
    {
        var table = LtkProjectLayers.ForFantome(Project(new ProjectLayer { Name = "base", Priority = 7 }));
        var wadPath = Wad("Map11.wad.client");

        FantomeExporter.Export(Meta(table.ToArray()), new[] { new FantomeWad(wadPath) }, null, Out);

        using var info = Info(Out);
        var layers = info.RootElement.GetProperty("Layers");
        Assert.Equal(new[] { "base" }, Keys(layers));
        Assert.Equal(0, layers.GetProperty("base").GetProperty("Priority").GetInt32());
        using var zip = ZipFile.OpenRead(Out);
        Assert.Contains("WAD/Map11.wad.client", Names(zip));
    }

    // ===================================================== all or nothing (M814 review)

    /// <summary>Every file beside the output, by name - the output, and any temporary file a failed export left.</summary>
    private string[] FilesBeside(string output) =>
        Directory.GetFiles(Path.GetDirectoryName(output)!).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();

    /// <summary>A WAD another program holds open exclusively: reading it to store it fails after the archive has begun.</summary>
    private FileStream Lock(string wad) => new(wad, FileMode.Open, FileAccess.Read, FileShare.None);

    [Fact]
    public void AnExportThatFailsWhileWritingLeavesAnExistingPackageAsItWas()
    {
        FantomeExporter.Export(Meta(), new[] { new FantomeWad(Wad("a.wad.client")) }, null, Out);
        byte[] good = File.ReadAllBytes(Out);

        string locked = Wad("locked.wad.client");
        using var hold = Lock(locked);
        // info.json is written before the WADs, so a half-written archive existed when this fails
        Assert.ThrowsAny<IOException>(() => FantomeExporter.Export(
            Meta(new FantomeLayer("base", 0), new FantomeLayer("fix", 5)),
            new[] { new FantomeWad(Wad("a.wad.client")), new FantomeWad(locked, "fix") }, null, Out));

        Assert.Equal(good, File.ReadAllBytes(Out));                    // not deleted, not replaced, not truncated
        Assert.Equal(new[] { "mod.fantome" }, FilesBeside(Out));       // and no temporary file
    }

    [Fact]
    public void AnExportThatFailsWhileWritingLeavesNoPackageWhereThereWasNone()
    {
        string locked = Wad("locked.wad.client");
        using var hold = Lock(locked);

        Assert.ThrowsAny<IOException>(() => FantomeExporter.Export(Meta(), new[] { new FantomeWad(locked) }, null, Out));

        Assert.False(File.Exists(Out));
        Assert.Empty(FilesBeside(Out));
    }

    [Fact]
    public void ARefusedExportNeverTouchesAnExistingPackage()
    {
        FantomeExporter.Export(Meta(), new[] { new FantomeWad(Wad("a.wad.client")) }, null, Out);
        byte[] good = File.ReadAllBytes(Out);

        Assert.Throws<InvalidOperationException>(() => FantomeExporter.Export(
            Meta(new FantomeLayer("my layer", 1)), new[] { new FantomeWad(Wad("a.wad.client")) }, null, Out));
        Assert.Throws<InvalidOperationException>(() => FantomeExporter.Export(
            Meta(), new[] { new FantomeWad(Wad("a.wad.client"), "ghost") }, null, Out));          // a layer the mod does not declare
        Assert.Throws<InvalidOperationException>(() => FantomeExporter.Export(
            Meta(), new[] { new FantomeWad(Wad("a.wad.client")), new FantomeWad(Wad("a.wad.client")) }, null, Out));   // one name twice

        Assert.Equal(good, File.ReadAllBytes(Out));
        Assert.Equal(new[] { "mod.fantome" }, FilesBeside(Out));
    }

    /// <summary>The last step is the move over the old package. When that fails - another program has the old package
    /// open, say - the old package is still what it was and the finished temporary archive is removed.</summary>
    [Fact]
    public void AnExistingPackageThatCannotBeReplacedIsLeftAsItWasAndTheTemporaryFileIsRemoved()
    {
        FantomeExporter.Export(Meta(), new[] { new FantomeWad(Wad("a.wad.client")) }, null, Out);
        byte[] good = File.ReadAllBytes(Out);

        using (var open = new FileStream(Out, FileMode.Open, FileAccess.Read, FileShare.Read))      // no FileShare.Delete: not replaceable
        {
            var ex = Assert.ThrowsAny<Exception>(() => FantomeExporter.Export(
                Meta(new FantomeLayer("fix", 3)), new[] { new FantomeWad(Wad("b.wad.client"), "fix") }, null, Out));
            Assert.True(ex is IOException or UnauthorizedAccessException, ex.GetType().Name);
        }

        Assert.Equal(good, File.ReadAllBytes(Out));
        Assert.Equal(new[] { "mod.fantome" }, FilesBeside(Out));
    }

    [Fact]
    public void ASuccessfulExportReplacesAnExistingPackageAndLeavesNoTemporaryFile()
    {
        FantomeExporter.Export(Meta(), new[] { new FantomeWad(Wad("a.wad.client")) }, null, Out);
        byte[] first = File.ReadAllBytes(Out);

        var second = Meta(new FantomeLayer("fix", 3));
        second.Name = "Second";
        FantomeExporter.Export(second, new[] { new FantomeWad(Wad("b.wad.client"), "fix") }, null, Out);

        Assert.NotEqual(first, File.ReadAllBytes(Out));
        using (var info = Info(Out))
        {
            Assert.Equal("Second", info.RootElement.GetProperty("Name").GetString());
            Assert.Equal(new[] { "base", "fix" }, Keys(info.RootElement.GetProperty("Layers")));
        }
        using (var zip = ZipFile.OpenRead(Out))
            Assert.Equal(new[] { "META/details.json", "META/info.json", "WAD_fix/b.wad.client" }, Names(zip).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "mod.fantome" }, FilesBeside(Out));
    }

    /// <summary>A temporary file a killed export left behind is the export's own name: the next export replaces it
    /// rather than failing on it or trusting it.</summary>
    [Fact]
    public void ATemporaryFileLeftByAKilledExportIsReplacedNotTrusted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Out)!);
        File.WriteAllText(Out + ".tmp", "half of an archive");

        FantomeExporter.Export(Meta(), new[] { new FantomeWad(Wad("a.wad.client")) }, null, Out);

        using (var zip = ZipFile.OpenRead(Out)) Assert.Contains("WAD/a.wad.client", Names(zip));
        Assert.Equal(new[] { "mod.fantome" }, FilesBeside(Out));
    }

    [Fact]
    public void TheOutputFolderIsMadeForAFirstExport()
    {
        string deep = Path.Combine(_dir, "a", "b", "c", "mod.fantome");

        FantomeExporter.Export(Meta(), new[] { new FantomeWad(Wad("a.wad.client")) }, null, deep);

        Assert.True(File.Exists(deep));
        Assert.Equal(new[] { "mod.fantome" }, FilesBeside(deep));
    }
}

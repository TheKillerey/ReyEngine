using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M850: a .fantome layer's declarations are the YAML entry <c>META/game_data/&lt;layer&gt;/game_data.yaml</c> (league-mod PR #318,
/// <c>ltk_fantome</c> 0.18.0). Export writes it; import reads it before the <c>GameData</c> field of <c>META/info.json</c>, which stays the
/// fallback for every earlier archive; an override file may not take the entry's name.
/// </summary>
public sealed class FantomeManifestYamlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m850-" + Guid.NewGuid().ToString("N"));

    private string Projects => Path.Combine(_root, "projects");

    public FantomeManifestYamlTests() => Directory.CreateDirectory(Projects);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static JsonNode Doc(params string[] targets) => JsonNode.Parse(
        "{\"version\":1,\"modules\":[" + string.Join(",", targets.Select((t, i) =>
            $"{{\"target\":\"{t}\",\"edits\":[{{\"A/b\":{{\"speed\":2}}}}],\"origin\":{{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":{i}}}}}")) + "]}")!;

    private string Out(string name = "mod.fantome") => Path.Combine(_root, "out", name);

    private static FantomeMeta Meta(bool field, params FantomeLayer[] layers) => new()
    {
        Name = "Yaml Mod", Author = "T", Version = "1.0.0", Layers = layers, GameDataInInfoJson = field,
    };

    private string Export(FantomeMeta meta, string? name = null)
    {
        string path = Out(name ?? "mod.fantome");
        FantomeExporter.Export(meta, Array.Empty<FantomeWad>(), null, path);
        return path;
    }

    private static string Entry(ZipArchive zip, string name)
    {
        var e = zip.Entries.FirstOrDefault(x => x.FullName == name);
        Assert.NotNull(e);
        using var r = new StreamReader(e!.Open(), new UTF8Encoding(false));
        return r.ReadToEnd();
    }

    private string Archive(string name, params (string Entry, string Text)[] entries)
    {
        string path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries)
        {
            var e = zip.CreateEntry(entry);
            using var s = e.Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
        return path;
    }

    private FantomeImportResult Import(string archive) => FantomeImporter.Import(archive, Projects, null, new HashDatabase());
    private static ReyProject Open(FantomeImportResult r) => ReyProjectService.OpenFolder(r.RootPath);

    private static string[] Targets(string documentText) =>
        GameDataDocumentText.Read(documentText).Modules.Select(m => m.Target ?? "").ToArray();

    // ===================================================== export

    [Fact]
    public void ExportWritesEachLayersDeclarationsAsAYamlEntryThatReadsBackToTheDocument()
    {
        var baseDoc = Doc("data/maps/a.bin");
        var fixDoc = Doc("data/x/skin0.bin", "data/x/skin1.bin");
        string path = Export(Meta(true, new FantomeLayer("base", 0, GameData: baseDoc), new FantomeLayer("fix", 10, GameData: fixDoc), new FantomeLayer("plain", 5)));

        using var zip = ZipFile.OpenRead(path);
        string baseYaml = Entry(zip, "META/game_data/base/game_data.yaml");
        string fixYaml = Entry(zip, "META/game_data/fix/game_data.yaml");
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("META/game_data/plain/", StringComparison.Ordinal));

        Assert.StartsWith("version: 1\nmodules:\n  - {\"target\":\"data/maps/a.bin\"", baseYaml);
        Assert.DoesNotContain("origin", baseYaml);
        // read back, the YAML is the document the layer was written from
        Assert.Equal(baseDoc.ToJsonString(), JsonNode.Parse(GameDataManifestYaml.ToDocumentJson(baseYaml))!.ToJsonString());
        Assert.Equal(fixDoc.ToJsonString(), JsonNode.Parse(GameDataManifestYaml.ToDocumentJson(fixYaml))!.ToJsonString());
    }

    [Fact]
    public void ByDefaultTheGameDataFieldIsKeptBesideTheEntryForManagersThatReadOnlyTheField()
    {
        Assert.True(FantomeLayers.GameDataInInfoJson);
        string path = Export(new FantomeMeta { Name = "n", Author = "a", Layers = new[] { new FantomeLayer("base", 0, GameData: Doc("data/a.bin")) } });

        using var zip = ZipFile.OpenRead(path);
        using var info = JsonDocument.Parse(Entry(zip, "META/info.json"));
        Assert.Equal(JsonValueKind.Object, info.RootElement.GetProperty("Layers").GetProperty("base").GetProperty("GameData").ValueKind);
        Assert.NotNull(zip.GetEntry("META/game_data/base/game_data.yaml"));
    }

    [Fact]
    public void WithTheFieldSwitchedOffInfoJsonHoldsNoGameDataAndTheEntryCarriesTheLayer()
    {
        string path = Export(Meta(false, new FantomeLayer("base", 0, GameData: Doc("data/a.bin")), new FantomeLayer("fix", 3, GameData: Doc("data/b.bin"))));

        using var zip = ZipFile.OpenRead(path);
        string infoText = Entry(zip, "META/info.json");
        Assert.DoesNotContain("GameData", infoText);
        using var info = JsonDocument.Parse(infoText);
        Assert.False(info.RootElement.GetProperty("Layers").GetProperty("fix").TryGetProperty("GameData", out _));
        Assert.NotNull(zip.GetEntry("META/game_data/base/game_data.yaml"));
        Assert.NotNull(zip.GetEntry("META/game_data/fix/game_data.yaml"));
    }

    [Fact]
    public void AnOverrideFileWithTheNameOfTheManifestIsRefusedAndNothingIsWritten()
    {
        string file = Path.Combine(_root, "files", "x.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        foreach (string clash in new[] { "game_data.yaml", "Game_Data.YAML" })
        {
            var layer = new FantomeLayer("base", 0, GameData: Doc("data/a.bin")) { OverrideFiles = new[] { new FantomeOverrideFile(clash, file) } };
            string path = Out("clash.fantome");
            var ex = Assert.Throws<InvalidOperationException>(() => FantomeExporter.Export(Meta(false, layer), Array.Empty<FantomeWad>(), null, path));
            Assert.Contains("game_data.yaml", ex.Message);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
    }

    [Fact]
    public void ADocumentOfAShapeNoManifestHoldsStaysInTheFieldEvenWhenTheFieldIsOff()
    {
        var odd = JsonNode.Parse("""{"version":1,"modules":[],"extra":true}""")!;
        string path = Export(Meta(false, new FantomeLayer("base", 0, GameData: odd)));

        using var zip = ZipFile.OpenRead(path);
        Assert.Contains("\"extra\"", Entry(zip, "META/info.json"));
        Assert.Null(zip.GetEntry("META/game_data/base/game_data.yaml"));
    }

    [Fact]
    public void AnEmptyModuleListIsWrittenAsAnExplicitList()
    {
        Assert.Equal("version: 1\nmodules: []\n", GameDataManifestYaml.FromDocument("""{"version":1,"modules":[]}"""));
        Assert.Null(GameDataManifestYaml.FromDocument("""{"version":1,"modules":[],"extra":1}"""));
    }

    // ===================================================== import

    private const string BlockYaml = """
        version: 1
        modules:
        - target: data/new/a.bin
          edits:
          - links:
            - Shared/One
            -links:
            - "y"
        """;

    private static string InfoJson(string? gameData) =>
        "{\"Name\":\"Hand\",\"Author\":\"T\",\"Version\":\"1\",\"Description\":\"\",\"Layers\":{\"base\":{"
        + (gameData is null ? "" : "\"GameData\":" + gameData + ",") + "\"Name\":\"base\",\"Priority\":0}}}";

    [Fact]
    public void ANewStyleArchiveImportsItsYamlEntryAndTheDocumentIsStoredWithOrigins()
    {
        string archive = Archive("new.fantome", ("META/info.json", InfoJson(null)), ("META/game_data/base/game_data.yaml", BlockYaml));
        var result = Import(archive);

        Assert.Equal(1, result.GameDataModules);
        Assert.DoesNotContain(result.Notes, n => n.Contains("game_data"));
        var layer = LtkProjectStore.ReadLayer(Open(result), "base")!;
        Assert.Equal(new[] { "data/new/a.bin" }, Targets(layer.DocumentText));
        Assert.Equal("""{"version":1,"modules":[{"target":"data/new/a.bin","edits":[{"links":["Shared/One"],"-links":["y"]}],"origin":{"manifest":"game_data.yaml","source":null,"module":0}}]}""",
            layer.DocumentText);
        // the entry is not an override file
        Assert.Equal(0, result.OverrideFiles);
    }

    [Fact]
    public void AnOldStyleArchiveWithOnlyTheFieldStillImports()
    {
        string field = Doc("data/old/a.bin").ToJsonString();
        var result = Import(Archive("old.fantome", ("META/info.json", InfoJson(field))));

        Assert.Equal(1, result.GameDataModules);
        Assert.Equal(new[] { "data/old/a.bin" }, Targets(LtkProjectStore.ReadLayer(Open(result), "base")!.DocumentText));
    }

    [Fact]
    public void WhenBothArePresentTheYamlEntryWins()
    {
        string field = Doc("data/old/a.bin").ToJsonString();
        var result = Import(Archive("both.fantome", ("META/info.json", InfoJson(field)), ("META/game_data/base/game_data.yaml", BlockYaml)));

        Assert.Equal(new[] { "data/new/a.bin" }, Targets(LtkProjectStore.ReadLayer(Open(result), "base")!.DocumentText));
    }

    [Fact]
    public void AnUnreadableEntryIsANoteAndTheFieldIsUsedInstead()
    {
        string field = Doc("data/old/a.bin").ToJsonString();
        var result = Import(Archive("bad.fantome", ("META/info.json", InfoJson(field)), ("META/game_data/base/game_data.yaml", "version: 1\nmodules:\n- &a {target: x}\n")));

        Assert.Contains(result.Notes, n => n.Contains("game_data.yaml") && n.Contains("GameData field"));
        Assert.Equal(new[] { "data/old/a.bin" }, Targets(LtkProjectStore.ReadLayer(Open(result), "base")!.DocumentText));
    }

    [Fact]
    public void AnEntryOfALayerTheTableDoesNotListAddsTheLayerAndAnOverrideFileBesideItIsStillCarried()
    {
        string yaml = "version: 1\nmodules:\n- target: data/z.bin\n  edits:\n  - overrides:\n    - z.ptch\n    links: []\n";
        var result = Import(Archive("extra.fantome", ("META/info.json", InfoJson(null)),
            ("META/game_data/late/game_data.yaml", yaml), ("META/game_data/late/z.ptch", "patch")));

        Assert.Contains(result.Layers, l => l.Name == "late" && l.GameDataModules == 1 && l.OverrideFiles == 1);
        Assert.Equal(1, result.OverrideFiles);
    }

    // ===================================================== round trip

    [Fact]
    public void ImportThenExportGivesTheDeclarationsBackWithAndWithoutTheField()
    {
        var source = Doc("data/maps/a.bin", "data/maps/b.bin");
        foreach (bool field in new[] { true, false })
        {
            string first = Export(Meta(field, new FantomeLayer("base", 0, GameData: source)), $"first-{field}.fantome");
            var imported = Import(first);
            var project = Open(imported);
            string stored = LtkProjectStore.ReadLayer(project, "base")!.DocumentText;
            Assert.Equal(source.ToJsonString(), JsonNode.Parse(stored)!.ToJsonString());

            string second = Export(Meta(field, new FantomeLayer("base", 0) { ImportedGameData = stored }), $"second-{field}.fantome");
            using var a = ZipFile.OpenRead(first);
            using var b = ZipFile.OpenRead(second);
            Assert.Equal(Entry(a, "META/game_data/base/game_data.yaml"), Entry(b, "META/game_data/base/game_data.yaml"));
            // the field (when written) holds the same document, laid out as the pretty-printed export or the stored compact text
            Assert.Equal(JsonNode.Parse(Entry(a, "META/info.json"))!.ToJsonString(), JsonNode.Parse(Entry(b, "META/info.json"))!.ToJsonString());
            Directory.Delete(imported.RootPath, recursive: true);
        }
    }

    [Fact]
    public void ALeagueModBlockManifestReadsToTheSameDeclarationsAsAnExportOfThem()
    {
        // league-mod's own vector (ltk_game_data tests/declarations.rs, a_written_manifest_loads_to_the_same_declarations): the text manifest_yaml() writes
        const string leagueMod = "version: 1\nmodules:\n- target: shared\n  edits:\n  - overrides:\n    - a.ptch\n    links:\n    - x\n    -links:\n    - \"y\"\n  - links: []\n"
            + "- entries:\n    Characters/A:\n      links:\n      - s\n    \"0x0000abcd\":\n      links: []\n      -links:\n      - t\n"
            + "- target: 0123456789ABCDEF\n  edits:\n  - overrides:\n    - b.ptch\n    links: []\n";

        string document = GameDataManifestYaml.ToDocumentJson(leagueMod);
        using var parsed = JsonDocument.Parse(document);
        var modules = parsed.RootElement.GetProperty("modules");
        Assert.Equal("""[{"overrides":["a.ptch"],"links":["x"],"-links":["y"]},{"links":[]}]""", modules[0].GetProperty("edits").GetRawText());
        Assert.Equal("""{"Characters/A":{"links":["s"]},"0x0000abcd":{"links":[],"-links":["t"]}}""", modules[1].GetProperty("entries").GetRawText());
        Assert.Equal("0123456789ABCDEF", modules[2].GetProperty("target").GetString());   // a string, not a number
        Assert.Equal(new[] { "a.ptch", "b.ptch" }, GameDataDocumentText.Read(document).OverridePaths());

        // ReyEngine's text for the same declarations reads to the same document
        string ours = GameDataManifestYaml.FromDocument(document)!;
        Assert.Equal(document, GameDataManifestYaml.ToDocumentJson(ours));
    }

    [Fact]
    public void TheAwkwardManifestOfLeagueModsTestsReadsWithEveryTypeKept()
    {
        // ltk_game_data tests/declarations.rs, AWKWARD_MANIFEST
        const string yaml = """

            version: 1
            modules:
              - name: '123'
                target: '0123456789012345'
                edits:
                  - links: ['1e3', 'null', '~', 'true', '0x12345678', 'a: b', 'c #d', '  padded  ']
                    -links: ['-1']
                    '0x12345678':
                      count: 18446744073709551615
                      offset: -1
                      ratio: 1.0
                      text: 'null'
                      multiline: "first\nsecond\n"
                      long: 'one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty'
                      flag: true
                      absent: null
                      pin: !f32 1.5
                      ptr: !pointer(Particle) {'0x00000001': 1}
                      +tags: ['1', '2']
              - target: '0123456789ABCDEF'
                links: []
              - target: '1234'
                links: []
              - target: '12345678901234e5'
                links: []
              - target: data/characters/a.bin
                objects:
                  '0xdeadbeef': {class: '0x00c0ffee', set: {a: 1}}
                  Characters/Gone: {remove: true}
              - name: Ordered
                entries:
                  Characters/Z: {a: 1}
                  '0xABCDEF01': {b: 2}
                  Characters/A: {links: ['0123456789012345']}
                  Characters/M: {'-c': [3]}
              - entries: {}
            """;
        using var doc = JsonDocument.Parse(GameDataManifestYaml.ToDocumentJson(yaml));
        var m = doc.RootElement.GetProperty("modules");
        Assert.Equal(7, m.GetArrayLength());

        Assert.Equal("123", m[0].GetProperty("name").GetString());
        Assert.Equal("0123456789012345", m[0].GetProperty("target").GetString());
        var edit = m[0].GetProperty("edits")[0];
        Assert.Equal(new[] { "1e3", "null", "~", "true", "0x12345678", "a: b", "c #d", "  padded  " }, edit.GetProperty("links").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("-1", edit.GetProperty("-links")[0].GetString());
        var props = edit.GetProperty("0x12345678");
        Assert.Equal("18446744073709551615", props.GetProperty("count").GetRawText());
        Assert.Equal("-1", props.GetProperty("offset").GetRawText());
        Assert.Equal("1.0", props.GetProperty("ratio").GetRawText());
        Assert.Equal("null", props.GetProperty("text").GetString());
        Assert.Equal("first\nsecond\n", props.GetProperty("multiline").GetString());
        Assert.StartsWith("one two three", props.GetProperty("long").GetString());
        Assert.Equal(JsonValueKind.True, props.GetProperty("flag").ValueKind);
        Assert.Equal(JsonValueKind.Null, props.GetProperty("absent").ValueKind);
        Assert.Equal("""{"f32":1.5}""", props.GetProperty("pin").GetRawText());
        Assert.Equal("""{"pointer":{"class":"Particle","set":{"0x00000001":1}}}""", props.GetProperty("ptr").GetRawText());
        Assert.Equal("""["1","2"]""", props.GetProperty("+tags").GetRawText());

        // compact bindings become the one edit, targets stay text
        Assert.Equal("""[{"links":[]}]""", m[1].GetProperty("edits").GetRawText());
        Assert.Equal("1234", m[2].GetProperty("target").GetString());
        Assert.Equal("12345678901234e5", m[3].GetProperty("target").GetString());
        Assert.Equal("""{"0xdeadbeef":{"class":"0x00c0ffee","set":{"a":1}},"Characters/Gone":{"remove":true}}""", m[4].GetProperty("edits")[0].GetProperty("objects").GetRawText());
        Assert.Equal(new[] { "Characters/Z", "0xABCDEF01", "Characters/A", "Characters/M" }, m[5].GetProperty("entries").EnumerateObject().Select(p => p.Name));
        Assert.Equal("{}", m[6].GetProperty("entries").GetRawText());
        for (int i = 0; i < 7; i++) Assert.Equal(i, m[i].GetProperty("origin").GetProperty("module").GetInt32());
    }

    [Fact]
    public void ATagOnAnObjectBodyIsItsClassAndWhatTheReaderCannotTakeIsRefused()
    {
        using (var doc = JsonDocument.Parse(GameDataManifestYaml.ToDocumentJson("version: 1\nmodules:\n- target: a.bin\n  objects:\n    '0x1': !Spell {a: 1}\n    '0x2': !Empty\n")))
            Assert.Equal("""{"0x1":{"class":"Spell","set":{"a":1}},"0x2":{"class":"Empty"}}""",
                doc.RootElement.GetProperty("modules")[0].GetProperty("edits")[0].GetProperty("objects").GetRawText());

        foreach (string bad in new[]
                 {
                     "version: 1\nmodules:\n- target: a.bin\n  source: s.yaml\n",                  // the entry names no source
                     "version: 1\nmodules:\n- target: a.bin\n  links: [&a x]\n",                  // anchors
                     "version: 1\nmodules:\n- target: a.bin\n  p: !f32 .nan\n",                   // not finite
                     "version: 1\nmodules:\n- target: a.bin\n  a: 1\n  a: 2\n",                   // duplicate key
                     "version: 1\nmodules:\n- target: a.bin\n  p: !nosuch 1\n",                   // unknown type pin
                     "version: 1\nextra: 1\nmodules: []\n",                                       // a key a manifest does not have
                     "- not a mapping\n",
                 })
            Assert.Throws<FormatException>(() => GameDataManifestYaml.ToDocumentJson(bad));
    }

    [Fact]
    public void BlockScalarsCommentsAndMultiLineFlowAreRead()
    {
        string yaml = "# a manifest\nversion: 1   # the version\nmodules:\n"
            + "  - target: a.bin   # comment\n    edits:\n"
            + "      - links: [\n          one,\n          two  # last\n        ]\n"
            + "        text: |\n          line1\n          line2\n"
            + "        folded: >-\n          a\n          b\n";
        using var doc = JsonDocument.Parse(GameDataManifestYaml.ToDocumentJson(yaml));
        var edit = doc.RootElement.GetProperty("modules")[0].GetProperty("edits")[0];
        Assert.Equal(new[] { "one", "two" }, edit.GetProperty("links").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("line1\nline2\n", edit.GetProperty("text").GetString());
        Assert.Equal("a b", edit.GetProperty("folded").GetString());
    }
}

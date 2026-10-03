using System.Text.Json.Nodes;
using ReyEngine.Core.Build;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816: Send to LTK Manager for a project that imported an LTK-layered .fantome. The layer's imported GameData goes into
/// <c>content/&lt;layer&gt;/game_data.yaml</c> as JSON in YAML flow style, one module to a line; its override files go beside it at
/// the layer-relative paths the document names them by; <c>mod.config.json</c> names the display names, string overrides, license,
/// tags, champions and maps.
///
/// <para>Checked outside the suite against league-mod's own project loader (<c>ltk_mod_project::game_data::load_layer</c> and
/// <c>ModProject::load</c>) over Crauzer's 13 modules - see the M816 commit. What is pinned here is the text itself and the files.</para>
/// </summary>
public sealed class LtkSendImportedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m816-send-" + Guid.NewGuid().ToString("N"));

    public LtkSendImportedTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const string Doc =
        "{\n  \"version\": 1,\n  \"modules\": [\n    {\n      \"name\": \"Baron draws the Milkshake skin\",\n      \"target\": \"data/characters/sru_baron/skins/skin0.bin\",\n"
        + "      \"edits\": [\n        {\n          \"links\": [\"DATA/x.bin\"],\n          \"Characters/SRU_Baron/Skins/Skin0\": {\n            \"skinMeshProperties\": {\"ref\": \"Characters/SRU_Baron/Skins/Skin9:skinMeshProperties\"},\n"
        + "            \"speed\": 1E-30\n          }\n        }\n      ],\n      \"origin\": {\"manifest\": \"game_data.yaml\", \"source\": null, \"module\": 0}\n    },\n"
        + "    {\"target\":\"data/maps/a.bin\",\"edits\":[{\"overrides\":[\"data/maps/a.ptch\"]}],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":1}}\n  ]\n}";

    private ReyProject ImportedProject()
    {
        string project = Path.Combine(_root, "project");
        LtkProjectStore.WriteDeclarations(project, "base", Doc);
        LtkProjectStore.WriteDeclarations(project, "events", "{\"version\":1,\"modules\":[]}");
        LtkProjectStore.WriteOverrideFile(project, "base", "data/maps/a.ptch", new byte[] { 1, 2, 3, 4 });
        var p = new ReyProject { RootPath = project, Name = "Imported", ModAuthor = "Crauzer", ModVersion = "0.3.0" };
        p.Layers.Add(new ProjectLayer { Name = "base", DeclarationsKey = "base", Description = LtkProjectLayers.BaseDescription });   // as an import makes it
        p.Layers.Add(new ProjectLayer
        {
            Name = "events", Priority = 2, DisplayName = "Events", DeclarationsKey = "events",
            StringOverrides = JsonNode.Parse("{\"en_us\":{\"b\":\"2\",\"a\":\"1\"}}")!.AsObject(),
        });
        p.ModLicense = new ProjectLicense { Name = "MIT for the mod files", AsObject = true };
        p.ModTags.Add("map-skin");
        p.ModMaps.Add("summoners-rift");
        return p;
    }

    private static DeclaredChunk Own(string target, string yaml = "") => new(target, yaml.Length == 0 ? "  - target: \"" + target + "\"\n    X/y:\n      speed: 2\n" : yaml, null);

    // ===================================================== the manifest text

    [Fact]
    public void ImportedModulesComeFirstAsOneJsonMappingPerLineWithoutTheirOrigins()
    {
        var imported = GameDataDocumentText.Read(Doc).Modules;
        string text = BinDeclarations.Manifest(new[] { Own("data/own.bin") }, imported);

        var lines = text.Split('\n');
        Assert.StartsWith("# Written by ReyEngine", lines[0]);
        Assert.Contains("The first 2 module(s) are declarations imported from a .fantome", text);
        int at = Array.IndexOf(lines, "modules:");
        Assert.Equal("version: 1", lines[at - 1]);

        // two list items, each a JSON mapping on its own line, then the module of the project's own
        Assert.StartsWith("  - {\"name\":\"Baron draws the Milkshake skin\",\"target\":\"data/characters/sru_baron/skins/skin0.bin\"", lines[at + 1]);
        Assert.StartsWith("  - {\"target\":\"data/maps/a.bin\",\"edits\":[{\"overrides\":[\"data/maps/a.ptch\"]}]}", lines[at + 2]);
        Assert.Equal("  - target: \"data/own.bin\"", lines[at + 3]);
        Assert.DoesNotContain("origin", text);
        Assert.Contains("1E-30", text);                                                            // a number keeps its token
        Assert.Contains("{\"ref\":\"Characters/SRU_Baron/Skins/Skin9:skinMeshProperties\"}", text);

        foreach (var line in lines.Skip(at + 1).Take(2))
        {
            var module = JsonNode.Parse(line[4..])!.AsObject();                                   // every line is JSON
            Assert.True(module.ContainsKey("target"));
            Assert.False(module.ContainsKey("origin"));
        }
    }

    [Fact]
    public void ALayerWithoutImportedModulesIsTheManifestItAlwaysWas()
    {
        var chunks = new[] { Own("data/a.bin"), Own("data/b.bin") };
        string plain = BinDeclarations.Manifest(chunks);
        Assert.Equal(plain, BinDeclarations.Manifest(chunks, null));
        Assert.Equal(plain, BinDeclarations.Manifest(chunks, Array.Empty<GameDataModuleText>()));
        Assert.Equal(
            "# Written by ReyEngine: each module is one bin's changes against the game's copy at the time\n"
            + "# of sending. LTK Manager applies them over the installed patch's bin at every build.\n"
            + "version: 1\nmodules:\n  - target: \"data/a.bin\"\n    X/y:\n      speed: 2\n  - target: \"data/b.bin\"\n    X/y:\n      speed: 2\n",
            plain);
    }

    [Fact]
    public void ALayerWithOnlyImportedModulesGetsAManifestOfThem()
    {
        var p = ImportedProject();
        var imported = LtkProjectStore.ReadLayers(p);

        var all = BinDeclarations.WithImported(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["events"] = "ours" }, imported);

        Assert.Equal(new[] { "base", "events" }, all.Keys.Order());
        Assert.Equal("ours", all["events"]);                                                       // a layer this project declares for keeps its own
        Assert.StartsWith("# Written by ReyEngine", all["base"]);
        Assert.Contains("version: 1\nmodules:\n  - {\"name\":\"Baron", all["base"]);
    }

    [Fact]
    public void AManifestModuleDoesNotRewriteTheStringsOfTheSource()
    {
        // an emoji written raw stays raw, an escape stays an escape - and a PAIR of surrogate escapes becomes the emoji, because YAML takes no pair
        string module = "{\"target\":\"a\",\"edits\":[{\"A/b\":{\"s\":\"\\u00e9 \\\"q\\\" \U0001F600 \\ud83d\\ude00\"}}],\"origin\":{\"manifest\":\"m\",\"source\":null,\"module\":0}}";
        string line = GameDataDocumentText.ManifestModule(module);
        Assert.Contains("\"\\u00e9 \\\"q\\\" \U0001F600 \U0001F600\"", line);
        Assert.Equal("{\"target\":\"a\",\"edits\":[{\"A/b\":{\"s\":\"\\u00e9 \\\"q\\\" \U0001F600 \U0001F600\"}}]}", line);
    }

    // ===================================================== review: a manifest of no modules is an empty list

    /// <summary>A bare <c>modules:</c> is YAML null, not a list. A layer whose stored document has no modules (a GameData-only layer an
    /// author left empty) used to get exactly that.</summary>
    [Fact]
    public void AManifestOfNoModulesIsAnEmptyListAndNotYamlNull()
    {
        string noImported = BinDeclarations.Manifest(Array.Empty<DeclaredChunk>(), Array.Empty<GameDataModuleText>());
        string nothingDeclared = BinDeclarations.Manifest(new[] { new DeclaredChunk("data/a.bin", null, "ships whole") });
        string plain = BinDeclarations.Manifest(Array.Empty<DeclaredChunk>());

        foreach (string text in new[] { noImported, nothingDeclared, plain })
        {
            Assert.EndsWith("version: 1\nmodules: []\n", text);
            Assert.DoesNotContain("modules:\n", text);
        }
        Assert.StartsWith("# Written by ReyEngine", noImported);                                   // the header is the same
    }

    [Fact]
    public void ALayerWhoseStoredDocumentHasNoModulesGetsTheEmptyListInItsManifest()
    {
        var imported = LtkProjectStore.ReadLayers(ImportedProject());                              // "events" holds {"version":1,"modules":[]}

        var all = BinDeclarations.WithImported(new Dictionary<string, string>(), imported);

        Assert.EndsWith("version: 1\nmodules: []\n", all["events"]);
        Assert.Contains("modules:\n  - {", all["base"]);                                            // and a layer that has modules is unchanged
    }

    // ===================================================== the send

    [Fact]
    public void TheSendWritesTheManifestsTheOverrideFilesAndTheConfig()
    {
        var p = ImportedProject();
        var imported = LtkProjectStore.ReadLayers(p);
        string workshop = Path.Combine(_root, "workshop");
        Directory.CreateDirectory(workshop);
        var options = new LtkSendOptions(workshop, "imported", "Imported", "0.3.0", "d", "Crauzer") { Layers = LtkProjectLayers.Of(p) };
        var send = LtkProjectLayers.ForSend(options, p, imported, BinDeclarations.WithImported(new Dictionary<string, string>(), imported));

        var result = LtkWorkshopExporter.Send(send, Array.Empty<(string, string, string, string)>());

        string mod = result.ModFolder;
        Assert.True(File.Exists(Path.Combine(mod, "content", "base", "game_data.yaml")));
        Assert.True(File.Exists(Path.Combine(mod, "content", "events", "game_data.yaml")));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(mod, "content", "base", "data", "maps", "a.ptch")));
        Assert.Equal(3, result.FilesWritten);                                                       // two manifests and one override file

        var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "mod.config.json")))!.AsObject();
        var layers = cfg["layers"]!.AsArray().Select(l => l!.AsObject()).ToList();
        Assert.Equal(new[] { "base", "events" }, layers.Select(l => (string)l["name"]!));
        Assert.Equal("Base layer of the mod", (string)layers[0]["description"]!);                  // a base layer an import re-declared keeps its text
        Assert.False(layers[0].ContainsKey("display_name"));
        Assert.Equal("Events", (string)layers[1]["display_name"]!);
        Assert.Equal(new[] { "en_us" }, layers[1]["string_overrides"]!.AsObject().Select(x => x.Key));
        Assert.Equal(new[] { "b", "a" }, layers[1]["string_overrides"]!["en_us"]!.AsObject().Select(x => x.Key));

        // mod.config.json spells a license object with lowercase keys
        Assert.Equal("MIT for the mod files", (string)cfg["license"]!["name"]!);
        Assert.False(cfg["license"]!.AsObject().ContainsKey("url"));
        Assert.Equal(new[] { "map-skin" }, cfg["tags"]!.AsArray().Select(t => (string)t!));
        Assert.Equal(new[] { "summoners-rift" }, cfg["maps"]!.AsArray().Select(t => (string)t!));
        Assert.False(cfg.ContainsKey("champions"));
    }

    [Fact]
    public void ALicenseGivenAsAStringIsWrittenAsAString()
    {
        var p = ImportedProject();
        p.ModLicense = new ProjectLicense { Name = "MIT", AsObject = false };
        var options = new LtkSendOptions(Path.Combine(_root, "w2"), "m", "M", "1", "d", "A");
        Directory.CreateDirectory(options.WorkshopRoot);
        var send = LtkProjectLayers.ForSend(options, p, Array.Empty<ImportedLayerData>(), new Dictionary<string, string>());

        var result = LtkWorkshopExporter.Send(send, new[] { ("", "Map11", "data/x.bin", WriteFile("x.bin")) });

        var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(result.ModFolder, "mod.config.json")))!.AsObject();
        Assert.Equal("MIT", (string)cfg["license"]!);
    }

    [Fact]
    public void AProjectThatNeverImportedAnythingSendsAsItDid()
    {
        string workshop = Path.Combine(_root, "w3");
        Directory.CreateDirectory(workshop);
        string existing = Path.Combine(workshop, "m");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "mod.config.json"),
            "{\"name\":\"m\",\"tags\":[\"hand-set\"],\"maps\":[\"aram\"],\"license\":\"GPL-3.0\",\"champions\":[\"Lux\"],\"layers\":[{\"name\":\"base\",\"priority\":0,\"display_name\":\"By hand\"}]}");
        var p = new ReyProject { Name = "Plain", RootPath = Path.Combine(_root, "plain") };
        var options = new LtkSendOptions(workshop, "m", "M", "1", "d", "A") { Layers = LtkProjectLayers.Of(p) };
        var send = LtkProjectLayers.ForSend(options, p, Array.Empty<ImportedLayerData>(), new Dictionary<string, string>());

        LtkWorkshopExporter.Send(send, new[] { ("", "Map11", "data/x.bin", WriteFile("y.bin")) });

        // no license, tags, champions or maps of the project's own: what the config (or the manager's editor) holds stays
        var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(existing, "mod.config.json")))!.AsObject();
        Assert.Equal("GPL-3.0", (string)cfg["license"]!);
        Assert.Equal(new[] { "hand-set" }, cfg["tags"]!.AsArray().Select(t => (string)t!));
        Assert.Equal(new[] { "aram" }, cfg["maps"]!.AsArray().Select(t => (string)t!));
        Assert.Equal(new[] { "Lux" }, cfg["champions"]!.AsArray().Select(t => (string)t!));
        Assert.Equal("By hand", (string)cfg["layers"]![0]!["display_name"]!);
    }

    [Fact]
    public void ALayerOfDeclarationsAndFilesAloneIsAModWithNoWadFolder()
    {
        var p = ImportedProject();
        var imported = LtkProjectStore.ReadLayers(p);
        var options = new LtkSendOptions(Path.Combine(_root, "w4"), "g", "G", "1", "d", "A") { Layers = LtkProjectLayers.Of(p) };
        Directory.CreateDirectory(options.WorkshopRoot);
        var send = LtkProjectLayers.ForSend(options, p, imported, BinDeclarations.WithImported(new Dictionary<string, string>(), imported));

        var result = LtkWorkshopExporter.Send(send, Array.Empty<(string, string, string, string)>());

        Assert.False(Directory.EnumerateDirectories(Path.Combine(result.ModFolder, "content", "base")).Any(d => d.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase)));
        Assert.True(File.Exists(Path.Combine(result.ModFolder, "content", "base", "game_data.yaml")));
    }

    [Fact]
    public void AnOverrideFilePathThatClimbsOutOfItsLayerIsSkippedNotWritten()
    {
        string workshop = Path.Combine(_root, "w5");
        Directory.CreateDirectory(workshop);
        var options = new LtkSendOptions(workshop, "m", "M", "1", "d", "A") with
        {
            LayerFiles = new[] { ("base", "../escape.ptch", WriteFile("e.ptch")), ("base", "ok.ptch", WriteFile("ok.ptch")) },
            GameData = new Dictionary<string, string> { ["base"] = "version: 1\nmodules: []\n" },
        };

        var result = LtkWorkshopExporter.Send(options, Array.Empty<(string, string, string, string)>());

        Assert.True(File.Exists(Path.Combine(result.ModFolder, "content", "base", "ok.ptch")));
        Assert.False(File.Exists(Path.Combine(result.ModFolder, "content", "escape.ptch")));
        Assert.False(File.Exists(Path.Combine(result.ModFolder, "escape.ptch")));
    }

    private string WriteFile(string name)
    {
        string path = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 9 });
        return path;
    }
}

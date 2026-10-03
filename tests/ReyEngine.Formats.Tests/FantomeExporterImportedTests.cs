using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReyEngine.Core.Build;
using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816: what Export .fantome writes for a package that carries more than M814's did - a license, tags, champions, maps, a layer's
/// imported GameData text, its string overrides and override files, the package's README and license text - and that a package
/// without any of it is written byte for byte as M814 wrote it.
/// </summary>
public sealed class FantomeExporterImportedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rey-m816-exp-" + Guid.NewGuid().ToString("N"));

    public FantomeExporterImportedTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Out => Path.Combine(_dir, "out", "mod.fantome");

    private string File1(string name, params byte[] bytes)
    {
        string path = Path.Combine(_dir, "files", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes.Length == 0 ? new byte[] { 1 } : bytes);
        return path;
    }

    private string Wad(string name)
    {
        string path = Path.Combine(_dir, "wads", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[96]);
        return path;
    }

    private static FantomeMeta Meta(params FantomeLayer[] layers) => new()
    {
        Name = "My Mod", Author = "Someone", Version = "1.2.3", Description = "d", Generator = "ReyEngine 0.5.1", Layers = layers,
    };

    // ===================================================== M814's bytes, unchanged

    /// <summary>M814's rendering of info.json, kept here as the reference: a <see cref="JsonObject"/> written with the export's
    /// options. M816 writes the same document with a <see cref="Utf8JsonWriter"/> so that a layer's GameData can go in as text; for a
    /// package with none of M816's content the bytes must be the ones this gives.</summary>
    private static string M814(FantomeMeta meta, IReadOnlyList<FantomeLayer> layers, bool hashtable)
    {
        var info = new JsonObject { ["Name"] = meta.Name, ["Author"] = meta.Author, ["Version"] = meta.Version, ["Description"] = meta.Description };
        if (!string.IsNullOrWhiteSpace(meta.Heart)) info["Heart"] = meta.Heart;
        if (!string.IsNullOrWhiteSpace(meta.Home)) info["Home"] = meta.Home;
        var table = new JsonObject();
        foreach (var layer in layers)
        {
            var entry = new JsonObject();
            if (layer.GameData is not null) entry["GameData"] = layer.GameData.DeepClone();
            entry["Name"] = layer.Name;
            if (!string.IsNullOrEmpty(layer.DisplayName)) entry["DisplayName"] = layer.DisplayName;
            entry["Priority"] = layer.Priority;
            table[layer.Name] = entry;
        }
        info["Layers"] = table;
        if (hashtable)
            info["Hashtables"] = new JsonArray(new JsonObject { ["Path"] = FantomeHashtables.HarvestedPath, ["Category"] = "game", ["Algorithm"] = "xxh64", ["Bits"] = 64 });
        if (!string.IsNullOrWhiteSpace(meta.Generator)) info["Generator"] = meta.Generator;
        return info.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    [Fact]
    public void APackageWithNoM816ContentIsWrittenByteForByteAsM814WroteIt()
    {
        var doc = JsonNode.Parse("""{"version":1,"modules":[{"target":"data/a.bin","edits":[{"A/b":{"speed":1E-30,"z":-0.0,"s":"café \"q\" <&>'+"}}],"origin":{"manifest":"game_data.yaml","source":null,"module":0}}]}""")!;
        var cases = new (FantomeMeta Meta, bool Hashtable)[]
        {
            (Meta(), false),
            (new FantomeMeta { Name = "", Author = "", Version = "", Description = "" }, false),
            (new FantomeMeta { Name = "café – \"quoted\" <tag> & 'x'", Author = "A\nB", Version = "1", Description = "line1\r\nline2\ttab", Heart = "https://h", Home = "https://o", Generator = "g" }, true),
            (Meta(new FantomeLayer("base", 0, GameData: doc), new FantomeLayer("fix", 10, "Particle fix"), new FantomeLayer("low", -3), new FantomeLayer("layer10", 1), new FantomeLayer("layer9", 1)), true),
            (Meta(new FantomeLayer("only", 2, GameData: doc)), false),
        };
        foreach (var (meta, hashtable) in cases)
        {
            var layers = FantomeExporter.OrderLayers(meta.Layers);
            Assert.Equal(M814(meta, layers, hashtable), FantomeExporter.BuildInfoJson(meta, layers, hashtable));
        }
    }

    // ===================================================== the new keys

    [Fact]
    public void LicenseTagsChampionsAndMapsFollowDescriptionInTheOrderLtkFantomeWritesThem()
    {
        var meta = Meta();
        meta.Heart = "https://h";
        meta.License = new ProjectLicense { Name = "MIT", Url = "https://l", AsObject = true };
        meta.Tags = new[] { "map-skin", "sfx" };
        meta.Champions = new[] { "Ahri" };
        meta.Maps = new[] { "summoners-rift" };

        string text = FantomeExporter.BuildInfoJson(meta, FantomeExporter.OrderLayers(meta.Layers), hashtable: true);

        using var info = JsonDocument.Parse(text);
        Assert.Equal(new[] { "Name", "Author", "Version", "Description", "Heart", "License", "Tags", "Champions", "Maps", "Layers", "Hashtables", "Generator" },
            info.RootElement.EnumerateObject().Select(p => p.Name));
        var license = info.RootElement.GetProperty("License");
        Assert.Equal(new[] { "Name", "Url" }, license.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "map-skin", "sfx" }, info.RootElement.GetProperty("Tags").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void ALicenseIsAStringUnlessItWasAnObjectOrHasALink()
    {
        string Written(ProjectLicense license)
        {
            var meta = Meta();
            meta.License = license;
            using var info = JsonDocument.Parse(FantomeExporter.BuildInfoJson(meta, FantomeExporter.OrderLayers(meta.Layers), false));
            return info.RootElement.GetProperty("License").GetRawText().Replace(" ", "").Replace("\n", "");
        }
        Assert.Equal("\"MIT\"", Written(new ProjectLicense { Name = "MIT" }));
        Assert.Equal("{\"Name\":\"Terms\"}", Written(new ProjectLicense { Name = "Terms", AsObject = true }));
        Assert.Equal("{\"Name\":\"X\",\"Url\":\"https://x\"}", Written(new ProjectLicense { Name = "X", Url = "https://x" }));     // a link needs an object
    }

    [Fact]
    public void KeysWithNothingToSayAreNotWritten()
    {
        var meta = Meta();
        meta.Tags = Array.Empty<string>();
        meta.License = null;
        using var info = JsonDocument.Parse(FantomeExporter.BuildInfoJson(meta, FantomeExporter.OrderLayers(meta.Layers), false));
        Assert.False(info.RootElement.TryGetProperty("License", out _));
        Assert.False(info.RootElement.TryGetProperty("Tags", out _));
        Assert.False(info.RootElement.TryGetProperty("Champions", out _));
        Assert.False(info.RootElement.TryGetProperty("Maps", out _));
    }

    // ===================================================== a layer's imported GameData

    private const string ImportedText = "{\n  \"version\": 1,\n  \"modules\": [ {\"name\":\"n\",\"target\":\"a\",\"edits\":[{\"A/b\":{\"x\":1.0}}],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":0}} ]\n}";

    private static string InfoText(string output)
    {
        using var zip = ZipFile.OpenRead(output);
        using var s = zip.GetEntry("META/info.json")!.Open();
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    [Fact]
    public void AnImportedDocumentIsWrittenAsItsTextAndTheLayerKeepsFantomeLayerInfosOrder()
    {
        var layer = new FantomeLayer("events", 3, "Events") { ImportedGameData = ImportedText };
        string text = FantomeExporter.BuildInfoJson(Meta(layer), FantomeExporter.OrderLayers(new[] { layer }), false);

        Assert.Contains("\"GameData\": " + ImportedText, text);
        using var info = JsonDocument.Parse(text);
        Assert.Equal(new[] { "GameData", "Name", "DisplayName", "Priority" }, info.RootElement.GetProperty("Layers").GetProperty("events").EnumerateObject().Select(p => p.Name));
        Assert.Equal(ImportedText, info.RootElement.GetProperty("Layers").GetProperty("events").GetProperty("GameData").GetRawText());
    }

    [Fact]
    public void OwnModulesAreWrittenBehindTheImportedOnes()
    {
        var own = JsonNode.Parse("{\"version\":1,\"modules\":[{\"target\":\"data/own.bin\",\"edits\":[],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":1}}]}")!;
        var layer = new FantomeLayer("events", 3, GameData: own) { ImportedGameData = ImportedText };

        string text = FantomeExporter.BuildInfoJson(Meta(layer), FantomeExporter.OrderLayers(new[] { layer }), false);

        using var info = JsonDocument.Parse(text);
        var modules = info.RootElement.GetProperty("Layers").GetProperty("events").GetProperty("GameData").GetProperty("modules");
        Assert.Equal(new[] { "a", "data/own.bin" }, modules.EnumerateArray().Select(m => m.GetProperty("target").GetString()));
        Assert.Contains("{\"name\":\"n\",\"target\":\"a\",\"edits\":[{\"A/b\":{\"x\":1.0}}]", text);          // the imported module, as it was spelled
    }

    [Fact]
    public void ADocumentOfOwnModulesAloneIsWrittenAsBefore()
    {
        var own = JsonNode.Parse("{\"version\":1,\"modules\":[{\"target\":\"data/own.bin\",\"edits\":[],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":0}}]}")!;
        var layer = new FantomeLayer("base", 0, GameData: own);
        Assert.Equal(M814(Meta(layer), FantomeExporter.OrderLayers(new[] { layer }), false),
            FantomeExporter.BuildInfoJson(Meta(layer), FantomeExporter.OrderLayers(new[] { layer }), false));
    }

    [Fact]
    public void StringOverridesComeAfterPriority()
    {
        var layer = new FantomeLayer("words", 2) { StringOverrides = JsonNode.Parse("{\"en_us\":{\"b\":\"2\",\"a\":\"1\"}}")!.AsObject() };
        using var info = JsonDocument.Parse(FantomeExporter.BuildInfoJson(Meta(layer), FantomeExporter.OrderLayers(new[] { layer }), false));
        var words = info.RootElement.GetProperty("Layers").GetProperty("words");
        Assert.Equal(new[] { "Name", "Priority", "StringOverrides" }, words.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "b", "a" }, words.GetProperty("StringOverrides").GetProperty("en_us").EnumerateObject().Select(p => p.Name));

        var empty = new FantomeLayer("words", 2) { StringOverrides = new JsonObject() };
        using var none = JsonDocument.Parse(FantomeExporter.BuildInfoJson(Meta(empty), FantomeExporter.OrderLayers(new[] { empty }), false));
        Assert.False(none.RootElement.GetProperty("Layers").GetProperty("words").TryGetProperty("StringOverrides", out _));
    }

    // ===================================================== override files, the package's text files, WAD names

    [Fact]
    public void OverrideFilesAreStoredBelowMetaGameDataByLayer()
    {
        var layer = new FantomeLayer("events", 3)
        {
            ImportedGameData = ImportedText,
            OverrideFiles = new[] { new FantomeOverrideFile("data/x.ptch", File1("x.ptch", 1, 2, 3)), new FantomeOverrideFile("y.ptch", File1("y.ptch", 9)) },
        };

        FantomeExporter.Export(Meta(layer), Array.Empty<FantomeWad>(), null, Out);

        using var zip = ZipFile.OpenRead(Out);
        Assert.Equal(new byte[] { 1, 2, 3 }, LayeredFantomeSupport.Bytes(zip, "META/game_data/events/data/x.ptch"));
        Assert.Equal(new byte[] { 9 }, LayeredFantomeSupport.Bytes(zip, "META/game_data/events/y.ptch"));
    }

    [Fact]
    public void AMissingOrUnsafeOverrideFileRefusesTheExportAndLeavesNothingBehind()
    {
        foreach (var file in new[]
                 {
                     new FantomeOverrideFile("x.ptch", Path.Combine(_dir, "gone.ptch")),
                     new FantomeOverrideFile("../x.ptch", File1("a.ptch")),
                     new FantomeOverrideFile("a\\b.ptch", File1("b.ptch")),
                 })
        {
            var layer = new FantomeLayer("events", 3) { OverrideFiles = new[] { file } };
            Assert.Throws<InvalidOperationException>(() => FantomeExporter.Export(Meta(layer), Array.Empty<FantomeWad>(), null, Out));
            Assert.False(File.Exists(Out));
            Assert.False(File.Exists(Out + ".tmp"));
        }
    }

    [Fact]
    public void TheReadmeAndLicenseTextAreStoredWhereTheyCameFrom()
    {
        var meta = Meta();
        meta.MetaFiles = new[] { ("META/README.md", File1("readme", 35, 32)), ("META/LICENSE", File1("license", 77)) };

        FantomeExporter.Export(meta, Array.Empty<FantomeWad>(), null, Out);

        using var zip = ZipFile.OpenRead(Out);
        Assert.Equal(new byte[] { 35, 32 }, LayeredFantomeSupport.Bytes(zip, "META/README.md"));
        Assert.Equal(new byte[] { 77 }, LayeredFantomeSupport.Bytes(zip, "META/LICENSE"));
    }

    [Fact]
    public void AMissingTextFileRefusesTheExport()
    {
        var meta = Meta();
        meta.MetaFiles = new[] { ("META/README.md", Path.Combine(_dir, "nothing.md")) };
        Assert.Throws<InvalidOperationException>(() => FantomeExporter.Export(meta, Array.Empty<FantomeWad>(), null, Out));
        Assert.False(File.Exists(Out));
    }

    [Fact]
    public void AWadIsStoredUnderItsOwnNameWhenTheStagedFileHasAnother()
    {
        var a = new FantomeWad(Wad("Map11.wad.client"));
        var b = new FantomeWad(Wad("layers_winter_Map11.wad.client"), "winter") { Name = "Map11.wad.client" };
        Assert.Equal("WAD/Map11.wad.client", FantomeExporter.WadEntryName(a));
        Assert.Equal("WAD_winter/Map11.wad.client", FantomeExporter.WadEntryName(b));

        FantomeExporter.Export(Meta(new FantomeLayer("winter", 5)), new[] { a, b }, null, Out);

        using var zip = ZipFile.OpenRead(Out);
        Assert.NotNull(zip.GetEntry("WAD/Map11.wad.client"));
        Assert.NotNull(zip.GetEntry("WAD_winter/Map11.wad.client"));
        Assert.Null(zip.GetEntry("WAD_winter/layers_winter_Map11.wad.client"));
    }

    [Theory]
    [InlineData("Map11", "Map11.wad.client")]
    [InlineData("Map11.wad.client", "Map11.wad.client")]
    [InlineData("Foo.wad", "Foo.wad")]
    [InlineData("FOO.WAD.MOBILE", "FOO.WAD.MOBILE")]
    [InlineData("Map11.wad.old", "Map11.wad.old.wad.client")]
    public void AWadFolderIsStoredUnderANameLtkFantomeRecognises(string leaf, string stored) =>
        Assert.Equal(stored, FantomeLayers.WadFileName(leaf));

    [Fact]
    public void TwoWadsOfOneStoredNameAreStillRefused()
    {
        var a = new FantomeWad(Wad("A.wad.client")) { Name = "Map11.wad.client" };
        var b = new FantomeWad(Wad("B.wad.client")) { Name = "map11.WAD.client" };
        var ex = Assert.Throws<InvalidOperationException>(() => FantomeExporter.Export(Meta(), new[] { a, b }, null, Out));
        Assert.Contains("Two WADs would be stored as", ex.Message);
    }
}

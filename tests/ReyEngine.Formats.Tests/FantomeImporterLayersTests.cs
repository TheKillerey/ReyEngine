using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M814 review: Import .fantome reads <c>WAD/</c>, <c>RAW/</c> and the thumbnail; Export .fantome now writes LTK's
/// layered layout (<c>WAD_&lt;layer&gt;/</c> for a layer other than base, <c>Layers.&lt;name&gt;.GameData</c> for game bins
/// shipped as declarations). Importing an export of that kind used to give a smaller mod without a word. These pin that
/// the import counts what it leaves behind, per layer, and says so (<see cref="FantomeImportResult.NotImportedWarning"/>),
/// and that a package of the base layer alone still imports as it always did, with nothing to report.
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
                    new FantomeLayer("fix", 10, GameData: Doc("data/characters/ahri/skins/skin0.bin", "data/characters/ahri/skins/skin1.bin")),
                },
            },
            new[] { new FantomeWad(map), new FantomeWad(ahri, "fix") }, null, archive);
        return archive;
    }

    private FantomeImportResult Import(string archive) => FantomeImporter.Import(archive, Projects, null, new HashDatabase());

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

    private static (string, int, int)[] Shape(FantomeImportResult r) =>
        r.SkippedLayers.Select(l => (l.Name, l.Wads, l.GameDataModules)).ToArray();

    // ===================================================== a layered export

    [Fact]
    public void ALayeredExportImportsItsBaseWadsAndCountsWhatItLeavesBehind()
    {
        var result = Import(LayeredExport());

        // what the import has always read: the base layer's WAD/ folder
        Assert.Equal(1, result.Wads);
        Assert.Equal(0, result.FailedChunks);
        Assert.True(Directory.Exists(Path.Combine(result.RootPath, "Map11")));

        // what it does not: the layer's WAD directory and every GameData module
        Assert.False(Directory.Exists(Path.Combine(result.RootPath, "Ahri")));
        Assert.Equal(1, result.LayerWads);
        Assert.Equal(3, result.GameDataModules);
        Assert.Equal(new[] { ("base", 0, 1), ("fix", 1, 2) }, Shape(result));
    }

    [Fact]
    public void TheWarningNamesWhatWasNotImportedLayerByLayerAndThatALaterVersionReadsIt()
    {
        var result = Import(LayeredExport());

        Assert.Equal(
            "This .fantome uses LTK's layered layout, and the import reads only the base layer's WAD/ folders and RAW/. "
            + "NOT imported: 1 WAD(s) stored in WAD_<layer>/ directories and 3 declared game bin(s) (Layers.*.GameData) - "
            + "base: 1 declared bin(s); fix: 1 WAD(s), 2 declared bin(s). "
            + "ReyEngine does not read them yet - a later version will - so a mod built from this project would ship without them. "
            + "The .fantome itself is untouched.",
            result.NotImportedWarning);
    }

    [Fact]
    public void ALayerWithWadsAndNoDeclarationsIsReportedWithoutADeclarationClause()
    {
        string archive = Path.Combine(_root, "wads-only.fantome");
        FantomeExporter.Export(
            new FantomeMeta { Name = "W", Author = "T", Layers = new[] { new FantomeLayer("fx", 5) } },
            new[] { new FantomeWad(PackedWad("Ahri.wad.client", ("assets/y.dds", "yy")), "fx") }, null, archive);

        var result = Import(archive);

        Assert.Equal(0, result.Wads);                                  // base held nothing
        Assert.Equal(1, result.LayerWads);
        Assert.Equal(0, result.GameDataModules);
        Assert.Equal(new[] { ("fx", 1, 0) }, Shape(result));
        Assert.Contains("1 WAD(s) stored in WAD_<layer>/ directories - fx: 1 WAD(s).", result.NotImportedWarning);
        Assert.DoesNotContain("GameData", result.NotImportedWarning);
    }

    [Fact]
    public void AnImportOfAnExportWithOnlyDeclarationsReportsOnlyDeclarations()
    {
        string archive = Path.Combine(_root, "declared-only.fantome");
        FantomeExporter.Export(
            new FantomeMeta { Name = "D", Author = "T", Layers = new[] { new FantomeLayer("base", 0, GameData: Doc("data/a.bin", "data/b.bin")) } },
            Array.Empty<FantomeWad>(), null, archive);

        var result = Import(archive);

        Assert.Equal(0, result.LayerWads);
        Assert.Equal(2, result.GameDataModules);
        Assert.Contains("2 declared game bin(s) (Layers.*.GameData) - base: 2 declared bin(s).", result.NotImportedWarning);
        Assert.DoesNotContain("WAD(s) stored", result.NotImportedWarning);
    }

    // ===================================================== a package with nothing to report

    [Fact]
    public void ABaseOnlyPackageHasNothingLeftBehindAndNoWarning()
    {
        string archive = Path.Combine(_root, "plain.fantome");
        FantomeExporter.Export(
            new FantomeMeta { Name = "Plain", Author = "T" },
            new[] { new FantomeWad(PackedWad("Map11.wad.client", ("data/maps/new.bin", "new"))) }, null, archive);

        var result = Import(archive);

        Assert.Equal(1, result.Wads);
        Assert.Empty(result.SkippedLayers);
        Assert.Equal(0, result.LayerWads);
        Assert.Equal(0, result.GameDataModules);
        Assert.Null(result.NotImportedWarning);
    }

    [Fact]
    public void ALegacyPackageWithoutALayersTableHasNothingLeftBehind()
    {
        var result = Import(Archive("legacy.fantome",
            ("META/info.json", "{\"Name\":\"Legacy\",\"Author\":\"A\",\"Version\":\"1.0.0\"}"),
            ("RAW/assets/safe.txt", "inside")));

        Assert.Equal(1, result.RawFiles);
        Assert.Empty(result.SkippedLayers);
        Assert.Null(result.NotImportedWarning);
    }

    [Fact]
    public void AnEmptyDeclarationDocumentOrAnEmptyLayerIsNothingLeftBehind()
    {
        var result = Import(Archive("empty-layers.fantome",
            ("META/info.json",
                "{\"Name\":\"E\",\"Layers\":{\"base\":{\"Name\":\"base\",\"Priority\":0,\"GameData\":{\"version\":1,\"modules\":[]}},"
                + "\"fix\":{\"Name\":\"fix\",\"Priority\":1}}}"),
            ("RAW/assets/safe.txt", "inside")));

        Assert.Empty(result.SkippedLayers);
        Assert.Null(result.NotImportedWarning);
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
            ("WAD_fx/Lux.wad.client", "packed"),
            ("WAD_empty/", "")));

        Assert.Equal(new[] { ("fx", 2, 0) }, Shape(result));
        Assert.Equal(2, result.LayerWads);
    }

    [Fact]
    public void LayerNamesAreComparedWithoutRegardToCaseAndTheTablesSpellingWins()
    {
        var result = Import(Archive("case.fantome",
            ("META/info.json", "{\"Layers\":{\"Fix\":{\"Name\":\"Fix\",\"Priority\":1,\"GameData\":{\"version\":1,\"modules\":[{}]}}}}"),
            ("WAD_fix/Ahri.wad.client", "x")));

        Assert.Equal(new[] { ("Fix", 1, 1) }, Shape(result));
    }

    [Fact]
    public void LayersAreListedBaseFirstThenInPlainOrder()
    {
        var result = Import(Archive("order.fantome",
            ("META/info.json",
                "{\"Layers\":{\"zeta\":{\"GameData\":{\"modules\":[{}]}},\"base\":{\"GameData\":{\"modules\":[{},{}]}},\"alpha\":{\"GameData\":{\"modules\":[{}]}}}}"),
            ("WAD_mid/A.wad.client", "x")));

        Assert.Equal(new[] { "base", "alpha", "mid", "zeta" }, result.SkippedLayers.Select(l => l.Name).ToArray());
    }

    [Theory]
    [InlineData("{\"Layers\":[1,2,3]}")]                                           // not a table
    [InlineData("{\"Layers\":{\"fx\":5}}")]                                        // an entry that is not an object
    [InlineData("{\"Layers\":{\"fx\":{\"GameData\":\"nope\"}}}")]                  // a document that is not an object
    [InlineData("{\"Layers\":{\"fx\":{\"GameData\":{\"modules\":{}}}}}")]          // modules that is not a list
    [InlineData("{\"Layers\":null}")]
    [InlineData("[1,2,3]")]                                                        // not even an object
    [InlineData("{ this is not json")]
    public void AMalformedLayersTableIsToleratedAndTheDirectoriesStillCount(string info)
    {
        var result = Import(Archive("odd.fantome",
            ("META/info.json", info),
            ("WAD_fx/Ahri.wad.client", "x")));

        Assert.Equal(new[] { ("fx", 1, 0) }, Shape(result));
    }

    /// <summary>The import does not touch what it reports: the source package is read-only to it.</summary>
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
    public void TheProgressSaysWhatWasLeftBehindToo()
    {
        var lines = new List<string>();
        FantomeImporter.Import(LayeredExport(), Projects, null, new HashDatabase(), new Collect(lines));
        Assert.Contains(lines, l => l == "Not imported: 1 WAD(s) in WAD_<layer>/ and 3 GameData bin(s)");

        lines.Clear();
        FantomeImporter.Import(Archive("quiet.fantome", ("RAW/a.txt", "a")), Projects, null, new HashDatabase(), new Collect(lines));
        Assert.DoesNotContain(lines, l => l.StartsWith("Not imported", StringComparison.Ordinal));
    }

    private sealed class Collect : IProgress<string>
    {
        private readonly List<string> _lines;
        public Collect(List<string> lines) => _lines = lines;
        public void Report(string value) => _lines.Add(value);
    }
}

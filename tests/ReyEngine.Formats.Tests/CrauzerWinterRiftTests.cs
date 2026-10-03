using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using static ReyEngine.Formats.Tests.LayeredFantomeSupport;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816: Crauzer's <c>winter-rift-2025_0.3.0.fantome</c> (built with <c>ltk_mod_project</c> 0.16.2) - a complete Snowdown Summoner's
/// Rift whose one WAD carries 346 textures and a .mapgeo and NO .bin, and whose 13 bin changes are GameData modules in three layers
/// (<c>base</c>, <c>snowdown-baron</c>, <c>snowdown-minions</c>) - imported into a project and exported again.
///
/// <para>The file is a real artefact of the tool this feature has to interoperate with, so it is the check that synthetic packages
/// cannot be. It is 397 MB and lives on the development machine only: the tests return at once where it is absent, as the other
/// tests of installed assets do. Set <c>REY_M816_SCRATCH</c> to put the work (about 2 GB while it runs) somewhere other than the
/// temp folder, and <c>REY_M816_REEXPORT</c> to keep the re-exported package at a path for the outside checks (the M816 harness
/// reads it with league-mod's own crates).</para>
///
/// <para>The import and the export run once for all the tests of the class.</para>
/// </summary>
public sealed class CrauzerWinterRiftTests : IClassFixture<CrauzerWinterRiftTests.Sample>
{
    private const string SamplePath = @"C:\Users\theki\Downloads\winter-rift-2025_0.3.0.fantome";

    public sealed class Sample : IDisposable
    {
        private readonly string _work;

        public bool Available { get; }
        public FantomeImportResult? Import { get; }
        public ReyProject? Project { get; }
        public string? ReExport { get; }
        public bool KeepReExport { get; }

        public Sample()
        {
            if (!File.Exists(SamplePath)) { _work = ""; return; }
            Available = true;
            string baseDir = System.Environment.GetEnvironmentVariable("REY_M816_SCRATCH") is { Length: > 0 } s ? s : Path.GetTempPath();
            _work = Path.Combine(baseDir, "rey-m816-sample-" + Guid.NewGuid().ToString("N"));
            string projects = Path.Combine(_work, "projects");
            Directory.CreateDirectory(projects);

            Import = FantomeImporter.Import(SamplePath, projects, null, new HashDatabase());
            Project = ReyProjectService.OpenFolder(Import.RootPath);
            Project.OutputDirectory = Path.Combine(_work, "build");

            string? keep = System.Environment.GetEnvironmentVariable("REY_M816_REEXPORT");
            KeepReExport = !string.IsNullOrEmpty(keep);
            string output = KeepReExport ? keep! : Path.Combine(_work, "reexport.fantome");
            var vm = new MainWindowViewModel { Project = Project };
            ReExport = Export(vm, output);
        }

        public string Work => _work;

        public void Dispose()
        {
            if (_work.Length == 0) return;
            try { Directory.Delete(_work, recursive: true); } catch { }
        }
    }

    private readonly Sample _s;

    public CrauzerWinterRiftTests(Sample sample) => _s = sample;

    private static ZipArchive Source() => ZipFile.OpenRead(SamplePath);

    // ===================================================== the import

    [Fact]
    public void ImportsThreeLayersWithTheirPrioritiesAndDisplayNames()
    {
        if (!_s.Available) return;

        Assert.Equal(new[] { "base", "snowdown-baron", "snowdown-minions" }, _s.Project!.Layers.Select(l => l.Name));
        Assert.Equal(new[] { 0, 1, 2 }, _s.Project.Layers.Select(l => l.Priority));
        Assert.Equal(new string?[] { null, "Snowdown Baron", "Snowdown Minions" }, _s.Project.Layers.Select(l => l.DisplayName));
        Assert.Equal(new[] { ("base", 4), ("snowdown-baron", 1), ("snowdown-minions", 8) },
            _s.Import!.Layers.Select(l => (l.Name, l.GameDataModules)));
        Assert.Equal(13, _s.Import.GameDataModules);
        Assert.Equal(new[] { "Map11" }, _s.Project.ProjectFolders);
        Assert.Equal(0, _s.Import.FailedChunks);
    }

    [Fact]
    public void EveryChunkIsNamedFromTheHarvestedTable()
    {
        if (!_s.Available) return;

        Assert.Equal(347, _s.Import!.TableNames);
        Assert.Equal(347, _s.Import.ChunksNamedByTables);
        Assert.Equal(347, _s.Import.ExtractedFiles);

        string map = Path.Combine(_s.Import.RootPath, "Map11");
        var files = Directory.EnumerateFiles(map, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(347, files.Count);
        // not one of them is left under the 16 digits of its hash
        Assert.DoesNotContain(files, f => !Path.GetRelativePath(map, f).Contains(Path.DirectorySeparatorChar) && Path.GetFileNameWithoutExtension(f).Length == 16);
        Assert.Equal(1, files.Count(f => f.EndsWith(".mapgeo", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(346, files.Count(f => f.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)));
        Assert.Null(_s.Import.NotImportedWarning);
    }

    [Fact]
    public void EachLayersGameDataIsStoredByteEqualToTheSourceText()
    {
        if (!_s.Available) return;
        using var src = Source();

        foreach (var layer in new[] { "base", "snowdown-baron", "snowdown-minions" })
        {
            string want = GameData(src, layer)!;
            var stored = LtkProjectStore.ReadLayer(_s.Project!, layer)!;
            Assert.Equal(want, stored.DocumentText);
            Assert.Equal(Encoding.UTF8.GetBytes(want), File.ReadAllBytes(stored.DocumentPath));       // the file is the bytes, not a rendering of them
            Assert.True(stored.Document.IsExpectedShape);
        }
        Assert.Equal(new[] { 4, 1, 8 }, new[] { "base", "snowdown-baron", "snowdown-minions" }.Select(l => LtkProjectStore.ReadLayer(_s.Project!, l)!.Modules.Count));
        Assert.Contains("SRU_OrderMinionMelee draws the Snowdown mesh", LtkProjectStore.ReadLayer(_s.Project!, "snowdown-minions")!.Modules.Select(m => m.Name));
        Assert.Equal("data/maps/shipping/map11/map11.bin", LtkProjectStore.ReadLayer(_s.Project!, "base")!.Modules[0].Target);
    }

    [Fact]
    public void TheMetadataIsKept()
    {
        if (!_s.Available) return;
        var p = _s.Project!;

        Assert.Equal("Winter Rift 2025", p.ModName);
        Assert.Equal("Crauzer", p.ModAuthor);
        Assert.Equal("0.3.0", p.ModVersion);
        Assert.Equal("Summoner's Rift with the 2025 Snowdown event map", p.ModDescription);
        Assert.Equal("MIT for the mod files, Riot Games assets excluded (see LICENSE)", p.ModLicense!.Name);
        Assert.True(p.ModLicense.AsObject);
        Assert.Null(p.ModLicense.Url);
        Assert.Equal(new[] { "map-skin" }, p.ModTags);
        Assert.Equal(new[] { "summoners-rift" }, p.ModMaps);
        Assert.Empty(p.ModChampions);
        Assert.Equal("ltk_mod_project 0.16.2", p.ImportedGenerator);
        Assert.True(p.ThumbnailPath is not null && File.Exists(p.ThumbnailPath));

        var meta = LtkProjectStore.ReadMetaFiles(_s.Import!.RootPath);
        Assert.Equal(new[] { "META/LICENSE", "META/README.md" }, meta.Select(m => m.EntryName));
        using var src = Source();
        Assert.Equal(Bytes(src, "META/README.md"), File.ReadAllBytes(meta.Single(m => m.EntryName == "META/README.md").FullPath));
    }

    [Fact]
    public void TheHashtablesAreKeptWithTheProjectAndTeachTheDictionaryWhenItOpens()
    {
        if (!_s.Available) return;

        var tables = LtkProjectStore.ReadHashtables(_s.Import!.RootPath);
        var table = Assert.Single(tables);
        Assert.True(table.IsWadPaths);
        Assert.Equal("META/hashes/game.harvested.hashes.txt", table.Source);

        var db = new HashDatabase();
        Assert.Equal(347, LtkProjectStore.LoadHashtables(_s.Import.RootPath, db));
        Assert.Equal(347, db.WadCount);
        Assert.Equal(347, LtkProjectStore.LoadHashtables(_s.Import.RootPath, db));              // idempotent
        Assert.Equal(347, db.WadCount);
        Assert.Equal(0, db.ConflictCount);

        // and each packed file's path hashes to the name it was given
        string map = Path.Combine(_s.Import.RootPath, "Map11");
        foreach (var (hash, path) in WadPackService.EnumerateChunkFiles(map).Take(40))
            Assert.True(db.TryGetPath(hash, out var name) && name.Equals(Path.GetRelativePath(map, path).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    // ===================================================== the export

    [Fact]
    public void TheReExportKeepsEachLayersGameDataTextExactly()
    {
        if (!_s.Available) return;
        using var src = Source();
        using var re = ZipFile.OpenRead(_s.ReExport!);

        foreach (var layer in new[] { "base", "snowdown-baron", "snowdown-minions" })
            Assert.Equal(GameData(src, layer), GameData(re, layer));

        // the table is ordered as a reader applies it, the layers keep their numbers and names
        Assert.Equal(new[] { "base", "snowdown-baron", "snowdown-minions" }, LayerKeys(re));
        using var a = JsonDocument.Parse(Bytes(src, "META/info.json"));
        using var b = JsonDocument.Parse(Bytes(re, "META/info.json"));
        foreach (var layer in new[] { "base", "snowdown-baron", "snowdown-minions" })
        {
            var x = a.RootElement.GetProperty("Layers").GetProperty(layer);
            var y = b.RootElement.GetProperty("Layers").GetProperty(layer);
            Assert.Equal(x.GetProperty("Priority").GetInt32(), y.GetProperty("Priority").GetInt32());
            Assert.Equal(x.GetProperty("Name").GetString(), y.GetProperty("Name").GetString());
            Assert.Equal(x.TryGetProperty("DisplayName", out var dx) ? dx.GetString() : null, y.TryGetProperty("DisplayName", out var dy) ? dy.GetString() : null);
        }
    }

    [Fact]
    public void TheReExportKeepsTheMetadata()
    {
        if (!_s.Available) return;
        using var src = Source();
        using var re = ZipFile.OpenRead(_s.ReExport!);
        using var a = JsonDocument.Parse(Bytes(src, "META/info.json"));
        using var b = JsonDocument.Parse(Bytes(re, "META/info.json"));

        foreach (var key in new[] { "Name", "Author", "Version", "Description" })
            Assert.Equal(a.RootElement.GetProperty(key).GetString(), b.RootElement.GetProperty(key).GetString());
        Assert.Equal(a.RootElement.GetProperty("License").GetRawText(), b.RootElement.GetProperty("License").GetRawText());
        Assert.Equal(a.RootElement.GetProperty("Tags").GetRawText(), b.RootElement.GetProperty("Tags").GetRawText());
        Assert.Equal(a.RootElement.GetProperty("Maps").GetRawText(), b.RootElement.GetProperty("Maps").GetRawText());
        Assert.False(b.RootElement.TryGetProperty("Champions", out _));
        Assert.Equal(Bytes(src, "META/README.md"), Bytes(re, "META/README.md"));
        Assert.Equal(Bytes(src, "META/LICENSE"), Bytes(re, "META/LICENSE"));
        Assert.StartsWith("ReyEngine ", b.RootElement.GetProperty("Generator").GetString());     // the tool that wrote this archive
    }

    [Fact]
    public void TheReExportHoldsTheBaseWadChunkForChunk()
    {
        if (!_s.Available) return;
        using var src = Source();
        using var re = ZipFile.OpenRead(_s.ReExport!);
        Assert.Equal(new[] { "WAD/Map11.wad.client" }, re.Entries.Where(e => e.FullName.EndsWith(".wad.client")).Select(e => e.FullName));

        string pa = Path.Combine(_s.Work, "source.wad.client"), pb = Path.Combine(_s.Work, "reexport.wad.client");
        src.GetEntry("WAD/Map11.wad.client")!.ExtractToFile(pa, true);
        re.GetEntry("WAD/Map11.wad.client")!.ExtractToFile(pb, true);
        using var wa = WadArchive.Open(pa);
        using var wb = WadArchive.Open(pb);

        // pack order may differ: compared by hash, and then chunk by chunk
        var ha = wa.Entries.Select(e => e.PathHash).ToHashSet();
        Assert.Equal(347, ha.Count);
        Assert.True(ha.SetEquals(wb.Entries.Select(e => e.PathHash)));
        foreach (var hash in ha) Assert.True(wa.Extract(hash).AsSpan().SequenceEqual(wb.Extract(hash)), $"chunk {hash:x16} differs");
    }

    [Fact]
    public void TheReExportsHashtableCoversTheNamesOfThePackedChunks()
    {
        if (!_s.Available) return;
        using var src = Source();
        using var re = ZipFile.OpenRead(_s.ReExport!);

        string want = Text(src, FantomeHashtables.HarvestedPath), got = Text(re, FantomeHashtables.HarvestedPath);
        Assert.Equal(want, got);                                                                  // the same 347 names, sorted, one per line
        var names = got.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(347, names.Length);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);

        string pb = Path.Combine(_s.Work, "reexport-names.wad.client");
        re.GetEntry("WAD/Map11.wad.client")!.ExtractToFile(pb, true);
        using var wb = WadArchive.Open(pb);
        Assert.True(wb.Entries.Select(e => e.PathHash).ToHashSet().SetEquals(names.Select(Hash)));
        using var info = JsonDocument.Parse(Bytes(re, "META/info.json"));
        Assert.Equal("META/hashes/game.harvested.hashes.txt", info.RootElement.GetProperty("Hashtables")[0].GetProperty("Path").GetString());
    }
}

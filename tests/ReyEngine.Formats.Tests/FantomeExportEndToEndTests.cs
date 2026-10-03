using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meta;
using LogEntry = ReyEngine.Core.Diagnostics.LogEntry;
using LogLevel = ReyEngine.Core.Diagnostics.LogLevel;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M814: Project &gt; Export .fantome through the real view model - staging, the declaration plan, packing, the
/// layered archive - over a synthetic project whose "Riot" copy is a reference WAD built here. The pure pieces
/// are tested on their own (<see cref="BinDeclarationPlannerTests"/>, <see cref="FantomeExporterLayersTests"/>);
/// these pin how the view model puts them together: which bins leave the WADs, which layer a folder rides, what
/// the setting changes, and that Send to LTK Manager and Export are given the same declarations.
/// </summary>
public sealed class FantomeExportEndToEndTests : IDisposable
{
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-e2e-" + Guid.NewGuid().ToString("N"));

    public FantomeExportEndToEndTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

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

    /// <summary>The project, and the reference WAD standing for the installed game.</summary>
    private ReyProject Fixture(bool declare, bool withMapFiles = true, string layerName = "fix")
    {
        string project = Path.Combine(_root, "project");
        string riot = Path.Combine(_root, "riot");

        // what Riot ships
        Put(riot, "data/maps/a.bin", Speed(1f));
        Put(riot, "data/maps/same.bin", Speed(1f));
        Put(riot, "data/maps/whole.bin", Bin(new BinTreeF32(H("speed"), 1f), new BinTreeU32(H("count"), 3)));
        Put(riot, "data/characters/ahri/skins/skin0.bin", Speed(1f));
        string refWad = Path.Combine(_root, "Map11.wad.client");
        Assert.True(WadPackService.Pack(riot, refWad).Success);

        // what the project holds
        string map = Path.Combine(project, "Map11");
        Put(map, "data/maps/a.bin", Speed(2f));                                   // changed: declared
        if (withMapFiles)
        {
            Put(map, "data/maps/same.bin", Speed(1f));                            // identical to Riot's: not shipped
            Put(map, "data/maps/whole.bin", Bin(new BinTreeF32(H("speed"), 1f))); // lost a property: ships whole
            Put(map, "data/maps/new.bin", Speed(7f));                             // Riot has no such bin: new content
            Put(map, "assets/x.tex", new byte[] { 1, 2, 3 });
        }
        string ahri = Path.Combine(project, "Ahri");
        Put(ahri, "data/characters/ahri/skins/skin0.bin", Speed(5f));
        Put(ahri, "assets/y.dds", new byte[] { 4, 5, 6 });

        var p = new ReyProject
        {
            Name = "Synthetic",
            RootPath = project,
            OutputDirectory = Path.Combine(_root, "build"),
            ShipBinEditsAsDeclarations = declare,
            ProjectFolders = { "Map11", "Ahri" },
            ReferenceWads = { refWad },
        };
        p.Layers.Add(new ProjectLayer { Name = layerName, Priority = 10, Folders = { "Ahri" } });
        return p;
    }

    private static FantomeMeta Meta() => new()
    {
        Name = "Synthetic",
        Author = "Test",
        Version = "1.0.0",
        Description = "d",
    };

    private sealed class NoProgress : IProgress<(double Frac, string Stage)>
    {
        public void Report((double Frac, string Stage) value) { }
    }

    private string Export(MainWindowViewModel vm, string name = "out.fantome") => ExportWithOutcome(vm, name).Path;

    /// <summary>Runs the export the command runs and says how it ended: the output path and the name of the
    /// <c>FantomeExportOutcome</c> ("Written" or "NothingToShip"). A failure is thrown as itself.</summary>
    private (string Path, string Outcome) ExportWithOutcome(MainWindowViewModel vm, string name = "out.fantome")
    {
        string output = Path.Combine(_root, name);
        Directory.CreateDirectory(vm.Project.OutputDirectory!);   // the command makes the build folder before it builds
        var method = typeof(MainWindowViewModel).GetMethod("ExportFantomeCore", NonPublic)!;
        try
        {
            object? outcome = method.Invoke(vm, new object?[]
            {
                output, Meta(), null, vm.Project.OutputDirectory, new NoProgress(), "Export", "No WAD was produced.", "Zipping…",
            });
            return (output, outcome!.ToString()!);
        }
        catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
    }

    private static JsonDocument Info(string fantome)
    {
        using var zip = ZipFile.OpenRead(fantome);
        using var s = zip.GetEntry("META/info.json")!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return JsonDocument.Parse(ms.ToArray());
    }

    /// <summary>The chunk hashes of a packed WAD stored in the archive.</summary>
    private HashSet<ulong> Chunks(string fantome, string entry)
    {
        using var zip = ZipFile.OpenRead(fantome);
        string path = Path.Combine(_root, "unpacked", Guid.NewGuid().ToString("N") + ".wad.client");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var s = zip.GetEntry(entry)!.Open())
        using (var f = File.Create(path))
            s.CopyTo(f);
        using var wad = WadArchive.Open(path);
        return wad.Entries.Select(e => e.PathHash).ToHashSet();
    }

    /// <summary>The one edited value of a module: its one entry's one key. The names are the hash database's to give
    /// (the synthetic ones may or may not be in it), so the test does not depend on how they are spelled.</summary>
    private static JsonElement OnlyValue(JsonElement module) =>
        module.GetProperty("edits")[0].EnumerateObject().Single().Value.EnumerateObject().Single().Value;

    private static ulong Hash(string path) => HashAlgorithms.WadPath(path);

    private static string[] Names(string fantome)
    {
        using var zip = ZipFile.OpenRead(fantome);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }

    // ===================================================== the setting on

    [Fact]
    public void WithTheSettingOnChangedGameBinsAreGameDataAndNotPacked()
    {
        var vm = new MainWindowViewModel { Project = Fixture(declare: true) };
        string fantome = Export(vm);

        var names = Names(fantome);
        Assert.Contains("WAD/Map11.wad.client", names);
        Assert.Contains("WAD_fix/Ahri.wad.client", names);
        Assert.Contains(FantomeHashtables.HarvestedPath, names);

        // the declared bin (a.bin) and the identical one (same.bin) are not in the WAD; the rest is
        var map = Chunks(fantome, "WAD/Map11.wad.client");
        Assert.DoesNotContain(Hash("data/maps/a.bin"), map);
        Assert.DoesNotContain(Hash("data/maps/same.bin"), map);
        Assert.Contains(Hash("data/maps/whole.bin"), map);
        Assert.Contains(Hash("data/maps/new.bin"), map);
        Assert.Contains(Hash("assets/x.tex"), map);

        var ahri = Chunks(fantome, "WAD_fix/Ahri.wad.client");
        Assert.DoesNotContain(Hash("data/characters/ahri/skins/skin0.bin"), ahri);
        Assert.Contains(Hash("assets/y.dds"), ahri);

        using var info = Info(fantome);
        var layers = info.RootElement.GetProperty("Layers");
        Assert.Equal(new[] { "base", "fix" }, layers.EnumerateObject().Select(l => l.Name).ToArray());
        Assert.Equal("ReyEngine " + ReyEngine.App.AppInfo.Version, info.RootElement.GetProperty("Generator").GetString());

        var baseData = layers.GetProperty("base").GetProperty("GameData");
        Assert.Equal(1, baseData.GetProperty("version").GetInt32());
        var baseModule = Assert.Single(baseData.GetProperty("modules").EnumerateArray());
        Assert.Equal("data/maps/a.bin", baseModule.GetProperty("target").GetString());
        Assert.Equal(0, baseModule.GetProperty("origin").GetProperty("module").GetInt32());
        Assert.Equal(2, OnlyValue(baseModule).GetDouble());

        var fixData = layers.GetProperty("fix").GetProperty("GameData");
        var fixModule = Assert.Single(fixData.GetProperty("modules").EnumerateArray());
        Assert.Equal("data/characters/ahri/skins/skin0.bin", fixModule.GetProperty("target").GetString());
        Assert.Equal(5, OnlyValue(fixModule).GetDouble());

        // no module carries a name: ltk_game_data 0.6.0 (LTK Manager v1.21.0) refuses the whole layer for one
        Assert.False(FantomeLayers.ModuleNames);
        Assert.Equal(new[] { "target", "edits", "origin" }, baseModule.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "target", "edits", "origin" }, fixModule.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void TheHarvestedTableNamesWhatIsPackedAndNotWhatIsDeclared()
    {
        var vm = new MainWindowViewModel { Project = Fixture(declare: true) };
        string fantome = Export(vm);

        using var zip = ZipFile.OpenRead(fantome);
        using var reader = new StreamReader(zip.GetEntry(FantomeHashtables.HarvestedPath)!.Open());
        var names = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[]
        {
            "assets/x.tex", "assets/y.dds", "data/maps/new.bin", "data/maps/whole.bin",
        }, names);
    }

    [Fact]
    public void ABinThatShipsWholeIsPackedBitForBitFromTheProject()
    {
        var project = Fixture(declare: true);
        var vm = new MainWindowViewModel { Project = project };
        string fantome = Export(vm);

        using var zip = ZipFile.OpenRead(fantome);
        string path = Path.Combine(_root, "whole.wad.client");
        using (var s = zip.GetEntry("WAD/Map11.wad.client")!.Open())
        using (var f = File.Create(path)) s.CopyTo(f);
        using var wad = WadArchive.Open(path);
        wad.TryGetEntry(Hash("data/maps/whole.bin"), out var entry);
        Assert.Equal(File.ReadAllBytes(Path.Combine(project.RootPath!, "Map11", "data", "maps", "whole.bin")), wad.Extract(entry));
    }

    /// <summary>The project's own files are never touched: the declared bins are removed from the STAGED copy.</summary>
    [Fact]
    public void TheProjectsOwnFilesAreNeverTouched()
    {
        var project = Fixture(declare: true);
        var vm = new MainWindowViewModel { Project = project };
        var before = Directory.EnumerateFiles(project.RootPath!, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => File.ReadAllBytes(f));
        Export(vm);
        var after = Directory.EnumerateFiles(project.RootPath!, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => File.ReadAllBytes(f));
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (file, bytes) in before) Assert.Equal(bytes, after[file]);
    }

    /// <summary>Send to LTK Manager and Export .fantome are given the same declarations: one planner, one result.</summary>
    [Fact]
    public void SendToLtkManagerAndExportAreGivenTheSameDeclarations()
    {
        var project = Fixture(declare: true);
        var vm = new MainWindowViewModel { Project = project };
        string fantome = Export(vm);

        // Send to LTK Manager's side: the YAML manifests over the project's own files
        var files = new List<(string Layer, string WadFolder, string RelPath, string AbsPath)>();
        foreach (var folder in project.ProjectFolders)
        {
            string abs = project.ResolveProjectPath(folder);
            foreach (var (_, path) in WadPackService.EnumerateChunkFiles(abs))
                files.Add((project.LayerOf(Path.GetFileName(abs)), Path.GetFileName(abs), Path.GetRelativePath(abs, path).Replace('\\', '/'), path));
        }
        var declare = typeof(MainWindowViewModel).GetMethod("DeclareGameBins", NonPublic)!;
        dynamic sent = declare.Invoke(vm, new object[] { files })!;
        Dictionary<string, string> manifests = sent.Item2;
        List<(string Layer, string WadFolder, string RelPath, string AbsPath)> keptBySend = sent.Item1;

        using var info = Info(fantome);
        var layers = info.RootElement.GetProperty("Layers");
        Assert.Equal(manifests.Keys.OrderBy(k => k, StringComparer.Ordinal), new[] { "base", "fix" });
        foreach (var (layer, yaml) in manifests)
        {
            // the same modules, in the same order, with the same number of edits per module
            var modules = layers.GetProperty(layer).GetProperty("GameData").GetProperty("modules").EnumerateArray().ToArray();
            var yamlTargets = System.Text.RegularExpressions.Regex.Matches(yaml, "^  - target: \"(.+)\"$", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(yamlTargets, modules.Select(m => m.GetProperty("target").GetString()).ToArray());
        }

        // what Send keeps as files is exactly what Export packs
        var map = Chunks(fantome, "WAD/Map11.wad.client");
        var ahri = Chunks(fantome, "WAD_fix/Ahri.wad.client");
        var packed = map.Concat(ahri).ToHashSet();
        Assert.Equal(keptBySend.Select(k => Hash(k.RelPath)).Order(), packed.Order());
    }

    // ===================================================== the log

    private static List<ReyEngine.Core.Diagnostics.LogEntry> CaptureLog(MainWindowViewModel vm)
    {
        var lines = new List<ReyEngine.Core.Diagnostics.LogEntry>();
        ((ReyEngine.Core.Diagnostics.Logger)typeof(MainWindowViewModel).GetField("_log", NonPublic)!.GetValue(vm)!).Logged +=
            e => { lock (lines) lines.Add(e); };
        return lines;
    }

    /// <summary>The export log names the layers, the declared bins, the bins that ship whole with their reason, and
    /// warns that GameData is for LTK Manager only.</summary>
    [Fact]
    public void TheExportLogNamesLayersDeclaredBinsAndWholeBinsAndWarnsAboutGameData()
    {
        var vm = new MainWindowViewModel { Project = Fixture(declare: true) };
        var log = CaptureLog(vm);
        Export(vm);

        List<ReyEngine.Core.Diagnostics.LogEntry> lines;
        lock (log) lines = log.Where(l => l.Category == "Export").ToList();
        string all = string.Join("\n", lines.Select(l => l.Message));

        Assert.Contains("Layers - base (priority 0): 1 WAD(s), 1 declared bin(s); fix (priority 10): 1 WAD(s), 1 declared bin(s).", all);
        Assert.Contains("Declarations: 2 game bin(s) shipped as changes", all);
        Assert.Contains("1 unchanged bin(s) not shipped, 1 shipped whole.", all);
        Assert.Contains("declared [base] data/maps/a.bin: 1 propert(ies)", all);
        Assert.Contains("declared [fix] data/characters/ahri/skins/skin0.bin: 1 propert(ies)", all);
        Assert.Contains("shipped whole - Map11/data/maps/whole.bin: ", all);
        Assert.Contains("no declaration removes a property", all);
        Assert.Contains("1 bin(s) have no game copy", all);                      // new.bin

        var layerWarning = Assert.Single(lines, l => l.Level == ReyEngine.Core.Diagnostics.LogLevel.Warning && l.Message.Contains("WAD_<layer>"));
        Assert.Contains("fix", layerWarning.Message);                            // the layer with WAD content
        Assert.DoesNotContain("base", layerWarning.Message);                     // base is in WAD/, which everything reads

        var warning = Assert.Single(lines, l => l.Level == ReyEngine.Core.Diagnostics.LogLevel.Warning && l.Message.Contains("GameData"));
        Assert.Contains("cslol-manager", warning.Message);
        // the modules carry no names, so there is no "needs a newer manager" caveat to give
        Assert.DoesNotContain("ltk_game_data", warning.Message);
        Assert.False(warning.Message.EndsWith(' '));
    }

    [Fact]
    public void WithTheSettingOffTheLogNamesTheLayersAndHasNoGameDataWarning()
    {
        var vm = new MainWindowViewModel { Project = Fixture(declare: false) };
        var log = CaptureLog(vm);
        Export(vm);

        List<ReyEngine.Core.Diagnostics.LogEntry> lines;
        lock (log) lines = log.Where(l => l.Category == "Export").ToList();
        Assert.Contains(lines, l => l.Message.StartsWith("Layers - base (priority 0): 1 WAD(s), 0 declared bin(s)", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Message.Contains("GameData") || l.Message.StartsWith("Declarations:", StringComparison.Ordinal));
    }

    // ===================================================== the setting off

    [Fact]
    public void WithTheSettingOffEveryBinShipsWholeAndThereIsNoGameData()
    {
        var vm = new MainWindowViewModel { Project = Fixture(declare: false) };
        string fantome = Export(vm);

        var map = Chunks(fantome, "WAD/Map11.wad.client");
        foreach (var path in new[] { "data/maps/a.bin", "data/maps/same.bin", "data/maps/whole.bin", "data/maps/new.bin", "assets/x.tex" })
            Assert.Contains(Hash(path), map);
        var ahri = Chunks(fantome, "WAD_fix/Ahri.wad.client");
        Assert.Contains(Hash("data/characters/ahri/skins/skin0.bin"), ahri);

        using var info = Info(fantome);
        foreach (var layer in info.RootElement.GetProperty("Layers").EnumerateObject())
            Assert.False(layer.Value.TryGetProperty("GameData", out _), layer.Name);
        // layered, with a harvested table, and a generator, all the same
        Assert.Equal(new[] { "base", "fix" }, info.RootElement.GetProperty("Layers").EnumerateObject().Select(l => l.Name).ToArray());
        Assert.True(info.RootElement.TryGetProperty("Hashtables", out _));
        Assert.True(info.RootElement.TryGetProperty("Generator", out _));
    }

    [Fact]
    public void BuildPackageStillBuildsWholeBins()
    {
        // Build Package passes no declaration request: its WADs are what they always were
        var project = Fixture(declare: true);
        var vm = new MainWindowViewModel { Project = project };
        Directory.CreateDirectory(project.OutputDirectory!);
        var build = typeof(MainWindowViewModel).GetMethod("BuildProjectCore", NonPublic)!
            .Invoke(vm, new object?[] { project.OutputDirectory, null, false })!;
        var wads = (System.Collections.IEnumerable)build.GetType().GetProperty("Wads")!.GetValue(build)!;
        var map = wads.Cast<FantomeWad>().Single(w => w.Path.EndsWith("Map11.wad.client", StringComparison.Ordinal));
        using var wad = WadArchive.Open(map.Path);
        Assert.True(wad.TryGetEntry(Hash("data/maps/a.bin"), out _));
        Assert.Null(build.GetType().GetProperty("Declarations")!.GetValue(build));
    }

    [Fact]
    public void AnEmptyProjectFolderBuildsAndExportsWithoutAWadOfItsOwn()
    {
        // An EMPTY project folder threw DirectoryNotFoundException out of Build Package and Export: CopyTree staged nothing for
        // it and the M768 mapgeo pass enumerated the missing directory. It is now staged, packs no WAD, and the rest builds.
        var project = Fixture(declare: false);
        Directory.CreateDirectory(Path.Combine(project.RootPath!, "Empty"));
        project.ProjectFolders.Add("Empty");
        var vm = new MainWindowViewModel { Project = project };
        Directory.CreateDirectory(project.OutputDirectory!);

        var build = typeof(MainWindowViewModel).GetMethod("BuildProjectCore", NonPublic)!
            .Invoke(vm, new object?[] { project.OutputDirectory, null, false })!;
        var wads = ((System.Collections.IEnumerable)build.GetType().GetProperty("Wads")!.GetValue(build)!).Cast<FantomeWad>().ToList();
        Assert.DoesNotContain(wads, w => w.Path.EndsWith("Empty.wad.client", StringComparison.Ordinal));
        Assert.Contains(wads, w => w.Path.EndsWith("Map11.wad.client", StringComparison.Ordinal));

        var names = Names(Export(vm));
        Assert.DoesNotContain(names, n => n.Contains("Empty", StringComparison.Ordinal));
        Assert.Contains("WAD/Map11.wad.client", names);
    }

    // ===================================================== layers and what a package may be

    [Fact]
    public void AFolderWhoseEveryFileIsADeclarationPacksNoWadAtAll()
    {
        var project = Fixture(declare: true, withMapFiles: false);   // Map11 holds only the changed bin
        var vm = new MainWindowViewModel { Project = project };
        string fantome = Export(vm);

        var names = Names(fantome);
        Assert.DoesNotContain("WAD/Map11.wad.client", names);       // nothing left to pack
        Assert.Contains("WAD_fix/Ahri.wad.client", names);
        using var info = Info(fantome);
        Assert.True(info.RootElement.GetProperty("Layers").GetProperty("base").TryGetProperty("GameData", out _));
    }

    [Fact]
    public void ALayerNameTheFormatCannotCarryStopsTheExportBeforeAnythingIsBuilt()
    {
        var project = Fixture(declare: true, layerName: "my layer");
        var vm = new MainWindowViewModel { Project = project };

        var ex = Assert.Throws<InvalidOperationException>(() => Export(vm));
        Assert.Contains("my layer", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(project.OutputDirectory!, "staged")));
        Assert.False(File.Exists(Path.Combine(_root, "out.fantome")));
    }

    /// <summary>M814 review: a project that re-declares its own base layer (Send to LTK Manager accepts that, so
    /// Export does) puts both folders in base: both WADs are entries of <c>WAD/</c> and there is one layer.</summary>
    [Fact]
    public void AProjectThatRedeclaresBaseExportsBothFoldersIntoWadAndOneBaseLayer()
    {
        var vm = new MainWindowViewModel { Project = Fixture(declare: true, layerName: "base") };
        string fantome = Export(vm);

        var names = Names(fantome);
        Assert.Contains("WAD/Map11.wad.client", names);
        Assert.Contains("WAD/Ahri.wad.client", names);
        Assert.DoesNotContain(names, n => n.StartsWith("WAD_", StringComparison.Ordinal));
        using var info = Info(fantome);
        var layers = info.RootElement.GetProperty("Layers");
        Assert.Equal(new[] { "base" }, layers.EnumerateObject().Select(l => l.Name).ToArray());
        Assert.Equal(0, layers.GetProperty("base").GetProperty("Priority").GetInt32());
        // a.bin and skin0.bin are both declared into the one layer
        Assert.Equal(2, layers.GetProperty("base").GetProperty("GameData").GetProperty("modules").GetArrayLength());
    }

    [Fact]
    public void NothingToPackAndNothingDeclaredIsStillTheEmptyProjectError()
    {
        var project = new ReyProject
        {
            Name = "Empty",
            RootPath = Path.Combine(_root, "empty"),
            OutputDirectory = Path.Combine(_root, "build"),
        };
        Directory.CreateDirectory(project.RootPath!);
        var vm = new MainWindowViewModel { Project = project };
        var ex = Assert.Throws<InvalidOperationException>(() => Export(vm));
        Assert.Equal("No WAD was produced.", ex.Message);
    }

    // ===================================================== a project that matches the game (M814 review)

    /// <summary>A project whose every bin is identical to Riot's copy of it, and optionally one asset that is not a bin.</summary>
    private ReyProject MatchingFixture(bool declare, bool withAsset = false)
    {
        string project = Path.Combine(_root, "project");
        string riot = Path.Combine(_root, "riot");
        Put(riot, "data/maps/a.bin", Speed(1f));
        Put(riot, "data/maps/same.bin", Speed(1f));
        string refWad = Path.Combine(_root, "Map11.wad.client");
        Assert.True(WadPackService.Pack(riot, refWad).Success);

        string map = Path.Combine(project, "Map11");
        Put(map, "data/maps/a.bin", Speed(1f));
        Put(map, "data/maps/same.bin", Speed(1f));
        if (withAsset) Put(map, "assets/x.tex", new byte[] { 1, 2, 3 });
        return new ReyProject
        {
            Name = "Synthetic",
            RootPath = project,
            OutputDirectory = Path.Combine(_root, "build"),
            ShipBinEditsAsDeclarations = declare,
            ProjectFolders = { "Map11" },
            ReferenceWads = { refWad },
        };
    }

    /// <summary>Everything matching the game is the answer, not a failure: no error, no empty mod, and the reason in the log.</summary>
    [Fact]
    public void AProjectThatMatchesTheGameBinForBinShipsNothingAndSaysSoWithoutFailing()
    {
        var vm = new MainWindowViewModel { Project = MatchingFixture(declare: true) };
        var log = CaptureLog(vm);

        var (path, outcome) = ExportWithOutcome(vm);          // does not throw

        Assert.Equal("NothingToShip", outcome);
        Assert.False(File.Exists(path), "an empty mod must not be written");
        Assert.False(File.Exists(path + ".tmp"));

        List<LogEntry> lines;
        lock (log) lines = log.Where(l => l.Category == "Export").ToList();
        Assert.DoesNotContain(lines, l => l.Level is LogLevel.Error or LogLevel.Warning);
        var nothing = Assert.Single(lines, l => l.Message.StartsWith("Nothing to ship:", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Info, nothing.Level);
        Assert.Contains("all 2 game bin(s) in the project are identical to the game's own copy", nothing.Message);
        Assert.Contains("No .fantome was written", nothing.Message);
        Assert.Contains(lines, l => l.Message.StartsWith("Declarations: 0 game bin(s) shipped as changes", StringComparison.Ordinal)
            && l.Message.Contains("2 unchanged bin(s) not shipped"));
        Assert.DoesNotContain(lines, l => l.Message.StartsWith("Layers -", StringComparison.Ordinal));   // no layout: nothing was written
    }

    [Fact]
    public void NothingToShipLeavesAnEarlierPackageAtThatPathAsItWas()
    {
        var vm = new MainWindowViewModel { Project = MatchingFixture(declare: true) };
        string output = Path.Combine(_root, "out.fantome");
        File.WriteAllText(output, "an earlier package");

        var (_, outcome) = ExportWithOutcome(vm);

        Assert.Equal("NothingToShip", outcome);
        Assert.Equal("an earlier package", File.ReadAllText(output));
    }

    [Fact]
    public void WithTheSettingOffAMatchingProjectStillShipsItsBinsWhole()
    {
        var vm = new MainWindowViewModel { Project = MatchingFixture(declare: false) };

        var (path, outcome) = ExportWithOutcome(vm);

        Assert.Equal("Written", outcome);
        var map = Chunks(path, "WAD/Map11.wad.client");
        Assert.Contains(Hash("data/maps/a.bin"), map);
        Assert.Contains(Hash("data/maps/same.bin"), map);
    }

    [Fact]
    public void MatchingBinsBesideOtherContentStillWriteAPackageOfThatContent()
    {
        var vm = new MainWindowViewModel { Project = MatchingFixture(declare: true, withAsset: true) };

        var (path, outcome) = ExportWithOutcome(vm);

        Assert.Equal("Written", outcome);
        var map = Chunks(path, "WAD/Map11.wad.client");
        Assert.Contains(Hash("assets/x.tex"), map);
        Assert.DoesNotContain(Hash("data/maps/a.bin"), map);          // matches the game: not shipped
        using var info = Info(path);
        Assert.False(info.RootElement.GetProperty("Layers").GetProperty("base").TryGetProperty("GameData", out _));
    }

    /// <summary>"Nothing to ship" is for a project that matches the game. A project with nothing in it to compare is
    /// still the error, with the setting on as it is with it off.</summary>
    [Fact]
    public void AProjectWithNoBinsToCompareIsStillTheEmptyProjectErrorWithTheSettingOn()
    {
        var project = new ReyProject
        {
            Name = "Empty",
            RootPath = Path.Combine(_root, "empty"),
            OutputDirectory = Path.Combine(_root, "build"),
            ShipBinEditsAsDeclarations = true,
        };
        Directory.CreateDirectory(project.RootPath!);
        var vm = new MainWindowViewModel { Project = project };

        var ex = Assert.Throws<InvalidOperationException>(() => Export(vm));

        Assert.Equal("No WAD was produced.", ex.Message);
    }

    private static async Task<bool> Rebuild(MainWindowViewModel vm, ReyProject project)
    {
        var method = typeof(MainWindowViewModel).GetMethod("BuildUpdatedProjectArtifactsAsync", NonPublic)!;
        return await (Task<bool>)method.Invoke(vm, new object[] { project })!;
    }

    /// <summary>The rebuild after a Riot patch records "nothing to ship" as the update's outcome. It is not "Automatic build
    /// failed" and it asks the user to review nothing.</summary>
    [Fact]
    public async Task TheAutomaticRebuildOfAMatchingProjectRecordsNothingToShipAndAsksForNoReview()
    {
        var project = MatchingFixture(declare: true);
        project.ProjectFilePath = Path.Combine(_root, "saved", "project.json");
        project.LastPatchUpdateSummary = "Rebased 2 bin(s).";
        project.PatchUpdateNeedsReview = false;
        project.RiotPatchVersion = "16.20";
        var vm = new MainWindowViewModel { Project = project };
        var log = CaptureLog(vm);

        bool ok = await Rebuild(vm, project);

        Assert.True(ok);
        Assert.False(project.PatchUpdateNeedsReview);
        Assert.StartsWith("Rebased 2 bin(s).", project.LastPatchUpdateSummary);
        Assert.Contains("Nothing to ship: every bin in the project is identical to the game's, so no package was built.", project.LastPatchUpdateSummary);
        Assert.DoesNotContain("Automatic build failed", project.LastPatchUpdateSummary);
        Assert.Empty(Directory.EnumerateFiles(project.OutputDirectory!, "*.fantome"));

        // and it is on disk, where the next session reads it
        var saved = ReyProjectService.Open(project.ProjectFilePath!);
        Assert.Contains("Nothing to ship", saved.LastPatchUpdateSummary);
        Assert.False(saved.PatchUpdateNeedsReview);

        List<LogEntry> lines;
        lock (log) lines = log.Where(l => l.Category == "PatchUpdate").ToList();
        Assert.DoesNotContain(lines, l => l.Level == LogLevel.Error);
        Assert.Contains(lines, l => l.Level == LogLevel.Info && l.Message.StartsWith("Nothing to ship after the update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEarlierPackageIsNamedWhenTheRebuildLeavesItBehind()
    {
        var project = MatchingFixture(declare: true);
        project.ProjectFilePath = Path.Combine(_root, "saved", "project.json");
        project.LastPatchUpdateSummary = "Rebased.";
        Directory.CreateDirectory(project.OutputDirectory!);
        string earlier = Path.Combine(project.OutputDirectory!, "Synthetic by Unknown.fantome");
        File.WriteAllText(earlier, "last patch's package");
        var vm = new MainWindowViewModel { Project = project };

        Assert.True(await Rebuild(vm, project));

        Assert.Equal("last patch's package", File.ReadAllText(earlier));          // not replaced, not deleted
        Assert.Contains("The earlier package Synthetic by Unknown.fantome was left as it was and no longer matches the project.",
            project.LastPatchUpdateSummary);
    }

    /// <summary>The contrast: a project with nothing to package at all is still a failed automatic build that needs a review.</summary>
    [Fact]
    public async Task TheAutomaticRebuildOfAnEmptyProjectIsStillAFailureThatNeedsReview()
    {
        var project = new ReyProject
        {
            Name = "Empty",
            RootPath = Path.Combine(_root, "empty"),
            OutputDirectory = Path.Combine(_root, "build"),
            ShipBinEditsAsDeclarations = true,
            ProjectFilePath = Path.Combine(_root, "saved", "project.json"),
            LastPatchUpdateSummary = "Rebased.",
        };
        Directory.CreateDirectory(project.RootPath!);
        var vm = new MainWindowViewModel { Project = project };

        bool ok = await Rebuild(vm, project);

        Assert.False(ok);
        Assert.True(project.PatchUpdateNeedsReview);
        Assert.Contains("Automatic build failed: No WAD was produced from the updated project.", project.LastPatchUpdateSummary);
    }

    [Fact]
    public async Task TheAutomaticRebuildOfAChangedProjectStillWritesItsPackage()
    {
        var project = Fixture(declare: true);
        project.ProjectFilePath = Path.Combine(_root, "saved", "project.json");
        project.LastPatchUpdateSummary = "Rebased.";
        var vm = new MainWindowViewModel { Project = project };

        Assert.True(await Rebuild(vm, project));

        Assert.False(project.PatchUpdateNeedsReview);
        Assert.Equal("Rebased.", project.LastPatchUpdateSummary);
        var package = Assert.Single(Directory.EnumerateFiles(project.OutputDirectory!, "*.fantome"));
        using var info = Info(package);
        Assert.True(info.RootElement.GetProperty("Layers").GetProperty("base").TryGetProperty("GameData", out _));
    }

    // ===================================================== reference WADs are opened once per plan (M814 review)

    /// <summary>Adds what makes the old lookup slow: twelve bins no reference holds (new content falls through every
    /// reference), a second real reference, one that is not there and one that is not a WAD. Returns every reference.</summary>
    private string[] WithAwkwardReferences(ReyProject project)
    {
        string map = Path.Combine(project.RootPath!, "Map11");
        for (int i = 0; i < 12; i++) Put(map, $"data/maps/new{i}.bin", Speed(i + 10f));

        string other = SecondReference();
        string missing = Path.Combine(_root, "Missing.wad.client");
        string corrupt = Path.Combine(_root, "Corrupt.wad.client");
        File.WriteAllBytes(corrupt, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7)).ToArray());
        project.ReferenceWads.AddRange(new[] { other, missing, corrupt });
        return project.ReferenceWads.ToArray();
    }

    private static Dictionary<string, int> CountOpens(MainWindowViewModel vm)
    {
        var opens = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Func<string, IReferenceWad?> counting = path =>
        {
            lock (opens) opens[path] = opens.GetValueOrDefault(path) + 1;
            return ReferenceWadSet.OpenFile(path);
        };
        typeof(MainWindowViewModel).GetField("_referenceWadOpener", NonPublic)!.SetValue(vm, counting);
        return opens;
    }

    /// <summary>A plan asks the references about every bin of the project; the ones the game does not have fall through
    /// every reference. Each reference is opened ONCE for the whole run - the old per-bin lookup opened every one of
    /// them for each of those bins (16 bins here, so 16 opens of each reference that is reached, not 1).</summary>
    [Fact]
    public void AnExportOpensEachReferenceWadAtMostOnceHoweverManyBinsAskForThem()
    {
        var project = Fixture(declare: true);
        var references = WithAwkwardReferences(project);
        var vm = new MainWindowViewModel { Project = project };
        var opens = CountOpens(vm);

        string fantome = Export(vm);

        Assert.Equal(references.Length, opens.Count);                 // every reference was reached: the new bins fell through them all
        Assert.All(opens, kv => Assert.True(kv.Value == 1, $"{Path.GetFileName(kv.Key)} was opened {kv.Value} times"));

        // and the answers are what they were: new content ships as files, the changed game bin is declared
        var map = Chunks(fantome, "WAD/Map11.wad.client");
        for (int i = 0; i < 12; i++) Assert.Contains(Hash($"data/maps/new{i}.bin"), map);
        Assert.DoesNotContain(Hash("data/maps/a.bin"), map);
        using var info = Info(fantome);
        Assert.Equal(1, info.RootElement.GetProperty("Layers").GetProperty("base").GetProperty("GameData").GetProperty("modules").GetArrayLength());
    }

    private string SecondReference()
    {
        string otherSrc = Path.Combine(_root, "other-src");
        Put(otherSrc, "data/other/b.bin", Speed(3f));
        string other = Path.Combine(_root, "Other.wad.client");
        Assert.True(WadPackService.Pack(otherSrc, other).Success);
        return other;
    }

    /// <summary>A reference is opened when a lookup reaches it, not before: lookups the first reference answers never open the second.</summary>
    [Fact]
    public void AReferenceNoLookupReachesIsNeverOpened()
    {
        var project = MatchingFixture(declare: true);                  // both bins are in the first reference
        string other = SecondReference();
        project.ReferenceWads.Add(other);
        var vm = new MainWindowViewModel { Project = project };
        var opens = CountOpens(vm);

        var (_, outcome) = ExportWithOutcome(vm);

        Assert.Equal("NothingToShip", outcome);
        Assert.Equal(1, opens[project.ReferenceWads[0]]);
        Assert.DoesNotContain(other, opens.Keys);
    }

    /// <summary>...and once a lookup does reach it, it is opened that once however many more lookups follow.</summary>
    [Fact]
    public void AReferenceThatALookupReachesIsOpenedOnce()
    {
        var project = Fixture(declare: true);                          // new.bin is in no reference
        string other = SecondReference();
        project.ReferenceWads.Add(other);
        var vm = new MainWindowViewModel { Project = project };
        var opens = CountOpens(vm);

        Export(vm);

        Assert.Equal(1, opens[project.ReferenceWads[0]]);
        Assert.Equal(1, opens[other]);
    }

    private static IDeclarationNames NamesOf(MainWindowViewModel vm)
    {
        var resolver = typeof(MainWindowViewModel).GetField("_resolver", NonPublic)!.GetValue(vm)!;
        var db = resolver.GetType().GetProperty("Database")!.GetValue(resolver)!;
        var type = typeof(MainWindowViewModel).GetNestedType("DeclarationNames", BindingFlags.NonPublic)!;
        return (IDeclarationNames)Activator.CreateInstance(type, db)!;
    }

    /// <summary>The shared set changes how often a reference is opened and nothing else: the plan it gives is the plan
    /// the one-argument lookup gives, which opens every reference for each bin as the old code did.</summary>
    [Fact]
    public void TheSharedSetGivesTheSamePlanAsOpeningEveryReferenceForEveryBin()
    {
        var project = Fixture(declare: true);
        WithAwkwardReferences(project);
        var vm = new MainWindowViewModel { Project = project };

        var files = new List<DeclarationFile>();
        foreach (var folder in project.ProjectFolders)
        {
            string abs = project.ResolveProjectPath(folder);
            string leaf = Path.GetFileName(abs);
            foreach (var (_, path) in WadPackService.EnumerateChunkFiles(abs))
                files.Add(new DeclarationFile(project.LayerOf(leaf), leaf, Path.GetRelativePath(abs, path).Replace('\\', '/'), path));
        }

        var shared = (DeclarationPlan)typeof(MainWindowViewModel).GetMethod("PlanDeclarations", NonPublic)!.Invoke(vm, new object[] { files })!;

        var readOne = typeof(MainWindowViewModel).GetMethod("ReadRiotOriginalBytes", NonPublic, null, new[] { typeof(WadAssetEntry) }, null)!;
        var perBin = BinDeclarationPlanner.Plan(files,
            (hash, rel) => (byte[]?)readOne.Invoke(vm, new object[] { new WadAssetEntry { PathHash = hash, Path = rel } }),
            NamesOf(vm));

        Assert.Equal(perBin.Kept.Select(f => f.RelPath), shared.Kept.Select(f => f.RelPath));
        Assert.Equal(perBin.Dropped.Select(f => f.RelPath), shared.Dropped.Select(f => f.RelPath));
        Assert.Equal(perBin.DeclaredBins.Select(b => (b.Layer, b.RelPath)), shared.DeclaredBins.Select(b => (b.Layer, b.RelPath)));
        Assert.Equal(perBin.Whole, shared.Whole);
        Assert.Equal(perBin.NoGameCopy, shared.NoGameCopy);
        Assert.Equal((perBin.Declared, perBin.Unchanged, perBin.Properties, perBin.ObjectsAdded, perBin.ObjectsRemoved),
            (shared.Declared, shared.Unchanged, shared.Properties, shared.ObjectsAdded, shared.ObjectsRemoved));
        foreach (var (layer, chunks) in perBin.Modules)
            Assert.Equal(
                BinDeclarations.GameDataDocument(chunks).ToJsonString(),
                BinDeclarations.GameDataDocument(shared.Modules[layer]).ToJsonString());
        Assert.Equal(perBin.Modules.Keys.Order(), shared.Modules.Keys.Order());
        Assert.True(shared.Declared >= 1 && shared.NoGameCopy.Count >= 12, "the comparison has something to compare");
    }

    // ===================================================== importing the export back (M814 review)

    private static void LogImport(MainWindowViewModel vm, FantomeImportResult result) =>
        typeof(MainWindowViewModel).GetMethod("LogFantomeImport", NonPublic)!.Invoke(vm, new object[] { result });

    private FantomeImportResult ImportBack(string fantome)
    {
        string projects = Path.Combine(_root, "imported");
        Directory.CreateDirectory(projects);
        return FantomeImporter.Import(fantome, projects, null, new HashDatabase());
    }

    /// <summary>The export carries Map11 in base, Ahri in WAD_fix/, and two declared bins (one per layer). M816: the import brings
    /// in all of it - both WADs, both layers, both modules - and the log says what came in, with no warning.</summary>
    [Fact]
    public void ImportingALayeredExportBringsEverythingInAndLogsWhatCameIn()
    {
        var vm = new MainWindowViewModel { Project = Fixture(declare: true) };
        string fantome = Export(vm);

        var result = ImportBack(fantome);

        Assert.Equal(2, result.Wads);                                  // Map11 from WAD/, and Ahri from WAD_fix/
        Assert.Equal(1, result.LayerWads);
        Assert.Equal(2, result.GameDataModules);                       // a.bin (base) and skin0.bin (fix)
        Assert.Equal(new[] { ("base", 1, 1), ("fix", 1, 1) }, result.Layers.Select(l => (l.Name, l.Wads, l.GameDataModules)).ToArray());
        Assert.True(Directory.Exists(Path.Combine(result.RootPath, "Map11")));
        Assert.True(Directory.Exists(Path.Combine(result.RootPath, "layers", "fix", "Ahri")));
        Assert.Null(result.NotImportedWarning);

        var log = CaptureLog(vm);
        LogImport(vm, result);
        List<LogEntry> lines;
        lock (log) lines = log.Where(l => l.Category == "Import").ToList();
        Assert.Contains(lines, l => l.Level == LogLevel.Success && l.Message.StartsWith("Synthetic: 2 WAD(s)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Level == LogLevel.Info && l.Message.StartsWith("Layers - base (priority 0): 1 WAD(s), 1 GameData module(s); fix (priority 10): 1 WAD(s), 1 GameData module(s).", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public void ImportingALayeredExportWithTheSettingOffBringsTheLayerWadsAndNoDeclarations()
    {
        var vm = new MainWindowViewModel { Project = Fixture(declare: false) };

        var result = ImportBack(Export(vm));

        Assert.Equal(2, result.Wads);
        Assert.Equal(1, result.LayerWads);
        Assert.Equal(0, result.GameDataModules);
        var log = CaptureLog(vm);
        LogImport(vm, result);
        List<LogEntry> lines;
        lock (log) lines = log.Where(l => l.Category == "Import").ToList();
        Assert.DoesNotContain(lines, l => l.Level == LogLevel.Warning);
        Assert.Contains(lines, l => l.Message.Contains("fix (priority 10): 1 WAD(s), 0 GameData module(s)"));
    }

    [Fact]
    public void ImportingAPackageWithNothingLayeredLogsNoLayersAndNoWarning()
    {
        // the layer holds no WAD here (its folder is left out), and nothing is declared: a base-only package
        var project = MatchingFixture(declare: false, withAsset: true);
        var vm = new MainWindowViewModel { Project = project };

        var result = ImportBack(Export(vm));

        Assert.Empty(result.Layers);
        var log = CaptureLog(vm);
        LogImport(vm, result);
        List<LogEntry> lines;
        lock (log) lines = log.Where(l => l.Category == "Import").ToList();
        // what it always logged: what was unpacked. M816 adds that the package's own hashtable named the chunks (an export writes one)
        Assert.Equal(LogLevel.Success, lines[0].Level);
        Assert.DoesNotContain(lines, l => l.Level == LogLevel.Warning);
        Assert.DoesNotContain(lines, l => l.Message.StartsWith("Layers -", StringComparison.Ordinal));
        Assert.All(lines.Skip(1), l => Assert.Contains("named from the package's own hashtables", l.Message));
    }
}

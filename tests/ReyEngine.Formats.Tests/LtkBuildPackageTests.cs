using System.Reflection;
using System.Runtime.ExceptionServices;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Build;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using static ReyEngine.Formats.Tests.LayeredFantomeSupport;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816 review: Build Package of a project imported from an LTK-layered .fantome. It writes WADs, and the project has more than WADs: it
/// used to drop the GameData and the override files without a word and to stage a layer's WAD as <c>layers_winter_Map11.wad.client</c>,
/// a name no game patches. The WAD of a layer is now written under its real name in a folder of the layer
/// (<c>Build/winter/Map11.wad.client</c>), the stale-output sweep covers that folder, and the log says what the build cannot carry.
/// A project that never imported a package builds with the output and the log it always had.
/// </summary>
public sealed class LtkBuildPackageTests : IDisposable
{
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m816-build-" + Guid.NewGuid().ToString("N"));

    private string Projects => Path.Combine(_root, "projects");
    private string Scratch => Path.Combine(_root, "scratch");
    private string BuildRoot => Path.Combine(_root, "Build");

    public LtkBuildPackageTests() => Directory.CreateDirectory(Projects);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const string Doc1 =
        "{\"version\":1,\"modules\":[{\"target\":\"data/maps/a.bin\",\"edits\":[],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":0}}]}";

    private const string Doc2 =
        "{\"version\":1,\"modules\":[{\"target\":\"data/maps/a.bin\",\"edits\":[{\"overrides\":[\"data/x.ptch\"]}],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":0}},"
        + "{\"target\":\"data/maps/b.bin\",\"edits\":[],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":1}}]}";

    /// <summary>The same WAD name in base and in "winter", GameData in both (3 modules), one override file, string overrides.</summary>
    private string LayeredPackage()
    {
        string wads = Path.Combine(_root, "wads");
        var baseWad = PackWad(Scratch, Path.Combine(wads, "base.wad.client"),
            ("data/maps/a.bin", new byte[] { 1, 2, 3 }), ("assets/shared.tex", new byte[] { 9, 9, 9 }), ("assets/base-only.tex", new byte[] { 4 }));
        var winterWad = PackWad(Scratch, Path.Combine(wads, "winter.wad.client"),
            ("assets/shared.tex", new byte[] { 7, 7 }), ("assets/snow.tex", new byte[] { 5, 5, 5 }));
        string info = "{\"Name\":\"Layered\",\"Author\":\"T\",\"Description\":\"d\",\"Layers\":{"
            + "\"base\":{\"GameData\":" + Doc1 + ",\"Name\":\"base\",\"Priority\":0},"
            + "\"winter\":{\"GameData\":" + Doc2 + ",\"Name\":\"winter\",\"DisplayName\":\"Winter\",\"Priority\":5,\"StringOverrides\":{\"default\":{\"k\":\"v\"}}}},"
            + "\"Hashtables\":" + TableManifest + "}";
        return WriteFantome(Path.Combine(_root, "layered.fantome"),
            Entry("META/info.json", info),
            Table("data/maps/a.bin", "assets/shared.tex", "assets/base-only.tex", "assets/snow.tex"),
            ("WAD/Map11.wad.client", baseWad), ("WAD_winter/Map11.wad.client", winterWad),
            ("META/game_data/winter/data/x.ptch", new byte[] { 1, 2, 3, 4 }));
    }

    private (MainWindowViewModel Vm, ReyProject Project, List<LogEntry> Log) OpenImported()
    {
        var import = FantomeImporter.Import(LayeredPackage(), Projects, null, new HashDatabase());
        var project = ReyProjectService.OpenFolder(import.RootPath);
        project.OutputDirectory = BuildRoot;
        var vm = new MainWindowViewModel { Project = project };
        return (vm, project, CaptureLog(vm));
    }

    private static List<LogEntry> CaptureLog(MainWindowViewModel vm)
    {
        var lines = new List<LogEntry>();
        ((Logger)typeof(MainWindowViewModel).GetField("_log", NonPublic)!.GetValue(vm)!).Logged += e => { lock (lines) lines.Add(e); };
        return lines;
    }

    /// <summary>Runs the build core Build Package and Export .fantome both end in.</summary>
    private static List<FantomeWad> Build(MainWindowViewModel vm, string buildRoot)
    {
        var method = typeof(MainWindowViewModel).GetMethod("BuildProjectCore", NonPublic)!;
        try
        {
            object result = method.Invoke(vm, new object?[] { buildRoot, null, false })!;
            return (List<FantomeWad>)result.GetType().GetProperty("Wads")!.GetValue(result)!;
        }
        catch (TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
    }

    private static IEnumerable<string> Wads(string root) =>
        Directory.EnumerateFiles(root, "*.wad.client", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "staged" + Path.DirectorySeparatorChar))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal);

    private static Dictionary<ulong, byte[]> ChunksOf(string wad)
    {
        using var archive = WadArchive.Open(wad);
        return archive.Entries.ToDictionary(e => e.PathHash, e => archive.Extract(e.PathHash));
    }

    // ===================================================== where a layer's WAD goes

    [Fact]
    public void ALayersWadIsBuiltUnderItsRealNameInAFolderOfTheLayer()
    {
        var (vm, _, _) = OpenImported();

        var built = Build(vm, BuildRoot);

        // base is what it always was: a file beside the others. The layer's WAD has the name the game patches, in the layer's folder.
        Assert.Equal(new[] { "Map11.wad.client", "winter/Map11.wad.client" }, Wads(BuildRoot));
        Assert.DoesNotContain(Directory.EnumerateFiles(BuildRoot, "*", SearchOption.AllDirectories), f => Path.GetFileName(f).StartsWith("layers_", StringComparison.Ordinal));

        var baseChunks = ChunksOf(Path.Combine(BuildRoot, "Map11.wad.client"));
        var winterChunks = ChunksOf(Path.Combine(BuildRoot, "winter", "Map11.wad.client"));
        Assert.Equal(3, baseChunks.Count);
        Assert.Equal(new byte[] { 9, 9, 9 }, baseChunks[Hash("assets/shared.tex")]);
        Assert.Equal(2, winterChunks.Count);
        Assert.Equal(new byte[] { 7, 7 }, winterChunks[Hash("assets/shared.tex")]);          // the same path, each WAD's own bytes

        // and the wads an export stores come out as they did: the layer, and the name of the WAD
        var winter = built.Single(w => w.Layer == "winter");
        Assert.Equal("Map11.wad.client", winter.Name);
        Assert.Equal("WAD_winter/Map11.wad.client", FantomeExporter.WadEntryName(winter));
        Assert.Equal("WAD/Map11.wad.client", FantomeExporter.WadEntryName(built.Single(w => w.Layer == "base")));
    }

    [Fact]
    public void ABuildRepeatedLeavesTheSameFilesAndNoStaleOnes()
    {
        var (vm, _, _) = OpenImported();
        Build(vm, BuildRoot);

        Build(vm, BuildRoot);

        Assert.Equal(new[] { "Map11.wad.client", "winter/Map11.wad.client" }, Wads(BuildRoot));
    }

    [Fact]
    public void TheStaleOutputSweepCoversAnImportedLayersFolderAndOnlyThat()
    {
        var (vm, project, log) = OpenImported();
        project.Layers.Add(new ProjectLayer { Name = "snow", Priority = 9, DeclarationsKey = "snow" });      // an imported layer that holds no folder now
        string stale = Path.Combine(BuildRoot, "winter", "Old.wad.client");
        string notAWad = Path.Combine(BuildRoot, "winter", "notes.txt");
        string snowStale = Path.Combine(BuildRoot, "snow", "Gone.wad.client");
        string elsewhere = Path.Combine(BuildRoot, "elsewhere", "Keep.wad.client");        // a folder no layer of the project is called
        string flatStale = Path.Combine(BuildRoot, "Stale.wad.client");                      // the sweep this extends
        foreach (string f in new[] { stale, notAWad, snowStale, elsewhere, flatStale })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, "old");
        }

        Build(vm, BuildRoot);

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(snowStale));
        Assert.False(File.Exists(flatStale));
        Assert.True(File.Exists(notAWad));                                                    // only WADs are swept
        Assert.True(File.Exists(elsewhere));                                                  // and only the folders of the project's own layers
        Assert.False(Directory.Exists(Path.Combine(BuildRoot, "snow")));                      // a folder the sweep emptied goes with it
        Assert.True(File.Exists(Path.Combine(BuildRoot, "winter", "Map11.wad.client")));
        lock (log) Assert.Contains(log, l => l.Message == "Removed stale build output winter/Old.wad.client (not part of this project anymore).");
    }

    // ===================================================== a project that predates M816 builds as it did

    [Fact]
    public void AProjectThatPredatesM816BuildsTheFilesItAlwaysDidAndSaysNothingNew()
    {
        string project = Path.Combine(_root, "legacy");
        foreach (var (folder, rel) in new[] { ("Map11", "assets/m.dds"), ("mods/Ahri", "assets/a.dds"), ("Lux", "assets/l.dds") })
        {
            string path = Path.Combine(project, folder.Replace('/', Path.DirectorySeparatorChar), rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        }
        var p = new ReyProject { Name = "Legacy", RootPath = project, OutputDirectory = BuildRoot, ProjectFolders = { "Map11", "mods/Ahri", "Lux" } };
        p.Layers.Add(new ProjectLayer { Name = "fix", Priority = 10, Folders = { "Ahri", "Lux" } });        // leaf names, as the dialog wrote them
        var vm = new MainWindowViewModel { Project = p };
        var log = CaptureLog(vm);
        // a folder named like the layer, with a WAD in it, that the user keeps there: a build of this project never wrote to it
        string users = Path.Combine(BuildRoot, "fix", "mine.wad.client");
        Directory.CreateDirectory(Path.GetDirectoryName(users)!);
        File.WriteAllText(users, "mine");

        var built = Build(vm, BuildRoot);
        typeof(MainWindowViewModel).GetMethod("WarnBuildPackageCannotCarry", NonPublic)!.Invoke(vm, null);

        Assert.Equal(new[] { "Lux.wad.client", "Map11.wad.client", "fix/mine.wad.client", "mods_Ahri.wad.client" }, Wads(BuildRoot));      // flat, and the nested one under its underscored name
        Assert.True(File.Exists(users));
        Assert.Equal("mine", File.ReadAllText(users));
        // and byte for byte what packing the project's own folder gives - the build adds nothing to a project that never imported one
        foreach (var (folder, wad) in new[] { ("Map11", "Map11.wad.client"), ("mods/Ahri", "mods_Ahri.wad.client"), ("Lux", "Lux.wad.client") })
        {
            string direct = Path.Combine(_root, "direct-" + wad);
            Assert.True(WadPackService.Pack(Path.Combine(project, folder.Replace('/', Path.DirectorySeparatorChar)), direct, knownTypesOnly: true).Success);
            Assert.Equal(File.ReadAllBytes(direct), File.ReadAllBytes(Path.Combine(BuildRoot, wad)));
        }
        Assert.All(built, w => Assert.Null(w.Name));                                         // the name of the file is the name of the WAD, as before
        Assert.Equal(new[] { "fix", "fix", "base" }, built.Select(w => w.Layer).OrderByDescending(l => l, StringComparer.Ordinal));
        lock (log)
        {
            Assert.DoesNotContain(log, l => l.Level == LogLevel.Warning);                    // and none of the log this change adds
            Assert.DoesNotContain(log, l => l.Message.Contains("Build Package writes WAD files only", StringComparison.Ordinal));
        }
    }

    // ===================================================== what Build Package cannot carry

    [Fact]
    public void BuildPackageSaysWhatItLeavesOutAndWhereToGetIt()
    {
        var (vm, _, log) = OpenImported();
        Build(vm, BuildRoot);

        typeof(MainWindowViewModel).GetMethod("WarnBuildPackageCannotCarry", NonPublic)!.Invoke(vm, null);

        List<LogEntry> warnings;
        lock (log) warnings = log.Where(l => l.Level == LogLevel.Warning && l.Category == "Build").ToList();
        string message = Assert.Single(warnings).Message;
        Assert.StartsWith("Build Package writes WAD files only. This project came from an LTK-layered .fantome, and the build output does not carry ", message);
        Assert.Contains("1 layer(s) with their priorities and switches (the WAD of winter is in Build/<layer>/, on its own)", message);
        Assert.Contains("3 GameData module(s)", message);
        Assert.Contains("1 override file(s)", message);
        Assert.Contains("the string overrides of 1 layer(s)", message);
        Assert.EndsWith("Project > Export .fantome and Send to LTK Manager write them.", message);
    }

    [Fact]
    public void ABrokenStoredDocumentIsSaidToo()
    {
        var (vm, project, log) = OpenImported();
        File.WriteAllText(Path.Combine(LtkProjectStore.DirectoryOf(project.RootPath!, "winter"), LtkProjectStore.DeclarationsFileName), "{ nope");

        typeof(MainWindowViewModel).GetMethod("WarnBuildPackageCannotCarry", NonPublic)!.Invoke(vm, null);

        lock (log) Assert.Contains("a stored GameData document that could not be read", Assert.Single(log, l => l.Level == LogLevel.Warning).Message);
    }

    // ===================================================== what a build refuses, before it stages anything

    [Fact]
    public void ALayerCalledStagedHasNoBuildFolderBecauseTheBuildStagesThere()
    {
        string root = Path.Combine(_root, "staged-layer");
        Directory.CreateDirectory(Path.Combine(root, "layers", "staged", "Map11", "assets"));
        File.WriteAllText(Path.Combine(root, "layers", "staged", "Map11", "assets", "a.tex"), "x");
        var p = new ReyProject { Name = "S", RootPath = root, OutputDirectory = BuildRoot, ProjectFolders = { "layers/staged/Map11" } };
        p.Layers.Add(new ProjectLayer { Name = "staged", Priority = 1, Folders = { "layers/staged/Map11" } });
        var vm = new MainWindowViewModel { Project = p };

        var ex = Assert.Throws<InvalidOperationException>(() => Build(vm, BuildRoot));

        Assert.Contains("Build/staged is where the build stages its files", ex.Message);
    }

    [Fact]
    public void ALayerNameThatIsNotAFolderStopsTheBuildAndWritesNothingOutside()
    {
        string root = Path.Combine(_root, "odd");
        Directory.CreateDirectory(Path.Combine(root, "layers", "x", "Map11", "assets"));
        File.WriteAllText(Path.Combine(root, "layers", "x", "Map11", "assets", "a.tex"), "x");
        var p = new ReyProject { Name = "Odd", RootPath = root, OutputDirectory = BuildRoot, ProjectFolders = { "layers/x/Map11" } };
        p.Layers.Add(new ProjectLayer { Name = "..\\..", Priority = 1, Folders = { "layers/x/Map11" } });      // as a hand-edited project.json can hold
        var vm = new MainWindowViewModel { Project = p };

        var ex = Assert.Throws<InvalidOperationException>(() => Build(vm, BuildRoot));

        Assert.Contains("cannot be written to a folder", ex.Message);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.wad.client", SearchOption.AllDirectories));
    }

    [Fact]
    public void AFolderEntryThatClimbsOutOfTheStagingFolderStopsTheBuild()
    {
        // the staging folder is written to and emptied by every build: a backslash path in an entry (project.json is a file a person can
        // edit) used to be joined to it as it was - '/' became '_', '\' did not - and its files were copied wherever it pointed
        var p = new ReyProject { Name = "Climb", RootPath = Path.Combine(_root, "climb"), OutputDirectory = BuildRoot, ProjectFolders = { "..\\evil" } };
        Directory.CreateDirectory(p.RootPath!);
        var vm = new MainWindowViewModel { Project = p };

        var ex = Assert.Throws<InvalidOperationException>(() => Build(vm, BuildRoot));

        Assert.Contains("'..\\evil' would be staged outside the build's staging folder", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(BuildRoot, "evil")));
        Assert.False(Directory.Exists(Path.Combine(_root, "evil")));
    }

    [Fact]
    public void TwoFoldersOfOneWadInOneLayerStopTheBuildBeforeAnythingIsStaged()
    {
        var (vm, project, _) = OpenImported();
        // Project Settings can move a second folder of the same WAD into the layer: its leaf is the same, so the WAD is
        string snow = Path.Combine(project.RootPath!, "layers", "snow", "Map11", "assets");
        Directory.CreateDirectory(snow);
        File.WriteAllText(Path.Combine(snow, "s.tex"), "s");
        project.ProjectFolders.Add("layers/snow/Map11");
        project.Layers.Single(l => l.Name == "winter").Folders.Add("layers/snow/Map11");
        Directory.CreateDirectory(BuildRoot);

        var ex = Assert.Throws<InvalidOperationException>(() => Build(vm, BuildRoot));

        Assert.Contains("'layers/winter/Map11' and 'layers/snow/Map11' would both ship as Map11.wad.client in layer 'winter'", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(BuildRoot, "staged")));
        Assert.Empty(Wads(BuildRoot));
    }
}

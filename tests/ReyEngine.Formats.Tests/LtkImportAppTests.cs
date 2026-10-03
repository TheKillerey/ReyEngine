using System.Reflection;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using static ReyEngine.Formats.Tests.LayeredFantomeSupport;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816: the parts of the app around an imported LTK-layered .fantome - the names its hashtables gave the dictionary (taught when
/// a project opens and again after the dictionary is swapped for a synced one), and what the import log says.
/// </summary>
public sealed class LtkImportAppTests : IDisposable
{
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m816-app-" + Guid.NewGuid().ToString("N"));

    public LtkImportAppTests() => Directory.CreateDirectory(Path.Combine(_root, "projects"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>A package whose WAD holds two files and whose table names them, plus one name the WAD does not hold.</summary>
    private string Package()
    {
        string wad = Path.Combine(_root, "wads", "a.wad.client");
        var bytes = PackWad(Path.Combine(_root, "scratch"), wad, ("assets/one.tex", new byte[] { 1 }), ("data/two.bin", new byte[] { 2 }));
        return WriteFantome(Path.Combine(_root, "pkg.fantome"),
            Entry("META/info.json", "{\"Name\":\"Pkg\",\"Author\":\"T\",\"Description\":\"d\",\"Hashtables\":" + TableManifest + ",\"Layers\":{\"events\":{\"Name\":\"events\",\"Priority\":1,\"GameData\":{\"version\":1,\"modules\":[]}}}}"),
            Table("assets/one.tex", "data/two.bin", "assets/not-in-the-wad.tex"),
            ("WAD/Map11.wad.client", bytes));
    }

    private static WadPathResolver ResolverOf(MainWindowViewModel vm) =>
        (WadPathResolver)typeof(MainWindowViewModel).GetField("_resolver", NonPublic)!.GetValue(vm)!;

    private static void Call(MainWindowViewModel vm, string method, params object[] args) =>
        typeof(MainWindowViewModel).GetMethod(method, NonPublic)!.Invoke(vm, args);

    private static List<LogEntry> CaptureLog(MainWindowViewModel vm)
    {
        var lines = new List<LogEntry>();
        ((Logger)typeof(MainWindowViewModel).GetField("_log", NonPublic)!.GetValue(vm)!).Logged += e => { lock (lines) lines.Add(e); };
        return lines;
    }

    [Fact]
    public void OpeningAProjectTeachesTheDictionaryTheNamesItsPackageDeclaredAndASwapDoesNotLoseThem()
    {
        var import = FantomeImporter.Import(Package(), Path.Combine(_root, "projects"), null, new HashDatabase());
        var vm = new MainWindowViewModel { Project = ReyProjectService.OpenFolder(import.RootPath) };
        var resolver = ResolverOf(vm);
        ulong notInTheWad = HashAlgorithms.WadPath("assets/not-in-the-wad.tex");
        Assert.False(resolver.Database.TryGetPath(notInTheWad, out _));

        Call(vm, "LoadProjectHashtables", true);

        Assert.True(resolver.Database.TryGetPath(notInTheWad, out var name));              // a name the table gave and no file of the project has
        Assert.Equal("assets/not-in-the-wad.tex", name);
        Assert.True(resolver.Database.TryGetPath(HashAlgorithms.WadPath("assets/one.tex"), out _));

        // a synced dictionary replaces the one the names were taught to; the next application of hashes teaches the new one
        resolver.Swap(new HashDatabase());
        Assert.False(resolver.Database.TryGetPath(notInTheWad, out _));
        Call(vm, "ApplyHashesToOpenWad");
        Assert.True(resolver.Database.TryGetPath(notInTheWad, out _));
    }

    [Fact]
    public void AProjectWithNoImportedTablesLeavesTheDictionaryAlone()
    {
        var project = new ReyProject { RootPath = Path.Combine(_root, "plain") };
        Directory.CreateDirectory(project.RootPath);
        var vm = new MainWindowViewModel { Project = project };
        int before = ResolverOf(vm).Database.WadCount;

        Call(vm, "LoadProjectHashtables", true);

        Assert.Equal(before, ResolverOf(vm).Database.WadCount);
    }

    [Fact]
    public void TheImportLogSaysWhatCameInAndWarnsOnlyAboutTheSmallPrint()
    {
        var vm = new MainWindowViewModel();
        var log = CaptureLog(vm);
        var result = FantomeImporter.Import(Package(), Path.Combine(_root, "projects"), null, new HashDatabase());

        Call(vm, "LogFantomeImport", result);

        List<LogEntry> lines;
        lock (log) lines = log.Where(l => l.Category == "Import").ToList();
        Assert.Contains(lines, l => l.Level == LogLevel.Success && l.Message.StartsWith("Pkg: 1 WAD(s), 2 file(s) unpacked", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Level == LogLevel.Info && l.Message.StartsWith("Layers - events (priority 1): 0 WAD(s), 0 GameData module(s).", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Level == LogLevel.Info && l.Message.StartsWith("2 chunk(s) named from the package's own hashtables (3 name(s))", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public void TheImportLogWarnsAboutWhatWasNotCarried()
    {
        string path = WriteFantome(Path.Combine(_root, "odd.fantome"),
            Entry("META/info.json", "{\"Name\":\"Odd\",\"Extension\":1}"), Entry("RAW/a.dds", "a"));
        var vm = new MainWindowViewModel();
        var log = CaptureLog(vm);

        Call(vm, "LogFantomeImport", FantomeImporter.Import(path, Path.Combine(_root, "projects"), null, new HashDatabase()));

        List<LogEntry> lines;
        lock (log) lines = log.Where(l => l.Category == "Import").ToList();
        var warning = Assert.Single(lines, l => l.Level == LogLevel.Warning);
        Assert.StartsWith("Not everything in this .fantome was carried into the project: META/info.json has key(s) ReyEngine does not carry (Extension)", warning.Message);
    }

    [Fact]
    public void AHashDatabaseHandedToTheImportIsTaughtThePackagesNamesBeforeItUnpacks()
    {
        var db = new HashDatabase();

        FantomeImporter.Import(Package(), Path.Combine(_root, "projects"), null, db);

        Assert.True(db.TryGetPath(HashAlgorithms.WadPath("assets/not-in-the-wad.tex"), out var name));
        Assert.Equal("assets/not-in-the-wad.tex", name);
        Assert.Equal(3, db.WadCount);
    }

    // ===================================================== review: the dictionary is changed in place, and thumbnails read through it

    [Fact]
    public void LoadingTheProjectsHashtablesMovesTheThumbnailInputsBeforeAndAfter()
    {
        var import = FantomeImporter.Import(Package(), Path.Combine(_root, "projects"), null, new HashDatabase());
        var vm = new MainWindowViewModel { Project = ReyProjectService.OpenFolder(import.RootPath) };
        var counter = typeof(MainWindowViewModel).GetField("_mapThumbnailInputs", NonPublic)!;
        long before = (long)counter.GetValue(vm)!;

        Call(vm, "LoadProjectHashtables", true);

        // a map thumbnail on a worker reads names through the dictionary this teaches: a draw that overlapped the change is not kept,
        // which is what a bump before AND one after make true (the same bracket as the hash sync's)
        Assert.Equal(before + 2, (long)counter.GetValue(vm)!);
    }

    [Fact]
    public void ALoadThatFailsStillClosesTheBracket()
    {
        var import = FantomeImporter.Import(Package(), Path.Combine(_root, "projects"), null, new HashDatabase());
        var vm = new MainWindowViewModel { Project = ReyProjectService.OpenFolder(import.RootPath) };
        var log = CaptureLog(vm);
        var counter = typeof(MainWindowViewModel).GetField("_mapThumbnailInputs", NonPublic)!;
        long before = (long)counter.GetValue(vm)!;
        string table = Directory.EnumerateFiles(Path.Combine(LtkProjectStore.RootOf(import.RootPath), "hashes")).Single();

        using (new FileStream(table, FileMode.Open, FileAccess.Read, FileShare.None))        // another program holds the file: the read throws
            Call(vm, "LoadProjectHashtables", true);

        Assert.Equal(before + 2, (long)counter.GetValue(vm)!);
        lock (log) Assert.Contains(log, l => l.Message.StartsWith("The project's hashtables could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void OpeningAProjectLoadsItsNamesAfterTheTileInHandIsStopped()
    {
        string source = ViewModelSource();
        int open = source.IndexOf("private void OpenProjectAt(string folder)", StringComparison.Ordinal);
        Assert.True(open >= 0);
        string body = source[open..source.IndexOf("public string ProjectsFolder", open, StringComparison.Ordinal)];

        int abandon = body.IndexOf("AbandonMapThumbnails();", StringComparison.Ordinal);
        int load = body.IndexOf("LoadProjectHashtables(announce: true);", StringComparison.Ordinal);
        int mounts = body.IndexOf("BuildMounts();", StringComparison.Ordinal);
        Assert.True(abandon >= 0 && load > abandon, "the hashtables are loaded after AbandonMapThumbnails()");
        Assert.True(load < mounts, "and before any bin is read by the mounts");
    }

    private static string ViewModelSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException("MainWindowViewModel.cs was not found above the test output.");
    }

    // ===================================================== review: Send reports what is wrong with a project, it does not throw

    private static async Task SendTo(MainWindowViewModel vm, string workshop) =>
        await (Task)typeof(MainWindowViewModel).GetMethod("SendProjectToWorkshop", NonPublic)!.Invoke(vm, new object[] { workshop })!;

    [Fact]
    public async Task AStoredDocumentSomeoneBrokeIsAnErrorInTheLogAndNotAnExceptionOnTheUiThread()
    {
        var import = FantomeImporter.Import(Package(), Path.Combine(_root, "projects"), null, new HashDatabase());
        var project = ReyProjectService.OpenFolder(import.RootPath);
        // ReadLayers throws InvalidDataException for a document that is not JSON, by design: it must not write it into a package
        File.WriteAllText(Path.Combine(LtkProjectStore.DirectoryOf(import.RootPath, "events"), LtkProjectStore.DeclarationsFileName), "{ this is not json");
        string workshop = Path.Combine(_root, "workshop");
        Directory.CreateDirectory(workshop);
        var vm = new MainWindowViewModel { Project = project };
        var log = CaptureLog(vm);

        await SendTo(vm, workshop);                                  // an exception here is an unhandled one on the UI thread

        List<LogEntry> errors;
        lock (log) errors = log.Where(l => l.Category == "LTK" && l.Level == LogLevel.Error).ToList();
        var error = Assert.Single(errors);
        Assert.StartsWith("Send failed: The GameData document of layer 'events'", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workshop));      // and nothing was sent
        Assert.False(vm.IsBuilding);
    }

    [Fact]
    public async Task ALayerNameThatCannotBeAFolderIsAnErrorInTheLogAndNothingIsWritten()
    {
        string root = Path.Combine(_root, "odd");
        Directory.CreateDirectory(Path.Combine(root, "Map11", "assets"));
        File.WriteAllText(Path.Combine(root, "Map11", "assets", "a.tex"), "x");
        var project = new ReyProject { Name = "Odd", RootPath = root, ProjectFolders = { "Map11" } };
        project.Layers.Add(new ProjectLayer { Name = "..\\..", Priority = 1 });          // as a hand-edited project.json can hold
        string workshop = Path.Combine(_root, "workshop");
        Directory.CreateDirectory(workshop);
        var vm = new MainWindowViewModel { Project = project };
        var log = CaptureLog(vm);

        await SendTo(vm, workshop);

        List<LogEntry> errors;
        lock (log) errors = log.Where(l => l.Category == "LTK" && l.Level == LogLevel.Error).ToList();
        Assert.Contains("Rename the layer in Project > Project Settings", Assert.Single(errors).Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workshop));
        Assert.False(vm.IsBuilding);
    }

    [Fact]
    public async Task TwoFoldersOfOneWadInOneLayerAreRefusedBeforeAnythingIsSent()
    {
        string root = Path.Combine(_root, "clash");
        foreach (string folder in new[] { "layers/winter/Map11", "layers/snow/Map11" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar), "assets"));
            File.WriteAllText(Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar), "assets", "a.tex"), folder);
        }
        var project = new ReyProject { Name = "Clash", RootPath = root, ProjectFolders = { "layers/winter/Map11", "layers/snow/Map11" } };
        project.Layers.Add(new ProjectLayer { Name = "winter", Priority = 1, Folders = { "layers/winter/Map11", "layers/snow/Map11" } });   // both moved into one layer
        string workshop = Path.Combine(_root, "workshop");
        Directory.CreateDirectory(workshop);
        var vm = new MainWindowViewModel { Project = project };
        var log = CaptureLog(vm);

        await SendTo(vm, workshop);

        List<LogEntry> errors;
        lock (log) errors = log.Where(l => l.Category == "LTK" && l.Level == LogLevel.Error).ToList();
        var message = Assert.Single(errors).Message;
        Assert.Contains("'layers/winter/Map11' and 'layers/snow/Map11' would both ship as Map11.wad.client in layer 'winter'", message);
        Assert.Contains("Project Settings", message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workshop));
    }

    [Fact]
    public void TheSharedResolverOfTheAppIsNotWrittenToFromTheImportThread()
    {
        // WadPathResolver wraps the dictionary every other thread reads: the import names chunks through its own overlay and leaves
        // the dictionary alone; the app teaches it, on its own thread, when the project opens
        var db = new HashDatabase();
        var shared = new WadPathResolver(db);

        var import = FantomeImporter.Import(Package(), Path.Combine(_root, "projects"), null, shared);

        Assert.Equal(0, db.WadCount);
        Assert.True(File.Exists(Path.Combine(import.RootPath, "Map11", "assets", "one.tex")));       // and the chunks are named all the same
        Assert.Equal(2, import.ChunksNamedByTables);
    }
}

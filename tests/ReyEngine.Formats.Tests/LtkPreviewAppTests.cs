using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Avalonia.Collections;
using ReyEngine.App.Documents;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using static ReyEngine.Formats.Tests.LayeredFantomeSupport;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M819: the editor around the preview - a project that stores GameData gets one and a project that does not gets nothing; what a read answers before it is ready and after; the order layers shadow one another in; a broken
/// document or a missing game that leaves the project usable; every write path that refuses a chunk the GameData changes; the caches that go stale; the tree, the badge, the diagnostics window. A synthetic game and a
/// synthetic imported package, through the view model's own private members (the way the other view-model tests reach them).
/// </summary>
public sealed class LtkPreviewAppTests : IAsyncLifetime, IDisposable
{
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly TempFolder _temp = new();
    private readonly List<MainWindowViewModel> _vms = new();
    private readonly List<IDisposable> _gates = new();
    private int _n;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Nothing a test started may still be running when it ends. The gates are opened (a worker waiting at one goes on, or ends), every preview is cancelled, and what the view model awaits for it is awaited - with a limit, so
    /// that a hang is a failure and not a run that never ends. What is left in flight after a test is what comes back later to a thread, a context or a folder that is gone, and on a pool thread that kills the process.
    /// </summary>
    public async Task DisposeAsync()
    {
        foreach (var gate in _gates) gate.Dispose();
        foreach (var vm in _vms) await Quiesce(vm);
    }

    /// <summary>Cancels the view model's preview (a settled one is left as it is) and waits - for at most a minute - until the work the view model keeps for it has ended.</summary>
    private static async Task Quiesce(MainWindowViewModel vm)
    {
        Preview(vm)?.Cancel();
        await Prop<Task>(vm, "GameDataApplied").WaitAsync(TimeSpan.FromSeconds(60));
    }

    public void Dispose() => _temp.Dispose();

    // a game bin the GameData names (it lives only in the game), a bin the mod ships itself and the GameData names as well, and a bin the mod ships and nothing names
    private const string A = "data/t/a.bin", M = "data/t/m.bin", P = "data/t/plain.bin";

    /// <summary>
    /// A view model that cannot read the machine's files for a schema: ReyEngine's copy is a database of the test's own, and LTK Manager's cache is not there. Without these the preview of a test would depend on which
    /// class schema the machine has and how new it is (the manager's cache, <c>data/meta</c>), and an assertion about what it makes of an edit could pass or fail with the machine.
    /// </summary>
    private MainWindowViewModel NewVm(ReyProject project)
    {
        var vm = new MainWindowViewModel { Project = project };
        SetField(vm, "GameDataSchemaPath", TempSchema());
        SetField(vm, "GameDataManagerSchemaPath", _temp.Combine("no-manager-cache", "meta-schema.json"));
        _vms.Add(vm);   // quiesced when the test ends
        return vm;
    }

    /// <summary>A game with a gate in front of its table that is opened when the test ends, whatever became of the test: a worker must not be left waiting at it.</summary>
    private GatedGame Gated(IGameDataGame inner)
    {
        var gated = new GatedGame(inner);
        _gates.Add(gated);
        return gated;
    }

    private string TempSchema(uint latest = 100, string? classes = null)
    {
        string path = _temp.Combine("schema-" + latest + "-" + (classes is null ? "plain" : "classes" + classes.GetHashCode()) + ".json");
        if (!File.Exists(path)) File.WriteAllText(path, "{\"formatVersion\":1,\"latest\":" + latest + ",\"classes\":{" + classes + "}}");
        return path;
    }

    private static object? Call(MainWindowViewModel vm, string method, params object?[] args)
    {
        try { return typeof(MainWindowViewModel).GetMethod(method, NonPublic)!.Invoke(vm, args); }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }

    private static T Field<T>(MainWindowViewModel vm, string name) => (T)typeof(MainWindowViewModel).GetField(name, NonPublic)!.GetValue(vm)!;
    private static void SetField(MainWindowViewModel vm, string name, object? value) => typeof(MainWindowViewModel).GetField(name, NonPublic)!.SetValue(vm, value);
    private static T Prop<T>(MainWindowViewModel vm, string name) => (T)typeof(MainWindowViewModel).GetProperty(name, NonPublic)!.GetValue(vm)!;

    private static AssetMountService Mounts(MainWindowViewModel vm) => Field<AssetMountService>(vm, "_mounts");
    private static GameDataPreview? Preview(MainWindowViewModel vm) => Prop<GameDataPreview?>(vm, "GameData");
    /// <summary>What the view model keeps for its preview, with a limit: a gate nobody opened is a failed test, not a run that waits for ever.</summary>
    private static Task Applied(MainWindowViewModel vm) => Prop<Task>(vm, "GameDataApplied").WaitAsync(TimeSpan.FromSeconds(60));

    private static List<LogEntry> CaptureLog(MainWindowViewModel vm)
    {
        var lines = new List<LogEntry>();
        Field<Logger>(vm, "_log").Logged += e => { lock (lines) lines.Add(e); };
        return lines;
    }

    private static List<LogEntry> Lines(List<LogEntry> log, string category)
    {
        lock (log) return log.Where(l => l.Category == category).ToList();
    }

    private static ulong Hash(string path) => HashAlgorithms.WadPath(path);

    private static string Text(byte[]? bytes) => Encoding.UTF8.GetString(bytes!);

    /// <summary>The game with a gate in front of its table: a preview over it is pending until the test opens the gate (or ends: disposing it opens the gate).</summary>
    private sealed class GatedGame : IGameDataGame, IDisposable
    {
        private readonly IGameDataGame _inner;
        public readonly ManualResetEventSlim Gate = new(false);
        public GatedGame(IGameDataGame inner) { _inner = inner; }

        public void Dispose() => Gate.Set();

        public GameChunkTable GetTable(CancellationToken cancellationToken)
        {
            Gate.Wait(cancellationToken);
            return _inner.GetTable(cancellationToken);
        }

        public GameObjectIndex GetObjects(CancellationToken cancellationToken) => _inner.GetObjects(cancellationToken);
        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken) => _inner.ReadChunk(archive, chunk, maxBytes, cancellationToken);
        public string? IndexNotSaved => _inner.IndexNotSaved;
    }

    /// <summary>
    /// One thread that runs what it is given in order, and the synchronization context of it: the editor's UI thread, as far as a wait is concerned. An <c>await</c> started on it comes back to it, and nothing else runs on
    /// it between two of its steps - which is what lets a test rebuild the mounts while a map load waits without the load seeing a half-built service, as it never can in the editor.
    ///
    /// <para>It never throws at a poster. A continuation that comes after the context is disposed - the test has ended - has nothing left to do and is dropped, as a dispatcher that has stopped drops what is posted to it:
    /// a throw here would be on a pool thread, where it ends the whole test run. A callback that throws is recorded (<see cref="Faults"/>) and the thread goes on.</para>
    /// </summary>
    private sealed class UiLikeContext : SynchronizationContext, IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly List<Exception> _faults = new();
        private readonly Thread _thread;
        private bool _completed;

        public UiLikeContext()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "UiLikeContext" };
            _thread.Start();
        }

        /// <summary>What the callbacks that threw threw.</summary>
        public IReadOnlyList<Exception> Faults
        {
            get { lock (_gate) return _faults.ToList(); }
        }

        private void Loop()
        {
            SetSynchronizationContext(this);
            while (true)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (_gate)
                {
                    while (_queue.Count == 0)
                    {
                        if (_completed) return;
                        Monitor.Wait(_gate);
                    }
                    next = _queue.Dequeue();
                }
                try { next.Callback(next.State); }
                catch (Exception e) { lock (_gate) _faults.Add(e); }
            }
        }

        private bool TryPost(SendOrPostCallback callback, object? state)
        {
            lock (_gate)
            {
                if (_completed) return false;
                _queue.Enqueue((callback, state));
                Monitor.Pulse(_gate);
                return true;
            }
        }

        public override void Post(SendOrPostCallback d, object? state) => TryPost(d, state);

        /// <summary>Runs <paramref name="work"/> on the thread. After the context is disposed the task is cancelled: it never runs.</summary>
        public Task<T> Run<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool posted = TryPost(_ =>
            {
                try { done.SetResult(work()); }
                catch (Exception e) { done.SetException(e); }
            }, null);
            if (!posted) done.SetCanceled();
            return done.Task;
        }

        /// <summary>Ends the context: what was posted and has not run is dropped, what is posted from now on is dropped, and the thread ends (waited for, for a few seconds).</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _completed = true;
                _queue.Clear();
                Monitor.PulseAll(_gate);
            }
            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    private static string AddTag(string path, string obj, string tag) => Target(path, $"{{\"{obj}\":{{\"+tags\":[\"{tag}\"]}}}}");

    /// <summary>An installation the editor accepts as a game folder (it holds DATA.wad.client and Global.wad.client) with the bins the tests name.</summary>
    private string Game(string name)
    {
        var game = new SyntheticGame()
            .Add("DATA.wad.client", "data/readme.txt", new byte[] { 1 })
            .Add("Global.wad.client", "data/global.txt", new byte[] { 2 })
            .Add("Champions/A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
            .Add("Map11.wad.client", M, Bin(Obj("Test/Obj/M", new[] { "game" })));
        return game.Write(_temp.Combine(name));
    }

    private string Package(string name, string info, params (string Entry, byte[] Bytes)[] more) =>
        WriteFantome(_temp.Combine(name + _n++ + ".fantome"), new[] { Entry("META/info.json", info) }.Concat(more).ToArray());

    private static string Layers(string baseDoc, string? fixDoc = null) =>
        "{\"Name\":\"t\",\"Author\":\"a\",\"Version\":\"1.0.0\",\"Description\":\"d\",\"Hashtables\":" + TableManifest + ",\"Layers\":{\"base\":{\"Name\":\"base\",\"Priority\":0,\"GameData\":" + baseDoc + "}"
        + (fixDoc is null ? "" : ",\"fix\":{\"Name\":\"fix\",\"Priority\":1,\"GameData\":" + fixDoc + "}") + "}}";

    private byte[] ModWad(string name, params (string Rel, byte[] Bytes)[] files) => PackWad(_temp.Combine("pack-" + name + _n++), _temp.Combine("mod-" + name + _n++ + ".wad.client"), files);

    /// <summary>A project imported from a package: its WADs and its GameData as the import stores them, and the game folder set.</summary>
    private ReyProject Import(string name, string info, string gameDirectory, params (string Entry, byte[] Bytes)[] more)
    {
        string projects = _temp.Combine("projects");
        Directory.CreateDirectory(projects);
        var result = FantomeImporter.Import(Package(name, info, more), projects, null, new HashDatabase());
        var project = ReyProjectService.OpenFolder(result.RootPath);
        project.GameDirectory = gameDirectory;
        return project;
    }

    private async Task<MainWindowViewModel> Open(ReyProject project, Func<string, IGameDataGame>? factory = null)
    {
        var vm = NewVm(project);
        SetField(vm, "GameDataIndexCachePath", _temp.Combine("cache" + _n++, "index.idx"));
        if (factory is not null) SetField(vm, "GameDataGameFactory", factory);
        Call(vm, "BuildMounts");
        await Applied(vm);
        return vm;
    }

    private static WadAssetEntry EntryOf(MainWindowViewModel vm, string path)
    {
        Assert.True(Mounts(vm).TryGet(Hash(path), out var asset), "not mounted: " + path);
        return asset.ToEntry();
    }

    /// <summary>The project every GameData test starts from: base GameData on A and M, a fix layer on A, the mod's own copy of M and P, and the fix layer's own P.</summary>
    private ReyProject LayeredProject(string gameDirectory) => Import("layered",
        Layers(Doc(AddTag(A, "Test/Obj/A", "base"), AddTag(M, "Test/Obj/M", "base")), Doc(AddTag(A, "Test/Obj/A", "fix"))),
        gameDirectory,
        ("WAD/Map11.wad.client", ModWad("base", (M, Bin(Obj("Test/Obj/M", new[] { "mod" }))), (P, Encoding.UTF8.GetBytes("plain of base")))),
        ("WAD_fix/Map11.wad.client", ModWad("fix", (P, Encoding.UTF8.GetBytes("plain of fix")))),
        Table(A, M, P));

    // ================================================================================================ a project with no GameData

    [Fact]
    public async Task A_project_that_stores_no_GameData_gets_no_preview_no_overlay_no_log_line_and_the_mounts_it_always_had()
    {
        string root = _temp.Combine("legacy");
        foreach (var (folder, text) in new[] { ("One", "from one"), ("Two", "from two") })
        {
            Directory.CreateDirectory(Path.Combine(root, folder, "data", "t"));
            File.WriteAllText(Path.Combine(root, folder, "data", "t", "plain.bin"), text);
        }
        var project = new ReyProject { Name = "legacy", RootPath = root, ProjectFolders = { "One", "Two" }, GameDirectory = Game("legacy-game") };
        var vm = NewVm(project);
        var log = CaptureLog(vm);

        Call(vm, "BuildMounts");
        Call(vm, "BuildProjectTree");
        await Applied(vm);

        Assert.Null(Preview(vm));
        Assert.Null(Mounts(vm).Overlay);
        Assert.Empty(Lines(log, "GameData"));
        Assert.Equal(new[] { "One", "Two" }, Mounts(vm).Mounts.Where(m => m.Kind == AssetSourceKind.ProjectFolder).Select(m => m.Name).ToArray());
        Assert.Equal("from one", Text(Mounts(vm).Read(Hash(P))));                       // first listed wins, as ever
        Assert.False(Mounts(vm).IsOverlaid(Hash(P)));
        Assert.False(Mounts(vm).IsOverlayTarget(Hash(P)));
        Assert.DoesNotContain(vm.RootNodes, n => n.Name == "LTK GameData");
        Assert.Equal("Project", vm.RootNodes[0].Name);
    }

    [Fact]
    public async Task A_project_of_layers_that_stores_no_GameData_is_not_given_one_either()
    {
        string root = _temp.Combine("layers-only");
        Directory.CreateDirectory(Path.Combine(root, "Map11"));
        var project = new ReyProject { Name = "layers-only", RootPath = root, ProjectFolders = { "Map11" }, GameDirectory = Game("lo-game") };
        project.Layers.Add(new ProjectLayer { Name = "fix", Priority = 1 });                    // M744: layers, and no declarations

        var vm = await Open(project);

        Assert.Null(Preview(vm));
        Assert.Null(Mounts(vm).Overlay);
    }

    // ================================================================================================ readiness

    [Fact]
    public async Task A_target_only_the_game_has_is_not_readable_until_the_preview_is_ready_and_then_it_is_the_bytes_LTK_would_install()
    {
        string game = Game("g-ready");
        var gated = Gated(new InstalledGame(game, _temp.Combine("c-ready", "i.idx")));
        var project = LayeredProject(game);
        var vm = NewVm(project);
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        var log = CaptureLog(vm);

        Call(vm, "BuildMounts");
        var preview = Preview(vm)!;

        // pending: a read answers what the mounts hold - and nothing holds this chunk
        Assert.True(preview.IsPending);
        Assert.False(Mounts(vm).Has(Hash(A)));
        Assert.StartsWith("LTK GameData: ", vm.Status);                         // the status line says the preview is being prepared (the worker's own words follow at once)
        // the mod's own copy of M is what a read gives while pending (it is NOT final)
        Assert.Equal(new[] { "mod" }, TagsOf(Mounts(vm).Read(Hash(M))!, "Test/Obj/M"));

        gated.Gate.Set();
        await Applied(vm);

        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        Assert.True(Mounts(vm).Has(Hash(A)));
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(Mounts(vm).Read(Hash(A))!, "Test/Obj/A"));        // base layer, then the fix layer
        // the mod's own copy of M is the base the declarations apply to
        Assert.Equal(new[] { "mod", "base" }, TagsOf(Mounts(vm).Read(Hash(M))!, "Test/Obj/M"));
        Assert.Equal(new[] { "mod" }, TagsOf(Mounts(vm).ReadRaw(Hash(M))!, "Test/Obj/M"));                      // raw never answers the overlay
        Assert.StartsWith(preview.Summary!.Headline, vm.Status);
        Assert.Contains(Lines(log, "GameData"), l => l.Message.StartsWith("GameData: 3 modules changed 2 bins;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task What_a_read_returns_is_what_the_overlay_makes_of_the_chunk_byte_for_byte()
    {
        string game = Game("g-bytes");
        var project = LayeredProject(game);
        var vm = await Open(project);

        var preview = Preview(vm)!;
        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        var alone = new GameDataOverlay(preview.Setup!.Layers, preview.Setup.Game, preview.Setup.ModFiles, preview.Setup.Options);
        foreach (string path in new[] { A, M })
        {
            var expected = alone.Apply(Hash(path))!;
            Assert.True(expected.Applied);
            Assert.Equal(expected.Bytes, Mounts(vm).Read(Hash(path)));
        }
    }

    [Fact]
    public async Task Rebuilding_the_mounts_cancels_the_preview_it_replaces_and_starts_a_new_one()
    {
        string game = Game("g-cancel");
        var gated = Gated(new InstalledGame(game, _temp.Combine("c-cancel", "i.idx")));
        var project = LayeredProject(game);
        var vm = NewVm(project);
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));

        Call(vm, "BuildMounts");
        var first = Preview(vm)!;
        Call(vm, "BuildMounts");
        var second = Preview(vm)!;

        Assert.NotSame(first, second);
        await first.Settled.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(GameDataPreviewState.Cancelled, first.State);
        Assert.True(second.IsPending);
        gated.Gate.Set();
        await Applied(vm);
        Assert.Equal(GameDataPreviewState.Ready, second.State);
        Assert.Same(second, Mounts(vm).Overlay);
    }

    [Fact]
    public async Task A_map_that_is_opened_while_the_preview_is_pending_waits_for_it_without_blocking_and_a_settled_preview_makes_the_wait_no_wait()
    {
        string game = Game("g-wait");
        var gated = Gated(new InstalledGame(game, _temp.Combine("c-wait", "i.idx")));
        var project = LayeredProject(game);
        var vm = NewVm(project);
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        Call(vm, "BuildMounts");

        var waiting = (Task)Call(vm, "WaitForGameDataAsync")!;
        await Task.Delay(150);
        Assert.False(waiting.IsCompleted);                                   // it is waiting, and this thread is not

        gated.Gate.Set();
        await waiting.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(GameDataPreviewState.Ready, Preview(vm)!.State);
        Assert.True(((Task)Call(vm, "WaitForGameDataAsync")!).IsCompletedSuccessfully);   // nothing left to wait for
    }

    [Fact]
    public async Task A_project_with_no_GameData_never_waits()
    {
        var vm = NewVm(new ReyProject { Name = "x", RootPath = _temp.Combine("nowait") });
        Call(vm, "BuildMounts");

        Assert.True(((Task)Call(vm, "WaitForGameDataAsync")!).IsCompletedSuccessfully);
        await Applied(vm);
    }

    // ================================================================================================ failure never blocks a project

    [Fact]
    public async Task A_stored_document_that_is_not_JSON_is_an_error_in_the_log_and_the_project_stays_usable()
    {
        string game = Game("g-broken");
        var project = LayeredProject(game);
        File.WriteAllText(Path.Combine(LtkProjectStore.DirectoryOf(project.RootPath!, "base"), LtkProjectStore.DeclarationsFileName), "{ this is not json");
        var vm = NewVm(project);
        SetField(vm, "GameDataIndexCachePath", _temp.Combine("c-broken", "i.idx"));
        var log = CaptureLog(vm);

        Call(vm, "BuildMounts");                                             // an exception here would block the project from opening
        await Applied(vm);

        var preview = Preview(vm)!;
        Assert.Equal(GameDataPreviewState.Failed, preview.State);
        var error = Assert.Single(Lines(log, "GameData"), l => l.Level == LogLevel.Error);
        Assert.Contains("cannot be previewed", error.Message);
        Assert.Contains("is not valid JSON", error.Message);
        Assert.Contains("The project is open without it", error.Message);
        // nothing is served, no overlay is attached (so no guard claims a bin that is not overlaid), and the project's own files read as they do
        Assert.Null(Mounts(vm).Overlay);
        Assert.False(Mounts(vm).IsOverlaid(Hash(M)));
        Assert.False(Mounts(vm).IsOverlayTarget(Hash(M)));
        Assert.Equal(new[] { "mod" }, TagsOf(Mounts(vm).Read(Hash(M))!, "Test/Obj/M"));
        Assert.Equal("plain of fix", Text(Mounts(vm).Read(Hash(P))));
        Assert.Equal("LTK GameData: not applied - see the console.", vm.Status);
    }

    [Fact]
    public async Task A_missing_game_folder_is_a_warning_in_the_log_and_not_a_project_that_will_not_open()
    {
        var project = LayeredProject(Game("g-missing"));
        project.GameDirectory = _temp.Combine("no-such-game");
        var vm = NewVm(project);
        var log = CaptureLog(vm);

        Call(vm, "BuildMounts");                                             // an exception here would block the project from opening
        await Applied(vm);

        // the preview is ready and serves nothing of the GAME's bins: a bin the declarations change in the game alone is not readable, and the console says why
        var preview = Preview(vm)!;
        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        Assert.Null(preview.Failure);
        Assert.False(Mounts(vm).Has(Hash(A)));
        var warning = Assert.Single(Lines(log, "GameData"), l => l.Message.Contains("The game cannot be read", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("The configured folder does not exist", warning.Message);
        Assert.Contains("Set Game Folder", warning.Message);
        Assert.DoesNotContain(Lines(log, "GameData"), l => l.Level == LogLevel.Error);
        Assert.EndsWith("(see the console)", vm.Status);
        // what the declarations name is still known: the write guard holds without the game
        Assert.True(Mounts(vm).IsOverlayTarget(Hash(A)));
        Assert.True(Mounts(vm).IsOverlayTarget(Hash(M)));
        Assert.False(Mounts(vm).IsOverlayTarget(Hash(P)));
        // the mod's own copy of M is a base that needs no game: it is what LTK would make of it, and it is served; the project's other files read as they do
        Assert.Equal(new[] { "mod", "base" }, TagsOf(Mounts(vm).Read(Hash(M))!, "Test/Obj/M"));
        Assert.Equal("plain of fix", Text(Mounts(vm).Read(Hash(P))));
        var bin = Assert.Single(preview.Bins, b => b.Chunk == Hash(A));
        Assert.False(bin.Applied);
        Assert.Contains(bin.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.TargetSkipped && d.Message.Contains("the game index is unavailable", StringComparison.Ordinal));
        // the diagnostics window titles the warning, and offers no Retry for it: Project > Set Game Folder rebuilds the mounts
        var view = GameDataDiagnosticsView.Build(preview, b => (string)Call(vm, "GameDataBinName", b)!, () => { });
        var general = Assert.Single(view.Groups, g => g.BinName == "Not about one bin");
        var row = Assert.Single(general.Rows, r => r.ObjectName == "The game cannot be read");
        Assert.Null(row.FixLabel);
        Assert.False(row.HasFix);
    }

    // ================================================================================================ layers shadow one another as LTK installs them

    [Fact]
    public async Task A_higher_priority_layers_copy_of_a_file_replaces_the_base_layers()
    {
        var vm = await Open(LayeredProject(Game("g-layers")));

        // the base layer ships P, and so does the fix layer (priority 1): LTK keeps the fix layer's
        Assert.Equal("plain of fix", Text(Mounts(vm).Read(Hash(P))));
        Assert.Equal(new[] { "layers/fix/Map11", "Map11" }, Mounts(vm).SourcesOf(Hash(P)).Select(m => m.Name).Where(n => n != "Overrides").ToArray());
        // the browser still lists the mounts as the project lists them
        Assert.Equal(new[] { "Map11", "layers/fix/Map11" }, Mounts(vm).Mounts.Where(m => m.Kind == AssetSourceKind.ProjectFolder).Select(m => m.Name).ToArray());
    }

    [Fact]
    public async Task A_RAW_file_wins_over_every_layer_and_the_mod_copy_RAW_holds_is_the_base_of_a_target()
    {
        string game = Game("g-raw");
        var project = LayeredProject(game);
        string raw = Path.Combine(project.RootPath!, "RAW", "data", "t");
        Directory.CreateDirectory(raw);
        File.WriteAllText(Path.Combine(raw, "plain.bin"), "plain of raw");
        File.WriteAllBytes(Path.Combine(raw, "m.bin"), Bin(Obj("Test/Obj/M", new[] { "raw" })));
        project.ProjectFolders.Add("RAW");

        var vm = await Open(project);

        Assert.Equal("plain of raw", Text(Mounts(vm).Read(Hash(P))));
        Assert.Equal(new[] { "raw", "base" }, TagsOf(Mounts(vm).Read(Hash(M))!, "Test/Obj/M"));    // RAW is the base, then the base layer's module
    }

    [Fact]
    public async Task A_project_that_never_had_layers_mounts_its_folders_as_listed_even_with_a_folder_called_RAW()
    {
        string root = _temp.Combine("no-layers");
        foreach (var (folder, text) in new[] { ("Map11", "map"), ("RAW", "raw") })
        {
            Directory.CreateDirectory(Path.Combine(root, folder, "data", "t"));
            File.WriteAllText(Path.Combine(root, folder, "data", "t", "plain.bin"), text);
        }
        var vm = NewVm(new ReyProject { Name = "n", RootPath = root, ProjectFolders = { "Map11", "RAW" }, GameDirectory = Game("g-nl") });

        Call(vm, "BuildMounts");
        await Applied(vm);

        Assert.Equal("map", Text(Mounts(vm).Read(Hash(P))));       // listed first wins: a project that predates layers is not re-ordered
    }

    [Fact]
    public async Task A_rebuild_of_the_mounts_makes_a_new_preview_of_the_same_declarations_and_says_nothing_of_it_again()
    {
        // the file watcher rebuilds the mounts after any change under the project: the console must not repeat the summary every time
        var vm = NewVm(LayeredProject(Game("g-quiet")));
        SetField(vm, "GameDataIndexCachePath", _temp.Combine("c-quiet", "i.idx"));
        var log = CaptureLog(vm);
        Call(vm, "BuildMounts");
        await Applied(vm);
        var first = Preview(vm)!;
        int said = Lines(log, "GameData").Count;
        Assert.True(said > 0);

        Call(vm, "BuildMounts");
        await Applied(vm);

        Assert.NotSame(first, Preview(vm));
        Assert.Equal(GameDataPreviewState.Ready, Preview(vm)!.State);
        Assert.Equal(said, Lines(log, "GameData").Count);
        Assert.True(Mounts(vm).IsOverlaid(Hash(A)));                           // and the new service serves what the old one did
    }

    // ================================================================================================ read-only until editing GameData targets is supported

    [Fact]
    public async Task Every_write_path_refuses_a_chunk_the_GameData_changes_with_one_message_and_writes_nothing()
    {
        var project = LayeredProject(Game("g-guard"));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        var target = EntryOf(vm, A);                                            // a bin only the game has, as the GameData changes it
        var modCopy = EntryOf(vm, M);                                           // a bin the mod ships, and the GameData names
        var plain = EntryOf(vm, P);                                             // a bin the mod ships and nothing names
        Assert.Equal(AssetSourceKind.LtkGameData, target.SourceKind);
        Assert.True(target.ReadOnly);
        Assert.Equal(AssetSourceKind.LtkGameData, modCopy.SourceKind);
        string mFile = Path.Combine(project.RootPath!, "Map11", "data", "t", "m.bin");
        string mBefore = Convert.ToHexString(File.ReadAllBytes(mFile));
        string overrides = Path.Combine(project.RootPath!, ".reyengine", "overrides");
        string message = $"is {MainWindowViewModel.GameDataEditRefusal}";

        // the guard every editor starts a save with
        Assert.False((bool)Call(vm, "GuardEditable", target)!);
        Assert.False((bool)Call(vm, "GuardEditable", modCopy)!);
        Assert.True((bool)Call(vm, "GuardEditable", plain)!);                 // a bin nothing names is edited as ever
        Assert.Contains(Lines(log, "GameData"), l => l.Level == LogLevel.Warning && l.Message.Contains(message));
        // the one choke point every editor, repair, import and patch update saves a bin through
        Assert.False(await (Task<bool>)Call(vm, "SaveMapBinBytesAsync", target, Bin(Obj("Test/Obj/A", new[] { "x" })))!);
        Assert.False(await (Task<bool>)Call(vm, "SaveMapBinBytesAsync", modCopy, Bin(Obj("Test/Obj/M", new[] { "overlaid saved back" })))!);
        // the low paths refuse by throwing, so a caller that forgot to guard cannot write
        var ex = Assert.Throws<InvalidOperationException>(() => Call(vm, "TryWriteToProjectFile", modCopy, new byte[] { 1 }, ""));
        Assert.Contains(message, ex.Message);
        Assert.Throws<InvalidOperationException>(() => Call(vm, "TryPlaceInProjectFolder", target, new byte[] { 1 }, ""));
        Assert.Throws<InvalidOperationException>(() => Call(vm, "ThrowIfGameDataTarget", target));
        // the writer the lightmap flows end in (a materials.bin rewritten under the map): it must fail rather than write
        var baked = Assert.Throws<InvalidOperationException>(() => Call(vm, "WriteBakedAsset", M, new byte[] { 1 }, ".bin"));
        Assert.Contains(message, baked.Message);
        // Copy To Project of a game bin that GameData changes is refused too
        var node = new AssetNodeViewModel(new AssetTreeNode { Name = "a.bin", FullPath = A, Entry = target });
        Assert.False(await (Task<bool>)Call(vm, "CopyOneAssetToProject", node, null)!);

        Assert.Equal(mBefore, Convert.ToHexString(File.ReadAllBytes(mFile)));          // the project's file is as it was
        Assert.False(Directory.Exists(overrides) && Directory.EnumerateFiles(overrides).Any());   // and the override store is empty
        Assert.False(File.Exists(Path.Combine(project.RootPath!, "Map11", "data", "t", "a.bin")));   // nothing was copied in
        // a bin nothing names saves as ever
        Assert.True(await (Task<bool>)Call(vm, "SaveMapBinBytesAsync", plain, Bin(Obj("Test/Obj/P", new[] { "ok" })))!);
    }

    [Fact]
    public async Task The_lightmap_flows_that_end_in_the_maps_materials_bin_refuse_it_before_they_write_anything()
    {
        var (vm, _) = await DescribedTile("flows");
        var log = CaptureLog(vm);
        var project = vm.Project;
        // a map is open: the mapgeo the project ships, whose materials.bin is a chunk the GameData changes
        SetField(vm, "_currentMap", new MapGeoAsset { Positions = Array.Empty<float>(), Normals = Array.Empty<float>(), Uvs = Array.Empty<float>(), Indices = Array.Empty<uint>(), Groups = Array.Empty<MapGeoGroup>() });
        SetField(vm, "_currentMapEntry", EntryOf(vm, MapGeo));
        SetField(vm, "_currentMapBytes", new byte[] { 1, 2, 3 });
        Assert.True(Mounts(vm).IsOverlayTarget(Hash(MapMaterials)));
        string message = $"is {MainWindowViewModel.GameDataEditRefusal}";

        string said = await vm.EnableExperimentalLightmapShadersAsync();           // it would stage a shader companion and then write the materials.bin back
        var layout = await vm.GenerateLightmapLayoutAsync(new ReyEngine.Formats.Baking.BakeSettings());   // it would rewrite the mapgeo and then clear a macro in the materials.bin

        Assert.Contains(message, said);
        Assert.Null(layout);
        Assert.Equal(2, Lines(log, "GameData").Count(l => l.Level == LogLevel.Warning && l.Message.Contains(message)));
        Assert.DoesNotContain("ShaderCache.dx11", project.ProjectFolders);
        Assert.False(Directory.Exists(Path.Combine(project.RootPath!, "ShaderCache.dx11")));
        Assert.False(Directory.Exists(Path.Combine(project.RootPath!, ".reyengine", "backups")));
        string overrides = Path.Combine(project.RootPath!, ".reyengine", "overrides");
        Assert.False(Directory.Exists(overrides) && Directory.EnumerateFiles(overrides).Any());
    }

    [Fact]
    public async Task A_target_is_refused_while_the_preview_is_still_pending_by_what_the_declarations_name_themselves()
    {
        string game = Game("g-pending-guard");
        var gated = Gated(new InstalledGame(game, _temp.Combine("c-pg", "i.idx")));
        var project = LayeredProject(game);
        var vm = NewVm(project);
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        Call(vm, "BuildMounts");
        var preview = Preview(vm)!;
        for (int i = 0; i < 3000 && preview.DeclaredTargets is null; i++) await Task.Delay(20);   // up to a minute: a machine under load may not start the worker for a while       // the documents are read at once, the game is not

        Assert.True(preview.IsPending);
        Assert.True(Mounts(vm).IsOverlayTarget(Hash(M)));
        Assert.True(Mounts(vm).IsOverlayTarget(Hash(A)));
        Assert.False(Mounts(vm).IsOverlayTarget(Hash(P)));
        var modCopy = EntryOf(vm, M);
        Assert.False((bool)Call(vm, "GuardEditable", modCopy)!);
        gated.Gate.Set();
        await Applied(vm);
    }

    [Fact]
    public async Task A_patch_update_reads_the_projects_own_bytes_and_leaves_out_the_bins_the_GameData_changes()
    {
        var project = LayeredProject(Game("g-patch"));
        var vm = await Open(project);
        var log = CaptureLog(vm);

        // the rebase reads RAW: what the mod ships, never the declarations' result
        Assert.Equal(new[] { "mod" }, TagsOf((byte[])Call(vm, "ReadAssetRaw", Hash(M))!, "Test/Obj/M"));
        Assert.Equal(new[] { "mod", "base" }, TagsOf((byte[])Call(vm, "ReadAsset", Hash(M))!, "Test/Obj/M"));
        // and it does not offer a bin it could not save
        Assert.True((bool)Call(vm, "SkipsGameDataBin", EntryOf(vm, M))!);
        Assert.False((bool)Call(vm, "SkipsGameDataBin", EntryOf(vm, P))!);
        Assert.Contains(Lines(log, "PatchUpdate"), l => l.Message.Contains("not rebased onto the new patch"));
    }

    [Fact]
    public async Task The_Map_Skin_Switcher_refuses_a_shipping_bin_the_GameData_changes_before_it_writes_a_backup()
    {
        // the switcher reads the overlaid map bin and would write it back as a whole file: it asks first
        var project = LayeredProject(Game("g-switch"));
        var vm = await Open(project);
        vm.ProjectMode = true;
        var request = new MapSkinApplyRequest(
            new MapSkinMapViewModel { MapId = 11, ShippingBinEntry = EntryOf(vm, M), Catalog = new ReyEngine.Formats.Meta.MapSkinCatalog("SR", 0, Array.Empty<ReyEngine.Formats.Meta.MapSkinInfo>()) },
            new MapSkinOptionViewModel { Info = new ReyEngine.Formats.Meta.MapSkinInfo(0, 1, "a", "Default", null, 0) },
            new MapSkinOptionViewModel { Info = new ReyEngine.Formats.Meta.MapSkinInfo(1, 2, "b", "Other", "x", 0) });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => (Task<string>)Call(vm, "ApplyMapSkinSwapAsync", request)!);

        Assert.Contains(MainWindowViewModel.GameDataEditRefusal, ex.Message);
        string backups = Path.Combine(project.RootPath!, ".reyengine", "backups");
        Assert.False(Directory.Exists(backups) && Directory.EnumerateFileSystemEntries(backups).Any());
    }

    // ================================================================================================ the caches that go stale

    [Fact]
    public async Task When_what_is_served_changes_the_caches_built_from_the_bins_are_dropped_and_when_it_does_not_they_are_kept()
    {
        var project = LayeredProject(Game("g-stale"));
        var vm = await Open(project);
        var mounts = Mounts(vm);
        var preview = Preview(vm)!;

        void Dirty()
        {
            SetField(vm, "_mapState", ReyEngine.Formats.MapGeo.MapStateData.Empty);
            SetField(vm, "_mapStateFor", "x.mapgeo");
            SetField(vm, "_mapMaterialNames", new List<string> { "stale" });
            SetField(vm, "_propLightGrid", ("x.mapgeo", (ReyEngine.Formats.Lighting.LightGridFile?)null));
            Field<ConcurrentDictionary<string, IReadOnlyList<ReyEngine.Formats.Skeletons.AnimClipInfo>>>(vm, "_propClipTables")["skin"] = Array.Empty<ReyEngine.Formats.Skeletons.AnimClipInfo>();
            vm.Documents.Clear();
            vm.Documents.Add(new EditorDocument { Title = "map", Kind = DocumentKind.Map, Key = 7, Scene = new object() });
        }
        bool Stale() => Field<object?>(vm, "_mapState") is null && Field<object?>(vm, "_mapMaterialNames") is null
            && Field<(string?, ReyEngine.Formats.Lighting.LightGridFile?)>(vm, "_propLightGrid").Item1 is null
            && Field<ConcurrentDictionary<string, IReadOnlyList<ReyEngine.Formats.Skeletons.AnimClipInfo>>>(vm, "_propClipTables").IsEmpty
            && vm.Documents.All(d => d.Scene is null);

        // the same preview again: what is served is what the editor already shows, so nothing is dropped
        Dirty();
        Call(vm, "ApplyGameDataSettled", preview, mounts);
        Assert.False(Stale());

        // another set of declarations: the key moves, and every cache built from the old bins goes
        SetField(vm, "_gameDataShownKey", "an older key");
        Dirty();
        Call(vm, "ApplyGameDataSettled", preview, mounts);
        Assert.True(Stale());
    }

    [Fact]
    public async Task The_key_of_a_preview_is_its_documents_and_what_it_serves_and_changes_with_either()
    {
        string game = Game("g-key");
        var one = await Open(LayeredProject(game));
        var same = await Open(LayeredProject(game));
        var other = await Open(Import("other", Layers(Doc(AddTag(A, "Test/Obj/A", "different"))), game, Table(A)));

        string? keyOne = MainWindowViewModelKey(Preview(one)!), keySame = MainWindowViewModelKey(Preview(same)!), keyOther = MainWindowViewModelKey(Preview(other)!);

        Assert.NotNull(keyOne);
        Assert.Equal(keyOne, keySame);
        Assert.NotEqual(keyOne, keyOther);
    }

    private static string? MainWindowViewModelKey(GameDataPreview preview) =>
        (string?)typeof(MainWindowViewModel).GetMethod("GameDataKeyOf", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { preview });

    // ================================================================================================ the tree, the badge and the diagnostics

    private static AssetNodeViewModel[] Walk(IEnumerable<AssetNodeViewModel> roots) => roots.SelectMany(r => new[] { r }.Concat(Walk(r.Children))).ToArray();

    [Fact]
    public async Task A_bin_only_the_game_has_is_listed_under_LTK_GameData_and_a_bin_the_project_ships_is_listed_as_what_is_read()
    {
        var vm = await Open(LayeredProject(Game("g-tree")));
        Call(vm, "BuildProjectTree");
        var roots = vm.RootNodes;

        var group = Assert.Single(roots, r => r.Name == "LTK GameData");
        var listed = Walk(new[] { group }).Where(n => n.Entry is not null).ToList();
        var a = Assert.Single(listed);
        Assert.Equal(Hash(A), a.Entry!.PathHash);
        Assert.Equal("LTK", a.SourceTag);
        Assert.True(a.IsGameData);
        Assert.True(a.IsReadOnly);
        Assert.Contains("LTK GameData", a.ReadOnlyTip);
        // the project's own copy of M is listed in its folder as what the editor reads for it
        var m = Walk(roots.Where(r => r.Name == "Project")).Single(n => n.Entry?.PathHash == Hash(M));
        Assert.Equal("LTK", m.SourceTag);
        Assert.True(m.IsReadOnly);
        Assert.False(m.HasConflict);                                             // the project's copy sits under what is read by design: no conflict between mounts
        // a bin nothing names is what it was
        var p = Walk(roots.Where(r => r.Name == "Project")).First(n => n.Entry?.PathHash == Hash(P));
        Assert.Equal("PRJ", p.SourceTag);
        Assert.False(p.IsGameData);
        Assert.Equal("Read-only Riot reference", p.ReadOnlyTip);
    }

    [Fact]
    public async Task The_diagnostics_window_lists_each_bin_with_its_state_and_collapses_a_kind_that_repeats()
    {
        // one module with more skipped edits than a window lists row by row
        var edits = string.Join(",", Enumerable.Range(0, GameDataDiagnosticsView.ListEachUpTo + 4).Select(i => $"\"Test/Obj/Missing{i}\":{{\"+tags\":[\"x\"]}}"));
        var project = Import("diag", Layers(Doc(Target(A, $"{{{edits}}}"), AddTag(M, "Test/Obj/M", "base"))), Game("g-diag"),
            ("WAD/Map11.wad.client", ModWad("diag", (M, Bin(Obj("Test/Obj/M", new[] { "mod" }))))), Table(A, M));
        var vm = await Open(project);

        var view = GameDataDiagnosticsView.Build(Preview(vm)!, bin => (string)Call(vm, "GameDataBinName", bin)!, null);

        Assert.StartsWith("LTK GameData - GameData: 2 modules changed 1 bin; ", view.BinName);
        Assert.Contains("Read-only: editing a changed bin comes with the next update", view.Description);
        Assert.Null(view.RepairAsync);
        var unchanged = Assert.Single(view.Groups, g => g.BinName.StartsWith(A, StringComparison.Ordinal));
        Assert.Contains("not changed", unchanged.BinName);                          // every edit of its module was skipped: the bin is as it was
        var collapsed = Assert.Single(unchanged.Rows, r => r.Kind == "propertyEditSkipped");
        Assert.StartsWith($"{GameDataDiagnosticsView.ListEachUpTo + 4} x propertyEditSkipped", collapsed.ObjectName);
        Assert.StartsWith("For example: ", collapsed.Message);
        Assert.Contains(unchanged.Rows, r => r.Kind == "noEffect");
        var changed = Assert.Single(view.Groups, g => g.BinName.StartsWith(M, StringComparison.Ordinal));
        Assert.Contains("changed (1 application(s) from base)", changed.BinName);
        // the test's own schema knows no class and the game said no build: the edit is typed by the fallback, which is said, and only said (informational)
        Assert.NotEmpty(changed.Rows);
        Assert.All(changed.Rows, r => Assert.Equal("schemaFallback", r.Kind));
    }

    [Fact]
    public async Task An_incomplete_index_is_a_row_with_a_Retry_that_rebuilds_the_mounts()
    {
        var game = new SyntheticGame()
            .Add("DATA.wad.client", "data/readme.txt", new byte[] { 1 })
            .Add("Global.wad.client", "data/global.txt", new byte[] { 2 })
            .Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
            .Add("B.wad.client", "data/t/b.bin", Bin(Obj("Test/Obj/B", new[] { "b" })));
        string directory = game.Write(_temp.Combine("g-unsettled"));
        var project = Import("unsettled", Layers(Doc(Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]},\"Test/Obj/B\":{\"+tags\":[\"e\"]}"))), directory, Table(A));
        GameDataPreview preview;
        int retries = 0;
        using (new FileStream(Path.Combine(directory, "DATA", "FINAL", "B.wad.client"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var vm0 = await Open(project);
            preview = Preview(vm0)!;
        }
        Assert.True(preview.Summary!.IndexUnsettled);

        var view = GameDataDiagnosticsView.Build(preview, bin => $"0x{bin.Chunk:x16}", () => retries++);

        var general = view.Groups.First();
        Assert.Equal("Not about one bin", general.BinName);
        var row = Assert.Single(general.Rows, r => r.ObjectName == "The game's index is incomplete");
        Assert.Equal("Retry", row.FixLabel);
        Assert.True(row.HasFix);
        Assert.Contains("not a fact about the game", row.Message);
        await row.ApplyFixCommand.ExecuteAsync(null);
        Assert.Equal(1, retries);
        Assert.Equal("Retrying - the mounts are rebuilt and the game is read again.", row.FixStatus);
        // an entry no bin declares says that may be wrong, too
        Assert.Contains(view.Groups.SelectMany(g => g.Rows), r => r.Kind == "entryUnresolved" && r.Suggestion.Contains("that may be wrong"));
    }

    // ================================================================================================ the map the game loads

    [Fact]
    public async Task The_container_maps_of_a_preview_are_listed_among_the_maps_even_when_only_the_Riot_reference_holds_them()
    {
        const string ship = "data/maps/shipping/map11/map11.bin";
        var skin = new PropObject(H("Maps/Shipping/Map11/MapSkins/Default"), H("MapSkin"));
        skin.Properties.Set(H("name"), new PropString("Default"));
        skin.Properties.Set(H("mMapContainerLink"), new PropString("Maps/MapGeometry/Map11/Base_SRX"));
        string directory = new SyntheticGame()
            .Add("DATA.wad.client", "data/readme.txt", new byte[] { 1 })
            .Add("Global.wad.client", "data/global.txt", new byte[] { 2 })
            .Add("Map11.wad.client", ship, Bin(skin))
            .Write(_temp.Combine("g-maps"));
        var project = Import("maps", Layers(Doc(Target(ship, "{\"Maps/Shipping/Map11/MapSkins/Default\":{\"mMapContainerLink\":\"Maps/MapGeometry/Map11/Milkshake_SRS\"}}"))), directory, Table(ship));

        var vm = await Open(project);

        var maps = Field<HashSet<ulong>>(vm, "_gameDataContainerMaps");
        Assert.Equal(new[] { Hash("data/Maps/MapGeometry/Map11/Milkshake_SRS.mapgeo") }, maps.ToArray());
    }

    // ================================================================================================ the map thumbnails

    private const string MapGeo = "data/maps/mapgeometry/map11/milkshake_srs.mapgeo", MapMaterials = "data/maps/mapgeometry/map11/milkshake_srs.materials.bin";

    private async Task<(MainWindowViewModel Vm, object? Tile)> DescribedTile(string tag, GatedGame? gate = null)
    {
        string directory = new SyntheticGame()
            .Add("DATA.wad.client", "data/readme.txt", new byte[] { 1 })
            .Add("Global.wad.client", "data/global.txt", new byte[] { 2 })
            .Add("Map11.wad.client", MapMaterials, Bin(Obj("Test/Obj/M", new[] { "game" })))
            .Write(_temp.Combine("g-tile-" + tag + _n++));
        File.WriteAllBytes(Path.Combine(directory, "DATA", "FINAL", "ShaderCache.dx11.wad.client"), new byte[] { 1 });   // a tile needs a shader cache to be drawn with
        var project = Import("tile" + tag, Layers(Doc(AddTag(MapMaterials, "Test/Obj/M", tag))), directory,
            ("WAD/Map11.wad.client", ModWad("tile" + tag, (MapGeo, new byte[] { 1, 2, 3 }), (MapMaterials, Bin(Obj("Test/Obj/M", new[] { "mod" }))))), Table(MapGeo, MapMaterials));
        var vm = NewVm(project);
        SetField(vm, "GameDataIndexCachePath", _temp.Combine("c-tile-" + tag + _n++, "i.idx"));
        if (gate is not null) SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gate));
        Call(vm, "BuildMounts");
        if (gate is null) await Applied(vm);
        var node = new AssetNodeViewModel(new AssetTreeNode { Name = "milkshake_srs.mapgeo", FullPath = MapGeo, Entry = EntryOf(vm, MapGeo) });
        return (vm, Call(vm, "DescribeMapThumbnail", node));
    }

    [Fact]
    public async Task A_map_tile_is_not_described_while_the_preview_is_pending_and_its_key_follows_the_GameData_once_it_is_ready()
    {
        string directory = Game("g-tile-pending");
        var gated = Gated(new InstalledGame(directory, _temp.Combine("c-tile-pending", "i.idx")));
        var (pendingVm, pendingTile) = await DescribedTile("a", gated);
        Assert.Null(pendingTile);                                              // no picture is better than one of the unchanged bins
        gated.Gate.Set();
        await Applied(pendingVm);

        var (vmA, readyA) = await DescribedTile("a");
        Assert.NotNull(readyA);
        string KeyOf(object? tile) => (string)tile!.GetType().GetProperty("Key")!.GetValue(tile)!;
        string withGameData = KeyOf(readyA);
        // the same tile with the preview taken away (as a project with no GameData is): the documents' fingerprint is the line the key loses
        var node = new AssetNodeViewModel(new AssetTreeNode { Name = "milkshake_srs.mapgeo", FullPath = MapGeo, Entry = EntryOf(vmA, MapGeo) });
        SetField(vmA, "_gameData", null);
        string without = KeyOf(Call(vmA, "DescribeMapThumbnail", node));
        Assert.NotEqual(withGameData, without);
        // and the materials bin it reads is the overlay's own bytes, which no file on disk identifies: the overlay's identity is in the key as well
        var (_, readyB) = await DescribedTile("b");
        Assert.NotEqual(withGameData, KeyOf(readyB));
    }

    [Fact]
    public async Task A_preview_that_settles_with_nothing_to_serve_still_asks_the_map_tiles_it_held_back_again()
    {
        // the declarations change nothing (the object is not in the bin), so there is no tree to rebuild - and the tiles that were not described while the preview was pending must still be asked
        string game = Game("g-noop");
        var gated = Gated(new InstalledGame(game, _temp.Combine("c-noop", "i.idx")));
        var project = Import("noop", Layers(Doc(AddTag(A, "Test/Obj/NotThere", "x"))), game, Table(A));
        var vm = NewVm(project);
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        Call(vm, "BuildMounts");
        int asked = 0;
        vm.RefreshMapThumbnailCommand.CanExecuteChanged += (_, _) => Interlocked.Increment(ref asked);   // what the tile service is told with: NotifyMapThumbnailAvailability

        gated.Gate.Set();
        await Applied(vm);

        Assert.Equal(GameDataPreviewState.Ready, Preview(vm)!.State);
        Assert.Empty(Preview(vm)!.Entries);
        Assert.True(Volatile.Read(ref asked) >= 1);
    }

    // ================================================================================================ what the command says with nothing to show

    [Fact]
    public async Task The_diagnostics_command_says_so_for_a_project_with_no_GameData_and_does_not_throw()
    {
        var vm = NewVm(new ReyProject { Name = "plain", RootPath = _temp.Combine("plain-cmd") });
        var log = CaptureLog(vm);
        Call(vm, "BuildMounts");
        await Applied(vm);

        Call(vm, "OpenGameDataDiagnostics");

        Assert.Contains(Lines(log, "GameData"), l => l.Message.StartsWith("This project stores no LTK GameData", StringComparison.Ordinal));
    }

    // ================================================================================================ the guard while the preview works

    private static MapGeoAsset EmptyMap() => new()
    {
        Positions = Array.Empty<float>(), Normals = Array.Empty<float>(), Uvs = Array.Empty<float>(),
        Indices = Array.Empty<uint>(), Groups = Array.Empty<MapGeoGroup>(),
    };

    [Fact]
    public async Task Until_the_preview_has_planned_every_bin_is_refused_and_a_bin_an_entries_module_names_is_a_target_once_it_has()
    {
        string game = Game("g-entries-guard");
        // an ENTRIES module names Test/Obj/M; the mod ships m.bin, and no target module names it: only the plan, made from the game's index, knows it is a target
        var project = Import("entries-guard", Layers(Doc(Entries("\"Test/Obj/M\":{\"+tags\":[\"e\"]}"))), game,
            ("WAD/Map11.wad.client", ModWad("eg", (M, Bin(Obj("Test/Obj/M", new[] { "mod" }))), (P, Encoding.UTF8.GetBytes("plain")))), Table(M, P));
        var gated = Gated(new InstalledGame(game, _temp.Combine("c-entries-guard", "i.idx")));
        var vm = NewVm(project);
        SetField(vm, "GameDataWriteWait", TimeSpan.FromMilliseconds(150));   // a save made while the preview works waits for it - for this long here, so that the refusal that follows is the one asserted
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        var log = CaptureLog(vm);

        Call(vm, "BuildMounts");
        var preview = Preview(vm)!;
        for (int i = 0; i < 3000 && preview.DeclaredTargets is null; i++) await Task.Delay(20);   // up to a minute: a machine under load may not start the worker for a while

        // pending, the game's table not read: the declarations alone do not say which bins they name
        Assert.True(preview.IsPending);
        Assert.False(preview.TargetsKnown);
        Assert.False(Mounts(vm).IsOverlayTarget(Hash(M)));
        Assert.False(Mounts(vm).OverlayTargetsKnown);
        var modCopy = EntryOf(vm, M);
        var plain = EntryOf(vm, P);
        string pending = MainWindowViewModel.GameDataPendingRefusal;
        Assert.False((bool)Call(vm, "GuardEditable", modCopy)!);                  // the bin the entries module will turn out to name
        Assert.False((bool)Call(vm, "GuardEditable", plain)!);                    // and any other: which are the targets is not known yet
        Assert.False(await (Task<bool>)Call(vm, "SaveMapBinBytesAsync", modCopy, Bin(Obj("Test/Obj/M", new[] { "held from before" })))!);
        Assert.False(await (Task<bool>)Call(vm, "SaveMapBinBytesAsync", plain, Bin(Obj("Test/Obj/P", new[] { "autosave" })))!);
        Assert.Throws<InvalidOperationException>(() => Call(vm, "ThrowIfGameDataTarget", plain));
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains("'plain.bin': " + pending + ". Nothing was written.", StringComparison.Ordinal));
        // a file that is no bin is not held back
        var texture = new WadAssetEntry { PathHash = Hash("data/t/x.dds"), Path = "data/t/x.dds", IsResolved = true, Type = AssetType.Texture };
        Assert.True((bool)Call(vm, "GuardEditable", texture)!);
        string mFile = Path.Combine(project.RootPath!, "Map11", "data", "t", "m.bin");
        string before = Convert.ToHexString(File.ReadAllBytes(mFile));

        gated.Gate.Set();
        await Applied(vm);

        // planned: the entries module's bin is a target for good, and a bin nothing names is edited as ever
        Assert.Equal(GameDataPreviewState.Ready, preview.State);
        Assert.True(preview.TargetsKnown);
        Assert.True(Mounts(vm).IsOverlayTarget(Hash(M)));
        Assert.False(Mounts(vm).IsOverlayTarget(Hash(P)));
        Assert.False((bool)Call(vm, "GuardEditable", EntryOf(vm, M))!);
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains($"is {MainWindowViewModel.GameDataEditRefusal}", StringComparison.Ordinal));
        Assert.True((bool)Call(vm, "GuardEditable", EntryOf(vm, P))!);
        Assert.Equal(before, Convert.ToHexString(File.ReadAllBytes(mFile)));      // nothing was written all along
    }

    [Fact]
    public async Task Staging_is_all_or_nothing_where_the_GameData_is_concerned_and_a_placement_is_checked_before_anything_is_staged()
    {
        var project = LayeredProject(Game("g-staging"));
        var vm = await Open(project);
        var destination = EntryOf(vm, P);
        // a file with no Riot source of its own is staged under "Overrides" (the folder named for the Riot WAD it would belong to)
        string staged = Path.Combine(project.RootPath!, "Overrides", "data", "t", "staged-first.bin");
        var sources = new List<(string Path, byte[] Bytes)> { ("data/t/staged-first.bin", Bin(Obj("Test/Obj/S", new[] { "s" }))), (M, Bin(Obj("Test/Obj/M", new[] { "x" }))) };

        var result = Staged(Call(vm, "WriteStagedAssets", sources, destination, new List<string>(), false));

        Assert.Equal(0, result.Written);
        Assert.Equal(new[] { M }, result.Missing.ToArray());                         // the one the GameData changes
        Assert.Contains($"is {MainWindowViewModel.GameDataEditRefusal}", result.Refusal);   // and the reason is told as it is, not as a file that was not found
        Assert.False(File.Exists(staged));                                            // and the one before it, which no module names, was not written either

        // a placement ends in the map's bins: checked before the first file is staged
        Assert.Null(Call(vm, "PlacementWriteRefusal", EntryOf(vm, P)));              // no materials.bin beside it: nothing to refuse

        var (tileVm, _) = await DescribedTile("placement");
        string? refusal = (string?)Call(tileVm, "PlacementWriteRefusal", EntryOf(tileVm, MapGeo));
        Assert.NotNull(refusal);
        Assert.Contains($"is {MainWindowViewModel.GameDataEditRefusal}", refusal);

        // nothing is held against a staging that touches no bin the GameData names
        var clear = Staged(Call(vm, "WriteStagedAssets",
            new List<(string Path, byte[] Bytes)> { ("data/t/staged-first.bin", new byte[] { 1 }) }, destination, new List<string>(), false));
        Assert.Equal(1, clear.Written);
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public async Task The_flows_that_end_by_writing_the_maps_materials_bin_say_no_before_they_stage_a_thing()
    {
        var (vm, _) = await DescribedTile("preflight");
        var log = CaptureLog(vm);
        SetField(vm, "_currentMap", EmptyMap());
        SetField(vm, "_currentMapEntry", EntryOf(vm, MapGeo));
        string message = $"is {MainWindowViewModel.GameDataEditRefusal}";

        // Add Mesh: a plan that creates a material would stage its texture first and write the bin last
        var plan = new AddMeshPlan(Array.Empty<ImportedSceneMesh>(),
            new[] { new AddMeshMaterialPlan("m", CreateNew: true, ExistingMaterial: null, NewName: "NewMat", ShaderPath: "Shaders/Test", TextureBytes: null, TextureFileNameHint: null) },
            new Dictionary<string, string>(), 0);
        await (Task)Call(vm, "ExecuteAddMeshPlanAsync", plan)!;
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains(message, StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(log, "AddMesh"), l => l.Message.StartsWith("Materials bin:", StringComparison.Ordinal));   // it never got as far as reading the bin to change it

        // the Workshop imports: the textures and assets they bring are staged before the bin is written
        var material = await Assert.ThrowsAsync<InvalidOperationException>(() => (Task<string>)Call(vm, "ImportWorkshopMaterialAsync", null, "NewMat")!);
        Assert.Contains(message, material.Message);
        var particle = await Assert.ThrowsAsync<InvalidOperationException>(() => (Task<string>)Call(vm, "ImportWorkshopParticleAsync", null, "NewFx")!);
        Assert.Contains(message, particle.Message);
        Assert.False(Directory.Exists(Path.Combine(vm.Project.RootPath!, ".reyengine", "overrides")) && Directory.EnumerateFiles(Path.Combine(vm.Project.RootPath!, ".reyengine", "overrides")).Any());
    }

    [Fact]
    public async Task The_wizard_of_the_patch_update_waits_for_the_preview_instead_of_choosing_its_rows_without_it()
    {
        var gated = Gated(new InstalledGame(Game("g-wizard"), _temp.Combine("c-wizard", "i.idx")));
        var project = LayeredProject(Game("g-wizard-p"));
        var vm = NewVm(project);
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        var log = CaptureLog(vm);
        Call(vm, "BuildMounts");

        Call(vm, "OpenPatchUpdateWizard");                                           // opens no window while the preview is pending

        Assert.Contains(Lines(log, "PatchUpdate"), l => l.Message.Contains("open the Patch Update wizard again in a moment", StringComparison.Ordinal));
        gated.Gate.Set();
        await Applied(vm);
    }

    // ================================================================================================ what a reload may throw away

    [Fact]
    public async Task The_reload_that_follows_a_preview_leaves_the_open_map_alone_while_it_holds_work_nobody_has_saved()
    {
        var (vm, _) = await DescribedTile("unsaved");
        var preview = Preview(vm)!;
        var log = CaptureLog(vm);
        SetField(vm, "_currentMap", EmptyMap());
        SetField(vm, "_currentMapEntry", EntryOf(vm, MapGeo));
        // the test asks whether the map is reloaded; it does not load one (the three bytes of its mapgeo would fail, and a failed load awaits the editor's dispatcher, which a test does not run)
        var reloads = new List<string>();
        SetField(vm, "GameDataMapReload", new Func<WadAssetEntry, Task>(e => { lock (reloads) reloads.Add(e.Path); return Task.CompletedTask; }));
        bool Reloaded(int from) => Lines(log, "GameData").Skip(from).Any(l => l.Message.StartsWith("Reloading milkshake_srs.mapgeo", StringComparison.Ordinal));
        bool Warned(int from) => Lines(log, "GameData").Skip(from).Any(l => l.Message.Contains("has unsaved edits, so it was not reloaded", StringComparison.Ordinal));

        var kinds = new (string What, Action Set, Action Clear)[]
        {
            ("mesh moves, deleted pieces, face edits", () => vm.HasMapMoves = true, () => vm.HasMapMoves = false),
            ("placement edits", () => vm.HasParticleMoves = true, () => vm.HasParticleMoves = false),
            ("painted textures", () => vm.HasUnsavedPaint = true, () => vm.HasUnsavedPaint = false),
            ("a stroke in progress", () => SetField(vm, "_paintStrokeActive", true), () => SetField(vm, "_paintStrokeActive", false)),
        };
        foreach (var (what, set, clear) in kinds)
        {
            int from = Lines(log, "GameData").Count;
            set();
            Call(vm, "RefreshForGameData", preview);
            Assert.True(Warned(from), what);
            Assert.False(Reloaded(from), what);
            lock (reloads) Assert.Empty(reloads);
            clear();
        }

        // and with nothing to lose it is loaded again, as before
        int start = Lines(log, "GameData").Count;
        Call(vm, "RefreshForGameData", preview);
        Assert.True(Reloaded(start));
        Assert.False(Warned(start));
        lock (reloads) Assert.Equal(new[] { MapGeo }, reloads.ToArray());
    }

    [Fact]
    public async Task A_tab_that_was_left_with_unsaved_edits_keeps_its_snapshot_when_a_preview_changes_what_is_served()
    {
        var (vm, _) = await DescribedTile("tabs");
        var log = CaptureLog(vm);
        var sceneType = typeof(MainWindowViewModel).GetNestedType("MapScene", BindingFlags.NonPublic)!;
        object Snapshot(bool moves)
        {
            var scene = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(sceneType);
            void Set(string name, object? value) => sceneType.GetField($"<{name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(scene, value);
            Set("HasMoves", moves);
            Set("Pieces", new List<MapPieceViewModel>());
            Set("Map", EmptyMap());
            return scene;
        }

        vm.Documents.Clear();
        var withEdits = new EditorDocument { Title = "left with edits", Kind = DocumentKind.Map, Key = 1, Scene = Snapshot(moves: true) };
        var clean = new EditorDocument { Title = "left clean", Kind = DocumentKind.Map, Key = 2, Scene = Snapshot(moves: false) };
        var notAMap = new EditorDocument { Title = "a bin", Kind = DocumentKind.Bin, Key = 3, Scene = new object() };
        vm.Documents.Add(withEdits);
        vm.Documents.Add(clean);
        vm.Documents.Add(notAMap);

        Call(vm, "InvalidateGameDataState");

        Assert.NotNull(withEdits.Scene);                                              // dropping it would drop the edits
        Assert.Null(clean.Scene);                                                     // the others are loaded again when the tab is
        Assert.NotNull(notAMap.Scene);
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains("left with edits holds unsaved edits", StringComparison.Ordinal));
    }

    // ================================================================================================ the settle is the editor's, and the readers that wait

    [Fact]
    public async Task A_settle_that_throws_is_logged_and_does_not_fault_what_the_readers_that_wait_are_waiting_for()
    {
        string game = Game("g-throw");
        var gated = Gated(new InstalledGame(game, _temp.Combine("c-throw", "i.idx")));
        var vm = NewVm(LayeredProject(game));
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        var log = CaptureLog(vm);
        Call(vm, "BuildMounts");
        // whatever the settle touches that raises an exception: here, a handler of the status line - once, so that nothing that sets the status later, on a thread nothing guards, meets it
        int armed = 1;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Status" && vm.Status.StartsWith("GameData:", StringComparison.Ordinal) && Interlocked.Exchange(ref armed, 0) == 1)
                throw new InvalidOperationException("boom");
        };

        gated.Gate.Set();
        await Applied(vm);                                                           // would throw if the task had faulted
        await ((Task)Call(vm, "WaitForGameDataAsync")!).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Contains(Lines(log, "GameData"), l => l.Level == LogLevel.Error && l.Message.Contains("could not be applied to the editor: boom", StringComparison.Ordinal));
        Assert.Equal("LTK GameData: not applied - see the console.", vm.Status);
    }

    [Fact]
    public async Task A_map_load_that_waited_for_the_preview_is_dropped_when_the_project_changed_meanwhile()
    {
        var gated = Gated(new InstalledGame(Game("g-drop"), _temp.Combine("c-drop", "i.idx")));
        var (vm, _) = await DescribedTile("drop", gated);                            // pending
        var log = CaptureLog(vm);
        var entry = EntryOf(vm, MapGeo);

        var load = (Task)Call(vm, "LoadMapGeoAsync", entry)!;
        await Task.Delay(150);
        Assert.False(load.IsCompleted);                                              // waiting, and nobody is blocked
        vm.Project = new ReyProject { Name = "another", RootPath = _temp.Combine("another-project") };   // the user opens another project meanwhile
        gated.Gate.Set();
        await load.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains("was not opened: the project it was asked for changed", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Category == "MapGeo" && l.Message.StartsWith("Decoding", StringComparison.Ordinal));
        Assert.Null(Field<WadAssetEntry?>(vm, "_currentMapEntry"));
    }

    [Fact]
    public async Task A_map_load_that_waited_for_the_preview_follows_a_rebuild_of_the_mounts_to_the_entry_they_hold_now()
    {
        using var ui = new UiLikeContext();                                         // the editor's one thread: the wait comes back to it, and the rebuild is not interleaved with it
        var gated = Gated(new InstalledGame(Game("g-follow"), _temp.Combine("c-follow", "i.idx")));
        var (vm, _) = await DescribedTile("follow", gated);
        var log = CaptureLog(vm);
        var entry = EntryOf(vm, MapGeo);
        var before = Mounts(vm);

        // the part of a map load that waits for the preview and looks again at what was asked for - not the decoding after it, which a test cannot end (see EntryAfterGameDataAsync)
        var waiting = await ui.Run(() => (Task<WadAssetEntry?>)Call(vm, "EntryAfterGameDataAsync", entry)!);
        await Task.Delay(150);
        Assert.False(waiting.IsCompleted);
        await ui.Run(() => Call(vm, "BuildMounts"));                                 // a file changed: the mounts are rebuilt while the load waits
        gated.Gate.Set();
        var followed = await waiting.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.NotSame(before, Mounts(vm));
        Assert.NotNull(followed);                                                    // it goes on, with the entry the mounts that are there now hold
        Assert.Equal(entry.PathHash, followed!.PathHash);
        Assert.DoesNotContain(Lines(log, "GameData"), l => l.Message.Contains("was not opened", StringComparison.Ordinal));

        // nothing is left running that could come back to the context, and nothing the context ran threw
        await Quiesce(vm);
        Assert.Empty(ui.Faults);
    }

    [Fact]
    public async Task A_map_load_that_waited_for_the_preview_is_dropped_when_the_rebuilt_mounts_no_longer_hold_the_map()
    {
        using var ui = new UiLikeContext();
        var gated = Gated(new InstalledGame(Game("g-gone"), _temp.Combine("c-gone", "i.idx")));
        var (vm, _) = await DescribedTile("gone", gated);
        var log = CaptureLog(vm);
        var gone = new WadAssetEntry { PathHash = Hash("data/maps/mapgeometry/map11/vanished.mapgeo"), Path = "data/maps/mapgeometry/map11/vanished.mapgeo", IsResolved = true, Type = AssetType.MapGeometry };

        var waiting = await ui.Run(() => (Task<WadAssetEntry?>)Call(vm, "EntryAfterGameDataAsync", gone)!);
        await Task.Delay(150);
        await ui.Run(() => Call(vm, "BuildMounts"));
        gated.Gate.Set();
        var followed = await waiting.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Null(followed);
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains("vanished.mapgeo was not opened: the rebuilt project no longer holds it", StringComparison.Ordinal));
        await Quiesce(vm);
        Assert.Empty(ui.Faults);
    }

    [Fact]
    public async Task A_one_thread_context_never_throws_at_a_poster_that_comes_after_its_end_and_says_what_threw()
    {
        var ui = new UiLikeContext();
        Assert.Equal(3, await ui.Run(() => 3));
        var threw = ui.Run<int>(() => throw new InvalidOperationException("in the callback"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => threw);          // the caller of Run hears of it
        bool ran = false;
        ui.Post(_ => throw new InvalidOperationException("posted, and thrown"), null);   // and a posted callback that throws is recorded, not fatal
        Assert.Equal(1, await ui.Run(() => 1));
        Assert.Contains(ui.Faults, e => e.Message == "posted, and thrown");

        // an await that began on the context, and whose continuation comes after the context has ended: what a map load that outlives its test does
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool resumed = false;
        var started = await ui.Run(async () => { await release.Task; resumed = true; return 1; });

        ui.Dispose();
        ui.Post(_ => ran = true, null);                                              // late: dropped, no exception
        var late = ui.Run(() => 1);
        Assert.True(late.IsCanceled);
        await Task.Run(() => release.SetResult());                                   // the continuation is posted from a pool thread, to a context that is gone: dropped there too
        await Task.Delay(100);
        Assert.False(ran);
        Assert.False(resumed);
        Assert.False(started.IsCompleted);                                           // it never resumes: there is nowhere left to resume it
        Assert.Equal(new[] { "posted, and thrown" }, ui.Faults.Select(e => e.Message).ToArray());   // the one the test posted on purpose, and nothing the late posts did
    }

    // ================================================================================================ a read that was not final

    [Fact]
    public async Task A_chunk_read_while_the_preview_was_pending_is_refreshed_when_it_is_ready_even_when_the_result_is_the_same()
    {
        string game = Game("g-late");
        var vm = await Open(LayeredProject(game));
        var preview = Preview(vm)!;
        string keyBefore = MainWindowViewModelKey(preview)!;

        void Dirty()
        {
            SetField(vm, "_mapMaterialNames", new List<string> { "built from the unchanged bins" });
            Field<ConcurrentDictionary<string, IReadOnlyList<ReyEngine.Formats.Skeletons.AnimClipInfo>>>(vm, "_propClipTables")["skin"] = Array.Empty<ReyEngine.Formats.Skeletons.AnimClipInfo>();
        }
        bool Stale() => Field<object?>(vm, "_mapMaterialNames") is null;

        async Task Rebuild(bool readATargetMeanwhile)
        {
            var gated = Gated(new InstalledGame(game, _temp.Combine("cache-late-" + _n++, "i.idx")));
            SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
            Call(vm, "BuildMounts");                                                 // a file changed: a new preview of the same declarations
            Dirty();
            var next = Preview(vm)!;
            Assert.True(next.IsPending);
            if (readATargetMeanwhile) Assert.NotNull(Mounts(vm).Read(Hash(M)));      // answered from the mounts: not what the preview will serve
            gated.Gate.Set();
            await Applied(vm);
            Assert.Equal(keyBefore, MainWindowViewModelKey(next));                   // the same result as before
        }

        await Rebuild(readATargetMeanwhile: false);
        Assert.False(Stale());                                                       // nothing read it early, and it is the same: nothing is dropped

        await Rebuild(readATargetMeanwhile: true);
        Assert.True(Stale());                                                        // something did: it is built again from what is served
    }

    [Fact]
    public async Task What_the_tree_reads_while_the_preview_is_pending_does_not_count_as_a_stale_read()
    {
        // the project ships the map's materials.bin, which the GameData changes: the tree reads it (it lists the materials in it) every time it is built
        var (vm, _) = await DescribedTile("quiet-tree");

        async Task Rebuild(bool aReaderTakesTheBinMeanwhile)
        {
            var gated = Gated(new InstalledGame(vm.Project.GameDirectory!, _temp.Combine("cache-quiet-tree-" + _n++, "i.idx")));
            SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
            Call(vm, "BuildMounts");
            SetField(vm, "_mapMaterialNames", new List<string> { "built from the unchanged bins" });
            Assert.True(Preview(vm)!.IsPending);

            Call(vm, "BuildProjectTree");                                            // reads the bin; it is built again when the preview is ready
            if (aReaderTakesTheBinMeanwhile) Assert.NotNull(Mounts(vm).Read(Hash(MapMaterials)));
            gated.Gate.Set();
            await Applied(vm);
        }

        await Rebuild(aReaderTakesTheBinMeanwhile: false);
        Assert.NotNull(Field<object?>(vm, "_mapMaterialNames"));                     // the tree's own read is not one that has to be undone

        await Rebuild(aReaderTakesTheBinMeanwhile: true);
        Assert.Null(Field<object?>(vm, "_mapMaterialNames"));                        // another reader's is
    }

    // ================================================================================================ which schema the editor hands the preview

    [Fact]
    public async Task The_view_model_hands_the_preview_the_managers_cache_it_is_given_and_the_preview_names_it()
    {
        string game = Game("g-manager");
        string manager = _temp.Combine("manager", "meta-schema.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manager)!);
        File.WriteAllText(manager, "{\"formatVersion\":1,\"latest\":40,\"classes\":{}}");
        var vm = NewVm(LayeredProject(game));
        SetField(vm, "GameDataIndexCachePath", _temp.Combine("cache-manager", "i.idx"));
        SetField(vm, "GameDataManagerSchemaPath", manager);

        Call(vm, "BuildMounts");
        await Applied(vm);

        Assert.Equal("LTK Manager's cache (newest build it describes: 40)", Preview(vm)!.Setup!.SchemaSource);
        Assert.Equal("Class schema: LTK Manager's cache (newest build it describes: 40).", Preview(vm)!.Summary!.Notes[0]);
    }

    // ================================================================================================ names that come from a package

    [Fact]
    public async Task A_file_made_of_an_assets_name_is_written_below_the_folder_it_was_asked_for_whatever_the_name_says()
    {
        string folder = _temp.Combine("export");
        Directory.CreateDirectory(folder);
        WadAssetEntry Named(string path) => new() { PathHash = Hash(path), Path = path, IsResolved = true, Type = AssetType.Unknown };
        var safeFileIn = typeof(MainWindowViewModel).GetMethod("SafeFileIn", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Fn(string path) => (string)safeFileIn.Invoke(null, new object[] { folder, Named(path) })!;

        Assert.Equal(Path.Combine(folder, "plain.bin"), Fn("data/t/plain.bin"));
        Assert.Equal(Path.Combine(folder, "y.cmd"), Fn("data/x/..\\..\\y.cmd"));                      // the part before the last separator, backslash included, goes
        Assert.Equal(Path.Combine(folder, "dll.dll"), Fn("C:\\Windows\\dll.dll"));
        foreach (string odd in new[] { "data/x/..", "data/x/CON.txt", "data/x/a?.bin", "data/x/trailing.", "data/x/<>.bin" })
        {
            string full = Fn(odd);
            Assert.Equal(folder, Path.GetDirectoryName(full));
            Assert.StartsWith("0x", Path.GetFileName(full));                                     // the hash form: a name that is no file name is not used as one
        }
        Assert.EndsWith(".txt", Fn("data/x/CON.txt"));
        // whatever else a spelling can say, the file is in the folder
        foreach (string any in new[] { "data/x/a:b.bin", "C:y.cmd", "\\\\server\\share\\z.bin", "../../up.bin", "a/../../../up2.bin", ".", "" })
            Assert.Equal(folder, Path.GetDirectoryName(Fn(any)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_copy_into_the_project_folder_of_an_asset_whose_name_leaves_it_is_not_placed_there()
    {
        var project = LayeredProject(Game("g-place"));
        var vm = await Open(project);
        SetField(vm, "_currentMapEntry", EntryOf(vm, P));                             // WriteBakedAsset places a baked file beside the map that is open

        // a Riot entry whose name came from a package's tables, and which climbs out of the folder it would be placed in
        var hostile = new WadAssetEntry { PathHash = Hash("data/t/e.bin"), Path = "data/t/../../../escaped.bin", IsResolved = true, Type = AssetType.Bin, SourceKind = AssetSourceKind.RiotReference };
        var args = new object?[] { hostile, new byte[] { 1 }, "" };
        bool placed = (bool)Call(vm, "TryPlaceInProjectFolder", args)!;

        Assert.False(placed);                                                         // the caller stores a hash-named override instead
        Assert.False(File.Exists(Path.Combine(project.RootPath!, "escaped.bin")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(project.RootPath!)!, "escaped.bin")));

        var thrown = Assert.Throws<InvalidDataException>(() => Call(vm, "WriteBakedAsset", "data/t/../../../escaped2.bin", new byte[] { 1 }, ".dat"));
        Assert.Contains("not a path a project file can have", thrown.Message);
        Assert.False(File.Exists(Path.Combine(project.RootPath!, "escaped2.bin")));
    }

    // ================================================================================================ the guard around the editor's own saves

    private const string Q = "data/t/q.bin";

    /// <summary>A project of base GameData on A and M whose mod ships M (a target), P and Q (nothing names them): two bins an editor can save while the GameData is previewed.</summary>
    private ReyProject WithQ(string gameDirectory) => Import("withq", Layers(Doc(AddTag(A, "Test/Obj/A", "base"), AddTag(M, "Test/Obj/M", "base"))), gameDirectory,
        ("WAD/Map11.wad.client", ModWad("q", (M, Bin(Obj("Test/Obj/M", new[] { "mod" }))), (P, Bin(Obj("Test/Obj/P", new[] { "p" }))), (Q, QBytes("q")))),
        Table(A, M, P, Q));

    private static byte[] QBytes(string tag) => Bin(Obj("Test/Obj/Q", new[] { tag }));

    private static (int Written, IReadOnlyList<string> Missing, string? Refusal) Staged(object? staged)
    {
        var type = staged!.GetType();
        return ((int)type.GetProperty("Written")!.GetValue(staged)!, (IReadOnlyList<string>)type.GetProperty("Missing")!.GetValue(staged)!, (string?)type.GetProperty("Refusal")!.GetValue(staged));
    }

    private static Task<bool> Save(MainWindowViewModel vm, WadAssetEntry entry, byte[] bytes) => (Task<bool>)Call(vm, "SaveMapBinBytesAsync", entry, bytes)!;

    [Fact]
    public async Task A_read_in_the_moment_between_the_preview_being_ready_and_the_editor_applying_it_is_refreshed_too()
    {
        using var ui = new UiLikeContext();                                       // the editor's one thread: the settle of the preview comes back to it
        string game = Game("g-window");
        var vm = await Open(LayeredProject(game));
        string keyBefore = MainWindowViewModelKey(Preview(vm)!)!;
        var gated = Gated(new InstalledGame(game, _temp.Combine("cache-window", "i.idx")));
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        await ui.Run(() => Call(vm, "BuildMounts"));                              // a file changed: a new preview of the same declarations over the same game
        SetField(vm, "_mapMaterialNames", new List<string> { "built from the unchanged bins" });
        var next = Preview(vm)!;
        Assert.True(next.IsPending);

        using var busy = new ManualResetEventSlim(false);
        var blocked = ui.Run(() => busy.Wait(TimeSpan.FromSeconds(60)));          // the UI thread is busy: whatever the worker finishes, the editor cannot apply it yet
        try
        {
            gated.Gate.Set();
            await next.Settled.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(GameDataPreviewState.Ready, next.State);
            Assert.False(next.IsPending);                                        // ready, and not applied: the index of the mounts does not hold its chunks yet
            Assert.False(Mounts(vm).IsOverlaid(Hash(M)));
            Assert.NotNull(Mounts(vm).Read(Hash(M)));                            // so that read answered what the mounts hold, and the editor must hear of it
        }
        finally { busy.Set(); }
        await blocked.WaitAsync(TimeSpan.FromSeconds(60));
        await Applied(vm);

        Assert.Equal(keyBefore, MainWindowViewModelKey(next));                   // the same result as before...
        Assert.Null(Field<object?>(vm, "_mapMaterialNames"));                    // ...and still what was built from the unchanged bytes is built again
        await Quiesce(vm);
        Assert.Empty(ui.Faults);
    }

    [Fact]
    public async Task A_reader_that_still_holds_the_mounts_a_rebuild_replaced_is_not_counted_among_the_reads_the_new_preview_must_undo()
    {
        string game = Game("g-retired");
        var gated = Gated(new InstalledGame(game, _temp.Combine("c-retired", "i.idx")));
        var vm = NewVm(LayeredProject(game));
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        Call(vm, "BuildMounts");
        var service = Mounts(vm);
        Assert.True(Preview(vm)!.IsPending);
        Call(vm, "TakeGameDataReads");

        Assert.NotNull(service.Read(Hash(M)));                                   // while the service is the editor's, a read of a target is not final: it is noted
        Assert.Single((HashSet<ulong>)Call(vm, "TakeGameDataReads")!);

        Call(vm, "CancelGameData");                                              // the first thing a rebuild does: this service and its preview are finished with
        Assert.NotNull(service.Read(Hash(M)));                                   // a thumbnail in flight still reads through it...
        Assert.Empty((HashSet<ulong>)Call(vm, "TakeGameDataReads")!);            // ...and the new preview has nothing to undo for that
    }

    [Fact]
    public async Task The_editors_own_save_is_not_refused_in_the_rebuild_that_follows_the_one_before_and_a_target_stays_refused()
    {
        string game = Game("g-own-saves");
        var project = WithQ(game);
        var vm = await Open(project);                                            // Ready once: it names A and M
        var log = CaptureLog(vm);
        SetField(vm, "GameDataWriteWait", TimeSpan.FromSeconds(3));              // a save that did not get its answer at once would wait this long, and then fail the test

        Assert.True(await Save(vm, EntryOf(vm, P), Bin(Obj("Test/Obj/P", new[] { "first save" }))));      // the editor saves a bin...
        var gated = Gated(new InstalledGame(game, _temp.Combine("cache-own-saves", "i.idx")));
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        Call(vm, "BuildMounts");                                                 // ...and the file watcher rebuilds the mounts a moment later: a new preview, held at the game's table
        var next = Preview(vm)!;
        Assert.True(next.IsPending);
        Assert.True(next.TargetsKnown);                                          // the same documents over the same game: what the last preview named is known at once

        Assert.True(await Save(vm, EntryOf(vm, Q), QBytes("second save")));      // the next save does not wait for the game to be read (the gate is shut: a save that waited would be refused as pending when its time ran out), and is not refused for it
        string qFile = Path.Combine(project.RootPath!, "Map11", "data", "t", "q.bin");
        Assert.Equal(QBytes("second save"), File.ReadAllBytes(qFile));

        // a target stays refused, with its own words and not the pending ones
        string mFile = Path.Combine(project.RootPath!, "Map11", "data", "t", "m.bin");
        byte[] mBefore = File.ReadAllBytes(mFile);
        Assert.False(await Save(vm, EntryOf(vm, M), Bin(Obj("Test/Obj/M", new[] { "held from before" }))));
        Assert.Equal(mBefore, File.ReadAllBytes(mFile));
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains($"is {MainWindowViewModel.GameDataEditRefusal}", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(log, "GameData"), l => l.Message.Contains(MainWindowViewModel.GameDataPendingRefusal, StringComparison.Ordinal));

        gated.Gate.Set();
        await Applied(vm);
        Assert.Equal(GameDataPreviewState.Ready, next.State);
    }

    [Fact]
    public async Task A_save_made_while_the_preview_is_still_working_waits_for_it_and_is_then_decided_by_what_it_says()
    {
        string game = Game("g-wait-save");
        // an ENTRIES module names Test/Obj/M: only the plan, made from the game's index, says that m.bin is a target
        var project = Import("wait-save", Layers(Doc(Entries("\"Test/Obj/M\":{\"+tags\":[\"e\"]}"))), game,
            ("WAD/Map11.wad.client", ModWad("ws", (M, Bin(Obj("Test/Obj/M", new[] { "mod" }))), (Q, QBytes("q")), (P, Bin(Obj("Test/Obj/P", new[] { "p" }))))), Table(M, Q, P));
        var gated = Gated(new InstalledGame(game, _temp.Combine("c-ws", "i.idx")));
        var vm = NewVm(project);
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        var log = CaptureLog(vm);
        Call(vm, "BuildMounts");                                                 // the first preview of this session: nothing to inherit
        Assert.False(Preview(vm)!.TargetsKnown);
        string qFile = Path.Combine(project.RootPath!, "Map11", "data", "t", "q.bin"), mFile = Path.Combine(project.RootPath!, "Map11", "data", "t", "m.bin");
        byte[] mBefore = File.ReadAllBytes(mFile);

        var saveQ = Save(vm, EntryOf(vm, Q), QBytes("saved"));
        var saveM = Save(vm, EntryOf(vm, M), Bin(Obj("Test/Obj/M", new[] { "held" })));
        // the raw bin editor's own save asks the guard first, and waits there as well
        byte[] pBytes = Bin(Obj("Test/Obj/P", new[] { "p" }));
        vm.BinEditor.Load(ReyEngine.Formats.Meta.BinEditorDocument.Parse(pBytes, _ => null), EntryOf(vm, P), pBytes);
        vm.BinEditor.IsDirty = true;
        var editorSave = (Task)Call(vm, "SaveBinToOverride")!;
        await Task.Delay(200);
        Assert.False(saveQ.IsCompleted);                                         // they wait, and nobody is blocked
        Assert.False(saveM.IsCompleted);
        Assert.False(editorSave.IsCompleted);

        gated.Gate.Set();
        Assert.True(await saveQ.WaitAsync(TimeSpan.FromSeconds(60)));            // no module names q.bin
        Assert.False(await saveM.WaitAsync(TimeSpan.FromSeconds(60)));           // and one does name m.bin
        await editorSave.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains(Lines(log, "Bin"), l => l.Message.StartsWith("Saved edited plain.bin", StringComparison.Ordinal));
        await Applied(vm);
        Assert.Equal(QBytes("saved"), File.ReadAllBytes(qFile));
        Assert.Equal(mBefore, File.ReadAllBytes(mFile));
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains($"is {MainWindowViewModel.GameDataEditRefusal}", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(log, "GameData"), l => l.Message.Contains(MainWindowViewModel.GameDataPendingRefusal, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_save_that_waited_as_long_as_it_may_is_refused_as_pending_and_one_the_project_was_changed_under_writes_nothing()
    {
        string game = Game("g-wait-limit");
        var project = Import("wait-limit", Layers(Doc(Entries("\"Test/Obj/M\":{\"+tags\":[\"e\"]}"))), game,
            ("WAD/Map11.wad.client", ModWad("wl", (M, Bin(Obj("Test/Obj/M", new[] { "mod" }))), (Q, QBytes("q")))), Table(M, Q));
        var gated = Gated(new InstalledGame(game, _temp.Combine("c-wl", "i.idx")));
        var vm = NewVm(project);
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        var log = CaptureLog(vm);
        Call(vm, "BuildMounts");
        string qFile = Path.Combine(project.RootPath!, "Map11", "data", "t", "q.bin");
        byte[] qBefore = File.ReadAllBytes(qFile);

        SetField(vm, "GameDataWriteWait", TimeSpan.FromMilliseconds(200));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await Save(vm, EntryOf(vm, Q), QBytes("too late")));        // the game is not read within the limit: refused as pending, as before
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"the save waited {watch.Elapsed}");
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains(MainWindowViewModel.GameDataPendingRefusal, StringComparison.Ordinal));
        Assert.Equal(qBefore, File.ReadAllBytes(qFile));

        SetField(vm, "GameDataWriteWait", TimeSpan.FromSeconds(60));
        var waiting = Save(vm, EntryOf(vm, Q), QBytes("for another project"));
        await Task.Delay(150);
        Assert.False(waiting.IsCompleted);
        vm.Project = new ReyProject { Name = "another", RootPath = _temp.Combine("another-project-save") };   // the user opens another project meanwhile
        gated.Gate.Set();
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Contains(Lines(log, "GameData"), l => l.Message == MainWindowViewModel.GameDataProjectChangedRefusal);
        Assert.Equal(qBefore, File.ReadAllBytes(qFile));                          // nothing was written for the project it was asked for
    }

    [Fact]
    public async Task The_flows_that_stage_before_they_write_a_bin_wait_for_the_preview_instead_of_failing_and_are_then_refused_by_what_it_says()
    {
        // an ENTRIES module names the map's materials.bin: only the plan, made from the game's index, says that it is a target
        string directory = new SyntheticGame()
            .Add("DATA.wad.client", "data/readme.txt", new byte[] { 1 })
            .Add("Global.wad.client", "data/global.txt", new byte[] { 2 })
            .Add("Map11.wad.client", MapMaterials, Bin(Obj("Test/Obj/M", new[] { "game" })))
            .Write(_temp.Combine("g-flows-wait"));
        var project = Import("flows-wait", Layers(Doc(Entries("\"Test/Obj/M\":{\"+tags\":[\"e\"]}"))), directory,
            ("WAD/Map11.wad.client", ModWad("fw", (MapGeo, new byte[] { 1, 2, 3 }), (MapMaterials, Bin(Obj("Test/Obj/M", new[] { "mod" }))))), Table(MapGeo, MapMaterials));
        var gated = Gated(new InstalledGame(directory, _temp.Combine("c-fw", "i.idx")));
        var vm = NewVm(project);
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        var log = CaptureLog(vm);
        Call(vm, "BuildMounts");
        vm.ProjectMode = true;
        SetField(vm, "_currentMap", EmptyMap());
        SetField(vm, "_currentMapEntry", EntryOf(vm, MapGeo));
        SetField(vm, "_currentMapBytes", new byte[] { 1, 2, 3 });
        Assert.False(Preview(vm)!.TargetsKnown);

        var plan = new AddMeshPlan(Array.Empty<ImportedSceneMesh>(),
            new[] { new AddMeshMaterialPlan("m", CreateNew: true, ExistingMaterial: null, NewName: "NewMat", ShaderPath: "Shaders/Test", TextureBytes: null, TextureFileNameHint: null) },
            new Dictionary<string, string>(), 0);
        var addMesh = (Task)Call(vm, "ExecuteAddMeshPlanAsync", plan)!;
        var port = (Task)Call(vm, "PortLegacyMap")!;
        await Task.Delay(200);
        Assert.False(addMesh.IsCompleted);                                       // both wait for the preview, without a thread
        Assert.False(port.IsCompleted);

        gated.Gate.Set();
        await addMesh.WaitAsync(TimeSpan.FromSeconds(60));
        await port.WaitAsync(TimeSpan.FromSeconds(60));
        await Applied(vm);

        // and are refused by what the preview said - the bin is a target - not for the moment it took to say it
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains($"is {MainWindowViewModel.GameDataEditRefusal}", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(log, "GameData"), l => l.Message.Contains(MainWindowViewModel.GameDataPendingRefusal, StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(log, "AddMesh"), l => l.Message.StartsWith("Materials bin:", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(log, "Legacy Port"), l => l.Message.StartsWith("Converting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_the_preview_that_worked_fails_the_bins_it_named_stay_refused_with_the_reason_even_for_an_editor_that_holds_what_it_made()
    {
        string game = Game("g-failed-after");
        var project = WithQ(game);
        var vm = await Open(project);                                            // Ready: it names A and M
        var log = CaptureLog(vm);
        var named = EntryOf(vm, M);
        var plain = EntryOf(vm, P);
        string mFile = Path.Combine(project.RootPath!, "Map11", "data", "t", "m.bin");
        byte[] mBefore = File.ReadAllBytes(mFile);
        // the raw bin editor was opened while the preview worked: it holds what the preview made of the bin, with an edit pending
        byte[] held = Mounts(vm).Read(Hash(M))!;
        Assert.Equal(new[] { "mod", "base" }, TagsOf(held, "Test/Obj/M"));
        vm.BinEditor.Load(ReyEngine.Formats.Meta.BinEditorDocument.Parse(held, _ => null), named, held);
        vm.BinEditor.IsDirty = true;

        // the next preview fails: the stored document is not JSON any more
        string document = Path.Combine(LtkProjectStore.DirectoryOf(project.RootPath!, "base"), LtkProjectStore.DeclarationsFileName);
        string good = File.ReadAllText(document);
        File.WriteAllText(document, "{ this is not json");
        Call(vm, "BuildMounts");
        await Applied(vm);
        Assert.Equal(GameDataPreviewState.Failed, Preview(vm)!.State);
        Assert.Null(Mounts(vm).Overlay);                                         // no overlay: the service has nothing to ask
        Assert.False(Mounts(vm).IsOverlayTarget(Hash(M)));

        // yet the bin the last preview named is refused by every way an editor writes - and the refusal says the preview failed, and why
        Assert.False((bool)Call(vm, "GuardEditable", named)!);
        Assert.Contains(Lines(log, "GameData"), l => l.Level == LogLevel.Warning && l.Message.Contains($"is {MainWindowViewModel.GameDataEditRefusal}, and its preview failed (", StringComparison.Ordinal)
                                                  && l.Message.Contains("is not valid JSON", StringComparison.Ordinal));
        await (Task)Call(vm, "SaveBinToOverride")!;                              // the editor's own save: its rebase would fall back to the overlaid bytes it holds
        Assert.False(await Save(vm, named, held));
        Assert.Throws<InvalidOperationException>(() => Call(vm, "TryWriteToProjectFile", named, held, ""));
        Assert.True((bool)Call(vm, "SkipsGameDataBin", named)!);                 // and a patch update leaves it out, as it does while the preview works
        Assert.Equal(mBefore, File.ReadAllBytes(mFile));                         // nothing was written
        Assert.True(await Save(vm, plain, Bin(Obj("Test/Obj/P", new[] { "ok" }))));      // a bin nothing named saves as ever

        // a preview that could not even be started says that instead
        Call(vm, "NoteGameDataStartFailed", new IOException("boom"));
        Assert.False((bool)Call(vm, "GuardEditable", named)!);
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains("and its preview could not be started (boom)", StringComparison.Ordinal));

        // the document is repaired: the next preview is Ready, and the refusal is the usual one again
        File.WriteAllText(document, good);
        Call(vm, "BuildMounts");
        await Applied(vm);
        Assert.Equal(GameDataPreviewState.Ready, Preview(vm)!.State);
        Assert.True(Mounts(vm).IsOverlayTarget(Hash(M)));
        int said = Lines(log, "GameData").Count;
        Assert.False((bool)Call(vm, "GuardEditable", named)!);
        Assert.DoesNotContain(Lines(log, "GameData").Skip(said), l => l.Message.Contains("its preview", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_that_no_longer_stores_GameData_is_not_held_to_what_a_preview_once_named()
    {
        string game = Game("g-forgotten");
        var project = WithQ(game);
        var vm = await Open(project);
        Assert.False((bool)Call(vm, "GuardEditable", EntryOf(vm, M))!);          // named
        foreach (var layer in project.Layers) layer.DeclarationsKey = null;      // the declarations are gone from the project

        Call(vm, "BuildMounts");
        await Applied(vm);

        Assert.Null(Preview(vm));
        Assert.True((bool)Call(vm, "GuardEditable", EntryOf(vm, M))!);           // nothing names it now: it is the mod's own file
    }

    [Fact]
    public async Task The_auto_save_does_not_say_it_saved_what_the_GameData_refused()
    {
        var vm = await Open(WithQ(Game("g-autosave")));
        var log = CaptureLog(vm);
        vm.Settings.AutoSaveEdits = true;                                        // in this instance only: nothing here calls Save
        void Hold(WadAssetEntry entry)
        {
            typeof(MaterialEditorViewModel).GetProperty("BinEntry")!.SetValue(vm.MaterialEditor, entry);   // as Load leaves it, without a document: the save asks the guard first
            vm.MaterialEditor.IsDirty = true;
        }
        async Task<List<LogEntry>> Tick(int already)
        {
            Call(vm, "OnAutoSaveTick", null, EventArgs.Empty);                   // async void: it ends by logging
            for (int i = 0; i < 3000 && Lines(log, "Auto-save").Count <= already; i++) await Task.Delay(20);
            return Lines(log, "Auto-save").Skip(already).ToList();
        }

        Hold(EntryOf(vm, M));                                                    // a material edit pending on a bin the GameData names
        var refused = await Tick(0);
        Assert.DoesNotContain(refused, l => l.Message.StartsWith("Saved pending edits", StringComparison.Ordinal));
        Assert.Contains(refused, l => l.Level == LogLevel.Warning && l.Message.Contains("were not saved", StringComparison.Ordinal));
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains($"is {MainWindowViewModel.GameDataEditRefusal}", StringComparison.Ordinal));

        Hold(EntryOf(vm, P));                                                    // and one on a bin nothing names: the tick says what it always said
        var accepted = await Tick(refused.Count);
        Assert.Contains(accepted, l => l.Message.StartsWith("Saved pending edits", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refusal_while_staging_is_told_as_it_is_and_not_as_a_file_missing_from_the_installed_patch()
    {
        var project = LayeredProject(Game("g-stage-reason"));
        var vm = await Open(project);
        var destination = EntryOf(vm, P);
        string folder = _temp.Combine("legacy-art");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "spark.dds"), new byte[] { 1, 2, 3 });

        // the legacy particle art is staged to a path the GameData changes
        var refused = Staged(Call(vm, "StageLegacyParticleAssets", new[] { new ReyEngine.Formats.Particles.TroyAssetMapping("spark.dds", M, false) }, new[] { folder }, destination));
        Assert.Equal(0, refused.Written);
        Assert.NotNull(refused.Refusal);
        Assert.Contains($"is {MainWindowViewModel.GameDataEditRefusal}", refused.Refusal);
        Assert.DoesNotContain("installed patch", refused.Refusal);
        Assert.Equal(new[] { M }, refused.Missing.ToArray());                    // a caller that does not look at the reason still fails

        // a set that touches nothing the GameData names carries no refusal
        var sources = new List<(string Path, byte[] Bytes)> { ("data/t/staged-reason.bin", Bin(Obj("Test/Obj/S", new[] { "s" }))) };
        var clear = Staged(Call(vm, "WriteStagedAssets", sources, destination, new List<string>(), false));
        Assert.Equal(1, clear.Written);
        Assert.Null(clear.Refusal);
    }

    [Fact]
    public async Task A_recolored_texture_whose_name_leaves_the_folder_is_not_written_and_a_plain_one_is_written_in_its_wad_folder()
    {
        var project = LayeredProject(Game("g-recolor"));
        var vm = await Open(project);
        string root = project.RootPath!;

        foreach (string hostile in new[] { "data/t/../../../escaped-recolor.tex", "data/t/x.tex:hidden", "C:/escaped-recolor.tex" })
        {
            var ex = Assert.Throws<InvalidDataException>(() => Call(vm, "WriteRecoloredAsset", hostile, new byte[] { 1 }, ".tex"));
            Assert.Contains("not a path a project file can have", ex.Message);
        }
        Assert.False(File.Exists(Path.Combine(root, "escaped-recolor.tex")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escaped-recolor.tex")));
        Assert.False(File.Exists("C:/escaped-recolor.tex"));
        Assert.Empty(Directory.EnumerateFiles(root, "x.tex*", SearchOption.AllDirectories));

        string written = (string)Call(vm, "WriteRecoloredAsset", "assets/t/recolor-ok.tex", new byte[] { 1, 2 }, ".tex")!;
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(written));
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(written));

        // the undo of a recolour deletes only what lies below the folder: a record that names a path outside it - a project somebody shared may carry one - deletes nothing
        string victim = Path.Combine(Path.GetDirectoryName(root)!, "victim-revert.tex");
        File.WriteAllBytes(victim, new byte[] { 7 });
        const string outside = "../../victim-revert.tex";
        vm.RevertRecolors(new[] { new ReyEngine.App.Services.RecolorTarget(Hash(outside), outside) });
        Assert.True(File.Exists(victim));
        // and the one it wrote is removed
        Assert.Equal(1, vm.RevertRecolors(new[] { new ReyEngine.App.Services.RecolorTarget(Hash("assets/t/recolor-ok.tex"), "assets/t/recolor-ok.tex") }));
        Assert.False(File.Exists(written));
    }

    [Fact]
    public async Task A_baked_file_whose_name_fails_the_proof_is_not_taught_to_the_dictionary_and_one_that_climbs_back_in_is_not_deleted_as_stale()
    {
        var project = LayeredProject(Game("g-baked"));
        var vm = await Open(project);
        string root = project.RootPath!;
        var database = Field<WadPathResolver>(vm, "_resolver").Database;
        // the open map is in Map11: a baked file is placed beside it
        SetField(vm, "_currentMapEntry", new WadAssetEntry { PathHash = Hash("data/maps/mapgeometry/Map11/x.mapgeo"), Path = "data/maps/mapgeometry/Map11/x.mapgeo", IsResolved = true, Type = AssetType.MapGeometry });

        // a name that fails the proof is written nowhere and not taught
        const string climbing = "data/t/../../../escaped-baked.tex";
        Assert.Throws<InvalidDataException>(() => Call(vm, "WriteBakedAsset", climbing, new byte[] { 1 }, ".tex"));
        Assert.False(database.TryGetPath(Hash(climbing), out _));
        Assert.False(File.Exists(Path.Combine(root, "escaped-baked.tex")));

        // a plain one is written below the map's folder, and taught
        const string plainPath = "assets/maps/lightmaps/baked-ok.tex";
        string written = (string)Call(vm, "WriteBakedAsset", plainPath, new byte[] { 2 }, ".tex")!;
        Assert.Equal(Path.Combine(root, "Map11", "assets", "maps", "lightmaps", "baked-ok.tex"), written);
        Assert.True(database.TryGetPath(Hash(plainPath), out var taught));
        Assert.Equal(plainPath, taught);

        // a spelling that climbs out of Overrides and back into the map's folder IS the file just written: it is not a stale copy to delete
        const string detour = "../Map11/baked-detour.tex";
        string detoured = (string)Call(vm, "WriteBakedAsset", detour, new byte[] { 3 }, ".tex")!;
        Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(detoured));
        Assert.True(File.Exists(Path.Combine(root, "Map11", "baked-detour.tex")));
        Assert.False(database.TryGetPath(Hash(detour), out _));                  // contained, but not a plain spelling: written, not taught

        // and a stale copy under Overrides is still removed once the right one is durable
        string stale = Path.Combine(root, "Overrides", "assets", "maps", "lightmaps", "baked-ok.tex");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllBytes(stale, new byte[] { 9 });
        Call(vm, "WriteBakedAsset", plainPath, new byte[] { 2 }, ".tex");
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(written));
    }
}

using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.Meta;
using static ReyEngine.Formats.Tests.LayeredFantomeSupport;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M823: editing a bin the imported GameData changes, through the view model's own save paths - the editors' choke points (<c>SaveMapBinBytesAsync</c>, <c>TryWriteToProjectFile</c>) and their guard
/// (<c>GuardBinEdit</c>). An edit is kept as ONE module on top of the package's modules, never as a file: the project's files and its override store are as they were, the preview serves the edit, an
/// edit no declaration can express is refused with the reason, and Export .fantome and Send to LTK Manager write the package's modules as they came and then the edit. A synthetic game and a synthetic
/// package (the real ones are checked outside the suite, against LTK's own crates - see the M823 commit).
/// </summary>
public sealed partial class LtkEditFlowTests : IAsyncLifetime, IDisposable
{
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly TempFolder _temp = new();
    private readonly List<MainWindowViewModel> _vms = new();
    private int _n;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var vm in _vms)
        {
            Preview(vm)?.Cancel();
            await Prop<Task>(vm, "GameDataApplied").WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    public void Dispose() => _temp.Dispose();

    // a game bin the GameData names (it lives only in the game), a bin the mod ships itself and the GameData names too, and a bin the mod ships and nothing names
    private const string A = "data/t/a.bin", M = "data/t/m.bin", P = "data/t/plain.bin";

    private MainWindowViewModel NewVm(ReyProject project)
    {
        var vm = new MainWindowViewModel { Project = project };
        SetField(vm, "GameDataSchemaPath", TempSchema());
        SetField(vm, "GameDataManagerSchemaPath", _temp.Combine("no-manager-cache", "meta-schema.json"));
        _vms.Add(vm);
        return vm;
    }

    private string TempSchema()
    {
        string path = _temp.Combine("schema.json");
        if (!File.Exists(path)) File.WriteAllText(path, "{\"formatVersion\":1,\"latest\":100,\"classes\":{}}");
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

    private static ulong ChunkOf(string path) => HashAlgorithms.WadPath(path);

    private string Game(string name, byte[]? a = null)
    {
        var game = new SyntheticGame()
            .Add("DATA.wad.client", "data/readme.txt", new byte[] { 1 })
            .Add("Global.wad.client", "data/global.txt", new byte[] { 2 })
            .Add("Champions/A.wad.client", A, a ?? Bin(Obj("Test/Obj/A", new[] { "g1" })))
            .Add("Map11.wad.client", M, Bin(Obj("Test/Obj/M", new[] { "game" })));
        return game.Write(_temp.Combine(name));
    }

    private static string AddTag(string path, string obj, string tag) => Target(path, $"{{\"{obj}\":{{\"+tags\":[\"{tag}\"]}}}}");

    private static string Layers(string baseDoc, string fixDoc) =>
        "{\"Name\":\"t\",\"Author\":\"a\",\"Version\":\"1.0.0\",\"Description\":\"d\",\"Hashtables\":" + TableManifest + ",\"Layers\":{\"base\":{\"Name\":\"base\",\"Priority\":0,\"GameData\":" + baseDoc + "}"
        + ",\"fix\":{\"Name\":\"fix\",\"Priority\":1,\"GameData\":" + fixDoc + "}}}";

    private byte[] ModWad(string name, params (string Rel, byte[] Bytes)[] files) => PackWad(_temp.Combine("pack-" + name + _n++), _temp.Combine("mod-" + name + _n++ + ".wad.client"), files);

    /// <summary>A package of base GameData on A and M and a fix layer on A; the mod ships its own copy of M, and a plain bin nothing names. <paramref name="extraModule"/> is one more module of the base layer, for the bin <paramref name="extraName"/>.</summary>
    private ReyProject Package(string gameDirectory, string name = "pkg", string? extraModule = null, string? extraName = null)
    {
        var baseModules = new List<string> { AddTag(A, "Test/Obj/A", "base"), AddTag(M, "Test/Obj/M", "base") };
        if (extraModule is not null) baseModules.Add(extraModule);
        string info = Layers(Doc(baseModules.ToArray()), Doc(AddTag(A, "Test/Obj/A", "fix")));
        string package = WriteFantome(_temp.Combine(name + _n++ + ".fantome"),
            Entry("META/info.json", info),
            ("WAD/Map11.wad.client", ModWad("base", (M, Bin(Obj("Test/Obj/M", new[] { "mod" }))), (P, Bin(Obj("Test/Obj/P", new[] { "plain" }))))),
            extraName is null ? Table(A, M, P) : Table(A, M, P, extraName));
        string projects = _temp.Combine("projects");
        Directory.CreateDirectory(projects);
        var result = FantomeImporter.Import(package, projects, null, new HashDatabase());
        var project = ReyProjectService.OpenFolder(result.RootPath);
        project.GameDirectory = gameDirectory;
        project.OutputDirectory = _temp.Combine("build" + _n++);
        return project;
    }

    private async Task<MainWindowViewModel> Open(ReyProject project)
    {
        var vm = NewVm(project);
        SetField(vm, "GameDataIndexCachePath", _temp.Combine("cache" + _n++, "index.idx"));
        Call(vm, "BuildMounts");
        await Applied(vm);
        Assert.Equal(GameDataPreviewState.Ready, Preview(vm)!.State);
        return vm;
    }

    private static WadAssetEntry EntryOf(MainWindowViewModel vm, string path)
    {
        Assert.True(Mounts(vm).TryGet(ChunkOf(path), out var asset), "not mounted: " + path);
        return asset.ToEntry();
    }

    private static byte[] Read(MainWindowViewModel vm, string path) => (byte[])Call(vm, "ReadAsset", ChunkOf(path))!;
    private static Task<bool> Save(MainWindowViewModel vm, WadAssetEntry entry, byte[] bytes) => (Task<bool>)Call(vm, "SaveMapBinBytesAsync", entry, bytes)!;
    private static string StoreFile(ReyProject project, string layerKey) => LtkEditStore.PathOf(project.RootPath!, layerKey);

    private static void AssertSameData(byte[] expected, byte[] actual, string because = "")
    {
        string? difference = BinTreeEquivalence.FirstDifference(SafeBinTree.Parse(expected), SafeBinTree.Parse(actual));
        Assert.True(difference is null, $"{because}: {difference}");
    }

    /// <summary>Every file of the project folder (not the store, not the project file) with a digest of what it holds.</summary>
    private static Dictionary<string, string> Files(ReyProject project)
    {
        string root = project.RootPath!;
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (Rel: Path.GetRelativePath(root, f).Replace('\\', '/'), Path: f))
            .Where(f => !f.Rel.StartsWith(".reyengine/ltk/", StringComparison.Ordinal) && f.Rel != ".reyengine/project.json" && f.Rel != ".reyengine/thumbnail.png")
            .ToDictionary(f => f.Rel, f => Convert.ToHexString(System.IO.Hashing.XxHash64.Hash(File.ReadAllBytes(f.Path))));
    }

    private static byte[] AEdited(int count = 7, params string[] tags) =>
        Bin(Obj("Test/Obj/A", tags.Length > 0 ? tags : new[] { "g1", "base", "fix", "mine" }, count: count));

    // ================================================================================================ the save

    [Fact]
    public async Task An_edit_of_a_bin_only_the_game_has_is_kept_as_a_module_in_the_layer_that_applies_last_and_never_as_a_file()
    {
        var project = Package(Game("g-save"));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        var entry = EntryOf(vm, A);
        byte[] b = Read(vm, A);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(b, "Test/Obj/A"));
        var filesBefore = Files(project);
        byte[] edited = AEdited();

        Assert.True(await (ValueTask<bool>)Call(vm, "GuardBinEditAsync", entry)!);
        bool saved = await Save(vm, entry, edited);

        Assert.True(saved, vm.Status);
        // kept in the layer that applies last (the fix layer applies after base), beside the package's document
        Assert.True(File.Exists(StoreFile(project, "fix")));
        Assert.False(File.Exists(StoreFile(project, "base")));
        var module = Assert.Single(GameDataDocumentText.Read(File.ReadAllText(StoreFile(project, "fix"))).Modules);
        Assert.Equal(A, module.Target);
        // never a file: the project's folders and its override store are exactly as they were
        Assert.Equal(filesBefore, Files(project));
        Assert.False(Directory.Exists(Path.Combine(project.RootPath!, ".reyengine", "overrides")) && Directory.EnumerateFiles(Path.Combine(project.RootPath!, ".reyengine", "overrides")).Any());
        // the editor reads the edit back, and it is what the editor saved
        AssertSameData(edited, Read(vm, A), "the bin read back");
        var served = EntryOf(vm, A);
        Assert.Equal(AssetSourceKind.LtkGameData, served.SourceKind);
        Assert.False(served.ReadOnly);
        Assert.Contains(Lines(log, "GameData"), l => l.Level == LogLevel.Success && l.Message.StartsWith("Saved a.bin as a declaration on top of the mod's GameData, in layer 'fix'", StringComparison.Ordinal));
        Assert.True(project.IsDirty);
        // nothing else about the package changed
        Assert.Equal(new[] { "mod", "base" }, TagsOf(Read(vm, M), "Test/Obj/M"));
    }

    [Fact]
    public async Task An_edit_of_a_bin_the_mod_ships_itself_leaves_the_mods_file_as_it_was_because_the_declarations_apply_to_that_file()
    {
        var project = Package(Game("g-copy"));
        var vm = await Open(project);
        var entry = EntryOf(vm, M);
        string file = Path.Combine(project.RootPath!, "Map11", "data", "t", "m.bin");
        byte[] fileBefore = File.ReadAllBytes(file);
        Assert.Equal(new[] { "mod", "base" }, TagsOf(Read(vm, M), "Test/Obj/M"));          // the package's module over the mod's copy
        byte[] edited = Bin(Obj("Test/Obj/M", new[] { "mod", "base", "mine" }));

        Assert.True(await Save(vm, entry, edited), vm.Status);

        Assert.Equal(fileBefore, File.ReadAllBytes(file));                                  // the file is the base of the declarations: writing the overlaid bin into it would apply them twice
        Assert.True(File.Exists(StoreFile(project, "base")));                              // M is named by the base layer alone
        AssertSameData(edited, Read(vm, M), "the bin read back");
        Assert.Equal(fileBefore, (byte[])Mounts(vm).ReadRaw(ChunkOf(M))!);                  // and the raw read still gives the project's own file
    }

    [Fact]
    public async Task The_synchronous_writer_every_editor_flow_ends_in_keeps_the_edit_the_same_way_and_says_where()
    {
        var project = Package(Game("g-sync"));
        var vm = await Open(project);
        var entry = EntryOf(vm, A);
        byte[] edited = AEdited(count: 9);
        var args = new object?[] { entry, edited, "" };

        bool written = (bool)Call(vm, "TryWriteToProjectFile", args)!;

        Assert.True(written);
        Assert.EndsWith("(a declaration on top of the GameData)", (string)args[2]!);
        Assert.StartsWith(StoreFile(project, "fix"), (string)args[2]!);
        AssertSameData(edited, Read(vm, A), "the bin read back at once, by whoever asks next");
        Assert.Single(Mounts(vm).Assets, a => a.PathHash == ChunkOf(A));
    }

    [Fact]
    public async Task Saving_again_keeps_one_module_computed_against_the_package_and_an_edit_equal_to_it_takes_the_module_away()
    {
        var project = Package(Game("g-again"));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        var entry = EntryOf(vm, A);
        byte[] b = Read(vm, A);

        Assert.True(await Save(vm, entry, AEdited(count: 2)));
        Assert.True(await Save(vm, entry, AEdited(count: 3)));                              // the editor saves again: the module is replaced, not added to

        var module = Assert.Single(GameDataDocumentText.Read(File.ReadAllText(StoreFile(project, "fix"))).Modules);
        using (var parsed = JsonDocument.Parse(module.Text))
        {
            var body = parsed.RootElement.GetProperty("edits")[0].EnumerateObject().Single().Value;      // one object, spelled by the hash the name table does not know
            Assert.Equal(3, body.EnumerateObject().Single(p => p.Value.ValueKind == JsonValueKind.Number).Value.GetInt32());   // the count: the second save's, not the first's
        }
        AssertSameData(AEdited(count: 3), Read(vm, A));

        // saving the bin as the package makes it is "no edit": the module goes, and the file with it
        Assert.True(await Save(vm, EntryOf(vm, A), b));

        Assert.False(File.Exists(StoreFile(project, "fix")));
        AssertSameData(b, Read(vm, A), "back to the package's bin");
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains("the edit on top of it was taken away", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_same_edit_saved_again_writes_nothing_so_the_auto_save_does_not_wake_the_file_watcher_and_a_different_one_does()
    {
        var project = Package(Game("g-same"));
        var vm = await Open(project);
        Assert.True(await Save(vm, EntryOf(vm, A), AEdited(count: 4)));
        string file = StoreFile(project, "fix");
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, old);                                                  // a rewrite would move it
        string text = File.ReadAllText(file);
        var preview = Preview(vm);

        Assert.True(await Save(vm, EntryOf(vm, A), AEdited(count: 4)));                       // the auto-save's next tick: the editor still holds the same bin

        Assert.Equal(old, File.GetLastWriteTimeUtc(file));
        Assert.Equal(text, File.ReadAllText(file));
        Assert.Same(preview, Preview(vm));
        AssertSameData(AEdited(count: 4), Read(vm, A), "still the edit");
        Assert.Contains("nothing changed since the last save", vm.Status);

        // the low writer says where the edit lives either way
        var args = new object?[] { EntryOf(vm, A), AEdited(count: 4), "" };
        Assert.True((bool)Call(vm, "TryWriteToProjectFile", args)!);
        Assert.EndsWith("(a declaration on top of the GameData)", (string)args[2]!);
        Assert.Equal(old, File.GetLastWriteTimeUtc(file));

        Assert.True(await Save(vm, EntryOf(vm, A), AEdited(count: 5)));                       // another edit is a new module text: written
        Assert.NotEqual(old, File.GetLastWriteTimeUtc(file));
        AssertSameData(AEdited(count: 5), Read(vm, A), "the new edit");
    }

    [Fact]
    public async Task A_save_that_changes_nothing_of_a_bin_without_an_edit_keeps_nothing_and_says_so()
    {
        var project = Package(Game("g-nothing"));
        var vm = await Open(project);
        var log = CaptureLog(vm);

        Assert.True(await Save(vm, EntryOf(vm, A), Read(vm, A)));

        Assert.False(File.Exists(StoreFile(project, "fix")));
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains("there is no edit to keep", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_edit_is_still_there_when_the_project_is_opened_again_and_after_the_mounts_are_rebuilt()
    {
        var project = Package(Game("g-persist"));
        var vm = await Open(project);
        byte[] edited = AEdited(count: 4);
        Assert.True(await Save(vm, EntryOf(vm, A), edited));
        string keyBefore = Field<string?>(vm, "_gameDataShownKey")!;

        // the file watcher rebuilds the mounts a moment after a save: the new preview serves the same, and the editor does not reload what it is showing
        var reloads = new List<ulong>();
        SetField(vm, "GameDataMapReload", new Func<WadAssetEntry, Task>(e => { lock (reloads) reloads.Add(e.PathHash); return Task.CompletedTask; }));
        SetField(vm, "_currentMapEntry", EntryOf(vm, P));
        Call(vm, "BuildMounts");
        await Applied(vm);
        AssertSameData(edited, Read(vm, A), "after the rebuild");
        Assert.Equal(keyBefore, Field<string?>(vm, "_gameDataShownKey"));
        lock (reloads) Assert.Empty(reloads);

        // and a new session over the same project
        var again = ReyProjectService.OpenFolder(project.RootPath!);
        again.GameDirectory = project.GameDirectory;
        var reopened = await Open(again);
        AssertSameData(edited, Read(reopened, A), "in a new session");
        Assert.Contains(ChunkOf(A), Field<HashSet<ulong>>(reopened, "_gameDataEdited"));
    }

    // ================================================================================================ what is refused

    [Fact]
    public async Task An_edit_no_declaration_can_express_is_refused_with_the_reason_and_nothing_is_written_or_changed()
    {
        var project = Package(Game("g-refuse"));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        var entry = EntryOf(vm, A);
        byte[] b = Read(vm, A);
        var gone = Obj("Test/Obj/A", new[] { "g1", "base", "fix" });
        gone.Properties.Remove(H("count"));                                                  // a property removed: no declaration removes a property
        var filesBefore = Files(project);
        int refusals = Field<int>(vm, "_gameDataRefusals");

        bool saved = await Save(vm, entry, Bin(gone));

        Assert.False(saved);
        Assert.False(File.Exists(StoreFile(project, "fix")));
        Assert.Equal(filesBefore, Files(project));
        Assert.Equal(b, Read(vm, A));                                                       // the preview still serves the package's bin
        Assert.Equal(refusals + 1, Field<int>(vm, "_gameDataRefusals"));                    // the auto-save will not say it saved this
        var said = Assert.Single(Lines(log, "GameData"), l => l.Level == LogLevel.Warning);
        Assert.Contains("'a.bin' cannot be saved on top of the mod's GameData", said.Message);
        Assert.Contains("no declaration removes a property", said.Message);
        Assert.Contains("Nothing was written; the edit stays pending", said.Message);
        Assert.Equal(said.Message, vm.Status);

        // the same bytes again (the auto-save's next tick): refused the same way, counted, and not said again
        Assert.False(await Save(vm, entry, Bin(gone)));
        Assert.Equal(refusals + 2, Field<int>(vm, "_gameDataRefusals"));
        Assert.Single(Lines(log, "GameData"), l => l.Level == LogLevel.Warning);

        // an edit that can be kept is kept
        Assert.True(await Save(vm, entry, AEdited()));
    }

    [Fact]
    public async Task An_edit_LTK_would_skip_for_want_of_a_class_schema_is_refused_and_the_message_names_the_schema()
    {
        var project = Package(Game("g-schema"));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        var added = Obj("Test/Obj/A", new[] { "g1", "base", "fix" });
        added.Properties.Set(H("extra"), new PropInt(PropKind.I32, 9));                       // a property the bin does not hold: LTK types it with the class schema, which here knows no class

        Assert.False(await Save(vm, EntryOf(vm, A), Bin(added)));

        var said = Assert.Single(Lines(log, "GameData"), l => l.Level == LogLevel.Warning);
        Assert.Contains("LTK would skip", said.Message);
        Assert.Contains("The class schema is the cause", said.Message);
        Assert.False(File.Exists(StoreFile(project, "fix")));
    }

    [Fact]
    public async Task A_bin_that_is_not_a_bin_is_refused_by_the_low_writer_with_an_exception_and_by_the_save_with_false()
    {
        var project = Package(Game("g-garbage"));
        var vm = await Open(project);
        var entry = EntryOf(vm, A);

        Assert.False(await Save(vm, entry, new byte[] { 1, 2, 3 }));
        var ex = Assert.Throws<InvalidOperationException>(() => Call(vm, "TryWriteToProjectFile", entry, new byte[] { 1, 2, 3 }, ""));

        Assert.Contains("cannot be saved on top of the mod's GameData", ex.Message);
        Assert.False(File.Exists(StoreFile(project, "fix")));
    }

    // ================================================================================================ the guards

    [Fact]
    public async Task The_guard_of_a_whole_file_write_stays_strict_the_editors_guard_lets_a_bin_through_and_a_copy_into_the_project_is_refused_with_the_way_to_edit_it()
    {
        var project = Package(Game("g-guards"));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        var entry = EntryOf(vm, A);

        Assert.False((bool)Call(vm, "GuardEditable", entry)!);
        Assert.True((bool)Call(vm, "GuardBinEdit", entry)!);
        Assert.True((bool)Call(vm, "GuardBinEdit", EntryOf(vm, M))!);
        Assert.True((bool)Call(vm, "GuardBinEdit", EntryOf(vm, P))!);                       // a bin nothing names: as ever

        var node = new AssetNodeViewModel(new AssetTreeNode { Name = "a.bin", FullPath = A, Entry = entry });
        Assert.False(await (Task<bool>)Call(vm, "CopyOneAssetToProject", node, null)!);
        var said = Lines(log, "GameData").Last(l => l.Level == LogLevel.Warning);
        Assert.Contains("open it in an editor and save", said.Message);                  // the way to change a bin a copy was asked of
        Assert.Contains("declaration on top of the GameData", said.Message);
        Assert.False(File.Exists(Path.Combine(project.RootPath!, "Map11", "data", "t", "a.bin")));                // nothing was copied in
        Assert.Throws<InvalidOperationException>(() => Call(vm, "TryPlaceInProjectFolder", entry, new byte[] { 1 }, ""));
    }

    [Fact]
    public async Task A_flow_that_stages_files_first_asks_whether_the_edit_can_be_kept_before_it_stages_anything()
    {
        var project = Package(Game("g-preflight"));
        var vm = await Open(project);
        var entry = EntryOf(vm, A);
        var gone = Obj("Test/Obj/A", new[] { "g1", "base", "fix" });
        gone.Properties.Remove(H("count"));

        // the preflight proves the declaration without keeping it
        Assert.Null(await (Task<string?>)Call(vm, "GameDataEditPreflightAsync", entry, AEdited())!);
        var refusal = await (Task<string?>)Call(vm, "GameDataEditPreflightAsync", entry, Bin(gone))!;
        Assert.NotNull(refusal);
        Assert.Contains("no declaration removes a property", refusal);
        Assert.False(File.Exists(StoreFile(project, "fix")));
        // a bin nothing names has nothing to prove
        Assert.Null(await (Task<string?>)Call(vm, "GameDataEditPreflightAsync", EntryOf(vm, P), Bin(Obj("Test/Obj/P", new[] { "p" })))!);

        // the Workshop imports ask first: a bin whose edit can be kept passes, with the preview working
        await ((ValueTask)Call(vm, "ThrowIfNotGameDataEditableAsync", entry)!).AsTask();
        await ((ValueTask)Call(vm, "ThrowIfNotGameDataEditableAsync", EntryOf(vm, P))!).AsTask();
    }

    [Fact]
    public async Task While_the_preview_is_still_working_a_save_of_a_target_waits_and_when_it_does_not_come_is_refused_as_pending_and_nothing_is_kept()
    {
        var project = Package(Game("g-pending"));
        var vm = await Open(project);                                                        // Ready once: A is named, so the next preview knows it at once
        var log = CaptureLog(vm);
        var entry = EntryOf(vm, A);                                                          // a bin only the game has is not mounted while the next preview works: the entry is taken before
        var gated = new GatedGame(new InstalledGame(project.GameDirectory!, _temp.Combine("cache-gate", "i.idx")));
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        SetField(vm, "GameDataWriteWait", TimeSpan.FromMilliseconds(200));
        Call(vm, "BuildMounts");
        Assert.True(Preview(vm)!.IsPending);

        Assert.False(await Save(vm, entry, AEdited()));

        Assert.False(File.Exists(StoreFile(project, "fix")));
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains(MainWindowViewModel.GameDataPendingRefusal, StringComparison.Ordinal));
        gated.Gate.Set();
        await Applied(vm);
        Assert.True(await Save(vm, entry, AEdited()));                                       // and once the preview is ready it is kept
        gated.Dispose();
    }

    private sealed class GatedGame : IGameDataGame, IDisposable
    {
        private readonly IGameDataGame _inner;
        public readonly ManualResetEventSlim Gate = new(false);
        public GatedGame(IGameDataGame inner) { _inner = inner; }
        public void Dispose() => Gate.Set();
        public GameChunkTable GetTable(CancellationToken cancellationToken) { Gate.Wait(cancellationToken); return _inner.GetTable(cancellationToken); }
        public GameObjectIndex GetObjects(CancellationToken cancellationToken) => _inner.GetObjects(cancellationToken);
        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken) => _inner.ReadChunk(archive, chunk, maxBytes, cancellationToken);
        public string? IndexNotSaved => _inner.IndexNotSaved;
    }

    [Fact]
    public async Task No_save_path_of_the_editor_writes_a_targets_bytes_to_a_project_file_or_the_override_store_whatever_the_edit_and_whether_or_not_it_is_kept()
    {
        var project = Package(Game("g-never"));
        var vm = await Open(project);
        var entryA = EntryOf(vm, A);
        var entryM = EntryOf(vm, M);
        string mFile = Path.Combine(project.RootPath!, "Map11", "data", "t", "m.bin");
        var filesBefore = Files(project);
        byte[] mBefore = File.ReadAllBytes(mFile);
        Assert.True(Preview(vm)!.TryReadImportedOnly(ChunkOf(A), out var packageA, out _));            // the bin the package alone makes: B
        byte[] editedM = Bin(Obj("Test/Obj/M", new[] { "mod", "base", "mine" }));
        var gone = Obj("Test/Obj/A", new[] { "g1", "base", "fix" });
        gone.Properties.Remove(H("count"));

        // every route a bin takes into the project: the choke point of the editors, the low writer under it (edits that are kept and ones that are refused), a repoint, taking an edit away
        Assert.True(await Save(vm, entryA, AEdited(count: 2)));
        Assert.True(await Save(vm, entryM, editedM));
        Assert.True((bool)Call(vm, "TryWriteToProjectFile", entryA, AEdited(count: 3), "")!);
        Assert.False(await Save(vm, entryA, Bin(gone)));                                                                     // refused
        Assert.Throws<InvalidOperationException>(() => Call(vm, "TryWriteToProjectFile", entryA, Bin(gone), ""));            // refused by the low writer, by throwing
        Assert.True(await (Task<bool>)Call(vm, "RepointAssetRefAsync", EntryOf(vm, A), "g1", "g1-fixed")!);
        byte[] shownA = Read(vm, A);
        SetField(vm, "_contextOverride", new AssetNodeViewModel(new AssetTreeNode { Name = "m.bin", FullPath = M, Entry = EntryOf(vm, M) }));
        vm.ProjectMode = true;
        vm.RevertGameDataEditsCommand.Execute(null);                                                                          // the edit of M goes
        // the routes that write whole files stay shut
        Assert.False(await (Task<bool>)Call(vm, "CopyOneAssetToProject", new AssetNodeViewModel(new AssetTreeNode { Name = "a.bin", FullPath = A, Entry = EntryOf(vm, A) }), null)!);
        Assert.Throws<InvalidOperationException>(() => Call(vm, "TryPlaceInProjectFolder", EntryOf(vm, A), new byte[] { 1 }, ""));
        Assert.Throws<InvalidOperationException>(() => Call(vm, "WriteBakedAsset", M, new byte[] { 1 }, ".bin"));

        // not a file of the project changed, nothing was copied in, and the override store never heard of either bin
        Assert.Equal(filesBefore, Files(project));
        Assert.Equal(mBefore, File.ReadAllBytes(mFile));
        Assert.False(File.Exists(Path.Combine(project.RootPath!, "Map11", "data", "t", "a.bin")));
        string overridesFolder = Path.Combine(project.RootPath!, ".reyengine", "overrides");
        Assert.False(Directory.Exists(overridesFolder) && Directory.EnumerateFiles(overridesFolder).Any());
        var overrides = Field<AssetOverrideStore>(vm, "_overrides");
        Assert.False(overrides.Has(ChunkOf(A)) || overrides.Has(ChunkOf(M)));
        Assert.DoesNotContain(project.Overrides, o => o.PathHash == ChunkOf(A) || o.PathHash == ChunkOf(M));
        // what the project keeps of the edits is the declarations: one module for A, in the layer that applies last, and none for M, whose edit was taken away
        var module = Assert.Single(GameDataDocumentText.Read(File.ReadAllText(StoreFile(project, "fix"))).Modules);
        Assert.Equal(A, module.Target);
        Assert.False(File.Exists(StoreFile(project, "base")));
        // neither the bin the package makes of A nor the bin the editor shows (the edit in it) is the content of any file of the project
        Assert.Equal(new[] { "g1-fixed", "base", "fix", "mine" }, TagsOf(shownA, "Test/Obj/A"));
        foreach (string file in Directory.EnumerateFiles(project.RootPath!, "*", SearchOption.AllDirectories))
        {
            byte[] content = File.ReadAllBytes(file);
            Assert.False(content.AsSpan().SequenceEqual(shownA) || content.AsSpan().SequenceEqual(packageA!), file);
        }
    }

    [Fact]
    public async Task A_repoint_of_a_reference_inside_a_target_is_an_edit_like_any_other_and_is_kept_as_a_declaration()
    {
        var project = Package(Game("g-repoint"));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        var filesBefore = Files(project);

        bool done = await (Task<bool>)Call(vm, "RepointAssetRefAsync", EntryOf(vm, A), "g1", "g1-fixed")!;

        Assert.True(done);
        Assert.Equal(new[] { "g1-fixed", "base", "fix" }, TagsOf(Read(vm, A), "Test/Obj/A"));
        Assert.True(File.Exists(StoreFile(project, "fix")));
        Assert.Equal(filesBefore, Files(project));
        Assert.Contains(Lines(log, "Validate"), l => l.Level == LogLevel.Success && l.Message.Contains("repointed 1 reference(s)", StringComparison.Ordinal));

        // a reference that is not there is not an edit
        Assert.False(await (Task<bool>)Call(vm, "RepointAssetRefAsync", EntryOf(vm, A), "nothing-like-it", "x")!);
        Assert.Equal(new[] { "g1-fixed", "base", "fix" }, TagsOf(Read(vm, A), "Test/Obj/A"));
    }

    [Fact]
    public async Task A_Workshop_import_into_a_target_asks_before_it_stages_and_while_the_preview_works_it_is_refused_as_pending_and_goes_on_when_it_is_ready()
    {
        var project = Package(Game("g-import"));
        var vm = await Open(project);                                                        // Ready once: A is named, so the next preview knows it at once
        var entry = EntryOf(vm, A);
        var gated = new GatedGame(new InstalledGame(project.GameDirectory!, _temp.Combine("cache-gate-import", "i.idx")));
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        SetField(vm, "GameDataWriteWait", TimeSpan.FromMilliseconds(200));
        Call(vm, "BuildMounts");
        Assert.True(Preview(vm)!.IsPending);
        var filesBefore = Files(project);

        // pending: nothing can be proven, so the import is refused before it stages a thing
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ((ValueTask)Call(vm, "ThrowIfNotGameDataEditableAsync", entry)!).AsTask());
        Assert.Contains(MainWindowViewModel.GameDataPendingRefusal.TrimEnd('.'), ex.Message);
        var preflight = await (Task<string?>)Call(vm, "GameDataEditPreflightAsync", entry, AEdited())!;
        Assert.NotNull(preflight);
        Assert.Contains(MainWindowViewModel.GameDataPendingRefusal.TrimEnd('.'), preflight);
        Assert.Equal(filesBefore, Files(project));

        gated.Gate.Set();
        await Applied(vm);
        await ((ValueTask)Call(vm, "ThrowIfNotGameDataEditableAsync", entry)!).AsTask();                  // ready: passes
        Assert.Null(await (Task<string?>)Call(vm, "GameDataEditPreflightAsync", entry, AEdited())!);
        gated.Dispose();
    }

    [Fact]
    public async Task The_export_does_not_wait_for_the_preview_it_makes_the_planners_baselines_itself_and_writes_the_same_document()
    {
        var project = Package(Game("g-export-nopreview"));
        project.ShipBinEditsAsDeclarations = true;
        project.ReferenceWads.Add(Path.Combine(project.GameDirectory!, "DATA", "FINAL", "Map11.wad.client"));
        var vm = await Open(project);
        Assert.True(await Save(vm, EntryOf(vm, M), Bin(Obj("Test/Obj/M", new[] { "mod", "base", "mine" }))));
        Assert.True(await Save(vm, EntryOf(vm, A), AEdited()));
        string withPreview = Export(vm, _temp.Combine("out-ready", "ready.fantome"));

        // the mounts are being rebuilt (the file watcher does it after a save): the preview that will serve the export's project is not ready yet, and there is none to take the baselines from
        SetField(vm, "_gameData", null);
        string without = Export(vm, _temp.Combine("out-independent", "independent.fantome"));

        using var ready = ZipFile.OpenRead(withPreview);
        using var independent = ZipFile.OpenRead(without);
        foreach (string layer in new[] { "base", "fix" })
            Assert.Equal(GameData(ready, layer), GameData(independent, layer));
        // and the document is the one that matters: the edited M is declared, ahead of nothing the package already does
        var written = GameDataDocumentText.Read(GameData(independent, "base")!);
        Assert.Contains(written.Modules, m => m.Target == M);
    }

    // ================================================================================================ revert

    [Fact]
    public async Task Reverting_takes_one_bins_edit_away_and_leaves_the_others_and_the_command_is_enabled_only_where_there_is_one()
    {
        var project = Package(Game("g-revert"));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        byte[] bA = Read(vm, A), bM = Read(vm, M);
        Assert.True(await Save(vm, EntryOf(vm, A), AEdited()));
        byte[] editedM = Bin(Obj("Test/Obj/M", new[] { "mod", "base", "mine" }));
        Assert.True(await Save(vm, EntryOf(vm, M), editedM));
        vm.ProjectMode = true;
        void Select(string path) => SetField(vm, "_contextOverride", new AssetNodeViewModel(new AssetTreeNode { Name = path, FullPath = path, Entry = EntryOf(vm, path) }));

        Select(P);
        Assert.False(vm.RevertGameDataEditsCommand.CanExecute(null));                       // nothing to take away from a bin with no edit
        Select(A);
        Assert.True(vm.RevertGameDataEditsCommand.CanExecute(null));

        vm.RevertGameDataEditsCommand.Execute(null);

        AssertSameData(bA, Read(vm, A), "A is the package's again");
        AssertSameData(editedM, Read(vm, M), "M keeps its edit");
        Assert.False(File.Exists(StoreFile(project, "fix")));
        Assert.True(File.Exists(StoreFile(project, "base")));
        Assert.False(vm.RevertGameDataEditsCommand.CanExecute(null));
        Assert.Contains(Lines(log, "GameData"), l => l.Level == LogLevel.Success && l.Message.StartsWith("Took away the edit on top of the mod's GameData of a.bin", StringComparison.Ordinal));

        Select(M);
        vm.RevertGameDataEditsCommand.Execute(null);
        AssertSameData(bM, Read(vm, M), "M is the package's again");
        Assert.False(File.Exists(StoreFile(project, "base")));
        Assert.False(LtkEditStore.Any(project));
    }

    // ================================================================================================ the export

    [Fact]
    public async Task Export_writes_the_imported_modules_as_they_came_then_the_edits_and_the_mods_file_unchanged()
    {
        var project = Package(Game("g-export"));
        var vm = await Open(project);
        byte[] editedM = Bin(Obj("Test/Obj/M", new[] { "mod", "base", "mine" }));
        Assert.True(await Save(vm, EntryOf(vm, A), AEdited()));
        Assert.True(await Save(vm, EntryOf(vm, M), editedM));
        var original = LtkProjectStore.ReadLayers(project).ToDictionary(l => l.Layer, l => l.DocumentText);
        string mFile = Path.Combine(project.RootPath!, "Map11", "data", "t", "m.bin");
        byte[] mFileBefore = File.ReadAllBytes(mFile);

        string fantome = Export(vm, _temp.Combine("out", "edited.fantome"));

        using var zip = ZipFile.OpenRead(fantome);
        foreach (var (layer, expectedEdits) in new[] { ("base", new[] { M }), ("fix", new[] { A }) })
        {
            var written = GameDataDocumentText.Read(GameData(zip, layer)!);
            var imported = GameDataDocumentText.Read(original[layer]!);
            Assert.Equal(imported.Modules.Count + 1, written.Modules.Count);
            Assert.Equal(imported.Modules.Select(m => m.Text), written.Modules.Take(imported.Modules.Count).Select(m => m.Text));      // verbatim: the package's modules, byte for byte
            Assert.Equal(expectedEdits, written.Modules.Skip(imported.Modules.Count).Select(m => m.Target).ToArray());                // then the edit
            var origins = written.Modules.Select(m => JsonDocument.Parse(m.Text).RootElement.GetProperty("origin").GetProperty("module").GetInt32()).ToArray();
            Assert.Equal(Enumerable.Range(0, written.Modules.Count), origins);                                                       // numbered 0, 1, 2... in the order they are written
        }
        // the mod's own copy of M ships as the package shipped it: the declarations apply to it, edit included, at install
        var chunks = Chunks(zip, "WAD/Map11.wad.client", _temp.Combine("unpack"));
        Assert.Equal(mFileBefore, chunks[ChunkOf(M)]);
        Assert.DoesNotContain(ChunkOf(A), chunks.Keys);                                                                             // and A, which only the game has, is in no WAD
        // the project's store was only read
        Assert.True(File.Exists(StoreFile(project, "fix")));
    }

    [Fact]
    public async Task What_the_export_installs_over_the_game_is_what_the_editor_showed_edit_included()
    {
        var project = Package(Game("g-installs"));
        var vm = await Open(project);
        Assert.True(await Save(vm, EntryOf(vm, A), AEdited(count: 5)));
        Assert.True(await Save(vm, EntryOf(vm, M), Bin(Obj("Test/Obj/M", new[] { "mod", "base", "mine" }))));
        string fantome = Export(vm, _temp.Combine("out2", "edited.fantome"));

        // LTK's own order: the layers the package declares, each with the document the export wrote; over the game's bins; the mod's own copy of M is the base of M
        using var zip = ZipFile.OpenRead(fantome);
        var layers = new[] { ("base", 0), ("fix", 1) }.Select(l => new GameDataLayerInput(l.Item1, l.Item2, GameData(zip, l.Item1))).ToList();
        var game = new InstalledGame(project.GameDirectory!, _temp.Combine("cache-installs", "i.idx"));
        var overlay = new GameDataOverlay(layers, game, new SingleCopy(M, File.ReadAllBytes(Path.Combine(project.RootPath!, "Map11", "data", "t", "m.bin"))));

        foreach (string path in new[] { A, M })
        {
            var installed = overlay.Apply(ChunkOf(path))!;
            Assert.DoesNotContain(installed.Diagnostics, d => d.Kind != GameDataOverlayDiagnosticKind.SchemaFallback);
            AssertSameData(Read(vm, path), installed.Bytes!, path);
        }
    }

    private sealed class SingleCopy : IGameDataModFiles
    {
        private readonly ulong _chunk;
        private readonly byte[] _bytes;
        public SingleCopy(string path, byte[] bytes) { _chunk = ChunkOf(path); _bytes = bytes; }
        public byte[]? ReadLayerFile(string layer, ulong chunk, long maxBytes) => chunk == _chunk ? (byte[])_bytes.Clone() : null;
        public byte[]? ReadRawFile(ulong chunk, long maxBytes) => null;
    }

    [Fact]
    public async Task With_declarations_on_a_bin_the_mod_ships_and_the_GameData_names_is_declared_with_the_package_applied_to_both_sides_and_installs_as_the_editor_shows_it()
    {
        var project = Package(Game("g-decl"));
        project.ShipBinEditsAsDeclarations = true;
        project.ReferenceWads.Add(Path.Combine(project.GameDirectory!, "DATA", "FINAL", "Map11.wad.client"));
        var vm = await Open(project);
        byte[] shown = Read(vm, M);                                                          // the package's module over the mod's copy: [mod, base]
        Assert.Equal(new[] { "mod", "base" }, TagsOf(shown, "Test/Obj/M"));

        string fantome = Export(vm, _temp.Combine("out3", "decl.fantome"));

        using var zip = ZipFile.OpenRead(fantome);
        var written = GameDataDocumentText.Read(GameData(zip, "base")!);
        var planned = written.Modules.Last();
        Assert.Equal(M, planned.Target);                                                     // the mod's copy became a module...
        using (var module = JsonDocument.Parse(planned.Text))                              // ...stating the copy with the package's tag in it, not the copy alone ("mod") which would drop the package's effect
        {
            var tags = module.RootElement.GetProperty("edits")[0].EnumerateObject().Single().Value.EnumerateObject().Single().Value;
            Assert.Equal(new[] { "mod", "base" }, tags.EnumerateArray().Select(t => t.GetString()).ToArray());
        }
        Assert.DoesNotContain(ChunkOf(M), Chunks(zip, "WAD/Map11.wad.client", _temp.Combine("unpack3")).Keys);   // and the file no longer ships

        var layers = new[] { ("base", 0), ("fix", 1) }.Select(l => new GameDataLayerInput(l.Item1, l.Item2, GameData(zip, l.Item1))).ToList();
        var installed = new GameDataOverlay(layers, new InstalledGame(project.GameDirectory!, _temp.Combine("cache-decl", "i.idx"))).Apply(ChunkOf(M))!;   // over the GAME's bin: the file did not ship
        AssertSameData(shown, installed.Bytes!, "what is installed against what the editor showed");
    }

    [Fact]
    public async Task With_declarations_on_an_edit_to_such_a_bin_runs_before_the_projects_own_module_and_the_two_give_the_edited_bin()
    {
        var project = Package(Game("g-decl-edit"));
        project.ShipBinEditsAsDeclarations = true;
        project.ReferenceWads.Add(Path.Combine(project.GameDirectory!, "DATA", "FINAL", "Map11.wad.client"));
        var vm = await Open(project);
        byte[] editedM = Bin(Obj("Test/Obj/M", new[] { "mod", "base", "mine" }));
        Assert.True(await Save(vm, EntryOf(vm, M), editedM));

        string fantome = Export(vm, _temp.Combine("out4", "decl-edit.fantome"));

        using var zip = ZipFile.OpenRead(fantome);
        var layers = new[] { ("base", 0), ("fix", 1) }.Select(l => new GameDataLayerInput(l.Item1, l.Item2, GameData(zip, l.Item1))).ToList();
        var installed = new GameDataOverlay(layers, new InstalledGame(project.GameDirectory!, _temp.Combine("cache-decl-edit", "i.idx"))).Apply(ChunkOf(M))!;
        AssertSameData(editedM, installed.Bytes!, "the edited bin, installed over the game's own");
        // the copy differs from the game's only by what the package and the edit already do, so nothing of it is stated a second time
        var written = GameDataDocumentText.Read(GameData(zip, "base")!);
        Assert.Equal(GameDataDocumentText.Read(LtkProjectStore.ReadLayers(project).Single(l => l.Layer == "base").DocumentText).Modules.Count + 1, written.Modules.Count);
    }

    [Fact]
    public async Task Send_writes_the_same_modules_into_each_layers_manifest_the_package_first_then_the_edit()
    {
        var project = Package(Game("g-send"));
        var vm = await Open(project);
        Assert.True(await Save(vm, EntryOf(vm, A), AEdited()));
        string workshop = _temp.Combine("workshop");
        Directory.CreateDirectory(workshop);

        await (Task)Call(vm, "SendProjectToWorkshop", workshop)!;

        string manifest = File.ReadAllText(Directory.EnumerateFiles(workshop, "game_data.yaml", SearchOption.AllDirectories).Single(f => f.Contains($"{Path.DirectorySeparatorChar}fix{Path.DirectorySeparatorChar}")));
        var lines = manifest.Split('\n');
        int at = Array.IndexOf(lines, "modules:");
        Assert.Contains("\"+tags\":[\"fix\"]", lines[at + 1]);                               // the package's module of the layer
        Assert.StartsWith("  - {\"target\":\"data/t/a.bin\",\"edits\":[{\"0x", lines[at + 2]);   // then the edit (its object spelled by hash: the name tables do not know the test's object)
        Assert.Matches("\"(Count|0x[0-9a-f]{8})\":7[,}]", lines[at + 2]);                    // the count, spelled by name where the hash tables know it and by hash where they do not
        Assert.DoesNotContain("origin", manifest);
        string baseManifest = File.ReadAllText(Directory.EnumerateFiles(workshop, "game_data.yaml", SearchOption.AllDirectories).Single(f => f.Contains($"{Path.DirectorySeparatorChar}base{Path.DirectorySeparatorChar}")));
        Assert.DoesNotMatch("\"(Count|0x[0-9a-f]{8})\":7[,}]", baseManifest);                       // the layer the edit is not in is as the package made it
    }

    // ================================================================================================ patches

    [Fact]
    public async Task The_edit_is_a_literal_module_so_it_keeps_applying_after_Riots_patch_and_the_patch_shows_through_everywhere_else()
    {
        var project = Package(Game("g-before-patch"));
        var vm = await Open(project);
        byte[] b = Read(vm, A);
        // an edit of the count only
        Assert.True(await Save(vm, EntryOf(vm, A), AEdited(count: 7, "g1", "base", "fix")));

        // Riot patches the bin: the game's tags change and an object is added
        var patched = Bin(Obj("Test/Obj/A", new[] { "g1", "patched" }), Obj("Test/Obj/Patch", new[] { "new" }));
        project.GameDirectory = Game("g-after-patch", patched);
        var next = await Open(project);

        var served = Read(next, A);
        Assert.Equal(new[] { "g1", "patched", "base", "fix" }, TagsOf(served, "Test/Obj/A"));        // the package's modules over the patched bin
        var obj = PropCodec.ReadObject(served, H("Test/Obj/A"))!;
        Assert.Equal(7, (int)((PropInt)obj.Properties.ValueAt(obj.Properties.IndexOf(H("count")))).Signed);   // and the edit still applies: it states the count and nothing else
        Assert.NotNull(PropCodec.ReadObject(served, H("Test/Obj/Patch")));                          // Riot's new object is there
        Assert.NotEqual(b, served);
    }

    // ================================================================================================ a project with no GameData

    [Fact]
    public async Task A_project_that_stores_no_GameData_saves_a_bin_as_ever()
    {
        // a project that never imported a package: the same project with the declarations gone from it
        var project = Package(Game("g-legacy"));
        foreach (var layer in project.Layers) layer.DeclarationsKey = null;
        string root = project.RootPath!;
        var vm = NewVm(project);
        Call(vm, "BuildMounts");
        await Applied(vm);
        Assert.Null(Preview(vm));
        var entry = EntryOf(vm, P);

        Assert.True((bool)Call(vm, "GuardBinEdit", entry)!);
        Assert.True(await Save(vm, entry, Bin(Obj("Test/Obj/P", new[] { "edited" }))));

        Assert.Equal(new[] { "edited" }, TagsOf(File.ReadAllBytes(Path.Combine(root, "Map11", "data", "t", "plain.bin")), "Test/Obj/P"));   // written in place, as it has always been
        Assert.False(LtkEditStore.Any(project));
        Assert.Empty(Field<HashSet<ulong>>(vm, "_gameDataEdited"));
    }
}

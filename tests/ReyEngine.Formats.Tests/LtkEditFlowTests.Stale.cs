using System.Diagnostics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Particles;
using static ReyEngine.Formats.Tests.LayeredFantomeSupport;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M823, the review's round: an edit on top of the GameData made from a document that does not hold what the package put into the bin (an editor opened while the preview was still working, or before another editor saved)
/// is merged onto the bin as it is served and never written over it; a save is made against ONE preview, and planned again when the mounts were rebuilt under it; several bins saved together are kept or none is; the store is
/// read with a limit; and the smaller things (what the watcher wakes, what a non-target waits for, what a revert shows).
/// </summary>
public sealed partial class LtkEditFlowTests
{
    private const string X = "data/t/fx.bin", Sys = "Test/Fx/Sys";

    private static uint F(string name) => HashAlgorithms.Fnv1a(name);

    /// <summary>One VFX system, <c>Test/Fx/Sys</c>, named <paramref name="particleName"/>, with two emitters: <c>off</c> (disabled) and <c>smoke</c>.</summary>
    private static byte[] FxBin(string particleName = "sys")
    {
        BinTreeStruct Emitter(string name, bool disabled) => new(0, F("VfxEmitterDefinitionData"),
            disabled
                ? new BinTreeProperty[] { new BinTreeString(F("emitterName"), name), new BinTreeBool(F("disabled"), true) }
                : new BinTreeProperty[] { new BinTreeString(F("emitterName"), name) });
        var system = new BinTreeObject(F(Sys), F("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(F("particleName"), particleName),
            new BinTreeF32(F("fxScale"), 1f),
            new BinTreeContainer(F("complexEmitterDefinitionData"), BinPropertyType.Struct, new BinTreeProperty[] { Emitter("off", disabled: true), Emitter("smoke", disabled: false) }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static string ParticleNameOf(byte[] bytes) =>
        ((BinTreeString)SafeBinTree.Parse(bytes).Objects[F(Sys)].Properties[F("particleName")]).Value;

    private static float FxScaleOf(byte[] bytes) =>
        ((BinTreeF32)SafeBinTree.Parse(bytes).Objects[F(Sys)].Properties[F("fxScale")]).Value;

    private static int CountOf(byte[] bytes, string path)
    {
        var obj = PropCodec.ReadObject(bytes, H(path))!;
        return (int)((PropInt)obj.Properties.ValueAt(obj.Properties.IndexOf(H("count")))).Signed;
    }

    /// <summary>The game of the flow tests with a particle bin, <see cref="X"/>, in a WAD of its own.</summary>
    private string GameWithFx(string name) =>
        new SyntheticGame()
            .Add("DATA.wad.client", "data/readme.txt", new byte[] { 1 })
            .Add("Global.wad.client", "data/global.txt", new byte[] { 2 })
            .Add("Champions/A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
            .Add("Map11.wad.client", M, Bin(Obj("Test/Obj/M", new[] { "game" })))
            .Add("Champions/Fx.wad.client", X, FxBin())
            .Write(_temp.Combine(name));

    /// <summary>The package of <see cref="GameWithFx"/>: the base layer renames the system.</summary>
    private ReyProject PackageWithFx(string name) =>
        Package(GameWithFx(name), extraModule: Target(X, "{\"Test/Fx/Sys\":{\"particleName\":\"pkg\"}}"), extraName: X);

    /// <summary>A package of the given documents for the bins A, M and P.</summary>
    private ReyProject PackageOf(string gameDirectory, string baseDoc, string fixDoc, string name)
    {
        string package = WriteFantome(_temp.Combine(name + _n++ + ".fantome"),
            Entry("META/info.json", Layers(baseDoc, fixDoc)),
            ("WAD/Map11.wad.client", ModWad("base", (M, Bin(Obj("Test/Obj/M", new[] { "mod" }))), (P, Bin(Obj("Test/Obj/P", new[] { "plain" }))))),
            Table(A, M, P));
        string projects = _temp.Combine("projects");
        Directory.CreateDirectory(projects);
        var result = FantomeImporter.Import(package, projects, null, new HashDatabase());
        var project = ReyProjectService.OpenFolder(result.RootPath);
        project.GameDirectory = gameDirectory;
        project.OutputDirectory = _temp.Combine("build" + _n++);
        return project;
    }

    // ================================================================================================ stale documents

    [Fact]
    public async Task A_particle_editor_opened_before_the_preview_was_ready_is_merged_onto_the_bin_the_GameData_makes_and_not_written_over_it()
    {
        var project = PackageWithFx("g-fx");
        var vm = await Open(project);
        var entry = EntryOf(vm, X);
        Assert.Equal("pkg", ParticleNameOf(Read(vm, X)));                                            // the package renames the system
        // the editor was opened while the preview was working, so it holds the bytes the game has: no rename in it
        Assert.True(vm.ParticleEditor.Load(entry, FxBin(), editable: true));
        Assert.Equal(FxBin(), vm.ParticleEditor.BaseBytes);
        // the person edits a field of the system that the package did not touch
        vm.ParticleEditor.Document!.Systems[0].Properties.Single(p => p.Name.Contains(F("fxScale").ToString("x8"), StringComparison.OrdinalIgnoreCase)).Apply("3");
        Assert.True(vm.ParticleEditor.Document!.IsDirty);
        var filesBefore = Files(project);

        await vm.ParticleEditor.SaveOverrideCommand.ExecuteAsync(null);

        var served = Read(vm, X);
        Assert.Equal("pkg", ParticleNameOf(served));                                                 // the package's effect is still there - diffed as it was, the save would have given the game's name back
        Assert.Equal(3f, FxScaleOf(served));                                                         // and the person's edit is
        var module = Assert.Single(GameDataDocumentText.Read(File.ReadAllText(StoreFile(project, "base"))).Modules);
        Assert.DoesNotContain("particleName", module.Text);                                          // what is kept states the field and nothing the package already says
        Assert.Equal(filesBefore, Files(project));
    }

    [Fact]
    public async Task A_Map_Bin_Editor_opened_before_the_preview_was_ready_saves_its_edit_on_top_of_the_package_and_does_not_take_the_package_out()
    {
        var project = Package(Game("g-mapbin"));
        var vm = await Open(project);
        var entry = EntryOf(vm, A);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(Read(vm, A), "Test/Obj/A"));
        byte[] raw = Bin(Obj("Test/Obj/A", new[] { "g1" }));                                         // the game's bin: what the editor read while the preview was working
        vm.MapBinEditor.Load(entry, raw);
        var count = vm.MapBinEditor.Rows.First(r => r.IsEditableText && r.Text == "1");
        count.Text = "11";
        Assert.True(vm.MapBinEditor.IsDirty);

        await vm.MapBinEditor.SaveCommand.ExecuteAsync(null);

        var served = Read(vm, A);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(served, "Test/Obj/A"));                  // the package's tags are still there
        Assert.Equal(11, CountOf(served, "Test/Obj/A"));
        Assert.Equal(served, vm.MapBinEditor.BaseBytes);                                             // and the editor shows the bin as it is served, which its next save is merged from
        Assert.False(vm.MapBinEditor.IsDirty);
        // the same edit saved again changes nothing
        Assert.Contains("Saved", vm.MapBinEditor.Status);
    }

    [Fact]
    public async Task An_editor_with_edits_when_the_preview_becomes_ready_keeps_them_and_its_save_is_merged_onto_the_bin_the_GameData_makes()
    {
        var project = Package(Game("g-pending-open"));
        var vm = await Open(project);
        var gated = new GatedGame(new InstalledGame(project.GameDirectory!, _temp.Combine("cache-gate-open", "i.idx")));
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        Call(vm, "BuildMounts");                                                                     // the project is read again: the preview is working
        Assert.True(Preview(vm)!.IsPending);
        var entry = EntryOf(vm, M);                                                                  // the mod's own copy is mounted at once
        byte[] raw = Read(vm, M);
        Assert.Equal(new[] { "mod" }, TagsOf(raw, "Test/Obj/M"));                                    // what is read while it works: no module of the package in it

        // the Map Bin Editor is opened on it, and the person edits a field
        vm.MapBinEditor.Load(entry, raw);
        var count = vm.MapBinEditor.Rows.First(r => r.IsEditableText && r.Text == "1");
        count.Text = "21";
        Assert.True(vm.MapBinEditor.IsDirty);

        gated.Gate.Set();
        await Applied(vm);                                                                           // the preview is ready: what the editors hold is not what is served

        Assert.Equal(new[] { "mod", "base" }, TagsOf(Read(vm, M), "Test/Obj/M"));
        Assert.Equal(raw, vm.MapBinEditor.BaseBytes);                                                // an editor with edits keeps them (and the base they were made against)
        Assert.True(vm.MapBinEditor.IsDirty);

        await vm.MapBinEditor.SaveCommand.ExecuteAsync(null);                                        // and its save is merged, not written over

        var served = Read(vm, M);
        Assert.Equal(new[] { "mod", "base" }, TagsOf(served, "Test/Obj/M"));                        // the package's tag is still there
        Assert.Equal(21, CountOf(served, "Test/Obj/M"));
        Assert.True(File.Exists(StoreFile(project, "base")));
        gated.Dispose();
    }

    [Fact]
    public async Task A_clean_Map_Bin_Editor_opened_while_the_preview_is_pending_is_loaded_again_when_it_is_ready()
    {
        var project = Package(Game("g-pending-clean"));
        var vm = await Open(project);
        var gated = new GatedGame(new InstalledGame(project.GameDirectory!, _temp.Combine("cache-gate-clean", "i.idx")));
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        Call(vm, "BuildMounts");
        Assert.True(Preview(vm)!.IsPending);
        var entry = EntryOf(vm, M);
        byte[] raw = Read(vm, M);
        vm.MapBinEditor.Load(entry, raw);                                                            // opened on what the mounts hold while the preview works

        gated.Gate.Set();
        await Applied(vm);

        Assert.Equal(Read(vm, M), vm.MapBinEditor.BaseBytes);                                        // it shows the bin as it is served now
        Assert.NotEqual(raw, vm.MapBinEditor.BaseBytes);
        Assert.False(vm.MapBinEditor.IsDirty);
        gated.Dispose();
    }

    [Fact]
    public async Task Two_editors_on_one_bin_the_second_to_save_keeps_what_the_first_saved()
    {
        var project = Package(Game("g-two"));
        var vm = await Open(project);
        var entry = EntryOf(vm, A);
        vm.MapBinEditor.Load(entry, Read(vm, A));                                                    // opened on the bin as it is served: nothing stale about it yet
        var row = vm.MapBinEditor.Rows.First(r => r.IsEditableText && r.Text == "1");

        Assert.True(await Save(vm, entry, AEdited(1, "g1", "base", "fix", "mine")));                  // another editor saves a tag first
        Assert.Equal(new[] { "g1", "base", "fix", "mine" }, TagsOf(Read(vm, A), "Test/Obj/A"));

        row.Text = "5";
        await vm.MapBinEditor.SaveCommand.ExecuteAsync(null);

        var served = Read(vm, A);
        Assert.Equal(new[] { "g1", "base", "fix", "mine" }, TagsOf(served, "Test/Obj/A"));          // the first editor's tag survived the second's save
        Assert.Equal(5, CountOf(served, "Test/Obj/A"));
    }

    [Fact]
    public async Task A_repair_of_a_particle_bin_the_GameData_changes_is_guarded_and_merged_and_does_not_write_the_stale_document_over_it()
    {
        var project = PackageWithFx("g-repair");
        var vm = await Open(project);
        var entry = EntryOf(vm, X);
        Assert.True(vm.ParticleEditor.Load(entry, FxBin(), editable: true));                         // opened on the game's bytes
        var filesBefore = Files(project);

        Assert.True(await (Task<bool>)Call(vm, "RepairParticleBinAsync", entry, vm.ParticleEditor.Document!)!);

        Assert.Equal("pkg", ParticleNameOf(Read(vm, X)));                                            // the package's effect is still there
        Assert.False(LtkEditStore.Any(project));                                                     // and nothing was kept: the document held nothing of its own
        Assert.Equal(filesBefore, Files(project));
        Assert.Equal(Read(vm, X), vm.ParticleEditor.BaseBytes);                                      // the editor is loaded again from the bin as it is served

        // a preview that cannot be relied on refuses the repair before anything is saved: the actions of the Bin Issues window are guarded now that the bin is editable
        SetField(vm, "_gameData", null);
        Assert.False(await (Task<bool>)Call(vm, "RepairParticleBinAsync", entry, vm.ParticleEditor.Document!)!);
        Assert.Contains("cannot be saved on top of the mod's GameData", vm.Status);
    }

    [Fact]
    public async Task A_repair_whose_editor_holds_another_document_by_now_is_refused_because_its_base_bytes_are_not_that_documents()
    {
        var project = PackageWithFx("g-repair-moved");
        var vm = await Open(project);
        var entry = EntryOf(vm, X);
        Assert.True(vm.ParticleEditor.Load(entry, FxBin(), editable: true));
        var shownByTheWindow = vm.ParticleEditor.Document!;                                          // what the Bin Issues window was opened for
        Assert.True(vm.ParticleEditor.Load(entry, Read(vm, X), editable: true));                     // and the editor was loaded again meanwhile (the preview settled, say)
        var filesBefore = Files(project);

        Assert.False(await (Task<bool>)Call(vm, "RepairParticleBinAsync", entry, shownByTheWindow)!);

        Assert.Contains("holds another document now", vm.Status);
        Assert.False(LtkEditStore.Any(project));
        Assert.Equal(filesBefore, Files(project));
        Assert.Equal("pkg", ParticleNameOf(Read(vm, X)));

        // the Materials editor holds no bin at all: what it would repair is not this one
        Assert.False(await (Task<bool>)Call(vm, "RepairMaterialBinAsync", entry, vm.MaterialEditor)!);
        Assert.Contains("holds another bin now", vm.Status);
        Assert.False(LtkEditStore.Any(project));
    }

    [Fact]
    public async Task The_save_that_cannot_await_merges_a_stale_document_or_refuses_it_and_leaves_any_other_bin_alone()
    {
        var project = Package(Game("g-sync-rebase"));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        var entryA = EntryOf(vm, A);
        byte[] raw = Bin(Obj("Test/Obj/A", new[] { "g1" }));
        byte[] mine = Bin(Obj("Test/Obj/A", new[] { "g1" }, count: 4));                                 // the document of an editor opened on the game's bin, edited (a property the package did not touch)

        var merged = (byte[]?)Call(vm, "RebaseForSave", entryA, mine, raw, "Bin");

        Assert.NotNull(merged);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(merged!, "Test/Obj/A"));                  // the package's tags are kept
        Assert.Equal(4, CountOf(merged!, "Test/Obj/A"));
        Assert.Contains(Lines(log, "Bin"), l => l.Message.Contains("merged", StringComparison.Ordinal));

        // an editor that is fresh is handed back as it is
        byte[] served = Read(vm, A);
        Assert.Same(mine, (byte[]?)Call(vm, "RebaseForSave", entryA, mine, served, "Bin"));

        // one that does not know what it was opened from is refused, with the reason
        Assert.Null((byte[]?)Call(vm, "RebaseForSave", entryA, mine, null, "Bin"));
        Assert.Contains("does not know which bin its document was read from", vm.Status);

        // and a bin the GameData does not change is not its business
        Assert.Same(mine, (byte[]?)Call(vm, "RebaseForSave", EntryOf(vm, P), mine, raw, "Bin"));
    }

    [Fact]
    public async Task An_editor_that_cannot_say_what_its_document_was_read_from_is_refused_for_a_bin_the_GameData_changes_and_a_flow_that_read_the_bin_just_now_is_not()
    {
        var project = Package(Game("g-nobase"));
        var vm = await Open(project);
        var entry = EntryOf(vm, A);

        Assert.False(await (Task<bool>)Call(vm, "SaveEditorBinBytesAsync", entry, AEdited(), null, "Particle", true)!);

        Assert.Contains("does not know which bin its document was read from", vm.Status);
        Assert.False(LtkEditStore.Any(project));
        Assert.True(await Save(vm, entry, AEdited()));                                               // a flow that read the bin a moment ago has no document to be stale
        Assert.True(File.Exists(StoreFile(project, "fix")));
    }

    // ================================================================================================ a save is made against one preview

    [Fact]
    public async Task A_save_whose_plan_is_overtaken_by_a_rebuild_of_the_mounts_settles_and_plans_again_instead_of_being_refused_and_rolled_back()
    {
        var project = Package(Game("g-race"));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        var entry = EntryOf(vm, A);
        var first = Preview(vm);
        int landed = 0;
        // the file watcher rebuilds the mounts a moment after any file changes: here, between the plan and the keeping
        SetField(vm, "GameDataBeforeCommit", new Func<Task>(() =>
        {
            if (Interlocked.Increment(ref landed) == 1) Call(vm, "BuildMounts");
            return Task.CompletedTask;
        }));

        bool saved = await Save(vm, entry, AEdited());

        Assert.True(saved, vm.Status);
        Assert.Equal(2, landed);                                                                     // planned twice: against the preview the rebuild replaced, then against its successor
        Assert.NotSame(first, Preview(vm));
        Assert.DoesNotContain(Lines(log, "GameData"), l => l.Level == LogLevel.Warning);             // no "preview is not ready", no rollback
        Assert.DoesNotContain(Lines(log, "GameData"), l => l.Message.Contains("not ready", StringComparison.Ordinal));
        Assert.Single(GameDataDocumentText.Read(File.ReadAllText(StoreFile(project, "fix"))).Modules);
        await Applied(vm);
        AssertSameData(AEdited(), Read(vm, A), "the bin read back");
    }

    [Fact]
    public async Task A_save_that_is_overtaken_again_and_again_says_so_and_keeps_nothing()
    {
        var project = Package(Game("g-race-again"));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        var entry = EntryOf(vm, A);
        SetField(vm, "GameDataBeforeCommit", new Func<Task>(() => { Call(vm, "BuildMounts"); return Task.CompletedTask; }));

        bool saved = await Save(vm, entry, AEdited());

        Assert.False(saved);
        // said in the log: the status line is also written by the progress of the preview the last rebuild started, which lands whenever the thread pool gets to it
        Assert.Contains(Lines(log, "GameData"), l => l.Message.Contains("again and again", StringComparison.Ordinal));
        Assert.False(File.Exists(StoreFile(project, "fix")));
        SetField(vm, "GameDataBeforeCommit", null);
        await Applied(vm);
    }

    [Fact]
    public async Task Bins_saved_together_are_proven_before_any_is_kept_and_one_that_cannot_be_kept_keeps_none()
    {
        var project = Package(Game("g-group"));
        var vm = await Open(project);
        var entryA = EntryOf(vm, A);
        var entryM = EntryOf(vm, M);
        var gone = Obj("Test/Obj/M", new[] { "mod", "base" });
        gone.Properties.Remove(H("count"));                                                          // a property removed: no declaration says that
        var filesBefore = Files(project);

        string? why = await (Task<string?>)Call(vm, "SaveMapBinsTogetherAsync", new List<(WadAssetEntry, byte[])> { (entryA, AEdited()), (entryM, Bin(gone)) })!;

        Assert.NotNull(why);
        Assert.Contains("no declaration removes a property", why);
        Assert.False(LtkEditStore.Any(project));                                                     // the edit of A, which was fine, was not kept either
        Assert.Equal(filesBefore, Files(project));
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(Read(vm, A), "Test/Obj/A"));

        // and the same two bins, both fine, are both kept
        Assert.Null(await (Task<string?>)Call(vm, "SaveMapBinsTogetherAsync",
            new List<(WadAssetEntry, byte[])> { (entryA, AEdited()), (entryM, Bin(Obj("Test/Obj/M", new[] { "mod", "base", "mine" }))) })!);
        Assert.Equal(new[] { ChunkOf(A), ChunkOf(M) }.Order(), Field<HashSet<ulong>>(vm, "_gameDataEdited").Order());
    }

    [Fact]
    public async Task Bins_saved_together_where_the_second_cannot_be_stored_leave_the_first_as_it_was()
    {
        var project = Package(Game("g-rollback"));
        var vm = await Open(project);
        var entryA = EntryOf(vm, A);
        var entryM = EntryOf(vm, M);
        byte[] bytesA = Read(vm, A), bytesM = Read(vm, M);
        Directory.CreateDirectory(StoreFile(project, "base"));                                       // M's layer cannot be written: a folder stands where its file goes
        var filesBefore = Files(project);

        string? why = await (Task<string?>)Call(vm, "SaveMapBinsTogetherAsync",
            new List<(WadAssetEntry, byte[])> { (entryA, AEdited()), (entryM, Bin(Obj("Test/Obj/M", new[] { "mod", "base", "mine" }))) })!;

        Assert.NotNull(why);
        Assert.Contains("could not be stored", why);
        Assert.Contains("None of the bins saved with it was kept", why);
        Assert.False(File.Exists(StoreFile(project, "fix")));                                        // A's edit, which was kept first, was taken away again
        Assert.Equal(bytesA, Read(vm, A));
        Assert.Equal(bytesM, Read(vm, M));
        Assert.Empty(Field<HashSet<ulong>>(vm, "_gameDataEdited"));
        Assert.Equal(filesBefore, Files(project));
    }

    // ================================================================================================ smaller things

    [Fact]
    public async Task A_bin_that_is_no_target_is_saved_at_once_while_the_preview_is_still_working()
    {
        var project = Package(Game("g-nonblock"));
        var vm = await Open(project);                                                                // Ready once: the next preview inherits what the documents name
        var gated = new GatedGame(new InstalledGame(project.GameDirectory!, _temp.Combine("cache-gate-nonblock", "i.idx")));
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        SetField(vm, "GameDataWriteWait", TimeSpan.FromSeconds(12));      // a save that waited for the preview would take all of it
        Call(vm, "BuildMounts");
        Assert.True(Preview(vm)!.IsPending);
        var entry = EntryOf(vm, P);
        var clock = Stopwatch.StartNew();

        bool saved = await Save(vm, entry, Bin(Obj("Test/Obj/P", new[] { "edited" })));

        Assert.True(saved);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the save waited {clock.Elapsed}");
        Assert.True(Preview(vm)!.IsPending);                                                         // and it did not wait for the preview
        Assert.Equal(new[] { "edited" }, TagsOf(File.ReadAllBytes(Path.Combine(project.RootPath!, "Map11", "data", "t", "plain.bin")), "Test/Obj/P"));
        gated.Gate.Set();
        await Applied(vm);
        gated.Dispose();
    }

    [Fact]
    public async Task While_the_edits_of_a_layer_cannot_be_used_a_new_edit_of_its_bins_is_refused_and_the_package_still_previews()
    {
        var project = Package(Game("g-damaged"));
        var vm = await Open(project);
        Assert.True(await Save(vm, EntryOf(vm, A), AEdited()));
        File.WriteAllText(StoreFile(project, "fix"), "{ not json");
        var log = CaptureLog(vm);
        Call(vm, "BuildMounts");                                                                     // the project is read again: the file watcher, or a new session
        await Applied(vm);

        Assert.Equal(GameDataPreviewState.Ready, Preview(vm)!.State);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(Read(vm, A), "Test/Obj/A"));              // the package alone
        Assert.Contains(Lines(log, "GameData"), l => l.Level == LogLevel.Warning && l.Message.Contains("edits kept on top of layer 'fix' are not applied", StringComparison.Ordinal));
        Assert.Empty(Field<HashSet<ulong>>(vm, "_gameDataEdited"));

        Assert.False(await Save(vm, EntryOf(vm, A), AEdited(9)));
        Assert.Contains("not applied", vm.Status);
        Assert.Equal("{ not json", File.ReadAllText(StoreFile(project, "fix")));                     // the person's file is as they left it
    }

    [Fact]
    public async Task The_backup_of_a_cleanup_of_a_bin_the_GameData_changes_is_the_bin_without_the_GameData()
    {
        var project = Package(Game("g-backup"));
        var vm = await Open(project);
        byte[] served = Read(vm, M);
        Assert.Equal(new[] { "mod", "base" }, TagsOf(served, "Test/Obj/M"));

        string? dir = (string?)Call(vm, "BackupMaterialsBin", EntryOf(vm, M), served, new List<string> { "x" });

        Assert.NotNull(dir);
        Assert.Equal(new[] { "mod" }, TagsOf(File.ReadAllBytes(Path.Combine(dir!, "m.bin")), "Test/Obj/M"));   // the mod's own copy: put back by hand it does not apply the GameData twice
        // a bin no declaration names is backed up as the cleanup read it
        byte[] plain = Read(vm, P);
        string? plainDir = (string?)Call(vm, "BackupMaterialsBin", EntryOf(vm, P), plain, new List<string> { "y" });
        Assert.Equal(plain, File.ReadAllBytes(Path.Combine(plainDir!, "plain.bin")));
    }

    [Fact]
    public async Task What_a_refusal_says_to_do_instead_is_said_only_where_it_can_be_done()
    {
        var project = Package(Game("g-texts"));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        Assert.DoesNotContain("in place", MainWindowViewModel.GameDataEditRefusal);

        // a whole-file write that has no editor to go to says why, and nothing more
        Assert.False((bool)Call(vm, "GuardEditable", EntryOf(vm, A))!);
        Assert.DoesNotContain("open it in an editor", Lines(log, "GameData").Last().Message);

        // and what is kept or not is told by the low writer in words that fit
        var args = new object?[] { EntryOf(vm, A), Read(vm, A), "" };
        Assert.True((bool)Call(vm, "TryWriteToProjectFile", args)!);                                  // the bin as the package makes it: nothing to keep
        Assert.DoesNotContain("reyengine-edits.json", (string)args[2]!);
        Assert.Contains("no file", (string)args[2]!);
        Assert.True(await Save(vm, EntryOf(vm, A), AEdited()));
        args = new object?[] { EntryOf(vm, A), AEdited(), "" };
        Assert.True((bool)Call(vm, "TryWriteToProjectFile", args)!);                                  // the same edit again: it is where it was
        Assert.EndsWith("(a declaration on top of the GameData)", (string)args[2]!);
        Assert.True(await Save(vm, EntryOf(vm, A), Bin(Obj("Test/Obj/A", new[] { "g1", "base", "fix" }))));   // and back to the package's bin: the edit is taken away
        Assert.False(LtkEditStore.Any(project));
    }

    [Fact]
    public async Task Taking_an_edit_away_shows_the_bin_again_in_the_editors_that_hold_it_even_when_the_GameData_changes_nothing_of_it()
    {
        // the package names M and A and changes neither (its edits name an object they do not hold, and are skipped): the bins are what the mod and the game have
        var project = PackageOf(Game("g-noeffect"), Doc(Target(M, "{\"Test/Obj/Ghost\":{\"count\":1}}")), Doc(Target(A, "{\"Test/Obj/Ghost\":{\"count\":1}}")), "noeffect");
        var vm = await Open(project);
        Assert.Empty(Preview(vm)!.Entries);
        var entry = EntryOf(vm, M);                                                                  // the mod's own copy
        byte[] original = Read(vm, M);
        Assert.True(await Save(vm, entry, Bin(Obj("Test/Obj/M", new[] { "mod", "mine" }))));
        Assert.NotEmpty(Preview(vm)!.Entries);
        vm.MapBinEditor.Load(EntryOf(vm, M), Read(vm, M));                                            // an editor holds the bin with the edit in it
        Assert.NotEqual(original, vm.MapBinEditor.BaseBytes);
        vm.ProjectMode = true;
        SetField(vm, "_contextOverride", new AssetNodeViewModel(new AssetTreeNode { Name = "m.bin", FullPath = M, Entry = EntryOf(vm, M) }));

        vm.RevertGameDataEditsCommand.Execute(null);

        Assert.Empty(Preview(vm)!.Entries);                                                          // nothing is served any more: the refresh used to stop here
        Assert.Equal(original, Read(vm, M));
        Assert.Equal(original, vm.MapBinEditor.BaseBytes);                                           // and the editor shows the bin as it is
    }

    [Fact]
    public async Task The_LTK_store_is_nothing_the_browsers_refresh_is_woken_for()
    {
        var project = Package(Game("g-watch"));
        var vm = await Open(project);
        string root = project.RootPath!;
        string store = LtkEditStore.PathOf(root, "fix");
        string project_json = Path.Combine(root, ".reyengine", "project.json");

        Assert.True((bool)Call(vm, "IsInLtkStore", store)!);
        Assert.True((bool)Call(vm, "IsInLtkStore", store.Replace('\\', '/'))!);
        Assert.True((bool)Call(vm, "IsInLtkStore", store + ".tmp")!);
        Assert.True((bool)Call(vm, "IsInLtkStore", Path.Combine(root, ".reyengine", "ltk"))!);                    // the folder itself: the watcher reports it for a file written below it
        Assert.True((bool)Call(vm, "IsInLtkStore", Path.Combine(root, ".reyengine", "ltk") + Path.DirectorySeparatorChar)!);
        Assert.True((bool)Call(vm, "IsInLtkStore", Path.Combine(root, ".reyengine", "ltk", "hashes", "game.harvested.hashes.txt"))!);
        Assert.False((bool)Call(vm, "IsInLtkStore", project_json)!);
        Assert.False((bool)Call(vm, "IsInLtkStore", Path.Combine(root, ".reyengine", "ltkx", "a"))!);   // a sibling that merely starts alike
        Assert.False((bool)Call(vm, "IsInLtkStore", Path.Combine(root, "Map11", "data", "t", "m.bin"))!);
        Assert.False((bool)Call(vm, "IsInLtkStore", new object?[] { null })!);

        // an event only about the store schedules nothing; one that is also about a file of the project does
        Call(vm, "ScheduleBrowserRefresh", new object?[] { new string?[] { store, store + ".tmp" } });
        Assert.Null(Field<System.Threading.Timer?>(vm, "_watchDebounce"));
        Call(vm, "ScheduleBrowserRefresh", new object?[] { new string?[] { store, Path.Combine(root, "Map11", "data", "t", "new.bin") } });
        var woken = Field<System.Threading.Timer?>(vm, "_watchDebounce");
        Assert.NotNull(woken);
        woken!.Dispose();
        SetField(vm, "_watchDebounce", null);

        // and for real: an edit saved on top of the GameData writes the store and the watcher says nothing the browser acts on
        vm.ProjectMode = true;
        Call(vm, "StartProjectWatchers");
        try
        {
            Assert.True(await Save(vm, EntryOf(vm, A), AEdited()));
            Assert.True(File.Exists(store));
            await Task.Delay(350);                                                                   // the events arrive within a few milliseconds; the debounce fires at 600
            Assert.Null(Field<System.Threading.Timer?>(vm, "_watchDebounce"));
        }
        finally
        {
            Call(vm, "StopProjectWatchers");
            Field<System.Threading.Timer?>(vm, "_watchDebounce")?.Dispose();
            SetField(vm, "_watchDebounce", null);
        }
    }
}

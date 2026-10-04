using LeagueToolkit.Core.Meta;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using static ReyEngine.Formats.Tests.LayeredFantomeSupport;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M823, the final round: the moment between the preview being ready and the editor applying it (a read still answers the bytes the declarations were not applied to), the flows that hold the bytes they read,
/// what a merge does with a property both sides changed, a rollback after the project was switched, the settle reload of the Particle Editor, and a revert under an editor that holds the edits.
/// </summary>
public sealed partial class LtkEditFlowTests
{
    // ================================================================================================ ready, and not yet applied

    /// <summary>
    /// Brings the editor to the moment between the preview being ready on its worker and the editor applying it, and runs <paramref name="inTheWindow"/> in it - on the editor's one thread, which is busy for as long as that runs,
    /// so the continuation that applies the preview is queued behind it. A read in that moment answers the bytes the mounts hold - the mod's own copy of a bin, without the package's changes (a bin only the game has is not mounted yet: a
    /// read of it fails). When it returns the editor applies the preview.
    /// </summary>
    private async Task InTheWindow(MainWindowViewModel vm, ReyProject project, LtkPreviewAppTests.UiLikeContext ui, Action<WadAssetEntry> inTheWindow)
    {
        var entry = EntryOf(vm, M);                                                                  // the mod ships its own copy of M: it is mounted whatever the preview does, and a read answers it
        var gated = new GatedGame(new InstalledGame(project.GameDirectory!, _temp.Combine("cache-window" + _n++, "i.idx")));
        try
        {
            SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
            await ui.Run(() => { Call(vm, "BuildMounts"); return 0; });                              // a file changed: a new preview of the same declarations, whose continuation is the editor's thread's
            var next = Preview(vm)!;
            Assert.True(next.IsPending);
            await ui.Run(() =>
            {
                gated.Gate.Set();
                Assert.True(next.Settled.Wait(TimeSpan.FromSeconds(60)));
                Assert.Equal(GameDataPreviewState.Ready, next.State);
                Assert.False(next.IsPending);                                                        // ready...
                Assert.False(Mounts(vm).IsOverlaid(ChunkOf(M)));                                     // ...and not applied: the index of the mounts does not hold its chunks yet
                Assert.Equal(new[] { "mod" }, TagsOf(Read(vm, M), "Test/Obj/M"));                    // so a read answers the mod's own bytes, without the package's tag
                inTheWindow(entry);
                return 0;
            });
            await Applied(vm);
        }
        finally { gated.Dispose(); }
    }

    [Fact]
    public async Task In_the_moment_the_preview_is_ready_and_not_applied_the_writers_that_cannot_wait_refuse_and_a_flow_that_waits_reads_the_bin_the_GameData_makes()
    {
        using var ui = new LtkPreviewAppTests.UiLikeContext();
        var project = Package(Game("g-window"));
        var vm = await Open(project);
        var filesBefore = Files(project);
        Task<bool>? flow = null;

        async Task<bool> Flow(WadAssetEntry entry)
        {
            if (!await (ValueTask<bool>)Call(vm, "GuardBinEditAsync", entry)!) return false;         // what a writer that can wait does first
            string[] tags = TagsOf(Read(vm, M), "Test/Obj/M");                                       // and only then reads: the bin the GameData makes
            return await Save(vm, entry, Bin(Obj("Test/Obj/M", tags, count: 9)));
        }

        await InTheWindow(vm, project, ui, entry =>
        {
            byte[] fromTheWindow = Bin(Obj("Test/Obj/M", new[] { "mod" }, count: 9));                // what an editor builds from the bytes it read in the window: the package's tag is not in it
            Assert.False((bool)Call(vm, "GuardBinEdit", entry)!);                                    // the guard of the writers that cannot wait
            Assert.Contains("try again in a moment", vm.Status);
            Assert.Throws<InvalidOperationException>(() => Call(vm, "TryWriteToProjectFile", entry, fromTheWindow, ""));
            Assert.Null(Call(vm, "RebaseForSave", entry, fromTheWindow, Read(vm, M), "Bin"));        // the raw bin and the Ritobin editors
            Assert.False(LtkEditStore.Any(project));

            flow = Flow(entry);                                                                      // starts in the window: the guard waits for the editor to apply the preview
            Assert.False(flow.IsCompleted);
        });

        Assert.True(await flow!.WaitAsync(TimeSpan.FromSeconds(60)));
        var served = Read(vm, M);
        Assert.Equal(new[] { "mod", "base" }, TagsOf(served, "Test/Obj/M"));                        // the package's tag was not taken away
        Assert.Equal(9, CountOf(served, "Test/Obj/M"));
        var module = Assert.Single(GameDataDocumentText.Read(File.ReadAllText(StoreFile(project, "base"))).Modules);
        Assert.DoesNotContain("tags", module.Text);                                                  // the edit states the count and nothing the package already says
        Assert.Equal(filesBefore, Files(project));
        Assert.Empty(ui.Faults);
    }

    [Fact]
    public async Task An_editor_that_read_the_bin_in_that_moment_and_saves_in_it_waits_and_has_its_edits_merged_onto_the_bin_the_GameData_makes()
    {
        using var ui = new LtkPreviewAppTests.UiLikeContext();
        var project = Package(Game("g-window-editor"));
        var vm = await Open(project);
        Task? saving = null;

        await InTheWindow(vm, project, ui, entry =>
        {
            vm.MapBinEditor.Load(entry, Read(vm, M));                                                // opened in the window: the mod's own bytes
            vm.MapBinEditor.Rows.First(r => r.IsEditableText && r.Text == "1").Text = "5";
            Assert.True(vm.MapBinEditor.IsDirty);
            saving = vm.MapBinEditor.SaveCommand.ExecuteAsync(null);                                 // and saves in it
            Assert.False(saving.IsCompleted);
        });

        await saving!.WaitAsync(TimeSpan.FromSeconds(60));
        var served = Read(vm, M);
        Assert.Equal(new[] { "mod", "base" }, TagsOf(served, "Test/Obj/M"));                        // the save waited for the preview, and the edit was merged onto the bin it made
        Assert.Equal(5, CountOf(served, "Test/Obj/M"));
        Assert.Empty(ui.Faults);
    }

    // ================================================================================================ the flows that hold the bytes they read

    [Fact]
    public async Task A_repoint_waits_for_the_preview_before_it_reads_the_bin_so_it_repoints_the_bin_the_GameData_makes()
    {
        var project = Package(Game("g-repoint-wait"));
        var vm = await Open(project);                                                                // Ready once: the next preview knows at once which bins the documents name
        var entry = EntryOf(vm, A);
        var gated = new GatedGame(new InstalledGame(project.GameDirectory!, _temp.Combine("cache-repoint", "i.idx")));
        SetField(vm, "GameDataGameFactory", new Func<string, IGameDataGame>(_ => gated));
        Call(vm, "BuildMounts");
        Assert.True(Preview(vm)!.IsPending);

        var repoint = (Task<bool>)Call(vm, "RepointAssetRefAsync", entry, "g1", "gone")!;
        Assert.False(repoint.IsCompleted);                                                           // it waits for the preview: a read now would answer the game's bytes
        gated.Gate.Set();

        Assert.True(await repoint.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(new[] { "gone", "base", "fix" }, TagsOf(Read(vm, A), "Test/Obj/A"));           // the package's tags are still there
        await Applied(vm);
        gated.Dispose();
    }

    [Fact]
    public async Task A_repoint_whose_bin_was_saved_by_another_editor_meanwhile_has_its_edit_merged_onto_it()
    {
        var project = Package(Game("g-repoint-merge"));
        var vm = await Open(project);
        var entry = EntryOf(vm, A);
        SetField(vm, "GameDataBeforeCommit", new Func<Task>(async () =>
        {
            SetField(vm, "GameDataBeforeCommit", null);
            Assert.True(await Save(vm, entry, AEdited(4, "g1", "base", "fix")));                    // between the repoint's read and its keeping, another editor saves the count
        }));

        Assert.True(await (Task<bool>)Call(vm, "RepointAssetRefAsync", entry, "g1", "gone")!);

        var served = Read(vm, A);
        Assert.Equal(new[] { "gone", "base", "fix" }, TagsOf(served, "Test/Obj/A"));
        Assert.Equal(4, CountOf(served, "Test/Obj/A"));                                              // the other editor's count was not put back to 1
    }

    private const string Mats = "data/t/mats.bin", Ground = "Maps/Test/Materials/Ground", OldBush = "Maps/Test/Materials/OldBush", OldRock = "Maps/Test/Materials/OldRock";

    private static PropObject Mat(string path, params string[] tags)
    {
        var material = new PropObject(H(path), H("StaticMaterialDef"));
        material.Properties.Set(H("tags"), Strings(tags));
        return material;
    }

    private static byte[] MatsBin(params string[] groundTags) => Bin(Mat(Ground, groundTags), Mat(OldBush, "old"), Mat(OldRock, "old"));

    /// <summary>The game of the flow tests with a materials bin of three materials, <see cref="Mats"/>.</summary>
    private string GameWithMats(string name) =>
        new SyntheticGame()
            .Add("DATA.wad.client", "data/readme.txt", new byte[] { 1 })
            .Add("Global.wad.client", "data/global.txt", new byte[] { 2 })
            .Add("Champions/A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
            .Add("Map11.wad.client", M, Bin(Obj("Test/Obj/M", new[] { "game" })))
            .Add("Champions/Mats.wad.client", Mats, MatsBin("g"))
            .Write(_temp.Combine(name));

    /// <summary>The package of <see cref="GameWithMats"/>: the base layer tags the Ground material.</summary>
    private ReyProject PackageWithMats(string name) =>
        Package(GameWithMats(name), extraModule: AddTag(Mats, Ground, "base"), extraName: Mats);

    [Fact]
    public async Task A_cleanup_whose_dialog_was_open_while_another_editor_saved_the_bin_has_its_removal_merged_onto_it()
    {
        var project = PackageWithMats("g-cleanup-merge");
        var vm = await Open(project);
        var log = CaptureLog(vm);
        var entry = EntryOf(vm, Mats);
        Assert.Equal(new[] { "g", "base" }, TagsOf(Read(vm, Mats), Ground));
        // the dialog lasts as long as the person likes: another editor tags the Ground material meanwhile
        SetField(vm, "GameDataFlowPause", new Func<Task>(async () =>
        {
            SetField(vm, "GameDataFlowPause", null);
            Assert.True(await Save(vm, entry, MatsBin("g", "base", "mine")));
        }));

        bool removed = await (Task<bool>)Call(vm, "RemoveUnusedMaterialsAsync", entry, new List<string> { Ground }, (IReadOnlyList<string>)new List<string> { OldBush, OldRock })!;   // the window hands over the materials the person ticked

        Assert.True(removed, string.Join(" | ", Lines(log, "Materials").Concat(Lines(log, "GameData")).Select(l => l.Message)));
        var served = Read(vm, Mats);
        Assert.Equal(new[] { "g", "base", "mine" }, TagsOf(served, Ground));                         // the other editor's tag is still there
        Assert.Null(PropCodec.ReadObject(served, H(OldBush)));                                       // and the materials the person confirmed are gone
        Assert.Null(PropCodec.ReadObject(served, H(OldRock)));
        Assert.NotNull(PropCodec.ReadObject(served, H(Ground)));
        var module = Assert.Single(GameDataDocumentText.Read(File.ReadAllText(StoreFile(project, "base"))).Modules);
        Assert.Contains("mine", module.Text);
    }

    // ================================================================================================ what a merge does with a property both sides changed

    private ReyProject PackageWithFxScale(string name) =>
        Package(GameWithFx(name), extraModule: Target(X, "{\"Test/Fx/Sys\":{\"fxScale\":2}}"), extraName: X);

    private static void EditFxScale(MainWindowViewModel vm, string value) =>
        vm.ParticleEditor.Document!.Systems[0].Properties.Single(p => p.Name.Contains(F("fxScale").ToString("x8"), StringComparison.OrdinalIgnoreCase)).Apply(value);

    [Fact]
    public async Task A_property_the_GameData_changed_and_a_stale_editor_changed_to_another_value_refuses_the_save_and_writes_nothing()
    {
        var project = PackageWithFxScale("g-conflict");
        var vm = await Open(project);
        var entry = EntryOf(vm, X);
        Assert.Equal(2f, FxScaleOf(Read(vm, X)));                                                    // the package scales the system by 2
        Assert.True(vm.ParticleEditor.Load(entry, FxBin(), editable: true));                         // an editor opened on the game's bytes: scale 1
        EditFxScale(vm, "3");
        var filesBefore = Files(project);

        await vm.ParticleEditor.SaveOverrideCommand.ExecuteAsync(null);

        Assert.Contains("changed", vm.Status);
        Assert.Contains("since this editor opened it", vm.Status);
        Assert.Contains("reopen the editor", vm.Status);
        Assert.False(LtkEditStore.Any(project));
        Assert.Equal(2f, FxScaleOf(Read(vm, X)));                                                    // neither value was taken
        Assert.Equal(filesBefore, Files(project));
        Assert.True(vm.ParticleEditor.Document!.IsDirty);                                            // and the edit stays pending
    }

    [Fact]
    public async Task A_property_both_changed_to_the_same_value_is_no_conflict()
    {
        var project = PackageWithFxScale("g-conflict-equal");
        var vm = await Open(project);
        var entry = EntryOf(vm, X);
        Assert.True(vm.ParticleEditor.Load(entry, FxBin(), editable: true));
        EditFxScale(vm, "2");                                                                        // the package's own value

        await vm.ParticleEditor.SaveOverrideCommand.ExecuteAsync(null);

        Assert.DoesNotContain("since this editor opened it", vm.Status);
        Assert.Contains("no edit to keep", vm.Status);                                               // it is what the GameData makes of the bin
        Assert.False(LtkEditStore.Any(project));
        Assert.Equal(2f, FxScaleOf(Read(vm, X)));
    }

    [Fact]
    public async Task A_second_save_of_an_editor_after_a_kept_one_reports_no_conflict_with_its_own_first()
    {
        var project = PackageWithFx("g-second-save");
        var vm = await Open(project);
        var entry = EntryOf(vm, X);
        Assert.True(vm.ParticleEditor.Load(entry, Read(vm, X), editable: true));                     // opened on the bin as it is served
        EditFxScale(vm, "3");

        await vm.ParticleEditor.SaveOverrideCommand.ExecuteAsync(null);

        Assert.Equal(3f, FxScaleOf(Read(vm, X)));
        Assert.Equal(Read(vm, X), vm.ParticleEditor.BaseBytes);                                      // its document stands for the bin as served now, edits kept included
        EditFxScale(vm, "4");

        await vm.ParticleEditor.SaveOverrideCommand.ExecuteAsync(null);

        Assert.DoesNotContain("since this editor opened it", vm.Status);
        Assert.Equal(4f, FxScaleOf(Read(vm, X)));
        Assert.Equal("pkg", ParticleNameOf(Read(vm, X)));
    }

    [Fact]
    public async Task A_save_that_had_to_merge_something_in_leaves_the_base_of_the_editor_where_it_was()
    {
        var project = PackageWithFx("g-base-stays");
        var vm = await Open(project);
        var entry = EntryOf(vm, X);
        byte[] raw = FxBin();
        Assert.True(vm.ParticleEditor.Load(entry, raw, editable: true));                             // stale: the game's bytes, without the package's rename
        EditFxScale(vm, "3");

        await vm.ParticleEditor.SaveOverrideCommand.ExecuteAsync(null);

        Assert.Equal("pkg", ParticleNameOf(Read(vm, X)));                                            // the rename was merged in
        Assert.Equal(raw, vm.ParticleEditor.BaseBytes);                                              // and the document, which lacks it, keeps the base it was parsed from
    }

    [Fact]
    public async Task The_Materials_editor_is_rebased_like_the_particle_editor_after_a_save_that_kept_its_document()
    {
        var project = PackageWithMats("g-material-base");
        var vm = await Open(project);
        var entry = EntryOf(vm, Mats);
        byte[] served = Read(vm, Mats);
        byte[] opened = (byte[])served.Clone();                                                      // the bytes the document was parsed from: the same content, another array
        vm.MaterialEditor.Load(MaterialDocument.Parse(opened, _ => null, _ => null), entry, opened);
        vm.MaterialEditor.IsDirty = true;

        await (Task)Call(vm, "SaveMaterialOverrideFor", vm.MaterialEditor, (Action)(() => { }))!;

        Assert.NotSame(opened, vm.MaterialEditor.BaseBytes);                                         // told what is served now
        Assert.Equal(Read(vm, Mats), vm.MaterialEditor.BaseBytes);
    }

    // ================================================================================================ a rollback after the project was switched

    [Fact]
    public async Task A_rollback_after_the_project_was_switched_puts_the_edit_back_in_the_project_it_was_kept_in()
    {
        var project = Package(Game("g-switch"));
        var vm = await Open(project);
        var other = new ReyProject { Name = "another project" };
        int planned = 0;
        // the second bin's save: between its plan and its keeping the person opens another project
        SetField(vm, "GameDataBeforeCommit", new Func<Task>(() =>
        {
            if (Interlocked.Increment(ref planned) == 2) vm.Project = other;
            return Task.CompletedTask;
        }));

        string? why = await (Task<string?>)Call(vm, "SaveMapBinsTogetherAsync",
            new List<(WadAssetEntry, byte[])> { (EntryOf(vm, A), AEdited()), (EntryOf(vm, M), Bin(Obj("Test/Obj/M", new[] { "mod", "base", "mine" }))) })!;

        Assert.NotNull(why);
        Assert.Contains("None of the bins saved with it was kept", why);
        Assert.DoesNotContain("could NOT be put back", why);
        Assert.False(LtkEditStore.Any(project));                                                     // the first bin's edit is gone from the project it was kept in
        Assert.False(File.Exists(StoreFile(project, "fix")));
        Assert.Same(other, vm.Project);                                                              // and the project the editor shows was not touched
    }

    // ================================================================================================ several bins, one unit: what is said and done when it fails

    [Fact]
    public async Task A_unit_that_fails_says_why_the_bin_failed_and_not_what_the_status_line_said_before_and_rebuilds_a_preview_that_cannot_follow()
    {
        var project = Package(Game("g-unit-rebuild"));
        var vm = await Open(project);
        var first = Preview(vm);
        vm.Status = "A STALE STATUS";
        int planned = 0;
        // the second bin's save: the preview goes away between its plan and its keeping (the project is rebuilt for good), so the first bin's edit cannot be taken away in place
        SetField(vm, "GameDataBeforeCommit", new Func<Task>(() =>
        {
            if (Interlocked.Increment(ref planned) == 2) Call(vm, "CancelGameData");
            return Task.CompletedTask;
        }));

        string? why = await (Task<string?>)Call(vm, "SaveMapBinsTogetherAsync",
            new List<(WadAssetEntry, byte[])> { (EntryOf(vm, A), AEdited()), (EntryOf(vm, M), Bin(Obj("Test/Obj/M", new[] { "mod", "base", "mine" }))) })!;

        Assert.NotNull(why);
        Assert.DoesNotContain("A STALE STATUS", why);
        Assert.Contains("None of the bins saved with it was kept", why);
        Assert.False(LtkEditStore.Any(project));                                                     // the first bin's edit was put back in the store
        Assert.NotSame(first, Preview(vm));                                                          // and the mounts were rebuilt, once, so that the preview serves the store as it is
        await Applied(vm);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(Read(vm, A), "Test/Obj/A"));
    }

    [Fact]
    public async Task A_unit_whose_rollback_cannot_be_done_says_which_edit_could_not_be_put_back()
    {
        var project = Package(Game("g-unit-stuck"));
        var vm = await Open(project);
        FileStream? hold = null;
        int planned = 0;
        // after the first bin's edit was kept, its file is locked: the second bin fails, and the first cannot be taken away
        SetField(vm, "GameDataBeforeCommit", new Func<Task>(() =>
        {
            if (Interlocked.Increment(ref planned) == 2) hold = new FileStream(StoreFile(project, "fix"), FileMode.Open, FileAccess.Read, FileShare.None);
            return Task.CompletedTask;
        }));

        try
        {
            string? why = await (Task<string?>)Call(vm, "SaveMapBinsTogetherAsync",
                new List<(WadAssetEntry, byte[])> { (EntryOf(vm, A), AEdited()), (EntryOf(vm, M), Bin(Obj("Test/Obj/M", new[] { "mod", "base", "mine" }))) })!;

            Assert.NotNull(why);
            Assert.Contains("could NOT be put back", why);
            Assert.Contains($"0x{ChunkOf(A):x16}", why);
        }
        finally { hold?.Dispose(); }
        Assert.True(File.Exists(StoreFile(project, "fix")));                                         // the file is as it was left: the person is told
    }

    [Fact]
    public async Task A_unit_whose_second_bin_fails_without_a_refusal_says_that_bin_could_not_be_saved_and_not_what_the_status_line_said_of_the_first()
    {
        var project = Package(Game("g-unit-status"));
        var vm = await Open(project);
        var entryA = EntryOf(vm, A);

        // the first bin is kept (and the status line says so); the second is no bin at all, which fails without any refusal of the GameData to quote
        string? why = await (Task<string?>)Call(vm, "SaveMapBinsTogetherAsync",
            new List<(WadAssetEntry, byte[])> { (entryA, AEdited()), (EntryOf(vm, P), new byte[] { 1, 2, 3 }) })!;

        Assert.NotNull(why);
        Assert.Contains("'plain.bin' could not be saved", why);
        Assert.DoesNotContain("saved as a declaration", why);                                       // what the status line said of the first bin is no reason for the second
        Assert.Contains("None of the bins saved with it was kept", why);
        Assert.False(LtkEditStore.Any(project));                                                     // and the first was put back
    }

    // ================================================================================================ the Particle Editor's reload when the preview settles

    [Fact]
    public async Task A_reload_of_the_particle_editor_nobody_asked_for_is_dropped_when_the_editor_holds_another_document_by_the_time_it_is_parsed()
    {
        using var ui = new LtkPreviewAppTests.UiLikeContext();
        var project = PackageWithFx("g-particle-reload");
        var vm = await Open(project);
        var entry = EntryOf(vm, X);
        Assert.True(vm.ParticleEditor.Load(entry, Read(vm, X), editable: true));                     // clean
        var held = vm.ParticleEditor.Document;
        Task? reload = null;

        await ui.Run(() =>
        {
            reload = (Task)Call(vm, "OpenParticleEditorForAsync", entry, false)!;                    // the settle's reload starts: it parses off the thread, and its result comes back to this one
            Assert.True(vm.ParticleEditor.Load(entry, FxBin("other"), editable: true));              // the person opens a document meanwhile
            return 0;
        });
        await reload!.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.NotSame(held, vm.ParticleEditor.Document);
        Assert.Equal(FxBin("other"), vm.ParticleEditor.BaseBytes);                                   // the reload did not take its place

        // one with edits keeps them
        EditFxScale(vm, "7");
        Task? again = null;
        await ui.Run(() => { again = (Task)Call(vm, "OpenParticleEditorForAsync", entry, false)!; return 0; });
        await again!.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(vm.ParticleEditor.Document!.IsDirty);
        Assert.Equal(FxBin("other"), vm.ParticleEditor.BaseBytes);

        // and a clean editor that is still the one it started from takes it
        Assert.True(vm.ParticleEditor.Load(entry, FxBin(), editable: true));
        Task? loaded = null;
        await ui.Run(() => { loaded = (Task)Call(vm, "OpenParticleEditorForAsync", entry, false)!; return 0; });
        await loaded!.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(Read(vm, X), vm.ParticleEditor.BaseBytes);
        Assert.Empty(ui.Faults);
    }

    // ================================================================================================ the Map Bin Editor's save

    [Fact]
    public async Task A_Map_Bin_Editor_that_was_opened_on_another_bin_while_it_saved_is_left_as_it_is()
    {
        var editor = new MapBinEditorViewModel();
        var entryA = new WadAssetEntry { PathHash = ChunkOf(A), Path = A, IsResolved = true, Type = AssetType.Bin };
        var entryB = new WadAssetEntry { PathHash = ChunkOf(P), Path = P, IsResolved = true, Type = AssetType.Bin };
        byte[] binA = Bin(Obj("Test/Obj/A", new[] { "a" })), binB = Bin(Obj("Test/Obj/P", new[] { "b" }));
        var asked = new List<string>();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.ReadServedAfterSave = e => { asked.Add(e.Path); return null; };
        editor.SaveBytes = async (_, _) => { await gate.Task; return true; };
        editor.Load(entryA, binA);
        editor.IsDirty = true;

        var saving = editor.SaveCommand.ExecuteAsync(null);                                          // the save waits (for a preview, say)
        editor.Load(entryB, binB);                                                                   // and the person opens another bin in the window
        gate.SetResult(true);
        await saving;

        Assert.Same(entryB, editor.Entry);
        Assert.Equal(binB, editor.BaseBytes);                                                        // the other bin's document was not replaced by this save's bytes
        Assert.Equal(entryB.DisplayName, editor.Title);
        Assert.Empty(asked);                                                                         // and the served bytes of the first were not read into it
        Assert.Contains("another bin", editor.Status);
    }

    // ================================================================================================ a revert under an editor that holds the edits

    [Fact]
    public async Task After_a_revert_an_editor_that_holds_the_edits_is_stale_and_its_saves_are_refused_so_the_next_auto_save_does_not_declare_them_again()
    {
        var project = PackageWithFx("g-revert-stale");
        var vm = await Open(project);
        var entry = EntryOf(vm, X);
        Assert.True(vm.ParticleEditor.Load(entry, Read(vm, X), editable: true));
        EditFxScale(vm, "3");
        await vm.ParticleEditor.SaveOverrideCommand.ExecuteAsync(null);
        Assert.Equal(3f, FxScaleOf(Read(vm, X)));
        Assert.True(LtkEditStore.Any(project));

        SetField(vm, "_contextOverride", new AssetNodeViewModel(new AssetTreeNode { Name = "fx.bin", FullPath = X, Entry = entry }));
        vm.ProjectMode = true;
        vm.RevertGameDataEditsCommand.Execute(null);

        Assert.False(LtkEditStore.Any(project));
        Assert.Equal(1f, FxScaleOf(Read(vm, X)));
        Assert.NotNull(vm.ParticleEditor.StaleReason);                                               // the document still holds the scale it was saved with
        await vm.ParticleEditor.SaveOverrideCommand.ExecuteAsync(null);                              // the auto-save's tick
        Assert.Contains("reverted", vm.Status);
        Assert.Contains("reopen the editor", vm.Status);
        Assert.False(LtkEditStore.Any(project));                                                     // nothing was declared again
        Assert.Equal(1f, FxScaleOf(Read(vm, X)));

        // opened again, it is an editor like any other
        Assert.True(vm.ParticleEditor.Load(entry, Read(vm, X), editable: true));
        Assert.Null(vm.ParticleEditor.StaleReason);
        EditFxScale(vm, "5");
        await vm.ParticleEditor.SaveOverrideCommand.ExecuteAsync(null);
        Assert.Equal(5f, FxScaleOf(Read(vm, X)));
    }
}

using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M902: <c>SkinCharacterDataProperties.PersistentEffectConditions</c> - read, evaluated, and played by the Character window - and the idle
/// cycle / interrupted-clip rules that came with it. The reader and evaluator are proven on the REAL skin bins of Ahri 97, Aatrox 33,
/// Kai'Sa 83 and Evelynn 65 (skipped without the game install); the view-model rules on small synthetic graphs.
/// </summary>
public sealed class SkinPersistentEffectTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private sealed record Skin(byte[] SkinBin, byte[]? Graph, HashDatabase Db) : IDisposable
    {
        public string? Name(uint h) => Db.TryGetBinName(h, out var n) ? n : null;
        public string? Path(ulong h) => Db.TryGetPath(h, out var p) ? p : null;
        public void Dispose() { }
    }

    private static Skin? Real(string champion, int skin)
    {
        string wad = System.IO.Path.Combine(Final, "Champions", champion + ".wad.client");
        if (!File.Exists(wad) || Database.Value is not { } db) return null;
        using var archive = WadArchive.Open(wad, new WadPathResolver(db));
        byte[]? Read(string path) => archive.TryGetEntry(HashAlgorithms.WadPath(path), out _) ? archive.Extract(HashAlgorithms.WadPath(path)) : null;
        string c = champion.ToLowerInvariant();
        byte[]? bin = Read($"data/characters/{c}/skins/skin{skin}.bin");
        return bin is null ? null : new Skin(bin, Read($"data/characters/{c}/animations/skin{skin}.bin"), db);
    }

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static PersistentContext Ctx(Skin s, IEnumerable<string>? on = null, IEnumerable<string>? clips = null,
        IEnumerable<string>? visible = null, Action<string>? unknown = null)
    {
        var onSet = new HashSet<string>(on ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var playing = new HashSet<uint>((clips ?? new[] { "Idle1" }).Select(AnimationClipGraph.HashOf));
        var shown = new HashSet<uint>((visible ?? Array.Empty<string>()).Select(H));
        return new PersistentContext
        {
            PlayingClips = playing,
            IsOn = onSet.Contains,
            Resolve = s.Name,
            AnySubmeshVisible = hs => hs.Any(shown.Contains),
            OnUnknown = unknown,
        };
    }

    private static PersistentEffectCondition EntryWith(IReadOnlyList<PersistentEffectCondition> all, string effect) =>
        all.First(e => e.Vfxs.Any(v => v.EffectKey == H(effect)));

    private static IEnumerable<string> DefaultsOn(Skin s, IReadOnlyList<PersistentEffectCondition> all) =>
        PersistentConditions.GatesOf(all, s.Name).Where(g => g.DefaultOn).Select(g => g.Key);

    // ===================================================== the reader

    [Fact]
    public void Ahri97_the_orb_the_tail_fire_and_the_submesh_lists_are_read_in_order()
    {
        using var s = Real("Ahri", 97); if (s is null) return;
        var all = SkinPersistentEffects.Read(s.SkinBin);
        Assert.True(all.Count >= 6, "Ahri 97 authors at least six persistent entries (dump lines 338-535+)");

        var orb = EntryWith(all, "Ahri_Orb");
        var v = Assert.Single(orb.Vfxs);
        Assert.Equal("BUFFBONE_GLB_WEAPON_1", v.BoneName);

        var fire = EntryWith(all, "Ahri_Idle_fire");
        Assert.Equal(9, fire.Vfxs.Count);
        Assert.Equal("R_D_Tail10", fire.Vfxs[0].BoneName);
        Assert.Equal("L_D_Tail10", fire.Vfxs[8].BoneName);

        // the R entry hides "Fire"; another shows "Sunglasses" - submesh lists are kept as the bin's hashes
        Assert.Contains(all, e => e.SubmeshesToHide.Contains(H("Fire")));
        Assert.Contains(all, e => e.SubmeshesToShow.Contains(H("Sunglasses")));
        // every field the reader does not name is still there
        Assert.All(all, e => Assert.NotNull(e.Source));
    }

    [Fact]
    public void Aatrox33_turbines_and_the_camera_bound_record()
    {
        using var s = Real("Aatrox", 33); if (s is null) return;
        var all = SkinPersistentEffects.Read(s.SkinBin);
        var turbines = EntryWith(all, "Aatrox_Skin33_Idle_Banner_Turbines");
        Assert.Contains(turbines.Vfxs, v => v.BoneName == "R_Banner_VFX");
        Assert.Contains(turbines.Vfxs, v => v.BoneName == "L_Banner_VFX");

        var veins = EntryWith(all, "Aatrox_Skin33_R_CameraBoundVFX_Techno_Veins").Vfxs.Single(v => v.EffectKey == H("Aatrox_Skin33_R_CameraBoundVFX_Techno_Veins"));
        Assert.True(veins.AttachToCamera);
        Assert.True(veins.ShowToOwnerOnly);
        Assert.Equal("", veins.BoneName);
    }

    [Fact]
    public void Kaisa83_the_engine_glow_and_a_beam_with_a_target_bone()
    {
        using var s = Real("Kaisa", 83); if (s is null) return;
        var all = SkinPersistentEffects.Read(s.SkinBin);
        var engine = EntryWith(all, "Kaisa_Idle_engine");
        Assert.Equal("R_Engine_Upper", engine.Vfxs[0].BoneName);
        Assert.Equal("L_Engine_Upper", engine.Vfxs[1].BoneName);
        Assert.Contains(all.SelectMany(e => e.Vfxs), v => v.TargetBoneName == "L_Gun_Top");
        Assert.Contains(all.SelectMany(e => e.Vfxs), v => v.BoneName == "");   // Kaisa_E_active names no bone
        Assert.Contains(all, e => e.SubmeshesToShow.Contains(H("Kaisa_Q2_Mat")) && e.SubmeshesToHide.Contains(H("Kaisa_Q1_Mat")));
    }

    [Fact]
    public void An_unreadable_bin_is_an_empty_list() => Assert.Empty(SkinPersistentEffects.Read(new byte[] { 1, 2, 3 }));

    // ===================================================== the evaluator

    [Fact]
    public void Ahri97_the_orb_is_on_at_idle_through_its_cosmetic_buff_default_and_the_switch_turns_it_off()
    {
        using var s = Real("Ahri", 97); if (s is null) return;
        var all = SkinPersistentEffects.Read(s.SkinBin);
        var orb = EntryWith(all, "Ahri_Orb");
        var gates = PersistentConditions.GatesOf(all, s.Name);

        var idleBuff = gates.Single(g => g.Key == "HasBuff:AhriIdleOrbVFXBuff");
        Assert.True(idleBuff.DefaultOn);                                   // INFERRED default
        Assert.False(gates.Single(g => g.Key == "HasBuff:AhriR").DefaultOn);

        Assert.True(PersistentConditions.IsActive(orb, Ctx(s, DefaultsOn(s, all))));
        Assert.False(PersistentConditions.IsActive(orb, Ctx(s, on: Array.Empty<string>())));                       // the switch off
        Assert.False(PersistentConditions.IsActive(orb, Ctx(s, DefaultsOn(s, all), clips: new[] { "Recall" })));   // IsAnimationPlaying: the clip decides
        Assert.False(PersistentConditions.IsActive(orb, Ctx(s, DefaultsOn(s, all), clips: new[] { "Dance_Loop" })));
    }

    [Fact]
    public void Ahri97_the_tail_fire_reads_the_submeshes_and_the_clip_and_treats_the_champion_tag_as_true()
    {
        using var s = Real("Ahri", 97); if (s is null) return;
        var all = SkinPersistentEffects.Read(s.SkinBin);
        var fire = EntryWith(all, "Ahri_Idle_fire");
        var on = DefaultsOn(s, all).ToList();

        Assert.True(PersistentConditions.IsActive(fire, Ctx(s, on, visible: new[] { "Tails" })));              // small tails shown: Tail_Large not visible
        Assert.False(PersistentConditions.IsActive(fire, Ctx(s, on, visible: new[] { "Tail_Large" })));        // SubmeshVisibility: the big tail is out
        Assert.False(PersistentConditions.IsActive(fire, Ctx(s, on, clips: new[] { "Idle_In" }, visible: new[] { "Tails" })));
        Assert.False(PersistentConditions.IsActive(fire, Ctx(s, on.Append("HasBuff:AhriR"), visible: new[] { "Tails" })));   // Ahri's R
    }

    [Fact]
    public void Ahri97_the_R_entry_follows_its_switch_and_hides_the_fire_submesh()
    {
        using var s = Real("Ahri", 97); if (s is null) return;
        var all = SkinPersistentEffects.Read(s.SkinBin);
        var rEntry = all.First(e => e.SubmeshesToHide.Contains(H("Fire")));
        Assert.False(PersistentConditions.IsActive(rEntry, Ctx(s)));
        Assert.True(PersistentConditions.IsActive(rEntry, Ctx(s, on: new[] { "HasBuff:AhriR" })));
    }

    [Fact]
    public void Aatrox33_an_unknown_condition_is_OFF_and_reported_and_the_turbines_run_at_rest()
    {
        using var s = Real("Aatrox", 33); if (s is null) return;
        var all = SkinPersistentEffects.Read(s.SkinBin);
        var unknown = new List<string>();

        var water = EntryWith(all, "Aatrox_Skin33_Q_Cast_InWater");   // AllTrue { 0xf5821f8b {}, IsAnimationPlaying }
        Assert.False(PersistentConditions.IsActive(water, Ctx(s, clips: new[] { "Idle1" }, unknown: unknown.Add)));
        Assert.Contains("0xf5821f8b", unknown);

        var turbines = EntryWith(all, "Aatrox_Skin33_Idle_Banner_Turbines");
        Assert.True(PersistentConditions.IsActive(turbines, Ctx(s, DefaultsOn(s, all))));
        Assert.False(PersistentConditions.IsActive(turbines, Ctx(s, new[] { "HasBuff:AatroxBannerWings" })));   // Not(HasBuff ...)
    }

    [Fact]
    public void Kaisa83_the_engine_glow_runs_unless_the_shield_buff_is_on_and_buffs_otherwise_start_off()
    {
        using var s = Real("Kaisa", 83); if (s is null) return;
        var all = SkinPersistentEffects.Read(s.SkinBin);
        Assert.Empty(DefaultsOn(s, all));                                  // no "Idle" buff here: every switch starts off
        var engine = EntryWith(all, "Kaisa_Idle_engine");
        Assert.True(PersistentConditions.IsActive(engine, Ctx(s)));
        Assert.False(PersistentConditions.IsActive(engine, Ctx(s, new[] { "HasBuff:KaisaRShield" })));
        Assert.False(PersistentConditions.IsActive(EntryWith(all, "Kaisa_R_Shield_Buf"), Ctx(s)));
    }

    [Fact]
    public void Evelynn65_the_stealth_ring_eyes_stay_off_at_idle()
    {
        using var s = Real("Evelynn", 65); if (s is null) return;
        var all = SkinPersistentEffects.Read(s.SkinBin);
        Assert.Empty(DefaultsOn(s, all));
        Assert.All(all, e => Assert.False(PersistentConditions.IsActive(e, Ctx(s))));
    }

    // ===================================================== the idle cycle

    [Fact]
    public void Ahri97_the_real_graph_resolves_to_idle1_then_a_two_way_selector()
    {
        using var s = Real("Ahri", 97); if (s is null || s.Graph is null) return;
        var clips = ChampionAnimationData.ParseClips(s.Graph, s.Name, s.Path);
        var nodes = AnimationClipGraph.ParseNodes(s.Graph, s.Name);
        var steps = AnimationClipGraph.ResolveSteps("Idle1", clips, nodes)!;
        Assert.Equal(2, steps.Count);
        Assert.EndsWith("idle1.anm", steps[0].Clips[0].AnmPath);
        var choices = steps[1].Choices!;
        Assert.Equal(2, choices.Count);
        Assert.All(choices, c => Assert.Equal(50f, c.Probability));
        Assert.EndsWith("idle_var2.anm", choices[0].Clips[0].AnmPath);
    }

    private static IdleChoice Choice(float? p, string file) =>
        new(p, new[] { new AnimClipInfo(file, "x/" + file + ".anm", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>()) });

    [Fact]
    public void A_selector_picks_by_probability()
    {
        var weighted = new[] { Choice(90f, "a"), Choice(10f, "b") };
        Assert.Equal(0, AnimationClipGraph.PickChoice(weighted, 0.0));
        Assert.Equal(0, AnimationClipGraph.PickChoice(weighted, 0.899));
        Assert.Equal(1, AnimationClipGraph.PickChoice(weighted, 0.9));
        Assert.Equal(1, AnimationClipGraph.PickChoice(weighted, 0.9999));

        var unauthored = new[] { Choice(null, "a"), Choice(null, "b") };      // no probability = equal weights
        Assert.Equal(0, AnimationClipGraph.PickChoice(unauthored, 0.49));
        Assert.Equal(1, AnimationClipGraph.PickChoice(unauthored, 0.51));
        Assert.Equal(0, AnimationClipGraph.PickChoice(new[] { Choice(0f, "a"), Choice(0f, "b") }, 0.9));   // nothing weighs: the first
        Assert.Equal(1, AnimationClipGraph.PickChoice(new[] { Choice(0f, "a"), Choice(5f, "b") }, 0.0));   // a zero weight is never picked
        Assert.Equal(-1, AnimationClipGraph.PickChoice(Array.Empty<IdleChoice>(), 0.5));
    }

    private static AnimClipInfo Clip(string name, string file) =>
        new(name, "assets/x/animations/" + file, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>());

    private static MeshPreviewViewModel AhriLikeIdle(out string[] files)
    {
        files = new[] { "idle1.anm", "idle_var2.anm", "idle_var3.anm" };
        var clips = new[] { Clip("0x1efabc75", "idle1.anm"), Clip("Idle_Var2", "idle_var2.anm"), Clip("0xbb020a62", "idle_var3.anm") };
        uint Hash(string n) => AnimationClipGraph.HashOf(n);
        var nodes = new[]
        {
            new AnimClipNode("Idle1", Hash("Idle1"), AnimClipNodeKind.ConditionBool, "ConditionBoolClipData",
                new[] { new AnimClipChild(Hash("Idle1_Base"), 0f), new AnimClipChild(Hash("Spell4_ToIdle"), 1f) }),
            new AnimClipNode("Idle1_Base", Hash("Idle1_Base"), AnimClipNodeKind.Sequencer, "SequencerClipData",
                new[] { new AnimClipChild(Hash("0x1efabc75"), null), new AnimClipChild(Hash("Sel"), null) }),
            new AnimClipNode("Sel", Hash("Sel"), AnimClipNodeKind.Selector, "SelectorClipData",
                new[] { new AnimClipChild(Hash("Idle_Var2"), 50f), new AnimClipChild(Hash("0xbb020a62"), 50f) }),
        };
        var vm = new MeshPreviewViewModel();
        vm.Animation.SetSkeleton(10);
        vm.Animation.SetAnimations(files.Select(f => new AnimationEntryViewModel(new WadAssetEntry { Path = "a/" + f })));
        vm.SetSubmeshRules(Array.Empty<string>(), null, clips, clips, nodes);
        return vm;
    }

    private static List<string> CycleOf(int ends)
    {
        var vm = AhriLikeIdle(out _);
        Assert.True(vm.PlayLoadedIdle());
        var seen = new List<string> { vm.Animation.SelectedAnimation!.Name };
        for (int i = 0; i < ends; i++) { vm.Animation.ClipEnded!(); seen.Add(vm.Animation.SelectedAnimation!.Name); }
        return seen;
    }

    [Fact]
    public void The_idle_plays_the_base_clip_then_a_variant_then_the_base_clip_again_and_is_seeded()
    {
        var seen = CycleOf(8);
        Assert.Equal("idle1.anm", seen[0]);
        for (int i = 0; i < seen.Count; i++)
            if (i % 2 == 0) Assert.Equal("idle1.anm", seen[i]);
            else Assert.Contains(seen[i], new[] { "idle_var2.anm", "idle_var3.anm" });
        Assert.Contains("idle_var2.anm", seen.Concat(CycleOf(40)));            // both variants are reachable
        Assert.Contains("idle_var3.anm", seen.Concat(CycleOf(40)));
        Assert.Equal(CycleOf(12), CycleOf(12));                                 // deterministic: --simcheck must not depend on the dice
    }

    [Fact]
    public void A_cycle_clip_plays_once_where_a_single_idle_loops()
    {
        var vm = AhriLikeIdle(out _);
        Assert.True(vm.PlayLoadedIdle());
        Assert.False(vm.Animation.Loop);                                       // a cycle clip plays once; its end brings the next

        var plain = new MeshPreviewViewModel();
        var one = new[] { Clip("Idle1", "idle1.anm") };
        plain.Animation.SetSkeleton(10);
        plain.Animation.SetAnimations(new[] { new AnimationEntryViewModel(new WadAssetEntry { Path = "a/idle1.anm" }) });
        plain.SetSubmeshRules(Array.Empty<string>(), null, one, one, Array.Empty<AnimClipNode>());
        Assert.True(plain.PlayLoadedIdle());
        Assert.True(plain.Animation.Loop);                                     // M886 unchanged
    }

    // ===================================================== the Character window: persistent show/hide and the switches

    [Fact]
    public void Kaisa83_a_switch_swaps_the_Q_submeshes_and_the_default_state_leaves_the_base()
    {
        using var s = Real("Kaisa", 83); if (s is null) return;
        var vm = new MeshPreviewViewModel();
        foreach (var n in new[] { "Body", "Kaisa_Q1_Mat", "Kaisa_Q2_Mat" })
            vm.Submeshes.Add(new SubmeshToggleViewModel { Index = vm.Submeshes.Count, Name = n });
        vm.SetSubmeshRules(new[] { "Kaisa_Q2_Mat" }, null);
        vm.SetPersistentEffects(SkinPersistentEffects.Read(s.SkinBin), s.Name);

        Assert.True(vm.HasPersistentGates);
        Assert.Equal(new[] { true, true, false }, vm.Submeshes.Select(x => x.IsVisible));
        var q = vm.PersistentGates.Single(g => g.Key == "HasBuff:KaisaQEvolved");
        Assert.False(q.IsOn);
        q.IsOn = true;
        Assert.Equal(new[] { true, false, true }, vm.Submeshes.Select(x => x.IsVisible));
        q.IsOn = false;
        Assert.Equal(new[] { true, true, false }, vm.Submeshes.Select(x => x.IsVisible));
    }

    [Fact]
    public void A_user_tick_outranks_a_persistent_hide()
    {
        using var s = Real("Kaisa", 83); if (s is null) return;
        var vm = new MeshPreviewViewModel();
        foreach (var n in new[] { "Body", "Kaisa_Q1_Mat", "Kaisa_Q2_Mat" })
            vm.Submeshes.Add(new SubmeshToggleViewModel { Index = vm.Submeshes.Count, Name = n });
        vm.SetSubmeshRules(new[] { "Kaisa_Q2_Mat" }, null);
        vm.SetPersistentEffects(SkinPersistentEffects.Read(s.SkinBin), s.Name);
        vm.PersistentGates.Single(g => g.Key == "HasBuff:KaisaQEvolved").IsOn = true;
        vm.Submeshes[1].IsVisible = true;                                       // the user shows Q1 again
        Assert.True(vm.Submeshes[1].IsVisible);
    }

    // ===================================================== an interrupted clip's ranged visibility

    private static AnimClipInfo Ranged(string name, string file, int start, int end) =>
        new(name, "assets/x/animations/" + file, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>(),
            VisibilityEvents: new[] { new AnimVisibilityEvent(start, end, new[] { "Tail_Large" }, new[] { "Tails" }, Array.Empty<uint>(), Array.Empty<uint>()) });

    private static MeshPreviewViewModel AhriTails(out AnimationEntryViewModel intro, out AnimationEntryViewModel idle, out AnimationEntryViewModel run)
    {
        var vm = new MeshPreviewViewModel();
        foreach (var n in new[] { "Body", "Tails", "Tail_Large" })
            vm.Submeshes.Add(new SubmeshToggleViewModel { Index = vm.Submeshes.Count, Name = n });
        var clips = new[] { Ranged("Idle_In", "idle_in.anm", 0, 100), Clip("Idle1", "idle1.anm"), Clip("Run", "run.anm") };
        vm.Animation.SetSkeleton(10);
        vm.Animation.SetAnimations(new[] { "idle_in.anm", "idle1.anm", "run.anm" }.Select(f => new AnimationEntryViewModel(new WadAssetEntry { Path = "a/" + f })));
        vm.SetSubmeshRules(new[] { "Tail_Large" }, clips.ToDictionary(c => System.IO.Path.GetFileName(c.AnmPath)), clips, clips, Array.Empty<AnimClipNode>());
        intro = vm.Animation.Animations[0]; idle = vm.Animation.Animations[1]; run = vm.Animation.Animations[2];
        return vm;
    }

    [Fact]
    public void A_clip_cut_short_takes_back_the_ranged_event_it_was_inside()
    {
        var vm = AhriTails(out var intro, out _, out var run);
        vm.Animation.Loop = false;
        vm.Animation.SelectedAnimation = intro; vm.Animation.ClipChanged!(null);
        vm.Animation.Time = 50 / 30.0;                                           // frame 50 of the 0-100 window: big tail shown, small hidden
        Assert.Equal(new[] { true, false, true }, vm.Submeshes.Select(x => x.IsVisible));

        vm.Animation.SelectedAnimation = run; vm.Animation.ClipChanged!(null);   // a pick over it: the clip was cut
        Assert.Equal(new[] { true, true, false }, vm.Submeshes.Select(x => x.IsVisible));   // back to the skin base
    }

    [Fact]
    public void A_clip_that_ends_naturally_keeps_the_state_it_left()
    {
        var vm = AhriTails(out var intro, out _, out _);
        vm.Animation.Loop = false;
        vm.Animation.SelectedAnimation = intro; vm.Animation.ClipChanged!(null);
        vm.Animation.Time = 50 / 30.0;
        Assert.Equal(new[] { true, false, true }, vm.Submeshes.Select(x => x.IsVisible));

        // a real clip load reports ClipChanged inside the selection; this fixture has no .anm to load, so report it the same way
        vm.Animation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnimationInspectorViewModel.SelectedAnimation)) vm.Animation.ClipChanged!(null);
        };
        vm.Animation.ClipEnded!();                                               // ran out (the window end, frame 100, was not reached): M884 carry-over
        Assert.Equal("idle1.anm", vm.Animation.SelectedAnimation?.Name);
        Assert.Equal(new[] { true, false, true }, vm.Submeshes.Select(x => x.IsVisible));
    }
}

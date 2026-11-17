using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Characters.Simulation;
using ReyEngine.Formats.Skeletons;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M903: (1) the idle clip's own ParticleEventData plays as an idle layer (Annie skin 0's hand flame lives in RAW_Idle1, not in idleParticlesEffects);
/// (2) Brand's passive marks play on the enemy; (3) an eroding emitter takes no particleColorTexture gradient.
/// The Annie and Brand cases read the installed game (skipped with a message without it).
/// </summary>
public sealed class M903Tests(ITestOutputHelper output)
{
    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private static readonly string Champions = Path.Combine(Game, "DATA", "FINAL", "Champions");
    private static bool Installed => Directory.Exists(Champions);

    private static readonly Lazy<(HashDatabase Db, WadPathResolver Resolver)> Hashes = new(() =>
    {
        var db = new HashSyncService().LoadLocal(_ => { });
        return (db, new WadPathResolver(db));
    });

    // ---------------------------------------------------------------- 1. the idle layer

    private static readonly Lazy<WadArchive> AnnieWad = new(() => WadArchive.Open(Path.Combine(Champions, "Annie.wad.client"), Hashes.Value.Resolver));

    private sealed record AnnieSkin(Dictionary<uint, VfxSystemDefinition> Systems, Dictionary<uint, uint> Resources,
        IReadOnlyList<AnimClipInfo> Clips, IReadOnlyList<AnimClipNode> Nodes);

    private static AnnieSkin ReadAnnie()
    {
        var db = Hashes.Value.Db;
        var wad = AnnieWad.Value;
        var systems = new Dictionary<uint, VfxSystemDefinition>();
        var resources = new Dictionary<uint, uint>();
        var queue = new Queue<ulong>();
        var visited = new HashSet<ulong>();
        queue.Enqueue(HashAlgorithms.WadPath("data/characters/annie/skins/skin0.bin"));
        while (queue.TryDequeue(out var hash))
        {
            if (!visited.Add(hash) || !wad.TryGetEntry(hash, out _)) continue;
            var bytes = wad.Extract(hash);
            foreach (var (k, v) in VfxSystemResolver.ExtractAll(bytes)) systems.TryAdd(k, v);
            foreach (var (k, v) in VfxSystemResolver.ExtractResourceMap(bytes)) resources.TryAdd(k, v);
            foreach (var dep in VfxSystemResolver.ExtractDependencies(bytes)) queue.Enqueue(HashAlgorithms.WadPath(dep));
        }
        var anim = wad.Extract(HashAlgorithms.WadPath("data/characters/annie/animations/skin0.bin"));
        Func<uint, string?> name = h => db.TryGetBinName(h, out var n) ? n : null;
        var clips = ChampionAnimationData.ParseClips(anim, name, h => db.TryGetPath(h, out var p) ? p : null);
        return new AnnieSkin(systems, resources, clips, AnimationClipGraph.ParseNodes(anim, name));
    }

    private static string FileOf(AnimClipInfo c) => Path.GetFileName(c.AnmPath.Replace('\\', '/'));

    private static MeshPreviewViewModel LoadAnnie(AnnieSkin skin)
    {
        var vm = new MeshPreviewViewModel();
        vm.SetVfx(skin.Systems, skin.Resources);
        vm.Animation.SetSkeleton(60);
        // the real .anm of each clip, decoded on selection: ClipChanged only runs when a clip was loaded
        vm.Animation.ClipLoader = e => AnnieWad.Value.TryGetEntry(HashAlgorithms.WadPath(e.Path), out _)
            ? ReyEngine.Formats.Animation.AnimationDecoder.Decode(AnnieWad.Value.Extract(HashAlgorithms.WadPath(e.Path)), Path.GetFileName(e.Path)) : null;
        vm.Animation.SetAnimations(skin.Clips.GroupBy(FileOf, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(c => new AnimationEntryViewModel(new WadAssetEntry { Path = c.AnmPath.Replace((char)92, (char)47) })));
        var byAnm = new Dictionary<string, AnimClipInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in skin.Clips) byAnm.TryAdd(FileOf(c), c);
        vm.SetSubmeshRules(Array.Empty<string>(), byAnm, skin.Clips, skin.Clips, skin.Nodes);
        return vm;
    }

    private static bool IsFire(VfxPlaybackItem i) => i.System.Name.Contains("Idle_fire", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Annie0_the_idle_clips_fire_event_plays_on_her_hand_once()
    {
        if (!Installed) { output.WriteLine("SKIPPED: no game install"); return; }
        var vm = LoadAnnie(ReadAnnie());
        Assert.True(vm.PlayLoadedIdle());
        output.WriteLine("idle clip: " + vm.Animation.SelectedAnimation?.Name);

        var fire = Assert.Single(vm.Playback?.Items.Where(IsFire).ToList() ?? new List<VfxPlaybackItem>());
        Assert.Equal("Cstm_Buffbone_Idle_Fire", fire.AttachBone);
        Assert.True(vm.IdleEffectCount >= 1);
    }

    [Fact]
    public void Annie0_a_one_shot_fires_at_its_start_frame_and_a_looping_event_has_none()
    {
        if (!Installed) { output.WriteLine("SKIPPED: no game install"); return; }
        var vm = LoadAnnie(ReadAnnie());
        vm.PlayLoadedIdle();
        // walk the cycle: the clip with a start frame (RAW_Idle_Enter, frame 12) and the looping one (RAW_Idle1, no start frame)
        var delays = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 6; i++)
        {
            var fire = vm.Playback?.Items.FirstOrDefault(IsFire);
            if (fire is not null && vm.Animation.SelectedAnimation?.Name is { } n) delays[n] = fire.StartDelay;
            vm.Animation.ClipEnded!();
        }
        output.WriteLine(string.Join(", ", delays.Select(kv => kv.Key + "=" + kv.Value)));
        Assert.Contains(delays, kv => kv.Value > 0f);      // the Enter clip's event waits for frame 12
        Assert.Contains(delays, kv => kv.Value == 0f);     // the looping one starts with the clip
    }

    [Fact]
    public void Annie0_the_effect_is_never_listed_twice_with_a_record_and_goes_with_a_non_idle_clip()
    {
        if (!Installed) { output.WriteLine("SKIPPED: no game install"); return; }
        var vm = LoadAnnie(ReadAnnie());
        vm.PlayLoadedIdle();
        // the same effect on the same bone as an idle record (the debugger's injection): still one item
        vm.SetIdleEffects(new[] { new SkinIdleEffect(HashAlgorithms.Fnv1a("Annie_Idle_fire"), "Cstm_Buffbone_Idle_Fire") });
        Assert.Equal(1, vm.Playback!.Items.Count(IsFire));

        // M883 / M892: a clip the user picks is not an idle - the idle clip's own events go with it (the record's effect stays)
        var run = vm.Animation.Animations.FirstOrDefault(a => a.Name.Contains("run", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(run);
        vm.Animation.SelectedAnimation = run;
        Assert.True(vm.Playback!.Items.Count(IsFire) <= 1);
    }

    [Fact]
    public void Annie0_without_the_record_the_flame_goes_when_a_user_picks_another_clip()
    {
        if (!Installed) { output.WriteLine("SKIPPED: no game install"); return; }
        var vm = LoadAnnie(ReadAnnie());
        vm.PlayLoadedIdle();
        Assert.Equal(1, vm.Playback!.Items.Count(IsFire));
        var run = vm.Animation.Animations.First(a => a.Name.Contains("run", StringComparison.OrdinalIgnoreCase));
        vm.Animation.SelectedAnimation = run;
        Assert.Equal(0, vm.Playback?.Items.Count(IsFire) ?? 0);
    }

    // ---------------------------------------------------------------- 2. Brand's passive

    private static readonly string[] BrandP =
    {
        "Brand_Base_P_FireMark", "Brand_Base_P_FireMark2", "Brand_Base_P_FireMark3", "Brand_Base_P_redring", "Brand_Base_P_redring_Ignition_Child",
        "Brand_Base_P_Timer", "Brand_Base_P_Apoc_Detonate_big", "Brand_Base_P_Detonate_Audio", "Brand_Base_P_Stack1_Audio", "Brand_Base_P_Revolving_Audio",
    };

    [Theory]
    [InlineData("Brand_Base_P_FireMark", ChampionSystemTokens.Role.Target)]
    [InlineData("Brand_Base_P_FireMark2", ChampionSystemTokens.Role.Target)]
    [InlineData("Brand_Base_P_FireMark3", ChampionSystemTokens.Role.Target)]
    [InlineData("Brand_Base_P_redring", ChampionSystemTokens.Role.Target)]
    [InlineData("Brand_Base_P_Timer", ChampionSystemTokens.Role.Target)]
    [InlineData("Brand_Base_P_Apoc_Detonate_big", ChampionSystemTokens.Role.Target)]
    [InlineData("Brand_Base_P_Detonate_Audio", ChampionSystemTokens.Role.Caster)]     // a sound stays with the caster
    [InlineData("Brand_Base_P_Stack1_Audio", ChampionSystemTokens.Role.Caster)]
    [InlineData("Brand_Base_P_Revolving_Audio", ChampionSystemTokens.Role.Caster)]
    public void A_passives_marks_timer_ring_and_detonation_are_on_the_enemy(string name, ChampionSystemTokens.Role role)
    {
        var names = new HashSet<string>(BrandP, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(role, ChampionSystemTokens.Classify(name, passive: true, names));
    }

    [Fact]
    public void A_timer_without_a_mark_beside_it_and_a_self_buff_stay_on_the_caster()
    {
        var singed = new HashSet<string>(new[] { "Singed_Base_P_timer", "Singed_Base_P_Poison" }, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ChampionSystemTokens.Role.Caster, ChampionSystemTokens.Classify("Singed_Base_P_timer", passive: true, singed));
        Assert.Equal(ChampionSystemTokens.Role.Caster, ChampionSystemTokens.Classify("Evelynn_Base_P_Charm_buf", passive: true));
        Assert.Equal(ChampionSystemTokens.Role.Caster, ChampionSystemTokens.Classify("Kindred_Base_P_Mark_Sound_Self", passive: true));
        // and a non-passive spell is classified as before
        Assert.Equal(ChampionSystemTokens.Role.Target, ChampionSystemTokens.Classify("Brand_Base_W_tar"));
        Assert.Equal(ChampionSystemTokens.Role.Caster, ChampionSystemTokens.Classify("Brand_Base_Q_cast"));
    }

    private static ChampionSimDefinition Brand()
    {
        var ctx = new ChampionSimContext
        {
            GameVersion = "test",
            ResolveBinName = h => Hashes.Value.Db.TryGetBinName(h, out var n) ? n : null,
            ResolveWadPath = h => Hashes.Value.Db.TryGetPath(h, out var p) ? p : null,
        };
        using var wad = WadArchive.Open(ChampionSimBatch.WadPathOf(Champions, "Brand"), Hashes.Value.Resolver);
        return ChampionSimGenerator.Generate(wad, "Brand", ctx);
    }

    [Fact]
    public void Brand_P_the_marks_are_target_systems_and_the_script_plays_stack_1_2_3_then_the_detonation_on_the_enemy()
    {
        if (!Installed) { output.WriteLine("SKIPPED: no game install"); return; }
        var def = Brand();
        var p = Assert.Single(def.Abilities, a => a.Key == "P");
        var target = p.TargetSystems.Select(s => s.Name).ToList();
        var caster = p.CasterSystems.Select(s => s.Name).ToList();
        foreach (var n in new[] { "Brand_Base_P_FireMark", "Brand_Base_P_FireMark2", "Brand_Base_P_FireMark3", "Brand_Base_P_redring", "Brand_Base_P_Timer", "Brand_Base_P_Apoc_Detonate_big" })
        {
            Assert.Contains(n, target);
            Assert.DoesNotContain(n, caster);
        }

        var script = SimulationPlanner.Plan(def, p, new SimStage(Vector3.Zero, 0f, new Vector3(0f, 0f, 500f)));
        var fx = script.Steps.Where(s => s.Kind == SimStepKind.Effect).ToList();
        Assert.All(fx, s => Assert.Equal("enemy", s.Unit));
        float At(string name) => fx.Single(s => s.Resource == name).Time;
        Assert.True(At("Brand_Base_P_FireMark") < At("Brand_Base_P_FireMark2"));
        Assert.True(At("Brand_Base_P_FireMark2") < At("Brand_Base_P_FireMark3"));
        Assert.True(At("Brand_Base_P_FireMark3") < At("Brand_Base_P_Apoc_Detonate_big"));
        Assert.Equal(At("Brand_Base_P_FireMark3"), At("Brand_Base_P_Timer"), 3);
        // the composite does not play them again at the cast
        var composite = script.Find(SimStepKind.Composite)!;
        Assert.Contains("Brand_Base_P_FireMark2", composite.Exclude!);
        Assert.All(script.Steps.Where(s => s.Kind != SimStepKind.End), s => Assert.True(s.Time < script.EndTime));
    }

    // ---------------------------------------------------------------- 3. erosion

    private static VfxEmitterDefinition Emitter(string name, bool eroding) => new(
        Name: name, Rate: VfxCurveF.Const(40f), ParticleLifetime: VfxCurveF.Const(3f), EmitterLifetime: null, ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f, IsSingleParticle: false, Disabled: false, BlendMode: 1,
        BirthScale: VfxCurve3.Const(new Vector3(12f, 7f, 1f)), ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(new Vector4(0.9f, 0.7f, 0.5f, 1f)), ColorOverLife: null, BirthVelocity: null, Acceleration: null,
        BirthRotationalVelocity: null, EmitterPosition: VfxCurve3.Const(Vector3.Zero), TexturePath: "ASSETS/Test/plain.dds",
        TexDiv: new Vector2(1f, 1f), NumFrames: 1, RandomStartFrame: false, IsMeshPrimitive: false, UseTextureAspect: false, FrameRate: null,
        AlphaErosion: eroding ? new VfxAlphaErosion(null, new Vector4(1, 0, 0, 0), VfxCurveF.Const(0f), 0.5f, 0.1f, 0.1f) : null);

    [Fact]
    public void An_eroding_emitter_takes_no_color_gradient_and_a_plain_one_still_does()
    {
        var system = new VfxSystemDefinition(PathHash: 0xE0510Eu, Name: "erosion_test", ParticlePath: "assets/test/erosion.bin",
            Emitters: new[] { Emitter("eroding", true), Emitter("plain", false) });
        var px = new byte[4 * 4 * 4]; Array.Fill(px, (byte)200);
        var gradient = new TextureImage(4, 4, px);
        var item = new VfxPlaybackItem(system, Matrix4x4.Identity,
            EmitterTextures: new TextureImage?[] { null, null }, EmitterColorTextures: new TextureImage?[] { gradient, gradient });
        var sim = new VfxParticleSimulator(1);
        sim.SetSystem(item.System, item.Transform);
        VfxPlaybackSim.ApplySimulationAssets(sim, item);

        Assert.Null(sim.Emitters.Single(e => e.Def.Name == "eroding").ColorGradient);
        Assert.NotNull(sim.Emitters.Single(e => e.Def.Name == "plain").ColorGradient);
    }
}

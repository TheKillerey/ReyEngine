using System.Numerics;
using System.Reflection;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using ReyEngine.Rendering.Vfx;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M854: child systems. The simulator only SCHEDULES the children a parent particle asks for; the GL viewport drained that
/// queue and the D3D11 driver - the default backend - never did, so no child ever spawned there (37,047 emitters in 20,069
/// systems, the Jinx R rocket's child among them), and the Particle Editor never resolved children at all.
///
/// <para>The device test no-ops (not fails) when the game install, the hash dictionary, the shader cache or a D3D11 device
/// is absent.</para>
/// </summary>
public sealed class VfxChildSystemTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private readonly ITestOutputHelper _output;
    public VfxChildSystemTests(ITestOutputHelper output) => _output = output;

    private static VfxEmitterDefinition Emitter(string name, bool single, VfxChildParticleSet? children = null) => new(
        Name: name,
        Rate: VfxCurveF.Const(single ? 1f : 30f),
        ParticleLifetime: VfxCurveF.Const(5f),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: single,
        Disabled: false,
        BlendMode: 1,
        BirthScale: VfxCurve3.Const(new Vector3(60f)),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null,
        BirthVelocity: null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(new Vector3(0f, 60f, 0f)),
        TexturePath: "ASSETS/Test/" + name + ".dds",
        TexDiv: new Vector2(1f, 1f),
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: false,
        Children: children);

    private static VfxChildParticleSet ChildSet(params uint[] links)
        => new(links.Select(l => new VfxChildIdentifier(0, l)).ToArray(), EmitOnDeath: false, ExpectedCount: 1f, BonesToSpawnAt: Array.Empty<string>());

    private static VfxSystemDefinition System(uint hash, string name, params VfxEmitterDefinition[] emitters)
        => new(PathHash: hash, Name: name, ParticlePath: "", Emitters: emitters);

    private static readonly TextureImage White = new(4, 4, Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray());

    // ---------------------------------------------------------------- the shared resolver (what the Particle Editor now calls)

    private static VfxPlaybackItem Plain(VfxSystemDefinition def) => new(def, Vector3.Zero, new TextureImage?[def.Emitters.Count]);

    private static int DepthOf(VfxPlaybackItem item)
    {
        var kids = item.EmitterChildren?.Where(l => l is not null).SelectMany(l => l!).ToList();
        return kids is { Count: > 0 } ? 1 + kids.Max(DepthOf) : 0;
    }

    private static VfxPlaybackItem Build(IReadOnlyDictionary<uint, VfxSystemDefinition> systems, VfxSystemDefinition root, ISet<uint>? chain = null)
    {
        chain ??= new HashSet<uint>();
        VfxPlaybackItem Make(VfxSystemDefinition def, int depth) => Plain(def) with
        {
            EmitterChildren = VfxChildResolver.Resolve(def, depth, h => systems.GetValueOrDefault(h), _ => null, Make, chain),
        };
        return Make(root, 0);
    }

    [Fact]
    public void ChildrenAreResolvedFourLevelsDeepAndNoDeeper()
    {
        // 292 chains in the game go three or more levels deep; the old cap of 2 cut every one of them short
        var all = Enumerable.Range(1, 8).Select(i => System((uint)i, "s" + i, Emitter("e", true, i < 8 ? ChildSet((uint)(i + 1)) : null))).ToDictionary(s => s.PathHash);
        var root = Build(all, all[1]);
        Assert.Equal(VfxChildResolver.MaxChildDepth, DepthOf(root));
        Assert.Equal(4, VfxChildResolver.MaxChildDepth);
    }

    [Fact]
    public void AChainThatReachesBackToItsOwnParentIsCutAtTheAncestor()
    {
        // a -> b -> a: b's child is the ancestor a, which would only repeat the chain up to the cap
        var a = System(1, "a", Emitter("e", true, ChildSet(2)));
        var b = System(2, "b", Emitter("e", true, ChildSet(1)));
        var root = Build(new Dictionary<uint, VfxSystemDefinition> { [1] = a, [2] = b }, a);
        Assert.Equal(1, DepthOf(root));
        Assert.Null(root.EmitterChildren![0]![0].EmitterChildren);
    }

    [Fact]
    public void AChildNamedByEffectKeyIsFoundThroughTheResourceMap()
    {
        var child = System(20, "child", Emitter("e", true));
        var parent = System(10, "parent", Emitter("e", true, new VfxChildParticleSet(new[] { new VfxChildIdentifier(777, 0) }, false, 1f, Array.Empty<string>())));
        var items = VfxChildResolver.Resolve(parent, 0, h => h == 20 ? child : null, key => key == 777 ? 20u : null,
            (c, d) => Plain(c), new HashSet<uint>());
        Assert.NotNull(items);
        Assert.Same(child, items![0]![0].System);
    }

    // ---------------------------------------------------------------- the D3D11 driver

    [Fact]
    public void ADirect3D11TickSpawnsTheChildAParentParticleQueued()
    {
        if (!Directory.Exists(Final) || Database() is not { } db) return;
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(db), out _);
        if (cache is null) { _output.WriteLine("SKIPPED: no shader cache"); return; }
        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) { _output.WriteLine("SKIPPED: no D3D11 device"); return; }

        var childSystem = System(200, "child", Emitter("child_sparks", single: false));
        var parentSystem = System(100, "parent", Emitter("parent_body", single: true, ChildSet(200)));
        var childItem = new VfxPlaybackItem(childSystem, Vector3.Zero, new TextureImage?[] { White });
        var parentItem = new VfxPlaybackItem(parentSystem, Vector3.Zero, new TextureImage?[] { White },
            emitterChildren: new IReadOnlyList<VfxPlaybackItem>?[] { new[] { childItem } });

        var driver = new D3D11MapParticles(renderer, cache);
        ((ParticleWarmupQueue)typeof(D3D11MapParticles).GetField("_warmup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(driver)!).BudgetMs = 1e9;
        var eye = new Vector3(0f, 520f, 950f);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, 60f, 0f), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, 5f, 30000f);
        var mirrored = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;

        driver.SetPlayback(new VfxPlayback(new[] { parentItem }));
        driver.Tick(1f / 30f, mirrored, mirrored * proj, eye, 950f);   // builds, warms and starts the parent
        Assert.True(driver.HasPlayback);
        for (int i = 0; i < 8; i++) driver.Tick(1f / 30f, mirrored, mirrored * proj, eye, 950f);

        _output.WriteLine($"children {driver.ChildSystems}, live particles {driver.LiveParticles}, slices {driver.DrawSlices}");
        Assert.True(driver.ChildSystems >= 1, "no child system was spawned by the parent's particle");
        // the parent's one body particle plus the child's stream (30 a second)
        Assert.True(driver.LiveParticles >= 2, $"only {driver.LiveParticles} particle(s) are live: the child's billboards were not packed");

        // a child is retired on a timer and its emitter states leave the shared slices
        for (int i = 0; i < (int)(VfxPlaybackSim.ChildSimMaxAge * 30f) + 15; i++) driver.Tick(1f / 30f, mirrored, mirrored * proj, eye, 950f);
        Assert.True(driver.ChildSystems <= VfxPlaybackSim.MaxLiveChildSims);
        // ... and a child of the SAME definition that spawns after that retirement still draws (the parent's burst loops):
        // its billboard slice must not have died with the first child
        for (int i = 0; i < 40; i++) driver.Tick(1f / 30f, mirrored, mirrored * proj, eye, 950f);
        Assert.True(driver.ChildSystems >= 1, "no child after the first one was retired");
        Assert.True(driver.LiveParticles >= 2, $"a later child drew nothing ({driver.LiveParticles} live)");
        driver.SetPlayback(null);
        driver.Tick(0f, mirrored, mirrored * proj, eye, 950f);
        Assert.Equal(0, driver.ChildSystems);
    }

    [Fact]
    public void TheCycleToldAboutItsRestartLetsTheHostRetireItsChildren()
    {
        var sim = new VfxParticleSimulator(1);
        var def = System(1, "p", Emitter("e", single: true));
        sim.SetSystem(def, Matrix4x4.Identity);
        var item = Plain(def);
        var pairs = new[] { (item, sim) };
        int restarts = 0;
        var cycle = new VfxPreviewCycle { Restarted = () => restarts++ };
        sim.Stop();   // stopped with nothing alive: the auto-stop cycle restarts it
        sim.Reset(); sim.Stop();
        cycle.Tick(0.1f, null, stopped: false, autoStop: true, pairs, pairs);
        Assert.Equal(1, restarts);
    }

    private static HashDatabase? Database()
    {
        try { return new HashSyncService().LoadLocal(_ => { }); } catch { return null; }
    }
}

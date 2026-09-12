using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M708, found while porting the white-texture reading: a system's child particle systems are held per
/// AUTHORED emitter, and the loop that spawned them indexed that list by the SIMULATOR's own position.
///
/// <para>Those two are not the same list. <c>VfxParticleSimulator.SetSystem</c> drops every emitter
/// <c>IsVisual</c> refuses, and in the installed game 23,006 emitters name no texture at all - 12,845 of
/// them exist for nothing but spawning children. So the moment any emitter before a child-spawning one is
/// dropped, the spawning emitter reads a DIFFERENT emitter's child list and plays the wrong effect, or
/// runs off the end and plays none. The asset binder a few lines above it has remapped by reference since
/// M180; this loop was the copy that never did.</para>
/// </summary>
public sealed class ChildSpawnIndexTests
{
    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static VfxEmitterDefinition Emitter(string name, string? texture) => new(
        Name: name,
        Rate: VfxCurveF.Const(10f),
        ParticleLifetime: VfxCurveF.Const(1f),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: false,
        Disabled: false,
        BlendMode: 1,
        BirthScale: VfxCurve3.Const(new Vector3(10f, 10f, 10f)),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null,
        BirthVelocity: null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(Vector3.Zero),
        TexturePath: texture,
        TexDiv: new Vector2(1f, 1f),
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: false);

    [Fact]
    public void TheSimulatorsEmitterIndexIsNotTheAuthoredOne()
    {
        // [0] is a carrier: no texture, nothing drawable. Exactly the shape 12,845 emitters in the game
        // have, and exactly the shape the simulator refuses to run.
        var system = new VfxSystemDefinition(
            PathHash: 1, Name: "parent", ParticlePath: "",
            Emitters: new[]
            {
                Emitter("carrier", null),
                Emitter("spawner", "ASSETS/Test/a.dds"),
                Emitter("other", "ASSETS/Test/b.dds"),
            });

        var sim = new VfxParticleSimulator(1);
        sim.SetSystem(system, Matrix4x4.Identity);

        // the carrier is gone, so every later emitter has shifted down by one
        Assert.Equal(2, sim.Emitters.Count);
        Assert.Equal("spawner", sim.Emitters[0].Def.Name);
        Assert.Equal(1, VfxPlaybackSim.AuthoredIndex(system, sim.Emitters[0].Def));
        Assert.Equal(2, VfxPlaybackSim.AuthoredIndex(system, sim.Emitters[1].Def));

        // which is the whole bug: spawner sits at simulator index 0 and authored index 1, so a child list
        // indexed by the simulator's position would hand it the carrier's children
        Assert.NotEqual(0, VfxPlaybackSim.AuthoredIndex(system, sim.Emitters[0].Def));
    }

    [Fact]
    public void TheChildSpawnLoopRemapsToTheAuthoredIndex()
    {
        string? src = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        Assert.NotNull(src);
        int remap = src!.IndexOf("int authored = Services.VfxPlaybackSim.AuthoredIndex(item.System, sim.Emitters[e].Def);",
            StringComparison.Ordinal);
        Assert.True(remap > 0, "the child spawn loop does not remap to the authored emitter");
        Assert.Contains("if (perEmitter[authored] is not { } childItems", src);
        // and it no longer reads the list at the simulator's own position
        Assert.DoesNotContain("perEmitter[e]", src);
    }
}

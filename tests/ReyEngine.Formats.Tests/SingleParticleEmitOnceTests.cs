using System.Numerics;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>M880: an isSingleParticle emitter bursts once per system run. KSante_Base_W_Cas / shockwaves2 (single, no
/// emitterLifetime, particleLifetime 0.25) kept re-appearing because the simulator re-armed the burst.</summary>
public sealed class SingleParticleEmitOnceTests
{
    private static VfxEmitterDefinition Emitter(bool single, float particleLifetime, float rate) => new(
        Name: "e", Rate: VfxCurveF.Const(rate), ParticleLifetime: VfxCurveF.Const(particleLifetime),
        EmitterLifetime: null, ParticleLinger: 0f, TimeBeforeFirstEmission: 0f, IsSingleParticle: single,
        Disabled: false, BlendMode: 1, BirthScale: VfxCurve3.Const(Vector3.One), ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(Vector4.One), ColorOverLife: null, BirthVelocity: null, Acceleration: null,
        BirthRotationalVelocity: null, EmitterPosition: VfxCurve3.Const(Vector3.Zero),
        TexturePath: "ASSETS/Test/p.dds", TexDiv: new Vector2(1f, 1f), NumFrames: 1, RandomStartFrame: false,
        IsMeshPrimitive: false);

    private static VfxParticleSimulator Make(VfxEmitterDefinition e, bool once = true)
    {
        var sim = new VfxParticleSimulator(seed: 7) { EmitSingleOnce = once };
        sim.SetSystem(new VfxSystemDefinition(PathHash: 1, Name: "once", ParticlePath: "", Emitters: new[] { e }), Matrix4x4.Identity);
        return sim;
    }

    private static (int live, int born) Step(VfxParticleSimulator sim, float seconds)
    {
        int born = 0, last = sim.Emitters.Single().InstanceCount;
        for (float t = 0; t < seconds; t += 0.02f)
        {
            sim.Update(0.02f);
            int n = sim.Emitters.Single().InstanceCount;
            if (n > last) born += n - last;
            last = n;
        }
        return (last, born);
    }

    [Fact]
    public void SingleParticleWithNoEmitterLifetimeEmitsOnceAndDies()
    {
        var sim = Make(Emitter(single: true, particleLifetime: 0.25f, rate: 1f));
        var early = Step(sim, 0.1f);
        Assert.Equal(1, early.live);
        var late = Step(sim, 1.5f);
        Assert.Equal(0, late.live);
        Assert.Equal(0, late.born);
    }

    [Fact]
    public void MapModeKeepsTheOldRearmOfALifetimelessBurst()
    {
        var sim = Make(Emitter(single: true, particleLifetime: 0.25f, rate: 1f), once: false);
        Step(sim, 0.1f);
        Step(sim, 0.5f);
        Assert.True(Step(sim, 1f).born >= 1, "map mode must keep re-emitting the burst");
    }

    [Fact]
    public void ResetRearmsTheBurst()
    {
        var sim = Make(Emitter(single: true, particleLifetime: 0.25f, rate: 1f));
        Step(sim, 1f);
        sim.Reset();
        Assert.Equal(1, Step(sim, 0.1f).live);
    }

    [Fact]
    public void ContinuousEmitterWithNoEmitterLifetimeKeepsLooping()
    {
        var sim = Make(Emitter(single: false, particleLifetime: 0.25f, rate: 20f));
        Step(sim, 1f);
        var later = Step(sim, 1f);
        Assert.True(later.born > 5, "a continuous emitter must keep emitting");
        Assert.True(later.live > 0);
    }
}

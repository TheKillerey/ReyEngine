using System.Numerics;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M852: bindWeight. A missile's body (Ezreal R, the Jinx R rocket, Jhin R) is a particle bound to the emitter;
/// SetWorldTransform used to move only the spawn point, so the body stayed where the missile was cast while the
/// emitter flew to the target.
/// </summary>
public sealed class VfxBindWeightTests
{
    private static VfxEmitterDefinition Emitter(VfxEmitterExtras? extras) => new(
        Name: "body",
        Rate: VfxCurveF.Const(1f),
        ParticleLifetime: VfxCurveF.Const(10f),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: true,
        Disabled: false,
        BlendMode: 1,
        BirthScale: VfxCurve3.Const(new Vector3(10f)),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null,
        BirthVelocity: null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(new Vector3(100f, 0f, 0f)),
        TexturePath: "ASSETS/Test/p.dds",
        TexDiv: new Vector2(1f, 1f),
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: false,
        Extras: extras);

    private static Vector3 ParticleAfter(VfxEmitterExtras? extras, Matrix4x4 moved)
    {
        var sim = new VfxParticleSimulator(seed: 7);
        sim.SetSystem(new VfxSystemDefinition(PathHash: 1, Name: "bind", ParticlePath: "", Emitters: new[] { Emitter(extras) }), Matrix4x4.Identity);
        sim.Update(0.1f);
        sim.SetWorldTransform(moved);
        sim.Update(0.001f);
        var s = sim.Emitters.Single();
        Assert.Equal(1, s.InstanceCount);
        return new Vector3(s.Instances[0], s.Instances[1], s.Instances[2]);
    }

    private static readonly Matrix4x4 Move = Matrix4x4.CreateTranslation(1000f, 0f, 0f);

    [Fact]
    public void AnUnboundParticleStaysInTheWorld()
    {
        Assert.Equal(100f, ParticleAfter(null, Move).X, 2);
        Assert.Equal(100f, ParticleAfter(new VfxEmitterExtras { BindWeight = 0f }, Move).X, 2);
    }

    [Fact]
    public void AFullyBoundParticleTravelsWithItsEmitter()
    {
        var p = ParticleAfter(new VfxEmitterExtras { BindWeight = 1f, BindWeightCurve = VfxCurveF.Const(1f) }, Move);
        Assert.Equal(1100f, p.X, 2);
    }

    [Fact]
    public void APartialWeightFollowsPartOfTheMove()
    {
        var p = ParticleAfter(new VfxEmitterExtras { BindWeightCurve = VfxCurveF.Const(0.25f) }, Move);
        Assert.Equal(100f + 250f, p.X, 2);
    }

    [Fact]
    public void AnEmittersRotationCarriesABoundParticleAround()
    {
        // the emitter origin is the world origin, so a quarter turn about Y takes (100,0,0) to (0,0,-100)
        var p = ParticleAfter(new VfxEmitterExtras { BindWeightCurve = VfxCurveF.Const(1f) }, Matrix4x4.CreateRotationY(MathF.PI / 2f));
        Assert.Equal(0f, p.X, 2);
        Assert.Equal(-100f, p.Z, 2);
    }

    [Fact]
    public void AnAnimatedWeightIsSampledOverTheParticlesLife()
    {
        // weight 1 at birth falling to 0 at the end of its life: 0.1 s into a 10 s life it is still ~0.99
        var curve = new VfxCurveF(0f, new[] { 0f, 1f }, new[] { 1f, 0f });
        var p = ParticleAfter(new VfxEmitterExtras { BindWeightCurve = curve }, Move);
        Assert.InRange(p.X, 100f + 985f, 100f + 995f);
    }

    [Fact]
    public void BindWeightIsNoLongerParkedAndTheEditorDoesNotBadgeIt()
    {
        Assert.DoesNotContain("bindWeight", VfxParkedEmitterFields.Names);
        Assert.Null(VfxPreviewCoverage.IgnoredNote(ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a("bindWeight")));
    }
}

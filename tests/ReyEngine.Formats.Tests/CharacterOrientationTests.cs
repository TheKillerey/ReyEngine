using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M881: character playback orients particles as LTK Manager's VFX engine does. Expected values are LTK's formulas
/// (basis.ts standingInto / alongInto / flightInto, integrate.ts frameInto / turn) evaluated on fixed inputs.
/// Maps keep the old behaviour (the character: false runs).
/// </summary>
public sealed class CharacterOrientationTests
{
    private static VfxEmitterDefinition Emitter(VfxEmitterExtras? extras = null, Vector3? velocity = null, bool arbitrary = true,
        bool direction = false, float life = 2f, VfxCurve3? rotation0 = null, Vector3? position = null) => new(
        Name: "e",
        Rate: VfxCurveF.Const(1f),
        ParticleLifetime: VfxCurveF.Const(life),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: true,
        Disabled: false,
        BlendMode: 1,
        BirthScale: VfxCurve3.Const(new Vector3(10f, 10f, 10f)),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null,
        BirthVelocity: velocity is { } v ? VfxCurve3.Const(v) : null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(position ?? Vector3.Zero),
        TexturePath: "ASSETS/Test/p.dds",
        TexDiv: new Vector2(1f, 1f),
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: false,
        RotationOverLife: rotation0,
        IsDirectionOriented: direction,
        IsArbitraryQuad: arbitrary,
        Extras: extras);

    private static VfxParticleSimulator.EmitterState Run(VfxEmitterDefinition e, Matrix4x4 placement, bool character = true, float seconds = 0.05f)
    {
        var sim = new VfxParticleSimulator(seed: 1234) { CharacterSemantics = character };
        sim.SetSystem(new VfxSystemDefinition(PathHash: 1, Name: "t", ParticlePath: "", Emitters: new[] { e }), placement);
        for (float t = 0; t < seconds - 1e-4f; t += 0.05f) sim.Update(0.05f);
        var s = sim.Emitters.Single();
        Assert.True(s.InstanceCount > 0, "no particle was emitted, so the check would be vacuous");
        return s;
    }

    private static Vector3 Euler(VfxParticleSimulator.EmitterState s) => new(s.Instances[15], s.Instances[16], s.Instances[17]);
    private static void Near(Vector3 expected, Vector3 actual, float tol = 1e-3f)
    {
        Assert.InRange(actual.X, expected.X - tol, expected.X + tol);
        Assert.InRange(actual.Y, expected.Y - tol, expected.Y + tol);
        Assert.InRange(actual.Z, expected.Z - tol, expected.Z + tol);
    }

    [Fact]
    public void StandingMatchesLtkAndEulerOfInvertsIt()
    {
        // basis.ts: yaw 90 about Y sends local +Z to world +X, local +X to world -Z
        Near(Vector3.UnitX, VfxOrientationMath.Apply(VfxOrientationMath.Standing(0, 90, 0), Vector3.UnitZ));
        Near(-Vector3.UnitZ, VfxOrientationMath.Apply(VfxOrientationMath.Standing(0, 90, 0), Vector3.UnitX));
        var b = VfxOrientationMath.Standing(20f, -40f, 70f);
        var e = VfxOrientationMath.EulerOf(b) * (180f / MathF.PI);
        Near(new Vector3(20f, -40f, 70f), e, 0.02f);
    }

    [Fact]
    public void RotationOverrideIsTheBirthFrameWithItsColumnsScaled()
    {
        var extras = new VfxEmitterExtras { RotationOverride = new Vector3(0f, 90f, 0f), ScaleOverride = new Vector3(2f, 1f, 1f) };
        var s = Run(Emitter(extras, velocity: new Vector3(1f, 0f, 0f)), Matrix4x4.CreateTranslation(1000f, 0f, 0f));
        Near(Vector3.UnitX, s.PlacementForward);                 // local Z -> world X
        Near(-Vector3.UnitZ, s.PlacementRight);                  // local X -> world -Z
        var moved = new Vector3(s.Instances[0], s.Instances[1], s.Instances[2]) - s.BasePos;
        Assert.True(moved.Z < -0.01f && MathF.Abs(moved.X) < 1e-3f, $"velocity along local X must go to world -Z, got {moved}");
        // the same emitter on a map keeps the unrotated frame
        var off = Run(Emitter(extras, velocity: new Vector3(1f, 0f, 0f)), Matrix4x4.CreateTranslation(1000f, 0f, 0f), character: false);
        Near(Vector3.UnitZ, off.PlacementForward);
    }

    [Fact]
    public void LocalOrientationTurnsTheFrameByTheHostAndNonLocalRemovesOnlyTheHost()
    {
        var host = Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(500f, 0f, 0f);
        var local = Run(Emitter(new VfxEmitterExtras { RotationOverride = new Vector3(0f, 90f, 0f), TranslationOverride = new Vector3(0f, 0f, 100f) }), host);
        // yaw(override(Z)) = yaw(+X) = -Z; translationOverride is turned by the host (+Z -> +X), never by the override
        Near(-Vector3.UnitZ, local.PlacementForward);
        Near(new Vector3(600f, 0f, 0f), local.BasePos);
        var nonLocal = Run(Emitter(new VfxEmitterExtras { IsLocalOrientation = false, RotationOverride = new Vector3(0f, 90f, 0f), TranslationOverride = new Vector3(0f, 0f, 100f) }), host);
        Near(Vector3.UnitX, nonLocal.PlacementForward);          // override stays, only the host turn is gone
        Near(new Vector3(500f, 0f, 100f), nonLocal.BasePos);     // position still follows the host
        var plain = Run(Emitter(new VfxEmitterExtras { IsLocalOrientation = false }), host);
        Near(Vector3.UnitZ, plain.PlacementForward);
    }

    [Fact]
    public void Rotation0IntegratesEveryAxisAtSixtyPerSecondOfRate()
    {
        // keyed, 1.5 deg per 1/60 s on Y for 1 s = 90 deg; a constant is a fixed angle of c * 60 * lifetime from birth
        var keyed = new VfxCurve3(new Vector3(0f, 1.5f, 0f), new[] { 0f, 1f }, new[] { new Vector3(0f, 1.5f, 0f), new Vector3(0f, 1.5f, 0f) });
        var on = new VfxEmitterExtras { IsRotationEnabled = true };
        var s = Run(Emitter(on, rotation0: keyed, life: 4f), Matrix4x4.Identity, seconds: 1f);
        Near(new Vector3(0f, MathF.PI / 2f, 0f), Euler(s), 0.02f);
        var fixedAngle = Run(Emitter(on, rotation0: VfxCurve3.Const(new Vector3(0f, 0f, 0.75f)), life: 2f), Matrix4x4.Identity);
        Assert.InRange(MathF.Abs(Euler(fixedAngle).Z), MathF.PI / 2f - 0.02f, MathF.PI / 2f + 0.02f);   // 0.75 * 60 * 2 = 90 deg
        var gated = Run(Emitter(null, rotation0: keyed, life: 4f), Matrix4x4.Identity, seconds: 1f);
        Near(Vector3.Zero, Euler(gated));                         // no isRotationEnabled -> no rotation0
    }

    [Fact]
    public void ADirectionOrientedArbitraryQuadAimsItsForwardAlongItsDrift()
    {
        var s = Run(Emitter(velocity: new Vector3(10f, 0f, 0f), direction: true), Matrix4x4.Identity);
        var e = Euler(s);
        var basis = VfxOrientationMath.Standing(e.X * 57.29578f, e.Y * 57.29578f, e.Z * 57.29578f);
        Near(Vector3.UnitX, VfxOrientationMath.Apply(basis, Vector3.UnitZ));
        // off: nothing aims
        Near(Vector3.Zero, Euler(Run(Emitter(velocity: new Vector3(10f, 0f, 0f), direction: true), Matrix4x4.Identity, character: false)));
    }

    [Fact]
    public void DirectionVelocityScaleStretchesTheCameraUpAndTheArbitrarySide()
    {
        var extras = new VfxEmitterExtras { DirectionVelocityScale = 0.1f, DirectionVelocityMinScale = 1f };
        var cam = Run(Emitter(extras, velocity: new Vector3(100f, 0f, 0f), arbitrary: false), Matrix4x4.Identity);
        var arb = Run(Emitter(extras, velocity: new Vector3(100f, 0f, 0f)), Matrix4x4.Identity);
        Assert.InRange(cam.Instances[4], 90f, 110f);   // 10 * (100 * 0.1)
        Assert.Equal(10f, cam.Instances[3], 3);
        Assert.InRange(arb.Instances[3], 90f, 110f);
        Assert.Equal(10f, arb.Instances[4], 3);
    }

    [Theory]
    [InlineData(0f, 0f, 1f)]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0.6f, 0.5f, -0.4f)]
    [InlineData(0f, 1f, 1f)]
    public void AMissileFliesWithItsLocalYAlongTheTravel(float x, float y, float z)
    {
        var dir = Vector3.Normalize(new Vector3(x, y, z));
        var b = VfxOrientationMath.Flight(dir);
        Near(dir, VfxOrientationMath.Apply(b, Vector3.UnitY));
        var item = new VfxPlaybackItem(new VfxSystemDefinition(1, "m", "", Array.Empty<VfxEmitterDefinition>()),
                Matrix4x4.CreateTranslation(100f, 0f, 100f), Array.Empty<ReyEngine.Core.Decoding.TextureImage?>())
            with { TravelTo = new Vector3(100f, 0f, 100f) + dir * 800f };
        var placed = MeshPreviewViewModel.CharacterItem(item);
        Assert.True(placed.CharacterSemantics && placed.EmitSingleOnce);
        Near(dir, Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, placed.Transform)));
        Assert.Equal(new Vector3(100f, 0f, 100f), placed.Transform.Translation);
    }
}

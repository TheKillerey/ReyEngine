using System.Numerics;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M853: a trail is one ribbon through the emitter's particles in birth order. Until then every particle carried
/// a ribbon of its own past positions, which is empty for a particle that does not move - the shape of every
/// missile trail in the game (ribbons=0 for Ezreal Q and R, Jinx R and W, Jhin R and Thresh Q).
/// </summary>
public sealed class VfxTrailStrandTests
{
    private const int Stride = VfxParticleRenderer.TrailStride;

    private static VfxEmitterDefinition TrailEmitter(VfxTrailDefinition trail, float rate = 10f, float lifetime = 10f, VfxEmitterExtras? extras = null) => new(
        Name: "trail",
        Rate: VfxCurveF.Const(rate),
        ParticleLifetime: VfxCurveF.Const(lifetime),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: false,
        Disabled: false,
        BlendMode: 1,
        BirthScale: VfxCurve3.Const(new Vector3(20f)),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null,
        BirthVelocity: null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(Vector3.Zero),
        TexturePath: "ASSETS/Test/trail.dds",
        TexDiv: new Vector2(1f, 1f),
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: false,
        Extras: extras,
        Trail: trail);

    private static (Vector3[] Points, float[] Half, Vector4[] Colour) Line(int n, float spacing)
        => (Enumerable.Range(0, n).Select(i => new Vector3(i * spacing, 0f, 0f)).ToArray(),
            Enumerable.Repeat(10f, n).ToArray(),
            Enumerable.Repeat(Vector4.One, n).ToArray());

    private static int Strand(float[] buf, VfxTrailDefinition trail, (Vector3[] P, float[] H, Vector4[] C) s)
        => VfxParticleRenderer.BuildTrailStrand(buf, 0, s.P, s.H, s.C, trail, arbitrary: false,
            Vector3.UnitY, Vector3.UnitZ, new Vector3(0f, 500f, 0f));

    [Fact]
    public void AStrandOfNPointsIsNMinusOneQuads()
    {
        var buf = new float[10 * 6 * Stride];
        int k = Strand(buf, new VfxTrailDefinition(500f, 0f, 0, 0), Line(6, 100f));
        Assert.Equal(5 * 6 * Stride, k);
    }

    [Fact]
    public void FewerThanTwoParticlesDrawNothing()
    {
        var buf = new float[64 * Stride];
        Assert.Equal(0, Strand(buf, new VfxTrailDefinition(500f, 0f, 1, 0), Line(1, 100f)));
        Assert.Equal(0, Strand(buf, new VfxTrailDefinition(500f, 0f, 1, 0), Line(0, 100f)));
    }

    [Fact]
    public void TheCutoffEndsTheWalkOnceThatLengthIsCovered()
    {
        // points 100 apart, newest first (mode 1): walked 100 and 200 are drawn, 300 is past the 250 cutoff
        var buf = new float[10 * 6 * Stride];
        int k = Strand(buf, new VfxTrailDefinition(500f, 250f, 0, 0), Line(8, 100f));
        Assert.Equal(2 * 6 * Stride, k);
    }

    [Fact]
    public void ModeOneOpensUAtTheNewestParticleAndModeTwoAtTheOldest()
    {
        var line = Line(10, 100f);   // index 0 is the oldest, at x = 0; the newest is at x = 900
        var newestFirst = new float[10 * 6 * Stride];
        var oldestFirst = new float[10 * 6 * Stride];
        int kNew = Strand(newestFirst, new VfxTrailDefinition(100f, 0f, 1, 0), line);
        int kOld = Strand(oldestFirst, new VfxTrailDefinition(100f, 0f, 2, 0), line);
        Assert.True(kNew > 0 && kOld > 0);
        // vertex 0 of the first quad: x then u (floats 0 and 3)
        Assert.Equal(900f, newestFirst[0], 1);
        Assert.Equal(0f, newestFirst[3], 4);
        Assert.Equal(0f, oldestFirst[0], 1);
        Assert.Equal(0f, oldestFirst[3], 4);
        // U is arc length over mBirthTilingSize.x, so it only grows along the walk and ends a little short of 900 / 100 (the box filter pulls the free end in)
        Assert.InRange(newestFirst[kNew - Stride + 3], 6.5f, 9.01f);
        Assert.InRange(oldestFirst[kOld - Stride + 3], 6.5f, 9.01f);
    }

    [Fact]
    public void EachPointTakesItsOwnWidthAndColour()
    {
        var line = Line(2, 100f);
        line.Half[0] = 4f; line.Half[1] = 12f;
        line.Colour[1] = new Vector4(1f, 0f, 0f, 0.5f);
        var buf = new float[6 * Stride];
        Strand(buf, new VfxTrailDefinition(500f, 0f, 0, 0), line);
        // vertices 0,1 sit on the first point of the walk (the newest, half 12) and are tinted by it; camera along +Y,
        // ribbon along X, so the sides lie along Z
        Assert.Equal(12f, MathF.Abs(buf[2]), 3);
        Assert.Equal(0.5f, buf[8], 3);
        // the next point of the walk is the oldest: half 4, white
        Assert.Equal(4f, MathF.Abs(buf[2 * Stride + 2]), 3);
        Assert.Equal(1f, buf[2 * Stride + 8], 3);
    }

    [Fact]
    public void AnEmitterWhoseParticlesDoNotMoveStillDrawsARibbon()
    {
        // The measured failure: particles born at a travelling emitter and left in the world. Each one is still, so a
        // per-particle history was empty for all of them; the strand through them is the trail.
        var trail = new VfxTrailDefinition(500f, 1000f, 1, 0);
        var sim = new VfxParticleSimulator(seed: 3);
        sim.SetSystem(new VfxSystemDefinition(PathHash: 1, Name: "trail", ParticlePath: "", Emitters: new[] { TrailEmitter(trail) }),
            Matrix4x4.Identity);
        for (int i = 0; i < 40; i++)
        {
            sim.SetWorldTransform(Matrix4x4.CreateTranslation(i * 20f, 0f, 0f));
            sim.Update(0.05f);
        }
        var state = sim.Emitters.Single();
        Assert.True(state.ParticleCount >= 10, "the emitter should have laid down particles");

        float[] buf = new float[16];
        int k = VfxParticleRenderer.BuildTrailRibbon(ref buf, state, new Vector3(0f, 800f, 0f));
        Assert.True(k >= 9 * 6 * Stride, $"expected a ribbon through {state.ParticleCount} particles, built {k} floats");
        Assert.Equal(0, k % (6 * Stride));
    }

    [Fact]
    public void MaxAddedPerFrameCapsTheSpawnsOfOneStep()
    {
        var capped = new VfxTrailDefinition(500f, 1000f, 1, 3);
        var open = new VfxTrailDefinition(500f, 1000f, 1, 0);
        int Spawned(VfxTrailDefinition t)
        {
            var sim = new VfxParticleSimulator(seed: 5);
            sim.SetSystem(new VfxSystemDefinition(PathHash: 1, Name: "trail", ParticlePath: "",
                Emitters: new[] { TrailEmitter(t, rate: 500f) }), Matrix4x4.Identity);
            sim.Update(0.05f);   // 25 particles' worth of one step, plus the first emission
            return sim.Emitters.Single().ParticleCount;
        }
        Assert.InRange(Spawned(open), 20, 30);
        Assert.Equal(3, Spawned(capped));
    }

    [Fact]
    public void ATrailsRateFollowsTheEmittersSpeedWhenItAuthorsRateByVelocity()
    {
        // Jinx W's GLOWTRAIL: rate 1 a second, rateByVelocityFunction (0.5, 0.5). The authored rate strings nothing;
        // the speed does - 400 u/s * 0.5 + 0.5 = 200 particles a second, a still emitter 0.5.
        var trail = new VfxTrailDefinition(500f, 1000f, 1, 0);
        var extras = new VfxEmitterExtras { RateByVelocityFunction = new Vector2(0.5f, 0.5f) };
        int Particles(float unitsPerStep)
        {
            var sim = new VfxParticleSimulator(seed: 9);
            sim.SetSystem(new VfxSystemDefinition(PathHash: 1, Name: "trail", ParticlePath: "",
                Emitters: new[] { TrailEmitter(trail, rate: 1f, extras: extras) }), Matrix4x4.Identity);
            for (int i = 0; i < 20; i++)
            {
                sim.SetWorldTransform(Matrix4x4.CreateTranslation(i * unitsPerStep, 0f, 0f));
                sim.Update(0.05f);
            }
            return sim.Emitters.Single().ParticleCount;
        }
        Assert.InRange(Particles(20f), 150, 250);   // 200/s over one second
        Assert.InRange(Particles(0f), 0, 2);
    }
}

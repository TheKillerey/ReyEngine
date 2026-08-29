using System.Numerics;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M595: two editor-only defects the author hit on Jade (Map453), both of which look like the DATA is
/// wrong when it is the preview that is.
///
/// <para><b>1. The flipbook invented a frame rate.</b> With no authored <c>frameRate</c> the simulator
/// spread one full pass of the atlas over the particle lifetime. On Jade's <c>ripples</c> emitter -
/// <c>numFrames</c> 4, <c>particleLifetime</c> 6 s, <c>frameRate</c> absent, <c>isRandomStartFrame</c>
/// true - that is one hard cell jump every 1.5 s, which the author reported as "like 1 fps instead of
/// 30". Absent means zero, not "some default": <c>.bin</c> omits any property equal to the class default,
/// and the two <c>Waterbugs</c> emitters in the SAME system author <c>frameRate = 30</c> explicitly, so
/// 30 is not it. A random start frame with no rate is a VARIANT atlas (<c>ripple_pieces32</c> is four
/// ripple shapes), so the cell is chosen at birth and held.</para>
///
/// <para><b>2. The warm-up did not cover the window it was asked for.</b> A fixed 1/15 s step under a
/// 150-step cap is 10 s of fill regardless of the argument. Jade's <c>lillypad</c> emitter authors
/// <c>rate</c> 0.05/s and <c>particleLifetime</c> 100 s - a steady state of 5 - so it came out of warm-up
/// with 0.5 of a particle accumulated and drew NOTHING. That is the "WaterBugs_Env is not showing"
/// report: the waterbugs were fine, the lilypads were the missing part.</para>
/// </summary>
public class ParticleFlipbookAndWarmupTests
{
    /// <summary>The instance-buffer stride and the slot the flipbook cell lands in, as
    /// <c>BuildInstances</c> writes them: pos(3) size(2) colour(4) rot(1) frame(1).</summary>
    private const int Stride = 19;
    private const int OffFrame = 10;

    private static VfxSystemDefinition One(string name, float rate, float lifetime, int numFrames,
        bool randomStart, float? frameRate, float startFrame = 0f, bool single = false) => new(
        PathHash: 1, Name: name, ParticlePath: "",
        Emitters: new[]
        {
            new VfxEmitterDefinition(
                Name: name,
                Rate: VfxCurveF.Const(rate),
                ParticleLifetime: VfxCurveF.Const(lifetime),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: single,
                Disabled: false,
                BlendMode: 1,
                BirthScale: VfxCurve3.Const(new Vector3(20f, 20f, 20f)),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: "ASSETS/Test/p.dds",
                TexDiv: new Vector2(2f, 2f),
                NumFrames: numFrames,
                RandomStartFrame: randomStart,
                IsMeshPrimitive: false,
                FrameRate: frameRate,
                StartFrame: startFrame),
        });

    private static List<float> FramesOverTime(VfxSystemDefinition system, float seconds)
    {
        var sim = new VfxParticleSimulator(seed: 1234);
        sim.SetSystem(system, Matrix4x4.Identity);
        var seen = new List<float>();
        for (float t = 0f; t < seconds; t += 1f / 30f)
        {
            sim.Update(1f / 30f);
            var e = sim.Emitters.First();
            if (e.InstanceCount > 0) seen.Add(e.Instances[OffFrame]);
        }
        return seen;
    }

    // ======================================================== 1. the invented frame rate

    /// <summary>Jade's ripples, to the authored numbers. Before M595 this produced 4 distinct cells over
    /// a 6 s particle life; now the cell a particle is born with is the cell it keeps.</summary>
    [Fact]
    public void WithNoAuthoredRateTheCellIsHeldForTheWholeParticleLife()
    {
        // A single-particle burst, so every sample is the SAME particle ageing. StartFrame 0 is
        // deterministic, and the old code would have walked it 0 -> 1 -> 2 across these 4 seconds.
        var frames = FramesOverTime(
            One("ripples", rate: 1f, lifetime: 6f, numFrames: 4, randomStart: false, frameRate: null, single: true),
            seconds: 4f);

        Assert.NotEmpty(frames);
        Assert.Single(frames.Distinct());
    }

    [Fact]
    public void AnAuthoredRateStillAnimatesTheFlipbook()
    {
        // The Waterbugs emitters in the same Jade system: numFrames 2, frameRate 30. Those must keep moving.
        var frames = FramesOverTime(
            One("waterbugs", rate: 1f, lifetime: 5f, numFrames: 2, randomStart: false, frameRate: 30f, single: true),
            seconds: 2f);

        Assert.Equal(2, frames.Distinct().Count());
    }

    [Fact]
    public void AHeldCellIsAlwaysARealCellOfTheAtlas()
    {
        // A held frame still indexes the grid, so it must stay inside [0, numFrames) and be a whole cell.
        for (int seed = 0; seed < 24; seed++)
        {
            var sim = new VfxParticleSimulator(seed);
            // Update clamps dt to 0.1 s, so the rate has to fill within that to have particles at all.
            sim.SetSystem(One("ripples", 200f, 6f, numFrames: 4, randomStart: true, frameRate: null), Matrix4x4.Identity);
            sim.Update(0.1f);

            var e = sim.Emitters.First();
            Assert.True(e.InstanceCount > 0);
            for (int i = 0; i < e.InstanceCount; i++)
            {
                float frame = e.Instances[i * Stride + OffFrame];
                Assert.InRange(frame, 0f, 3f);
                Assert.Equal(MathF.Floor(frame), frame);
            }
        }
    }

    [Fact]
    public void ASingleFrameEmitterIsUnaffected()
    {
        var frames = FramesOverTime(
            One("solid", rate: 1f, lifetime: 4f, numFrames: 1, randomStart: false, frameRate: null, single: true),
            seconds: 1f);

        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.Equal(0f, f));
    }

    // ======================================================== 2. the warm-up window

    /// <summary>The fill time is one particle lifetime past first emission - NOT the emitter whole run,
    /// and not the 30 s auto-stop cycle that used to stand in for it.</summary>
    [Fact]
    public void FillDurationCoversAParticleLifetimeAndIsNotCappedAtTheAutoStopCycle()
    {
        var sim = new VfxParticleSimulator(seed: 1234);
        sim.SetSystem(One("lillypad", rate: 0.05f, lifetime: 100f, numFrames: 4, randomStart: true, frameRate: null),
            Matrix4x4.Identity);

        Assert.Equal(100f, sim.FillDuration, 1);
        Assert.Equal(30f, sim.NaturalDuration, 1);   // the preview cycle is a different number and stays put
    }

    /// <summary>The reported symptom, reproduced by the numbers: 0.05 particles/s over a 100 s life is a
    /// steady state of 5 lilypads. A warm-up that only ever covered 10 s produced none at all.</summary>
    [Fact]
    public void PreWarmReachesTheSteadyStateOfASlowLongLivedEmitter()
    {
        var sim = new VfxParticleSimulator(seed: 1234);
        sim.SetSystem(One("lillypad", rate: 0.05f, lifetime: 100f, numFrames: 4, randomStart: true, frameRate: null),
            Matrix4x4.Identity);
        sim.PreWarm(sim.FillDuration);

        Assert.InRange(sim.LiveParticleCount, 4, 6);
    }

    [Fact]
    public void AFastEmitterKeepsTheResolutionItAlreadyHad()
    {
        // Anything that fits inside the old 10 s budget still steps at 1/15 s, so this change cannot have
        // moved the warm-up of a short system.
        var sim = new VfxParticleSimulator(seed: 1234);
        sim.SetSystem(One("bats", rate: 15f, lifetime: 10f, numFrames: 1, randomStart: false, frameRate: null),
            Matrix4x4.Identity);
        sim.PreWarm(sim.FillDuration);

        Assert.Equal(10f, sim.FillDuration, 1);
        Assert.InRange(sim.LiveParticleCount, 140, 152);   // authored steady state 150
    }

    [Fact]
    public void PreWarmIsStillBoundedForAnAbsurdRequest()
    {
        // The cost bound is the point of the cap; coverage must not be bought with unbounded work.
        var sim = new VfxParticleSimulator(seed: 1234);
        sim.SetSystem(One("slow", rate: 1f, lifetime: 5000f, numFrames: 1, randomStart: false, frameRate: null),
            Matrix4x4.Identity);

        Assert.Equal(150f, sim.FillDuration, 1);   // clamped
        sim.PreWarm(1e6f);                          // must return promptly rather than stepping forever
        Assert.True(sim.LiveParticleCount > 0);
    }

    [Fact]
    public void PreWarmOfNothingDoesNothing()
    {
        var sim = new VfxParticleSimulator(seed: 1234);
        sim.SetSystem(One("e", rate: 10f, lifetime: 2f, numFrames: 1, randomStart: false, frameRate: null),
            Matrix4x4.Identity);
        sim.PreWarm(0f);

        Assert.Equal(0, sim.LiveParticleCount);
    }
}

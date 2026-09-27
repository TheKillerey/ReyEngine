using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M783: <see cref="VfxPreviewCycle"/> is a device-free port of ViewportControl's M712 rig / M185 Stop /
/// M186 auto-stop state machine, so the D3D11 particle preview can run the exact same cycle without
/// depending on the GL control. Pinned here against real <see cref="VfxParticleSimulator"/> instances - no
/// renderer, no device, no <c>D3D11MapParticles</c> - because <c>ViewportControl</c> is an
/// <c>OpenGlControlBase</c> and cannot be constructed headlessly, so the semantics have to be provable
/// independent of it.
/// </summary>
public sealed class VfxPreviewCycleTests
{
    private static VfxEmitterDefinition Emitter(float particleLifetime = 0.3f, float? emitterLifetime = 0.2f) =>
        new(
            Name: "e", Rate: VfxCurveF.Const(10f), ParticleLifetime: VfxCurveF.Const(particleLifetime),
            EmitterLifetime: emitterLifetime, ParticleLinger: 0f, TimeBeforeFirstEmission: 0f,
            IsSingleParticle: false, Disabled: false, BlendMode: 1,
            BirthScale: VfxCurve3.Const(Vector3.One), ScaleOverLife: null,
            BirthColor: VfxCurve4.Const(Vector4.One), ColorOverLife: null,
            BirthVelocity: null, Acceleration: null, BirthRotationalVelocity: null,
            EmitterPosition: VfxCurve3.Const(Vector3.Zero),
            TexturePath: "ASSETS/Test/m783.dds", TexDiv: Vector2.One, NumFrames: 1, RandomStartFrame: false,
            IsMeshPrimitive: false);

    private static (VfxPlaybackItem Item, VfxParticleSimulator Sim) Placement(
        Vector3 worldPos, string? attachBone = null, Vector3? travelTo = null, float travelSeconds = 0f,
        float particleLifetime = 0.3f, float? emitterLifetime = 0.2f)
    {
        var system = new VfxSystemDefinition(0x783u, "cycle-probe", "test/m783",
            new[] { Emitter(particleLifetime, emitterLifetime) });
        var texture = new TextureImage(1, 1, new byte[] { 255, 255, 255, 255 });
        var item = new VfxPlaybackItem(system, worldPos, new TextureImage?[] { texture })
        {
            AttachBone = attachBone, TravelTo = travelTo, TravelSeconds = travelSeconds,
        };
        var sim = VfxPlaybackSim.Create(item);
        Assert.NotNull(sim);
        return (item, sim!);
    }

    private static List<(VfxPlaybackItem, VfxParticleSimulator)> One(VfxPlaybackItem item, VfxParticleSimulator sim)
        => new() { (item, sim) };

    // ===================================================== M712: the rig re-anchor

    [Fact]
    public void TheRigMovesAPlainPlacementToItsPose()
    {
        var (item, sim) = Placement(new Vector3(100f, 0f, 0f));
        var all = One(item, sim);
        var cycle = new VfxPreviewCycle();
        var rig = new VfxPreviewRig(VfxRigMode.Missile, Height: 0f, Distance: 1200f, Speed: 1600f);

        cycle.Tick(1f / 60f, rig, stopped: false, autoStop: false, all, all);

        float phase = rig.Phase(cycle.RigElapsed, sim.NaturalDuration);
        var expected = (rig.Pose(phase) * Matrix4x4.CreateTranslation(item.WorldPos)).Translation;
        // EmitterPosition is the constant zero vector, so BasePos IS the placement's translation.
        Assert.True(Vector3.Distance(expected, sim.Emitters[0].BasePos) < 1e-3f,
            $"expected {expected}, got {sim.Emitters[0].BasePos}");
    }

    [Fact]
    public void TheRigSkipsABoneAttachedPlacement()
    {
        var (item, sim) = Placement(new Vector3(50f, 0f, 0f), attachBone: "root");
        var all = One(item, sim);
        var cycle = new VfxPreviewCycle();
        var before = sim.Emitters[0].BasePos;

        cycle.Tick(1f / 60f, new VfxPreviewRig(VfxRigMode.Missile), stopped: false, autoStop: false, all, all);

        // Reanchor (a D3D11MapParticles concern, not exercised here) owns this placement instead - the
        // cycle must leave it exactly where SetSystem put it.
        Assert.Equal(before, sim.Emitters[0].BasePos);
    }

    [Fact]
    public void TheRigSkipsATravellingPlacement()
    {
        var (item, sim) = Placement(Vector3.Zero, travelTo: new Vector3(500f, 0f, 0f), travelSeconds: 1f);
        var all = One(item, sim);
        var cycle = new VfxPreviewCycle();
        var before = sim.Emitters[0].BasePos;

        cycle.Tick(1f / 60f, new VfxPreviewRig(VfxRigMode.Missile), stopped: false, autoStop: false, all, all);

        Assert.Equal(before, sim.Emitters[0].BasePos);
    }

    [Fact]
    public void APausedFrameDoesNotAdvanceTheRig()
    {
        var (item, sim) = Placement(new Vector3(100f, 0f, 0f));
        var all = One(item, sim);
        var cycle = new VfxPreviewCycle();

        cycle.Tick(0f, new VfxPreviewRig(VfxRigMode.Missile), stopped: false, autoStop: false, all, all);

        Assert.Equal(0f, cycle.RigElapsed);
    }

    [Fact]
    public void NoRigMeansNoReanchorAtAll()
    {
        var (item, sim) = Placement(new Vector3(100f, 0f, 0f));
        var all = One(item, sim);
        var cycle = new VfxPreviewCycle();
        var before = sim.Emitters[0].BasePos;

        cycle.Tick(1f / 60f, null, stopped: false, autoStop: false, all, all);

        Assert.Equal(before, sim.Emitters[0].BasePos);
        Assert.Equal(0f, cycle.RigElapsed);
    }

    // ===================================================== M185: manual Stop

    [Fact]
    public void StoppedStopsEverySimulatorEveryFrame()
    {
        var (item, sim) = Placement(Vector3.Zero);
        var all = One(item, sim);
        var cycle = new VfxPreviewCycle();

        Assert.False(sim.IsStopped);
        cycle.Tick(1f / 60f, null, stopped: true, autoStop: false, all, all);
        Assert.True(sim.IsStopped);
        // idempotent under repeated frames - a UI toggle held down does not re-trigger anything
        cycle.Tick(1f / 60f, null, stopped: true, autoStop: false, all, all);
        Assert.True(sim.IsStopped);
    }

    // ===================================================== M186: auto-stop

    [Fact]
    public void AutoStopStopsAfterOneNaturalCycleThenResetsOnceEmpty()
    {
        var (item, sim) = Placement(Vector3.Zero, particleLifetime: 0.1f, emitterLifetime: 0.1f);
        var all = One(item, sim);
        var cycle = new VfxPreviewCycle();
        float cycleLength = sim.NaturalDuration;   // floor-clamped to >= 0.5s

        const float dt = 1f / 30f;
        float t = 0f;
        while (t < cycleLength + 1f && !sim.IsStopped)
        {
            sim.Update(dt);   // the ordinary per-frame simulate step - particles must actually exist to die out
            cycle.Tick(dt, null, stopped: false, autoStop: true, all, all);
            t += dt;
        }
        Assert.True(sim.IsStopped, $"auto-stop should have fired within {cycleLength:0.00}s (reached t={t:0.00})");

        // its short particles are long gone by the time the cycle's own duration elapses, so the very next
        // tick should see zero live particles and restart the run
        cycle.Tick(dt, null, stopped: false, autoStop: true, all, all);
        Assert.Equal(0, sim.LiveParticleCount);
        Assert.False(sim.IsStopped, "auto-stop should have reset the simulator once every particle had gone");
        Assert.Equal(0f, cycle.AutoStopElapsed);
    }

    [Fact]
    public void AutoStopStandsDownUnderAReplayingRig()
    {
        var (item, sim) = Placement(Vector3.Zero);
        var all = One(item, sim);
        var cycle = new VfxPreviewCycle();
        var replayRig = new VfxPreviewRig(VfxRigMode.Burst, Replay: true);

        // a long run: if auto-stop were still live it would have fired well before this
        for (int i = 0; i < 120; i++) cycle.Tick(1f / 30f, replayRig, stopped: false, autoStop: true, all, all);

        Assert.False(sim.IsStopped, "Replay owns the cycle; auto-stop must not fight it for the same simulator");
    }

    [Fact]
    public void ManualStopWinsOverAutoStop()
    {
        var (item, sim) = Placement(Vector3.Zero);
        var all = One(item, sim);
        var cycle = new VfxPreviewCycle();

        cycle.Tick(1f / 30f, null, stopped: true, autoStop: true, all, all);
        Assert.True(sim.IsStopped);
        // held with zero live particles: manual Stop must not let auto-stop's restart run underneath it
        cycle.Tick(1f / 30f, null, stopped: true, autoStop: true, all, all);
        Assert.True(sim.IsStopped);
    }

    // ===================================================== Reset

    [Fact]
    public void ResetClearsAllThreePiecesOfState()
    {
        var (item, sim) = Placement(new Vector3(100f, 0f, 0f));
        var all = One(item, sim);
        var cycle = new VfxPreviewCycle();
        var rig = new VfxPreviewRig(VfxRigMode.Missile, StopMidRun: true);

        for (int i = 0; i < 60; i++) cycle.Tick(1f / 30f, rig, stopped: false, autoStop: true, all, all);
        Assert.True(cycle.RigElapsed > 0f);

        cycle.Reset();
        Assert.Equal(0f, cycle.RigElapsed);
        Assert.Equal(0f, cycle.AutoStopElapsed);

        // and the rig's own stop latch is gone too - a rebuilt playback (a fresh sim, in the real host)
        // must not be silently skipped by a HashSet still holding the OLD one. Re-run enough of the rig
        // that StopAt would fire again, and confirm it still can.
        sim.Reset();
        for (int i = 0; i < 60; i++) cycle.Tick(1f / 30f, rig, stopped: false, autoStop: false, all, all);
        Assert.True(sim.IsStopped, "a rig with StopMidRun should still be able to stop the simulator after Reset");
    }
}

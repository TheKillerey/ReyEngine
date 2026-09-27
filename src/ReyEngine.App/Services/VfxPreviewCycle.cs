using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.App.Services;

/// <summary>
/// M783: ViewportControl's M712 rig re-anchor, M185 manual Stop and M186 auto-stop cycle, ported as one
/// GL-independent state machine so the D3D11 particle preview (the Particle Editor's own viewport, and any
/// future D3D11 host that wants the same preview conventions) can run the identical semantics without
/// depending on <c>ViewportControl</c> - an <c>OpenGlControlBase</c> that cannot be reached from a
/// device-free path, and must not become one.
///
/// <para><b>A port, not a shared implementation.</b> ViewportControl.cs keeps its own copy of this logic,
/// field for field (<c>_rigElapsed</c>, <c>_rigStopped</c>, <c>_autoStopElapsed</c>). This class is read
/// against that copy as the spec and is not called by it; a future change to one has to be carried to the
/// other by hand, exactly as M712/M185/M186 were written once and are the ground truth here.</para>
///
/// <para><b>Device-free by construction</b>, like <see cref="VfxParticleSimulator"/> itself: no shader, no
/// mesh, no renderer handle - only the three pieces of clock state ViewportControl carries as fields, so
/// this is unit-testable without a D3D11 device or a GL context.</para>
/// </summary>
public sealed class VfxPreviewCycle
{
    private float _rigElapsed;
    private float _autoStopElapsed;
    private readonly HashSet<VfxParticleSimulator> _rigStopped = new(ReferenceEqualityComparer.Instance);

    /// <summary>How far into its run the rig is. Read by tests and by a status line; Tick's caller does
    /// not need it.</summary>
    public float RigElapsed => _rigElapsed;
    public float AutoStopElapsed => _autoStopElapsed;

    /// <summary>A rebuilt playback starts its run over - M186 and M712's own comments for the reset this
    /// mirrors ("a rebuilt playback starts its cycle over" / "starts its run over").</summary>
    public void Reset()
    {
        _rigElapsed = 0f;
        _autoStopElapsed = 0f;
        _rigStopped.Clear();
    }

    /// <summary>
    /// One tick of the cycle.
    /// </summary>
    /// <param name="dt">Seconds since the last tick; 0 while paused, exactly like ViewportControl's own dt.</param>
    /// <param name="rig">The active preview rig, or null to run neither the rig nor its mid-run stop.</param>
    /// <param name="stopped">M185: the manual Stop toggle. Wins over auto-stop, like ViewportControl's
    /// <c>if (ParticleStopped) ... else if (ParticleAutoStop) ...</c>.</param>
    /// <param name="autoStop">M186: loop as run -> stop -> linger -> restart. Stands down while a Replay
    /// rig owns the same cycle.</param>
    /// <param name="allItems">EVERY placement this playback holds, active or not - the rig re-anchors all
    /// of them regardless of any camera gate, exactly as ViewportControl's loop over its full
    /// item-to-simulator cache does (never the camera-active subset alone).</param>
    /// <param name="activeItems">The camera-active subset - identical to <paramref name="allItems"/> for any
    /// playback that does not cull by camera, which is every current caller of this class.
    /// <see cref="VfxParticleSimulator.NaturalDuration"/>, <c>Stop</c>, <c>Reset</c>, <c>IsStopped</c> and
    /// <c>LiveParticleCount</c> are all read from this list, never from <paramref name="allItems"/> -
    /// matching ViewportControl's own split between <c>_particleSimCache</c> (all) and <c>_particleSims</c>
    /// (active).</param>
    public void Tick(float dt, VfxPreviewRig? rig, bool stopped, bool autoStop,
        IReadOnlyList<(VfxPlaybackItem Item, VfxParticleSimulator Sim)> allItems,
        IReadOnlyList<(VfxPlaybackItem Item, VfxParticleSimulator Sim)> activeItems)
    {
        // M712: the rig. Carries the whole system along a path the file does not describe. A placement
        // travelling of its own accord (a bone attachment, a missile in flight) is left alone - those have
        // their own re-anchor, and rigging them on top would mean two owners of one transform.
        if (dt > 0f && rig is { } r && activeItems.Count > 0)
        {
            _rigElapsed += dt;
            float span = activeItems.Max(static a => a.Sim.NaturalDuration);
            float phase = r.Phase(_rigElapsed, span);
            foreach (var (item, sim) in allItems)
            {
                if (item.TravelTo is not null) continue;
                if (item.AttachBone is not null) continue;
                sim.SetWorldTransform(r.Pose(phase) * Matrix4x4.CreateTranslation(item.WorldPos));
            }
            // A missile stops emitting where it lands, and any rig can be asked to stop half way, so the
            // teardown is visible without holding Stop down. Once per run, not per frame.
            if (r.StopAt(span) is { } stopAt && phase >= stopAt)
                foreach (var (_, sim) in activeItems)
                    if (_rigStopped.Add(sim)) sim.Stop();
            // Replay owns the cycle when it is on, so auto-stop below stands down rather than restarting
            // the run underneath it at a different moment.
            if (r.Replay && _rigElapsed >= r.RunLength(span))
            {
                foreach (var (_, sim) in activeItems) sim.Reset();
                _rigStopped.Clear();
                _rigElapsed = 0f;
                _autoStopElapsed = 0f;
            }
        }

        // M185: the Stop action. Idempotent on the simulator side, so holding the toggle down every frame
        // does not re-trigger anything.
        if (stopped)
        {
            foreach (var (_, sim) in activeItems) sim.Stop();
        }
        // M186: the auto-stop cycle - run, stop, let the Linger curves play, restart when the last
        // particle has gone. The restart waits on the particle count rather than on a timer, so a long
        // linger window is never cut short. Manual Stop takes precedence (the branch above), and a Replay
        // rig owns the same job, so this stands down under one.
        else if (autoStop && rig is not { Replay: true } && activeItems.Count > 0)
        {
            bool anyStopped = activeItems.Any(static a => a.Sim.IsStopped);
            if (!anyStopped)
            {
                _autoStopElapsed += dt;
                float cycle = activeItems.Max(static a => a.Sim.NaturalDuration);
                if (_autoStopElapsed >= cycle)
                    foreach (var (_, sim) in activeItems) sim.Stop();
            }
            else if (activeItems.All(static a => a.Sim.LiveParticleCount == 0))
            {
                foreach (var (_, sim) in activeItems) sim.Reset();
                _autoStopElapsed = 0f;
            }
        }
    }
}

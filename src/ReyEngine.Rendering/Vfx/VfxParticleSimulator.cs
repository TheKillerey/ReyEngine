using System.Numerics;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Rendering.Vfx;

/// <summary>
/// CPU particle simulator (M36) — turns a parsed <see cref="VfxSystemDefinition"/> into live particles
/// each frame: emitters spawn at their <c>rate</c>, particles integrate velocity + acceleration, and
/// size/colour are driven by the birth values times their over-life multiplier curves. Deterministic-ish
/// (seeded RNG) and GL-free, so it can be unit-tested headlessly. One instance drives one placed system.
/// </summary>
public sealed class VfxParticleSimulator
{
    /// <summary>Per-emitter live state + drawable output. One batch renders with one texture/blend.</summary>
    public sealed class EmitterState
    {
        public required VfxEmitterDefinition Def { get; init; }
        public Vector3 BasePos;                 // world spawn origin (placement + emitterPosition)
        public Vector3 PlacementRight, PlacementUp, PlacementForward;
        /// <summary>M183 (2.5): the beam's two world-space endpoints, recomputed whenever the target or
        /// the placement changes. HasBeamEndpoints is false when the beam would be degenerate, which the
        /// renderer treats as "draw nothing" rather than drawing a zero-length ribbon.</summary>
        public Vector3 BeamSource, BeamTarget;
        public bool HasBeamEndpoints;
        public uint Texture;                    // GL handle for this emitter's sprite (0 = not uploaded/skip)
        public uint TextureMult;                // optional Riot multiplier/noise texture stage
        public uint DistortionTexture;          // normal map for screen-space heat haze/refraction
        public uint ErosionTexture;             // M174 (2.1): alpha-erosion dissolve map
        public uint PaletteTexture;             // M175 (2.6): gradient strip the sprite's RGB is remapped through
        public uint ReflectionCubemap;          // M181 (2.12): reflectionMapTexture, a real DDS cubemap
        // M68: CPU copy of particleColorTexture (RGBA8, top-left origin). When present, each particle's colour
        // is looked up from this 2-D colour-over-life gradient (U = age, V = per-particle variant) instead of
        // rendering white. Null = emitter has no colour texture (keeps its birthColor/color curve colour).
        public byte[]? ColorGradient;
        public int ColorGradientW, ColorGradientH;
        public float SpriteAspect = 1f;         // legacy scalar quads preserve one atlas cell's width/height
        internal float SpawnAccum;
        /// <summary>M174 (2.14): per-instance start jitter from HasVariableStartTime (3,973 emitters).
        /// Without it, every repeat of an effect fires in lockstep and reads as mechanical.</summary>
        internal float StartOffset;
        internal float Age;                     // emitter age (seconds)
        internal bool BurstDone;                // for isSingleParticle
        internal readonly List<Particle> Particles = new();

        /// <summary>M185 (2.15): mirrors the simulator's stopped flag so BuildInstances, which is static
        /// and only receives an EmitterState, can tell whether to use the linger curves.</summary>
        internal bool Stopped;

        internal bool IsLingering(in Particle p) => Stopped && p.LingerWindow > 0f;

        /// <summary>How many particles this emitter currently has alive.</summary>
        public int ParticleCount => Particles.Count;

        /// <summary>M180 (2.7): child systems this emitter's particles asked to spawn since the last
        /// drain. The simulator SCHEDULES these; it deliberately does not instantiate them, because a
        /// child system needs textures and meshes resolved and this class is GL-free by design.</summary>
        internal readonly List<ChildSpawn> PendingChildSpawns = new();

        /// <summary>Take and clear the child spawns queued since the last call.</summary>
        public IReadOnlyList<ChildSpawn> TakeChildSpawns()
        {
            if (PendingChildSpawns.Count == 0) return Array.Empty<ChildSpawn>();
            var copy = PendingChildSpawns.ToArray();
            PendingChildSpawns.Clear();
            return copy;
        }

        /// <summary>M177 (2.5): the ribbon points behind particle <paramref name="index"/>, oldest first.
        /// Empty for every non-trail emitter. Exposed because the ribbon is generated rather than
        /// authored, so it is the only way to inspect what a trail actually became.</summary>
        public ReadOnlySpan<Vector3> GetTrail(int index)
        {
            if (index < 0 || index >= Particles.Count) return default;
            var p = Particles[index];
            return p.History is { } h ? h.AsSpan(0, Math.Min(p.HistoryCount, h.Length)) : default;
        }

        /// <summary>Packed instance data for the renderer: 18 floats per particle:
        /// position, size, color, rotation/frame, age/velocity, and Euler rotation.</summary>
        public float[] Instances = System.Array.Empty<float>();
        public int InstanceCount;

        // M47: mesh-primitive emitters — GL handles set by VfxParticleRenderer.UploadEmitterMesh
        // (0 = billboard). The renderer draws the .scb/.sco geometry per particle instead of a quad.
        public uint MeshVao, MeshVbo, MeshEbo;
        public int MeshVertexCount, MeshIndexCount;
        public float[]? MeshInterleaved;      // cached pos3+uv2 stream, re-skinned per frame (M48)
        /// <summary>Emitter age in seconds — drives UV scroll + wing-flap animation time.</summary>
        public float EmitterAge => Age;
    }

    internal struct Particle
    {
        public Vector3 Pos, Vel, BirthAccel, BirthOrbitalVelocity, BirthDrag;
        public float Age, Life;
        public Vector2 BirthSize;   // absolute (birthScale0 xy)
        public Vector4 BirthColor;
        public Vector3 BirthRotation;
        public float Rot, RotVel;
        public float StartFrame, FrameRate;
        public float ColorRandom;   // M68: stable per-particle 0..1 roll for the colour-gradient variant axis

        /// <summary>M177 (2.5): where this particle has been, oldest first, for trail emitters only. Null
        /// on every other particle, so nothing is allocated for the 94% of emitters that have no ribbon.
        ///
        /// A reference field inside a struct looks odd, but it is what makes this work: Particle is copied
        /// in and out of the list by value on every update, and a reference member survives that copy
        /// while a growable collection per particle would not.</summary>
        public Vector3[]? History;
        public int HistoryCount;

        /// <summary>M185 (2.15): the age at which this particle entered the linger phase, and how long
        /// that phase lasts. LingerWindow &lt;= 0 means the particle is not lingering.</summary>
        public float LingerStart, LingerWindow;
    }

    /// <summary>M180 (2.7): one request to instantiate a child system, at a world position, from entry
    /// <see cref="ChildIndex"/> of the emitter's child set.</summary>
    public readonly record struct ChildSpawn(Vector3 Position, int ChildIndex, bool OnDeath);

    /// <summary>M180: a hard ceiling on child spawns queued per emitter per frame.
    ///
    /// A child set turns every parent particle into a whole VFX system, so an emitter running at a few
    /// hundred particles per second multiplies into hundreds of systems per second without one. This is a
    /// frame-rate guard, not a Riot behaviour, and it is applied at SCHEDULING time so a consumer that
    /// never drains the queue cannot grow it without bound either.</summary>
    private const int MaxChildSpawnsPerFrame = 16;

    /// <summary>M177: ribbon points per particle. Fixed rather than growable so a long-lived particle
    /// cannot creep upward in memory, and because the sampling below spaces points to span the authored
    /// cutoff length whatever the particle's speed - so more points would buy smoothness, not reach.</summary>
    internal const int TrailPoints = 48;

    public IReadOnlyList<EmitterState> Emitters => _emitters;
    private readonly List<EmitterState> _emitters = new();
    private readonly Random _rng;
    private Matrix4x4 _worldTransform = Matrix4x4.Identity;
    private Matrix4x4 _inverseWorldTransform = Matrix4x4.Identity;
    public int LiveParticleCount { get; private set; }

    // Emitters that never terminate loop forever; give a hard cap so a runaway rate can't explode memory.
    private const int MaxParticlesPerEmitter = 4000;

    /// <summary>M183 (2.5): where beams terminate. Null means no target is bound - see ResolveBeams for
    /// what a beam does then.</summary>
    private Vector3? _beamTarget;

    /// <summary>M183: bind the beam target (the M114 practice dummy in the model preview). Cheap and
    /// idempotent, so callers can push it every frame without tracking changes.</summary>
    public void SetBeamTarget(Vector3? worldTarget)
    {
        if (_beamTarget.HasValue == worldTarget.HasValue
            && (!worldTarget.HasValue || _beamTarget!.Value == worldTarget.Value)) return;
        _beamTarget = worldTarget;
        ResolveBeams();
    }

    /// <summary>M183 (2.5): turn each beam emitter's authored offsets plus the bound target into two world
    /// points.
    ///
    /// The offsets are LOCAL, so they compose through the placement basis with TransformNormal - not
    /// Vector3.Transform, which would add the placement translation a second time on top of BasePos.
    ///
    /// With NO target bound, the beam still has to go somewhere. Riot's own data shows the pattern: the
    /// `Augment_LaserEyes_Beam` family pairs a target-bound emitter carrying (0,0,20) with a sibling
    /// literally named `_Long_FakeDir` carrying (0,0,-1250) - i.e. when there is no target, the authored
    /// offset alone defines the far end. That is what the first branch does. Only when there is neither a
    /// target nor an offset does this fall back to a fixed length along the placement forward axis, and
    /// that length is an editor substitute, not a Riot value.</summary>
    private void ResolveBeams()
    {
        foreach (var s in _emitters)
        {
            if (s.Def.Beam is not { } beam) { s.HasBeamEndpoints = false; continue; }
            var source = s.BasePos + Vector3.TransformNormal(beam.SourceOffset, _worldTransform);
            var targetOffset = Vector3.TransformNormal(beam.TargetOffset, _worldTransform);

            Vector3 target;
            if (_beamTarget is { } bound) target = bound + targetOffset;
            else if (targetOffset.LengthSquared() > 1e-6f) target = source + targetOffset;
            else target = source - s.PlacementForward * FallbackBeamLength;

            float len = Vector3.Distance(source, target);
            // A zero-length beam has no direction to extrude around, and an absurd one is junk data
            // rather than a 100km laser - the same class of guard EffectiveCutoff applies to mCutoff's
            // 2^36 outlier.
            s.HasBeamEndpoints = len > 1e-3f && len < 100000f;
            s.BeamSource = source;
            s.BeamTarget = target;
        }
    }

    /// <summary>M183: how long a beam runs when nothing binds its far end and it authors no target offset.
    /// An editor substitute so the emitter is visible at all - NOT a Riot value.</summary>
    private const float FallbackBeamLength = 500f;

    /// <summary>M185 (2.15): set once <see cref="Stop"/> is called. The linger stage is triggered by an
    /// EXTERNAL stop, not by an emitter running out its own lifetime - see VfxLinger for the measurement
    /// that refutes the lifetime reading.</summary>
    private bool _stopped;

    /// <summary>M185 (2.15): stop emitting and begin the linger teardown.
    ///
    /// This is the event Riot's linger data has always been waiting for and that this simulator never
    /// had: M174 correctly observed that emitterLinger had nothing to extend, precisely because nothing
    /// ever hard-stopped an emitter. Every live particle now gets a death deadline, and while it counts
    /// down the emitter's Linger curve set drives it instead of the ordinary ones.
    ///
    /// Idempotent: stopping an already-stopped system does nothing, so a UI button can be mashed.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        foreach (var s in _emitters)
        {
            s.Stopped = true;
            var d = s.Def;
            for (int i = 0; i < s.Particles.Count; i++)
            {
                var p = s.Particles[i];
                p.LingerStart = p.Age;
                p.LingerWindow = LingerWindowFor(d, p);
                s.Particles[i] = p;
            }
        }
    }

    /// <summary>Is this system stopped and running out its linger phase?</summary>
    public bool IsStopped => _stopped;

    /// <summary>M185: how long a particle's linger phase lasts once the system stops.
    ///
    /// particleLinger is the authored window. It is NOT "seconds added to a particle's own lifetime" -
    /// that reading is arithmetically impossible for most of the corpus: the median
    /// particleLinger/particleLifetime ratio is 1.5, and among single-particle emitters carrying both,
    /// particleLinger exceeds particleLifetime 62.3% of the time and exceeds lifetime+particleLifetime
    /// 42.0% of the time. A per-particle tail would make the tail the dominant part of most effects.
    ///
    /// INFERRED: what to do for the 46.4% of Linger structs that ship no duration at all. The particle's
    /// remaining natural life is used, which is a no-op for immortal particles - and immortal particles
    /// are exactly the cohort whose authors DO always ship particleLinger, which is why the gap is
    /// survivable. A particle with neither a window nor a finite life dies at the stop instant, matching
    /// the 82.5% of the immortal cohort that carries no linger mechanism and is meant to pop.</summary>
    private static float LingerWindowFor(VfxEmitterDefinition d, in Particle p)
    {
        if (d.ParticleLinger > 0f) return d.ParticleLinger;
        if (float.IsPositiveInfinity(p.Life)) return 0f;
        return MathF.Max(0f, p.Life - p.Age);
    }

    public VfxParticleSimulator(int seed = 1234) => _rng = new Random(seed);

    /// <summary>Configure from a system placed at <paramref name="worldPos"/>. Only visual emitters are simulated.</summary>
    public void SetSystem(VfxSystemDefinition system, Vector3 worldPos, bool includeNonVisual = false)
        => SetSystem(system, Matrix4x4.CreateTranslation(worldPos), includeNonVisual);

    /// <summary>Configure a system with its complete authored placement transform.</summary>
    public void SetSystem(VfxSystemDefinition system, Matrix4x4 worldTransform, bool includeNonVisual = false)
    {
        _emitters.Clear();
        _worldTransform = worldTransform;
        if (!Matrix4x4.Invert(worldTransform, out _inverseWorldTransform))
            _inverseWorldTransform = Matrix4x4.Identity;
        foreach (var e in system.Emitters)
        {
            if (!includeNonVisual && !e.IsVisual) continue;
            _emitters.Add(new EmitterState
            {
                Def = e,
                // M174 (2.14): jitter the start when HasVariableStartTime is set. Spread over the
                // emitter's own period when it has one, otherwise over a second - the exact distribution
                // Riot uses is UNKNOWN, so this only has to break the lockstep, not match it.
                StartOffset = e.HasVariableStartTime
                    ? (float)_rng.NextDouble() * MathF.Max(0.1f, e.Period ?? 1f)
                    : 0f,
                BasePos = Vector3.Transform(e.EmitterPosition.Constant, worldTransform),
                PlacementRight = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, worldTransform), Vector3.UnitX),
                PlacementUp = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, worldTransform), Vector3.UnitY),
                PlacementForward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, worldTransform), Vector3.UnitZ),
            });
        }
        ResolveBeams();   // M183: endpoints depend on BasePos + the placement basis just computed
        Reset();
    }

    private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
        => value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;

    /// <summary>M86: move the whole system WITHOUT resetting live particles — used to follow an animated
    /// bone per frame (spawn origins and placement axes update; existing particles keep flying).</summary>
    public void SetWorldTransform(Matrix4x4 worldTransform)
    {
        _worldTransform = worldTransform;
        if (!Matrix4x4.Invert(worldTransform, out _inverseWorldTransform))
            _inverseWorldTransform = Matrix4x4.Identity;
        foreach (var s in _emitters)
        {
            s.BasePos = Vector3.Transform(s.Def.EmitterPosition.Constant, worldTransform);
            s.PlacementRight = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, worldTransform), Vector3.UnitX);
            s.PlacementUp = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, worldTransform), Vector3.UnitY);
            s.PlacementForward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, worldTransform), Vector3.UnitZ);
        }
        // M183: the beam endpoints hang off BasePos and the placement basis, both of which just moved -
        // M116 missiles re-anchor through here every frame, so a beam on a travelling system has to
        // follow rather than stay where it was first resolved.
        ResolveBeams();
    }

    public void Reset()
    {
        foreach (var s in _emitters) { s.Particles.Clear(); s.SpawnAccum = 0; s.Age = 0; s.BurstDone = false; s.InstanceCount = 0; s.Stopped = false; }
        LiveParticleCount = 0;
        _stopped = false;   // M185: replaying a stopped system starts it running again
    }

    /// <summary>Advance the whole system by <paramref name="dt"/> seconds and rebuild render instances.</summary>
    // M91: frame-accurate clip events — the sim exists from clip start but stays dormant until its
    // event's StartFrame time. Keeps playback rebuilds atomic (no mid-clip sim resets).
    private float _startDelay;
    public void SetStartDelay(float seconds) => _startDelay = MathF.Max(0f, seconds);

    public void Update(float dt)
    {
        if (dt <= 0f) return;
        if (_startDelay > 0f)
        {
            _startDelay -= dt;
            if (_startDelay > 0f) return;
            dt = MathF.Min(dt, -_startDelay);   // only the portion past the trigger point
            _startDelay = 0f;
            if (dt <= 0f) return;
        }
        dt = MathF.Min(dt, 0.1f);   // clamp big frame gaps so bursts don't teleport
        int live = 0;
        foreach (var s in _emitters)
        {
            UpdateEmitter(s, dt);
            BuildInstances(s);
            live += s.InstanceCount;
        }
        LiveParticleCount = live;
    }

    private void UpdateEmitter(EmitterState s, float dt)
    {
        var d = s.Def;
        s.Age += dt;

        // spawn
        // M174: emitterLinger (95,954 emitters) is PARSED but has no effect yet. It governs how long an
        // emitter survives after it stops emitting, and this simulator never hard-stops one - live
        // particles keep integrating until they age out regardless - so there is nothing for it to
        // extend. It becomes meaningful alongside the Linger shutdown stage (report 2.15), which needs a
        // second colour/scale/velocity curve set this model does not carry.
        // M185 (2.15): a stopped system emits nothing more; its live particles run out their linger.
        bool emitting = !_stopped
                        && s.Age >= s.StartOffset + d.TimeBeforeFirstEmission
                        && (d.EmitterLifetime is not { } life || s.Age <= s.StartOffset + d.TimeBeforeFirstEmission + life);

        // M174 (2.14): duty cycle. The emitter is active for TimeActiveDuringPeriod out of every Period
        // seconds. Phase is measured from first emission, so the first pulse starts immediately.
        if (emitting && d.Period is > 0f && d.TimeActiveDuringPeriod is { } active)
        {
            float phase = (s.Age - s.StartOffset - d.TimeBeforeFirstEmission) % d.Period.Value;
            if (phase >= active) emitting = false;
        }
        if (emitting)
        {
            if (d.IsSingleParticle)
            {
                if (!s.BurstDone) { Spawn(s); s.BurstDone = true; }
            }
            else
            {
                float emitterT = d.EmitterLifetime is > 0f
                    ? Math.Clamp((s.Age - d.TimeBeforeFirstEmission) / d.EmitterLifetime.Value, 0f, 1f)
                    : 0f;
                float rate = MathF.Max(0f, d.Rate.Sample(emitterT));
                s.SpawnAccum += rate * dt;
                while (s.SpawnAccum >= 1f && s.Particles.Count < MaxParticlesPerEmitter)
                {
                    Spawn(s);
                    s.SpawnAccum -= 1f;
                }
                if (s.Particles.Count >= MaxParticlesPerEmitter) s.SpawnAccum = 0f;
            }
        }

        // M177: spacing between ribbon points, so TrailPoints of them cover the authored cutoff, and the
        // per-frame budget of new points. An unauthored budget fills the whole ribbon in one frame, which
        // is the behaviour a teleporting particle needs; a budget of 0 would freeze the trail entirely.
        float trailStep = d.Trail is { } tr ? tr.EffectiveCutoff / (TrailPoints - 1) : 0f;
        int trailBudget = d.Trail is { } tb && tb.MaxAddedPerFrame > 0
            ? Math.Min(tb.MaxAddedPerFrame, TrailPoints)
            : TrailPoints;

        // integrate + cull
        for (int i = s.Particles.Count - 1; i >= 0; i--)
        {
            var p = s.Particles[i];
            p.Age += dt;
            // particleLinger controls shutdown retention in Riot; it does not extend every live particle.
            // M185 (2.15): once stopped, a particle dies at the end of its linger window rather than at
            // its natural lifetime - which for an immortal particle is the only death it will ever get.
            bool lingering = _stopped && p.LingerWindow > 0f;
            float lingerT = lingering ? Math.Clamp((p.Age - p.LingerStart) / p.LingerWindow, 0f, 1f) : 0f;
            bool lingerDone = _stopped && (p.LingerWindow <= 0f || lingerT >= 1f);
            if (lingerDone || p.Age >= p.Life)
            {
                if (d.Children is { EmitOnDeath: true } deathSet)
                    QueueChildSpawns(s, deathSet, p.Pos, onDeath: true);
                s.Particles.RemoveAt(i);
                continue;
            }
            float particleT = float.IsPositiveInfinity(p.Life) ? 0f : Math.Clamp(p.Age / p.Life, 0f, 1f);
            // The linger curves are normalised over the linger WINDOW, not over particle life - measured:
            // their maximum key time is 1.0 from p10 to p90, identical to the ordinary curves.
            var linger = lingering ? d.Linger : null;
            var worldAccel = linger?.KeyedAcceleration?.Sample(lingerT)
                             ?? d.Acceleration?.Sample(particleT) ?? Vector3.Zero;
            worldAccel = Vector3.TransformNormal(worldAccel, _worldTransform);
            p.Vel += (p.BirthAccel + worldAccel) * dt;
            // M174 (1.9): `velocity` is a separate over-life curve from birthVelocity - 23,112 emitters
            // author it and it was being ignored entirely. Added rather than replacing, so an emitter
            // carrying both keeps its launch speed and gains the authored drift.
            if (linger?.KeyedVelocity is { } lingerVel)
                p.Vel += Vector3.TransformNormal(lingerVel.Sample(lingerT), _worldTransform) * dt;
            else if (d.VelocityOverLife is { } velCurve)
                p.Vel += Vector3.TransformNormal(velCurve.Sample(particleT), _worldTransform) * dt;
            var dragOverLife = linger?.KeyedDrag?.Sample(lingerT)
                               ?? d.DragOverLife?.Sample(particleT) ?? Vector3.Zero;
            var drag = Vector3.Max(Vector3.Zero, p.BirthDrag + dragOverLife);
            p.Vel *= new Vector3(MathF.Exp(-drag.X * dt), MathF.Exp(-drag.Y * dt), MathF.Exp(-drag.Z * dt));
            // M176 (2.4): force fields, before the position step so a field acts on this frame's movement
            // rather than a frame late. Four of the five adjust velocity; the orbital field rotates the
            // particle's position and heading directly - see ApplyForceFields for why.
            if (d.ForceFields is { } fields) ApplyForceFields(s, fields, ref p, dt);
            p.Pos += p.Vel * dt;
            if (p.BirthOrbitalVelocity.LengthSquared() > 1e-8f)
            {
                var localRelative = Vector3.TransformNormal(p.Pos - s.BasePos, _inverseWorldTransform);
                var angularStep = p.BirthOrbitalVelocity * dt;
                var orbit = Quaternion.CreateFromYawPitchRoll(angularStep.Y, angularStep.X, angularStep.Z);
                p.Pos = s.BasePos + Vector3.TransformNormal(Vector3.Transform(localRelative, orbit), _worldTransform);
            }
            // M177 (2.5): record where the ribbon has been, at EXACTLY trailStep spacing so that
            // TrailPoints of them span the authored cutoff.
            //
            // The spacing has to be exact, not "append the current position once the particle has moved
            // far enough". That cheaper version overshoots by however far the particle travelled in the
            // frame that crossed the threshold, which makes the ribbon's length depend on its SPEED:
            // measured, a 1,000-unit cutoff produced 1,018 units at 50 u/s and 1,567 at 2,000 u/s, and
            // every authored cutoff overshot. Walking the segment and inserting points at the exact
            // interval removes the frame rate and the speed from the result entirely.
            //
            // Inserting several points in one frame is also what mMaxAddedPerFrame is for - the field only
            // makes sense if the engine can add more than one, which is a small confirmation that this is
            // the right shape.
            if (p.History is { } hist && trailStep > 0f)
            {
                if (p.HistoryCount == 0)
                {
                    hist[0] = p.Pos;
                    p.HistoryCount = 1;
                }
                else
                {
                    for (int added = 0; added < trailBudget; added++)
                    {
                        var last = hist[p.HistoryCount - 1];
                        var delta = p.Pos - last;
                        float dist = delta.Length();
                        if (dist < trailStep) break;
                        var next = last + delta * (trailStep / dist);
                        if (p.HistoryCount < hist.Length) hist[p.HistoryCount++] = next;
                        else
                        {
                            // Full: drop the oldest point and slide the rest down. TrailPoints is small
                            // and this only runs once the ribbon is at full length, so the copy is cheap.
                            Array.Copy(hist, 1, hist, 0, hist.Length - 1);
                            hist[^1] = next;
                        }
                    }
                }
            }
            p.Rot += p.RotVel * dt;
            // M174 (1.9): rotation0 is an IntegratedValueVector3 - it accumulates rather than setting an
            // absolute angle, so it is integrated here alongside RotVel rather than sampled into Rot.
            // 52,647 emitters. Only Z drives the billboard spin; X/Y matter for mesh primitives, which
            // read BirthRotation, so they are left alone until that path grows over-life support.
            if (linger?.Rotation is { } lingerRot)
                p.Rot += lingerRot.Sample(lingerT).Z * (MathF.PI / 180f) * dt;
            else if (d.RotationOverLife is { } rotCurve)
                p.Rot += rotCurve.Sample(particleT).Z * (MathF.PI / 180f) * dt;
            s.Particles[i] = p;
        }

        // Infinite emitter with no live particles and a finished burst -> allow looping single particles.
        if (d.IsSingleParticle && s.BurstDone && s.Particles.Count == 0 && d.EmitterLifetime is null)
            s.BurstDone = false;
    }

    /// <summary>M180 (2.7): queue the child systems one parent event asks for.
    ///
    /// COUNT: childrenProbability is read as an expected count - floor(p) children plus one more with
    /// probability frac(p). See VfxChildParticleSet for why that reading, and not a 0..1 probability, is
    /// the one consistent with the measured value set.
    ///
    /// WHICH child: when a set names more than one identifier (255 of 10,608 measured sets do) one is
    /// chosen at random per spawn. That is INFERRED - the data gives a list and a count and says nothing
    /// about how they pair up - but a set of alternatives with a roll count is what the two fields
    /// together suggest, and 97.6% of sets name exactly one child so it rarely bites.</summary>
    private void QueueChildSpawns(EmitterState s, VfxChildParticleSet set, Vector3 pos, bool onDeath)
    {
        if (set.Children.Count == 0) return;
        int n = set.RollCount(_rng);
        for (int i = 0; i < n; i++)
        {
            if (s.PendingChildSpawns.Count >= MaxChildSpawnsPerFrame) return;
            int which = set.Children.Count == 1 ? 0 : _rng.Next(set.Children.Count);
            s.PendingChildSpawns.Add(new ChildSpawn(pos, which, onDeath));
        }
    }

    /// <summary>M176 (2.4): integrate Riot's five force-field types into a particle's velocity.
    ///
    /// Field positions are authored in the system's local space (the corpus is full of offsets like
    /// (0, 150, 0) - "a bit above the emitter"), so they are transformed by the placement the same way
    /// <see cref="EmitterState.BasePos"/> is.
    ///
    /// See <see cref="VfxForceFields"/> for what is measured and what is inferred. The short version:
    /// the data shapes are all measured off live bins, but the MATHS cannot be validated the way M175's
    /// shader stages were, because these are integrated on the CPU and leave no bytecode to decode.</summary>
    private void ApplyForceFields(EmitterState s, VfxForceFields f, ref Particle p, float dt)
    {
        // ---- uniform acceleration: gravity, wind, updraft ----
        // Nothing inferred here beyond world space, and isLocalSpace measured false on all 472 samples.
        foreach (var a in f.Acceleration)
            p.Vel += Vector3.TransformNormal(a.Acceleration, _worldTransform) * dt;

        // ---- attraction / repulsion ----
        foreach (var at in f.Attraction)
        {
            if (MathF.Abs(at.Acceleration) < 1e-6f) continue;
            var centre = Vector3.Transform(at.Position, _worldTransform);
            var toCentre = centre - p.Pos;
            float dist = toCentre.Length();
            if (dist < 1e-4f) continue;
            float falloff = Falloff(dist, at.Radius);
            if (falloff <= 0f) continue;
            // Acceleration is SIGNED: negative values push away instead of pulling in.
            p.Vel += (toCentre / dist) * at.Acceleration * falloff * dt;
        }

        // ---- localised drag ----
        foreach (var dr in f.Drag)
        {
            if (dr.Strength <= 0f) continue;
            float falloff = Falloff(Vector3.Distance(Vector3.Transform(dr.Position, _worldTransform), p.Pos), dr.Radius);
            if (falloff <= 0f) continue;
            // Same exponential form the birthDrag path above uses, so the two compose rather than fight.
            p.Vel *= MathF.Exp(-dr.Strength * falloff * dt);
        }

        // ---- orbital: spin about the emitter origin ----
        //
        // This ROTATES the particle rather than pushing it, which is the one thing an orbital field must
        // not get wrong. Converting the rotation into a velocity contribution (v += omega x r) looks
        // tempting because it would compose with the other fields, but velocity PERSISTS across frames:
        // adding the orbital term every step accumulates it, and the particle spirals away under runaway
        // acceleration instead of going round. It still "curves the path", so a test that only asks
        // whether the trajectory bent would pass while the behaviour was badly wrong.
        //
        // Position and velocity are rotated together, so a particle that also has linear motion keeps its
        // heading relative to the orbit instead of being torn out of it. This matches how the existing
        // per-particle birthOrbitalVelocity path works.
        foreach (var o in f.Orbital)
        {
            var step = o.Direction * dt;
            if (step.LengthSquared() < 1e-12f) continue;
            var orbit = Quaternion.CreateFromYawPitchRoll(step.Y, step.X, step.Z);
            var localRelative = Vector3.TransformNormal(p.Pos - s.BasePos, _inverseWorldTransform);
            p.Pos = s.BasePos + Vector3.TransformNormal(Vector3.Transform(localRelative, orbit), _worldTransform);
            var localVel = Vector3.TransformNormal(p.Vel, _inverseWorldTransform);
            p.Vel = Vector3.TransformNormal(Vector3.Transform(localVel, orbit), _worldTransform);
        }

        // ---- noise / turbulence ----
        foreach (var n in f.Noise)
        {
            if (n.VelocityDelta <= 0f) continue;
            float falloff = Falloff(Vector3.Distance(Vector3.Transform(n.Position, _worldTransform), p.Pos), n.Radius);
            if (falloff <= 0f) continue;
            // frequency read as a spatial WAVELENGTH in world units - see VfxNoiseField for why the
            // multiplier reading cannot be right. The time term makes the field evolve instead of being a
            // fixed pattern a particle simply flies through.
            float wavelength = MathF.Max(MathF.Abs(n.Frequency), 1e-3f);
            var q = p.Pos / wavelength + new Vector3(s.Age * 0.5f, 0f, 0f);
            var turbulence = new Vector3(Noise3(q, 0), Noise3(q, 1), Noise3(q, 2));
            p.Vel += turbulence * n.AxisFraction * n.VelocityDelta * falloff * dt;
        }

        if (!float.IsFinite(p.Vel.X) || !float.IsFinite(p.Vel.Y) || !float.IsFinite(p.Vel.Z))
            p.Vel = Vector3.Zero;
    }

    /// <summary>Radial weight for a field with a finite radius. INFERRED - the falloff SHAPE is not
    /// recoverable from the data. Linear-to-zero is used because a hard cut at the radius makes particles
    /// visibly jerk as they cross the boundary, and every authored radius would then be a seam. A radius
    /// of zero or less is treated as unbounded, which is what the acceleration field (no radius at all)
    /// already does.</summary>
    private static float Falloff(float distance, float radius)
    {
        if (radius <= 0f) return 1f;
        return distance >= radius ? 0f : 1f - distance / radius;
    }

    /// <summary>Deterministic 3-D value noise in [-1, 1]. Small and seeded rather than a library import:
    /// the simulator is meant to stay GL-free and headlessly testable, and identical input must give
    /// identical output so a preview does not shimmer differently on every replay.</summary>
    private static float Noise3(Vector3 p, int seed)
    {
        int xi = (int)MathF.Floor(p.X), yi = (int)MathF.Floor(p.Y), zi = (int)MathF.Floor(p.Z);
        float xf = p.X - xi, yf = p.Y - yi, zf = p.Z - zi;
        // smoothstep the fractional part, so the field is continuous in its first derivative and the
        // motion reads as a swirl rather than a grid of kinks.
        float u = xf * xf * (3f - 2f * xf), v = yf * yf * (3f - 2f * yf), w = zf * zf * (3f - 2f * zf);
        float Lerp(float a, float b, float t) => a + (b - a) * t;
        float c00 = Lerp(Hash(xi, yi, zi, seed), Hash(xi + 1, yi, zi, seed), u);
        float c10 = Lerp(Hash(xi, yi + 1, zi, seed), Hash(xi + 1, yi + 1, zi, seed), u);
        float c01 = Lerp(Hash(xi, yi, zi + 1, seed), Hash(xi + 1, yi, zi + 1, seed), u);
        float c11 = Lerp(Hash(xi, yi + 1, zi + 1, seed), Hash(xi + 1, yi + 1, zi + 1, seed), u);
        return Lerp(Lerp(c00, c10, v), Lerp(c01, c11, v), w);
    }

    private static float Hash(int x, int y, int z, int seed)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + z * 1274126177 + seed * 982451653;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 8388607.5f - 1f;   // [-1, 1]
        }
    }

    private void Spawn(EmitterState s)
    {
        var d = s.Def;
        // M174 (2.14): per-spawn skip. 14 emitters author 1.0, which means "emit nothing at all" - they
        // currently render at full rate.
        if (d.ChanceToNotExist > 0f && _rng.NextDouble() < d.ChanceToNotExist) return;
        // M47: exact per-particle randomisation — Value* probability tables (VfxProbabilityTableData)
        // are rolled per particle when the data carries them; SampleBirth falls back to the constant.
        float sampledLife = d.ParticleLifetime.SampleBirth(_rng);
        float life = sampledLife < 0f ? float.PositiveInfinity : MathF.Max(0.05f, sampledLife);
        var birthScale = d.BirthScale.SampleBirth(_rng);
        var vel = d.BirthVelocity?.SampleBirth(_rng) ?? Vector3.Zero;
        var birthAccel = d.BirthAcceleration?.SampleBirth(_rng) ?? Vector3.Zero;
        var birthOrbitalVelocity = d.BirthOrbitalVelocity?.SampleBirth(_rng) ?? Vector3.Zero;
        var birthDrag = d.BirthDrag?.SampleBirth(_rng) ?? Vector3.Zero;
        var birthRotation = d.BirthRotation?.SampleBirth(_rng) ?? Vector3.Zero;
        var rotVel = d.BirthRotationalVelocity?.SampleBirth(_rng) ?? Vector3.Zero;

        // Riot spawn-shape and probability-table values are sampled independently for every particle.
        // Mesh primitives keep their authored orientation; their movement comes from the same data path.
        var localOffset = d.SpawnShape?.SampleOffset(_rng) ?? Vector3.Zero;
        // M174: emitterPosition carries probability tables on 37,220 of the 186,374 emitters that author
        // it (60-WAD sample), and its curve keys are all (0,0,0) on exactly those - so the scatter IS the
        // value. Rolled per particle here; the constant part is already baked into BasePos, so only the
        // per-particle deviation from it is added.
        var posSample = d.EmitterPosition.SampleBirth(_rng);
        localOffset += posSample - d.EmitterPosition.Constant;
        var worldOffset = Vector3.TransformNormal(localOffset, _worldTransform);
        vel = Vector3.TransformNormal(vel, _worldTransform);
        birthAccel = Vector3.TransformNormal(birthAccel, _worldTransform);

        s.Particles.Add(new Particle
        {
            Pos = s.BasePos + worldOffset,
            Vel = vel,
            BirthAccel = birthAccel,
            BirthOrbitalVelocity = birthOrbitalVelocity,
            BirthDrag = birthDrag,
            Age = 0f,
            Life = life,
            BirthSize = new Vector2(birthScale.X, birthScale.Y == 0 ? birthScale.X : birthScale.Y),
            BirthColor = d.BirthColor.SampleBirth(_rng),
            BirthRotation = birthRotation * (MathF.PI / 180f),
            Rot = d.IsMeshPrimitive ? 0f : birthRotation.X * (MathF.PI / 180f),
            RotVel = rotVel.X * (MathF.PI / 180f),
            StartFrame = d.RandomStartFrame && d.NumFrames > 1
                ? _rng.Next(d.NumFrames)
                : Math.Clamp(d.StartFrame, 0f, Math.Max(0, d.NumFrames - 1)),
            FrameRate = d.BirthFrameRate?.SampleBirth(_rng) ?? d.FrameRate ?? 0f,
            ColorRandom = (float)_rng.NextDouble(),
            // M177 (2.5): only trail emitters carry a ribbon buffer.
            History = d.Trail is not null ? new Vector3[TrailPoints] : null,
        });

        // M180 (2.7): children spawn at particle BIRTH unless childEmitOnDeath says otherwise. That
        // default is measured rather than assumed - the flag is Bool and true in all 178 instances that
        // carry it, so its ABSENCE is what means "at birth".
        if (d.Children is { EmitOnDeath: false } birthSet)
            QueueChildSpawns(s, birthSet, s.BasePos + worldOffset, onDeath: false);
    }

    private static void BuildInstances(EmitterState s)
    {
        var d = s.Def;
        int n = s.Particles.Count;
        // Sized from the renderer's stride, never a literal - see the comment on VfxParticleRenderer.Stride.
        int stride = VfxParticleRenderer.Stride;
        if (s.Instances.Length < n * stride) s.Instances = new float[Math.Max(n * stride, stride * 4)];
        var buf = s.Instances;
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            var p = s.Particles[i];
            float t = float.IsPositiveInfinity(p.Life) ? 0f : Math.Clamp(p.Age / p.Life, 0f, 1f);
            // M185 (2.15): during the linger window the shutdown curves REPLACE the over-life ones.
            // Measured shape: SeparateLingerColor starts at alpha median 1.0 and ends <= 0.001 in 87.2%
            // (a fade-out), LingerScale starts at median 1.0 and ends at median 0.01 (a shrink).
            bool lingering = s.IsLingering(p);
            float lt = lingering ? Math.Clamp((p.Age - p.LingerStart) / p.LingerWindow, 0f, 1f) : 0f;
            var lg = lingering ? d.Linger : null;

            var scaleMul = lg?.Scale?.Sample(lt) ?? d.ScaleOverLife?.Sample(t) ?? Vector3.One;
            var colMul = lg?.SeparateColor?.Sample(lt) ?? d.ColorOverLife?.Sample(t) ?? Vector4.One;
            var col = p.BirthColor * colMul;

            // M68: modulate by the particleColorTexture gradient. U is the colour-over-life axis (age); V is a
            // per-particle variant selector. This is what colours emitters that leave birthColor/color unset
            // (all the Jade fire/ember/glow systems) — without it they render white.
            if (s.ColorGradient is { } grad && s.ColorGradientW > 0 && s.ColorGradientH > 0)
            {
                float speed = p.Vel.Length();
                float u = ApplyLookupTransform(LookupCoord(d.ColorLookUpTypeX ?? 0, t, speed, p.ColorRandom),
                    d.ColorLookUpScale.X == 0f ? 1f : d.ColorLookUpScale.X, d.ColorLookUpOffset.X);
                // V: an explicit lookup type drives the variant axis; an absent field samples a stable centre
                // row (a complete valid gradient) rather than sweeping V diagonally with age.
                float v = d.ColorLookUpTypeY is { } ty
                    ? ApplyLookupTransform(LookupCoord(ty, t, speed, p.ColorRandom),
                        d.ColorLookUpScale.Y == 0f ? 1f : d.ColorLookUpScale.Y, d.ColorLookUpOffset.Y)
                    : 0.5f;
                col *= SampleGradient(grad, s.ColorGradientW, s.ColorGradientH, u, v);
            }

            float frame = 0f;
            if (d.NumFrames > 1)
                frame = MathF.Floor(p.FrameRate > 0f
                    ? (p.StartFrame + p.Age * p.FrameRate) % d.NumFrames
                    : (p.StartFrame + t * d.NumFrames) % d.NumFrames);

            buf[k++] = p.Pos.X; buf[k++] = p.Pos.Y; buf[k++] = p.Pos.Z;
            float sizeX = p.BirthSize.X * scaleMul.X;
            if (d.UseTextureAspect) sizeX *= s.SpriteAspect;
            buf[k++] = sizeX;
            buf[k++] = p.BirthSize.Y * scaleMul.Y;
            buf[k++] = col.X; buf[k++] = col.Y; buf[k++] = col.Z; buf[k++] = col.W;
            buf[k++] = p.Rot;
            buf[k++] = frame;
            buf[k++] = p.Age;
            buf[k++] = p.Vel.X; buf[k++] = p.Vel.Y; buf[k++] = p.Vel.Z;
            buf[k++] = p.Rot; buf[k++] = p.BirthRotation.Y; buf[k++] = p.BirthRotation.Z;
            // M174 (2.1): the erosion drive is ONE scalar per particle - Riot's quad path feeds it
            // through a vertex attribute (quad_vs: mov o3.z, v2.w), which is why the curve is
            // evaluated here on the CPU rather than in the shader.
            buf[k++] = d.AlphaErosion is { } ero ? ero.Drive.Sample(t) : 0f;
        }
        s.InstanceCount = n;
    }

    /// <summary>M68: map a Riot colorLookUpType to a 0..1 texture coordinate. 0 = particle life (age), the
    /// colour-over-life axis; 2/3 = a per-particle random variant; 1 = normalised speed. Unknown types fall
    /// back to age. Exact enum values 2/3 are approximate (both treated as a per-particle random variant),
    /// which is enough to colour the particle correctly instead of leaving it white.</summary>
    private static float LookupCoord(int type, float age, float speed, float random) => type switch
    {
        1 => Math.Clamp(speed / 400f, 0f, 1f),   // ByVelocity — 400 u/s reaches the ramp end (approx)
        2 or 3 => random,                        // per-particle random variant
        _ => age,                                // 0 (Life) and anything else: colour over life
    };

    /// <summary>M174 (1.7): apply the authored affine transform to a colour-lookup coordinate.
    /// colorLookUpScales (36,423 emitters) and colorLookUpOffsets (23,920) were both being ignored, so
    /// every gradient was sampled across its full width regardless of the sub-range the artist selected.
    /// Clamped after transform because the gradient sampler clamps anyway - doing it here keeps the two
    /// axes independent.</summary>
    private static float ApplyLookupTransform(float coord, float scale, float offset)
        => Math.Clamp(coord * scale + offset, 0f, 1f);

    /// <summary>Bilinear RGBA sample of a tightly-packed RGBA8 image (top-left origin), UV clamped to [0,1].</summary>
    private static Vector4 SampleGradient(byte[] rgba, int w, int h, float u, float v)
    {
        u = Math.Clamp(u, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);
        float fx = u * (w - 1), fy = v * (h - 1);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = fx - x0, tyf = fy - y0;
        Vector4 c00 = Texel(rgba, w, x0, y0), c10 = Texel(rgba, w, x1, y0);
        Vector4 c01 = Texel(rgba, w, x0, y1), c11 = Texel(rgba, w, x1, y1);
        return Vector4.Lerp(Vector4.Lerp(c00, c10, tx), Vector4.Lerp(c01, c11, tx), tyf);
    }

    private static Vector4 Texel(byte[] rgba, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return new Vector4(rgba[i] / 255f, rgba[i + 1] / 255f, rgba[i + 2] / 255f, rgba[i + 3] / 255f);
    }
}

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
        public uint Texture;                    // GL handle for this emitter's sprite (0 = not uploaded/skip)
        internal float SpawnAccum;
        internal float Age;                     // emitter age (seconds)
        internal bool BurstDone;                // for isSingleParticle
        internal readonly List<Particle> Particles = new();

        /// <summary>Packed instance data for the renderer: 11 floats per particle
        /// [cx,cy,cz, sizeX,sizeY, r,g,b,a, rot, frame]. Rebuilt each Update.</summary>
        public float[] Instances = System.Array.Empty<float>();
        public int InstanceCount;

        // M47: mesh-primitive emitters — GL handles set by VfxParticleRenderer.UploadEmitterMesh
        // (0 = billboard). The renderer draws the .scb/.sco geometry per particle instead of a quad.
        public uint MeshVao, MeshVbo;
        public int MeshVertexCount;
    }

    internal struct Particle
    {
        public Vector3 Pos, Vel;
        public float Age, Life;
        public Vector2 BirthSize;   // absolute (birthScale0 xy)
        public Vector4 BirthColor;
        public float Rot, RotVel;
        public float StartFrame;
    }

    public IReadOnlyList<EmitterState> Emitters => _emitters;
    private readonly List<EmitterState> _emitters = new();
    private readonly Random _rng;
    public int LiveParticleCount { get; private set; }

    // Emitters that never terminate loop forever; give a hard cap so a runaway rate can't explode memory.
    private const int MaxParticlesPerEmitter = 4000;

    public VfxParticleSimulator(int seed = 1234) => _rng = new Random(seed);

    /// <summary>Configure from a system placed at <paramref name="worldPos"/>. Only visual emitters are simulated.</summary>
    public void SetSystem(VfxSystemDefinition system, Vector3 worldPos, bool includeNonVisual = false)
    {
        _emitters.Clear();
        foreach (var e in system.Emitters)
        {
            if (!includeNonVisual && !e.IsVisual) continue;
            _emitters.Add(new EmitterState { Def = e, BasePos = worldPos + e.EmitterPosition });
        }
        Reset();
    }

    public void Reset()
    {
        foreach (var s in _emitters) { s.Particles.Clear(); s.SpawnAccum = 0; s.Age = 0; s.BurstDone = false; s.InstanceCount = 0; }
        LiveParticleCount = 0;
    }

    /// <summary>Advance the whole system by <paramref name="dt"/> seconds and rebuild render instances.</summary>
    public void Update(float dt)
    {
        if (dt <= 0f) return;
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
        bool emitting = s.Age >= d.TimeBeforeFirstEmission
                        && (d.EmitterLifetime is not { } life || s.Age <= d.TimeBeforeFirstEmission + life);
        if (emitting)
        {
            if (d.IsSingleParticle)
            {
                if (!s.BurstDone) { Spawn(s); s.BurstDone = true; }
            }
            else
            {
                float rate = MathF.Max(0f, d.Rate.Sample(0f));
                s.SpawnAccum += rate * dt;
                while (s.SpawnAccum >= 1f && s.Particles.Count < MaxParticlesPerEmitter)
                {
                    Spawn(s);
                    s.SpawnAccum -= 1f;
                }
                if (s.Particles.Count >= MaxParticlesPerEmitter) s.SpawnAccum = 0f;
            }
        }

        // integrate + cull
        var accel = d.Acceleration?.Sample(0f) ?? Vector3.Zero;
        for (int i = s.Particles.Count - 1; i >= 0; i--)
        {
            var p = s.Particles[i];
            p.Age += dt;
            if (p.Age >= p.Life + d.ParticleLinger) { s.Particles.RemoveAt(i); continue; }
            p.Vel += accel * dt;
            p.Pos += p.Vel * dt;
            p.Rot += p.RotVel * dt;
            s.Particles[i] = p;
        }

        // Infinite emitter with no live particles and a finished burst -> allow looping single particles.
        if (d.IsSingleParticle && s.BurstDone && s.Particles.Count == 0 && d.EmitterLifetime is null)
            s.BurstDone = false;
    }

    private void Spawn(EmitterState s)
    {
        var d = s.Def;
        // M47: exact per-particle randomisation — Value* probability tables (VfxProbabilityTableData)
        // are rolled per particle when the data carries them; SampleBirth falls back to the constant.
        float life = MathF.Max(0.05f, d.ParticleLifetime.SampleBirth(_rng));
        var birthScale = d.BirthScale.SampleBirth(_rng);
        var vel = d.BirthVelocity?.SampleBirth(_rng) ?? Vector3.Zero;
        var rotVel = d.BirthRotationalVelocity?.SampleBirth(_rng) ?? Vector3.Zero;

        // M46 approximate variance, ONLY where no probability tables exist (most map VFX): velocity
        // direction/magnitude spread, spawn-position jitter scaled by sprite size, lifetime variance —
        // without any randomness every particle follows the identical path (fire renders as a line).
        Vector3 RandUnit()
        {
            var v = new Vector3((float)(_rng.NextDouble() * 2 - 1), (float)(_rng.NextDouble() * 2 - 1), (float)(_rng.NextDouble() * 2 - 1));
            float len = v.Length();
            return len > 1e-4f ? v / len : Vector3.UnitY;
        }
        float sizeRef = MathF.Max(MathF.Abs(birthScale.X), MathF.Abs(birthScale.Y));
        bool hasVelProb = d.BirthVelocity is { } bv && bv.HasProb;
        float speed = vel.Length();
        // M47c: mesh primitives (waterfalls...) are placed EXACTLY and animate by UV scroll — no jitter,
        // no random billboard spin (the real shader keeps the authored mesh orientation).
        Vector3 posJitter = Vector3.Zero;
        if (!d.IsMeshPrimitive)
        {
            if (!hasVelProb && speed > 1e-3f) vel += RandUnit() * speed * 0.30f;   // ~17 degree cone + magnitude spread
            posJitter = RandUnit() * sizeRef * 0.35f;
            if (d.ParticleLifetime.Prob is null) life *= 0.8f + 0.4f * (float)_rng.NextDouble();
        }

        s.Particles.Add(new Particle
        {
            Pos = s.BasePos + posJitter,
            Vel = vel,
            Age = 0f,
            Life = life,
            BirthSize = new Vector2(MathF.Abs(birthScale.X), MathF.Abs(birthScale.Y == 0 ? birthScale.X : birthScale.Y)),
            BirthColor = d.BirthColor.Sample(0f),
            Rot = d.IsMeshPrimitive ? 0f : (float)(_rng.NextDouble() * MathF.Tau),
            RotVel = rotVel.Z * (MathF.PI / 180f),          // degrees/s -> rad/s
            StartFrame = d.RandomStartFrame && d.NumFrames > 1 ? _rng.Next(d.NumFrames) : 0,
        });
    }

    private static void BuildInstances(EmitterState s)
    {
        var d = s.Def;
        int n = s.Particles.Count;
        if (s.Instances.Length < n * 11) s.Instances = new float[Math.Max(n * 11, 64)];
        var buf = s.Instances;
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            var p = s.Particles[i];
            float t = Math.Clamp(p.Age / p.Life, 0f, 1f);
            var scaleMul = d.ScaleOverLife?.Sample(t) ?? Vector3.One;
            var colMul = d.ColorOverLife?.Sample(t) ?? Vector4.One;
            var col = p.BirthColor * colMul;

            float frame = 0f;
            if (d.NumFrames > 1) frame = (p.StartFrame + t * d.NumFrames) % d.NumFrames;

            buf[k++] = p.Pos.X; buf[k++] = p.Pos.Y; buf[k++] = p.Pos.Z;
            buf[k++] = p.BirthSize.X * scaleMul.X;
            buf[k++] = p.BirthSize.Y * scaleMul.Y;
            buf[k++] = col.X; buf[k++] = col.Y; buf[k++] = col.Z; buf[k++] = col.W;
            buf[k++] = p.Rot;
            buf[k++] = frame;
        }
        s.InstanceCount = n;
    }
}

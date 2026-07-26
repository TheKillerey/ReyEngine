using System.Numerics;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// A parsed League VFX system (M36) — one <c>VfxSystemDefinitionData</c> and its emitters, enough to
/// actually simulate/preview the particles (birth rate, lifetime, size/colour/velocity over life, texture,
/// flipbook, blend). This is a faithful-but-approximate model of Riot's runtime, not a 1:1 reproduction.
/// </summary>
public sealed record VfxSystemDefinition(
    uint PathHash,
    string Name,
    string ParticlePath,
    IReadOnlyList<VfxEmitterDefinition> Emitters,
    string? PersistentSoundEventName = null,
    string? OnCreateSoundEventName = null,
    float VisibilityRadius = 0f);

/// <summary>One emitter inside a system. Curves are absolute-valued (sampled over normalised particle age 0..1).</summary>
public sealed record VfxEmitterDefinition(
    string Name,
    VfxCurveF Rate,                 // particles per second
    VfxCurveF ParticleLifetime,     // seconds a particle lives
    float? EmitterLifetime,         // emitter runtime; null = infinite (loops)
    float ParticleLinger,           // retention window used while an emitter shuts down
    float TimeBeforeFirstEmission,
    bool IsSingleParticle,          // burst of exactly one particle
    bool Disabled,
    int BlendMode,                  // 1/3/4/5 = additive family, 0/2 = alpha (M117 survey — see VfxParticleRenderer.IsAdditive)
    VfxCurve3 BirthScale,           // ABSOLUTE size at birth (birthScale0), world units
    VfxCurve3? ScaleOverLife,       // scale0: normalised MULTIPLIER over age → effective size = BirthScale * this
    VfxCurve4 BirthColor,           // rgba at birth
    VfxCurve4? ColorOverLife,       // color: MULTIPLIER over age → effective colour = BirthColor * this (alpha usually fades)
    VfxCurve3? BirthVelocity,       // initial velocity
    VfxCurve3? Acceleration,        // worldAcceleration (gravity/wind)
    VfxCurve3? BirthRotationalVelocity,
    /// <summary>Offset of this emitter within the system.
    ///
    /// A CURVE, not a bare Vector3, because it carries per-particle probability tables. Measured over 60
    /// champion WADs: of 186,374 emitters that author this field, 37,327 have no constantValue at all and
    /// every one of their curve keys is exactly (0,0,0) - but 37,220 of those carry probabilityTables.
    /// The authored intent is a per-particle SCATTER around the origin, not a fixed offset, so collapsing
    /// this to a single Vector3 made every particle emit from the same point.</summary>
    VfxCurve3 EmitterPosition,
    string? TexturePath,            // particle sprite (.dds/.tex)
    Vector2 TexDiv,                 // flipbook grid (cols, rows); (1,1) = single frame
    int NumFrames,
    bool RandomStartFrame,
    bool IsMeshPrimitive,           // primitive is a mesh (billboarded only when the mesh can't load)
    string? MeshPath = null,        // M47: VfxPrimitiveMesh -> VfxMeshDefinitionData.mSimpleMeshName (.scb/.sco)
    Vector2 UvScrollRate = default, // M47c: birthUvScrollRate — mesh particles FLOW by scrolling UVs (waterfalls)
    string? MeshSkeletonPath = null, // M48: skinned mesh primitive (.skl) — butterflies
    string? MeshAnimationPath = null, // M48: idle animation (.anm) — the wing flap
    VfxSpawnShape? SpawnShape = null,
    VfxCurve3? BirthAcceleration = null,
    VfxCurve3? BirthOrbitalVelocity = null,
    VfxCurve3? BirthDrag = null,
    VfxCurve3? DragOverLife = null,
    VfxCurve3? BirthRotation = null,
    bool IsDirectionOriented = false,
    bool IsArbitraryQuad = false,
    VfxCurveF? BirthFrameRate = null,
    float? FrameRate = null,
    string? TextureMultPath = null,
    Vector2 TextureMultTexDiv = default,
    Vector2 TextureMultUvScrollRate = default,
    float StartFrame = 0f,
    bool UseTextureAspect = false,
    VfxDistortionDefinition? Distortion = null,
    // M68: particleColorTexture is a 2-D colour-over-life gradient. Riot samples it per particle to derive
    // the particle colour (these emitters leave birthColor/color unset, so without it they render white).
    // colorLookUpTypeX/Y pick what drives the U/V lookup axes; null = the field was absent (Riot default 0).
    string? ParticleColorTexturePath = null,
    int? ColorLookUpTypeX = null,
    int? ColorLookUpTypeY = null,
    // ---- M174 tier 1 ----
    /// <summary>Draw order within a system. Present on 79.7% of League's emitters with 2,913 distinct
    /// values across the full I16 range; until M174 it was discarded entirely, so additive glows drew in
    /// container order and layered effects stacked wrongly.</summary>
    int Pass = 0,
    /// <summary>Alpha-test cutoff, 0..255 as authored. The engine confirms this one — `quad_ps` declares
    /// ALPHA_TEST / AlphaTestReferenceValue. 34,788 emitters author a non-zero cutoff.</summary>
    int AlphaRef = 0,
    /// <summary>Affine transform applied to the particleColorTexture lookup coordinate before sampling.</summary>
    Vector2 ColorLookUpScale = default,
    Vector2 ColorLookUpOffset = default,
    /// <summary>velocity over particle life (distinct from birthVelocity), 23,112 emitters.</summary>
    VfxCurve3? VelocityOverLife = null,
    /// <summary>rotation0 — an INTEGRATED value: accumulate it, do not sample it as an absolute angle.
    /// 52,647 emitters.</summary>
    VfxCurve3? RotationOverLife = null)
{
    /// <summary>Does this emitter produce anything drawable (has a texture and isn't disabled)?</summary>
    public bool IsVisual => !Disabled && (!string.IsNullOrEmpty(TexturePath) ||
        !string.IsNullOrEmpty(TextureMultPath) || !string.IsNullOrEmpty(MeshPath) ||
        Distortion is { NormalMapTexturePath.Length: > 0 });
}

/// <summary>Riot's screen-space particle distortion stage (heat haze/refraction).</summary>
public sealed record VfxDistortionDefinition(float Strength, int Mode, string? NormalMapTexturePath);

/// <summary>Which volume an emitter spawns particles in.</summary>
public enum VfxShapeKind
{
    /// <summary>No volume — the emitOffset (possibly randomised by its probability tables) IS the position.
    /// This covers VfxShapeLegacy and the unresolved 0xee39916f, which together are 69.4% of shapes.</summary>
    Offset,
    Sphere,
    Box,
    Cylinder,
}

/// <summary>
/// Authored particle spawn volume. <see cref="EmitOffset"/> is randomized by its ValueVector3
/// probability tables, then the authored axis/angle rotations are applied in order.
///
/// M174: before this, every shape collapsed to a point. Measured over the live corpus, 216,819 emitters
/// (15.5%) are a sphere, box or cylinder and NOT ONE of them carries an emitOffset — so every particle
/// spawned at the emitter origin where League fills a volume. That is one of the largest single reasons
/// an effect looks wrong in the editor.
/// </summary>
public sealed record VfxSpawnShape(
    VfxCurve3 EmitOffset,
    IReadOnlyList<Vector3> RotationAxes,
    IReadOnlyList<VfxCurveF> RotationAngles,
    VfxShapeKind Kind = VfxShapeKind.Offset,
    /// <summary>Sphere/cylinder radius, world units.</summary>
    float Radius = 0f,
    /// <summary>Cylinder height, world units.</summary>
    float Height = 0f,
    /// <summary>Box half-extents... or full extents. UNKNOWN which — see SampleOffset.</summary>
    Vector3 Size = default)
{
    /// <summary>Ranges in the live data are extreme — sphere radius up to 3.02e8, cylinder height 250,100.
    /// A particle spawned that far out is invisible and only costs simulation, so the volume is clamped for
    /// preview. Not a correctness claim about the game, just a guard against a runaway buffer.</summary>
    private const float MaxExtent = 100_000f;

    public Vector3 SampleOffset(Random rng)
    {
        var offset = EmitOffset.SampleBirth(rng);
        offset += SampleVolume(rng);

        int count = Math.Min(RotationAxes.Count, RotationAngles.Count);
        for (int i = 0; i < count; i++)
        {
            var axis = RotationAxes[i];
            if (axis.LengthSquared() <= 1e-8f) continue;
            float radians = RotationAngles[i].SampleBirth(rng) * (MathF.PI / 180f);
            offset = Vector3.Transform(offset,
                Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians));
        }
        return offset;
    }

    /// <summary>A point inside the authored volume.
    ///
    /// UNIFORM THROUGHOUT THE VOLUME is an assumption, not a measurement. League may well emit from the
    /// surface instead, and the `flags` byte present on 95.2% of boxes (domain unmeasured) is the obvious
    /// candidate for selecting between them. Uniform-solid is the choice that looks right for the smoke
    /// and dust these shapes mostly drive; revisit if a shell-emitting effect looks wrong.
    ///
    /// Box `Size` is likewise treated as HALF-extents. Full-extents would make every box twice as large.
    /// Neither reading is confirmed.</summary>
    private Vector3 SampleVolume(Random rng)
    {
        float Sym(float half) => (float)(rng.NextDouble() * 2.0 - 1.0) * half;

        switch (Kind)
        {
            case VfxShapeKind.Sphere:
            {
                float r = MathF.Min(MathF.Abs(Radius), MaxExtent);
                if (r <= 0f) return Vector3.Zero;
                // Rejection sampling: the cube-to-sphere hit rate is pi/6 ~= 52%, so this terminates fast,
                // and it avoids the clustering that naive spherical coordinates produce at the poles.
                for (int i = 0; i < 8; i++)
                {
                    var v = new Vector3(Sym(1f), Sym(1f), Sym(1f));
                    if (v.LengthSquared() <= 1f) return v * r;
                }
                return Vector3.Zero;
            }
            case VfxShapeKind.Box:
            {
                var h = new Vector3(
                    MathF.Min(MathF.Abs(Size.X), MaxExtent),
                    MathF.Min(MathF.Abs(Size.Y), MaxExtent),
                    MathF.Min(MathF.Abs(Size.Z), MaxExtent));
                return new Vector3(Sym(h.X), Sym(h.Y), Sym(h.Z));
            }
            case VfxShapeKind.Cylinder:
            {
                float r = MathF.Min(MathF.Abs(Radius), MaxExtent);
                float hh = MathF.Min(MathF.Abs(Height), MaxExtent) * 0.5f;
                if (r <= 0f && hh <= 0f) return Vector3.Zero;
                // sqrt on the radius keeps the disc uniform by AREA; without it particles bunch at the axis.
                double ang = rng.NextDouble() * Math.PI * 2.0;
                float rr = r * MathF.Sqrt((float)rng.NextDouble());
                // Y is the axis — consistent with League's Y-up world. UNVERIFIED against the game.
                return new Vector3(rr * MathF.Cos((float)ang), Sym(hh), rr * MathF.Sin((float)ang));
            }
            default:
                return Vector3.Zero;
        }
    }
}

/// <summary>M47: one per-component probability table (VfxProbabilityTableData): a particle rolls r in 0..1
/// at birth and takes the piecewise-linear value at r — Riot's exact per-particle randomisation.</summary>
public readonly record struct VfxProbTable(float[] Times, float[] Values)
{
    public bool IsEmpty => Times is not { Length: > 0 } || Values is not { Length: > 0 };
    public float Sample(float r) => VfxCurve.Interp(Times, Values, r, static (a, b, f) => a + (b - a) * f);
}

/// <summary>A scalar value that is either constant or an animation curve over normalised age (0..1).</summary>
public readonly record struct VfxCurveF(float Constant, float[]? Times, float[]? Values, VfxProbTable[]? Prob = null)
{
    public float Sample(float t)
    {
        if (Times is null || Values is null || Times.Length == 0) return Constant;
        return VfxCurve.Interp(Times, Values, t, static (a, b, f) => a + (b - a) * f);
    }
    /// <summary>Birth-time value: exact per-particle randomisation via the probability table when present.</summary>
    public float SampleBirth(Random rng)
    {
        float value = Sample(0f);
        return Prob is { Length: > 0 } && !Prob[0].IsEmpty
            ? value * Prob[0].Sample((float)rng.NextDouble())
            : value;
    }
    public static readonly VfxCurveF Zero = new(0f, null, null);
    public static VfxCurveF Const(float v) => new(v, null, null);
}

/// <summary>A Vector3 value that is either constant or an animation curve over normalised age.</summary>
public readonly record struct VfxCurve3(Vector3 Constant, float[]? Times, Vector3[]? Values, VfxProbTable[]? Prob = null)
{
    public Vector3 Sample(float t)
    {
        if (Times is null || Values is null || Times.Length == 0) return Constant;
        return VfxCurve.Interp(Times, Values, t, static (a, b, f) => Vector3.Lerp(a, b, f));
    }
    /// <summary>Birth-time value with per-component probability tables (independent rolls, Riot-style).</summary>
    public Vector3 SampleBirth(Random rng)
    {
        var v = Sample(0f);
        if (Prob is not { Length: > 0 }) return v;
        return new Vector3(
            Prob.Length > 0 && !Prob[0].IsEmpty ? v.X * Prob[0].Sample((float)rng.NextDouble()) : v.X,
            Prob.Length > 1 && !Prob[1].IsEmpty ? v.Y * Prob[1].Sample((float)rng.NextDouble()) : v.Y,
            Prob.Length > 2 && !Prob[2].IsEmpty ? v.Z * Prob[2].Sample((float)rng.NextDouble()) : v.Z);
    }
    public bool HasProb => Prob is { Length: > 0 } && Prob.Any(static p => !p.IsEmpty);
    public static VfxCurve3 Const(Vector3 v) => new(v, null, null);
}

/// <summary>A Vector4/colour value that is either constant or an animation curve over normalised age.</summary>
public readonly record struct VfxCurve4(Vector4 Constant, float[]? Times, Vector4[]? Values, VfxProbTable[]? Prob = null)
{
    public Vector4 Sample(float t)
    {
        if (Times is null || Values is null || Times.Length == 0) return Constant;
        return VfxCurve.Interp(Times, Values, t, static (a, b, f) => Vector4.Lerp(a, b, f));
    }
    public Vector4 SampleBirth(Random rng)
    {
        var v = Sample(0f);
        if (Prob is not { Length: > 0 }) return v;
        return new Vector4(
            Prob.Length > 0 && !Prob[0].IsEmpty ? v.X * Prob[0].Sample((float)rng.NextDouble()) : v.X,
            Prob.Length > 1 && !Prob[1].IsEmpty ? v.Y * Prob[1].Sample((float)rng.NextDouble()) : v.Y,
            Prob.Length > 2 && !Prob[2].IsEmpty ? v.Z * Prob[2].Sample((float)rng.NextDouble()) : v.Z,
            Prob.Length > 3 && !Prob[3].IsEmpty ? v.W * Prob[3].Sample((float)rng.NextDouble()) : v.W);
    }
    public static VfxCurve4 Const(Vector4 v) => new(v, null, null);
}

internal static class VfxCurve
{
    /// <summary>Piecewise-linear sample of (times,values) at t, clamped at both ends.</summary>
    public static T Interp<T>(float[] times, T[] values, float t, Func<T, T, float, T> lerp)
    {
        int n = Math.Min(times.Length, values.Length);
        if (n == 1 || t <= times[0]) return values[0];
        if (t >= times[n - 1]) return values[n - 1];
        for (int i = 1; i < n; i++)
        {
            if (t <= times[i])
            {
                float span = times[i] - times[i - 1];
                float f = span > 1e-6f ? (t - times[i - 1]) / span : 0f;
                return lerp(values[i - 1], values[i], f);
            }
        }
        return values[n - 1];
    }
}

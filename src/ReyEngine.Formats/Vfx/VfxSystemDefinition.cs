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
    /// <summary>rotation0 - an INTEGRATED value: accumulate it, do not sample it as an absolute angle.
    /// 52,647 emitters.</summary>
    VfxCurve3? RotationOverLife = null,

    // ---- M174 tier 2.3: the UV transform stack ----
    // ReyEngine implemented exactly one term of this (birthUvScrollRate * age). These are the rest, in
    // descending order of how many emitters author them.
    /// <summary>birthUVOffset - a fixed shift of the sampled UV. 146,833 emitters.</summary>
    Vector2 UvOffset = default,
    /// <summary>uvScale - zoom about UvTransformCenter. 135,073 emitters.</summary>
    Vector2 UvScale = default,
    /// <summary>particleUVScrollRate - an INTEGRATED value: it accumulates, so it must not be treated as
    /// an ordinary per-age curve. 86,904 emitters.</summary>
    Vector2 UvScrollIntegrated = default,
    /// <summary>uvRotation, degrees, about UvTransformCenter. 50,439 emitters.</summary>
    float UvRotation = 0f,
    /// <summary>uvScrollClamp - clamp the final coordinate to [0,1] instead of wrapping. 29,820.</summary>
    bool UvScrollClamp = false,
    /// <summary>emitterUvScrollRate - scrolls with EMITTER age rather than particle age. 23,890.</summary>
    Vector2 EmitterUvScrollRate = default,
    /// <summary>TextureFlipV / TextureFlipU. 7,443 / 6,747.</summary>
    bool UvFlipU = false,
    bool UvFlipV = false,
    /// <summary>particleUVRotateRate - INTEGRATED, degrees per second. 8,676.</summary>
    float UvRotateIntegrated = 0f,
    /// <summary>birthUvRotateRate - degrees per second, from birth. 8,203.</summary>
    float UvRotateRate = 0f,
    /// <summary>uvTransformCenter - the pivot rotation and scale act about. 3,977; defaults to the
    /// texel-space centre (0.5, 0.5) when absent, which is the only pivot that leaves a centred sprite
    /// in place.</summary>
    Vector2 UvTransformCenter = default,

    // ---- M174 tier 2.14/2.15: emission control and shutdown ----
    /// <summary>period / timeActiveDuringPeriod - a duty cycle. The emitter emits for
    /// TimeActiveDuringPeriod seconds out of every Period seconds. 8,132 / 8,240 emitters; without it a
    /// pulsed emitter runs continuously, which is very visible on map beacons and braziers.</summary>
    float? Period = null,
    float? TimeActiveDuringPeriod = null,
    /// <summary>ChanceToNotExist - per-spawn skip probability. 1,164 emitters; 14 of them author 1.0 and
    /// should therefore emit NOTHING.</summary>
    float ChanceToNotExist = 0f,
    /// <summary>HasVariableStartTime - randomise the emitter's start offset so repeats of the same effect
    /// do not look mechanically synchronised. 3,973 emitters.</summary>
    bool HasVariableStartTime = false,
    /// <summary>emitterLinger - how long the emitter survives after it stops emitting. 95,954 emitters;
    /// without it an effect cuts off instead of trailing away.</summary>
    float? EmitterLinger = null,

    /// <summary>M174 (2.1) alphaErosionDefinition - the dissolve stage. 307,050 emitters (22%), the
    /// largest single visual feature ReyEngine did not implement; without it they all fade by uniform
    /// alpha instead of eroding away through a noise map.</summary>
    VfxAlphaErosion? AlphaErosion = null,

    // ---- M175 tier 2.2 / 2.6 / 2.8 ----
    /// <summary>M175 (2.2) softParticleParams - fade the sprite out as it approaches the geometry behind
    /// it, instead of hard-clipping against it. 95,671 emitters.</summary>
    VfxSoftParticle? SoftParticle = null,
    /// <summary>M175 (2.6) paletteDefinition - replace the sprite's RGB with a lookup into a gradient
    /// strip. 43,621 emitters, 13,823 of which have no colour texture at all and so currently render as
    /// flat birthColor.</summary>
    VfxPalette? Palette = null,
    /// <summary>M175 (2.8) depthPushPull - move the sprite along the camera->vertex ray before projecting,
    /// which changes only its depth and never its screen position. 61,588 emitters.
    ///
    /// DECODED from quad_vs (see <see cref="VfxEmitterDef"/> callers): the shader computes
    /// <c>pos += normalize(pos - vCamera) * PARTICLE_DEPTH_PUSH_PULL</c>, so POSITIVE pushes the sprite
    /// AWAY from the camera and negative pulls it toward. Measured on the live corpus, 74.6% of authored
    /// values are negative (median -5, range -2000..9999) - i.e. the overwhelmingly common use is pulling
    /// a sprite toward the camera so it survives the depth test against terrain, which is exactly what the
    /// decoded sign predicts.</summary>
    float DepthPushPull = 0f,

    /// <summary>M176 (2.4) fieldCollectionDefinition - noise / drag / acceleration / attraction / orbital
    /// force fields. 39,904 emitters. Without these, particles travel in straight lines where League
    /// swirls them.</summary>
    VfxForceFields? ForceFields = null,

    /// <summary>M177 (2.5) mTrail - ribbon geometry trailing behind each particle. 78,852 emitters across
    /// VfxPrimitiveCameraTrail and VfxPrimitiveArbitraryTrail, all of which billboarded before.</summary>
    VfxTrailDefinition? Trail = null,
    /// <summary>M177: true for VfxPrimitiveArbitraryTrail, false for VfxPrimitiveCameraTrail. Decides
    /// whether the ribbon twists to face the camera or holds the placement's orientation.</summary>
    bool IsArbitraryTrail = false,

    /// <summary>M183 (2.5) mBeam - a ribbon from a source point to a bound TARGET. 11,452 payloads across
    /// 15,416 VfxPrimitiveBeam emitters. Unlike a trail the geometry is not motion history: it is
    /// regenerated every frame from two endpoints.</summary>
    VfxBeamDefinition? Beam = null,

    /// <summary>M178 (2.12) reflectionDefinition - the fresnel rim and cubemap reflection stage.
    /// 59,149 emitters.</summary>
    VfxReflection? Reflection = null,

    /// <summary>M180 (2.7) childParticleSetDefinition - other VFX systems spawned by this emitter's
    /// particles. 35,510 emitters.</summary>
    VfxChildParticleSet? Children = null,

    /// <summary>M182 (2.9) stencilMode. U8, measured {2: 58.9%, 3: 25.9%, 1: 15.0%, 4: 0.2%}.
    ///
    /// Only mode 1 has a defined meaning - a normal stencil WRITE, i.e. draw as usual and replace the
    /// stencil value with <see cref="StencilRef"/> where the fragment passes. Modes 2, 3 and 4 are
    /// UNRESOLVED: nothing in the data distinguishes them, and between them they are 85% of the
    /// authored total. Emitters carrying them are drawn with the stencil untouched, exactly as before,
    /// rather than being given a guessed test function.</summary>
    int StencilMode = 0,
    /// <summary>M185 (2.15) Linger - the shutdown curve set, applied once the system is STOPPED.
    /// 22,274 emitters.</summary>
    VfxLinger? Linger = null,
    /// <summary>M185 (2.15) particleLingerType, U8 {1: 13,763, 2: 1,669}; -1 = absent. PARSED, NOT ACTED
    /// ON. Measured, it is largely independent of the Linger struct - 19,406 Linger structs carry no
    /// type at all, and 11,347 type values sit on emitters with no Linger struct - so nothing in the
    /// corpus says it selects a linger behaviour.</summary>
    int ParticleLingerType = -1,
    /// <summary>M193 (tier 4.1): 43 fields the resolver now parses but the renderer does not consume.
    /// Null when the emitter authored none of them. See <see cref="VfxEmitterExtras"/> - every member is
    /// nullable because Riot omits default-valued properties, so absent never means zero.</summary>
    VfxEmitterExtras? Extras = null,

    /// <summary>M184 (2.11) disableBackfaceCull. Bool, and TRUE in all 358,113 occurrences - no `false`
    /// exists anywhere in the corpus.
    ///
    /// That is not "the field is redundant", it is what tells us the DEFAULT. Riot's bin writer omits any
    /// property equal to its class default: measured corpus-wide there are 970 distinct (class, bool
    /// field) pairs and NOT ONE ships both polarities, while six Vfx bools ship only `false`. Both
    /// polarities are therefore representable, so a field written only as `true` must default to `false`.
    /// Absent means **culling enabled**. Independently corroborated by StaticMaterialPassDef.cullEnable,
    /// which is authored 3,641 times and is always `false`.</summary>
    bool DisableBackfaceCull = false,
    /// <summary>M184 (2.10) PaletteTextureAddressMode. Riot's address enum, read off their own NAMED
    /// shared samplers in assets/shaders/shareddata.bin: Wrap_No_Mip / CharacterWrap / EnvironmentWrap all
    /// write 0, and the sampler literally called `Mirror` writes 2. So 0 = Wrap and 2 = Mirror, measured;
    /// 1 = Clamp by elimination plus usage evidence. This is Unity's TextureWrapMode ordering, NOT
    /// D3D11_TEXTURE_ADDRESS_MODE - 0 is not even a legal value in the D3D enum, and it is authored 75,451
    /// times. -1 = absent.</summary>
    int PaletteAddressMode = -1,

    /// <summary>M182 (2.9) stencilRef. U8, commonest values 1-7 with a tail to 48. The value written to
    /// the stencil buffer under mode 1, or compared against under modes 2 and 3.
    ///
    /// -1 means ABSENT, and that distinction matters rather than being tidiness: 726 of 3,891 emitters
    /// with a stencilMode author no numeric ref, most of them naming a symbolic `StencilReferenceId`
    /// instead (86 distinct hashes, not resolved here). Collapsing absent to 0 would make every one of
    /// those a test against 0 - harmless for mode 2, but mode 3 would then be "draw where the stencil is
    /// not 0", which on a freshly cleared buffer fails everywhere and deletes the emitter outright.</summary>
    int StencilRef = -1)
{
    /// <summary>Does this emitter produce anything drawable (has a texture and isn't disabled)?</summary>
    public bool IsVisual => !Disabled && (!string.IsNullOrEmpty(TexturePath) ||
        !string.IsNullOrEmpty(TextureMultPath) || !string.IsNullOrEmpty(MeshPath) ||
        Distortion is { NormalMapTexturePath.Length: > 0 });
}

/// <summary>M174 (2.1): Riot's alpha-erosion (dissolve) stage.
///
/// The maths here is DECODED from the shipped DXBC, not inferred from field names - the SHEX instruction
/// stream of particlesystem/quad_ps, particlesystem/mesh_ps and skinnedmesh/particle_ps all agree:
///
///   E    = saturate(dot(erosionTexel.rgba, mixer.rgba))     // full dp4, saturated
///   t    = drive - E
///   a    = saturate((t + P.y) * P.z)                        // leading edge
///   b    = saturate( t        * P.w)                        // trailing edge
///   mask = a - b                                            // NOT re-clamped by Riot
///   alpha *= mask                                           // pure multiply, applied last
///
/// Note it is a difference of two LINEAR ramps - a trapezoid - not a smoothstep. Riot uses a real
/// smoothstep for soft particles in the very same shader, so the linear ramp here is deliberate.
///
/// WHAT IS NOT KNOWN: which authored field lands in which of P.y/P.z/P.w. The verifier established that
/// this is UNDECIDABLE from the bytecode, because the CPU packs the vector before upload. The mapping
/// below (slice width -> P.y, 1/featherIn -> P.z, 1/featherOut -> P.w) is the plausible reading; if
/// erosion looks inverted in practice, swapping FeatherIn and FeatherOut is the one-line fix.</summary>
public sealed record VfxAlphaErosion(
    string? MapPath,
    /// <summary>erosionMapChannelMixer, dotted against the texel's RGBA. Overwhelmingly (1,0,0,0) - the
    /// red channel - at ~74% of the corpus.</summary>
    Vector4 ChannelMixer,
    /// <summary>erosionDriveCurve over particle life. The shader takes ONE scalar per particle, so
    /// UseLingerErosionDriveCurve can only be a CPU-side choice of which curve feeds it.</summary>
    VfxCurveF Drive,
    float SliceWidth,
    float FeatherIn,
    float FeatherOut)
{
    /// <summary>Pack into the shader's cAlphaErosionParams.yzw. Absent feathers must degrade to a no-op
    /// edge rather than to zero: a missing leading edge becomes a hard step (large slope), and a missing
    /// trailing edge becomes slope 0 so nothing is subtracted. Getting that backwards erases the sprite
    /// entirely, since mask = a - b.</summary>
    public Vector3 PackYzw() => new(
        SliceWidth,
        FeatherIn > 1e-6f ? 1f / FeatherIn : 1000f,
        FeatherOut > 1e-6f ? 1f / FeatherOut : 0f);

    /// <summary>Would this configuration erase the sprite outright, at every point of the drive curve
    /// and for every possible erosion-map value? Measured on the live corpus, 13,965 of 87,573 erosion
    /// emitters (16%) evaluate that way under the inferred parameter packing.
    ///
    /// This is a GUARD, not League behaviour. The formula itself is decoded from Riot's bytecode and is
    /// certain; which authored field lands in which of P.y/P.z/P.w is NOT — the verifier established it
    /// is undecidable from the shaders because the CPU packs the vector before upload. If that mapping is
    /// wrong, the visible symptom is exactly this: particles vanishing. Skipping the stage in that case
    /// can only ever restore the pre-M174 appearance, never make something worse, so it is the safe way
    /// to ship an inferred mapping. If erosion ever looks absent where it should dissolve, this guard and
    /// the FeatherIn/FeatherOut assignment are the two things to revisit together.</summary>
    public bool IsDegenerate
    {
        get
        {
            var yzw = PackYzw();
            for (int di = 0; di <= 8; di++)
            {
                float drive = Drive.Sample(di / 8f);
                for (int ei = 0; ei <= 8; ei++)
                {
                    float t = drive - ei / 8f;
                    float a = Math.Clamp((t + yzw.X) * yzw.Y, 0f, 1f);
                    float b = Math.Clamp(t * yzw.Z, 0f, 1f);
                    if (a - b > 0.01f) return false;
                }
            }
            return true;
        }
    }
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

/// <summary>M175 (2.2): Riot's soft-particle stage - fade the sprite as it nears the geometry behind it.
///
/// The maths is DECODED from the shipped DXBC and then VALIDATED by running Riot's own
/// `particlesystem/quad_ps` permutation #128 on a D3D11 device and comparing against this formula over
/// three configurations x 256 depth values. Worst disagreement 0.0021, under one 8-bit step
/// (docs/research/d3d11-spike.md):
///
///   lin(z) = 1 / (z * dc.y + dc.x)          // window depth -> view distance
///   diff   = lin(sceneDepth) - lin(selfDepth)
///   t      = saturate((diff - P.xy) * P.zw)  // .x pairs with .z, .y with .w
///   s      = t * t * (3 - 2t)                // a REAL smoothstep, unlike alpha erosion's linear ramps
///   fade   = s.x - s.y
///   rgb   *= C.x + C.y * fade
///   alpha *= C.z + C.w * fade
///
/// Two things about that are worth stating plainly, because both were open questions the validation
/// closed: the smoothstep is genuine (alpha erosion, in the very same shader family, uses linear ramps -
/// so the two stages really do differ), and the `.x`-pairs-with-`.z` swizzle is Riot's, not a guess.
///
/// WHAT IS NOT KNOWN: what feeds `cSoftParticleControl` (C). The CPU packs it before upload, so the
/// bytecode cannot say. This uses (1, 0, 0, 1) - leave RGB alone, multiply alpha by the fade - which is
/// the standard soft-particle behaviour and the only reading consistent with the feature's name. The
/// unresolved U8 field 0x3bf176bc {1:2, 2:239} inside this struct is the likeliest place a "fade colour
/// instead of alpha" mode would live; 74 emitters author it and none of them are handled specially.</summary>
public sealed record VfxSoftParticle(float BeginIn, float DeltaIn, float BeginOut, float DeltaOut)
{
    /// <summary>Pack into the shader's cSoftParticleParams. Absent widths must degrade to a no-op edge,
    /// exactly as in <see cref="VfxAlphaErosion.PackYzw"/>: a missing fade-in becomes a hard step, and a
    /// missing fade-out slope 0 so nothing is subtracted. Getting that backwards erases the sprite,
    /// since fade = s.x - s.y.
    ///
    /// The sign of the delta is PRESERVED rather than clamped. 1,500-odd emitters author a negative
    /// deltaIn (-1000 through -15 all occur), which under this formula inverts the ramp so the sprite is
    /// visible only NEAR geometry rather than away from it. That is a coherent authoring choice for
    /// ground-hugging mist, and clamping it to positive would silently discard it - the same mistake
    /// M174 (1.6) found in the texDiv handling.</summary>
    public Vector4 PackParams() => new(
        BeginIn, BeginOut,
        MathF.Abs(DeltaIn) > 1e-6f ? 1f / DeltaIn : 1000f,
        MathF.Abs(DeltaOut) > 1e-6f ? 1f / DeltaOut : 0f);

    /// <summary>Would this configuration erase the sprite at every distance from the geometry behind it?
    /// The same class of guard as <see cref="VfxAlphaErosion.IsDegenerate"/>, and for the same reason: if
    /// the parameter packing is wrong the symptom is particles vanishing, and skipping the stage can only
    /// restore the pre-M175 appearance, never make anything worse.
    ///
    /// Note this stage is far less dangerous than erosion to begin with: a particle in open air has no
    /// geometry behind it, so `diff` is enormous and the fade saturates to fully visible. Only fragments
    /// close to a surface are affected at all.</summary>
    public bool IsDegenerate
    {
        get
        {
            // 0 .. 4000 world units covers point-blank to well beyond any authored begin/delta (max 5000
            // appears once); the last sample stands in for "nothing behind this particle at all".
            foreach (float diff in new[] { 0f, 1f, 5f, 20f, 50f, 100f, 250f, 1000f, 4000f, 1e6f })
            {
                var p = PackParams();
                float tx = Math.Clamp((diff - p.X) * p.Z, 0f, 1f);
                float ty = Math.Clamp((diff - p.Y) * p.W, 0f, 1f);
                float fade = tx * tx * (3f - 2f * tx) - ty * ty * (3f - 2f * ty);
                if (fade > 0.01f) return false;
            }
            return true;
        }
    }
}

/// <summary>M175 (2.6): Riot's palette stage - replace the sprite's RGB with a lookup into a gradient
/// strip, keeping the source alpha.
///
/// DECODED from `particlesystem/quad_ps` and VALIDATED against Riot's own permutation #12 on a D3D11
/// device; worst disagreement 0.0096 across three configurations (docs/research/d3d11-spike.md):
///
///   m   = saturate(dot(sourceTexel.rgba, cPaletteSrcMixerMain))
///   U   = m + cPaletteSelectMain.z
///   V   = cPaletteSelectMain.x + cPaletteSelectMain.w
///   rgb = paletteTexture.Sample(U, V).rgb          // alpha comes from the source, untouched
///
/// The U offset was pinned rather than merely fitted: adding 0.25 moved the sampled texel from
/// palette[128] to palette[192] on a 256-wide strip, exactly +0.25 x 255.
///
/// The measured data matches that shape. `paletteSelector` is a ValueVector3 whose .x is a small integer
/// (1, 2, 4, 6, 9 ... ) and `paletteCount` is typically 16 - so the texture is a vertical stack of
/// gradients, U walks along one and V picks which. `palleteSrcMixColor` (Riot's typo, hash-confirmed) is
/// dominated by (1,0,0,0) and (0.3,0.59,0.11,0) - the red channel and Rec.601 luma weights - which is
/// exactly what a "which channel drives the gradient" mixer should look like.
///
/// WHAT IS INFERRED, because the CPU packs cPaletteSelectMain before upload:
///  - that V is row-centred as (selector + 0.5) / count. Centring is the only choice that reliably lands
///    on ONE row under the bilinear sampler Riot binds; sampling at selector/count straddles two.
///  - that PaletteU/VAnimationCurve feed the .z and .w offsets. They are the only per-emitter animated
///    scalars in the struct and the shader adds exactly two such offsets, but the pairing is by name.
///  - the DEFAULT mixer when `palleteSrcMixColor` is absent (70% of the corpus). Luma is used here. On a
///    greyscale source texture - the common case for a palettised sprite - luma and red-only agree
///    exactly, so this choice only changes anything for colour source art.</summary>
public sealed record VfxPalette(
    string? TexturePath,
    /// <summary>paletteCount - how many gradient rows the strip holds. -1 and 0 both occur and are
    /// nonsense as a divisor; <see cref="IsUsable"/> rejects them.</summary>
    int Count,
    /// <summary>paletteSelector.x - which row. Authored as a ValueVector3 with y/z always zero.</summary>
    float Selector,
    Vector4 SrcMixer,
    VfxCurveF? UAnim,
    VfxCurveF? VAnim)
{
    /// <summary>The row's V coordinate, centred. See the class remarks - the +0.5 is inferred.</summary>
    public float RowV => Count > 0 ? (Selector + 0.5f) / Count : 0.5f;

    /// <summary>A palette with no texture, or a row count that cannot be divided by, does nothing.</summary>
    public bool IsUsable => !string.IsNullOrEmpty(TexturePath) && Count > 0;
}

/// <summary>M176 (2.4): Riot's <c>VfxFieldCollectionDefinitionData</c> - the force fields applied to a
/// particle each step. 39,904 emitters carry one.
///
/// Every field's SHAPE here is measured, not guessed: the class hashes, the inner property names and
/// their BinTree types were read off 6,134 live collections across 28 WADs. Two measurements are worth
/// recording because they close questions the support report left open:
///
///  - <c>isLocalSpace</c> is <b>false in every one</b> of the 616 acceleration and orbital fields
///    sampled. World space is therefore not an assumption here, it is the only case that ships.
///  - attraction's <c>acceleration</c> is <b>signed</b> (min -10000), so the same field type does both
///    attraction and repulsion. Clamping it positive would have silently dropped every repulsor.
///
/// What is NOT measurable is the MATHS - these fields are integrated on the CPU, so unlike the erosion,
/// soft-particle and palette stages there is no shader bytecode to decode and no way to validate against
/// Riot's own implementation. The per-field remarks say exactly which part is inferred.</summary>
public sealed record VfxForceFields(
    IReadOnlyList<VfxNoiseField> Noise,
    IReadOnlyList<VfxDragField> Drag,
    IReadOnlyList<VfxAccelerationField> Acceleration,
    IReadOnlyList<VfxAttractionField> Attraction,
    IReadOnlyList<VfxOrbitalField> Orbital)
{
    public bool IsEmpty => Noise.Count == 0 && Drag.Count == 0 && Acceleration.Count == 0
                           && Attraction.Count == 0 && Orbital.Count == 0;
}

/// <summary>Turbulence. The commonest field by a wide margin - 4,654 of 6,134 collections.
///
/// INFERRED: that <c>frequency</c> is a spatial WAVELENGTH (noise sampled at <c>pos / frequency</c>)
/// rather than a multiplier. Measured median is 25 with a range of 0.005..5000; read as a wavelength that
/// is a 25-unit swirl, which is a sensible scale for smoke next to a ~100-unit champion. Read as a
/// multiplier it would be 25 cycles per world unit - white noise at any distance a particle travels -
/// so the wavelength reading is the only one that produces motion at all. Neither reading is tidy at the
/// extremes of the range.
///
/// INFERRED: that <c>velocityDelta</c> is an acceleration in units/second rather than an absolute
/// velocity offset. The field-shaped reading is the one consistent with the other four field types
/// (attraction's equivalent knob is literally named <c>acceleration</c>), and an absolute offset applied
/// per frame would make the motion frame-rate dependent.</summary>
public sealed record VfxNoiseField(Vector3 AxisFraction, float Radius, float Frequency, float VelocityDelta, Vector3 Position);

/// <summary>Slows particles within <see cref="Radius"/> of <see cref="Position"/>. Uses the same
/// exponential form the simulator already applies to birthDrag, so a particle inside a drag field decays
/// its velocity by <c>exp(-strength*dt)</c>. INFERRED: the radial falloff (see VfxForceFields).</summary>
public sealed record VfxDragField(float Radius, float Strength, Vector3 Position);

/// <summary>A uniform directional push - gravity, wind, updraft. No radius and no position, so it applies
/// everywhere. This is the one field type with essentially nothing inferred: the value is added to
/// velocity per second, in world space.</summary>
public sealed record VfxAccelerationField(Vector3 Acceleration, bool IsLocalSpace);

/// <summary>Pulls particles toward <see cref="Position"/> - or pushes them away, since
/// <see cref="Acceleration"/> is signed. INFERRED: the radial falloff (see VfxForceFields).</summary>
public sealed record VfxAttractionField(float Radius, float Acceleration, Vector3 Position);

/// <summary>Spins particles about the emitter origin. <see cref="Direction"/> is read as an angular
/// velocity vector - axis times radians per second - which is how the simulator already treats the
/// per-particle <c>birthOrbitalVelocity</c>, so the two compose consistently. Authored values like
/// (0.1, 0.3, 0.1) and (0, 2, 0) are the right magnitude for radians/second.</summary>
public sealed record VfxOrbitalField(Vector3 Direction, bool IsLocalSpace);

/// <summary>M177 (2.5): Riot's <c>VfxTrailDefinitionData</c> (class 0x00c2a390) - the ribbon that follows
/// a particle. Carried by 78,852 emitters, every one of which billboarded before.
///
/// The support report flagged "trails and beams are ribbon geometry" as a reading of the CLASS NAMES,
/// explicitly not a measurement. Reading the payload settles it: across 14,755 live trail definitions the
/// fields are a texture tiling LENGTH, a maximum length, a smoothing mode and a per-frame point budget -
/// which is the parameter set of a ribbon generator and of nothing else. The geometry itself is not in
/// the data at all; it is built at runtime from where the particle has been, and these values only say
/// how.
///
/// <c>mMode</c> is 1 on all 14,133 that author it, so there is no mode branch to get wrong.</summary>
public sealed record VfxTrailDefinition(
    /// <summary>mBirthTilingSize - how far along the ribbon one repeat of the texture spans, in world
    /// units. Authored as a Vector3 but overwhelmingly X-only: (500,0,0) is the single commonest value at
    /// 4,375 of 14,596, followed by (300,0,0), (600,0,0) and (400,0,0). Only X is used here; what a
    /// non-zero Y or Z would mean is UNKNOWN and does not occur often enough to guess at.</summary>
    float TilingLength,
    /// <summary>mCutoff - how long the ribbon is allowed to get, in world units. Median 1,000.
    ///
    /// The authored range includes junk that must not reach the geometry builder: -1 appears, and the
    /// maximum observed is 68,719,476,736 (2^36). <see cref="EffectiveCutoff"/> is what callers should
    /// use.</summary>
    float Cutoff,
    /// <summary>mSmoothingMode, {1, 2}. Meaning UNKNOWN - both values occur (12,183 samples) and nothing
    /// in the data distinguishes them. Parsed and carried so the editor can show it; the geometry builder
    /// does not branch on it, which is the honest thing to do with an unresolved enum.</summary>
    int SmoothingMode,
    /// <summary>mMaxAddedPerFrame - how many ribbon points may be appended per frame. Median 50.</summary>
    int MaxAddedPerFrame)
{
    /// <summary>The ribbon length to actually use. Rejects the -1 and 2^36 outliers and falls back to the
    /// corpus median, so a single junk value cannot produce a trail that stretches across the map or one
    /// that never appears.</summary>
    public float EffectiveCutoff => Cutoff > 1f && Cutoff < 100000f ? Cutoff : 1000f;

    /// <summary>One repeat of the texture per this many world units. Guards a zero so the UV generator
    /// cannot divide by it.</summary>
    public float EffectiveTiling => TilingLength > 1e-3f ? TilingLength : 500f;
}

/// <summary>M178 (2.12): Riot's <c>VfxReflectionDefinitionData</c> - a fresnel rim light, and a cubemap
/// reflection on top of it.
///
/// DECODED from `particlesystem/mesh_vs` and `mesh_ps`, permutation REFLECTIVE. The vertex stage:
///
///   N  = normalize(mul(normal, mWorld))
///   R  = V - 2*dot(V, N)*N                            // reflect(), -> cubemap lookup direction
///   f  = saturate(dot(-V, N))                         // NdotV
///   fresnelTerm    = 1 - pow(f, vFresnel.w)
///   o.fresnelColor = fresnelTerm * vFresnel.rgb
///   reflTerm       = 1 - pow(f, vReflection.x)
///   o.reflOpacity  = lerp(vReflection.y, vReflection.z, reflTerm)
///
/// and the pixel stage:
///
///   refl  = cubemap.Sample(R).rgb * reflOpacity
///   tint  = lerp(1, vReflectionFColor.rgb, reflOpacity)
///   rgb  += refl * tint
///   rgb  += fresnelColor * alpha                      // alpha = texel.a * colorTex.a * vertexColor.a
///
/// THE FIELD MAPPING IS PINNED BY THE MATHS, not guessed. This is the opposite situation to alpha
/// erosion, where which authored value landed in which cbuffer slot was undecidable: here the lerp
/// endpoints determine it. At a direct view NdotV = 1, so reflTerm = 0 and the opacity is exactly
/// <c>vReflection.y</c> - which the authored name calls <c>reflectionOpacityDirect</c>. The glancing end
/// is <c>.z</c>, named <c>reflectionOpacityGlancing</c>. Names and maths agree independently.
///
/// SCOPE, measured rather than assumed: REFLECTIVE is a define on `mesh_vs`/`mesh_ps` and does not exist
/// in `quad_ps`'s define pool at all, so Riot never compiles reflection into the billboard path. The
/// authored data agrees - 95.9% of emitters carrying this struct use a mesh-capable primitive. Putting
/// fresnel on a camera-facing quad would be inventing behaviour, not restoring it.</summary>
public sealed record VfxReflection(
    /// <summary>fresnelColor - the rim colour, added to RGB scaled by the fresnel term. 7,281 of 8,058
    /// measured instances author it.</summary>
    Vector4 FresnelColor,
    /// <summary>fresnel - the rim exponent, vFresnel.w. Median 0.1, range -1..20.</summary>
    float Fresnel,
    /// <summary>reflectionFresnelColor - tints the cubemap sample. Only meaningful with a map.</summary>
    Vector4 ReflectionFresnelColor,
    /// <summary>reflectionFresnel - the reflection ramp exponent, vReflection.x. Median 0.3.</summary>
    float ReflectionFresnel,
    /// <summary>reflectionOpacityDirect - reflection strength looking straight on (vReflection.y).</summary>
    float OpacityDirect,
    /// <summary>reflectionOpacityGlancing - reflection strength at grazing angles (vReflection.z).</summary>
    float OpacityGlancing,
    /// <summary>reflectionMapTexture - a real cubemap .dds. Only 1,062 of 8,058 measured instances have
    /// one, which is why the fresnel rim and the cubemap are separable and the rim ships first.</summary>
    string? MapPath)
{
    /// <summary>Does the rim actually do anything? A zero colour adds nothing however the exponent is
    /// set, and pow() needs a positive exponent to be a ramp rather than a constant.</summary>
    public bool HasFresnel =>
        (FresnelColor.X != 0f || FresnelColor.Y != 0f || FresnelColor.Z != 0f) && Fresnel > 0f;
}

/// <summary>M180 (2.7): Riot's <c>VfxChildParticleSetDefinitionData</c> (class 0xb520045a) - whole VFX
/// systems spawned by this emitter's particles. 35,510 emitters carry one.
///
/// Measured on 11,308 live sets. <c>childrenIdentifiers</c> is a container, almost always of one entry
/// (10,353 of 10,608) but up to four, and each entry references a system either by <c>effectKey</c> (a
/// Hash, 6,348) or by <c>effect</c> (an ObjectLink, 4,978).
///
/// <c>effectKey</c> is NOT a system's object path-hash. It lives in the same namespace as
/// <c>ResourceResolver.resourceMap</c>, so resolving one means going through that map - and per the
/// census only 4,392 of 6,334 sampled keys resolve within the same bin, the rest needing the skin's
/// dependency bins.
///
/// RECURSION IS NOT RULED OUT. An early probe appeared to show no cycles and a maximum chain depth of 1,
/// but it was comparing child keys against system object-hashes - two different keyspaces - so it was
/// measuring nothing and every key looked unresolvable. Nothing here establishes that a child chain
/// cannot reach back to its own parent, which is why callers must impose a hard depth cap rather than
/// trusting the data to terminate.</summary>
public sealed record VfxChildParticleSet(
    IReadOnlyList<VfxChildIdentifier> Children,
    /// <summary>childEmitOnDeath - spawn when the parent particle DIES rather than when it is born.
    /// Bool, and <c>true</c> in all 178 instances that author it, so absence means birth.</summary>
    bool EmitOnDeath,
    /// <summary>childrenProbability - despite the name, NOT a 0..1 probability: the census flagged that
    /// values exceed 1.0, and the measured set is {0.005, 0.1, 0.2, 0.35, 0.4, 0.5, 0.55, 1, 2, 3, 8, 10}
    /// with 108 of 131 whole numbers and a median of exactly 1.
    ///
    /// Read here as an EXPECTED COUNT, which is the one reading that covers the whole range coherently:
    /// spawn <c>floor(p)</c> children, plus one more with probability <c>frac(p)</c>. That makes 1 mean
    /// "always exactly one" (the overwhelming default), 0.5 mean "half the time", and 8 mean "eight" -
    /// all without a special case. Only 136 of 11,308 sets author it at all.</summary>
    float ExpectedCount,
    /// <summary>boneToSpawnAt - a container of bone names, present on 931 sets. Parsed and carried; the
    /// simulator has no skeleton binding for child spawns, so it is not acted on yet.</summary>
    IReadOnlyList<string> BonesToSpawnAt)
{
    /// <summary>How many children to spawn for one parent event, rolling the fractional part.</summary>
    public int RollCount(Random rng)
    {
        float p = ExpectedCount <= 0f ? 1f : ExpectedCount;
        int whole = (int)MathF.Floor(p);
        return whole + (rng.NextDouble() < p - whole ? 1 : 0);
    }
}

/// <summary>One entry of a child set. A system is named either by <see cref="EffectKey"/> (resolved
/// through the resource map) or by <see cref="EffectLink"/> (a direct object link).</summary>
public sealed record VfxChildIdentifier(uint EffectKey, uint EffectLink)
{
    public bool IsEmpty => EffectKey == 0 && EffectLink == 0;
}

/// <summary>M183 (2.5): Riot's <c>VfxBeamDefinitionData</c> (class 0x1fb8df09) - a ribbon stretched from
/// the emitter to a bound target. Measured across 239 WADs, 45,931 bins and 15,416 beam primitives.
///
/// THERE IS NO BEAM SHADER. A substring search for "beam" - and for "trail" - over every path in
/// ShaderCache.dx11.wad.client returns zero hits, and the union of all 29 shader #defines across the 140
/// shipped shaders contains nothing beam, ribbon or segment related. Riot's quad_vs consumes an
/// already-expanded WORLD-SPACE position and performs no billboard expansion at all, so a CPU-tessellated
/// ribbon is the same vertex stream a billboard quad produces. Beams are CPU geometry, which is exactly
/// why there is no beam shader to find.</summary>
public sealed record VfxBeamDefinition(
    /// <summary>mBirthTilingSize, kept as authored so the whole vector stays inspectable. Only Y - with X
    /// as a fallback - is used; see <see cref="TilingLength"/>.</summary>
    Vector3 BirthTilingSize,
    /// <summary>mLocalSpaceSourceOffset - where the beam starts, relative to the emitter. 2,846 payloads.</summary>
    Vector3 SourceOffset,
    /// <summary>mLocalSpaceTargetOffset - offset applied at the TARGET end. 1,791 payloads; 9,661 of
    /// 11,452 author none at all, which is why the target itself has to come from a runtime binding.</summary>
    Vector3 TargetOffset,
    /// <summary>mSegments; -1 = absent (94.3%). Parsed and stored, not acted on - a straight beam with N
    /// segments is geometrically identical to one with a single segment, and nothing in the payload bends,
    /// sags or perturbs it. Subdivision only buys something once per-vertex distance colour is resolved.</summary>
    int Segments,
    /// <summary>mAnimatedColorWithDistance. Parsed and stored, NOT applied: the curve's time axis is in
    /// WORLD UNITS rather than normalised life (248 of 767 curves have a maximum key time above 1, with
    /// clusters at 200/400/1400/1600/2400), and whether it is sampled per vertex or once for the whole
    /// beam is unresolved. Applying it would be a silent guess.</summary>
    VfxCurve4? AnimatedColorWithDistance,
    /// <summary>mIsColorBindedWithDistance. True in all 798 instances - no `false` exists in the corpus.</summary>
    bool IsColorBoundToDistance,
    /// <summary>mMode; -1 = absent. Only ever 1 (89 instances, all Camille R tethers). Parsed, never
    /// branched on: there is no second value to branch to.</summary>
    int Mode,
    /// <summary>mTrailMode; -1 = absent. Only ever 1 (24 instances). Parsed, never branched on.</summary>
    int TrailMode)
{
    /// <summary>The texture repeat parameter. Y IS THE LENGTH SLOT FOR A BEAM, not X.
    ///
    /// Measured on 9,834 authored vectors: Y is non-zero on 96.6%, X on only 7.3%. That is the opposite of
    /// <see cref="VfxTrailDefinition"/>, where the length lives in X - so reusing the trail's reader here
    /// would zero 96.6% of beams and silently substitute a fallback. X is kept as a secondary because 290
    /// beams have X as their only non-zero component.
    ///
    /// Z is deliberately ignored: it is only ever a small integer {1, 2, 3, 30, 100}, never a world
    /// length, and 53 of its instances are one unexplained Camille R emitter at Z=100.</summary>
    public float TilingLength =>
        MathF.Abs(BirthTilingSize.Y) > 1e-3f ? BirthTilingSize.Y : BirthTilingSize.X;

    /// <summary>How many times the texture repeats along a beam of <paramref name="beamLength"/> units.
    ///
    /// INFERRED - the sign split. Positive is read as world-units-per-repeat, negative as a repeat COUNT
    /// over the whole beam. The negative cluster is {-0.5 x21, -1 x1275, -2 x18, -3 x9}, and -0.5 is only
    /// meaningful as a count: read as a length it would mean a repeat every half unit, i.e. thousands of
    /// aliased repeats along a beam. Absent or zero gives exactly one repeat end to end, which is an
    /// editor substitute rather than a known Riot default.</summary>
    public float UvRepeats(float beamLength)
    {
        float t = TilingLength;
        if (t < -1e-3f) return MathF.Abs(t);
        if (t > 1e-3f) return beamLength / t;
        return 1f;
    }
}

/// <summary>M185 (2.15): Riot's <c>VfxLingerDefinitionData</c> (class 0x9b19f2b5) - a second, complete
/// curve set that replaces the ordinary ones while a stopped system tears down. 22,274 emitters.
///
/// THE TRIGGER IS AN EXTERNAL STOP, and that is measured rather than assumed. The obvious hypothesis -
/// that lingering happens when an emitter's own lifetime expires - is REFUTED: across 1,490,205 emitters
/// the Linger group shows no enrichment at all for a finite lifetime (80.1% vs 82.0% for the corpus), and
/// 4,665 of 23,491 Linger emitters carry no lifetime field whatsoever, so they never end on their own.
///
/// Proof by construction: AurelionSol_Skin*_W_Buff emitter "Body" authors particleLifetime = -1, no
/// lifetime, isSingleParticle, and a constant opaque colour with no dynamics. Nothing in the data can
/// ever end it. Its only fade is SeparateLingerColor going alpha 1 -> 0 over one second - because Astral
/// Flight is a gameplay-toggled buff, and the mesh overlay has to persist until the buff drops and then
/// fade. That is a stop event arriving from outside the particle system entirely.
///
/// The curves are normalised over the linger window exactly like the ordinary ones: across 12,457
/// SeparateLingerColor curves the maximum key time is 1.0 from p10 to p90, identical to the main colour
/// curve. Measured shape: start alpha median 1.0, end alpha <= 0.001 in 87.2% - it is a fade-out. The
/// 3,865 LingerScale curves start at median 1.0 and end at median 0.01 - a shrink.</summary>
public sealed record VfxLinger(
    VfxCurve4? SeparateColor,
    VfxCurve3? Scale,
    VfxCurve3? Rotation,
    VfxCurve3? KeyedVelocity,
    VfxCurve3? KeyedDrag,
    VfxCurve3? KeyedAcceleration)
{
    /// <summary>Is there anything to do during the linger window?</summary>
    public bool HasAny => SeparateColor is not null || Scale is not null || Rotation is not null
                          || KeyedVelocity is not null || KeyedDrag is not null || KeyedAcceleration is not null;
}

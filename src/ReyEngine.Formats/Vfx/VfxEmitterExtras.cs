using System.Numerics;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M193 (tier 4.1): emitter fields ReyEngine now PARSES but does not RENDER.
///
/// 43 of the 60 fields the resolver ignored are kept here so downstream code - a future renderer stage, a
/// mod validator, a diff tool - can reach them without re-walking the bin. They are deliberately raw: no
/// interpretation is applied to any value whose meaning is not established, and several of the biggest
/// (<c>miscRenderFlags</c> 484,286 occurrences, <c>depthBiasFactors</c> 183,823, <c>uvMode</c> 35,694) have
/// UNKNOWN semantics. Parking a raw value is honest; guessing at what it means would not be.
///
/// EVERY MEMBER IS NULLABLE, and that is load-bearing. Riot's writer omits default-valued properties, so
/// "absent" means "whatever the executable defaults to". Collapsing absent to <c>false</c>/<c>0</c> would
/// invent data.
///
/// <para><b>M652 corrects this class on one point.</b> It used to say that because several of these bools
/// ship with only ONE value, "their polarity is unreadable from the corpus at all". That was true when
/// M193 was written and is not any more: the meta-class database arrived afterwards (M368/M370) and
/// DECLARES each field's default, and every one of these is written only when it differs from that
/// default - so the value is the non-default one and the field's PRESENCE is the signal. Measured over 6
/// shipping map wads and 70 champion wads, against <c>data/meta/meta.db.json</c>:</para>
///
/// <list type="table">
///   <item><c>isUniformScale</c> - declared false, written true 420,772/420,772</item>
///   <item><c>isGroundLayer</c> - declared false, written true 160,406/160,406</item>
///   <item><c>isLocalOrientation</c> - declared <b>true</b>, written <b>false</b> 162,164/162,164</item>
///   <item><c>particleIsLocalOrientation</c> - declared false, written true 125,910/125,910</item>
///   <item><c>meshRenderFlags</c> - declared <b>1</b>, written <b>0</b> 77,178/77,178</item>
///   <item><c>useNavmeshMask</c> - declared false, written true 77,028/77,028</item>
///   <item><c>isRotationEnabled</c> - declared false, written true 63,857/63,857</item>
/// </list>
///
/// <para><b>Polarity is not semantics, and the semantics are still unknown.</b> Knowing that
/// <c>isGroundLayer</c> means "true" does not say what the renderer should do differently, and the corpus
/// cannot settle it: <c>isUniformScale</c> was tested directly and 89.3% of the emitters carrying it ship
/// an anisotropic scale - but so do 80.7% of the emitters without it, so the data shape does not correlate
/// with the flag. Wiring a renderer on the name alone would reshape 177,296 emitters on a guess, which is
/// the M354 mistake. The remaining route is a frame capture of the live client, and
/// <c>docs/research/renderdoc-capture-plan.md</c> explains why that is not worth an account.</para>
///
/// <para>What the correction IS worth: a later renderer stage reading <c>IsLocalOrientation ?? false</c>
/// would be wrong for every emitter that omits the field, because the declared default is TRUE - use
/// <see cref="IsLocalOrientationOrDefault"/>. It is the only one of these this class parks whose default
/// is not the zero value; <c>meshRenderFlags</c> (default 1) has the same trap but is not parked here at
/// all, so anyone adding it should bring its default with it.</para>
///
/// Init-only properties rather than positional parameters: <see cref="VfxEmitterDefinition"/> already
/// carries 71 positional params, and 43 more optional ones - all defaulted, so most misorderings would
/// still compile - is a trap rather than a record.
/// </summary>
public sealed record VfxEmitterExtras
{
    // ---- render state ------------------------------------------------------------------------------
    /// <summary>484,286 occurrences. Values 1 (480,922), 5, 3, 4, 2 - only bits 0-2 ever set. Meaning UNKNOWN.
    /// M259 measured it as the constant 1 in 99.3%, and its declared default is 0 - so unlike the flags in
    /// the class remarks this one really does carry almost nothing, and ranking the unread fields by
    /// frequency puts it third from the top for no gain.</summary>
    public int? MiscRenderFlags { get; init; }
    /// <summary>341,316. Values 3 (310,104), 1, 5, 4, 0. Meaning UNKNOWN; likely a draw-priority bucket.</summary>
    public int? Importance { get; init; }
    /// <summary>
    /// 183,823 occurrences. Meaning STILL UNKNOWN, but M653 narrowed it to one hypothesis and established
    /// why the files cannot confirm it. Not the same thing as depthPushPull, which M175 decoded from the
    /// vertex shader and the renderer does apply.
    ///
    /// <para><b>Shape</b> (6 shipping map wads + 40 champion wads, 65,197 of 487,907 emitters carry it):
    /// the two components have completely different characters. X takes 30 distinct values but is -1 in
    /// 80.0%, 0 in 8.7% and 1 in 8.7% - 97.4% inside {-1, 0, 1}, full range -110..100. Y takes 176 distinct
    /// values spread across -18000..5000 (-1, -80, -30, -50, -100, -2, -3, -5, -10, -60 …). A small
    /// signed factor beside a wide-ranging magnitude is exactly the shape of D3D11's rasterizer pair
    /// <c>SlopeScaledDepthBias</c> (a float, conventionally around 1) and <c>DepthBias</c> (an int in
    /// depth-buffer units, conventionally tens to thousands), and the field name is plural.</para>
    ///
    /// <para><b>Why that is a hypothesis and not a finding.</b> Rasterizer state never appears in shader
    /// reflection, so its absence there is consistent with the reading but is not evidence for it. What
    /// evidence there is points away from the obvious alternatives: no particle shader carries it (all
    /// four of quad_vs/quad_ps/mesh_vs/mesh_ps checked - their only depth constants are
    /// PARTICLE_DEPTH_PUSH_PULL, which a different field already feeds, and an unused NORMAL_OFFSET_BIAS);
    /// it does NOT correlate with isGroundLayer (14.8% of carriers against a 21.3% baseline, slightly
    /// anti-correlated, so it is not a decal z-fighting fix); it almost never appears beside depthPushPull
    /// (2.8%); and it is spread evenly across every primitive class, both main blend modes, and every kind
    /// of effect by name - Idle, Recall, Emote, Glow, death - with no ground-drawn cluster at all.</para>
    ///
    /// <para><b>The trap worth recording.</b> <c>mesh_ps</c> DOES declare <c>CONSTANT_DEPTH_BIAS</c> and
    /// <c>SLOPE_SCALED_DEPTH_BIAS</c> - the exact pair the plural name suggests. They are not it: both sit
    /// in <c>PerFramePixelCB</c> beside SHADOW_SAMPLE_OFFSETS and SPOT_SHADOW_SAMPLE_OFFSETS, which is
    /// per-frame shadow-map state and cannot carry a per-emitter value. A name match in the right
    /// neighbourhood is not a wiring.</para>
    /// </summary>
    public Vector2? DepthBiasFactors { get; init; }
    /// <summary>633. Meaning UNKNOWN.</summary>
    public int? RenderPhaseOverride { get; init; }
    /// <summary>1,041, always true. Meaning UNKNOWN.</summary>
    public bool? SortEmittersByPos { get; init; }
    /// <summary>98, always true.</summary>
    public bool? WriteAlphaOnly { get; init; }
    /// <summary>5,696, always true.</summary>
    public bool? DoesCastShadow { get; init; }
    /// <summary>260,835, always true. Named as though it flattens the effect onto terrain.</summary>
    public bool? IsGroundLayer { get; init; }
    /// <summary>133. Meaning UNKNOWN.</summary>
    public int? ColorblindVisibility { get; init; }
    /// <summary>2,697. A hash reference; M182 reads the separate <c>stencilRef</c>/<c>stencilMode</c> pair.</summary>
    public uint? StencilReferenceId { get; init; }
    /// <summary>7,157. A texture path the renderer does not sample.</summary>
    public string? FalloffTexture { get; init; }
    /// <summary>5,798.</summary>
    public Vector4? ModulationFactor { get; init; }
    /// <summary>5,522.</summary>
    public Vector4? CensorModulateValue { get; init; }
    /// <summary>1,145. Meaning UNKNOWN.</summary>
    public float? SliceTechniqueRange { get; init; }
    /// <summary>1,225.</summary>
    public bool? IsTexturePixelated { get; init; }

    // ---- orientation / placement -------------------------------------------------------------------
    /// <summary>694,827, always true.</summary>
    public bool? IsUniformScale { get; init; }
    /// <summary>M652: 162,164 occurrences, always FALSE - not "always true" as this line used to read,
    /// which contradicted the class remarks' own list of seven all-true bools (it never included this one).
    /// The declared default is TRUE, so an emitter that omits the field is local-oriented and one that
    /// writes it is not.</summary>
    public bool? IsLocalOrientation { get; init; }

    /// <summary>The declared default is <c>true</c>, so absent does NOT mean false here.</summary>
    public bool IsLocalOrientationOrDefault => IsLocalOrientation ?? true;
    /// <summary>217,121, always true.</summary>
    public bool? ParticleIsLocalOrientation { get; init; }
    /// <summary>1,547, always true.</summary>
    public bool? IsEmitterSpace { get; init; }
    /// <summary>108,379, always true.</summary>
    public bool? IsRotationEnabled { get; init; }
    /// <summary>21,626, always true. What a "post rotate orientation" is, is UNKNOWN.</summary>
    public bool? HasPostRotateOrientation { get; init; }
    /// <summary>1,056.</summary>
    public Vector3? PostRotateOrientationAxis { get; init; }
    /// <summary>2,318.</summary>
    public Vector3? RotationOverride { get; init; }
    /// <summary>1,779.</summary>
    public Vector3? TranslationOverride { get; init; }
    /// <summary>113.</summary>
    public Vector3? ScaleOverride { get; init; }
    /// <summary>11,767, always true.</summary>
    public bool? IsFollowingTerrain { get; init; }
    /// <summary>149,705, always true.</summary>
    public bool? UseNavmeshMask { get; init; }
    /// <summary>1,017. The constant of a ValueVector3 curve; the curve itself is not kept.</summary>
    public Vector3? BirthRotationalAcceleration { get; init; }

    // ---- emission ----------------------------------------------------------------------------------
    /// <summary>699,969 - the most frequent unread field in the schema. The constant of a ValueFloat curve.
    /// Named as though it weights how strongly a particle follows its bound bone.</summary>
    public float? BindWeight { get; init; }
    /// <summary>15,504. The constant of a ValueVector2 curve.</summary>
    public Vector2? RateByVelocityFunction { get; init; }
    /// <summary>1,225.</summary>
    public float? MaximumRateByVelocity { get; init; }
    /// <summary>7,909, always true.</summary>
    public bool? ParticlesShareRandomValue { get; init; }
    /// <summary>54,361.</summary>
    public float? DirectionVelocityScale { get; init; }
    /// <summary>11,548.</summary>
    public float? DirectionVelocityMinScale { get; init; }
    /// <summary>2,167. Emission is seeded from this mesh; the preview emits from the shape instead.</summary>
    public string? EmissionMeshName { get; init; }
    /// <summary>5,388.</summary>
    public float? EmissionMeshScale { get; init; }
    /// <summary>2,502, always true.</summary>
    public bool? UseEmissionMeshNormalForBirth { get; init; }
    /// <summary>7, always true.</summary>
    public bool? DoesLifetimeScale { get; init; }
    /// <summary>38.</summary>
    public Vector3? OffsetLifetimeScaling { get; init; }
    /// <summary>54. Meaning UNKNOWN.</summary>
    public int? OffsetLifeScalingSymmetryMode { get; init; }

    // ---- texture -----------------------------------------------------------------------------------
    /// <summary>71,374. Unity TextureWrapMode: 0=Wrap, 1=Clamp, 2=Mirror (M175). The renderer applies the
    /// PALETTE address mode but not this one.</summary>
    public int? TexAddressModeBase { get; init; }
    /// <summary>35,694. Values 2 (27,229), 1, 3, 4, 5. Meaning UNKNOWN.</summary>
    public int? UvMode { get; init; }
    /// <summary>1,442.</summary>
    public float? UvParallaxScale { get; init; }
}

/// <summary>
/// The single source of truth for which emitter fields are PARSED BUT NOT RENDERED.
///
/// <para>M192 established that "the resolver reads it" is not "the preview renders it", and that the badge
/// in the Particle Editor must not infer one from the other. So the fields <see cref="VfxEmitterExtras"/>
/// parks are declared here rather than as hash constants on <see cref="VfxSystemResolver"/>:
/// <see cref="VfxPreviewCoverage"/> reads this table and badges every field in it. Adding a field to the
/// parked model therefore badges it automatically - there is no naming convention to remember and no way
/// to park a value while silently telling the user it renders.</para>
///
/// <para>When a renderer stage starts consuming one of these, delete its entry here. That is the one edit
/// required, and forgetting it produces a spurious badge - the harmless direction.</para>
/// </summary>
public static class VfxParkedEmitterFields
{
    /// <summary>Field name -&gt; why the preview does not show it. Names are hashed with the ordinary
    /// <see cref="HashAlgorithms.Fnv1a"/>, which lowercases, so the spelling here is display-only.</summary>
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "miscRenderFlags", "importance", "depthBiasFactors", "renderPhaseOverride", "SortEmittersByPos",
        "WriteAlphaOnly", "doesCastShadow", "isGroundLayer", "colorblindVisibility", "StencilReferenceId",
        "falloffTexture", "modulationFactor", "censorModulateValue", "sliceTechniqueRange", "isTexturePixelated",
        "isUniformScale", "isLocalOrientation", "particleIsLocalOrientation", "IsEmitterSpace",
        "isRotationEnabled", "hasPostRotateOrientation", "postRotateOrientationAxis", "rotationOverride",
        "translationOverride", "scaleOverride", "isFollowingTerrain", "useNavmeshMask",
        "birthRotationalAcceleration",
        "bindWeight", "rateByVelocityFunction", "MaximumRateByVelocity", "ParticlesShareRandomValue",
        "directionVelocityScale", "directionVelocityMinScale", "emissionMeshName", "emissionMeshScale",
        "useEmissionMeshNormalForBirth", "doesLifetimeScale", "offsetLifetimeScaling",
        "offsetLifeScalingSymmetryMode",
        "texAddressModeBase", "uvMode", "uvParallaxScale",
    };

    /// <summary>The same set by hash, for <see cref="VfxPreviewCoverage"/> and the resolver.</summary>
    public static readonly IReadOnlySet<uint> Hashes =
        Names.Select(HashAlgorithms.Fnv1a).ToHashSet();
}


/// <summary>
/// M194 (tier 4.3): system-level fields ReyEngine now PARSES but does not RENDER.
///
/// <para><b>transform is deliberately not applied to the preview, and that is the main finding of this
/// item.</b> The report scheduled it as "67.7% pure scale, so it is a sizing fix". Re-derived from the raw
/// file bytes over all 4,440 authored matrices, pure scale is 29.2-29.5% - the 67.7% was the complement of
/// (rotation OR translation), which silently counts the 912 identity matrices as scale. And the engine
/// cannot express a size change through this channel at all:</para>
/// <list type="bullet">
///   <item>Sprite size is <c>BirthSize * scaleMul</c> (VfxParticleSimulator) with no world-transform term,
///     and the placement basis is re-normalised, which strips scale.</item>
///   <item><c>VfxParticleSimulator.SetWorldTransform</c> REPLACES the matrix and is re-issued every frame
///     from ViewportControl for bone-attached systems and for travelling missiles, so anything composed in
///     at build time survives until the first animated frame - and missiles are exactly the cohort the
///     report's own example (<c>Aatrox_Skin37_W_mis</c>) belongs to.</item>
/// </list>
/// <para>Applying it would therefore be a no-op that looks like a feature. The value is parsed and kept
/// here so a later milestone that fixes the re-anchor path has it available.</para>
///
/// <para>Matrix convention, proven rather than assumed: for 520/520 translation-bearing systems the 16 raw
/// floats on disk match <c>M11..M44</c> in sequential order (0/520 match the transposed order), with
/// floats 3/7/11 zero and float 15 one - so floats 12/13/14 are the translation, which is what
/// <c>System.Numerics.Matrix4x4.Translation</c> returns. Independently, 29,805 of 29,809 map particle
/// placements have a 4th ROW magnitude &gt; 1 and 0 of 29,809 have a 4th COLUMN one.</para>
/// </summary>
public sealed record VfxSystemExtras
{
    /// <summary>2,400 champion systems (2,040 more map-side). Authored TRS; see the class remarks for why
    /// the preview does not consume it. Measured decomposition of the champion set: 912 identity, 702-708
    /// pure scale (the 6-case spread is pure-mirror matrices, which one method calls scale and another
    /// rotation), 520 with translation, 345 with rotation.</summary>
    public Matrix4x4? Transform { get; init; }
    /// <summary>74,589 - the most frequent unread system field. A U16 bitfield of UNKNOWN meaning.</summary>
    public int? Flags { get; init; }
    /// <summary>6,361. Caps how far the system may be scaled up; the preview does not scale systems.</summary>
    public float? OverrideScaleCap { get; init; }
    /// <summary>1,147. Seconds; meaning inferred from the name only.</summary>
    public float? BuildUpTime { get; init; }
    /// <summary>1,139.</summary>
    public bool? ScaleDynamicallyWithAttachedBone { get; init; }
    /// <summary>410 / 104. Voice-over events; the preview has no audio.</summary>
    public string? VoiceOverOnCreateDefault { get; init; }
    public string? VoiceOverPersistentDefault { get; init; }
    /// <summary>303.</summary>
    public bool? IsPoseAfterimage { get; init; }
    /// <summary>176. The system-level twin of the map placement's eyeCandy flag.</summary>
    public bool? EyeCandy { get; init; }
    /// <summary>74 / 10. HUD anchoring; ReyEngine's HUD editor is a separate surface.</summary>
    public bool? HudAnchorPositionFromWorldProjection { get; init; }
    public float? HudLayerDimension { get; init; }
    /// <summary>43. Meaning UNKNOWN.</summary>
    public int? DrawingLayer { get; init; }
    /// <summary>42 / 41. Audio parameter plumbing; the preview has no audio.</summary>
    public int? AudioParameterFlexId { get; init; }
    public float? AudioParameterTimeScaledDuration { get; init; }
    /// <summary>7. Meaning UNKNOWN.</summary>
    public int? ClockToUse { get; init; }
    /// <summary>1.</summary>
    public float? SelfIllumination { get; init; }
    /// <summary>983 systems carry an assetRemappingTable and 336 a materialOverrideDefinitions container.
    /// Only their PRESENCE is recorded: modelling their contents is a separate item, and a count is honest
    /// where an empty list would imply we had read them.</summary>
    public int AssetRemapCount { get; init; }
    public int MaterialOverrideCount { get; init; }
}

/// <summary>System-level counterpart of <see cref="VfxParkedEmitterFields"/>; same contract - being in this
/// table is what makes <see cref="VfxPreviewCoverage"/> badge the field.</summary>
public static class VfxParkedSystemFields
{
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "transform", "flags", "overrideScaleCap", "buildUpTime", "scaleDynamicallyWithAttachedBone",
        "voiceOverOnCreateDefault", "voiceOverPersistentDefault", "mIsPoseAfterimage", "mEyeCandy",
        "hudAnchorPositionFromWorldProjection", "hudLayerDimension", "drawingLayer",
        "audioParameterFlexID", "audioParameterTimeScaledDuration", "ClockToUse", "selfIllumination",
        "assetRemappingTable", "materialOverrideDefinitions",
    };

    public static readonly IReadOnlySet<uint> Hashes = Names.Select(HashAlgorithms.Fnv1a).ToHashSet();
}

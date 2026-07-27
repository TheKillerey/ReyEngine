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
/// "absent" means "whatever the executable defaults to" - which for most of these is unknown. Collapsing
/// absent to <c>false</c>/<c>0</c> would invent data. Seven of the bools here ship ONLY <c>true</c>
/// (isUniformScale, isGroundLayer, useNavmeshMask, particleIsLocalOrientation, isRotationEnabled,
/// hasPostRotateOrientation, doesCastShadow), so their polarity is unreadable from the corpus at all.
///
/// Init-only properties rather than positional parameters: <see cref="VfxEmitterDefinition"/> already
/// carries 71 positional params, and 43 more optional ones - all defaulted, so most misorderings would
/// still compile - is a trap rather than a record.
/// </summary>
public sealed record VfxEmitterExtras
{
    // ---- render state ------------------------------------------------------------------------------
    /// <summary>484,286 occurrences. Values 1 (480,922), 5, 3, 4, 2 - only bits 0-2 ever set. Meaning UNKNOWN.</summary>
    public int? MiscRenderFlags { get; init; }
    /// <summary>341,316. Values 3 (310,104), 1, 5, 4, 0. Meaning UNKNOWN; likely a draw-priority bucket.</summary>
    public int? Importance { get; init; }
    /// <summary>183,823. Both components continuous. Meaning UNKNOWN - not the same thing as depthPushPull,
    /// which M175 decoded from the vertex shader and the renderer does apply.</summary>
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
    /// <summary>265,968, always true.</summary>
    public bool? IsLocalOrientation { get; init; }
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

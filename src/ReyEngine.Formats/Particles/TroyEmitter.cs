using System.Numerics;

namespace ReyEngine.Formats.Particles;

/// <summary>
/// One legacy emitter, read from the decoded body by key rather than guessed at.
///
/// <para>Every field here is bound to THIS emitter by construction, because the key is
/// <c>sdbm(lowercase(emitterName + fieldName))</c>. That is what retires the converter's
/// filename-similarity heuristics: measured binding rates are 91.2% of textures (4,995 of 5,475), 98.3%
/// of colour ramps (1,000 of 1,017) and 88.5% of meshes (757 of 855), and of the string references that
/// do bind, <b>0 of 13,921 are ambiguous</b> - each one resolves to exactly one emitter.</para>
///
/// <para>Nulls mean the field was absent, which is normal: the format elides defaults.</para>
/// </summary>
public sealed record TroyEmitter(
    string Name,
    string? TexturePath,
    string? ColorTexturePath,
    string? TextureMultPath,
    string? MeshPath,
    float? Rate,
    float? ParticleLifetime,
    float? EmitterLifetime,
    float? Scale,
    int? FrameCount,
    float? FrameRate,
    int? StartFrame,
    int? ParticleType,
    Vector3? Velocity,
    Vector3? Acceleration,
    Vector3? WorldAcceleration,
    Vector3? Offset,
    Vector3? Drag,
    Vector3? OrbitalVelocity,
    Vector3? ScaleVector,
    TroyProbability? VelocitySpread = null,
    TroyProbability? OffsetSpread = null,
    TroyProbability? ScaleSpread = null,
    IReadOnlyList<TroyEmitRotation>? EmitRotations = null,
    System.Numerics.Vector2? TexDiv = null,
    /// <summary>M520: the names of the force fields this emitter pulls in, in reference order. Look
    /// them up in <see cref="TroyBinFile.ForceFields"/>.</summary>
    IReadOnlyList<string>? FieldReferences = null,
    /// <summary>How long the emitter keeps emitting after being told to stop.</summary>
    float? EmitterLinger = null,
    /// <summary>How long a particle hangs around past its lifetime. Riot writes it as
    /// <c>particleLinger: option[f32]</c>.</summary>
    float? ParticleLinger = null,
    /// <summary>M520: <c>e-active</c>, RAW and uninterpreted.
    ///
    /// <para>It is not the on/off flag it looks like. Measured over the corpus it is present on only
    /// 270 emitters, is <b>never</b> 0, and 192 of the 270 hold a value that is neither 0 nor 1 - so
    /// reading it as a bool would invent a meaning the data does not support. Left raw until something
    /// pins it down.</para></summary>
    float? EmitterActive = null,
    /// <summary>Render-order bucket: -1 behind, 1 in front.</summary>
    int? Pass = null,
    int? RenderMode = null,
    /// <summary>M521: <c>*e-rateP{n}</c>. Riot's own conversion writes it as the rate's
    /// <c>probabilityTables[0]</c>, so an emitter that bursts (rate 10, table 0/0.98/1 -> 0/0/2) reads
    /// as a steady trickle without it.</summary>
    TroyProbability? RateSpread = null,
    /// <summary>M521: <c>*p-lifeP{n}</c>. Without it every particle of an emitter dies at exactly the
    /// same age, which is what makes a converted puff look like a shutter rather than a fade.</summary>
    TroyProbability? LifetimeSpread = null,
    /// <summary>
    /// M521: scale over life, from <c>*p-xscale1..4</c>.
    ///
    /// <para>The one curve in this format that is NOT a P{n} probability table - the keys are numbered
    /// directly and each is (time, scale). Riot converts it to <c>scale0</c>, a ValueVector3 carrying
    /// only <c>dynamics.times</c>/<c>values</c> and no constant, which is the multiplier applied over
    /// the particle's life.</para></summary>
    IReadOnlyList<(float Time, System.Numerics.Vector3 Scale)>? ScaleOverLife = null,
    /// <summary>
    /// M521: <c>*p-xscale</c> - the MULTIPLIER for <see cref="ScaleOverLife"/>, not an enable flag.
    ///
    /// <para>It reads like a flag because the one file with a text twin has <c>p-xscale=1</c>. It is
    /// not: SRU_Lane_Motes has <c>(20,20,20)</c> against curve keys of 0.2/1/0.2, and Riot's own
    /// converted <c>scale0</c> for it holds 4/20/4 - exactly the product. SRU_Chaos_Shopkeeper_smoke
    /// agrees at <c>1.75</c>. Left as a separate value here because the file stores it separately;
    /// the converter is what folds it in, because folding it in is Riot's rule and not the
    /// format's.</para></summary>
    System.Numerics.Vector3? ScaleMultiplier = null,
    /// <summary>M525: colour over life, read BY KEY from <c>*p-xrgba{n}</c> - so it belongs to this
    /// emitter by construction rather than by the positional guess the converter used to make.</summary>
    IReadOnlyList<(float Time, System.Numerics.Vector4 Color)>? ColorOverLife = null,
    /// <summary>M525: <c>*p-xrgba</c>, the vec4 multiplier for <see cref="ColorOverLife"/>.</summary>
    System.Numerics.Vector4? ColorMultiplier = null,
    /// <summary>M534: <c>*p-normal-map</c>. A heat haze is a REFRACTION, not a coloured sprite - it
    /// perturbs what is behind it using this normal map. Without it the emitter falls onto the ordinary
    /// billboard path and draws its sprite, which for these effects is <c>color-hold</c>: a deliberate
    /// 8x8 all-white card. That is the reported "HeatHaze is white".</summary>
    string? NormalMapPath = null,
    /// <summary><c>*p-distortion-mode</c>, Riot's <c>distortionMode</c>.</summary>
    float? DistortionMode = null,
    /// <summary><c>*p-distortion-power</c>, Riot's <c>distortion</c>. Small: 0.02 to 0.1 in the torches
    /// and the cauldron.</summary>
    float? DistortionPower = null,
    /// <summary>M534: the probability table on <c>*p-quadrot</c>. Birth rotation is a RANGE, not a value -
    /// env_fall_leaves says "uniform 0..360" - and collapsing it to the constant gave every particle the
    /// same orientation, which reads as the whole effect moving in one direction.</summary>
    TroyProbability? QuadRotationSpread = null,
    /// <summary>M534: the probability table on <c>*p-rotvel</c>, the per-particle spin rate.</summary>
    TroyProbability? RotationVelocitySpread = null,
    /// <summary>M534: the probability table on <c>*p-postoffset</c>.</summary>
    TroyProbability? PostOffsetSpread = null)
{
    /// <summary>An <c>*e-life</c> of -1 means the emitter runs forever.
    /// Torches, auras and buff loops all use it.</summary>
    public bool Loops => EmitterLifetime is { } l && l < 0f;

    /// <summary>A flipbook sheet, not a single sprite. Rendering one frame-atlas texture as a plain
    /// quad is what made converted effects look like undifferentiated blobs.</summary>
    public bool IsFlipbook => FrameCount is > 1;
}

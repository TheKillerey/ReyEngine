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
    int? ParticleType)
{
    /// <summary>An <c>*e-life</c> of -1 means the emitter runs forever.
    /// Torches, auras and buff loops all use it.</summary>
    public bool Loops => EmitterLifetime is { } l && l < 0f;

    /// <summary>A flipbook sheet, not a single sprite. Rendering one frame-atlas texture as a plain
    /// quad is what made converted effects look like undifferentiated blobs.</summary>
    public bool IsFlipbook => FrameCount is > 1;
}

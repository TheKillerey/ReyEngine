namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M719: <c>texAddressModeBase</c>, in the engine's own <c>ParticleSystem::TEXTUREADDRESS</c> - 0 WRAP,
/// 1 MIRROR, 2 CLAMP, 3 BORDER (ltk-manager's plan, section 3.2).
///
/// <para>That is NOT the order the erosion and palette fields reach the sampler in. Those are copied to the
/// sampler unremapped, and the sampler's own enum swaps mirror and clamp - 0 wrap, 1 clamp, 2 mirror, which
/// M184 read off Riot's named shared samplers and M717 honours for the erosion map. The note parked on this
/// field since M175 guessed Unity's order and read 2 as a mirror. The corpus sides with the engine's name:
/// 68% of the emitters that hold their scroll ramp with uvScrollClamp author a 2, and a held scroll over a
/// clamped edge is a wipe.</para>
///
/// <para>It is what replaces the whole-coordinate clamp the GL viewport used to apply: reading 2.11 clamps
/// only the birth ramp, and a sprite is held at its edge by its own address mode. Both renderers bind
/// samplers in the sampler's enum, so this converts. BORDER folds to CLAMP - neither renderer carries a
/// border sampler and GLES 3.0 has none - which is recorded rather than built.</para>
/// </summary>
public static class VfxTextureAddress
{
    public const int Wrap = 0, Mirror = 1, Clamp = 2, Border = 3;

    /// <summary>The sampler-enum mode (0 wrap, 1 clamp, 2 mirror) a texture address draws with. Absent is
    /// the declared default, WRAP; so is any value past the enum.</summary>
    public static int SamplerModeOf(int? textureAddress) => textureAddress switch
    {
        Mirror => 2,
        Clamp or Border => 1,
        _ => 0,
    };
}

namespace ReyEngine.Formats.Materials;

/// <summary>How a map is lit, as one choice rather than three separate steps.</summary>
public enum MapLightingMode
{
    /// <summary>
    /// Dynamic lights only. Every material that can take it gets <c>NO_BAKED_LIGHTING</c>, so nothing
    /// samples a baked lightmap and the map is lit by the sun and the point lights at runtime.
    ///
    /// <para>This is the mode a freshly ported legacy map wants: it has no lightmap atlases, and a
    /// material that samples one it does not have has nothing to sample.</para>
    /// </summary>
    Dynamic,

    /// <summary>
    /// Baked lightmaps, using the atlases the map already has. Clears <c>NO_BAKED_LIGHTING</c> so the
    /// materials sample them again, and bakes nothing.
    /// </summary>
    Baked,

    /// <summary>
    /// Baked lightmaps, generated now. The same material change as <see cref="Baked"/>, then the bake -
    /// which is the step that makes the difference between "the materials are asking for a lightmap"
    /// and "there is a lightmap for them to ask for".
    /// </summary>
    BakeNow,
}

/// <summary>
/// What a lighting mode actually does, kept apart from the UI so the mapping can be stated once and
/// tested (M530).
///
/// <para><b>The macro reads backwards from its name and that has cost time before.</b>
/// <c>NO_BAKED_LIGHTING</c> ON is the DYNAMIC mode - it tells the shader to skip the baked lightmap.
/// OFF is the baked mode. Both directions ask the client for a define set it may never have cooked, so
/// neither is safe to apply blindly: clearing it on Map11/base_srx left 20 of 184 materials with no
/// cooked permutation (M166), and setting it across 78 DefaultEnv_Flat_AlphaTest materials produced
/// "Unable to find correct hash for shader" and a map that rendered nothing (M486). The caller must
/// keep the per-material permutation check; this type only says which direction to ask for.</para>
/// </summary>
public sealed record MapLightingPlan(bool NoBakedLighting, bool RunBake, string Label, string Explanation)
{
    public static MapLightingPlan For(MapLightingMode mode) => mode switch
    {
        MapLightingMode.Dynamic => new(true, false, "Dynamic lights",
            "Every material stops sampling a baked lightmap, so the map is lit by the sun and its point "
            + "lights at runtime. This is what a freshly ported legacy map wants - it has no lightmap "
            + "atlases to sample."),

        MapLightingMode.Baked => new(false, false, "Baked lightmaps (use existing)",
            "Materials sample their baked lightmap again. Nothing is baked, so this is for a map whose "
            + "atlases already exist - on a map without them it asks for a texture that is not there."),

        MapLightingMode.BakeNow => new(false, true, "Baked lightmaps (bake now)",
            "Materials sample their baked lightmap, and the bake runs so there is one to sample."),

        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>Every mode, in the order a menu should show them.</summary>
    public static IReadOnlyList<MapLightingMode> All { get; } = new[]
    {
        MapLightingMode.Dynamic, MapLightingMode.Baked, MapLightingMode.BakeNow,
    };
}

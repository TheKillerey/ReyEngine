using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M648: what the preview makes of an emitter's <c>primitive</c> class.
///
/// <para>An emitter's <c>primitive</c> decides its whole shape - a camera-facing billboard, a mesh, a
/// motion trail, a beam - and 82,862 of the 210,770 primitives measured across 6 shipping map WADs and 25
/// champion WADs are structs with NO fields at all: the class name is the entire content. The particle
/// editor used to render exactly those as "Read-only property (unsupported type or Riot reference)",
/// which hid the single most important fact about the emitter behind a message about type support.</para>
///
/// <para>This is the honest half of fixing that: naming the class is easy, but the useful question is
/// whether the preview will DRAW that shape or quietly fall back to a billboard. Every entry below is
/// taken from a branch that exists in <see cref="VfxSystemResolver"/> and is consumed by a renderer, not
/// from the class list - a class the resolver merely mentions is not a class the preview draws.</para>
/// </summary>
public static class VfxPrimitiveSupport
{
    /// <summary>Classes the preview gives their own geometry, and the flag that carries each one.</summary>
    private static readonly Dictionary<uint, string> Drawn = new()
    {
        [HashAlgorithms.Fnv1a("VfxPrimitiveMesh")] = "drawn as its mesh",
        [HashAlgorithms.Fnv1a("VfxPrimitiveBeam")] = "drawn as a beam ribbon",
        [HashAlgorithms.Fnv1a("VfxPrimitiveCameraTrail")] = "drawn as a camera-facing trail",
        [HashAlgorithms.Fnv1a("VfxPrimitiveArbitraryTrail")] = "drawn as a world-oriented trail",
        [HashAlgorithms.Fnv1a("VfxPrimitiveArbitraryQuad")] = "drawn as a world-oriented quad",
    };

    /// <summary>VfxPrimitiveAttachedMesh only gains geometry when it names a mesh FILE. Most instances
    /// carry a submesh mask of the host model instead and stay billboards, which is a property of the
    /// individual emitter rather than of the class - so it gets its own sentence.</summary>
    private static readonly uint AttachedMesh = HashAlgorithms.Fnv1a("VfxPrimitiveAttachedMesh");

    private static readonly uint Mesh = HashAlgorithms.Fnv1a("VfxPrimitiveMesh");

    /// <summary>
    /// M717: this emitter is drawn by the client through <c>quad_ps_fixedalphauv</c>, which compiles
    /// neither the alpha-erosion stage nor the soft-particle fade.
    ///
    /// <para>Read off Riot's own shader cache rather than inferred: that shader ships 64 permutations over
    /// exactly ALPHA_TEST, COLORPALETTE_COLORBLIND, DISABLE_FOW, MASKED, MULT_PASS and PALETTIZE_TEXTURES.
    /// There is no ALPHA_EROSION axis and no SOFT_PARTICLES axis in its table of contents at all, so an
    /// emitter routed to it cannot erode and cannot fade however much it authors.</para>
    ///
    /// <para>The condition is <c>uvMode</c> 2 - LOCK_ALPHA in the engine's own UV_MODE enum, where 0 is
    /// the default, 1 screen space, and 3 to 5 the three local-space modes - on anything that is not a
    /// mesh. The mesh kinds keep their erosion because Riot's <c>mesh_ps</c> carries its own
    /// SEPARATE_ALPHA_UV axis that coexists with ALPHA_EROSION across 1,024 shipped permutations, so the
    /// asymmetry is in the shader set rather than a convenience.</para>
    /// </summary>
    public static bool DrawsFixedAlphaUv(int? uvMode, uint primitiveClass) =>
        uvMode == LockAlphaUvMode && primitiveClass != Mesh && primitiveClass != AttachedMesh;

    /// <summary>The engine's <c>UV_MODE.lockAlpha</c>. 0 is the default and is written zero times.</summary>
    public const int LockAlphaUvMode = 2;

    /// <summary>League does not render this one at all. Worth its own note: falling back to a billboard
    /// here does not degrade the picture, it INVENTS geometry the game never draws.</summary>
    private static readonly uint NonRenderable = HashAlgorithms.Fnv1a("VfxPrimitiveNonRenderable");

    /// <summary>
    /// What the preview does with this primitive class, or null when there is nothing to warn about
    /// (the class is drawn as itself).
    /// </summary>
    /// <remarks>An emitter with NO primitive is the ordinary case - a camera-facing billboard - and is
    /// not this method's business; callers only ask about a class that is actually written.</remarks>
    public static string? DegradeNote(uint classHash)
    {
        if (Drawn.ContainsKey(classHash)) return null;
        if (classHash == AttachedMesh)
            return "Attached mesh: the preview draws it as a mesh only when this primitive names a mesh file. "
                 + "The common case carries a submesh mask of the host model instead and stays a billboard.";
        if (classHash == NonRenderable)
            return "League draws nothing for this primitive. The preview has no case for it and falls back to a "
                 + "camera-facing billboard, so it shows something the game does not.";
        return "The preview has no case for this primitive and falls back to a camera-facing billboard.";
    }

    /// <summary>A short phrase for a class the preview draws as itself; null for anything else.</summary>
    public static string? DrawnAs(uint classHash) => Drawn.GetValueOrDefault(classHash);
}

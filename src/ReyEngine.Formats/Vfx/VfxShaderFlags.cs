namespace ReyEngine.Formats.Vfx;

/// <summary>
/// <para>M232: which shader defines an emitter's authored flags select, for
/// <c>assets/shaders/hlsl/particlesystem/quad_vs</c> + <c>quad_ps</c>.</para>
///
/// <para>Every entry below was derived by flipping one axis at a time on the cooked TOC and diffing the
/// reflected interface — the shader states its own feature set, so nothing here is inferred from a field
/// name. The pixel stage has 8 axes / 256 permutations, the vertex stage 4 / 16:</para>
///
/// <list type="table">
/// <listheader><term>define</term><description>what the permutation gains or loses</description></listheader>
/// <item><term>DISABLE_FOW</term><description>PS drops <c>FOW_MAP_SharedTexture</c>; VS drops <c>FOG_OF_WAR_PARAMS</c></description></item>
/// <item><term>MASKED</term><description>PS gains <c>NAVMESH_MASK_TEXTURE_SharedTexture</c>; VS gains <c>NAV_GRID_XFORM</c> — so MASKED is the NAVMESH mask, which no field name would have told us</description></item>
/// <item><term>SOFT_PARTICLES</term><description>gains <c>sDepthTexture_SharedTexture</c>, <c>cSoftParticleParams</c>, <c>cSoftParticleControl</c>, <c>cDepthConversionParams</c></description></item>
/// <item><term>PALETTIZE_TEXTURES</term><description>gains <c>sPalettesTexture__TX</c>, <c>cPaletteSelectMain</c>, <c>cPaletteSrcMixerMain</c></description></item>
/// <item><term>ALPHA_EROSION</term><description>gains <c>sAlphaErosionTexture__TX</c> + params, and <b>loses</b> <c>PARTICLE_COLOR_TEXTURE__TX</c></description></item>
/// <item><term>ALPHA_TEST</term><description>gains <c>AlphaTestReferenceValue</c></description></item>
/// <item><term>MULT_PASS</term><description>PS gains <c>TEXTUREMULT__TX</c>; VS gains a second <c>TEXTURE_INFO_2</c> atlas descriptor</description></item>
/// <item><term>COLORPALETTE_COLORBLIND</term><description>gains <c>APPLY_TEAM_COLOR_CORRECTION</c> — a client accessibility setting, not emitter data, so it is never set from an emitter</description></item>
/// </list>
/// </summary>
public static class VfxShaderFlags
{
    /// <summary>The define set <paramref name="e"/> selects. Only presence matters — these are all
    /// presence/absence axes with the single value "1", so a disabled feature is an ABSENT key, never
    /// <c>NAME=0</c> (the resolver treats those differently, and getting it backwards resolves nothing).</summary>
    public static Dictionary<string, string> For(VfxEmitterDefinition e, out List<string> why)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reasons = new List<string>();

        void Set(string name, string reason) { d[name] = "1"; reasons.Add($"{name}  <- {reason}"); }

        // ALPHA_EROSION — the dissolve stage. 307,050 emitters (22.0%) author this struct.
        if (e.AlphaErosion is not null)
            Set("ALPHA_EROSION", "alphaErosionDefinition is present");

        // SOFT_PARTICLES — depth-fade against the scene. 95,671 emitters (6.8%).
        if (e.SoftParticle is not null)
            Set("SOFT_PARTICLES", "softParticleParams is present");

        // PALETTIZE_TEXTURES — RGB remapped through a gradient strip. 43,621 emitters (3.1%).
        if (e.Palette is not null)
            Set("PALETTIZE_TEXTURES", "paletteDefinition is present");

        // MULT_PASS — the second, multiplied texture stage.
        if (!string.IsNullOrEmpty(e.TextureMultPath))
            Set("MULT_PASS", "textureMult supplies a texture");

        // MASKED — clip against the navmesh. Named by the texture the define adds, not by the field.
        if (e.Extras?.UseNavmeshMask == true)
            Set("MASKED", "useNavmeshMask is set");

        // ALPHA_TEST — only meaningful with a non-zero cutoff. BIN writes alphaRef as an explicit 0 on
        // 391,078 emitters, so "authored" does not imply "enabled"; the VALUE is what decides.
        if (e.AlphaRef > 0)
            Set("ALPHA_TEST", $"alphaRef = {e.AlphaRef}");

        why = reasons;
        return d;
    }

    /// <summary>
    /// <para>Whether this emitter's <c>blendMode</c> means additive.</para>
    ///
    /// <para><b>This is a guess, and deliberately the same guess the GL renderer already makes</b> so the two
    /// previews cannot disagree. The integer→blend-state table is not in shipped data: <c>shaders.bin</c>
    /// has no blend-related field on any of its 347 <c>CustomShaderDef</c>s, and <c>quad_ps</c> declares no
    /// blend state either — it lives in the executable. Modes 6/7/8 (258 emitters) fall off the end of this
    /// list rather than being decided, and an absent blendMode (110,540 emitters, 7.9%) defaults to 1.</para>
    /// </summary>
    public static bool IsAdditive(int blendMode) => blendMode is 1 or 3 or 4 or 5;
}

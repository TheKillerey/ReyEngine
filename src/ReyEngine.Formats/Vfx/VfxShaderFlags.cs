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
    /// <para>Whether this emitter's <c>blendMode</c> means additive, ignoring its texture.</para>
    ///
    /// <para><b>This is a guess, and deliberately the same guess the GL renderer already makes</b> so the two
    /// previews cannot disagree. The integer→blend-state table is not in shipped data: <c>shaders.bin</c>
    /// has no blend-related field on any of its 347 <c>CustomShaderDef</c>s, and <c>quad_ps</c> declares no
    /// blend state either — it lives in the executable. Modes 6/7/8 (258 emitters) fall off the end of this
    /// list rather than being decided, and an absent blendMode (110,540 emitters, 7.9%) defaults to 1.</para>
    ///
    /// <para>Modes 2 and 3 are NOT decidable from the integer alone — see the overload below. This one is
    /// what they fall back to when the sprite is unknown, so mode 2 answers Alpha here.</para>
    /// </summary>
    public static bool IsAdditive(int blendMode) => blendMode is 1 or 3 or 4 or 5;

    /// <summary>
    /// <para>M273: the effective blend, given the emitter's sprite. <b>Modes 2 and 3 are mixed populations
    /// and the alpha channel decides which cluster a given emitter is in</b>; every other mode uses the flat
    /// table above. Pass <c>null</c> for <paramref name="textureHasAlpha"/> when the sprite is not known -
    /// that falls back to the texture-blind answer rather than guessing.</para>
    ///
    /// <para>Mode 3 has worked this way since M117c (Kayn's R scythe flipbooks are dark-background with no
    /// alpha and must be additive; skin02's R ghost reuses the skin's TX_CM which has real alpha and washes
    /// out when added). M273 extends it to mode 2 because mode 2 is the same shape of problem, and the two
    /// bodies of evidence that looked contradictory are really describing the two clusters:</para>
    ///
    /// <list type="bullet">
    /// <item>M117 assigned 2 -> Alpha because Kayn's <c>BlackMotes</c> and <c>Darkunderglow_*</c> are DARK
    /// on-screen effects, which additive cannot produce. Re-measured: those sprites are 99.6%, 99.7% and
    /// 100.0% alpha-varied, so they stay Alpha under this rule. The observation is preserved, not overruled.</item>
    /// <item>M260's authoring census classified 7,310 mode-2 emitters as 65% additive / 5% straight - a
    /// mixture no single boolean fits.</item>
    /// <item>M272 measured Map22 <c>impactStones_smoke</c> (mode 2) painting solid black rectangles: over a
    /// mid-grey clear it darkened 100.0% of the 3,271 px it covered, worst channel drop 127 (grey to black),
    /// while the identical quads rendered additively darkened 0 px. Its sprite,
    /// <c>TFT_PDM_Cosmic_Spark_2x2</c>, is 0.0% alpha-varied / 100% opaque / 100% dark border - textbook
    /// additive art carrying no silhouette at all, which is why alpha blending painted its whole black
    /// field. It flips to additive here.</item>
    /// </list>
    ///
    /// <para>Over the corpus this cuts mode 2 into 67% of textures (62% of emitters) additive and 33% (38%)
    /// alpha, which independently reproduces M260's 65% additive from a different measurement - that census
    /// classified by border darkness and this rule by alpha variance, and they agree.</para>
    ///
    /// <para>Still a guess about Riot's actual pipeline state, and a capture is still the thing that would
    /// settle it. What changed is that it is now a guess no measurement we hold contradicts.</para>
    /// </summary>
    public static bool IsAdditive(int blendMode, bool? textureHasAlpha) =>
        blendMode is 2 or 3 && textureHasAlpha is { } hasAlpha ? !hasAlpha : IsAdditive(blendMode);

    /// <summary>
    /// <para>Does this sprite use its alpha channel at all? The M117c predicate, and now the ONE copy of it -
    /// the GL renderer inlined this and the D3D11 path had no equivalent, so extending the rule to mode 2
    /// meant the two could have drifted on the threshold.</para>
    ///
    /// <para>"Uses alpha" is >1% of pixels below 250, not "any pixel below 255": DXT/BC encoders leave a
    /// scatter of 254s on art whose alpha is authored flat, so an exact test reads compression noise as a
    /// silhouette. Measured on the mode-2 corpus the two clusters are nowhere near the threshold - the
    /// additive ones sit at 0.0% varied and the alpha ones at 97.9-100.0% - so the exact cutoff is not load
    /// bearing. The only borderline cases found were <c>Kayle_Skin24_Light</c> (2.3%) and
    /// <c>Morgana_Skin50_Light</c> (1.6%), which stay Alpha, i.e. keep today's behaviour.</para>
    /// </summary>
    /// <param name="rgba">Tightly packed RGBA8, which is what both renderers decode to.</param>
    public static bool TextureUsesAlpha(ReadOnlySpan<byte> rgba)
    {
        int pixels = rgba.Length / 4, varied = 0;
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] < 250) varied++;
        return varied > pixels / 100;
    }
}

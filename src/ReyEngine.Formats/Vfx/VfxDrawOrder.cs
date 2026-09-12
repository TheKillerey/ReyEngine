namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M709: what order a system's emitters draw in.
///
/// <para><b>The engine reading.</b> Taken back from ltk-manager's particle renderer, whose
/// <c>compareDrawOrder</c> is five keys deep: the ground layer first, then <c>pass</c>, then a rank derived
/// from the blend mode, then the whole <c>miscRenderFlags</c> byte, then the emitter's own authored index.
/// The screen case that settled the first key is <c>AurelionSol_Skin11_E_ExecuteZone_ChildParticle</c>,
/// where <c>BG_BrighterInterior5</c> (pass 599, ground layer) drew over the rocks of
/// <c>REFLECTION_SPHERE2</c> and <c>REFLECTION_SPHERE4</c> (passes 102 and 103). An emitter carrying
/// <c>isGroundLayer</c> goes to the engine's <c>Render_Ground_Layer</c> display list, which draws before
/// the default one, and <c>pass</c> orders emitters inside a list only. Before this, both of our renderers
/// sorted on <c>pass</c> alone and a ground-layer emitter authored high drew on top of everything.</para>
///
/// <para><b>Two of the five keys, deliberately.</b> Key 3 is a rank indexed by the blend mode, and our
/// blend table disagrees with the engine's on the most common value: <see cref="VfxShaderFlags.IsAdditive"/>
/// treats mode 1 as additive where the engine's enum calls it ALPHA, and its own doc comment calls our
/// table a guess. Porting a key that indexes a table we know to be wrong would be worse than not porting
/// it, so keys 3 and 4 wait on the blend-mode reading. Key 5 is the authored index, which we already get
/// for free: every caller uses <c>OrderBy</c>, a STABLE sort, over a list still in authored order.</para>
///
/// <para><b>Scope: one system.</b> Both of our renderers order the emitters of a single simulator, and so
/// does the reference renderer - a child system's emitters rank after the whole of their parent's, and two
/// placements are not ordered against each other at all. The engine's comparator has a sixth key, comparing
/// the system's own position, that neither of us implements. So "the ground layer draws first" is true
/// inside one system instance and is not a claim about the frame.</para>
///
/// <para><b>The stencil exclusion, measured rather than reasoned.</b> An emitter with a stencil mode is
/// never promoted. Our GL renderer emulates Riot's stencil masking by drawing a mode-1 WRITER before the
/// mode-2/3 TESTERS that read its mask, and <c>VfxParticleRenderer.ApplyStencil</c> says in its own remarks
/// that this works only because emitters draw in pass order. Promoting a tester past its writer makes a
/// mode-2 emitter test against a mask nobody has written, which draws NOTHING - a disappearance, not a
/// reshuffle. Over the installed game: 1,908 system occurrences hold both a writer and a tester, and in 19
/// of them the promotion would separate a pair, costing 49 tester emitters (44 of them mode 2) their
/// visuals - Aatrox_Skin30_Q_cas3, Kaisa_Skin69_E_active, LeeSin_Skin31_W_shield_self,
/// Mordekaiser_Skin58_R_Arena03 and Nunu_Skin16_R_Cas_Child_stage among them. Excluding stencil emitters
/// takes that to zero and costs 20,054 of 333,296 ground-layer emitters (6%) their promotion. The exclusion
/// protects OUR emulation's precondition, which is an inference; the alternative is to compound it with a
/// second one and lose 44 effects. It is a divergence, and this is where it is written down.</para>
///
/// <para><b>What it moves, and it is not nothing.</b> 52,223 of 198,195 reachable system occurrences
/// (26.4%) change order, moving 500,497 emitters. An earlier reading of the numbers called that visually
/// inert because 99.5% of the movers are additive under our own blend table - but that table is the one
/// above, the corpus favours the engine's enum (blend mode 0 is authored zero times in 1,581,956 emitters,
/// which under Riot's omit-the-default rule makes 0 the default and the engine calls 0 "add"), and under
/// the engine's enum 45.4% of the movers are order-dependent. Separately, 58,114 movers (11.6%) carry
/// distortion, soft particles or a stencil mode and are order-dependent whatever the blend. This is a
/// visible change to half a million emitter draws. <see cref="GroundLayerFirst"/> is how it gets turned
/// off and compared, rather than argued about.</para>
/// </summary>
public static class VfxDrawOrder
{
    /// <summary>
    /// Apply the ported ground-layer key. True is the engine's behaviour and the default.
    ///
    /// <para>A switch exists for the same reason the renderer's pipeline sort has one: if an ordering
    /// artefact ever does appear, being able to turn this off is how it gets identified rather than
    /// guessed at. Nothing in the editor writes it today; it is a seam for a bisect.</para>
    /// </summary>
    public static bool GroundLayerFirst { get; set; } = true;

    /// <summary>
    /// Does this emitter draw in the ground layer?
    ///
    /// <para><c>== true</c>, never <c>?? false</c> or <c>?? true</c>: the field is a <c>bool?</c> that the
    /// corpus writes only when it is TRUE (160,406 of 160,406 measured against the schema default), so
    /// absence is the only false there is. The stencil term is the exclusion described on the class.</para>
    /// </summary>
    public static bool IsGroundLayer(VfxEmitterDefinition e) =>
        e.Extras?.IsGroundLayer == true && e.StencilMode == 0;

    /// <summary>The sort key, smallest first: the layer, then the authored pass. Use with a STABLE sort -
    /// the authored order is the tiebreak, which is the engine's fifth key.</summary>
    public static (int Layer, int Pass) KeyFor(VfxEmitterDefinition e, bool groundLayerFirst) =>
        (groundLayerFirst && IsGroundLayer(e) ? 0 : 1, e.Pass);

    /// <summary>The sort key under the current <see cref="GroundLayerFirst"/> setting.</summary>
    public static (int Layer, int Pass) KeyFor(VfxEmitterDefinition e) => KeyFor(e, GroundLayerFirst);
}

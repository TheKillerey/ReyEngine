namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M711: the three bits of an emitter's <c>miscRenderFlags</c>, and the one of them a renderer acts on.
///
/// <para><b>The engine reading.</b> Taken back from ltk-manager, whose enum names them from the engine's
/// own symbol <c>ParticleSystem::MISC_RENDER_FLAG</c>: <c>0x1 DISABLE_ZBUFFER</c>, <c>0x2 PROJECTED</c>,
/// <c>0x4 DISABLE_FOW</c>. Their renderer derives the depth TEST from bit 0 and from nothing else, and it
/// does exactly that one thing: the depth WRITE comes from the blend mode, the blend state comes from the
/// blend mode, and the draw order is a separate key. Their other two bits are named and read by nothing,
/// so this ports the one bit that has an implementation behind it.</para>
///
/// <para><b>Absence means zero, and that is verified rather than assumed.</b> The meta database declares
/// this field U8 with a default of 0, and Riot's writer omits a property equal to its class default. That
/// rule was tested on this exact class over the installed game: seven fields, including the three whose
/// default is NOT zero - <c>importance</c> 2, <c>renderPhaseOverride</c> 7, <c>meshRenderFlags</c> 1 - and
/// across 2.9 million authored occurrences not one of them ever writes its declared value. So an emitter
/// that omits the field depth-tests normally.</para>
///
/// <para><b>What the corpus says.</b> 615,284 of 1,581,956 emitters author the field, and 613,808 of those
/// set bit 0 - two in five emitters in the game. That is not the suspicious shape it first looks like: the
/// old note here read "values 1 (480,922), 5, 3, 4, 2", which is a histogram over AUTHORS, not over
/// emitters. Every cross-tab points the way a depth-test opt-out should. Bit 0 rides on 81.7% of
/// ground-layer emitters against 27.0% of the rest, on 58-68% of stencil emitters, and on 57.2% of
/// distortion emitters - all cases where a coincident surface has to paint over what it sits on - and it is
/// ANTI-correlated with soft particles (24.6% against 39.8%), which are the emitters that consume the depth
/// buffer. It is also a genuine per-emitter field rather than a file vintage: 44.9% of systems mix authors
/// with omitters, and 1,476 emitters set another bit while leaving bit 0 clear.</para>
///
/// <para><b>Bits 1 and 2 are not ported.</b> ltk-manager's plan says 0x2 makes the emitter unbatchable and
/// 0x4 emits a fog-of-war define, and their own renderer implements neither; both are asserted engine
/// behaviour with nothing behind them that can be read. Our corpus authors them barely at all - 0x2 on 401
/// emitters, 0x4 on 1,075. They are named here so the byte is documented and so the next reader does not
/// have to find the enum again.</para>
/// </summary>
public static class VfxMiscRenderFlags
{
    /// <summary>Bit 0. The emitter does not test against the depth buffer, so it draws over everything.</summary>
    public const int DisableZBuffer = 0x1;

    /// <summary>Bit 1. Named <c>PROJECTED</c> by the engine. Read by nothing here, and by nothing in the
    /// renderer the reading came from. 401 emitters author it.</summary>
    public const int Projected = 0x2;

    /// <summary>Bit 2. Named <c>DISABLE_FOW</c> by the engine - the fog of war, which this editor does not
    /// draw at all. 1,075 emitters author it.</summary>
    public const int DisableFogOfWar = 0x4;

    /// <summary>Does this emitter draw with the depth test off? The only bit either renderer acts on.</summary>
    public static bool DisablesDepthTest(VfxEmitterDefinition e) =>
        ((e.Extras?.MiscRenderFlags ?? 0) & DisableZBuffer) != 0;
}

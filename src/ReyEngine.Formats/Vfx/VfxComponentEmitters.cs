using System.Text;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M800: the hashes of Riot's component-based VFX format ("Shimmer", added in 16.16).
///
/// <para>They live HERE and not as constants on <see cref="VfxSystemResolver"/> on purpose.
/// <see cref="VfxPreviewCoverage"/> reflects over the resolver's static uint fields and counts every one of
/// them as "the preview reads this field", which is the opposite of the truth for these: ReyEngine only
/// COUNTS the format (see <see cref="VfxComponentEmitters"/>) and draws none of it. Declaring them as resolver
/// constants would have un-badged the raw-tree rows that hold the data.</para>
/// </summary>
public static class VfxComponentFormat
{
    /// <summary>The system field holding the Shimmer emitter list, <c>ShimmerEmitterDefinitionData</c>
    /// (0xeb0aabeb). A container of <see cref="ShimmerEmitterClass"/> structs.</summary>
    public static readonly uint ShimmerListField = HashAlgorithms.Fnv1a("ShimmerEmitterDefinitionData");

    /// <summary>One Shimmer emitter, <c>VfxShimmerEmitterDefinitionData</c> (0x94be93d1): an emitterName, a
    /// <c>disabled</c> flag and a <see cref="ComponentsField"/> block.</summary>
    public static readonly uint ShimmerEmitterClass = HashAlgorithms.Fnv1a("VfxShimmerEmitterDefinitionData");

    /// <summary><c>VfxComponents</c> - the lifetime / physics / render / geometry / child component block. A
    /// Shimmer emitter is made of it, and 16.16's Map11 data ALSO copies it, unread, onto classic
    /// <c>VfxEmitterDefinitionData</c> entries.</summary>
    public static readonly uint ComponentsField = HashAlgorithms.Fnv1a("VfxComponents");
}

/// <summary>
/// M800: how much of one system is authored in Riot's component-based ("Shimmer") VFX format, which ReyEngine
/// does not simulate. Carried on <see cref="VfxSystemDefinition.ComponentEmitters"/>; null there when the
/// system authors none of it. COUNTS only - nothing here changes what the preview draws.
///
/// <para><b>Why the editor needs to say anything.</b> A system whose emitters are all in this format draws
/// nothing in ReyEngine: the resolver reads <c>VfxEmitterDefinitionData</c> entries only, and the classic
/// entries that carry a <c>VfxComponents</c> block author no texture, no primitive and no rate, so none of
/// them is visual. That is indistinguishable from a loading failure - "HoL_26_CubeGrid does not load" -
/// when the empty picture is the correct one.</para>
///
/// <para><b>What was measured</b> (M800, a census of every non-locale WAD in the installed game, harness
/// <c>.codex_tmp/ShimmerProbe</c>): only two systems use the format, HoL_26_CubeGrid and HoL_26_CubeGrid_02,
/// copied into 20 Map11 bins - 80 Shimmer emitters and 80 classic entries carrying the block, every Shimmer
/// emitter disabled and none of the classic entries authoring a texture, primitive or rate. The M800
/// diagnosis also found the classic entries' <c>VfxComponents</c> field undeclared by the client's class
/// schema in every one of the 255 builds it checked, so the client has nothing to read it with. The
/// placements (SRU_MSI_Winner1/2) are additionally gated by a map-visibility controller. The claim in
/// <see cref="Note"/> that the game draws nothing is INFERRED from those facts (disabled emitters, an
/// undeclared field); no frame of the running client was captured.</para>
///
/// <para><b>"Classic component-only" is the census's "not hybrid"</b>: a classic entry that carries the
/// block and none of <c>texture</c>, <c>primitive</c> and <c>rate</c>. An entry that also authored a classic
/// payload would draw through it as it always did, so it is not counted here.</para>
/// </summary>
/// <param name="ShimmerEmitters">Entries of the system's <c>ShimmerEmitterDefinitionData</c> list.</param>
/// <param name="ShimmerEmittersDisabled">How many of those carry <c>disabled = true</c>.</param>
/// <param name="ClassicComponentOnlyEmitters">Classic <c>VfxEmitterDefinitionData</c> entries that carry a
/// <c>VfxComponents</c> block and no classic payload.</param>
public sealed record VfxComponentEmitters(
    int ShimmerEmitters,
    int ShimmerEmittersDisabled,
    int ClassicComponentOnlyEmitters)
{
    /// <summary>Does the system use the format at all?</summary>
    public bool Any => ShimmerEmitters > 0 || ClassicComponentOnlyEmitters > 0;

    /// <summary>Shimmer emitters that are not disabled - the only ones the game could draw.</summary>
    public int ShimmerEmittersEnabled => Math.Max(0, ShimmerEmitters - ShimmerEmittersDisabled);

    /// <summary>The sentence the Particle Editor shows on the system card. One wording source: the
    /// "Nothing to draw" status (<see cref="NothingToDrawStatus"/>) and the coverage notes
    /// (<see cref="VfxPreviewCoverage"/>) say the same thing in fewer words.
    ///
    /// <para>The "draws nothing" conclusion is only drawn while no Shimmer emitter is enabled. An enabled one
    /// (none ships; a mod can make one) is something the game may well draw, so the note says that instead of
    /// repeating a claim it can no longer support.</para></summary>
    public string Note
    {
        get
        {
            if (!Any) return "";
            const string head = "This system uses Riot's component (Shimmer) VFX format, added in 16.16. "
                              + "ReyEngine does not simulate it.";
            bool classic = ClassicComponentOnlyEmitters > 0;
            int live = ShimmerEmittersEnabled;
            var sb = new StringBuilder(head);

            if (live > 0)
            {
                sb.Append(' ').Append(live).Append(" of ").Append(ShimmerEmitters).Append(" Shimmer emitter")
                  .Append(ShimmerEmitters == 1 ? "" : "s").Append(" here ").Append(live == 1 ? "is" : "are")
                  .Append(" enabled, so the game may draw ").Append(live == 1 ? "it" : "them")
                  .Append(" and ReyEngine cannot.");
                if (classic) sb.Append(" The client ignores the VfxComponents block on classic emitters.");
                return sb.ToString();
            }

            const string tail = "so the game draws nothing from them either.";
            const string ignored = "the client ignores the VfxComponents block on classic emitters";
            string? quiet = ShimmerEmitters switch
            {
                0 => null,
                1 => "The only shipped Shimmer emitter here is disabled",
                _ => $"All {ShimmerEmitters} shipped Shimmer emitters here are disabled",
            };
            if (quiet is not null)
                sb.Append(' ').Append(quiet).Append(classic ? ", and " + ignored + ", " : ", ").Append(tail);
            else
                sb.Append(" The client ignores the VfxComponents block on classic emitters, ").Append(tail);
            return sb.ToString();
        }
    }

    /// <summary>The D3D11 preview's "Nothing to draw" status for a system that uses the format. A status line
    /// is trimmed to a few dozen characters (the full sentence is its tooltip), so the clause that matters -
    /// which format, and that it is not simulated - comes first; <see cref="Note"/> has the counts.</summary>
    public string NothingToDrawStatus => ShimmerEmittersEnabled > 0
        ? "Nothing to draw - component (Shimmer) VFX is not simulated; the game may draw it."
        : "Nothing to draw - component (Shimmer) VFX is not simulated; the game draws nothing from it either.";
}

/// <summary>
/// M800: "this system has nothing to draw", and the status line that says so. The D3D11 preview used to report
/// such a system as <c>render failed: no shader loaded</c> - the renderer's name for "no emitter produced a
/// material" - which reads as a device fault when it is the correct outcome for a stub, a fully disabled
/// system or one authored in the component format.
/// </summary>
public static class VfxNothingToDraw
{
    /// <summary>True when no emitter of the system is visual (<see cref="VfxEmitterDefinition.IsVisual"/>),
    /// i.e. when the simulator has nothing to run and the renderer is handed no material.</summary>
    public static bool Applies(VfxSystemDefinition system) => !system.Emitters.Any(static e => e.IsVisual);

    /// <summary>The status line for a system that has nothing to draw, or null when it has something. The
    /// component format's own reason wins, because it is the one that is not a mistake the author made.</summary>
    public static string? Status(VfxSystemDefinition system)
    {
        if (!Applies(system)) return null;
        if (system.ComponentEmitters is { Any: true } components) return components.NothingToDrawStatus;
        if (system.Emitters.Count == 0) return "Nothing to draw - this system has no emitters.";
        if (system.Emitters.All(static e => e.Disabled)) return "Nothing to draw - every emitter of this system is disabled.";
        return "Nothing to draw - no enabled emitter names a texture or a mesh.";
    }
}

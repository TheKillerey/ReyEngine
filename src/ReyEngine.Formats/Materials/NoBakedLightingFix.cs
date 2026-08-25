namespace ReyEngine.Formats.Materials;

/// <summary>What happened when a material was asked to stop sampling the baked lightmap.</summary>
public enum NoBakedLightingOutcome
{
    /// <summary>Already set — nothing to do.</summary>
    AlreadySet,
    /// <summary>The macro on its own resolved to a cooked permutation.</summary>
    Set,
    /// <summary>The macro needed a companion static switch, which was added along with the blend change
    /// it implies.</summary>
    SetWithCompanion,
    /// <summary>The companion the shader wants premultiplies the output and this material has no blend to
    /// absorb it, so setting it would darken the surface by its own alpha.</summary>
    RefusedOpaque,
    /// <summary>No cooked permutation, with or without the companion. The material is left untouched.</summary>
    RefusedNotCooked,
}

/// <summary>
/// M588: turn off baked lighting on a material the way the shader cache will actually accept.
///
/// <para>The reason this needs its own step: on <c>DefaultEnv_Flat_AlphaTest</c>, <c>NO_BAKED_LIGHTING=1</c>
/// on its own is NOT a cooked permutation — M486 authored it across 78 materials and League answered
/// <c>Unable to find correct hash for shader ... Failed to compile shader</c>. It becomes cooked once
/// <c>MULTIPLY_ALPHA</c> is on. <see cref="ShaderPermutationIndex.SuggestFixes"/> has been naming that
/// companion all along and every caller only ever refused, so on a ported map "set every material unlit"
/// refused all 88 materials and there was no way through at all.</para>
///
/// <para><b>Why it matters beyond lighting.</b> The macro selects the vertex permutation, and the two
/// families want different vertex layouts — measured off the compiled DXBC across all 224 cooked vertex
/// permutations of that shader: 128 read <c>POSITION NORMAL TEXCOORD0 TEXCOORD7</c> and 96 read
/// <c>POSITION NORMAL TEXCOORD0</c>, the 96 being the NO_BAKED_LIGHTING ones. A mesh with no Texcoord7 —
/// which is every mesh <c>MapGeoMeshAppender</c> writes, since it emits Position/Normal/Texcoord0 — hands
/// the client an input layout it cannot complete, and the draw is skipped. In the editor the same mesh
/// renders fine, because our renderer builds its layout from what the mesh HAS. That is the whole gap
/// between "looks right here" and "missing in game".</para>
///
/// <para>Refuses rather than guesses, in both directions: an opaque material is left alone because the
/// companion premultiplies the output and there is no blend to absorb it (M542), and anything that does
/// not come back cooked is reverted to exactly the bytes it had.</para>
/// </summary>
public static class NoBakedLightingFix
{
    /// <summary>The one companion switch this will set. Deliberately not "whatever SuggestFixes names":
    /// this is the switch whose side effect is known and compensated for below, and a switch whose effect
    /// we cannot state is not one to turn on behind the user's back.</summary>
    public const string CompanionSwitch = "MULTIPLY_ALPHA";

    /// <summary>
    /// Set <c>NO_BAKED_LIGHTING</c> on <paramref name="material"/>, adding the companion switch if the
    /// shader needs it. The material is only modified when the result is a cooked permutation.
    /// </summary>
    /// <param name="perms">The permutation oracle. When it has no shader cache it proves nothing, and
    /// every material is refused rather than written blind.</param>
    public static NoBakedLightingOutcome Apply(MaterialBinding material, ShaderPermutationIndex perms)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(perms);

        if (material.MacroOn(MaterialBinding.MacroNoBakedLighting)) return NoBakedLightingOutcome.AlreadySet;
        if (!perms.IsAvailable) return NoBakedLightingOutcome.RefusedNotCooked;

        if (perms.CanSetMacro(material, MaterialBinding.MacroNoBakedLighting, "1"))
        {
            material.SetMacro(MaterialBinding.MacroNoBakedLighting, true);
            return NoBakedLightingOutcome.Set;
        }

        // SuggestFixes judges the material AS IT STANDS, and as it stands there is nothing wrong with it —
        // it has not asked for the macro yet, so the advice comes back empty. Set the macro first, ask what
        // the shader wants, and unwind if there is no way through.
        material.SetMacro(MaterialBinding.MacroNoBakedLighting, true);

        if (!perms.SuggestFixes(material)
                  .Any(f => f.Contains(CompanionSwitch, StringComparison.OrdinalIgnoreCase)))
        { material.RemoveMacro(MaterialBinding.MacroNoBakedLighting); return NoBakedLightingOutcome.RefusedNotCooked; }

        // M542: the companion makes the shader output premultiplied. A blend can absorb that by moving
        // its source factor to One; an opaque or purely alpha-tested draw cannot, and would darken by its
        // own alpha. Blended is also exactly the set this is needed for.
        if (!material.BlendEnable)
        { material.RemoveMacro(MaterialBinding.MacroNoBakedLighting); return NoBakedLightingOutcome.RefusedOpaque; }
        if (!material.CanEditSwitches)
        { material.RemoveMacro(MaterialBinding.MacroNoBakedLighting); return NoBakedLightingOutcome.RefusedNotCooked; }

        var existing = material.AllSwitches.FirstOrDefault(s =>
            s.Name.Equals(CompanionSwitch, StringComparison.OrdinalIgnoreCase));
        var sw = existing ?? material.AddSwitch(CompanionSwitch);
        if (sw is null) return NoBakedLightingOutcome.RefusedNotCooked;

        bool wasOn = sw.On;
        int srcColor = material.GetPassU32("srcColorBlendFactor");
        int srcAlpha = material.GetPassU32("srcAlphaBlendFactor");
        sw.SetOn(true);

        // Riot expresses One by ABSENCE — 0 of 9,800 shipped passes author srcColorBlendFactor = 1, and
        // 8,545 leave it out. Only SourceAlpha is moved: a factor already at One (or anything else the
        // author chose) is left as it is.
        if (srcColor == (int)MaterialBlendFactor.SourceAlpha) material.RemovePassProperty("srcColorBlendFactor");
        if (srcAlpha == (int)MaterialBlendFactor.SourceAlpha) material.RemovePassProperty("srcAlphaBlendFactor");

        if (perms.TryExactKey(material, out _, out bool cooked, out _) && cooked)
            return NoBakedLightingOutcome.SetWithCompanion;

        // Not cooked even with the companion — put it back exactly as it was.
        if (srcColor >= 0) material.SetPassU32("srcColorBlendFactor", (uint)srcColor);
        if (srcAlpha >= 0) material.SetPassU32("srcAlphaBlendFactor", (uint)srcAlpha);
        if (existing is null) material.RemoveSwitch(sw); else sw.SetOn(wasOn);
        material.RemoveMacro(MaterialBinding.MacroNoBakedLighting);
        return NoBakedLightingOutcome.RefusedNotCooked;
    }

    /// <summary>Did the material end up unlit?</summary>
    public static bool Succeeded(this NoBakedLightingOutcome outcome) =>
        outcome is NoBakedLightingOutcome.AlreadySet or NoBakedLightingOutcome.Set
                or NoBakedLightingOutcome.SetWithCompanion;

    /// <summary>Did this call change anything?</summary>
    public static bool Changed(this NoBakedLightingOutcome outcome) =>
        outcome is NoBakedLightingOutcome.Set or NoBakedLightingOutcome.SetWithCompanion;
}

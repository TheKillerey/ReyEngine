using ReyEngine.Core.Decoding;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Characters;

namespace ReyEngine.App.Services;

/// <summary>M826: one effect system the Chroma Studio lists: the system the skin reaches, the bin it lives in and which other skins of the champion use it.</summary>
public sealed record ChromaEffectSystemInfo(uint Hash, string Name, string Bin, IReadOnlyList<string> SharedWith, bool IsInferred, string ReachedBy, string ParticlePath)
{
    public bool IsShared => SharedWith.Count > 0;
}

/// <summary>M826: one effect colour field as the card lists it: Riot's field (the values every recolour is derived from), the bin it lives in, and whether the recolour may act on it.</summary>
/// <param name="EditedOutside">The project's value differs from Riot's and no recipe accounts for it: edited in the Particle Editor (or by a mod's GameData). Left off by default.</param>
/// <param name="OwnedEdited">The recipe owns the field but the project no longer holds what the recipe wrote: edited since. Left off and kept as it is until switched on again.</param>
/// <param name="DefaultOffReason">Why the field starts switched off although a recolour may act on it (a save of it is likely to be refused), or null.</param>
public sealed record ChromaEffectFieldInfo(EffectColorField Riot, string Bin, bool Recolourable, string Reason, bool EditedOutside, bool OwnedEdited, string? DefaultOffReason)
{
    public EffectColorKey Key => Riot.Key;
}

/// <summary>M826: what the host read for the card's effect lists. <see cref="Problem"/> says why the lists are empty or short, or is empty. <see cref="Preview"/> is the working
/// set the live preview evaluates (null when no bin could be read).</summary>
public sealed record ChromaEffectSnapshot(IReadOnlyList<ChromaEffectSystemInfo> Systems, IReadOnlyList<ChromaEffectFieldInfo> Fields,
    IReadOnlyList<EffectExcludedField> Excluded, string Problem = "", EffectColorWorkingSet? Preview = null)
{
    public static ChromaEffectSnapshot None(string problem = "") =>
        new(Array.Empty<ChromaEffectSystemInfo>(), Array.Empty<ChromaEffectFieldInfo>(), Array.Empty<EffectExcludedField>(), problem);
}

/// <summary>M826: what a save of the effect colours did, for the card's status line and the log.</summary>
/// <param name="Settled">The fields the bins now hold as the recipe says (written, or already so): these are the saved recipe.</param>
public sealed record ChromaEffectSaveResult(int Written, int Unchanged, int Skipped, int Reverted,
    IReadOnlyList<EffectColorKey> Settled, IReadOnlyList<string> Notes)
{
    public string Summary =>
        $"{Written} effect colour(s) written" + (Unchanged > 0 ? $", {Unchanged} already as wanted" : "")
        + (Skipped > 0 ? $", {Skipped} left alone" : "") + (Reverted > 0 ? $", {Reverted} put back to Riot's" : "");
}

/// <summary>M826: what project.json holds for one skin's effect recolour: the sliders' transform, the colour fields it owns and the effect textures it recoloured.</summary>
public sealed record ChromaSavedEffects(ColorTransform Transform, IReadOnlyList<ChromaEffectColorRef> Colors, IReadOnlyList<ChromaTarget> Textures);

/// <summary>M826: one recoloured effect texture on its way to the renderer's pool, found by the lower-cased path the particle pipeline keys it under.</summary>
public sealed record ChromaEffectPush(ulong Hash, string Key, byte[] Rgba, int Width, int Height);

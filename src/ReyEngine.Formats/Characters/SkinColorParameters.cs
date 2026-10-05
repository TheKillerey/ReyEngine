using System.Numerics;
using System.Text.RegularExpressions;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Characters;

/// <summary>
/// M825: the identity of one colour parameter inside a skin bin - the material object it belongs to (the path hash of its
/// StaticMaterialDef; 0 for the skin's own default block), its name as the bin spells it (<c>0x...</c> when the dictionary cannot
/// name it) and its position among the material's parameters of that name (a material may repeat a name, M789: 29 repeats over
/// the 14,937 shipped skin bins). Stable across Riot's untouched bin and the project's edited copy of it, which is what lets a
/// recolour re-derive a value from the one and write it into the other.
/// </summary>
public readonly record struct SkinColorParamKey(uint Material, string Name, int Occurrence);

/// <summary>M825: one colour parameter of a skin bin, with the verdict on whether a recolour may act on it.</summary>
/// <param name="Value">The value as the bin holds it (a Vector3 reads with alpha 1; a value the entry omits is the authored zero).</param>
/// <param name="Recolourable">The parameter is a colour a hue shift may move. False comes with a <paramref name="Reason"/>.</param>
/// <param name="DrivenBy">The material's <c>dynamicMaterial</c> drives this parameter at run time (a state switch - alive, dead, has gear -
/// sets it): a description of the driver, or null. The authored value is what is recoloured, and a preview that draws the driver's value instead
/// cannot show the change.</param>
public sealed record SkinColorParam(SkinColorParamKey Key, string MaterialName, string TypeName, Vector4 Value, bool ValueOmitted,
    bool IsLinked, string? LinkedFromBin, bool Recolourable, string Reason, string? DrivenBy = null);

/// <summary>What <see cref="SkinColorParameters.Rewrite"/> did with one parameter.</summary>
public enum SkinColorParamOutcome
{
    /// <summary>The bin now holds a value different from the one it had.</summary>
    Written,
    /// <summary>The bin already held exactly the value the recolour (or the restore) gives.</summary>
    Unchanged,
    /// <summary>The parameter is not in the bin it was to be written into, or not in Riot's bin it was to be derived from: left alone.</summary>
    Missing,
    /// <summary>Not a colour a recolour may act on (a size, a mask, a negative colour...): left exactly as authored.</summary>
    Skipped,
    /// <summary>To be given back to Riot's value, but the bin no longer holds what the recipe wrote: somebody edited it since (in the Material tab). Left as it is.</summary>
    Kept,
}

public sealed record SkinColorParamEdit(SkinColorParamKey Key, SkinColorParamOutcome Outcome, Vector4 Before, Vector4 After, string Note);

/// <summary>M825: the bin after a rewrite - null <see cref="Bytes"/> when nothing in it changed - and what happened to each parameter asked for.</summary>
public sealed record SkinColorParamRewrite(byte[]? Bytes, IReadOnlyList<SkinColorParamEdit> Edits)
{
    public int Written => Edits.Count(e => e.Outcome == SkinColorParamOutcome.Written);
}

/// <summary>
/// M825: Chroma Studio C3 - the colour PARAMETERS of a skin (<c>TintColor</c>, <c>OutlineColor</c>, the skin block's
/// <c>fresnelColor</c>...) read and recoloured with the same <see cref="ColorTransform"/> the body textures use.
///
/// <para><b>Which parameters.</b> The ones M812's inventory lists as body colour parameters (<see cref="SkinColorScanner.IsColorName"/>:
/// the name says colour or tint; the value is a Vector4, a Color or a Vector3) in the skin bin's OWN materials and its default
/// block. That heuristic is a NAME test, and measured over the 14,937 shipped skin bins it also lists numbers that are not colours at
/// all - <c>VColor_G_Mask_Discard_Size = (100, 0, 0, 0)</c> on 1,920 materials, <c>ColorFresnelSize</c>, <c>Fresnel_Color_Intensity</c>,
/// <c>FresnelColor_Bias</c>, <c>Rim_Color_Strength</c> - a hue shift of which would turn a size into a different channel. Reading
/// stays as wide as M812's; <see cref="Classify"/> is the narrower test a WRITE needs: not a mask/size/intensity/bias word in the
/// name, not a lone number in the first channel, not a negative colour (<see cref="ColorTransform.CanTransform(Vector4)"/>), not a
/// value the entry leaves out, not defined in a linked bin (that bin is another skin's too, C5's business).</para>
///
/// <para><b>Never compounding.</b> <see cref="Rewrite"/> computes every new value from RIOT's untouched bin and writes it into the
/// CURRENT bin: the result is a function of Riot's value and the transform only, however often it is run. Everything else in the
/// current bin - other parameters, other materials, a Material-tab edit to a parameter the recolour does not own - is carried as it is.
/// Alpha is never changed (the current value's own alpha is kept), HDR values keep their intensity and negative colours stay
/// untouched, because <see cref="ColorTransform.Apply(Vector4)"/> does.</para>
/// </summary>
public static class SkinColorParameters
{
    /// <summary>Every colour parameter of the bin the inventory would list, with its key and verdict.</summary>
    /// <param name="readBin">Follows <c>tree.Dependencies</c> for a material the bin links but does not define (null: such a material is absent).</param>
    public static IReadOnlyList<SkinColorParam> Read(byte[] bin, Func<uint, string?> resolve, Func<ulong, string?>? resolveWadPath = null,
        Func<string, byte[]?>? readBin = null) =>
        Read(MaterialDocument.Parse(bin, resolve, resolveWadPath, readBin));

    public static IReadOnlyList<SkinColorParam> Read(MaterialDocument document)
    {
        var list = new List<SkinColorParam>();
        foreach (var material in document.Materials)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var parameter in material.Parameters)
            {
                int occurrence = seen.TryGetValue(parameter.Name, out int n) ? n : 0;
                seen[parameter.Name] = occurrence + 1;
                if (!SkinColorScanner.IsColorName(parameter.Name) || !parameter.TryGetColor(out Vector4 value)) continue;
                var key = new SkinColorParamKey(material.ObjectPathHash, parameter.Name, occurrence);
                var (ok, reason) = Classify(parameter.Name, parameter.TypeName, value, parameter.IsValueOmitted, material.IsLinked, material.LinkedFromBin);
                var driver = material.DynamicParameters.FirstOrDefault(d => d.Enabled && d.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
                list.Add(new SkinColorParam(key, material.Name, parameter.TypeName, value, parameter.IsValueOmitted,
                    material.IsLinked, material.LinkedFromBin, ok, reason, driver?.Driver));
            }
        }
        return list;
    }

    // ============================================================ the verdict

    /// <summary>Words that make a colour-named Vector4 a number or a mask. Whole words of the name, split at underscores, digits and
    /// camel-case humps: <c>BloomColorFresnelSize</c> is Bloom Color Fresnel Size.</summary>
    private static readonly HashSet<string> NotColourWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "size", "intensity", "strength", "bias", "mask", "range", "rotation", "tile", "tiling", "slice", "smooth", "lerp",
        "discard", "location", "offset", "scale", "speed", "power", "threshold", "width", "radius", "uv", "amount", "opacity",
        "falloff", "exponent",
    };

    private static readonly Regex Words = new("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// May a recolour act on this parameter? True with an empty reason, or false with the reason in words (shown on the card).
    /// The name's words are tested for every type; the lone-number shape only for a Vector4 (a Color or a Vector3 has no room for a scalar).
    /// </summary>
    public static (bool Ok, string Reason) Classify(string name, string typeName, Vector4 value, bool valueOmitted, bool isLinked, string? linkedFrom)
    {
        if (isLinked) return (false, "Defined in a linked bin" + (linkedFrom is null ? "" : " (" + linkedFrom + ")") + ", which other skins use too: not recoloured here.");
        if (valueOmitted) return (false, "The material names it and writes no value (an authored zero): there is no colour to move.");
        if (!ColorTransform.CanTransform(value)) return (false, "A negative or non-finite colour: left exactly as authored.");
        // the name's words apply to every type: a Color or a Vector3 called "...Mask..." is a mask whatever its type (none ships today; the rule must not
        // depend on that)
        foreach (Match word in Words.Matches(name))
            if (NotColourWords.Contains(word.Value))
                return (false, $"Its name says '{word.Value.ToLowerInvariant()}': a number or a mask, not a colour.");
        // a lone number in the first channel is the shape of a scalar packed in a Vector4. (x, y, 0, 0) with both non-zero is NOT excluded: the census of
        // the shipped skins finds only orange and brown colours with alpha 0 there (Fresnel_Color (1, 0.5, 0, 0), Ink_Color (0.17, 0.07, 0, 0)), and no
        // name with params / mix in it.
        if (typeName == "Vector4" && value.Y == 0f && value.Z == 0f && value.W == 0f && value.X != 0f)
            return (false, "A single number in the first channel, not a colour.");
        return (true, "");
    }

    // ============================================================ the rewrite

    /// <summary>
    /// Write into <paramref name="current"/> the colours the recolour (and the restore) say, derived from <paramref name="riot"/>.
    /// </summary>
    /// <param name="current">The bin as the project serves it now - what is written into.</param>
    /// <param name="riot">Riot's untouched bin - what every value is derived from.</param>
    /// <param name="recolour">Parameters to set to <paramref name="transform"/> applied to Riot's value.</param>
    /// <param name="restore">Parameters to put back to Riot's value (a parameter in both lists is recoloured).</param>
    /// <param name="recipe">The transform the recipe that owns the <paramref name="restore"/> parameters wrote them with. When given, a parameter whose value in
    /// <paramref name="current"/> is no longer what that recipe wrote (<see cref="HoldsRecipeValue"/>) is NOT put back - it was edited since, and giving it back
    /// would overwrite the edit (outcome <see cref="SkinColorParamOutcome.Kept"/>). Null: put back whatever it holds (an explicit Revert).</param>
    public static SkinColorParamRewrite Rewrite(byte[] current, byte[] riot, ColorTransform transform,
        IReadOnlyCollection<SkinColorParamKey> recolour, IReadOnlyCollection<SkinColorParamKey> restore,
        Func<uint, string?> resolve, Func<ulong, string?>? resolveWadPath = null, ColorTransform? recipe = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(riot);
        ArgumentNullException.ThrowIfNull(transform);
        var edits = new List<SkinColorParamEdit>();
        var want = new List<(SkinColorParamKey Key, bool Recolour)>();
        foreach (var key in recolour) want.Add((key, true));
        var recolourSet = recolour.ToHashSet();
        foreach (var key in restore) if (!recolourSet.Contains(key)) want.Add((key, false));
        if (want.Count == 0) return new SkinColorParamRewrite(null, edits);

        var now = MaterialDocument.Parse(current, resolve, resolveWadPath);
        var original = MaterialDocument.Parse(riot, resolve, resolveWadPath);
        var nowIndex = Index(now);
        var riotIndex = Index(original);

        bool any = false;
        foreach (var (key, doRecolour) in want)
        {
            if (!riotIndex.TryGetValue(key, out var from))
            { edits.Add(new SkinColorParamEdit(key, SkinColorParamOutcome.Missing, default, default, "Riot's skin bin has no such parameter, so there is nothing to derive it from.")); continue; }
            if (!nowIndex.TryGetValue(key, out var into))
            { edits.Add(new SkinColorParamEdit(key, SkinColorParamOutcome.Missing, default, default, "The project's skin bin no longer has this parameter.")); continue; }
            if (!from.Parameter.TryGetColor(out Vector4 riotValue) || !into.Parameter.TryGetColor(out Vector4 held))
            { edits.Add(new SkinColorParamEdit(key, SkinColorParamOutcome.Skipped, default, default, "It is not a colour in one of the bins.")); continue; }

            var (ok, reason) = Classify(key.Name, from.Parameter.TypeName, riotValue, from.Parameter.IsValueOmitted, from.Material.IsLinked, from.Material.LinkedFromBin);
            if (!ok)
            { edits.Add(new SkinColorParamEdit(key, SkinColorParamOutcome.Skipped, held, held, reason)); continue; }
            if (into.Material.IsLinked || into.Parameter.IsValueOmitted)
            { edits.Add(new SkinColorParamEdit(key, SkinColorParamOutcome.Skipped, held, held, "The project's bin holds it in a linked material or without a value.")); continue; }

            if (!doRecolour && recipe is not null && !HoldsRecipeValue(held, riotValue, recipe, from.Parameter.TypeName == "Color"))
            { edits.Add(new SkinColorParamEdit(key, SkinColorParamOutcome.Kept, held, held, "Edited since the recolour: left as it is.")); continue; }

            // RGB from Riot's value through the transform; alpha stays what the current bin holds (never changed by a recolour)
            Vector4 target = doRecolour ? transform.Apply(riotValue) : riotValue;
            var next = new Vector4(target.X, target.Y, target.Z, held.W);
            if (SameRgb(held, next))
            { edits.Add(new SkinColorParamEdit(key, SkinColorParamOutcome.Unchanged, held, held, "")); continue; }
            if (!into.Parameter.TrySetColor(next))
            { edits.Add(new SkinColorParamEdit(key, SkinColorParamOutcome.Skipped, held, held, "The value could not be written in its type.")); continue; }
            // what the bin holds after the write (a Color is stored as bytes, so read it back)
            into.Parameter.TryGetColor(out Vector4 stored);
            bool moved = !SameRgb(held, stored);
            edits.Add(new SkinColorParamEdit(key, moved ? SkinColorParamOutcome.Written : SkinColorParamOutcome.Unchanged, held, stored, ""));
            any |= moved;
        }
        return new SkinColorParamRewrite(any ? now.Serialize() : null, edits);
    }

    /// <summary>Whether <paramref name="held"/> is what a recipe with <paramref name="recipe"/> writes for Riot's <paramref name="riot"/> value: the transform of
    /// it, red green and blue (a Color is stored in bytes, so it is the clamped value to within a byte's rounding).</summary>
    public static bool HoldsRecipeValue(Vector4 held, Vector4 riot, ColorTransform recipe, bool isColor)
    {
        var expected = recipe.Apply(riot);
        if (isColor) expected = new Vector4(Math.Clamp(expected.X, 0f, 1f), Math.Clamp(expected.Y, 0f, 1f), Math.Clamp(expected.Z, 0f, 1f), expected.W);
        float tolerance = isColor ? 0.004f : 1e-6f;
        return Math.Abs(held.X - expected.X) <= tolerance && Math.Abs(held.Y - expected.Y) <= tolerance && Math.Abs(held.Z - expected.Z) <= tolerance;
    }

    /// <summary>Bit-exact on red, green and blue (so -0 differs from +0 and a NaN equals itself), the comparison the transform itself uses.</summary>
    public static bool SameRgb(Vector4 a, Vector4 b) =>
        BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
        && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

    private static Dictionary<SkinColorParamKey, (MaterialBinding Material, MaterialParameter Parameter)> Index(MaterialDocument document)
    {
        var map = new Dictionary<SkinColorParamKey, (MaterialBinding, MaterialParameter)>();
        foreach (var material in document.Materials)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var parameter in material.Parameters)
            {
                int occurrence = seen.TryGetValue(parameter.Name, out int n) ? n : 0;
                seen[parameter.Name] = occurrence + 1;
                map.TryAdd(new SkinColorParamKey(material.ObjectPathHash, parameter.Name, occurrence), (material, parameter));
            }
        }
        return map;
    }
}

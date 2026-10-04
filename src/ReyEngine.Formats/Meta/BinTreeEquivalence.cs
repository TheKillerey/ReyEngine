using LeagueToolkit.Core.Meta;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M823: whether two bins hold the same data, however they are laid out in the file. The tool an edit on top of a mod's GameData is checked with: the bin the editor holds and the bin LTK
/// makes of the game's with the edit's declaration applied are written by different writers (LTK re-encodes the canonical PROP version 3; the editors write their own form), so
/// their bytes need not agree and their contents must.
///
/// <para><b>What is compared.</b> The objects, by path hash: each one's class and each of its properties, deeply (<see cref="BinPropEquality"/>: a container in order, a map without regard to the order of
/// its entries, a struct by its fields). The dependencies, as the format treats them: without regard to order, to ASCII case and to a repeat. <b>What is not:</b> the order of the objects, the order of
/// the fields of an object, the version of the file - none of which the game reads.</para>
/// </summary>
public static class BinTreeEquivalence
{
    /// <summary>The first place the two bins differ, worded for a person, or null when they hold the same data.</summary>
    /// <param name="expected">The bin the caller wants.</param>
    /// <param name="actual">The bin it got.</param>
    /// <param name="names">Plaintext for the hashes the answer spells; null for none.</param>
    public static string? FirstDifference(BinTree expected, BinTree actual, IDeclarationNames? names = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        string Entry(uint hash) => names?.Entry(hash) is { Length: > 0 } n ? n : $"0x{hash:x8}";
        string Field(uint hash) => names?.Field(hash) is { Length: > 0 } n ? n : $"0x{hash:x8}";

        foreach (var (hash, want) in expected.Objects)
        {
            if (!actual.Objects.TryGetValue(hash, out var have)) return $"the object {Entry(hash)} is missing";
            if (want.ClassHash != have.ClassHash) return $"the object {Entry(hash)} has another class";
            if (BinPropEquality.ObjectsEqual(want, have)) continue;
            foreach (var (field, value) in want.Properties)
                if (!have.Properties.TryGetValue(field, out var other)) return $"{Entry(hash)} lacks {Field(field)}";
                else if (!BinPropEquality.PropsEqual(value, other)) return $"{Entry(hash)}.{Field(field)} differs";
            foreach (var field in have.Properties.Keys)
                if (!want.Properties.ContainsKey(field)) return $"{Entry(hash)} has an extra {Field(field)}";
            return $"the object {Entry(hash)} differs";
        }
        foreach (var hash in actual.Objects.Keys)
            if (!expected.Objects.ContainsKey(hash)) return $"the object {Entry(hash)} is extra";

        var wantLinks = expected.Dependencies.Select(Fold).ToHashSet(StringComparer.Ordinal);
        var haveLinks = actual.Dependencies.Select(Fold).ToHashSet(StringComparer.Ordinal);
        foreach (string link in wantLinks)
            if (!haveLinks.Contains(link)) return $"the dependency {link} is missing";
        foreach (string link in haveLinks)
            if (!wantLinks.Contains(link)) return $"the dependency {link} is extra";
        return null;
    }

    /// <summary>The format compares dependencies by ASCII case only.</summary>
    private static string Fold(string link)
    {
        var chars = link.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + 32);
        return new string(chars);
    }
}

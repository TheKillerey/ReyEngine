using System.Text.RegularExpressions;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M802: how an event - a <c>MutatorMapVisibilityController</c>'s <c>MutatorName</c> - is written for the user. The raw
/// name is what the game matches and stays in the tooltip; the label only makes it readable.
/// </summary>
public static class MapEventNames
{
    // A word break after a lower-case letter or digit that meets a capital ("ObjectESport"), and inside a run of two
    // or more capitals that ends a word ("MSI|Trophy"). A single capital before a word ("E|Sport") is left joined, so
    // "ESport" stays "ESport" rather than becoming "E Sport".
    private static readonly Regex WordBreak = new("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z]{2,})(?=[A-Z][a-z])", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(" {2,}", RegexOptions.Compiled);

    /// <summary>"SR_Hall_Of_Legends" is "SR Hall Of Legends", "MSITrophy" is "MSI Trophy" and
    /// "MapObjectESportSponsorBanners" is "Map Object ESport Sponsor Banners".</summary>
    public static string Label(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";
        string spaced = WordBreak.Replace(name.Replace('_', ' '), " ");
        return Spaces.Replace(spaced, " ").Trim();
    }
}

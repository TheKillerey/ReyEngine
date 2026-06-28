namespace ReyEngine.Core.Assets;

/// <summary>
/// Discovers the original Riot game WADs to use as read-only fallback references, so a mod project
/// that only ships its changed files can still resolve everything else (skin bins, textures, meshes)
/// from the installed game. Picks shared WADs (DATA / Common / Global) plus the map WAD(s) that match
/// the project's content.
/// </summary>
public static class GameReferenceLibrary
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    public static List<string> Discover(string? gameDirectory, IEnumerable<string> mapNames)
    {
        var result = new List<string>();
        var final = FindFinalDir(gameDirectory);
        if (final is null) return result;

        void AddIf(params string[] relParts)
        {
            var p = Path.Combine(new[] { final }.Concat(relParts).ToArray());
            if (File.Exists(p) && !result.Contains(p, StringComparer.OrdinalIgnoreCase)) result.Add(p);
        }

        AddIf("DATA.wad.client");
        AddIf("Common.wad.client");
        AddIf("Global.wad.client");

        foreach (var name in mapNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddIf("Maps", "Shipping", name + ".wad.client");
            AddIf("Maps", "Shipping", name + ".en_US.wad.client");
        }
        return result;
    }

    /// <summary>Resolve the DATA/FINAL directory from a configured game path (Game, Game/DATA/FINAL, …).</summary>
    private static string? FindFinalDir(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory)) return null;
        string[] candidates =
        {
            gameDirectory,
            Path.Combine(gameDirectory, "DATA", "FINAL"),
            Path.Combine(gameDirectory, "Game", "DATA", "FINAL"),
        };
        foreach (var c in candidates)
            if (File.Exists(Path.Combine(c, "DATA.wad.client"))) return c;
        return null;
    }
}

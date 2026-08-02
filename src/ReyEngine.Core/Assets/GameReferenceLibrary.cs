namespace ReyEngine.Core.Assets;

/// <summary>Result of checking whether a configured League folder can provide original game assets.</summary>
public sealed record GameReferenceStatus(
    bool IsValid,
    string? GameDirectory,
    string? FinalDirectory,
    string Message);

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
        AddIf("Shaders", "Shaders.wad.client");   // shared shader textures (normal maps, masks)

        foreach (var name in mapNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddIf("Maps", "Shipping", name + ".wad.client");
            AddIf("Maps", "Shipping", name + ".en_US.wad.client");
        }
        return result;
    }

    /// <summary>
    /// Validate and normalize a folder selected by a user. The picker asks for the League <c>Game</c>
    /// folder, but accepting the install root or DATA/FINAL as input makes recovery from an existing
    /// misconfiguration painless. A usable install must have the three shared WADs on which asset
    /// lookup depends; finding just one stale WAD is not reported as a healthy reference library.
    /// </summary>
    public static GameReferenceStatus Inspect(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
            return new(false, null, null, "No League game folder is configured.");

        string selected;
        try { selected = Path.GetFullPath(gameDirectory.Trim().Trim('"')); }
        catch (Exception ex) { return new(false, null, null, $"The configured path is invalid ({ex.Message})."); }

        if (!Directory.Exists(selected))
            return new(false, null, null, $"The configured folder does not exist: {selected}");

        string? final = FindFinalDir(selected);
        if (final is null)
            return new(false, null, null,
                $"No League DATA/FINAL installation was found under: {selected}");

        string[] required = ["DATA.wad.client", "Common.wad.client", "Global.wad.client"];
        var missing = required.Where(name => !File.Exists(Path.Combine(final, name))).ToArray();
        string game = NormalizeGameDirectory(selected, final);
        if (missing.Length > 0)
            return new(false, game, final,
                $"The League installation is incomplete or outdated; missing {string.Join(", ", missing)} under {final}.");

        return new(true, game, final, $"League game assets found under {final}.");
    }

    /// <summary>Locate the compiled DX11 shader cache WAD in the game install (for the shader database).</summary>
    public static string? FindShaderCache(string? gameDirectory)
    {
        var final = FindFinalDir(gameDirectory);
        if (final is null) return null;
        var p = Path.Combine(final, "ShaderCache.dx11.wad.client");
        return File.Exists(p) ? p : null;
    }

    /// <summary>M103: the WAD holding <c>data/shaders/shaders.bin</c> — every CustomShaderDef the
    /// client knows about, so the shader catalogue differs correctly between Live and PBE.</summary>
    public static string? FindGlobalWad(string? gameDirectory)
    {
        var final = FindFinalDir(gameDirectory);
        if (final is null) return null;
        var p = Path.Combine(final, "Global.wad.client");
        return File.Exists(p) ? p : null;
    }

    /// <summary>M115: the WAD holding the practice-tool target dummy (and all Map11 assets).</summary>
    public static string? FindMap11Wad(string? gameDirectory)
    {
        var final = FindFinalDir(gameDirectory);
        if (final is null) return null;
        var p = Path.Combine(final, "Maps", "Shipping", "Map11.wad.client");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Resolve the DATA/FINAL directory from a configured game path (Game, Game/DATA/FINAL, …).</summary>
    ///
    /// <para>M302 exposed this. Callers that rebuilt the path by hand as
    /// <c>gameDirectory + "/DATA/FINAL"</c> silently found nothing on two of the three supported layouts -
    /// and a cleanup scan that indexes no game WADs would have called real Riot content unused.</para>
    public static string? FindFinalDirectory(string? gameDirectory) => FindFinalDir(gameDirectory);

    private static string NormalizeGameDirectory(string selected, string final)
    {
        var finalInfo = new DirectoryInfo(final);
        if (finalInfo.Name.Equals("FINAL", OIC)
            && finalInfo.Parent?.Name.Equals("DATA", OIC) == true
            && finalInfo.Parent.Parent is { } game)
            return game.FullName;
        return selected;
    }

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

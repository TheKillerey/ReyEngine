namespace ReyEngine.Formats.Meta;

// NOTE: Champion material → diffuse resolution moved to ReyEngine.Formats.Materials
// (MaterialDocument / ChampionMaterialResolver) in M9 — it reads the StaticMaterialDef sampler
// system correctly (per-submesh multi-material), replacing the old single-texture heuristic.

/// <summary>Maps a champion .skn path to its skin .bin path (best-effort, standard layout).</summary>
public static class SkinPaths
{
    public static string? BinPathForSkn(string sknPath)
    {
        try
        {
            var parts = sknPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            int ci = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
            int si = Array.FindIndex(parts, p => p.Equals("skins", StringComparison.OrdinalIgnoreCase));
            if (ci < 0 || si < 0 || ci + 1 >= parts.Length || si + 1 >= parts.Length) return null;

            string champ = parts[ci + 1];
            string folder = parts[si + 1];
            int num;
            if (folder.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                num = 0;
            }
            else
            {
                int digit = folder.IndexOfAny("0123456789".ToCharArray());
                if (digit < 0 || !int.TryParse(folder.AsSpan(digit), out num)) return null;
            }
            return $"data/characters/{champ}/skins/skin{num}.bin";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// M728: the skin bin a preview of this mesh should read - the skin that was CHOSEN when there is one, and
    /// the bin in the mesh's own folder (<see cref="BinPathForSkn"/>) only when nobody chose.
    ///
    /// <para>The folder answer is a guess, and for a chroma it is wrong by construction. A chroma ships no mesh
    /// of its own: Lillia's <c>skin49.bin</c> names <c>ASSETS/Characters/Lillia/Skins/Skin46/Lillia_Skin46.skn</c>
    /// and carries its own textures, material instances and particle resolver. Worked out from that path the
    /// bin is <c>skin46.bin</c>, so the character window showed every chroma as the skin it recolours.</para>
    /// </summary>
    /// <param name="chosenBinPath">The skin bin picked in the character browser, or a placement's skin.</param>
    /// <param name="sknPath">The mesh being previewed, for the fallback.</param>
    public static string? PreviewBinPath(string? chosenBinPath, string? sknPath) =>
        !string.IsNullOrWhiteSpace(chosenBinPath) ? chosenBinPath
        : string.IsNullOrWhiteSpace(sknPath) ? null
        : BinPathForSkn(sknPath);
}

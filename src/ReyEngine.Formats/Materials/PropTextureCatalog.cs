using ReyEngine.Formats.Meshes;

namespace ReyEngine.Formats.Materials;

/// <summary>One placed-character skin and how many times the open map places it.</summary>
public sealed record PropSkinUsage(string SkinPath, int Placements);

/// <summary>One colour-bearing diffuse texture used by placed mobs/animated props.</summary>
public sealed record PropTextureUsage(string AssetPath, int Placements, int Skins);

/// <summary>
/// Discovers the diffuse textures used by map-placeable character skins (Baron, dragons, jungle
/// camps, shopkeepers and animated props). This deliberately excludes masks, normals and gradients:
/// those encode shader data rather than surface colour and must not be hue-shifted.
/// </summary>
public static class PropTextureCatalog
{
    public static IReadOnlyList<PropTextureUsage> Discover(
        IEnumerable<PropSkinUsage> usages,
        Func<string, byte[]?> readSkinBin,
        Func<uint, string?> resolve)
    {
        var bySkin = usages
            .Where(usage => !string.IsNullOrWhiteSpace(usage.SkinPath) && usage.Placements > 0)
            .GroupBy(usage => NormalizeSkin(usage.SkinPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => new PropSkinUsage(group.Key, group.Sum(item => item.Placements)))
            .ToList();
        var byTexture = new Dictionary<string, (int Placements, int Skins)>(StringComparer.OrdinalIgnoreCase);

        foreach (var usage in bySkin)
        {
            byte[]? bytes;
            try { bytes = readSkinBin(usage.SkinPath); }
            catch { continue; }
            if (bytes is null) continue;

            var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Add(textures, SkinMeshExtractor.Extract(bytes)?.DefaultTexture);

            var material = ChampionMaterialResolver.Resolve(bytes, resolve);
            Add(textures, material.DefaultDiffuse);
            foreach (string path in material.SubmeshDiffuse.Values) Add(textures, path);

            foreach (string texture in textures)
            {
                var current = byTexture.GetValueOrDefault(texture);
                byTexture[texture] = (current.Placements + usage.Placements, current.Skins + 1);
            }
        }

        return byTexture
            .Select(pair => new PropTextureUsage(pair.Key, pair.Value.Placements, pair.Value.Skins))
            .OrderByDescending(item => item.Placements)
            .ThenBy(item => item.AssetPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeSkin(string path)
    {
        string clean = path.Replace('\\', '/').Trim();
        if (clean.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) clean = clean[5..];
        if (clean.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) clean = clean[..^4];
        return clean.Trim('/');
    }

    private static void Add(ISet<string> textures, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string clean = path.Replace('\\', '/').Trim();
        if (!clean.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) return;
        textures.Add(clean);
    }
}

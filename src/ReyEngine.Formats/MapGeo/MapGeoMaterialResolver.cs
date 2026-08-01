using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// Resolves map material names (StaticMaterialDef paths from .mapgeo submeshes) to their
/// diffuse texture paths using the map's companion .materials.bin. Objects in the bin are
/// keyed by the FNV-1a hash of the material path.
/// </summary>
public static class MapGeoMaterialResolver
{
    public static string MaterialsBinPathFor(string mapgeoPath)
    {
        int dot = mapgeoPath.LastIndexOf(".mapgeo", StringComparison.OrdinalIgnoreCase);
        return dot < 0 ? mapgeoPath + ".materials.bin" : mapgeoPath[..dot] + ".materials.bin";
    }

    /// <summary>M321: map-wide paint mask supplied to TERRAIN_BLEND_SharedTexture by the game. Unlike a
    /// material sampler, this path is derived from the mapgeo asset being rendered.</summary>
    public static string TerrainBlendTexturePathFor(string mapgeoPath)
    {
        string normalized = mapgeoPath.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) normalized = normalized[5..];
        int dot = normalized.LastIndexOf(".mapgeo", StringComparison.OrdinalIgnoreCase);
        string stem = dot >= 0 ? normalized[..dot] : normalized;
        return "assets/maps/terrainpaint/" + stem + "_array_1_of_1.tex";
    }

    /// <summary>M322: derive the shared terrain-paint world transform from the meshes that use it.
    /// Riot terrain canvases are square and use one origin for world X/Z. Some maps only occupy part of
    /// that square on one axis, so taking independent mesh minima would move the paint on that axis.</summary>
    public static Vector4 TerrainBlendWorldTransformFor(MapGeoAsset map, IEnumerable<string> materialNames)
    {
        var wanted = materialNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var meshIds = map.Groups
            .Where(g => wanted.Contains(g.Material) && g.MeshIndex >= 0)
            .Select(g => g.MeshIndex)
            .ToHashSet();
        var meshes = map.Meshes.Where(m => meshIds.Contains(m.Index)).ToArray();
        if (meshes.Length == 0) return new Vector4(1f / 16000f, 1f / 16000f, 0f, 0f);

        float origin = meshes.Min(m => MathF.Min(m.BoundsMin.X, m.BoundsMin.Z));
        float end = meshes.Max(m => MathF.Max(m.BoundsMax.X, m.BoundsMax.Z));

        // Source bounds commonly stop a unit or two inside clean authored canvas edges. Recover those
        // edges when the measurement is close, e.g. Map21 -598.509..15401.969 -> -600..15400.
        static float SnapHundred(float value)
        {
            float snapped = MathF.Round(value / 100f) * 100f;
            return MathF.Abs(value - snapped) <= 5f ? snapped : value;
        }
        origin = SnapHundred(origin);
        end = SnapHundred(end);
        float extent = end - origin;
        if (!float.IsFinite(extent) || extent <= 1f)
            return new Vector4(1f / 16000f, 1f / 16000f, 0f, 0f);

        float scale = 1f / extent;
        float bias = -origin * scale;
        return new Vector4(scale, scale, bias, bias);
    }

    /// <summary>material name → diffuse texture path.</summary>
    public static Dictionary<string, string> Resolve(byte[] materialsBinData, IEnumerable<string> materialNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BinTree bin;
        try { bin = ReyEngine.Formats.Meta.SafeBinTree.Parse(materialsBinData); }
        catch { return result; }

        foreach (var name in materialNames.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryGetObject(bin, name, out var matObj)) continue;
            var tex = ResolveDiffuse(bin, matObj);
            if (tex is not null) result[name] = tex;
        }
        return result;
    }

    private static bool TryGetObject(BinTree bin, string path, out BinTreeObject obj)
    {
        if (bin.Objects.TryGetValue(HashAlgorithms.Fnv1a(path), out obj!)) return true;
        if (bin.Objects.TryGetValue(HashAlgorithms.Fnv1aRaw(path), out obj!)) return true;
        return false;
    }

    private static string? ResolveDiffuse(BinTree bin, BinTreeObject material)
    {
        if (Field(material.Properties, "samplerValues") is not BinTreeContainer samplers) return null;

        string? first = null;
        string? terrainBottom = null;
        foreach (var el in samplers.Elements)
        {
            if (el is not BinTreeStruct s) continue;
            // Map materials: sampler name is in 'TextureName', the path is in 'texturePath'.
            // (Other schemas use 'samplerName' / 'textureName'.)
            var name = (Field(s.Properties, "TextureName") as BinTreeString)?.Value
                       ?? (Field(s.Properties, "samplerName") as BinTreeString)?.Value ?? "";
            var path = (Field(s.Properties, "texturePath") as BinTreeString)?.Value
                       ?? (Field(s.Properties, "textureName") as BinTreeString)?.Value;
            if (!IsTexturePath(path)) continue;
            first ??= path;
            if (name.Equals("Bottom_Texture", StringComparison.OrdinalIgnoreCase)) terrainBottom = path;
            if (name.Contains("Diffuse", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Color", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Albedo", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Main", StringComparison.OrdinalIgnoreCase))
                return path;
        }
        // Terrain shader 0xe25b830f lists Mask_Texture first. Its actual base colour is Bottom_Texture;
        // returning the mask as diffuse was the source of the bright patchwork fallback rendering.
        return terrainBottom ?? first;
    }

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> props, string name)
    {
        if (props.TryGetValue(HashAlgorithms.Fnv1aRaw(name), out var p)) return p;
        if (props.TryGetValue(HashAlgorithms.Fnv1a(name), out p)) return p;
        return null;
    }

    private static bool IsTexturePath(string? s) =>
        !string.IsNullOrEmpty(s) &&
        (s.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
}

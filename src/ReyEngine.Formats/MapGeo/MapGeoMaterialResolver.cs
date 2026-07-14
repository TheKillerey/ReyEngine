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

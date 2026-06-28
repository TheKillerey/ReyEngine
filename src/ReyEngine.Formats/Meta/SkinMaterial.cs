using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Meta;

public sealed class SkinMaterialInfo
{
    public string? DefaultTexture { get; set; }
    public Dictionary<string, string> SubmeshTexture { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> AllTextures { get; } = new();
    public bool HasAny => DefaultTexture is not null || SubmeshTexture.Count > 0;
}

/// <summary>
/// Pulls diffuse texture paths out of a champion skin .bin (SkinCharacterDataProperties).
/// Navigates by FNV-1a field-name hashes (trying both case conventions), so it needs no
/// hash dictionary.
/// </summary>
public static class SkinMaterialExtractor
{
    public static SkinMaterialInfo Extract(byte[] data)
    {
        var info = new SkinMaterialInfo();
        BinTree bin;
        try { bin = new BinTree(new MemoryStream(data, writable: false)); }
        catch { return info; }

        foreach (var obj in bin.Objects.Values)
        {
            if (Field(obj.Properties, "skinMeshProperties") is not BinTreeStruct smp) continue;

            if (Field(smp.Properties, "texture") is BinTreeString defTex && !string.IsNullOrEmpty(defTex.Value))
                info.DefaultTexture = defTex.Value;
            else if (Field(smp.Properties, "material") is BinTreeObjectLink defMat)
                info.DefaultTexture = ResolveMaterialDiffuse(bin, defMat.Value);

            if (Field(smp.Properties, "materialOverride") is BinTreeContainer overrides)
            {
                foreach (var el in overrides.Elements)
                {
                    if (el is not BinTreeStruct ov) continue;
                    var submesh = (Field(ov.Properties, "submesh") as BinTreeString)?.Value;
                    if (string.IsNullOrEmpty(submesh)) continue;

                    var tex = (Field(ov.Properties, "texture") as BinTreeString)?.Value;
                    if (!IsTexturePath(tex) && Field(ov.Properties, "material") is BinTreeObjectLink ml)
                        tex = ResolveMaterialDiffuse(bin, ml.Value);
                    if (IsTexturePath(tex)) info.SubmeshTexture[submesh!] = tex!;
                }
            }
            break;
        }

        CollectTextures(bin, info.AllTextures);
        info.DefaultTexture ??= info.AllTextures.FirstOrDefault(
            t => t.Contains("_tx_", StringComparison.OrdinalIgnoreCase) || t.Contains("_cm", StringComparison.OrdinalIgnoreCase))
            ?? info.AllTextures.FirstOrDefault();
        return info;
    }

    private static string? ResolveMaterialDiffuse(BinTree bin, uint pathHash)
    {
        if (!bin.Objects.TryGetValue(pathHash, out var mat)) return null;
        if (Field(mat.Properties, "samplerValues") is not BinTreeContainer samplers) return null;

        string? first = null;
        foreach (var el in samplers.Elements)
        {
            if (el is not BinTreeStruct s) continue;
            var name = (Field(s.Properties, "samplerName") as BinTreeString)?.Value ?? "";
            var texName = (Field(s.Properties, "textureName") as BinTreeString)?.Value;
            if (!IsTexturePath(texName)) continue;
            first ??= texName;
            if (name.Contains("Diffuse", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Color", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Albedo", StringComparison.OrdinalIgnoreCase))
                return texName;
        }
        return first;
    }

    private static void CollectTextures(BinTree bin, List<string> result)
    {
        void Walk(BinTreeProperty p)
        {
            switch (p)
            {
                case BinTreeString s:
                    var v = s.Value;
                    if ((v.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                         v.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) && !result.Contains(v))
                        result.Add(v);
                    break;
                case BinTreeContainer c:
                    foreach (var e in c.Elements) Walk(e);
                    break;
                case BinTreeStruct st:
                    foreach (var e in st.Properties.Values) Walk(e);
                    break;
                case BinTreeOptional o when o.Value is not null:
                    Walk(o.Value);
                    break;
            }
        }

        foreach (var obj in bin.Objects.Values)
            foreach (var pr in obj.Properties.Values)
                Walk(pr);
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
}

using System.Collections;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>A placed cubemap reflection probe (M38): captures the environment for reflections at a point.</summary>
public sealed record MapCubemapProbe(string Name, Vector3 Position, Matrix4x4 Transform, string? CubemapPath)
{
    /// <summary>Leaf file name of the baked cubemap (for display), or null.</summary>
    public string? CubemapFile => CubemapPath is null ? null
        : CubemapPath.Contains('/') ? CubemapPath[(CubemapPath.LastIndexOf('/') + 1)..] : CubemapPath;
}

/// <summary>A placed character/animated prop (M38): a creature or prop with a character record + skin
/// (Baron, jungle camps, etc.) positioned on the map.</summary>
public sealed record MapAnimatedProp(string Name, Vector3 Position, Matrix4x4 Transform, string CharacterRecord, string Skin)
{
    /// <summary>Short character identity, e.g. "SRU_Baron" from "Characters/SRU_Baron/CharacterRecords/Root".</summary>
    public string CharacterName
    {
        get
        {
            var parts = CharacterRecord.Split('/');
            int i = Array.FindIndex(parts, p => p.Equals("Characters", StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : (parts.Length > 0 ? parts[^1] : CharacterRecord);
        }
    }

    /// <summary>Skin leaf, e.g. "Skin0".</summary>
    public string SkinName => Skin.Contains('/') ? Skin[(Skin.LastIndexOf('/') + 1)..] : Skin;

    /// <summary>Display label combining character + skin.</summary>
    public string Display => SkinName.Equals("Skin0", StringComparison.OrdinalIgnoreCase) ? CharacterName : $"{CharacterName} / {SkinName}";
}

/// <summary>
/// Reads the remaining <c>MapPlaceableContainer.items</c> types beyond particles (M38): cubemap reflection
/// probes (<c>MapCubemapProbe</c>) and placed characters / animated props (identified structurally by a
/// <c>characterRecord</c> field). Never throws.
/// </summary>
public static class MapPlaceableExtractor
{
    private static readonly uint ContainerClass = HashAlgorithms.Fnv1a("MapPlaceableContainer");
    private static readonly uint CubemapProbeClass = HashAlgorithms.Fnv1a("MapCubemapProbe");
    private static readonly uint F_transform = HashAlgorithms.Fnv1a("transform");
    private static readonly uint F_name = HashAlgorithms.Fnv1a("name");
    private static readonly uint F_characterRecord = HashAlgorithms.Fnv1a("characterRecord");
    private static readonly uint F_skin = HashAlgorithms.Fnv1a("skin");
    private static readonly uint F_cubemapTexture = 0xfe380acfu;   // texture path string on MapCubemapProbe

    public static (IReadOnlyList<MapCubemapProbe> Probes, IReadOnlyList<MapAnimatedProp> Props) Extract(byte[] materialsBin)
    {
        var probes = new List<MapCubemapProbe>();
        var props = new List<MapAnimatedProp>();
        BinTree bin;
        try { bin = SafeBinTree.Parse(materialsBin); }
        catch { return (probes, props); }

        foreach (var o in bin.Objects.Values)
        {
            if (o.ClassHash != ContainerClass) continue;
            if (Field(o.Properties, "items") is not IEnumerable en) continue;

            foreach (var it in en)
            {
                if (it.GetType().GetProperty("Value")?.GetValue(it) is not BinTreeStruct s) continue;
                var transform = s.Properties.TryGetValue(F_transform, out var tp) && tp is BinTreeMatrix44 m ? m.Value : Matrix4x4.Identity;

                if (s.ClassHash == CubemapProbeClass)
                {
                    probes.Add(new MapCubemapProbe(
                        NameOf(s), transform.Translation, transform,
                        (Get(s, F_cubemapTexture) as BinTreeString)?.Value));
                }
                else if (FindCharacterData(s) is ({ } cr, var skin))
                {
                    props.Add(new MapAnimatedProp(NameOf(s), transform.Translation, transform, cr, skin ?? ""));
                }
            }
        }
        return (probes, props);
    }

    /// <summary>Placed characters wrap their character record + skin in an embedded "character data" struct.
    /// Search the item's embedded structs for one carrying a <c>characterRecord</c> string.</summary>
    private static (string? CharacterRecord, string? Skin) FindCharacterData(BinTreeStruct s)
    {
        foreach (var (_, p) in s.Properties)
        {
            if (p is BinTreeStruct emb && emb.Properties.TryGetValue(F_characterRecord, out var crp) && crp is BinTreeString cr)
                return (cr.Value, (emb.Properties.TryGetValue(F_skin, out var sk) ? sk : null) is BinTreeString skin ? skin.Value : null);
        }
        return (null, null);
    }

    private static string NameOf(BinTreeStruct s) => Get(s, F_name) switch
    {
        BinTreeString str => str.Value,
        BinTreeHash h => $"0x{h.Value:x8}",
        _ => "(unnamed)"
    };

    private static BinTreeProperty? Get(BinTreeStruct s, uint hash) => s.Properties.TryGetValue(hash, out var p) ? p : null;

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> props, string name)
        => props.TryGetValue(HashAlgorithms.Fnv1a(name), out var p) ? p
         : props.TryGetValue(HashAlgorithms.Fnv1aRaw(name), out var q) ? q : null;
}

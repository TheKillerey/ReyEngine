using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M438: fill a graphics feature from a map that Riot actually ships with it.
///
/// <para><b>Why copy instead of invent.</b> Most of what these components hold is not a scalar: MapSSAO
/// is one embedded MapSSAORenderer with a nested settings struct, MapClouds is a 3-element container of
/// layer structs plus a texture path, MapDynamicLighting is an embed of unnamed fields. M370's
/// <see cref="MetaDefaultProperty"/> deliberately refuses to build those from a type tuple, and it is
/// right to: a malformed embed written into a bin the game loads is real damage. But a component lifted
/// whole out of a shipped map is not invented at all — it is Riot's own working configuration, textures
/// and values included, which is exactly what an author needs as a starting point.</para>
///
/// <para><b>Source maps are the measured ones</b> (counts over the 206 shipped map materials.bin):
/// MapLightingV2 180, MapTerrainPaint 29, MapSSAO 1, MapClouds 1. The four remaining named features —
/// MapGameplayTexture, MapDynamicLighting, MapLightRegions, MapAntiAliasing — occur 0 times, so there is
/// nothing to copy and this offers nothing rather than guessing.</para>
///
/// <para><b>Link fields still do not survive.</b> An ObjectLink points at an object elsewhere in the
/// SOURCE bin; copying the number into another bin gives a link to nothing. Those are dropped and
/// reported, which is the whole reason MapGameplayTexture cannot be fixed this way — all four of its
/// channel fields are links.</para>
/// </summary>
public static class MapGraphicsFeaturePresets
{
    private static readonly uint ContainerCls = HashAlgorithms.Fnv1a("MapContainer");
    private static readonly uint ComponentsField = HashAlgorithms.Fnv1a("components");

    /// <summary>A shipped map that carries this feature, or null when Riot ships none.</summary>
    public static string? SourceMap(MapGraphicsFeatures.Feature feature)
    {
        if (ReferenceEquals(feature, MapGraphicsFeatures.LightingV2)
            || ReferenceEquals(feature, MapGraphicsFeatures.TerrainPaint))
            return "data/maps/mapgeometry/map21/ioniabase.materials.bin";   // carries both, and is the BakedTerrain reference
        if (ReferenceEquals(feature, MapGraphicsFeatures.Ssao))
            return "data/maps/mapgeometry/map12/crepe.materials.bin";
        if (ReferenceEquals(feature, MapGraphicsFeatures.Clouds))
            return "data/maps/mapgeometry/map22/default.materials.bin";
        return null;                                                        // 0 shipped examples
    }

    public sealed record Extracted(IReadOnlyList<BinTreeProperty> Fields, IReadOnlyList<string> Dropped)
    {
        public string Summary =>
            $"{Fields.Count} field(s)" + (Dropped.Count > 0 ? $"; dropped {Dropped.Count} link field(s): {string.Join(", ", Dropped)}" : "");
    }

    /// <summary>
    /// Pull a feature's fields out of a shipped bin, deep-cloned so nothing is shared with the source
    /// tree. Returns null when that bin does not declare the feature.
    /// </summary>
    public static Extracted? Extract(byte[] shippedBin, MapGraphicsFeatures.Feature feature,
        Func<uint, string?>? resolveName = null)
    {
        ArgumentNullException.ThrowIfNull(shippedBin);
        ArgumentNullException.ThrowIfNull(feature);

        BinTree tree;
        try { tree = new BinTree(new MemoryStream(shippedBin, false)); }
        catch { return null; }

        foreach (var (_, obj) in tree.Objects)
        {
            if (obj.ClassHash != ContainerCls) continue;
            if (!obj.Properties.TryGetValue(ComponentsField, out var prop) || prop is not BinTreeContainer comps) continue;

            var source = comps.Elements.OfType<BinTreeStruct>().FirstOrDefault(s => s.ClassHash == feature.Hash);
            if (source is null) continue;

            var fields = new List<BinTreeProperty>();
            var dropped = new List<string>();
            foreach (var p in source.Properties.Values)
            {
                // A link's value is an object id in the SOURCE bin. Carrying the number across would
                // point at nothing here, so drop it and say which.
                if (ContainsLink(p))
                {
                    dropped.Add(resolveName?.Invoke(p.NameHash) is { Length: > 0 } n ? n : $"0x{p.NameHash:x8}");
                    continue;
                }
                if (!BinTreeCloner.CanClone(p))
                {
                    dropped.Add((resolveName?.Invoke(p.NameHash) is { Length: > 0 } n2 ? n2 : $"0x{p.NameHash:x8}") + " (uncloneable)");
                    continue;
                }
                fields.Add(BinTreeCloner.Clone(p, p.NameHash));
            }
            return new Extracted(fields, dropped);
        }
        return null;
    }

    /// <summary>Does this property, at any depth, carry an ObjectLink? Those cannot cross bins.</summary>
    private static bool ContainsLink(BinTreeProperty p) => p switch
    {
        BinTreeObjectLink => true,
        BinTreeStruct s => s.Properties.Values.Any(ContainsLink),
        BinTreeContainer c => c.Elements.Any(ContainsLink),
        BinTreeOptional o => o.Value is not null && ContainsLink(o.Value),
        BinTreeMap m => m.Any(kv => ContainsLink(kv.Value)),
        _ => false,
    };
}

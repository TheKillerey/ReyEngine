using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.MapGeo;

/// <summary>M167: the map's bake settings component — most importantly WHERE the lightgrid lives.
///
/// Without <c>lightGridFileName</c> nothing loads the lightgrid we bake, so probe lighting for
/// characters, effects and NO_BAKED_LIGHTING surfaces stays dead no matter how good the bake is.
///
/// Structure, measured across all 204 MapBakeProperties instances in the 7 shipping map wads:
///   BinTreeObject(class MapContainer 0xdde8c114)
///     .components (0x1bf51169, Container&lt;Struct&gt;)
///       [i] BinTreeStruct(class MapBakeProperties 0x6a4a3409, NameHash 0)   // sibling of MapSunProperties
///             0x469be1a2 lightGridSize                          U32     (256 in 48/48 occurrences)
///             0x5c6a0e0c lightGridCharacterFullBrightIntensity  F32
///             0x7561b09e lightGridFileName                      String
/// The index within components varies (0, 1 and 3 all occur in shipped maps), so it is found by class
/// hash, not position. Name hashes are FNV-1a over the LOWERCASED name — Fnv1aRaw does not match.
///
/// Only these three fields are written. Map12/Bilgewater ships a working lightmapped map with just
/// <c>{lightGridSize, lightGridFileName}</c>, which proves the Rma* and scale fields are optional; any
/// fields the map already has are left untouched.</summary>
public static class MapBakeProperties
{
    public static readonly uint ClassHash = HashAlgorithms.Fnv1a("MapBakeProperties");
    public static readonly uint FieldLightGridSize = HashAlgorithms.Fnv1a("lightGridSize");
    public static readonly uint FieldCharacterFullBright = HashAlgorithms.Fnv1a("lightGridCharacterFullBrightIntensity");
    public static readonly uint FieldLightGridFileName = HashAlgorithms.Fnv1a("lightGridFileName");
    private static readonly uint ContainerCls = HashAlgorithms.Fnv1a("MapContainer");
    private static readonly uint ComponentsField = HashAlgorithms.Fnv1a("components");

    /// <summary>What a write did, so the caller can report it honestly.</summary>
    public sealed record Result(bool Written, bool CreatedStruct, string Detail);

    /// <summary>Point the map at a baked lightgrid. Rewrites the whole bin (BinTree.Write always does —
    /// it also re-sorts objects by path hash, so expect a large byte diff even for a small edit; the
    /// object set is unchanged and shipped code already relies on this).</summary>
    /// <returns>The new bin bytes, or null when there is no MapContainer to attach to.</returns>
    public static byte[]? Write(byte[] materialsBin, string lightGridPath,
        int lightGridSize, float characterFullBrightIntensity, out Result result)
    {
        result = new Result(false, false, "");
        BinTree tree;
        try { tree = new BinTree(new MemoryStream(materialsBin, false)); }
        catch (Exception ex) { result = new Result(false, false, $"bin did not parse: {ex.Message}"); return null; }

        foreach (var (_, obj) in tree.Objects)
        {
            if (obj.ClassHash != ContainerCls) continue;
            if (!obj.Properties.TryGetValue(ComponentsField, out var prop) || prop is not BinTreeContainer comps) continue;

            var bake = comps.Elements.OfType<BinTreeStruct>().FirstOrDefault(s => s.ClassHash == ClassHash);
            bool created = false;
            if (bake is null)
            {
                // No MapBakeProperties at all — build one and append it. Container elements carry no name
                // hash on the wire, so NameHash 0 is what Riot writes and what round-trips.
                bake = new BinTreeStruct(0, ClassHash, new BinTreeProperty[]
                {
                    new BinTreeU32(FieldLightGridSize, (uint)Math.Max(1, lightGridSize)),
                });
                if (comps.Elements is IList<BinTreeProperty> list) list.Add(bake);
                else
                {
                    obj.Properties[ComponentsField] = new BinTreeContainer(ComponentsField, comps.ElementType,
                        comps.Elements.Append(bake));
                }
                created = true;
            }

            bake.Properties[FieldLightGridSize] = new BinTreeU32(FieldLightGridSize, (uint)Math.Max(1, lightGridSize));
            bake.Properties[FieldCharacterFullBright] = new BinTreeF32(FieldCharacterFullBright, characterFullBrightIntensity);
            bake.Properties[FieldLightGridFileName] = new BinTreeString(FieldLightGridFileName, lightGridPath);

            var ms = new MemoryStream();
            tree.Write(ms);
            result = new Result(true, created,
                $"lightGridSize={lightGridSize}, characterFullBright={characterFullBrightIntensity:0.##}, file='{lightGridPath}'"
                + (created ? " (created the component)" : " (updated the existing component)"));
            return ms.ToArray();
        }

        result = new Result(false, false, "no MapContainer in this bin");
        return null;
    }

    /// <summary>Read back what a bin currently declares, for reporting/verification. Null when absent.</summary>
    public static (int Size, float FullBright, string File)? Read(byte[] materialsBin)
    {
        try
        {
            var tree = new BinTree(new MemoryStream(materialsBin, false));
            foreach (var (_, obj) in tree.Objects)
            {
                if (obj.ClassHash != ContainerCls) continue;
                if (!obj.Properties.TryGetValue(ComponentsField, out var p) || p is not BinTreeContainer c) continue;
                var bake = c.Elements.OfType<BinTreeStruct>().FirstOrDefault(s => s.ClassHash == ClassHash);
                if (bake is null) continue;
                int size = bake.Properties.TryGetValue(FieldLightGridSize, out var sp) && sp is BinTreeU32 su ? (int)su.Value : 0;
                float fb = bake.Properties.TryGetValue(FieldCharacterFullBright, out var fp) && fp is BinTreeF32 ff ? ff.Value : 0f;
                string file = bake.Properties.TryGetValue(FieldLightGridFileName, out var np) && np is BinTreeString ns ? ns.Value : "";
                return (size, fb, file);
            }
        }
        catch { }
        return null;
    }
}

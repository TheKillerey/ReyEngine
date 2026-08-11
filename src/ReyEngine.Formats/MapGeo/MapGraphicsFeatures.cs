using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M434: the <c>MapGraphicsFeature</c> family — the per-map switches that decide which shared rendering
/// resources the engine builds — read and written in <c>MapContainer.components[]</c>.
///
/// <para><b>Where they live.</b> Same slot as MapBakeProperties and MapSunProperties (see
/// <see cref="MapBakeProperties"/>): EMBEDDED inside the container, not top-level bin objects. Scanning a
/// bin's object table alone finds zero of them across all 49,602 shipped bins — they only appear when you
/// walk nested properties.</para>
///
/// <para><b>Why they matter for shaders.</b> The descendants line up one-for-one with the shared
/// resources a map shader can bind — measured against <c>Mantis_Env_Baked_PBR</c>'s compiled pixel
/// shader: MapTerrainPaint ↔ TERRAIN_BLEND_SharedTexture, MapLightingV2 ↔ IBL_CUBEMAP_SharedTexture +
/// IBL_CUBEMAP_SCALES_BUFFER, MapSSAO ↔ SSAO_TEXTURE_SharedTexture, MapDynamicLighting ↔
/// DYNAMIC_ENV_LIGHT_FACTOR/IDS, MapGameplayTexture ↔ GAMEPLAY_TEXTURE_SharedTexture, MapLightRegions ↔
/// LightRegionInfo_SharedDataBuffer.</para>
///
/// <para><b>Corpus.</b> Of 206 shipped map materials.bin, 179 declare MapLightingV2 and 27 do not — and
/// the 27 are the map11 family plus map21/base, i.e. the legacy lighting path.
/// <c>DefaultEnv_Flat_BakedTerrain</c>, the only shipped FEATURE_BAKED_PAINT shader, occurs in exactly 2
/// bins (map21/ioniabase, map35/base) and BOTH declare MapLightingV2 — it never appears without it.</para>
///
/// <para><b>Field layouts come from the LeagueToolkit meta dump</b> (data/meta/meta.db.json), not from
/// guesswork: the wiki page for MapGraphicsFeature is a stub with no field documentation. Only features
/// with a shipped example are given typed helpers here; the rest can still be added as bare markers,
/// which is exactly how map12/jade ships MapLightingV2 (zero properties).</para>
/// </summary>
public static class MapGraphicsFeatures
{
    private static readonly uint ContainerCls = HashAlgorithms.Fnv1a("MapContainer");
    private static readonly uint ComponentsField = HashAlgorithms.Fnv1a("components");

    /// <summary>One member of the family. <paramref name="Hash"/> is the class hash; the hash-only entries
    /// have no name in any dump and are carried by literal hash.</summary>
    public sealed record Feature(string Name, uint Hash, string Purpose)
    {
        public override string ToString() => Name;
    }

    public static readonly Feature LightingV2 = new("MapLightingV2", HashAlgorithms.Fnv1a("MapLightingV2"),
        "Image-based lighting (IBL cubemap + scales buffer). Required by every shipped map that uses a baked-terrain shader.");
    public static readonly Feature TerrainPaint = new("MapTerrainPaint", HashAlgorithms.Fnv1a("MapTerrainPaint"),
        "Terrain blend atlas (TERRAIN_BLEND_SharedTexture).");
    public static readonly Feature Ssao = new("MapSSAO", HashAlgorithms.Fnv1a("MapSSAO"),
        "Screen-space ambient occlusion buffer (SSAO_TEXTURE_SharedTexture).");
    public static readonly Feature Clouds = new("MapClouds", HashAlgorithms.Fnv1a("MapClouds"),
        "Cloud shadow layers (the CLOUD_SHADOWS shader define).");
    public static readonly Feature DynamicLighting = new("MapDynamicLighting", HashAlgorithms.Fnv1a("MapDynamicLighting"),
        "Dynamic environment lights (DYNAMIC_ENV_LIGHT_FACTOR/IDS textures).");
    public static readonly Feature GameplayTexture = new("MapGameplayTexture", HashAlgorithms.Fnv1a("MapGameplayTexture"),
        "Packed gameplay channels (GAMEPLAY_TEXTURE_SharedTexture).");
    public static readonly Feature LightRegions = new("MapLightRegions", HashAlgorithms.Fnv1a("MapLightRegions"),
        "Per-region lighting overrides (LightRegionInfo_SharedDataBuffer).");
    public static readonly Feature AntiAliasing = new("MapAntiAliasing", HashAlgorithms.Fnv1a("MapAntiAliasing"),
        "Anti-aliasing mode.");
    public static readonly Feature TransitionPositions = new("MapTransitionPositions", HashAlgorithms.Fnv1a("MapTransitionPositions"),
        "Transition locator positions.");

    /// <summary>Every member we can name. Ordered with the ones that have shipped examples first.</summary>
    public static readonly IReadOnlyList<Feature> All = new[]
    {
        LightingV2, TerrainPaint, Ssao, Clouds, DynamicLighting,
        GameplayTexture, LightRegions, AntiAliasing, TransitionPositions,
    };

    public sealed record Result(bool Changed, string Detail);

    /// <summary>Which of the family does this bin already declare?</summary>
    public static IReadOnlyList<Feature> Read(byte[] materialsBin)
    {
        var found = new List<Feature>();
        BinTree tree;
        try { tree = new BinTree(new MemoryStream(materialsBin, false)); }
        catch { return found; }

        foreach (var comps in Containers(tree))
            foreach (var el in comps.Elements)
            {
                uint cls = ClassOf(el);
                var feature = All.FirstOrDefault(f => f.Hash == cls);
                if (feature is not null && !found.Contains(feature)) found.Add(feature);
            }
        return found;
    }

    /// <summary>
    /// Add a graphics feature to <c>MapContainer.components[]</c>, or update the fields of one already
    /// there.
    ///
    /// <para>Added as a BARE MARKER by default, with no properties. That is not a shortcut — it is the
    /// shipped form: map12/jade's MapLightingV2 carries zero properties, so the engine takes every value
    /// from its defaults. Pass <paramref name="fields"/> only when you have a reason, e.g. matching
    /// map21/ioniabase, which sets MinimumEnvironmentColorContribution=0.9 and
    /// BounceLightFalloffDistance=2000.</para>
    /// </summary>
    /// <returns>The new bin bytes, or null when nothing was written.</returns>
    public static byte[]? Add(byte[] materialsBin, Feature feature,
        IEnumerable<BinTreeProperty>? fields, out Result result)
    {
        ArgumentNullException.ThrowIfNull(materialsBin);
        ArgumentNullException.ThrowIfNull(feature);
        result = new Result(false, "");

        BinTree tree;
        try { tree = new BinTree(new MemoryStream(materialsBin, false)); }
        catch (Exception ex) { result = new Result(false, $"bin did not parse: {ex.Message}"); return null; }

        foreach (var (obj, comps) in ContainersWithOwner(tree))
        {
            var existing = comps.Elements.FirstOrDefault(e => ClassOf(e) == feature.Hash);
            var props = fields?.ToList() ?? new List<BinTreeProperty>();

            if (existing is not null)
            {
                if (props.Count == 0)
                { result = new Result(false, $"{feature.Name} is already declared"); return null; }
                // BinTreeEmbedded derives from BinTreeStruct, so this covers both wire forms.
                var bag = existing is BinTreeStruct s ? s.Properties : null;
                if (bag is null) { result = new Result(false, $"{feature.Name} is present but not a struct"); return null; }
                foreach (var p in props) bag[p.NameHash] = p;
                result = new Result(true, $"updated {feature.Name} ({props.Count} field(s))");
            }
            else
            {
                // Container elements carry no name hash on the wire, so NameHash 0 is what Riot writes
                // and what round-trips. Struct (0x82) matches every shipped component in this container.
                var added = new BinTreeStruct(0, feature.Hash, props);
                if (comps.Elements is IList<BinTreeProperty> list) list.Add(added);
                else obj.Properties[ComponentsField] =
                    new BinTreeContainer(ComponentsField, comps.ElementType, comps.Elements.Append(added));
                result = new Result(true, $"added {feature.Name}"
                                          + (props.Count > 0 ? $" with {props.Count} field(s)" : " (bare marker, as map12/jade ships it)"));
            }

            var ms = new MemoryStream();
            tree.Write(ms);
            return ms.ToArray();
        }

        result = new Result(false, "no MapContainer in this bin");
        return null;
    }

    /// <summary>Remove a graphics feature. Returns null when it was not there to begin with.</summary>
    public static byte[]? Remove(byte[] materialsBin, Feature feature, out Result result)
    {
        ArgumentNullException.ThrowIfNull(materialsBin);
        result = new Result(false, "");

        BinTree tree;
        try { tree = new BinTree(new MemoryStream(materialsBin, false)); }
        catch (Exception ex) { result = new Result(false, $"bin did not parse: {ex.Message}"); return null; }

        foreach (var (obj, comps) in ContainersWithOwner(tree))
        {
            var kept = comps.Elements.Where(e => ClassOf(e) != feature.Hash).ToList();
            if (kept.Count == comps.Elements.Count) continue;

            // M414: an EMPTY container crashes the game at map load (Riot ships 0 of 33,645). Refuse
            // rather than produce one.
            if (kept.Count == 0)
            { result = new Result(false, $"removing {feature.Name} would empty components[] — refused"); return null; }

            obj.Properties[ComponentsField] = new BinTreeContainer(ComponentsField, comps.ElementType, kept);
            var ms = new MemoryStream();
            tree.Write(ms);
            result = new Result(true, $"removed {feature.Name}");
            return ms.ToArray();
        }

        result = new Result(false, $"{feature.Name} is not declared in this bin");
        return null;
    }

    /// <summary>The fields map21/ioniabase sets on MapLightingV2 — the closest shipped analogue to a
    /// baked-terrain/PBR map. Offered as a named preset so the values are traceable to their source.</summary>
    public static IReadOnlyList<BinTreeProperty> IoniaBaseLightingV2Fields() => new BinTreeProperty[]
    {
        new BinTreeF32(HashAlgorithms.Fnv1a("MinimumEnvironmentColorContribution"), 0.9f),
        new BinTreeF32(HashAlgorithms.Fnv1a("BounceLightFalloffDistance"), 2000f),
    };

    /// <summary>Embedded (0x83) and pointer (0x82) both surface as BinTreeStruct - Embedded derives from
    /// it - so one test covers the two forms a component can take on the wire.</summary>
    private static uint ClassOf(BinTreeProperty p) => p is BinTreeStruct s ? s.ClassHash : 0u;

    private static IEnumerable<BinTreeContainer> Containers(BinTree tree) =>
        ContainersWithOwner(tree).Select(t => t.Components);

    private static IEnumerable<(BinTreeObject Owner, BinTreeContainer Components)> ContainersWithOwner(BinTree tree)
    {
        foreach (var (_, obj) in tree.Objects)
        {
            if (obj.ClassHash != ContainerCls) continue;
            if (obj.Properties.TryGetValue(ComponentsField, out var prop) && prop is BinTreeContainer comps)
                yield return (obj, comps);
        }
    }
}

using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M848: which meshes go into the scene bucket grids.
///
/// <para><b>Measured</b> against every grid of the 202 mapgeos Riot ships in Maps/Shipping. A mesh counts as
/// in a grid when most of its triangles are there; render-region meshes are matched in their own local
/// space, see below.</para>
/// <list type="bullet">
///   <item><b>VertexDeform without lightmap UVs is left out.</b> All 8,259 VertexDeform meshes without
///   Texcoord7 are missing from Riot's grids (every one of them on Summoner's Rift, e.g. base_srx's 149
///   plants), and all 398 VertexDeform meshes WITH Texcoord7 are in them (ARAM's Wall_of_Grass, Arena's
///   brush). Their materials do not tell the two apart: ARAM's Wall_of_Grass carries the same switches
///   and macros as Summoner's Rift's VertexDeform_inst. Texcoord7 coincides with the map as well, so it
///   is the rule that reproduces Riot rather than a proven cause. A shader-only rule would drop ARAM's
///   and Arena's VertexDeform meshes, which Riot keeps.</item>
///   <item><b>FaeLights cards are NOT left out.</b> All 12 of base_srx's <c>Indicator_Faelights</c>
///   cards are in Riot's grids, one render-region grid each, stored in the mesh's local space rather
///   than world space. That is why a world-space search does not find them. The builder still writes
///   render-region grids in world space, which is unchanged here.</item>
///   <item>Riot leaves out more meshes than this, for reasons not established: on base_srx, the 26
///   <c>Ocean_WaterLily_A_back</c> meshes and some spawn/periphery walls. They stay in.</item>
/// </list>
///
/// <para>The shader comes from the map's materials.bin, as the client's other per-mesh choices do (see
/// <see cref="ExtendedChannelRule"/>). A mesh is left out when ANY of its submeshes uses a VertexDeform
/// material and it has no lightmap UVs.</para>
/// </summary>
public sealed class MapBucketGridMeshSelection
{
    /// <summary>Shader names (the last path segment) whose meshes Riot leaves out when they have no
    /// lightmap UVs.</summary>
    public static readonly string[] VertexDeformShaderPrefixes = { "VertexDeform" };

    /// <summary>Every mesh goes into the grids - what the builder did before M848.</summary>
    public static MapBucketGridMeshSelection All { get; } = new(new HashSet<string>(StringComparer.Ordinal));

    /// <summary>The materials that use a VertexDeform shader.</summary>
    public IReadOnlySet<string> VertexDeformMaterials { get; }

    public MapBucketGridMeshSelection(IReadOnlySet<string> vertexDeformMaterials)
    {
        ArgumentNullException.ThrowIfNull(vertexDeformMaterials);
        VertexDeformMaterials = vertexDeformMaterials;
    }

    /// <summary>The pass shader as <see cref="MaterialDocument"/> spells it when the hash dictionary cannot
    /// name it. Riot's maps use exactly this one VertexDeform shader, so the rule still holds without hashes.</summary>
    private static readonly string VertexDeformLinkName =
        $"0x{ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a("Shaders/StaticMesh/VertexDeform"):x8}";

    /// <summary>True when this shader is in the VertexDeform family.</summary>
    public static bool IsVertexDeformShader(string? shader)
    {
        if (string.Equals(shader, VertexDeformLinkName, StringComparison.OrdinalIgnoreCase)) return true;
        string leaf = shader is { Length: > 0 } ? shader[(shader.LastIndexOf('/') + 1)..] : "";
        return VertexDeformShaderPrefixes.Any(p => leaf.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Riot's selection for a parsed materials document.</summary>
    public static MapBucketGridMeshSelection FromMaterials(IEnumerable<MaterialBinding> materials)
    {
        // case-insensitive like the editor's other mesh-to-material matching; Riot's names agree in case anyway
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in materials)
            if (m.Name.Length > 0 && IsVertexDeformShader(m.RenderShader ?? m.ShaderName))
                set.Add(m.Name);
        return new MapBucketGridMeshSelection(set);
    }

    /// <summary>
    /// Riot's selection for a map's materials.bin, to hand to <see cref="MapBucketGridBuilder.Rebuild"/> and
    /// <see cref="MapGeoWriter.WriteWithRegeneratedBucketGrids"/>.
    /// </summary>
    /// <param name="materialsBin">The map's <c>*.materials.bin</c> bytes.</param>
    /// <param name="resolve">Hash-to-name resolver, as <see cref="MaterialDocument.Parse"/> wants it. Shader
    /// links it cannot name stay hashes; of those, only Riot's own <c>Shaders/StaticMesh/VertexDeform</c> is
    /// recognised.</param>
    public static MapBucketGridMeshSelection FromMaterialsBin(byte[]? materialsBin, Func<uint, string?> resolve)
    {
        if (materialsBin is not { Length: > 0 }) return All;
        try
        {
            return FromMaterials(MaterialDocument.Parse(materialsBin, resolve).Materials);
        }
        catch
        {
            // A bin we cannot parse means we cannot tell. Keeping every mesh is what the builder always did:
            // the grid is complete, just not trimmed the way Riot trims it.
            return All;
        }
    }

    /// <summary>True when the mesh stays out of every grid.</summary>
    public bool Excludes(MapGeoAsset asset, int meshIndex)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (VertexDeformMaterials.Count == 0 || meshIndex < 0 || meshIndex >= asset.Meshes.Count) return false;
        if (asset.Meshes[meshIndex].HasLightmapUv) return false;
        return asset.Groups.Any(g => g.MeshIndex == meshIndex && VertexDeformMaterials.Contains(g.Material));
    }

    /// <summary>Indices of the meshes that stay out of every grid.</summary>
    public HashSet<int> ExcludedMeshes(MapGeoAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var meshes = new HashSet<int>();
        if (VertexDeformMaterials.Count == 0) return meshes;
        foreach (var group in asset.Groups)
            if (group.MeshIndex >= 0 && group.MeshIndex < asset.Meshes.Count
                && !asset.Meshes[group.MeshIndex].HasLightmapUv
                && VertexDeformMaterials.Contains(group.Material))
                meshes.Add(group.MeshIndex);
        return meshes;
    }
}

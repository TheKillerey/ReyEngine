using System.Numerics;

namespace ReyEngine.Formats.MapGeo;

/// <summary>What one add-Texcoord7 pass did.</summary>
public sealed record UvChannelResult(int MeshesChanged, int VerticesWritten, IReadOnlyList<string> Skipped)
{
    public string Summary => MeshesChanged == 0
        ? "No mesh changed" + (Skipped.Count > 0 ? ": " + string.Join("; ", Skipped.Take(3)) : ".")
        : $"Added Texcoord7 to {MeshesChanged:n0} mesh(es), {VerticesWritten:n0} vertices"
          + (Skipped.Count > 0 ? $"; skipped {Skipped.Count} ({string.Join("; ", Skipped.Take(3))})" : ".");
}

/// <summary>
/// M432: give a mesh the baked UV set (Texcoord7) WITHOUT baking a lightmap or packing an atlas.
///
/// <para><b>Why the channel is wanted on its own.</b> Some shaders need Texcoord7 as a VERTEX CONTRACT
/// rather than to sample a lightmap. <c>Shaders/StaticMesh/Mantis_Env_Baked_PBR</c> declares
/// FEATURE_BAKED_PAINT and reads the baked UV set for its BAKED_* samplers, and those textures come from
/// the MATERIAL, not from a generated atlas. Measured: all 18 shipped meshes on the only other
/// FEATURE_BAKED_PAINT shader carry Texcoord7; 0 of the 586 meshes in map11's base_srx do. A full
/// lightmap bake is a disproportionate way to satisfy that.</para>
///
/// <para><b>The UVs are copied from Texcoord0.</b> A stated choice, not a derived one: it yields a valid
/// in-range channel with the same layout the diffuse already uses, so BAKED_* textures sample coherently
/// with the diffuse. It is NOT a lightmap unwrap — charts overlap between meshes, which is exactly why no
/// atlas reference is written. Use the light-baking path when you need real, non-overlapping lightmap
/// UVs.</para>
/// </summary>
public static class MeshUvChannelBuilder
{
    /// <summary>
    /// Add Texcoord7 to the meshes at the given ordinals (the same index the outliner and
    /// <see cref="MapGeoMesh.Index"/> use), copying each vertex's Texcoord0. An empty selection means
    /// every mesh. Meshes that already carry the channel are skipped, never duplicated.
    /// </summary>
    /// <returns>The rewritten mapgeo, or the input unchanged when nothing qualified.</returns>
    public static byte[] AddTexcoord7(byte[] mapGeo, IEnumerable<int> meshIndices, out UvChannelResult result)
    {
        ArgumentNullException.ThrowIfNull(mapGeo);
        if (!MapGeoBinary.TryReadEditable(mapGeo, out var map) || map is null)
            throw new InvalidOperationException(
                "This mapgeo does not round-trip byte-exactly, so it cannot be safely rewritten.");
        return AddTexcoord7(map, meshIndices, out result) ? map.Write() : mapGeo;
    }

    /// <summary>In-place variant for callers that already hold the editable layer.</summary>
    public static bool AddTexcoord7(MapGeoBinary map, IEnumerable<int> meshIndices, out UvChannelResult result)
    {
        ArgumentNullException.ThrowIfNull(map);
        var wanted = new HashSet<int>(meshIndices);
        var skipped = new List<string>();
        int changed = 0, vertices = 0;

        for (int index = 0; index < map.Meshes.Count; index++)
        {
            if (wanted.Count > 0 && !wanted.Contains(index)) continue;
            var mesh = map.Meshes[index];

            if (map.MeshHasLightmapUv(mesh)) { skipped.Add($"mesh {index} already has Texcoord7"); continue; }
            if (mesh.VertexCount == 0) { skipped.Add($"mesh {index} has no vertices"); continue; }
            if (!TryReadTexcoord0(map, mesh, out var uv))
            { skipped.Add($"mesh {index} has no readable Texcoord0 to copy"); continue; }

            map.AddUvChannelOnly(mesh, uv);
            changed++;
            vertices += uv.Length;
        }

        if (changed > 0) map.Compact();
        result = new UvChannelResult(changed, vertices, skipped);
        return changed > 0;
    }

    /// <summary>Pull the mesh's Texcoord0 out of whichever of its buffers declares it.</summary>
    private static bool TryReadTexcoord0(MapGeoBinary map, MapGeoBinary.Mesh mesh, out Vector2[] uv)
    {
        uv = Array.Empty<Vector2>();
        for (int i = 0; i < mesh.VertexBufferIds.Count; i++)
        {
            var decl = map.Declarations[mesh.VertexDeclarationBase + i];
            int offset = 0;
            foreach (var (element, format) in decl.Elements)
            {
                if (element == MapGeoBinary.ElemTexcoord0 && format == MapGeoBinary.FmtXY_Float32)
                {
                    var data = map.VertexBuffers[mesh.VertexBufferIds[i]].Data;
                    int stride = decl.Stride;
                    if (stride <= 0 || data.Length < stride * mesh.VertexCount) return false;
                    var result = new Vector2[mesh.VertexCount];
                    for (int v = 0; v < mesh.VertexCount; v++)
                    {
                        int at = v * stride + offset;
                        result[v] = new Vector2(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4));
                    }
                    uv = result;
                    return true;
                }
                offset += MapGeoBinary.FormatSize(format);
            }
        }
        return false;
    }
}

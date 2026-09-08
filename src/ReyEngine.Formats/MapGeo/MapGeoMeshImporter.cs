using System.Numerics;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Memory;

namespace ReyEngine.Formats.MapGeo;

/// <summary>One mesh in a source mapgeo, as offered for import.</summary>
public sealed record ImportableMapMesh(
    int Index,
    string Name,
    IReadOnlyList<string> Materials,
    int VertexCount,
    int TriangleCount,
    Vector3 Centre)
{
    public string Summary =>
        $"{VertexCount:n0} verts · {TriangleCount:n0} tris · {(Materials.Count == 1 ? Materials[0] : $"{Materials.Count} materials")}";
}

/// <summary>
/// M512: lift a mesh out of one mapgeo so it can be appended to another.
///
/// <para>The counterpart to <see cref="MapGeoMeshAppender"/>, which already knows how to splice a
/// <see cref="NewMapMesh"/> into a file. What was missing was a source other than an .obj/.fbx on disk:
/// the richest source of League-shaped geometry is League's own maps, and their meshes come with a
/// material that is already correct — right shader, right samplers, right macros, cooked by definition
/// because the game ships it.</para>
///
/// <para>Geometry is extracted in the mesh's own LOCAL space with its placement transform carried
/// separately, exactly as <see cref="NewMapMesh"/> wants it, so the imported mesh stays gizmo-movable.</para>
/// </summary>
public static class MapGeoMeshImporter
{
    /// <summary>Every mesh in a source file, with enough detail to choose one.</summary>
    public static IReadOnlyList<ImportableMapMesh> List(byte[] mapgeo)
    {
        ArgumentNullException.ThrowIfNull(mapgeo);
        var result = new List<ImportableMapMesh>();
        using var ms = new MemoryStream(mapgeo, writable: false);
        var env = new EnvironmentAsset(ms);

        int index = 0;
        foreach (var mesh in env.Meshes)
        {
            var materials = mesh.Submeshes.Select(s => s.Material)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var centre = Vector3.Zero;
            int vertexCount = mesh.VerticesView.VertexCount;
            if (mesh.VerticesView.TryGetAccessor(ElementName.Position, out var pos))
            {
                var array = ReadVector3(pos, vertexCount);
                foreach (var v in array) centre += v;
                if (array.Length > 0) centre /= array.Length;
                centre = Vector3.Transform(centre, mesh.Transform);
            }

            result.Add(new ImportableMapMesh(index++, mesh.Name, materials, vertexCount,
                mesh.Indices.Count / 3, centre));
        }
        return result;
    }

    /// <summary>
    /// The whole file as an <see cref="Meshes.ImportedScene"/>, so the Add Mesh window can offer a mapgeo
    /// beside the .fbx/.obj it already reads — mesh list, tick boxes, visibility layers and material
    /// mapping all come for free.
    ///
    /// <para>Positions are baked to WORLD space, which is the contract that record already has for
    /// scene files (node transforms applied). The placement offset is then re-derived from the bounds by
    /// the staging code, so an imported mesh lands where the gizmo is rather than where it sat in the
    /// source map — which is what someone lifting a rock out of Summoner's Rift actually wants.</para>
    /// </summary>
    public static Meshes.ImportedScene? ToScene(byte[] mapgeo, out string? error)
    {
        ArgumentNullException.ThrowIfNull(mapgeo);
        error = null;
        try
        {
            using var ms = new MemoryStream(mapgeo, writable: false);
            var env = new EnvironmentAsset(ms);

            var meshes = new List<Meshes.ImportedSceneMesh>();
            var materials = new List<Meshes.ImportedSceneMaterial>();
            var seenMaterial = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mesh in env.Meshes)
            {
                if (!mesh.VerticesView.TryGetAccessor(ElementName.Position, out var positionAccessor)) continue;
                int vertexCount = mesh.VerticesView.VertexCount;

                var positions = new float[vertexCount * 3];
                var positionArray = ReadVector3(positionAccessor, vertexCount);
                for (int i = 0; i < vertexCount; i++)
                {
                    var v = Vector3.Transform(positionArray[i], mesh.Transform);
                    positions[i * 3] = v.X; positions[i * 3 + 1] = v.Y; positions[i * 3 + 2] = v.Z;
                }

                var normals = new float[vertexCount * 3];
                if (mesh.VerticesView.TryGetAccessor(ElementName.Normal, out var normalAccessor))
                {
                    var normalArray = ReadVector3(normalAccessor, vertexCount);
                    for (int i = 0; i < vertexCount; i++)
                    {
                        var n = Vector3.TransformNormal(normalArray[i], mesh.Transform);
                        n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
                        normals[i * 3] = n.X; normals[i * 3 + 1] = n.Y; normals[i * 3 + 2] = n.Z;
                    }
                }

                var uvs = new float[vertexCount * 2];
                if (mesh.VerticesView.TryGetAccessor(ElementName.Texcoord0, out var uvAccessor))
                {
                    var uvArray = ReadVector2(uvAccessor, vertexCount);
                    for (int i = 0; i < vertexCount; i++)
                    { uvs[i * 2] = uvArray[i].X; uvs[i * 2 + 1] = uvArray[i].Y; }
                }

                // One entry per SUBMESH: a mapgeo mesh can carry several materials, and the window maps
                // materials one at a time. Splitting here keeps that mapping honest.
                //
                // M654: each entry gets ONLY the vertices its own indices reach, renumbered from zero.
                // Handing every submesh the parent's whole buffer (what this did until M654) was wrong in
                // two ways that both show up as "Add Mesh from a mapgeo is broken":
                //   - the window's size gate and the staging code's bounds both read Positions, so a
                //     submesh was measured, judged and PLACED as its parent. Measured over Map11's 27
                //     mapgeos: up to 45 entries per file whose own centre is not the parent's, worst
                //     1,850 units away from where the gizmo was;
                //   - the save path casts indices to ushort unchecked, so an index above 65,535 wrapped
                //     silently. Riot's own meshes never reach that (0 of 601 in base_srx), but a merged
                //     or ported map is exactly where it would bite, and compaction removes the question.
                foreach (var submesh in mesh.Submeshes)
                {
                    int end = submesh.StartIndex + submesh.IndexCount;
                    var remap = new Dictionary<int, int>();
                    var indices = new List<int>(submesh.IndexCount);
                    var order = new List<int>();
                    for (int i = submesh.StartIndex; i < end && i < mesh.Indices.Count; i++)
                    {
                        int source = (int)mesh.Indices[i];
                        if (source < 0 || source >= vertexCount) continue;   // a damaged file, not a crash
                        if (!remap.TryGetValue(source, out int mapped))
                        {
                            mapped = order.Count;
                            remap[source] = mapped;
                            order.Add(source);
                        }
                        indices.Add(mapped);
                    }
                    if (indices.Count < 3) continue;

                    var subPositions = new float[order.Count * 3];
                    var subNormals = new float[order.Count * 3];
                    var subUvs = new float[order.Count * 2];
                    for (int i = 0; i < order.Count; i++)
                    {
                        int src = order[i];
                        subPositions[i * 3] = positions[src * 3];
                        subPositions[i * 3 + 1] = positions[src * 3 + 1];
                        subPositions[i * 3 + 2] = positions[src * 3 + 2];
                        subNormals[i * 3] = normals[src * 3];
                        subNormals[i * 3 + 1] = normals[src * 3 + 1];
                        subNormals[i * 3 + 2] = normals[src * 3 + 2];
                        subUvs[i * 2] = uvs[src * 2];
                        subUvs[i * 2 + 1] = uvs[src * 2 + 1];
                    }

                    string material = string.IsNullOrWhiteSpace(submesh.Material) ? "Imported" : submesh.Material;
                    string name = mesh.Submeshes.Count > 1 ? $"{mesh.Name}_{Short(material)}" : mesh.Name;
                    meshes.Add(new Meshes.ImportedSceneMesh(name, material, subPositions, subNormals, subUvs,
                        indices.ToArray()));
                    if (seenMaterial.Add(material))
                        materials.Add(new Meshes.ImportedSceneMaterial(material, null));
                }
            }

            if (meshes.Count == 0) { error = "no drawable mesh in this mapgeo."; return null; }
            return new Meshes.ImportedScene(meshes, materials);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    // mapgeo attributes are float32 OR packed 16-bit, and which one is a per-file choice — the user's own
    // ported Map453 stores positions as XYZ_Packed161616, where AsVector3Array simply refuses. Same
    // fallback the decoder has used since M33; without it this importer read nothing at all from it.
    private static Vector3[] ReadVector3(VertexElementAccessor accessor, int count)
    {
        var result = new Vector3[count];
        try
        {
            var array = accessor.AsVector3Array();
            for (int i = 0; i < count; i++) result[i] = array[i];
        }
        catch
        {
            var array = accessor.AsXyzF16Array();
            for (int i = 0; i < count; i++)
            {
                var h = array[i];
                result[i] = new Vector3((float)h.Item1, (float)h.Item2, (float)h.Item3);
            }
        }
        return result;
    }

    private static Vector2[] ReadVector2(VertexElementAccessor accessor, int count)
    {
        var result = new Vector2[count];
        try
        {
            var array = accessor.AsVector2Array();
            for (int i = 0; i < count; i++) result[i] = array[i];
        }
        catch
        {
            var array = accessor.AsXyF16Array();
            for (int i = 0; i < count; i++)
            {
                var h = array[i];
                result[i] = new Vector2((float)h.Item1, (float)h.Item2);
            }
        }
        return result;
    }

    private static string Short(string s)
    {
        int i = s.LastIndexOf('/');
        return i >= 0 ? s[(i + 1)..] : s;
    }

    /// <summary>
    /// Extract one mesh as something <see cref="MapGeoMeshAppender"/> can splice in.
    ///
    /// <para>Refuses rather than truncates when the mesh has more than 65,535 vertices: the appender's
    /// index buffer is 16-bit, and silently dropping the tail would produce geometry that looks almost
    /// right, which is worse than not importing it.</para>
    /// </summary>
    public static NewMapMesh? Extract(byte[] mapgeo, int meshIndex, out string? error)
    {
        ArgumentNullException.ThrowIfNull(mapgeo);
        error = null;
        try
        {
            using var ms = new MemoryStream(mapgeo, writable: false);
            var env = new EnvironmentAsset(ms);
            if (meshIndex < 0 || meshIndex >= env.Meshes.Count)
            { error = $"mesh {meshIndex} is outside the {env.Meshes.Count} in this file."; return null; }

            var mesh = env.Meshes[meshIndex];
            int vertexCount = mesh.VerticesView.VertexCount;
            if (vertexCount > ushort.MaxValue)
            {
                error = $"'{mesh.Name}' has {vertexCount:n0} vertices; the mapgeo append path writes 16-bit "
                      + "indices and tops out at 65,535.";
                return null;
            }

            if (!mesh.VerticesView.TryGetAccessor(ElementName.Position, out var positionAccessor))
            { error = $"'{mesh.Name}' has no position channel."; return null; }

            var positions = new float[vertexCount * 3];
            var positionArray = ReadVector3(positionAccessor, vertexCount);
            for (int i = 0; i < vertexCount; i++)
            {
                var v = positionArray[i];
                positions[i * 3] = v.X; positions[i * 3 + 1] = v.Y; positions[i * 3 + 2] = v.Z;
            }

            float[]? normals = null;
            if (mesh.VerticesView.TryGetAccessor(ElementName.Normal, out var normalAccessor))
            {
                normals = new float[vertexCount * 3];
                var normalArray = ReadVector3(normalAccessor, vertexCount);
                for (int i = 0; i < vertexCount; i++)
                {
                    var n = normalArray[i];
                    normals[i * 3] = n.X; normals[i * 3 + 1] = n.Y; normals[i * 3 + 2] = n.Z;
                }
            }

            float[]? uvs = null;
            if (mesh.VerticesView.TryGetAccessor(ElementName.Texcoord0, out var uvAccessor))
            {
                uvs = new float[vertexCount * 2];
                var uvArray = ReadVector2(uvAccessor, vertexCount);
                for (int i = 0; i < vertexCount; i++)
                {
                    var uv = uvArray[i];
                    uvs[i * 2] = uv.X; uvs[i * 2 + 1] = uv.Y;
                }
            }

            var source = mesh.Indices;
            var indices = new ushort[source.Count];
            for (int i = 0; i < source.Count; i++) indices[i] = (ushort)source[i];

            string material = mesh.Submeshes.Select(s => s.Material)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? "";

            return new NewMapMesh(material, positions, normals, uvs, indices, mesh.Transform);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }
}

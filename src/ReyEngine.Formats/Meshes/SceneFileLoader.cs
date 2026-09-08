namespace ReyEngine.Formats.Meshes;

/// <summary>What a mesh file turned into, plus where its real materials live when that is knowable.</summary>
/// <param name="SourceMaterialsBin">M512/M654: for a <c>.mapgeo</c>, the sibling <c>.materials.bin</c>
/// when there is one. Copying the original material out of it beats rebuilding one from a shader by a
/// distance - the shader, samplers, macros and render state are Riot's own.</param>
public sealed record LoadedSceneFile(
    ImportedScene Scene,
    string Path,
    bool IsMapGeo,
    string? SourceMaterialsBin);

/// <summary>
/// M656: the one place that decides how a path on disk becomes an <see cref="ImportedScene"/>.
///
/// <para>Add Mesh grew this switch first; the Workshop's mesh shelf needs exactly the same answer for
/// exactly the same extensions, and two copies of "which importer for which extension" is how a format
/// ends up supported in one window and not the other.</para>
/// </summary>
public static class SceneFileLoader
{
    /// <summary>Every extension either window offers, in the order a file picker should list them.</summary>
    public static readonly string[] Extensions =
        { ".mapgeo", ".fbx", ".glb", ".gltf", ".obj", ".scb", ".sco", ".skn" };

    public static bool IsSupported(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Never throws - null plus a reason on failure.</summary>
    public static LoadedSceneFile? Load(string path, out string? error)
    {
        error = null;
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        bool isMapGeo = ext is ".mapgeo";
        ImportedScene? scene;
        string? sourceBin = null;
        try
        {
            if (isMapGeo)
            {
                scene = MapGeo.MapGeoMeshImporter.ToScene(File.ReadAllBytes(path), out error);
                string sibling = System.IO.Path.ChangeExtension(path, null) + ".materials.bin";
                if (File.Exists(sibling)) sourceBin = sibling;
            }
            else if (ext is ".skn")
                scene = LoadSkinnedAsStatic(path, out error);
            else if (ext is ".obj" or ".scb" or ".sco")
                scene = LoadLegacy(path, out error);
            else
                scene = SceneMeshImporter.Import(path, out error);
        }
        catch (Exception ex) { error = $"{ex.GetType().Name}: {ex.Message}"; return null; }

        if (scene is null) { error ??= "the file could not be read."; return null; }
        return new LoadedSceneFile(scene, path, isMapGeo, sourceBin);
    }

    /// <summary>
    /// M662: a <c>.skn</c> as STATIC geometry - the skeleton is deliberately not read.
    ///
    /// <para>A SimpleSkin carries bone indices and weights, but a map has no skeleton to bind them to and
    /// the mapgeo append path writes Position/Normal/Texcoord0 and nothing else. Everything needed to put
    /// a champion's mesh into a map is already in the file without them, so they are dropped rather than
    /// the format being refused - which is what it did until now, since it was not in
    /// <see cref="Extensions"/> at all.</para>
    ///
    /// <para>One entry per SUBMESH, each carrying only the vertices its own triangles reach, renumbered
    /// from zero. That is the M654 rule and it matters here for the same reason: a champion is one vertex
    /// buffer of a dozen submeshes, and handing each entry the whole buffer would size, gate and PLACE
    /// every piece as the whole character.</para>
    /// </summary>
    private static ImportedScene? LoadSkinnedAsStatic(string path, out string? error)
    {
        error = null;
        try
        {
            var mesh = SkinnedMeshDecoder.Decode(File.ReadAllBytes(path));
            if (mesh.VertexCount == 0 || mesh.Indices.Length < 3) { error = "no drawable geometry in this .skn"; return null; }

            string file = System.IO.Path.GetFileNameWithoutExtension(path);
            var meshes = new List<ImportedSceneMesh>();
            var materials = new List<ImportedSceneMaterial>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var pieces = mesh.SubMeshes.Count > 0
                ? mesh.SubMeshes
                : new[] { new SubMeshInfo(file, 0, mesh.Indices.Length, mesh.VertexCount) };

            foreach (var piece in pieces)
            {
                var remap = new Dictionary<int, int>();
                var order = new List<int>();
                var indices = new List<int>(piece.IndexCount);
                int end = piece.StartIndex + piece.IndexCount;
                for (int i = piece.StartIndex; i < end && i < mesh.Indices.Length; i++)
                {
                    int source = (int)mesh.Indices[i];
                    if (source < 0 || source >= mesh.VertexCount) continue;
                    if (!remap.TryGetValue(source, out int mapped))
                    {
                        mapped = order.Count;
                        remap[source] = mapped;
                        order.Add(source);
                    }
                    indices.Add(mapped);
                }
                if (indices.Count < 3) continue;

                var positions = new float[order.Count * 3];
                var normals = new float[order.Count * 3];
                var uvs = new float[order.Count * 2];
                for (int i = 0; i < order.Count; i++)
                {
                    int src = order[i];
                    positions[i * 3] = mesh.Positions[src * 3];
                    positions[i * 3 + 1] = mesh.Positions[src * 3 + 1];
                    positions[i * 3 + 2] = mesh.Positions[src * 3 + 2];
                    if (mesh.Normals.Length >= (src + 1) * 3)
                    {
                        normals[i * 3] = mesh.Normals[src * 3];
                        normals[i * 3 + 1] = mesh.Normals[src * 3 + 1];
                        normals[i * 3 + 2] = mesh.Normals[src * 3 + 2];
                    }
                    if (mesh.Uvs.Length >= (src + 1) * 2)
                    {
                        uvs[i * 2] = mesh.Uvs[src * 2];
                        uvs[i * 2 + 1] = mesh.Uvs[src * 2 + 1];
                    }
                }

                string material = string.IsNullOrWhiteSpace(piece.Material) ? file : piece.Material;
                string name = pieces.Count > 1 ? $"{file}_{material}" : file;
                meshes.Add(new ImportedSceneMesh(name, material, positions, normals, uvs, indices.ToArray()));
                if (seen.Add(material)) materials.Add(new ImportedSceneMaterial(material, null));
            }

            if (meshes.Count == 0) { error = "no drawable submesh in this .skn"; return null; }
            return new ImportedScene(meshes, materials);
        }
        catch (Exception ex) { error = $"{ex.GetType().Name}: {ex.Message}"; return null; }
    }

    /// <summary>.obj/.scb/.sco go through the old single-mesh importers - one mesh, no material info.</summary>
    private static ImportedScene? LoadLegacy(string path, out string? error)
    {
        error = null;
        try
        {
            float[]? pos, nrm = null, uv = null; int[]? idx;
            if (path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            {
                var m = ObjMeshImporter.Import(File.ReadAllText(path), System.IO.Path.GetFileName(path));
                if (m is null) { error = "obj parse failed"; return null; }
                (pos, nrm, uv, idx) = (m.Positions, m.Normals, m.Uvs, m.Indices);
            }
            else
            {
                var sm = StaticObjectDecoder.Decode(File.ReadAllBytes(path), path);
                if (sm is null) { error = "scb/sco parse failed"; return null; }
                (pos, uv, idx) = (sm.Positions, sm.Uvs, Array.ConvertAll(sm.Indices, i => (int)i));
            }
            if (pos is null || idx is null) { error = "empty mesh"; return null; }
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var mesh = new ImportedSceneMesh(name, "Imported", pos, nrm ?? new float[pos.Length],
                uv ?? new float[pos.Length / 3 * 2], idx);
            return new ImportedScene(new[] { mesh }, new[] { new ImportedSceneMaterial("Imported", null) });
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }
}

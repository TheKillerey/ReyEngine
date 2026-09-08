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
        { ".mapgeo", ".fbx", ".glb", ".gltf", ".obj", ".scb", ".sco" };

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
            else if (ext is ".obj" or ".scb" or ".sco")
                scene = LoadLegacy(path, out error);
            else
                scene = SceneMeshImporter.Import(path, out error);
        }
        catch (Exception ex) { error = $"{ex.GetType().Name}: {ex.Message}"; return null; }

        if (scene is null) { error ??= "the file could not be read."; return null; }
        return new LoadedSceneFile(scene, path, isMapGeo, sourceBin);
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

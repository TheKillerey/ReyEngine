namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M517: change which material an EXISTING mesh is drawn with.
///
/// <para>Unlike the layer/visibility edits, this one cannot be patched in place: a submesh's material is a
/// length-prefixed string, so a longer or shorter name moves every byte after it. That rules out
/// <see cref="MapGeoLayerWriter"/>'s signature-and-poke approach and calls for the full round trip through
/// <see cref="MapGeoBinary"/>, which reads the whole file into a model and writes it back — byte-exact
/// when nothing changed (M159), which is what makes it safe to run over a map that has only a material
/// edit pending.</para>
///
/// <para>An edit applies to EVERY submesh of the mesh. Most map meshes have exactly one; a mesh with
/// several is being told to draw entirely in the chosen material, which is what "change the material of
/// this mesh" means and what the caller is expected to have made clear.</para>
/// </summary>
public static class MapGeoMaterialWriter
{
    public static bool HasEdits(IEnumerable<MapGeoMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        return meshes.Any(m => m.HasMaterialEdit);
    }

    /// <param name="extendedChannelMaterials">The Mantis material names, as
    /// <see cref="MapGeoBinary.Read(byte[], IReadOnlySet{string}?)"/> needs them — the per-mesh channel
    /// block is 40 or 48 bytes depending on the MATERIAL, so reading without this desynchronises the mesh
    /// section on a map that uses one (M444).</param>
    public static byte[]? TryWriteMaterialEdits(byte[] originalMapgeo, IReadOnlyList<MapGeoMesh> meshes,
        IReadOnlySet<string>? extendedChannelMaterials, out string? error)
    {
        ArgumentNullException.ThrowIfNull(originalMapgeo);
        ArgumentNullException.ThrowIfNull(meshes);
        error = null;

        var edits = meshes.Where(m => m.HasMaterialEdit).ToList();
        if (edits.Count == 0) return originalMapgeo;

        MapGeoBinary binary;
        try { binary = MapGeoBinary.Read(originalMapgeo, extendedChannelMaterials); }
        catch (Exception ex) { error = $"Mapgeo did not parse for material editing: {ex.Message}"; return null; }

        foreach (var mesh in edits)
        {
            if (mesh.Index < 0 || mesh.Index >= binary.Meshes.Count)
            {
                error = $"Mesh {mesh.Index} is outside the {binary.Meshes.Count} in the file — nothing was written.";
                return null;
            }
            string material = mesh.MaterialEdit!;
            foreach (var submesh in binary.Meshes[mesh.Index].Submeshes) submesh.Material = material;
        }

        try { return binary.Write(); }
        catch (Exception ex) { error = $"Mapgeo could not be rewritten: {ex.Message}"; return null; }
    }
}

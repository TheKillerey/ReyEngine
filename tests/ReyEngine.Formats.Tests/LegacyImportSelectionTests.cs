using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M472: which meshes a legacy-import nudge is allowed to move. The nudge exists because
/// <see cref="LegacyMapPorter.LegacyPositionCorrection"/> is baked into the geometry at port time and
/// recorded nowhere, so fine-tuning it otherwise meant re-porting the whole map.
///
/// <para>The selection is the dangerous half, not the arithmetic: a group nudge that also caught the
/// DESTINATION map's own geometry would move the part that was already aligned, and the symptom (the whole
/// map shifted) looks nothing like the cause. So these pin the boundary.</para>
/// </summary>
public class LegacyImportSelectionTests
{
    private static MapGeoMesh Mesh(int index, string name) => new()
    {
        Index = index,
        Name = name,
        VertexStart = 0,
        VertexCount = 0,
        Transform = Matrix4x4.Identity,
        Pivot = Vector3.Zero,
    };

    private static MapGeoAsset Asset(params (int MeshIndex, string Material)[] groups) => new()
    {
        Positions = Array.Empty<float>(),
        Normals = Array.Empty<float>(),
        Uvs = Array.Empty<float>(),
        Indices = Array.Empty<uint>(),
        Groups = groups.Select(g => new MapGeoGroup(g.Material, 0, 0, MeshIndex: g.MeshIndex)).ToList(),
        Meshes = groups.Select(g => g.MeshIndex).Distinct().Select(i => Mesh(i, $"MapGeo_Instance_{i}")).ToList(),
    };

    [Fact]
    public void Only_meshes_with_LegacyPort_materials_are_selected()
    {
        var map = Asset(
            (0, "LegacyPort/Terrain_01"),
            (1, "Maps/KitPieces/Jade/Base/Materials/Default/Jade_Rock_BA_MAT"),
            (2, "LegacyPort/Grass_03"));

        var imported = LegacyMapPorter.ImportedMeshes(map);

        Assert.Equal(new[] { 0, 2 }, imported.Select(m => m.Index).OrderBy(i => i));
    }

    /// <summary>Riot batches unlike materials into one mesh record. A mesh with even one imported submesh
    /// IS imported geometry — leaving it behind would tear the import in half along a seam the user cannot
    /// see.</summary>
    [Fact]
    public void A_mesh_is_imported_if_any_of_its_submeshes_is()
    {
        var map = Asset(
            (0, "Maps/KitPieces/Jade/Base/Materials/Default/Jade_Rock_BA_MAT"),
            (0, "LegacyPort/Terrain_01"));

        Assert.Single(LegacyMapPorter.ImportedMeshes(map));
    }

    [Fact]
    public void A_map_with_no_import_selects_nothing()
    {
        var map = Asset((0, "Maps/KitPieces/Jade/Base/Materials/Default/Jade_Rock_BA_MAT"));
        Assert.Empty(LegacyMapPorter.ImportedMeshes(map));
    }

    /// <summary>Groups with no owning mesh (MeshIndex -1) occur in decoded assets; they must not select a
    /// mesh by accident — index -1 would otherwise match nothing or, worse, be used as a lookup.</summary>
    [Fact]
    public void Groups_without_a_mesh_index_are_ignored()
    {
        var map = new MapGeoAsset
        {
            Positions = Array.Empty<float>(),
            Normals = Array.Empty<float>(),
            Uvs = Array.Empty<float>(),
            Indices = Array.Empty<uint>(),
            Groups = new[] { new MapGeoGroup("LegacyPort/Terrain_01", 0, 0, MeshIndex: -1) },
            Meshes = new[] { Mesh(0, "MapGeo_Instance_0") },
        };

        Assert.Empty(LegacyMapPorter.ImportedMeshes(map));
    }

    [Theory]
    [InlineData("LegacyPort/Terrain_01", true)]
    [InlineData("legacyport/terrain_01", true)]      // the porter writes it, the corpus may not match case
    [InlineData("Maps/KitPieces/Jade/X", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_marker_is_the_material_prefix(string? material, bool expected) =>
        Assert.Equal(expected, LegacyMapPorter.IsLegacyImportMaterial(material));

    /// <summary>The nudge must ACCUMULATE. TranslateMesh assigns Offset rather than adding to it, so the
    /// caller passes Offset + delta; passing the raw delta would make a second nudge silently undo the
    /// first, which reads as "the button stopped working".</summary>
    [Fact]
    public void Nudging_twice_accumulates()
    {
        var map = Asset((0, "LegacyPort/Terrain_01"));
        var mesh = LegacyMapPorter.ImportedMeshes(map).Single();

        map.TranslateMesh(mesh, mesh.Offset + new Vector3(10, 0, 0));
        map.TranslateMesh(mesh, mesh.Offset + new Vector3(10, 0, 0));

        Assert.Equal(new Vector3(20, 0, 0), mesh.Offset);
    }
}

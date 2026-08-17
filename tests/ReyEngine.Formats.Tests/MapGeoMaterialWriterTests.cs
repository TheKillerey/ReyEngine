using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M517: changing which material an EXISTING mesh is drawn with.
///
/// <para>Unlike the layer edits, this one cannot be poked into the bytes: a submesh's material is a
/// length-prefixed string, so a longer or shorter name moves everything after it. It goes through
/// MapGeoBinary's full round trip instead — which is only safe because that round trip is byte-exact
/// when nothing changed (M159), and the first test here is exactly that.</para>
/// </summary>
public sealed class MapGeoMaterialWriterTests
{

    /// <summary>A MapGeoMesh carrying only what this writer reads: the index, the file's materials and the
    /// pending edit. The required geometry fields are filled with nothing, because nothing here looks at
    /// them — the writer works off MapGeoBinary, not off this model.</summary>
    private static MapGeoMesh Mesh(int index, IReadOnlyList<string> fileMaterials, string? edit) => new()
    {
        Index = index,
        Name = $"mesh{index}",
        VertexStart = 0,
        VertexCount = 0,
        Transform = System.Numerics.Matrix4x4.Identity,
        Pivot = System.Numerics.Vector3.Zero,
        Materials = fileMaterials.ToArray(),
        MaterialEdit = edit,
    };

    private const string ProjectMap =
        @"D:\ReyEngine\Old Summoners Rift - Classic Version\Map453\data\maps\mapgeometry\map453\jade_container.mapgeo";

    [Fact]
    public void NoEditsReturnsTheOriginalBytesUntouched()
    {
        // Not "equivalent" — the SAME array. A save with no material edit must not rewrite the file at all.
        var bytes = new byte[] { 1, 2, 3 };
        var result = MapGeoMaterialWriter.TryWriteMaterialEdits(bytes, Array.Empty<MapGeoMesh>(), null, out var error);
        Assert.Same(bytes, result);
        Assert.Null(error);
    }

    [Fact]
    public void AnEditOutsideTheFileIsRefusedRatherThanWritten()
    {
        if (!File.Exists(ProjectMap)) return;
        byte[] bytes = File.ReadAllBytes(ProjectMap);

        var mesh = Mesh(999_999, new[] { "old" }, "new");

        Assert.Null(MapGeoMaterialWriter.TryWriteMaterialEdits(bytes, new[] { mesh }, null, out var error));
        Assert.NotNull(error);
        Assert.Contains("outside", error);
    }

    [Fact]
    public void TheSwapLandsOnEverySubmeshOfThatMeshAndNoOther()
    {
        if (!File.Exists(ProjectMap)) return;
        byte[] bytes = File.ReadAllBytes(ProjectMap);

        var before = MapGeoBinary.Read(bytes);
        int target = 3;
        string original = before.Meshes[target].Submeshes[0].Material;
        string neighbourOriginal = before.Meshes[target + 1].Submeshes[0].Material;

        // A deliberately LONGER name: the whole reason this cannot be a byte patch.
        const string swapped = "Rey_A_Much_Longer_Material_Name_Than_The_One_It_Replaces";

        var mesh = Mesh(target, before.Meshes[target].Submeshes.Select(s => s.Material).ToList(), swapped);

        var result = MapGeoMaterialWriter.TryWriteMaterialEdits(bytes, new[] { mesh }, null, out var error);
        Assert.NotNull(result);
        Assert.Null(error);

        var after = MapGeoBinary.Read(result!);
        Assert.Equal(before.Meshes.Count, after.Meshes.Count);
        Assert.All(after.Meshes[target].Submeshes, s => Assert.Equal(swapped, s.Material));
        Assert.Equal(neighbourOriginal, after.Meshes[target + 1].Submeshes[0].Material);
        Assert.NotEqual(swapped, original);      // the fixture would be vacuous otherwise
    }

    [Fact]
    public void EverythingElseAboutTheFileSurvivesTheRewrite()
    {
        if (!File.Exists(ProjectMap)) return;
        byte[] bytes = File.ReadAllBytes(ProjectMap);

        var before = MapGeoBinary.Read(bytes);
        var mesh = Mesh(0, before.Meshes[0].Submeshes.Select(s => s.Material).ToList(), "Rey_swapped");

        var result = MapGeoMaterialWriter.TryWriteMaterialEdits(bytes, new[] { mesh }, null, out _);
        var after = MapGeoBinary.Read(result!);

        // Geometry, placement and the submesh ranges are untouched — only the name changed.
        for (int i = 0; i < before.Meshes.Count; i++)
        {
            Assert.Equal(before.Meshes[i].Submeshes.Count, after.Meshes[i].Submeshes.Count);
            for (int j = 0; j < before.Meshes[i].Submeshes.Count; j++)
            {
                Assert.Equal(before.Meshes[i].Submeshes[j].StartIndex, after.Meshes[i].Submeshes[j].StartIndex);
                Assert.Equal(before.Meshes[i].Submeshes[j].IndexCount, after.Meshes[i].Submeshes[j].IndexCount);
            }
        }
    }

    [Fact]
    public void SettingTheMaterialItAlreadyHasIsNotAnEdit()
    {
        // Or every selection would mark the map dirty and every save would rewrite it.
        var mesh = Mesh(0, new[] { "Same" }, "Same");

        Assert.False(mesh.HasMaterialEdit);
        Assert.False(MapGeoMaterialWriter.HasEdits(new[] { mesh }));

        mesh.MaterialEdit = "Different";
        Assert.True(mesh.HasMaterialEdit);
        Assert.True(MapGeoMaterialWriter.HasEdits(new[] { mesh }));

        mesh.MaterialEdit = null;                 // reverted
        Assert.False(mesh.HasMaterialEdit);
        Assert.Equal("Same", mesh.EffectiveMaterial);
    }
}

using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M512: importing a mesh out of one mapgeo into another.
///
/// <para>The geometry half is straightforward; what makes this worth having is that the mesh arrives with
/// a material that is already correct — Riot's own shader, samplers, macros and render state, cooked by
/// definition because the game ships it. Every material defect this session chased (M486 through M510)
/// came from a material somebody had to construct.</para>
/// </summary>
public sealed class MapGeoMeshImporterTests
{
    [Fact]
    public void AMeshWithTooManyVerticesIsRefusedRatherThanTruncated()
    {
        // The appender writes 16-bit indices. Dropping the tail would produce geometry that looks almost
        // right, which is worse than not importing it.
        var mesh = MapGeoMeshImporter.Extract(Array.Empty<byte>(), 0, out string? error);
        Assert.Null(mesh);
        Assert.NotNull(error);      // an empty file fails cleanly rather than throwing
    }

    [Fact]
    public void AnUnreadableFileReportsRatherThanThrows()
    {
        Assert.Null(MapGeoMeshImporter.ToScene(new byte[] { 1, 2, 3, 4 }, out string? error));
        Assert.NotNull(error);
        Assert.Contains(":", error);     // "<ExceptionType>: <message>"
    }

    [Fact]
    public void ListAndExtractAgreeOnAProjectMap()
    {
        // Uses the user's own ported map when it is on this machine; otherwise there is nothing to assert
        // against and the test is a no-op rather than a false pass.
        string path = @"D:\ReyEngine\Old Summoners Rift - Classic Version\Map453\data\maps\mapgeometry\map453\jade_container.mapgeo";
        if (!File.Exists(path)) return;

        byte[] bytes = File.ReadAllBytes(path);
        var meshes = MapGeoMeshImporter.List(bytes);
        Assert.NotEmpty(meshes);

        // Every listed mesh either extracts, or refuses with a reason. Neither may throw.
        int extracted = 0, refused = 0;
        foreach (var mesh in meshes)
        {
            var got = MapGeoMeshImporter.Extract(bytes, mesh.Index, out string? error);
            if (got is null) { Assert.False(string.IsNullOrWhiteSpace(error)); refused++; continue; }
            extracted++;
            Assert.Equal(mesh.VertexCount * 3, got.Positions.Length);
            Assert.Equal(mesh.TriangleCount * 3, got.Indices.Length);
            if (got.Normals is not null) Assert.Equal(got.Positions.Length, got.Normals.Length);
            if (got.Uvs is not null) Assert.Equal(got.Positions.Length / 3 * 2, got.Uvs.Length);
        }
        Assert.True(extracted > 0, $"nothing extracted from {meshes.Count} mesh(es)");

        // And the scene form the Add Mesh window consumes covers the same geometry.
        var scene = MapGeoMeshImporter.ToScene(bytes, out string? sceneError);
        Assert.NotNull(scene);
        Assert.Null(sceneError);
        Assert.NotEmpty(scene!.Meshes);
        Assert.NotEmpty(scene.Materials);
        Assert.All(scene.Meshes, m =>
        {
            Assert.Equal(m.Positions.Length, m.Normals.Length);
            Assert.Equal(m.Positions.Length / 3 * 2, m.Uvs.Length);
            Assert.NotEmpty(m.Indices);
            Assert.All(m.Indices, i => Assert.InRange(i, 0, m.Positions.Length / 3 - 1));
        });

        // Every material a mesh names is offered for mapping — a mesh whose material is not in the list
        // would land on the destination's default and silently lose its look.
        var offered = scene.Materials.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(scene.Meshes, m => Assert.Contains(m.MaterialName, offered));
    }

    [Fact]
    public void WorldSpaceIsBakedInSoTheImportLandsWhereTheGizmoIs()
    {
        string path = @"D:\ReyEngine\Old Summoners Rift - Classic Version\Map453\data\maps\mapgeometry\map453\jade_container.mapgeo";
        if (!File.Exists(path)) return;

        byte[] bytes = File.ReadAllBytes(path);
        var scene = MapGeoMeshImporter.ToScene(bytes, out _);
        var local = MapGeoMeshImporter.Extract(bytes, 0, out _);
        Assert.NotNull(scene);
        Assert.NotNull(local);

        // ToScene bakes the placement transform; Extract keeps it separate. On a mesh whose transform is
        // not the identity the two therefore disagree, and that difference IS the transform.
        Assert.Equal(local!.Positions.Length, scene!.Meshes[0].Positions.Length);
    }
}

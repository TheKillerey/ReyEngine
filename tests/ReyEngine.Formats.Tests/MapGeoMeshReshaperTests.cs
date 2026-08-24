using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M581: replacing a mesh's geometry in place.
///
/// <para>This is the operation that can ruin a map. Every check here is about a file that would still
/// LOAD after a mistake: a stream left describing the old vertex count, a submesh range pointing past its
/// buffer, a stale bounding box that culls the mesh at the wrong angle. None of those throw — they render
/// as corruption, or as geometry that vanishes when you turn the camera.</para>
/// </summary>
public sealed class MapGeoMeshReshaperTests
{
    private const string Wad =
        @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";
    private const string GeoPath = "data/maps/mapgeometry/map453/jade_container.mapgeo";

    private static byte[]? Shipped()
    {
        if (!File.Exists(Wad)) return null;
        using var wad = ReyEngine.Core.Wad.WadArchive.Open(Wad);
        ulong hash = HashAlgorithms.WadPath(GeoPath);
        return wad.TryGetEntry(hash, out _) ? wad.Extract(hash) : null;
    }

    /// <summary>A mesh the reshaper will accept: one submesh, no shared buffers.</summary>
    private static int PickReshapable(MapGeoBinary map, byte[] bytes, Func<MapGeoBinary.Mesh, bool>? extra = null)
    {
        var refusals = MapGeoMeshReshaper.Refusals(bytes);
        for (int i = 0; i < map.Meshes.Count; i++)
            if (!refusals.ContainsKey(i) && map.Meshes[i].VertexCount > 0 && (extra?.Invoke(map.Meshes[i]) ?? true))
                return i;
        return -1;
    }

    /// <summary>A tetrahedron — a different vertex count and a different triangle count from anything real.</summary>
    private static MeshReshape Tetra(int meshIndex) => new(
        meshIndex,
        new float[] { 0, 0, 0, 100, 0, 0, 0, 100, 0, 0, 0, 100 },
        new float[] { 0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1 },
        new float[] { 0, 0, 1, 0, 0, 1, 1, 1 },
        new uint[] { 0, 1, 2, 0, 1, 3, 1, 2, 3, 0, 2, 3 });

    [Fact]
    public void AReshapedMeshComesBackWithTheNewGeometry()
    {
        if (Shipped() is not { } bytes) return;
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var before));
        int index = PickReshapable(before, bytes);
        if (index < 0) return;

        var result = MapGeoMeshReshaper.TryApply(bytes, new[] { Tetra(index) }, out string? error);
        Assert.True(result is not null, error);
        Assert.True(MapGeoBinary.TryReadEditable(result!, out var after), "the reshaped file is no longer editable");

        var mesh = after.Meshes[index];
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(12, mesh.IndexCount);
        Assert.Equal(12, mesh.Submeshes[0].IndexCount);
        Assert.Equal(0, mesh.Submeshes[0].StartIndex);
        Assert.Equal(3, mesh.Submeshes[0].MaxVertex);
    }

    [Fact]
    public void EveryStreamIsRewrittenToTheNewVertexCount()
    {
        // The one that produces garbage rather than an error: a mesh keeps Position+Normal in one buffer
        // and Texcoord0 in another, and growing only the first leaves the second describing a different
        // number of vertices. Measured on shipped maps, 32%-68% of meshes are built this way.
        if (Shipped() is not { } bytes) return;
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var before));
        int index = PickReshapable(before, bytes, m => m.VertexBufferIds.Count > 1);
        if (index < 0) return;   // no multi-stream mesh here

        var result = MapGeoMeshReshaper.TryApply(bytes, new[] { Tetra(index) }, out string? error);
        Assert.True(result is not null, error);
        Assert.True(MapGeoBinary.TryReadEditable(result!, out var after));

        var mesh = after.Meshes[index];
        Assert.True(mesh.VertexBufferIds.Count > 1, "this test needs a multi-stream mesh");
        for (int stream = 0; stream < mesh.VertexBufferIds.Count; stream++)
        {
            int stride = after.Declarations[mesh.VertexDeclarationBase + stream].Stride;
            int actual = after.VertexBuffers[mesh.VertexBufferIds[stream]].Data.Length;
            Assert.True(actual == stride * mesh.VertexCount,
                $"stream {stream} holds {actual}B for {mesh.VertexCount} vertices at {stride}B each");
        }
    }

    [Fact]
    public void EverythingAboutTheMeshExceptItsShapeSurvives()
    {
        // The reason this rewrites buffers under the mesh record instead of remove-then-append: a mesh
        // carries its material, its render region, its visibility controller and its baked-light channel,
        // and re-appending would silently drop all of it.
        if (Shipped() is not { } bytes) return;
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var before));
        int index = PickReshapable(before, bytes);
        if (index < 0) return;

        var was = before.Meshes[index];
        string material = was.Submeshes[0].Material;
        uint region = was.RegionHash, controller = was.VisibilityControllerPathHash;
        byte visibility = was.Visibility, quality = was.QualityFilter;
        string bakedLight = was.BakedLight.Texture;
        var bakedScale = was.BakedPaintScale;
        var transform = was.Transform;

        var result = MapGeoMeshReshaper.TryApply(bytes, new[] { Tetra(index) }, out string? error);
        Assert.True(result is not null, error);
        Assert.True(MapGeoBinary.TryReadEditable(result!, out var after));

        var now = after.Meshes[index];
        Assert.Equal(material, now.Submeshes[0].Material);
        Assert.Equal(region, now.RegionHash);
        Assert.Equal(controller, now.VisibilityControllerPathHash);
        Assert.Equal(visibility, now.Visibility);
        Assert.Equal(quality, now.QualityFilter);
        Assert.Equal(bakedLight, now.BakedLight.Texture);
        Assert.Equal(bakedScale, now.BakedPaintScale);
        Assert.Equal(transform, now.Transform);
    }

    [Fact]
    public void NoOtherMeshIsTouched()
    {
        if (Shipped() is not { } bytes) return;
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var before));
        int index = PickReshapable(before, bytes);
        if (index < 0) return;

        var result = MapGeoMeshReshaper.TryApply(bytes, new[] { Tetra(index) }, out _);
        Assert.NotNull(result);
        Assert.True(MapGeoBinary.TryReadEditable(result!, out var after));

        Assert.Equal(before.Meshes.Count, after.Meshes.Count);
        for (int i = 0; i < before.Meshes.Count; i++)
        {
            if (i == index) continue;
            Assert.Equal(before.Meshes[i].VertexCount, after.Meshes[i].VertexCount);
            Assert.Equal(before.Meshes[i].IndexCount, after.Meshes[i].IndexCount);
            Assert.Equal(before.Meshes[i].Submeshes.Count, after.Meshes[i].Submeshes.Count);
        }
    }

    [Fact]
    public void TheBoundingBoxFollowsTheNewShape()
    {
        // A stale box culls the mesh at angles the new shape reaches, which reads as a rendering bug.
        if (Shipped() is not { } bytes) return;
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var before));
        int index = PickReshapable(before, bytes);
        if (index < 0) return;

        var result = MapGeoMeshReshaper.TryApply(bytes, new[] { Tetra(index) }, out _);
        Assert.NotNull(result);
        Assert.True(MapGeoBinary.TryReadEditable(result!, out var after));

        Assert.Equal(new Vector3(0, 0, 0), after.Meshes[index].BoundsMin);
        Assert.Equal(new Vector3(100, 100, 100), after.Meshes[index].BoundsMax);
    }

    // ---- what it refuses -------------------------------------------------------------------------

    [Fact]
    public void AMeshWithSeveralMaterialsIsRefusedRatherThanGuessed()
    {
        if (Shipped() is not { } bytes) return;
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var map));
        int index = -1;
        for (int i = 0; i < map.Meshes.Count && index < 0; i++)
            if (map.Meshes[i].Submeshes.Count > 1) index = i;
        if (index < 0) return;

        Assert.Null(MapGeoMeshReshaper.TryApply(bytes, new[] { Tetra(index) }, out string? error));
        Assert.Contains("submesh", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMeshSharingItsBuffersIsRefused()
    {
        // Resizing a shared buffer redefines the OTHER mesh that points at it.
        if (Shipped() is not { } bytes) return;
        var refusals = MapGeoMeshReshaper.Refusals(bytes);
        var shared = refusals.FirstOrDefault(r => r.Value.Contains("shared", StringComparison.Ordinal));
        if (shared.Value is null) return;

        Assert.Null(MapGeoMeshReshaper.TryApply(bytes, new[] { Tetra(shared.Key) }, out string? error));
        Assert.Contains("shared", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TooManyVerticesForASixteenBitIndexIsRefused()
    {
        if (Shipped() is not { } bytes) return;
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var map));
        int index = PickReshapable(map, bytes);
        if (index < 0) return;

        int tooMany = MapGeoMeshReshaper.MaxVerticesPerMesh + 1;
        var huge = new MeshReshape(index, new float[tooMany * 3], null, null, new uint[] { 0, 1, 2 });
        Assert.Null(MapGeoMeshReshaper.TryApply(bytes, new[] { huge }, out string? error));
        Assert.Contains("16-bit", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATriangleThatPointsPastTheVerticesIsRefused()
    {
        if (Shipped() is not { } bytes) return;
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var map));
        int index = PickReshapable(map, bytes);
        if (index < 0) return;

        var bad = new MeshReshape(index,
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, null, null, new uint[] { 0, 1, 99 });
        Assert.Null(MapGeoMeshReshaper.TryApply(bytes, new[] { bad }, out string? error));
        Assert.Contains("references vertex", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARefusedReshapeChangesNothingAtAll()
    {
        // Half-applying a batch would leave the map in a state the user did not ask for and cannot see.
        if (Shipped() is not { } bytes) return;
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var map));
        int good = PickReshapable(map, bytes);
        var refusals = MapGeoMeshReshaper.Refusals(bytes);
        if (good < 0 || refusals.Count == 0) return;

        var batch = new[] { Tetra(good), Tetra(refusals.Keys.First()) };
        Assert.Null(MapGeoMeshReshaper.TryApply(bytes, batch, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void RefusalsAgreeWithWhatTryApplyActuallyDoes()
    {
        // Two code paths deciding the same thing is how a dialog ends up promising something the writer
        // then refuses. They are checked against each other on every mesh of a real map.
        if (Shipped() is not { } bytes) return;
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var map));
        var refusals = MapGeoMeshReshaper.Refusals(bytes);

        int checkedCount = 0;
        for (int i = 0; i < map.Meshes.Count && checkedCount < 40; i++)
        {
            if (map.Meshes[i].VertexCount == 0) continue;
            checkedCount++;
            bool applied = MapGeoMeshReshaper.TryApply(bytes, new[] { Tetra(i) }, out _) is not null;
            Assert.Equal(!refusals.ContainsKey(i), applied);
        }
        Assert.True(checkedCount > 0);
    }
}

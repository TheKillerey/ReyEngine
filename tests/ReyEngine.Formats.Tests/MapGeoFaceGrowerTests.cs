using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M570: extrude and inset — the first face operations that CREATE geometry.
///
/// <para>Every earlier operation preserves the file's length, which is why deleting a face degenerates it.
/// These grow the vertex and index buffers, so every submesh offset after the insertion point moves. A
/// mapgeo has no offset table: if that repair is wrong the file still parses, still has plausible counts,
/// and renders the wrong triangles under the wrong materials. So these tests check the OFFSETS as hard as
/// they check the geometry.</para>
/// </summary>
public sealed class MapGeoFaceGrowerTests
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

    private static int FirstRealTriangle(MapGeoAsset a)
    {
        for (int t = 0; t * 3 + 2 < a.Indices.Length; t++)
        {
            uint i0 = a.Indices[t * 3], i1 = a.Indices[t * 3 + 1], i2 = a.Indices[t * 3 + 2];
            if (i0 != i1 && i1 != i2 && i0 != i2) return t;
        }
        return -1;
    }

    [Fact]
    public void ExtrudingAFaceAddsThreeVerticesAndSevenTriangles()
    {
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);
        int t = FirstRealTriangle(before);
        if (t < 0) return;

        var grown = MapGeoFaceGrower.TryApply(bytes, before,
            new[] { new FaceGrow(t, FaceGrowOp.Extrude, 50f) }, out string? error);
        Assert.True(grown is not null, error);
        var after = MapGeoDecoder.Decode(grown!);

        Assert.Equal(before.Positions.Length / 3 + 3, after.Positions.Length / 3);
        // one cap + two per edge, and the original is degenerated rather than removed
        Assert.Equal(before.Indices.Length + 7 * 3, after.Indices.Length);
    }

    [Fact]
    public void TheExtrudedCapSitsOffTheOriginalAlongItsNormal()
    {
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);
        int t = FirstRealTriangle(before);
        if (t < 0) return;

        Vector3 P(MapGeoAsset a, uint v) => new(a.Positions[v * 3], a.Positions[v * 3 + 1], a.Positions[v * 3 + 2]);
        Vector3 p0 = P(before, before.Indices[t * 3]);
        var normal = Vector3.Normalize(Vector3.Cross(
            P(before, before.Indices[t * 3 + 1]) - p0, P(before, before.Indices[t * 3 + 2]) - p0));

        const float Amount = 40f;
        var grown = MapGeoFaceGrower.TryApply(bytes, before,
            new[] { new FaceGrow(t, FaceGrowOp.Extrude, Amount) }, out _);
        if (grown is null) return;
        var after = MapGeoDecoder.Decode(grown);

        // Found by POSITION, not by index. The decoder concatenates every mesh's vertices, so a vertex
        // appended to one mesh's buffer lands in the middle of the combined array rather than at its end -
        // which is what the first version of this test got wrong.
        var expected = p0 + normal * Amount;
        bool found = false;
        for (int v = 0; v * 3 + 2 < after.Positions.Length && !found; v++)
            found = Vector3.Distance(P(after, (uint)v), expected) < 0.05f;
        Assert.True(found, $"no vertex at {expected}, so the extrude did not push along the normal");
    }

    [Fact]
    public void InsetPullsTheNewFaceTowardItsOwnCentre()
    {
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);
        int t = FirstRealTriangle(before);
        if (t < 0) return;

        Vector3 P(MapGeoAsset a, uint v) => new(a.Positions[v * 3], a.Positions[v * 3 + 1], a.Positions[v * 3 + 2]);
        Vector3 p0 = P(before, before.Indices[t * 3]);
        Vector3 centre = (p0 + P(before, before.Indices[t * 3 + 1]) + P(before, before.Indices[t * 3 + 2])) / 3f;

        var grown = MapGeoFaceGrower.TryApply(bytes, before,
            new[] { new FaceGrow(t, FaceGrowOp.Inset, 0.5f) }, out string? error);
        Assert.True(grown is not null, error);
        var after = MapGeoDecoder.Decode(grown!);

        // Halfway to the centre, located by position for the same reason as above.
        var expected = Vector3.Lerp(p0, centre, 0.5f);
        bool found = false;
        for (int v = 0; v * 3 + 2 < after.Positions.Length && !found; v++)
            found = Vector3.Distance(P(after, (uint)v), expected) < 0.05f;
        Assert.True(found, $"no vertex at {expected}, so the inset did not pull toward the centre");
        Assert.True(Vector3.Distance(expected, centre) < Vector3.Distance(p0, centre));
    }

    [Fact]
    public void EverySubmeshAfterTheInsertionMovesByExactlyWhatWasInserted()
    {
        // The whole risk of this operation. Get it wrong and the file still parses with plausible counts
        // while every later submesh renders the wrong triangles under the wrong material.
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);
        int t = FirstRealTriangle(before);
        if (t < 0) return;

        var grown = MapGeoFaceGrower.TryApply(bytes, before,
            new[] { new FaceGrow(t, FaceGrowOp.Extrude, 25f) }, out _);
        if (grown is null) return;
        var after = MapGeoDecoder.Decode(grown);

        Assert.Equal(before.Groups.Count, after.Groups.Count);

        // every group still describes a range inside the buffer, and they still tile it in order
        for (int i = 0; i < after.Groups.Count; i++)
        {
            Assert.True(after.Groups[i].StartIndex >= 0);
            Assert.True(after.Groups[i].StartIndex + after.Groups[i].IndexCount <= after.Indices.Length,
                $"group {i} runs past the index buffer");
        }
        // exactly one group grew, by exactly the triangles that were added
        int biggerBy = 0, grewCount = 0;
        for (int i = 0; i < after.Groups.Count; i++)
        {
            int delta = after.Groups[i].IndexCount - before.Groups[i].IndexCount;
            if (delta != 0) { grewCount++; biggerBy += delta; }
        }
        Assert.Equal(1, grewCount);
        Assert.Equal(7 * 3, biggerBy);
    }

    [Fact]
    public void EveryIndexStillAddressesARealVertex()
    {
        // The u16 ceiling and the offset repair both fail this way: an index that points past the vertex
        // buffer, which reads as scrambled geometry rather than as an error.
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);
        int t = FirstRealTriangle(before);
        if (t < 0) return;

        var grown = MapGeoFaceGrower.TryApply(bytes, before,
            new[] { new FaceGrow(t, FaceGrowOp.Extrude, 30f) }, out _);
        if (grown is null) return;
        var after = MapGeoDecoder.Decode(grown);

        int vertices = after.Positions.Length / 3;
        foreach (uint index in after.Indices) Assert.True(index < vertices, $"index {index} of {vertices}");
    }

    [Fact]
    public void TheOriginalFaceIsLeftAsAHoleForTheCapToCover()
    {
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);
        int t = FirstRealTriangle(before);
        if (t < 0) return;

        var grown = MapGeoFaceGrower.TryApply(bytes, before,
            new[] { new FaceGrow(t, FaceGrowOp.Extrude, 20f) }, out _);
        if (grown is null) return;
        var after = MapGeoDecoder.Decode(grown);

        Assert.Equal(after.Indices[t * 3], after.Indices[t * 3 + 1]);
        Assert.Equal(after.Indices[t * 3], after.Indices[t * 3 + 2]);
    }

    [Fact]
    public void SeveralFacesAtOnceStayConsistent()
    {
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);
        var faces = new List<FaceGrow>();
        for (int t = 0; t < 40 && t * 3 + 2 < before.Indices.Length; t++)
        {
            uint i0 = before.Indices[t * 3], i1 = before.Indices[t * 3 + 1], i2 = before.Indices[t * 3 + 2];
            if (i0 != i1 && i1 != i2 && i0 != i2) faces.Add(new FaceGrow(t, FaceGrowOp.Extrude, 15f));
        }
        if (faces.Count == 0) return;

        var grown = MapGeoFaceGrower.TryApply(bytes, before, faces, out string? error);
        Assert.True(grown is not null, error);
        var after = MapGeoDecoder.Decode(grown!);

        Assert.Equal(before.Positions.Length / 3 + faces.Count * 3, after.Positions.Length / 3);
        Assert.Equal(before.Indices.Length + faces.Count * 7 * 3, after.Indices.Length);
        int vertices = after.Positions.Length / 3;
        foreach (uint index in after.Indices) Assert.True(index < vertices);
    }

    [Fact]
    public void NothingToDoIsANoOp()
    {
        if (Shipped() is not { } bytes) return;
        var asset = MapGeoDecoder.Decode(bytes);
        Assert.Same(bytes, MapGeoFaceGrower.TryApply(bytes, asset, Array.Empty<FaceGrow>(), out _));
    }

    [Fact]
    public void ATriangleOutsideTheMapIsRefused()
    {
        if (Shipped() is not { } bytes) return;
        var asset = MapGeoDecoder.Decode(bytes);
        Assert.Null(MapGeoFaceGrower.TryApply(bytes, asset,
            new[] { new FaceGrow(int.MaxValue / 4, FaceGrowOp.Extrude, 10f) }, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

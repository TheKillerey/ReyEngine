using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M566: editing individual faces of a map.
///
/// <para>Every operation preserves the file's length, and that is a requirement rather than a nicety: a
/// mapgeo has no offset table, so submesh ranges are raw offsets into shared index buffers. Removing
/// three indices would shift every later submesh in the same buffer. Delete therefore degenerates the
/// triangle instead.</para>
/// </summary>
public sealed class MapGeoFaceWriterTests
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

    /// <summary>A triangle with three distinct corners, so degenerating it is observable.</summary>
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
    public void DeletingAFaceCollapsesItAndKeepsTheFileTheSameLength()
    {
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);
        int t = FirstRealTriangle(before);
        if (t < 0) return;

        var edited = MapGeoFaceWriter.TryApply(bytes, before,
            new[] { new FaceEdit(t, FaceOp.Delete) }, out string? error);
        Assert.True(edited is not null, error);

        var after = MapGeoDecoder.Decode(edited!);
        Assert.Equal(before.Indices.Length, after.Indices.Length);
        Assert.Equal(before.Positions.Length, after.Positions.Length);
        Assert.Equal(before.Groups.Count, after.Groups.Count);

        // zero area: all three corners are now the same vertex
        Assert.Equal(after.Indices[t * 3], after.Indices[t * 3 + 1]);
        Assert.Equal(after.Indices[t * 3], after.Indices[t * 3 + 2]);
    }

    [Fact]
    public void OnlyTheEditedFaceChanges()
    {
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);
        int t = FirstRealTriangle(before);
        if (t < 0) return;

        var edited = MapGeoFaceWriter.TryApply(bytes, before,
            new[] { new FaceEdit(t, FaceOp.Delete) }, out _);
        if (edited is null) return;
        var after = MapGeoDecoder.Decode(edited);

        int differing = 0;
        for (int i = 0; i < before.Indices.Length; i++)
            if (before.Indices[i] != after.Indices[i]) differing++;
        Assert.InRange(differing, 1, 2);   // two of the three corners are rewritten
        Assert.True(before.Positions.SequenceEqual(after.Positions), "delete must not touch positions");
    }

    [Fact]
    public void FlippingAFaceReversesItsWinding()
    {
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);
        int t = FirstRealTriangle(before);
        if (t < 0) return;

        Vector3 P(MapGeoAsset a, uint v) => new(a.Positions[v * 3], a.Positions[v * 3 + 1], a.Positions[v * 3 + 2]);
        var n0 = Vector3.Cross(
            P(before, before.Indices[t * 3 + 1]) - P(before, before.Indices[t * 3]),
            P(before, before.Indices[t * 3 + 2]) - P(before, before.Indices[t * 3]));

        var edited = MapGeoFaceWriter.TryApply(bytes, before,
            new[] { new FaceEdit(t, FaceOp.Flip) }, out string? error);
        Assert.True(edited is not null, error);
        var after = MapGeoDecoder.Decode(edited!);

        var n1 = Vector3.Cross(
            P(after, after.Indices[t * 3 + 1]) - P(after, after.Indices[t * 3]),
            P(after, after.Indices[t * 3 + 2]) - P(after, after.Indices[t * 3]));

        Assert.True(Vector3.Dot(Vector3.Normalize(n0), Vector3.Normalize(n1)) < -0.99f,
            "the flipped face should point the opposite way");
        Assert.Equal(before.Positions.Length, after.Positions.Length);
    }

    [Fact]
    public void MovingAFaceShiftsTheVerticesItUses()
    {
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);
        int t = FirstRealTriangle(before);
        if (t < 0) return;

        var delta = new Vector3(0f, 25f, 0f);
        var edited = MapGeoFaceWriter.TryApply(bytes, before,
            new[] { new FaceEdit(t, FaceOp.Move, delta) }, out string? error);
        Assert.True(edited is not null, error);
        var after = MapGeoDecoder.Decode(edited!);

        foreach (uint v in new[] { before.Indices[t * 3], before.Indices[t * 3 + 1], before.Indices[t * 3 + 2] })
            Assert.Equal(before.Positions[v * 3 + 1] + delta.Y, after.Positions[v * 3 + 1], 2);
        Assert.Equal(before.Indices.Length, after.Indices.Length);
    }

    [Fact]
    public void AVertexSharedByTwoSelectedFacesMovesOnce()
    {
        // Collected per vertex rather than applied per triangle: two faces sharing a corner would
        // otherwise drag it twice as far, which reads as the selection tearing apart as you drag it.
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);

        // find two triangles that share at least one vertex
        int a = -1, b = -1;
        for (int t = 0; t * 3 + 5 < before.Indices.Length && a < 0; t++)
        {
            var first = new[] { before.Indices[t * 3], before.Indices[t * 3 + 1], before.Indices[t * 3 + 2] };
            var second = new[] { before.Indices[t * 3 + 3], before.Indices[t * 3 + 4], before.Indices[t * 3 + 5] };
            if (first.Intersect(second).Any()) { a = t; b = t + 1; }
        }
        if (a < 0) return;

        uint shared = new[] { before.Indices[a * 3], before.Indices[a * 3 + 1], before.Indices[a * 3 + 2] }
            .Intersect(new[] { before.Indices[b * 3], before.Indices[b * 3 + 1], before.Indices[b * 3 + 2] })
            .First();

        var delta = new Vector3(0f, 40f, 0f);
        var edited = MapGeoFaceWriter.TryApply(bytes, before,
            new[] { new FaceEdit(a, FaceOp.Move, delta), new FaceEdit(b, FaceOp.Move, delta) }, out _);
        if (edited is null) return;
        var after = MapGeoDecoder.Decode(edited);

        Assert.Equal(before.Positions[shared * 3 + 1] + delta.Y, after.Positions[shared * 3 + 1], 2);
    }

    [Fact]
    public void ManyFacesAtOnceStillPreserveTheLayout()
    {
        if (Shipped() is not { } bytes) return;
        var before = MapGeoDecoder.Decode(bytes);
        var edits = Enumerable.Range(0, 200)
            .Where(t => t * 3 + 2 < before.Indices.Length)
            .Select(t => new FaceEdit(t, FaceOp.Delete)).ToList();
        if (edits.Count == 0) return;

        var edited = MapGeoFaceWriter.TryApply(bytes, before, edits, out string? error);
        Assert.True(edited is not null, error);
        var after = MapGeoDecoder.Decode(edited!);

        Assert.Equal(before.Indices.Length, after.Indices.Length);
        Assert.Equal(before.Groups.Count, after.Groups.Count);
        for (int i = 0; i < before.Groups.Count; i++)
        {
            Assert.Equal(before.Groups[i].StartIndex, after.Groups[i].StartIndex);
            Assert.Equal(before.Groups[i].IndexCount, after.Groups[i].IndexCount);
        }
    }

    [Fact]
    public void ATriangleOutsideTheMapIsRefusedRatherThanWritten()
    {
        if (Shipped() is not { } bytes) return;
        var asset = MapGeoDecoder.Decode(bytes);
        Assert.Null(MapGeoFaceWriter.TryApply(bytes, asset,
            new[] { new FaceEdit(int.MaxValue / 4, FaceOp.Delete) }, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void NoEditsIsANoOp()
    {
        if (Shipped() is not { } bytes) return;
        var asset = MapGeoDecoder.Decode(bytes);
        var same = MapGeoFaceWriter.TryApply(bytes, asset, Array.Empty<FaceEdit>(), out _);
        Assert.Same(bytes, same);
    }
}

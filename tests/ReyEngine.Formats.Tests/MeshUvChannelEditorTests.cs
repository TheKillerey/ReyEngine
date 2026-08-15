using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M492: editing the SECOND UV set (Texcoord7) in place.
///
/// <para>The property that matters most here is what the edit does NOT do: the vertex declaration, the
/// stride and the buffer length must come out identical, because it only overwrites floats already present.
/// That is the difference between this and adding a channel, and it is what keeps it clear of the failure
/// classes of M476 (element order IS the byte layout) and M416 (wire form).</para>
/// </summary>
public sealed class MeshUvChannelEditorTests
{
    /// <summary>One mesh, Position + Texcoord0 + Texcoord7 inline, in Riot's element order. Positions are
    /// laid out so world XZ spans a known rect, which is what the planar projection is checked against.</summary>
    private static MapGeoBinary OneMeshMap(Matrix4x4? transform = null)
    {
        var decl = new MapGeoBinary.VertexDeclaration();
        decl.Elements.Add((MapGeoBinary.ElemPosition, MapGeoBinary.FmtXYZ_Float32));
        decl.Elements.Add((MapGeoBinary.ElemTexcoord0, MapGeoBinary.FmtXY_Float32));
        decl.Elements.Add((MapGeoBinary.ElemTexcoord7, MapGeoBinary.FmtXY_Float32));

        // 4 vertices over the XZ square (0,0)..(100,200); Texcoord0 is a plain 0..1 quad; Texcoord7 starts
        // as recognisable junk so a failure to write is visible rather than accidentally correct.
        var positions = new[]
        {
            new Vector3(0, 5, 0), new Vector3(100, 5, 0),
            new Vector3(0, 5, 200), new Vector3(100, 5, 200),
        };
        var uv0 = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };

        int stride = decl.Stride;
        var data = new byte[stride * positions.Length];
        for (int v = 0; v < positions.Length; v++)
        {
            int at = v * stride;
            BitConverter.TryWriteBytes(data.AsSpan(at, 4), positions[v].X);
            BitConverter.TryWriteBytes(data.AsSpan(at + 4, 4), positions[v].Y);
            BitConverter.TryWriteBytes(data.AsSpan(at + 8, 4), positions[v].Z);
            BitConverter.TryWriteBytes(data.AsSpan(at + 12, 4), uv0[v].X);
            BitConverter.TryWriteBytes(data.AsSpan(at + 16, 4), uv0[v].Y);
            BitConverter.TryWriteBytes(data.AsSpan(at + 20, 4), -7f);
            BitConverter.TryWriteBytes(data.AsSpan(at + 24, 4), -7f);
        }

        var map = new MapGeoBinary { Version = 18 };
        map.Declarations.Add(decl);
        map.VertexBuffers.Add(new MapGeoBinary.VertexBuffer { Data = data });
        map.IndexBuffers.Add(new MapGeoBinary.IndexBuffer { Data = new byte[6] });
        map.Meshes.Add(new MapGeoBinary.Mesh
        {
            VertexCount = positions.Length,
            VertexDeclarationBase = 0,
            VertexBufferIds = { 0 },
            IndexCount = 6,
            IndexBufferId = 0,
            BoundsMin = new Vector3(0, 5, 0),
            BoundsMax = new Vector3(100, 5, 200),
            Transform = transform ?? Matrix4x4.Identity,
        });
        return map;
    }

    [Fact]
    public void WorldPlanarProjectsXzOntoTheCanvasRectAndLeavesTheLayoutUntouched()
    {
        var map = OneMeshMap();
        var declBefore = map.Declarations[0].Elements.ToList();
        int strideBefore = map.Declarations[0].Stride;
        int lengthBefore = map.VertexBuffers[0].Data.Length;
        int declCountBefore = map.Declarations.Count;
        int bufferCountBefore = map.VertexBuffers.Count;

        bool changed = MeshUvChannelEditor.SetTexcoord7(map, Array.Empty<int>(), UvEditMode.WorldPlanarXz,
            Vector2.One, Vector2.Zero, out var result);

        Assert.True(changed);
        Assert.Equal(1, result.MeshesChanged);
        Assert.Equal(4, result.VerticesWritten);
        Assert.Empty(result.Skipped);

        // The projection: world XZ normalised over the map bounds (0,0)..(100,200).
        Assert.True(MeshUvChannelEditor.TryReadUvSet(map, map.Meshes[0], MapGeoBinary.ElemTexcoord7, out var uv7));
        Assert.Equal(new Vector2(0, 0), uv7[0]);
        Assert.Equal(new Vector2(1, 0), uv7[1]);
        Assert.Equal(new Vector2(0, 1), uv7[2]);
        Assert.Equal(new Vector2(1, 1), uv7[3]);

        // THE point of an in-place edit: nothing about the layout moved.
        Assert.Equal(declCountBefore, map.Declarations.Count);
        Assert.Equal(bufferCountBefore, map.VertexBuffers.Count);
        Assert.Equal(declBefore, map.Declarations[0].Elements);
        Assert.Equal(strideBefore, map.Declarations[0].Stride);
        Assert.Equal(lengthBefore, map.VertexBuffers[0].Data.Length);

        // ...and the neighbouring channels are intact, which a wrong stride or offset would corrupt.
        Assert.True(MeshUvChannelEditor.TryReadUvSet(map, map.Meshes[0], MapGeoBinary.ElemTexcoord0, out var uv0));
        Assert.Equal(new Vector2(1, 1), uv0[3]);
        Assert.True(MeshUvChannelEditor.TryReadWorldPositions(map, map.Meshes[0], out var world));
        Assert.Equal(new Vector3(100, 5, 200), world[3]);
    }

    [Fact]
    public void PlanarProjectionFollowsTheMeshTransformIntoWorldSpace()
    {
        // The canvas is a WORLD projection, so a mesh moved by its transform must land elsewhere on it.
        // Reading local positions instead would put both meshes at the same UVs — the bug this pins down.
        var map = OneMeshMap(Matrix4x4.CreateTranslation(new Vector3(50, 0, 100)));
        var rect = (Min: new Vector2(0, 0), Max: new Vector2(100, 200));

        MeshUvChannelEditor.SetTexcoord7(map, Array.Empty<int>(), UvEditMode.WorldPlanarXz,
            Vector2.One, Vector2.Zero, out _, rect);

        Assert.True(MeshUvChannelEditor.TryReadUvSet(map, map.Meshes[0], MapGeoBinary.ElemTexcoord7, out var uv7));
        Assert.Equal(new Vector2(0.5f, 0.5f), uv7[0]);
        Assert.Equal(new Vector2(1.5f, 1.5f), uv7[3]);
    }

    [Fact]
    public void ScaleAndOffsetApplyToTheExistingChannelInThatOrder()
    {
        var map = OneMeshMap();
        MeshUvChannelEditor.SetTexcoord7(map, Array.Empty<int>(), UvEditMode.CopyTexcoord0,
            Vector2.One, Vector2.Zero, out _);

        MeshUvChannelEditor.SetTexcoord7(map, Array.Empty<int>(), UvEditMode.ScaleExisting,
            new Vector2(2, 4), new Vector2(0.5f, -1f), out var result);

        Assert.True(MeshUvChannelEditor.TryReadUvSet(map, map.Meshes[0], MapGeoBinary.ElemTexcoord7, out var uv7));
        Assert.Equal(new Vector2(0.5f, -1f), uv7[0]);           // (0,0) * (2,4) + (0.5,-1)
        Assert.Equal(new Vector2(2.5f, 3f), uv7[3]);            // (1,1) * (2,4) + (0.5,-1)
        Assert.Equal(new Vector2(0.5f, -1f), result.UvMin);
        Assert.Equal(new Vector2(2.5f, 3f), result.UvMax);
    }

    [Fact]
    public void MeshWithoutTexcoord7IsReportedRatherThanGivenOne()
    {
        // Adding the channel is a different operation with different risks (declaration, stride, buffer
        // length all change). This one must refuse and say why, not quietly do the bigger thing.
        var map = OneMeshMap();
        var decl = map.Declarations[0];
        decl.Elements.RemoveAll(e => e.Name == MapGeoBinary.ElemTexcoord7);

        bool changed = MeshUvChannelEditor.SetTexcoord7(map, Array.Empty<int>(), UvEditMode.WorldPlanarXz,
            Vector2.One, Vector2.Zero, out var result);

        Assert.False(changed);
        Assert.Equal(0, result.MeshesChanged);
        Assert.Contains(result.Skipped, s => s.Contains("no Texcoord7", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(map.Declarations[0].Elements, e => e.Name == MapGeoBinary.ElemTexcoord7);
    }

    [Fact]
    public void DegenerateCanvasWritesZeroRatherThanNaN()
    {
        // A flat map (or a single-plane selection) gives a zero-width rect. Dividing by it would write NaN,
        // which survives every check our own reader makes and shows up only as geometry the game drops.
        var map = OneMeshMap();
        var flat = (Min: new Vector2(10, 10), Max: new Vector2(10, 10));

        MeshUvChannelEditor.SetTexcoord7(map, Array.Empty<int>(), UvEditMode.WorldPlanarXz,
            Vector2.One, Vector2.Zero, out _, flat);

        Assert.True(MeshUvChannelEditor.TryReadUvSet(map, map.Meshes[0], MapGeoBinary.ElemTexcoord7, out var uv7));
        Assert.All(uv7, v => Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y)));
    }

    [Fact]
    public void OnlySelectedMeshesAreRewritten()
    {
        var map = OneMeshMap();
        // A second mesh sharing the declaration but its own buffer, so an edit to one is visible on the other.
        var clone = map.VertexBuffers[0].Data.ToArray();
        map.VertexBuffers.Add(new MapGeoBinary.VertexBuffer { Data = clone });
        map.Declarations.Add(map.Declarations[0]);
        map.Meshes.Add(new MapGeoBinary.Mesh
        {
            VertexCount = 4, VertexDeclarationBase = 1, VertexBufferIds = { 1 },
            IndexCount = 6, IndexBufferId = 0,
            BoundsMin = new Vector3(0, 5, 0), BoundsMax = new Vector3(100, 5, 200),
            Transform = Matrix4x4.Identity,
        });

        MeshUvChannelEditor.SetTexcoord7(map, new[] { 1 }, UvEditMode.CopyTexcoord0,
            Vector2.One, Vector2.Zero, out var result);

        Assert.Equal(1, result.MeshesChanged);
        Assert.True(MeshUvChannelEditor.TryReadUvSet(map, map.Meshes[0], MapGeoBinary.ElemTexcoord7, out var untouched));
        Assert.Equal(new Vector2(-7, -7), untouched[0]);        // the sentinel the fixture wrote
        Assert.True(MeshUvChannelEditor.TryReadUvSet(map, map.Meshes[1], MapGeoBinary.ElemTexcoord7, out var edited));
        Assert.Equal(new Vector2(0, 0), edited[0]);
    }
}

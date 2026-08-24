using System.Numerics;
using System.Text;
using ReyEngine.Formats.Interop;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M580: the Blender link.
///
/// <para>Two things here can go wrong quietly. A wire format that decodes to plausible-but-wrong numbers
/// looks like corrupt geometry rather than like a bug, and a transform that does not round trip shows up
/// as a mesh drifting a little further every time the user presses Push. Both are pinned.</para>
/// </summary>
public sealed class BlenderBridgeTests
{
    /// <summary>
    /// A two-mesh map. The second mesh starts at vertex 3, which is the case that catches index rebasing:
    /// the decoder concatenates every mesh's vertices, so raw indices are absolute and Blender needs them
    /// relative to each object.
    /// </summary>
    private static MapGeoAsset TwoMeshMap()
    {
        var positions = new float[]
        {
            0, 0, 0,   10, 0, 0,   0, 0, 10,          // mesh A around (3.33, 0, 3.33)
            100, 5, 100,  110, 5, 100,  100, 5, 110,  // mesh B, far away
        };
        var normals = new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0 };
        var indices = new uint[] { 0, 1, 2, 3, 4, 5 };

        MapGeoMesh Mesh(int index, string name, int start) => new()
        {
            Index = index, Name = name, VertexStart = start, VertexCount = 3,
            Transform = Matrix4x4.Identity,
            Pivot = Centre(positions, start),
            IndexCount = 3,
        };

        return new MapGeoAsset
        {
            Positions = positions,
            Normals = normals,
            Uvs = new float[12],
            Indices = indices,
            Groups = new[]
            {
                new MapGeoGroup("mat/a", 0, 3, "a", MeshIndex: 0),
                new MapGeoGroup("mat/b", 3, 3, "b", MeshIndex: 1),
            },
            Meshes = new[] { Mesh(0, "meshA", 0), Mesh(1, "meshB", 3) },
        };
    }

    private static Vector3 Centre(float[] positions, int start)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = start * 3; i < start * 3 + 9; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min + max) * 0.5f;
    }

    // ---- wire format -----------------------------------------------------------------------------

    [Fact]
    public void AMeshSurvivesTheWireExactly()
    {
        var sent = new BridgeMesh(7, "sru_wall", new Vector3(1, 2, 3),
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
            new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0 },
            new uint[] { 0, 1, 2 },
            new Vector3(10, 20, 30), new Vector3(0, 90, 0), new Vector3(2, 2, 2));

        var back = Assert.Single(BlenderBridgeProtocol.DecodeMeshes(
            BlenderBridgeProtocol.EncodeMeshes(new[] { sent })));

        Assert.Equal(sent.Index, back.Index);
        Assert.Equal(sent.Name, back.Name);
        Assert.Equal(sent.Pivot, back.Pivot);
        Assert.Equal(sent.Positions, back.Positions);
        Assert.Equal(sent.Normals, back.Normals);
        Assert.Equal(sent.Indices, back.Indices);
        Assert.Equal(sent.Location, back.Location);
        Assert.Equal(sent.RotationDegrees, back.RotationDegrees);
        Assert.Equal(sent.Scale, back.Scale);
    }

    [Fact]
    public void AMeshWithNoNormalsStaysAMeshWithNoNormals()
    {
        var sent = new BridgeMesh(1, "flat", Vector3.Zero,
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, null, new uint[] { 0, 1, 2 },
            Vector3.Zero, Vector3.Zero, Vector3.One);
        Assert.Null(Assert.Single(BlenderBridgeProtocol.DecodeMeshes(
            BlenderBridgeProtocol.EncodeMeshes(new[] { sent }))).Normals);
    }

    [Fact]
    public void ATruncatedPayloadIsRefusedRatherThanHalfRead()
    {
        // Half a map decoded without complaint reads as "the user deleted geometry".
        var payload = BlenderBridgeProtocol.EncodeMeshes(new[]
        {
            new BridgeMesh(1, "m", Vector3.Zero, new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, null,
                new uint[] { 0, 1, 2 }, Vector3.Zero, Vector3.Zero, Vector3.One),
        });
        Assert.ThrowsAny<Exception>(() => BlenderBridgeProtocol.DecodeMeshes(payload[..(payload.Length / 2)]));
    }

    [Fact]
    public void AnIndexPastTheEndOfTheVertexBufferIsRefused()
    {
        // Blender would take it and render scrambled geometry; better to say so at the boundary.
        var good = new BridgeMesh(1, "m", Vector3.Zero, new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, null,
            new uint[] { 0, 1, 9 }, Vector3.Zero, Vector3.Zero, Vector3.One);
        Assert.ThrowsAny<Exception>(() =>
            BlenderBridgeProtocol.DecodeMeshes(BlenderBridgeProtocol.EncodeMeshes(new[] { good })));
    }

    [Fact]
    public void AHeaderAndItsBodyComeBackSeparately()
    {
        // The framing's whole job: the reader must stop at the newline and leave the binary alone.
        using var stream = new MemoryStream();
        var body = new byte[] { 1, 2, 3, 4, 5 };
        BlenderBridgeProtocol.WriteMessage(stream, "{\"op\":\"meshes\",\"bytes\":5}", body);
        stream.Position = 0;

        Assert.Equal("{\"op\":\"meshes\",\"bytes\":5}", BlenderBridgeProtocol.ReadHeader(stream));
        Assert.Equal(body, BlenderBridgeProtocol.ReadExactly(stream, 5));
        Assert.Null(BlenderBridgeProtocol.ReadHeader(stream));
    }

    [Fact]
    public void AShortBodyThrowsInsteadOfReturningPadding()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2 });
        Assert.Throws<EndOfStreamException>(() => BlenderBridgeProtocol.ReadExactly(stream, 5));
    }

    // ---- snapshot --------------------------------------------------------------------------------

    [Fact]
    public void EachMeshIndexesItsOwnVertices()
    {
        var snapshot = MapGeoBlenderBridge.Snapshot(TwoMeshMap());
        Assert.Equal(2, snapshot.Meshes.Count);
        foreach (var mesh in snapshot.Meshes)
        {
            Assert.Equal(3, mesh.VertexCount);
            Assert.Equal(1, mesh.TriangleCount);
            Assert.All(mesh.Indices, i => Assert.True(i < mesh.VertexCount, $"index {i} of {mesh.VertexCount}"));
        }
        // and the second mesh got ITS triangle, not the first one's
        Assert.Equal(new uint[] { 0, 1, 2 }, snapshot.Meshes[1].Indices);
    }

    [Fact]
    public void GeometryIsSentRelativeToThePivotThatBlenderWillRotateAbout()
    {
        var map = TwoMeshMap();
        var mesh = map.Meshes[1];
        var sent = MapGeoBlenderBridge.Snapshot(map).Meshes.Single(m => m.Index == 1);

        // pivot-relative + pivot == the world position the editor holds
        for (int i = 0; i < sent.Positions.Length; i += 3)
        {
            var world = new Vector3(sent.Positions[i], sent.Positions[i + 1], sent.Positions[i + 2]) + sent.Pivot;
            var actual = new Vector3(map.Positions[mesh.VertexStart * 3 + i],
                                     map.Positions[mesh.VertexStart * 3 + i + 1],
                                     map.Positions[mesh.VertexStart * 3 + i + 2]);
            Assert.True(Vector3.Distance(world, actual) < 1e-4f, $"{world} vs {actual}");
        }
        Assert.Equal(mesh.Pivot, sent.Location);   // untouched mesh sits on its pivot
    }

    [Fact]
    public void AMeshCarryingABatchTransformIsReportedRatherThanSentWrong()
    {
        // A group matrix applies AFTER the mesh's own transform and the bridge has nowhere to put it, so
        // sending the mesh anyway would mean the first push snapped it somewhere else.
        var map = TwoMeshMap();
        map.Meshes[0].GroupMatrix = Matrix4x4.CreateTranslation(5, 0, 0);

        var snapshot = MapGeoBlenderBridge.Snapshot(map);
        Assert.Single(snapshot.Meshes);
        Assert.Equal(1, snapshot.Meshes[0].Index);
        Assert.Contains(snapshot.Notes, n => n.Contains("batch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnlyTheFilteredMeshesAreSent()
    {
        var snapshot = MapGeoBlenderBridge.Snapshot(TwoMeshMap(), m => m.Index == 1);
        Assert.Equal(1, Assert.Single(snapshot.Meshes).Index);
    }

    // ---- push ------------------------------------------------------------------------------------

    [Fact]
    public void PushingWhatWasPulledChangesNothing()
    {
        // The property that keeps a mesh from drifting: pull, push, push again, and it has not moved.
        var map = TwoMeshMap();
        var before = (float[])map.Positions.Clone();
        var sent = MapGeoBlenderBridge.Snapshot(map).Meshes;

        var same = sent.Select(m => new BridgeTransform(m.Index, m.Location, m.RotationDegrees, m.Scale)).ToList();
        Assert.Empty(MapGeoBlenderBridge.ApplyTransforms(map, same, out var notes));
        Assert.Empty(notes);
        Assert.Equal(before, map.Positions);
    }

    [Fact]
    public void MovingAnObjectInBlenderMovesTheMesh()
    {
        var map = TwoMeshMap();
        var mesh = map.Meshes[0];
        var moved = new BridgeTransform(0, mesh.Pivot + new Vector3(0, 50, 0), Vector3.Zero, Vector3.One);

        Assert.Equal(new[] { 0 }, MapGeoBlenderBridge.ApplyTransforms(map, new[] { moved }, out _));
        Assert.Equal(new Vector3(0, 50, 0), mesh.Offset);
        Assert.Equal(50f, map.Positions[1], 3);      // first vertex lifted by 50
        Assert.Equal(5f, map.Positions[10], 3);      // the OTHER mesh is untouched
    }

    [Fact]
    public void PushingTheSameMoveTwiceDoesNotMoveItTwice()
    {
        // Absolute, not incremental. An incremental protocol would double every repeated push, which is
        // exactly what an auto-push on every Blender edit would do.
        var map = TwoMeshMap();
        var moved = new BridgeTransform(0, map.Meshes[0].Pivot + new Vector3(0, 50, 0), Vector3.Zero, Vector3.One);

        MapGeoBlenderBridge.ApplyTransforms(map, new[] { moved }, out _);
        float after = map.Positions[1];
        Assert.Empty(MapGeoBlenderBridge.ApplyTransforms(map, new[] { moved }, out _));
        Assert.Equal(after, map.Positions[1], 3);
    }

    [Fact]
    public void ATransformThatWouldDestroyTheMeshIsRefusedWithAReason()
    {
        var map = TwoMeshMap();
        var before = (float[])map.Positions.Clone();
        var bad = new[]
        {
            new BridgeTransform(0, Vector3.Zero, Vector3.Zero, new Vector3(1, 0, 1)),               // collapses it
            new BridgeTransform(1, new Vector3(float.NaN, 0, 0), Vector3.Zero, Vector3.One),        // not a place
            new BridgeTransform(99, Vector3.Zero, Vector3.Zero, Vector3.One),                       // not this map
        };

        Assert.Empty(MapGeoBlenderBridge.ApplyTransforms(map, bad, out var notes));
        Assert.Equal(3, notes.Count);
        Assert.Equal(before, map.Positions);
    }

    [Fact]
    public void RotationGoesThroughTheSamePivotTheEditorUses()
    {
        // Blender rotates about the object origin and ReyEngine about the mesh pivot; the bridge only
        // works because those are put in the same place. A 180 degree turn about Y should mirror the
        // mesh through its own pivot and leave the pivot itself where it was.
        var map = TwoMeshMap();
        var mesh = map.Meshes[1];
        var pivot = mesh.Pivot;
        var corner = new Vector3(map.Positions[9], map.Positions[10], map.Positions[11]);

        MapGeoBlenderBridge.ApplyTransforms(map,
            new[] { new BridgeTransform(1, pivot, new Vector3(0, 180, 0), Vector3.One) }, out _);

        var turned = new Vector3(map.Positions[9], map.Positions[10], map.Positions[11]);
        var expected = pivot + new Vector3(-(corner.X - pivot.X), corner.Y - pivot.Y, -(corner.Z - pivot.Z));
        Assert.True(Vector3.Distance(turned, expected) < 1e-3f, $"{turned} vs {expected}");
    }

    // ---- the add-on ------------------------------------------------------------------------------

    [Fact]
    public void TheAddOnAgreesWithTheProtocolVersion()
    {
        // A version skew decodes into plausible nonsense rather than failing, so the two halves are
        // checked against each other here as well as at connect time.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return;
        string addon = Path.Combine(dir.FullName, "tools", "blender", "reyengine_bridge.py");
        if (!File.Exists(addon)) return;

        string source = File.ReadAllText(addon);
        Assert.Contains($"PROTOCOL = {BlenderBridgeProtocol.Version}", source, StringComparison.Ordinal);
        Assert.Contains($"default={BlenderBridgeProtocol.DefaultPort}", source, StringComparison.Ordinal);
        // and it reads the fields in the order the encoder writes them
        int pivot = source.IndexOf("pivot = floats(3)", StringComparison.Ordinal);
        int location = source.IndexOf("location = floats(3)", StringComparison.Ordinal);
        int positions = source.IndexOf("positions = floats(u32())", StringComparison.Ordinal);
        int indices = source.IndexOf("index_count = u32()", StringComparison.Ordinal);
        Assert.True(pivot > 0 && pivot < location && location < positions && positions < indices,
            "the add-on decodes the mesh block in a different order than the encoder writes it");
    }
}

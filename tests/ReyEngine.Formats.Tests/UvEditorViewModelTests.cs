using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M492: the UV editor's view model.
///
/// <para>These live here deliberately. ReyEngine.App has no test project of its own, so view-model logic is
/// exactly the layer a green suite cannot see — M464 proved that the hard way. The wiring tested here (which
/// meshes get listed, which UV array is read, what Apply sends to the save callback) is where this feature
/// can silently do the wrong thing while every Formats test still passes.</para>
/// </summary>
public sealed class UvEditorViewModelTests
{
    /// <summary>A two-mesh map that survives a write/decode round trip: mesh 0 has Texcoord7, mesh 1 does
    /// not, so the "has the channel" split is real rather than mocked.</summary>
    private static byte[] BuildMapGeo()
    {
        var withUv7 = new MapGeoBinary.VertexDeclaration
        {
            Elements = { (MapGeoBinary.ElemPosition, MapGeoBinary.FmtXYZ_Float32),
                         (MapGeoBinary.ElemTexcoord0, MapGeoBinary.FmtXY_Float32),
                         (MapGeoBinary.ElemTexcoord7, MapGeoBinary.FmtXY_Float32) },
            Padding = new byte[8 * 12],
        };
        var noUv7 = new MapGeoBinary.VertexDeclaration
        {
            Elements = { (MapGeoBinary.ElemPosition, MapGeoBinary.FmtXYZ_Float32),
                         (MapGeoBinary.ElemTexcoord0, MapGeoBinary.FmtXY_Float32) },
            Padding = new byte[8 * 13],
        };

        var map = new MapGeoBinary { Version = 18 };
        map.Declarations.Add(withUv7);
        map.Declarations.Add(noUv7);

        // mesh 0: three vertices spread over world XZ, uv0 a 0..1 triangle, uv7 deliberately OUTSIDE the
        // unit square so the "% outside" readout has something real to report.
        var a = new byte[3 * withUv7.Stride];
        var pos = new[] { new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(0, 0, 100) };
        var uv0 = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1) };
        var uv7 = new[] { new Vector2(-1, -1), new Vector2(3, 0), new Vector2(0, 3) };
        for (int v = 0; v < 3; v++)
        {
            int at = v * withUv7.Stride;
            BitConverter.TryWriteBytes(a.AsSpan(at), pos[v].X);
            BitConverter.TryWriteBytes(a.AsSpan(at + 4), pos[v].Y);
            BitConverter.TryWriteBytes(a.AsSpan(at + 8), pos[v].Z);
            BitConverter.TryWriteBytes(a.AsSpan(at + 12), uv0[v].X);
            BitConverter.TryWriteBytes(a.AsSpan(at + 16), uv0[v].Y);
            BitConverter.TryWriteBytes(a.AsSpan(at + 20), uv7[v].X);
            BitConverter.TryWriteBytes(a.AsSpan(at + 24), uv7[v].Y);
        }
        map.VertexBuffers.Add(new MapGeoBinary.VertexBuffer { Data = a, HasVisibility = true, Visibility = 0xFF });
        map.VertexBuffers.Add(new MapGeoBinary.VertexBuffer
        { Data = new byte[3 * noUv7.Stride], HasVisibility = true, Visibility = 0xFF });

        var indices = new byte[6];
        for (int i = 0; i < 3; i++) BitConverter.TryWriteBytes(indices.AsSpan(i * 2), (ushort)i);
        map.IndexBuffers.Add(new MapGeoBinary.IndexBuffer { Data = indices, HasVisibility = true, Visibility = 0xFF });

        map.Meshes.Add(Mesh(0, 0, "Maps/Test/HasUv7"));
        map.Meshes.Add(Mesh(1, 1, "Maps/Test/NoUv7"));
        map.Tail = new byte[8];
        return map.Write();

        static MapGeoBinary.Mesh Mesh(int declBase, int bufferId, string material)
        {
            var m = new MapGeoBinary.Mesh
            {
                VertexCount = 3, VertexDeclarationBase = declBase, VertexBufferIds = { bufferId },
                IndexCount = 3, IndexBufferId = 0, Transform = Matrix4x4.Identity,
                BoundsMin = new Vector3(0, 0, 0), BoundsMax = new Vector3(100, 0, 100),
                HasVisibility = true, Visibility = 0xFF,
                HasRegionHash = true, HasVcHash = true, HasDisableBackface = true,
                HasLayerTransition = true, RenderFlagsIsUshort = true,
            };
            m.Submeshes.Add(new MapGeoBinary.Submesh
            { Material = material, StartIndex = 0, IndexCount = 3, MinVertex = 0, MaxVertex = 2 });
            return m;
        }
    }

    private static UvEditorContext Context(byte[] bytes, params int[] selected) =>
        new(MapGeoDecoder.Decode(bytes), bytes, null, selected, HasPendingEdits: false, MapName: "test.mapgeo");

    [Fact]
    public void ListsMeshesAndFlagsTheOneWithoutTheChannel()
    {
        var bytes = BuildMapGeo();
        var vm = new UvEditorViewModel(() => Context(bytes), (_, _) => Task.CompletedTask);

        Assert.Equal(2, vm.Meshes.Count);
        Assert.True(vm.Meshes[0].HasUv7);
        Assert.False(vm.Meshes[1].HasUv7);
        Assert.Contains("no UV7", vm.Meshes[1].Label, StringComparison.OrdinalIgnoreCase);
        // A mesh that cannot be edited must not be the one preselected.
        Assert.Equal(0, vm.SelectedMesh?.Index);
    }

    [Fact]
    public void ReadsTheRawTexcoord7AndReportsHowMuchLeavesTheUnitSquare()
    {
        // The distinction that matters: RawLightmapUvs is what the vertex buffer holds and what an edit
        // writes; LightmapUvs is the same data through the atlas scale/bias. Showing the transformed one
        // would draw a picture that disagrees with the bytes.
        var bytes = BuildMapGeo();
        var vm = new UvEditorViewModel(() => Context(bytes), (_, _) => Task.CompletedTask);

        Assert.NotNull(vm.Segments);
        Assert.NotEmpty(vm.Segments!);
        Assert.Contains("UV7", vm.ChannelInfo);
        Assert.Contains("u -1", vm.ChannelInfo);          // the fixture's uv7 starts at -1
        Assert.Contains("100% outside", vm.ChannelInfo);  // all three vertices are outside 0..1
    }

    [Fact]
    public void ComparisonChannelFollowsTheToggle()
    {
        var bytes = BuildMapGeo();
        var vm = new UvEditorViewModel(() => Context(bytes), (_, _) => Task.CompletedTask);

        Assert.NotNull(vm.CompareSegments);
        Assert.Contains("UV0", vm.ChannelInfo);

        vm.ShowTexcoord0 = false;
        Assert.Null(vm.CompareSegments);
        Assert.DoesNotContain("UV0", vm.ChannelInfo);
    }

    [Fact]
    public async Task ApplyRewritesOnlyMeshesThatHaveTheChannelAndHandsBytesToTheSaver()
    {
        var bytes = BuildMapGeo();
        byte[]? saved = null;
        UvEditResult? seen = null;
        var vm = new UvEditorViewModel(() => Context(bytes), (b, r) => { saved = b; seen = r; return Task.CompletedTask; });

        vm.ModeIndex = 0;                                  // world planar XZ
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        Assert.NotNull(seen);
        Assert.Equal(1, seen!.MeshesChanged);              // mesh 1 has no Texcoord7 and is not listed as a target
        Assert.Equal(3, seen.VerticesWritten);

        // The projection put the mesh back inside 0..1, which is the whole point of the operation.
        var after = MapGeoDecoder.Decode(saved!);
        var uv7 = after.RawLightmapUvs;
        Assert.NotNull(uv7);
        for (int i = 0; i < 3; i++)
        {
            Assert.InRange(uv7![i * 2], 0f, 1f);
            Assert.InRange(uv7[i * 2 + 1], 0f, 1f);
        }
    }

    [Fact]
    public async Task ApplyRefusesWhileThereArePendingMeshEdits()
    {
        // The rewrite starts from the SAVED bytes, so applying it over unsaved mesh edits would discard
        // them silently. Refusing with a reason is the only honest option.
        var bytes = BuildMapGeo();
        bool savedCalled = false;
        var vm = new UvEditorViewModel(
            () => new UvEditorContext(MapGeoDecoder.Decode(bytes), bytes, null, Array.Empty<int>(),
                HasPendingEdits: true, MapName: "test.mapgeo"),
            (_, _) => { savedCalled = true; return Task.CompletedTask; });

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.False(savedCalled);
        Assert.Contains("pending", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoMapOpenIsStatedRatherThanCrashing()
    {
        var vm = new UvEditorViewModel(() => null, (_, _) => Task.CompletedTask);

        Assert.Empty(vm.Meshes);
        Assert.Null(vm.Segments);
        Assert.Contains("No map", vm.Status, StringComparison.OrdinalIgnoreCase);
    }
}

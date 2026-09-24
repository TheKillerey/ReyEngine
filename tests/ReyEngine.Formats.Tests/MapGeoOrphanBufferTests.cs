using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M768: "Vertex buffer 2213 is not referenced by any mesh" - LTK Manager's loader (ltk_mapgeo) refuses a mapgeo
/// with a vertex buffer no mesh uses, and ReyEngine's mesh delete left exactly those behind on purpose (the user's
/// Classic Rift - Halloween port, after two Add Mesh meshes were deleted). Riot ships none: 0 of 200 mapgeos.
/// </summary>
public sealed class MapGeoOrphanBufferTests
{
    private const string Map453 = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";

    private static readonly Lazy<byte[]?> Jade = new(() =>
    {
        try
        {
            if (!File.Exists(Map453)) return null;
            var db = new HashSyncService().LoadLocal(_ => { });
            using var archive = WadArchive.Open(Map453, new WadPathResolver(db));
            return archive.Extract(HashAlgorithms.WadPath("data/maps/mapgeometry/map453/jade_container.mapgeo"));
        }
        catch { return null; }
    });

    private static NewMapMesh Quad(float x) => new("Quad_Mat",
        new[] { x, 0f, 0f, x + 100f, 0f, 0f, x + 100f, 0f, 100f, x, 0f, 100f },
        null, null, new ushort[] { 0, 1, 2, 0, 2, 3 }, Matrix4x4.Identity);

    private static void AssertEveryBufferIsUsed(MapGeoBinary map)
    {
        Assert.Empty(map.UnreferencedVertexBuffers());
        var usedIb = map.Meshes.Select(m => m.IndexBufferId).ToHashSet();
        Assert.All(Enumerable.Range(0, map.IndexBuffers.Count), i => Assert.Contains(i, usedIb));
    }

    [Fact]
    public void Deleting_an_added_mesh_leaves_no_buffer_behind()
    {
        if (Jade.Value is not { } riot) return;
        Assert.True(MapGeoBinary.TryReadEditable(riot, out var original));
        AssertEveryBufferIsUsed(original);   // Riot's own file is the standard

        var withTwo = MapGeoMeshAppender.Append(riot, new[] { Quad(0f), Quad(500f) }, out var appendError);
        Assert.True(withTwo is not null, appendError);
        Assert.True(MapGeoBinary.TryReadEditable(withTwo!, out var before));
        int first = before.Meshes.Count - 2;

        var removed = MapGeoMeshRemover.Remove(withTwo!, new[] { first }, out var removeError);
        Assert.True(removed is not null, removeError);
        Assert.True(MapGeoBinary.TryReadEditable(removed!, out var after), "the result must re-read exactly");

        // what LTK Manager checks - before M768 this reported the first quad's buffer
        AssertEveryBufferIsUsed(after);
        Assert.Equal(before.Meshes.Count - 1, after.Meshes.Count);

        // every surviving mesh still draws exactly the bytes it drew before
        var survivors = before.Meshes.Where((_, i) => i != first).ToList();
        for (int i = 0; i < survivors.Count; i++)
        {
            var was = survivors[i]; var now = after.Meshes[i];
            Assert.Equal(was.VertexCount, now.VertexCount);
            Assert.Equal(before.IndexBuffers[was.IndexBufferId].Data, after.IndexBuffers[now.IndexBufferId].Data);
            for (int s = 0; s < was.VertexBufferIds.Count; s++)
                Assert.Equal(before.VertexBuffers[was.VertexBufferIds[s]].Data, after.VertexBuffers[now.VertexBufferIds[s]].Data);
        }
    }

    [Fact]
    public void A_clean_map_passes_through_unchanged()
    {
        if (Jade.Value is not { } riot) return;
        var result = MapGeoBinary.CompactOrphans(riot, null, out int vb, out int ib);
        Assert.Same(riot, result);
        Assert.Equal(0, vb + ib);
    }

    [Fact]
    public void Orphans_already_in_a_file_are_dropped_on_save_and_build()
    {
        if (Jade.Value is not { } riot) return;
        // recreate the Halloween file's state: meshes appended, then removed the pre-M768 way (records only)
        var withTwo = MapGeoMeshAppender.Append(riot, new[] { Quad(0f), Quad(500f) }, out _)!;
        Assert.True(MapGeoBinary.TryReadEditable(withTwo, out var map));
        map.Meshes.RemoveRange(map.Meshes.Count - 2, 2);
        var orphaned = map.Write();
        Assert.True(MapGeoBinary.TryReadEditable(orphaned, out var check));
        Assert.Equal(2, check.UnreferencedVertexBuffers().Count);

        var clean = MapGeoBinary.CompactOrphans(orphaned, null, out int vb, out int ib);
        Assert.Equal(2, vb);
        Assert.Equal(2, ib);
        Assert.True(MapGeoBinary.TryReadEditable(clean, out var cleaned));
        AssertEveryBufferIsUsed(cleaned);
        Assert.Equal(riot, clean);   // exactly back to Riot's file
    }

    [Fact]
    public void Compact_keeps_the_longest_declaration_run_of_a_shared_base()
    {
        if (Jade.Value is not { } riot) return;
        Assert.True(MapGeoBinary.TryReadEditable(riot, out var map));
        // two meshes on one declaration base: the first with n streams, the second with n + 1
        var a = map.Meshes.First(m => m.VertexDeclarationBase + m.VertexBufferIds.Count < map.Declarations.Count);
        int n = a.VertexBufferIds.Count;
        var extraDecl = map.Declarations[a.VertexDeclarationBase + n];
        var b = new MapGeoBinary.Mesh
        {
            VertexCount = a.VertexCount, VertexDeclarationBase = a.VertexDeclarationBase,
            VertexBufferIds = a.VertexBufferIds.Append(a.VertexBufferIds[0]).ToList(),
            IndexCount = a.IndexCount, IndexBufferId = a.IndexBufferId, Transform = a.Transform,
        };
        map.Meshes.Clear();
        map.Meshes.Add(a);
        map.Meshes.Add(b);

        map.Compact();

        Assert.Equal(map.Meshes[0].VertexDeclarationBase, map.Meshes[1].VertexDeclarationBase);
        Assert.True(map.Meshes[1].VertexDeclarationBase + n < map.Declarations.Count, "the longer mesh's last declaration was dropped");
        Assert.Same(extraDecl, map.Declarations[map.Meshes[1].VertexDeclarationBase + n]);
    }
}

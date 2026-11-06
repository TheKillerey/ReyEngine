using System.Numerics;
using LeagueToolkit.Core.Environment;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M866: Add Mesh puts new meshes before the file's final mesh, never after it.
///
/// <para>Measured in game (League 16.22, Map11 base_srx, replay self-test on 2026-11-06): a base_srx whose last mesh was written by this
/// appender crashed the client at load (League of Legends.exe+0x1139cd9, null+0x60); so did a byte-identical copy of Riot's own mesh 0
/// appended last, while the same copy second-to-last loaded and drew. These tests pin the ordering and that nothing else about the file
/// changes: every existing record keeps its bytes (the final one only moves), buffer and declaration ids are unchanged, and the result
/// opens through LeagueToolkit. Real data: Map11 base_srx; skipped without the install.</para>
/// </summary>
public sealed class MapGeoAppendOrderTests(ITestOutputHelper output)
{
    private const string Wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
    private const string GeoPath = "data/maps/mapgeometry/map11/base_srx.mapgeo";

    private static byte[]? Shipped()
    {
        if (!File.Exists(Wad)) return null;
        using var wad = WadArchive.Open(Wad);
        ulong hash = HashAlgorithms.WadPath(GeoPath);
        return wad.TryGetEntry(hash, out _) ? wad.Extract(hash) : null;
    }

    private static NewMapMesh Quad(string material, float x) => new(material,
        new float[] { 0, 0, 0, 100, 0, 0, 100, 0, 100, 0, 0, 100 }, null, new float[] { 0, 0, 1, 0, 1, 1, 0, 1 },
        new ushort[] { 0, 2, 1, 0, 3, 2 }, Matrix4x4.CreateTranslation(x, 60, 7000));

    [Fact]
    public void Added_meshes_go_before_the_final_mesh_and_every_existing_record_keeps_its_bytes()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map11 is not installed"); return; }
        Assert.True(MapGeoBinary.TryReadEditable(shipped, out var before));
        int count = before.Meshes.Count;
        string material = before.Meshes[0].Submeshes[0].Material;

        byte[]? appended = MapGeoMeshAppender.Append(shipped, new[] { Quad("Test/A", 7000), Quad("Test/B", 7200) }, out var error);

        Assert.True(appended is not null, error);
        Assert.True(MapGeoBinary.TryReadEditable(appended!, out var back));
        Assert.Equal(count + 2, back.Meshes.Count);
        int at = MapGeoMeshAppender.InsertionIndex(count);
        Assert.Equal(count - 1, at);
        Assert.Equal("Test/A", back.Meshes[at].Submeshes[0].Material);
        Assert.Equal("Test/B", back.Meshes[at + 1].Submeshes[0].Material);
        // the file still ends on its own final mesh, and every existing mesh is the same mesh with the same buffers
        for (int i = 0; i < count; i++)
        {
            int j = MapGeoMeshAppender.RemapExistingIndex(i, count, 2);
            Assert.Equal(Graft(before, before.Meshes[i]), Graft(back, back.Meshes[j]));
            Assert.Equal(before.Meshes[i].VertexBufferIds, back.Meshes[j].VertexBufferIds);
            Assert.Equal(before.Meshes[i].IndexBufferId, back.Meshes[j].IndexBufferId);
            Assert.Equal(before.Meshes[i].Transform, back.Meshes[j].Transform);
        }
        Assert.Equal(count + 1, MapGeoMeshAppender.RemapExistingIndex(count - 1, count, 2));
        Assert.Equal(0, MapGeoMeshAppender.RemapExistingIndex(0, count, 2));
        Assert.Equal(count - 2, MapGeoMeshAppender.RemapExistingIndex(count - 2, count, 2));
        // the new buffers and declarations are appended after the existing ones, as before
        Assert.Equal(before.VertexBuffers.Count, back.Meshes[at].VertexBufferIds[0]);
        Assert.Equal(before.IndexBuffers.Count, back.Meshes[at].IndexBufferId);

        using var env = new EnvironmentAsset(new MemoryStream(appended!, false));
        Assert.Equal(count + 2, env.Meshes.Count);
        output.WriteLine($"ran: {count} meshes + 2 at {at}; final mesh '{back.Meshes[^1].Submeshes[0].Material}' (was '{before.Meshes[^1].Submeshes[0].Material}'); first material {material}");
    }

    [Fact]
    public void A_bucket_grid_rebuild_after_the_append_includes_the_new_geometry()
    {
        if (Shipped() is not { } shipped) { output.WriteLine("SKIPPED: Map11 is not installed"); return; }
        var appended = MapGeoMeshAppender.Append(shipped, new[] { Quad("Test/A", 7000) }, out var error);
        Assert.True(appended is not null, error);
        var asset = MapGeoDecoder.Decode(appended!);
        var rebuilt = MapGeoWriter.WriteWithRegeneratedBucketGrids(appended!, asset);
        var back = MapGeoDecoder.Decode(rebuilt);
        var baseGrids = MapGeoDecoder.Decode(MapGeoWriter.WriteWithRegeneratedBucketGrids(shipped, MapGeoDecoder.Decode(shipped))).BucketGrids;
        Assert.Equal(asset.Meshes.Count, back.Meshes.Count);
        Assert.True(back.BucketGrids.Sum(g => g.IndexCount) > baseGrids.Sum(g => g.IndexCount));
    }

    [Fact]
    public void An_empty_mesh_list_gets_the_new_mesh_at_index_zero()
    {
        Assert.Equal(0, MapGeoMeshAppender.InsertionIndex(0));
        Assert.Equal(5, MapGeoMeshAppender.RemapExistingIndex(5, 0, 3));
        Assert.Equal(4, MapGeoMeshAppender.RemapExistingIndex(4, 5, 0));
    }

    private static string Graft(MapGeoBinary map, MapGeoBinary.Mesh m)
    {
        var sb = new System.Text.StringBuilder();
        for (int k = 0; k < m.VertexBufferIds.Count; k++)
        {
            var d = map.Declarations[m.VertexDeclarationBase + k];
            sb.Append($"{string.Join(",", d.Elements.Select(e => $"{e.Name}/{e.Format}"))}|{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(map.VertexBuffers[m.VertexBufferIds[k]].Data))};");
        }
        sb.Append(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(map.IndexBuffers[m.IndexBufferId].Data)));
        sb.Append($"|{string.Join(",", m.Submeshes.Select(s => s.Material))}|{m.Visibility}|{m.VisibilityControllerPathHash:x8}|{m.RegionHash:x8}|{m.BoundsMin}|{m.BoundsMax}");
        return sb.ToString();
    }
}

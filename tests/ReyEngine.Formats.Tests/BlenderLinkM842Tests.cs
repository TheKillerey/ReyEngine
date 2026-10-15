using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Interop;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M842: the Blender link's second round on real 16.20 data - a reshape keeps the lightmap, colour and grass
/// channels and can carry several materials; and the
/// protocol 4 wire (grass pivots, the read-only navgrid).
/// </summary>
public sealed class BlenderLinkM842Tests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static byte[]? Extract(string map, string path)
    {
        string wadPath = Path.Combine(Final, "Maps", "Shipping", map + ".wad.client");
        if (!File.Exists(wadPath)) return null;
        using var wad = WadArchive.Open(wadPath);
        ulong h = HashAlgorithms.WadPath(path);
        return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
    }

    /// <summary>The bytes of one vertex element of a mesh, vertex by vertex, from whichever stream holds it.</summary>
    private static byte[]? ElementBytes(MapGeoBinary map, MapGeoBinary.Mesh mesh, uint element)
    {
        for (int stream = 0; stream < mesh.VertexBufferIds.Count; stream++)
        {
            var decl = map.Declarations[mesh.VertexDeclarationBase + stream];
            int offset = 0;
            foreach (var (name, format) in decl.Elements)
            {
                int size = MapGeoBinary.FormatSize(format);
                if (name == element)
                {
                    var data = map.VertexBuffers[mesh.VertexBufferIds[stream]].Data;
                    var result = new byte[mesh.VertexCount * size];
                    for (int v = 0; v < mesh.VertexCount; v++)
                        Buffer.BlockCopy(data, v * decl.Stride + offset, result, v * size, size);
                    return result;
                }
                offset += size;
            }
        }
        return null;
    }

    /// <summary>Pull a mesh the way the link does and push it straight back (same vertices, same order).</summary>
    private static byte[] RoundTrip(byte[] bytes, Func<MapGeoMesh, bool> pick, out int index)
    {
        var asset = MapGeoDecoder.Decode(bytes);
        var mesh = asset.Meshes.First(pick);
        index = mesh.Index;
        var sent = MapGeoBlenderBridge.Snapshot(asset, m => m.Index == mesh.Index).Meshes.Single();
        var reshape = MapGeoBlenderBridge.ToReshape(asset, sent, out string? error);
        Assert.True(reshape is not null, error);
        var result = MapGeoMeshReshaper.TryApply(bytes, new[] { reshape! }, out error);
        Assert.True(result is not null, error);
        return result!;
    }

    [Fact]
    public void AReshapedBushKeepsItsVertexColourAndGrassPivotAndItsSharersAreUntouched()
    {
        // Map11 bushes: VertexDeform, PRIMARY_COLOR + TEXCOORD5, instanced (a shared vertex buffer).
        if (Extract("Map11", "data/maps/mapgeometry/map11/base_srx.mapgeo") is not { } bytes) return;
        var sharing = MapGeoMeshReshaper.SharingMeshes(bytes);
        var after = RoundTrip(bytes, m => sharing.Contains(m.Index) && m.HasVertexColor
            && Array.IndexOf(m.Attributes, "Texcoord5") >= 0 && m.Materials.Count == 1, out int index);

        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var b0));
        Assert.True(MapGeoBinary.TryReadEditable(after, out var b1));
        Assert.Equal(ElementBytes(b0, b0.Meshes[index], MapGeoBinary.ElemPrimaryColor),
                     ElementBytes(b1, b1.Meshes[index], MapGeoBinary.ElemPrimaryColor));   // byte for byte
        var g0 = ElementBytes(b0, b0.Meshes[index], MapGeoBinary.ElemTexcoord5)!;
        var g1 = ElementBytes(b1, b1.Meshes[index], MapGeoBinary.ElemTexcoord5)!;
        Assert.Equal(g0.Length, g1.Length);
        for (int i = 0; i < g0.Length; i += 4)   // through world -> local: float noise, not loss
            Assert.True(Math.Abs(BitConverter.ToSingle(g0, i) - BitConverter.ToSingle(g1, i)) < 0.01f);
        Assert.NotEqual(b0.Meshes[index].VertexBufferIds, b1.Meshes[index].VertexBufferIds);   // its own buffers now
        Assert.Empty(b1.UnreferencedVertexBuffers());
    }

    [Fact]
    public void AReshapedLightmappedMeshKeepsItsRawTexcoord7()
    {
        if (Extract("Map12", "data/maps/mapgeometry/map12/base.mapgeo") is not { } bytes) return;
        var sharing = MapGeoMeshReshaper.SharingMeshes(bytes);
        var after = RoundTrip(bytes, m => sharing.Contains(m.Index) && m.HasLightmapUv && m.Materials.Count == 1, out int index);
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var b0));
        Assert.True(MapGeoBinary.TryReadEditable(after, out var b1));
        Assert.Equal(ElementBytes(b0, b0.Meshes[index], MapGeoBinary.ElemTexcoord7),
                     ElementBytes(b1, b1.Meshes[index], MapGeoBinary.ElemTexcoord7));
        Assert.Equal(ElementBytes(b0, b0.Meshes[index], MapGeoBinary.ElemTexcoord0),
                     ElementBytes(b1, b1.Meshes[index], MapGeoBinary.ElemTexcoord0));
    }

    [Fact]
    public void AMeshWithSeveralMaterialsIsReshapedOneSubmeshPerMaterial()
    {
        if (Extract("Map11", "data/maps/mapgeometry/map11/base_srx.mapgeo") is not { } bytes) return;
        var after = RoundTrip(bytes, m => m.Materials.Count > 1, out int index);
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var b0));
        Assert.True(MapGeoBinary.TryReadEditable(after, out var b1));
        var s0 = b0.Meshes[index].Submeshes;
        var s1 = b1.Meshes[index].Submeshes;
        Assert.Equal(s0.Select(s => s.Material), s1.Select(s => s.Material));
        Assert.Equal(s0.Select(s => s.Hash), s1.Select(s => s.Hash));   // an existing submesh keeps its hash
        Assert.True(s0.Sum(s => s.IndexCount) >= s1.Sum(s => s.IndexCount));   // only degenerate triangles are dropped
        Assert.Equal(b1.Meshes[index].IndexCount, s1.Sum(s => s.IndexCount));
    }

    // ---- the protocol 4 wire --------------------------------------------------------------------------------

    [Fact]
    public void GrassPivotsTravelInTheMeshBlock()
    {
        var sent = new BridgeMesh(1, "bush", Vector3.Zero, new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, null, null,
            new uint[] { 0, 1, 2 }, Vector3.Zero, Vector3.Zero, Vector3.One)
        { GrassPivots = new float[] { 5, 6, 7, 5, 6, 7, 5, 6, 7 } };
        var back = Assert.Single(BlenderBridgeProtocol.DecodeMeshes(BlenderBridgeProtocol.EncodeMeshes(new[] { sent })));
        Assert.Equal(sent.GrassPivots, back.GrassPivots);
    }

    [Fact]
    public void TheNavGridIsServedWithTheEditorsLayersAndNeverEdited()
    {
        var grid = NavGrid.CreateForTests(new Vector3(0, 0, 0), 50f, 3, 2,
            new ushort[] { 0, 1, 2, 0, 0x40, 0 }, new float[] { 1, 2, 3, 4, 5, 6 });
        var layers = new[]
        {
            new BridgeNavLayer(0x0002, "not walkable   (bit 1)", new Vector4(0.26f, 0.85f, 0.36f, 0.80f), 1, false, true),
            new BridgeNavLayer(0x0040, "not-walkable outline   (bit 6)", new Vector4(0.95f, 0.35f, 0.35f, 0.80f), 1, true, false),
        };
        using var server = new BlenderBridgeServer(a => a(), (_, _) => { })
        {
            Handlers = new BlenderBridgeHandlers(() => "m", () => new BridgeSnapshot(Array.Empty<BridgeMesh>(), Array.Empty<string>()), _ => "", _ => "")
            {
                NavGrid = () => new BridgeNavGrid(true, "assets/maps/navgrid/test/aipath.aimesh_ngrid", "project", grid, layers, null),
            },
        };
        Assert.True(server.Start(0, out _));
        using var client = new TcpClient();
        client.Connect(System.Net.IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        BlenderBridgeProtocol.WriteMessage(stream, "{\"op\":\"hello\",\"protocol\":5}");
        BlenderBridgeProtocol.ReadHeader(stream);

        BlenderBridgeProtocol.WriteMessage(stream, "{\"op\":\"navgrid\"}");
        var header = JsonDocument.Parse(BlenderBridgeProtocol.ReadHeader(stream)!).RootElement;
        Assert.True(header.GetProperty("found").GetBoolean());
        Assert.Equal(3, header.GetProperty("count_x").GetInt32());
        Assert.Equal(BridgeNavGrid.Lift, header.GetProperty("lift").GetSingle());
        var sentLayers = header.GetProperty("layers").EnumerateArray().ToList();
        Assert.Equal(2, sentLayers.Count);
        Assert.Equal(0x40, sentLayers[1].GetProperty("mask").GetInt32());
        Assert.Equal("not-walkable outline   (bit 6)", sentLayers[1].GetProperty("label").GetString());
        Assert.Equal(0.95f, sentLayers[1].GetProperty("color")[0].GetSingle());
        Assert.True(sentLayers[1].GetProperty("visible").GetBoolean());
        Assert.True(sentLayers[0].GetProperty("default_visible").GetBoolean());
        var body = BlenderBridgeProtocol.ReadExactly(stream, header.GetProperty("bytes").GetInt32());
        Assert.Equal(6 * 6, body.Length);
        Assert.Equal(4f, BitConverter.ToSingle(body, 3 * 4));
        Assert.Equal((ushort)0x40, BitConverter.ToUInt16(body, 6 * 4 + 4 * 2));

        // there is no navgrid editing op
        BlenderBridgeProtocol.WriteMessage(stream, "{\"op\":\"navgrid_edit\",\"cells\":[{\"i\":3,\"flags\":2}]}");
        var reply = JsonDocument.Parse(BlenderBridgeProtocol.ReadHeader(stream)!).RootElement;
        Assert.Equal("error", reply.GetProperty("op").GetString());
    }
}

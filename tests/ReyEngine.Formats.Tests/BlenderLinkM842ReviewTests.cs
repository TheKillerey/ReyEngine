using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Interop;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M842 review fixes, on real 16.20 Summoner's Rift data: a reshape writes WORLD-space bounds, honours the
/// extended-channel (Mantis) rule, defaults a missing grass pivot to the vertex itself, and a submesh table
/// pulled before a pending material edit does not put the old material back. Also the bucket-grid weld.
/// </summary>
public sealed class BlenderLinkM842ReviewTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string Mantis = "Test/M842/Mantis_Extended_MAT";

    private static byte[]? Map11()
    {
        string wadPath = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(wadPath)) return null;
        using var wad = WadArchive.Open(wadPath);
        ulong h = HashAlgorithms.WadPath("data/maps/mapgeometry/map11/base_srx.mapgeo");
        return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
    }

    private static IReadOnlySet<string> Set(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);

    private static bool IsIdentity(Matrix4x4 m) => m.Equals(Matrix4x4.Identity);

    private static MeshReshape Pulled(byte[] bytes, int index, IReadOnlySet<string>? extended = null)
    {
        var asset = MapGeoDecoder.Decode(bytes, extended);
        var sent = MapGeoBlenderBridge.Snapshot(asset, m => m.Index == index).Meshes.Single();
        var reshape = MapGeoBlenderBridge.ToReshape(asset, sent, out string? error);
        Assert.True(reshape is not null, error);
        return reshape!;
    }

    private static (Vector3 Min, Vector3 Max) DecodedBounds(MapGeoAsset asset, int index)
    {
        var mesh = asset.Meshes.Single(m => m.Index == index);
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            int o = (mesh.VertexStart + v) * 3;
            var p = new Vector3(asset.Positions[o], asset.Positions[o + 1], asset.Positions[o + 2]);
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.05f)
    {
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void AReshapedSharedMeshWithATransformWritesWorldSpaceBounds()
    {
        if (Map11() is not { } bytes) return;
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var b0));
        var sharing = MapGeoMeshReshaper.SharingMeshes(bytes);
        int index = Enumerable.Range(0, b0.Meshes.Count)
            .FirstOrDefault(i => sharing.Contains(i) && !IsIdentity(b0.Meshes[i].Transform) && b0.Meshes[i].Submeshes.Count == 1, -1);
        Assert.True(index >= 0, "Map11 has no instanced mesh with a transform");

        // Riot's own bounds are world space: the decoder's world positions agree with the stored box
        var (rMin, rMax) = DecodedBounds(MapGeoDecoder.Decode(bytes), index);
        Near(rMin, b0.Meshes[index].BoundsMin); Near(rMax, b0.Meshes[index].BoundsMax);

        // a moved copy of the same shape, so the box has to follow the vertices
        var reshape = Pulled(bytes, index);
        var moved = (float[])reshape.Positions.Clone();
        for (int i = 0; i < moved.Length; i += 3) moved[i + 1] += 25f;
        var after = MapGeoMeshReshaper.TryApply(bytes, new[] { reshape with { Positions = moved } }, out string? error);
        Assert.True(after is not null, error);

        Assert.True(MapGeoBinary.TryReadEditable(after!, out var b1));
        var mesh = b1.Meshes[index];
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int i = 0; i < moved.Length; i += 3)
        {
            var w = Vector3.Transform(new Vector3(moved[i], moved[i + 1], moved[i + 2]), mesh.Transform);
            min = Vector3.Min(min, w); max = Vector3.Max(max, w);
        }
        Near(min, mesh.BoundsMin, 0.01f); Near(max, mesh.BoundsMax, 0.01f);

        var (dMin, dMax) = DecodedBounds(MapGeoDecoder.Decode(after!), index);   // and the decoder agrees
        Near(dMin, mesh.BoundsMin); Near(dMax, mesh.BoundsMax);
    }

    [Fact]
    public void AReshapeOntoAMantisMaterialGetsTheExtendedChannelBlock()
    {
        if (Map11() is not { } bytes) return;
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var b0));
        int index = Enumerable.Range(0, b0.Meshes.Count).First(i => b0.Meshes[i].Submeshes.Count == 1 && b0.Meshes[i].VertexCount > 16);
        var reshape = Pulled(bytes, index);
        reshape = reshape with { Submeshes = new[] { new ReshapeSubmesh(Mantis, 0, reshape.Indices.Length) } };

        var after = MapGeoMeshReshaper.TryApply(bytes, new[] { reshape }, Set(Mantis), out string? error, out var notes);
        Assert.True(after is not null, error);
        Assert.Contains(notes, n => n.Contains("extended-channel", StringComparison.Ordinal));
        Assert.True(MapGeoBinary.TryReadEditable(after!, out var b1, Set(Mantis)));
        Assert.True(b1.Meshes[index].HasExtendedChannel);
        Assert.Equal(1, b1.Meshes.Count(m => m.HasExtendedChannel));
        Assert.Equal(after, b1.Write());                                   // byte-exact under the rule
        Assert.Equal(b0.Meshes.Count, MapGeoDecoder.Decode(after!, Set(Mantis)).Meshes.Count);
    }

    [Fact]
    public void AReshapeOnAMapAlreadyUsingMantisKeepsItsExtendedMeshes()
    {
        if (Map11() is not { } bytes) return;
        // a map already using Mantis: one mesh moved onto an extended material, as the material writer does
        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var map));
        int mantisMesh = Enumerable.Range(0, map.Meshes.Count).First(i => map.Meshes[i].Submeshes.Count == 1);
        map.Meshes[mantisMesh].Submeshes[0].Material = Mantis;
        Assert.Equal(1, ExtendedChannelRule.Apply(map, Set(Mantis)));
        byte[] mantisMap = map.Write();

        var sharing = MapGeoMeshReshaper.SharingMeshes(mantisMap, Set(Mantis));
        int index = Enumerable.Range(mantisMesh + 1, map.Meshes.Count - mantisMesh - 1)
            .First(i => sharing.Contains(i) && map.Meshes[i].Submeshes.Count == 1);
        var after = MapGeoMeshReshaper.TryApply(mantisMap, new[] { Pulled(mantisMap, index, Set(Mantis)) },
            Set(Mantis), out string? error, out _);
        Assert.True(after is not null, error);
        Assert.True(MapGeoBinary.TryReadEditable(after!, out var b1, Set(Mantis)));
        Assert.True(b1.Meshes[mantisMesh].HasExtendedChannel);
        Assert.False(b1.Meshes[index].HasExtendedChannel);
        Assert.Equal(after, b1.Write());
    }

    [Fact]
    public void AReshapedBushWithoutGrassPivotsUsesEachVertexAsItsOwnPivot()
    {
        if (Map11() is not { } bytes) return;
        var asset = MapGeoDecoder.Decode(bytes);
        var bush = asset.Meshes.First(m => Array.IndexOf(m.Attributes, "Texcoord5") >= 0 && m.Materials.Count == 1);
        var reshape = Pulled(bytes, bush.Index) with { GrassPivots = null };
        var after = MapGeoMeshReshaper.TryApply(bytes, new[] { reshape }, out string? error);
        Assert.True(after is not null, error);

        var back = MapGeoDecoder.Decode(after!);
        var mesh = back.Meshes.Single(m => m.Index == bush.Index);
        Assert.NotNull(back.GrassPivots);
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            int o = (mesh.VertexStart + v) * 3;
            var p = new Vector3(back.Positions[o], back.Positions[o + 1], back.Positions[o + 2]);
            var g = new Vector3(back.GrassPivots![o], back.GrassPivots[o + 1], back.GrassPivots[o + 2]);
            Near(p, g, 0.01f);   // not the mesh origin
        }
    }

    [Fact]
    public void ASubmeshTableNamingTheOldMaterialFollowsAPendingMaterialEdit()
    {
        if (Map11() is not { } bytes) return;
        var asset = MapGeoDecoder.Decode(bytes);
        var mesh = asset.Meshes.First(m => m.Materials.Count > 1);   // several submeshes (Map11: one material)
        // Blender pulled BEFORE the edit: its table names the file's materials
        var sent = MapGeoBlenderBridge.Snapshot(asset, m => m.Index == mesh.Index).Meshes.Single();
        Assert.NotEmpty(sent.Submeshes);
        string old = mesh.Materials[0];
        string edited = asset.Meshes.SelectMany(m => m.Materials).First(m => m != old);
        // then the edit, still pending: every submesh drawn with 'old' is now 'edited'
        mesh.SubmeshMaterialEdits = Enumerable.Range(0, mesh.Materials.Count).Where(i => mesh.Materials[i] == old)
            .ToDictionary(i => i, _ => edited);

        var reshape = MapGeoBlenderBridge.ToReshape(asset, sent, out string? error);
        Assert.True(reshape is not null, error);
        Assert.DoesNotContain(reshape!.Submeshes!, s => s.Material == old);
        Assert.Contains(reshape.Submeshes!, s => s.Material == edited);

        // the save order: material edits first, then the reshape - the edit survives
        var withEdit = MapGeoMaterialWriter.TryWriteMaterialEdits(bytes, asset.Meshes, Set(), out var mErr);
        Assert.True(withEdit is not null, mErr);
        var after = MapGeoMeshReshaper.TryApply(withEdit!, new[] { reshape }, out error);
        Assert.True(after is not null, error);
        Assert.True(MapGeoBinary.TryReadEditable(after!, out var b1));
        Assert.DoesNotContain(b1.Meshes[mesh.Index].Submeshes, s => s.Material == old);
    }

    [Fact]
    public void AnAmbiguousPendingEditIsNotRemapped()
    {
        var mesh = new MapGeoMesh
        {
            Index = 0, Name = "m", VertexStart = 0, VertexCount = 3, Transform = Matrix4x4.Identity, Pivot = Vector3.Zero,
            Materials = new[] { "A", "A", "C" },
            SubmeshMaterialEdits = new Dictionary<int, string> { [0] = "B", [2] = "D" },
        };
        var remap = MapGeoBlenderBridge.PendingMaterialRemap(mesh);
        Assert.False(remap.ContainsKey("A"));   // submesh 1 is still A
        Assert.Equal("D", remap["C"]);
    }

    [Fact]
    public void TheRebuiltBucketGridWeldsVerticesByPosition()
    {
        // M842 review #9: Riot's grids hold about one vertex per face (base_srx: 196,057 for 192,509); the
        // rebuild keyed vertices by source index and held 2.3 per face, which on base_srx was +26 MB per save.
        var positions = new float[] { 0, 0, 0, 10, 0, 0, 0, 0, 10, /* second mesh, same corners */ 0, 0, 0, 10, 0, 0, 0, 0, 10 };
        var asset = new MapGeoAsset
        {
            Positions = positions, Normals = new float[positions.Length], Uvs = new float[positions.Length / 3 * 2],
            Indices = new uint[] { 0, 1, 2, 3, 4, 5 },
            Groups = new[] { new MapGeoGroup("", 0, 3, MeshIndex: 0), new MapGeoGroup("", 3, 3, MeshIndex: 1) },
            VertexCount = 6,
            Meshes = new[]
            {
                new MapGeoMesh { Index = 0, Name = "a", VertexStart = 0, VertexCount = 3, Transform = Matrix4x4.Identity, Pivot = Vector3.Zero },
                new MapGeoMesh { Index = 1, Name = "b", VertexStart = 3, VertexCount = 3, Transform = Matrix4x4.Identity, Pivot = Vector3.Zero },
            },
        };
        var grid = MapBucketGridBuilder.Rebuild(asset, 1000f).Single();
        // every bucket the triangle touches holds both copies of it: two faces, but the three corners once
        int faces = grid.Indices.Count / 3, used = grid.Buckets.Count(b => b.InsideFaceCount + b.StickingOutFaceCount > 0);
        Assert.Equal(2 * used, faces);
        Assert.Equal(3 * used, grid.Vertices.Count);   // keyed by source index it was 6 per bucket
    }
}

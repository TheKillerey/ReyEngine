using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using Xunit;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M848: the scene bucket grid stores every face ONCE, in the bucket holding the min-X/min-Z corner of its
/// X/Z box. The client draws planar projection VFX onto these triangles additively, and the old builder
/// copied a face into every bucket it overlapped, which showed in game as bright, stepped washes under the
/// Jade turret shots (Old Summoner's Rift, 2026-10-25).
/// </summary>
public sealed class MapBucketGridRiotRuleTests
{
    private sealed record Mesh(string Material, bool HasLightmapUv, params (Vector3 A, Vector3 B, Vector3 C)[] Tris);

    private static (Vector3, Vector3, Vector3) T(float ax, float az, float bx, float bz, float cx, float cz) =>
        (new Vector3(ax, 0, az), new Vector3(bx, 0, bz), new Vector3(cx, 0, cz));

    private static MapGeoAsset Asset(params Mesh[] meshes)
    {
        var pos = new List<float>();
        var idx = new List<uint>();
        var groups = new List<MapGeoGroup>();
        var list = new List<MapGeoMesh>();
        for (int m = 0; m < meshes.Length; m++)
        {
            int vertexStart = pos.Count / 3, indexStart = idx.Count;
            foreach (var (a, b, c) in meshes[m].Tris)
                foreach (var p in new[] { a, b, c })
                {
                    idx.Add((uint)(pos.Count / 3));
                    pos.AddRange(new[] { p.X, p.Y, p.Z });
                }
            groups.Add(new MapGeoGroup(meshes[m].Material, indexStart, idx.Count - indexStart, MeshIndex: m));
            list.Add(new MapGeoMesh
            {
                Index = m, Name = $"mesh{m}", VertexStart = vertexStart, VertexCount = pos.Count / 3 - vertexStart,
                Transform = Matrix4x4.Identity, Pivot = Vector3.Zero, HasLightmapUv = meshes[m].HasLightmapUv,
            });
        }
        return new MapGeoAsset
        {
            Positions = pos.ToArray(), Normals = new float[pos.Count], Uvs = new float[pos.Count / 3 * 2],
            Indices = idx.ToArray(), VertexCount = pos.Count / 3, Groups = groups, Meshes = list,
        };
    }

    internal static IEnumerable<(int X, int Z, bool Inside, Vector3 A, Vector3 B, Vector3 C)> Faces(MapBucketGridData g)
    {
        int n = g.BucketsPerSide;
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            var bucket = g.Buckets[z * n + x];
            int count = bucket.InsideFaceCount + bucket.StickingOutFaceCount;
            for (int f = 0; f < count; f++)
            {
                int i0 = (int)bucket.StartIndex + f * 3;
                yield return (x, z, f < bucket.InsideFaceCount,
                    g.Vertices[(int)bucket.BaseVertex + g.Indices[i0]],
                    g.Vertices[(int)bucket.BaseVertex + g.Indices[i0 + 1]],
                    g.Vertices[(int)bucket.BaseVertex + g.Indices[i0 + 2]]);
            }
        }
    }

    // A 399 x 399 square, so with a 100-unit target the grid is 4 x 4 buckets of ~99.75: the two anchor
    // triangles fix the bounds, the rest sit where the tests need them.
    private static readonly (Vector3, Vector3, Vector3) AnchorLow = T(0, 0, 5, 0, 0, 5);
    private static readonly (Vector3, Vector3, Vector3) AnchorHigh = T(399, 399, 394, 399, 399, 394);

    // min corner (150, 250) -> bucket [1,2]; centroid (231.7, 300) -> bucket [2,3]; crosses both +edges
    private static readonly (Vector3, Vector3, Vector3) Wide = T(150, 250, 390, 260, 155, 390);
    // wholly inside bucket [1,2]
    private static readonly (Vector3, Vector3, Vector3) Small = T(110, 210, 120, 210, 110, 220);
    // spans the whole map: the old builder copied it into all 16 buckets
    private static readonly (Vector3, Vector3, Vector3) Huge = T(1, 1, 398, 1, 1, 398);

    private static MapBucketGridData Grid(params (Vector3, Vector3, Vector3)[] tris) =>
        MapBucketGridBuilder.Rebuild(Asset(new Mesh("mat", false, new[] { AnchorLow, AnchorHigh }.Concat(tris).ToArray())), 100f).Single();

    [Fact]
    public void EveryFaceIsStoredExactlyOnce()
    {
        var grid = Grid(Wide, Small, Huge);
        Assert.Equal(4, grid.BucketsPerSide);

        var faces = Faces(grid).ToList();
        Assert.Equal(5, faces.Count);
        Assert.Equal(5, grid.FaceVisibilityFlags.Count);
        Assert.Equal(5, faces.Select(f => (f.A, f.B, f.C)).Distinct().Count());
        // the huge triangle in particular: one bucket, not every bucket it covers
        Assert.Single(faces, f => f.A == Huge.Item1 && f.B == Huge.Item2);
    }

    [Fact]
    public void AFaceLivesInTheBucketOfItsMinCornerNotItsCentroid()
    {
        var grid = Grid(Wide);
        var wide = Faces(grid).Single(f => f.A == Wide.Item1);
        Assert.Equal((1, 2), (wide.X, wide.Z));
        Assert.False(wide.Inside);

        var huge = Faces(Grid(Huge)).Single(f => f.A == Huge.Item1);
        Assert.Equal((0, 0), (huge.X, huge.Z));
    }

    [Fact]
    public void InsideMeansAllThreeVerticesAreInTheBucketAndInsideFacesComeFirst()
    {
        // the sticking-out face is first in the SOURCE order; the bucket must still list the inside one first
        var grid = Grid(Wide, Small);
        var bucket = grid.Buckets[2 * grid.BucketsPerSide + 1];
        Assert.Equal(1, bucket.InsideFaceCount);
        Assert.Equal(1, bucket.StickingOutFaceCount);

        var inCell = Faces(grid).Where(f => (f.X, f.Z) == (1, 2)).ToList();
        Assert.True(inCell[0].Inside);
        Assert.Equal(Small.Item1, inCell[0].A);
        Assert.False(inCell[1].Inside);
        Assert.Equal(Wide.Item1, inCell[1].A);
    }

    [Fact]
    public void MaxStickOutIsTheOverhangPastThePlusXAndPlusZEdges()
    {
        var grid = Grid(Wide, Small);
        var bucket = grid.Buckets[2 * grid.BucketsPerSide + 1];
        float right = grid.MinX + 2 * grid.BucketSizeX, top = grid.MinZ + 3 * grid.BucketSizeZ;
        Assert.Equal(390f - right, bucket.MaxStickOutX, 3);
        Assert.Equal(390f - top, bucket.MaxStickOutZ, 3);

        Assert.Equal(grid.Buckets.Max(b => b.MaxStickOutX), grid.MaxStickOutX);
        Assert.Equal(grid.Buckets.Max(b => b.MaxStickOutZ), grid.MaxStickOutZ);
        // a bucket holding only inside faces sticks out nowhere
        Assert.All(grid.Buckets.Where(b => b.StickingOutFaceCount == 0), b =>
        {
            Assert.Equal(0f, b.MaxStickOutX);
            Assert.Equal(0f, b.MaxStickOutZ);
        });
    }

    [Theory]
    [InlineData(20f, 25f, 2, true)]    // min corner exactly on an edge belongs to the bucket ABOVE it
    [InlineData(20f, 30f, 2, false)]   // max corner exactly on the +X edge reads as the next bucket: sticking out
    [InlineData(25f, 29.5f, 2, true)]
    [InlineData(150f, 155f, 9, false)] // past the last bucket: clamped to it, and sticking out of the grid
    public void LocateFaceIsTheSinglePrecisionFloorRiotUses(float minX, float maxX, int expectedX, bool inside)
    {
        var (x, z, ins) = MapBucketGridBuilder.LocateFace(
            new Vector3(minX, 0, 1), new Vector3(maxX, 0, 1), new Vector3(minX, 0, 2), 0f, 0f, 10f, 10f, 10);
        Assert.Equal(expectedX, x);
        Assert.Equal(0, z);
        Assert.Equal(inside, ins);
    }

    [Fact]
    public void TheDefaultCellSizeIsRiotsAbout120Units() =>
        Assert.Equal(120f, MapBucketGridBuilder.TargetBucketSize);

    // ===================================================== mesh selection

    [Theory]
    [InlineData("Shaders/StaticMesh/VertexDeform", true)]
    [InlineData("Shaders/StaticMesh/VertexDeform_Bush", true)]
    [InlineData("Shaders/StaticMesh/DefaultEnv_Flat", false)]
    [InlineData("Shaders/StaticMesh/Indicator_Faelights", false)]   // Riot keeps them, in their region grids
    [InlineData("Shaders/StaticMesh/TFT_VertexBend", false)]
    [InlineData(null, false)]
    public void OnlyTheVertexDeformFamilyIsRecognised(string? shader, bool expected) =>
        Assert.Equal(expected, MapBucketGridMeshSelection.IsVertexDeformShader(shader));

    [Fact]
    public void AVertexDeformMeshStaysOutOnlyWithoutLightmapUvs()
    {
        var plant = new Mesh("plant", false, T(10, 10, 20, 10, 10, 20));
        var lightmappedPlant = new Mesh("plant", true, T(30, 30, 40, 30, 30, 40));
        var ground = new Mesh("ground", false, T(0, 0, 100, 0, 0, 100));
        var asset = Asset(plant, lightmappedPlant, ground);
        var selection = new MapBucketGridMeshSelection(new HashSet<string> { "plant" });

        Assert.Equal(new[] { 0 }, selection.ExcludedMeshes(asset).ToArray());
        Assert.True(selection.Excludes(asset, 0));
        Assert.False(selection.Excludes(asset, 1));
        Assert.False(selection.Excludes(asset, 2));

        int CountFaces(MapBucketGridMeshSelection? s) =>
            MapBucketGridBuilder.Rebuild(asset, 100f, meshSelection: s).Sum(g => g.FaceVisibilityFlags.Count);
        Assert.Equal(2, CountFaces(selection));
        Assert.Equal(3, CountFaces(null));                             // null keeps every mesh, as before M848
        Assert.Equal(3, CountFaces(MapBucketGridMeshSelection.All));
    }

    [Fact]
    public void TheSelectionIsReadFromTheMaterialsBinsPassShader()
    {
        static uint H(string s) => HashAlgorithms.Fnv1a(s);
        BinTreeObject Material(string name, string shader) => new(H(name), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), name),
            new BinTreeContainer(H("techniques"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
                {
                    new BinTreeContainer(H("passes"), BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
                        {
                            new BinTreeObjectLink(H("shader"), H(shader)),
                        }),
                    }),
                }),
            }),
        });
        var names = new[] { "StaticMaterialDef", "Shaders/StaticMesh/VertexDeform", "Shaders/StaticMesh/DefaultEnv_Flat",
                            "Shaders/StaticMesh/Indicator_Faelights" }.ToDictionary(H);
        using var ms = new MemoryStream();
        new BinTree(new[]
        {
            Material("Maps/Test/Bush_MAT", "Shaders/StaticMesh/VertexDeform"),
            Material("Maps/Test/Ground_MAT", "Shaders/StaticMesh/DefaultEnv_Flat"),
            Material("Maps/Test/FaeLights_MAT", "Shaders/StaticMesh/Indicator_Faelights"),
        }, Array.Empty<string>()).Write(ms);

        var selection = MapBucketGridMeshSelection.FromMaterialsBin(ms.ToArray(), h => names.TryGetValue(h, out var n) ? n : null);
        Assert.Equal(new[] { "Maps/Test/Bush_MAT" }, selection.VertexDeformMaterials.ToArray());

        // without the hash dictionary the shader stays a hash, and Riot's one VertexDeform shader is still recognised
        var unnamed = MapBucketGridMeshSelection.FromMaterialsBin(ms.ToArray(), h => h == H("StaticMaterialDef") ? "StaticMaterialDef" : null);
        Assert.Equal(new[] { "Maps/Test/Bush_MAT" }, unnamed.VertexDeformMaterials.ToArray());

        // an unreadable bin keeps every mesh rather than guessing
        Assert.Empty(MapBucketGridMeshSelection.FromMaterialsBin(new byte[] { 1, 2, 3 }, _ => null).VertexDeformMaterials);
        Assert.Empty(MapBucketGridMeshSelection.FromMaterialsBin(null, _ => null).VertexDeformMaterials);
    }
}

/// <summary>
/// M848 on real data: Riot's own Map11 base_srx.mapgeo. Riot's grids must follow the rule
/// <see cref="MapBucketGridBuilder.LocateFace"/> implements, and a rebuild from Riot's own meshes must hold
/// every face once and break none of it. Where the rebuild differs from Riot's grids (bucket counts, mesh
/// selection, render-region space) the difference is REPORTED in the test output, not hidden. No-op when the
/// game install is not present, like the other real-data tests.
/// </summary>
public sealed class MapBucketGridRiotRealDataTests(ITestOutputHelper output)
{
    private const string Map11 = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
    private const string MapGeoPath = "data/maps/mapgeometry/map11/base_srx.mapgeo";
    private const string MaterialsPath = "data/maps/mapgeometry/map11/base_srx.materials.bin";

    private static readonly Lazy<HashDatabase?> Hashes = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); } catch { return null; }
    });

    private static string? Name(uint h) => Hashes.Value is { } db && db.TryGetBinName(h, out var n) ? n : null;

    private static bool TryRead(out byte[] mapgeo, out byte[] materials)
    {
        mapgeo = materials = Array.Empty<byte>();
        if (!File.Exists(Map11)) return false;
        using var wad = WadArchive.Open(Map11);
        ulong g = HashAlgorithms.WadPath(MapGeoPath), m = HashAlgorithms.WadPath(MaterialsPath);
        if (!wad.TryGetEntry(g, out _) || !wad.TryGetEntry(m, out _)) return false;
        mapgeo = wad.Extract(g);
        materials = wad.Extract(m);
        return true;
    }

    /// <summary>Quantised to 0.1 units, so a face in Riot's grid and the same face decoded from Riot's mesh
    /// compare equal despite the float noise of the world transform.</summary>
    private static (long, long, long) Key(Vector3 a, Vector3 b, Vector3 c)
    {
        static long Q(Vector3 v) => ((long)MathF.Round(v.X * 10) * 73856093L) ^ ((long)MathF.Round(v.Y * 10) * 19349663L)
                                    ^ ((long)MathF.Round(v.Z * 10) * 83492791L);
        var k = new[] { Q(a), Q(b), Q(c) };
        Array.Sort(k);
        return (k[0], k[1], k[2]);
    }

    [Fact]
    public void RiotsOwnGridsFollowTheRule()
    {
        if (!TryRead(out var mapgeo, out _)) return;
        using var env = new EnvironmentAsset(new MemoryStream(mapgeo, false));

        long faces = 0, cellMismatches = 0, insideMismatches = 0, insideOffEdge = 0;
        float worstStickOut = 0f;
        foreach (var sg in env.SceneGraphs)
        {
            var span = sg.Buckets.Span;
            int n = span.Width;
            for (int bz = 0; bz < span.Height; bz++)
            for (int bx = 0; bx < span.Width; bx++)
            {
                var bucket = span[bz, bx];
                float right = sg.MinX + (bx + 1) * sg.BucketSizeX, top = sg.MinZ + (bz + 1) * sg.BucketSizeZ;
                float stickX = 0f, stickZ = 0f;
                int count = bucket.InsideFaceCount + bucket.StickingOutFaceCount;
                for (int f = 0; f < count; f++)
                {
                    int i0 = (int)bucket.StartIndex + f * 3;
                    Vector3 a = sg.Vertices[(int)bucket.BaseVertex + sg.Indices[i0]];
                    Vector3 b = sg.Vertices[(int)bucket.BaseVertex + sg.Indices[i0 + 1]];
                    Vector3 c = sg.Vertices[(int)bucket.BaseVertex + sg.Indices[i0 + 2]];
                    bool inside = f < bucket.InsideFaceCount;
                    faces++;
                    var (x, z, ins) = MapBucketGridBuilder.LocateFace(a, b, c, sg.MinX, sg.MinZ, sg.BucketSizeX, sg.BucketSizeZ, n);
                    if (x != bx || z != bz) cellMismatches++;
                    else if (ins != inside)
                    {
                        insideMismatches++;
                        // only a max corner sitting on a +X/+Z edge, within float error, may disagree
                        float maxX = MathF.Max(a.X, MathF.Max(b.X, c.X)), maxZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
                        if (MathF.Min(MathF.Abs(maxX - right), MathF.Abs(maxZ - top)) > 0.01f) insideOffEdge++;
                    }
                    if (!inside)
                        foreach (var p in new[] { a, b, c })
                        { stickX = MathF.Max(stickX, p.X - right); stickZ = MathF.Max(stickZ, p.Z - top); }
                }
                worstStickOut = MathF.Max(worstStickOut, MathF.Max(MathF.Abs(stickX - bucket.MaxStickOutX), MathF.Abs(stickZ - bucket.MaxStickOutZ)));
            }
        }

        output.WriteLine($"Riot base_srx: {env.SceneGraphs.Count} grids, {faces:n0} faces; cell mismatches {cellMismatches}, "
                         + $"inside-flag mismatches {insideMismatches} (all on a cell edge: {insideOffEdge == 0}), worst stick-out deviation {worstStickOut:G3}");
        Assert.True(faces > 0);
        Assert.Equal(0, cellMismatches);
        Assert.Equal(0, insideOffEdge);
        Assert.True(insideMismatches <= 16, $"{insideMismatches} inside-flag mismatches - more than the measured handful of edge cases");
        Assert.True(worstStickOut <= 0.005f, $"stick-out deviates by {worstStickOut}");
    }

    [Fact]
    public void ARebuildFromRiotsMeshesHoldsEveryFaceOnceAndFollowsTheRule()
    {
        if (!TryRead(out var mapgeo, out var materials)) return;
        var asset = MapGeoDecoder.Decode(mapgeo, ExtendedChannelRule.From(materials, Name));
        // The app's resolver order (MainWindowViewModel.ResolveBinName): hash tables, then the open map's own
        // material names keyed by FNV-1a - since 16.21 a material's name in the bin is a hash (M847).
        var mapNames = new Dictionary<uint, string>();
        foreach (var g in asset.Groups.Where(g => g.Material.Length > 0))
        {
            mapNames.TryAdd(HashAlgorithms.Fnv1a(g.Material), g.Material);
            mapNames.TryAdd(HashAlgorithms.Fnv1aRaw(g.Material), g.Material);
        }
        uint staticMaterialDef = HashAlgorithms.Fnv1a("StaticMaterialDef");
        string? AppName(uint h) => Name(h) ?? (mapNames.TryGetValue(h, out var n) ? n : h == staticMaterialDef ? "StaticMaterialDef" : null);

        var selection = MapBucketGridMeshSelection.FromMaterialsBin(materials, AppName);
        var excluded = selection.ExcludedMeshes(asset);
        // base_srx's plants: 149 VertexDeform meshes without lightmap UVs on the 16.21 data. A selection that
        // silently matched nothing would pass every check below, so it must actually leave meshes out.
        Assert.True(excluded.Count > 0, "no VertexDeform mesh was recognised - the selection did nothing");
        // and the same without any hash table (a fresh worktree): names from the mapgeo, the shader by its hash
        string? NoTables(uint h) => mapNames.TryGetValue(h, out var n) ? n : h == staticMaterialDef ? "StaticMaterialDef" : null;
        Assert.Equal(excluded.OrderBy(i => i), MapBucketGridMeshSelection.FromMaterialsBin(materials, NoTables).ExcludedMeshes(asset).OrderBy(i => i));
        var grids = MapBucketGridBuilder.Rebuild(asset, meshSelection: selection);

        // what went in: every triangle of every kept mesh that survives the height filter, each exactly once
        long expected = 0;
        var keptKeys = new HashSet<(long, long, long)>();
        var excludedKeys = new HashSet<(long, long, long)>();
        foreach (var g in asset.Groups)
            for (int i = g.StartIndex; i < g.StartIndex + g.IndexCount; i += 3)
            {
                Vector3 P(uint v) => new(asset.Positions[v * 3], asset.Positions[v * 3 + 1], asset.Positions[v * 3 + 2]);
                Vector3 a = P(asset.Indices[i]), b = P(asset.Indices[i + 1]), c = P(asset.Indices[i + 2]);
                float lo = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), hi = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
                if (hi < MapBucketGridBuilder.HeightRangeMin || lo > MapBucketGridBuilder.HeightRangeMax) continue;
                if (excluded.Contains(g.MeshIndex)) { excludedKeys.Add(Key(a, b, c)); continue; }
                expected++;
                keptKeys.Add(Key(a, b, c));
            }

        long faces = 0, violations = 0, crossCellCopies = 0;
        foreach (var grid in grids)
        {
            int n = grid.BucketsPerSide;
            var cellOf = new Dictionary<(Vector3, Vector3, Vector3), int>();
            var stickX = new float[n * n];
            var stickZ = new float[n * n];
            foreach (var f in MapBucketGridRiotRuleTests.Faces(grid))
            {
                faces++;
                var (x, z, inside) = MapBucketGridBuilder.LocateFace(f.A, f.B, f.C, grid.MinX, grid.MinZ, grid.BucketSizeX, grid.BucketSizeZ, n);
                if (x != f.X || z != f.Z || inside != f.Inside) violations++;
                int cell = f.Z * n + f.X;
                if (cellOf.TryGetValue((f.A, f.B, f.C), out int other) && other != cell) crossCellCopies++;
                cellOf.TryAdd((f.A, f.B, f.C), cell);
                if (f.Inside) continue;
                float right = grid.MinX + (f.X + 1) * grid.BucketSizeX, top = grid.MinZ + (f.Z + 1) * grid.BucketSizeZ;
                foreach (var p in new[] { f.A, f.B, f.C })
                {
                    stickX[cell] = MathF.Max(stickX[cell], p.X - right);
                    stickZ[cell] = MathF.Max(stickZ[cell], p.Z - top);
                }
            }
            for (int cell = 0; cell < n * n; cell++)
                if (stickX[cell] != grid.Buckets[cell].MaxStickOutX || stickZ[cell] != grid.Buckets[cell].MaxStickOutZ) violations++;
            if (grid.MaxStickOutX != grid.Buckets.Max(b => b.MaxStickOutX) || grid.MaxStickOutZ != grid.Buckets.Max(b => b.MaxStickOutZ)) violations++;
        }

        Assert.Equal(expected, faces);          // no face duplicated, none lost
        Assert.Equal(0, crossCellCopies);
        Assert.Equal(0, violations);

        // ---- against Riot's own grids: reported, not hidden
        using var env = new EnvironmentAsset(new MemoryStream(mapgeo, false));
        var riotKeys = new HashSet<(long, long, long)>();
        long riotFaces = 0;
        foreach (var sg in env.SceneGraphs)
        {
            var span = sg.Buckets.Span;
            for (int bz = 0; bz < span.Height; bz++)
            for (int bx = 0; bx < span.Width; bx++)
            {
                var bucket = span[bz, bx];
                for (int f = 0; f < bucket.InsideFaceCount + bucket.StickingOutFaceCount; f++)
                {
                    int i0 = (int)bucket.StartIndex + f * 3;
                    riotFaces++;
                    riotKeys.Add(Key(sg.Vertices[(int)bucket.BaseVertex + sg.Indices[i0]],
                        sg.Vertices[(int)bucket.BaseVertex + sg.Indices[i0 + 1]], sg.Vertices[(int)bucket.BaseVertex + sg.Indices[i0 + 2]]));
                }
            }
        }
        long excludedInRiot = excludedKeys.Count(riotKeys.Contains);
        output.WriteLine($"rebuild: {grids.Count} grids (Riot {env.SceneGraphs.Count}), master {grids[0].BucketsPerSide}x{grids[0].BucketsPerSide} @ {grids[0].BucketSizeX:F1} "
                         + $"(Riot {env.SceneGraphs[0].Buckets.Span.Width}x{env.SceneGraphs[0].Buckets.Span.Width} @ {env.SceneGraphs[0].BucketSizeX:F1}), "
                         + $"faces {faces:n0} (Riot {riotFaces:n0})");
        output.WriteLine($"mesh selection: {excluded.Count} mesh(es) left out as VertexDeform without lightmap UVs"
                         + (Hashes.Value is null ? " (no hash dictionary - names from the mapgeo, shader by its hash)" : "")
                         + $"; {excludedInRiot} of their {excludedKeys.Count:n0} faces are in Riot's grids");
        output.WriteLine($"faces only in ours: {keptKeys.Count(k => !riotKeys.Contains(k)):n0} (meshes Riot also leaves out, e.g. water lilies), "
                         + $"only in Riot: {riotKeys.Count(k => !keptKeys.Contains(k)):n0} (render-region grids, which Riot stores in mesh-local space)");
        Assert.Equal(0, excludedInRiot);       // the rule never drops a face Riot keeps

        // ---- and it survives the byte-level write + LeagueToolkit re-read unchanged
        byte[] written = MapGeoWriter.WriteWithRegeneratedBucketGrids(mapgeo, asset, meshSelection: selection);
        using var reread = new EnvironmentAsset(new MemoryStream(written, false));
        Assert.Equal(grids.Count, reread.SceneGraphs.Count);
        Assert.Equal(faces, reread.SceneGraphs.Sum(g => (long)g.Indices.Count / 3));
    }
}

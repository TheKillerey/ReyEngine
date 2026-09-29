using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M794: a mesh emitter's uvScale reaches Riot's mesh_vs on D3D11. The report was a hard vertical seam in
/// Map22 aprilfool's sky band on the board's centre line. The ground ring (Level1_AprilFool_Skybox's GroundA
/// and GroundB) is four quarters of ONE quarter-disc texture: GroundB's mesh is GroundA's mirrored in X with u
/// flipped, and its uvScale (-1, 1) flips u back. The D3D11 mesh path never read uvScale, so GroundB's
/// quarters sampled 1 - u and broke against GroundA's along X = 0.
/// </summary>
public sealed class MeshEmitterUvScaleTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static Vector2 Apply(float[] m, float u, float v) =>
        new(m[0] * u + m[1] * v + m[2], m[4] * u + m[5] * v + m[6]);

    [Fact]
    public void TheUvScaleComposesAsTheQuadCellDoes()
    {
        foreach (var (scale, degrees, centre) in new[]
                 {
                     (new Vector2(-1f, 1f), 0f, new Vector2(0.5f, 0.5f)),
                     (new Vector2(2f, 0.5f), 0f, new Vector2(0.5f, 0.5f)),
                     (new Vector2(3f, -2f), 30f, new Vector2(0.3f, 0.6f)),
                 })
        {
            var layer = new VfxUvLayer(Vector2.Zero, Vector2.Zero, new Vector2(0.25f, 0f), Vector2.Zero, false,
                scale, degrees, 0f, centre, false, false);
            Assert.True(VfxUvTransform.MeshTransformsUv(layer));
            foreach (float age in new[] { 0f, 1.3f })
            {
                var m = VfxUvTransform.MeshAffine(layer, Vector2.One, Vector2.Zero, age);
                foreach (var (u, v) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (0.3f, 0.8f) })
                {
                    var want = VfxUvTransform.Cell(layer, u, v, age, 0f);
                    var got = Apply(m, u, v);
                    Assert.Equal(want.X, got.X, 4);
                    Assert.Equal(want.Y, got.Y, 4);
                }
            }
        }
    }

    [Fact]
    public void AScaleAloneSelectsThePerParticleTransformAndAZeroComponentReadsAsOne()
    {
        var mirror = new VfxUvLayer(Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, false,
            new Vector2(-1f, 1f), 0f, 0f, new Vector2(0.5f, 0.5f), false, false);
        Assert.True(VfxUvTransform.MeshTransformsUv(mirror));
        var m = VfxUvTransform.MeshAffine(mirror, Vector2.One, Vector2.Zero, 0f);
        Assert.Equal(1f, Apply(m, 0f, 0.2f).X, 5);        // u' = 1 - u
        Assert.Equal(0.25f, Apply(m, 0.75f, 0.2f).X, 5);
        Assert.Equal(0.2f, Apply(m, 0.75f, 0.2f).Y, 5);   // v untouched

        var zero = mirror with { Scale = new Vector2(0f, 2f) };
        var one = mirror with { Scale = new Vector2(1f, 2f) };
        Assert.Equal(VfxUvTransform.MeshAffine(one, Vector2.One, Vector2.Zero, 0f), VfxUvTransform.MeshAffine(zero, Vector2.One, Vector2.Zero, 0f));
        Assert.False(VfxUvTransform.MeshTransformsUv(mirror with { Scale = new Vector2(0f, 0f) }));
    }

    private sealed record Ground(VfxSystemDefinition System, VfxEmitterDefinition A, VfxEmitterDefinition B,
        StaticMeshData MeshA, StaticMeshData MeshB, TextureImage Texture, WadArchive Wad, WadPathResolver Resolver) : IDisposable
    {
        public void Dispose() => Wad.Dispose();
    }

    private static Ground? LoadGround()
    {
        string wadPath = Path.Combine(Final, @"Maps\Shipping\Map22.wad.client");
        if (!File.Exists(wadPath)) return null;
        var resolver = new WadPathResolver(new HashSyncService().LoadLocal(_ => { }));
        var wad = WadArchive.Open(wadPath, resolver);
        byte[]? Read(string path)
        {
            ulong h = BinTexturePath.HashOfReference(path);
            return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
        }
        var bin = Read("data/maps/mapgeometry/map22/aprilfool.materials.bin");
        var system = bin is null ? null : VfxSystemResolver.ExtractAll(bin).Values.FirstOrDefault(s => s.Name == "Level1_AprilFool_Skybox");
        if (system is null) { wad.Dispose(); return null; }
        var a = system.Emitters.Single(e => e.Name == "GroundA");
        var b = system.Emitters.Single(e => e.Name == "GroundB");
        var meshA = StaticObjectDecoder.Decode(Read(a.MeshPath!)!, a.MeshPath!)!;
        var meshB = StaticObjectDecoder.Decode(Read(b.MeshPath!)!, b.MeshPath!)!;
        var texture = TextureDecoder.Decode(Read(a.TexturePath!)!);
        return new Ground(system, a, b, meshA, meshB, texture, wad, resolver);
    }

    /// <summary>The data itself: every GroundA vertex with a GroundB twin at (-x, y, z) samples the same texel as
    /// its twin once each mesh's uv goes through its own <see cref="VfxUvTransform.MeshAffine"/> - and without
    /// GroundB's uvScale (the pre-M794 constant) almost none do.</summary>
    [Fact]
    public void AprilFoolGroundBIsGroundAMirroredAndItsUvScaleLinesTheTexturesUp()
    {
        using var g = LoadGround();
        if (g is null) return;
        Assert.Equal(new Vector2(-1f, 1f), g.B.UvScale);
        Assert.Equal(Vector2.One, g.A.UvScale);
        Assert.Equal(g.A.TexturePath, g.B.TexturePath);
        Assert.True(g.A.IsMeshPrimitive && g.B.IsMeshPrimitive);

        var layerA = VfxUvLayer.BaseOf(g.A);
        var layerB = VfxUvLayer.BaseOf(g.B);
        Assert.False(VfxUvTransform.MeshTransformsUv(layerA));
        Assert.True(VfxUvTransform.MeshTransformsUv(layerB));
        var affineA = VfxUvTransform.MeshAffine(layerA, Vector2.One, Vector2.Zero, 0f);
        var affineB = VfxUvTransform.MeshAffine(layerB, Vector2.One, Vector2.Zero, 0f);
        var before = VfxUvTransform.MeshAffine(layerB with { Scale = Vector2.One }, Vector2.One, Vector2.Zero, 0f);

        static (long, long, long) Key(float x, float y, float z) =>
            ((long)MathF.Round(x * 2f), (long)MathF.Round(y * 2f), (long)MathF.Round(z * 2f));
        var twins = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < g.MeshB.Positions.Length / 3; i++)
        {
            var k = Key(g.MeshB.Positions[i * 3], g.MeshB.Positions[i * 3 + 1], g.MeshB.Positions[i * 3 + 2]);
            if (!twins.TryGetValue(k, out var list)) twins[k] = list = new List<int>();
            list.Add(i);
        }

        int paired = 0, alignedAfter = 0, alignedBefore = 0;
        for (int i = 0; i < g.MeshA.Positions.Length / 3; i++)
        {
            var p = g.MeshA.Positions;
            if (!twins.TryGetValue(Key(-p[i * 3], p[i * 3 + 1], p[i * 3 + 2]), out var list)) continue;
            paired++;
            var wantUv = Apply(affineA, g.MeshA.Uvs[i * 2], g.MeshA.Uvs[i * 2 + 1]);
            bool Aligned(float[] affine) => list.Any(j =>
                Vector2.Distance(Apply(affine, g.MeshB.Uvs[j * 2], g.MeshB.Uvs[j * 2 + 1]), wantUv) < 0.01f);
            if (Aligned(affineB)) alignedAfter++;
            if (Aligned(before)) alignedBefore++;
        }

        // 809 of GroundA's 1,014 soup vertices have a twin; all of them line up with the scale applied.
        Assert.True(paired > 700, $"expected GroundB to mirror GroundA, {paired} twins");
        Assert.Equal(paired, alignedAfter);
        Assert.True(alignedBefore < paired / 20, $"without uvScale {alignedBefore} of {paired} twins lined up");    }

    /// <summary>The real emitters on the real D3D11 path, seen from straight above the ring's centre: the ring is
    /// mirror-symmetric about X = 0 with GroundB's uvScale, and lopsided on the pre-M794 constant (same frame,
    /// same particles).</summary>
    [Fact]
    public void AprilFoolGroundRingIsMirrorSymmetricOnTheD3D11MeshPath()
    {
        using var g = LoadGround();
        if (g is null) return;
        using var cache = ShaderCacheReader.Open(Final, g.Resolver, out _);
        if (cache is null) return;

        var ring = g.System with { PathHash = HashAlgorithms.Fnv1a("m794/ground"), Emitters = new[] { g.A, g.B } };
        var item = new VfxPlaybackItem(ring, Matrix4x4.Identity, new[] { g.Texture, g.Texture },
            new StaticMeshData?[] { g.MeshA, g.MeshB });

        // Straight down on the ring (it lies 1,000-2,000 units below the board, 8,700 wide), far side up.
        var eye = new Vector3(0f, 9000f, 0f);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, -1500f, 0f), Vector3.UnitZ);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1f, 100f, 40000f);
        var mirroredView = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;
        const int Size = 256;
        var settings = new PreviewSettings
        {
            SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
            AlphaBlend = true, DepthTest = true, MirrorX = true, TransposeMatrices = true,
            CullBackFaces = false, SortByPipeline = false,
            ClearColor = new Vector4(0f, 0f, 0f, 1f), TimeSeconds = 1f,
        };

        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return;   // no device: nothing to measure
        var driver = new D3D11MapParticles(renderer, cache);
        driver.SetPlayback(new VfxPlayback(new[] { item }));
        for (int i = 0; i < 10; i++) driver.Tick(1f / 30f, mirroredView, mirroredView * proj, eye, 9000f);
        Assert.Equal(2, driver.RiotMeshEmitters);
        var mats = driver.Materials.Where(m => m.RiotMeshGeometryId is not null).ToList();
        var scaled = Assert.Single(mats, m => m.MeshUvLayer is not null);   // GroundB only
        Assert.Equal(new Vector2(-1f, 1f), scaled.MeshUvLayer!.Value.Scale);

        (double Asymmetry, int Lit) Measure()
        {
            var frame = renderer.RenderFrame(Size, Size, settings, out _)!;
            double sum = 0; int lit = 0;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size / 2; x++)
                {
                    int k = (y * Size + x) * 4, j = (y * Size + Size - 1 - x) * 4;
                    if (frame[k] + frame[k + 1] + frame[k + 2] + frame[j] + frame[j + 1] + frame[j + 2] == 0) continue;
                    sum += (Math.Abs(frame[k] - frame[j]) + Math.Abs(frame[k + 1] - frame[j + 1]) + Math.Abs(frame[k + 2] - frame[j + 2])) / 3.0;
                    lit++;
                }
            return (lit == 0 ? 0 : sum / lit, lit);
        }

        // the pre-M794 draw first: GroundB on the per-material constant, untouched
        var layer = scaled.MeshUvLayer;
        scaled.MeshUvLayer = null;
        var before = Measure();
        scaled.MeshUvLayer = layer;
        var after = Measure();
        driver.StopAll();

        // measured: mean |left - mirrored right| 48.2 before, 5.8 after, over ~12,900 lit pixel pairs
        Assert.True(before.Lit > 5000 && after.Lit > 5000, $"nothing drawn: before {before}, after {after}");
        Assert.True(after.Asymmetry < 12, $"the ring should be mirror-symmetric with uvScale, got {after}");
        Assert.True(before.Asymmetry > 3 * after.Asymmetry, $"the pre-M794 ring should be lopsided, got {before} vs {after}");
    }
}

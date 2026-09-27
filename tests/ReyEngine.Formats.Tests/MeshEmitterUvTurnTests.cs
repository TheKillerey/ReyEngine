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
/// M786: a mesh emitter's uv rotation and particleUVScrollRate reach Riot's mesh_vs. The report was
/// TFT_Anniversary_Idle_Ievel1_Candles02 on Map22 anniversary: small upright flames in game, flat wide
/// yellow smears in the editor. The flame is a flat teardrop mesh textured with a HORIZONTAL wavy band that
/// uvRotation 90 stands upright; the D3D11 mesh path applied only texDiv and the birth scroll, so the band
/// stayed horizontal and cut the flame into a wide strip.
/// </summary>
public sealed class MeshEmitterUvTurnTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static Vector2 Apply(float[] m, float u, float v) =>
        new(m[0] * u + m[1] * v + m[2], m[4] * u + m[5] * v + m[6]);

    [Fact]
    public void WithNoTurnAndNoIntegratedScrollTheOldConstantComesBack()
    {
        var layer = new VfxUvLayer(Vector2.Zero, new Vector2(0.4f, 0f), Vector2.Zero, Vector2.Zero, true,
            new Vector2(2f, 3f), 0f, 0f, new Vector2(0.5f, 0.5f), true, false);   // scale/flip/clamp: still unread

        var m = VfxUvTransform.MeshAffine(layer, new Vector2(2f, 0.5f), new Vector2(0.3f, -0.2f), particleAge: 1.7f);

        // exactly what TickMeshSlices has written since M640: { dv.X, 0, sc.X, 0, 0, dv.Y, sc.Y, 0, 0, 0, 1, 0 }
        var old = new[] { 2f, 0f, 0.3f, 0f, 0f, 0.5f, -0.2f, 0f, 0f, 0f, 1f, 0f };
        for (int i = 0; i < 12; i++) Assert.Equal(old[i], m[i], 6);
        Assert.False(VfxUvTransform.MeshTurnsOrScrolls(layer));
    }

    [Fact]
    public void TheTurnAndTheIntegratedScrollComposeAsTheQuadCellDoes()
    {
        // Candles02 'new': uvRotation 90, particleUVScrollRate (1.2, 0), default centre, nothing else.
        var layer = new VfxUvLayer(Vector2.Zero, Vector2.Zero, new Vector2(1.2f, 0f), Vector2.Zero, false,
            Vector2.One, 90f, 0f, new Vector2(0.5f, 0.5f), false, false);
        Assert.True(VfxUvTransform.MeshTurnsOrScrolls(layer));

        foreach (float age in new[] { 0f, 0.37f, 2.5f })
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

    [Fact]
    public void TurnedNinetyDegreesTheBandRunsAlongTheFlameInsteadOfAcrossIt()
    {
        // TFT_ArenaSkin_Intro_010.tex holds its alpha in a band at texture v 0.34..0.69. The flame mesh's v runs
        // along its height, u across it. Turned, texture v follows mesh u - the band is a vertical stripe.
        var layer = new VfxUvLayer(Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, false,
            Vector2.One, 90f, 0f, new Vector2(0.5f, 0.5f), false, false);
        var m = VfxUvTransform.MeshAffine(layer, Vector2.One, Vector2.Zero, 0f);

        Assert.Equal(0.5f, Apply(m, 0.5f, 0.05f).Y, 4);   // bottom of the flame, centre line: in the band
        Assert.Equal(0.5f, Apply(m, 0.5f, 0.95f).Y, 4);   // top of the flame, centre line: in the band
        Assert.Equal(0.1f, Apply(m, 0.1f, 0.5f).Y, 4);    // the flame's side at mid height: outside it
    }

    /// <summary>The real emitter on the real D3D11 path: the flame's drawn footprint is taller than wide with
    /// the turn, and a wide flat strip without it (the pre-M786 constant, same frame, same particle).</summary>
    [Fact]
    public void Candles02FlameStandsUprightOnTheD3D11MeshPath()
    {
        string wadPath = Path.Combine(Final, @"Maps\Shipping\Map22.wad.client");
        if (!File.Exists(wadPath)) return;
        var resolver = new WadPathResolver(new HashSyncService().LoadLocal(_ => { }));
        using var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) return;
        using var wad = WadArchive.Open(wadPath, resolver);
        byte[]? Read(string path)
        {
            ulong h = BinTexturePath.HashOfReference(path);
            return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
        }
        var bin = Read("data/maps/mapgeometry/map22/anniversary.materials.bin");
        if (bin is null) return;
        var system = VfxSystemResolver.ExtractAll(bin).Values.FirstOrDefault(s => s.Name == "TFT_Anniversary_Idle_Ievel1_Candles02");
        if (system is null) return;
        var flame = system.Emitters.Single(e => e.Name == "new");
        Assert.True(flame.IsMeshPrimitive);
        Assert.Equal(90f, flame.UvRotation);
        Assert.Equal(new Vector2(1.2f, 0f), flame.UvScrollIntegrated);

        var one = system with { PathHash = HashAlgorithms.Fnv1a("m786/flame"), Emitters = new[] { flame } };
        var texture = Read(flame.TexturePath!) is { } tb ? TextureDecoder.Decode(tb) : null;
        var mesh = Read(flame.MeshPath!) is { } mb ? StaticObjectDecoder.Decode(mb, flame.MeshPath!) : null;
        Assert.NotNull(texture);
        Assert.NotNull(mesh);
        var item = new VfxPlaybackItem(one, Matrix4x4.Identity, new[] { texture }, new StaticMeshData?[] { mesh });

        // Looking at the flame's face, from +Z; the flame is ~265 x 128 units in XY (scale 125/30 x 0.7).
        var eye = new Vector3(0f, 64f, 520f);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, 64f, 0f), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, 5f, 20000f);
        var mirroredView = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;
        const int Size = 320;
        var settings = new PreviewSettings
        {
            SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
            AlphaBlend = true, DepthTest = true, MirrorX = true, TransposeMatrices = true,
            CullBackFaces = false, SortByPipeline = false,
            ClearColor = new Vector4(0f, 0f, 0f, 1f), TimeSeconds = 3.5f,
        };

        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return;   // no device: nothing to measure
        var driver = new D3D11MapParticles(renderer, cache);
        driver.SetPlayback(new VfxPlayback(new[] { item }));
        // 3.5 s: the first flame (born near 2 s, rate 0.5) is 1.5 s into its 5 s life - fully faded in
        for (int i = 0; i < 105; i++) driver.Tick(1f / 30f, mirroredView, mirroredView * proj, eye, 520f);
        Assert.Equal(1, driver.RiotMeshEmitters);
        var mat = driver.Materials.Single(m => m.RiotMeshGeometryId is not null);
        Assert.NotNull(mat.MeshUvLayer);

        (int W, int H) Footprint()
        {
            var frame = renderer.RenderFrame(Size, Size, settings, out _)!;
            int x0 = Size, x1 = -1, y0 = Size, y1 = -1;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int k = (y * Size + x) * 4;
                    if (frame[k] + frame[k + 1] + frame[k + 2] <= 60) continue;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                }
            return x1 < 0 ? (0, 0) : (x1 - x0 + 1, y1 - y0 + 1);
        }

        // the pre-M786 draw first: the per-material constant TickMeshSlices wrote, untouched
        var layer = mat.MeshUvLayer;
        mat.MeshUvLayer = null;
        var before = Footprint();
        mat.MeshUvLayer = layer;
        var after = Footprint();
        driver.StopAll();

        Assert.True(before.W > 0 && after.W > 0, $"nothing drawn: before {before}, after {after}");
        Assert.True(before.W > 2 * before.H, $"the unturned band should be a wide strip, got {before}");
        Assert.True(after.H > after.W, $"the turned flame should stand upright, got {after}");
    }
}

using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M796. Map22 dawnbringernightbringer: with Play All on, the board's ground vanished under a sky bowl, a
/// city ring and cloud meshes that the level places BELOW the board.
///
/// <para>Every board material there is a DefaultEnv_Flat alpha blend (6/7) with the default writeMask 31.
/// The editor draws blended map passes without depth (M279, deliberately kinder for decals), so particles
/// drawn afterwards were never depth-rejected by the board. The client writes depth for such a pass
/// (M557/M558) - its writeMask keeps bit 4 - so there the board hides whatever lies beneath it. The D3D11
/// renderer now replays exactly those passes (<see cref="PreviewMaterial.ClientWritesDepth"/>) depth-only
/// before the first particle draw.</para>
///
/// <para>Real map data and the real D3D11 path (Dx11SceneBuilder + D3D11MapParticles + ShaderPreviewRenderer),
/// with one synthetic opaque particle 600 units under the board. Skips (does not fail) without the game
/// install or a D3D11 device, like the other device tests here.</para>
/// </summary>
public sealed class MapClientDepthReplayTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string GeoPath = "data/maps/mapgeometry/map22/dawnbringernightbringer.mapgeo";
    private const string BinPath = "data/maps/mapgeometry/map22/dawnbringernightbringer.materials.bin";
    private const string L7Board = "Maps/KitPieces/TFT/Set15/Materials/Default/L7_DawnbringerNightbringer_Board_Mat";
    private const string StencilWriter = "Maps/KitPieces/TFT/Set15/Materials/Default/DawnbringerNightbringer_StencilWrite01";
    private const int W = 256, H = 256;

    private sealed record Scene(ShaderCacheReader Cache, Dx11SceneBuilder.PreparedScene Prepared) : IDisposable
    {
        public void Dispose() => Cache.Dispose();
    }

    private static Scene? Load()
    {
        string wadPath = Path.Combine(Final, "Maps", "Shipping", "Map22.wad.client");
        if (!File.Exists(wadPath)) return null;   // no game install on this machine
        var db = new HashSyncService().LoadLocal(_ => { });
        var resolver = new WadPathResolver(db);
        using var wad = WadArchive.Open(wadPath, resolver);
        string? BinName(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        string? WadPathOf(ulong h) => db.TryGetPath(h, out var p) ? p : null;
        byte[]? Read(ulong h) => wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;

        ulong geoHash = HashAlgorithms.WadPath(GeoPath), binHash = HashAlgorithms.WadPath(BinPath);
        if (!wad.TryGetEntry(geoHash, out _) || !wad.TryGetEntry(binHash, out _)) return null;   // the asset moved
        var matBin = wad.Extract(binHash);
        var map = MapGeoDecoder.Decode(wad.Extract(geoHash), ExtendedChannelRule.From(matBin, BinName));
        var doc = MaterialDocument.Parse(matBin, BinName, WadPathOf);
        var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) return null;
        var prepared = Dx11SceneBuilder.Prepare(cache, new ShaderPermutationIndex(Final), map, doc.Materials, Read,
            GeoPath, null, false);
        return new Scene(cache, prepared);
    }

    /// <summary>One opaque green quad (blend NONE), single particle, constant size. Green because the board's
    /// own texture is dark stone with red and orange lava - no board texel is pure green.</summary>
    private static VfxEmitterDefinition Under(float scale) => new(
        Name: "under_the_board",
        Rate: VfxCurveF.Const(1f),
        ParticleLifetime: VfxCurveF.Const(10f),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: true,
        Disabled: false,
        BlendMode: 3,
        BirthScale: VfxCurve3.Const(new Vector3(scale, scale, scale)),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(new Vector4(0f, 1f, 0f, 1f)),
        ColorOverLife: null,
        BirthVelocity: null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(Vector3.Zero),
        TexturePath: "ASSETS/Test/white.dds",
        TexDiv: Vector2.One,
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: false,
        Pass: 0,
        StencilMode: 0,
        StencilRef: -1);

    [Fact]
    public void TheBlendedBoardPassesAreTheOnesTheClientWritesDepthFor()
    {
        using var scene = Load();
        if (scene is null) return;
        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return;   // no D3D11 device on this machine
        Dx11SceneBuilder.Commit(renderer, scene.Prepared, "");

        // The board: blended, so the editor draws it without depth (M279) - and writeMask 31, so the client
        // writes it. Exactly the case the replay exists for.
        var board = renderer.Materials.Single(m => m.Name.Equals(L7Board, StringComparison.OrdinalIgnoreCase));
        Assert.False(board.WritesDepth);
        Assert.True(board.ClientWritesDepth);

        // The stage-transition stencil writer (writeMask 32, depthEnable false) writes no depth in either
        // renderer and must not be replayed.
        var writer = renderer.Materials.Single(m => m.Name.Equals(StencilWriter, StringComparison.OrdinalIgnoreCase));
        Assert.False(writer.WritesDepth);
        Assert.False(writer.ClientWritesDepth);
    }

    [Fact]
    public void AParticleUnderTheBlendedBoardIsHiddenByItAsInGame()
    {
        using var scene = Load();
        if (scene is null) return;
        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return;
        Dx11SceneBuilder.Commit(renderer, scene.Prepared, "");

        var eye = new Vector3(2000f, 3500f, 800f);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(2000f, 0f, 2000f), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, (float)W / H, 10f, 20000f);
        var settings = new PreviewSettings
        {
            SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
            AlphaBlend = true, DepthTest = true, MirrorX = false, CullBackFaces = false,
            SortByPipeline = true,   // the editor's default (Game Depth off) - blended map passes in the tail
            Bloom = false, Shadows = false, ClearColor = Vector4.Zero,
        };
        byte[] Frame() => renderer.RenderFrame(W, H, settings, out _, new List<string>())!.ToArray();

        // The board's own pixels: the board materials alone against the clear colour.
        var shown = renderer.Materials.Select(m => (m, m.Visible)).ToList();
        foreach (var (m, _) in shown) m.Visible = m.Name.Contains("Board_Mat", StringComparison.OrdinalIgnoreCase);
        var boardOnly = Frame();
        foreach (var (m, v) in shown) m.Visible = v;
        var mask = new bool[W * H];
        int maskPixels = 0;
        for (int i = 0; i < W * H; i++)
            if (boardOnly[i * 4] + boardOnly[i * 4 + 1] + boardOnly[i * 4 + 2] > 12) { mask[i] = true; maskPixels++; }
        Assert.True(maskPixels > W * H / 10, $"the board should fill a good part of the frame ({maskPixels} px)");

        var mapOnly = Frame();
        Assert.Equal(0, renderer.DepthReplayDraws);   // nothing but map geometry drew: no replay

        // The particle: one opaque green quad facing the camera, 600 units under the board's centre and wider
        // than the board on screen.
        var system = new VfxSystemDefinition(0xD796, "UnderTheBoard", "test/under", new[] { Under(6000f) });
        var white = new TextureImage(1, 1, new byte[] { 255, 255, 255, 255 });
        var item = new VfxPlaybackItem(system, Matrix4x4.CreateTranslation(2000f, -600f, 2000f), new[] { (TextureImage?)white });
        var driver = new D3D11MapParticles(renderer, scene.Cache);
        driver.SetPlayback(new VfxPlayback(new[] { item }));
        for (int i = 0; i < 5; i++) driver.Tick(1f / 30f, view, view * proj, eye, 1000f);

        int GreenOnBoard(byte[] bgra)
        {
            int n = 0;
            for (int i = 0; i < W * H; i++)
                if (mask[i] && bgra[i * 4 + 1] > 200 && bgra[i * 4] < 60 && bgra[i * 4 + 2] < 60) n++;
            return n;
        }

        // Before M796 (no pass flagged, so the replay never runs): the particle is painted over the board.
        var flagged = renderer.Materials.Where(m => m.ClientWritesDepth).ToList();
        Assert.NotEmpty(flagged);
        foreach (var m in flagged) m.ClientWritesDepth = false;
        var before = Frame();
        Assert.Equal(0, renderer.DepthReplayDraws);
        foreach (var m in flagged) m.ClientWritesDepth = true;

        // M796: the blended board's depth is replayed ahead of the particle, which then fails the depth test
        // wherever the board is - the ground stays as it was without the particle.
        var after = Frame();
        Assert.True(renderer.DepthReplayDraws > 0, "the replay must actually run with a particle in the frame");
        driver.StopAll();

        Assert.Equal(0, GreenOnBoard(mapOnly));
        int coveredBefore = GreenOnBoard(before), coveredAfter = GreenOnBoard(after);
        Assert.True(coveredBefore > maskPixels / 2,
            $"without the replay the particle should cover most of the board ({coveredBefore} of {maskPixels} px)");
        Assert.True(coveredAfter < maskPixels / 50,
            $"with the replay the board should hide the particle beneath it ({coveredAfter} of {maskPixels} px green)");
    }
}

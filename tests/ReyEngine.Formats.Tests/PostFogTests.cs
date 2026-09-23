using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Environment;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M760: the screen fog - PostEffectOptions depth + height fog, read and written through the unnamed
/// MapGraphicsFeature 0x50db156b, and drawn by Riot's own gamma/postfog.ps over the finished frame.
/// </summary>
public sealed class PostFogTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string Map11 = Final + @"\Maps\Shipping\Map11.wad.client";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static byte[]? SummonersRiftMaterials()
    {
        if (!File.Exists(Map11) || Database.Value is not { } db) return null;
        using var archive = WadArchive.Open(Map11, new WadPathResolver(db));
        return archive.Extract(HashAlgorithms.WadPath("data/maps/mapgeometry/map11/base_srx.materials.bin"));
    }

    private static MapPostFog HeightOnly(Vector4 colour) => MapPostFog.Defaults with
    {
        HeightFog = MapPostFog.Defaults.HeightFog with { Enabled = true, Color = colour },
    };

    // ---------------------------------------------------------------- the maths blob 0 runs

    [Fact]
    public void A_fog_that_is_off_gets_a_zero_maximum_so_the_shader_draws_nothing()
    {
        var p = MapPostFog.ShaderParams(MapPostFog.Defaults);
        Assert.Equal(0f, p.DepthParams.X);
        Assert.Equal(0f, p.HeightParams.X);
        Assert.False(MapPostFog.Defaults.DrawsAnything);
    }

    [Fact]
    public void An_enabled_fog_carries_max_start_and_the_reciprocal_span()
    {
        var p = MapPostFog.ShaderParams(HeightOnly(Vector4.One)).HeightParams;
        Assert.Equal(new Vector4(1f, 300f, 1f / -400f, 0f), p);
    }

    [Theory]
    [InlineData(400f, 0f)]      // above Start: clear
    [InlineData(300f, 0f)]      // at Start
    [InlineData(0f, 0.75f)]     // ground level, with the defaults 300 .. -100
    [InlineData(-100f, 1f)]     // at End
    [InlineData(-5000f, 1f)]    // saturated below
    public void Height_fog_ramps_linearly_from_start_to_end(float y, float expected) =>
        Assert.Equal(expected, MapPostFog.Amount(HeightOnly(Vector4.One).HeightFog, y), 4);

    [Fact]
    public void Max_intensity_caps_the_ramp()
    {
        var f = MapPostFog.Defaults.DepthFog with { Enabled = true, MaxIntensity = 0.4f };
        Assert.Equal(0.4f, MapPostFog.Amount(f, 1e6f), 4);
        Assert.Equal(0f, MapPostFog.Amount(f, 1000f), 4);   // nearer than Start (5000)
    }

    // ---------------------------------------------------------------- the constant buffer

    [Fact]
    public void The_constants_put_the_inverse_view_projection_where_blob_0_reads_it()
    {
        var eye = new Vector3(100f, 900f, 700f);
        var vp = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY)
                 * Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1.3f, 10f, 20000f);
        var fog = MapPostFog.ShaderParams(HeightOnly(new Vector4(0.25f, 0.5f, 0.75f, 1f)));
        var b = ShaderPreviewRenderer.PostFogConstants(eye, vp, fog);
        float R(int at) => BitConverter.ToSingle(b, at);

        Assert.Equal(144, b.Length);
        Assert.Equal(eye, new Vector3(R(0), R(4), R(8)));

        // blob 0: world.x = dot(clip, cb0[1]) ... w = dot(clip, cb0[4]); world /= w
        var world = new Vector3(-250f, 40f, 310f);
        var clip = Vector4.Transform(new Vector4(world, 1f), vp);
        clip /= clip.W;
        Vector4 Reg(int i) => new(R(16 + i * 16), R(20 + i * 16), R(24 + i * 16), R(28 + i * 16));
        float w = Vector4.Dot(clip, Reg(3));
        var back = new Vector3(Vector4.Dot(clip, Reg(0)), Vector4.Dot(clip, Reg(1)), Vector4.Dot(clip, Reg(2))) / w;
        Assert.True(Vector3.Distance(world, back) < 0.05f, $"reconstructed {back}, expected {world}");

        Assert.Equal(fog.HeightParams, new Vector4(R(112), R(116), R(120), R(124)));
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), new Vector3(R(128), R(132), R(136)));
        Assert.Equal(0f, R(80));   // depth fog off: zero maximum
    }

    // ---------------------------------------------------------------- the bin

    [Fact]
    public void Summoners_Rift_ships_no_screen_fog()
    {
        var bin = SummonersRiftMaterials();
        if (bin is null) return;
        Assert.Null(MapPostFog.Extract(bin));
    }

    [Fact]
    public void All_defaults_on_a_map_without_the_component_leaves_the_bin_untouched()
    {
        var bin = SummonersRiftMaterials();
        if (bin is null) return;
        var written = MapPostFog.Write(bin, MapPostFog.Defaults, out _);
        Assert.Same(bin, written);
    }

    [Fact]
    public void Writing_adds_the_component_and_reads_back_exactly()
    {
        var bin = SummonersRiftMaterials();
        if (bin is null) return;
        var fog = new MapPostFog(
            new MapScreenFog(true, new Vector4(0.3f, 0.35f, 0.4f, 1f), 3000f, 9000f, 0.6f),
            new MapScreenFog(true, new Vector4(0.1f, 0.2f, 0.3f, 1f), 200f, -300f, 0.8f));
        var written = MapPostFog.Write(bin, fog, out var detail);
        Assert.NotNull(written);
        Assert.Contains("created", detail);
        Assert.Equal(fog, MapPostFog.Extract(written!));

        // the rest of the map is where it was
        Assert.Equal(MapSunProperties.Extract(bin), MapSunProperties.Extract(written!));

        // and a second edit updates in place rather than adding a second component
        var again = MapPostFog.Write(written!, fog with { HeightFog = fog.HeightFog with { Enabled = false } }, out var detail2);
        Assert.DoesNotContain("created", detail2);
        var tree = Meta.SafeBinTree.Parse(again!);
        int components = tree.Objects.Values
            .Select(o => o.Properties.GetValueOrDefault(HashAlgorithms.Fnv1a("components")))
            .OfType<LeagueToolkit.Core.Meta.Properties.BinTreeContainer>()
            .Sum(c => c.Elements.OfType<LeagueToolkit.Core.Meta.Properties.BinTreeStruct>().Count(e => e.ClassHash == MapPostFog.ComponentClass));
        Assert.Equal(1, components);
        Assert.False(MapPostFog.Extract(again!)!.HeightFog.Enabled);
    }

    [Fact]
    public void A_written_component_passes_the_shape_validator()
    {
        var bin = SummonersRiftMaterials();
        if (bin is null) return;
        var written = MapPostFog.Write(bin, HeightOnly(new Vector4(0.5f, 0.5f, 0.5f, 1f)), out _)!;
        var issues = Meta.ModShapeValidator.ValidateBin(Meta.SafeBinTree.Parse(written), written, _ => null);
        Assert.Empty(issues);
    }

    // ---------------------------------------------------------------- Riot's blob, on a device

    [Fact]
    public void The_empty_define_set_is_the_globals_variant()
    {
        if (!Directory.Exists(Final) || Database.Value is not { } db) return;
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(db), out _);
        if (cache is null) return;
        var blobs = Dx11SceneBuilder.LoadPostFogShaders(cache);
        Assert.NotNull(blobs);
        var ps = DxbcReflection.Parse(blobs![1]!);
        // blob 0 reads its colours from $Globals, at the offsets PostFogConstants writes; the light-region
        // variant has no HeightFogColor at all (it takes the colour from LightRegionInfo)
        var globals = ps.ConstantBuffers.Single(c => c.Name == "$Globals").Variables.ToDictionary(v => v.Name, v => v.Offset);
        Assert.Equal(0, globals["CameraPos"]);
        Assert.Equal(16, globals["WorldViewProjInverse"]);
        Assert.Equal(80, globals["DepthFogParams"]);
        Assert.Equal(96, globals["DepthFogColor"]);
        Assert.Equal(112, globals["HeightFogParams"]);
        Assert.Equal(128, globals["HeightFogColor"]);
        Assert.Contains(ps.Textures, t => t.Name.StartsWith("sDepthTexture", StringComparison.Ordinal));
    }

    [Fact]
    public void Riots_postfog_blob_fogs_the_ground_and_leaves_the_sky()
    {
        if (!Directory.Exists(Final) || Database.Value is not { } db) return;
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(db), out _);
        if (cache is null || Dx11SceneBuilder.LoadPostFogShaders(cache) is not { } blobs) return;

        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return;   // no D3D11 device here
        string Log() => string.Join(" | ", renderer.Diagnostics.TakeLast(8));
        renderer.SetMesh(PreviewGeometry.CreateBuiltIn("Sphere"));
        Assert.True(renderer.SetBackdrop(new BackdropScene(Ground(), new[]
        {
            new BackdropSubmesh(0, 6, null, null, null, null, null, null,
                CompositeGround: false, AlphaMode: 0, AlphaCutoff: 0.25f, ClampUv: false),
        })), Log());
        renderer.SetPostFogShaders(blobs[0], blobs[1]);
        var lit = BackdropLighting.Resolve(false, 0.55f, 4f, NvrSunSettings.BuiltIn("Map8")!, useMapSun: true);
        renderer.SetBackdropFrame(MeshPreviewViewModel.ResolveBackdropFrame(true, Matrix4x4.Identity, lit,
            new System.Collections.Generic.List<PointLight>(), lightsEnabled: false, lightIntensity: 1f));

        var eye = new Vector3(0f, 900f, 900f);
        PreviewSettings Settings((Vector4, Vector3, Vector4, Vector3)? fog) => new()
        {
            SuppliedView = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY),
            SuppliedProjection = Matrix4x4.CreatePerspectiveFieldOfView(0.6f, 1f, 10f, 10000f),
            SuppliedCameraPosition = eye,
            MirrorX = true, DepthTest = true, AlphaBlend = true, TransposeMatrices = true,
            Bloom = false, Shadows = false, ScreenFog = fog,
        };
        var plain = renderer.RenderFrame(64, 64, Settings(null), out var e1)?.ToArray();   // the renderer reuses its buffer
        Assert.True(plain is not null, $"{e1}: {Log()}");
        Assert.Equal(0, renderer.PostFogPasses);

        // grey, so the check does not depend on the target's channel order; the ground is at y = 0, where
        // the default 300 .. -100 ramp reads 0.75
        var fogged = renderer.RenderFrame(64, 64, Settings(MapPostFog.ShaderParams(HeightOnly(new Vector4(0.5f, 0.5f, 0.5f, 1f)))), out var e2)?.ToArray();
        Assert.True(fogged is not null, $"{e2}: {Log()}");
        Assert.True(renderer.PostFogPasses == 1, $"the post-fog pass did not run: {Log()}");

        int centre = (32 * 64 + 32) * 4, corner = 0;
        Assert.True(renderer.BackdropDraws > 0, Log());
        for (int c = 0; c < 3; c++)
        {
            double expected = plain![centre + c] * 0.25 + 127.5 * 0.75;
            Assert.True(Math.Abs(fogged![centre + c] - expected) <= 3,
                $"channel {c}: {fogged[centre + c]}, expected {expected:0.0} from {plain[centre + c]}");
            Assert.Equal(plain[corner + c], fogged[corner + c]);   // sky: depth 1 passes through
        }
    }

    /// <summary>A ground quad at y = 0 that covers the middle of the frame and not its corners.</summary>
    private static MeshAsset Ground() => new()
    {
        Positions = new float[] { -300f, 0f, -300f, 300f, 0f, -300f, 300f, 0f, 300f, -300f, 0f, 300f },
        Normals = new float[] { 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f },
        Uvs = new float[8],
        Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
        SubMeshes = new[] { new SubMeshInfo("ground", 0, 6, 4) },
        VertexCount = 4,
    };
}

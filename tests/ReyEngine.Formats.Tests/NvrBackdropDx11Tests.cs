using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M727: the Character Viewer's legacy map backdrop under Direct3D 11.
///
/// <para>It drew WHITE. Two bugs made that: SetBackground assigned the mesh FIRST, and the mesh's change hook
/// rebuilt the D3D11 prop while the textures and map name were still unset; and the prop was cached on the mesh
/// alone, so it never rebuilt when they arrived. The session log named it - every backdrop material registered as
/// "prop:backdrop|?", the "?" being the unset map name. The M725 prop was also only ever diffuse-only; this
/// milestone replaces it with a real pass lit by the GL viewport's own recipe.</para>
/// </summary>
public sealed class NvrBackdropDx11Tests
{
    // ===================================================== the lighting, resolved once for both renderers (M729)

    [Fact]
    public void AMapWithACompositeAtlasGetsTheDimNightSunFromAbove()
    {
        // M142.6's night sun, for the LM_ and decal meshes the composite model leaves unlit. M729: GL's SetSunLighting
        // takes the direction TOWARD the sun and was handed the direction light travels, so GL lit these meshes from
        // underneath while D3D11 lit them from above. One resolver now, and it points up.
        var lit = BackdropLighting.Resolve(compositeModel: true, brightness: 1.1f, vertexLight: 3f, sun: null, useMapSun: true);
        var f = MeshPreviewViewModel.ResolveBackdropFrame(true, Matrix4x4.Identity, lit,
            lights: null, lightsEnabled: false, lightIntensity: 8f);

        Assert.True(f.CompositeModel);
        Assert.True(f.DirectionToSun.Y > 0.8f, $"the night sun must shine from above, got {f.DirectionToSun}");
        AssertNear(new Vector3(0.60f, 0.60f, 0.72f), f.SunColor);   // k = 2
        AssertNear(new Vector3(0.40f, 0.42f, 0.52f), f.SkyColor);
        Assert.Equal(0f, f.VertexBakedScale);   // the two models never run together
    }

    [Fact]
    public void AMaskBlendMapWithNoAuthoredSunGetsWhatGlAlreadyDrewAndBrightnessNowMovesIt()
    {
        // GL's SetSunLighting(Vector3.Zero, ...) never meant "no sun": it substitutes a sun of 0.75 and a sky of 0.35
        // shining from (0.4, 0.85, 0.45) and drops the colours it was handed, so the Bright slider was inert there.
        // M727 read the zero literally and drew D3D11 flat. At the neutral 0.55 this is GL's old picture exactly.
        var neutral = BackdropLighting.Resolve(false, 0.55f, 0f, sun: null, useMapSun: true);
        AssertNear(Vector3.Normalize(new Vector3(0.4f, 0.85f, 0.45f)), neutral.DirectionToSun);
        AssertNear(new Vector3(0.75f), neutral.SunColor);
        AssertNear(new Vector3(0.35f), neutral.SkyColor);

        var brighter = BackdropLighting.Resolve(false, 1.1f, 0f, sun: null, useMapSun: true);
        AssertNear(new Vector3(1.5f), brighter.SunColor);
        AssertNear(new Vector3(0.7f), brighter.SkyColor);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");

    // ===================================================== the white bug, pinned where it lived

    [Fact]
    public void TheMeshIsAssignedLastSoEverythingThePropReadsIsAlreadyThere()
    {
        string? vm = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.cs");
        if (vm is null) return;

        int body = vm.IndexOf("public void SetBackground(", StringComparison.Ordinal);
        Assert.True(body > 0);
        int textures = vm.IndexOf("BackgroundTextures = bg?.SubmeshTextures;", body, StringComparison.Ordinal);
        int name = vm.IndexOf("BackgroundMapName = bg?.MapName;", body, StringComparison.Ordinal);
        int mesh = vm.IndexOf("BackgroundMesh = bg?.Mesh;", body, StringComparison.Ordinal);
        Assert.True(textures > 0 && name > 0 && mesh > 0, "all three assignments must still exist");
        // assigning the mesh fires the rebuild; anything assigned after it is invisible to that rebuild
        Assert.True(mesh > textures && mesh > name,
            "BackgroundMesh is assigned before the textures or the map name - the D3D11 backdrop builds white again");
    }

    [Fact]
    public void BothBackdropCachesAreKeyedOnTheTexturesNotJustTheMesh()
    {
        string? arena = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.Arena.cs");
        string? backdrop = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.Backdrop.cs");
        if (arena is null || backdrop is null) return;

        Assert.Contains("ReferenceEquals(_backdropPropTexturesFor, tex)", arena);
        Assert.Contains("ReferenceEquals(_dx11BackdropTexturesFor, textures)", backdrop);
    }

    // ===================================================== the pass and its wiring

    [Fact]
    public void TheBackdropDrawsAfterTheSkyAndBeforeTheSceneAndIsDisposed()
    {
        string? r = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        if (r is null) return;

        int sky = r.IndexOf("DrawSky(view, proj);", StringComparison.Ordinal);
        int backdrop = r.IndexOf("DrawBackdrop(view, proj);", StringComparison.Ordinal);
        int scene = r.IndexOf("foreach (var drawIndex in _drawOrder)", StringComparison.Ordinal);
        Assert.True(sky > 0 && backdrop > 0 && scene > 0);
        Assert.True(sky < backdrop && backdrop < scene, "the backdrop must draw after the sky and before the scene pass");
        Assert.Contains("DisposeBackdrop();", r);
        // IsReady needs a material and the backdrop owns none, so RenderFrame's own gate must count the backdrop -
        // or a subject with no D3D11 scene refuses every frame and takes the map with it. The device-gated pixel
        // test below is what caught this; this line keeps it caught on a machine with no device.
        Assert.Contains("(_materials.Count == 0 && !HasBackdrop)", r);
    }

    [Fact]
    public void TheDiffuseOnlyPropIsOnlyAFallbackSoTheMapIsNeverDrawnTwice()
    {
        string? arena = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.Arena.cs");
        string? window = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
        if (arena is null || window is null) return;

        Assert.Contains("UseDx11Preview && Dx11BackdropFailed && BackgroundVisible && BackdropProp() is { } backdrop", arena);
        Assert.Contains("&& Dx11BackdropFailed;", arena);   // the card's diffuse-only note tells the truth
        Assert.Contains("vm.Dx11BackdropFailed = _dx11.BackdropFailed;", window);
        Assert.Contains("_dx11.Backdrop = vm.Dx11Backdrop;", window);
        Assert.Contains("_dx11.BackdropLighting = vm.Dx11BackdropFrame();", window);
    }

    [Fact]
    public void TheBackdropShaderIsAsciiAndSamplesOutsideFlowControl()
    {
        string? pass = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.Backdrop.cs");
        if (pass is null) return;

        int start = pass.IndexOf("private const string BackdropHlsl = @\"", StringComparison.Ordinal);
        int end = pass.IndexOf("}\";", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        string hlsl = pass.Substring(start, end - start);
        // M117b: one non-ASCII byte in a shader string builds fine and then corrupts what the compiler sees
        Assert.DoesNotContain(hlsl, c => c > 127);

        // every Sample happens before the first branch: a gradient op inside flow control is a compile hazard
        int firstIf = hlsl.IndexOf("if (", hlsl.IndexOf("float4 psmain", StringComparison.Ordinal), StringComparison.Ordinal);
        int lastSample = hlsl.LastIndexOf(".Sample(", StringComparison.Ordinal);
        Assert.True(firstIf > 0 && lastSample > 0 && lastSample < firstIf, "a texture is sampled inside a branch");
    }

    // ===================================================== the picture, on the real packs

    [Theory]
    [InlineData("Map8")]
    [InlineData("Map10")]
    public void TheRealPackDrawsTexturedAndLitRatherThanWhite(string map)
    {
        string dir = map == "Map8" ? SetupService.Map8InstallDir : SetupService.Map10InstallDir;
        if (!SetupService.HasNvr(dir)) return;   // the pack is not installed on this machine

        var bg = MapPreviewLoader.Load(dir);
        var scene = MeshPreviewViewModel.BuildBackdropScene(bg.Mesh, bg.SubmeshTextures, bg.SubmeshMask, bg.SubmeshBlend,
            bg.SubmeshColor1, bg.SubmeshColor2, bg.SubmeshColor3, bg.SubmeshLightmap, bg.SubmeshMaterials);
        Assert.Equal(bg.Mesh.SubMeshes.Count, scene.Submeshes.Count);

        // the window's own placement and composition: translate, then rotate about Y (ViewportControl.BackgroundModel)
        var (ox, oy, oz, rot) = MeshPreviewViewModel.BackdropPlacement(bg.MapName);
        var world = Matrix4x4.CreateTranslation((float)ox, (float)oy, (float)oz)
                    * Matrix4x4.CreateRotationY((float)(rot * Math.PI / 180.0));
        bool composite = bg.SubmeshLightmap is not null;
        // M729: the window's own defaults for this map and the sun it resolves - for Dominion the built-in copy of the
        // level's terrain.inibin, since its pack ships without one.
        var sun = MeshPreviewViewModel.BackdropSunFor(bg);
        var (lightsOn, vertexLight) = BackdropLighting.Defaults(composite, sun is not null, bg.Lights.Count);
        var lit = BackdropLighting.Resolve(composite, BackdropLighting.NeutralBrightness, (float)vertexLight, sun, useMapSun: true);
        var frame = MeshPreviewViewModel.ResolveBackdropFrame(true, world, lit, bg.Lights, lightsOn, lightIntensity: 8f);

        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return;   // no D3D11 device here
        string Log() => string.Join(" | ", renderer.Diagnostics.TakeLast(8));

        // RenderFrame refuses a frame with no static mesh at all ("no mesh set"). The surface starts from the
        // same built-in sphere; with no materials registered it draws nothing, so every pixel below is backdrop.
        renderer.SetMesh(PreviewGeometry.CreateBuiltIn("Sphere"));
        Assert.True(renderer.SetBackdrop(scene), $"the backdrop pipeline could not be built: {Log()}");
        Assert.True(renderer.HasBackdrop, $"the backdrop uploaded no draws: {Log()}");
        renderer.SetBackdropFrame(frame);

        const int Size = 512;
        var eye = new Vector3(0f, 1400f, 1600f);
        var s = new PreviewSettings
        {
            SuppliedView = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY),
            SuppliedProjection = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, 10f, 60000f),
            SuppliedCameraPosition = eye,
            MirrorX = true, DepthTest = true, AlphaBlend = true, TransposeMatrices = true,
            Bloom = false, Shadows = false,
            ClearColor = new Vector4(0.039f, 0.051f, 0.075f, 1f),
        };
        var px = renderer.RenderFrame(Size, Size, s, out var error);
        Assert.True(px is not null, $"{error ?? "no frame"}: {Log()}");
        Assert.True(renderer.BackdropDraws > 0, $"the backdrop pass drew nothing: {Log()}");

        long covered = 0, white = 0;
        double lum = 0;
        for (int i = 0; i + 3 < Size * Size * 4; i += 4)
        {
            int b = px![i], g = px[i + 1], r = px[i + 2];
            if (Math.Abs(r - 10) <= 3 && Math.Abs(g - 13) <= 3 && Math.Abs(b - 19) <= 3) continue;   // the clear colour
            covered++;
            if (r >= 240 && g >= 240 && b >= 240) white++;
            lum += 0.299 * r + 0.587 * g + 0.114 * b;
        }
        double coverage = covered / (double)(Size * Size);
        double whiteShare = covered > 0 ? white / (double)covered : 1.0;
        double meanLum = covered > 0 ? lum / covered : 0.0;

        // System.Environment, qualified: inside this namespace a bare Environment is ReyEngine.Formats.Environment.
        if (System.Environment.GetEnvironmentVariable("REYENGINE_DUMP_BACKDROP") is { Length: > 0 })
            WritePng(Path.Combine(Path.GetTempPath(), $"reyengine-backdrop-{map}.png"), px, Size, Size);

        Assert.True(coverage > 0.25, $"{map}: the backdrop covers only {coverage:P1} of the frame");
        // the bug this milestone fixed painted every backdrop pixel white
        Assert.True(whiteShare < 0.10, $"{map}: {whiteShare:P1} of the backdrop is white");
        Assert.True(meanLum > 3 && meanLum < 220, $"{map}: mean luminance {meanLum:0.0} is not a lit, textured map");
    }

    // ===================================================== helpers

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) continue;
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        return null;
    }

    /// <summary>A minimal RGBA PNG, so a failing frame can be LOOKED at. Written only on request.</summary>
    private static void WritePng(string path, byte[] bgra, int w, int h)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)w);
        BigEndian(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 6;   // 8-bit RGBA
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(raw, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[w * 4 + 1];
            for (int y = 0; y < h; y++)
            {
                row[0] = 0;
                for (int x = 0; x < w; x++)
                {
                    int src = (y * w + x) * 4, dst = 1 + x * 4;
                    row[dst] = bgra[src + 2]; row[dst + 1] = bgra[src + 1]; row[dst + 2] = bgra[src]; row[dst + 3] = 255;
                }
                z.Write(row);
            }
        }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void BigEndian(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BigEndian(len, 0, (uint)data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint c = 0xFFFFFFFF;
        foreach (var buf in new[] { t, data })
            foreach (byte x in buf)
            {
                c ^= x;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
        var crc = new byte[4];
        BigEndian(crc, 0, c ^ 0xFFFFFFFF);
        s.Write(crc);
    }
}

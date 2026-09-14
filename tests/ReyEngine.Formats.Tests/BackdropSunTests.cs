using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Environment;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.Meshes;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M729: the Character Viewer backdrop's sun and point lights. Reported: not all point lights show, and the sun and
/// sky are not the map's own light settings. Both were true.
///
/// <para><b>Lights.</b> The D3D11 pass capped the list at 256. Dominion's Light.dat has 462, so 206 torches never lit
/// anything there; GL takes 1024.</para>
///
/// <para><b>Sun.</b> Nothing lit the backdrop with the level's authored sun: the packs ship without terrain.inibin, and
/// GL's SetSunLighting - which takes the direction TOWARD the sun, and treats a zero direction as "use my default sun"
/// - was handed a zero for Dominion and the travel direction for Twisted Treeline's night sun. D3D11's M727 copy of the
/// recipe read both literally, so the two renderers did not even agree with each other.</para>
///
/// <para><b>What the game does</b> (the 4.20 client's DATA/Shaders/HLSL/Environment/LIT_VS and LIT_PS, whose .vs_2_0 and
/// .ps_2_0 files are plain source): on a level with no colour map, lighting = saturate(-dot(SunDir, N)) *
/// DIRECTIONAL_LIGHT_COLOR * 2 + vColor * 4 + AMBIENT_COLOR. There is no point-light term at all. The torch pools are
/// baked into the vertex colours: of Dominion's 1,027,174 vertices, the 267,279 outside every Light.dat radius average
/// a colour of 0.000, and vertex luminance tracks summed light reach at r = 0.635.</para>
/// </summary>
public sealed class BackdropSunTests
{
    private const string LegacyLevels = @"K:\LeagueSandbox\League_Sandbox_Client\LEVELS";

    private static NvrSunSettings Dominion => NvrSunSettings.BuiltIn("Map8")!;

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

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    // ===================================================== the sun Dominion shipped with

    [Fact]
    public void DominionIsLitTheWayTheClientsOwnEnvironmentShaderLightsIt()
    {
        var lit = BackdropLighting.Resolve(compositeModel: false, brightness: BackdropLighting.NeutralBrightness,
            vertexLight: (float)BackdropLighting.LegacyVertexLight, sun: Dominion, useMapSun: true);

        Assert.False(lit.CompositeModel);
        AssertNear(-Dominion.SunDirection, lit.DirectionToSun);   // terrain.inibin's SunDir is the way light TRAVELS
        Assert.True(lit.DirectionToSun.Y > 0.8f, $"Dominion's sun is high overhead, got {lit.DirectionToSun}");
        AssertNear(new Vector3(155f, 125f, 83f) / 255f * 2f, lit.SunColor);   // LIT_VS doubles DIRECTIONAL_LIGHT_COLOR
        AssertNear(new Vector3(7f, 31f, 68f) / 255f, lit.SkyColor);           // LIT_PS adds AMBIENT_COLOR once
        Assert.Equal(4f, lit.VertexBakedScale);                                 // LIT_PS: m_Color1.rgb * 4
    }

    [Fact]
    public void BrightnessScalesTheAuthoredSunAndAmbientTogether()
    {
        var neutral = BackdropLighting.Resolve(false, 0.55f, 4f, Dominion, useMapSun: true);
        var doubled = BackdropLighting.Resolve(false, 1.1f, 4f, Dominion, useMapSun: true);
        AssertNear(neutral.SunColor * 2f, doubled.SunColor);
        AssertNear(neutral.SkyColor * 2f, doubled.SkyColor);
        AssertNear(neutral.DirectionToSun, doubled.DirectionToSun);
    }

    [Fact]
    public void TurningTheMapsSunOffFallsBackToTheReferenceSun()
    {
        var off = BackdropLighting.Resolve(false, 0.55f, 4f, Dominion, useMapSun: false);
        var none = BackdropLighting.Resolve(false, 0.55f, 4f, sun: null, useMapSun: true);
        Assert.Equal(none, off);
    }

    [Fact]
    public void NoModelEverHandsGlAZeroSunDirection()
    {
        // GL's SetSunLighting swaps in its own default sun for a zero direction and drops the colours passed with it -
        // which is how Dominion's Bright slider came to do nothing on GL. So no branch may produce one.
        foreach (bool composite in new[] { false, true })
            foreach (var sun in new[] { null, Dominion })
                foreach (bool use in new[] { false, true })
                {
                    var lit = BackdropLighting.Resolve(composite, 0.55f, 4f, sun, use);
                    Assert.True(MathF.Abs(lit.DirectionToSun.Length() - 1f) < 1e-3f,
                        $"composite={composite} sun={sun is not null} use={use}: {lit.DirectionToSun} is not a unit vector");
                }
    }

    [Fact]
    public void TheBuiltInDominionSunIsTheLevelsOwnTerrainInibin()
    {
        // The packs ship without terrain.inibin, so the built-in copy is what Dominion is actually lit by. Hold it to the
        // client's own file wherever that file is on disk.
        string folder = Path.Combine(LegacyLevels, "Map8");
        if (!File.Exists(Path.Combine(folder, "terrain.inibin"))) return;

        var file = NvrSunSettings.TryLoad(folder);
        Assert.NotNull(file);
        Assert.Equal("terrain.inibin", file!.Source);
        AssertNear(file.SunDirection, Dominion.SunDirection);
        AssertNear(file.SunColor, Dominion.SunColor);
        AssertNear(file.AmbientColor, Dominion.AmbientColor);
    }

    [Fact]
    public void OnlyDominionHasABuiltInSun()
    {
        Assert.NotNull(NvrSunSettings.BuiltIn("map8"));   // folder names are not case-reliable
        Assert.Null(NvrSunSettings.BuiltIn("Map10"));     // its environment shader reads a colour map, not a sun
        Assert.Null(NvrSunSettings.BuiltIn(null));
    }

    // ===================================================== one recipe, both renderers

    [Fact]
    public void BothRenderersTakeTheirBackdropLightFromTheOneResolver()
    {
        string? gl = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        string? dx = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.Backdrop.cs");
        string? window = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        if (gl is null || dx is null || window is null) return;

        Assert.Contains("Services.BackdropLighting.Resolve(m142, bright, (float)BackgroundVertexLight, BackgroundSun, BackgroundUseMapSun)", gl);
        Assert.Contains("bg.SetSunLighting(lit.DirectionToSun,", gl);
        // the two GL calls that did not do what their arguments said
        Assert.DoesNotContain("bg.SetSunLighting(Vector3.Zero", gl);
        Assert.DoesNotContain("bg.SetSunLighting(new Vector3(-0.3f, -0.85f, -0.4f)", gl);
        Assert.Contains("Services.BackdropLighting.Resolve(", dx);
        Assert.Contains("BackgroundSun=\"{Binding BackgroundSun}\"", window);
        Assert.Contains("BackgroundUseMapSun=\"{Binding BackgroundUseMapSun}\"", window);
    }

    // ===================================================== the lights, and the defaults around them

    [Fact]
    public void ThePoolsStartOffWhereTheMapAlreadyBakesThem()
    {
        Assert.Equal((false, 0d), BackdropLighting.Defaults(compositeModel: true, hasSun: false, lightCount: 95));    // Twisted Treeline
        Assert.Equal((false, 4d), BackdropLighting.Defaults(compositeModel: false, hasSun: true, lightCount: 462));   // Dominion
        Assert.Equal((true, 0d), BackdropLighting.Defaults(compositeModel: false, hasSun: false, lightCount: 30));    // a level with neither
        Assert.Equal((false, 0d), BackdropLighting.Defaults(compositeModel: false, hasSun: false, lightCount: 0));
    }

    [Fact]
    public void ABackdropsLightingDefaultsApplyOncePerMapSoAToggleSurvivesTheNextSkin()
    {
        var vm = new MeshPreviewViewModel();
        var dominion = Backdrop("Map8", composite: false);

        vm.SetBackground(dominion);
        Assert.NotNull(vm.BackgroundSun);            // the built-in copy: this pack has no terrain.inibin
        Assert.True(vm.HasBackgroundSun);
        Assert.False(vm.BackgroundLightsEnabled);
        Assert.Equal(4d, vm.BackgroundVertexLight);

        vm.BackgroundLightsEnabled = true;           // the user ticks the runtime lights on...
        vm.SetBackground(dominion);                  // ...and the next skin reloads the same backdrop
        Assert.True(vm.BackgroundLightsEnabled, "reloading the same map threw the user's Light.dat toggle away");

        vm.SetBackground(Backdrop("Map10", composite: true));   // a different map gets its own defaults
        Assert.Null(vm.BackgroundSun);
        Assert.False(vm.BackgroundLightsEnabled);
        Assert.Equal(0d, vm.BackgroundVertexLight);
    }

    [Fact]
    public void D3D11TakesAsManyBackdropLightsAsGl()
    {
        Assert.Equal(1024, ShaderPreviewRenderer.MaxBackdropLights);

        string? gl = Source("src", "ReyEngine.Rendering", "ViewportMeshRenderer.cs");
        string? pass = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.Backdrop.cs");
        if (gl is null || pass is null) return;
        Assert.Contains("System.Math.Min(lights?.Count ?? 0, 1024)", gl);   // GL's upload
        Assert.Contains("for (int i = 0; i < 1024; i++)", gl);              // GL's shader loop
        // the HLSL arrays are sized from the constant, never from a second literal that can drift from it
        Assert.Contains("gLightPosRadius[MAX_BACKDROP_LIGHTS]", pass);
        Assert.Contains("gLightColorStrength[MAX_BACKDROP_LIGHTS]", pass);
        Assert.Contains(".Replace(\"MAX_BACKDROP_LIGHTS\"", pass);
    }

    [Theory]
    [InlineData(462, 0)]     // Dominion's Light.dat: 206 of these never lit anything at the old cap of 256
    [InlineData(1030, 6)]    // past GL's bound as well: truncated, and the truncation is reported
    public void TheD3D11PassUploadsEveryLightUpToGlsBound(int count, int dropped)
    {
        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return;   // no D3D11 device here
        string Log() => string.Join(" | ", renderer.Diagnostics.TakeLast(8));

        var scene = new BackdropScene(Triangle(), new[]
        {
            new BackdropSubmesh(0, 3, null, null, null, null, null, null,
                CompositeGround: false, AlphaMode: 0, AlphaCutoff: 0.25f, ClampUv: false),
        });
        var lights = Enumerable.Range(0, count)
            .Select(i => new PointLight(new Vector3(i % 40 * 20f - 400f, 50f, i / 40 * 20f - 400f), new Vector3(1f, 0.5f, 0.2f), 120f))
            .ToList();
        var lit = BackdropLighting.Resolve(false, 0.55f, 4f, Dominion, useMapSun: true);

        // RenderFrame refuses a frame with no static mesh at all; the built-in sphere with no materials draws nothing.
        renderer.SetMesh(PreviewGeometry.CreateBuiltIn("Sphere"));
        Assert.True(renderer.SetBackdrop(scene), $"the backdrop pipeline could not be built: {Log()}");
        renderer.SetBackdropFrame(MeshPreviewViewModel.ResolveBackdropFrame(true, Matrix4x4.Identity, lit, lights,
            lightsEnabled: true, lightIntensity: 1f));

        var eye = new Vector3(0f, 900f, 900f);
        var settings = new PreviewSettings
        {
            SuppliedView = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY),
            SuppliedProjection = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, 10f, 10000f),
            SuppliedCameraPosition = eye,
            MirrorX = true, DepthTest = true, AlphaBlend = true, TransposeMatrices = true,
            Bloom = false, Shadows = false,
        };
        var px = renderer.RenderFrame(64, 64, settings, out var error);
        Assert.True(px is not null, $"{error ?? "no frame"}: {Log()}");
        Assert.True(renderer.BackdropDraws > 0, $"the backdrop pass drew nothing - did the shader compile? {Log()}");
        Assert.Equal(dropped, renderer.BackdropLightsDropped);
    }

    // ===================================================== helpers

    private static MeshAsset Triangle() => new()
    {
        Positions = new float[] { -500f, 0f, -500f, 500f, 0f, -500f, 0f, 0f, 500f },
        Normals = new float[] { 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f },
        Uvs = new float[6],
        Indices = new uint[] { 0, 1, 2 },
        SubMeshes = new[] { new SubMeshInfo("ground", 0, 3, 3) },
        VertexCount = 3,
    };

    private static MapPreviewBackground Backdrop(string name, bool composite)
    {
        var none = new TextureImage?[1];
        var lights = new List<PointLight> { new(new Vector3(0f, 50f, 0f), Vector3.One, 300f) };
        return new MapPreviewBackground(name, Triangle(), none, none, none, none, none, new[] { false }, lights,
            MeshCount: 1, MissingTextures: 0, SubmeshLightmap: composite ? new TextureImage?[1] : null);
    }
}

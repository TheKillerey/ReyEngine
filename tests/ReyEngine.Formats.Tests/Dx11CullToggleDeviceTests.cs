using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// The D3D11 "Cull Back Faces" toggle bug: <c>_raster</c> used to be built with
/// <c>CullMode = s.CullBackFaces ? Back : None</c>, and the draw-loop selection is
/// <c>s.CullBackFaces &amp;&amp; mat.CullBackFaces ? _rasterCull : _raster</c>. With the toggle ON, that made
/// _raster ALSO cull - so a two-sided material (mat.CullBackFaces false: every particle, since only the
/// map/character material builders ever set it true) got culled anyway, contradicting the M354/M540 comment
/// and GL's own <c>cullBackfaces &amp;&amp; !DoubleSided</c> rule. The fix pins <c>_raster</c> to
/// <c>CullMode.None</c> unconditionally and leaves the toggle's only job to gate which state the loop binds.
///
/// <para>Renders a hand-built, two-triangle, single-sided quad (no camera-facing billboarding - a real static
/// winding) through <see cref="D3D11MapParticles"/>' Riot mesh-shader path from BOTH sides with the global
/// toggle ON. A one-sided quad shows from only one of those two cameras; the emitter's own authored
/// <c>disableBackfaceCull</c> (mirrored as an inverse into <c>PreviewMaterial.CullBackFaces</c>) says it is
/// two-sided, so with the bug fixed it must show from both.</para>
/// </summary>
public sealed class Dx11CullToggleDeviceTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private static bool Installed => Directory.Exists(Final);
    private const int W = 200, H = 200;

    [Fact]
    public void ATwoSidedMeshEmitterDrawsFromBothSidesWithTheGlobalCullToggleOn()
    {
        if (!Installed) return;
        var database = new HashSyncService().LoadLocal(_ => { });
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(database), out _);
        if (cache is null) return;

        // A flat quad in the XY plane at Z=0, wound CCW as seen from +Z (positions[0..2] cross product points
        // toward +Z) - an arbitrary, fixed, non-billboarded winding. disableBackfaceCull = true authors the
        // TWO-SIDED intent (PreviewMaterial.CullBackFaces = !true = false), which is what this test is about;
        // it is not about which side happens to be the winding-front.
        float[] positions =
        {
            -100f, -100f, 0f,
             100f, -100f, 0f,
             100f,  100f, 0f,
            -100f, -100f, 0f,
             100f,  100f, 0f,
            -100f,  100f, 0f,
        };
        float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 0f, 1f, 1f, 0f, 1f };
        uint[] indices = { 0, 1, 2, 3, 4, 5 };
        var mesh = new StaticMeshData(positions, uvs, indices, "quad");

        var emitter = new VfxEmitterDefinition(
            Name: "twoSidedQuad", Rate: VfxCurveF.Const(1f), ParticleLifetime: VfxCurveF.Const(10f),
            EmitterLifetime: null, ParticleLinger: 0f, TimeBeforeFirstEmission: 0f, IsSingleParticle: true,
            Disabled: false, BlendMode: 3,   // NONE: opaque, so "covered" is a clean pixel count
            BirthScale: VfxCurve3.Const(Vector3.One), ScaleOverLife: null,
            BirthColor: VfxCurve4.Const(new Vector4(1f, 1f, 1f, 1f)), ColorOverLife: null,
            BirthVelocity: null, Acceleration: null, BirthRotationalVelocity: null,
            EmitterPosition: VfxCurve3.Const(Vector3.Zero),
            TexturePath: "ASSETS/Test/white.dds", TexDiv: Vector2.One, NumFrames: 1, RandomStartFrame: false,
            IsMeshPrimitive: true, MeshPath: "ASSETS/Test/quad.scb",
            DisableBackfaceCull: true);

        var system = new VfxSystemDefinition(0xFACE, "CullProbe", "test/cull", new[] { emitter });
        var texture = new TextureImage(1, 1, new byte[] { 255, 255, 255, 255 });
        var item = new VfxPlaybackItem(system, Matrix4x4.Identity,
            new TextureImage?[] { texture }, new StaticMeshData?[] { mesh });

        int CoveredFrom(Vector3 eye)
        {
            using var renderer = new ShaderPreviewRenderer();
            if (!renderer.Initialize(out _)) return -1;
            var driver = new D3D11MapParticles(renderer, cache);
            driver.SetPlayback(new VfxPlayback(new[] { item }));

            var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, (float)W / H, 1f, 5000f);
            var viewProj = view * proj;
            for (int i = 0; i < 5; i++) driver.Tick(1f / 30f, view, viewProj, eye, 1000f);

            var settings = new PreviewSettings
            {
                SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
                AlphaBlend = true, DepthTest = true, MirrorX = false,
                CullBackFaces = true,   // the global toggle THIS bug is about - ON
                SortByPipeline = false, Bloom = false, Shadows = false,
                ClearColor = Vector4.Zero,
            };
            var frame = renderer.RenderFrame(W, H, settings, out var error, new List<string>());
            driver.StopAll();
            if (frame is null) return -1;
            var bgra = frame.ToArray();
            int n = 0;
            for (int k = 0; k + 3 < bgra.Length; k += 4)
                if (bgra[k] > 200 && bgra[k + 1] > 200 && bgra[k + 2] > 200) n++;   // near-white
            return n;
        }

        int fromFront = CoveredFrom(new Vector3(0f, 0f, -500f));
        int fromBack = CoveredFrom(new Vector3(0f, 0f, 500f));
        Assert.True(fromFront >= 0 && fromBack >= 0, "device or shader cache unavailable");

        Assert.True(fromFront > 500, $"the quad's WINDING-FRONT side should show regardless of the bug ({fromFront}px)");
        // This is the assertion that actually distinguishes the bug from the fix: before it, the
        // winding-BACK side was rejected by _raster's own CullMode.Back whenever the toggle was on, even
        // though the emitter authored disableBackfaceCull (mat.CullBackFaces = false).
        Assert.True(fromBack > 500, $"the two-sided quad must show from its winding-back side too with the "
            + $"toggle on - disableBackfaceCull said two-sided ({fromBack}px)");
    }
}

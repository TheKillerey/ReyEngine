using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M804: a particle host with NO particle alive - between auto-stop cycles, or before a delayed emitter's first
/// spawn - still has a frame to draw: the floor grid, the force shapes and the Move handle.
///
/// <para><see cref="ShaderPreviewRenderer.RenderFrame"/> refused such a frame with "no mesh set" whenever it had
/// no static mesh: its material is registered, but its geometry is per frame and there was none this instant. The
/// Particle Editor's own surface never hit it - <c>Dx11ViewportSurface.Initialize</c> installs a fallback sphere,
/// measured on a real device and a real shader cache with the editor's window in its real order - but a BARE
/// renderer (a harness, a future particle-only host) refused every such frame, which reads as a fault and blanks
/// the picture. A registered particle material now counts as something to draw.</para>
///
/// <para>Needs a D3D11 device and the installed game's shader cache; on a machine without either these report
/// SKIPPED, like the other device tests here.</para>
/// </summary>
public sealed class ParticleOnlyFrameTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const int W = 320, H = 240;

    /// <summary>A visual emitter that does not spawn for a minute: a system that HAS something to draw and has
    /// nothing alive for the whole test.</summary>
    private static VfxPlayback DelayedPlayback()
    {
        var emitter = new VfxEmitterDefinition(
            Name: "late", Rate: VfxCurveF.Const(20f), ParticleLifetime: VfxCurveF.Const(1f),
            EmitterLifetime: null, ParticleLinger: 0f, TimeBeforeFirstEmission: 60f, IsSingleParticle: false,
            Disabled: false, BlendMode: 1,
            BirthScale: VfxCurve3.Const(new Vector3(60f)), ScaleOverLife: null,
            BirthColor: VfxCurve4.Const(Vector4.One), ColorOverLife: null,
            BirthVelocity: null, Acceleration: null, BirthRotationalVelocity: null,
            EmitterPosition: VfxCurve3.Const(Vector3.Zero),
            TexturePath: "ASSETS/Test/white.dds", TexDiv: Vector2.One, NumFrames: 1, RandomStartFrame: false,
            IsMeshPrimitive: false);
        var system = new VfxSystemDefinition(0xD804, "DelayedSystem", "test/m804", new[] { emitter });
        var texture = new TextureImage(1, 1, new byte[] { 255, 255, 255, 255 });
        return new VfxPlayback(new[]
        {
            new VfxPlaybackItem(system, Matrix4x4.Identity, new TextureImage?[] { texture }) { Seed = 804 },
        });
    }

    private sealed record Outcome(bool Rendered, string? Error, int LitPixels, int LiveParticles, int Slices,
        int Materials);

    /// <summary>One zero-particle frame through the real renderer and the real map-particle driver. Null when
    /// this machine has no device or no shader cache.</summary>
    private Outcome? ZeroParticleFrame(bool staticMesh, bool groundGrid, Action<IReadOnlyCollection<PreviewMaterial>>? tweak = null)
    {
        if (!Directory.Exists(Final)) { output.WriteLine("SKIPPED: no game install at " + Final); return null; }
        var database = new HashSyncService().LoadLocal(_ => { });
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(database), out _);
        if (cache is null) { output.WriteLine("SKIPPED: the game's shader cache did not open"); return null; }
        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out var why)) { output.WriteLine("SKIPPED: no D3D11 device here: " + why); return null; }

        // The one thing that differs between the editor's surface and a bare renderer: the fallback sphere.
        if (staticMesh) renderer.SetMesh(PreviewGeometry.CreateBuiltIn("Sphere"));

        // The Particle Editor's furniture, pushed the way ParticleEditorView.Dx11.cs StartDx11 pushes it.
        renderer.GroundGrid = groundGrid;
        renderer.SetGroundGridLines(ReyEngine.Rendering.GridRenderer.BuildGeometry(20, 100f, out _, out _));

        var eye = new Vector3(0f, 500f, 700f);
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, (float)W / H, 1f, 20000f);
        var mirrored = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;

        var driver = new D3D11MapParticles(renderer, cache);
        driver.SetPlayback(DelayedPlayback());
        for (int i = 0; i < 5; i++) driver.Tick(1f / 30f, mirrored, mirrored * proj, eye, 700f);
        tweak?.Invoke(driver.Materials);

        // The surface's own settings (Dx11ViewportSurface.Render): the editor camera, mirrored, transposed.
        var settings = new PreviewSettings
        {
            SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
            AlphaBlend = true, DepthTest = true, CullBackFaces = true, SortByPipeline = true,
            MirrorX = true, TransposeMatrices = true, Bloom = false, Shadows = false,
            ClearColor = Vector4.Zero,
        };
        var frame = renderer.RenderFrame(W, H, settings, out var error, new List<string>());
        int lit = 0;
        if (frame is not null)
            for (int i = 0; i + 3 < frame.Length; i += 4)
                if (frame[i] > 12 || frame[i + 1] > 12 || frame[i + 2] > 12) lit++;
        var outcome = new Outcome(frame is not null, error, lit, driver.LiveParticles, driver.DrawSlices,
            driver.Materials.Count);
        output.WriteLine($"RAN on a real D3D11 device: staticMesh={staticMesh} grid={groundGrid} -> {outcome}");
        return outcome;
    }

    [Fact]
    public void ABareParticleRendererDrawsItsFurnitureWhileNoParticleIsAlive()
    {
        // The failing case: no static mesh, one visual emitter registered, zero quads alive.
        if (ZeroParticleFrame(staticMesh: false, groundGrid: true) is not { } o) return;

        Assert.Equal(0, o.LiveParticles);     // the premise: nothing is alive
        Assert.True(o.Materials > 0 && o.Slices > 0, "the emitter registered a material - this is not the empty-system case");
        Assert.Null(o.Error);                 // was "no mesh set"
        Assert.True(o.Rendered, "a zero-particle frame must render, not be refused");
        Assert.True(o.LitPixels > 500, $"the floor grid must be on screen ({o.LitPixels} lit pixels)");
    }

    [Fact]
    public void TheFurnitureIsWhatFillsAZeroParticleFrame()
    {
        // The same frame with the grid off is the clear colour and nothing else: the pixels above are the
        // furniture's, not a particle's and not a stale buffer's. Still a frame, not a refusal.
        if (ZeroParticleFrame(staticMesh: false, groundGrid: false) is not { } o) return;

        Assert.Null(o.Error);
        Assert.True(o.Rendered);
        Assert.Equal(0, o.LitPixels);
    }

    [Fact]
    public void TheEditorsOwnConfigurationRendersAZeroParticleFrame()
    {
        // The Particle Editor's surface: the fallback sphere is installed, so this frame was never refused. Pinned
        // so the claim stays true when the renderer's rule changes again.
        if (ZeroParticleFrame(staticMesh: true, groundGrid: true) is not { } o) return;

        Assert.Equal(0, o.LiveParticles);
        Assert.Null(o.Error);
        Assert.True(o.Rendered);
        Assert.True(o.LitPixels > 500, $"the floor grid must be on screen ({o.LitPixels} lit pixels)");
    }

    [Fact]
    public void ABareRendererWithNoParticleMaterialIsStillToldThereIsNoMesh()
    {
        // The diagnostic survives for what it was written for: materials registered, nothing to draw them with
        // and nothing that draws its own geometry. The materials here are the very ones above with their
        // particle marker taken off - to the renderer that is a scene whose host forgot SetMesh.
        if (ZeroParticleFrame(staticMesh: false, groundGrid: true,
                materials => { foreach (var m in materials) m.UsesDynamicMesh = false; }) is not { } o) return;

        Assert.False(o.Rendered);
        Assert.Equal("no mesh set", o.Error);
    }
}

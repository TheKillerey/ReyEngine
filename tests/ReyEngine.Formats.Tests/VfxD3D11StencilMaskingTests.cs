using System.Numerics;
using System.Text;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// Particle stencil masking on the Direct3D 11 renderer (the Cherry_GoH_Portal_Noxus_1 fix): D3D11 had no
/// stencil plane at all, so every emitter authoring stencilMode/stencilRef drew unmasked - the reported
/// rainbow orb, hard-edged rectangle and stripe panels outside the Arena portal arch.
///
/// <para>Mirrors the GL device proof from the M182 commit message (a mode-1 writer at pass 0, a larger
/// tester at pass 10; EQUAL draws exactly the writer's footprint, EQUAL + NOT-EQUAL tile the tester's whole
/// area with no overlap and no gap) through the REAL D3D11 device and the REAL production path
/// (<see cref="VfxD3D11EmitterPipeline.Build"/> + <see cref="D3D11MapParticles"/>), against synthetic
/// emitters rather than a real WAD system - the stencil STATE MACHINE is what is under test, not any one
/// asset. Needs the game installed for its real quad_vs/quad_ps shader cache; skips (does not fail)
/// otherwise, matching every other device test in this file.</para>
/// </summary>
public sealed class VfxD3D11StencilMaskingTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private static bool Installed => Directory.Exists(Final);

    private const int W = 200, H = 200;

    /// <summary>One opaque white texel - texel * BirthColor reproduces BirthColor exactly, with no soft-dot
    /// fallback fading the footprint's edges and no alpha test dropping any of it.</summary>
    private static TextureImage White() => new(1, 1, new byte[] { 255, 255, 255, 255 });

    private static VfxEmitterDefinition Quad(string name, Vector4 color, float scale, int pass,
        int stencilMode, int stencilRef) => new(
        Name: name,
        Rate: VfxCurveF.Const(1f),
        ParticleLifetime: VfxCurveF.Const(10f),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: true,
        Disabled: false,
        BlendMode: 3,   // NONE: opaque, writes depth (VfxBlend) - color counting needs a hard overwrite,
                        // not a blend, or an overlapping tester's colour would mix with the writer's rather
                        // than replace it and no pixel would count as cleanly "one or the other".
        BirthScale: VfxCurve3.Const(new Vector3(scale, scale, scale)),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(color),
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
        Pass: pass,
        StencilMode: stencilMode,
        StencilRef: stencilRef);

    /// <summary>Render one system (a subset of the two synthetic emitters) and return the BGRA readback.</summary>
    private static byte[]? Render(ShaderCacheReader cache, params VfxEmitterDefinition[] emitters)
    {
        var system = new VfxSystemDefinition(0xABCD, "StencilProbe", "test/probe", emitters);
        var textures = emitters.Select(_ => (TextureImage?)White()).ToList();
        var item = new VfxPlaybackItem(system, Matrix4x4.Identity, textures);

        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return null;
        var driver = new D3D11MapParticles(renderer, cache);
        driver.SetPlayback(new VfxPlayback(new[] { item }));

        var eye = new Vector3(0f, 0f, -1000f);
        var target = Vector3.Zero;
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, (float)W / H, 1f, 5000f);
        var viewProj = view * proj;
        // A few ticks so the single-burst particle is definitely alive; ScaleOverLife is null (constant
        // size), so nothing about the footprint depends on exactly how many.
        for (int i = 0; i < 5; i++) driver.Tick(1f / 30f, view, viewProj, eye, 1000f);

        var settings = new PreviewSettings
        {
            SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
            AlphaBlend = true, DepthTest = true, MirrorX = false, CullBackFaces = false,
            SortByPipeline = false, Bloom = false, Shadows = false,
            ClearColor = Vector4.Zero,
        };
        var frame = renderer.RenderFrame(W, H, settings, out var error, new List<string>());
        driver.StopAll();
        return frame?.ToArray();
    }

    private static int CountNear(byte[] bgra, byte b, byte g, byte r)
    {
        int n = 0;
        for (int k = 0; k + 3 < bgra.Length; k += 4)
            if (Math.Abs(bgra[k] - b) < 8 && Math.Abs(bgra[k + 1] - g) < 8 && Math.Abs(bgra[k + 2] - r) < 8) n++;
        return n;
    }

    [Fact]
    public void EqualDrawsExactlyTheWritersFootprintAndEqualPlusNotEqualTileTheTester()
    {
        if (!Installed) return;
        var database = new HashSyncService().LoadLocal(_ => { });
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(database), out var cacheError);
        if (cache is null) return;   // no shader cache reachable on this machine - nothing to test against

        var white = new Vector4(1f, 1f, 1f, 1f);
        var red = new Vector4(1f, 0f, 0f, 1f);
        const int Ref = 9;

        // Ground truth: each quad's own footprint, measured with no stencil interaction at all (mode 0).
        var writerSolo = Render(cache, Quad("writer", white, 90f, pass: 0, stencilMode: 0, stencilRef: -1));
        var testerSolo = Render(cache, Quad("tester", red, 320f, pass: 10, stencilMode: 0, stencilRef: -1));
        Assert.NotNull(writerSolo); Assert.NotNull(testerSolo);
        int writerFootprint = CountNear(writerSolo!, 255, 255, 255);
        int testerFootprint = CountNear(testerSolo!, 0, 0, 255);   // BGRA: red channel at index 2
        Assert.True(writerFootprint > 200, $"writer solo footprint too small to measure ({writerFootprint}px)");
        Assert.True(testerFootprint > writerFootprint * 2,
            $"tester solo footprint ({testerFootprint}px) should dwarf the writer's ({writerFootprint}px) - "
            + "the writer must sit entirely inside it for the partition check below to mean anything");

        // The real case: a mode-1 WRITER (invisible-in-spirit here it is opaque white so it can be counted,
        // exactly like the ground-truth solo render) followed by a mode-2/3 TESTER at the same ref.
        var equalFrame = Render(cache,
            Quad("writer", white, 90f, pass: 0, stencilMode: 1, stencilRef: Ref),
            Quad("tester", red, 320f, pass: 10, stencilMode: 2, stencilRef: Ref));
        var notEqualFrame = Render(cache,
            Quad("writer", white, 90f, pass: 0, stencilMode: 1, stencilRef: Ref),
            Quad("tester", red, 320f, pass: 10, stencilMode: 3, stencilRef: Ref));
        Assert.NotNull(equalFrame); Assert.NotNull(notEqualFrame);

        int equalRed = CountNear(equalFrame!, 0, 0, 255);
        int notEqualRed = CountNear(notEqualFrame!, 0, 0, 255);

        // EQUAL: the tester draws (opaque, so it OVERWRITES the writer's white) only where the stencil
        // already equals the writer's ref - which, on a per-frame-cleared buffer with nothing else drawing
        // stencil, is exactly the writer's own footprint. Without the fix this renderer had StencilEnable=0
        // everywhere, so the tester drew across its own WHOLE footprint (~testerFootprint px) rather than
        // being confined to the writer's.
        Assert.Equal(writerFootprint, equalRed);

        // NOT-EQUAL: the complement - the tester draws everywhere in ITS OWN footprint except the writer's.
        Assert.Equal(testerFootprint - writerFootprint, notEqualRed);

        // The M182 GL proof's own check, ported: EQUAL and NOT-EQUAL partition the tester's whole area with
        // no overlap and no gap.
        Assert.Equal(testerFootprint, equalRed + notEqualRed);
    }

    /// <summary>Non-particle draws must never touch the stencil: every DepthStencilDesc this renderer builds
    /// for the sky, the backdrop, the dynamic-light overlay, the post-fog pass and the selection/editor
    /// overlay leaves StencilEnable at its struct default (0/false) - only <see cref="ShaderPreviewRenderer"/>
    /// itself builds the WRITE/EQUAL/NOT-EQUAL variants, and only <c>DepthStateFor</c> (driven by
    /// <c>PreviewMaterial.StencilMode</c>, which only a particle material ever sets) selects one of them.</summary>
    [Fact]
    public void OnlyParticleMaterialsCanEverSelectAStencilState()
    {
        string? Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
            if (dir is null) return null;
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        var dx11 = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        Assert.NotNull(dx11);

        // The stencil-variant array is declared once, built once (inside UpdateStates, from the base depth
        // descs), and read only by the one chooser every draw site goes through.
        Assert.Equal(1, dx11!.Split("private readonly ComPtr<ID3D11DepthStencilState>[,] _stencilDepthStates").Length - 1);
        Assert.Contains("d.StencilEnable = 1;", dx11);
        Assert.Contains("if (mat.StencilMode is >= 1 and <= 3 && _stencilDepthStates[b, mat.StencilMode].Handle is not null)", dx11);

        // Every OTHER DepthStencilDesc object literal in this file (the selection/icon overlay, and the base
        // descs the stencil variants are cloned FROM) either omits StencilEnable altogether or writes it as
        // 0 - the struct default, i.e. "do not touch the stencil plane". None of them writes 1 directly; only
        // the stencil-variant loop does, and only after cloning one of those bases.
        int at = 0;
        while ((at = dx11.IndexOf("new DepthStencilDesc", at, StringComparison.Ordinal)) >= 0)
        {
            int end = dx11.IndexOf("};", at, StringComparison.Ordinal);
            string block = dx11[at..end];
            Assert.DoesNotContain("StencilEnable = 1", block);
            at = end + 2;
        }

        // The sky, the legacy NVR backdrop, the dynamic-light overlay and the screen fog draw every frame
        // regardless of any particle's stencil mode - none of their own depth-stencil states may reference
        // the array or set the enable flag, or one of them would silently start writing or testing the mask
        // particles rely on.
        foreach (var file in new[] { "ShaderPreviewRenderer.Sky.cs", "ShaderPreviewRenderer.Backdrop.cs",
                     "ShaderPreviewRenderer.PostFog.cs", "ShaderPreviewRenderer.DynamicLights.cs" })
        {
            var text = Source("src", "ReyEngine.Rendering.D3D11", file);
            if (text is null) continue;
            Assert.DoesNotContain("_stencilDepthStates", text);
            Assert.DoesNotContain("StencilEnable = 1", text);
        }
    }
}

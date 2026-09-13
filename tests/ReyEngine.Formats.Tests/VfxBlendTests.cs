using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M720, the seventh ltk-manager reading ported: 2.49's CPU premultiply, and the blend enum it stands on
/// (3.2, 2.19, 2.17), and the two draw-order keys that were waiting for it (2.11).
///
/// <para>The state table is the reference renderer's, row for row (blend.ts). Where this editor departs
/// from the engine on purpose - no particle writes destination alpha, TARGETALPHA draws nothing - the test
/// says so rather than hiding it inside a renderer.</para>
/// </summary>
public sealed class VfxBlendTests
{
    private const int Stride = 19;
    private const int OffColor = 5;

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static VfxEmitterDefinition Emitter(int blendMode, Vector4? colour = null, int pass = 0,
        VfxEmitterExtras? extras = null, VfxDistortionDefinition? distortion = null) => new(
        Name: "e" + blendMode,
        Rate: VfxCurveF.Const(1f),
        ParticleLifetime: VfxCurveF.Const(5f),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: true,
        Disabled: false,
        BlendMode: blendMode,
        BirthScale: VfxCurve3.Const(new Vector3(20f, 20f, 20f)),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(colour ?? Vector4.One),
        ColorOverLife: null,
        BirthVelocity: null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(Vector3.Zero),
        TexturePath: "ASSETS/Test/p.dds",
        TexDiv: Vector2.One,
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: false,
        Distortion: distortion,
        Pass: pass,
        Extras: extras);

    // ================================================================ the enum

    [Fact]
    public void TheEnumIsTheEngines()
    {
        // ParticleSystem::BLEND_MODE, plan 3.2
        Assert.Equal(new[] { "Add", "Alpha", "Subtract", "None", "AlphaAdd", "PremultipliedAlpha", "Min", "Max", "TargetAlpha" },
            Enumerable.Range(0, 9).Select(m => VfxBlend.ModeOf(m).ToString()).ToArray());
        // nothing outside 0..8 is authored; it reads as the declared default, as the reference renderer does
        Assert.Equal(VfxBlendMode.Add, VfxBlend.ModeOf(9));
        Assert.Equal(VfxBlendMode.Add, VfxBlend.ModeOf(-1));
    }

    [Theory]
    // mode, enabled, src, dst, op, writes depth, adds to target
    [InlineData(0, true, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendOp.Add, false, true)]
    [InlineData(1, true, VfxBlendFactor.SrcAlpha, VfxBlendFactor.InvSrcAlpha, VfxBlendOp.Add, false, false)]
    [InlineData(2, true, VfxBlendFactor.Zero, VfxBlendFactor.InvSrcColor, VfxBlendOp.Add, false, false)]
    [InlineData(3, false, VfxBlendFactor.One, VfxBlendFactor.Zero, VfxBlendOp.Add, true, false)]
    [InlineData(4, true, VfxBlendFactor.SrcAlpha, VfxBlendFactor.One, VfxBlendOp.Add, false, true)]
    [InlineData(5, true, VfxBlendFactor.One, VfxBlendFactor.InvSrcAlpha, VfxBlendOp.Add, false, false)]
    [InlineData(6, true, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendOp.Min, false, false)]
    [InlineData(7, true, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendOp.Max, false, false)]
    public void EachModeIsTheReferenceRenderersState(int mode, bool enabled, VfxBlendFactor src, VfxBlendFactor dst,
        VfxBlendOp op, bool writesDepth, bool adds)
    {
        var st = VfxBlend.StateFor(Emitter(mode), null, VfxBlendOptions.Engine);
        Assert.Equal((VfxBlendMode)mode, st.Mode);
        Assert.Equal(enabled, st.Enabled);
        Assert.Equal(src, st.Src);
        Assert.Equal(dst, st.Dst);
        Assert.Equal(op, st.Op);
        Assert.Equal(writesDepth, st.WritesDepth);
        Assert.Equal(adds, st.AddsToTarget);
        Assert.True(st.WritesColor);
    }

    [Fact]
    public void TargetAlphaDrawsNothingHereAndSaysWhy()
    {
        // The engine's InvDestAlpha, DestAlpha against a destination alpha this editor pins at 1 is
        // (1 - 1) * src + 1 * dst - nothing. Drawn as that answer, so neither viewport shows a faint quad.
        var st = VfxBlend.StateFor(Emitter(8), null, VfxBlendOptions.Engine);
        Assert.Equal(VfxBlendFactor.Zero, st.Src);
        Assert.Equal(VfxBlendFactor.One, st.Dst);
        Assert.Contains("destination alpha", st.Why);
    }

    [Fact]
    public void NoParticleStateWritesDestinationAlpha()
    {
        // Both presentations composite the target's alpha, so a particle that lowered it would show the
        // window through. The record says so, and both renderers mask it - pinned below by source.
        foreach (int mode in Enumerable.Range(0, 9))
        {
            Assert.False(VfxBlend.StateFor(Emitter(mode), null, VfxBlendOptions.Engine).WritesAlpha);
            Assert.False(VfxBlend.StateFor(Emitter(mode), true, VfxBlendOptions.Legacy).WritesAlpha);
        }

        string? gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        Assert.NotNull(gl);
        Assert.Contains("_gl.BlendFuncSeparate(GlFactor(st.Src), GlFactor(st.Dst), BlendingFactor.Zero, BlendingFactor.One);", gl);
        Assert.Contains("_gl.ColorMask(st.WritesColor, st.WritesColor, st.WritesColor, false);", gl);
        // separate, or MIN and MAX take the alpha equation and ignore the factors pinning it
        Assert.Contains("_gl.BlendEquationSeparate(", gl);
        Assert.Contains("_gl.ColorMask(true, true, true, true);", gl);

        string? dx = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        Assert.NotNull(dx);
        Assert.Contains("SrcBlendAlpha = Blend.Zero, DestBlendAlpha = Blend.One, BlendOpAlpha = BlendOp.Add,", dx);
        Assert.Contains("? (byte)(ColorWriteEnable.Red | ColorWriteEnable.Green | ColorWriteEnable.Blue)", dx);
    }

    [Fact]
    public void NoneWritesDepthUnderItsOwnSwitch()
    {
        Assert.True(VfxBlend.StateFor(Emitter(3), null, VfxBlendOptions.Engine).WritesDepth);
        Assert.False(VfxBlend.StateFor(Emitter(3), null, new VfxBlendOptions(EngineModes: true, NoneWritesDepth: false)).WritesDepth);
        foreach (int mode in new[] { 0, 1, 2, 4, 5, 6, 7, 8 })
            Assert.False(VfxBlend.StateFor(Emitter(mode), null, VfxBlendOptions.Engine).WritesDepth);
    }

    [Fact]
    public void ADistortionEmitterDrawsStraightAlphaAndOnlyIfItNamesAMap()
    {
        var warp = new VfxDistortionDefinition(0.02f, 1, "ASSETS/Test/n.dds");
        var st = VfxBlend.StateFor(Emitter(0, distortion: warp), null, VfxBlendOptions.Engine);
        Assert.Equal(VfxBlendFactor.SrcAlpha, st.Src);
        Assert.Equal(VfxBlendFactor.InvSrcAlpha, st.Dst);
        Assert.False(VfxBlend.Premultiplies(Emitter(0, distortion: warp), VfxBlendOptions.Engine));

        // a block that names no map is not a warp - in either renderer, which used to disagree on it
        var noMap = new VfxDistortionDefinition(0.02f, 1, "");
        Assert.False(VfxBlend.IsDistortion(Emitter(0, distortion: noMap)));
        Assert.True(VfxBlend.Premultiplies(Emitter(0, distortion: noMap), VfxBlendOptions.Engine));
    }

    [Fact]
    public void WriteAlphaOnlyWritesNoColour()
    {
        var st = VfxBlend.StateFor(Emitter(6, extras: new VfxEmitterExtras { WriteAlphaOnly = true }), null, VfxBlendOptions.Engine);
        Assert.False(st.WritesColor);
        Assert.DoesNotContain("WriteAlphaOnly", VfxParkedEmitterFields.Names);
        Assert.True(VfxPreviewCoverage.IsParsed(HashAlgorithms.Fnv1a("WriteAlphaOnly")));
    }

    // ================================================================ 2.49

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    public void OnlyAddAndSubtractPremultiply(int mode, bool premultiplies)
    {
        Assert.Equal(premultiplies, VfxBlend.Premultiplies(Emitter(mode), VfxBlendOptions.Engine));
        Assert.False(VfxBlend.Premultiplies(Emitter(mode), VfxBlendOptions.Legacy));
    }

    [Fact]
    public void ThePremultiplyIsTheReferenceRenderersNumbers()
    {
        // materials.test.ts: (0.5, 0.25, 1, 0.5) -> (0.25, 0.125, 0.5, 1)
        Assert.Equal(new Vector4(0.25f, 0.125f, 0.5f, 1f), VfxBlend.Premultiply(new Vector4(0.5f, 0.25f, 1f, 0.5f)));
    }

    [Fact]
    public void TheSimulatorWritesThePremultipliedColourForAddAndLeavesAlphaAlone()
    {
        var colour = new Vector4(0.5f, 0.25f, 1f, 0.5f);
        float[] Slots(int mode)
        {
            var sim = new VfxParticleSimulator(seed: 7);
            sim.SetSystem(new VfxSystemDefinition(PathHash: 1, Name: "s", ParticlePath: "",
                Emitters: new[] { Emitter(mode, colour) }), Matrix4x4.Identity);
            sim.Update(1f / 30f);
            var e = sim.Emitters.First();
            Assert.True(e.InstanceCount > 0);
            return e.Instances.Skip(OffColor).Take(4).ToArray();
        }

        Assert.Equal(new[] { 0.25f, 0.125f, 0.5f, 1f }, Slots(0));   // ADD
        Assert.Equal(new[] { 0.25f, 0.125f, 0.5f, 1f }, Slots(2));   // SUBTRACT
        Assert.Equal(new[] { 0.5f, 0.25f, 1f, 0.5f }, Slots(1));     // ALPHA keeps its alpha
        Assert.Equal(new[] { 0.5f, 0.25f, 1f, 0.5f }, Slots(4));     // ALPHAADD weighs it on the GPU
    }

    [Fact]
    public void ThePremultiplyRunsBeforeTheGradientTheEngineSamplesPerPixel()
    {
        // quad_ps multiplies PARTICLE_COLOR_TEXTURE into the texel and then by the vertex colour, so under
        // ONE,ONE the gradient's alpha reaches output alpha only. Premultiplying after our CPU gradient would
        // scale the colour by it instead.
        string? sim = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleSimulator.cs");
        Assert.NotNull(sim);
        int premultiply = sim!.IndexOf("if (VfxBlend.Premultiplies(d, VfxBlend.Options)) col = VfxBlend.Premultiply(col);", StringComparison.Ordinal);
        int gradient = sim.IndexOf("col *= SampleGradient(", StringComparison.Ordinal);
        Assert.True(premultiply > 0 && gradient > premultiply, "the premultiply must precede the colour gradient");
    }

    // ================================================================ soft fade and draw rank

    [Theory]
    [InlineData(1, 1f, 0f, 0f, 1f)]
    [InlineData(4, 1f, 0f, 0f, 1f)]
    [InlineData(5, 0f, 1f, 0f, 1f)]
    [InlineData(0, 0f, 1f, 1f, 0f)]
    [InlineData(2, 0f, 1f, 1f, 0f)]
    [InlineData(3, 0f, 1f, 1f, 0f)]
    [InlineData(8, 0f, 1f, 1f, 0f)]
    public void TheSoftFadeLandsWhereTheModeWeighsTheColour(int mode, float x, float y, float z, float w) =>
        Assert.Equal(new Vector4(x, y, z, w), VfxBlend.SoftControl(Emitter(mode), VfxBlendOptions.Engine));

    [Theory]
    [InlineData(3, 0)]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 2)]
    [InlineData(8, 3)]
    public void TheDrawRankIsBlendRank(int mode, int rank) => Assert.Equal(rank, VfxBlend.DrawRank(mode));

    [Fact]
    public void TheDrawOrderIsTheEnginesFiveKeys()
    {
        // model.test.ts: a lower pass first whatever the blend; on a tied pass NONE < ADD < ALPHA < TARGETALPHA;
        // the render-flags byte below the rank; the authored index last.
        List<string> Order(params VfxEmitterDefinition[] es) =>
            es.OrderBy(e => VfxDrawOrder.KeyFor(e, true, VfxBlendOptions.Engine)).Select(e => e.Name ?? "").ToList();

        var target = Emitter(8, pass: -10) with { Name = "target" };
        var none = Emitter(3, pass: 10) with { Name = "none" };
        Assert.Equal(new[] { "target", "none" }, Order(none, target));

        var t = Emitter(8) with { Name = "t" };
        var a = Emitter(1) with { Name = "a" };
        var ad = Emitter(0) with { Name = "add" };
        var n = Emitter(3) with { Name = "n" };
        Assert.Equal(new[] { "n", "add", "a", "t" }, Order(t, a, ad, n));

        var flagged = Emitter(4, extras: new VfxEmitterExtras { MiscRenderFlags = 1 }) with { Name = "flagged" };
        var plain = Emitter(4) with { Name = "plain" };
        Assert.Equal(new[] { "plain", "flagged" }, Order(flagged, plain));

        // the legacy table leaves keys 3 and 4 neutral, as they were
        Assert.Equal((1, 0, 0, 0), VfxDrawOrder.KeyFor(t, true, VfxBlendOptions.Legacy));
        // and a host sorting across systems takes the first two keys only
        Assert.Equal((1, 0), VfxDrawOrder.KeyAcrossSystems(t));
    }

    // ================================================================ the A/B

    [Fact]
    public void TheLegacyTableIsWhatDrewBeforeM720()
    {
        // An absent blendMode read 1 and drew additive. It reads 0 now; 0 is never written, so the legacy
        // branch maps it back - otherwise switching the table off would draw 116,150 emitters as alpha, a
        // look that never existed.
        var absent = VfxBlend.StateFor(Emitter(0), null, VfxBlendOptions.Legacy);
        Assert.Equal((VfxBlendFactor.SrcAlpha, VfxBlendFactor.One), (absent.Src, absent.Dst));
        var one = VfxBlend.StateFor(Emitter(1), null, VfxBlendOptions.Legacy);
        Assert.Equal((VfxBlendFactor.SrcAlpha, VfxBlendFactor.One), (one.Src, one.Dst));
        // M273's sprite rule for mode 2 survives inside it
        Assert.Equal(VfxBlendFactor.InvSrcAlpha, VfxBlend.StateFor(Emitter(2), true, VfxBlendOptions.Legacy).Dst);
        Assert.Equal(VfxBlendFactor.One, VfxBlend.StateFor(Emitter(2), false, VfxBlendOptions.Legacy).Dst);
        Assert.False(VfxBlend.StateFor(Emitter(3), null, VfxBlendOptions.Legacy).WritesDepth);
    }

    // ================================================================ one definition, both renderers

    [Fact]
    public void BothRenderersReadTheOneTable()
    {
        string? gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        Assert.NotNull(gl);
        Assert.DoesNotContain("IsAdditiveFor(", gl);
        Assert.Equal(4, gl!.Split("ApplyBlend(es.Def, es.Texture);").Length - 1);   // quad, mesh, trail, beam
        Assert.Contains("bool isDistortion = ReyEngine.Formats.Vfx.VfxBlend.IsDistortion(es.Def);", gl);

        string? pipeline = Source("src", "ReyEngine.App", "Services", "VfxD3D11EmitterPipeline.cs");
        Assert.NotNull(pipeline);
        Assert.Contains("var blend = VfxBlend.StateFor(e, texHasAlpha, blendOptions);", pipeline);
        Assert.Contains("mat.ParticleBlend = blend;", pipeline);
        Assert.Contains("mat.WritesDepth = blend.WritesDepth;", pipeline);
        Assert.Contains("mat.SortableByPipeline = false;", pipeline);
        Assert.Contains("bool isDistortion = VfxBlend.IsDistortion(e);", pipeline);

        string? dx = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        Assert.NotNull(dx);
        // the draw loop and the legacy mesh path take the particle state; so does the ribbon, below
        Assert.Equal(2, dx!.Split("ParticleBlendState(").Length - 1 - 1);   // two call sites plus the definition
        // a NONE emitter drawn before the first soft one must not land in the soft-depth snapshot
        Assert.Contains("mat.ParticleBlend is { WritesDepth: true }", dx);
        Assert.Contains("mat.ParticleSoftControl is { } softControl", dx);

        string? ribbon = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.Ribbon.cs");
        Assert.NotNull(ribbon);
        Assert.Contains("_ctx.OMSetDepthStencilState(DepthStateFor(mat), 0);", ribbon);
        Assert.Contains("ParticleBlendState(ribbonBlend)", ribbon);
    }

    [Fact]
    public void TheResolverReadsBothDeclaredDefaults()
    {
        string? resolver = Source("src", "ReyEngine.Formats", "Vfx", "VfxSystemResolver.cs");
        Assert.NotNull(resolver);
        Assert.Contains("BlendMode: GetU8(p, F_blendMode) ?? 0,", resolver);
        Assert.Contains("AlphaRef: GetU8(p, F_alphaRef) ?? 5,", resolver);
    }
}

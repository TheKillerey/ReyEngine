using System.Numerics;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M629: the alpha-erosion and palette constants the D3D11 particle path never sent.
///
/// <para>The permutation was selected with ALPHA_EROSION / PALETTIZE_TEXTURES on and the sampler was
/// bound, and then nothing wrote the constants — so the renderer's deliberately-neutral stand-ins stood.
/// They are documented as no-ops "a real emitter overrides", and for two milestones no emitter did.</para>
///
/// <para>What is asserted here is the ARITHMETIC and the two guards, because that is where a wrong value
/// silently erases a sprite. The upload itself is a wiring assertion, and the visible result needs a
/// screenshot.</para>
/// </summary>
public sealed class Dx11ParticleConstantsTests
{
    private static VfxAlphaErosion Erosion(float slice, float featherIn, float featherOut) =>
        new(MapPath: "x.tex", ChannelMixer: new Vector4(1, 0, 0, 0),
            Drive: default!, SliceWidth: slice, FeatherIn: featherIn, FeatherOut: featherOut);

    private static string? Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string? Pipeline() =>
        Read("src", "ReyEngine.App", "Services", "VfxD3D11EmitterPipeline.cs");

    // ===================================================== the arithmetic

    [Fact]
    public void AMissingFeatherDegradesToANoOpEdgeRatherThanToZero()
    {
        // mask = sat(x) - sat(y). A missing LEADING edge has to become a hard step (large slope) and a
        // missing TRAILING edge slope 0, so nothing is subtracted. Getting that backwards erases the
        // sprite completely, which is exactly the failure mode this whole stage is prone to.
        var yzw = Erosion(0.25f, featherIn: 0f, featherOut: 0f).PackYzw();

        Assert.Equal(0.25f, yzw.X, 4);
        Assert.Equal(1000f, yzw.Y, 1);   // no leading feather -> hard step
        Assert.Equal(0f, yzw.Z, 4);      // no trailing feather -> subtract nothing
    }

    [Fact]
    public void AFeatherBecomesItsReciprocalSlope()
    {
        var yzw = Erosion(0.5f, featherIn: 0.25f, featherOut: 0.5f).PackYzw();

        Assert.Equal(0.5f, yzw.X, 4);
        Assert.Equal(4f, yzw.Y, 4);
        Assert.Equal(2f, yzw.Z, 4);
    }

    [Fact]
    public void ThePaletteRowIsCentredSoItLandsOnOneRow()
    {
        // Sampling at selector/count straddles two rows under the bilinear sampler Riot binds; the +0.5
        // is what makes the lookup land on the row the artist chose.
        Assert.Equal(0.5f / 16f, new VfxPalette("p.tex", 16, 0f, new Vector4(1, 0, 0, 0), null, null).RowV, 5);
        Assert.Equal(4.5f / 16f, new VfxPalette("p.tex", 16, 4f, new Vector4(1, 0, 0, 0), null, null).RowV, 5);
    }

    [Fact]
    public void APaletteWithNoRowsIsNotUsable()
    {
        // -1 and 0 both occur in the corpus and are nonsense as a divisor.
        Assert.False(new VfxPalette("p.tex", 0, 0f, new Vector4(1, 0, 0, 0), null, null).IsUsable);
        Assert.False(new VfxPalette("p.tex", -1, 0f, new Vector4(1, 0, 0, 0), null, null).IsUsable);
        Assert.False(new VfxPalette(null, 16, 0f, new Vector4(1, 0, 0, 0), null, null).IsUsable);
        Assert.True(new VfxPalette("p.tex", 16, 0f, new Vector4(1, 0, 0, 0), null, null).IsUsable);
    }

    // ===================================================== the two guards

    [Fact]
    public void TheDegenerateGuardIsAppliedOnTheD3D11PathToo()
    {
        // GL skips erosion whose inferred packing evaluates to a mask of zero everywhere - 16% of the
        // corpus. Without the same guard here the D3D11 viewport would erase particles the GL one draws,
        // which is worse than the pre-M629 behaviour it replaces.
        if (Pipeline() is not { } text) return;
        Assert.Contains("!erosion.IsDegenerate", text);
    }

    [Fact]
    public void TheParametersAreOnlySentWhenTheMapActuallyBound()
    {
        // Against the renderer's WHITE stand-in the erosion texel reads 1, and real parameters then
        // evaluate to mask = 0 over most of the drive: the sprite vanishes. The neutral default exists to
        // survive exactly that, so an unbound map must leave it standing.
        if (Pipeline() is not { } text) return;

        Assert.Contains("slot is not null && !erosion.IsDegenerate", text);
        Assert.Contains("slot is not null && palette.IsUsable", text);
    }

    [Fact]
    public void BindTextureReportsWhetherItBound()
    {
        // The guard above is only possible because binding answers. It used to return void.
        if (Pipeline() is not { } text) return;

        Assert.Contains("private static string? BindTexture(", text);
        Assert.DoesNotContain("private static void BindTexture(", text);
    }

    // ===================================================== the upload

    [Fact]
    public void AllFourConstantsAreSent()
    {
        // The names have to match the shader's exactly - a typo here is silent, and its symptom is the
        // stand-in standing, which is the bug being fixed.
        if (Pipeline() is not { } text) return;

        foreach (string name in new[]
                 {
                     "cAlphaErosionParams", "cAlphaErosionTextureMixer",
                     "cPaletteSelectMain", "cPaletteSrcMixerMain",
                 })
            Assert.Contains($"mat.Params[\"{name}\"]", text);
    }

    [Fact]
    public void TheRendererStillHasItsNeutralStandIns()
    {
        // They are what an emitter that does NOT author a stage falls back to, and what a degenerate or
        // unbound one keeps. Removing them because "a real emitter overrides them now" would break every
        // case the guards above deliberately decline to handle.
        if (Read("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs") is not { } text) return;

        Assert.Contains("\"CALPHAEROSIONPARAMS\" => new[] { 0f, 2f, 1f, 0f }", text);
        Assert.Contains("\"CPALETTESRCMIXERMAIN\" => new[] { 1f, 0f, 0f, 0f }", text);
    }
}

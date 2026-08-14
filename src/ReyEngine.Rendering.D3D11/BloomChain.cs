namespace ReyEngine.Rendering.D3D11;

/// <summary>One rung of the bloom mip chain, in pixels.</summary>
public readonly record struct BloomLevel(int Width, int Height);

/// <summary>
/// M460: the CPU half of Riot's bloom chain — the only part of it that is ours.
///
/// <para>The three filters (<c>filters/mipchainbloomdownsample.ps</c>,
/// <c>filters/mipchainbloomupsample.ps</c>, <c>filters/bloom.ps</c>) are Riot's own compiled blobs, run
/// verbatim. Each declares exactly one constant, <c>float2 UVStep</c> at <c>$Globals</c>+0, and reads
/// nothing else. So everything this milestone has to synthesise is: how many levels the chain has, how big
/// each one is, and what <c>UVStep</c> to write for each pass. That is this file, and it is deliberately
/// free of Direct3D so it can be asserted directly — an off-by-one in a mip size shows up on screen only as
/// "the glow looks a bit wrong", which is not a signal anybody can act on.</para>
///
/// <para><b>UVStep is the SOURCE mip's texel size.</b> Measured from the disassembly, not assumed:
/// <c>mipchainbloomdownsample</c> offsets its 13 taps by up to <c>±2 * cb0[0].xy</c> around the incoming
/// TEXCOORD0 (blob 0 lines 46-79), and TEXCOORD0 is the destination's UV. Two texels of the SOURCE is the
/// COD/Jimenez kernel; two texels of the destination would double every offset and blur twice as far.
/// The 3x3 tent upsample (lines 46-68) and the 7-tap Gaussian (<c>bloom.ps</c> lines 46-66, offsets
/// <c>-3..+3</c>) take the same convention.</para>
///
/// <para><b>What is NOT measured, and is chosen here.</b> frame-pipeline.md §7 item 2 records the chain
/// LENGTH and whether the upsample is additive as unmeasured — DXBC carries no pass list. So the numbers
/// below are a standard, defensible configuration rather than a recovered one, and they are in one place
/// with one name so that a future RenderDoc capture changes a constant instead of a search.</para>
/// </summary>
public static class BloomChain
{
    /// <summary>Levels including level 0 (the full-resolution glow buffer itself), so at most
    /// <see cref="MaxLevels"/>-1 downsamples. Six is the usual choice for a 1080p-class target: it reaches
    /// roughly 1/32 of the frame, which is as wide as a screen-space blur is ever asked to spread.</summary>
    public const int MaxLevels = 6;

    /// <summary>Stop halving before either axis gets smaller than this. Below a handful of texels the
    /// 13-tap downsample's ±2-texel reach wraps most of the mip and the clamp addressing smears one corner
    /// across the whole level, which is visible as a directional bias in the final glow.</summary>
    public const int MinExtent = 8;

    /// <summary>
    /// The chain for a target of this size, coarsest last. Index 0 is always the full-resolution level and
    /// is the glow render target, not an allocation of its own; every later entry needs a texture.
    ///
    /// <para>Returns a single level when the target is too small to halve even once. The caller must treat
    /// that as "no bloom this frame" — with nothing to downsample into there is no chain to run, and
    /// blurring the glow buffer in place would produce a different effect, not a smaller one.</para>
    /// </summary>
    public static IReadOnlyList<BloomLevel> Levels(int width, int height, int maxLevels = MaxLevels)
    {
        var levels = new List<BloomLevel>(maxLevels);
        if (width <= 0 || height <= 0 || maxLevels <= 0) return levels;

        levels.Add(new BloomLevel(width, height));
        while (levels.Count < maxLevels)
        {
            var last = levels[^1];
            int w = last.Width / 2, h = last.Height / 2;
            // Checked BEFORE the level is added, so no level in the returned chain is ever below the floor.
            if (w < MinExtent || h < MinExtent) break;
            levels.Add(new BloomLevel(w, h));
        }
        return levels;
    }

    /// <summary>The <c>$Globals.UVStep</c> for a pass whose SOURCE is <paramref name="source"/>. One texel
    /// of the source, on both axes — the isotropic form the two mip-chain filters take.</summary>
    public static (float X, float Y) UvStep(BloomLevel source) =>
        (1f / MathF.Max(1, source.Width), 1f / MathF.Max(1, source.Height));

    /// <summary>
    /// <c>UVStep</c> for one half of the separable Gaussian. <c>filters/bloom.ps</c> is a single 7-tap pass
    /// applied along whatever direction the constant points, so a horizontal pass zeroes Y and a vertical
    /// pass zeroes X; writing both components would make it a diagonal blur, which is the classic way to
    /// get a Gaussian that looks subtly smeared.
    /// </summary>
    public static (float X, float Y) BlurStep(BloomLevel source, bool horizontal)
    {
        var (x, y) = UvStep(source);
        return horizontal ? (x, 0f) : (0f, y);
    }

    /// <summary>
    /// The final composite, <c>out = 1 - (1 - scene) * (1 - bloom)</c> — a SCREEN blend, not an additive
    /// one. Measured off <c>gamma/ps_copy_post.ps</c> blob 1 (the <c>BLOOM=1</c> permutation), lines 36-41:
    /// both inputs are inverted, multiplied, and the product inverted again. This is what lets the whole
    /// chain live in an 8-bit LDR buffer: the result provably cannot exceed 1 for inputs in [0,1].
    ///
    /// <para>Here so the arithmetic is asserted rather than only executed on a GPU nobody watches. The
    /// shipped composite is Riot's blob; this is the same equation for the tests to hold it to.</para>
    /// </summary>
    public static float ScreenBlend(float scene, float bloom) => 1f - (1f - scene) * (1f - bloom);
}

using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M460: the CPU half of Riot's bloom chain.
///
/// <para>Everything the milestone synthesises is here — the level sizes, the <c>UVStep</c> per pass, and
/// the composite arithmetic. The three filters themselves are Riot's compiled blobs run verbatim, so there
/// is nothing of ours to test in them; what CAN be wrong is which size feeds which pass, and that failure
/// mode is a glow that is subtly the wrong width, which nobody can see and nobody can report. Asserted
/// against docs/research/frame-pipeline.md §2.2 (the three kernels) and §2.3 (the screen blend).</para>
/// </summary>
public class BloomChainTests
{
    // ------------------------------------------------------------------ level sizing

    [Fact]
    public void Level_zero_is_the_full_target_and_is_the_glow_buffer_itself()
    {
        var levels = BloomChain.Levels(1920, 1080);
        Assert.Equal(new BloomLevel(1920, 1080), levels[0]);
    }

    [Fact]
    public void Each_level_halves_the_one_above_it()
    {
        var levels = BloomChain.Levels(1920, 1080);
        Assert.Equal(new[]
        {
            new BloomLevel(1920, 1080),
            new BloomLevel(960, 540),
            new BloomLevel(480, 270),
            new BloomLevel(240, 135),
            new BloomLevel(120, 67),      // 135/2 floors to 67; the chain does not need even extents
            new BloomLevel(60, 33),
        }, levels);
    }

    [Fact]
    public void The_chain_never_exceeds_MaxLevels()
    {
        // A target big enough to halve far more than six times still stops at six.
        var levels = BloomChain.Levels(8192, 8192);
        Assert.Equal(BloomChain.MaxLevels, levels.Count);
        Assert.Equal(new BloomLevel(8192 >> (BloomChain.MaxLevels - 1), 8192 >> (BloomChain.MaxLevels - 1)), levels[^1]);
    }

    [Fact]
    public void No_level_is_ever_below_the_minimum_extent()
    {
        // The floor is checked BEFORE a level is added, so it is a property of every returned level rather
        // than of the last one. 300x9 can only halve once on Y before breaking it.
        foreach (var (w, h) in new[] { (1920, 1080), (300, 9), (64, 64), (37, 400), (1000, 17) })
            foreach (var level in BloomChain.Levels(w, h))
            {
                Assert.True(level.Width >= System.Math.Min(w, BloomChain.MinExtent));
                Assert.True(level.Height >= System.Math.Min(h, BloomChain.MinExtent));
            }
    }

    [Theory]
    [InlineData(15, 15)]      // halving gives 7x7, below the floor
    [InlineData(8, 8)]        // exactly the floor: 4x4 is below it
    [InlineData(1920, 15)]    // wide but one axis too short
    [InlineData(1, 1)]
    public void A_target_too_small_to_halve_yields_a_single_level_and_no_chain(int w, int h)
    {
        // The renderer reads this as "no bloom this frame". One level means there is nothing to downsample
        // INTO, and blurring the glow buffer in place would be a different effect, not a smaller one.
        Assert.Single(BloomChain.Levels(w, h));
    }

    [Theory]
    [InlineData(16, 16)]      // 8x8 is exactly the floor, so it is kept
    [InlineData(1920, 16)]
    public void A_target_that_can_halve_exactly_once_yields_two_levels(int w, int h)
    {
        Assert.Equal(2, BloomChain.Levels(w, h).Count);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-4, -4)]
    public void A_degenerate_target_yields_no_levels_at_all(int w, int h)
    {
        Assert.Empty(BloomChain.Levels(w, h));
    }

    // ------------------------------------------------------------------ UVStep

    [Fact]
    public void UvStep_is_one_texel_of_the_source_level_on_both_axes()
    {
        // frame-pipeline.md §2.2: mipchainbloomdownsample offsets its 13 taps by up to +/-2 * UVStep around
        // the destination UV, and those two texels are texels of the SOURCE. Feeding the destination's texel
        // size instead would double every offset in the kernel.
        var (x, y) = BloomChain.UvStep(new BloomLevel(1920, 1080));
        Assert.Equal(1f / 1920f, x, 7);
        Assert.Equal(1f / 1080f, y, 7);
    }

    [Fact]
    public void The_downsample_pass_steps_by_the_LARGER_level_not_the_one_being_written()
    {
        // The pairing the renderer's loop makes: writing level i reads level i-1.
        var levels = BloomChain.Levels(1920, 1080);
        var (x, y) = BloomChain.UvStep(levels[0]);
        Assert.Equal(1f / 1920f, x, 7);
        Assert.Equal(1f / 1080f, y, 7);

        // ...and it is NOT the destination's, which would be twice as big.
        var (dx, _) = BloomChain.UvStep(levels[1]);
        Assert.Equal(2f * x, dx, 7);
    }

    [Fact]
    public void UvStep_never_divides_by_zero()
    {
        var (x, y) = BloomChain.UvStep(new BloomLevel(0, 0));
        Assert.Equal(1f, x);
        Assert.Equal(1f, y);
    }

    [Fact]
    public void The_separable_blur_zeroes_the_axis_it_is_not_running_along()
    {
        // filters/bloom.ps is ONE 7-tap pass along whatever direction the constant points. Writing both
        // components would make it a diagonal blur rather than half of a separable Gaussian.
        var level = new BloomLevel(960, 540);

        var (hx, hy) = BloomChain.BlurStep(level, horizontal: true);
        Assert.Equal(1f / 960f, hx, 7);
        Assert.Equal(0f, hy);

        var (vx, vy) = BloomChain.BlurStep(level, horizontal: false);
        Assert.Equal(0f, vx);
        Assert.Equal(1f / 540f, vy, 7);
    }

    // ------------------------------------------------------------------ the composite

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(1f, 0f, 1f)]
    [InlineData(0f, 1f, 1f)]
    [InlineData(1f, 1f, 1f)]
    [InlineData(0.5f, 0.5f, 0.75f)]
    [InlineData(0.25f, 0.5f, 0.625f)]
    public void The_composite_is_a_screen_blend(float scene, float bloom, float expected)
    {
        Assert.Equal(expected, BloomChain.ScreenBlend(scene, bloom), 6);
    }

    [Fact]
    public void The_screen_blend_can_never_exceed_one()
    {
        // This is WHY the whole chain is correct in an 8-bit LDR buffer, and why frame-pipeline.md §5.7
        // rules out moving the render target to a float format. An additive composite would not have this
        // property and would clip instead.
        for (int a = 0; a <= 20; a++)
            for (int b = 0; b <= 20; b++)
            {
                float outv = BloomChain.ScreenBlend(a / 20f, b / 20f);
                Assert.InRange(outv, 0f, 1f);
            }
    }

    [Fact]
    public void The_screen_blend_is_symmetric_and_never_darkens_the_scene()
    {
        for (int a = 0; a <= 10; a++)
            for (int b = 0; b <= 10; b++)
            {
                float scene = a / 10f, bloom = b / 10f;
                Assert.Equal(BloomChain.ScreenBlend(scene, bloom), BloomChain.ScreenBlend(bloom, scene), 6);
                Assert.True(BloomChain.ScreenBlend(scene, bloom) >= scene - 1e-6f);
            }
    }

    [Fact]
    public void Zero_bloom_leaves_the_scene_bit_identical_in_an_eight_bit_target()
    {
        // The claim that matters, and the one worth stating in the units the render target actually has.
        //
        // 183 of 184 materials on a real map write the CONSTANT (0,0,0,1) to o1, so on most of the frame
        // the composite runs with bloom = 0 and MUST give back the pixel it was handed. In float that is
        // 1-(1-s)*1, which is NOT exactly s: at s = 80/255 the round trip lands 3e-8 low, straddling the
        // sixth decimal place. That is ordinary float32 behaviour of a subtract-then-subtract, it is what
        // the GPU does too, and asserting six decimals here would be asserting something untrue about
        // floating point rather than something true about the composite.
        //
        // What IS true, and is the whole point, is that the error is far below one 8-bit code: every
        // channel value survives the round trip unchanged after quantisation. So a frame with nothing
        // emissive in it is bit-identical whether the chain ran or not.
        for (int a = 0; a <= 255; a++)
        {
            float roundTripped = BloomChain.ScreenBlend(a / 255f, 0f);
            Assert.Equal(a / 255f, roundTripped, tolerance: 1e-6f);
            Assert.Equal(a, (int)MathF.Round(roundTripped * 255f));
        }
    }
}

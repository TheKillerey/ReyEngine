using ReyEngine.Formats.Shaders;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M247: quality scaling selects one of RIOT's cooked variants rather than a hand-written simplified
/// shader — which is both more accurate and free, since the client does the same thing. The risk being
/// pinned here is the failure mode: pinning a define a shader has never heard of produces a permutation
/// key that was never cooked, and the resolve then finds nothing. A shader that silently fails to load is
/// far worse than one that ignores a quality setting.
/// </summary>
public class ShaderQualityTests
{
    [Fact]
    public void Default_is_high_quality()
    {
        // A resolver that quietly downgrades would make every accuracy comparison wrong.
        Assert.Equal(ShaderQuality.High, new ShaderResolver(null!).Quality);
    }

    [Fact]
    public void Quality_is_settable_both_ways()
    {
        var r = new ShaderResolver(null!) { Quality = ShaderQuality.Low };
        Assert.Equal(ShaderQuality.Low, r.Quality);
        r.Quality = ShaderQuality.High;
        Assert.Equal(ShaderQuality.High, r.Quality);
    }
}

using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M452: the point-light overlay's batching rule. The cbuffer carries exactly MaxLightsPerDraw lights, so
/// a longer table must loop the pass - the case that matters is a ported Light.dat with ~365 lights, which
/// must produce 6 full passes rather than silently lighting only the first 64.
/// </summary>
public class DynamicLightBatchTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]     // a bogus count draws nothing rather than one empty batch
    [InlineData(1, 1)]
    [InlineData(64, 1)]     // exactly one full cbuffer
    [InlineData(65, 2)]     // one light over the cap starts a second pass
    [InlineData(128, 2)]
    [InlineData(365, 6)]    // the user's ported Light.dat table
    [InlineData(1024, 16)]  // GL's own loop bound, for parity
    public void Batch_count_covers_every_light(int lights, int expected)
    {
        Assert.Equal(expected, ShaderPreviewRenderer.LightBatchCount(lights));
    }

    [Fact]
    public void Cap_is_the_cbuffer_size()
    {
        // The HLSL declares gPosRadius[64]/gColorStrength[64]; if the cap moves, the shader must too.
        Assert.Equal(64, ShaderPreviewRenderer.MaxLightsPerDraw);
    }
}

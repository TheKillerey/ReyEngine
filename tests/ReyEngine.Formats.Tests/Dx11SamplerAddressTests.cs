using ReyEngine.App.Services;
using ReyEngine.Formats.Materials;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

public class Dx11SamplerAddressTests
{
    [Theory]
    [InlineData(false, false, PreviewSamplerAddress.Wrap)]
    [InlineData(true, false, PreviewSamplerAddress.ClampU)]
    [InlineData(false, true, PreviewSamplerAddress.ClampV)]
    [InlineData(true, true, PreviewSamplerAddress.ClampUV)]
    public void MaterialClampAxesSelectTheMatchingDx11Sampler(
        bool clampU, bool clampV, PreviewSamplerAddress expected)
    {
        var profile = MaterialProfile.Default with { ClampU = clampU, ClampV = clampV };
        Assert.Equal(expected, Dx11SceneBuilder.SamplerAddressFor(profile));
    }
}

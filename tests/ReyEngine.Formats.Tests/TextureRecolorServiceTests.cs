using ReyEngine.App.Services;
using ReyEngine.Core.Decoding;

namespace ReyEngine.Formats.Tests;

public sealed class TextureRecolorServiceTests
{
    [Fact]
    public async Task MissingOriginals_AreCountedAndExplained()
    {
        var targets = new[]
        {
            new RecolorTarget(1, "assets/maps/a.tex"),
            new RecolorTarget(2, "assets/maps/b.tex"),
        };
        var service = new TextureRecolorService(_ => null, (_, _, _) => throw new InvalidOperationException());

        var result = await service.RunAsync(targets, new TextureAdjustment { HueDegrees = 45 });

        Assert.Equal(0, result.Written);
        Assert.Equal(2, result.Failed);
        Assert.Equal(2, result.MissingSources);
        Assert.Empty(result.WrittenTargets);
        Assert.Contains(result.Notes, note => note.Contains("original source", StringComparison.OrdinalIgnoreCase));
    }
}

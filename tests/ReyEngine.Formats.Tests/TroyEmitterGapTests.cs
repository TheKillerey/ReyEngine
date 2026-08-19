using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M531: GroupPart numbering has holes, and the scan used to stop at the first one.
///
/// <para>Legacy emitter lists are keyed <c>System*GroupPart{n}</c>, and the numbering is NOT dense -
/// FireTorch_Med is 1,2,3,5,6,7. The scan broke out of the loop at the first missing index, so every
/// emitter past the hole was silently dropped: FireTorch_Med converted 3 of its 6, SmallTorch 2 of 3.
/// Across the legacy corpus that is 707 of 5,851 files and 2,520 of 23,089 emitters.</para>
///
/// <para>The dropped emitters are not dead data. Riot's own conversion of the same effect,
/// <c>Jade_FireTorch_Med</c> in Map12's jade.materials.bin, ships exactly the six the scan was cutting
/// short - so the truncation was measurable against ground truth, not just against the file's own keys.</para>
///
/// <para>These tests read the legacy client and return early when it is absent, matching
/// <see cref="TroyParityHarnessTests"/>.</para>
/// </summary>
public sealed class TroyEmitterGapTests
{
    private const string Legacy = @"K:\LeagueSandbox\League_Sandbox_Client\DATA\Particles";

    private static TroyBinFile? Load(string stem)
    {
        string path = Path.Combine(Legacy, stem + ".troybin");
        if (!File.Exists(path)) return null;
        return TroyBinFile.TryParse(File.ReadAllBytes(path), out var file, out _) ? file : null;
    }

    [Theory]
    [InlineData("FireTorch_Med", 6, "Embers", "Flame", "Flat", "FlameDark", "HeatHaze", "Glow")]
    [InlineData("SmallTorch", 3, "embers", "Fire_Test", "Glow")]
    public void EmittersPastAGapAreKept(string stem, int expected, params string[] names)
    {
        // FireTorch_Med numbers its parts 1,2,3,5,6,7 and SmallTorch 1,2,5. Both used to stop at the hole.
        if (Load(stem) is not { } troy) return;

        Assert.Equal(expected, troy.Emitters.Count);
        Assert.Equal(names, troy.Emitters.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void NoEmitterIsLostAnywhereInTheLegacyCorpus()
    {
        // The real assertion: every GroupPart key present in a file becomes an emitter. Counting keys
        // independently of the parser is what makes this a check rather than a restatement.
        if (!Directory.Exists(Legacy)) return;

        int keys = 0, produced = 0, files = 0;
        foreach (string path in Directory.EnumerateFiles(Legacy, "*.troybin"))
        {
            if (!TroyBinFile.TryParse(File.ReadAllBytes(path), out var troy, out _)) continue;
            if (troy is null || !troy.HasDecodedBody) continue;

            files++;
            produced += troy.Emitters.Count;
            for (int i = 1; i <= 64; i++)
                if (troy.Sections?.TryGetStringOffset(TroyHash.EmitterNameKey(i), out _) == true) keys++;
        }

        Assert.True(files > 1000, $"expected the legacy corpus, found {files} parseable files");
        Assert.Equal(keys, produced);
    }
}

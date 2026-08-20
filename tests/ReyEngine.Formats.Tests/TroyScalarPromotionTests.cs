using System.Numerics;
using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M532: a section-12 value that is a SINGLE numeric token stands in for a uniform vector.
///
/// <para>This was the largest silent loss in the reader. <c>*p-scale</c> is authored as a bare string
/// - <c>"20"</c>, <c>"100"</c> - on 7,081 of the 22,180 emitters that carry one, and
/// <see cref="TroySections.TryGetVector3"/> refused it: scalar promotion was gated to sections 1-5, and
/// the section-12 branch demanded exactly three tokens. <c>TryGetScalar</c> read those values correctly
/// the whole time, so the number was never missing from the file - only from the vector path. The
/// converter saw a null ScaleVector and substituted an invented 50.</para>
///
/// <para>Riot's own conversions settle that the promotion is uniform rather than, say, (v,v,0):
/// LavaCauldron/Smoke's <c>"20"</c> is (20,20,20) in Jade_LavaCauldron and GemGlow/glow's <c>"100"</c>
/// is (100,100,100) in Jade_GemGlow.</para>
/// </summary>
public sealed class TroyScalarPromotionTests
{
    private const string Legacy = @"K:\LeagueSandbox\League_Sandbox_Client\DATA\Particles";

    private static TroyBinFile? Load(string stem)
    {
        string path = Path.Combine(Legacy, stem + ".troybin");
        if (!File.Exists(path)) return null;
        return TroyBinFile.TryParse(File.ReadAllBytes(path), out var file, out _) ? file : null;
    }

    private static Vector3? Scale(string stem, string emitter) =>
        Load(stem)?.Emitters.FirstOrDefault(e => e.Name == emitter)?.ScaleVector;

    [Theory]
    [InlineData("LavaCauldron", "Smoke", 20f)]      // Jade_LavaCauldron ships (20,20,20)
    [InlineData("LavaCauldron", "Surface", 40f)]    // (40,40,40)
    [InlineData("LavaCauldron", "HeatHaze", 70f)]   // (70,70,70)
    [InlineData("GemGlow", "glow", 100f)]           // Jade_GemGlow ships (100,100,100)
    [InlineData("GemGlow", "sparkles", 20f)]        // (20,20,20)
    [InlineData("FireTorch_Med", "Flame", 10f)]     // Jade_FireTorch_Med ships (10,10,10)
    [InlineData("FireTorch_Med", "Glow", 60f)]      // (60,60,60)
    public void ASingleTokenScaleBecomesAUniformVector(string stem, string emitter, float expected)
    {
        if (Scale(stem, emitter) is not { } scale) return;
        Assert.Equal(new Vector3(expected, expected, expected), scale);
    }

    [Theory]
    [InlineData("FireTorch_Med", "Embers", 2f, 2f, 0f)]
    [InlineData("GemGlow", "littleray", 20f, 60f, 0f)]
    [InlineData("SmallTorch", "embers", 2f, 4f, 0f)]
    [InlineData("WaterRipples", "ripples", 20f, 20f, 0f)]
    public void AZeroThirdComponentIsPreservedNotPromoted(string stem, string emitter, float x, float y, float z)
    {
        // These are already vectors, in other sections, and Riot keeps their zero Z verbatim -
        // Jade_FireTorch_Med ships Embers as (2,2,0). Promotion must only ever apply to a lone token.
        if (Scale(stem, emitter) is not { } scale) return;
        Assert.Equal(new Vector3(x, y, z), scale);
    }

    [Fact]
    public void NoEmitterInMap2sSystemsFallsBackToAnInventedScaleAnyMore()
    {
        // 19 of these 43 used to get (50,50,50) because the reader could not see a value that was there.
        string[] stems =
        {
            "Bugs_env", "CANDLE", "DarkSmoke", "Env_bats", "env_death_lotus", "env_fall_leaves",
            "env_fog_green", "Firefly", "FireTorch_Med", "FireTorch_Simple", "GemGlow", "GemGlow_p",
            "LavaCauldron", "PumpkinCandle", "SmallTorch", "WaterRipples",
        };
        if (!Directory.Exists(Legacy)) return;

        var missing = new List<string>();
        int emitters = 0;
        foreach (string stem in stems)
        {
            if (Load(stem) is not { } troy) return;
            foreach (var e in troy.Emitters)
            {
                emitters++;
                if (e.ScaleVector is null || e.ScaleVector == Vector3.Zero) missing.Add($"{stem}/{e.Name}");
            }
        }

        Assert.Equal(43, emitters);
        Assert.Empty(missing);
    }

    [Fact]
    public void TheCorpusWideShareOfEmittersWithARealScaleStaysUp()
    {
        // 91.7% when this was written, up from 65.3%. A floor rather than the figure, since the rest is
        // the 909 emitters that genuinely have no *p-scale key at all.
        if (!Directory.Exists(Legacy)) return;

        int total = 0, real = 0;
        foreach (string path in Directory.EnumerateFiles(Legacy, "*.troybin"))
        {
            if (!TroyBinFile.TryParse(File.ReadAllBytes(path), out var troy, out _) || troy is null) continue;
            foreach (var e in troy.Emitters)
            {
                total++;
                if (e.ScaleVector is { } sv && sv != Vector3.Zero) real++;
            }
        }

        Assert.True(total > 20_000, $"expected the legacy corpus, saw {total} emitters");
        Assert.True((double)real / total >= 0.90,
            $"only {real} of {total} ({(double)real / total:P1}) emitters read a real scale");
    }
}

using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M534: three fields the reader could already name but nothing ever read.
///
/// <para>Each produced a visible defect in a ported map: a ground quad standing upright
/// (<c>*p-simpleorient</c>), a heat haze drawn as a white card (<c>*p-normal-map</c> and friends), and
/// every particle born at one angle (the rotation probability tables).</para>
/// </summary>
public sealed class TroyOrientationAndDistortionTests
{
    private const string Legacy = @"K:\LeagueSandbox\League_Sandbox_Client\DATA\Particles";

    private static TroyBinFile? Load(string stem)
    {
        string path = Path.Combine(Legacy, stem + ".troybin");
        if (!File.Exists(path)) return null;
        return TroyBinFile.TryParse(File.ReadAllBytes(path), out var f, out _) ? f : null;
    }

    /// <summary>The converted emitter as a property bag, keyed by resolved field name.</summary>
    private static BinTreeStruct? Emitter(string stem, string name)
    {
        if (Load(stem) is not { } troy) return null;
        var converted = TroyBinConverter.Convert(troy, stem, "Particles/" + stem);
        var tree = SafeBinTree.Parse(converted.BinBytes);
        var system = tree.Objects.Values.First();
        if (system.Properties.GetValueOrDefault(HashAlgorithms.Fnv1a("complexEmitterDefinitionData"))
            is not BinTreeContainer emitters) return null;

        foreach (var element in emitters.Elements)
            if (element is BinTreeStruct s
                && s.Properties.GetValueOrDefault(HashAlgorithms.Fnv1a("emitterName")) is BinTreeString n
                && n.Value == name)
                return s;
        return null;
    }

    private static T? Prop<T>(BinTreeStruct s, string field) where T : BinTreeProperty =>
        s.Properties.GetValueOrDefault(HashAlgorithms.Fnv1a(field)) as T;

    [Fact]
    public void SimpleOrientTwoLaysTheQuadOnTheGround()
    {
        // Riot's own Jade_LavaCauldron writes (-90, 1, 0) for this emitter. Without the pitch the quad
        // stands up, which is what "a mesh going in the height" was.
        if (Emitter("LavaCauldron", "Surface") is not { } surface) return;

        var rotation = Prop<BinTreeEmbedded>(surface, "birthRotation0");
        Assert.NotNull(rotation);
        var constant = rotation!.Properties.GetValueOrDefault(HashAlgorithms.Fnv1a("constantValue")) as BinTreeVector3;
        Assert.NotNull(constant);
        Assert.Equal(-90f, constant!.Value.X);
    }

    [Fact]
    public void TheSameRuleReachesTheFogTheUserReported()
    {
        // env_fog_green has no Riot twin, but carries the identical trio - simpleorient 2, a scalar
        // quadrot, type 1 - so it takes the same pitch. Stated as an expectation by ANALOGY.
        if (Emitter("env_fog_green", "fog") is not { } fog) return;

        var constant = Prop<BinTreeEmbedded>(fog, "birthRotation0")?
            .Properties.GetValueOrDefault(HashAlgorithms.Fnv1a("constantValue")) as BinTreeVector3;
        Assert.NotNull(constant);
        Assert.Equal(-90f, constant!.Value.X);
    }

    [Fact]
    public void AnEmitterWithNoSimpleOrientIsUntouched()
    {
        // The majority case. 832 paired emitters have no *p-simpleorient and score identically before and
        // after the rule - so this asserts the rule stays out of their way.
        // CANDLE/Glow, not CANDLE/Flame - Flame DOES carry the field, which is worth knowing: this
        // vocabulary is common, not exotic, so the rule has to stay narrow.
        if (Load("CANDLE") is not { } troy) return;
        Assert.False(troy.Sections!.ByKey.ContainsKey(
            TroyHash.FieldKey("Glow", TroyFields.SimpleOrient)));

        if (Emitter("CANDLE", "Glow") is not { } glow) return;
        var constant = Prop<BinTreeEmbedded>(glow, "birthRotation0")?
            .Properties.GetValueOrDefault(HashAlgorithms.Fnv1a("constantValue")) as BinTreeVector3;
        if (constant is not null) Assert.NotEqual(-90f, constant.Value.X);
    }

    [Theory]
    [InlineData("FireTorch_Simple", 0.05f)]
    [InlineData("LavaCauldron", 0.1f)]
    [InlineData("FireTorch_Med", 0.02f)]
    public void AHeatHazeBecomesADistortionRatherThanAWhiteCard(string stem, float power)
    {
        // Its own sprite is color-hold: a deliberate 8x8 all-white card. Drawn as an ordinary billboard
        // that IS the white quad. The refraction is what makes it a heat haze.
        if (Emitter(stem, "HeatHaze") is not { } haze) return;

        var distortion = Prop<BinTreeStruct>(haze, "distortionDefinition");
        Assert.NotNull(distortion);

        var map = distortion!.Properties.GetValueOrDefault(HashAlgorithms.Fnv1a("normalMapTexture")) as BinTreeString;
        Assert.NotNull(map);
        Assert.Contains("distort-heat", map!.Value, StringComparison.OrdinalIgnoreCase);

        var amount = distortion.Properties.GetValueOrDefault(HashAlgorithms.Fnv1a("distortion")) as BinTreeF32;
        Assert.NotNull(amount);
        Assert.Equal(power, amount!.Value, 4);
    }

    [Fact]
    public void TheNormalMapIsStagedOrItWouldBeAReferenceToNothing()
    {
        if (Load("LavaCauldron") is not { } troy) return;
        var converted = TroyBinConverter.Convert(troy, "LavaCauldron", "Particles/LavaCauldron");

        Assert.Contains(converted.Assets, a =>
            a.SourcePath.Contains("distort-heat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnEmitterWithNoNormalMapGetsNoDistortionBlock()
    {
        if (Emitter("LavaCauldron", "Bubbles") is not { } bubbles) return;
        Assert.Null(Prop<BinTreeStruct>(bubbles, "distortionDefinition"));
    }

    [Fact]
    public void FallingLeavesAreBornAcrossARangeOfAnglesNotAtOne()
    {
        // "They go in just one direction": birth rotation and spin were bare constants, so every leaf
        // shared them. The file says the rotation is a distribution.
        if (Emitter("env_fall_leaves", "leaves") is not { } leaves) return;

        var rotation = Prop<BinTreeEmbedded>(leaves, "birthRotation0");
        Assert.NotNull(rotation);
        var dynamics = rotation!.Properties.GetValueOrDefault(HashAlgorithms.Fnv1a("dynamics")) as BinTreeStruct;
        Assert.NotNull(dynamics);
        var tables = dynamics!.Properties.GetValueOrDefault(HashAlgorithms.Fnv1a("probabilityTables")) as BinTreeContainer;
        Assert.NotNull(tables);
        Assert.NotEmpty(tables!.Elements);
    }

    [Fact]
    public void TheSpreadsReachAMeaningfulShareOfTheCorpus()
    {
        // 9,715 of 23,089 emitters carry a birth-rotation table and 4,781 a spin table. All of them were
        // being flattened to a constant, so this is not a two-file curiosity.
        if (!Directory.Exists(Legacy)) return;

        int total = 0, rotation = 0;
        foreach (string path in Directory.EnumerateFiles(Legacy, "*.troybin"))
        {
            if (!TroyBinFile.TryParse(File.ReadAllBytes(path), out var troy, out _) || troy is null) continue;
            foreach (var e in troy.Emitters)
            {
                total++;
                if (e.QuadRotationSpread is not null) rotation++;
            }
        }

        Assert.True(total > 20_000, $"expected the legacy corpus, saw {total}");
        Assert.True(rotation > 8_000, $"only {rotation} emitters read a birth-rotation spread");
    }
}

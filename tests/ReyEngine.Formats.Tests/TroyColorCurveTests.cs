using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M525: the colour curve, bound by key.
///
/// <para>It was the last field still assigned by heuristic - runs of five-token strings handed to
/// emitters POSITIONALLY - and it was the largest remaining disagreement with Riot's own conversion:
/// 456 differed against 1,036 agreed, with the first key's time landing on 0.2 or 0.5 where Riot always
/// has 0. Bound by key, a curve belongs to its emitter by construction.</para>
///
/// <para><b>The field name was solved, not guessed.</b> sdbm is invertible enough to be attacked
/// algebraically: two emitters sharing a field satisfy <c>k1 - k2 = (base1 - base2) * 65599^n</c>,
/// which yields the field's LENGTH without knowing the field. That came out at 9, with the tail sums
/// falling into runs differing by 1 - the signature of a numbered suffix - and exactly one 9-character
/// name of that shape means anything: <c>*p-xrgba{n}</c>, parallel to <c>*p-xscale{n}</c>.</para>
/// </summary>
public sealed class TroyColorCurveTests
{
    private const string Corpus = @"K:\LeagueSandbox\League_Sandbox_Client\DATA\Particles";

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static TroyBinFile? Load(string name)
    {
        string path = Path.Combine(Corpus, name + ".troybin");
        if (!File.Exists(path)) return null;
        Assert.True(TroyBinFile.TryParse(File.ReadAllBytes(path), out var t, out var error), error);
        return t;
    }

    private static BinTreeProperty? Prop(IEnumerable<BinTreeProperty> props, string name)
        => props.FirstOrDefault(p => p.NameHash == H(name));

    private static IEnumerable<BinTreeProperty> Kids(BinTreeProperty? p) => p switch
    {
        BinTreeEmbedded e => e.Properties.Values,
        BinTreeStruct s => s.Properties.Values,
        BinTreeContainer c => c.Elements,
        _ => Array.Empty<BinTreeProperty>(),
    };

    private static BinTreeProperty Emitter(TroyBinFile troy, string name)
    {
        var result = TroyBinConverter.Convert(troy, "Test", "Particles/Test");
        BinTree tree;
        using (var ms = new MemoryStream(result.BinBytes)) tree = new BinTree(ms);
        var emitters = Kids(tree.Objects.Values.Single().Properties[H("complexEmitterDefinitionData")]);
        return emitters.Single(e => Prop(Kids(e), "emitterName") is BinTreeString s && s.Value == name);
    }

    [Fact]
    public void TheFieldNameFollowsTheXScalePattern()
    {
        // Two numbered curve families with the same shape, which is what made the solved name credible
        // rather than merely arithmetically valid.
        Assert.Equal("*p-xscale1", TroyFields.XScaleKey(1));
        Assert.Equal("*p-xrgba1", TroyFields.ColorKey(1));
        Assert.Equal("*p-xrgba", TroyFields.ColorOverLife);

        // and the hash the algebra produced, pinned so a typo in the spelling fails loudly instead of
        // quietly resolving nothing
        Assert.Equal(0x2C73C4ACu, TroyHash.Sdbm("*p-xrgba1"));
    }

    [Fact]
    public void ACurveIsReadKeyByKeyWithTimeFirst()
    {
        var t = Load("Acidtrail_buf");
        if (t is null) return;

        // [snow_blue] *p-xrgba1 = "0 1 1 1 0", *p-xrgba2 = ".1 1 1 1 1", *p-xrgba3 = "1 1 1 1 0"
        var curve = t.Emitters.Single(e => e.Name == "snow_blue").ColorOverLife;
        Assert.NotNull(curve);
        Assert.Equal(3, curve!.Count);
        Assert.Equal(new[] { 0f, 0.1f, 1f }, curve.Select(k => k.Time));

        // the alpha ramp is the point of the curve: invisible, opaque, invisible
        Assert.Equal(0f, curve[0].Color.W);
        Assert.Equal(1f, curve[1].Color.W);
        Assert.Equal(0f, curve[2].Color.W);
    }

    [Fact]
    public void TheMultiplierIsFoldedIn()
    {
        var t = Load("Acidtrail_buf");
        if (t is null) return;

        // [snow_blue] *p-xrgba = (1, .8, 1, 1). Folding it in is not a stylistic choice - it halves the
        // disagreement with Riot across the paired systems, 135 differences down to 66.
        var emitter = t.Emitters.Single(e => e.Name == "snow_blue");
        Assert.Equal(new Vector4(1f, 0.8f, 1f, 1f), emitter.ColorMultiplier);

        var dyn = Prop(Kids(Prop(Kids(Emitter(t, "snow_blue")), "Color")), "dynamics");
        Assert.NotNull(dyn);
        var second = Kids(Prop(Kids(dyn), "values")).OfType<BinTreeVector4>().ElementAt(1).Value;
        Assert.Equal(1f, second.X);
        Assert.Equal(0.8f, second.Y, 4);   // the green channel carries the multiplier
        Assert.Equal(1f, second.W);
    }

    [Fact]
    public void EachEmitterGetsItsOwnCurve()
    {
        var t = Load("Acidtrail_buf");
        if (t is null) return;

        // The heuristic handed curve i to emitter i. Two emitters of the same file with different key
        // counts is exactly the case that got shuffled.
        var sparks = t.Emitters.Single(e => e.Name == "sparks").ColorOverLife;
        var snow = t.Emitters.Single(e => e.Name == "snow_blue").ColorOverLife;
        Assert.NotNull(sparks);
        Assert.NotNull(snow);
        Assert.NotEqual(sparks!.Count, snow!.Count);
        Assert.Equal(0f, sparks[0].Time);
        Assert.Equal(0f, snow[0].Time);   // Riot's converted curves always start at 0
    }

    [Fact]
    public void AnEmitterWithNoColourKeysGetsNoCurveOfItsOwn()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        // FireTorch tints with a colour RAMP TEXTURE (p-rgba="color-addflame.tga") instead of inline
        // keys, so there is nothing to bind - and inventing one would be the old heuristic's mistake.
        Assert.Null(t.Emitters.Single(e => e.Name == "Flame").ColorOverLife);
        Assert.Equal("ASSETS/Legacy/color-addflame.tex",
            ((BinTreeString)Prop(Kids(Emitter(t, "Flame")), "particleColorTexture")!).Value);
    }

    [Fact]
    public void ColoursAuthoredZeroToTwoFiftyFiveAreScaledDown()
    {
        // Both authored scales occur in the corpus. The rule is per curve and the TIME is never
        // rescaled, which the old string scan already had right and this route keeps.
        if (!Directory.Exists(Corpus)) return;

        int checkedCurves = 0;
        foreach (var path in Directory.EnumerateFiles(Corpus, "*.troybin").Take(400))
        {
            if (!TroyBinFile.TryParse(File.ReadAllBytes(path), out var t, out _) || t is null
                || !t.HasDecodedBody) continue;
            foreach (var e in t.Emitters)
            {
                if (e.ColorOverLife is not { Count: > 0 } curve) continue;
                checkedCurves++;
                Assert.All(curve, k =>
                {
                    Assert.True(k.Color.X <= 1.001f && k.Color.Y <= 1.001f
                                && k.Color.Z <= 1.001f && k.Color.W <= 1.001f,
                        $"{Path.GetFileName(path)} {e.Name}: colour above 1 survived the rescale");
                });
            }
        }
        Assert.True(checkedCurves > 0);
    }
}

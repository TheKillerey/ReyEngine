using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M526: the measured legacy-to-modern field table.
///
/// <para>Every row was established against Riot's own conversion and then re-measured by a second pass
/// briefed to refute it. Two rows did not survive contact with the parity harness and are recorded
/// here as much as the ones that did - the harness caught both, which is the reason it exists.</para>
/// </summary>
public sealed class TroyFieldMapTests
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
    public void EveryRuleCarriesItsEvidence()
    {
        // The table is the documentation. A row without a measurement behind it is exactly the kind of
        // plausible guess this whole exercise exists to keep out.
        Assert.NotEmpty(TroyFieldMap.Rules);
        Assert.All(TroyFieldMap.Rules, r =>
        {
            Assert.StartsWith("*", r.Legacy);                       // legacy field keys are prefixed
            Assert.False(string.IsNullOrWhiteSpace(r.Modern));
            Assert.Matches(@"^\d+/\d+$", r.Evidence);
            var parts = r.Evidence.Split('/');
            Assert.True(int.Parse(parts[0]) > 0 && int.Parse(parts[0]) <= int.Parse(parts[1]),
                $"{r.Modern}: evidence {r.Evidence} is not a sane agree/total");
        });

        // no field written twice - two rows targeting one property would race
        Assert.Equal(TroyFieldMap.Rules.Count, TroyFieldMap.Rules.Select(r => r.Modern).Distinct().Count());
    }

    [Fact]
    public void IsLocalOrientationIsNotWrittenBecauseItsSenseIsInverted()
    {
        // The one the harness caught. *e-local-orient predicts the property perfectly by presence, so it
        // looks like a clean mapping - but a legacy 0 makes Riot write FALSE and a legacy 1 makes Riot
        // write nothing. Implemented the obvious way round, it produced a set of emitters disjoint from
        // Riot's: 78 missed, 17 invented, 0 agreements.
        Assert.DoesNotContain(TroyFieldMap.Rules, r => r.Modern == "isLocalOrientation");

        var t = Load("FireTorch_Simple");
        if (t is null) return;
        Assert.Null(Prop(Kids(Emitter(t, "Flame")), "isLocalOrientation"));
    }

    [Fact]
    public void AFlagIsWrittenAsABitBoolAndOnlyWhenTheLegacyValueIsSet()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        // BitBool, not Bool: Riot packs these, and a Bool byte in that slot is a different wire type -
        // the client reads the property as malformed and drops it.
        foreach (var rule in TroyFieldMap.Rules.Where(r => r.Write == TroyWrite.BitBool))
            foreach (var emitter in t.Emitters)
            {
                var written = Prop(Kids(Emitter(t, emitter.Name)), rule.Modern);
                if (written is null) continue;
                var bit = Assert.IsType<BinTreeBitBool>(written);
                Assert.True(bit.Value);                                   // never written false
                Assert.NotEqual(0f, t.Sections!.TryGetScalar(
                    TroyHash.FieldKey(emitter.Name, rule.Legacy), t.StringAt, out float v) ? v : 0f);
            }
    }

    [Fact]
    public void ALegacyZeroIsOmittedRatherThanWrittenFalse()
    {
        // Riot omits a defaulted value. Writing the zero would differ from every shipped file, and for a
        // flag it would say "explicitly off" where the author said nothing at all.
        if (!Directory.Exists(Corpus)) return;

        int checkedZeros = 0;
        foreach (var path in Directory.EnumerateFiles(Corpus, "*.troybin").Take(300))
        {
            if (!TroyBinFile.TryParse(File.ReadAllBytes(path), out var t, out _) || t is null
                || !t.HasDecodedBody) continue;

            foreach (var e in t.Emitters)
            foreach (var rule in TroyFieldMap.Rules.Where(r => r.Write == TroyWrite.BitBool))
            {
                uint key = TroyHash.FieldKey(e.Name, rule.Legacy);
                if (!t.Sections!.ByKey.ContainsKey(key)) continue;
                if (!t.Sections.TryGetScalar(key, t.StringAt, out float v) || v != 0f) continue;

                checkedZeros++;
                var props = new List<BinTreeProperty>();
                TroyFieldMap.Apply(t.Sections, t.StringAt, e.Name, props);
                Assert.DoesNotContain(props, p => p.NameHash == H(rule.Modern));
            }
        }
        Assert.True(checkedZeros > 0, "no legacy zero encountered - the assertion never ran");
    }

    [Fact]
    public void AScalarPromotesToAVectorTheWayItsOwnFieldWasMeasured()
    {
        // The two promotions are not interchangeable and each was measured on its own field: a rotation
        // zero-pads into X (a spin about one axis), a uniform magnitude broadcasts. Getting them the
        // wrong way round is silent - both produce a valid vec3.
        Assert.Equal(TroyWrite.ValueVector3,
            TroyFieldMap.Rules.Single(r => r.Modern == "birthRotation0").Write);

        // M535: colorLookUpScales used to be the Broadcast example here, and that was wrong twice over.
        // Riot writes a BARE Vector2 - 49,936 of 49,936 shipped emitters, with no ValueVector3 form of the
        // property existing at all - and the legacy value lives in section 8, which neither TryGetVector3
        // nor TryGetScalar accepts, so the rule fired on 0.76% of the field and its "23/23" evidence
        // string could not be reproduced (0 hits over 3,724 paired systems).
        var scales = TroyFieldMap.Rules.Single(r => r.Modern == "colorLookUpScales");
        Assert.Equal(TroyWrite.Vector2, scales.Write);
        Assert.Equal(System.Numerics.Vector2.One, scales.OmitAt);   // (1,1) is the identity, not (0,0)

        var t = Load("DestroyedBuilding_idle");
        if (t is null) return;

        // sparkles has *p-quadrot = 45 (a scalar), and Riot's conversion is (45, 0, 0)
        var rot = Prop(Kids(Emitter(t, "sparkles")), "birthRotation0");
        if (rot is null) return;
        var v = ((BinTreeVector3)Prop(Kids(rot), "constantValue")!).Value;
        Assert.Equal(45f, v.X);
        Assert.Equal(Vector3.Zero.Y, v.Y);
        Assert.Equal(Vector3.Zero.Z, v.Z);
    }

    [Fact]
    public void TimeBeforeFirstEmissionIsAPlainFloatNotAValueFloat()
    {
        // The other one the harness caught. Written as a ValueFloat it scored 0 of 51: Riot ships it as
        // a bare F32 leaf, and a right value in the wrong wire type is a property the client drops.
        Assert.Equal(TroyWrite.F32,
            TroyFieldMap.Rules.Single(r => r.Modern == "timeBeforeFirstEmission").Write);
    }

    [Fact]
    public void TheSolvedNamesBreakTheUsualPrefixConvention()
    {
        // These three carry no *p-/*e- prefix, which is exactly why guessing never found them and the
        // algebraic solve did. Pinned so a "tidy-up" cannot regularise them into names that resolve
        // nothing.
        Assert.Contains(TroyFieldMap.Rules, r => r.Legacy == "*single-particle");
        Assert.Contains(TroyFieldMap.Rules, r => r.Legacy == "*uniformscale");
        Assert.Contains(TroyFieldMap.Rules, r => r.Legacy == "*particle-velocity");
    }
}

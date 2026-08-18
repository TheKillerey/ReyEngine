using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M521: converting the curves, checked against Riot's own conversion.
///
/// <para>Riot re-authored these legacy effects as modern <c>VfxSystemDefinitionData</c> and shipped the
/// result, so for the 199 systems that exist under the same name in both there is a right answer to
/// compare against rather than a plausible one. The expected values below are read off
/// <c>Map12.wad.client:data/maps/mapgeometry/map12/jade.materials.bin</c>.</para>
///
/// <para>Measured agreement across all 444 emitters matched by name: rate constants 97.3%, particle
/// lifetimes 98.5%, and every probability table plus the scale curve at 100%. The residue is not
/// converter error - it is Riot re-tuning values in the decade since this legacy snapshot
/// (<c>runeTimeGlow</c>'s lifetime is 320 in the old file and 598 in the shipped one) and the binary's
/// own tenths quantisation.</para>
/// </summary>
public sealed class TroyConversionParityTests
{
    private const string Corpus = @"K:\LeagueSandbox\League_Sandbox_Client\DATA\Particles";

    private static TroyBinFile? Load(string name)
    {
        string path = Path.Combine(Corpus, name + ".troybin");
        if (!File.Exists(path)) return null;
        Assert.True(TroyBinFile.TryParse(File.ReadAllBytes(path), out var t, out var error), error);
        return t;
    }

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static BinTreeProperty? Prop(IEnumerable<BinTreeProperty> props, string name)
        => props.FirstOrDefault(p => p.NameHash == H(name));

    private static IEnumerable<BinTreeProperty> Kids(BinTreeProperty? p) => p switch
    {
        BinTreeEmbedded e => e.Properties.Values,
        BinTreeStruct s => s.Properties.Values,
        BinTreeContainer c => c.Elements,
        BinTreeOptional o => o.Value is null ? Array.Empty<BinTreeProperty>() : new[] { o.Value },
        _ => Array.Empty<BinTreeProperty>(),
    };

    /// <summary>The converted emitter of that name, read back out of the bytes the converter produced -
    /// so the test exercises serialization too, not just the object graph.</summary>
    private static BinTreeProperty Emitter(TroyBinFile troy, string name)
    {
        var result = TroyBinConverter.Convert(troy, "Test", "Particles/Test");
        BinTree tree;
        using (var ms = new MemoryStream(result.BinBytes)) tree = new BinTree(ms);
        var emitters = Kids(tree.Objects.Values.Single().Properties[H("complexEmitterDefinitionData")]);
        return emitters.Single(e => Prop(Kids(e), "emitterName") is BinTreeString s && s.Value == name);
    }

    private static List<float> Floats(BinTreeProperty? container)
        => Kids(container).OfType<BinTreeF32>().Select(f => f.Value).ToList();

    [Fact]
    public void AnOffsetIntoTheMiddleOfAStringStillResolves()
    {
        var t = Load("SRU_DragonPit_WaterFall_01");
        if (t is null) return;

        // The block holds "0.000000 1.0 1.0 1.0" at offset 389 and SteamBot2's *e-rate points at 406 -
        // the trailing "1.0". Resolving only against recorded string STARTS read it as absent, which
        // downstream became "use the default rate of 10"; Riot's converted rate for it is 1.
        Assert.Equal("0.000000 1.0 1.0 1.0", t.StringAt(389));
        Assert.Equal("1.0", t.StringAt(406));

        var steam = t.Emitters.Single(e => e.Name == "SteamBot2");
        Assert.Equal(1f, steam.Rate);
        Assert.Equal(5f, steam.ParticleLifetime);
    }

    [Fact]
    public void TheRateCarriesItsProbabilityTableInRiotsShape()
    {
        var t = Load("DestroyedBuilding_idle");
        if (t is null) return;

        // legacy: e-rate=10 with e-rateP1..3 = (0,0) (0.98,0) (1,2)
        // Riot:   rate = ValueFloat { constantValue 10, dynamics { probabilityTables[0]
        //           { keyTimes 0,0.98,1  keyValues 0,0,2 }, times {0}, values {10} } }
        var rate = Prop(Kids(Emitter(t, "sparkburst3")), "rate");
        Assert.NotNull(rate);
        Assert.Equal(10f, ((BinTreeF32)Prop(Kids(rate), "constantValue")!).Value);

        var dyn = Prop(Kids(rate), "dynamics");
        Assert.NotNull(dyn);
        var table = Kids(Prop(Kids(dyn), "probabilityTables")).Single();
        Assert.Equal(new[] { 0f, 0.98f, 1f }, Floats(Prop(Kids(table), "keyTimes")));
        Assert.Equal(new[] { 0f, 0f, 2f }, Floats(Prop(Kids(table), "keyValues")));

        // Riot writes times/values on every dynamics block, holding the constant at t=0
        Assert.Equal(new[] { 0f }, Floats(Prop(Kids(dyn), "times")));
        Assert.Equal(new[] { 10f }, Floats(Prop(Kids(dyn), "values")));
    }

    [Fact]
    public void TheParticleLifetimeCarriesItsTableToo()
    {
        var t = Load("DestroyedBuilding_idle");
        if (t is null) return;

        // p-life=1 with p-lifeP1..3 = (0,0.5) (0.9,0.6) (1,1)
        var life = Prop(Kids(Emitter(t, "sparkburst3")), "particleLifetime");
        var table = Kids(Prop(Kids(Prop(Kids(life), "dynamics")), "probabilityTables")).Single();
        Assert.Equal(new[] { 0f, 0.9f, 1f }, Floats(Prop(Kids(table), "keyTimes")));
        Assert.Equal(new[] { 0.5f, 0.6f, 1f }, Floats(Prop(Kids(table), "keyValues")));
    }

    [Fact]
    public void ScaleOverLifeIsTheCurveTimesTheMultiplier()
    {
        var t = Load("SRU_Lane_Motes");
        if (t is null) return;

        // p-xscale1..3 = 0.2 / 1.0 / 0.2 with p-xscale = (20,20,20).
        // Riot's scale0 for this emitter is 4 / 20 / 4 - exactly the product. *p-xscale reads like an
        // enable flag only because the one file with a text twin happens to have it set to 1.
        var motes = t.Emitters.Single(e => e.Name == "Motes");
        Assert.Equal(new Vector3(20, 20, 20), motes.ScaleMultiplier);
        Assert.Equal(new[] { 0.2f, 1f, 0.2f }, motes.ScaleOverLife!.Select(k => k.Scale.X));

        var dyn = Prop(Kids(Prop(Kids(Emitter(t, "Motes")), "scale0")), "dynamics");
        Assert.NotNull(dyn);
        Assert.Equal(new[] { 0f, 0.5f, 1f }, Floats(Prop(Kids(dyn), "times")));
        Assert.Equal(new[] { 4f, 20f, 4f },
            Kids(Prop(Kids(dyn), "values")).OfType<BinTreeVector3>().Select(v => v.Value.X));

        // it is a multiplier over life, so unlike birthScale0 it carries no constant
        Assert.Null(Prop(Kids(Prop(Kids(Emitter(t, "Motes")), "scale0")), "constantValue"));
    }

    [Fact]
    public void AnAxisWithNoTableGetsAnEmptyTableRatherThanNone()
    {
        var t = Load("DestroyedBuilding_idle");
        if (t is null) return;

        // sparkburst3 randomises velocity on Y only (p-velYP1/YP2). Riot still writes three tables and
        // leaves X and Z EMPTY, because the container is read by index - a two-element list would make
        // the Y table read as X. Empty STRUCTS are what Riot ships here; empty CONTAINERS crash the
        // client at load (M414), which is why the empty case writes no keyTimes/keyValues at all.
        var dyn = Prop(Kids(Prop(Kids(Emitter(t, "sparkburst3")), "birthVelocity")), "dynamics");
        var tables = Kids(Prop(Kids(dyn), "probabilityTables")).ToList();
        Assert.Equal(3, tables.Count);

        Assert.Empty(Kids(tables[0]));
        Assert.Equal(new[] { 0f, 1f }, Floats(Prop(Kids(tables[1]), "keyTimes")));
        Assert.Equal(new[] { 1f, 2f }, Floats(Prop(Kids(tables[1]), "keyValues")));
        Assert.Empty(Kids(tables[2]));
    }

    [Fact]
    public void ParticleLingerIsWrittenAsAnOption()
    {
        var t = Load("Sru_braziers_fire_temp");
        if (t is null) return;

        // p-linger=2 on Fire, and Riot's shipped conversion has particleLinger: option[f32] = { 2 }.
        // An OPTION, not a plain f32 - a different wire type there and the client skips the property.
        var linger = Prop(Kids(Emitter(t, "Fire")), "particleLinger");
        var optional = Assert.IsType<BinTreeOptional>(linger);
        Assert.Equal(2f, ((BinTreeF32)optional.Value!).Value);
    }

    [Fact]
    public void ALingerOfZeroIsNotWrittenAtAll()
    {
        var t = Load("SRU_DragonPit_WaterFall_01");
        if (t is null) return;

        // The guard is measured, not stylistic: across the paired emitters, a legacy p-linger of 0
        // corresponds to Riot writing no particleLinger in 218 of 219 cases.
        Assert.Equal(0f, t.Emitters.Single(e => e.Name == "SteamBot2").ParticleLinger);
        Assert.Null(Prop(Kids(Emitter(t, "SteamBot2")), "particleLinger"));
    }

    [Fact]
    public void RiotsOwnDefaultsAreNotInventedHere()
    {
        var t = Load("DestroyedBuilding_idle");
        if (t is null) return;

        // Riot's converted sparkburst3 carries particleLinger 10, and the legacy file has no p-linger
        // at all - so that 10 is Riot's own default, applied on 224 emitters whose source is silent.
        // Copying it would be inventing a value; the field is left off instead.
        Assert.Null(t.Emitters.Single(e => e.Name == "sparkburst3").ParticleLinger);
        Assert.Null(Prop(Kids(Emitter(t, "sparkburst3")), "particleLinger"));
    }

    [Fact]
    public void AnEmitterWithNoRandomisationGetsNoDynamicsAtAll()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        // Riot omits the dynamics block entirely when there is nothing to randomise (its EmitterPosition
        // is a bare constant). Writing an empty one would be a shape Riot never ships.
        var flat = Emitter(t, "Flat");
        var rate = Prop(Kids(flat), "rate");
        Assert.NotNull(Prop(Kids(rate), "constantValue"));
        Assert.Null(Prop(Kids(rate), "dynamics"));
    }
}

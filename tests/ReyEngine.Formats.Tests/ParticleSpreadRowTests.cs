using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M523: probability tables get their own rows in the Particle Editor.
///
/// <para>A Value* struct is rendered as a LEAF - one constant plus its over-life curve - which was
/// right for the constant and silently dropped the probability tables. Those tables are the
/// per-particle randomisation: a rate of 10 with keys (0,0) (0.98,0) (1,2) is an emitter that idles
/// and then bursts, and with the table hidden it reads as a steady trickle of 10. The preview applies
/// them (M47), so a row here is live rather than cosmetic.</para>
/// </summary>
public sealed class ParticleSpreadRowTests
{
    private static uint H(string s) => ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a(s);

    private static BinTreeProperty Table(params (float T, float V)[] keys) =>
        new BinTreeStruct(0, H("VfxProbabilityTableData"), keys.Length == 0
            ? Array.Empty<BinTreeProperty>()
            : new BinTreeProperty[]
            {
                new BinTreeContainer(H("keyTimes"), BinPropertyType.F32,
                    keys.Select(k => (BinTreeProperty)new BinTreeF32(0, k.T)).ToArray()),
                new BinTreeContainer(H("keyValues"), BinPropertyType.F32,
                    keys.Select(k => (BinTreeProperty)new BinTreeF32(0, k.V)).ToArray()),
            });

    /// <summary>A one-emitter system whose <c>rate</c> carries the given tables.</summary>
    private static ParticleDocument Document(params BinTreeProperty[] tables)
    {
        var dynamics = new BinTreeStruct(H("dynamics"), H("VfxAnimatedFloatVariableData"),
            new BinTreeProperty[]
            {
                new BinTreeContainer(H("probabilityTables"), BinPropertyType.Struct, tables),
                new BinTreeContainer(H("times"), BinPropertyType.F32,
                    new BinTreeProperty[] { new BinTreeF32(0, 0f) }),
                new BinTreeContainer(H("values"), BinPropertyType.F32,
                    new BinTreeProperty[] { new BinTreeF32(0, 10f) }),
            });

        var emitter = new BinTreeStruct(0, H("VfxEmitterDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("emitterName"), "e"),
            new BinTreeEmbedded(H("rate"), H("ValueFloat"), new BinTreeProperty[]
            {
                new BinTreeF32(H("constantValue"), 10f),
                dynamics,
            }),
        });

        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), "sys"),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct,
                new BinTreeProperty[] { emitter }),
        });

        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        var doc = ParticleDocument.Parse(ms.ToArray(), Name);
        Assert.NotNull(doc);
        return doc!;
    }

    /// <summary>The names this test needs resolved; the real host passes its hash dictionary.</summary>
    private static string? Name(uint hash)
    {
        foreach (string n in new[]
        {
            "emitterName", "rate", "constantValue", "dynamics", "probabilityTables",
            "keyTimes", "keyValues", "times", "values", "particleName", "complexEmitterDefinitionData",
        })
            if (H(n) == hash) return n;
        return null;
    }

    private static IReadOnlyList<ParticleProperty> Rows(ParticleDocument doc) =>
        doc.Systems[0].Emitters[0].Properties;

    [Fact]
    public void AScalarTableBecomesOneSpreadRowUnderItsField()
    {
        var doc = Document(Table((0f, 0f), (0.98f, 0f), (1f, 2f)));
        var rows = Rows(doc);

        int rate = rows.ToList().FindIndex(r => r.Name == "rate");
        Assert.True(rate >= 0);

        // immediately under its field, indented one level - a spread that floated elsewhere on the card
        // would read as belonging to whatever it landed next to
        var spread = rows[rate + 1];
        Assert.Equal("spread", spread.Name);
        Assert.Equal(rows[rate].Depth + 1, spread.Depth);
        Assert.Equal(rows[rate].Module, spread.Module);

        Assert.Equal(new[] { 0f, 0.98f, 1f }, spread.CurveTimes);
        Assert.Equal(new[] { 0f, 0f, 2f }, spread.CurveChannels![0]);
    }

    [Fact]
    public void ThreeTablesAreLabelledByAxis()
    {
        // The container is read by INDEX, so the label has to come from position - "spread Y" is the
        // second element whether or not the first has keys.
        var doc = Document(Table(), Table((0f, 1f), (1f, 2f)), Table());
        var spreads = Rows(doc).Where(r => r.Name.StartsWith("spread")).ToList();

        // an EMPTY table gets no row: there is nothing to show and nothing to edit
        Assert.Single(spreads);
        Assert.Equal("spread Y", spreads[0].Name);
        Assert.Equal(new[] { 0f, 1f }, spreads[0].CurveTimes);
    }

    [Fact]
    public void AFieldWithNoTablesGainsNoRows()
    {
        var doc = Document();
        Assert.DoesNotContain(Rows(doc), r => r.Name.StartsWith("spread"));
    }

    [Fact]
    public void TheSpreadRowHasNoScalarValueButItsKeysAreEditable()
    {
        var doc = Document(Table((0f, 1f), (1f, 3f)));
        var spread = Rows(doc).Single(r => r.Name == "spread");

        // No scalar to type into - a table IS its keys - but the key editor has to reach them, which it
        // could not while the pair was hardcoded to times/values.
        Assert.True(spread.IsReadOnly);
        Assert.Contains("probability", spread.ReadOnlyReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(spread.HasCurve);
        Assert.True(spread.CanEditCurve);
    }

    [Fact]
    public void EditingASpreadKeyReachesTheBin()
    {
        var doc = Document(Table((0f, 1f), (1f, 3f)));
        var spread = Rows(doc).Single(r => r.Name == "spread");

        spread.SetCurveKey(1, 1f, new[] { 5f });
        Assert.Equal(new[] { 1f, 5f }, spread.CurveChannels![0]);

        // and it survives the round trip, which is the whole point - an edit that only moved the
        // display was the M190 bug this machinery was built to fix
        var reloaded = ParticleDocument.Parse(doc.Serialize(), Name);
        var after = reloaded!.Systems[0].Emitters[0].Properties.Single(r => r.Name == "spread");
        Assert.Equal(new[] { 1f, 5f }, after.CurveChannels![0]);
    }

    [Fact]
    public void AddingAndRemovingSpreadKeysWorksTheSameAsOnACurve()
    {
        var doc = Document(Table((0f, 1f), (1f, 3f)));
        var spread = Rows(doc).Single(r => r.Name == "spread");

        spread.AddCurveKey(0.5f, new[] { 2f });
        Assert.Equal(new[] { 0f, 0.5f, 1f }, spread.CurveTimes);   // kept ascending
        Assert.Equal(new[] { 1f, 2f, 3f }, spread.CurveChannels![0]);

        spread.RemoveCurveKey(1);
        Assert.Equal(new[] { 0f, 1f }, spread.CurveTimes);
    }

    [Fact]
    public void TheOverLifeCurveAndTheSpreadAreDifferentRows()
    {
        // Both hang off the same Value* struct and both are two parallel float lists, so it would be
        // easy for one to overwrite the other. times/values belongs to the field row; keyTimes/keyValues
        // to the spread row.
        var doc = Document(Table((0f, 1f), (1f, 3f)));
        var rows = Rows(doc);

        var rate = rows.Single(r => r.Name == "rate");
        Assert.Equal(new[] { 0f }, rate.CurveTimes);          // times/values: one key holding the constant
        Assert.Equal(new[] { 10f }, rate.CurveChannels![0]);

        var spread = rows.Single(r => r.Name == "spread");
        Assert.Equal(new[] { 0f, 1f }, spread.CurveTimes);

        spread.SetCurveKey(0, 0f, new[] { 9f });
        Assert.Equal(new[] { 10f }, rate.CurveChannels![0]);   // the field's own curve is untouched
    }
}

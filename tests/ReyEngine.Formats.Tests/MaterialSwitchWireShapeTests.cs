using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M588: how an ENABLED static switch is written.
///
/// <para>Riot expresses "on" by leaving the <c>on</c> field out. Measured over every shipped map WAD:
/// of <b>23,338</b> StaticMaterialSwitchDef entries across 12,722 bins, <b>0</b> write
/// <c>on: bool = true</c> and 9,385 omit the field; MULTIPLY_ALPHA on its own is 0 true / 55 false /
/// 3,527 absent. The editor wrote the explicit true, which is a shape the corpus never contains — the
/// same class of divergence as the plain-Container switches block that made a client silently drop
/// MULTIPLY_ALPHA (M507) and the pointer container elements that loaded everywhere and rendered nowhere
/// (M416).</para>
///
/// <para>It is safe to omit because the reader resolves an absent 'on' to enabled, which is what the
/// round trip below pins.</para>
/// </summary>
public sealed class MaterialSwitchWireShapeTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private static readonly uint OnRaw = HashAlgorithms.Fnv1aRaw("on");
    private static readonly uint OnCanonical = H("on");

    /// <param name="switches">Name plus how 'on' is authored: null = field absent, otherwise explicit.</param>
    private static MaterialDocument Document(params (string Name, bool? On)[] switches)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), "Mat"),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded,
                new BinTreeProperty[]
                {
                    new BinTreeEmbedded(0, 0x0904b150, new BinTreeProperty[]
                    {
                        new BinTreeString(H("TextureName"), "DiffuseTexture"),
                        new BinTreeString(H("texturePath"), "assets/a.tex"),
                    }),
                }),
        };

        if (switches.Length > 0)
            props.Add(new BinTreeUnorderedContainer(H("switches"), BinPropertyType.Embedded,
                switches.Select(s =>
                {
                    var fields = new List<BinTreeProperty> { new BinTreeString(H("name"), s.Name) };
                    if (s.On is { } on) fields.Add(new BinTreeBool(OnRaw, on));
                    return (BinTreeProperty)new BinTreeEmbedded(0, H("StaticMaterialSwitchDef"), fields);
                }).ToList()));

        using var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(H("Mat"), H("StaticMaterialDef"), props) },
            Array.Empty<string>()).Write(ms);
        var names = new Dictionary<uint, string>
        {
            [H("StaticMaterialDef")] = "StaticMaterialDef",
            [H("StaticMaterialSwitchDef")] = "StaticMaterialSwitchDef",
        };
        return MaterialDocument.Parse(ms.ToArray(), h => names.TryGetValue(h, out var n) ? n : null);
    }

    /// <summary>How the named switch's 'on' field is authored in <paramref name="bytes"/>: null = absent.</summary>
    private static bool? AuthoredOn(byte[] bytes, string switchName)
    {
        using var ms = new MemoryStream(bytes);
        var tree = new BinTree(ms);
        foreach (var o in tree.Objects.Values)
        {
            if (!o.Properties.TryGetValue(H("switches"), out var prop) || prop is not BinTreeContainer c) continue;
            foreach (var el in c.Elements)
            {
                if (el is not BinTreeStruct st) continue;
                if (st.Properties.GetValueOrDefault(H("name")) is not BinTreeString n || n.Value != switchName) continue;
                foreach (uint h in new[] { OnRaw, OnCanonical })
                    if (st.Properties.TryGetValue(h, out var on))
                        return on switch { BinTreeBool b => b.Value, BinTreeBitBool bb => bb.Value, _ => null };
                return null;
            }
        }
        throw new Xunit.Sdk.XunitException($"no switch named '{switchName}' was written at all");
    }

    [Fact]
    public void AnEnabledSwitchIsWrittenWithNoOnField()
    {
        // The regression: AddSwitch turned it on by writing `on: bool = true`, which 0 of 23,338 shipped
        // entries do.
        var doc = Document();
        var sw = doc.Materials.Single().AddSwitch("MULTIPLY_ALPHA");
        Assert.NotNull(sw);
        Assert.True(sw!.On);

        Assert.Null(AuthoredOn(doc.Serialize(), "MULTIPLY_ALPHA"));
    }

    [Fact]
    public void ADisabledSwitchStillWritesTheFieldExplicitly()
    {
        // Off is the case that NEEDS the field: absent means enabled, so omitting it would invert it.
        var doc = Document();
        doc.Materials.Single().AddSwitch("MULTIPLY_ALPHA")!.SetOn(false);

        Assert.False(AuthoredOn(doc.Serialize(), "MULTIPLY_ALPHA"));
    }

    [Fact]
    public void TurningOneOfRiotsDisabledSwitchesBackOnRemovesTheField()
    {
        var doc = Document(("USE_DIFFUSE_TEXTURE", false));
        var sw = doc.Materials.Single().AllSwitches.Single();
        Assert.False(sw.On);
        sw.SetOn(true);

        Assert.Null(AuthoredOn(doc.Serialize(), "USE_DIFFUSE_TEXTURE"));
    }

    [Fact]
    public void AnEnabledSwitchStillReadsBackAsEnabled()
    {
        // What the explicit write used to be protecting. It was already covered by the reader, which
        // resolves an absent 'on' to true — so omitting the field loses nothing.
        var doc = Document();
        doc.Materials.Single().AddSwitch("MULTIPLY_ALPHA");

        var reread = MaterialDocument.Parse(doc.Serialize(), _ => "StaticMaterialDef");
        Assert.True(reread.Materials.Single().Switches["MULTIPLY_ALPHA"]);
    }

    [Fact]
    public void TogglingOffAndOnAgainLeavesNoField()
    {
        // A user ticking the box twice must not leave the shape different from where it started.
        var doc = Document();
        var sw = doc.Materials.Single().AddSwitch("MULTIPLY_ALPHA")!;
        sw.SetOn(false);
        sw.SetOn(true);

        Assert.Null(AuthoredOn(doc.Serialize(), "MULTIPLY_ALPHA"));
        Assert.True(MaterialDocument.Parse(doc.Serialize(), _ => "StaticMaterialDef")
            .Materials.Single().Switches["MULTIPLY_ALPHA"]);
    }

    [Fact]
    public void RevertingAnEditedSwitchRestoresHowRiotAuthoredIt()
    {
        var doc = Document(("USE_DIFFUSE_TEXTURE", false), ("SS_MASK", null));
        var material = doc.Materials.Single();

        var off = material.AllSwitches.Single(s => s.Name == "USE_DIFFUSE_TEXTURE");
        var on = material.AllSwitches.Single(s => s.Name == "SS_MASK");
        off.SetOn(true);
        on.SetOn(false);
        off.Revert();
        on.Revert();

        byte[] bytes = doc.Serialize();
        Assert.False(AuthoredOn(bytes, "USE_DIFFUSE_TEXTURE"));
        Assert.Null(AuthoredOn(bytes, "SS_MASK"));
    }
}

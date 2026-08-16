using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M506: one define, one row.
///
/// <para>The material editor rebuilds its macro list straight from <see cref="MaterialBinding.AllMacros"/>,
/// so anything that list yields twice is a define the user sees twice — with two tick boxes and two remove
/// buttons for one map entry, disagreeing about whether it is on. Switching shaders is the way in: the
/// common-setup pass removes every macro the new shader's setup does not list, and a later shader whose
/// setup DOES list it adds it back.</para>
///
/// <para>Switches never had this problem because <see cref="MaterialBinding.AllSwitches"/> filters on the
/// element still being in the container. Macros are map entries with no element to check, so the
/// bookkeeping has to be done where the entry is written.</para>
/// </summary>
public sealed class MaterialMacroIdentityTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static MaterialBinding Material(params (string Name, string Value)[] macros)
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
        if (macros.Length > 0)
            props.Add(new BinTreeMap(H("shaderMacros"), BinPropertyType.String, BinPropertyType.String,
                macros.Select(m => new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                    new BinTreeString(0, m.Name), new BinTreeString(0, m.Value))).ToList()));

        using var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(H("Mat"), H("StaticMaterialDef"), props) },
            Array.Empty<string>()).Write(ms);
        var names = new Dictionary<uint, string> { [H("StaticMaterialDef")] = "StaticMaterialDef" };
        return MaterialDocument.Parse(ms.ToArray(), h => names.TryGetValue(h, out var n) ? n : null)
            .Materials.Single();
    }

    [Fact]
    public void RemovingAMacroAndSettingItAgainLeavesOneEntry()
    {
        // Exactly what two shader changes do: the first shader's setup does not list the macro, the
        // second one does.
        var m = Material(("NO_BAKED_LIGHTING", "1"));

        Assert.True(m.RemoveMacro("NO_BAKED_LIGHTING"));
        Assert.Empty(m.AllMacros);

        m.SetMacroValue("NO_BAKED_LIGHTING", "1");

        var live = m.AllMacros.ToList();
        Assert.Single(live);
        Assert.Equal("1", live[0].Value);
        Assert.Equal("1", m.Macros["NO_BAKED_LIGHTING"]);
    }

    [Fact]
    public void TheSurvivingEntryStillTracksLaterEdits()
    {
        // Dedup is only correct if the object that survives is the one wired to the map — otherwise the
        // tick box would move and the bin would not.
        var m = Material(("NO_BAKED_LIGHTING", "1"));
        m.RemoveMacro("NO_BAKED_LIGHTING");
        m.SetMacroValue("NO_BAKED_LIGHTING", "1");

        m.SetMacro("NO_BAKED_LIGHTING", false);
        Assert.Equal("0", Assert.Single(m.AllMacros).Value);
        Assert.False(m.MacroOn("NO_BAKED_LIGHTING"));

        m.SetMacro("NO_BAKED_LIGHTING", true);
        Assert.Equal("1", Assert.Single(m.AllMacros).Value);
        Assert.True(m.MacroOn("NO_BAKED_LIGHTING"));
    }

    [Fact]
    public void TheBinKeepsOneMapEntryToo()
    {
        // A second key with the same name would be a bin the client reads differently from the editor.
        var m = Material(("NO_BAKED_LIGHTING", "1"), ("DISABLE_DEPTH_FOG", "1"));
        m.RemoveMacro("NO_BAKED_LIGHTING");
        m.SetMacroValue("NO_BAKED_LIGHTING", "0");

        Assert.Equal(2, m.Macros.Count);
        Assert.Equal("0", m.Macros["NO_BAKED_LIGHTING"]);
        Assert.Equal("1", m.Macros["DISABLE_DEPTH_FOG"]);
    }

    [Fact]
    public void AMacroAddedTwiceIsStillOneEntry()
    {
        var m = Material();
        m.SetMacro("NO_BAKED_LIGHTING", true);
        m.SetMacro("NO_BAKED_LIGHTING", true);
        Assert.Single(m.AllMacros);
    }

    [Fact]
    public void SwitchesAlreadyBehaveAndMustKeepBehaving()
    {
        // The reference implementation for the rule above — pinned so a later change cannot regress the
        // half that was right.
        var m = Material();
        var sw = m.AddSwitch("USE_MASK");
        Assert.NotNull(sw);
        Assert.True(m.RemoveSwitch(sw!));
        Assert.Empty(m.AllSwitches);

        m.AddSwitch("USE_MASK");
        Assert.Single(m.AllSwitches);
    }
}

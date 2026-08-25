using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M588: the way through to NO_BAKED_LIGHTING.
///
/// <para>On <c>DefaultEnv_Flat_AlphaTest</c> the macro alone is not a cooked permutation, so every caller
/// refused it — on a real ported map that meant all 88 materials refused and no way through at all. It
/// becomes cooked with <c>MULTIPLY_ALPHA</c> on, which <see cref="ShaderPermutationIndex.SuggestFixes"/>
/// has been naming all along.</para>
///
/// <para>These run against the installed shader cache and skip without one, because the whole point is
/// what the GAME cooked — a fake oracle would only test the branch structure and it is the permutation
/// data that decides every answer here.</para>
/// </summary>
public sealed class NoBakedLightingFixTests
{
    private const string GameFinal = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string FlatAlphaTest = "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest";

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static string? Resolve(uint h)
    {
        foreach (string n in new[]
        {
            "StaticMaterialDef", "name", "techniques", "passes", "shader", "samplerValues", "TextureName",
            "texturePath", "StaticMaterialTechniqueDef", "StaticMaterialPassDef", "paramValues",
            "StaticMaterialShaderParamDef", "value", "switches", "StaticMaterialSwitchDef", "on",
            "blendEnable", "srcColorBlendFactor", "dstColorBlendFactor", "srcAlphaBlendFactor",
            "dstAlphaBlendFactor", "shaderMacros", "type", FlatAlphaTest,
        })
            if (H(n) == h) return n;
        return null;
    }

    /// <summary>A one-material bin shaped like what the legacy porter writes.</summary>
    private static MaterialDocument Document(bool blended, int srcFactor = 6)
    {
        var pass = new List<BinTreeProperty> { new BinTreeObjectLink(H("shader"), H(FlatAlphaTest)) };
        if (blended)
        {
            pass.Add(new BinTreeBool(H("blendEnable"), true));
            pass.Add(new BinTreeU32(H("srcColorBlendFactor"), (uint)srcFactor));
            pass.Add(new BinTreeU32(H("srcAlphaBlendFactor"), (uint)srcFactor));
            pass.Add(new BinTreeU32(H("dstColorBlendFactor"), 7));
            pass.Add(new BinTreeU32(H("dstAlphaBlendFactor"), 7));
        }

        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), "Legacy/imported_mesh"),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, 0x0904b150, new BinTreeProperty[]
                {
                    new BinTreeString(H("TextureName"), "DiffuseTexture"),
                    new BinTreeString(H("texturePath"), "assets/t.tex"),
                }),
            }),
            new BinTreeContainer(H("techniques"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
                {
                    new BinTreeString(H("name"), "normal"),
                    new BinTreeContainer(H("passes"), BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, H("StaticMaterialPassDef"), pass),
                    }),
                }),
            }),
        };

        using var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(H("Mat"), H("StaticMaterialDef"), props) },
            Array.Empty<string>()).Write(ms);
        return MaterialDocument.Parse(ms.ToArray(), Resolve);
    }

    private static ShaderPermutationIndex? Perms()
    {
        if (!File.Exists(Path.Combine(GameFinal, "ShaderCache.dx11.wad.client"))) return null;
        var index = new ShaderPermutationIndex(GameFinal);
        return index.IsAvailable ? index : null;
    }

    [Fact]
    public void ABlendedMaterialGetsTheCompanionAndComesOutCooked()
    {
        if (Perms() is not { } perms) return;   // no game install
        var doc = Document(blended: true);
        var m = doc.Materials.Single();

        // The precondition this whole class exists for: the macro alone is refused.
        Assert.False(perms.CanSetMacro(m, MaterialBinding.MacroNoBakedLighting, "1"));

        Assert.Equal(NoBakedLightingOutcome.SetWithCompanion, NoBakedLightingFix.Apply(m, perms));

        Assert.True(m.MacroOn(MaterialBinding.MacroNoBakedLighting));
        Assert.True(m.Switches[NoBakedLightingFix.CompanionSwitch]);
        Assert.True(perms.TryExactKey(m, out _, out bool cooked, out _));
        Assert.True(cooked);
    }

    [Fact]
    public void TheSourceBlendFactorMovesToOneSoThePremultipliedOutputStillComposites()
    {
        if (Perms() is not { } perms) return;
        var doc = Document(blended: true);
        var m = doc.Materials.Single();
        Assert.Equal(6, m.GetPassU32("srcColorBlendFactor"));   // SourceAlpha, as the porter writes it

        NoBakedLightingFix.Apply(m, perms);

        // Absent, not 1: Riot expresses One by absence in 8,545 of 9,800 shipped passes and writes an
        // explicit 1 in none of them.
        Assert.Equal(-1, m.GetPassU32("srcColorBlendFactor"));
        Assert.Equal(-1, m.GetPassU32("srcAlphaBlendFactor"));
        Assert.Equal(7, m.GetPassU32("dstColorBlendFactor"));   // the destination side is untouched
    }

    [Fact]
    public void AnOpaqueMaterialIsRefusedRatherThanDarkened()
    {
        if (Perms() is not { } perms) return;
        var doc = Document(blended: false);
        var m = doc.Materials.Single();

        Assert.Equal(NoBakedLightingOutcome.RefusedOpaque, NoBakedLightingFix.Apply(m, perms));

        // M542: the companion premultiplies the output and an opaque draw has no blend to absorb it, so
        // it would darken by its own alpha. Nothing may have been touched.
        Assert.False(m.MacroOn(MaterialBinding.MacroNoBakedLighting));
        Assert.Empty(m.AllSwitches);
    }

    [Fact]
    public void AMaterialAlreadyUnlitIsLeftExactlyAlone()
    {
        if (Perms() is not { } perms) return;
        var doc = Document(blended: true);
        var m = doc.Materials.Single();
        NoBakedLightingFix.Apply(m, perms);
        int switchesAfterFirst = m.AllSwitches.Count();

        Assert.Equal(NoBakedLightingOutcome.AlreadySet, NoBakedLightingFix.Apply(m, perms));
        Assert.Equal(switchesAfterFirst, m.AllSwitches.Count());
    }

    [Fact]
    public void AFactorAlreadyAtOneIsNotDisturbed()
    {
        if (Perms() is not { } perms) return;
        // The premultiplied family Riot ships 3,104 times: source already One, so there is nothing to move.
        var doc = Document(blended: true, srcFactor: 1);
        var m = doc.Materials.Single();

        Assert.Equal(NoBakedLightingOutcome.SetWithCompanion, NoBakedLightingFix.Apply(m, perms));
        Assert.Equal(1, m.GetPassU32("srcColorBlendFactor"));
    }

    [Fact]
    public void WithNoShaderCacheNothingIsWritten()
    {
        // Runs everywhere: the fail-safe direction must hold on a machine with no game install, which is
        // exactly where a blind write would go unnoticed.
        var doc = Document(blended: true);
        var m = doc.Materials.Single();
        var none = new ShaderPermutationIndex(Path.Combine(Path.GetTempPath(), "reyengine-no-such-game"));
        Assert.False(none.IsAvailable);

        Assert.Equal(NoBakedLightingOutcome.RefusedNotCooked, NoBakedLightingFix.Apply(m, none));
        Assert.False(m.MacroOn(MaterialBinding.MacroNoBakedLighting));
        Assert.Empty(m.AllSwitches);
    }

    [Fact]
    public void TheResultIsAShapeTheModValidatorAccepts()
    {
        if (Perms() is not { } perms) return;
        var doc = Document(blended: true);
        NoBakedLightingFix.Apply(doc.Materials.Single(), perms);

        byte[] bytes = doc.Serialize();
        Assert.Empty(ModShapeValidator.ValidateBin(SafeBinTree.Parse(bytes), bytes, Resolve));

        var reread = MaterialDocument.Parse(bytes, Resolve);
        var back = reread.Materials.Single();
        Assert.Empty(back.MistaggedContainers);          // a mistagged switches block is silently dropped
        Assert.True(back.MacroOn(MaterialBinding.MacroNoBakedLighting));
        Assert.True(back.Switches[NoBakedLightingFix.CompanionSwitch]);
    }
}

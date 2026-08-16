using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using MacroSupport = ReyEngine.Formats.MapGeo.LegacyMapPorter.MacroSupport;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M506: the material editor's inline permutation verdicts.
///
/// <para>These are view models, and the test project already references ReyEngine.App — the reason nothing
/// covered this layer before was habit, not tooling. The behaviour is worth pinning because it encodes the
/// M486/M502 distinction in the one place a user acts on it: a define the game never cooked must not be a
/// working button, and a define the shader ignores must not read like one that breaks the map.</para>
/// </summary>
public sealed class MacroSupportBadgeTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private sealed record Fixture(MaterialEditorViewModel Editor, MaterialDocument Doc,
        MaterialBindingViewModel Binding)
    {
        /// <summary>Rebuild the rows from the model, the way the editor does after a shader change.</summary>
        public MaterialBindingViewModel Reload()
        {
            Editor.Load(Doc, new WadAssetEntry { Path = "data/maps/materials.bin" });
            var binding = Editor.Materials.Single();
            binding.RefreshMacroStatus();
            return binding;
        }
    }

    /// <summary>An editor holding one material on <paramref name="shader"/>, with a stub shader cache.</summary>
    private static MaterialBindingViewModel Editor(string shader,
        Func<string, IReadOnlyDictionary<string, bool>, string, string, MacroSupport>? ask,
        params (string Name, string Value)[] macros) => Load(shader, ask, macros).Binding;

    private static Fixture Load(string shader,
        Func<string, IReadOnlyDictionary<string, bool>, string, string, MacroSupport>? ask,
        params (string Name, string Value)[] macros)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), "Mat"),
            new BinTreeContainer(H("techniques"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
                {
                    new BinTreeContainer(H("passes"), BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
                        {
                            new BinTreeObjectLink(H("shader"), H(shader)),
                        }),
                    }),
                }),
            }),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded, new BinTreeProperty[]
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
        var names = new Dictionary<uint, string>
        {
            [H("StaticMaterialDef")] = "StaticMaterialDef",
            [H(shader)] = shader,
        };
        var doc = MaterialDocument.Parse(ms.ToArray(), h => names.TryGetValue(h, out var n) ? n : null);

        var editor = new MaterialEditorViewModel { AskMacroSupport = ask };
        editor.Load(doc, new WadAssetEntry { Path = "data/maps/materials.bin" });
        var binding = editor.Materials.Single();
        binding.RefreshMacroStatus();
        return new Fixture(editor, doc, binding);
    }

    private static Func<string, IReadOnlyDictionary<string, bool>, string, string, MacroSupport> Cache(
        MacroSupport verdict) => (_, _, _, _) => verdict;

    [Fact]
    public void AMacroTheGameNeverCookedIsNotOfferedAsAOneClickButton()
    {
        // M486: writing it makes the client fail to compile the shader and the map renders nothing. An
        // enabled button here is a trap, so the chip is disabled and says why.
        var binding = Editor("Shaders/AlphaTest", Cache(MacroSupport.NotCooked));

        var chip = binding.MissingMacros.Single(c => c.Name == MaterialBinding.MacroNoBakedLighting);
        Assert.False(chip.CanAdd);
        Assert.True(chip.IsFatal);
        Assert.False(chip.IsWarning);
        Assert.Equal("would crash", chip.StatusLabel);
        Assert.Contains("no cooked permutation", chip.Tip);
    }

    [Fact]
    public void AMacroTheShaderIgnoresReadsDifferentlyFromOneThatBreaksTheMap()
    {
        // M502: an undeclared axis is INERT — harmless to the game, but the material would claim something
        // the shader will not do. Showing it in the same red as the crash would train the user past both.
        var binding = Editor("Shaders/FourBlend", Cache(MacroSupport.NotDeclared));

        var chip = binding.MissingMacros.Single(c => c.Name == MaterialBinding.MacroNoBakedLighting);
        Assert.True(chip.CanAdd);
        Assert.False(chip.IsFatal);
        Assert.True(chip.IsWarning);
        Assert.Equal("ignored", chip.StatusLabel);
        Assert.Contains("ignores", chip.Tip);
    }

    [Fact]
    public void ACookedMacroCarriesNoBadgeAtAll()
    {
        var binding = Editor("Shaders/Flat", Cache(MacroSupport.Cooked));

        var chip = binding.MissingMacros.Single(c => c.Name == MaterialBinding.MacroNoBakedLighting);
        Assert.True(chip.CanAdd);
        Assert.False(chip.HasStatus);
        Assert.Equal("", chip.StatusLabel);
    }

    [Fact]
    public void WithNoShaderCacheEverythingReadsUncheckedRatherThanSafe()
    {
        // The rule the audit and the preset applier already follow: a check with no way to answer is
        // skipped and SAID to be skipped, never guessed into an all-clear.
        var binding = Editor("Shaders/Flat", ask: null);

        var chip = binding.MissingMacros.Single(c => c.Name == MaterialBinding.MacroNoBakedLighting);
        Assert.Equal("unchecked", chip.StatusLabel);
        Assert.True(chip.CanAdd);          // unknown must not block the user
        Assert.False(chip.IsFatal);
    }

    [Fact]
    public void AnAuthoredMacroGetsTheSameVerdictAsTheChipWould()
    {
        // The material the porter wrote is the one that matters: NO_BAKED_LIGHTING is already ON, and the
        // editor has to say the shader cannot take it.
        var binding = Editor("Shaders/AlphaTest", Cache(MacroSupport.NotCooked),
            (MaterialBinding.MacroNoBakedLighting, "1"));

        var row = binding.Macros.Single();
        Assert.Equal(MaterialBinding.MacroNoBakedLighting, row.Name);
        Assert.True(row.IsOn);
        Assert.True(row.IsFatal);
        Assert.Equal("would crash", row.StatusLabel);

        // ...and it is not ALSO offered as a chip.
        Assert.DoesNotContain(binding.MissingMacros,
            c => c.Name == MaterialBinding.MacroNoBakedLighting);
    }

    [Fact]
    public void TheTooltipSaysBothWhatTheDefineDoesAndWhatTheShaderWillDoWithIt()
    {
        var binding = Editor("Shaders/AlphaTest", Cache(MacroSupport.NotCooked),
            (MaterialBinding.MacroNoBakedLighting, "1"));

        string tip = binding.Macros.Single().StatusTip;
        Assert.Contains("baked lightmap", tip);            // what it does
        Assert.Contains("fail to compile", tip);           // what happens if you keep it
    }

    [Fact]
    public void AddingFromAChipMovesItOutOfTheOfferedListAndIntoTheRows()
    {
        var binding = Editor("Shaders/Flat", Cache(MacroSupport.Cooked));
        var chip = binding.MissingMacros.Single(c => c.Name == MaterialBinding.MacroDisableDepthFog);

        binding.AddMacroCommand.Execute(chip);

        Assert.Contains(binding.Macros, m => m.Name == MaterialBinding.MacroDisableDepthFog);
        Assert.DoesNotContain(binding.MissingMacros, c => c.Name == MaterialBinding.MacroDisableDepthFog);
        Assert.Equal("1", binding.Model.Macros[MaterialBinding.MacroDisableDepthFog]);

        // Removing puts it back on offer — absent is the shader's own default, not "0".
        binding.RemoveMacro(binding.Macros.Single(m => m.Name == MaterialBinding.MacroDisableDepthFog));
        Assert.DoesNotContain(binding.Macros, m => m.Name == MaterialBinding.MacroDisableDepthFog);
        Assert.Contains(binding.MissingMacros, c => c.Name == MaterialBinding.MacroDisableDepthFog);
    }

    [Fact]
    public void AChipTheGameNeverCookedDoesNothingEvenIfItsCommandIsInvoked()
    {
        // IsEnabled on the button is presentation. The refusal has to live in the command too, or a
        // keyboard route or a future caller writes the crash anyway.
        var binding = Editor("Shaders/AlphaTest", Cache(MacroSupport.NotCooked));
        var chip = binding.MissingMacros.Single(c => c.Name == MaterialBinding.MacroNoBakedLighting);

        binding.AddMacroCommand.Execute(chip);

        Assert.Empty(binding.Macros);
        Assert.Empty(binding.Model.AllMacros);
    }

    [Fact]
    public void TheVerdictIsAskedForPerShaderAndPerValue()
    {
        // Both are part of the permutation key. Caching a verdict against the macro NAME is the shape of
        // mistake M486 and M502 each made once.
        var asked = new List<string>();
        var binding = Editor("Shaders/Flat",
            (shader, switches, macro, value) =>
            {
                asked.Add($"{shader}|{macro}|{value}");
                return MacroSupport.Cooked;
            },
            (MaterialBinding.MacroNoBakedLighting, "1"));

        Assert.Contains($"Shaders/Flat|{MaterialBinding.MacroNoBakedLighting}|1", asked);

        asked.Clear();
        binding.Macros.Single().IsOn = false;      // now a different permutation key
        Assert.Contains($"Shaders/Flat|{MaterialBinding.MacroNoBakedLighting}|0", asked);
    }

    [Fact]
    public void SwitchingTheShaderDoesNotDuplicateAMacroRow()
    {
        // The bug this milestone started from: two shader changes removed the macro and added it back, and
        // the editor drew the define twice with two tick boxes disagreeing about whether it was on.
        var fixture = Load("Shaders/Flat", Cache(MacroSupport.Cooked),
            (MaterialBinding.MacroNoBakedLighting, "1"));

        fixture.Binding.Model.RemoveMacro(MaterialBinding.MacroNoBakedLighting);       // shader A omits it
        fixture.Binding.Model.SetMacroValue(MaterialBinding.MacroNoBakedLighting, "1"); // shader B has it

        var rebuilt = fixture.Reload();
        Assert.Single(rebuilt.Model.AllMacros);
        Assert.Single(rebuilt.Macros);
        Assert.True(rebuilt.Macros.Single().IsOn);
    }
}

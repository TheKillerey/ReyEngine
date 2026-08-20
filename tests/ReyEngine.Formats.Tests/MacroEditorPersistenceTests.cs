using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using MacroSupport = ReyEngine.Formats.MapGeo.LegacyMapPorter.MacroSupport;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// BUG 2 investigation: "adding NO_BAKED_LIGHTING through the Material Editor shows up but never reaches
/// the .bin". These pin which of the three possible stories is true for the editor's own add path —
/// accepted-and-persisted, refused-and-said-so, or refused-silently.
/// </summary>
public sealed class MacroEditorPersistenceTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>One StaticMaterialDef on <paramref name="shader"/> with no shaderMacros map at all — the
    /// shape every material in the user's Map453 bin has.</summary>
    private static byte[] OneMaterial(string shader)
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
                new BinTreeEmbedded(0, H("StaticMaterialShaderSamplerDef"), new BinTreeProperty[]
                {
                    new BinTreeString(H("TextureName"), "DiffuseTexture"),
                    new BinTreeString(H("texturePath"), "assets/a.tex"),
                }),
            }),
        };
        using var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(H("Mat"), H("StaticMaterialDef"), props) },
            Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static Func<uint, string?> Names(string shader)
    {
        var map = new Dictionary<uint, string>
        {
            [H("StaticMaterialDef")] = "StaticMaterialDef",
            [H("StaticMaterialShaderSamplerDef")] = "StaticMaterialShaderSamplerDef",
            [H(shader)] = shader,
        };
        return h => map.TryGetValue(h, out var n) ? n : null;
    }

    private static (MaterialEditorViewModel Editor, MaterialBindingViewModel Binding) Editor(
        string shader, MacroSupport verdict)
    {
        var doc = MaterialDocument.Parse(OneMaterial(shader), Names(shader));
        var editor = new MaterialEditorViewModel { AskMacroSupport = (_, _, _, _) => verdict };
        editor.Load(doc, new WadAssetEntry { Path = "data/maps/mapgeometry/map453/jade_container.materials.bin" });
        var binding = editor.Materials.Single();
        binding.RefreshMacroStatus();          // what OnSelectedMaterialChanged does
        return (editor, binding);
    }

    /// <summary>The editor's add path DOES reach the bytes when the shader cache allows it. This is the
    /// control: if this failed, the model layer would be the bug.</summary>
    [Fact]
    public void AddingTheMacroThroughTheEditorReachesTheSerializedBin()
    {
        const string shader = "Shaders/StaticMesh/Indicator_Faelights";
        var (editor, binding) = Editor(shader, MacroSupport.Cooked);

        var chip = binding.MissingMacros.Single(c => c.Name == MaterialBinding.MacroNoBakedLighting);
        Assert.True(chip.CanAdd);

        binding.AddMacroCommand.Execute(chip);

        Assert.Contains(binding.Macros, m => m.Name == MaterialBinding.MacroNoBakedLighting && m.IsOn);
        Assert.True(editor.IsDirty);

        var bytes = editor.Serialize();
        Assert.NotNull(bytes);
        var reparsed = MaterialDocument.Parse(bytes!, Names(shader));
        Assert.True(reparsed.Materials.Single().MacroOn(MaterialBinding.MacroNoBakedLighting));
    }

    /// <summary>The refusal case. The chip is disabled and carries a red "would crash" badge — but the
    /// command itself is a silent no-op, so a click that gets through (or any caller that does not check
    /// CanAdd first) changes nothing, marks nothing dirty and reports nothing anywhere but the chip.</summary>
    [Fact]
    public void AMacroTheGameNeverCookedIsRefusedAndNeverReachesTheBin()
    {
        const string shader = "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest";
        var (editor, binding) = Editor(shader, MacroSupport.NotCooked);

        var chip = binding.MissingMacros.Single(c => c.Name == MaterialBinding.MacroNoBakedLighting);
        // M533: the chip is now CLICKABLE (the XAML no longer binds IsEnabled to this) so that pressing
        // it reports the refusal through MaterialEditorViewModel.Warn. CanAdd still gates whether the
        // macro is APPLIED, which is what this asserts - the guard itself did not move.
        Assert.False(chip.CanAdd);
        Assert.Equal("would crash", chip.StatusLabel);

        binding.AddMacroCommand.Execute(chip);     // simulate the click landing anyway

        Assert.DoesNotContain(binding.Macros, m => m.Name == MaterialBinding.MacroNoBakedLighting);
        Assert.False(editor.IsDirty);

        var bytes = editor.Serialize();
        Assert.NotNull(bytes);
        var reparsed = MaterialDocument.Parse(bytes!, Names(shader));
        Assert.False(reparsed.Materials.Single().MacroOn(MaterialBinding.MacroNoBakedLighting));
    }

    /// <summary>With no shader cache / no catalogue the oracle answers Unknown, the chip is ENABLED and
    /// reads "unchecked" — and the add both lands and persists. So "shown as added but not in the file"
    /// cannot be produced by the editor's own add path in either verdict.</summary>
    [Fact]
    public void WithNoShaderCacheTheChipIsEnabledAndTheAddStillPersists()
    {
        const string shader = "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest";
        var doc = MaterialDocument.Parse(OneMaterial(shader), Names(shader));
        var editor = new MaterialEditorViewModel();   // AskMacroSupport left null == no oracle
        editor.Load(doc, new WadAssetEntry { Path = "x.materials.bin" });
        var binding = editor.Materials.Single();
        binding.RefreshMacroStatus();

        var chip = binding.MissingMacros.Single(c => c.Name == MaterialBinding.MacroNoBakedLighting);
        Assert.True(chip.CanAdd);
        Assert.Equal("unchecked", chip.StatusLabel);

        binding.AddMacroCommand.Execute(chip);
        Assert.True(editor.IsDirty);

        var reparsed = MaterialDocument.Parse(editor.Serialize()!, Names(shader));
        Assert.True(reparsed.Materials.Single().MacroOn(MaterialBinding.MacroNoBakedLighting));
    }

    /// <summary>The user's actual file. Skipped when the project is not on this machine. Documents what
    /// the bin holds so the verdict above can be tied to it: 86 of 87 materials sit on
    /// DefaultEnv_Flat_AlphaTest, and the only one carrying NO_BAKED_LIGHTING is on a different shader.</summary>
    [Fact]
    public void TheUsersMap453BinTakesTheMacroAtTheMODELLayerForEveryMaterial()
    {
        const string path = @"D:\ReyEngine\Classic Rift - Halloween\Map453\data\maps\mapgeometry\map453\jade_container.materials.bin";
        if (!File.Exists(path)) return;   // not this machine

        var db = new HashDatabase();
        if (!db.LoadCache(@"D:\GamingTools\ReyEngine\data\hashes\merged_hashes.cache")) return;
        string? Resolve(uint h) => db.TryGetBinName(h, out var n) ? n : null;

        byte[] bytes = File.ReadAllBytes(path);
        var doc = MaterialDocument.Parse(bytes, Resolve);

        // The parse is clean and round-trips byte-for-byte, so nothing about Serialize() is refusing.
        Assert.Empty(doc.Issues);
        Assert.Equal(bytes, doc.Serialize());

        // Every material can take the macro at the model layer — the refusal is not here.
        Assert.All(doc.Materials, m => Assert.True(m.CanEditMacros));
        foreach (var m in doc.Materials) Assert.NotNull(m.SetMacro(MaterialBinding.MacroNoBakedLighting, true));
        var back = MaterialDocument.Parse(doc.Serialize(), Resolve);
        Assert.All(back.Materials, m => Assert.True(m.MacroOn(MaterialBinding.MacroNoBakedLighting)));
    }
}

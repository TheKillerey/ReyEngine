using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

public sealed class MaterialMacroEditingTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    [Fact]
    public void MaterialWithoutShaderMacrosCanAddFirstMacroWithoutChangingUntouchedBytes()
    {
        var pass = new BinTreeStruct(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
        {
            new BinTreeObjectLink(H("shader"), H("Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest")),
        });
        var technique = new BinTreeStruct(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("passes"), BinPropertyType.Struct, new BinTreeProperty[] { pass }),
        });
        var material = new BinTreeObject(H("Maps/Test/Alpha"), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "Maps/Test/Alpha"),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Struct, Array.Empty<BinTreeProperty>()),
            new BinTreeContainer(H("techniques"), BinPropertyType.Struct, new BinTreeProperty[] { technique }),
        });
        var tree = new BinTree(new[] { material }, Array.Empty<string>());
        using var stream = new MemoryStream();
        tree.Write(stream);
        byte[] original = stream.ToArray();

        string? Resolve(uint hash) => hash switch
        {
            var h when h == H("StaticMaterialDef") => "StaticMaterialDef",
            var h when h == H("Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest") => "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest",
            _ => null,
        };

        var document = MaterialDocument.Parse(original, Resolve);
        var binding = Assert.Single(document.Materials);
        Assert.True(binding.CanEditMacros);
        Assert.Equal(original, document.Serialize());

        var added = binding.SetMacro(MaterialBinding.MacroNoBakedLighting, true);
        Assert.NotNull(added);
        Assert.True(binding.MacroOn(MaterialBinding.MacroNoBakedLighting));
        Assert.True(document.IsDirty);

        var reparsed = MaterialDocument.Parse(document.Serialize(), Resolve);
        var persisted = Assert.Single(reparsed.Materials).AllMacros.Single();
        Assert.Equal(MaterialBinding.MacroNoBakedLighting, persisted.Name);
        Assert.True(persisted.On);

        var savedTree = new BinTree(new MemoryStream(document.Serialize(), writable: false));
        var savedMaterial = Assert.Single(savedTree.Objects).Value;
        Assert.IsType<BinTreeMap>(savedMaterial.Properties[H("shaderMacros")]);
        Assert.False(savedMaterial.Properties.ContainsKey(HashAlgorithms.Fnv1aRaw("shaderMacros")));
    }

    [Fact]
    public void EditingLegacyRawHashedMacroMapMovesItToTheCanonicalSchemaField()
    {
        uint rawField = HashAlgorithms.Fnv1aRaw("shaderMacros");
        var macro = new KeyValuePair<BinTreeProperty, BinTreeProperty>(
            new BinTreeString(0, MaterialBinding.MacroNoBakedLighting), new BinTreeString(0, "0"));
        var material = new BinTreeObject(H("Maps/Test/RawMacro"), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "Maps/Test/RawMacro"),
            new BinTreeMap(rawField, BinPropertyType.String, BinPropertyType.String, new[] { macro }),
        });
        using var stream = new MemoryStream();
        new BinTree(new[] { material }, Array.Empty<string>()).Write(stream);
        string? Resolve(uint hash) => hash == H("StaticMaterialDef") ? "StaticMaterialDef" : null;

        var document = MaterialDocument.Parse(stream.ToArray(), Resolve);
        var binding = Assert.Single(document.Materials);
        Assert.NotNull(binding.SetMacro(MaterialBinding.MacroNoBakedLighting, true));

        var savedMaterial = Assert.Single(SafeBinTree.Parse(document.Serialize()).Objects).Value;
        var savedMap = Assert.IsType<BinTreeMap>(savedMaterial.Properties[H("shaderMacros")]);
        Assert.False(savedMaterial.Properties.ContainsKey(rawField));
        Assert.Equal("1", Assert.IsType<BinTreeString>(Assert.Single(savedMap).Value).Value);
    }
}

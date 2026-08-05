using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;

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
    }
}

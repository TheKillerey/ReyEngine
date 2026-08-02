using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

public sealed class MapMaterialFactoryTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    private static byte[] Write(params BinTreeObject[] objects)
    {
        using var stream = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(stream);
        return stream.ToArray();
    }

    [Fact]
    public void ImportsTemplateByExactObjectHashEvenWhenItsNameCannotBeResolved()
    {
        const uint templateHash = 0xF1234567u;
        var source = Write(new BinTreeObject(templateHash, H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "Unresolved_Test_Material"),
            new BinTreeU32(H("type"), 0),
        }));
        var target = Write(new BinTreeObject(0x100u, H("MapSunProperties"), Array.Empty<BinTreeProperty>()));

        var result = MapMaterialFactory.ImportMaterial(target, source, templateHash,
            "Unresolved_Test_Material", "Workshop_Imported", out var error);

        Assert.Null(error);
        Assert.NotNull(result);
        var tree = SafeBinTree.Parse(result!);
        uint importedHash = H("Workshop_Imported");
        Assert.True(tree.Objects.ContainsKey(importedHash));
        Assert.Equal("Workshop_Imported",
            ((BinTreeString)tree.Objects[importedHash].Properties[H("name")]).Value);
        Assert.True(tree.Objects.ContainsKey(0x100u));
    }
}

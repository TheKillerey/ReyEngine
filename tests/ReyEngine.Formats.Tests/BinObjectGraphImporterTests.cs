using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

public sealed class BinObjectGraphImporterTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    private static byte[] Write(params BinTreeObject[] objects)
    {
        using var stream = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(stream);
        return stream.ToArray();
    }

    [Fact]
    public void ImportsLinkedClosureAndCollectsAssetsWithoutChangingTheirWadPath()
    {
        const uint root = 0x100u, child = 0x200u;
        var source = Write(
            new BinTreeObject(root, H("VfxSystemDefinitionData"), new BinTreeProperty[]
            {
                new BinTreeObjectLink(H("childParticleSetDefinition"), child),
                new BinTreeString(H("texture"), "DATA/Characters/Test/Particles/spark.tex"),
            }),
            new BinTreeObject(child, H("VfxChildParticleSetDefinitionData"), new BinTreeProperty[]
            {
                new BinTreeString(H("mesh"), "ASSETS/Characters/Test/spark.scb"),
            }));
        var target = Write(new BinTreeObject(0x300u, H("MapSunProperties"), Array.Empty<BinTreeProperty>()));

        var result = BinObjectGraphImporter.Import(target, new[] { source }, new[] { root }, out var error);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(2, result!.ImportedObjects);
        var tree = new BinTree(new MemoryStream(result.Bytes, writable: false));
        Assert.True(tree.Objects.ContainsKey(root));
        Assert.True(tree.Objects.ContainsKey(child));
        Assert.True(tree.Objects.ContainsKey(0x300u));
        Assert.Contains("DATA/Characters/Test/Particles/spark.tex", result.AssetPaths);
        Assert.Contains("ASSETS/Characters/Test/spark.scb", result.AssetPaths);
    }

    [Fact]
    public void RefusesASameHashDifferentObjectCollision()
    {
        const uint root = 0x100u;
        var source = Write(new BinTreeObject(root, H("VfxSystemDefinitionData"), new BinTreeProperty[]
        { new BinTreeString(H("name"), "source") }));
        var target = Write(new BinTreeObject(root, H("VfxSystemDefinitionData"), new BinTreeProperty[]
        { new BinTreeString(H("name"), "target") }));

        var result = BinObjectGraphImporter.Import(target, new[] { source }, new[] { root }, out var error);

        Assert.Null(result);
        Assert.Contains("different data", error);
    }
}

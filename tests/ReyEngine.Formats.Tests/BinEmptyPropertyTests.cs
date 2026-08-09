using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M414. An empty container is not the same as an absent one, and Riot ships zero of them: measured over
/// every shipped wad, <b>33,645 StaticMaterialDef objects contain no empty switches, paramValues,
/// samplerValues, techniques or shaderMacros</b>. The Map453 mod shipped exactly one - an empty
/// <c>switches</c> on <c>LegacyPort/map11/Grass_31861292fa7a</c> - and the game died on a null
/// dereference 0.66 s into loading with that material's name the only asset string left in the dump.
/// </summary>
public class BinEmptyPropertyTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static BinTreeObject Material(params BinTreeProperty[] props) =>
        new(H("Some/Material"), H("StaticMaterialDef"), props);

    [Fact]
    public void StripsAnEmptyContainer()
    {
        var tree = new BinTree(new[]
        {
            Material(
                new BinTreeString(H("name"), "Some/Material"),
                new BinTreeUnorderedContainer(H("switches"), BinPropertyType.Struct, Array.Empty<BinTreeProperty>())),
        }, Array.Empty<string>());

        Assert.Equal(1, BinEmptyProperty.Strip(tree));
        var o = Assert.Single(tree.Objects).Value;
        Assert.False(o.Properties.ContainsKey(H("switches")));
        Assert.True(o.Properties.ContainsKey(H("name")));   // everything else survives
    }

    [Fact]
    public void StripsAnEmptyMap()
    {
        var tree = new BinTree(new[]
        {
            Material(new BinTreeMap(H("shaderMacros"), BinPropertyType.String, BinPropertyType.String,
                Array.Empty<KeyValuePair<BinTreeProperty, BinTreeProperty>>())),
        }, Array.Empty<string>());

        Assert.Equal(1, BinEmptyProperty.Strip(tree));
        Assert.Empty(Assert.Single(tree.Objects).Value.Properties);
    }

    /// <summary>A POPULATED container must be left exactly as it is - the whole value of this pass is that
    /// it only removes the shape Riot never ships.</summary>
    [Fact]
    public void KeepsPopulatedContainersAndMaps()
    {
        var sw = new BinTreeStruct(0, H("StaticMaterialSwitchDef"), new BinTreeProperty[] { new BinTreeString(H("name"), "MULTIPLY_ALPHA") });
        var tree = new BinTree(new[]
        {
            Material(
                new BinTreeUnorderedContainer(H("switches"), BinPropertyType.Struct, new BinTreeProperty[] { sw }),
                new BinTreeMap(H("shaderMacros"), BinPropertyType.String, BinPropertyType.String,
                    new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeString(0, "NO_BAKED_LIGHTING"), new BinTreeString(0, "1")) })),
        }, Array.Empty<string>());

        Assert.Equal(0, BinEmptyProperty.Strip(tree));
        var o = Assert.Single(tree.Objects).Value;
        Assert.Equal(2, o.Properties.Count);
    }

    /// <summary>Nested: an empty container inside a struct inside a container is still the bad shape.</summary>
    [Fact]
    public void StripsNestedEmptyContainers()
    {
        var pass = new BinTreeStruct(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("someList"), BinPropertyType.Struct, Array.Empty<BinTreeProperty>()),
            new BinTreeBool(H("blendEnable"), true),
        });
        var technique = new BinTreeStruct(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("passes"), BinPropertyType.Struct, new BinTreeProperty[] { pass }),
        });
        var tree = new BinTree(new[]
        {
            Material(new BinTreeContainer(H("techniques"), BinPropertyType.Struct, new BinTreeProperty[] { technique })),
        }, Array.Empty<string>());

        Assert.Equal(1, BinEmptyProperty.Strip(tree));
        Assert.False(pass.Properties.ContainsKey(H("someList")));
        Assert.True(pass.Properties.ContainsKey(H("blendEnable")));
    }

    /// <summary>An Optional carrying no value is a DIFFERENT thing - Riot ships those - so it stays.</summary>
    [Fact]
    public void LeavesAnEmptyOptionalAlone()
    {
        var tree = new BinTree(new[]
        {
            Material(new BinTreeOptional(H("maybe"), null)),
        }, Array.Empty<string>());

        Assert.Equal(0, BinEmptyProperty.Strip(tree));
        Assert.True(Assert.Single(tree.Objects).Value.Properties.ContainsKey(H("maybe")));
    }

    /// <summary>Stripping is idempotent and leaves a clean tree byte-stable across a save.</summary>
    [Fact]
    public void SecondStripFindsNothingAndTheTreeStillWrites()
    {
        var tree = new BinTree(new[]
        {
            Material(
                new BinTreeString(H("name"), "Some/Material"),
                new BinTreeUnorderedContainer(H("switches"), BinPropertyType.Struct, Array.Empty<BinTreeProperty>())),
        }, Array.Empty<string>());

        Assert.Equal(1, BinEmptyProperty.Strip(tree));
        Assert.Equal(0, BinEmptyProperty.Strip(tree));

        using var ms = new MemoryStream();
        tree.Write(ms);
        var reread = new BinTree(new MemoryStream(ms.ToArray(), writable: false));
        Assert.False(Assert.Single(reread.Objects).Value.Properties.ContainsKey(H("switches")));
    }
}

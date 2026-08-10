using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M416. A material's samplerValues, paramValues, techniques, passes and switches hold EMBEDDED elements
/// (wire type 0x83), never struct/pointer (0x82). Measured on Riot's shipped materials: every one uses
/// Embedded. The two forms parse identically and round-trip identically, so nothing in the toolchain
/// notices - but the GAME does not render a material built with the pointer form.
///
/// <para>That is what made the ported Map453 terrain invisible. One byte per container, and the only
/// way it surfaced was rebuilding a single material as a CLONE of Riot data (it rendered) versus
/// hand-constructing the same values (it did not).</para>
/// </summary>
public class EmbeddedElementTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>BinTreeEmbedded must be what the writers produce - and it is a distinct wire type from
    /// BinTreeStruct even though it derives from it in the CLR.</summary>
    [Fact]
    public void EmbeddedAndPointerAreDifferentWireTypes()
    {
        var embedded = new BinTreeEmbedded(0, H("StaticMaterialShaderSamplerDef"),
            new BinTreeProperty[] { new BinTreeString(H("TextureName"), "DiffuseTexture") });
        var pointer = new BinTreeStruct(0, H("StaticMaterialShaderSamplerDef"),
            new BinTreeProperty[] { new BinTreeString(H("TextureName"), "DiffuseTexture") });

        Assert.Equal(BinPropertyType.Embedded, embedded.Type);
        Assert.Equal(BinPropertyType.Struct, pointer.Type);
        Assert.NotEqual(embedded.Type, pointer.Type);
        Assert.IsAssignableFrom<BinTreeStruct>(embedded);   // CLR subtyping is why this is easy to get wrong
    }

    /// <summary>A container declares its element type on the wire, and it survives a round trip - so the
    /// wrong choice is silently preserved rather than corrected anywhere.</summary>
    [Theory]
    [InlineData(BinPropertyType.Embedded)]
    [InlineData(BinPropertyType.Struct)]
    public void ContainerElementTypeSurvivesARoundTrip(BinPropertyType elementType)
    {
        BinTreeProperty element = elementType == BinPropertyType.Embedded
            ? new BinTreeEmbedded(0, H("StaticMaterialShaderParamDef"),
                new BinTreeProperty[] { new BinTreeString(H("name"), "TintColor") })
            : new BinTreeStruct(0, H("StaticMaterialShaderParamDef"),
                new BinTreeProperty[] { new BinTreeString(H("name"), "TintColor") });

        var material = new BinTreeObject(H("Maps/Test/Elem"), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "Maps/Test/Elem"),
            new BinTreeUnorderedContainer(H("paramValues"), elementType, new[] { element }),
        });
        var tree = new BinTree(new[] { material }, Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);

        var reread = new BinTree(new MemoryStream(ms.ToArray(), writable: false));
        var container = Assert.IsType<BinTreeUnorderedContainer>(
            Assert.Single(reread.Objects).Value.Properties[H("paramValues")]);
        Assert.Equal(elementType, container.ElementType);
    }
}

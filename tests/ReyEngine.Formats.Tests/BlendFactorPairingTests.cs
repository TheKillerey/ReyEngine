using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M415. A blend pass carries FOUR factors and they move in pairs. Measured over every shipped wad: of
/// 3,192 materials on <c>DefaultEnv_Flat_AlphaTest</c> with <c>PREMULTIPLIED_ALPHA</c> +
/// <c>MULTIPLY_ALPHA</c>, all 3,192 author <c>dstAlphaBlendFactor</c> beside <c>dstColorBlendFactor</c> -
/// none authors the colour half alone.
///
/// <para>Writing only the colour half left the ported Map453 terrain <b>fully transparent in game</b>:
/// 117 materials drew nothing while the same meshes rendered fine in ReyEngine's own viewport. The
/// bisect that found it swapped one property at a time on a material that DID render.</para>
/// </summary>
public class BlendFactorPairingTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static BinTreeStruct? Pass(byte[] bin, string materialName)
    {
        var tree = new BinTree(new MemoryStream(bin, writable: false));
        if (!tree.Objects.TryGetValue(H(materialName), out var o)) return null;
        if (!o.Properties.TryGetValue(H("techniques"), out var tp) || tp is not BinTreeContainer tc) return null;
        foreach (var t in tc.Elements)
            if (t is BinTreeStruct ts && ts.Properties.TryGetValue(H("passes"), out var pp) && pp is BinTreeContainer pc)
                foreach (var p in pc.Elements)
                    if (p is BinTreeStruct ps) return ps;
        return null;
    }

    private static uint? U32(BinTreeStruct s, string field) =>
        s.Properties.TryGetValue(H(field), out var p) && p is BinTreeU32 u ? u.Value : null;

    /// <summary>The shape the setup writer must produce: both halves, same value.</summary>
    [Theory]
    [InlineData("srcColorBlendFactor", "srcAlphaBlendFactor")]
    [InlineData("dstColorBlendFactor", "dstAlphaBlendFactor")]
    public void ColourAndAlphaFactorsAreWrittenTogether(string colour, string alpha)
    {
        // A minimal material the setup applier can act on.
        var pass = new BinTreeStruct(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
        {
            new BinTreeObjectLink(H("shader"), H("Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest")),
        });
        var technique = new BinTreeStruct(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("passes"), BinPropertyType.Struct, new BinTreeProperty[] { pass }),
        });
        // A real sampler, because MaterialDocument only surfaces bindings that look like materials -
        // a technique alone is not enough for it to classify one.
        var sampler = new BinTreeStruct(0, H("StaticMaterialShaderSamplerDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("TextureName"), "DiffuseTexture"),
            new BinTreeString(H("texturePath"), "ASSETS/Test/diffuse.tex"),
        });
        var material = new BinTreeObject(H("Maps/Test/Blend"), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "Maps/Test/Blend"),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Struct, new BinTreeProperty[] { sampler }),
            new BinTreeContainer(H("techniques"), BinPropertyType.Struct, new BinTreeProperty[] { technique }),
        });
        var tree = new BinTree(new[] { material }, Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);

        var doc = MaterialDocument.Parse(ms.ToArray(), _ => null);
        var binding = Assert.Single(doc.Materials);
        binding.SetPassU32(colour, 7);
        binding.SetPassU32(alpha, 7);

        var written = Pass(doc.Serialize(), "Maps/Test/Blend");
        Assert.NotNull(written);
        Assert.Equal(7u, U32(written!, colour));
        Assert.Equal(7u, U32(written!, alpha));
    }

    /// <summary>The defect itself, stated so it cannot come back silently: a pass that enables blending
    /// and sets a destination colour factor must not leave the alpha side absent.</summary>
    [Fact]
    public void ABlendPassWithOnlyTheColourHalfIsTheKnownBadShape()
    {
        var pass = new BinTreeStruct(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
        {
            new BinTreeObjectLink(H("shader"), H("Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest")),
            new BinTreeBool(H("blendEnable"), true),
            new BinTreeU32(H("dstColorBlendFactor"), 7),
        });
        bool blend = pass.Properties.TryGetValue(H("blendEnable"), out var b) && b is BinTreeBool bb && bb.Value;
        bool hasColour = pass.Properties.ContainsKey(H("dstColorBlendFactor"));
        bool hasAlpha = pass.Properties.ContainsKey(H("dstAlphaBlendFactor"));
        Assert.True(blend && hasColour && !hasAlpha,
            "this fixture is meant to BE the bad shape - if it stops being so, the test below means nothing");
    }
}

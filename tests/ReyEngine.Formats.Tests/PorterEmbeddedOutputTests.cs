using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M416 regression, on the WRITER rather than on the type system.
///
/// <para>MapMaterialFactory is what the legacy-map porter calls for every content-unique texture set, so
/// it decides the shape of every ported material in every ported map. It used to emit container elements
/// as struct/pointer (0x82) where Riot ships Embedded (0x83). Both forms parse, round-trip and diff
/// identically - and the game renders NOTHING for the pointer form, which is what made the ported Map453
/// terrain invisible.</para>
///
/// <para>Nothing else can catch this: the element types are the one property the rest of the toolchain
/// deliberately ignores, because BinTreeEmbedded derives from BinTreeStruct and every reader treats them
/// alike. So it is pinned here, on the real output of the real writer.</para>
/// </summary>
public class PorterEmbeddedOutputTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static byte[] EmptyBin()
    {
        var tree = new BinTree(Array.Empty<BinTreeObject>(), Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static LeagueShaderDef Shader() => new(
        "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest",
        "StaticMesh",
        new List<ShaderTextureDef> { new("DiffuseTexture", "ASSETS/Shared/Materials/Generic_Hex_2.tex") },
        new List<ShaderParamDef> { new("TintColor", 0.5f, 0.5f, 0.5f, 1f), new("AlphaTestValue", 0.3f, 0, 0, 0) },
        new List<string> { "MULTIPLY_ALPHA" });

    /// <summary>Every container the porter writes must declare Embedded elements, and every element must
    /// actually BE one.</summary>
    [Fact]
    public void PortedMaterialsUseEmbeddedElementsThroughout()
    {
        byte[]? bytes = MapMaterialFactory.CreateFromShader(
            EmptyBin(), "LegacyPort/test/Ground", Shader(), out string? error,
            samplerOverrides: new Dictionary<string, string> { ["DiffuseTexture"] = "assets/test/ground.tex" },
            parameterOverrides: null,
            switches: new Dictionary<string, bool> { ["MULTIPLY_ALPHA"] = true },
            macros: new Dictionary<string, bool> { ["NO_BAKED_LIGHTING"] = true },
            replaceExisting: false,
            blendEnable: true,
            sourceBlendFactor: null,
            destinationBlendFactor: 7);

        Assert.Null(error);
        Assert.NotNull(bytes);

        var tree = new BinTree(new MemoryStream(bytes!, writable: false));
        var material = Assert.Single(tree.Objects).Value;

        int checkedContainers = 0;
        foreach (string field in new[] { "samplerValues", "paramValues", "techniques", "switches" })
        {
            if (!material.Properties.TryGetValue(H(field), out var prop)) continue;
            var container = Assert.IsAssignableFrom<BinTreeContainer>(prop);
            Assert.Equal(BinPropertyType.Embedded, container.ElementType);
            foreach (var element in container.Elements)
                Assert.IsType<BinTreeEmbedded>(element);
            checkedContainers++;
        }
        Assert.True(checkedContainers >= 3, $"expected the porter to write several containers, saw {checkedContainers}");

        // ...including the nested passes container inside the technique, which is a separate write site.
        var technique = Assert.IsType<BinTreeEmbedded>(
            Assert.IsAssignableFrom<BinTreeContainer>(material.Properties[H("techniques")]).Elements.Single());
        var passes = Assert.IsAssignableFrom<BinTreeContainer>(technique.Properties[H("passes")]);
        Assert.Equal(BinPropertyType.Embedded, passes.ElementType);
        Assert.IsType<BinTreeEmbedded>(passes.Elements.Single());
    }

    /// <summary>M415 rides along on the same output: colour and alpha blend factors are written as a pair.</summary>
    [Fact]
    public void PortedMaterialsWriteBothHalvesOfTheBlendEquation()
    {
        byte[]? bytes = MapMaterialFactory.CreateFromShader(
            EmptyBin(), "LegacyPort/test/Blend", Shader(), out string? error,
            samplerOverrides: null, parameterOverrides: null, switches: null, macros: null,
            replaceExisting: false, blendEnable: true, sourceBlendFactor: 6, destinationBlendFactor: 7);

        Assert.Null(error);
        var tree = new BinTree(new MemoryStream(bytes!, writable: false));
        var material = Assert.Single(tree.Objects).Value;
        var technique = Assert.IsType<BinTreeEmbedded>(
            Assert.IsAssignableFrom<BinTreeContainer>(material.Properties[H("techniques")]).Elements.Single());
        var pass = Assert.IsType<BinTreeEmbedded>(
            Assert.IsAssignableFrom<BinTreeContainer>(technique.Properties[H("passes")]).Elements.Single());

        foreach (var (colour, alpha) in new[]
                 {
                     ("srcColorBlendFactor", "srcAlphaBlendFactor"),
                     ("dstColorBlendFactor", "dstAlphaBlendFactor"),
                 })
        {
            Assert.True(pass.Properties.ContainsKey(H(colour)), $"{colour} missing");
            Assert.True(pass.Properties.ContainsKey(H(alpha)), $"{alpha} missing - half a blend equation");
            Assert.Equal(
                ((BinTreeU32)pass.Properties[H(colour)]).Value,
                ((BinTreeU32)pass.Properties[H(alpha)]).Value);
        }
    }
}

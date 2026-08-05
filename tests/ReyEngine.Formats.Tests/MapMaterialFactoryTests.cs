using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using System.Numerics;

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

    [Fact]
    public void CreatesLegacyRoleMaterialWithAuthoredBindingsAndFeatures()
    {
        var target = Write(new BinTreeObject(0x100u, H("MapSunProperties"), Array.Empty<BinTreeProperty>()));
        var shader = new LeagueShaderDef("Shaders/StaticMesh/TestLegacy", "StaticMesh",
            new() { new ShaderTextureDef("DiffuseTexture", "ASSETS/Shared/Materials/black.tex") },
            new() { new ShaderParamDef("BlendPower", 1, 0, 0, 0) },
            new() { "USE_GRASS" });

        var result = MapMaterialFactory.CreateFromShader(target, "LegacyPort/map11/Cutout", shader, out var error,
            new Dictionary<string, string> { ["DiffuseTexture"] = "assets/maps/legacy/test.dds" },
            new Dictionary<string, Vector4> { ["BlendPower"] = new(4, 0, 0, 0) },
            new Dictionary<string, bool> { ["USE_GRASS"] = true },
            new Dictionary<string, bool> { ["NO_BAKED_LIGHTING"] = true });

        Assert.Null(error);
        Assert.NotNull(result);
        var material = SafeBinTree.Parse(result!).Objects[H("LegacyPort/map11/Cutout")];
        var samplers = Assert.IsType<BinTreeUnorderedContainer>(material.Properties[H("samplerValues")]);
        var sampler = Assert.Single(samplers.Elements.OfType<BinTreeStruct>());
        Assert.Equal("assets/maps/legacy/test.dds", Assert.IsType<BinTreeString>(sampler.Properties[H("texturePath")]).Value);

        var parameters = Assert.IsType<BinTreeUnorderedContainer>(material.Properties[H("paramValues")]);
        var parameter = Assert.Single(parameters.Elements.OfType<BinTreeStruct>());
        Assert.Equal(new Vector4(4, 0, 0, 0), Assert.IsType<BinTreeVector4>(parameter.Properties[H("value")]).Value);

        var switches = Assert.IsType<BinTreeUnorderedContainer>(material.Properties[H("switches")]);
        var feature = Assert.Single(switches.Elements.OfType<BinTreeStruct>());
        Assert.Equal(H("StaticMaterialSwitchDef"), feature.ClassHash);
        Assert.True(Assert.IsType<BinTreeBool>(feature.Properties[H("on")]).Value);

        var macros = Assert.IsType<BinTreeMap>(material.Properties[H("shaderMacros")]);
        var macro = Assert.Single(macros);
        Assert.Equal("NO_BAKED_LIGHTING", Assert.IsType<BinTreeString>(macro.Key).Value);
        Assert.Equal("1", Assert.IsType<BinTreeString>(macro.Value).Value);
    }
}

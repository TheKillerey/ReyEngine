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
    public void UnusedMaterialCleanupKeepsMapGeoAndBinGraphDependencies()
    {
        const string mapGeoMaterial = "Maps/Test/Materials/Ground";
        const string linkedMaterial = "Maps/Test/Materials/Particle";
        const string orphanMaterial = "Maps/Test/Materials/OldBush";
        var bin = Write(
            new BinTreeObject(H(mapGeoMaterial), H("StaticMaterialDef"), Array.Empty<BinTreeProperty>()),
            new BinTreeObject(H(linkedMaterial), H("StaticMaterialDef"), Array.Empty<BinTreeProperty>()),
            new BinTreeObject(H(orphanMaterial), H("StaticMaterialDef"), Array.Empty<BinTreeProperty>()),
            new BinTreeObject(H("Maps/Test/Vfx"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
            {
                new BinTreeObjectLink(H("material"), H(linkedMaterial)),
            }));

        var cleaned = MapMaterialFactory.RemoveUnusedStaticMaterials(bin, new[] { mapGeoMaterial },
            out int removed, out var error);

        Assert.Null(error);
        Assert.NotNull(cleaned);
        Assert.Equal(1, removed);
        var tree = SafeBinTree.Parse(cleaned!);
        Assert.True(tree.Objects.ContainsKey(H(mapGeoMaterial)));
        Assert.True(tree.Objects.ContainsKey(H(linkedMaterial)));
        Assert.False(tree.Objects.ContainsKey(H(orphanMaterial)));
        Assert.True(tree.Objects.ContainsKey(H("Maps/Test/Vfx")));
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

    [Fact]
    public void CreatesDecalBlendStateAndCanReplaceAnEarlierGuessedMaterial()
    {
        const string name = "LegacyPort/map11/Decal_test";
        var target = Write(new BinTreeObject(0x100u, H("MapSunProperties"), Array.Empty<BinTreeProperty>()));
        var alpha = new LeagueShaderDef("Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest", "StaticMesh",
            new() { new ShaderTextureDef("DiffuseTexture", "ASSETS/Shared/Materials/white.tex") },
            new() { new ShaderParamDef("AlphaTestValue", 0.3f, 0, 0, 0) }, new());

        var created = MapMaterialFactory.CreateFromShader(target, name, alpha, out var error,
            new Dictionary<string, string> { ["__diffuse__"] = "assets/maps/legacy/decal.dds" },
            new Dictionary<string, Vector4> { ["AlphaTestValue"] = new(0.005f, 0, 0, 0) },
            replaceExisting: true, blendEnable: true, sourceBlendFactor: 6, destinationBlendFactor: 7);

        Assert.Null(error);
        var material = SafeBinTree.Parse(created!).Objects[H(name)];
        var techniques = Assert.IsType<BinTreeContainer>(material.Properties[H("techniques")]);
        var technique = Assert.Single(techniques.Elements.OfType<BinTreeStruct>());
        var passes = Assert.IsType<BinTreeContainer>(technique.Properties[H("passes")]);
        var pass = Assert.Single(passes.Elements.OfType<BinTreeStruct>());
        Assert.True(Assert.IsType<BinTreeBool>(pass.Properties[H("blendEnable")]).Value);
        Assert.Equal(6u, Assert.IsType<BinTreeU32>(pass.Properties[H("srcColorBlendFactor")]).Value);
        Assert.Equal(7u, Assert.IsType<BinTreeU32>(pass.Properties[H("dstColorBlendFactor")]).Value);

        var replacement = new LeagueShaderDef("Shaders/StaticMesh/VertexDeform", "StaticMesh",
            new() { new ShaderTextureDef("DiffuseTexture", "ASSETS/Shared/Materials/white.tex") }, new(), new());
        var replaced = MapMaterialFactory.CreateFromShader(created!, name, replacement, out error,
            new Dictionary<string, string> { ["__diffuse__"] = "assets/maps/legacy/grass.dds" },
            replaceExisting: true, blendEnable: false);

        Assert.Null(error);
        var tree = SafeBinTree.Parse(replaced!);
        Assert.Equal(2, tree.Objects.Count);
        material = tree.Objects[H(name)];
        techniques = Assert.IsType<BinTreeContainer>(material.Properties[H("techniques")]);
        technique = Assert.Single(techniques.Elements.OfType<BinTreeStruct>());
        passes = Assert.IsType<BinTreeContainer>(technique.Properties[H("passes")]);
        pass = Assert.Single(passes.Elements.OfType<BinTreeStruct>());
        Assert.Equal(H(replacement.Name), Assert.IsType<BinTreeObjectLink>(pass.Properties[H("shader")]).Value);
        Assert.False(Assert.IsType<BinTreeBool>(pass.Properties[H("blendEnable")]).Value);
    }
}

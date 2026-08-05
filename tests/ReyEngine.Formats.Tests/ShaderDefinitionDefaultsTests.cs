using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

public sealed class ShaderDefinitionDefaultsTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    [Fact]
    public void ReadsTextureParameterAndSwitchDefaultsFromShaderDefinition()
    {
        var definition = new BinTreeObject(1, 2, new BinTreeProperty[]
        {
            new BinTreeContainer(H("textures"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                new BinTreeStruct(0, 10, new BinTreeProperty[]
                {
                    new BinTreeString(H("name"), "Diffuse_Texture"),
                    new BinTreeString(H("defaultTexturePath"), "ASSETS/Shared/Materials/black.tex"),
                }),
                new BinTreeStruct(0, 10, new BinTreeProperty[]
                {
                    new BinTreeString(H("name"), "Optional_Texture"),
                    new BinTreeString(H("defaultTexturePath"), ""),
                }),
            }),
            new BinTreeContainer(H("parameters"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                new BinTreeStruct(0, 11, new BinTreeProperty[]
                {
                    new BinTreeString(H("name"), "TintColor"),
                    new BinTreeVector4(H("data"), new Vector4(0.25f, 0.5f, 0.75f, 1f)),
                }),
            }),
            new BinTreeContainer(H("staticSwitches"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                new BinTreeStruct(0, 12, new BinTreeProperty[]
                {
                    new BinTreeString(H("name"), "USE_OVERLAY"),
                    new BinTreeBool(H("onByDefault"), true),
                }),
                new BinTreeStruct(0, 12, new BinTreeProperty[]
                {
                    new BinTreeString(H("name"), "DISABLE_FOG"),
                }),
            }),
        });

        var defaults = ShaderPermutationIndex.ReadDefinitionDefaults(definition);

        Assert.Equal("ASSETS/Shared/Materials/black.tex", defaults.Textures["Diffuse_Texture"]);
        Assert.DoesNotContain("Optional_Texture", defaults.Textures.Keys);
        Assert.Equal(new[] { 0.25f, 0.5f, 0.75f, 1f }, defaults.Parameters["TintColor"]);
        Assert.True(defaults.Switches["USE_OVERLAY"]);
        Assert.False(defaults.Switches["DISABLE_FOG"]);
    }

    [Theory]
    [InlineData("assets/shaders/generated/shaders/staticmesh/defaultenv_flat", "shaders/staticmesh/defaultenv_flat")]
    [InlineData("ASSETS\\SHADERS\\GENERATED\\Shaders\\Particles\\DefaultParticleLit", "Shaders/Particles/DefaultParticleLit")]
    [InlineData("Shaders/StaticMesh/Default", "Shaders/StaticMesh/Default")]
    public void ConvertsCacheNamesToShaderDefinitionPaths(string cacheName, string expected) =>
        Assert.Equal(expected, ShaderPermutationIndex.DefinitionPathForCacheShader(cacheName));
}

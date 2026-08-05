using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

public sealed class LegacyMapPorterTests
{
    [Fact]
    public void AppliesUserShaderChoicesToEveryDetectedRole()
    {
        static LegacyMaterialPlan Plan(string name, LegacyMaterialRole role) => new(name, role, "old",
            new Dictionary<string, string>(), new Dictionary<string, System.Numerics.Vector4>(),
            new Dictionary<string, bool>(), new Dictionary<string, bool>());
        var source = new LegacyMapPortResult(Array.Empty<byte>(), Array.Empty<LegacyTextureCopy>(), new[]
        {
            Plan("normal", LegacyMaterialRole.Normal), Plan("decal", LegacyMaterialRole.Decal),
            Plan("grass", LegacyMaterialRole.Grass), Plan("terrain", LegacyMaterialRole.FourBlendTerrain),
        }, "room.nvr", "NVR", 4, 4, 0, 0, 4, Array.Empty<string>());

        var mapped = LegacyMapPorter.ApplyShaderOptions(source,
            new LegacyPortShaderOptions("normal_shader", "decal_shader", "grass_shader", "terrain_shader"));

        Assert.Equal("normal_shader", mapped.Materials.Single(m => m.Role == LegacyMaterialRole.Normal).Shader);
        Assert.Equal("decal_shader", mapped.Materials.Single(m => m.Role == LegacyMaterialRole.Decal).Shader);
        Assert.Equal("grass_shader", mapped.Materials.Single(m => m.Role == LegacyMaterialRole.Grass).Shader);
        Assert.Equal("terrain_shader", mapped.Materials.Single(m => m.Role == LegacyMaterialRole.FourBlendTerrain).Shader);
    }
}

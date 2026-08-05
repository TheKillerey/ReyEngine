using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

public sealed class LegacyMapPorterTests
{
    [Fact]
    public void LegacyPositionCorrectionMatchesMeasuredAnchorDifference()
    {
        var before = new System.Numerics.Vector3(1876.810f, 48.144f, 7264.247f);
        var after = new System.Numerics.Vector3(2349.930f, -18.828f, 7502.003f);

        var corrected = before + LegacyMapPorter.LegacyPositionCorrection;

        Assert.Equal(after.X, corrected.X, 3);
        Assert.Equal(after.Y, corrected.Y, 3);
        Assert.Equal(after.Z, corrected.Z, 3);
    }

    [Fact]
    public void JadeContainerUsesOnlyItsDefaultEnvGameplayBushMaterial()
    {
        var materials = LegacyMapPorter.MapSpecificBushMaterials(
            "data/maps/mapgeometry/map453/jade_container.mapgeo");

        Assert.NotNull(materials);
        Assert.Equal("Maps/KitPieces/Jade/Base/Materials/Default/Jade_Foliage_Grass_AA_MAT",
            Assert.Single(materials!));
        Assert.Null(LegacyMapPorter.MapSpecificBushMaterials(
            "data/maps/mapgeometry/map11/base_srx.mapgeo"));
    }

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

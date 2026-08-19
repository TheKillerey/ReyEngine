using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M529: the NVR material record declares what each material IS.
///
/// <para>Two integers sit between the 260-byte name and the texture list: a TYPE (0 ordinary, 1 decal,
/// 2 grass) and a ground flag. The porter had never read them, deriving the role from name substrings
/// instead - and <c>LooksLikeDecal</c> matches on the literal word "decal", which catches 3 of Map2's
/// 18 declared decals. The other 15 (base_chasm1/2/3, order_seam, new_stone_road, turret_stoneBase,
/// the tile-floor marks) were imported as ordinary geometry laid over the ground rather than as decals
/// stamped on it.</para>
///
/// <para>Checked against the real file rather than a fixture, because the point of the finding is that
/// the file says so.</para>
/// </summary>
public sealed class NvrMaterialTypeTests
{
    private const string Room = @"K:\LeagueSandbox\League_Sandbox_Client\LEVELS\Map2\Scene\room.nvr";

    [Fact]
    public void TheRecordDeclaresDecalsGrassAndGround()
    {
        if (!File.Exists(Room)) return;
        var types = LegacyMapPorter.NvrMaterialTypes(File.ReadAllBytes(Room));

        // 148 materials: 127 plain, 20 decal, 1 grass, and 9 carrying the ground flag. Two of the
        // decals ALSO carry the ground flag, which is why the type and the flag are separate reads.
        Assert.Equal(148, types.Count);
        Assert.Equal(20, types.Values.Count(t => t.Type == LegacyMapPorter.NvrDecalType));
        Assert.Equal(1, types.Values.Count(t => t.Type == LegacyMapPorter.NvrGrassType));
        Assert.Equal(9, types.Values.Count(t => t.IsGround));
    }

    [Fact]
    public void TheDecalsAreTheOnesTheNameHeuristicMissed()
    {
        if (!File.Exists(Room)) return;
        var types = LegacyMapPorter.NvrMaterialTypes(File.ReadAllBytes(Room));

        // Not one of these contains the word "decal", and every one is a ground stamp - the exact set
        // that came through as opaque geometry over the terrain.
        foreach (string name in new[]
        {
            "base_chasm1_", "base_chasm2_", "base_chasm3_", "Order_seam_", "order_seam2_",
            "new_stone_road_", "turret_stoneBase_", "turret_stoneBase_hq_", "nexus_stoneBase_",
            "order_tile_floor_mark1_", "order_tile_floor_border_", "base_center_mark2_",
        })
        {
            Assert.True(types.TryGetValue(name, out var t), $"{name} is not in the NVR table");
            Assert.Equal(LegacyMapPorter.NvrDecalType, t.Type);
            Assert.DoesNotContain("decal", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheGroundFlagMarksTheTerrain()
    {
        if (!File.Exists(Room)) return;
        var types = LegacyMapPorter.NvrMaterialTypes(File.ReadAllBytes(Room));

        // every material carrying it is a ground surface by name, which is what makes the reading safe
        Assert.All(types.Where(kv => kv.Value.IsGround),
            kv => Assert.Contains("ground", kv.Key, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AShortOrGarbageFileYieldsNothingRatherThanThrowing()
    {
        Assert.Empty(LegacyMapPorter.NvrMaterialTypes(Array.Empty<byte>()));
        Assert.Empty(LegacyMapPorter.NvrMaterialTypes(new byte[64]));
    }
}

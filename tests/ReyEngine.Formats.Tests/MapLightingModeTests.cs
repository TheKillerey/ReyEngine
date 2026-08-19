using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M530: picking how the map is lit, rather than setting a macro whose name reads backwards.
///
/// <para>These tests exist for one reason: <c>NO_BAKED_LIGHTING</c> ON is the DYNAMIC mode, and every
/// time that has been read the other way round it has cost a milestone. Setting it across 78
/// <c>DefaultEnv_Flat_AlphaTest</c> materials produced "Unable to find correct hash for shader" and a
/// map that rendered nothing (M486); clearing it on Map11 left 20 of 184 materials with no cooked
/// permutation (M166). Pinning the direction here means the UI cannot drift off it silently.</para>
/// </summary>
public sealed class MapLightingModeTests
{
    [Fact]
    public void DynamicSetsTheMacroBecauseTheMacroMeansSkipTheLightmap()
    {
        var plan = MapLightingPlan.For(MapLightingMode.Dynamic);

        Assert.True(plan.NoBakedLighting);
        Assert.False(plan.RunBake);
    }

    [Fact]
    public void BothBakedModesClearTheMacro()
    {
        Assert.False(MapLightingPlan.For(MapLightingMode.Baked).NoBakedLighting);
        Assert.False(MapLightingPlan.For(MapLightingMode.BakeNow).NoBakedLighting);
    }

    [Fact]
    public void OnlyBakeNowActuallyBakes()
    {
        // The difference between the two baked modes is the whole reason there are two: one uses the
        // atlases a map already has, the other makes them.
        Assert.False(MapLightingPlan.For(MapLightingMode.Baked).RunBake);
        Assert.True(MapLightingPlan.For(MapLightingMode.BakeNow).RunBake);
    }

    [Fact]
    public void EveryModeHasAPlanAndTheMenuShowsAllOfThem()
    {
        var modes = Enum.GetValues<MapLightingMode>();
        Assert.Equal(modes.Length, MapLightingPlan.All.Count);

        foreach (var mode in modes)
        {
            var plan = MapLightingPlan.For(mode);
            Assert.False(string.IsNullOrWhiteSpace(plan.Label));
            Assert.False(string.IsNullOrWhiteSpace(plan.Explanation));
            Assert.Contains(mode, MapLightingPlan.All);
        }
    }

    [Fact]
    public void TheModeNamesAreTheStringsTheMenuPassesAsCommandParameters()
    {
        // MainWindow.axaml binds CommandParameter="Dynamic"/"Baked"/"BakeNow" and the view model parses
        // them back. A rename here without a rename there would fail at runtime and nowhere else.
        foreach (string name in new[] { "Dynamic", "Baked", "BakeNow" })
            Assert.True(Enum.TryParse<MapLightingMode>(name, ignoreCase: true, out _), name);
    }
}

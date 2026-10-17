using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M844: opening a map with placed lights on D3D11 drew every light far too bright until the viewport was
/// re-toggled. MapGeneration's PropertyChanged starts the scene build synchronously and the build reads
/// DynamicLights to decide whether to pin Riot's in-shader light loop; the bump used to run BEFORE the
/// lights were published, so nothing was pinned and the additive overlay drew them.
/// </summary>
public sealed class Dx11LightPinOrderTests
{
    private static string? ViewModelSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(dir.FullName, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void LoadMapGeoBumpsMapGenerationOnlyAfterTheLightsArePublished()
    {
        if (ViewModelSource() is not { } source) return;

        int load = source.IndexOf("_currentMapEntry = entry;", StringComparison.Ordinal);
        Assert.True(load > 0, "LoadMapGeoAsync's map assignment has moved");
        int import = source.IndexOf("ImportPlacedLightsIntoPanel(entry.Path);", load, StringComparison.Ordinal);
        int restore = source.IndexOf("RestoreMapLighting(entry);", load, StringComparison.Ordinal);
        int bump = source.IndexOf("MapGeneration++;", load, StringComparison.Ordinal);
        Assert.True(import > 0 && restore > 0 && bump > 0, "an anchor in LoadMapGeoAsync has moved");
        Assert.True(bump > import && bump > restore,
            "MapGeneration++ must come after ImportPlacedLightsIntoPanel and RestoreMapLighting, or the D3D11 build sees no lights");
    }

    [Theory]
    [InlineData(true, true, false, true, true, true)]     // lights arrive after an unpinned build
    [InlineData(false, true, false, true, true, false)]   // GL viewport
    [InlineData(true, false, false, true, true, false)]   // no map
    [InlineData(true, true, true, true, true, false)]     // map load in flight: its own bump rebuilds
    [InlineData(true, true, false, false, true, false)]   // last build already pinned
    [InlineData(true, true, false, true, false, false)]   // lights went away
    public void RebuildOnlyWhenThePinDecisionWouldFlip(bool dx11, bool map, bool loading, bool unpinned, bool lights, bool expected)
        => Assert.Equal(expected, MainWindowViewModel.ShouldRebuildForLights(dx11, map, loading, unpinned, lights));
}

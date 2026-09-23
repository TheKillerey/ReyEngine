using ReyEngine.App.ViewModels;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M761: the sun a map WITHOUT MapSunProperties gets. M759 changed the record's fog defaults to the schema's
/// (fog on, 0..-2000) and fixed only one of the two places that build the no-map sun; Reset lighting (and
/// any sun-less map) went through the other and drew a fog the map never had. Found in review.
/// </summary>
public sealed class NoMapSunTests
{
    [Fact]
    public void Reset_lighting_with_no_authored_sun_draws_no_fog()
    {
        var vm = new MainWindowViewModel();
        vm.ResetLightingCommand.Execute(null);   // ApplySunProperties(null): the sun-less-map path

        Assert.False(vm.MapFogEnabled);
        Assert.False(vm.CurrentSunProperties?.FogEnabled ?? false);
        Assert.False(vm.HasMapFog);
        Assert.Equal(0.0, vm.FogStartRaw);
        Assert.Equal(0.0, vm.FogEndRaw);   // was -2000, the record default
    }

    [Fact]
    public void The_screen_fog_panel_starts_off()
    {
        var vm = new MainWindowViewModel();
        Assert.Null(vm.CurrentPostFog);
        Assert.False(vm.DepthFogEnabled);
        Assert.False(vm.HeightFogEnabled);
    }
}

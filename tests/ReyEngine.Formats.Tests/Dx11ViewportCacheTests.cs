using System.Numerics;
using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

public class Dx11ViewportCacheTests
{
    [Fact]
    public void Marker_list_is_reused_until_inputs_or_camera_scale_change()
    {
        var vm = new MainWindowViewModel
        {
            ParticleMarkers = new[] { new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f) },
        };

        var first = vm.Dx11Icons(1000f);
        Assert.Same(first, vm.Dx11Icons(1000f));

        var zoomed = vm.Dx11Icons(2000f);
        Assert.NotSame(first, zoomed);
        Assert.Equal(first.Count, zoomed.Count);

        vm.ParticleMarkers = new[] { Vector3.Zero };
        var replaced = vm.Dx11Icons(2000f);
        Assert.NotSame(zoomed, replaced);
        Assert.Single(replaced);
    }
}

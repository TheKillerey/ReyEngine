using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M783: the Particle Editor view model's half of the D3D11 preview - the toggle and its status, mirroring
/// <c>CharacterDx11SurfaceTests</c>'s coverage of the same contract on <see cref="MeshPreviewViewModel"/>.
/// The device, the frame loop and the presentation live in ParticleEditorView.Dx11.cs and cannot be
/// exercised without a live view (see the UiProbe's "dx11particle" card for that half).
/// </summary>
public sealed class ParticleEditorDx11Tests
{
    [Fact]
    public void OpensOnGlWithNoStatus()
    {
        var vm = new ParticleEditorViewModel();
        Assert.False(vm.UseDx11Preview);
        Assert.Equal("", vm.Dx11Status);
    }

    [Fact]
    public void TurningTheRendererOffClearsItsStatus()
    {
        // The status describes a renderer that is no longer drawing; leaving it up reads as if it still is
        // - the same reason MeshPreviewViewModel clears its own Dx11Status on the same transition.
        var vm = new ParticleEditorViewModel();
        vm.UseDx11Preview = true;
        vm.Dx11Status = "12 systems playing";   // whatever the frame loop last reported

        vm.UseDx11Preview = false;

        Assert.Equal("", vm.Dx11Status);
    }

    [Fact]
    public void TurningItOnDoesNotClearAnExistingStatus()
    {
        // Only OFF is the "stopped drawing" transition; flipping on must not erase a reason the LAST
        // attempt already gave (e.g. why it fell back), which the toggle should still show while it retries.
        var vm = new ParticleEditorViewModel();
        vm.Dx11Status = "D3D11 unavailable: unknown";

        vm.UseDx11Preview = true;

        Assert.Equal("D3D11 unavailable: unknown", vm.Dx11Status);
    }
}

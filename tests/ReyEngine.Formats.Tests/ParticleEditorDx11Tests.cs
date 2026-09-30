using System;
using System.IO;
using System.Linq;
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

    /// <summary>M799: "the d3d11 3d view in particle editor is not working" - a black view with an empty status.
    /// MainWindow builds the window with its DataContext BEFORE Show and D3D11 already on, so StartDx11 ran while
    /// the view was in no window: QueueDx11Frame set its "queued" flag, `?.` then skipped the frame request, and
    /// the flag stayed set for good. The loop only runs in a live window (the UiProbe "dx11particle" card drives
    /// the real order and failed before this fix), so this pins the two pieces that keep it from coming back.</summary>
    [Fact]
    public void TheFrameLoopStartsEvenWhenTurnedOnBeforeTheWindowShows()
    {
        var src = Source("src", "ReyEngine.App", "Views", "ParticleEditorView.Dx11.cs");
        if (src is null) return;
        int noWindow = src.IndexOf("if (TopLevel.GetTopLevel(this) is not { } top) return;", StringComparison.Ordinal);
        int queued = src.IndexOf("_dx11FrameQueued = true;", StringComparison.Ordinal);
        Assert.True(noWindow >= 0, "QueueDx11Frame must return without a window before it marks a frame queued");
        Assert.True(queued > noWindow, "the queued flag may only be set once a window (TopLevel) is known");
        Assert.Contains("AttachedToVisualTree +=", src);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) continue;
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        return null;
    }
}

using ReyEngine.App.Services;
using Xunit;
using Overlay = ReyEngine.App.Services.Dx11OverlayGate.Overlay;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M660: the navgrid overlay never drew in the D3D11 viewport.
///
/// <para>Not because anything was missing — the upload, the shader and the draw all existed since M569.
/// The publish call was written INSIDE the bucket grid's change guard, so it ran only when the BUCKET
/// GRID changed identity. Switching the navgrid on never republished it, so D3D11 kept whatever it had,
/// which was nothing. The face-edit selection sat in the same block with the same fault. GL was fine
/// throughout because it guards each overlay separately, which is what made this look like a renderer
/// bug rather than a wiring one.</para>
///
/// <para>These tests are about the GUARD, because that is where the bug lived. A guard that gates
/// something other than what it names is invisible in review and invisible in a screenshot.</para>
/// </summary>
public class Dx11OverlayGateTests
{
    [Fact]
    public void AnOverlayIsPublishedOnceAndThenNotAgain()
    {
        var gate = new Dx11OverlayGate();
        var grid = new float[] { 1f, 2f, 3f };

        Assert.True(gate.Changed(Overlay.BucketGrid, grid));
        Assert.False(gate.Changed(Overlay.BucketGrid, grid));
        Assert.True(gate.Changed(Overlay.BucketGrid, new float[] { 1f, 2f, 3f }));   // equal, not the same
    }

    /// <summary>The bug itself: the navgrid must publish while the bucket grid sits still.</summary>
    [Fact]
    public void TheNavGridPublishesWhenTheBucketGridHasNotChanged()
    {
        var gate = new Dx11OverlayGate();
        var grid = new float[] { 1f };
        Assert.True(gate.Changed(Overlay.BucketGrid, grid));
        Assert.False(gate.Changed(Overlay.BucketGrid, grid));   // settled

        Assert.True(gate.Changed(Overlay.NavGridCells, new float[] { 2f }));
        Assert.True(gate.Changed(Overlay.NavGridLayers, new object()));
        Assert.True(gate.Changed(Overlay.SelectedFaces, new float[] { 3f }));

        // ...and none of that disturbed the bucket grid's own state
        Assert.False(gate.Changed(Overlay.BucketGrid, grid));
    }

    /// <summary>Each slot is independent in both directions - publishing one must not make another look
    /// changed, which would put the multi-megabyte re-upload back that the guard exists to avoid.</summary>
    [Fact]
    public void TheSlotsDoNotBleedIntoEachOther()
    {
        var gate = new Dx11OverlayGate();
        var shared = new float[] { 7f };

        Assert.True(gate.Changed(Overlay.BucketGrid, shared));
        Assert.True(gate.Changed(Overlay.NavGridCells, shared));      // same array, different slot
        Assert.False(gate.Changed(Overlay.BucketGrid, shared));
        Assert.False(gate.Changed(Overlay.NavGridCells, shared));
    }

    /// <summary>Turning an overlay off publishes null, which has to reach the renderer to clear it -
    /// so null is a change like any other, and then settles.</summary>
    [Fact]
    public void TurningAnOverlayOffIsAChange()
    {
        var gate = new Dx11OverlayGate();
        Assert.True(gate.Changed(Overlay.NavGridCells, new float[] { 1f }));
        Assert.True(gate.Changed(Overlay.NavGridCells, null));
        Assert.False(gate.Changed(Overlay.NavGridCells, null));
    }

    /// <summary>After a renderer is torn down and rebuilt the GPU no longer holds what the gate thinks it
    /// sent, so everything has to go again.</summary>
    [Fact]
    public void ResetMakesEverythingPublishAgain()
    {
        var gate = new Dx11OverlayGate();
        var grid = new float[] { 1f };
        var cells = new float[] { 2f };
        gate.Changed(Overlay.BucketGrid, grid);
        gate.Changed(Overlay.NavGridCells, cells);
        Assert.False(gate.Changed(Overlay.BucketGrid, grid));

        gate.Reset();
        Assert.True(gate.Changed(Overlay.BucketGrid, grid));
        Assert.True(gate.Changed(Overlay.NavGridCells, cells));
    }
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M810: focusing the camera on what is selected - the Focus button of every Object card and the F key.
///
/// <para>The Focus command used to set <c>ParticleFocusPoint</c>, which the GL control picked up and applied on its next render. Under
/// Direct3D 11 - the default - that control is hidden and never renders, so every Focus button did nothing. It also read
/// <c>SelectedParticleMarker</c>, a highlight position that only particles, sounds, props and probes keep: a selected point light or
/// added mesh left it empty or holding the previous selection's, so their Focus went nowhere or to the wrong object. And the same
/// point twice raised no change, so a second Focus after the camera had been taken away did nothing even in GL.</para>
///
/// <para>Now the view model works out the point from what is actually selected, and asks for the focus with a counter that moves on
/// every request. The window answers by moving the shared camera (<c>Viewport.FocusOnPoint</c>, which mirrors the point into the
/// camera's display space), whichever renderer is drawing.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Moves each time the user asks to focus the selection (a Focus button, the F key) - also for the same point twice, which
    /// a property holding the point would not report.</summary>
    [ObservableProperty] private int _focusRequest;

    /// <summary>The world point the last <see cref="FocusRequest"/> asked for: the selection's position in the map's own
    /// (unmirrored) coordinates, as <c>ViewportControl.FocusOnPoint</c> takes it.</summary>
    public System.Numerics.Vector3 FocusRequestPoint { get; private set; }

    /// <summary>Where the current selection is, in world space, or null when nothing with a position is selected. By kind, and not from
    /// the highlight marker (<c>SelectedParticleMarker</c>), which a light or an added mesh never sets. A mesh selection is its
    /// bounding box's centre - the gizmo pivot.</summary>
    public System.Numerics.Vector3? SelectedFocusPoint()
    {
        if (SelectedParticleNode is { } particle) return particle.CurrentPosition;
        if (SelectedSound is { } sound) return sound.Position;
        if (SelectedAddedMesh is { } added) return added.PivotWorld;
        if (SelectedLight is { } light) return LightWorldPosition(light);
        if (SelectedPropNode is { } prop) return prop.Position;
        if (SelectedProbe is { } probe) return probe.Position;
        if (!_selection.IsEmpty && GizmoPivot is { } pivot) return pivot;
        return null;
    }

    /// <summary>Ask the window to focus the camera on the selection. False (and no request) when nothing with a position is
    /// selected.</summary>
    public bool RequestFocusOnSelection()
    {
        if (SelectedFocusPoint() is not { } point) return false;
        FocusRequestPoint = point;
        FocusRequest++;
        return true;
    }
}

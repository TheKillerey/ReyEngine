using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.App.Services;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Rendering;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M808: the camera a map is opened with. The Content Browser's picture of a map looks at the playable area from above and
/// behind (<see cref="MapThumbnailCamera"/>); the viewport used to open the same map with the camera wherever it happened to be
/// - at first 600 units from the world origin - because the only thing that ever framed it was the OpenGL control, which is
/// hidden, and never renders, under Direct3D 11. Now the open is framed here, by the same rule, in both renderers.
///
/// <para>This file holds what the view model knows: the area (<see cref="CurrentMapFrame"/>, from the SAME start state the
/// picture is drawn from) and the moment (<see cref="MapFrameRequest"/>, which moves when a map is OPENED - a different map from
/// the one in the viewport - and not when the same map is reloaded after an edit or a tab comes back). The window owns the
/// camera and the size of the area it is seen in, so it places the camera.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The area of the open map the camera frames, or null when the map has nothing to frame. The viewport is bound to
    /// it: a map with a frame is framed by the window on open (and by Frame), not by every upload of its mesh.</summary>
    [ObservableProperty] private MapOpenFrame? _currentMapFrame;

    /// <summary>Moves each time a map is OPENED in the viewport and has an area to frame. The window answers by placing the
    /// camera. A reload of the map already open, an undo, a tab returning - none of them move it.</summary>
    [ObservableProperty] private int _mapFrameRequest;

    /// <summary>Set by the window, which owns the camera: where it is now. A map tab takes it with it when another tab takes the
    /// viewport.</summary>
    public Func<OrbitCameraPose?>? CaptureCameraPose { get; set; }

    /// <summary>Set by the window: put the camera back where a map tab left it.</summary>
    public Action<OrbitCameraPose>? RestoreCameraPose { get; set; }

    // what BuildMapVisibility read for the frame: the shipping bin's own definition (no axis inferred, as the picture's start state
    // has it) and the controllers built on it. Taken by ComputeMapOpenFrame, which lets them go.
    private MapVisibilityDefinition? _mapStartDefinition;
    private MapVisibilityControllers? _mapStartControllers;

    /// <summary>The area of the map's start state - what the Content Browser's picture of it shows: the shipping bin's initial
    /// visibility, every event off, a TFT board in its first stage - as a box, by the picture's own sampling and trim.</summary>
    private MapOpenFrame? ComputeMapOpenFrame(MapGeoAsset map, MapBoardStageSet boardStages)
    {
        try
        {
            var start = MapStartState.ResolveWith(map, _mapStartDefinition ?? MapVisibilityDefinition.Empty, _mapStartControllers,
                boardStages.HasStages ? boardStages.Stages[0] : null);
            return MapViewCamera.FrameOf(map, start.GroupVisible);
        }
        catch (Exception ex)
        {
            // a camera that cannot be placed is the camera as it was, never a map that does not open
            _log.Warn("MapGeo", "The camera was not placed on the map: " + ex.Message);
            return null;
        }
        finally
        {
            _mapStartDefinition = null;
            _mapStartControllers = null;
        }
    }
}

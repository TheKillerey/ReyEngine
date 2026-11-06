using System;
using System.Numerics;
using Avalonia.Threading;
using ReyEngine.App.ViewModels;
using ReyEngine.Rendering;

namespace ReyEngine.App.Views;

public partial class MeshPreviewWindow
{
    private OrbitCameraPose? _editorCameraPose;
    private readonly DispatcherTimer _gameplayCameraTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private int _framedScene = -1;

    // M868: wide enough for the champion and the dummy 500 units apart to sit together in a 55-degree view.
    private const float DefaultGameplayDistance = 2200f;
    private const float StageMinDistance = 250f, StageMaxDistance = 6000f;
    // Above the ground, short of straight down: 8 to 85 degrees of pitch on the stage.
    private const float StageMinPitch = 8f * MathF.PI / 180f, StageMaxPitch = 85f * MathF.PI / 180f;

    private void InstallGameplayCamera()
    {
        _gameplayCameraTimer.Tick += (_, _) =>
        {
            if (DataContext is MeshPreviewViewModel { ArenaViewport: { } arena } preview)
                UpdateArenaPlayback(preview, arena);
            var camera = PreviewViewport.Camera;
            // The stage camera belongs to the stage only: with a legacy map, a prop or nothing on the stage the
            // user's own pose comes back and the camera is free.
            if (DataContext is not MeshPreviewViewModel { ControlMode: true } vm)
            {
                if (_editorCameraPose is { } pose) { camera.Restore(pose); _editorCameraPose = null; }
                _framedScene = -1;
                return;
            }
            if (_editorCameraPose is null) _editorCameraPose = camera.Pose;
            var focus = vm.CameraFocus;
            var target = new Vector3(-focus.X, focus.Y, focus.Z);   // the viewport mirrors world X
            if (_framedScene != vm.SceneFrameCount)
            {
                // A fresh stage or Reset scene frames the champion and the dummy, Follow or not: the stable
                // three-quarter view, snapped (not eased) so a reset never leaves the camera sliding.
                _framedScene = vm.SceneFrameCount;
                camera.Target = target;
                camera.Yaw = MathF.PI;
                camera.Pitch = 55f * MathF.PI / 180f;
                camera.Distance = DefaultGameplayDistance;
                camera.Near = 5f;
                camera.Far = 200000f;
            }
            else if (vm.ArenaFollowCamera)
            {
                // Follow keeps the TARGET on the champion/dummy midpoint. Yaw, pitch and distance are the user's:
                // orbit and zoom persist while following.
                camera.Target = Vector3.Lerp(camera.Target, target, 1f - MathF.Exp(-10f * 0.016f));
            }
            PreviewViewport.SyncPickMatrices(PreviewInput.Bounds.Width, PreviewInput.Bounds.Height);
            PreviewViewport.RequestNextFrameRendering();
        };
        _gameplayCameraTimer.Start();
        Closed += (_, _) => _gameplayCameraTimer.Stop();
    }
}

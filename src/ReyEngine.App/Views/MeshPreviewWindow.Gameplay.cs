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
    private float _gameplayDistance = 2200f;

    private void InstallGameplayCamera()
    {
        _gameplayCameraTimer.Tick += (_, _) =>
        {
            if (DataContext is MeshPreviewViewModel { ArenaViewport: { } arena } preview)
                UpdateArenaPlayback(preview, arena);
            var camera = PreviewViewport.Camera;
            if (DataContext is not MeshPreviewViewModel { ControlMode: true, ArenaFollowCamera: true } vm)
            {
                if (_editorCameraPose is { } pose) { camera.Restore(pose); _editorCameraPose = null; }
                return;
            }
            if (_editorCameraPose is null) _editorCameraPose = camera.Pose;
            var target = new Vector3(-vm.CharacterPosition.X, vm.CharacterPosition.Y, vm.CharacterPosition.Z);
            camera.Target = Vector3.Lerp(camera.Target, target, 1f - MathF.Exp(-10f * 0.016f));
            camera.Yaw = MathF.PI;
            camera.Pitch = 55f * MathF.PI / 180f;
            camera.Distance = _gameplayDistance;
            PreviewViewport.SyncPickMatrices(PreviewInput.Bounds.Width, PreviewInput.Bounds.Height);
            PreviewViewport.RequestNextFrameRendering();
        };
        _gameplayCameraTimer.Start();
        Closed += (_, _) => _gameplayCameraTimer.Stop();
    }
}

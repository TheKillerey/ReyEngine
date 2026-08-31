using System;
using System.Diagnostics;
using Avalonia.Controls;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>
/// M618: the D3D11 half of the model preview window — the device, the frame loop and the presentation.
///
/// <para>Deliberately the same shape as the map viewport's (M248): a compositor-driven, rate-capped loop
/// that only turns while the toggle is on, drawing into an Image that sits behind the same transparent
/// input Border the GL control does. Nothing about input, camera or picking changes — both renderers are
/// driven by the one <see cref="ViewportControl.Camera"/>, which is what keeps an A/B honest.</para>
/// </summary>
public partial class MeshPreviewWindow
{
    private Dx11ViewportSurface? _dx11;
    private bool _dx11FrameQueued;
    private bool _dx11Closed;
    private int _dx11CommittedRevision = -1;
    private readonly Stopwatch _dx11Clock = Stopwatch.StartNew();
    private double _dx11LastFrame = double.NegativeInfinity;

    /// <summary>~60 fps. The preview is one character, but the loop still self-throttles rather than
    /// spinning the GPU on a model nobody is moving.</summary>
    private const double Dx11FrameSeconds = 1.0 / 60.0;

    private void HookDx11()
    {
        Closed += (_, _) => { _dx11Closed = true; _dx11?.Dispose(); _dx11 = null; };
        DataContextChanged += (_, _) => WatchDx11Toggle();
        WatchDx11Toggle();
    }

    private void WatchDx11Toggle()
    {
        if (DataContext is not MeshPreviewViewModel vm) return;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MeshPreviewViewModel.UseDx11Preview) && vm.UseDx11Preview)
                StartDx11(vm);
        };
        if (vm.UseDx11Preview) StartDx11(vm);
    }

    private void StartDx11(MeshPreviewViewModel vm)
    {
        if (_dx11Closed) return;
        _dx11 ??= new Dx11ViewportSurface();
        if (!_dx11.IsReady && !_dx11.Initialize())
        {
            // Turned back off rather than left ticking against a device that does not exist: an unticked
            // box beside the reason is a state the user can act on, a frozen image is not.
            vm.Dx11Status = "D3D11 unavailable: " + (_dx11.Error ?? "unknown");
            vm.UseDx11Preview = false;
            return;
        }

        _dx11CommittedRevision = -1;    // commit whatever scene is loaded, on the next frame
        QueueDx11Frame();
    }

    private void QueueDx11Frame()
    {
        if (_dx11Closed || _dx11FrameQueued) return;
        _dx11FrameQueued = true;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
        {
            _dx11FrameQueued = false;
            if (_dx11Closed || DataContext is not MeshPreviewViewModel vm || !vm.UseDx11Preview) return;

            double now = _dx11Clock.Elapsed.TotalSeconds;
            if (now - _dx11LastFrame >= Dx11FrameSeconds)
            {
                _dx11LastFrame = now;
                RenderDx11Frame(vm);
            }
            QueueDx11Frame();
        });
    }

    private void RenderDx11Frame(MeshPreviewViewModel vm)
    {
        if (_dx11 is null || !_dx11.IsReady) return;

        // Sized from PreviewInput, not from PreviewViewport. PreviewViewport is the GL control and it is
        // HIDDEN while D3D11 is on; Avalonia freezes Bounds on an invisible control, so resizing would
        // leave the render size stuck at whatever GL last measured. PreviewInput is the transparent
        // Border that takes the clicks and is visible in both modes.
        var surface = PreviewInput.Bounds;
        double scale = RenderScaling;
        int w = (int)(surface.Width * scale);
        int h = (int)(surface.Height * scale);
        if (w <= 0 || h <= 0) return;

        if (vm.Dx11SceneRevision != _dx11CommittedRevision)
        {
            _dx11CommittedRevision = vm.Dx11SceneRevision;
            if (vm.Dx11Scene is { } scene)
            {
                int drew = Dx11CharacterScene.Commit(_dx11.Renderer, scene, GameVersionOf(vm));
                _dx11.HasScene = drew > 0;
                _dx11.SceneReport = scene.Report;
                vm.Dx11Status = drew > 0
                    ? $"{drew} material(s) drawing"
                      + (scene.Failures.Count > 0 ? $", {scene.Failures.Count} unresolved" : "")
                    : "nothing drew: " + (scene.Failures.Count > 0 ? scene.Failures[0] : "no materials resolved");
            }
            else
            {
                _dx11.HasScene = false;
            }
        }

        // The pose for THIS frame, off the same clock the GL path animates with. Null palette leaves the
        // renderer's bind-pose constant in place, which is right for anything unskinned.
        var (palette, bones) = vm.CurrentPose(vm.ShowBones);
        _dx11.BonePalette = palette;
        _dx11.BoneLines = bones;
        _dx11.Wireframe = vm.Wireframe;
        _dx11.CullBackFaces = vm.CullBackfaces;

        // M619: the VFX. The SAME playback object the GL viewport is bound to in XAML, so both viewports
        // show the same effect at the same age rather than two independent simulations.
        _dx11.ShaderCache = vm.Dx11ShaderCache;
        _dx11.ParticlePlayback = vm.Playback;

        // M619: the target dummy's translate gizmo, built by ViewportMeshRenderer's own builder at the arm
        // length PreviewViewport.HitTestGizmoAxis measures against - so what is DRAWN and what is
        // GRABBABLE are the same geometry by construction, which is the whole reason the map viewport
        // builds it this way too.
        if (vm.DummyGizmoPivot is { } pivot)
        {
            float arm = PreviewViewport.GizmoArmLengthFor(pivot);
            _dx11.Renderer.SetGizmoLines(
                Rendering.ViewportMeshRenderer.BuildGizmoAxis(0, pivot, System.Numerics.Vector3.UnitX, arm),
                Rendering.ViewportMeshRenderer.BuildGizmoAxis(0, pivot, System.Numerics.Vector3.UnitY, arm),
                Rendering.ViewportMeshRenderer.BuildGizmoAxis(0, pivot, System.Numerics.Vector3.UnitZ, arm));
        }
        else _dx11.Renderer.SetGizmoLines(null, null, null);

        if (!_dx11.Render(PreviewViewport.Camera, w, h)) return;

        Dx11Preview.Source = _dx11.Current;
        Dx11Preview.Width = surface.Width;
        Dx11Preview.Height = surface.Height;

        // The GL control is hidden and not rendering, so nothing else refreshes the matrices that picking
        // raycasts against - and control mode's right-click orders go through exactly those.
        PreviewViewport.SyncPickMatrices(surface.Width, surface.Height);
    }

    private static string GameVersionOf(MeshPreviewViewModel vm) => "";
}
